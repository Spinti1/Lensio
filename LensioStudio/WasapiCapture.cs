using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace ApexCam
{
    // The capture worker owns every COM interface from initialization through cleanup.
    // Windows performs high-quality conversion to the protocol's 48 kHz stereo PCM16.
    public class WasapiCapture : IDisposable
    {
        public event Action<byte[],int> OnAudioDataAvailable;
        private readonly object lifecycle=new object();
        private Thread captureThread;
        private volatile bool isRunning,hasCapture;
        private IntPtr pDevice,pAudioClient,pCaptureClient;
        private IAudioClient audioClient;
        private IAudioCaptureClient captureClient;
        public int SampleRate {get{return 48000;}}
        public int Channels {get{return 2;}}
        public bool IsCapturing {get{return hasCapture;}}
        public string LastError {get;private set;}
        [DllImport("winmm.dll",EntryPoint="timeBeginPeriod")]static extern uint TimeBeginPeriod(uint ms);
        [DllImport("winmm.dll",EntryPoint="timeEndPeriod")]static extern uint TimeEndPeriod(uint ms);
        public void Start(){lock(lifecycle){
            if(isRunning||(captureThread!=null&&captureThread.IsAlive))return;
            isRunning=true;
            captureThread=new Thread(CaptureLoop){IsBackground=true,Name="Lensio-audio-capture",Priority=ThreadPriority.AboveNormal};
            captureThread.SetApartmentState(ApartmentState.MTA);captureThread.Start();
        }}
        public void Stop(){Thread worker;lock(lifecycle){isRunning=false;worker=captureThread;}
            if(worker!=null&&worker!=Thread.CurrentThread)worker.Join(1500);
        }
        private void InitWasapi(){
            IMMDeviceEnumerator enumerator=null;IMMDevice device=null;IntPtr format=IntPtr.Zero;
            try{
                enumerator=(IMMDeviceEnumerator)new MMDeviceEnumerator();
                int hr=enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender,ERole.eMultimedia,out pDevice);
                if(hr<0||pDevice==IntPtr.Zero)hr=enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender,ERole.eConsole,out pDevice);
                Marshal.ThrowExceptionForHR(hr);if(pDevice==IntPtr.Zero)throw new InvalidOperationException("No playback endpoint available");
                device=(IMMDevice)Marshal.GetObjectForIUnknown(pDevice);
                Guid audioId=new Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
                Marshal.ThrowExceptionForHR(device.Activate(ref audioId,CLSCTX.ALL,IntPtr.Zero,out pAudioClient));
                audioClient=(IAudioClient)Marshal.GetObjectForIUnknown(pAudioClient);
                var pcm=new WaveFormatExtensible{wFormatTag=1,nChannels=2,nSamplesPerSec=48000,nAvgBytesPerSec=192000,nBlockAlign=4,wBitsPerSample=16,cbSize=0};
                format=Marshal.AllocCoTaskMem(40);Marshal.StructureToPtr(pcm,format,false);
                Marshal.ThrowExceptionForHR(audioClient.Initialize(AUDCLNT_SHAREMODE.SHARED,
                    AUDCLNT_STREAMFLAGS.LOOPBACK|AUDCLNT_STREAMFLAGS.AUTOCONVERTPCM|AUDCLNT_STREAMFLAGS.SRC_DEFAULT_QUALITY,
                    0,0,format,Guid.Empty));
                Guid captureId=new Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317");
                Marshal.ThrowExceptionForHR(audioClient.GetService(ref captureId,out pCaptureClient));
                captureClient=(IAudioCaptureClient)Marshal.GetObjectForIUnknown(pCaptureClient);
                Marshal.ThrowExceptionForHR(audioClient.Start());
                LastError=null;hasCapture=true;
            }finally{
                if(format!=IntPtr.Zero)Marshal.FreeCoTaskMem(format);
                if(device!=null)Marshal.ReleaseComObject(device);
                if(enumerator!=null)Marshal.ReleaseComObject(enumerator);
            }
        }
        private void CaptureLoop(){
            TimeBeginPeriod(1);
            try{while(isRunning){
                try{InitWasapi();CapturePackets();}
                catch(Exception error){LastError=error.Message;Console.WriteLine("[WASAPI] Recovering audio capture: "+error.Message);}
                finally{hasCapture=false;Cleanup();}
                if(isRunning)Thread.Sleep(300);
            }}finally{isRunning=false;TimeEndPeriod(1);}
        }
        private void CapturePackets(){
            var pcm=new byte[8192];var chunk=new byte[960];int chunkPos=0;
            while(isRunning){
                uint packetLength;Marshal.ThrowExceptionForHR(captureClient.GetNextPacketSize(out packetLength));
                if(packetLength==0){Thread.Sleep(2);continue;}
                IntPtr data;uint frames;AUDCLNT_BUFFERFLAGS flags;ulong deviceTime,clockTime;
                Marshal.ThrowExceptionForHR(captureClient.GetBuffer(out data,out frames,out flags,out deviceTime,out clockTime));
                int length=0;
                try{
                    if(frames>48000)throw new InvalidOperationException("Invalid audio capture packet size");
                    length=checked((int)frames*4);if(pcm.Length<length)pcm=new byte[length];
                    if((flags&AUDCLNT_BUFFERFLAGS.SILENT)!=0)Array.Clear(pcm,0,length);
                    else if(length>0)Marshal.Copy(data,pcm,0,length);
                }finally{Marshal.ThrowExceptionForHR(captureClient.ReleaseBuffer(frames));}
                for(int offset=0;offset<length;){
                    int copy=Math.Min(chunk.Length-chunkPos,length-offset);Buffer.BlockCopy(pcm,offset,chunk,chunkPos,copy);chunkPos+=copy;offset+=copy;
                    if(chunkPos==chunk.Length){var available=OnAudioDataAvailable;if(available!=null)available(chunk,chunk.Length);chunkPos=0;}
                }
            }
        }
        private void Cleanup(){
            // Never release these from the UI thread while GetBuffer is in progress.
            if(audioClient!=null)try{audioClient.Stop();}catch{}
            if(captureClient!=null){try{Marshal.ReleaseComObject(captureClient);}catch{}captureClient=null;}
            if(audioClient!=null){try{Marshal.ReleaseComObject(audioClient);}catch{}audioClient=null;}
            if(pCaptureClient!=IntPtr.Zero){Marshal.Release(pCaptureClient);pCaptureClient=IntPtr.Zero;}
            if(pAudioClient!=IntPtr.Zero){Marshal.Release(pAudioClient);pAudioClient=IntPtr.Zero;}
            if(pDevice!=IntPtr.Zero){Marshal.Release(pDevice);pDevice=IntPtr.Zero;}
        }
        public void Dispose(){Stop();}
        public static readonly Guid SubFormatIeeeFloat=new Guid("00000003-0000-0010-8000-00aa00389b71");
    }
    #region COM Interop Definitions

    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    public class MMDeviceEnumerator { }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(EDataFlow dataFlow, uint dwStateMask, out IntPtr ppDevices);
        [PreserveSig] int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IntPtr ppDevice);
        [PreserveSig] int GetDevice(string pwstrId, out IntPtr ppDevice);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr pClient);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr pClient);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, CLSCTX dwClsCtx, IntPtr pActivationParams, out IntPtr ppInterface);
        [PreserveSig] int OpenPropertyStore(uint stgmAccess, out IntPtr ppProperties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string ppstrId);
        [PreserveSig] int GetState(out uint pdwState);
    }

    [ComImport]
    [Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioClient
    {
        [PreserveSig] int Initialize(AUDCLNT_SHAREMODE ShareMode, AUDCLNT_STREAMFLAGS StreamFlags, long hnsBufferDuration, long hnsPeriodicity, IntPtr pFormat, [MarshalAs(UnmanagedType.LPStruct)] Guid AudioSessionGuid);
        [PreserveSig] int GetBufferSize(out uint pNumBufferFrames);
        [PreserveSig] int GetStreamLatency(out long phnsLatency);
        [PreserveSig] int GetCurrentPadding(out uint pNumPaddingFrames);
        [PreserveSig] int IsFormatSupported(AUDCLNT_SHAREMODE ShareMode, IntPtr pFormat, out IntPtr ppClosestMatch);
        [PreserveSig] int GetMixFormat(out IntPtr ppDeviceFormat);
        [PreserveSig] int GetDevicePeriod(out long phnsDefaultDevicePeriod, out long phnsMinimumDevicePeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr eventHandle);
        [PreserveSig] int GetService(ref Guid riid, out IntPtr ppv);
    }

    [ComImport]
    [Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr ppData, out uint pNumFramesToRead, out AUDCLNT_BUFFERFLAGS pdwFlags, out ulong pu64DevicePosition, out ulong pu64QPCPosition);
        [PreserveSig] int ReleaseBuffer(uint NumFramesRead);
        [PreserveSig] int GetNextPacketSize(out uint pNumFramesInNextPacket);
    }

    public enum EDataFlow { eRender = 0, eCapture = 1, eAll = 2 }
    public enum ERole { eConsole = 0, eMultimedia = 1, eCommunications = 2 }
    public enum CLSCTX { INPROC_SERVER = 1, INPROC_HANDLER = 2, LOCAL_SERVER = 4, ALL = 23 }
    public enum AUDCLNT_SHAREMODE { SHARED = 0, EXCLUSIVE = 1 }
    [Flags]
    public enum AUDCLNT_STREAMFLAGS : uint
    {
        LOOPBACK = 0x00020000,
        AUTOCONVERTPCM = 0x80000000,
        SRC_DEFAULT_QUALITY = 0x08000000
    }
    [Flags]
    public enum AUDCLNT_BUFFERFLAGS : uint
    {
        DATA_DISCONTINUITY = 0x1,
        SILENT = 0x2,
        TIMESTAMP_ERROR = 0x4
    }

    [StructLayout(LayoutKind.Explicit, Size = 40)]
    public struct WaveFormatExtensible
    {
        [FieldOffset(0)] public ushort wFormatTag;
        [FieldOffset(2)] public ushort nChannels;
        [FieldOffset(4)] public uint nSamplesPerSec;
        [FieldOffset(8)] public uint nAvgBytesPerSec;
        [FieldOffset(12)] public ushort nBlockAlign;
        [FieldOffset(14)] public ushort wBitsPerSample;
        [FieldOffset(16)] public ushort cbSize;
        [FieldOffset(18)] public ushort wValidBitsPerSample;
        [FieldOffset(20)] public uint dwChannelMask;
        [FieldOffset(24)] public Guid SubFormat;
    }
    #endregion
}
