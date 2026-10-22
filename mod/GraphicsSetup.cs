using System;
using System.Collections.Generic;
using PeakShim;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

namespace PeakIOS
{
    /// <summary>
    /// iOS render pipeline adjustments and a per-scene check for materials whose shader cannot run.
    /// The GPU Resident Drawer (BatchRendererGroup) draws the error shader (purple) for any shader the strict
    /// SRP Batcher check rejects; the converted Metal shaders do not all pass it, so it is turned off - the
    /// regular SRP Batcher path renders them normally.
    /// </summary>
    public static class GraphicsSetup
    {
        /// Depth priming (a depth-only pre-pass, then opaque objects drawn only where their depth is exactly equal)
        /// is turned off: on the phone no opaque object passed that test and only the sky was drawn. It is a
        /// desktop optimisation that Apple GPUs do not need (they remove hidden surfaces in hardware).
        public static readonly Dictionary<UniversalRendererData, DepthPrimingMode> GameDepthPriming = new Dictionary<UniversalRendererData, DepthPrimingMode>();

        /// The render graph's framebuffer-fetch copy pass (PeakShim.Gfx); off unless peakios_fbcopy=1.
        public static bool FramebufferFetchCopy
        {
            get => Gfx.FramebufferFetchCopy;
            set { Gfx.FramebufferFetchCopy = value; Prefs.SetInt("peakios_fbcopy", value ? 1 : 0); }
        }

        public static void Configure()
        {
            Gfx.FramebufferFetchCopy = Prefs.GetInt("peakios_fbcopy", 0) != 0;
            Log.Info($"render graph copy pass: {(Gfx.FramebufferFetchCopy ? "framebuffer fetch" : "blit")}");
            var seen = new HashSet<RenderPipelineAsset>();
            void Fix(RenderPipelineAsset a, string where)
            {
                if (a == null || !seen.Add(a)) return;
                if (a is UniversalRenderPipelineAsset u)
                {
                    try
                    {
                        var before = u.gpuResidentDrawerMode;
                        if (before != GPUResidentDrawerMode.Disabled) u.gpuResidentDrawerMode = GPUResidentDrawerMode.Disabled;
                        Log.Info($"URP asset '{u.name}' ({where}): GPU Resident Drawer {before} -> {u.gpuResidentDrawerMode}, SRP Batcher {u.useSRPBatcher}");
                        foreach (var rd in u.rendererDataList)
                            if (rd is UniversalRendererData ud && ud.depthPrimingMode != DepthPrimingMode.Disabled)
                            {
                                GameDepthPriming[ud] = ud.depthPrimingMode;
                                ud.depthPrimingMode = DepthPrimingMode.Disabled;
                                Log.Info($"renderer '{ud.name}': depth priming {GameDepthPriming[ud]} -> Disabled");
                            }
                    }
                    catch (Exception e) { Log.Warn($"URP asset '{u.name}': {e.Message}"); }
                }
                else Log.Info($"render pipeline asset '{a.name}' ({where}) is {a.GetType().Name}");
            }
            try
            {
                Fix(GraphicsSettings.defaultRenderPipeline, "default");
                for (int i = 0; i < QualitySettings.count; i++) Fix(QualitySettings.GetRenderPipelineAssetAt(i), "quality " + i);
                Fix(QualitySettings.renderPipeline, "current quality");
            }
            catch (Exception e) { Log.Error("graphics setup: " + e); }
            SceneManager.sceneLoaded += (s, m) => s_CheckFrame = Time.frameCount + 30;
        }

        static int s_CheckFrame = -1;
        static readonly HashSet<string> s_Reported = new HashSet<string>();

        /// Called every frame (TouchLayer.Update): ~30 frames after a scene load, lists unsupported shaders once.
        public static void Tick()
        {
            if (s_CheckFrame < 0 || Time.frameCount < s_CheckFrame) return;
            s_CheckFrame = -1;
            try
            {
                var bad = new Dictionary<string, List<string>>();
                foreach (var r in UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None))
                    foreach (var mat in r.sharedMaterials)
                    {
                        if (mat == null || mat.shader == null) continue;
                        var sh = mat.shader;
                        if (sh.isSupported && sh.name != "Hidden/InternalErrorShader") continue;
                        if (!bad.TryGetValue(sh.name, out var mats)) bad[sh.name] = mats = new List<string>();
                        if (mats.Count < 4 && !mats.Contains(mat.name)) mats.Add(mat.name);
                    }
                int n = 0;
                foreach (var kv in bad)
                {
                    if (!s_Reported.Add(kv.Key)) continue;
                    n++;
                    Log.Warn($"shader not supported on this device: '{kv.Key}' (materials: {string.Join(", ", kv.Value)})");
                }
                if (n == 0 && bad.Count == 0) Log.Info("shader check: every renderer's shader is supported");
            }
            catch (Exception e) { Log.Warn("shader check: " + e.Message); }
        }
    }
}
