// Synthetic source used only by integration tests; no camera or downloaded video needed.
#include <windows.h>
#include <mfapi.h>
#include <mfidl.h>
#include <mftransform.h>
#include <mferror.h>
#include <wmcodecdsp.h>
#include <codecapi.h>
#include <wrl/client.h>
#include <vector>
#include <cstdio>
using Microsoft::WRL::ComPtr;
#define REQUIRE(x) do{HRESULT hr=(x);if(FAILED(hr)){printf("Failed at %d: %08lx\n",__LINE__,hr);return 1;}}while(0)
int wmain(int argc,wchar_t** argv){
 if(argc<2)return 1;CoInitializeEx(nullptr,COINIT_MULTITHREADED);REQUIRE(MFStartup(MF_VERSION));
 int width=argc>2?_wtoi(argv[2]):1280,height=argc>3?_wtoi(argv[3]):720;
 ComPtr<IMFTransform> encoder;REQUIRE(CoCreateInstance(CLSID_CMSH264EncoderMFT,nullptr,CLSCTX_INPROC_SERVER,IID_PPV_ARGS(&encoder)));
 ComPtr<IMFAttributes> attrs;REQUIRE(encoder->GetAttributes(&attrs));attrs->SetUINT32(MF_LOW_LATENCY,1);
 ComPtr<ICodecAPI> api;encoder.As(&api);if(api){VARIANT zero{};zero.vt=VT_UI4;zero.ulVal=0;api->SetValue(&CODECAPI_AVEncMPVDefaultBPictureCount,&zero);}
 ComPtr<IMFMediaType> out;REQUIRE(MFCreateMediaType(&out));out->SetGUID(MF_MT_MAJOR_TYPE,MFMediaType_Video);out->SetGUID(MF_MT_SUBTYPE,MFVideoFormat_H264);out->SetUINT32(MF_MT_AVG_BITRATE,16000000);out->SetUINT32(MF_MT_MPEG2_PROFILE,66);out->SetUINT32(MF_MT_INTERLACE_MODE,MFVideoInterlace_Progressive);MFSetAttributeSize(out.Get(),MF_MT_FRAME_SIZE,width,height);MFSetAttributeRatio(out.Get(),MF_MT_FRAME_RATE,30,1);MFSetAttributeRatio(out.Get(),MF_MT_PIXEL_ASPECT_RATIO,1,1);REQUIRE(encoder->SetOutputType(0,out.Get(),0));
 ComPtr<IMFMediaType> in;REQUIRE(MFCreateMediaType(&in));in->SetGUID(MF_MT_MAJOR_TYPE,MFMediaType_Video);in->SetGUID(MF_MT_SUBTYPE,MFVideoFormat_NV12);in->SetUINT32(MF_MT_INTERLACE_MODE,MFVideoInterlace_Progressive);MFSetAttributeSize(in.Get(),MF_MT_FRAME_SIZE,width,height);MFSetAttributeRatio(in.Get(),MF_MT_FRAME_RATE,30,1);MFSetAttributeRatio(in.Get(),MF_MT_PIXEL_ASPECT_RATIO,1,1);REQUIRE(encoder->SetInputType(0,in.Get(),0));
 encoder->ProcessMessage(MFT_MESSAGE_NOTIFY_BEGIN_STREAMING,0);encoder->ProcessMessage(MFT_MESSAGE_NOTIFY_START_OF_STREAM,0);
 FILE* file;_wfopen_s(&file,argv[1],L"wb");if(!file)return 1;
 int emitted=0;
 auto drain=[&](){for(int n=0;n<200;n++){
  MFT_OUTPUT_STREAM_INFO info{};if(FAILED(encoder->GetOutputStreamInfo(0,&info)))return false;
  ComPtr<IMFSample> sample;MFCreateSample(&sample);ComPtr<IMFMediaBuffer> buffer;MFCreateMemoryBuffer(info.cbSize?info.cbSize:width*height*2,&buffer);sample->AddBuffer(buffer.Get());
  MFT_OUTPUT_DATA_BUFFER output{};output.pSample=sample.Get();DWORD status;HRESULT hr=encoder->ProcessOutput(0,1,&output,&status);if(output.pEvents)output.pEvents->Release();if(hr==MF_E_TRANSFORM_NEED_MORE_INPUT)return true;if(FAILED(hr))return false;
  ComPtr<IMFMediaBuffer> contiguous;sample->ConvertToContiguousBuffer(&contiguous);BYTE* p;DWORD length;contiguous->Lock(&p,nullptr,&length);UINT32 clean=0;sample->GetUINT32(MFSampleExtension_CleanPoint,&clean);fwrite(&length,4,1,file);fwrite(&clean,4,1,file);fwrite(p,1,length,file);contiguous->Unlock();emitted++;
 }return false;};
 for(int frame=0;frame<60;frame++){
  ComPtr<IMFSample> sample;ComPtr<IMFMediaBuffer> buffer;REQUIRE(MFCreateSample(&sample));REQUIRE(MFCreateMemoryBuffer(width*height*3/2,&buffer));BYTE* p;buffer->Lock(&p,nullptr,nullptr);
  for(int y=0;y<height;y++)for(int x=0;x<width;x++)p[y*width+x]=x<width/2?81:145;
  for(int i=0;i<width*height/2;i+=2){int x=i%width;p[width*height+i]=x<width/2?90:54;p[width*height+i+1]=x<width/2?240:34;}
  // Moving white stripe to distinguish real decoded frames from duplicated output.
  for(int y=height/3;y<height/3+20;y++)for(int x=frame*7%width;x<std::min(width,frame*7%width+20);x++)p[y*width+x]=235;
  buffer->Unlock();buffer->SetCurrentLength(width*height*3/2);sample->AddBuffer(buffer.Get());sample->SetSampleTime(frame*333333LL);sample->SetSampleDuration(333333);
  REQUIRE(encoder->ProcessInput(0,sample.Get(),0));if(!drain())return 2;
 }
 encoder->ProcessMessage(MFT_MESSAGE_COMMAND_DRAIN,0);if(!drain())return 3;fclose(file);printf("Generated %d AVC access units %dx%d\n",emitted,width,height);return emitted>=58?0:4;
}
