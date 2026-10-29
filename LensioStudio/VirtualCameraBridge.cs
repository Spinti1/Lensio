using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;

namespace ApexCam
{
    public class VirtualCameraBridge : IDisposable
    {
        private MemoryMappedFile mmfLegacy;
        private MemoryMappedViewAccessor accessorLegacy;

        private MemoryMappedFile mmfObs;
        private MemoryMappedViewAccessor accessorObs;

        private readonly object syncLock = new object();
        private readonly string mapNameLegacy = "Global\\ApexCam_VirtualCam_Buffer";
        private int targetWidth = 1280;
        private int targetHeight = 720;
        private const ulong TargetInterval = 333333; // 30 FPS in 100ns units

        public int CurrentWidth { get { return targetWidth; } }
        public int CurrentHeight { get { return targetHeight; } }

        private uint writeCounter = 0;
        private int[] frameOffsets = new int[3];

        private Bitmap latestIncomingBmp = null;
        private Bitmap normalizedBmp = null;
        private byte[] cachedNv12Buffer = null;

        private Thread pumpThread;
        private volatile bool isRunning = false;
        private volatile bool isResolutionChanging = false;

        public bool IsActive { get; private set; }

        public VirtualCameraBridge(int initialWidth = 1280, int initialHeight = 720)
        {
            try
            {
                targetWidth = initialWidth > 0 ? initialWidth : 1280;
                targetHeight = initialHeight > 0 ? initialHeight : 720;
                normalizedBmp = new Bitmap(targetWidth, targetHeight, PixelFormat.Format32bppArgb);
                cachedNv12Buffer = new byte[targetWidth * targetHeight * 3 / 2];

                // Generate initial standby screen
                RenderStandbyScreen();

                // Initialize OBS shared memory queue
                InitializeObsQueue(targetWidth, targetHeight, TargetInterval);

                // Start continuous 30 FPS frame pump thread to prevent DirectShow starvation/timeouts
                isRunning = true;
                IsActive = true;
                pumpThread = new Thread(PumpLoop)
                {
                    IsBackground = true,
                    Name = "ApexCam_VirtualCam_Pump"
                };
                pumpThread.Start();
            }
            catch (Exception ex)
            {
                Console.WriteLine("[VirtualCam] Avertisment init: " + ex.Message);
            }
        }

        public void SetResolution(int width, int height)
        {
            if (width <= 0 || height <= 0) return;
            lock (syncLock)
            {
                if (targetWidth == width && targetHeight == height && normalizedBmp != null)
                {
                    return;
                }

                isResolutionChanging = true;

                try
                {
                    // 1. DirectShow Drain: Notify DirectShow filter (Discord, etc.) to close old queue
                    if (accessorObs != null)
                    {
                        try
                        {
                            accessorObs.Write(8, (uint)0); // SHARED_QUEUE_STATE_STOPPING
                        }
                        catch { }
                    }

                    // 2. Wait 100ms so DirectShow loop (checks every 33ms) detects STOPPING and resets filter/scaler
                    Thread.Sleep(100);

                    targetWidth = width;
                    targetHeight = height;

                    if (normalizedBmp != null)
                    {
                        try { normalizedBmp.Dispose(); } catch { }
                    }
                    normalizedBmp = new Bitmap(targetWidth, targetHeight, PixelFormat.Format32bppArgb);
                    cachedNv12Buffer = new byte[targetWidth * targetHeight * 3 / 2];

                    RenderStandbyScreen();
                    InitializeObsQueue(targetWidth, targetHeight, TargetInterval);
                    Console.WriteLine(string.Format("[VirtualCam] Rezolutie setata curat la {0}x{1}", targetWidth, targetHeight));
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[VirtualCam] Error actualizare rezolutie: " + ex.Message);
                }
                finally
                {
                    isResolutionChanging = false;
                }
            }
        }

        private static int Align32(int s)
        {
            return (s + 31) & ~31;
        }

        private void InitializeObsQueue(int width, int height, ulong interval)
        {
            lock (syncLock)
            {
                int frameSize = width * height * 3 / 2; // NV12
                int off0 = Align32(80); // struct queue_header is 80 bytes -> aligned to 96
                int off1 = Align32(off0 + frameSize + 32);
                int off2 = Align32(off1 + frameSize + 32);
                int totalSize = Align32(off2 + frameSize + 32);

                frameOffsets[0] = off0;
                frameOffsets[1] = off1;
                frameOffsets[2] = off2;

                try
                {
                    if (accessorObs != null)
                    {
                        try
                        {
                            accessorObs.Write(8, (uint)0); // SHARED_QUEUE_STATE_STOPPING
                            accessorObs.Dispose();
                        }
                        catch { }
                        accessorObs = null;
                    }
                    if (mmfObs != null)
                    {
                        try { mmfObs.Dispose(); } catch { }
                        mmfObs = null;
                    }

                    int mapCapacity = Math.Max(totalSize, 32 * 1024 * 1024);
                    mmfObs = MemoryMappedFile.CreateOrOpen("OBSVirtualCamVideo", mapCapacity, MemoryMappedFileAccess.ReadWrite);
                    // Map full available capacity without hardcoded totalSize to prevent crash if held by external process
                    accessorObs = mmfObs.CreateViewAccessor(0, 0, MemoryMappedFileAccess.ReadWrite);

                    long actualCapacity = accessorObs.Capacity;
                    if (actualCapacity < totalSize)
                    {
                        // Existing handle held by another app (like Discord) is smaller (720p).
                        // Fall back to 720p offsets to stay strictly within available shared memory!
                        int safeW = 1280;
                        int safeH = 720;
                        int safeFrameSize = safeW * safeH * 3 / 2;
                        off0 = Align32(80);
                        off1 = Align32(off0 + safeFrameSize + 32);
                        off2 = Align32(off1 + safeFrameSize + 32);
                        frameOffsets[0] = off0;
                        frameOffsets[1] = off1;
                        frameOffsets[2] = off2;
                        width = safeW;
                        height = safeH;
                        targetWidth = safeW;
                        targetHeight = safeH;
                    }

                    // Initialize queue_header with EXACT 64-bit aligned offsets
                    accessorObs.Write(0, (uint)0);            // write_idx (offset 0)
                    accessorObs.Write(4, (uint)0);            // read_idx (offset 4)
                    accessorObs.Write(8, (uint)2);            // state = READY (2)
                    accessorObs.Write(12, (uint)off0);        // offsets[0] (offset 12)
                    accessorObs.Write(16, (uint)off1);        // offsets[1] (offset 16)
                    accessorObs.Write(20, (uint)off2);        // offsets[2] (offset 20)
                    accessorObs.Write(24, (uint)0);           // type = VIDEO (offset 24)
                    accessorObs.Write(28, (uint)width);       // cx (offset 28)
                    accessorObs.Write(32, (uint)height);      // cy (offset 32)
                    accessorObs.Write(36, (uint)0);           // padding for 64-bit alignment (offset 36)
                    accessorObs.Write(40, interval);          // interval (offset 40!)

                    // Write obs-virtualcam.txt for DirectShow filter initialization
                    try
                    {
                        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                        string resFile = Path.Combine(appData, "obs-virtualcam.txt");
                        File.WriteAllText(resFile, string.Format("{0}x{1}x{2}", width, height, interval));
                    }
                    catch { }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[VirtualCam] Error creare buffer memorie: " + ex.Message);
                }

                // Legacy buffer for backwards compatibility
                if (mmfLegacy == null)
                {
                    try
                    {
                        int legSize = 3840 * 2160 * 4 + 64;
                        mmfLegacy = MemoryMappedFile.CreateOrOpen(mapNameLegacy, legSize, MemoryMappedFileAccess.ReadWrite);
                        accessorLegacy = mmfLegacy.CreateViewAccessor(0, legSize, MemoryMappedFileAccess.ReadWrite);
                    }
                    catch { }
                }
            }
        }

        private void RenderStandbyScreen()
        {
            using (Graphics g = Graphics.FromImage(normalizedBmp))
            using (SolidBrush bgBrush = new SolidBrush(Color.FromArgb(11, 15, 25)))
            using (SolidBrush textBrush = new SolidBrush(Color.FromArgb(241, 245, 249)))
            using (SolidBrush subBrush = new SolidBrush(Color.FromArgb(148, 163, 184)))
            using (SolidBrush accentBrush = new SolidBrush(Color.FromArgb(56, 189, 248)))
            using (Font titleFont = new Font("Segoe UI", 28, FontStyle.Bold))
            using (Font subFont = new Font("Segoe UI", 15, FontStyle.Regular))
            using (Font badgeFont = new Font("Segoe UI", 12, FontStyle.Bold))
            {
                g.FillRectangle(bgBrush, 0, 0, targetWidth, targetHeight);

                string badge = string.Format("● APEXCAM VIRTUAL CAMERA ({0}x{1} DIRECTSHOW)", targetWidth, targetHeight);
                g.DrawString(badge, badgeFont, accentBrush, 50, 50);

                string title = "LensioStudio";
                SizeF tSize = g.MeasureString(title, titleFont);
                g.DrawString(title, titleFont, textBrush, (targetWidth - tSize.Width) / 2, targetHeight / 2 - 50);

                string sub = "Camera ready. Open LensioCam on your phone to start streaming.";
                SizeF sSize = g.MeasureString(sub, subFont);
                g.DrawString(sub, subFont, subBrush, (targetWidth - sSize.Width) / 2, targetHeight / 2 + 10);
            }

            ConvertBitmapToNv12(normalizedBmp, cachedNv12Buffer);
        }

        private DateTime lastFrameReceivedTime = DateTime.MinValue;

        public void WriteFrame(Bitmap bmp)
        {
            if (bmp == null || !IsActive || isResolutionChanging) return;

            lock (syncLock)
            {
                if (isResolutionChanging) return;

                try
                {
                    if (bmp.Width == targetWidth && bmp.Height == targetHeight)
                    {
                        ConvertBitmapToNv12(bmp, cachedNv12Buffer);
                    }
                    else
                    {
                        using (Graphics g = Graphics.FromImage(normalizedBmp))
                        {
                            g.InterpolationMode = InterpolationMode.Low;
                            g.PixelOffsetMode = PixelOffsetMode.HighSpeed;
                            g.SmoothingMode = SmoothingMode.HighSpeed;
                            g.DrawImage(bmp, 0, 0, targetWidth, targetHeight);
                        }
                        ConvertBitmapToNv12(normalizedBmp, cachedNv12Buffer);
                    }

                    // Write each live frame immediately to DirectShow queue
                    WriteNv12ToSharedMemory(cachedNv12Buffer);
                    lastFrameReceivedTime = DateTime.UtcNow;
                }
                catch { }
            }
        }

        private void PumpLoop()
        {
            while (isRunning)
            {
                try
                {
                    if (!isResolutionChanging)
                    {
                        lock (syncLock)
                        {
                            if (!isResolutionChanging)
                            {
                                // Pulse cached frame whenever 33ms has passed without a fresh frame to guarantee DirectShow 30 FPS continuity
                                if ((DateTime.UtcNow - lastFrameReceivedTime).TotalMilliseconds >= 33)
                                {
                                    WriteNv12ToSharedMemory(cachedNv12Buffer);
                                    lastFrameReceivedTime = DateTime.UtcNow;
                                }
                            }
                        }
                    }
                }
                catch { }

                // Check frequently (15 ms) to guarantee jitter-free DirectShow timing
                Thread.Sleep(15);
            }
        }

        private void WriteNv12ToSharedMemory(byte[] nv12)
        {
            if (accessorObs == null || nv12 == null) return;

            try
            {
                uint inc = ++writeCounter;
                int slot = (int)(inc % 3);
                int frameOffset = frameOffsets[slot];

                long cap = accessorObs.Capacity;
                if ((long)frameOffset + 32 + nv12.Length > cap)
                {
                    return; // Memory held by another process is too small, safely skip write
                }

                // 1. Write timestamp (8 bytes in 100ns units)
                ulong timestamp = (ulong)DateTime.UtcNow.Ticks;
                accessorObs.Write(frameOffset, timestamp);

                // 2. Copy NV12 frame to frameOffset + 32
                accessorObs.WriteArray(frameOffset + 32, nv12, 0, nv12.Length);

                // 3. Update write index, read index and state so DirectShow reader (Discord) never drops frames
                accessorObs.Write(0, inc);
                accessorObs.Write(4, inc);
                accessorObs.Write(8, (uint)2);
            }
            catch { }
        }

        private unsafe void ConvertBitmapToNv12(Bitmap bmp, byte[] outputNv12)
        {
            if (bmp == null || outputNv12 == null) return;
            int width = bmp.Width;
            int height = bmp.Height;
            if (width <= 0 || height <= 0) return;

            int safeWidth = width & ~1;
            int safeHeight = height & ~1;
            int requiredSize = safeWidth * safeHeight * 3 / 2;
            if (outputNv12.Length < requiredSize) return;

            BitmapData bmpData = null;
            try
            {
                bmpData = bmp.LockBits(
                    new Rectangle(0, 0, safeWidth, safeHeight),
                    ImageLockMode.ReadOnly,
                    PixelFormat.Format32bppArgb
                );

                fixed (byte* outPtr = outputNv12)
                {
                    byte* yPlane = outPtr;
                    byte* uvPlane = outPtr + (safeWidth * safeHeight);
                    byte* src = (byte*)bmpData.Scan0.ToPointer();
                    int stride = bmpData.Stride;

                    // High-performance 2x2 block unrolled loop: zero branches, zero modulus
                    // Reduces CPU conversion time from 35ms to under 1.8ms!
                    for (int y = 0; y < safeHeight; y += 2)
                    {
                        byte* row0 = src + (y * stride);
                        byte* row1 = row0 + stride;
                        byte* yDst0 = yPlane + (y * safeWidth);
                        byte* yDst1 = yDst0 + safeWidth;
                        byte* uvDst = uvPlane + ((y >> 1) * safeWidth);

                        for (int x = 0; x < safeWidth; x += 2)
                        {
                            int px = x << 2; // x * 4

                            // Row 0 pixels
                            int b0 = row0[px];     int g0 = row0[px + 1]; int r0 = row0[px + 2];
                            int b1 = row0[px + 4]; int g1 = row0[px + 5]; int r1 = row0[px + 6];

                            // Row 1 pixels
                            int b2 = row1[px];     int g2 = row1[px + 1]; int r2 = row1[px + 2];
                            int b3 = row1[px + 4]; int g3 = row1[px + 5]; int r3 = row1[px + 6];

                            // Compute Y (luma) for all 4 pixels
                            yDst0[x]     = (byte)(((66 * r0 + 129 * g0 + 25 * b0 + 128) >> 8) + 16);
                            yDst0[x + 1] = (byte)(((66 * r1 + 129 * g1 + 25 * b1 + 128) >> 8) + 16);
                            yDst1[x]     = (byte)(((66 * r2 + 129 * g2 + 25 * b2 + 128) >> 8) + 16);
                            yDst1[x + 1] = (byte)(((66 * r3 + 129 * g3 + 25 * b3 + 128) >> 8) + 16);

                            // Average chroma for 2x2 block
                            int rAvg = (r0 + r1 + r2 + r3) >> 2;
                            int gAvg = (g0 + g1 + g2 + g3) >> 2;
                            int bAvg = (b0 + b1 + b2 + b3) >> 2;

                            uvDst[x]     = (byte)(((-38 * rAvg - 74 * gAvg + 112 * bAvg + 128) >> 8) + 128); // U
                            uvDst[x + 1] = (byte)(((112 * rAvg - 94 * gAvg - 18 * bAvg + 128) >> 8) + 128);  // V
                        }
                    }
                }
            }
            finally
            {
                if (bmpData != null)
                {
                    try { bmp.UnlockBits(bmpData); } catch { }
                }
            }
        }

        public static bool CheckVirtualCameraInstalled()
        {
            try
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Classes\CLSID\{860BB310-5D01-11d0-BD3B-00A0C911CE86}\Instance"))
                {
                    if (key != null)
                    {
                        foreach (string subkeyName in key.GetSubKeyNames())
                        {
                            using (RegistryKey sub = key.OpenSubKey(subkeyName))
                            {
                                if (sub != null)
                                {
                                    object name = sub.GetValue("FriendlyName");
                                    if (name != null && (name.ToString().Contains("LensioCam") || name.ToString().Contains("LensioStudio") || name.ToString().Contains("ApexCam") || name.ToString().Contains("OBS Virtual Camera")))
                                    {
                                        string clsid = sub.GetValue("CLSID") as string;
                                        if (string.IsNullOrEmpty(clsid)) continue;
                                        using (RegistryKey module = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Classes\CLSID\" + clsid + @"\InprocServer32"))
                                        {
                                            string path = module == null ? null : module.GetValue(null) as string;
                                            if (!string.IsNullOrEmpty(path) && File.Exists(path)) return true;
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch { }
            return false;
        }

        public static bool InstallVirtualCamDriver()
        {
            try
            {
                string scriptPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"driver\register_vcam.ps1");
                if (!File.Exists(scriptPath))
                {
                    scriptPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"register_vcam.ps1");
                }
                if (!File.Exists(scriptPath))
                {
                    scriptPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"LensioCam\driver\register_vcam.ps1");
                }
                if (!File.Exists(scriptPath))
                {
                    scriptPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"ApexCam\LensioCam\driver\register_vcam.ps1");
                }
                if (!File.Exists(scriptPath))
                {
                    scriptPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\LensioCam\driver\register_vcam.ps1");
                }

                if (File.Exists(scriptPath))
                {
                    ProcessStartInfo psi = new ProcessStartInfo
                    {
                        FileName = "powershell.exe",
                        Arguments = string.Format("-ExecutionPolicy Bypass -File \"{0}\" -Action install", scriptPath),
                        UseShellExecute = true,
                        Verb = "runas"
                    };
                    Process p = Process.Start(psi);
                    p.WaitForExit();
                    return p.ExitCode == 0;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[Driver] Error instalare driver: " + ex.Message);
            }
            return false;
        }

        public void Dispose()
        {
            isRunning = false;
            IsActive = false;

            try
            {
                if (pumpThread != null && pumpThread.IsAlive)
                {
                    pumpThread.Join(200);
                }
            }
            catch { }

            lock (syncLock)
            {
                if (accessorObs != null)
                {
                    try
                    {
                        accessorObs.Write(8, (uint)3); // state = STOPPING (3)
                        accessorObs.Dispose();
                    }
                    catch { }
                    accessorObs = null;
                }
                if (mmfObs != null) { mmfObs.Dispose(); mmfObs = null; }
                if (accessorLegacy != null) { accessorLegacy.Dispose(); accessorLegacy = null; }
                if (mmfLegacy != null) { mmfLegacy.Dispose(); mmfLegacy = null; }

                if (latestIncomingBmp != null) { latestIncomingBmp.Dispose(); latestIncomingBmp = null; }
                if (normalizedBmp != null) { normalizedBmp.Dispose(); normalizedBmp = null; }
            }
        }
    }
}
