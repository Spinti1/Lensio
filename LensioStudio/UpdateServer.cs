using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace ApexCam
{
    public class UpdateServer : IDisposable
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetHandleInformation(IntPtr hObject, uint dwMask, uint dwFlags);
        private const uint HANDLE_FLAG_INHERIT = 1;

        private TcpListener listener;
        private volatile bool isRunning = false;
        private readonly int port;
        private readonly string apkPath;
        private const int CurrentVersionCode = 27;
        private const string CurrentVersionName = "2.9.1";

        public Action OnTriggerUpdateRequested;
        public Action<string> OnPhonePing;

        public int Port { get { return port; } }
        public bool IsRunning { get { return isRunning; } }

        public UpdateServer(int port = 8557, string customApkPath = null)
        {
            this.port = port;
            if (!string.IsNullOrEmpty(customApkPath) && File.Exists(customApkPath))
            {
                this.apkPath = customApkPath;
            }
            else
            {
                this.apkPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "LensioCam.apk");
            }
        }

        public void Start()
        {
            if (isRunning) return;

            try
            {
                listener = new TcpListener(IPAddress.Any, port);
                listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                try { SetHandleInformation(listener.Server.Handle, HANDLE_FLAG_INHERIT, 0); } catch { }
                listener.Start(10);
                isRunning = true;

                ThreadPool.QueueUserWorkItem(_ => ListenLoop());
                Console.WriteLine(string.Format("[UpdateServer] Serverul de actualizare Wi-Fi ruleaza pe portul {0}", port));
            }
            catch (Exception ex)
            {
                Console.WriteLine("[UpdateServer] Error pornire: " + ex.Message);
            }
        }

        private void ListenLoop()
        {
            while (isRunning)
            {
                try
                {
                    TcpClient client = listener.AcceptTcpClient();
                    ThreadPool.QueueUserWorkItem(state => ProcessClient((TcpClient)state), client);
                }
                catch
                {
                    if (!isRunning) break;
                }
            }
        }

        private void ProcessClient(TcpClient client)
        {
            using (client)
            {
                try
                {
                    try { SetHandleInformation(client.Client.Handle, HANDLE_FLAG_INHERIT, 0); } catch { }
                    client.ReceiveTimeout = 10000;
                    client.SendTimeout = 60000;

                    string clientIp = "";
                    try
                    {
                        IPEndPoint ep = client.Client.RemoteEndPoint as IPEndPoint;
                        if (ep != null) clientIp = ep.Address.ToString();
                    }
                    catch { }

                    if (!string.IsNullOrEmpty(clientIp) && clientIp != "127.0.0.1")
                    {
                        try
                        {
                            Action<string> ping = OnPhonePing;
                            if (ping != null) ping(clientIp);
                        }
                        catch { }
                    }

                    using (NetworkStream stream = client.GetStream())
                    using (StreamReader reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true))
                    {
                        string requestLine = reader.ReadLine();
                        if (string.IsNullOrEmpty(requestLine)) return;

                        string[] parts = requestLine.Split(' ');
                        if (parts.Length < 2) return;

                        string method = parts[0].ToUpperInvariant();
                        string rawPath = parts[1].Split('?')[0];

                        // Read until empty line (end of HTTP headers)
                        string line;
                        while (!string.IsNullOrEmpty(line = reader.ReadLine()))
                        {
                            // Drain remaining headers
                        }

                        if (method != "GET" && method != "HEAD")
                        {
                            SendSimpleResponse(stream, 405, "Method Not Allowed", "text/plain", "Metoda nepermisa.");
                            return;
                        }

                        if (rawPath.Equals("/api/trigger-update", StringComparison.OrdinalIgnoreCase) ||
                            rawPath.Equals("/trigger-update", StringComparison.OrdinalIgnoreCase))
                        {
                            if (OnTriggerUpdateRequested != null)
                            {
                                try { OnTriggerUpdateRequested(); } catch { }
                            }
                            SendSimpleResponse(stream, 200, "OK", "application/json; charset=utf-8", "{\"success\":true,\"message\":\"Update triggered on phone\"}");
                        }
                        else if (rawPath.Equals("/api/version", StringComparison.OrdinalIgnoreCase) ||
                            rawPath.Equals("/version", StringComparison.OrdinalIgnoreCase))
                        {
                            HandleVersionRequest(stream);
                        }
                        else if (rawPath.Equals("/download/LensioCam.apk", StringComparison.OrdinalIgnoreCase) || rawPath.Equals("/download/ApexPhoneShare.apk", StringComparison.OrdinalIgnoreCase) ||
                                 rawPath.Equals("/ApexPhoneShare.apk", StringComparison.OrdinalIgnoreCase) ||
                                 rawPath.Equals("/download/ApexCam.apk", StringComparison.OrdinalIgnoreCase) ||
                                 rawPath.Equals("/ApexCam.apk", StringComparison.OrdinalIgnoreCase) ||
                                 rawPath.EndsWith(".apk", StringComparison.OrdinalIgnoreCase))
                        {
                            HandleApkDownload(stream, method == "HEAD");
                        }
                        else
                        {
                            HandleRootInfo(stream);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[UpdateServer] Error client: " + ex.Message);
                }
            }
        }

        private string ResolveApkPath()
        {
            if (File.Exists(apkPath)) return apkPath;
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string branded = Path.Combine(baseDir, "LensioCam.apk");
            if (File.Exists(branded)) return branded;
            string parentBranded = Path.Combine(baseDir, "..", "LensioCam.apk");
            if (File.Exists(parentBranded)) return parentBranded;
            string localBuild = Path.Combine(baseDir, "apexcam", "android", "app", "build", "outputs", "apk", "debug", "app-debug.apk");
            if (File.Exists(localBuild)) return localBuild;
            string legacyLocal = Path.Combine(baseDir, "ApexPhoneShare.apk");
            if (File.Exists(legacyLocal)) return legacyLocal;
            string legacyCamera = Path.Combine(baseDir, "ApexCam.apk");
            if (File.Exists(legacyCamera)) return legacyCamera;
            return apkPath;
        }

        private void HandleVersionRequest(NetworkStream stream)
        {
            try { File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app.log"), DateTime.Now + " - [UpdateServer] Version check requested\n"); } catch { }
            string actualApk = ResolveApkPath();
            long fileSize = 0;
            if (File.Exists(actualApk))
            {
                try { fileSize = new FileInfo(actualApk).Length; } catch { }
            }

            string json = string.Format(
                "{{\"versionCode\":{0},\"versionName\":\"{1}\",\"apkSize\":{2},\"apkUrl\":\"/download/LensioCam.apk\",\"releaseNotes\":\"Camera streaming continues in the background and with the screen off. Use Stop camera to end the session.\"}}",
                CurrentVersionCode, CurrentVersionName, fileSize
            );

            SendSimpleResponse(stream, 200, "OK", "application/json; charset=utf-8", json);
        }

        private void HandleApkDownload(NetworkStream stream, bool headOnly)
        {
            try { File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app.log"), DateTime.Now + " - [UpdateServer] APK DOWNLOAD requested (headOnly=" + headOnly + ")\n"); } catch { }
            string actualApk = ResolveApkPath();
            if (!File.Exists(actualApk))
            {
                SendSimpleResponse(stream, 404, "Not Found", "text/plain; charset=utf-8", "LensioCam.apk was not found on this computer.");
                return;
            }

            try
            {
                FileInfo fi = new FileInfo(actualApk);
                long fileSize = fi.Length;

                StringBuilder sb = new StringBuilder();
                sb.Append("HTTP/1.1 200 OK\r\n");
                sb.Append("Content-Type: application/vnd.android.package-archive\r\n");
                sb.Append(string.Format("Content-Length: {0}\r\n", fileSize));
                sb.Append("Content-Disposition: attachment; filename=\"LensioCam.apk\"\r\n");
                sb.Append("Access-Control-Allow-Origin: *\r\n");
                sb.Append("Connection: close\r\n\r\n");

                byte[] headerBytes = Encoding.ASCII.GetBytes(sb.ToString());
                stream.Write(headerBytes, 0, headerBytes.Length);
                stream.Flush();

                if (headOnly) return;

                // Stream file in 64 KB chunks
                byte[] buffer = new byte[65536];
                using (FileStream fs = new FileStream(actualApk, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    int bytesRead;
                    while ((bytesRead = fs.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        stream.Write(buffer, 0, bytesRead);
                    }
                }
                stream.Flush();
            }
            catch (Exception ex)
            {
                Console.WriteLine("[UpdateServer] Error descarcare APK: " + ex.Message);
            }
        }

        private void HandleRootInfo(NetworkStream stream)
        {
            string actualApk = ResolveApkPath();
            long size = 0;
            if (File.Exists(actualApk))
            {
                try { size = new FileInfo(actualApk).Length / (1024 * 1024); } catch { }
            }

            string html = string.Format(
                "<!DOCTYPE html><html><head><meta charset='utf-8'><title>LensioStudio Update</title>" +
                "<style>body{{font-family:Segoe UI,sans-serif;background:#090d16;color:#f8fafc;padding:40px;line-height:1.6;}}" +
                ".card{{background:#131c2e;border:1px solid #1e293b;border-radius:16px;padding:28px;max-width:540px;margin:auto;box-shadow:0 10px 25px rgba(0,0,0,0.5);}}" +
                ".btn{{display:inline-block;background:#0284c7;color:#fff;padding:12px 24px;border-radius:10px;text-decoration:none;font-weight:bold;margin-top:16px;}}" +
                ".btn:hover{{background:#0ea5e9;}}</style></head><body>" +
                "<div class='card'><h2>🚀 LensioStudio Update Server</h2>" +
                "<p>The local Wi-Fi phone update server is <b>online</b>.</p>" +
                "<p>Current version: <b>v{0} (Build {1})</b></p>" +
                "<p>Package size: <b>{2} MB</b></p>" +
                "<a href='/download/LensioCam.apk' class='btn'>📥 Download LensioCam.apk</a>" +
                "</div></body></html>",
                CurrentVersionName, CurrentVersionCode, size
            );

            SendSimpleResponse(stream, 200, "OK", "text/html; charset=utf-8", html);
        }

        private void SendSimpleResponse(NetworkStream stream, int code, string status, string contentType, string body)
        {
            byte[] bodyBytes = Encoding.UTF8.GetBytes(body);
            StringBuilder sb = new StringBuilder();
            sb.Append(string.Format("HTTP/1.1 {0} {1}\r\n", code, status));
            sb.Append(string.Format("Content-Type: {0}\r\n", contentType));
            sb.Append(string.Format("Content-Length: {0}\r\n", bodyBytes.Length));
            sb.Append("Access-Control-Allow-Origin: *\r\n");
            sb.Append("Connection: close\r\n\r\n");

            byte[] headerBytes = Encoding.ASCII.GetBytes(sb.ToString());
            stream.Write(headerBytes, 0, headerBytes.Length);
            stream.Write(bodyBytes, 0, bodyBytes.Length);
            stream.Flush();
        }

        public void Stop()
        {
            isRunning = false;
            if (listener != null)
            {
                try { listener.Stop(); } catch { }
                listener = null;
            }
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
