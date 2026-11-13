#ifndef LENSIO_YUV_PLANE_H
#define LENSIO_YUV_PLANE_H
#include <stddef.h>
#include <stdint.h>
#include <string.h>
#if defined(__ARM_NEON)
#include <arm_neon.h>
#elif defined(__SSE2__)
#include <emmintrin.h>
#endif

/* Byte-preserving sample copy. No color arithmetic, scaling, or frame skipping. */
static void lensio_copy_plane(const uint8_t *src, size_t row_stride, int pixel_stride,
                             uint8_t *dst, size_t dst_stride, int width, int height) {
    for (int row=0; row<height; ++row) {
        const uint8_t *input=src+(size_t)row*row_stride;
        uint8_t *output=dst+(size_t)row*dst_stride;
        if (pixel_stride==1) { memcpy(output,input,(size_t)width); continue; }
        int x=0;
        if (pixel_stride==2) {
            /* Strict '<': the last row may end at the final visible sample,
               with no spare byte after it for a full interleaved vector load. */
            for (; x+16<width; x+=16) {
#if defined(__ARM_NEON)
                vst1q_u8(output+x,vld2q_u8(input+(size_t)x*2).val[0]);
#elif defined(__SSE2__)
                const __m128i mask=_mm_set1_epi16(255);
                __m128i a=_mm_and_si128(_mm_loadu_si128((const __m128i *)(input+(size_t)x*2)),mask);
                __m128i b=_mm_and_si128(_mm_loadu_si128((const __m128i *)(input+(size_t)x*2+16)),mask);
                _mm_storeu_si128((__m128i *)(output+x),_mm_packus_epi16(a,b));
#else
                for (int n=0; n<16; ++n) output[x+n]=input[(size_t)(x+n)*2];
#endif
            }
        }
        for (; x<width; ++x) output[x]=input[(size_t)x*(size_t)pixel_stride];
    }
}
#endif
