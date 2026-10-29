using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;

namespace ApexCam
{
    [ComImport]
    [Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioRenderClient
    {
        [PreserveSig] int GetBuffer(int numFramesRequested, out IntPtr pData);
        [PreserveSig] int ReleaseBuffer(int numFramesWritten, int dwFlags);
    }

    [ComImport]
    [Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out int pcDevices);
        [PreserveSig] int Item(int nDevice, out IntPtr ppDevice);
    }

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPropertyStore
    {
        [PreserveSig] int GetCount(out int cProps);
        [PreserveSig] int GetAt(int iProp, out PropertyKey pkey);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant pv);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant pv);
        [PreserveSig] int Commit();
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PropertyKey
    {
        public Guid fmtid;
        public int pid;

        public PropertyKey(Guid guid, int id)
        {
            fmtid = guid;
            pid = id;
        }
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct PropVariant
    {
        [FieldOffset(0)] public short vt;
        [FieldOffset(8)] public IntPtr pwszVal;
    }

    public class MicReceiver : IDisposable
    {
        public const ushort MIC_MAGIC = 0x4D49; // 'M', 'I'
        public const int HEADER_SIZE = 8;
        public const int DEFAULT_PAYLOAD_SIZE = 960; // 10ms @ 48kHz Mono 16-bit
        public const int PACKET_SIZE = HEADER_SIZE + DEFAULT_PAYLOAD_SIZE; // 968 bytes

        [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
        private static extern uint TimeBeginPeriod(uint uMilliseconds);

        [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
        private static extern uint TimeEndPeriod(uint uMilliseconds);

        private readonly int port;
        private UdpClient udpSocket;
        private TcpListener tcpListener;
        private volatile bool isRunning;
        private Thread receiveThread;
        private Thread tcpListenThread;
        private Thread renderThread;

        private readonly object tcpClientLock = new object();
        private readonly List<TcpClient> activeTcpClients = new List<TcpClient>();

        // WASAPI Rendering state
        private IntPtr pDevice = IntPtr.Zero;
        private IntPtr pAudioClient = IntPtr.Zero;
        private IntPtr pRenderClient = IntPtr.Zero;
        private IAudioClient audioClient;
        private IAudioRenderClient renderClient;
        private WaveFormatExtensible waveFormat;
        private int targetSampleRate = 48000;
        private int targetChannels = 2;
        private bool isTargetFloat = false;
        private bool isDeviceActive = false;
        private string activeDeviceName = "Necunoscut";

        // Jitter Buffer (Ring buffer for smooth playback)
        private const int BUFFER_FRAMES = 128; // ~1.28 seconds
        private readonly byte[][] audioBuffer = new byte[BUFFER_FRAMES][];
        private int bufferHead = 0;
        private int bufferTail = 0;
        private int bufferedCount = 0;
        private readonly object bufferLock = new object();

        // Statistics
        private long totalPacketsReceived = 0;
        private long totalBytesReceived = 0;
        private int lastPacketTick = 0;

        public long TotalPacketsReceived { get { return totalPacketsReceived; } }
        public long TotalBytesReceived { get { return totalBytesReceived; } }
        public bool IsDeviceActive { get { return isDeviceActive; } }
        public string ActiveDeviceName { get { return activeDeviceName; } }
        public bool HasRecentActivity { get { return Environment.TickCount - lastPacketTick < 3000; } }

        public volatile bool IsMuted = false;
        public volatile float VolumeGain = 1.20f;
        private volatile int currentRmsLevel = 0;
        public int CurrentRmsLevel { get { return currentRmsLevel; } }

        public MicReceiver(int port = 44101)
        {
            this.port = port;
            for (int i = 0; i < BUFFER_FRAMES; i++)
            {
                audioBuffer[i] = new byte[DEFAULT_PAYLOAD_SIZE];
            }
        }

        public void Start()
        {
            if (isRunning) return;
            isRunning = true;

            TimeBeginPeriod(1);

            // 1. Initialize UDP socket
            try
            {
                udpSocket = new UdpClient();
                udpSocket.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                udpSocket.Client.Bind(new IPEndPoint(IPAddress.Any, port));
                udpSocket.Client.ReceiveBufferSize = 128 * 1024;
                Console.WriteLine("[MIC UDP] Ascultare activă pe portul UDP {0}...", port);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[MIC UDP EROARE BIND] Nu s-a putut asculta pe portul {0}: {1}", port, ex.Message);
            }

            // 2. Initialize TCP Listener (for USB cable reverse forwarding: adb reverse tcp:44101 tcp:44101)
            try
            {
                tcpListener = new TcpListener(IPAddress.Any, port);
                tcpListener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                tcpListener.Server.LingerState = new LingerOption(false, 0);
                tcpListener.Start();
                Console.WriteLine("[MIC TCP] Ascultare activă pe portul TCP {0} (pentru USB cablu)...", port);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[MIC TCP EROARE BIND] Nu s-a putut asculta TCP pe portul {0}: {1}", port, ex.Message);
            }

            // 3. Try initializing WASAPI Render endpoint (VB-Audio CABLE Input)
            TryInitWasapiRender();

            // 4. Start Receive Thread (UDP)
            receiveThread = new Thread(ReceiveLoop)
            {
                IsBackground = true,
                Priority = ThreadPriority.Highest,
                Name = "ApexCamMicReceiveUdp"
            };
            receiveThread.Start();

            // 5. Start TCP Listen Thread
            if (tcpListener != null)
            {
                tcpListenThread = new Thread(TcpListenLoop)
                {
                    IsBackground = true,
                    Priority = ThreadPriority.Normal,
                    Name = "ApexCamMicTcpListen"
                };
                tcpListenThread.Start();
            }

            // 6. Start Render Thread
            renderThread = new Thread(RenderLoop)
            {
                IsBackground = true,
                Priority = ThreadPriority.Highest,
                Name = "ApexCamMicRender"
            };
            renderThread.Start();
        }

        private static void SafeRelease(ref IntPtr ptr, string label)
        {
            if (ptr != IntPtr.Zero)
            {
                try
                {
                    Marshal.Release(ptr);
                }
                catch { }
                ptr = IntPtr.Zero;
            }
        }

        public void TryInitWasapiRender()
        {
            try
            {
                var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
                IntPtr pCollection = IntPtr.Zero;
                int hr = enumerator.EnumAudioEndpoints(EDataFlow.eRender, 1 /* DEVICE_STATE_ACTIVE */, out pCollection);
                if (hr == 0 && pCollection != IntPtr.Zero)
                {
                    var collection = (IMMDeviceCollection)Marshal.GetObjectForIUnknown(pCollection);
                    int count = 0;
                    hr = collection.GetCount(out count);
                    if (hr == 0)
                    {
                        IntPtr primaryDev = IntPtr.Zero;
                        string primaryName = "";
                        IntPtr fallbackDev = IntPtr.Zero;
                        string fallbackName = "";

                        PropertyKey PKEY_Device_FriendlyName = new PropertyKey(
                            new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);

                        for (int i = 0; i < count; i++)
                        {
                            IntPtr pDev = IntPtr.Zero;
                            hr = collection.Item(i, out pDev);
                            if (hr == 0 && pDev != IntPtr.Zero)
                            {
                                var dev = (IMMDevice)Marshal.GetObjectForIUnknown(pDev);
                                IntPtr pPropStore = IntPtr.Zero;
                                hr = dev.OpenPropertyStore(0 /* STGM_READ */, out pPropStore);
                                if (hr == 0 && pPropStore != IntPtr.Zero)
                                {
                                    var store = (IPropertyStore)Marshal.GetObjectForIUnknown(pPropStore);
                                    PropVariant pv = new PropVariant();
                                    hr = store.GetValue(ref PKEY_Device_FriendlyName, out pv);
                                    if (hr == 0 && pv.vt == 31 && pv.pwszVal != IntPtr.Zero)
                                    {
                                        string name = Marshal.PtrToStringUni(pv.pwszVal);
                                        if (!string.IsNullOrEmpty(name))
                                        {
                                            bool isRealCableInput = name.IndexOf("CABLE Input", StringComparison.OrdinalIgnoreCase) >= 0 &&
                                                                    name.IndexOf("16ch", StringComparison.OrdinalIgnoreCase) < 0;
                                            bool isAnyCable = name.IndexOf("CABLE", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                              name.IndexOf("VB-Audio", StringComparison.OrdinalIgnoreCase) >= 0;

                                            if (isRealCableInput)
                                            {
                                                primaryDev = pDev;
                                                primaryName = name;
                                                SafeRelease(ref pPropStore, "pPropStore");
                                                break;
                                            }
                                            else if (isAnyCable && fallbackDev == IntPtr.Zero)
                                            {
                                                fallbackDev = pDev;
                                                fallbackName = name;
                                            }
                                        }
                                    }
                                    SafeRelease(ref pPropStore, "pPropStore");
                                }
                                if (primaryDev == IntPtr.Zero && pDev != fallbackDev)
                                {
                                    SafeRelease(ref pDev, "pDev");
                                }
                            }
                        }

                        SafeRelease(ref pCollection, "pCollection");

                        IntPtr chosenDev = primaryDev != IntPtr.Zero ? primaryDev : fallbackDev;
                        string chosenName = primaryDev != IntPtr.Zero ? primaryName : fallbackName;

                        if (chosenDev != IntPtr.Zero)
                        {
                            if (fallbackDev != IntPtr.Zero && fallbackDev != chosenDev)
                            {
                                SafeRelease(ref fallbackDev, "fallbackDev");
                            }
                            InitAudioClientOnDevice(chosenDev, chosenName);
                            return;
                        }
                    }
                    else
                    {
                        SafeRelease(ref pCollection, "pCollection");
                    }
                }

                Console.WriteLine("[MIC RECEIVER] VB-Cable nu este activat inca. Apasati butonul de instalare VB-Cable.");
            }
            catch (Exception ex)
            {
                Console.WriteLine("[MIC RECEIVER] Initializare WASAPI Render: " + ex.Message);
            }
        }

        private void InitAudioClientOnDevice(IntPtr devPtr, string name)
        {
            try
            {
                pDevice = devPtr;
                activeDeviceName = name;
                var dev = (IMMDevice)Marshal.GetObjectForIUnknown(pDevice);

                Guid iidAudioClient = new Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
                int hr = dev.Activate(ref iidAudioClient, CLSCTX.ALL, IntPtr.Zero, out pAudioClient);
                if (hr != 0 || pAudioClient == IntPtr.Zero)
                {
                    return;
                }

                audioClient = (IAudioClient)Marshal.GetObjectForIUnknown(pAudioClient);

                IntPtr formatPtr;
                audioClient.GetMixFormat(out formatPtr);
                waveFormat = (WaveFormatExtensible)Marshal.PtrToStructure(formatPtr, typeof(WaveFormatExtensible));

                targetSampleRate = (int)waveFormat.nSamplesPerSec;
                targetChannels = waveFormat.nChannels;
                isTargetFloat = (waveFormat.wFormatTag == 3 ||
                    (waveFormat.wFormatTag == 0xFFFE && waveFormat.SubFormat == WasapiCapture.SubFormatIeeeFloat));

                long hnsBufferDuration = 200000; // 20ms buffer
                hr = audioClient.Initialize(
                    AUDCLNT_SHAREMODE.SHARED,
                    0,
                    hnsBufferDuration,
                    0,
                    formatPtr,
                    Guid.Empty);

                if (hr != 0)
                {
                    return;
                }

                Guid iidRenderClient = typeof(IAudioRenderClient).GUID;
                hr = audioClient.GetService(ref iidRenderClient, out pRenderClient);
                if (hr != 0 || pRenderClient == IntPtr.Zero)
                {
                    return;
                }

                renderClient = (IAudioRenderClient)Marshal.GetObjectForIUnknown(pRenderClient);
                audioClient.Start();
                isDeviceActive = true;

                Console.WriteLine("[MIC RECEIVER] Conectat la dispozitivul virtual: {0} ({1} Hz, {2} ch)",
                    activeDeviceName, targetSampleRate, targetChannels);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[MIC RECEIVER] Error activare dispozitiv: " + ex.Message);
            }
        }

        private void ReceiveLoop()
        {
            byte[] recvBuffer = new byte[2048];
            EndPoint remoteEp = new IPEndPoint(IPAddress.Any, 0);

            while (isRunning)
            {
                try
                {
                    if (udpSocket == null)
                    {
                        Thread.Sleep(100);
                        continue;
                    }

                    int bytesRead = udpSocket.Client.ReceiveFrom(recvBuffer, ref remoteEp);
                    if (bytesRead >= HEADER_SIZE)
                    {
                        if (recvBuffer[0] == 0x4D && recvBuffer[1] == 0x49) // 'M', 'I'
                        {
                            int payloadLen = bytesRead - HEADER_SIZE;
                            if (payloadLen > 0)
                            {
                                EnqueueAudioFrame(recvBuffer, HEADER_SIZE, payloadLen);
                            }
                        }
                    }
                }
                catch (SocketException)
                {
                    if (!isRunning) break;
                    Thread.Sleep(2);
                }
                catch
                {
                    Thread.Sleep(2);
                }
            }
        }

        private void TcpListenLoop()
        {
            while (isRunning)
            {
                try
                {
                    TcpClient client = tcpListener.AcceptTcpClient();
                    client.NoDelay = true;
                    client.ReceiveBufferSize = 128 * 1024;
                    lock (tcpClientLock)
                    {
                        activeTcpClients.Add(client);
                    }
                    Console.WriteLine("[MIC TCP/USB] Telefon CONNECTED prin cablu USB pentru microfon: {0}", client.Client.RemoteEndPoint);

                    Thread clientThread = new Thread(() => HandleTcpClient(client))
                    {
                        IsBackground = true,
                        Priority = ThreadPriority.Highest,
                        Name = "ApexCamMicTcpClientWorker"
                    };
                    clientThread.Start();
                }
                catch (SocketException)
                {
                    if (!isRunning) break;
                    Thread.Sleep(50);
                }
                catch
                {
                    if (!isRunning) break;
                    Thread.Sleep(50);
                }
            }
        }

        private void HandleTcpClient(TcpClient client)
        {
            byte[] packetBuf = new byte[PACKET_SIZE];
            try
            {
                NetworkStream stream = client.GetStream();
                while (isRunning && client.Connected)
                {
                    int totalRead = 0;
                    while (totalRead < PACKET_SIZE)
                    {
                        int r = stream.Read(packetBuf, totalRead, PACKET_SIZE - totalRead);
                        if (r <= 0) return;
                        totalRead += r;
                    }

                    if (packetBuf[0] == 0x4D && packetBuf[1] == 0x49) // 'M', 'I'
                    {
                        EnqueueAudioFrame(packetBuf, HEADER_SIZE, DEFAULT_PAYLOAD_SIZE);
                    }
                }
            }
            catch { }
            finally
            {
                lock (tcpClientLock)
                {
                    activeTcpClients.Remove(client);
                }
                try { client.Close(); } catch { }
                Console.WriteLine("[MIC TCP/USB] Client cablu USB deconectat.");
            }
        }

        private void EnqueueAudioFrame(byte[] src, int offset, int payloadLen)
        {
            lock (bufferLock)
            {
                while (bufferedCount >= 5)
                {
                    bufferTail = (bufferTail + 1) % BUFFER_FRAMES;
                    bufferedCount--;
                }

                Buffer.BlockCopy(src, offset, audioBuffer[bufferHead], 0, Math.Min(payloadLen, DEFAULT_PAYLOAD_SIZE));
                bufferHead = (bufferHead + 1) % BUFFER_FRAMES;
                bufferedCount++;
            }
            Interlocked.Increment(ref totalPacketsReceived);
            Interlocked.Add(ref totalBytesReceived, payloadLen + HEADER_SIZE);
            lastPacketTick = Environment.TickCount;
        }

        private void RenderLoop()
        {
            byte[] frameToRender = new byte[DEFAULT_PAYLOAD_SIZE];
            byte[] zeroSilence = new byte[DEFAULT_PAYLOAD_SIZE];

            while (isRunning)
            {
                if (!isDeviceActive || renderClient == null || audioClient == null)
                {
                    if (Environment.TickCount % 5000 < 50)
                    {
                        TryInitWasapiRender();
                    }
                    Thread.Sleep(50);
                    continue;
                }

                try
                {
                    uint padding = 0;
                    int hrPad = audioClient.GetCurrentPadding(out padding);
                    uint bufferSize = 0;
                    int hrBuf = audioClient.GetBufferSize(out bufferSize);

                    if (hrPad == 0 && hrBuf == 0)
                    {
                        int availableFrames = (int)(bufferSize - padding);
                        int framesPerPacket = DEFAULT_PAYLOAD_SIZE / 2; // 480 samples mono (10ms @ 48kHz)

                        while (availableFrames >= framesPerPacket)
                        {
                            bool hasData = false;
                            lock (bufferLock)
                            {
                                if (bufferedCount > 0)
                                {
                                    Buffer.BlockCopy(audioBuffer[bufferTail], 0, frameToRender, 0, DEFAULT_PAYLOAD_SIZE);
                                    bufferTail = (bufferTail + 1) % BUFFER_FRAMES;
                                    bufferedCount--;
                                    hasData = true;
                                }
                            }

                            if (hasData)
                            {
                                IntPtr pBuffer;
                                int hr = renderClient.GetBuffer(framesPerPacket, out pBuffer);
                                if (hr == 0 && pBuffer != IntPtr.Zero)
                                {
                                    WriteSamplesToWasapi(frameToRender, framesPerPacket, pBuffer);
                                    renderClient.ReleaseBuffer(framesPerPacket, 0);
                                    availableFrames -= framesPerPacket;
                                }
                                else
                                {
                                    break;
                                }
                            }
                            else
                            {
                                if (padding < 120 && totalPacketsReceived > 0)
                                {
                                    IntPtr pBuffer;
                                    int hr = renderClient.GetBuffer(framesPerPacket, out pBuffer);
                                    if (hr == 0 && pBuffer != IntPtr.Zero)
                                    {
                                        WriteSamplesToWasapi(zeroSilence, framesPerPacket, pBuffer);
                                        renderClient.ReleaseBuffer(framesPerPacket, 0);
                                        availableFrames -= framesPerPacket;
                                    }
                                }
                                break;
                            }
                        }
                    }
                }
                catch { }

                Thread.Sleep(2);
            }
        }

        private unsafe void WriteSamplesToWasapi(byte[] mono16Bytes, int frameCount, IntPtr destPtr)
        {
            if (IsMuted)
            {
                currentRmsLevel = 0;
                if (isTargetFloat)
                {
                    float* destFloat = (float*)destPtr;
                    int total = frameCount * targetChannels;
                    for (int i = 0; i < total; i++) *destFloat++ = 0f;
                }
                else
                {
                    short* destShort = (short*)destPtr;
                    int total = frameCount * targetChannels;
                    for (int i = 0; i < total; i++) *destShort++ = 0;
                }
                return;
            }

            fixed (byte* pSrc = mono16Bytes)
            {
                short* srcShort = (short*)pSrc;
                float gain = VolumeGain;

                long sumSq = 0;
                for (int i = 0; i < frameCount; i++)
                {
                    int val = srcShort[i];
                    sumSq += (long)val * val;
                }
                currentRmsLevel = (int)Math.Sqrt((double)sumSq / frameCount);

                if (isTargetFloat)
                {
                    float* destFloat = (float*)destPtr;
                    for (int i = 0; i < frameCount; i++)
                    {
                        float sample = (srcShort[i] / 32768.0f) * gain;
                        if (sample > 1.0f) sample = 1.0f;
                        else if (sample < -1.0f) sample = -1.0f;

                        for (int ch = 0; ch < targetChannels; ch++)
                        {
                            *destFloat++ = sample;
                        }
                    }
                }
                else
                {
                    short* destShort = (short*)destPtr;
                    for (int i = 0; i < frameCount; i++)
                    {
                        int val = (int)(srcShort[i] * gain);
                        short sample = (short)(val > 32767 ? 32767 : (val < -32768 ? -32768 : val));

                        for (int ch = 0; ch < targetChannels; ch++)
                        {
                            *destShort++ = sample;
                        }
                    }
                }
            }
        }

        public void Stop()
        {
            isRunning = false;
            TimeEndPeriod(1);

            if (tcpListener != null)
            {
                try { tcpListener.Stop(); } catch { }
                tcpListener = null;
            }

            lock (tcpClientLock)
            {
                foreach (var c in activeTcpClients)
                {
                    try { c.Close(); } catch { }
                }
                activeTcpClients.Clear();
            }

            if (udpSocket != null)
            {
                try { udpSocket.Close(); } catch { }
                udpSocket = null;
            }

            if (audioClient != null)
            {
                try { audioClient.Stop(); } catch { }
            }

            if (pRenderClient != IntPtr.Zero) { Marshal.Release(pRenderClient); pRenderClient = IntPtr.Zero; }
            if (pAudioClient != IntPtr.Zero) { Marshal.Release(pAudioClient); pAudioClient = IntPtr.Zero; }
            if (pDevice != IntPtr.Zero) { Marshal.Release(pDevice); pDevice = IntPtr.Zero; }
            audioClient = null;
            renderClient = null;
            isDeviceActive = false;
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
