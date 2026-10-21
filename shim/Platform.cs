using System;
using System.Diagnostics;
using System.IO;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace PeakShim
{
    /// <summary>Stand-ins for desktop-only calls the patcher redirects here.</summary>
    public static class Platform
    {
        // ---- System.Diagnostics.Process: there are no other processes on iOS ------------------
        public static Process ProcessStart(ProcessStartInfo info)
        {
            Debug.LogWarning("[PEAK-iOS] Process.Start blocked: " + (info?.FileName ?? "<null>") + " " + (info?.Arguments ?? ""));
            return null;
        }
        public static Process ProcessStart(string file)
        {
            if (file != null && (file.StartsWith("http://") || file.StartsWith("https://")))
            {
                Debug.LogWarning("[PEAK-iOS] Process.Start(url) blocked: " + file);
                return null;
            }
            Debug.LogWarning("[PEAK-iOS] Process.Start blocked: " + file);
            return null;
        }
        /// RecordingsHandler "show in folder" (explorer.exe /select,...): the videos are in Files > PEAK.
        public static Process ProcessStart(string file, string args)
        {
            Debug.Log("[PEAK-iOS] Process.Start blocked: " + file + " " + args + " (videos are in Files > On My iPhone > PEAK > Videos)");
            return null;
        }
        public static bool ProcessStartInstance(Process p)
        {
            Debug.LogWarning("[PEAK-iOS] Process.Start blocked: " + (p?.StartInfo?.FileName ?? "<null>"));
            return false;
        }
        public static Process[] GetProcessesByName(string name) => new Process[0];

        // ---- Application ------------------------------------------------------------------------
        /// Set by the mod: flushes the log before the app goes away.
        public static Action BeforeQuit;

        public static void Quit()
        {
            Debug.Log("[PEAK-iOS] Application.Quit requested by the game");
            try { BeforeQuit?.Invoke(); } catch { }
            Application.Quit();
        }
        public static void Quit(int code) => Quit();

        // ---- Screen: the iOS player always runs fullscreen at the native resolution; the render scale is
        // an iOS option, so the game's desktop resolution / window options do nothing. -------------------
        public static void SetResolution(int width, int height, bool fullscreen)
        {
            Debug.Log($"[PEAK-iOS] Screen.SetResolution({width}x{height}) ignored on iOS");
        }
        public static void SetResolution(int width, int height, FullScreenMode mode)
        {
            Debug.Log($"[PEAK-iOS] Screen.SetResolution({width}x{height}, {mode}) ignored on iOS");
        }
        public static void SetFullScreen(bool fullscreen) { }
        public static bool GetFullScreen() => true;

        // ---- frame pacing ----------------------------------------------------------------------
        // iOS ignores QualitySettings.vSyncCount and runs at 30 fps unless targetFrameRate is set, so the
        // game's VSync / Max FPS options are mapped onto "match the display" capped by the user's iOS choice.
        static int vSyncCount = 1;
        static int requestedFps = -1;

        /// Highest refresh rate the display supports (120 on ProMotion iPhones).
        public static int DisplayMaxFps
        {
            get
            {
                int r = (int)Math.Round(Screen.currentResolution.refreshRateRatio.value);
                return r >= 30 ? r : 60;
            }
        }

        /// Cap set by the iOS mod (0 = no cap), default 60.
        public static int UserFpsCap = 60;

        public static int EffectiveTargetFps
        {
            get
            {
                int max = DisplayMaxFps;
                if (UserFpsCap > 0) max = Math.Min(max, UserFpsCap);
                if (vSyncCount > 0 || requestedFps <= 0) return max;
                return Math.Max(30, Math.Min(requestedFps, max));
            }
        }

        public static void SetVSyncCount(int v)
        {
            vSyncCount = v;
            ApplyFrameRate();
        }

        public static void SetTargetFrameRate(int fps)
        {
            requestedFps = fps;
            ApplyFrameRate();
        }

        public static void ApplyFrameRate()
        {
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = EffectiveTargetFps;
        }
    }

    /// <summary>Cursor state as the game sets it. iOS has no cursor; the touch layer reads this to tell
    /// gameplay (locked: stick + look + buttons) from menus (unlocked: touch acts as the mouse).</summary>
    public static class VCursor
    {
        public static CursorLockMode LockState = CursorLockMode.None;
        public static bool Visible = true;
        /// Frame (VInput.Frame) of the last change, so the touch layer can react once.
        public static int ChangedFrame;

        public static void set_lockState(CursorLockMode m)
        {
            if (m != LockState) ChangedFrame = VInput.Frame;
            LockState = m;
        }
        public static CursorLockMode get_lockState() => LockState;
        public static void set_visible(bool v)
        {
            if (v != Visible) ChangedFrame = VInput.Frame;
            Visible = v;
        }
        public static bool get_visible() => Visible;
    }

    /// <summary>Where things live on iOS. Everything the user can see or edit is under Documents
    /// (visible in Files > On My iPhone > PEAK): log, settings, saves, exported videos.</summary>
    public static class Paths
    {
        static string documents;
        public static string Documents
        {
            get
            {
                if (documents == null)
                {
                    documents = Application.persistentDataPath;
                    try { Directory.CreateDirectory(documents); } catch { }
                }
                return documents;
            }
        }

        /// Exported videos (the PC game saves them to the Desktop).
        public static string Videos
        {
            get
            {
                string p = Path.Combine(Documents, "Videos");
                try { Directory.CreateDirectory(p); } catch { }
                return p;
            }
        }

        /// Environment.GetFolderPath replacement: every desktop folder maps to Documents/Videos or Documents.
        public static string GetFolderPath(Environment.SpecialFolder folder)
        {
            switch (folder)
            {
                case Environment.SpecialFolder.Desktop:
                case Environment.SpecialFolder.DesktopDirectory:
                case Environment.SpecialFolder.MyVideos:
                    return Videos;
            }
            return Documents;
        }
    }

    /// <summary>
    /// UnityEngine.Microphone replacement. With <see cref="Enabled"/> off (iOS option "Microphone") the game
    /// sees no microphone at all, so iOS never asks for permission and voice is never recorded. With it on,
    /// everything forwards to Unity (the first Start shows the iOS permission prompt).
    /// </summary>
    public static class Mic
    {
        public static bool Enabled = false;   // voice chat is opt-in (off until the player turns it on)

        public static string[] devices
        {
            get
            {
                if (!Enabled) return new string[0];
                try { return Microphone.devices; } catch (Exception) { return new string[0]; }
            }
        }
        public static AudioClip Start(string deviceName, bool loop, int lengthSec, int frequency)
        {
            if (!Enabled) return null;
            try { return Microphone.Start(deviceName, loop, lengthSec, frequency); }
            catch (Exception e) { Debug.LogWarning("[PEAK-iOS] Microphone.Start failed: " + e.Message); return null; }
        }
        public static void End(string deviceName)
        {
            try { Microphone.End(deviceName); } catch (Exception) { }
        }
        public static bool IsRecording(string deviceName) => Enabled && Microphone.IsRecording(deviceName);
        public static int GetPosition(string deviceName) => Enabled ? Microphone.GetPosition(deviceName) : 0;
        public static void GetDeviceCaps(string deviceName, out int minFreq, out int maxFreq)
        {
            if (!Enabled) { minFreq = maxFreq = 0; return; }
            Microphone.GetDeviceCaps(deviceName, out minFreq, out maxFreq);
        }
    }
}
