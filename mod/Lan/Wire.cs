using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace PeakIOS.Lan
{
    /// <summary>
    /// Just enough of Photon's binary protocol (GpBinaryV18) for the LAN server: it walks values without
    /// building objects, so game data (events, properties) is forwarded byte-for-byte and the server never
    /// runs the game's custom-type deserializers (which touch PUN state) on its own threads.
    /// </summary>
    static class Wire
    {
        // Protocol18 type codes
        public const byte Bool = 2, Byte = 3, Short = 4, Float = 5, Double = 6, String = 7, Null = 8,
            CompressedInt = 9, CompressedLong = 10, Int1 = 11, Int1N = 12, Int2 = 13, Int2N = 14,
            L1 = 15, L1N = 16, L2 = 17, L2N = 18, Custom = 19, Dictionary = 20, Hashtable = 21,
            ObjectArray = 23, OpRequest = 24, OpResponse = 25, EventData = 26, False = 27, True = 28,
            ShortZero = 29, IntZero = 30, LongZero = 31, FloatZero = 32, DoubleZero = 33, ByteZero = 34,
            Array = 64, BoolArray = 66, ByteArray = 67, ShortArray = 68, FloatArray = 69, DoubleArray = 70,
            StringArray = 71, IntArray = 73, LongArray = 74, CustomArray = 83, DictionaryArray = 84,
            HashtableArray = 85;

        public static uint VarUInt(byte[] b, ref int p)
        {
            uint v = 0;
            for (int shift = 0; shift < 35; shift += 7)
            {
                byte x = b[p++];
                v |= (uint)(x & 0x7F) << shift;
                if ((x & 0x80) == 0) break;
            }
            return v;
        }

        static void SkipVarInt(byte[] b, ref int p)
        {
            for (int i = 0; i < 10; i++)
                if ((b[p++] & 0x80) == 0) return;
        }

        static int Count(byte[] b, ref int p)
        {
            uint n = VarUInt(b, ref p);
            if (n > b.Length) throw new InvalidDataException("bad count " + n);
            return (int)n;
        }

        /// Skips a value whose type byte comes first.
        public static void SkipTyped(byte[] b, ref int p) => Skip(b, ref p, b[p++]);

        public static void Skip(byte[] b, ref int p, byte t)
        {
            if (t >= 128 && t <= 228) { int n = Count(b, ref p); p += n; return; }   // slim custom type
            switch (t)
            {
                case Null: case False: case True: case ShortZero: case IntZero: case LongZero:
                case FloatZero: case DoubleZero: case ByteZero:
                    return;
                case Bool: case Byte: case Int1: case Int1N: case L1: case L1N: p += 1; return;
                case Short: case Int2: case Int2N: case L2: case L2N: p += 2; return;
                case Float: p += 4; return;
                case Double: p += 8; return;
                case String: case ByteArray: { int n = Count(b, ref p); p += n; return; }
                case CompressedInt: case CompressedLong: SkipVarInt(b, ref p); return;
                case Custom: { p++; int n = Count(b, ref p); p += n; return; }
                case Hashtable: SkipHashtableBody(b, ref p); return;
                case Dictionary:
                {
                    DictHeader(b, ref p, out byte kt, out byte vt);
                    SkipDictElements(b, ref p, kt, vt);
                    return;
                }
                case ObjectArray: case Array:
                {
                    int n = Count(b, ref p);
                    for (int i = 0; i < n; i++) SkipTyped(b, ref p);
                    return;
                }
                case OpRequest: p++; SkipParams(b, ref p); return;
                case EventData: p++; SkipParams(b, ref p); return;
                case OpResponse: p += 3; SkipTyped(b, ref p); SkipParams(b, ref p); return;
                case BoolArray: { int n = Count(b, ref p); p += (n + 7) / 8; return; }
                case ShortArray: { int n = Count(b, ref p); p += n * 2; return; }
                case FloatArray: { int n = Count(b, ref p); p += n * 4; return; }
                case DoubleArray: { int n = Count(b, ref p); p += n * 8; return; }
                case StringArray:
                {
                    int n = Count(b, ref p);
                    for (int i = 0; i < n; i++) { int l = Count(b, ref p); p += l; }
                    return;
                }
                case IntArray: case LongArray:
                {
                    int n = Count(b, ref p);
                    for (int i = 0; i < n; i++) SkipVarInt(b, ref p);
                    return;
                }
                case CustomArray:
                {
                    int n = Count(b, ref p);
                    p++;
                    for (int i = 0; i < n; i++) { int l = Count(b, ref p); p += l; }
                    return;
                }
                case HashtableArray:
                {
                    int n = Count(b, ref p);
                    for (int i = 0; i < n; i++) SkipHashtableBody(b, ref p);
                    return;
                }
                case DictionaryArray:
                {
                    DictHeader(b, ref p, out byte kt, out byte vt);
                    int n = Count(b, ref p);
                    for (int i = 0; i < n; i++) SkipDictElements(b, ref p, kt, vt);
                    return;
                }
                default:
                    throw new InvalidDataException($"unknown Photon type 0x{t:X2} at {p - 1}");
            }
        }

        static void SkipHashtableBody(byte[] b, ref int p)
        {
            int n = Count(b, ref p);
            for (int i = 0; i < n; i++) { SkipTyped(b, ref p); SkipTyped(b, ref p); }
        }

        static void SkipParams(byte[] b, ref int p)
        {
            int n = b[p++];
            for (int i = 0; i < n; i++) { p++; SkipTyped(b, ref p); }
        }

        // Mirrors Protocol18.ReadDictionaryType: key and value type; nested dictionary / array value types carry
        // more header bytes, and an array value type means "each value is typed".
        static void DictHeader(byte[] b, ref int p, out byte kt, out byte vt)
        {
            kt = b[p++];
            vt = b[p++];
            if (vt == Dictionary) NestedDictHeader(b, ref p);
            else if (vt == Array) { ArrayHeader(b, ref p); vt = 0; }
        }

        static void NestedDictHeader(byte[] b, ref int p)
        {
            p++;
            byte v = b[p++];
            if (v == Dictionary) NestedDictHeader(b, ref p);
            else if (v == Array) ArrayHeader(b, ref p);
        }

        static void ArrayHeader(byte[] b, ref int p)
        {
            while (b[p++] == Array) { }
        }

        static void SkipDictElements(byte[] b, ref int p, byte kt, byte vt)
        {
            int n = Count(b, ref p);
            for (int i = 0; i < n; i++)
            {
                if (kt == 0) SkipTyped(b, ref p); else Skip(b, ref p, kt);
                if (vt == 0) SkipTyped(b, ref p); else Skip(b, ref p, vt);
            }
        }

        /// A value as it appears on the wire (type byte included).
        public static byte[] Slice(byte[] b, int from, int to)
        {
            var r = new byte[to - from];
            Buffer.BlockCopy(b, from, r, 0, r.Length);
            return r;
        }

        /// Reads a typed value; primitives, strings and int/string arrays become objects, anything else
        /// comes back as its raw wire bytes (Raw).
        public static object ReadTyped(byte[] b, ref int p)
        {
            int start = p;
            byte t = b[p++];
            switch (t)
            {
                case Null: return null;
                case False: return false;
                case True: return true;
                case Bool: return b[p++] != 0;
                case ByteZero: return (byte)0;
                case Byte: return b[p++];
                case ShortZero: return (short)0;
                case Short: { short v = (short)(b[p] | (b[p + 1] << 8)); p += 2; return v; }
                case IntZero: return 0;
                case Int1: return (int)b[p++];
                case Int1N: return -(int)b[p++];
                case Int2: { int v = b[p] | (b[p + 1] << 8); p += 2; return v; }
                case Int2N: { int v = b[p] | (b[p + 1] << 8); p += 2; return -v; }
                case CompressedInt: { uint u = VarUInt(b, ref p); return (int)(u >> 1) ^ -(int)(u & 1); }
                case String:
                {
                    int n = Count(b, ref p);
                    string s = Encoding.UTF8.GetString(b, p, n);
                    p += n;
                    return s;
                }
                case IntArray:
                {
                    int n = Count(b, ref p);
                    var a = new int[n];
                    for (int i = 0; i < n; i++) { uint u = VarUInt(b, ref p); a[i] = (int)(u >> 1) ^ -(int)(u & 1); }
                    return a;
                }
                case ByteArray:
                {
                    int n = Count(b, ref p);
                    var a = new byte[n];
                    Buffer.BlockCopy(b, p, a, 0, n);
                    p += n;
                    return a;
                }
                default:
                    Skip(b, ref p, t);
                    return new Raw(Slice(b, start, p));
            }
        }

        /// Parameters of an operation request: code -> start offset of the typed value (and end).
        public static Dictionary<byte, (int from, int to)> ReadParams(byte[] b, ref int p)
        {
            int n = b[p++];
            var d = new Dictionary<byte, (int, int)>(n);
            for (int i = 0; i < n; i++)
            {
                byte k = b[p++];
                int from = p;
                SkipTyped(b, ref p);
                d[k] = (from, p);
            }
            return d;
        }

        /// Entries of a wire hashtable (typed value at [from, to)), each as raw key / raw value.
        public static List<KeyValuePair<byte[], byte[]>> ReadHashtableRaw(byte[] b, int from, int to)
        {
            var r = new List<KeyValuePair<byte[], byte[]>>();
            int p = from;
            if (b[p] == Null) return r;
            if (b[p++] != Hashtable) throw new InvalidDataException("expected a hashtable, got 0x" + b[from].ToString("X2"));
            int n = Count(b, ref p);
            for (int i = 0; i < n; i++)
            {
                int k = p;
                SkipTyped(b, ref p);
                int v = p;
                SkipTyped(b, ref p);
                r.Add(new KeyValuePair<byte[], byte[]>(Slice(b, k, v), Slice(b, v, p)));
            }
            if (p > to) throw new InvalidDataException("hashtable overrun");
            return r;
        }

        public static object Decode(byte[] typed)
        {
            int p = 0;
            return ReadTyped(typed, ref p);
        }

        public static string Key(byte[] raw) => Convert.ToBase64String(raw);
    }

    /// A value kept as its wire bytes (type byte included).
    sealed class Raw
    {
        public readonly byte[] Bytes;
        public Raw(byte[] bytes) { Bytes = bytes; }
    }

    /// Writes Photon messages: header, then values (objects or Raw).
    sealed class WireWriter
    {
        readonly MemoryStream m_S = new MemoryStream(256);

        public byte[] ToArray() => m_S.ToArray();
        public void B(byte v) => m_S.WriteByte(v);
        public void Bytes(byte[] v) => m_S.Write(v, 0, v.Length);

        public void VarUInt(uint v)
        {
            while (v >= 0x80) { m_S.WriteByte((byte)(v | 0x80)); v >>= 7; }
            m_S.WriteByte((byte)v);
        }

        public void Value(object o)
        {
            switch (o)
            {
                case null: B(Wire.Null); break;
                case Raw r: Bytes(r.Bytes); break;
                case bool v: B(v ? Wire.True : Wire.False); break;
                case byte v: B(Wire.Byte); B(v); break;
                case short v: B(Wire.Short); B((byte)v); B((byte)(v >> 8)); break;
                case int v: B(Wire.CompressedInt); VarUInt((uint)((v << 1) ^ (v >> 31))); break;
                case string v:
                {
                    B(Wire.String);
                    var bytes = Encoding.UTF8.GetBytes(v);
                    VarUInt((uint)bytes.Length);
                    Bytes(bytes);
                    break;
                }
                case int[] v:
                    B(Wire.IntArray);
                    VarUInt((uint)v.Length);
                    foreach (int x in v) VarUInt((uint)((x << 1) ^ (x >> 31)));
                    break;
                case byte[] v: B(Wire.ByteArray); VarUInt((uint)v.Length); Bytes(v); break;
                case Props h: Hashtable(h.Entries); break;
                case List<KeyValuePair<byte[], byte[]>> e: Hashtable(e); break;
                case List<KeyValuePair<object, object>> e:
                    B(Wire.Hashtable);
                    VarUInt((uint)e.Count);
                    foreach (var kv in e) { Value(kv.Key); Value(kv.Value); }
                    break;
                default: throw new ArgumentException("cannot write " + o.GetType());
            }
        }

        void Hashtable(IEnumerable<KeyValuePair<byte[], byte[]>> entries)
        {
            var list = entries as ICollection<KeyValuePair<byte[], byte[]>> ?? new List<KeyValuePair<byte[], byte[]>>(entries);
            B(Wire.Hashtable);
            VarUInt((uint)list.Count);
            foreach (var kv in list) { Bytes(kv.Key); Bytes(kv.Value); }
        }

        public void Params(List<KeyValuePair<byte, object>> ps)
        {
            B((byte)ps.Count);
            foreach (var kv in ps) { B(kv.Key); Value(kv.Value); }
        }
    }

    /// Room / player properties: wire key -> wire value, in insertion order.
    sealed class Props
    {
        public readonly List<KeyValuePair<byte[], byte[]>> Entries = new List<KeyValuePair<byte[], byte[]>>();
        readonly Dictionary<string, int> m_Index = new Dictionary<string, int>();

        public byte[] Get(byte[] key) => m_Index.TryGetValue(Wire.Key(key), out int i) ? Entries[i].Value : null;

        public byte[] Get(byte key) => Get(KeyOf(key));

        public static byte[] KeyOf(byte key) => key == 0 ? new[] { Wire.ByteZero } : new[] { Wire.Byte, key };

        public void Set(byte[] key, byte[] value)
        {
            string k = Wire.Key(key);
            if (m_Index.TryGetValue(k, out int i)) Entries[i] = new KeyValuePair<byte[], byte[]>(Entries[i].Key, value);
            else
            {
                m_Index[k] = Entries.Count;
                Entries.Add(new KeyValuePair<byte[], byte[]>(key, value));
            }
        }

        public void Set(byte key, object value)
        {
            var w = new WireWriter();
            w.Value(value);
            Set(KeyOf(key), w.ToArray());
        }

        public void Remove(byte[] key)
        {
            string k = Wire.Key(key);
            if (!m_Index.TryGetValue(k, out int i)) return;
            Entries.RemoveAt(i);
            m_Index.Clear();
            for (int j = 0; j < Entries.Count; j++) m_Index[Wire.Key(Entries[j].Key)] = j;
        }

        public Props Clone()
        {
            var p = new Props();
            foreach (var kv in Entries) p.Set(kv.Key, kv.Value);
            return p;
        }
    }
}
