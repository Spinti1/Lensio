using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace ApexCam
{
    public class UdpStreamer : IDisposable
    {
        public const ushort MAGIC = 0x5341; // 'S', 'A'
        public const int HEADER_SIZE = 8;
        public const int PAYLOAD_SIZE = 960;
        public const int PACKET_SIZE = HEADER_SIZE + PAYLOAD_SIZE; // 968 bytes

        [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
        private static extern uint TimeBeginPeriod(uint uMilliseconds);

        [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
        private static extern uint TimeEndPeriod(uint uMilliseconds);

        private readonly int port;
        private UdpClient udpSocket;
        private TcpListener tcpListener;
        private volatile bool isRunning;
        private Thread discoveryThread;
        private Thread tcpListenThread;
        private Thread transmitThread;

        private readonly object clientLock = new object();
        private readonly Dictionary<string, IPEndPoint> connectedClients = new Dictionary<string, IPEndPoint>();
        private readonly Dictionary<string, int> clientLastSeen = new Dictionary<string, int>();

        private readonly object tcpClientLock = new object();
        private readonly List<TcpClient> connectedTcpClients = new List<TcpClient>();

        // Pre-allocated RAM pool for zero-GC audio queue and pacing
        private const int QUEUE_CAPACITY = 512; // 512 * 5ms = 2.56 seconds pre-allocated in RAM
        private readonly byte[][] framePool = new byte[QUEUE_CAPACITY][];
        private int queueHead = 0;
        private int queueTail = 0;
        private int queueCount = 0;
        private readonly object queueLock = new object();

        private readonly byte[] sendPacketBuffer = new byte[PACKET_SIZE];
        private readonly byte[] transmitFrameBuffer = new byte[PAYLOAD_SIZE];

        private ushort sequenceNumber = 0;
        private long totalPacketsSent = 0;
        private long totalBytesSent = 0;

        public long TotalPacketsSent { get { return totalPacketsSent; } }
        public long TotalBytesSent { get { return totalBytesSent; } }
        public volatile bool IsMuted = false;
        private volatile int currentPlaybackRmsLevel = 0;
        public int CurrentPlaybackRmsLevel { get { return currentPlaybackRmsLevel; } }
        public int ConnectedClientCount
        {
            get
            {
                int udpCount;
                lock (clientLock) { udpCount = connectedClients.Count; }
                int tcpCount;
                lock (tcpClientLock) { tcpCount = connectedTcpClients.Count; }
                return udpCount + tcpCount;
            }
        }
        public int Port { get { return port; } }

        public UdpStreamer(int port = 44100)
        {
            this.port = port;
            for (int i = 0; i < QUEUE_CAPACITY; i++)
            {
                framePool[i] = new byte[PAYLOAD_SIZE];
            }
        }

        public void Start()
        {
            if (isRunning) return;

            try
            {
                udpSocket = new UdpClient();
                udpSocket.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                try
                {
                    // WMM / 802.11e Voice Priority QoS (DSCP 46 EF = 0xB8)
                    // Tells the Wi-Fi router to transmit audio ahead of any YouTube/web video traffic on other phones!
                    udpSocket.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.TypeOfService, 0xB8);
                }
                catch { }
                udpSocket.Client.Bind(new IPEndPoint(IPAddress.Any, port));
                udpSocket.EnableBroadcast = true;
                try
                {
                    udpSocket.Client.SendBufferSize = 128 * 1024;
                    udpSocket.Client.ReceiveBufferSize = 64 * 1024;
                }
                catch { }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[AUDIO UDP] Nu s-a putut porni serverul UDP pe portul {0}: {1}", port, ex.Message);
            }

            try
            {
                tcpListener = new TcpListener(IPAddress.Any, port);
                tcpListener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                tcpListener.Server.LingerState = new LingerOption(false, 0);
                tcpListener.Start();
            }
            catch (Exception ex)
            {
                Console.WriteLine("[AUDIO TCP] Nu s-a putut porni serverul TCP pe portul {0}: {1}", port, ex.Message);
            }

            isRunning = true;

            discoveryThread = new Thread(ListenLoop)
            {
                IsBackground = true,
                Priority = ThreadPriority.Normal,
                Name = "ApexCamAudioUdpListen"
            };
            discoveryThread.Start();

            if (tcpListener != null)
            {
                tcpListenThread = new Thread(TcpListenLoop)
                {
                    IsBackground = true,
                    Priority = ThreadPriority.Normal,
                    Name = "ApexCamAudioTcpListen"
                };
                tcpListenThread.Start();
            }

            transmitThread = new Thread(TransmitLoop)
            {
                IsBackground = true,
                Priority = ThreadPriority.Highest,
                Name = "ApexCamAudioTransmit"
            };
            transmitThread.Start();
        }

        public void AddClient(IPEndPoint endpoint)
        {
            lock (clientLock)
            {
                string key = endpoint.ToString();
                connectedClients[key] = endpoint;
                clientLastSeen[key] = Environment.TickCount;
            }
        }

        public void RemoveClient(IPEndPoint endpoint)
        {
            lock (clientLock)
            {
                string key = endpoint.ToString();
                connectedClients.Remove(key);
                clientLastSeen.Remove(key);
            }
        }

        public void SendAudioFrame(byte[] pcmData, int length)
        {
            if (!isRunning || pcmData == null || length < PAYLOAD_SIZE)
                return;

            lock (queueLock)
            {
                if (queueCount >= 6)
                {
                    queueTail = (queueTail + 1) % QUEUE_CAPACITY;
                    queueCount--;
                }

                Buffer.BlockCopy(pcmData, 0, framePool[queueHead], 0, PAYLOAD_SIZE);
                queueHead = (queueHead + 1) % QUEUE_CAPACITY;
                queueCount++;

                Monitor.Pulse(queueLock);
            }
        }

        private void TransmitLoop()
        {
            TimeBeginPeriod(1);
            Stopwatch sw = Stopwatch.StartNew();
            double nextSendMs = 0;
            bool isFirst = true;
            int silenceCycles = 0;

            while (isRunning)
            {
                bool hasFrame = false;

                int clients = ConnectedClientCount;
                if (clients == 0)
                {
                    Thread.Sleep(25);
                    isFirst = true;
                    silenceCycles = 0;
                    continue;
                }

                lock (queueLock)
                {
                    if (queueCount > 0)
                    {
                        Buffer.BlockCopy(framePool[queueTail], 0, transmitFrameBuffer, 0, PAYLOAD_SIZE);
                        queueTail = (queueTail + 1) % QUEUE_CAPACITY;
                        queueCount--;
                        hasFrame = true;
                        silenceCycles = 0;
                    }
                }

                if (!hasFrame)
                {
                    silenceCycles++;
                    // When PC is silent (no music/audio playing), throttle keepalive to 1 packet every 50ms (every 10 cycles)
                    // instead of flooding 200 packets/sec of digital zeros over the Wi-Fi network!
                    if (silenceCycles % 10 == 0)
                    {
                        Array.Clear(transmitFrameBuffer, 0, PAYLOAD_SIZE);
                        hasFrame = true;
                    }
                    else
                    {
                        Thread.Sleep(5);
                        continue;
                    }
                }

                if (hasFrame)
                {
                    double nowMs = sw.Elapsed.TotalMilliseconds;
                    if (isFirst || Math.Abs(nowMs - nextSendMs) > 15.0)
                    {
                        nextSendMs = nowMs;
                        isFirst = false;
                    }

                    while (isRunning)
                    {
                        double currentMs = sw.Elapsed.TotalMilliseconds;
                        double diff = nextSendMs - currentMs;
                        if (diff <= 0)
                            break;

                        if (diff > 1.5)
                        {
                            Thread.Sleep(1);
                        }
                        else
                        {
                            Thread.SpinWait(25);
                        }
                    }

                    if (!isRunning) break;

                    SendPacketToClients(transmitFrameBuffer);
                    nextSendMs += 5.0; // Steady 5.0ms cadence (200 packets/sec)
                }
            }

            TimeEndPeriod(1);
        }

        private void SendPacketToClients(byte[] pcmData)
        {
            List<IPEndPoint> targets;
            lock (clientLock)
            {
                if (connectedClients.Count > 0)
                    targets = new List<IPEndPoint>(connectedClients.Values);
                else
                    targets = null;
            }

            List<TcpClient> tcpTargets;
            lock (tcpClientLock)
            {
                if (connectedTcpClients.Count > 0)
                    tcpTargets = new List<TcpClient>(connectedTcpClients);
                else
                    tcpTargets = null;
            }

            if (targets == null && tcpTargets == null)
                return;

            // Packet layout:
            // [0..1]: Magic 0x53, 0x41 ('S', 'A')
            // [2..3]: Seq (uint16 big endian)
            // [4..7]: Timestamp ms (uint32 big endian)
            // [8..967]: PCM Data (960 bytes)
            sendPacketBuffer[0] = 0x53; // 'S'
            sendPacketBuffer[1] = 0x41; // 'A'

            ushort seq = sequenceNumber++;
            sendPacketBuffer[2] = (byte)(seq >> 8);
            sendPacketBuffer[3] = (byte)(seq & 0xFF);

            uint ts = (uint)(Environment.TickCount & 0xFFFFFFFF);
            sendPacketBuffer[4] = (byte)(ts >> 24);
            sendPacketBuffer[5] = (byte)((ts >> 16) & 0xFF);
            sendPacketBuffer[6] = (byte)((ts >> 8) & 0xFF);
            sendPacketBuffer[7] = (byte)(ts & 0xFF);

            if (IsMuted)
            {
                currentPlaybackRmsLevel = 0;
                Array.Clear(sendPacketBuffer, HEADER_SIZE, PAYLOAD_SIZE);
            }
            else
            {
                Buffer.BlockCopy(pcmData, 0, sendPacketBuffer, HEADER_SIZE, PAYLOAD_SIZE);
                int sampleCount = PAYLOAD_SIZE / 2;
                long sumSq = 0;
                for (int i = 0; i < sampleCount; i++)
                {
                    short s = (short)(pcmData[i * 2] | (pcmData[i * 2 + 1] << 8));
                    sumSq += (long)s * s;
                }
                currentPlaybackRmsLevel = (int)Math.Sqrt((double)sumSq / sampleCount);
            }

            // 1. Dispatch to UDP targets (Wi-Fi)
            if (targets != null)
            {
                for (int i = 0; i < targets.Count; i++)
                {
                    try
                    {
                        udpSocket.Send(sendPacketBuffer, PACKET_SIZE, targets[i]);
                        Interlocked.Increment(ref totalPacketsSent);
                        Interlocked.Add(ref totalBytesSent, PACKET_SIZE);
                    }
                    catch { }
                }
            }

            // 2. Dispatch to TCP targets (Wi-Fi LAN or Direct USB Cable)
            if (tcpTargets != null)
            {
                List<TcpClient> deadClients = null;
                for (int i = 0; i < tcpTargets.Count; i++)
                {
                    TcpClient client = tcpTargets[i];
                    try
                    {
                        if (!client.Connected)
                        {
                            if (deadClients == null) deadClients = new List<TcpClient>();
                            deadClients.Add(client);
                            continue;
                        }

                        // Check if client disconnected (received FIN)
                        if (client.Client.Poll(0, SelectMode.SelectRead) && client.Client.Available == 0)
                        {
                            if (deadClients == null) deadClients = new List<TcpClient>();
                            deadClients.Add(client);
                            continue;
                        }

                        // Non-blocking write check: only write if kernel socket buffer has space
                        if (client.Client.Poll(0, SelectMode.SelectWrite))
                        {
                            NetworkStream ns = client.GetStream();
                            ns.Write(sendPacketBuffer, 0, PACKET_SIZE);
                            Interlocked.Increment(ref totalPacketsSent);
                            Interlocked.Add(ref totalBytesSent, PACKET_SIZE);
                        }
                    }
                    catch
                    {
                        if (deadClients == null) deadClients = new List<TcpClient>();
                        deadClients.Add(client);
                    }
                }

                if (deadClients != null)
                {
                    lock (tcpClientLock)
                    {
                        for (int i = 0; i < deadClients.Count; i++)
                        {
                            connectedTcpClients.Remove(deadClients[i]);
                            try { deadClients[i].Close(); } catch { }
                            Console.WriteLine("[AUDIO TCP] Client deconectat.");
                        }
                    }
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
                    client.SendBufferSize = 128 * 1024;
                    client.SendTimeout = 25;
                    client.LingerState = new LingerOption(false, 0);
                    try
                    {
                        // WMM Voice Priority QoS (DSCP 46 EF = 0xB8)
                        client.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.TypeOfService, 0xB8);
                    }
                    catch { }

                    lock (tcpClientLock)
                    {
                        connectedTcpClients.Add(client);
                    }
                    Console.WriteLine("[AUDIO TCP] Telefon CONNECTED: {0}", client.Client.RemoteEndPoint);
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

        private void ListenLoop()
        {
            IPEndPoint remoteEp = new IPEndPoint(IPAddress.Any, 0);

            while (isRunning)
            {
                try
                {
                    if (udpSocket != null && udpSocket.Available > 0)
                    {
                        byte[] data = udpSocket.Receive(ref remoteEp);
                        if (data != null && data.Length > 0)
                        {
                            string msg = Encoding.ASCII.GetString(data).Trim();

                            if (msg.StartsWith("DISCOVER_AUDIO_SERVER"))
                            {
                                byte[] resp = Encoding.ASCII.GetBytes("AUDIO_SERVER_HERE:" + port);
                                udpSocket.Send(resp, resp.Length, remoteEp);
                            }
                            else if (msg.StartsWith("CONNECT"))
                            {
                                AddClient(remoteEp);
                                byte[] ack = Encoding.ASCII.GetBytes("CONNECTED_OK");
                                udpSocket.Send(ack, ack.Length, remoteEp);
                                Console.WriteLine("[AUDIO UDP] Telefon Conectat: {0}", remoteEp);
                            }
                            else if (msg.StartsWith("DISCONNECT"))
                            {
                                RemoveClient(remoteEp);
                                Console.WriteLine("[AUDIO UDP] Telefon Disconnected: {0}", remoteEp);
                            }
                            else if (msg.StartsWith("PING"))
                            {
                                AddClient(remoteEp);
                                byte[] pong = Encoding.ASCII.GetBytes(msg.Replace("PING", "PONG"));
                                udpSocket.Send(pong, pong.Length, remoteEp);
                            }
                        }
                    }
                    else
                    {
                        int now = Environment.TickCount;
                        lock (clientLock)
                        {
                            var toRemove = new List<string>();
                            foreach (var kvp in clientLastSeen)
                            {
                                if (now - kvp.Value > 15000)
                                    toRemove.Add(kvp.Key);
                            }
                            foreach (var key in toRemove)
                            {
                                connectedClients.Remove(key);
                                clientLastSeen.Remove(key);
                            }
                        }

                        Thread.Sleep(5);
                    }
                }
                catch (SocketException)
                {
                    if (!isRunning) break;
                    Thread.Sleep(10);
                }
                catch
                {
                    Thread.Sleep(10);
                }
            }
        }

        public void Stop()
        {
            isRunning = false;
            lock (queueLock)
            {
                Monitor.PulseAll(queueLock);
            }

            if (tcpListener != null)
            {
                try { tcpListener.Stop(); } catch { }
                tcpListener = null;
            }

            lock (tcpClientLock)
            {
                foreach (var client in connectedTcpClients)
                {
                    try { client.Close(); } catch { }
                }
                connectedTcpClients.Clear();
            }

            if (udpSocket != null)
            {
                try { udpSocket.Close(); } catch { }
                udpSocket = null;
            }

            if (transmitThread != null && transmitThread.IsAlive)
            {
                transmitThread.Join(500);
            }
            if (tcpListenThread != null && tcpListenThread.IsAlive)
            {
                tcpListenThread.Join(500);
            }
            if (discoveryThread != null && discoveryThread.IsAlive)
            {
                discoveryThread.Join(500);
            }
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
