// Original Lensio pixel kernels. SSE2 is available on every Windows x64 CPU.
// Keep the integer weights, rounding, opacity and BT.601 range identical to the managed path.
#include <windows.h>
#include <emmintrin.h>
#include <cstdint>
#include <cstring>

static bool Surface(int width,int height,int stride) {
 return width>0 && height>0 && width<=8192 && height<=8192 &&
  int64_t(width)*height<=16777216 && (stride>=width*4 || int64_t(stride)<=-int64_t(width)*4);
}
static __m128i Pixel(const uint8_t* p) {
 uint32_t value;std::memcpy(&value,p,4);
 return _mm_unpacklo_epi8(_mm_cvtsi32_si128(static_cast<int>(value)),_mm_setzero_si128());
}
static __m128i Weighted(__m128i value,__m128i weight) {
 return _mm_unpacklo_epi16(_mm_mullo_epi16(value,weight),_mm_mulhi_epu16(value,weight));
}
extern "C" __declspec(dllexport) int __cdecl LensioScaleBgra(
 const uint8_t* src,int sw,int sh,int stride,uint8_t* dst,int width,int height,int dstStride,
 const int* left,const int* right,const int* weights,const int* top,const int* bottom,const int* vertical,
 int start,int end,int alpha) {
 if(!src||!dst||!left||!right||!weights||!top||!bottom||!vertical||
  !Surface(sw,sh,stride)||!Surface(width,height,dstStride)||start<0||end>height||start>end||alpha<0||alpha>255)return E_INVALIDARG;
 const __m128i rounding=_mm_set1_epi32(32768),zero=_mm_setzero_si128(),opacity=_mm_set1_epi16(static_cast<short>(alpha));
 for(int y=start;y<end;y++) {
  auto output=dst+ptrdiff_t(y)*dstStride;
  if(alpha==0){std::memset(output,0,size_t(width)*4);continue;}
  const auto row0=src+ptrdiff_t(top[y])*stride,row1=src+ptrdiff_t(bottom[y])*stride;
  const auto wy=_mm_set1_epi16(static_cast<short>(vertical[y])),iy=_mm_set1_epi16(static_cast<short>(256-vertical[y]));
  for(int x=0;x<width;x++) {
   const int x0=left[x]*4,x1=right[x]*4;
   const auto wx=_mm_set1_epi16(static_cast<short>(weights[x])),ix=_mm_set1_epi16(static_cast<short>(256-weights[x]));
   auto first=_mm_add_epi16(_mm_mullo_epi16(Pixel(row0+x0),ix),_mm_mullo_epi16(Pixel(row0+x1),wx));
   auto second=_mm_add_epi16(_mm_mullo_epi16(Pixel(row1+x0),ix),_mm_mullo_epi16(Pixel(row1+x1),wx));
   auto value=_mm_srli_epi32(_mm_add_epi32(_mm_add_epi32(Weighted(first,iy),Weighted(second,wy)),rounding),16);
   value=_mm_packs_epi32(value,zero);
   if(alpha!=255) {
    value=_mm_add_epi16(_mm_mullo_epi16(value,opacity),_mm_set1_epi16(127));
    // Exact unsigned division by 255 for the bounded numerator (0..65152).
    value=_mm_srli_epi16(_mm_add_epi16(_mm_add_epi16(value,_mm_set1_epi16(1)),_mm_srli_epi16(value,8)),8);
   }
   const uint32_t pixel=(static_cast<uint32_t>(_mm_cvtsi128_si32(_mm_packus_epi16(value,zero)))&0x00ffffffu)|(uint32_t(alpha)<<24);
   std::memcpy(output+x*4,&pixel,4);
  }
 }
 return S_OK;
}
static void LumaPair(__m128i pixels,uint8_t* dst) {
 auto value=_mm_madd_epi16(pixels,_mm_setr_epi16(25,129,66,0,25,129,66,0));
 value=_mm_add_epi32(value,_mm_shuffle_epi32(value,_MM_SHUFFLE(2,3,0,1)));
 value=_mm_add_epi32(_mm_srli_epi32(_mm_add_epi32(value,_mm_set1_epi32(128)),8),_mm_set1_epi32(16));
 dst[0]=static_cast<uint8_t>(_mm_cvtsi128_si32(value));
 dst[1]=static_cast<uint8_t>(_mm_cvtsi128_si32(_mm_srli_si128(value,8)));
}
extern "C" __declspec(dllexport) int __cdecl LensioBgraToNv12(
 const uint8_t* src,int stride,int width,int height,uint8_t* dst,int capacity) {
 if(!src||!dst||!Surface(width,height,stride)||width%2||height%2||int64_t(width)*height*3/2>capacity)return E_INVALIDARG;
 const auto zero=_mm_setzero_si128();
 auto uv=dst+size_t(width)*height;
 for(int y=0;y<height;y+=2) {
  const auto row0=src+ptrdiff_t(y)*stride,row1=row0+stride;
  auto y0=dst+size_t(y)*width,y1=y0+width,chroma=uv+size_t(y/2)*width;
  for(int x=0;x<width;x+=2) {
   auto a=_mm_unpacklo_epi8(_mm_loadl_epi64(reinterpret_cast<const __m128i*>(row0+x*4)),zero);
   auto b=_mm_unpacklo_epi8(_mm_loadl_epi64(reinterpret_cast<const __m128i*>(row1+x*4)),zero);
   LumaPair(a,y0+x);LumaPair(b,y1+x);
   auto sum=_mm_add_epi16(a,b);
   auto average=_mm_srli_epi16(_mm_add_epi16(sum,_mm_srli_si128(sum,8)),2);
   const int bg=_mm_cvtsi128_si32(average),ra=_mm_cvtsi128_si32(_mm_srli_si128(average,4));
   const int blue=bg&65535,green=(bg>>16)&65535,red=ra&65535;
   chroma[x]=static_cast<uint8_t>(((-38*red-74*green+112*blue+128)>>8)+128);
   chroma[x+1]=static_cast<uint8_t>(((112*red-94*green-18*blue+128)>>8)+128);
  }
 }
 return S_OK;
}
