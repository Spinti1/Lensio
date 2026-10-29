using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace ApexCam
{
    public class DiscoveryBeacon : IDisposable
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetHandleInformation(IntPtr hObject, uint dwMask, uint dwFlags);
        private const uint HANDLE_FLAG_INHERIT = 1;

        private UdpClient udpClient;
        private Thread broadcastThread;
        private Thread listenThread;
        private volatile bool isRunning = false;
        public volatile bool IsConnected = false;
        private readonly string localIp;
        private readonly int port;

        public event Action<string, int> OnPhoneFound;

        public DiscoveryBeacon(string localIp, int streamingPort = 8556, int beaconPort = 8555)
        {
            this.localIp = localIp;
            this.port = beaconPort;
        }

        public void Start()
        {
            if (isRunning) return;
            isRunning = true;

            try
            {
                udpClient = new UdpClient();
                udpClient.EnableBroadcast = true;
                udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                try
                {
                    SetHandleInformation(udpClient.Client.Handle, HANDLE_FLAG_INHERIT, 0);
                }
                catch { }

                udpClient.Client.Bind(new IPEndPoint(IPAddress.Any, port));

                // 1. Thread for listening to discovery pings from phone
                listenThread = new Thread(ListenLoop)
                {
                    IsBackground = true,
                    Name = "ApexCam_BeaconListener"
                };
                listenThread.Start();

                // 2. Thread for periodic broadcast
                broadcastThread = new Thread(BroadcastLoop)
                {
                    IsBackground = true,
                    Name = "ApexCam_BeaconBroadcast"
                };
                broadcastThread.Start();
            }
            catch (Exception ex)
            {
                Console.WriteLine("[Beacon] Avertisment pornire beacon UDP: " + ex.Message);
            }
        }

        private List<IPEndPoint> GetAllBroadcastTargets()
        {
            List<IPEndPoint> targets = new List<IPEndPoint>();
            targets.Add(new IPEndPoint(IPAddress.Broadcast, port));

            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                    foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily == AddressFamily.InterNetwork)
                        {
                            string ipStr = ua.Address.ToString();
                            if (ipStr.StartsWith("192.168.") || ipStr.StartsWith("10.") || ipStr.StartsWith("172."))
                            {
                                string[] parts = ipStr.Split('.');
                                if (parts.Length == 4)
                                {
                                    string subStr = string.Format("{0}.{1}.{2}.255", parts[0], parts[1], parts[2]);
                                    targets.Add(new IPEndPoint(IPAddress.Parse(subStr), port));
                                }
                            }
                        }
                    }
                }
            }
            catch { }

            return targets;
        }

        private void BroadcastLoop()
        {
            string message = string.Format("APEXCAM_BEACON|{0}|8556", localIp);
            byte[] bytes = Encoding.UTF8.GetBytes(message);

            while (isRunning)
            {
                try
                {
                    if (udpClient != null)
                    {
                        List<IPEndPoint> targets = GetAllBroadcastTargets();
                        foreach (var target in targets)
                        {
                            try
                            {
                                udpClient.Send(bytes, bytes.Length, target);
                            }
                            catch { }
                        }
                    }
                }
                catch { }

                Thread.Sleep(IsConnected ? 8000 : 1200);
            }
        }

        private void ListenLoop()
        {
            IPEndPoint senderEp = new IPEndPoint(IPAddress.Any, 0);

            while (isRunning)
            {
                try
                {
                    if (udpClient == null) break;
                    byte[] data = udpClient.Receive(ref senderEp);
                    string msg = Encoding.UTF8.GetString(data);

                    if (msg.StartsWith("APEXCAM_DISCOVER"))
                    {
                        string reply = string.Format("APEXCAM_BEACON|{0}|8556", localIp);
                        byte[] replyBytes = Encoding.UTF8.GetBytes(reply);
                        udpClient.Send(replyBytes, replyBytes.Length, senderEp);
                    }
                    else if (msg.StartsWith("APEXCAM_PHONE|"))
                    {
                        string[] parts = msg.Split('|');
                        if (parts.Length >= 3)
                        {
                            string phoneIp = parts[1].Trim();
                            int p = 8080;
                            int.TryParse(parts[2].Trim(), out p);
                            if (OnPhoneFound != null) OnPhoneFound(phoneIp, p);
                        }
                    }
                }
                catch
                {
                    if (!isRunning) break;
                }
            }
        }

        public void Dispose()
        {
            isRunning = false;
            try
            {
                if (udpClient != null) udpClient.Close();
            }
            catch { }
        }
    }
}
