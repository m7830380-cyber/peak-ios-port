using System;
using System.Runtime.InteropServices;
using PeakShim;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PeakIOS
{
    // Entry point of the iOS additions (RuntimeInitializeOnLoadMethod: listed in RuntimeInitializeOnLoads.json
    // by the shell build, merged with the game's own entries by package_ipa.py).
    public static class Bootstrap
    {
        public static bool OnDevice => Application.platform == RuntimePlatform.IPhonePlayer;

        [DllImport("__Internal")] static extern void PeakCrash_Install(string logPath);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void EarlyInit()
        {
            string docs = Paths.Documents;
            Log.Init(docs);
            if (OnDevice)
            {
                try { PeakCrash_Install(Log.FilePath); }
                catch (Exception e) { Log.Warn("native crash logger unavailable: " + e.Message); }
            }
            Log.Info($"PEAK iOS starting. documents={docs}");
            // GameHandler writes Saves/metaData.m before SaveSystem creates the folder on a first run
            try { System.IO.Directory.CreateDirectory(Application.persistentDataPath + "/Saves"); }
            catch (Exception e) { Log.Warn("could not create Saves folder: " + e.Message); }
            if (Log.PreviousRunEnding != null) Log.Info("previous run: " + Log.PreviousRunEnding);
            Log.Info($"device={SystemInfo.deviceModel} os={SystemInfo.operatingSystem} gpu={SystemInfo.graphicsDeviceName} " +
                     $"api={SystemInfo.graphicsDeviceType} mem={SystemInfo.systemMemorySize}MB gfxmem={SystemInfo.graphicsMemorySize}MB " +
                     $"cpu={SystemInfo.processorCount}x screen={Screen.width}x{Screen.height}@{Screen.currentResolution.refreshRateRatio.value:0}Hz " +
                     $"dpi={Screen.dpi} safe={Screen.safeArea} unity={Application.unityVersion} " +
                     $"astc={SystemInfo.SupportsTextureFormat(TextureFormat.ASTC_4x4)} dxt={SystemInfo.SupportsTextureFormat(TextureFormat.DXT5)} " +
                     $"bc6h={SystemInfo.SupportsTextureFormat(TextureFormat.BC6H)} compute={SystemInfo.supportsComputeShaders}");
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Init()
        {
            try
            {
                Input.simulateMouseWithTouches = false;
                Input.multiTouchEnabled = true;
                Screen.sleepTimeout = SleepTimeout.NeverSleep;
                Platform.ApplyFrameRate();
                Platform.BeforeQuit = Log.Flush;
                Screen.autorotateToPortrait = Screen.autorotateToPortraitUpsideDown = false;
                Screen.autorotateToLandscapeLeft = Screen.autorotateToLandscapeRight = true;
                Screen.orientation = ScreenOrientation.AutoRotation;
                GraphicsSetup.Configure();

                var go = new GameObject("PEAK-iOS");
                UnityEngine.Object.DontDestroyOnLoad(go);
                go.AddComponent<DebugOverlay>();
                go.AddComponent<SettingsPanel>();
                go.AddComponent<RenderDiag>();
                IosSettings.Init();
                go.AddComponent<Lifecycle>();
                if (OnDevice)
                {
                    VInput.Virtual = true;
                    VirtualInput.Init();
                    go.AddComponent<TouchLayer>();
                }
                Log.Info($"touch controls={OnDevice} targetFrameRate={Application.targetFrameRate}");

                SceneManager.sceneLoaded += (s, mode) =>
                {
                    Log.Info($"scene loaded: '{s.name}' ({mode}) roots={s.rootCount} " +
                             $"mem={UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong() >> 20}MB " +
                             $"mono={UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong() >> 20}MB");
                    Platform.ApplyFrameRate();
                };
                Application.lowMemory += () => Log.Warn($"iOS low memory warning (allocated {UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong() >> 20}MB)");
            }
            catch (Exception e)
            {
                Log.Error("PEAK-iOS init failed: " + e);
            }
        }

        sealed class Lifecycle : MonoBehaviour
        {
            void OnApplicationPause(bool paused)
            {
                Log.Info(paused ? "app paused (background)" : "app resumed");
                if (paused) { VInput.ReleaseAll(); Prefs.Save(); Log.Flush(); }
            }
            void OnApplicationQuit() => Log.Info("app quitting");
        }
    }
}
