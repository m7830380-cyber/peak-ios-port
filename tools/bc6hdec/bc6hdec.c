// BC6H -> RGBA half-float decoder DLL for transcode_tex.py (ctypes). The decoding itself is bcdec.h
// (github.com/iOrange/bcdec, downloaded by CMakeLists.txt). texture2ddecoder only decodes BC6H to clamped
// 8-bit, which would cut every lightmap value above 1.0.
#define BCDEC_IMPLEMENTATION
#include "bcdec.h"
#include <stdint.h>

#ifdef _WIN32
#define EXPORT __declspec(dllexport)
#else
#define EXPORT __attribute__((visibility("default")))
#endif

// src: (w/4)*(h/4) BC6H blocks (w, h multiples of 4), dst: w*h*4 halfs (RGBA, A = 1.0)
EXPORT int bc6h_to_rgba16f(const uint8_t *src, int w, int h, int isSigned, uint16_t *dst)
{
    uint16_t block[4 * 4 * 3];
    int bw = w / 4, bh = h / 4;
    for (int by = 0; by < bh; by++)
        for (int bx = 0; bx < bw; bx++)
        {
            bcdec_bc6h_half(src + (size_t)(by * bw + bx) * BCDEC_BC6H_BLOCK_SIZE, block, 4 * 3, isSigned);
            for (int y = 0; y < 4; y++)
                for (int x = 0; x < 4; x++)
                {
                    uint16_t *o = dst + ((size_t)(by * 4 + y) * w + (bx * 4 + x)) * 4;
                    const uint16_t *i = block + (y * 4 + x) * 3;
                    o[0] = i[0]; o[1] = i[1]; o[2] = i[2]; o[3] = 0x3C00;
                }
        }
    return 0;
}
