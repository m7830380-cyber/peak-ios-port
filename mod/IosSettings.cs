using System;
using PeakShim;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

namespace PeakIOS
{
    /// <summary>
    /// iOS-only options (Documents/PEAK-settings.txt through PeakShim.Prefs), applied at start, on every scene load
    /// and whenever they change. The game's own Settings menu keeps every graphics option it has (quality,
    /// shadows, effects); these only add what a phone needs on top: frame-rate cap, render resolution, the
    /// touch controls' look speed / size / opacity, and the microphone opt-in (voice chat, off by default).
    /// </summary>
    public static class IosSettings
    {
        public static float LookSpeed { get => TouchHud.LookSpeed; set { TouchHud.LookSpeed = value; Prefs.SetFloat("peakios_look_speed", value); } }
        public static float HudScale { get => TouchHud.Scale; set { TouchHud.Scale = value; Prefs.SetFloat("peakios_hud_scale", value); TouchHud.Relayout(); } }
        public static float HudOpacity { get => TouchHud.Opacity; set { TouchHud.Opacity = value; Prefs.SetFloat("peakios_hud_alpha", value); } }
        public static int FpsCap { get => Prefs.GetInt("peakios_fps", 60); set { Prefs.SetInt("peakios_fps", value); Apply(); } }
        public static float RenderScale { get => Mathf.Clamp(Prefs.GetFloat("peakios_render_scale", 0.75f), 0.4f, 1f); set { Prefs.SetFloat("peakios_render_scale", value); Apply(); } }
        public static bool Microphone { get => Prefs.GetInt("peakios_mic", 0) != 0; set { Prefs.SetInt("peakios_mic", value ? 1 : 0); Apply(); } }

        public static void Init()
        {
            Apply();
            SceneManager.sceneLoaded += (s, m) => Apply();
        }

        public static void Save() => Prefs.Save();

        static float s_LoggedScale = -1;

        public static void Apply()
        {
            try
            {
                Platform.UserFpsCap = FpsCap;
                Platform.ApplyFrameRate();
                Mic.Enabled = Microphone;
                float scale = RenderScale;
                var seen = new System.Collections.Generic.HashSet<RenderPipelineAsset>();
                void Set(RenderPipelineAsset a)
                {
                    if (a is UniversalRenderPipelineAsset u && seen.Add(a) && Mathf.Abs(u.renderScale - scale) > 0.001f) u.renderScale = scale;
                }
                Set(GraphicsSettings.defaultRenderPipeline);
                Set(QualitySettings.renderPipeline);
                for (int i = 0; i < QualitySettings.count; i++) Set(QualitySettings.GetRenderPipelineAssetAt(i));
                if (Mathf.Abs(s_LoggedScale - scale) > 0.001f)
                {
                    s_LoggedScale = scale;
                    Log.Info($"iOS settings: fps cap={FpsCap} (target {Application.targetFrameRate}) render scale={scale:0.00} " +
                             $"({Mathf.RoundToInt(Screen.width * scale)}x{Mathf.RoundToInt(Screen.height * scale)}) mic={Microphone} " +
                             $"look={LookSpeed:0.00} hud={HudScale:0.00}/{HudOpacity:0.00} quality={QualitySettings.names[QualitySettings.GetQualityLevel()]}");
                }
            }
            catch (Exception e) { Log.Error("iOS settings: " + e); }
        }
    }
}
