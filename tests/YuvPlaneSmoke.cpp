#include "../LensioCam/android/app/src/main/jni/yuv_plane.h"
#include <vector>
#include <cstdio>
#include <stdexcept>
#include <chrono>
#ifdef _WIN32
#include <windows.h>
#else
#include <sys/mman.h>
#include <unistd.h>
#endif

static void check(bool value,const char *message){if(!value)throw std::runtime_error(message);}
int main(){
    // Compare against independent per-pixel reference, including tails and padding.
    for(int pixel=1;pixel<=4;++pixel)for(int width:{1,15,16,17,31,32,33,63,64,65,640,960,1920})for(int height:{1,7,1080}){
        size_t span=(size_t)(width-1)*pixel+1,stride=span+7,dst_stride=(size_t)width+5;
        std::vector<uint8_t> source(3+(height-1)*stride+span),output(2+height*dst_stride,0x6a);
        for(size_t i=0;i<source.size();++i)source[i]=(uint8_t)(i*37);
        lensio_copy_plane(source.data()+3,stride,pixel,output.data()+2,dst_stride,width,height);
        check(output[0]==0x6a&&output[1]==0x6a,"Destination prefix overwritten");
        for(int y=0;y<height;++y)for(size_t x=0;x<dst_stride;++x)
            check(output[2+y*dst_stride+x]==(x<(size_t)width?source[3+y*stride+x*pixel]:0x6a),"Pixel/padding differs");
    }
    // Guard page immediately after the last visible sample catches SIMD overreads.
#ifdef _WIN32
    SYSTEM_INFO info;GetSystemInfo(&info);size_t page=info.dwPageSize;
    auto memory=(uint8_t*)VirtualAlloc(nullptr,page*2,MEM_COMMIT|MEM_RESERVE,PAGE_READWRITE);
    check(memory!=nullptr,"Guard allocation failed");DWORD old;check(VirtualProtect(memory+page,page,PAGE_NOACCESS,&old)!=0,"Guard protection failed");
#else
    size_t page=(size_t)sysconf(_SC_PAGESIZE);
    auto memory=(uint8_t*)mmap(nullptr,page*2,PROT_READ|PROT_WRITE,MAP_PRIVATE|MAP_ANONYMOUS,-1,0);
    check(memory!=MAP_FAILED,"Guard allocation failed");check(mprotect(memory+page,page,PROT_NONE)==0,"Guard protection failed");
#endif
    for(int width:{1,15,16,17,31,32,33,640,960}){
        size_t span=(size_t)(width-1)*2+1;auto source=memory+page-span;
        for(size_t i=0;i<span;++i)source[i]=(uint8_t)i;
        std::vector<uint8_t> output(width);lensio_copy_plane(source,span,2,output.data(),width,width,1);
        for(int x=0;x<width;++x)check(output[x]==(uint8_t)(x*2),"Guarded plane differs");
    }
#ifdef _WIN32
    VirtualFree(memory,0,MEM_RELEASE);
#else
    munmap(memory,page*2);
#endif
    std::vector<uint8_t> input(1920*1080),output(960*1080);for(size_t i=0;i<input.size();++i)input[i]=(uint8_t)i;
    auto start=std::chrono::steady_clock::now();
    for(int frame=0;frame<1000;++frame)lensio_copy_plane(input.data(),1920,2,output.data(),960,960,1080);
    double ms=std::chrono::duration<double,std::milli>(std::chrono::steady_clock::now()-start).count()/1000;
    std::printf("PASS native YUV byte equivalence, strides, offsets, destination padding and protected-page tails; host interleaved plane %.3f ms\n",ms);
    return 0;
}
