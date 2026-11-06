// Lensio AVC receiver. Original implementation; uses Windows inbox Media Foundation.
#include <windows.h>
#include <mfapi.h>
#include <mfidl.h>
#include <mftransform.h>
#include <mferror.h>
#include <wmcodecdsp.h>
#include <d3d11.h>
#include <d3d10.h>
#include <wrl/client.h>
#include <algorithm>
#include <cstdint>
#include <new>
using Microsoft::WRL::ComPtr;
#define CHECK(x) do { HRESULT result=(x); if(FAILED(result))return result; } while(0)
struct Decoder {
 ComPtr<IMFTransform> transform;
 ComPtr<ID3D11Device> device;
 ComPtr<ID3D11DeviceContext> context;
 ComPtr<IMFDXGIDeviceManager> manager;
 ComPtr<ID3D11Texture2D> staging;
 UINT width=0,height=0,bufferHeight=0,expectedWidth=0,expectedHeight=0; LONG stride=0; bool hardware=false,com=false,mf=false,bt709=false,fullRange=false;
 ~Decoder(){if(transform){transform->ProcessMessage(MFT_MESSAGE_NOTIFY_END_OF_STREAM,0);transform->ProcessMessage(MFT_MESSAGE_COMMAND_FLUSH,0);}transform.Reset();staging.Reset();manager.Reset();context.Reset();device.Reset();if(mf)MFShutdown();if(com)CoUninitialize();}
 HRESULT OutputType(){
  for(DWORD i=0;i<64;i++){
   ComPtr<IMFMediaType> type;HRESULT hr=transform->GetOutputAvailableType(0,i,&type);if(hr==MF_E_NO_MORE_TYPES)break;CHECK(hr);
   GUID subtype;CHECK(type->GetGUID(MF_MT_SUBTYPE,&subtype));if(subtype!=MFVideoFormat_NV12)continue;
   UINT w=0,h=0;CHECK(MFGetAttributeSize(type.Get(),MF_MT_FRAME_SIZE,&w,&h));
   if(w<2||h<2||w>2048||h>2048||uint64_t(w)*h>4194304||w%2||h%2)return E_INVALIDARG;
   CHECK(transform->SetOutputType(0,type.Get(),0));width=std::min(w,expectedWidth);height=std::min(h,expectedHeight);bufferHeight=h;
   stride=static_cast<LONG>(MFGetAttributeUINT32(type.Get(),MF_MT_DEFAULT_STRIDE,w));if(stride<static_cast<LONG>(w)||stride>8192)return E_INVALIDARG;
   bt709=MFGetAttributeUINT32(type.Get(),MF_MT_YUV_MATRIX,MFVideoTransferMatrix_BT601)==MFVideoTransferMatrix_BT709;
   fullRange=MFGetAttributeUINT32(type.Get(),MF_MT_VIDEO_NOMINAL_RANGE,MFNominalRange_16_235)==MFNominalRange_0_255;
   staging.Reset();return S_OK;
  }return MF_E_INVALIDMEDIATYPE;
 }
 HRESULT Initialize(UINT w,UINT h,bool gpu){
  expectedWidth=w;expectedHeight=h;
  HRESULT init=CoInitializeEx(nullptr,COINIT_MULTITHREADED);com=SUCCEEDED(init);if(FAILED(init)&&init!=RPC_E_CHANGED_MODE)return init;
  CHECK(MFStartup(MF_VERSION,MFSTARTUP_LITE));mf=true;
  CHECK(CoCreateInstance(CLSID_CMSH264DecoderMFT,nullptr,CLSCTX_INPROC_SERVER,IID_PPV_ARGS(&transform)));
  ComPtr<IMFAttributes> attrs;CHECK(transform->GetAttributes(&attrs));
  attrs->SetUINT32(MF_LOW_LATENCY,TRUE);
  if(gpu&&MFGetAttributeUINT32(attrs.Get(),MF_SA_D3D11_AWARE,FALSE)){
   D3D_FEATURE_LEVEL level;UINT token;
   if(SUCCEEDED(D3D11CreateDevice(nullptr,D3D_DRIVER_TYPE_HARDWARE,nullptr,D3D11_CREATE_DEVICE_VIDEO_SUPPORT|D3D11_CREATE_DEVICE_BGRA_SUPPORT,nullptr,0,D3D11_SDK_VERSION,&device,&level,&context))){
    ComPtr<ID3D10Multithread> multithread;
    if(SUCCEEDED(device.As(&multithread))){
     multithread->SetMultithreadProtected(TRUE);
     if(SUCCEEDED(MFCreateDXGIDeviceManager(&token,&manager))&&SUCCEEDED(manager->ResetDevice(device.Get(),token))&&
        SUCCEEDED(transform->ProcessMessage(MFT_MESSAGE_SET_D3D_MANAGER,reinterpret_cast<ULONG_PTR>(manager.Get()))))hardware=true;
    }
   }
  }
  if(gpu&&!hardware)return MF_E_UNSUPPORTED_D3D_TYPE;
  ComPtr<IMFMediaType> input;CHECK(MFCreateMediaType(&input));CHECK(input->SetGUID(MF_MT_MAJOR_TYPE,MFMediaType_Video));CHECK(input->SetGUID(MF_MT_SUBTYPE,MFVideoFormat_H264));
  CHECK(MFSetAttributeSize(input.Get(),MF_MT_FRAME_SIZE,w,h));CHECK(MFSetAttributeRatio(input.Get(),MF_MT_FRAME_RATE,30,1));CHECK(input->SetUINT32(MF_MT_INTERLACE_MODE,MFVideoInterlace_Progressive));
  CHECK(transform->SetInputType(0,input.Get(),0));CHECK(OutputType());
  CHECK(transform->ProcessMessage(MFT_MESSAGE_NOTIFY_BEGIN_STREAMING,0));CHECK(transform->ProcessMessage(MFT_MESSAGE_NOTIFY_START_OF_STREAM,0));return S_OK;
 }
 HRESULT Push(const uint8_t* data,UINT size,LONGLONG time){
  ComPtr<IMFSample> sample;ComPtr<IMFMediaBuffer> buffer;CHECK(MFCreateSample(&sample));CHECK(MFCreateMemoryBuffer(size,&buffer));
  BYTE* p;CHECK(buffer->Lock(&p,nullptr,nullptr));memcpy(p,data,size);buffer->Unlock();CHECK(buffer->SetCurrentLength(size));CHECK(sample->AddBuffer(buffer.Get()));CHECK(sample->SetSampleTime(time));CHECK(sample->SetSampleDuration(333333));
  return transform->ProcessInput(0,sample.Get(),0);
 }
 static uint8_t Clamp(int v){return static_cast<uint8_t>(std::max(0,std::min(255,v)));}
 HRESULT Convert(const BYTE* yuv,UINT pitch,UINT textureHeight,BYTE* rgb,UINT capacity,int rotation,UINT* outW,UINT* outH){
  UINT rw=(rotation==90||rotation==270)?height:width,rh=(rotation==90||rotation==270)?width:height;
  if(pitch<width||textureHeight<height||capacity<uint64_t(rw)*rh*4)return E_INVALIDARG;
  const BYTE* chroma=yuv+size_t(pitch)*textureHeight;
  // Convert and rotate together; no intermediate RGB bitmap or extra full-frame rotation.
  for(UINT y=0;y<height;y++)for(UINT x=0;x<width;x++){
   int c=std::max(0,int(yuv[size_t(y)*pitch+x])-(fullRange?0:16)),u=int(chroma[size_t(y/2)*pitch+(x&~1u)])-128,v=int(chroma[size_t(y/2)*pitch+(x&~1u)+1])-128;
   UINT dx=x,dy=y;if(rotation==90){dx=height-1-y;dy=x;}else if(rotation==180){dx=width-1-x;dy=height-1-y;}else if(rotation==270){dx=y;dy=width-1-x;}
   int luma=(fullRange?256:298)*c;
   int blue=fullRange?(bt709?475:454):(bt709?541:516),red=fullRange?(bt709?403:359):(bt709?459:409);
   int greenU=fullRange?(bt709?48:88):(bt709?55:100),greenV=fullRange?(bt709?120:183):(bt709?136:208);
   BYTE* p=rgb+(size_t(dy)*rw+dx)*4;p[0]=Clamp((luma+blue*u+128)>>8);p[1]=Clamp((luma-greenU*u-greenV*v+128)>>8);p[2]=Clamp((luma+red*v+128)>>8);p[3]=255;
  }*outW=rw;*outH=rh;return S_OK;
 }
 HRESULT Pull(BYTE* rgb,UINT capacity,int rotation,UINT* outW,UINT* outH){
  for(int attempts=0;attempts<4;attempts++){
   MFT_OUTPUT_STREAM_INFO info{};CHECK(transform->GetOutputStreamInfo(0,&info));
   ComPtr<IMFSample> supplied;if(!(info.dwFlags&MFT_OUTPUT_STREAM_PROVIDES_SAMPLES)){
    ComPtr<IMFMediaBuffer> allocated;CHECK(MFCreateSample(&supplied));CHECK(MFCreateMemoryBuffer(std::max<DWORD>(info.cbSize,static_cast<DWORD>(stride)*bufferHeight*3/2),&allocated));CHECK(supplied->AddBuffer(allocated.Get()));
   }
   MFT_OUTPUT_DATA_BUFFER output{};output.pSample=supplied.Get();DWORD status=0;HRESULT hr=transform->ProcessOutput(0,1,&output,&status);
   if(output.pEvents)output.pEvents->Release();ComPtr<IMFSample> sample;if(output.pSample&&output.pSample!=supplied.Get())sample.Attach(output.pSample);else sample=supplied;
   if(hr==MF_E_TRANSFORM_STREAM_CHANGE){CHECK(OutputType());continue;}if(hr==MF_E_TRANSFORM_NEED_MORE_INPUT)return S_FALSE;CHECK(hr);if(!sample)return E_UNEXPECTED;
   ComPtr<IMFMediaBuffer> buffer;CHECK(sample->GetBufferByIndex(0,&buffer));
   ComPtr<IMFDXGIBuffer> dxgi;if(hardware&&SUCCEEDED(buffer.As(&dxgi))){
    ComPtr<ID3D11Texture2D> texture;UINT index;CHECK(dxgi->GetResource(IID_PPV_ARGS(&texture)));CHECK(dxgi->GetSubresourceIndex(&index));
    D3D11_TEXTURE2D_DESC desc;texture->GetDesc(&desc);if(desc.Format!=DXGI_FORMAT_NV12||desc.Width>8192||desc.Height>8192)return E_INVALIDARG;
    if(!staging){desc.Usage=D3D11_USAGE_STAGING;desc.BindFlags=0;desc.CPUAccessFlags=D3D11_CPU_ACCESS_READ;desc.MiscFlags=0;desc.ArraySize=1;desc.MipLevels=1;CHECK(device->CreateTexture2D(&desc,nullptr,&staging));}
    context->CopySubresourceRegion(staging.Get(),0,0,0,0,texture.Get(),index,nullptr);D3D11_MAPPED_SUBRESOURCE mapped{};CHECK(context->Map(staging.Get(),0,D3D11_MAP_READ,0,&mapped));
    hr=Convert(static_cast<BYTE*>(mapped.pData),mapped.RowPitch,desc.Height,rgb,capacity,rotation,outW,outH);context->Unmap(staging.Get(),0);return hr;
   }
   ComPtr<IMFMediaBuffer> contiguous;CHECK(sample->ConvertToContiguousBuffer(&contiguous));BYTE* p;DWORD length;CHECK(contiguous->Lock(&p,nullptr,&length));
   if(length<uint64_t(stride)*bufferHeight*3/2)hr=E_INVALIDARG;else hr=Convert(p,stride,bufferHeight,rgb,capacity,rotation,outW,outH);contiguous->Unlock();return hr;
  }return E_FAIL;
 }
};
extern "C" __declspec(dllexport) int __cdecl LensioAvcCreate(int width,int height,int allowGpu,void** handle){
 if(!handle||width<2||height<2||width>1920||height>1920||int64_t(width)*height>2073600||width%2||height%2)return E_INVALIDARG;*handle=nullptr;
 auto decoder=new(std::nothrow) Decoder();if(!decoder)return E_OUTOFMEMORY;HRESULT hr=decoder->Initialize(width,height,allowGpu!=0);
 if(FAILED(hr)&&allowGpu){delete decoder;decoder=new(std::nothrow) Decoder();if(!decoder)return E_OUTOFMEMORY;hr=decoder->Initialize(width,height,false);}
 if(FAILED(hr)){delete decoder;return hr;}*handle=decoder;return S_OK;
}
extern "C" __declspec(dllexport) int __cdecl LensioAvcPush(void* handle,const BYTE* data,int size,LONGLONG time){if(!handle||!data||size<1||size>4*1024*1024)return E_INVALIDARG;return static_cast<Decoder*>(handle)->Push(data,size,time);}
extern "C" __declspec(dllexport) int __cdecl LensioAvcPull(void* handle,BYTE* output,int capacity,int rotation,UINT* w,UINT* h){if(!handle||!output||capacity<1||!w||!h||(rotation!=0&&rotation!=90&&rotation!=180&&rotation!=270))return E_INVALIDARG;return static_cast<Decoder*>(handle)->Pull(output,capacity,rotation,w,h);}
extern "C" __declspec(dllexport) int __cdecl LensioAvcHardware(void* handle){return handle&&static_cast<Decoder*>(handle)->hardware?1:0;}
extern "C" __declspec(dllexport) void __cdecl LensioAvcClose(void* handle){delete static_cast<Decoder*>(handle);}
