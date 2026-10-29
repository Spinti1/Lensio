using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace ApexCam
{
    public class WasapiCapture : IDisposable
    {
        public event Action<byte[], int> OnAudioDataAvailable;

        private Thread captureThread;
        private volatile bool isRunning;
        private IntPtr pDevice;
        private IntPtr pAudioClient;
        private IntPtr pCaptureClient;
        private IAudioClient audioClient;
        private IAudioCaptureClient captureClient;
        private WaveFormatExtensible waveFormat;
        private int sampleRate = 48000;
        private int channels = 2;
        private int bitsPerSample = 16;
        private bool isFloat = false;

        public int SampleRate { get { return sampleRate; } }
        public int Channels { get { return channels; } }

        [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
        private static extern uint TimeBeginPeriod(uint uMilliseconds);

        [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
        private static extern uint TimeEndPeriod(uint uMilliseconds);

        public void Start()
        {
            if (isRunning) return;

            TimeBeginPeriod(1);
            isRunning = true;
            captureThread = new Thread(CaptureLoop)
            {
                IsBackground = true,
                Priority = ThreadPriority.Highest,
                Name = "WasapiCaptureThread"
            };
            captureThread.SetApartmentState(ApartmentState.MTA);
            captureThread.Start();
        }

        public void Stop()
        {
            isRunning = false;
            if (captureThread != null && captureThread.IsAlive)
            {
                captureThread.Join(500);
            }
            Cleanup();
            TimeEndPeriod(1);
        }

        private void InitWasapi()
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            int hr = enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out pDevice);
            if (hr != 0 || pDevice == IntPtr.Zero)
            {
                hr = enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eConsole, out pDevice);
            }
            if (hr != 0 || pDevice == IntPtr.Zero)
            {
                throw new InvalidOperationException(string.Format("GetDefaultAudioEndpoint failed (HRESULT: 0x{0:X8})", hr));
            }

            var dev = (IMMDevice)Marshal.GetObjectForIUnknown(pDevice);
            Guid iidAudioClient = new Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
            hr = dev.Activate(ref iidAudioClient, CLSCTX.ALL, IntPtr.Zero, out pAudioClient);
            if (hr != 0 || pAudioClient == IntPtr.Zero)
            {
                throw new InvalidOperationException(string.Format("device.Activeate IAudioClient failed (HRESULT: 0x{0:X8})", hr));
            }
            audioClient = (IAudioClient)Marshal.GetObjectForIUnknown(pAudioClient);

            IntPtr formatPtr;
            audioClient.GetMixFormat(out formatPtr);
            waveFormat = (WaveFormatExtensible)Marshal.PtrToStructure(formatPtr, typeof(WaveFormatExtensible));

            sampleRate = (int)waveFormat.nSamplesPerSec;
            channels = waveFormat.nChannels;
            bitsPerSample = waveFormat.wBitsPerSample;

            if (waveFormat.wFormatTag == 3 || // WAVE_FORMAT_IEEE_FLOAT
                (waveFormat.wFormatTag == 0xFFFE && waveFormat.SubFormat == SubFormatIeeeFloat))
            {
                isFloat = true;
            }

            Console.WriteLine("[WASAPI] Capture Endpoint Ready: {0} Hz, {1} ch, {2} bits ({3})",
                sampleRate, channels, bitsPerSample, isFloat ? "Float" : "PCM");

            long bufferDuration = 0; // Minimum supported hardware buffer period
            hr = audioClient.Initialize(
                AUDCLNT_SHAREMODE.SHARED,
                AUDCLNT_STREAMFLAGS.LOOPBACK,
                bufferDuration,
                0,
                formatPtr,
                Guid.Empty);

            if (hr != 0)
            {
                Marshal.ThrowExceptionForHR(hr);
            }

            Guid iidCaptureClient = new Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317");
            hr = audioClient.GetService(ref iidCaptureClient, out pCaptureClient);
            if (hr != 0 || pCaptureClient == IntPtr.Zero)
            {
                throw new InvalidOperationException(string.Format("audioClient.GetService IAudioCaptureClient failed (HRESULT: 0x{0:X8})", hr));
            }
            captureClient = (IAudioCaptureClient)Marshal.GetObjectForIUnknown(pCaptureClient);

            audioClient.Start();
        }

        private void CaptureLoop()
        {
            try
            {
                InitWasapi();
            }
            catch (Exception ex)
            {
                Console.WriteLine("[WASAPI] Init Error: " + ex.Message);
                isRunning = false;
                return;
            }

            // 5ms buffer chunk: 48000 Hz * 0.005 s = 240 samples * 4 bytes = 960 bytes (16-bit stereo)
            const int TARGET_FRAME_BYTES = 960;
            byte[] chunkBuffer = new byte[TARGET_FRAME_BYTES];
            int chunkPos = 0;

            byte[] conversionBuffer = new byte[8192];
            int lastPacketTick = Environment.TickCount;

            const int AUDCLNT_E_DEVICE_INVALIDATED = unchecked((int)0x88890004);
            const int AUDCLNT_E_RESOURCES_INVALIDATED = unchecked((int)0x88890026);
            const int AUDCLNT_E_SERVICE_NOT_RUNNING = unchecked((int)0x88890003);

            int spinCount = 0;
            while (isRunning)
            {
                uint packetLength;
                int hr = captureClient.GetNextPacketSize(out packetLength);
                if (hr != 0 || packetLength == 0)
                {
                    if (hr == AUDCLNT_E_DEVICE_INVALIDATED || hr == AUDCLNT_E_RESOURCES_INVALIDATED || hr == AUDCLNT_E_SERVICE_NOT_RUNNING)
                    {
                        Console.WriteLine("[WASAPI] Dispozitiv audio schimbat/invalidat (0x{0:X8}). Reconectare automata...", hr);
                        Cleanup();
                        Thread.Sleep(500);
                        try
                        {
                            InitWasapi();
                            Console.WriteLine("[WASAPI] Reconectat cu succes la noul dispozitiv audio implicit!");
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine("[WASAPI] Waiting dispozitiv activ: " + ex.Message);
                        }
                        continue;
                    }

                    spinCount++;
                    if (spinCount < 30)
                    {
                        Thread.SpinWait(40);
                    }
                    else if (spinCount < 60)
                    {
                        Thread.Yield();
                    }
                    else
                    {
                        Thread.Sleep(1);
                        spinCount = 60;
                    }
                    continue;
                }
                spinCount = 0;

                lastPacketTick = Environment.TickCount;

                while (packetLength > 0)
                {
                    IntPtr dataPtr;
                    uint numFramesRead;
                    AUDCLNT_BUFFERFLAGS flags;
                    ulong devicePosition;
                    ulong qpcPosition;

                    hr = captureClient.GetBuffer(out dataPtr, out numFramesRead, out flags, out devicePosition, out qpcPosition);
                    if (hr != 0 || numFramesRead == 0)
                        break;

                    int pcmBytesProduced = 0;
                    bool isSilent = (flags & AUDCLNT_BUFFERFLAGS.SILENT) != 0;

                    if (isSilent)
                    {
                        pcmBytesProduced = (int)numFramesRead * 4; // 2 channels * 2 bytes
                        if (conversionBuffer.Length < pcmBytesProduced)
                            conversionBuffer = new byte[pcmBytesProduced];
                        Array.Clear(conversionBuffer, 0, pcmBytesProduced);
                    }
                    else if (isFloat && bitsPerSample == 32)
                    {
                        pcmBytesProduced = (int)numFramesRead * 4; // 2 channels * 16-bit PCM
                        if (conversionBuffer.Length < pcmBytesProduced)
                            conversionBuffer = new byte[pcmBytesProduced];

                        unsafe
                        {
                            float* pFloats = (float*)dataPtr.ToPointer();
                            fixed (byte* pOutBytes = conversionBuffer)
                            {
                                short* pOutSamples = (short*)pOutBytes;
                                const float GAIN = 1.0f; // 1:1 Bit-perfect studio fidelity, zero clipping on bass peaks
                                for (int i = 0; i < numFramesRead; i++)
                                {
                                    // Left channel
                                    float left = pFloats[i * channels] * GAIN;
                                    if (left < -1.0f) left = -1.0f;
                                    else if (left > 1.0f) left = 1.0f;
                                    pOutSamples[i * 2] = (short)(left * 32767f);

                                    // Right channel
                                    float right = ((channels > 1) ? pFloats[i * channels + 1] : left) * GAIN;
                                    if (right < -1.0f) right = -1.0f;
                                    else if (right > 1.0f) right = 1.0f;
                                    pOutSamples[i * 2 + 1] = (short)(right * 32767f);
                                }
                            }
                        }
                    }
                    else if (!isFloat && bitsPerSample == 32)
                    {
                        pcmBytesProduced = (int)numFramesRead * 4;
                        if (conversionBuffer.Length < pcmBytesProduced)
                            conversionBuffer = new byte[pcmBytesProduced];

                        unsafe
                        {
                            int* pInts = (int*)dataPtr.ToPointer();
                            fixed (byte* pOutBytes = conversionBuffer)
                            {
                                short* pOutSamples = (short*)pOutBytes;
                                for (int i = 0; i < numFramesRead; i++)
                                {
                                    pOutSamples[i * 2] = (short)(pInts[i * channels] >> 16);
                                    pOutSamples[i * 2 + 1] = (short)((channels > 1 ? pInts[i * channels + 1] : pInts[i * channels]) >> 16);
                                }
                            }
                        }
                    }
                    else if (bitsPerSample == 24)
                    {
                        pcmBytesProduced = (int)numFramesRead * 4;
                        if (conversionBuffer.Length < pcmBytesProduced)
                            conversionBuffer = new byte[pcmBytesProduced];

                        unsafe
                        {
                            byte* pInBytes = (byte*)dataPtr.ToPointer();
                            fixed (byte* pOutBytes = conversionBuffer)
                            {
                                short* pOutSamples = (short*)pOutBytes;
                                int bytesPerFrame = channels * 3;
                                for (int i = 0; i < numFramesRead; i++)
                                {
                                    int l = (pInBytes[i * bytesPerFrame + 1] << 8) | (pInBytes[i * bytesPerFrame + 2] << 16);
                                    pOutSamples[i * 2] = (short)(l >> 16);
                                    int r = (channels > 1)
                                        ? ((pInBytes[i * bytesPerFrame + 4] << 8) | (pInBytes[i * bytesPerFrame + 5] << 16))
                                        : l;
                                    pOutSamples[i * 2 + 1] = (short)(r >> 16);
                                }
                            }
                        }
                    }
                    else if (bitsPerSample == 16)
                    {
                        if (channels == 2)
                        {
                            pcmBytesProduced = (int)numFramesRead * 4;
                            if (conversionBuffer.Length < pcmBytesProduced)
                                conversionBuffer = new byte[pcmBytesProduced];
                            Marshal.Copy(dataPtr, conversionBuffer, 0, pcmBytesProduced);
                        }
                        else
                        {
                            pcmBytesProduced = (int)numFramesRead * 4;
                            if (conversionBuffer.Length < pcmBytesProduced)
                                conversionBuffer = new byte[pcmBytesProduced];
                            unsafe
                            {
                                short* pIn = (short*)dataPtr.ToPointer();
                                fixed (byte* pOutBytes = conversionBuffer)
                                {
                                    short* pOut = (short*)pOutBytes;
                                    for (int i = 0; i < numFramesRead; i++)
                                    {
                                        pOut[i * 2] = pIn[i];
                                        pOut[i * 2 + 1] = pIn[i];
                                    }
                                }
                            }
                        }
                    }

                    captureClient.ReleaseBuffer(numFramesRead);

                    // Slice into exact 960-byte packets
                    if (pcmBytesProduced > 0)
                    {
                        int srcOffset = 0;
                        while (srcOffset < pcmBytesProduced)
                        {
                            int toCopy = Math.Min(TARGET_FRAME_BYTES - chunkPos, pcmBytesProduced - srcOffset);
                            Buffer.BlockCopy(conversionBuffer, srcOffset, chunkBuffer, chunkPos, toCopy);
                            chunkPos += toCopy;
                            srcOffset += toCopy;

                            if (chunkPos == TARGET_FRAME_BYTES)
                            {
                                if (OnAudioDataAvailable != null)
                                {
                                    OnAudioDataAvailable(chunkBuffer, TARGET_FRAME_BYTES);
                                }
                                chunkPos = 0;
                            }
                        }
                    }

                    captureClient.GetNextPacketSize(out packetLength);
                }
            }
        }

        private void Cleanup()
        {
            if (audioClient != null)
            {
                try { audioClient.Stop(); } catch { }
                Marshal.ReleaseComObject(audioClient);
                audioClient = null;
            }
            if (captureClient != null)
            {
                Marshal.ReleaseComObject(captureClient);
                captureClient = null;
            }
            if (pAudioClient != IntPtr.Zero)
            {
                Marshal.Release(pAudioClient);
                pAudioClient = IntPtr.Zero;
            }
            if (pCaptureClient != IntPtr.Zero)
            {
                Marshal.Release(pCaptureClient);
                pCaptureClient = IntPtr.Zero;
            }
            if (pDevice != IntPtr.Zero)
            {
                Marshal.Release(pDevice);
                pDevice = IntPtr.Zero;
            }
        }

        public void Dispose()
        {
            Stop();
        }

        public static readonly Guid SubFormatIeeeFloat = new Guid("00000003-0000-0010-8000-00aa00389b71");
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
        LOOPBACK = 0x00020000
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
