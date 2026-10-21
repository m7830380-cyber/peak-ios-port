using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using PeakShim;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

namespace PeakIOS
{
    /// <summary>
    /// Rendering diagnostics. On each scene with a camera it logs the camera / URP / renderer feature / volume /
    /// fog state, then (once per scene name, a few seconds in) captures the screen with one thing changed at a
    /// time - post-processing off, HDR off, each renderer feature off, the light volume replaced by white, full
    /// resolution - and logs the average brightness of each capture, so a black or white picture can be traced
    /// to the part that causes it. The first capture is saved to Documents/PEAK-shot-&lt;scene&gt;.png.
    /// The same switches are in the iOS settings panel (persisted) for testing by eye.
    /// </summary>
    public sealed class RenderDiag : MonoBehaviour
    {
        public static bool PostFx { get => Prefs.GetInt("peakios_diag_postfx", 1) != 0; set { Prefs.SetInt("peakios_diag_postfx", value ? 1 : 0); s_Reapply = true; } }
        public static bool Features { get => Prefs.GetInt("peakios_diag_features", 1) != 0; set { Prefs.SetInt("peakios_diag_features", value ? 1 : 0); s_Reapply = true; } }
        public static bool Hdr { get => Prefs.GetInt("peakios_diag_hdr", 1) != 0; set { Prefs.SetInt("peakios_diag_hdr", value ? 1 : 0); s_Reapply = true; } }

        static bool s_Reapply = true;
        static readonly HashSet<string> s_Swept = new HashSet<string>();
        static bool s_Sweeping;
        static int s_StpLogged;
        // feature -> the active state the game gave it before the "Features off" switch turned it off
        static readonly Dictionary<ScriptableRendererFeature, bool> s_FeatureState = new Dictionary<ScriptableRendererFeature, bool>();
        float m_NextReapply;

        void Awake() => SceneManager.sceneLoaded += (s, m) => StartCoroutine(OnScene(s.name));

        void Update()
        {
            ForceNoStp();
            if (s_Sweeping) return;
            if (s_Reapply || Time.unscaledTime >= m_NextReapply)
            {
                s_Reapply = false;
                m_NextReapply = Time.unscaledTime + 1f;
                ApplySwitches();
            }
        }

        /// STP (the URP upscaler used whenever render scale is below 1 and the game's Render Scale setting is not
        /// Native) runs compute shaders that did not convert to Metal; the plain bilinear upscale is used instead.
        static void ForceNoStp()
        {
            var u = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            if (u == null || u.upscalingFilter != UpscalingFilterSelection.STP) return;
            u.upscalingFilter = UpscalingFilterSelection.Linear;
            if (s_StpLogged++ < 5) Log.Info($"URP '{u.name}': STP upscaling -> Linear (STP compute shaders are not available on iOS), render scale {u.renderScale:0.00}");
        }

        static IEnumerable<UniversalRenderPipelineAsset> Assets()
        {
            var seen = new HashSet<RenderPipelineAsset>();
            if (GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset c && seen.Add(c)) yield return c;
            for (int i = 0; i < QualitySettings.count; i++)
                if (QualitySettings.GetRenderPipelineAssetAt(i) is UniversalRenderPipelineAsset q && seen.Add(q)) yield return q;
        }

        static List<ScriptableRendererFeature> AllFeatures()
        {
            var seen = new HashSet<ScriptableRendererFeature>();
            var all = new List<ScriptableRendererFeature>();
            foreach (var a in Assets())
                foreach (var rd in a.rendererDataList)
                    if (rd != null)
                        foreach (var f in rd.rendererFeatures)
                            if (f != null && seen.Add(f)) all.Add(f);
            return all;
        }

        static void ApplySwitches()
        {
            try
            {
                bool post = PostFx, hdr = Hdr, feats = Features;
                foreach (var cam in Camera.allCameras)
                {
                    var d = cam.GetUniversalAdditionalCameraData();
                    if (!post && d != null && d.renderPostProcessing) d.renderPostProcessing = false;
                    if (!hdr && cam.allowHDR) cam.allowHDR = false;
                }
                foreach (var a in Assets())
                    if (!hdr && a.supportsHDR) a.supportsHDR = false;
                if (!feats)
                {
                    foreach (var f in AllFeatures())
                        if (f.isActive) { s_FeatureState[f] = true; f.SetActive(false); }
                }
                else if (s_FeatureState.Count > 0)
                {
                    foreach (var kv in s_FeatureState) if (kv.Key != null) kv.Key.SetActive(kv.Value);
                    s_FeatureState.Clear();
                }
            }
            catch (Exception e) { Log.Warn("render switches: " + e.Message); }
        }

        IEnumerator OnScene(string scene)
        {
            for (int i = 0; i < 20; i++) yield return null;
            if (Camera.main == null) yield break;
            LogState(scene);
            if (s_Swept.Contains(scene) || s_Sweeping) yield break;
            // brightness over the first seconds, to catch a picture that shows and then goes dark
            var seq = new StringBuilder();
            float t0 = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - t0 < 6f)
            {
                yield return Capture((sh, tex) => seq.Append($" {Time.realtimeSinceStartup - t0:0.0}s:{(sh.R + sh.G + sh.B) / 3f:0}"));
                yield return new WaitForSecondsRealtime(0.4f);
            }
            Log.Info($"render '{scene}' brightness after load:{seq}");
            for (int i = 0; i < 60 && LoadingScreenHandler.loading; i++) yield return new WaitForSecondsRealtime(0.5f);
            if (Camera.main == null || SceneManager.GetActiveScene().name != scene) yield break;
            s_Swept.Add(scene);
            yield return Sweep(scene);
        }

        static void LogState(string scene)
        {
            var sb = new StringBuilder();
            try
            {
                sb.Append($"render state '{scene}':");
                foreach (var a in Assets())
                {
                    sb.Append($"\n  URP '{a.name}'{(a == GraphicsSettings.currentRenderPipeline ? " (current)" : "")}: scale={a.renderScale:0.00} upscale={a.upscalingFilter} " +
                              $"hdr={a.supportsHDR}/{a.hdrColorBufferPrecision} msaa={a.msaaSampleCount} depthTex={a.supportsCameraDepthTexture} " +
                              $"opaqueTex={a.supportsCameraOpaqueTexture} shadows={a.shadowDistance:0} mainLight={a.mainLightRenderingMode}");
                    var list = a.rendererDataList;
                    for (int i = 0; i < list.Length; i++)
                    {
                        var rd = list[i];
                        if (rd == null) continue;
                        sb.Append($"\n    renderer {i} '{rd.name}' ({rd.GetType().Name}):");
                        foreach (var f in rd.rendererFeatures)
                            if (f != null) sb.Append($" [{f.name}:{f.GetType().Name}{(f.isActive ? "" : " OFF")}]");
                    }
                }
                foreach (var cam in Camera.allCameras)
                {
                    var d = cam.GetUniversalAdditionalCameraData();
                    sb.Append($"\n  camera '{cam.name}' depth={cam.depth} clear={cam.clearFlags} bg={cam.backgroundColor} near/far={cam.nearClipPlane:0.##}/{cam.farClipPlane:0} " +
                              $"mask=0x{cam.cullingMask:X} target={(cam.targetTexture ? cam.targetTexture.name : "screen")} hdr={cam.allowHDR} pos={cam.transform.position}");
                    if (d != null)
                        sb.Append($" type={d.renderType} post={d.renderPostProcessing} aa={d.antialiasing} stack={d.cameraStack?.Count ?? 0}");
                }
                sb.Append($"\n  fog={RenderSettings.fog} {RenderSettings.fogMode} color={RenderSettings.fogColor} density={RenderSettings.fogDensity} " +
                          $"start/end={RenderSettings.fogStartDistance:0}/{RenderSettings.fogEndDistance:0} ambient={RenderSettings.ambientMode} " +
                          $"{RenderSettings.ambientLight} x{RenderSettings.ambientIntensity:0.##} skybox={(RenderSettings.skybox ? RenderSettings.skybox.shader.name : "none")}");
                sb.Append($"\n  globals _LightMap={Describe(Shader.GetGlobalTexture("_LightMap"))} _Brightness={Shader.GetGlobalFloat("_Brightness")} " +
                          $"_AmbienceStrength={Shader.GetGlobalFloat("_AmbienceStrength")} _AmbienceMin={Shader.GetGlobalFloat("_AmbienceMin")} " +
                          $"_GridRes={Shader.GetGlobalVector("_GridRes")} _GridOffset={Shader.GetGlobalVector("_GridOffset")} _RaySpacing={Shader.GetGlobalFloat("_RaySpacing")}");
                foreach (var l in FindObjectsByType<Light>(FindObjectsSortMode.None))
                    if (l.type == LightType.Directional)
                        sb.Append($"\n  sun '{l.name}' on={l.isActiveAndEnabled} intensity={l.intensity:0.##} color={l.color} shadows={l.shadows}");
                foreach (var v in FindObjectsByType<Volume>(FindObjectsSortMode.None))
                {
                    var p = v.HasInstantiatedProfile() ? v.profile : v.sharedProfile;
                    sb.Append($"\n  volume '{v.name}' on={v.isActiveAndEnabled} global={v.isGlobal} weight={v.weight:0.##} priority={v.priority}:");
                    if (p != null)
                        foreach (var c in p.components)
                            if (c != null) sb.Append($" {c.GetType().Name}{(c.active ? "" : "(off)")}");
                }
            }
            catch (Exception e) { sb.Append("\n  (failed: " + e.Message + ")"); }
            Log.Info(sb.ToString());
        }

        static bool LayerHasVisible(int layer)
        {
            foreach (var r in FindObjectsByType<Renderer>(FindObjectsSortMode.None))
                if (r.gameObject.layer == layer && r.enabled && r.isVisible) return true;
            return false;
        }

        static string Describe(Texture t) => t == null ? "null" : $"{t.name} {t.width}x{t.height} {t.graphicsFormat}";

        struct Shot { public float R, G, B, Dark; }

        static IEnumerator Capture(Action<Shot, Texture2D> done)
        {
            for (int i = 0; i < 4; i++) yield return null;
            yield return new WaitForEndOfFrame();
            Texture2D tex = null;
            try
            {
                tex = ScreenCapture.CaptureScreenshotAsTexture();
                var px = tex.GetPixels32();
                long r = 0, g = 0, b = 0; int n = 0, dark = 0;
                for (int i = 0; i < px.Length; i += 13, n++)
                {
                    var c = px[i];
                    r += c.r; g += c.g; b += c.b;
                    if (c.r + c.g + c.b < 24) dark++;
                }
                done(new Shot { R = r / (float)n, G = g / (float)n, B = b / (float)n, Dark = dark * 100f / n }, tex);
            }
            catch (Exception e) { Log.Warn("render test capture failed: " + e.Message); }
            finally { if (tex != null) Destroy(tex); }
        }

        static IEnumerator Sweep(string scene)
        {
            s_Sweeping = true;
            Log.Info($"render test '{scene}': average screen colour (0-255) and % near-black pixels, one change at a time");
            var results = new StringBuilder();
            IEnumerator Step(string name, Action on, Action off, bool save = false)
            {
                bool ok = true;
                try { on?.Invoke(); } catch (Exception e) { ok = false; results.Append($"\n  {name}: could not apply ({e.Message})"); }
                if (ok)
                    yield return Capture((s, tex) =>
                    {
                        results.Append($"\n  {name,-28} rgb=({s.R:0},{s.G:0},{s.B:0}) dark={s.Dark:0}%");
                        if (save)
                            try { File.WriteAllBytes(Path.Combine(Paths.Documents, $"PEAK-shot-{scene}.png"), tex.EncodeToPNG()); }
                            catch (Exception e) { Log.Warn("screenshot save failed: " + e.Message); }
                    });
                try { off?.Invoke(); } catch (Exception e) { results.Append($"\n  {name}: could not undo ({e.Message})"); }
            }

            var cams = new List<Camera>(Camera.allCameras);
            yield return Step("as is", null, null, true);

            var postWas = new Dictionary<Camera, bool>();
            yield return Step("post-processing off", () =>
            {
                foreach (var c in cams) { var d = c ? c.GetUniversalAdditionalCameraData() : null; if (d != null) { postWas[c] = d.renderPostProcessing; d.renderPostProcessing = false; } }
            }, () => { foreach (var kv in postWas) if (kv.Key) kv.Key.GetUniversalAdditionalCameraData().renderPostProcessing = kv.Value; });

            var hdrWas = new Dictionary<UniversalRenderPipelineAsset, bool>();
            yield return Step("HDR off", () => { foreach (var a in Assets()) { hdrWas[a] = a.supportsHDR; a.supportsHDR = false; } },
                () => { foreach (var kv in hdrWas) kv.Key.supportsHDR = kv.Value; });

            var feats = new List<ScriptableRendererFeature>();
            foreach (var f in AllFeatures()) if (f.isActive) feats.Add(f);
            yield return Step("all renderer features off", () => { foreach (var f in feats) f.SetActive(false); }, () => { foreach (var f in feats) f.SetActive(true); });
            foreach (var f in feats)
                yield return Step($"'{f.name}' off", () => f.SetActive(false), () => f.SetActive(true));

            Texture lm = Shader.GetGlobalTexture("_LightMap");
            Texture3D white = null;
            yield return Step("light volume white", () =>
            {
                white = new Texture3D(1, 1, 1, TextureFormat.RGBAHalf, false);
                white.SetPixel(0, 0, 0, Color.white);
                white.Apply(false, true);
                Shader.SetGlobalTexture("_LightMap", white);
            }, () => { Shader.SetGlobalTexture("_LightMap", lm); if (white) Destroy(white); });

            float lb = Shader.GetGlobalFloat("_Brightness");
            yield return Step("_Brightness x4", () => Shader.SetGlobalFloat("_Brightness", Mathf.Max(lb, 0.25f) * 4f), () => Shader.SetGlobalFloat("_Brightness", lb));

            var scaleWas = new Dictionary<UniversalRenderPipelineAsset, float>();
            yield return Step("resolution 100%", () => { foreach (var a in Assets()) { scaleWas[a] = a.renderScale; a.renderScale = 1f; } },
                () => { foreach (var kv in scaleWas) kv.Key.renderScale = kv.Value; });

            var main = Camera.main;
            if (main != null)
            {
                var clearWas = main.clearFlags; var bgWas = main.backgroundColor; int maskWas = main.cullingMask;
                yield return Step("camera: grey, draws nothing", () => { main.clearFlags = CameraClearFlags.SolidColor; main.backgroundColor = new Color(0.5f, 0.5f, 0.5f, 1f); main.cullingMask = 0; },
                    () => { main.clearFlags = clearWas; main.backgroundColor = bgWas; main.cullingMask = maskWas; });
                yield return Step("camera: grey background", () => { main.clearFlags = CameraClearFlags.SolidColor; main.backgroundColor = new Color(0.5f, 0.5f, 0.5f, 1f); },
                    () => { main.clearFlags = clearWas; main.backgroundColor = bgWas; });
                for (int layer = 0; layer < 32; layer++)
                {
                    int bit = 1 << layer;
                    if ((maskWas & bit) == 0 || !LayerHasVisible(layer)) continue;
                    yield return Step($"layer {layer} '{LayerMask.LayerToName(layer)}' hidden", () => main.cullingMask = maskWas & ~bit, () => main.cullingMask = maskWas);
                }
            }
            Log.Info($"render test '{scene}' results:{results}");

            // Hide every visible renderer that uses one shader, for each shader in view: finds a material that
            // covers the picture (a dark fog / sky / overlay) or that draws itself black.
            var groups = new Dictionary<string, List<Renderer>>();
            foreach (var r in FindObjectsByType<Renderer>(FindObjectsSortMode.None))
            {
                if (!r.enabled || !r.isVisible || r.GetType().Name == "ParticleSystemRenderer") continue;
                foreach (var m in r.sharedMaterials)
                {
                    if (m == null || m.shader == null) continue;
                    string key = m.shader.name;
                    if (!groups.TryGetValue(key, out var lst)) groups[key] = lst = new List<Renderer>();
                    if (!lst.Contains(r)) lst.Add(r);
                }
            }
            var order = new List<KeyValuePair<string, List<Renderer>>>(groups);
            order.Sort((a, b) => b.Value.Count.CompareTo(a.Value.Count));
            results.Clear();
            int shown = 0;
            foreach (var kv in order)
            {
                if (shown++ >= 48) break;
                var hidden = new List<Renderer>();
                var sample = kv.Value[0];
                var mat = Array.Find(sample.sharedMaterials, m => m != null && m.shader != null && m.shader.name == kv.Key);
                string info = $"{kv.Value.Count} renderers, e.g. '{sample.name}' mat '{(mat ? mat.name : "?")}' queue {(mat ? mat.renderQueue : 0)}";
                yield return Step($"hide '{kv.Key}'", () => { foreach (var r in kv.Value) if (r && r.enabled) { r.enabled = false; hidden.Add(r); } },
                    () => { foreach (var r in hidden) if (r) r.enabled = true; });
                results.Append($"  ({info})");
            }
            Log.Info($"render test '{scene}' shaders in view (picture with each one hidden):{results}");
            s_Sweeping = false;
            s_Reapply = true;
        }
    }
}
