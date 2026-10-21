using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace PeakShim
{
    /// <summary>
    /// Replacement for UnityEngine.PlayerPrefs that keeps settings in Documents/PEAK-settings.txt
    /// (one "type key=value" line each), so they are visible and editable in Files / Filza and survive
    /// reinstalls of the same app. The iOS mod stores its own options here too.
    /// </summary>
    public static class Prefs
    {
        const string FileName = "PEAK-settings.txt";
        static Dictionary<string, (char type, string value)> data;
        static bool dirty;

        static string FilePath => Path.Combine(Paths.Documents, FileName);
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        static Dictionary<string, (char type, string value)> Data
        {
            get
            {
                if (data != null) return data;
                data = new Dictionary<string, (char, string)>();
                try
                {
                    if (File.Exists(FilePath))
                        foreach (var raw in File.ReadAllLines(FilePath))
                        {
                            var line = raw.Trim();
                            if (line.Length < 3 || line[0] == '#' || line[1] != ' ') continue;
                            int eq = line.IndexOf('=');
                            if (eq < 3) continue;
                            data[Unescape(line.Substring(2, eq - 2))] = (line[0], Unescape(line.Substring(eq + 1)));
                        }
                }
                catch (Exception e) { Debug.LogWarning("[PEAK-iOS] could not read " + FileName + ": " + e.Message); }
                return data;
            }
        }

        static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\n", "\\n").Replace("=", "\\e");
        static string Unescape(string s)
        {
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '\\' && i + 1 < s.Length)
                {
                    char c = s[++i];
                    sb.Append(c == 'n' ? '\n' : c == 'e' ? '=' : c);
                }
                else sb.Append(s[i]);
            }
            return sb.ToString();
        }

        static void Set(string key, char type, string value)
        {
            if (key == null) return;
            if (Data.TryGetValue(key, out var old) && old.type == type && old.value == value) return;
            Data[key] = (type, value);
            dirty = true;
            Save();
        }

        public static void Save()
        {
            if (!dirty || data == null) return;
            try
            {
                var sb = new StringBuilder("# PEAK iOS settings (i = int, f = float, s = text). Delete this file to reset.\n");
                foreach (var kv in data) sb.Append(kv.Value.type).Append(' ').Append(Escape(kv.Key)).Append('=').Append(Escape(kv.Value.value)).Append('\n');
                string tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, sb.ToString());
                if (File.Exists(FilePath)) File.Delete(FilePath);
                File.Move(tmp, FilePath);
                dirty = false;
            }
            catch (Exception e) { Debug.LogWarning("[PEAK-iOS] could not write " + FileName + ": " + e.Message); }
        }

        public static void SetInt(string key, int value) => Set(key, 'i', value.ToString(Inv));
        public static void SetFloat(string key, float value) => Set(key, 'f', value.ToString("R", Inv));
        public static void SetString(string key, string value) => Set(key, 's', value ?? "");

        public static int GetInt(string key, int defaultValue)
        {
            if (key != null && Data.TryGetValue(key, out var v))
            {
                if (int.TryParse(v.value, NumberStyles.Integer, Inv, out int i)) return i;
                if (float.TryParse(v.value, NumberStyles.Float, Inv, out float f)) return (int)f;
            }
            return defaultValue;
        }
        public static int GetInt(string key) => GetInt(key, 0);
        public static float GetFloat(string key, float defaultValue)
        {
            if (key != null && Data.TryGetValue(key, out var v) && float.TryParse(v.value, NumberStyles.Float, Inv, out float f)) return f;
            return defaultValue;
        }
        public static float GetFloat(string key) => GetFloat(key, 0f);
        public static string GetString(string key, string defaultValue) =>
            key != null && Data.TryGetValue(key, out var v) ? v.value : defaultValue;
        public static string GetString(string key) => GetString(key, "");
        public static bool HasKey(string key) => key != null && Data.ContainsKey(key);
        public static void DeleteKey(string key)
        {
            if (key != null && Data.Remove(key)) { dirty = true; Save(); }
        }
        public static void DeleteAll()
        {
            Data.Clear();
            dirty = true;
            Save();
        }
    }
}
