using System;
using System.Drawing;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace ApexCam
{
    internal class VideoReceiverCore : IDisposable
    {
        private Thread decodeThread;
        private volatile bool isRunning = false;

        private readonly object clientLock = new object();
        private TcpClient currentClient = null;
        private NetworkStream clientStream = null;
        private long currentSessionId = 0;

        private class FramePacket
        {
            public long SessionId;
            public byte[] JpegBytes;
            public int Battery;
            public bool IsCharging;
            public float Temp;
            public int PhoneFps;
            public int Rtt;
        }

        private FramePacket latestPacket = null;
        private readonly AutoResetEvent frameReadyEvent = new AutoResetEvent(false);

        public event Action<Bitmap, int, bool, float, int, int> OnFrameReceived;
        public event Action<bool> OnConnectionStateChanged;

        public string DeviceLabel { get; set; }
        private int framesCount = 0;
        private DateTime lastFpsCalc = DateTime.UtcNow;
        public int CurrentFps { get; private set; }
        public long TotalBytesReceived { get; private set; }

        public bool IsConnected
        {
            get
            {
                lock (clientLock)
                {
                    return currentClient != null && currentClient.Connected;
                }
            }
        }

        public void Adopt(TcpClient client, byte[] firstMagic)
        {
            isRunning=true;
            long sessionId;
            lock(clientLock) {
                sessionId=++currentSessionId;
                currentClient=client;
                clientStream=client.GetStream();
            }
            decodeThread=new Thread(DecodeLoop) {IsBackground=true, Name="Lensio-device-decode"};
            decodeThread.Start();
            new Thread(() => HandleClientSession(client,sessionId,firstMagic)) {IsBackground=true,Name="Lensio-device-read"}.Start();
        }

        private void DecodeLoop()
        {
            while (isRunning)
            {
                frameReadyEvent.WaitOne(40);
                if (!isRunning) break;

                FramePacket packet = Interlocked.Exchange(ref latestPacket, null);
                if (packet == null || packet.SessionId != Interlocked.Read(ref currentSessionId)) continue;

                try
                {
                    using (MemoryStream ms = new MemoryStream(packet.JpegBytes))
                    using (Bitmap decodedBmp = new Bitmap(ms))
                    {
                        if (OnFrameReceived != null && packet.SessionId == Interlocked.Read(ref currentSessionId))
                        {
                            try { OnFrameReceived(decodedBmp, packet.Battery, packet.IsCharging, packet.Temp, packet.PhoneFps, packet.Rtt); } catch { }
                        }
                    }
                }
                catch
                {
                    // Ignore transient corrupt frame
                }
            }
        }

        private void HandleClientSession(TcpClient client, long sessionId, byte[] firstMagic = null)
        {
            NetworkStream stream = null;
            try
            {
                stream = client.GetStream();
                while (isRunning && client.Connected)
                {
                    if (sessionId != Interlocked.Read(ref currentSessionId))
                    {
                        // Newer client session took over - immediately close this client socket!
                        try { client.Close(); } catch { }
                        break;
                    }

                    // 1. Synchronize to 4 magic bytes "APCM" (0x41, 0x50, 0x43, 0x4D)
                    byte[] magic = new byte[4];
                    if(firstMagic!=null) { Buffer.BlockCopy(firstMagic,0,magic,0,4); firstMagic=null; }
                    else if (!ReadExact(stream, magic, 4)) break;

                    while (magic[0] != 0x41 || magic[1] != 0x50 || magic[2] != 0x43 || magic[3] != 0x4D)
                    {
                        int b = stream.ReadByte();
                        if (b < 0) return;
                        magic[0] = magic[1];
                        magic[1] = magic[2];
                        magic[2] = magic[3];
                        magic[3] = (byte)b;
                    }

                    // Read remaining 28 bytes of header
                    byte[] restHeader = new byte[28];
                    if (!ReadExact(stream, restHeader, 28)) break;

                    int width = BitConverter.ToInt32(restHeader, 0);
                    int height = BitConverter.ToInt32(restHeader, 4);
                    int payloadLength = BitConverter.ToInt32(restHeader, 8);
                    int phoneFps = BitConverter.ToInt32(restHeader, 12);
                    int battery = BitConverter.ToInt32(restHeader, 16);
                    float temp = BitConverter.ToInt32(restHeader, 20) / 10.0f;
                    int rtt = BitConverter.ToInt32(restHeader, 24);

                    if (width<=0 || height<=0 || width>8192 || height>8192 || payloadLength <= 0 || payloadLength > 10 * 1024 * 1024) break;

                    // 2. Read payload (JPEG frame) immediately off the socket
                    byte[] jpegBytes = new byte[payloadLength];
                    if (!ReadExact(stream, jpegBytes, payloadLength)) break;

                    TotalBytesReceived += payloadLength;
                    framesCount++;

                    // FPS calculation
                    DateTime now = DateTime.UtcNow;
                    if ((now - lastFpsCalc).TotalMilliseconds >= 1000)
                    {
                        CurrentFps = framesCount;
                        try
                        {
                            File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app.log"), string.Format("{0} - [FrameStats] Phone: "+DeviceLabel+" Res: {1}x{2}, Size: {3:0.0}KB, PhoneFps: {4}, PcFps: {5}, Battery: {6}, Temp: {7:0.0}C\n",
                                DateTime.Now, width, height, payloadLength / 1024.0f, phoneFps, CurrentFps, battery, temp));
                        }
                        catch { }
                        framesCount = 0;
                        lastFpsCalc = now;
                    }

                    bool isCharging = (battery & 0x10000) != 0 || (rtt == 1);
                    int cleanBattery = battery & 0xFFFF;
                    if (cleanBattery > 100) cleanBattery = 100;
                    if (cleanBattery < 0) cleanBattery = 0;

                    // Pass to decoupled worker thread - never stalls the TCP socket!
                    latestPacket = new FramePacket
                    {
                        SessionId = sessionId,
                        JpegBytes = jpegBytes,
                        Battery = cleanBattery,
                        IsCharging = isCharging,
                        Temp = temp,
                        PhoneFps = phoneFps,
                        Rtt = (rtt == 1 || rtt == 0) ? 8 : rtt
                    };
                    frameReadyEvent.Set();
                }
            }
            catch (Exception ex)
            {
                LogConnection("session " + sessionId + " ended: " + ex.Message);
            }
            finally
            {
                try { if (stream != null) stream.Close(); } catch { }
                try { client.Close(); } catch { }

                lock (clientLock)
                {
                    if (currentSessionId == sessionId)
                    {
                        currentClient = null;
                        clientStream = null;
                        if (OnConnectionStateChanged != null)
                        {
                            try { OnConnectionStateChanged(false); } catch { }
                        }
                    }
                }
            }
        }

        private void LogConnection(string message)
        {
            try { File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"app.log"),
                DateTime.Now + " - [Connection] "+DeviceLabel+" " + message + Environment.NewLine); } catch { }
        }

        private bool ReadExact(NetworkStream stream, byte[] buffer, int count)
        {
            int totalRead = 0;
            while (totalRead < count)
            {
                int read = stream.Read(buffer, totalRead, count - totalRead);
                if (read <= 0) return false;
                totalRead += read;
            }
            return true;
        }

        public void SendControl(string controlPayload)
        {
            try
            {
                lock (clientLock)
                {
                    if (clientStream != null && clientStream.CanWrite)
                    {
                        byte[] bytes = Encoding.UTF8.GetBytes(controlPayload + "\n");
                        clientStream.Write(bytes, 0, bytes.Length);
                        clientStream.Flush();
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[Receiver] Error transmitere control: " + ex.Message);
            }
        }

        public void Stop()
        {
            isRunning = false;
            try { frameReadyEvent.Set(); } catch { }
            try
            {
                lock (clientLock)
                {
                    if (clientStream != null) clientStream.Close();
                    if (currentClient != null) currentClient.Close();
                    clientStream = null;
                    currentClient = null;
                }
            }
            catch { }
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
