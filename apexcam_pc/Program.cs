using System;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

[assembly: System.Reflection.AssemblyVersion("2.8.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("2.8.0.0")]
[assembly: System.Reflection.AssemblyTitle("LensioStudio")]
[assembly: System.Reflection.AssemblyProduct("LensioStudio")]
[assembly: System.Reflection.AssemblyDescription("LensioStudio virtual camera and audio studio")]

namespace ApexCam
{
    static class Program
    {
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        private const int SW_RESTORE = 9;

        [STAThread]
        static void Main()
        {
            bool isFirstInstance;
            using (Mutex mutex = new Mutex(true, "Local\\ApexPhoneSharePC_App_Singleton_Mutex_v2", out isFirstInstance))
            {
                if (!isFirstInstance)
                {
                    try
                    {
                        Process current = Process.GetCurrentProcess();
                        foreach (Process p in Process.GetProcessesByName(current.ProcessName))
                        {
                            if (p.Id != current.Id && p.MainWindowHandle != IntPtr.Zero)
                            {
                                ShowWindow(p.MainWindowHandle, SW_RESTORE);
                                SetForegroundWindow(p.MainWindowHandle);
                                break;
                            }
                        }
                    }
                    catch { }
                    return;
                }

                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.ThreadException += (s, ev) =>
                {
                    try { System.IO.File.AppendAllText(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.log"), DateTime.Now + " ThreadException: " + ev.Exception.ToString() + "\n"); } catch { }
                };
                AppDomain.CurrentDomain.UnhandledException += (s, ev) =>
                {
                    try { System.IO.File.AppendAllText(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.log"), DateTime.Now + " UnhandledException: " + ev.ExceptionObject.ToString() + "\n"); } catch { }
                };

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                // Intercept and DROP mouse wheel messages for all TrackBars and closed ComboBoxes globally!
                Application.AddMessageFilter(new ScrollValueLockFilter());
                Application.Run(new MainForm());
            }
        }
    }

    // Global Message Filter: Prevents ANY slider, trackbar, or combobox from changing its value on mouse wheel scroll
    public class ScrollValueLockFilter : IMessageFilter
    {
        private const int WM_MOUSEWHEEL = 0x020A;
        private const int WM_MOUSEHWHEEL = 0x020E;

        [DllImport("user32.dll")]
        private static extern IntPtr WindowFromPoint(POINT point);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int x;
            public int y;
            public POINT(int x, int y) { this.x = x; this.y = y; }
        }

        public bool PreFilterMessage(ref Message m)
        {
            if (m.Msg == WM_MOUSEWHEEL || m.Msg == WM_MOUSEHWHEEL)
            {
                // 1. Check control from message target handle
                Control c = Control.FromHandle(m.HWnd);

                // 2. Also check control under the mouse cursor directly
                if (c == null)
                {
                    int x = (short)(m.LParam.ToInt32() & 0xFFFF);
                    int y = (short)((m.LParam.ToInt32() >> 16) & 0xFFFF);
                    IntPtr hUnderMouse = WindowFromPoint(new POINT(x, y));
                    if (hUnderMouse != IntPtr.Zero)
                    {
                        c = Control.FromHandle(hUnderMouse);
                    }
                }

                // 3. Fallback to Cursor.Position
                if (c == null)
                {
                    Point curPos = Cursor.Position;
                    IntPtr hCur = WindowFromPoint(new POINT(curPos.X, curPos.Y));
                    if (hCur != IntPtr.Zero)
                    {
                        c = Control.FromHandle(hCur);
                    }
                }

                while (c != null)
                {
                    // Block TrackBar completely on mouse wheel
                    if (c is TrackBar) return true;

                    // Block ComboBox on mouse wheel unless popup dropdown list is visibly open
                    ComboBox cb = c as ComboBox;
                    if (cb != null)
                    {
                        if (!cb.DroppedDown) return true;
                    }
                    if (c is NumericUpDown) return true;
                    c = c.Parent;
                }
            }
            return false;
        }
    }
}
