using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Callbacks;
using UnityEditor.iOS.Xcode;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

// Builds the iOS player "shell" with Unity 6000.3.15f1 (the game's exact version): an IL2CPP player that
// contains the patched retail PEAK assemblies (Assets/Plugins/Game, from the private repo), the iOS
// mod and the native helpers (Plugins/iOS/*.mm). The game's own data files are swapped into the .app afterwards
// by tools/package_ipa.py (privately, on the user's PC / private repo).
public static class IosShellBuild
{
    const string ScenePath = "Assets/Scenes/Boot.unity";
    static readonly NamedBuildTarget Target = NamedBuildTarget.iOS;

    public static void Build()
    {
        string outDir = Environment.GetEnvironmentVariable("SHELL_OUT") ?? "Build/iOS";

        if (!File.Exists(ScenePath))
        {
            Directory.CreateDirectory("Assets/Scenes");
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            EditorSceneManager.SaveScene(scene, ScenePath);
        }

        EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.iOS, BuildTarget.iOS);

        PlayerSettings.companyName = "LandCrab";
        PlayerSettings.productName = "PEAK";
        PlayerSettings.SetApplicationIdentifier(Target, "com.peak.iosport");
        PlayerSettings.bundleVersion = "2.6.0";          // Unity only accepts digits here; OnPostprocess writes the retail "2.6.b"
        PlayerSettings.iOS.buildNumber = Environment.GetEnvironmentVariable("GITHUB_RUN_NUMBER") ?? "1";
        PlayerSettings.SetScriptingBackend(Target, ScriptingImplementation.IL2CPP);
        // link.xml keeps every game assembly whole; Minimal is the least stripping IL2CPP allows.
        PlayerSettings.SetManagedStrippingLevel(Target, ManagedStrippingLevel.Minimal);
        PlayerSettings.stripEngineCode = false;
        PlayerSettings.SetIl2CppCompilerConfiguration(Target, Il2CppCompilerConfiguration.Release);
        PlayerSettings.SetApiCompatibilityLevel(Target, ApiCompatibilityLevel.NET_Unity_4_8);
        PlayerSettings.allowUnsafeCode = true;
        PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.iOS, false);
        PlayerSettings.SetGraphicsAPIs(BuildTarget.iOS, new[] { GraphicsDeviceType.Metal });
        PlayerSettings.colorSpace = ColorSpace.Linear;      // same as the retail game (URP)
        PlayerSettings.gpuSkinning = true;                  // same as the retail game
        PlayerSettings.MTRendering = true;
        PlayerSettings.iOS.targetOSVersionString = "15.0";
        PlayerSettings.iOS.sdkVersion = iOSSdkVersion.DeviceSDK;
        PlayerSettings.iOS.targetDevice = iOSTargetDevice.iPhoneAndiPad;
        PlayerSettings.iOS.requiresFullScreen = true;
        PlayerSettings.iOS.hideHomeButton = true;
        PlayerSettings.iOS.deferSystemGesturesMode = UnityEngine.iOS.SystemGestureDeferMode.All;
        PlayerSettings.iOS.microphoneUsageDescription = "PEAK uses the microphone for proximity voice chat with other players. It is off unless you turn it on in the iOS settings of the game.";
        PlayerSettings.iOS.appInBackgroundBehavior = iOSAppInBackgroundBehavior.Suspend;
        PlayerSettings.statusBarHidden = true;
        PlayerSettings.accelerometerFrequency = 0;
        PlayerSettings.defaultInterfaceOrientation = UIOrientation.AutoRotation;
        PlayerSettings.allowedAutorotateToLandscapeLeft = true;
        PlayerSettings.allowedAutorotateToLandscapeRight = true;
        PlayerSettings.allowedAutorotateToPortrait = false;
        PlayerSettings.allowedAutorotateToPortraitUpsideDown = false;
        PlayerSettings.SplashScreen.show = false;
        PlayerSettings.SplashScreen.showUnityLogo = false;
        PlayerSettings.enableInternalProfiler = false;
        PlayerSettings.SetStackTraceLogType(LogType.Log, StackTraceLogType.None);
        PlayerSettings.SetStackTraceLogType(LogType.Warning, StackTraceLogType.None);
        PlayerSettings.SetStackTraceLogType(LogType.Error, StackTraceLogType.ScriptOnly);
        PlayerSettings.SetStackTraceLogType(LogType.Assert, StackTraceLogType.ScriptOnly);
        PlayerSettings.SetStackTraceLogType(LogType.Exception, StackTraceLogType.ScriptOnly);
        PlayerSettings.usePlayerLog = true;
        PlayerSettings.iOS.appleEnableAutomaticSigning = false;
        SetProjectSetting("activeInputHandler", 2);         // both input systems, like the retail game
        // The retail game's PlayerSettings (in its globalgamemanagers) replace most of these at runtime,
        // but the shell must agree on anything baked into the player binary.

        var plugins = Directory.Exists("Assets/Plugins/Game") ? Directory.GetFiles("Assets/Plugins/Game", "*.dll").Length : 0;
        Debug.Log($"[IosShellBuild] {plugins} game/mod assemblies");
        if (plugins == 0) Debug.LogWarning("[IosShellBuild] no game assemblies in Assets/Plugins/Game - building an empty shell");

        var opts = new BuildPlayerOptions
        {
            scenes = new[] { ScenePath },
            locationPathName = outDir,
            target = BuildTarget.iOS,
            targetGroup = BuildTargetGroup.iOS,
            options = BuildOptions.None,
        };
        var report = BuildPipeline.BuildPlayer(opts);
        Debug.Log("[IosShellBuild] result: " + report.summary.result + " errors=" + report.summary.totalErrors);
        if (report.summary.result != UnityEditor.Build.Reporting.BuildResult.Succeeded)
        {
            // Keep the linked assemblies IL2CPP choked on (Library/Temp is wiped when the editor quits) so CI
            // can hand them to the PRIVATE repo for inspection. They contain game code: never publish them publicly.
            string dbg = Environment.GetEnvironmentVariable("SHELL_DEBUG_OUT");
            foreach (var staged in new[] { "Library/Bee/artifacts/iOS/ManagedStripped", "Temp/StagingArea/Data/Managed" })
            {
                if (string.IsNullOrEmpty(dbg) || !Directory.Exists(staged)) continue;
                Directory.CreateDirectory(dbg);
                foreach (var f in Directory.GetFiles(staged, "*.dll"))
                    File.Copy(f, Path.Combine(dbg, Path.GetFileName(f)), true);
                Debug.Log("[IosShellBuild] copied staged assemblies from " + staged + " to " + dbg);
            }
            EditorApplication.Exit(1);
        }
    }

    static void SetProjectSetting(string name, int value)
    {
        var asset = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/ProjectSettings.asset").FirstOrDefault();
        if (asset == null) { Debug.LogWarning("[IosShellBuild] ProjectSettings.asset not found"); return; }
        var so = new SerializedObject(asset);
        var p = so.FindProperty(name);
        if (p == null) { Debug.LogWarning("[IosShellBuild] project setting missing: " + name); return; }
        p.intValue = value;
        so.ApplyModifiedPropertiesWithoutUndo();
        Debug.Log($"[IosShellBuild] {name} = {value}");
    }

    [PostProcessBuild(100)]
    public static void OnPostprocess(BuildTarget target, string path)
    {
        if (target != BuildTarget.iOS) return;

        // ---- Info.plist ----
        string plistPath = Path.Combine(path, "Info.plist");
        var plist = new PlistDocument();
        plist.ReadFromFile(plistPath);
        var root = plist.root;
        root.SetString("CFBundleDisplayName", "PEAK");
        // Application.version on iOS is CFBundleShortVersionString: the game builds its Photon game version from it
        root.SetString("CFBundleShortVersionString", "2.6.b");
        root.SetBoolean("UIFileSharingEnabled", true);           // Documents visible in Files / Filza
        root.SetBoolean("LSSupportsOpeningDocumentsInPlace", true);
        root.SetBoolean("UISupportsDocumentBrowser", true);
        root.SetBoolean("CADisableMinimumFrameDurationOnPhone", true);   // allow 120 Hz on ProMotion
        root.SetBoolean("UIRequiresFullScreen", true);
        root.SetBoolean("UIStatusBarHidden", true);
        root.SetBoolean("ITSAppUsesNonExemptEncryption", false);
        root.SetBoolean("GCSupportsControllerUserInteraction", true);
        root.SetBoolean("GCSupportsGameMode", true);
        root.SetString("NSMicrophoneUsageDescription", PlayerSettings.iOS.microphoneUsageDescription);
        // local Wi-Fi multiplayer (later): the host runs a game server, joining phones find it on the network
        root.SetString("NSLocalNetworkUsageDescription", "PEAK uses your Wi-Fi to play with friends on the same network.");
        var gcProfiles = root.CreateArray("GCSupportedGameControllers");
        gcProfiles.AddDict().SetString("ProfileName", "ExtendedGamepad");
        var orientations = root.CreateArray("UISupportedInterfaceOrientations");
        orientations.AddString("UIInterfaceOrientationLandscapeRight");
        orientations.AddString("UIInterfaceOrientationLandscapeLeft");
        plist.WriteToFile(plistPath);

        // ---- Unity's native console output (and NSLog) -> Documents/PEAK-iOS-native.log (previous run:
        // -prev), so a failure before any managed code runs (data loading, IL2CPP init) still leaves a log. ----
        string mainPath = Path.Combine(path, "MainApp", "main.mm");
        if (!File.Exists(mainPath)) mainPath = Path.Combine(path, "Classes", "main.mm");
        string main = File.ReadAllText(mainPath);
        const string hook = "static void PeakRedirectNativeLog()";
        if (!main.Contains(hook))
        {
            string fn = hook + @"
{
    NSString* docs = [NSSearchPathForDirectoriesInDomains(NSDocumentDirectory, NSUserDomainMask, YES) firstObject];
    if (docs == nil) return;
    NSString* cur = [docs stringByAppendingPathComponent: @""PEAK-iOS-native.log""];
    NSString* prev = [docs stringByAppendingPathComponent: @""PEAK-iOS-native-prev.log""];
    NSFileManager* fm = [NSFileManager defaultManager];
    [fm removeItemAtPath: prev error: nil];
    [fm moveItemAtPath: cur toPath: prev error: nil];
    if (freopen([cur fileSystemRepresentation], ""w"", stderr) != NULL)
    {
        setvbuf(stderr, NULL, _IONBF, 0);
        dup2(fileno(stderr), fileno(stdout));
        setvbuf(stdout, NULL, _IOLBF, 0);
    }
}

";
            int at = main.IndexOf("int main(", StringComparison.Ordinal);
            if (at < 0) throw new BuildFailedException("main() not found in " + mainPath);
            main = main.Insert(at, fn);
            int brace = main.IndexOf('{', main.IndexOf("int main(", StringComparison.Ordinal));
            main = main.Insert(brace + 1, "\n    PeakRedirectNativeLog();");
            if (!main.Contains("#include <stdio.h>")) main = "#include <stdio.h>\n#include <unistd.h>\n" + main;
            File.WriteAllText(mainPath, main);
            Debug.Log("[IosShellBuild] native log redirect added to " + mainPath);
        }

        // ---- Xcode project: frameworks, unsigned (TrollStore install; CI fakesigns with ldid afterwards) ----
        string projPath = PBXProject.GetPBXProjectPath(path);
        var proj = new PBXProject();
        proj.ReadFromFile(projPath);
        string mainTarget = proj.GetUnityMainTargetGuid(), fw = proj.GetUnityFrameworkTargetGuid();
        foreach (var f in new[] { "GameController.framework", "AVFoundation.framework", "AudioToolbox.framework" })
            proj.AddFrameworkToProject(fw, f, false);
        proj.AddBuildProperty(fw, "OTHER_LDFLAGS", "-lc++");
        foreach (var guid in new[] { mainTarget, fw })
        {
            proj.SetBuildProperty(guid, "CODE_SIGNING_ALLOWED", "NO");
            proj.SetBuildProperty(guid, "CODE_SIGNING_REQUIRED", "NO");
            proj.SetBuildProperty(guid, "CODE_SIGN_IDENTITY", "");
            proj.SetBuildProperty(guid, "IPHONEOS_DEPLOYMENT_TARGET", "15.0");
            proj.AddBuildProperty(guid, "WARNING_CFLAGS", "-Wno-error");
            proj.SetBuildProperty(guid, "GCC_TREAT_WARNINGS_AS_ERRORS", "NO");
        }
        proj.WriteToFile(projPath);
        Debug.Log("[IosShellBuild] Xcode project post-processed (plist, native log, frameworks, unsigned)");
    }
}
