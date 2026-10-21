using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace PeakShim
{
    /// <summary>
    /// Replacement for Peak.CompressableLightMap.Decompress (the patcher sends the whole method here). The baked
    /// light volume is one BC6H (RGB) + one BC4 (alpha) texture per Z slice; the game reads them with GetPixels into
    /// a 333 MB Color[] and fills an RGBA half-float Texture3D. iOS has no BC6H, so the converter leaves the slices'
    /// bytes as they are (labelled R8) and this decodes them natively (PeakLightmap.mm) straight into the Texture3D,
    /// whose CPU copy is dropped once it is on the GPU.
    /// </summary>
    public static class Lightmap
    {
        [DllImport("__Internal")] static extern unsafe void PeakLM_DecodeSlice(byte* rgb, byte* alpha, int w, int h, ushort* dst);

        public static unsafe Texture3D Decompress(Texture2D[] rgb, Texture2D[] alpha, int gx, int gy, int gz)
        {
            var sw = Stopwatch.StartNew();
            var tex = new Texture3D(gx, gy, gz, TextureFormat.RGBAHalf, false)
            {
                name = "LightVolumeBakeTexture",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };
            int n = rgb == null ? 0 : Math.Min(gz, rgb.Length);
            int blocks = ((gx + 3) / 4) * ((gy + 3) / 4);
            int bad = 0;
            try
            {
                NativeArray<ushort> dst = tex.GetPixelData<ushort>(0);
                ushort* d = (ushort*)dst.GetUnsafePtr();
                long slice = (long)gx * gy * 4;
                for (int i = 0; i < n; i++)
                {
                    var r = rgb[i];
                    var a = alpha != null && i < alpha.Length ? alpha[i] : null;
                    if (r == null) { bad++; continue; }
                    NativeArray<byte> rb = r.GetRawTextureData<byte>();
                    if (rb.Length < blocks * 16) { bad++; if (bad == 1) Debug.LogWarning($"[PEAK-iOS] light volume slice {i}: {r.format} {r.width}x{r.height}, {rb.Length} bytes, expected {blocks * 16} (BC6H)"); continue; }
                    byte* ap = null;
                    if (a != null)
                    {
                        NativeArray<byte> ab = a.GetRawTextureData<byte>();
                        if (ab.Length >= blocks * 8) ap = (byte*)ab.GetUnsafeReadOnlyPtr();
                    }
                    PeakLM_DecodeSlice((byte*)rb.GetUnsafeReadOnlyPtr(), ap, gx, gy, d + slice * i);
                }
                tex.Apply(false, true);   // to the GPU; the CPU copy is released
            }
            catch (Exception e)
            {
                Debug.LogError("[PEAK-iOS] light volume decode failed: " + e);
            }
            Debug.Log($"[PEAK-iOS] light volume {gx}x{gy}x{gz}: {n} slices decoded natively in {sw.ElapsedMilliseconds} ms ({bad} unusable)");
            return tex;
        }
    }
}
