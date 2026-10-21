using System;
using System.IO;
using UnityEngine;

namespace PeakShim
{
    /// <summary>
    /// The local player without Steam: a name (Documents/PlayerName.txt, editable in Files) and a stable random
    /// 64-bit ID (Documents/PlayerId.txt). The iOS platform (PeakPlatform) and the patched Steamworks stubs
    /// (SteamFriends.GetPersonaName, SteamUser.GetSteamID) both report these.
    /// </summary>
    public static class Identity
    {
        static string name;
        static ulong id;

        public static string NickName
        {
            get
            {
                if (name != null) return name;
                name = "Player";
                try
                {
                    string p = Path.Combine(Paths.Documents, "PlayerName.txt");
                    if (File.Exists(p))
                    {
                        string n = File.ReadAllText(p).Trim();
                        int nl = n.IndexOfAny(new[] { '\r', '\n' });
                        if (nl >= 0) n = n.Substring(0, nl).Trim();
                        if (n.Length > 0) name = n.Length > 32 ? n.Substring(0, 32) : n;
                    }
                    else File.WriteAllText(p, name + "\n");
                }
                catch (Exception e) { Debug.LogWarning("[PEAK-iOS] player name: " + e.Message); }
                return name;
            }
        }

        public static void SetNickName(string n)
        {
            n = (n ?? "").Trim();
            if (n.Length == 0) return;
            if (n.Length > 32) n = n.Substring(0, 32);
            name = n;
            try { File.WriteAllText(Path.Combine(Paths.Documents, "PlayerName.txt"), n + "\n"); }
            catch (Exception e) { Debug.LogWarning("[PEAK-iOS] player name: " + e.Message); }
        }

        /// 17-digit number like a Steam ID, never 0, created once.
        public static ulong UserID
        {
            get
            {
                if (id != 0) return id;
                string p = Path.Combine(Paths.Documents, "PlayerId.txt");
                try
                {
                    if (File.Exists(p) && ulong.TryParse(File.ReadAllText(p).Trim(), out ulong v) && v != 0) return id = v;
                }
                catch (Exception e) { Debug.LogWarning("[PEAK-iOS] player id: " + e.Message); }
                var r = new System.Random(Guid.NewGuid().GetHashCode());
                id = 10000000000000000UL + (ulong)(r.NextDouble() * 89999999999999999.0);
                try { File.WriteAllText(p, id + "\n"); }
                catch (Exception e) { Debug.LogWarning("[PEAK-iOS] player id: " + e.Message); }
                return id;
            }
        }
    }
}
