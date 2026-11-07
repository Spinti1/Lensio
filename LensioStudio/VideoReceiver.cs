using System;
using System.Drawing;
using System.Collections.Generic;
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
            public bool Avc,Keyframe;public int Width,Height,Rotation;public long Timestamp;
            public byte[] JpegBytes;
            public int Battery;
            public bool IsCharging;
            public float Temp;
            public int PhoneFps;
            public int Rtt;
        }

        private FramePacket latestPacket = null;
        private readonly object packetGate=new object();
        private readonly Queue<FramePacket> avcPackets=new Queue<FramePacket>();
        private volatile bool avcRejected;private int queuedBytes;private bool waitingForKey=true;private DateTime lastKeyRequest=DateTime.MinValue;
        private readonly AutoResetEvent frameReadyEvent = new AutoResetEvent(false);

        public event Action<Bitmap, int, bool, float, int, int> OnFrameReceived;
        public event Action<bool> OnConnectionStateChanged;

        public string DeviceLabel { get; set; }
        public volatile string ActiveCodec="JPEG";
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

        private void RequestKeyframe(){if((DateTime.UtcNow-lastKeyRequest).TotalMilliseconds<1000)return;lastKeyRequest=DateTime.UtcNow;SendControl("{\"videoKeyframe\":true}");}
        private void DecodeLoop()
        {
            AvcVideoDecoder decoder=null;int decoderWidth=0,decoderHeight=0,failedRecoveries=0,healthyFrames=0;bool softwareOnly=false;
            try {while(isRunning){
                frameReadyEvent.WaitOne(40);if(!isRunning)break;
                while(isRunning){
                    FramePacket packet;
                    lock(packetGate){packet=latestPacket;latestPacket=null;if(packet==null&&avcPackets.Count>0){packet=avcPackets.Dequeue();queuedBytes-=packet.JpegBytes.Length;}}
                    if(packet==null)break;if(packet.SessionId!=Interlocked.Read(ref currentSessionId))continue;
                    try {
                        if(packet.Avc){
                            if(decoder==null||decoderWidth!=packet.Width||decoderHeight!=packet.Height){
                                if(decoder!=null)decoder.Dispose();decoder=null;
                                if(!packet.Keyframe){RequestKeyframe();continue;}
                                // The native constructor already falls back to software if GPU setup fails.
                                decoder=new AvcVideoDecoder(packet.Width,packet.Height,!softwareOnly);
                                decoderWidth=packet.Width;decoderHeight=packet.Height;
                                ActiveCodec=decoder.IsHardware?"H.264 GPU":"H.264 CPU";
                                LogConnection("H.264 decoder: "+(decoder.IsHardware?"D3D11 hardware":"Windows software")+" "+decoderWidth+"x"+decoderHeight);
                            }
                            decoder.Decode(packet.JpegBytes,packet.Timestamp,packet.Rotation,bmp=>{
                                DeliverFrame(packet,bmp);
                                if(++healthyFrames>=60){failedRecoveries=0;healthyFrames=60;}
                            });
                        }else{
                            if(decoder!=null){decoder.Dispose();decoder=null;LogConnection("Phone switched to JPEG");}ActiveCodec="JPEG";
                            using(var ms=new MemoryStream(packet.JpegBytes))using(var bmp=new Bitmap(ms))DeliverFrame(packet,bmp);
                        }
                    }catch(Exception error){
                        if(decoder!=null){decoder.Dispose();decoder=null;}
                        if(packet.Avc){
                            healthyFrames=0;failedRecoveries++;
                            lock(packetGate){avcPackets.Clear();queuedBytes=0;waitingForKey=true;}
                            if(failedRecoveries<=2){
                                if(failedRecoveries==2)softwareOnly=true;
                                LogConnection("H.264 resync "+failedRecoveries+": "+error.Message);RequestKeyframe();
                            }else{
                                avcRejected=true;LogConnection("H.264 fallback after bounded recovery: "+error.Message);SendControl("{\"videoCodec\":\"jpeg\"}");
                            }
                        }
                    }
                }
            }}finally{if(decoder!=null)decoder.Dispose();}
        }
        private void DeliverFrame(FramePacket packet,Bitmap image){if(isRunning&&packet.SessionId==Interlocked.Read(ref currentSessionId)&&OnFrameReceived!=null)try{OnFrameReceived(image,packet.Battery,packet.IsCharging,packet.Temp,packet.PhoneFps,packet.Rtt);}catch{}}
        private void QueuePacket(FramePacket packet){
            if(packet.Avc&&avcRejected)return;
            bool key=false;
            lock(packetGate){
                if(!packet.Avc){latestPacket=packet;avcPackets.Clear();queuedBytes=0;waitingForKey=true;}
                else {
                    latestPacket=null;
                    if(avcPackets.Count>=24 || queuedBytes+packet.JpegBytes.Length>4*1024*1024){avcPackets.Clear();queuedBytes=0;waitingForKey=true;key=true;}
                    if(waitingForKey&&!packet.Keyframe)key=true;
                    else{waitingForKey=false;avcPackets.Enqueue(packet);queuedBytes+=packet.JpegBytes.Length;}
                }
            }
            if(key)RequestKeyframe();frameReadyEvent.Set();
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

                    bool avc=magic[0]==76&&magic[1]==72&&magic[2]==50&&magic[3]==52;
                    while (!avc && (magic[0] != 0x41 || magic[1] != 0x50 || magic[2] != 0x43 || magic[3] != 0x4D))
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
                    long timestamp=0;int rotation=0;bool keyframe=false;
                    if(avc){var pts=new byte[8];if(!ReadExact(stream,pts,8))break;timestamp=BitConverter.ToInt64(pts,0);int flags=BitConverter.ToInt32(restHeader,24);rotation=(flags>>8)&0x1ff;keyframe=(flags&1)!=0;
                        if((flags>>24)!=1 || (rotation!=0&&rotation!=90&&rotation!=180&&rotation!=270)||timestamp<0)break;
                    }

                    int width = BitConverter.ToInt32(restHeader, 0);
                    int height = BitConverter.ToInt32(restHeader, 4);
                    int payloadLength = BitConverter.ToInt32(restHeader, 8);
                    int phoneFps = BitConverter.ToInt32(restHeader, 12);
                    int battery = BitConverter.ToInt32(restHeader, 16);
                    float temp = BitConverter.ToInt32(restHeader, 20) / 10.0f;
                    int rtt = BitConverter.ToInt32(restHeader, 24);

                    if (width<=0 || height<=0 || width>8192 || height>8192 || payloadLength <= 0 || payloadLength > 10 * 1024 * 1024) break;

                    if(avc && (width%2!=0||height%2!=0||width>1920||height>1920||(long)width*height>1920*1080||payloadLength>4*1024*1024))break;
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
                            File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app.log"), string.Format("{0} - [FrameStats] Phone: "+DeviceLabel+" Res: {1}x{2}, Size: {3:0.0}KB, PhoneFps: {4}, PcFps: {5}, Battery: {6}, Temp: {7:0.0}C, Codec: {8}\n",
                                DateTime.Now, width, height, payloadLength / 1024.0f, phoneFps, CurrentFps, battery, temp,avc?"H.264 rotation="+rotation:"JPEG"));
                        }
                        catch { }
                        framesCount = 0;
                        lastFpsCalc = now;
                    }

                    bool isCharging = (battery & 0x10000) != 0 || (!avc && rtt == 1);
                    int cleanBattery = battery & 0xFFFF;
                    if (cleanBattery > 100) cleanBattery = 100;
                    if (cleanBattery < 0) cleanBattery = 0;

                    // Pass to decoupled worker thread - never stalls the TCP socket!
                    QueuePacket(new FramePacket
                    {
                        Avc=avc,Keyframe=keyframe,Width=width,Height=height,Rotation=rotation,Timestamp=timestamp,
                        SessionId = sessionId,
                        JpegBytes = jpegBytes,
                        Battery = cleanBattery,
                        IsCharging = isCharging,
                        Temp = temp,
                        PhoneFps = phoneFps,
                        Rtt = avc ? 0 : ((rtt == 1 || rtt == 0) ? 8 : rtt)
                    });
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
            lock(packetGate){latestPacket=null;avcPackets.Clear();queuedBytes=0;}
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
