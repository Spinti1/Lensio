using System;
using System.Drawing;
using System.IO;
using System.Net;
using System.Threading;

namespace ApexCam
{
    public class MjpegReceiver : IDisposable
    {
        private readonly object stateLock = new object();
        private HttpWebRequest activeRequest;
        private long generation;
        private volatile bool isRunning;
        public event Action<Bitmap> OnFrameReceived;
        public event Action<bool> OnConnectionStateChanged;
        public int CurrentFps { get; private set; }
        public bool IsRunning { get { return isRunning; } }

        public void Connect(string url)
        {
            lock (stateLock)
            {
                Stop();
                var request = (HttpWebRequest)WebRequest.Create(url);
                request.Timeout = 4000;
                request.ReadWriteTimeout = 4000;
                request.KeepAlive = false;
                activeRequest = request;
                isRunning = true;
                long ticket = ++generation;
                new Thread(() => StreamLoop(request, ticket)) { IsBackground = true, Name = "Lensio-MJPEG" }.Start();
            }
        }

        private bool IsCurrent(long ticket) { lock (stateLock) return isRunning && generation == ticket; }

        private void StreamLoop(HttpWebRequest request, long ticket)
        {
            try
            {
                using (var response = request.GetResponse())
                using (var stream = response.GetResponseStream())
                using (var pending = new MemoryStream())
                {
                    lock (stateLock)
                    {
                        if (!IsCurrent(ticket)) return;
                        if (OnConnectionStateChanged != null) OnConnectionStateChanged(true);
                    }
                    byte[] buffer = new byte[65536];
                    int count = 0;
                    DateTime since = DateTime.UtcNow;
                    while (IsCurrent(ticket))
                    {
                        int read = stream.Read(buffer, 0, buffer.Length);
                        if (read <= 0) break;
                        pending.Write(buffer, 0, read);
                        byte[] data = pending.ToArray();
                        int consumed = 0, latestStart = -1, latestEnd = -1;
                        // Consume every complete JPEG, decode only the newest.
                        while (true)
                        {
                            int start = Find(data, consumed, 0xD8);
                            if (start < 0) break;
                            int end = Find(data, start + 2, 0xD9);
                            if (end < 0) break;
                            latestStart = start;
                            latestEnd = end + 2;
                            consumed = latestEnd;
                        }
                        if (latestStart >= 0)
                        {
                            using (var jpeg = new MemoryStream(data, latestStart, latestEnd - latestStart))
                            using (var bitmap = new Bitmap(jpeg))
                            {
                                if (IsCurrent(ticket) && OnFrameReceived != null) OnFrameReceived(bitmap);
                            }
                            count++;
                            double elapsed = (DateTime.UtcNow - since).TotalSeconds;
                            if (elapsed >= 1) { CurrentFps = (int)(count / elapsed); count = 0; since = DateTime.UtcNow; }
                            pending.SetLength(0);
                            pending.Write(data, consumed, data.Length - consumed);
                        }
                        if (pending.Length > 10 * 1024 * 1024) throw new IOException("MJPEG frame exceeds limit.");
                    }
                }
            }
            catch (Exception ex) { Console.WriteLine("[MJPEG] " + ex.Message); }
            finally
            {
                request.Abort();
                lock (stateLock)
                {
                    if (generation == ticket)
                    {
                        activeRequest = null;
                        isRunning = false;
                        CurrentFps = 0;
                        if (OnConnectionStateChanged != null) OnConnectionStateChanged(false);
                    }
                }
            }
        }

        private static int Find(byte[] bytes, int start, byte marker)
        {
            for (int i = start; i + 1 < bytes.Length; i++)
                if (bytes[i] == 0xFF && bytes[i + 1] == marker) return i;
            return -1;
        }

        public void Stop()
        {
            lock (stateLock)
            {
                generation++;
                isRunning = false;
                CurrentFps = 0;
                var request = activeRequest;
                activeRequest = null;
                if (request != null) request.Abort();
            }
        }
        public void Dispose() { Stop(); }
    }
}
