using System;
using System.Collections.Generic;
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
        public static void Configure()
        {
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
