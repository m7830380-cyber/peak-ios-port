// PEAK's baked light volume (LightVolume / Peak.CompressableLightMap) is stored as one BC6H (RGB) and one BC4
// (alpha) texture per Z slice. The game decodes them on the CPU with Texture2D.GetPixels, which has no BC6H path
// on iOS, so the converter keeps their bytes unchanged (labelled R8, see transcode_tex.py) and PeakShim.Lightmap
// calls this to decode one slice straight into the RGBA half-float Texture3D the game builds.
// Decoding: bcdec.h (github.com/iOrange/bcdec, MIT / Unlicense).
#define BCDEC_IMPLEMENTATION
#include "bcdec.h"
#include <stdint.h>
#include <string.h>

static inline uint16_t PeakFloatToHalf(float f)
{
    uint32_t x; memcpy(&x, &f, 4);
    uint32_t sign = (x >> 16) & 0x8000u, mant = x & 0x7fffffu;
    int32_t exp = (int32_t)((x >> 23) & 0xff) - 127 + 15;
    if (exp <= 0) return (uint16_t)sign;
    if (exp >= 31) return (uint16_t)(sign | 0x7c00u);
    return (uint16_t)(sign | ((uint32_t)exp << 10) | ((mant + 0x1000u) >> 13));
}

// rgb: ceil(w/4)*ceil(h/4) BC6H (unsigned) blocks, alpha: same count of BC4 blocks (may be NULL -> alpha 1),
// dst: w*h RGBA halfs, rows of w pixels.
extern "C" void PeakLM_DecodeSlice(const uint8_t *rgb, const uint8_t *alpha, int w, int h, uint16_t *dst)
{
    uint16_t c[4 * 4 * 3];
    uint8_t a[4 * 4];
    uint16_t alphaHalf[256];
    for (int i = 0; i < 256; i++) alphaHalf[i] = PeakFloatToHalf(i / 255.0f);
    int bw = (w + 3) / 4, bh = (h + 3) / 4;
    for (int by = 0; by < bh; by++)
        for (int bx = 0; bx < bw; bx++)
        {
            size_t b = (size_t)by * bw + bx;
            bcdec_bc6h_half(rgb + b * BCDEC_BC6H_BLOCK_SIZE, c, 4 * 3, 0);
            if (alpha) bcdec_bc4(alpha + b * BCDEC_BC4_BLOCK_SIZE, a, 4);
            for (int y = 0; y < 4; y++)
            {
                int py = by * 4 + y;
                if (py >= h) break;
                for (int x = 0; x < 4; x++)
                {
                    int px = bx * 4 + x;
                    if (px >= w) break;
                    uint16_t *o = dst + ((size_t)py * w + px) * 4;
                    const uint16_t *s = c + (y * 4 + x) * 3;
                    o[0] = s[0]; o[1] = s[1]; o[2] = s[2];
                    o[3] = alpha ? alphaHalf[a[y * 4 + x]] : 0x3C00;
                }
            }
        }
}
