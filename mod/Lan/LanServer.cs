using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace PeakIOS.Lan
{
    /// <summary>
    /// A small Photon server (TCP transport, GpBinaryV18) that runs inside the host's app. It speaks the subset
    /// of Photon's LoadBalancing protocol that PUN uses: master server (authenticate, find the game) and game
    /// server (join, events with room caching, properties, interest groups, master client hand-over) on one port.
    /// Every client, the host's own included, connects to it like it would to Photon Cloud.
    /// </summary>
    sealed class LanServer
    {
        public const int DefaultPort = 47778;
        const byte KickEvent = 18;

        public int Port { get; private set; }
        /// Only this Photon user may create the game room (the host's own client); others can only join it.
        public string HostUserId;
        /// Photon app ID of the game (rooms of other apps, like PUN voice, are open to anyone).
        public string GameAppId;
        public bool Running => m_Listener != null;

        readonly object m_Lock = new object();
        readonly List<Peer> m_Peers = new List<Peer>();
        readonly Dictionary<string, Room> m_Rooms = new Dictionary<string, Room>();
        TcpListener m_Listener;
        Thread m_AcceptThread, m_WatchThread;
        readonly Stopwatch m_Clock = Stopwatch.StartNew();
        volatile bool m_Stopping;
        int m_NextPeerId;

        // ---- public state for the UI / discovery ---------------------------------------------------
        public struct Info
        {
            public bool HasRoom;
            public string RoomName;
            public int Players, MaxPlayers, Connections;
            public string[] PlayerNames;
        }

        public Info GetInfo()
        {
            lock (m_Lock)
            {
                var info = new Info { Connections = m_Peers.Count };
                if (GameAppId != null && m_Rooms.TryGetValue(GameAppId, out var r))
                {
                    info.HasRoom = true;
                    info.RoomName = r.Name;
                    info.Players = r.Actors.Count;
                    info.MaxPlayers = r.MaxPlayers;
                    info.PlayerNames = r.Actors.Values.Select(a => a.NickName ?? ("#" + a.ActorNr)).ToArray();
                }
                return info;
            }
        }

        // ---------------------------------------------------------------------------------------------
        public void Start(int port)
        {
            if (Running) return;
            m_Stopping = false;
            var l = new TcpListener(IPAddress.Any, port);
            l.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            l.Start(16);
            m_Listener = l;
            Port = ((IPEndPoint)l.LocalEndpoint).Port;
            m_AcceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "LAN accept" };
            m_AcceptThread.Start();
            m_WatchThread = new Thread(WatchLoop) { IsBackground = true, Name = "LAN watchdog" };
            m_WatchThread.Start();
            Log.Info($"LAN server listening on port {Port}");
        }

        public void Stop()
        {
            if (!Running) return;
            m_Stopping = true;
            try { m_Listener.Stop(); } catch { }
            m_Listener = null;
            List<Peer> peers;
            lock (m_Lock)
            {
                peers = m_Peers.ToList();
                m_Peers.Clear();
                m_Rooms.Clear();
            }
            foreach (var p in peers) p.Close();
            Log.Info("LAN server stopped");
        }

        int ServerTime => (int)m_Clock.ElapsedMilliseconds;

        void AcceptLoop()
        {
            var l = m_Listener;
            while (!m_Stopping && l != null)
            {
                Socket s;
                try { s = l.AcceptSocket(); }
                catch (Exception e)
                {
                    if (!m_Stopping) Log.Warn("LAN accept: " + e.Message);
                    break;
                }
                try
                {
                    s.NoDelay = true;
                    s.SendTimeout = 4000;
                    var p = new Peer(this, s, Interlocked.Increment(ref m_NextPeerId));
                    lock (m_Lock) m_Peers.Add(p);
                    Log.Info($"LAN peer {p.Id} connected from {s.RemoteEndPoint}");
                    new Thread(() => ReceiveLoop(p)) { IsBackground = true, Name = "LAN peer " + p.Id }.Start();
                }
                catch (Exception e)
                {
                    Log.Warn("LAN accept setup: " + e.Message);
                    try { s.Close(); } catch { }
                }
            }
        }

        void WatchLoop()
        {
            while (!m_Stopping)
            {
                Thread.Sleep(1000);
                List<Peer> stale;
                lock (m_Lock) stale = m_Peers.Where(p => ServerTime - p.LastReceive > 20000).ToList();
                foreach (var p in stale)
                {
                    Log.Warn($"LAN peer {p.Id} timed out");
                    p.Close();
                }
            }
        }

        // ---- transport -----------------------------------------------------------------------------
        void ReceiveLoop(Peer p)
        {
            var head = new byte[7];
            try
            {
                while (!m_Stopping && !p.Closed)
                {
                    ReadExact(p.Socket, head, 0, 1);
                    p.LastReceive = ServerTime;
                    if (head[0] == 0xF0)
                    {
                        // ping: F0 + client time -> F0 + server time + client time
                        ReadExact(p.Socket, head, 1, 4);
                        var pong = new byte[9];
                        pong[0] = 0xF0;
                        PutBE(pong, 1, ServerTime);
                        Buffer.BlockCopy(head, 1, pong, 5, 4);
                        p.SendRaw(pong);
                        continue;
                    }
                    if (head[0] != 0xFB) throw new InvalidDataException("bad frame start 0x" + head[0].ToString("X2"));
                    ReadExact(p.Socket, head, 1, 6);
                    int len = (head[1] << 24) | (head[2] << 16) | (head[3] << 8) | head[4];
                    if (len < 9 || len > 4 * 1024 * 1024) throw new InvalidDataException("bad frame length " + len);
                    var payload = new byte[len - 7];
                    ReadExact(p.Socket, payload, 0, payload.Length);
                    lock (m_Lock)
                    {
                        if (p.Closed) break;
                        try { HandleMessage(p, payload); }
                        catch (Exception e) { Log.Error($"LAN peer {p.Id}: message failed: {e}"); }
                    }
                }
            }
            catch (Exception e)
            {
                if (!m_Stopping && !p.Closed && !(e is SocketException || e is EndOfStreamException || e is ObjectDisposedException))
                    Log.Warn($"LAN peer {p.Id}: {e.Message}");
            }
            p.Close();
            lock (m_Lock) RemovePeer(p);
        }

        static void ReadExact(Socket s, byte[] b, int off, int n)
        {
            while (n > 0)
            {
                int r = s.Receive(b, off, n, SocketFlags.None);
                if (r <= 0) throw new EndOfStreamException();
                off += r;
                n -= r;
            }
        }

        static void PutBE(byte[] b, int at, int v)
        {
            b[at] = (byte)(v >> 24);
            b[at + 1] = (byte)(v >> 16);
            b[at + 2] = (byte)(v >> 8);
            b[at + 3] = (byte)v;
        }

        static byte[] Frame(byte[] payload)
        {
            var f = new byte[7 + payload.Length];
            f[0] = 0xFB;
            PutBE(f, 1, f.Length);
            f[5] = 0;   // channel
            f[6] = 1;   // reliable
            Buffer.BlockCopy(payload, 0, f, 7, payload.Length);
            return f;
        }

        static byte[] ResponsePayload(byte msgType, byte op, short ret, string debug, List<KeyValuePair<byte, object>> ps)
        {
            var w = new WireWriter();
            w.B(0xF3);
            w.B(msgType);
            w.B(op);
            w.B((byte)ret);
            w.B((byte)(ret >> 8));
            w.Value(string.IsNullOrEmpty(debug) ? null : debug);
            w.Params(ps ?? new List<KeyValuePair<byte, object>>());
            return Frame(w.ToArray());
        }

        static byte[] EventFrame(byte code, List<KeyValuePair<byte, object>> ps)
        {
            var w = new WireWriter();
            w.B(0xF3);
            w.B(4);
            w.B(code);
            w.Params(ps);
            return Frame(w.ToArray());
        }

        static List<KeyValuePair<byte, object>> P(params (byte k, object v)[] ps)
        {
            var l = new List<KeyValuePair<byte, object>>(ps.Length);
            foreach (var (k, v) in ps) l.Add(new KeyValuePair<byte, object>(k, v));
            return l;
        }

        void Respond(Peer p, byte op, short ret = 0, string debug = null, List<KeyValuePair<byte, object>> ps = null)
        {
            p.Send(ResponsePayload(3, op, ret, debug, ps));
            if (ret != 0) Log.Info($"LAN peer {p.Id}: op {op} -> error {ret} {debug}");
        }

        // ---- messages ------------------------------------------------------------------------------
        void HandleMessage(Peer p, byte[] m)
        {
            if (m.Length < 2 || (m[0] != 0xF3 && m[0] != 0xFD)) throw new InvalidDataException("bad message header");
            int type = m[1] & 0x7F;
            bool encrypted = (m[1] & 0x80) != 0;
            if (type == 0)
            {
                // init: F3 00 <protocol 2> <sdk> <version 3> 00 <app id, 32 bytes>
                p.AppId = m.Length >= 41 ? Encoding.ASCII.GetString(m, 9, 32).TrimEnd('\0') : "";
                p.Send(Frame(new byte[] { 0xF3, 1, 0 }));   // the client reads 3 header bytes
                return;
            }
            byte[] body;
            if (encrypted)
            {
                if (p.Crypto == null) throw new InvalidDataException("encrypted message before key exchange");
                using (var d = p.Crypto.CreateDecryptor()) body = d.TransformFinalBlock(m, 2, m.Length - 2);
            }
            else
            {
                body = new byte[m.Length - 2];
                Buffer.BlockCopy(m, 2, body, 0, body.Length);
            }
            int pos = 0;
            byte op = body[pos++];
            var ps = Wire.ReadParams(body, ref pos);
            var req = new Request(body, ps);
            if (type == 6) HandleInternal(p, op, req);
            else if (type == 2) HandleOperation(p, op, req);
            else Log.Warn($"LAN peer {p.Id}: unexpected message type {type}");
        }

        // DH key exchange (Photon: Oakley group 1, generator 22, AES-256-CBC with SHA-256 of the shared secret)
        static readonly BigInteger s_Prime = BigInteger.Parse(
            "0FFFFFFFFFFFFFFFFC90FDAA22168C234C4C6628B80DC1CD129024E088A67CC74020BBEA63B139B22514A08798E3404DDEF9519B3" +
            "CD3A431B302B0A6DF25F14374FE1356D6D51C245E485B576625E7EC6F44C42E9A63A3620FFFFFFFFFFFFFFFF",
            System.Globalization.NumberStyles.HexNumber);

        void HandleInternal(Peer p, byte op, Request req)
        {
            if (op != 0)
            {
                p.Send(ResponsePayload(7, op, 0, null, null));
                return;
            }
            var clientKey = req.Get(1) as byte[];
            if (clientKey == null || clientKey.Length == 0) throw new InvalidDataException("no client key");
            var secretBytes = new byte[21];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(secretBytes);
            secretBytes[20] = 0;   // positive
            var secret = new BigInteger(secretBytes);
            var pub = BigInteger.ModPow(22, secret, s_Prime);
            var shared = BigInteger.ModPow(FromPhoton(clientKey), secret, s_Prime);
            byte[] key;
            using (var sha = SHA256.Create()) key = sha.ComputeHash(ToPhoton(shared));
            var aes = Aes.Create();
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            aes.Key = key;
            aes.IV = new byte[16];
            p.Crypto = aes;
            p.Send(ResponsePayload(7, 0, 0, null, P((1, ToPhoton(pub)))));
        }

        // Photon sends big integers big-endian without a sign byte.
        static BigInteger FromPhoton(byte[] be)
        {
            var le = new byte[be.Length + 1];
            for (int i = 0; i < be.Length; i++) le[i] = be[be.Length - 1 - i];
            return new BigInteger(le);
        }

        static byte[] ToPhoton(BigInteger v)
        {
            var le = v.ToByteArray();
            int n = le.Length;
            while (n > 1 && le[n - 1] == 0) n--;
            var be = new byte[n];
            for (int i = 0; i < n; i++) be[i] = le[n - 1 - i];
            return be;
        }

        sealed class Request
        {
            readonly byte[] m_B;
            readonly Dictionary<byte, (int from, int to)> m_P;
            public Request(byte[] b, Dictionary<byte, (int, int)> p) { m_B = b; m_P = p; }
            public bool Has(byte k) => m_P.ContainsKey(k);
            public object Get(byte k)
            {
                if (!m_P.TryGetValue(k, out var r)) return null;
                int p = r.from;
                return Wire.ReadTyped(m_B, ref p);
            }
            public Raw RawOf(byte k) => m_P.TryGetValue(k, out var r) ? new Raw(Wire.Slice(m_B, r.from, r.to)) : null;
            public List<KeyValuePair<byte[], byte[]>> Table(byte k) =>
                m_P.TryGetValue(k, out var r) ? Wire.ReadHashtableRaw(m_B, r.from, r.to) : null;
            public int Int(byte k, int def) => Get(k) switch { int i => i, byte b => b, short s => s, _ => def };
            public byte Byte(byte k, byte def) => Get(k) switch { byte b => b, int i => (byte)i, _ => def };
            public bool Bool(byte k, bool def) => Get(k) is bool b ? b : def;
        }

        // ---- LoadBalancing operations ------------------------------------------------------------------
        void HandleOperation(Peer p, byte op, Request req)
        {
            switch (op)
            {
                case 230:   // Authenticate (master and game server)
                case 231:   // AuthenticateOnce
                {
                    string uid = req.Get(221) as string ?? req.Get(225) as string;
                    if (string.IsNullOrEmpty(uid)) uid = p.UserId ?? Guid.NewGuid().ToString("N");
                    p.UserId = uid;
                    Respond(p, op, ps: P((225, uid), (221, uid)));
                    return;
                }
                case 229: case 228: case 218: case 251:   // join / leave lobby, settings, get properties
                    Respond(p, op);
                    return;
                case 217:   // game list
                    Respond(p, op, ps: P((222, new List<KeyValuePair<object, object>>())));
                    return;
                case 227: case 226: case 225:
                    if (req.Has(250)) JoinOnGameServer(p, op, req);
                    else MatchOnMaster(p, op, req);
                    return;
                case 253: RaiseEvent(p, req); return;
                case 252: SetProperties(p, req); return;
                case 248: ChangeGroups(p, req); return;
                case 254:   // Leave
                    Respond(p, op);
                    LeaveRoom(p);
                    return;
                default:
                    Respond(p, op, -2, "operation not supported by the LAN server");
                    return;
            }
        }

        Room RoomFor(Peer p) => m_Rooms.TryGetValue(p.AppId ?? "", out var r) ? r : null;

        bool MayCreate(Peer p) => p.AppId != GameAppId || HostUserId == null || p.UserId == HostUserId;

        // Master server: tell the client where the game server is (here) and what the room is called.
        // There is one room per app: every join / create / random request ends up in it.
        void MatchOnMaster(Peer p, byte op, Request req)
        {
            var room = RoomFor(p);
            string name = room?.Name ?? (req.Get(255) as string);
            if (room == null)
            {
                bool create = op == 227 || req.Byte(215, 0) == 1;
                if (!create || !MayCreate(p))
                {
                    Respond(p, op, op == 225 ? (short)32760 : (short)32758,
                        "The host has not opened the game yet");
                    return;
                }
                if (string.IsNullOrEmpty(name)) name = Guid.NewGuid().ToString("N").Substring(0, 8);
            }
            else if (room.IsFull || !room.IsOpen)
            {
                Respond(p, op, room.IsFull ? (short)32765 : (short)32764, room.IsFull ? "Game full" : "Game closed");
                return;
            }
            var local = (IPEndPoint)p.Socket.LocalEndPoint;
            Respond(p, op, ps: P((230, local.Address + ":" + Port), (255, name)));
        }

        void JoinOnGameServer(Peer p, byte op, Request req)
        {
            if (p.Room != null) LeaveRoom(p);
            var room = RoomFor(p);
            if (room == null)
            {
                if (!MayCreate(p))
                {
                    Respond(p, op, 32758, "The host has not opened the game yet");
                    return;
                }
                room = new Room
                {
                    Name = req.Get(255) as string ?? Guid.NewGuid().ToString("N").Substring(0, 8),
                    AppId = p.AppId ?? "",
                    Flags = req.Int(191, 0),
                    CleanupCacheOnLeave = req.Bool(241, true),
                };
                var gp = req.Table(248);
                if (gp != null) foreach (var kv in gp) room.Props.Set(kv.Key, kv.Value);
                m_Rooms[room.AppId] = room;
                Log.Info($"LAN room '{room.Name}' created (app {Short(room.AppId)}, flags 0x{room.Flags:X}, max {room.MaxPlayers})");
            }
            else if (p.UserId != null && room.Banned.Contains(p.UserId))
            {
                Respond(p, op, 32758, "KICKED");
                return;
            }
            else if (room.IsFull)
            {
                Respond(p, op, 32765, "Game full");
                return;
            }
            else if (!room.IsOpen)
            {
                Respond(p, op, 32764, "Game closed");
                return;
            }

            int actor = room.NextActor++;
            p.Room = room;
            p.ActorNr = actor;
            p.ActorProps = new Props();
            p.Groups.Clear();
            p.AllGroups = false;
            var pp = req.Table(249);
            if (pp != null) foreach (var kv in pp) p.ActorProps.Set(kv.Key, kv.Value);
            if ((room.Flags & 8) != 0 && p.UserId != null) p.ActorProps.Set(253, p.UserId);   // PublishUserId
            p.NickName = Wire.Decode(p.ActorProps.Get(255) ?? new[] { Wire.Null }) as string;
            room.Actors[actor] = p;
            if (room.Master == 0 || !room.Actors.ContainsKey(room.Master)) room.Master = actor;

            var allProps = new List<KeyValuePair<object, object>>();
            foreach (var a in room.Actors.Values) allProps.Add(new KeyValuePair<object, object>(a.ActorNr, a.ActorProps));
            var actors = room.Actors.Keys.ToArray();
            Respond(p, op, ps: P((254, actor), (249, allProps), (248, room.GameProps()), (252, actors), (191, room.Flags)));
            Log.Info($"LAN '{p.NickName}' joined room '{room.Name}' as actor {actor} ({room.Actors.Count} players, master {room.Master})");

            if ((room.Flags & 4) == 0)   // SuppressRoomEvents
            {
                var join = EventFrame(255, P((254, actor), (249, p.ActorProps), (252, actors)));
                foreach (var a in room.Actors.Values) a.Send(join);
            }
            foreach (var e in room.Cache)
            {
                var ps = new List<KeyValuePair<byte, object>>();
                if (e.Data != null) ps.Add(new KeyValuePair<byte, object>(245, e.Data));
                ps.Add(new KeyValuePair<byte, object>(254, e.Sender));
                p.Send(EventFrame(e.Code, ps));
            }
        }

        void RaiseEvent(Peer p, Request req)
        {
            var room = p.Room;
            if (room == null) return;
            byte code = req.Byte(244, 0);
            byte cache = req.Byte(247, 0);
            var data = req.RawOf(245);
            var targets = req.Get(252) as int[];
            // PEAK: event 18 (user ID) is the host's "kick player", carried out by the server plugin on Photon Cloud;
            // the kicked player may not come back into this game ("KICKED", as the plugin answers)
            if (code == KickEvent && p.ActorNr == room.Master && data != null && Wire.Decode(data.Bytes) is string kickId)
            {
                if (kickId != p.UserId) room.Banned.Add(kickId);
                foreach (var kicked in room.Actors.Values.Where(a => a.UserId == kickId && a != p).ToList())
                {
                    Log.Info($"LAN host kicked '{kicked.NickName}' (actor {kicked.ActorNr})");
                    kicked.Close();
                    RemovePeer(kicked);
                }
                return;
            }
            switch (cache)
            {
                case 6:   // RemoveFromRoomCache
                {
                    var filter = data != null ? Wire.ReadHashtableRaw(data.Bytes, 0, data.Bytes.Length) : null;
                    int before = room.Cache.Count;
                    room.Cache.RemoveAll(e =>
                        (code == 0 || e.Code == code) &&
                        (targets == null || targets.Contains(e.Sender)) &&
                        (filter == null || filter.Count == 0 || e.Matches(filter)));
                    if (Verbose) Log.Info($"LAN cache: removed {before - room.Cache.Count} (code {code})");
                    return;
                }
                case 7:   // RemoveFromRoomCacheForActorsLeft
                    room.Cache.RemoveAll(e => !e.Global && !room.Actors.ContainsKey(e.Sender));
                    return;
                case 3:   // RemoveCache (old per-actor cache)
                    room.Cache.RemoveAll(e => !e.Global && e.Sender == p.ActorNr && (code == 0 || e.Code == code));
                    return;
                case 1: case 2: case 4:   // MergeCache / ReplaceCache / AddToRoomCache
                    if (cache == 2) room.Cache.RemoveAll(e => !e.Global && e.Sender == p.ActorNr && e.Code == code);
                    room.Cache.Add(new CachedEvent(code, p.ActorNr, data, false));
                    break;
                case 5:   // AddToRoomCacheGlobal
                    room.Cache.Add(new CachedEvent(code, p.ActorNr, data, true));
                    break;
                case 10: case 11: case 12: case 13:   // cache slices: not used by PUN
                    return;
            }

            var ps = new List<KeyValuePair<byte, object>>(2);
            if (data != null) ps.Add(new KeyValuePair<byte, object>(245, data));
            ps.Add(new KeyValuePair<byte, object>(254, p.ActorNr));
            var frame = EventFrame(code, ps);
            if (targets != null)
            {
                foreach (int t in targets)
                    if (room.Actors.TryGetValue(t, out var a)) a.Send(frame);
                return;
            }
            byte group = req.Byte(240, 0);
            if (group != 0)
            {
                foreach (var a in room.Actors.Values)
                    if (a != p && (a.AllGroups || a.Groups.Contains(group))) a.Send(frame);
                return;
            }
            switch (req.Byte(246, 0))
            {
                case 1:   // All
                    foreach (var a in room.Actors.Values) a.Send(frame);
                    break;
                case 2:   // MasterClient
                    if (room.Actors.TryGetValue(room.Master, out var m)) m.Send(frame);
                    break;
                default:  // Others
                    foreach (var a in room.Actors.Values) if (a != p) a.Send(frame);
                    break;
            }
        }

        void SetProperties(Peer p, Request req)
        {
            var room = p.Room;
            if (room == null) { Respond(p, 252, -3, "not in a room"); return; }
            int target = req.Int(254, 0);
            Props props;
            if (target == 0) props = room.Props;
            else if (room.Actors.TryGetValue(target, out var ta)) props = ta.ActorProps;
            else { Respond(p, 252, -2, "no such actor"); return; }
            var changes = req.Table(251) ?? new List<KeyValuePair<byte[], byte[]>>();
            var expected = req.Table(231);
            if (expected != null)
                foreach (var kv in expected)
                {
                    var cur = target == 0 ? room.GetProp(kv.Key) : props.Get(kv.Key);
                    if (!SameValue(cur, kv.Value))
                    {
                        Respond(p, 252, -2, "CAS update failed: property changed");
                        return;
                    }
                }
            bool deleteNull = (room.Flags & 0x10) != 0;
            foreach (var kv in changes)
            {
                if (target == 0 && kv.Key.Length == 2 && kv.Key[0] == Wire.Byte && kv.Key[1] == 248)
                {
                    // master client switch
                    if (Wire.Decode(kv.Value) is int nm && room.Actors.ContainsKey(nm))
                    {
                        Log.Info($"LAN master client -> actor {nm}");
                        room.Master = nm;
                    }
                    continue;
                }
                if (deleteNull && kv.Value.Length == 1 && kv.Value[0] == Wire.Null) props.Remove(kv.Key);
                else props.Set(kv.Key, kv.Value);
                if (target != 0 && kv.Key.Length == 2 && kv.Key[0] == Wire.Byte && kv.Key[1] == 255)
                    ((Peer)room.Actors[target]).NickName = Wire.Decode(kv.Value) as string;
            }
            Respond(p, 252);
            var ev = EventFrame(253, P((251, changes), (253, target), (254, p.ActorNr)));
            bool toAll = (room.Flags & 0x20) != 0;
            foreach (var a in room.Actors.Values)
                if (toAll || a != p) a.Send(ev);
        }

        static bool SameValue(byte[] a, byte[] b)
        {
            if (a == null) return b == null || (b.Length == 1 && b[0] == Wire.Null);
            if (b == null) return a.Length == 1 && a[0] == Wire.Null;
            if (a.SequenceEqual(b)) return true;
            object x = Wire.Decode(a), y = Wire.Decode(b);
            if (x is Raw || y is Raw) return false;
            return Equals(Normalize(x), Normalize(y));
        }

        static object Normalize(object o) => o switch { byte b => (long)b, short s => (long)s, int i => (long)i, _ => o };

        void ChangeGroups(Peer p, Request req)
        {
            if (req.Has(239))
            {
                var rm = req.Get(239) as byte[];
                if (rm == null || rm.Length == 0) { p.Groups.Clear(); p.AllGroups = false; }
                else foreach (var g in rm) p.Groups.Remove(g);
            }
            if (req.Has(238))
            {
                var add = req.Get(238) as byte[];
                if (add == null || add.Length == 0) p.AllGroups = true;
                else foreach (var g in add) p.Groups.Add(g);
            }
        }

        void LeaveRoom(Peer p)
        {
            var room = p.Room;
            if (room == null) return;
            p.Room = null;
            room.Actors.Remove(p.ActorNr);
            if (room.CleanupCacheOnLeave) room.Cache.RemoveAll(e => !e.Global && e.Sender == p.ActorNr);
            Log.Info($"LAN '{p.NickName}' (actor {p.ActorNr}) left room '{room.Name}' ({room.Actors.Count} left)");
            if (room.Actors.Count == 0)
            {
                m_Rooms.Remove(room.AppId);
                Log.Info($"LAN room '{room.Name}' closed");
                return;
            }
            var ps = P((254, p.ActorNr), (233, false));
            if (room.Master == p.ActorNr)
            {
                room.Master = room.Actors.Keys.Min();
                ps.Add(new KeyValuePair<byte, object>(203, room.Master));
                Log.Info($"LAN master client -> actor {room.Master}");
            }
            if ((room.Flags & 4) == 0)
            {
                var ev = EventFrame(254, ps);
                foreach (var a in room.Actors.Values) a.Send(ev);
            }
        }

        void RemovePeer(Peer p)
        {
            if (!m_Peers.Remove(p)) return;
            LeaveRoom(p);
            Log.Info($"LAN peer {p.Id} disconnected");
        }

        public static bool Verbose;

        static string Short(string s) => string.IsNullOrEmpty(s) ? "-" : s.Length > 8 ? s.Substring(0, 8) : s;

        // ---- state -----------------------------------------------------------------------------------
        sealed class Peer
        {
            public readonly LanServer Server;
            public readonly Socket Socket;
            public readonly int Id;
            readonly object m_SendLock = new object();
            public volatile bool Closed;
            public volatile int LastReceive;
            public string AppId, UserId, NickName;
            public Aes Crypto;
            public Room Room;
            public int ActorNr;
            public Props ActorProps = new Props();
            public readonly HashSet<byte> Groups = new HashSet<byte>();
            public bool AllGroups;

            public Peer(LanServer server, Socket s, int id)
            {
                Server = server;
                Socket = s;
                Id = id;
                LastReceive = server.ServerTime;
            }

            public void Send(byte[] frame) => SendRaw(frame);

            public void SendRaw(byte[] data)
            {
                if (Closed) return;
                try
                {
                    lock (m_SendLock)
                    {
                        int off = 0;
                        while (off < data.Length) off += Socket.Send(data, off, data.Length - off, SocketFlags.None);
                    }
                }
                catch (Exception e)
                {
                    Log.Warn($"LAN peer {Id}: send failed ({e.Message})");
                    Close();
                }
            }

            public void Close()
            {
                if (Closed) return;
                Closed = true;
                try { Socket.Shutdown(SocketShutdown.Both); } catch { }
                try { Socket.Close(); } catch { }
            }
        }

        sealed class Room
        {
            public string Name, AppId;
            public readonly SortedDictionary<int, Peer> Actors = new SortedDictionary<int, Peer>();
            public int NextActor = 1, Master, Flags;
            public bool CleanupCacheOnLeave = true;
            public readonly Props Props = new Props();
            public readonly List<CachedEvent> Cache = new List<CachedEvent>();
            public readonly HashSet<string> Banned = new HashSet<string>();

            public int MaxPlayers
            {
                get
                {
                    var v = Props.Get(243);
                    if (v != null && Wire.Decode(v) is int i && i > 0) return i;
                    v = Props.Get(255);
                    if (v != null && Wire.Decode(v) is byte b) return b;
                    return 0;
                }
            }

            public bool IsFull => MaxPlayers > 0 && Actors.Count >= MaxPlayers;
            public bool IsOpen => !(Props.Get(253) is byte[] v && Wire.Decode(v) is bool b && !b);

            public byte[] GetProp(byte[] key)
            {
                if (key.Length == 2 && key[0] == Wire.Byte && key[1] == 248)
                {
                    var w = new WireWriter();
                    w.Value(Master);
                    return w.ToArray();
                }
                return Props.Get(key);
            }

            public Props GameProps()
            {
                var gp = Props.Clone();
                gp.Set(248, Master);
                return gp;
            }
        }

        sealed class CachedEvent
        {
            public readonly byte Code;
            public readonly int Sender;
            public readonly Raw Data;
            public readonly bool Global;
            Dictionary<string, byte[]> m_Table;

            public CachedEvent(byte code, int sender, Raw data, bool global)
            {
                Code = code;
                Sender = sender;
                Data = data;
                Global = global;
            }

            // RemoveFromRoomCache filter: the cached data is a hashtable containing every filter entry.
            public bool Matches(List<KeyValuePair<byte[], byte[]>> filter)
            {
                if (Data == null || Data.Bytes.Length == 0 || Data.Bytes[0] != Wire.Hashtable) return false;
                if (m_Table == null)
                {
                    m_Table = new Dictionary<string, byte[]>();
                    try
                    {
                        foreach (var kv in Wire.ReadHashtableRaw(Data.Bytes, 0, Data.Bytes.Length))
                            m_Table[KeyId(kv.Key)] = kv.Value;
                    }
                    catch (Exception e)
                    {
                        Log.Warn("LAN cache filter: " + e.Message);
                    }
                }
                foreach (var kv in filter)
                    if (!m_Table.TryGetValue(KeyId(kv.Key), out var v) || !SameValue(v, kv.Value)) return false;
                return true;
            }

            // keys compare by value (an int key may be encoded in different widths)
            static string KeyId(byte[] raw)
            {
                object o = Wire.Decode(raw);
                return o is Raw ? "r:" + Wire.Key(raw) : (o?.GetType().Name ?? "null") + ":" + Normalize(o);
            }
        }
    }
}
