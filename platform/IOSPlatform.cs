using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using PeakShim;
using Portningsbolaget.Platforms;
using UnityEngine;

namespace PeakPlatform
{
    /// <summary>
    /// The game's platform layer on iOS, in place of SteamRuntimeManager (the patcher changes the
    /// "new SteamRuntimeManager()" in PlatformManager to "new IOSPlatform()"). No Steam, no servers:
    /// name and ID are local (PeakShim.Identity). Steam kept achievements and stats (they unlock badges and
    /// cosmetics, and hold the highest ascent); here they live in Documents/PEAK-stats.txt. Game progress
    /// and other platform files go to Documents/Platform, and text dialogs use the iOS keyboard.
    /// </summary>
    public class IOSPlatform : IPlatform
    {
        public bool Initialized => true;
        public string NickName => Identity.NickName;
        public ulong UserID => Identity.UserID;
        public string FileOutputDirectory => Paths.Documents;
        public PlatformUtility.PlatformFamily PlatformFamily => PlatformUtility.PlatformFamily.IOS;
        public Action<bool> OnStatsReceived { get; set; }
        public Action<string, string> OnJoinedSession { get; set; }
        public Action<bool> OnSignedInStateChanged { get; set; }

        public void InitializeAfterAssembliesLoaded() => Debug.Log("[PEAK-iOS] iOS platform (no Steam): " + NickName + " / " + UserID);
        public void InitializeAfterSceneLoad() { }
        public void Teardown() => SaveStats();

        // ---- achievements and stats (Documents/PEAK-stats.txt: "a:<name>=1" / "s:<name>=<value>") ------------
        static readonly Dictionary<string, int> s_Stats = new Dictionary<string, int>();
        static readonly HashSet<string> s_Achievements = new HashSet<string>();
        static bool s_StatsLoaded, s_StatsDirty;
        static string StatsPath => Path.Combine(Paths.Documents, "PEAK-stats.txt");

        static void LoadStats()
        {
            if (s_StatsLoaded) return;
            s_StatsLoaded = true;
            try
            {
                if (!File.Exists(StatsPath)) return;
                foreach (var line in File.ReadAllLines(StatsPath))
                {
                    int eq = line.IndexOf('=');
                    if (line.Length < 3 || line[1] != ':' || eq < 0) continue;
                    string key = line.Substring(2, eq - 2);
                    if (line[0] == 'a') s_Achievements.Add(key);
                    else if (line[0] == 's' && int.TryParse(line.Substring(eq + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)) s_Stats[key] = v;
                }
                Debug.Log($"[PEAK-iOS] stats loaded: {s_Stats.Count} stats, {s_Achievements.Count} achievements");
            }
            catch (Exception e) { Debug.LogWarning("[PEAK-iOS] stats load: " + e.Message); }
        }

        static void SaveStats()
        {
            if (!s_StatsDirty) return;
            try
            {
                var sb = new StringBuilder();
                foreach (var a in s_Achievements) sb.Append("a:").Append(a).Append("=1\n");
                foreach (var kv in s_Stats) sb.Append("s:").Append(kv.Key).Append('=').Append(kv.Value.ToString(CultureInfo.InvariantCulture)).Append('\n');
                string tmp = StatsPath + ".tmp";
                File.WriteAllText(tmp, sb.ToString());
                if (File.Exists(StatsPath)) File.Delete(StatsPath);
                File.Move(tmp, StatsPath);
                s_StatsDirty = false;
            }
            catch (Exception e) { Debug.LogWarning("[PEAK-iOS] stats save: " + e.Message); }
        }

        public void UnlockAchievement(string achievement)
        {
            LoadStats();
            if (string.IsNullOrEmpty(achievement) || !s_Achievements.Add(achievement)) return;
            s_StatsDirty = true;
            SaveStats();
            Debug.Log("[PEAK-iOS] achievement unlocked: " + achievement);
        }

        public void ProgressAchievement(string achievement, int amount)
        {
            if (GetStat(achievement, out int v)) SetStat(achievement, v + amount);
        }

        public void GetAchievementState(string achievement, out bool achieved)
        {
            LoadStats();
            achieved = achievement != null && s_Achievements.Contains(achievement);
        }

        public void ClearAchievementsProgress() { }
        public void RequestStats() { LoadStats(); OnStatsReceived?.Invoke(true); }
        public void RequestStats(string[] achievements) => RequestStats();
        public void StoreStats() => SaveStats();
        public void InitStats(Tuple<string, int>[] targets) { }

        public bool GetStat(string achievement, out int value)
        {
            LoadStats();
            if (achievement == null) { value = 0; return false; }
            s_Stats.TryGetValue(achievement, out value);
            return true;
        }

        public bool SetStat(string achievement, int value)
        {
            LoadStats();
            if (achievement == null) return false;
            if (!s_Stats.TryGetValue(achievement, out int old) || old != value) { s_Stats[achievement] = value; s_StatsDirty = true; }
            return true;
        }

        // ---- game progress (same format and file name as the Steam version, which never saved it) ------------
        readonly Dictionary<string, int> m_Progress = new Dictionary<string, int>();
        const string ProgressFile = "GameProgress";

        public void SaveProgress(Action<FileResult> callback)
        {
            var sb = new StringBuilder();
            foreach (var kv in m_Progress) sb.Append(kv.Key).Append('.').Append(kv.Value.ToString(CultureInfo.InvariantCulture)).Append('\n');
            RequestSave(ProgressFile, Encoding.ASCII.GetBytes(sb.ToString()), callback);
        }

        public void LoadProgress(Action<FileResult> callback)
        {
            var e = LoadProgressAsync(callback);
            while (e.MoveNext()) { }
        }

        public IEnumerator LoadProgressAsync(Action<FileResult> callback)
        {
            if (m_Progress.Count > 0) yield break;
            Load(ProgressFile, -1, out var r, out var bytes);
            if (r == FileResult.Succeeded)
                foreach (var line in Encoding.ASCII.GetString(bytes).Split('\n'))
                {
                    var parts = line.Trim().Split('.');
                    if (parts.Length == 2 && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)) m_Progress[parts[0]] = v;
                }
            callback?.Invoke(r);
        }

        public bool GetProgress(string name, out int progress, bool fromPrefs = false)
        {
            if (fromPrefs) { progress = Prefs.GetInt(UserID + "-" + name, 0); return true; }
            if (!m_Progress.TryGetValue(name, out progress)) progress = 0;
            return true;
        }

        public bool SetProgress(string name, int progress, bool toPrefs = false)
        {
            if (string.IsNullOrEmpty(name)) return false;
            if (toPrefs) { Prefs.SetInt(UserID + "-" + name, progress); Prefs.Save(); return true; }
            if (m_Progress.TryGetValue(name, out int v) && v == progress) return false;
            m_Progress[name] = progress;
            return true;
        }

        // ---- text dialog (room code, names) on the iOS keyboard ---------------------------------------------
        TouchScreenKeyboard m_Keyboard;
        Action<string, DialogueResult> m_OnDone;

        public void OpenDialog(string title, DialogType dialogType, Action<string, DialogueResult> onDone)
        {
            try
            {
                if (m_Keyboard != null) { m_Keyboard.active = false; Finish(string.Empty, DialogueResult.Aborted); }
                m_OnDone = onDone;
                m_Keyboard = TouchScreenKeyboard.Open(string.Empty, TouchScreenKeyboardType.Default, false,
                    dialogType == DialogType.Chat, false, false, title ?? string.Empty,
                    dialogType == DialogType.Chat ? 200 : dialogType == DialogType.JoinRoom ? 6 : 20);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[PEAK-iOS] keyboard: " + e.Message);
                m_OnDone = null;
                onDone?.Invoke(string.Empty, DialogueResult.Failed);
            }
        }

        void Finish(string text, DialogueResult r)
        {
            var cb = m_OnDone;
            m_OnDone = null;
            m_Keyboard = null;
            try { cb?.Invoke(text, r); } catch (Exception e) { Debug.LogException(e); }
        }

        public void Update()
        {
            if (s_StatsDirty && Time.frameCount % 600 == 0) SaveStats();
            if (m_Keyboard == null) return;
            switch (m_Keyboard.status)
            {
                case TouchScreenKeyboard.Status.Done: Finish(m_Keyboard.text ?? string.Empty, DialogueResult.Succeeded); break;
                case TouchScreenKeyboard.Status.Canceled:
                case TouchScreenKeyboard.Status.LostFocus: Finish(string.Empty, DialogueResult.Aborted); break;
            }
        }

        // ---- online checks: the iOS app never talks to platform or Landfall servers ----------------------------
        public void FilterProfanity(string s, Action<string> callback) => callback?.Invoke(s);
        public void HasInternetConnection(Action<bool> callback) => callback?.Invoke(true);
        public void CanPlayMultiplayer(Action<bool> callback) => callback?.Invoke(true);
        public void CanCrossPlay(Action<bool> callback) => callback?.Invoke(true);
        public void CanCommunicate(Action<bool> callback) => callback?.Invoke(true);
        public void CanCommunicateWithUser(ulong targetUserID, Action<bool> callback) => callback?.Invoke(true);
        public void GetAvatarAsync(Action<Texture2D> callback) => callback?.Invoke(null);
        public void GetAvatarAsync(ulong xuid, Action<Texture2D> callback) => callback?.Invoke(null);

        // ---- platform files in Documents/Platform ------------------------------------------------------------
        static string FilePath(string fileName, int slot)
        {
            string dir = Path.Combine(Paths.Documents, "Platform");
            Directory.CreateDirectory(dir);
            string safe = string.Concat((fileName ?? "file").Split(Path.GetInvalidFileNameChars()));
            return Path.Combine(dir, slot >= 0 ? safe + "_" + slot : safe);
        }

        public void RequestSaveExists(string fileName, Action<FileResult> done) => RequestSaveExists(fileName, -1, done);
        public void RequestSaveExists(string fileName, int saveSlot, Action<FileResult> done)
        {
            FileResult r;
            try { r = File.Exists(FilePath(fileName, saveSlot)) ? FileResult.Succeeded : FileResult.NotFound; }
            catch (Exception) { r = FileResult.Failed; }
            done?.Invoke(r);
        }

        public void RequestSave(string fileName, byte[] data, Action<FileResult> done) => RequestSave(fileName, data, -1, done);
        public void RequestSave(string fileName, byte[] data, int saveSlot, Action<FileResult> done)
        {
            FileResult r;
            try { File.WriteAllBytes(FilePath(fileName, saveSlot), data ?? new byte[0]); r = FileResult.Succeeded; }
            catch (Exception e) { Debug.LogWarning("[PEAK-iOS] save " + fileName + ": " + e.Message); r = FileResult.Failed; }
            done?.Invoke(r);
        }

        public void RequestLoadAsync(string fileName, Action<FileResult, byte[]> done) => RequestLoadAsync(fileName, -1, done);
        public void RequestLoadAsync(string fileName, int saveSlot, Action<FileResult, byte[]> done)
        {
            Load(fileName, saveSlot, out var r, out var data);
            done?.Invoke(r, data);
        }

        public IEnumerator RequestLoad(string fileName, Action<FileResult, byte[]> done) => RequestLoad(fileName, -1, done);
        public IEnumerator RequestLoad(string fileName, int saveSlot, Action<FileResult, byte[]> done)
        {
            Load(fileName, saveSlot, out var r, out var data);
            done?.Invoke(r, data);
            yield break;
        }

        static void Load(string fileName, int slot, out FileResult r, out byte[] data)
        {
            data = null;
            try
            {
                string p = FilePath(fileName, slot);
                if (!File.Exists(p)) { r = FileResult.NotFound; return; }
                data = File.ReadAllBytes(p);
                r = FileResult.Succeeded;
            }
            catch (Exception e) { Debug.LogWarning("[PEAK-iOS] load " + fileName + ": " + e.Message); r = FileResult.Failed; }
        }

        public void RequestDelete(string fileName, Action<FileResult> done) => RequestDelete(fileName, -1, done);
        public void RequestDelete(string fileName, int saveSlot, Action<FileResult> done)
        {
            FileResult r;
            try { File.Delete(FilePath(fileName, saveSlot)); r = FileResult.Succeeded; }
            catch (Exception) { r = FileResult.Failed; }
            done?.Invoke(r);
        }
    }
}
