using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace ApexCam
{
    // DoubleBuffered Panel that completely eliminates screen flickering
    public class DoubleBufferedPanel : Panel
    {
        public DoubleBufferedPanel()
        {
            this.DoubleBuffered = true;
            this.SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            this.UpdateStyles();
        }
        protected override void OnPaintBackground(PaintEventArgs e) { }
    }

    // Custom TrackBar that drops MouseWheel value changes
    public class NoWheelTrackBar : TrackBar
    {
        public NoWheelTrackBar() { AutoSize = false; Height = 26; TickStyle = TickStyle.None; }
        protected override void WndProc(ref Message m)
        {
            const int WM_MOUSEWHEEL = 0x020A;
            const int WM_MOUSEHWHEEL = 0x020E;
            if (m.Msg == WM_MOUSEWHEEL || m.Msg == WM_MOUSEHWHEEL)
            {
                return; // Drop wheel event completely!
            }
            base.WndProc(ref m);
        }
    }

    // Custom ComboBox that drops MouseWheel selection changes unless popup list is open
    public class NoWheelComboBox : ComboBox
    {
        protected override void WndProc(ref Message m)
        {
            const int WM_MOUSEWHEEL = 0x020A;
            const int WM_MOUSEHWHEEL = 0x020E;
            if ((m.Msg == WM_MOUSEWHEEL || m.Msg == WM_MOUSEHWHEEL) && !this.DroppedDown)
            {
                return; // Drop wheel event unless popup dropdown is actively open!
            }
            base.WndProc(ref m);
        }
    }

    // Interceptor global de mesaje Windows: elimina complet orice sansa ca rotita mouse-ului sa modifice setari!
    public class GlobalWheelFilter : IMessageFilter
    {
        private const int WM_MOUSEWHEEL = 0x020A;
        private const int WM_MOUSEHWHEEL = 0x020E;

        public bool PreFilterMessage(ref Message m)
        {
            if (m.Msg == WM_MOUSEWHEEL || m.Msg == WM_MOUSEHWHEEL)
            {
                Control c = Control.FromHandle(m.HWnd);
                if (c is TrackBar) return true; // Blocheaza scroll-ul pe orice TrackBar din aplicatie!
                ComboBox cb = c as ComboBox;
                if (cb != null && !cb.DroppedDown) return true; // Blocheaza scroll pe ComboBox daca nu e lista deschisa!
                if (c is NumericUpDown) return true;
            }
            return false;
        }
    }

    public partial class MainForm : Form
    {
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        private VideoReceiver receiver;
        private VirtualCameraBridge vcamBridge;
        private UpdateServer updateServer;
        private DiscoveryBeacon beacon;
        private MjpegReceiver mjpegReceiver;

        // Video state
        private Bitmap currentFrame = null;
        private CameraFrameScaler previewScaler;
        private Bitmap previewSource;
        private readonly object frameLock = new object();
        private bool isConnected = false;

        // Resolution state
        private int currentResWidth = 1280;
        private int currentResHeight = 720;
        private Button btnRes1080p;
        private Button btnRes720p;

        // Settings & Filters
        private FilterMode currentFilter = FilterMode.None;
        private float currentZoom = 1.0f;
        private bool mirrorH = false;
        private bool flipV = false;
        private bool isTorchOn = false;
        private bool isFrontCam = false;
        private bool isVirtualCamActive = true;

        // Image Adjustments
        private bool isAutoMode = false;
        private float exposureVal = 0.0f;
        private float brightnessVal = 0.0f;
        private float contrastVal = 0.0f;
        private float saturationVal = 0.0f;
        private Button btnAutoMode;
        private Label lblExposureTitle;
        private Label lblBrightnessTitle;
        private Label lblContrastTitle;
        private Label lblSaturationTitle;
        private NoWheelTrackBar tbExposure;
        private NoWheelTrackBar tbBrightness;
        private NoWheelTrackBar tbContrast;
        private NoWheelTrackBar tbSaturation;
        private Label lblExposureVal;
        private Label lblBrightnessVal;
        private Label lblContrastVal;
        private Label lblSaturationVal;
        private Panel filterContent;
        private NoWheelComboBox cbPresets;
        private Label lblPreset;
        private Button btnResetF;

        // OBS Scene & Interactive Layers System
        private ObsScene scene = new ObsScene();
        private ObsLayer selectedLayer = null;

        // Drag & Transform handle interaction state
        private enum TransformHandle
        {
            None,
            Body,
            TopLeft,
            TopCenter,
            TopRight,
            MidLeft,
            MidRight,
            BottomLeft,
            BottomCenter,
            BottomRight
        }
        private TransformHandle activeHandle = TransformHandle.None;
        private bool isDraggingLayer = false;
        private PointF dragStartMouse = PointF.Empty;
        private RectangleF dragStartBounds = RectangleF.Empty;

        // Telemetry state (Phone battery, charging, temp, fps, latency)
        private int batteryPercent = 100;
        private bool isPhoneCharging = false;
        private float deviceTemp = 32.0f;
        private int latencyMs = 8;
        private int phoneFps = 30;
        private DateTime lastTelemetryRender = DateTime.MinValue;

        // UI Panels & Layout (Classic OBS Studio Layout)
        private Panel topBarPanel;
        private Label lblBrand;
        private Label lblStatusPill;
        private DoubleBufferedPanel panelTelemetry;
        private Label lblIpAddress;
        private TextBox txtPhoneIp;
        private Button btnConnectPhone;
        private Button btnTopUpdate;
        private Button btnQuickVcam;

        private Panel previewStage;
        private VideoPreviewCanvas previewPanel;
        private Panel quickBarPanel;

        // OBS Lower Docks Bar (FITS ALL CONTROLS WITHOUT SCROLLING!)
        private Panel bottomDocksPanel;
        private Panel dockSources;
        private Panel dockProperties;
        private Panel dockFilters;
        private Panel dockControls;

        // Dock 1: Sources controls
        private ListBox lbSources;
        private ContextMenuStrip menuAddSource;

        // Dock 2: Properties dynamic controls
        private Panel propContentPanel;
        private Label lblPropTitle;

        // Audio & Microphone engines
        private WasapiCapture wasapiCapture;
        private UdpStreamer audioStreamer;
        private MicReceiver micReceiver;
        private bool isPcAudioStreaming = false;
        private bool isMicStreaming = false;
        private Button btnTogglePcAudio;
        private Button btnToggleMic;
        private Label lblAudioStatus;

        // Camera controls
        private Button btnCamUltraWide;
        private Button btnCamMain;
        private Button btnCamSelfie;
        private Button btnTorch;
        private NoWheelTrackBar tbZoom;
        private Label lblZoomVal;
        private Button btnUsb;
        private Label lblZoom;
        private Panel ctrlContent;

        // Fullscreen state
        private bool isFullscreen = false;
        private FormWindowState previousWindowState = FormWindowState.Normal;
        private FormBorderStyle previousBorderStyle = FormBorderStyle.Sizable;

        // System Tray & Timer
        private NotifyIcon trayIcon;
        private ContextMenuStrip trayMenu;

        private System.Windows.Forms.Timer heartbeatTimer;

        // Obsidian & Sapphire Studio Theme
        private static readonly Color ColorBgObsidian = Color.FromArgb(24, 25, 28);
        private static readonly Color ColorHeaderBg = Color.FromArgb(33, 35, 39);
        private static readonly Color ColorDockBg = Color.FromArgb(37, 39, 43);
        private static readonly Color ColorCardBg = Color.FromArgb(43, 45, 49);
        private static readonly Color ColorCardBorder = Color.FromArgb(61, 64, 70);
        private static readonly Color ColorButtonDark = Color.FromArgb(54, 57, 63);
        private static readonly Color ColorAccentCyan = Color.FromArgb(14, 165, 233);
        private static readonly Color ColorAccentLightCyan = Color.FromArgb(56, 189, 248);
        private static readonly Color ColorAccentEmerald = Color.FromArgb(16, 185, 129);
        private static readonly Color ColorAccentAmber = Color.FromArgb(245, 158, 11);
        private static readonly Color ColorAccentRed = Color.FromArgb(239, 68, 68);
        private static readonly Color ColorTextPrimary = Color.FromArgb(248, 250, 252);
        private static readonly Color ColorTextSecondary = Color.FromArgb(148, 163, 184);

        public MainForm() : this(null) { }
        public MainForm(string settingsDirectory)
        {
            workspaceDirectory=settingsDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"LensioStudio");
            try { File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app.log"), DateTime.Now + " - MainForm OBS Studio start\n"); } catch { }
            try
            {
                try { Application.AddMessageFilter(new GlobalWheelFilter()); } catch { }
                InitializeComponent();
                EnableDarkTitleBar();
                SetupSystemTray();
                InitDefaultSceneSources();
                InitializeWorkspace();
            }
            catch (Exception ex)
            {
                try { File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.log"), DateTime.Now + " - MainForm ctor EXCEPTION: " + ex.ToString() + "\n"); } catch { }
                throw;
            }
        }

        private void InitDefaultSceneSources()
        {
            // Flux video nativ 100% curat implicit (zero consum CPU suplimentar, randare fluida 30-60 FPS)
            // Utilizatorul poate adauga oricand imagini, text, ceas prin butoanele dedicate din Dock-ul Surse!
            RefreshSourcesList();
        }

        private void EnableDarkTitleBar()
        {
            try
            {
                int darkMode = 1;
                DwmSetWindowAttribute(this.Handle, 20, ref darkMode, sizeof(int));
                DwmSetWindowAttribute(this.Handle, 19, ref darkMode, sizeof(int));
            }
            catch { }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            this.WindowState = workspaceMaximized ? FormWindowState.Maximized : FormWindowState.Normal;
            this.ShowInTaskbar = true;
            this.Visible = true;
            this.BringToFront();
            this.Activate();

            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                InitializeEngine();
                CheckGitHubUpdate(false);
            });
        }

        private void SafeBeginInvoke(Action action)
        {
            if (this.IsHandleCreated && !this.IsDisposed)
            {
                try { this.BeginInvoke(action); } catch { }
            }
        }

        private void InitializeComponent()
        {
            this.Text = "LensioStudio — Mini OBS Virtual Camera & Audio Engine";
            this.Size = new Size(1400, 890);
            this.MinimumSize = new Size(1180, 720);
            this.BackColor = ColorBgObsidian;
            this.ForeColor = ColorTextPrimary;
            this.StartPosition = FormStartPosition.CenterScreen;
            this.KeyPreview = true;
            this.KeyDown += OnFormKeyDown;

            // 1. TOP STATUS BAR (Battery, IP, Connect, Quick VCam)
            BuildTopBar();

            // 2. BOTTOM OBS DOCKS (Sources, Properties, Filters, Controls - NO SCROLLBARS NEEDED!)
            BuildBottomDocks();

            // 3. MAIN VIDEO MONITOR VIEWPORT (Fill)
            BuildPreviewStage();

            // Assemble UI
            this.Controls.Add(previewStage);
            this.Controls.Add(bottomDocksPanel);
            this.Controls.Add(topBarPanel);
            previewStage.BringToFront();

            InitializeStudioLayout();
            // Heartbeat timer for periodic refresh and auto-reconnect ping

            heartbeatTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            heartbeatTimer.Tick += (s, e) =>
            {
                if (!isConnected && previewPanel != null) previewPanel.Invalidate();
                if (panelTelemetry != null) panelTelemetry.Invalidate();
                UpdateAudioStatusLabel();
                RefreshStudioStatus();
                if(isConnected && ++cameraPollTicks % 5 == 0) RefreshCameraCatalog(false);

            };
            heartbeatTimer.Start();
        }

        // ==========================================
        // 1. TOP HEADER BAR
        // ==========================================
        private void BuildTopBar()
        {
            topBarPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 56,
                BackColor = ColorHeaderBg,
                Padding = new Padding(14, 6, 14, 6)
            };
            topBarPanel.Paint += (s, e) =>
            {
                using (Pen borderPen = new Pen(ColorCardBorder, 1))
                {
                    e.Graphics.DrawLine(borderPen, 0, topBarPanel.Height - 1, topBarPanel.Width, topBarPanel.Height - 1);
                }
            };

            lblBrand = new Label
            {
                Text = "LensioStudio",
                ForeColor = ColorAccentLightCyan,
                Font = new Font("Segoe UI", 11.5f, FontStyle.Bold),
                AutoSize = true,
                Location = new Point(12, 16)
            };

            lblStatusPill = new Label
            {
                Text = " WAITING FOR CONNECTION",
                ForeColor = ColorAccentRed,
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                AutoSize = true,
                Location = new Point(320, 18)
            };

            // Anti-flicker DoubleBuffered Panel for telemetry & battery
            panelTelemetry = new DoubleBufferedPanel
            {
                Location = new Point(510, 8),
                Size = new Size(420, 40),
                BackColor = ColorCardBg
            };
            panelTelemetry.Paint += RenderTelemetryWidget;

            lblIpAddress = new Label
            {
                Text = "PC: Detecting...",
                ForeColor = ColorTextSecondary,
                Font = new Font("Segoe UI", 9f),
                AutoSize = true
            };

            txtPhoneIp = new TextBox
            {
                Text = "",
                BackColor = ColorButtonDark,
                ForeColor = ColorTextPrimary,
                BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                Width = 108
            };

            btnConnectPhone = CreateModernButton("Connect", ColorButtonDark, 0, 12, 96, 32);
            btnConnectPhone.Click += (s, e) =>
            {
                if (mjpegReceiver != null && mjpegReceiver.IsRunning)
                {
                    mjpegReceiver.Stop();
                    btnConnectPhone.Text = "Connect";
                    btnConnectPhone.BackColor = ColorAccentCyan;
                    lblStatusPill.Text = " DISCONNECTED (Waiting)";
                    lblStatusPill.ForeColor = ColorAccentRed;
                }
                else
                {
                    ConnectToPhone(txtPhoneIp.Text.Trim());
                }
            };

            btnTopUpdate = CreateModernButton("Update APK", ColorAccentCyan, 0, 12, 100, 32);
            btnTopUpdate.Click += (s, e) => TriggerPhoneUpdate();

            btnQuickVcam = CreateModernButton("VCam: ON", ColorAccentEmerald, 0, 12, 115, 32);
            btnQuickVcam.Click += OnToggleVcam;

            topBarPanel.Controls.Add(lblBrand);
            topBarPanel.Controls.Add(lblStatusPill);
            topBarPanel.Controls.Add(panelTelemetry);
            topBarPanel.Controls.Add(lblIpAddress);
            topBarPanel.Controls.Add(txtPhoneIp);
            topBarPanel.Controls.Add(btnConnectPhone);
            topBarPanel.Controls.Add(btnTopUpdate);
            topBarPanel.Controls.Add(btnQuickVcam);

            topBarPanel.Resize += (s, e) => LayoutTopBar();
            LayoutTopBar();
        }

        private void LayoutTopBar() { LayoutStudioHeader(); }
        // Clean Vector & Typography Telemetry (NO squares, NO unicode tofu, NO flickering!)
        private void RenderTelemetryWidget(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            Rectangle rect = new Rectangle(0, 0, panelTelemetry.Width - 1, panelTelemetry.Height - 1);
            using (GraphicsPath path = VideoFilters.CreateRoundedRectangle(rect, 6))
            using (SolidBrush bgBrush = new SolidBrush(ColorCardBg))
            using (Pen borderPen = new Pen(ColorCardBorder, 1f))
            {
                g.FillPath(bgBrush, path);
                g.DrawPath(borderPen, path);
            }

            if(mainDeviceId!=null && mainDeviceId.StartsWith("web:")) {
                TextRenderer.DrawText(g,"Web camera | "+phoneFps+" FPS | Battery / temperature unavailable",
                    Font,new Rectangle(12,6,Math.Max(0,panelTelemetry.Width-24),panelTelemetry.Height-12),
                    ColorTextPrimary,TextFormatFlags.VerticalCenter|TextFormatFlags.EndEllipsis|TextFormatFlags.SingleLine);
                return;
            }
            int curBattery = Math.Max(0, Math.Min(100, batteryPercent));
            Color batteryColor = isPhoneCharging ? Color.FromArgb(250, 204, 21) : // Bright Yellow when charging
                                 (curBattery >= 60) ? ColorAccentEmerald :
                                 (curBattery >= 25) ? ColorAccentAmber : ColorAccentRed;

            // Draw Clean Battery Chassis
            int batX = 12;
            int batY = 12;
            int batW = 34;
            int batH = 16;

            using (Pen batPen = new Pen(Color.FromArgb(148, 163, 184), 1.5f))
            {
                g.DrawRectangle(batPen, batX, batY, batW, batH);
            }
            using (SolidBrush batNipple = new SolidBrush(Color.FromArgb(148, 163, 184)))
            {
                g.FillRectangle(batNipple, batX + batW + 1, batY + 4, 3, batH - 8);
            }

            // Battery fill level
            int fillW = (int)((batW - 4) * (curBattery / 100.0f));
            if (fillW > 0)
            {
                using (SolidBrush fillBrush = new SolidBrush(batteryColor))
                {
                    g.FillRectangle(fillBrush, batX + 2, batY + 2, fillW, batH - 3);
                }
            }

            // Charging indicator or battery % text
            string batText = isPhoneCharging ? string.Format(" {0}% (Charging)", curBattery) : string.Format("{0}%", curBattery);
            using (Font boldFont = new Font("Segoe UI", 9.5f, FontStyle.Bold))
            using (SolidBrush textBrush = new SolidBrush(batteryColor))
            {
                g.DrawString(batText, boldFont, textBrush, batX + batW + 8, batY - 2);
            }

            // Divider 1
            int div1X = isPhoneCharging ? (batX + batW + 132) : (batX + batW + 58);
            if (div1X < panelTelemetry.Width - 60)
            {
                using (Pen divPen = new Pen(ColorCardBorder, 1))
                {
                    g.DrawLine(divPen, div1X, 8, div1X, panelTelemetry.Height - 8);
                }

                // Temperature (Clear text without emoji boxes!)
                string tempText = string.Format("TEMP {0:0.0}°C", deviceTemp);
                using (Font font = new Font("Segoe UI", 9f, FontStyle.Bold))
                using (SolidBrush tempBrush = new SolidBrush(deviceTemp > 42 ? ColorAccentAmber : ColorTextPrimary))
                {
                    g.DrawString(tempText, font, tempBrush, div1X + 10, 11);
                }
            }

            // Divider 2
            int div2X = div1X + 94;
            if (div2X < panelTelemetry.Width - 50)
            {
                using (Pen divPen = new Pen(ColorCardBorder, 1))
                {
                    g.DrawLine(divPen, div2X, 8, div2X, panelTelemetry.Height - 8);
                }

                // FPS & Latency (Clean text without emoji boxes!)
                int displayFps = phoneFps > 0 ? phoneFps : (receiver != null && receiver.CurrentFps > 0 ? receiver.CurrentFps : 30);
                string perfText = string.Format("{0} FPS  |  {1}ms", displayFps, latencyMs);
                using (Font font = new Font("Segoe UI", 9f, FontStyle.Bold))
                using (SolidBrush perfBrush = new SolidBrush(ColorAccentLightCyan))
                {
                    g.DrawString(perfText, font, perfBrush, div2X + 10, 11);
                }
            }
        }

        // ==========================================
        // 2. VIDEO PREVIEW MONITOR WITH COMPACT BALANCED SIZING
        // ==========================================
        private void BuildPreviewStage()
        {
            previewStage = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = ColorBgObsidian,
                Padding = new Padding(12, 10, 12, 0)
            };

            previewPanel = new VideoPreviewCanvas(this)
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Black,
                AllowDrop = true
            };

            // Drag and drop images directly onto webcam feed!
            previewPanel.DragEnter += (s, e) =>
            {
                if (e.Data.GetDataPresent(DataFormats.FileDrop))
                {
                    e.Effect = DragDropEffects.Copy;
                }
            };
            previewPanel.DragDrop += (s, e) =>
            {
                try
                {
                    string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
                    if (files != null && files.Length > 0)
                    {
                        Point p = previewPanel.PointToClient(new Point(e.X, e.Y));
                        float canvasX, canvasY;
                        ScreenToCanvasCoords(p.X, p.Y, out canvasX, out canvasY);

                        foreach (string file in files)
                        {
                            string ext = Path.GetExtension(file).ToLower();
                            if (ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".bmp" || ext == ".gif" || ext == ".webp")
                            {
                                AddImageLayerFromFile(file, canvasX, canvasY);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Unable to add file: " + ex.Message, "Drag and Drop Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            };

            // Mouse interaction for moving and resizing layers live
            previewPanel.MouseDown += OnPreviewMouseDown;
            previewPanel.MouseMove += OnPreviewMouseMove;
            previewPanel.MouseUp += OnPreviewMouseUp;

            // Floating quick toolbar directly underneath video monitor
            quickBarPanel = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 38,
                BackColor = ColorHeaderBg,
                Padding = new Padding(8, 3, 8, 3)
            };
            quickBarPanel.Paint += (s, e) =>
            {
                using (Pen borderPen = new Pen(ColorCardBorder, 1))
                {
                    e.Graphics.DrawLine(borderPen, 0, 0, quickBarPanel.Width, 0);
                }
            };

            Button btnMirror = CreateModernButton(" Mirror: OFF", ColorButtonDark, 8, 3, 130, 30);
            btnMirror.Click += (s, e) =>
            {
                mirrorH = !mirrorH;
                btnMirror.Text = mirrorH ? " Mirror: ON" : " Mirror: OFF";
                btnMirror.BackColor = mirrorH ? ColorAccentCyan : ColorButtonDark;
            };

            Button btnFlip = CreateModernButton(" Flip: OFF", ColorButtonDark, 146, 3, 140, 30);
            btnFlip.Click += (s, e) =>
            {
                flipV = !flipV;
                btnFlip.Text = flipV ? " Flip: ON" : " Flip: OFF";
                btnFlip.BackColor = flipV ? ColorAccentCyan : ColorButtonDark;
            };

            Button btnSnap = CreateModernButton(" Save HD Snapshot", ColorAccentEmerald, 294, 3, 160, 30);
            btnSnap.Click += OnTakeSnapshot;

            Button btnFs = CreateModernButton(" Fullscreen (F11)", ColorButtonDark, 462, 3, 170, 30);
            btnFs.Click += (s, e) => ToggleFullscreen();

            Label lblDragTip = new Label
            {
                Text = " Drag an image onto the preview, then move or resize it.",
                ForeColor = ColorTextSecondary,
                Font = new Font("Segoe UI", 8.5f, FontStyle.Italic),
                AutoSize = true,
                Location = new Point(645, 9)
            };

            quickBarPanel.Controls.Add(btnMirror);
            quickBarPanel.Controls.Add(btnFlip);
            quickBarPanel.Controls.Add(btnSnap);
            quickBarPanel.Controls.Add(btnFs);
            quickBarPanel.Controls.Add(lblDragTip);

            previewStage.Controls.Add(previewPanel);
            previewStage.Controls.Add(quickBarPanel);
            previewPanel.BringToFront();
        }

        // ==========================================
        // 3. BOTTOM OBS STUDIO DOCKS (SPACIOUS HEIGHT: 340px - NO SCROLLBARS!)
        // ==========================================
        private void BuildBottomDocks() { BuildStudioDocks(); }
        private Panel CreateDockContainer(string title, int index, int width)
        {
            int gap = 8;
            Panel dock = new Panel
            {
                Location = new Point(10 + (width + gap) * index, 6),
                Size = new Size(width, 324),
                BackColor = ColorCardBg,
                Padding = new Padding(8, 28, 8, 6)
            };
            dock.Paint += (s, e) =>
            {
                using (Pen borderPen = new Pen(ColorCardBorder, 1f))
                using (GraphicsPath path = VideoFilters.CreateRoundedRectangle(new Rectangle(0, 0, dock.Width - 1, dock.Height - 1), 6))
                {
                    e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    e.Graphics.DrawPath(borderPen, path);
                }
            };

            Label lbl = new Label
            {
                Text = title,
                ForeColor = ColorAccentLightCyan,
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                Location = new Point(8, 6),
                AutoSize = true
            };
            dock.Controls.Add(lbl);
            return dock;
        }

        // ------------------------------------------
        // DOCK 1: SURSE (SOURCES)
        // ------------------------------------------
        private void BuildDockSourcesContent()
        {
            lbSources = new ListBox
            {
                Dock = DockStyle.Fill,
                BackColor = ColorButtonDark,
                ForeColor = ColorTextPrimary,
                BorderStyle = BorderStyle.None,
                Font = new Font("Segoe UI", 9.5f),
                IntegralHeight = false,
                ItemHeight = 24
            };
            lbSources.SelectedIndexChanged += (s, e) =>
            {
                if (refreshingSources) return;
                var layers = scene.GetLayersSnapshot();
                int sourceIndex = sourcePage * sourcePageSize + lbSources.SelectedIndex;
                if (lbSources.SelectedIndex >= 0 && sourceIndex < layers.Count)
                {
                    selectedLayer = layers[sourceIndex];
                }
                else
                {
                    selectedLayer = null;
                }
                if(selectedLayer!=null && selectedLayer.Type==SourceType.Camera && controlledDeviceId!=selectedLayer.DeviceId) {
                    SelectControlledPhone(selectedLayer.DeviceId);
                    return;
                }
                UpdatePropertiesDock();
                LayoutStudioDocks();
                if (previewPanel != null) previewPanel.Invalidate();
            };

            menuAddSource = new ContextMenuStrip();
            menuAddSource.BackColor = ColorHeaderBg;
            menuAddSource.ForeColor = ColorTextPrimary;
            menuAddSource.Font = new Font("Segoe UI", 9.5f);

            var itemImage = new ToolStripMenuItem(" Image (Image / Logo)", null, (s, e) =>
            {
                using (OpenFileDialog ofd = new OpenFileDialog())
                {
                    ofd.Title = "Select an image or logo";
                    ofd.Filter = "Images (*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp)|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp|All files (*.*)|*.*";
                    if (ofd.ShowDialog() == DialogResult.OK)
                    {
                        AddImageLayerFromFile(ofd.FileName, 100, 100);
                    }
                }
            });
            var itemText = new ToolStripMenuItem(" Text (GDI+)", null, (s, e) =>
            {
                ObsLayer textLayer = new ObsLayer
                {
                    Name = "Text " + (scene.GetLayersSnapshot().Count + 1),
                    Type = SourceType.Text,
                    TextContent = "Custom Live Text",
                    X = 100,
                    Y = 200,
                    Width = 380,
                    Height = 60,
                    FontSize = 24f
                };
                scene.AddLayer(textLayer);
                selectedLayer = textLayer;
                RefreshSourcesList();
            });
            var itemClock = new ToolStripMenuItem(" Live Digital Clock", null, (s, e) =>
            {
                ObsLayer clockLayer = new ObsLayer
                {
                    Name = "Live Digital Clock",
                    Type = SourceType.Clock,
                    ClockFormat = "HH:mm:ss",
                    X = 100,
                    Y = 100,
                    Width = 220,
                    Height = 55,
                    FontSize = 24f
                };
                scene.AddLayer(clockLayer);
                selectedLayer = clockLayer;
                RefreshSourcesList();
            });
            var itemColor = new ToolStripMenuItem(" Color Source (Color Source)", null, (s, e) =>
            {
                ObsLayer colorLayer = new ObsLayer
                {
                    Name = "Color Source",
                    Type = SourceType.ColorBox,
                    SolidColor = Color.FromArgb(180, 14, 165, 233),
                    X = 100,
                    Y = 300,
                    Width = 300,
                    Height = 120
                };
                scene.AddLayer(colorLayer);
                selectedLayer = colorLayer;
                RefreshSourcesList();
            });

            menuAddSource.Items.Add(itemImage);
            menuAddSource.Items.Add(itemText);
            menuAddSource.Items.Add(itemClock);
            menuAddSource.Items.Add(itemColor);

            Panel toolStrip = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 32,
                BackColor = ColorCardBg,
                Padding = new Padding(0, 4, 0, 0)
            };

            Button btnAdd = CreateSmallIconButton("+", 0, toolStrip);
            btnAdd.Click += (s, e) => menuAddSource.Show(btnAdd, new Point(0, btnAdd.Height));

            Button btnRemove = CreateSmallIconButton("-", 36, toolStrip);
            btnRemove.Click += (s, e) =>
            {
                if (selectedLayer != null)
                {
                    scene.RemoveLayer(selectedLayer);
                    selectedLayer = null;
                    RefreshSourcesList();
                }
            };

            Button btnUp = CreateSmallIconButton("⬆", 72, toolStrip);
            btnUp.Click += (s, e) =>
            {
                if (selectedLayer != null)
                {
                    scene.MoveUp(selectedLayer);
                    RefreshSourcesList();
                }
            };

            Button btnDown = CreateSmallIconButton("⬇", 108, toolStrip);
            btnDown.Click += (s, e) =>
            {
                if (selectedLayer != null)
                {
                    scene.MoveDown(selectedLayer);
                    RefreshSourcesList();
                }
            };

            Button btnEye = CreateSmallIconButton("V", 144, toolStrip);
            btnEye.Click += (s, e) =>
            {
                if (selectedLayer != null)
                {
                    selectedLayer.Visible = !selectedLayer.Visible;
                    RefreshSourcesList();
                    if (previewPanel != null) previewPanel.Invalidate();
                }
            };

            btnAdd.AccessibleName = "Add source";
            btnRemove.AccessibleName = "Remove source";
            btnUp.AccessibleName = "Move source up";
            btnDown.AccessibleName = "Move source down";
            btnEye.AccessibleName = "Show or hide source";
            dockSources.Controls.Add(lbSources);
            dockSources.Controls.Add(toolStrip);
            lbSources.BringToFront();
        }

        private Button CreateSmallIconButton(string text, int x, Panel parent)
        {
            Button btn = new StudioButton
            {
                Text = text,
                Location = new Point(x, 2),
                Size = new Size(32, 28),
                FlatStyle = FlatStyle.Flat,
                BackColor = ColorButtonDark,
                ForeColor = ColorTextPrimary,
                Font = new Font("Segoe UI", 9f),
                Cursor = Cursors.Hand
            };
            btn.FlatAppearance.BorderSize = 1;
            btn.FlatAppearance.BorderColor = ColorCardBorder;
            parent.Controls.Add(btn);
            return btn;
        }

        private void RefreshSourcesList()
        {
            if (lbSources == null || refreshingSources) return;
            refreshingSources = true;
            try {
                var layers = scene.GetLayersSnapshot();
                sourcePageSize = Math.Max(1,lbSources.ClientSize.Height / Math.Max(1,lbSources.ItemHeight));
                int selectedIndex = layers.IndexOf(selectedLayer);
                if (selectedIndex >= 0) sourcePage = selectedIndex / sourcePageSize;
                sourcePage = Math.Max(0,Math.Min(sourcePage,Math.Max(0,(layers.Count-1)/sourcePageSize)));
                lbSources.BeginUpdate();
                lbSources.Items.Clear();
                int start = sourcePage * sourcePageSize;
                for(int i=start; i<Math.Min(layers.Count,start+sourcePageSize); i++) {
                    var layer = layers[i];
                    lbSources.Items.Add(string.Format("{0} {1}",layer.Visible ? "[ON]" : "[OFF]",layer.Name));
                }
                if(selectedIndex >= start && selectedIndex < start+lbSources.Items.Count)
                    lbSources.SelectedIndex = selectedIndex-start;
                lbSources.EndUpdate();
                if(sourcePrevious != null) {
                    sourcePrevious.Visible = sourceNext.Visible = layers.Count > sourcePageSize;
                    sourcePrevious.Enabled = sourcePage > 0;
                    sourceNext.Enabled = start+sourcePageSize < layers.Count;
                }
            } finally { refreshingSources = false; }
            UpdatePropertiesDock();
            foreach(Control bar in dockSources.Controls) {
                if(bar is Panel && bar.Dock == DockStyle.Bottom)
                    for(int i=0;i<bar.Controls.Count;i++) {
                        bar.Controls[i].Enabled = i == 0 || selectedLayer != null;
                        if(i==4) bar.Controls[i].Text = selectedLayer != null && !selectedLayer.Visible ? "Show" : "Hide";
                    }
            }
            if(previewPanel != null) previewPanel.Invalidate();
        }

        private void AddImageLayerFromFile(string file, float x, float y)
        {
            try
            {
                byte[] bytes = File.ReadAllBytes(file);
                Bitmap bmp;
                using (MemoryStream ms = new MemoryStream(bytes))
                {
                    using(var decoded=Image.FromStream(ms))bmp = new Bitmap(decoded);
                }

                float initW = 320;
                float initH = (float)bmp.Height / bmp.Width * initW;
                if (initH > 400)
                {
                    initH = 400;
                    initW = (float)bmp.Width / bmp.Height * initH;
                }

                ObsLayer layer = new ObsLayer
                {
                    Name = Path.GetFileName(file),
                    Type = SourceType.Image,
                    BitmapImage = bmp,
                    ImagePath = file,
                    X = Math.Max(0, Math.Min(1920 - initW, x)),
                    Y = Math.Max(0, Math.Min(1080 - initH, y)),
                    Width = initW,
                    Height = initH,
                    Visible = true
                };

                scene.AddLayer(layer);
                selectedLayer = layer;
                RefreshSourcesList();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Unable to open image: " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // ------------------------------------------
        // DOCK 2: PROPRIETĂȚI SURSĂ (PROPERTIES - NO SCROLLBAR!)
        // ------------------------------------------
        private void BuildDockPropertiesContent()
        {
            lblPropTitle = new Label
            {
                Text = "Select a source in the list or preview to edit its properties.",
                ForeColor = ColorTextSecondary,
                Font = new Font("Segoe UI", 9f, FontStyle.Italic),
                Location = new Point(8, 34),
                Size = new Size(dockProperties.Width - 16, 40)
            };

            propContentPanel = new Panel
            {
                Location = new Point(8, 32),
                Size = new Size(dockProperties.Width - 16, dockProperties.Height - 40),
                BackColor = ColorCardBg,
                AutoScroll = false // Clean non-scrolling fit!
            };

            dockProperties.Controls.Add(lblPropTitle);
            dockProperties.Controls.Add(propContentPanel);
            UpdatePropertiesDock();
        }

        private void UpdatePropertiesDock()
        {
            if (propContentPanel == null) return;
            while (propContentPanel.Controls.Count > 0) propContentPanel.Controls[0].Dispose();
            propContentPanel.Visible = selectedLayer != null;

            if (selectedLayer == null)
            {
                lblPropTitle.Visible = true;
                lblPropTitle.Text = "No source selected.\nClick an item in the preview or select it in Sources.";
                return;
            }

            lblPropTitle.Visible = false;
            int y = 2;
            int w = propContentPanel.Width - 16;

            Label lblName = new Label
            {
                Text = "Source: " + selectedLayer.Name,
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                ForeColor = ColorAccentLightCyan,
                Location = new Point(4, y),
                AutoSize = true
            };
            propContentPanel.Controls.Add(lblName);
            y += 22;

            Label lblOp = new Label
            {
                Text = "Opacity: " + (int)(selectedLayer.Opacity * 100) + "%",
                ForeColor = ColorTextSecondary,
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
                Location = new Point(4, y),
                AutoSize = true
            };
            propContentPanel.Controls.Add(lblOp);
            y += 16;

            NoWheelTrackBar tbOp = new NoWheelTrackBar
            {
                Location = new Point(4, y),
                Width = w,
                Minimum = 5,
                Maximum = 100,
                Value = (int)(selectedLayer.Opacity * 100),
                TickFrequency = 10,
                Height = 28
            };
            tbOp.ValueChanged += (s, e) =>
            {
                selectedLayer.Opacity = tbOp.Value / 100.0f;
                lblOp.Text = "Opacity: " + tbOp.Value + "%";
                if (previewPanel != null) previewPanel.Invalidate();
            };
            propContentPanel.Controls.Add(tbOp);
            y += 30;

            if (selectedLayer.Type == SourceType.Camera)
            {
                var fit=CreateModernButton("Fit camera to canvas",ColorButtonDark,4,y,w,28);
                fit.Click += (s,e) => { selectedLayer.SetBounds(new RectangleF(0,0,1920,1080)); previewPanel.Invalidate(); };
                propContentPanel.Controls.Add(fit);
                y+=32;
            }
            else if (selectedLayer.Type == SourceType.Image)
            {
                Button btnChangeImg = CreateModernButton(" Replace Image...", ColorButtonDark, 4, y, w, 28);
                btnChangeImg.Click += (s, e) =>
                {
                    using (OpenFileDialog ofd = new OpenFileDialog())
                    {
                        ofd.Title = "Choose a replacement image";
                        ofd.Filter = "Images (*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp)|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp";
                        if (ofd.ShowDialog() == DialogResult.OK)
                        {
                            byte[] bytes = File.ReadAllBytes(ofd.FileName);
                            using (MemoryStream ms = new MemoryStream(bytes))
                            {
                                using(var decoded=Image.FromStream(ms))selectedLayer.SetCameraFrame(new Bitmap(decoded));
                                selectedLayer.ImagePath = ofd.FileName;
                                selectedLayer.Name = Path.GetFileName(ofd.FileName);
                                RefreshSourcesList();
                            }
                        }
                    }
                };
                propContentPanel.Controls.Add(btnChangeImg);
                y += 32;

                Button btnCenter = CreateModernButton(" Center in Frame", ColorButtonDark, 4, y, w, 28);
                btnCenter.Click += (s, e) =>
                {
                    selectedLayer.X = (1920 - selectedLayer.Width) / 2;
                    selectedLayer.Y = (1080 - selectedLayer.Height) / 2;
                    if (previewPanel != null) previewPanel.Invalidate();
                };
                propContentPanel.Controls.Add(btnCenter);
                y += 32;
            }
            else if (selectedLayer.Type == SourceType.Text)
            {
                Label lblText = new Label
                {
                    Text = "Text Banner:",
                    ForeColor = ColorTextSecondary,
                    Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
                    Location = new Point(4, y),
                    AutoSize = true
                };
                propContentPanel.Controls.Add(lblText);
                y += 16;

                TextBox txt = new TextBox
                {
                    Text = selectedLayer.TextContent,
                    Location = new Point(4, y),
                    Width = w,
                    BackColor = ColorButtonDark,
                    ForeColor = ColorTextPrimary,
                    BorderStyle = BorderStyle.FixedSingle,
                    Font = new Font("Segoe UI", 9f)
                };
                txt.TextChanged += (s, e) =>
                {
                    selectedLayer.TextContent = txt.Text;
                    if (previewPanel != null) previewPanel.Invalidate();
                };
                propContentPanel.Controls.Add(txt);
                y += 28;

                Label lblFSize = new Label
                {
                    Text = "Font Size: " + (int)selectedLayer.FontSize,
                    ForeColor = ColorTextSecondary,
                    Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
                    Location = new Point(4, y),
                    AutoSize = true
                };
                propContentPanel.Controls.Add(lblFSize);
                y += 16;

                NoWheelTrackBar tbF = new NoWheelTrackBar
                {
                    Location = new Point(4, y),
                    Width = w,
                    Minimum = 12,
                    Maximum = 60,
                    Value = (int)selectedLayer.FontSize,
                    TickFrequency = 4,
                    Height = 28
                };
                tbF.ValueChanged += (s, e) =>
                {
                    selectedLayer.FontSize = tbF.Value;
                    lblFSize.Text = "Font Size: " + tbF.Value;
                    if (previewPanel != null) previewPanel.Invalidate();
                };
                propContentPanel.Controls.Add(tbF);
                y += 30;
            }
            else if (selectedLayer.Type == SourceType.Clock)
            {
                Label lblClockFmt = new Label
                {
                    Text = "Clock Format: HH:mm:ss",
                    ForeColor = ColorTextSecondary,
                    Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
                    Location = new Point(4, y),
                    AutoSize = true
                };
                propContentPanel.Controls.Add(lblClockFmt);
                y += 20;

                Label lblFSize = new Label
                {
                    Text = "Clock Font Size: " + (int)selectedLayer.FontSize,
                    ForeColor = ColorTextSecondary,
                    Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
                    Location = new Point(4, y),
                    AutoSize = true
                };
                propContentPanel.Controls.Add(lblFSize);
                y += 16;

                NoWheelTrackBar tbF = new NoWheelTrackBar
                {
                    Location = new Point(4, y),
                    Width = w,
                    Minimum = 14,
                    Maximum = 60,
                    Value = (int)selectedLayer.FontSize,
                    TickFrequency = 4,
                    Height = 28
                };
                tbF.ValueChanged += (s, e) =>
                {
                    selectedLayer.FontSize = tbF.Value;
                    lblFSize.Text = "Clock Font Size: " + tbF.Value;
                    if (previewPanel != null) previewPanel.Invalidate();
                };
                propContentPanel.Controls.Add(tbF);
                y += 30;
            }
            ApplyStudioControlStyle(propContentPanel);
            LayoutPropertiesContent();
        }

        // ------------------------------------------
        // DOCK 3: FILTRE & EFECTE VIDEO (PERFECT FIT - NO SCROLLBAR!)
        // ------------------------------------------
        private void BuildDockFiltersContent()
        {
            filterContent = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = ColorCardBg,
                AutoScroll = false, // Clean non-scrolling fit!
                Padding = new Padding(2)
            };

            int y = 2;
            int w = dockFilters.Width - 20;

            lblPreset = new Label
            {
                Text = "Video Preset:",
                ForeColor = ColorTextSecondary,
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
                Location = new Point(4, y),
                AutoSize = true
            };
            filterContent.Controls.Add(lblPreset);
            y += 18;

            cbPresets = new NoWheelComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Location = new Point(4, y),
                Width = w,
                BackColor = ColorButtonDark,
                ForeColor = ColorTextPrimary,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 8.5f)
            };
            cbPresets.Items.AddRange(new object[] {
                "Standard (Natural)", "Night Mode", "Beauty", "Dynamic HDR",
                "Black and White", "Classic Sepia", "Teal & Orange", "Cyberpunk Neon", "Studio Warm"
            });
            cbPresets.SelectedIndex = 0;
            cbPresets.SelectedIndexChanged += (s, e) =>
            {
                currentFilter = (FilterMode)cbPresets.SelectedIndex;
                if (previewPanel != null) previewPanel.Invalidate();
            };
            filterContent.Controls.Add(cbPresets);
            y += 26;

            btnAutoMode = CreateModernButton(" Auto Levels (Auto): OFF", ColorButtonDark, 4, y, w, 24);
            btnAutoMode.Click += (s, e) =>
            {
                isAutoMode = !isAutoMode;
                btnAutoMode.Text = isAutoMode ? " Auto Levels: ON" : " Auto Levels: OFF";
                btnAutoMode.BackColor = isAutoMode ? ColorAccentEmerald : ColorButtonDark;
            };
            filterContent.Controls.Add(btnAutoMode);
            y += 28;

            AddDockSlider(filterContent, "Exposure:", ref y, w, -50, 50, 0, out lblExposureTitle, out tbExposure, out lblExposureVal, val =>
            {
                exposureVal = val;
                lblExposureVal.Text = (val > 0 ? "+" : "") + val;
            });

            AddDockSlider(filterContent, "Brightness:", ref y, w, -50, 50, 0, out lblBrightnessTitle, out tbBrightness, out lblBrightnessVal, val =>
            {
                brightnessVal = val;
                lblBrightnessVal.Text = (val > 0 ? "+" : "") + val;
            });

            AddDockSlider(filterContent, "Contrast:", ref y, w, -50, 50, 0, out lblContrastTitle, out tbContrast, out lblContrastVal, val =>
            {
                contrastVal = val;
                lblContrastVal.Text = (val > 0 ? "+" : "") + val;
            });

            AddDockSlider(filterContent, "Saturation:", ref y, w, -100, 100, 0, out lblSaturationTitle, out tbSaturation, out lblSaturationVal, val =>
            {
                saturationVal = val;
                lblSaturationVal.Text = (val > 0 ? "+" : "") + val;
            });

            btnResetF = CreateModernButton("Reset filters", ColorButtonDark, 4, y, w, 24);
            btnResetF.Click += (s, e) =>
            {
                currentFilter = FilterMode.None;
                if (cbPresets != null) cbPresets.SelectedIndex = 0;
                isAutoMode = false;
                exposureVal = 0;
                brightnessVal = 0;
                contrastVal = 0;
                saturationVal = 0;
                if (btnAutoMode != null)
                {
                    btnAutoMode.Text = " Auto Levels: OFF";
                    btnAutoMode.BackColor = ColorButtonDark;
                }
                if (tbExposure != null) tbExposure.Value = 0;
                if (tbBrightness != null) tbBrightness.Value = 0;
                if (tbContrast != null) tbContrast.Value = 0;
                if (tbSaturation != null) tbSaturation.Value = 0;
            };
            filterContent.Controls.Add(btnResetF);

            dockFilters.Controls.Add(filterContent);
            filterContent.Resize += (s, e) => LayoutDockFilters();
            LayoutDockFilters();
        }

        private void AddDockSlider(Panel parent, string title, ref int y, int width, int min, int max, int defaultVal, out Label lblTitle, out NoWheelTrackBar tb, out Label lblVal, Action<int> onValChange)
        {
            lblTitle = new Label
            {
                Text = title,
                Location = new Point(4, y),
                AutoSize = true,
                ForeColor = ColorTextSecondary,
                Font = new Font("Segoe UI", 8f, FontStyle.Bold)
            };
            lblVal = new Label
            {
                Text = defaultVal.ToString(),
                Location = new Point(width - 50, y),
                Size = new Size(50, 14),
                TextAlign = ContentAlignment.TopRight,
                ForeColor = ColorAccentLightCyan,
                Font = new Font("Segoe UI", 8f, FontStyle.Bold)
            };
            parent.Controls.Add(lblTitle);
            parent.Controls.Add(lblVal);
            y += 15;

            tb = new NoWheelTrackBar
            {
                Location = new Point(4, y),
                Width = width,
                Minimum = min,
                Maximum = max,
                Value = defaultVal,
                TickFrequency = Math.Max(1, (max - min) / 10),
                Height = 26
            };
            tb.AutoSize = false;
            tb.ValueChanged += (s, e) =>
            {
                NoWheelTrackBar senderTb = (NoWheelTrackBar)s;
                onValChange(senderTb.Value);
            };
            parent.Controls.Add(tb);
            y += 28;
        }

        // ------------------------------------------
        // DOCK 4: COMENZI & AUDIO (PERFECT FIT - NO SCROLLBAR!)
        // ------------------------------------------
        private void BuildDockControlsContent()
        {
            ctrlContent = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = ColorCardBg,
                AutoScroll = false, // Clean non-scrolling fit!
                Padding = new Padding(2)
            };

            int y = 4;
            int w = dockControls.Width - 20;
            int halfW = (w - 6) / 2;

            // 1. Resolution 1080p vs 720p
            btnRes1080p = CreateModernButton("1080p FHD", ColorButtonDark, 4, y, halfW, 26);
            btnRes1080p.Click += (s, e) => SelectResolution(1920, 1080);

            btnRes720p = CreateModernButton("720p HD", ColorAccentCyan, 4 + halfW + 6, y, halfW, 26);
            btnRes720p.Click += (s, e) => SelectResolution(1280, 720);

            ctrlContent.Controls.Add(btnRes1080p);
            ctrlContent.Controls.Add(btnRes720p);
            y += 30;

            // 2. Camera Lenses: 0.5x, 1.0x, Selfie
            int thirdW = (w - 10) / 3;
            btnCamUltraWide = CreateModernButton("0.5x Wide", ColorButtonDark, 4, y, thirdW, 26);
            btnCamMain = CreateModernButton("1.0x Main", ColorAccentCyan, 4 + thirdW + 5, y, thirdW, 26);
            btnCamSelfie = CreateModernButton("Selfie", ColorButtonDark, 4 + (thirdW + 5) * 2, y, thirdW, 26);

            btnCamUltraWide.Click += (s, e) => SelectCameraLens("ultrawide");
            btnCamMain.Click += (s, e) => SelectCameraLens("main");
            btnCamSelfie.Click += (s, e) => SelectCameraLens("selfie");

            ctrlContent.Controls.Add(btnCamUltraWide);
            ctrlContent.Controls.Add(btnCamMain);
            ctrlContent.Controls.Add(btnCamSelfie);
            y += 30;

            // 3. Zoom Digital
            lblZoom = new Label
            {
                Text = "Digital Zoom:",
                ForeColor = ColorTextSecondary,
                Font = new Font("Segoe UI", 8f, FontStyle.Bold),
                Location = new Point(4, y),
                AutoSize = true
            };
            lblZoomVal = new Label
            {
                Text = "1.0x",
                Location = new Point(w - 50, y),
                Size = new Size(50, 14),
                TextAlign = ContentAlignment.TopRight,
                ForeColor = ColorAccentLightCyan,
                Font = new Font("Segoe UI", 8f, FontStyle.Bold)
            };
            ctrlContent.Controls.Add(lblZoom);
            ctrlContent.Controls.Add(lblZoomVal);
            y += 18;

            tbZoom = new NoWheelTrackBar
            {
                Location = new Point(4, y),
                Width = w,
                Minimum = 10,
                Maximum = 50,
                Value = 10,
                TickFrequency = 5,
                Height = 30
            };
            tbZoom.ValueChanged += (s, e) =>
            {
                if(updatingCameraZoom) return;
                currentZoom = tbZoom.Value / 10.0f;
                lblZoomVal.Text = currentZoom.ToString("0.0") + "x";
                if (receiver != null) SendPhoneControl("{\"zoom\":" + currentZoom.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "}");
                if (previewPanel != null) previewPanel.Invalidate();
            };
            ctrlContent.Controls.Add(tbZoom);
            y += 38; // Ample clearance guarantees TrackBar never covers buttons below!

            // 4. Torch & Update Phone buttons side-by-side
            btnTorch = CreateModernButton("Flashlight: OFF", ColorButtonDark, 4, y, halfW, 28);
            btnTorch.Click += (s, e) =>
            {
                if (isFrontCam)
                {
                    MessageBox.Show("The front camera does not have a flashlight.", "LensioStudio", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                isTorchOn = !isTorchOn;
                btnTorch.Text = isTorchOn ? "Flashlight: ON" : "Flashlight: OFF";
                btnTorch.BackColor = isTorchOn ? ColorAccentAmber : ColorButtonDark;
                if (receiver != null) SendPhoneControl("{\"toggleTorch\":true}");
            };
            ctrlContent.Controls.Add(btnTorch);

            y += 32;

            // 5. Audio & Microphone toggles
            btnTogglePcAudio = CreateModernButton("PC Audio to Phone: OFF", ColorButtonDark, 4, y, w, 28);
            btnTogglePcAudio.Click += (s, e) => TogglePcAudio();
            ctrlContent.Controls.Add(btnTogglePcAudio);
            y += 32;

            btnToggleMic = CreateModernButton("Phone Mic to PC: OFF", ColorButtonDark, 4, y, w, 28);
            btnToggleMic.Click += (s, e) => ToggleMic();
            ctrlContent.Controls.Add(btnToggleMic);
            y += 32;

            // 6. USB ADB mode & Audio status
            btnUsb = CreateModernButton(" Connect USB (ADB)", ColorButtonDark, 4, y, w, 26);
            btnUsb.Click += (s, e) =>
            {
                bool ok = AdbTunnel.SetupPortForwarding();
                if (ok) MessageBox.Show("USB (ADB) port forwarding enabled.", "LensioStudio USB", MessageBoxButtons.OK, MessageBoxIcon.Information);
                else MessageBox.Show("Unable to enable ADB. Connect your phone and enable USB debugging.", "LensioStudio USB", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            };
            ctrlContent.Controls.Add(btnUsb);
            y += 28;

            lblAudioStatus = new Label
            {
                Text = "PC Audio: Off | Mic: Off",
                ForeColor = ColorTextSecondary,
                Font = new Font("Segoe UI", 8.5f),
                Location = new Point(4, y),
                AutoSize = true
            };
            ctrlContent.Controls.Add(lblAudioStatus);

            dockControls.Controls.Add(ctrlContent);
            ctrlContent.Resize += (s, e) => LayoutDockControls();
            LayoutDockControls();
        }


        private void ScreenToCanvasCoords(int screenX, int screenY, out float canvasX, out float canvasY)
        {
            int panelW = previewPanel.Width;
            int panelH = previewPanel.Height;

            float scale = Math.Min((float)panelW / 1920f, (float)panelH / 1080f);
            int drawW = (int)(1920 * scale);
            int drawH = (int)(1080 * scale);
            int drawX = (panelW - drawW) / 2;
            int drawY = (panelH - drawH) / 2;

            canvasX = (screenX - drawX) / scale;
            canvasY = (screenY - drawY) / scale;
        }

        private RectangleF CanvasToScreenRect(RectangleF r)
        {
            int panelW = previewPanel.Width;
            int panelH = previewPanel.Height;

            float scale = Math.Min((float)panelW / 1920f, (float)panelH / 1080f);
            int drawW = (int)(1920 * scale);
            int drawH = (int)(1080 * scale);
            int drawX = (panelW - drawW) / 2;
            int drawY = (panelH - drawH) / 2;

            return new RectangleF(drawX + r.X * scale, drawY + r.Y * scale, r.Width * scale, r.Height * scale);
        }

        private TransformHandle HitTestHandles(Point p, RectangleF screenRect)
        {
            const float handleSize = 10f;
            RectangleF[] handles = new RectangleF[]
            {
                new RectangleF(screenRect.Left - handleSize/2, screenRect.Top - handleSize/2, handleSize, handleSize),
                new RectangleF(screenRect.Left + screenRect.Width/2 - handleSize/2, screenRect.Top - handleSize/2, handleSize, handleSize),
                new RectangleF(screenRect.Right - handleSize/2, screenRect.Top - handleSize/2, handleSize, handleSize),
                new RectangleF(screenRect.Left - handleSize/2, screenRect.Top + screenRect.Height/2 - handleSize/2, handleSize, handleSize),
                new RectangleF(screenRect.Right - handleSize/2, screenRect.Top + screenRect.Height/2 - handleSize/2, handleSize, handleSize),
                new RectangleF(screenRect.Left - handleSize/2, screenRect.Bottom - handleSize/2, handleSize, handleSize),
                new RectangleF(screenRect.Left + screenRect.Width/2 - handleSize/2, screenRect.Bottom - handleSize/2, handleSize, handleSize),
                new RectangleF(screenRect.Right - handleSize/2, screenRect.Bottom - handleSize/2, handleSize, handleSize)
            };

            if (handles[0].Contains(p)) return TransformHandle.TopLeft;
            if (handles[1].Contains(p)) return TransformHandle.TopCenter;
            if (handles[2].Contains(p)) return TransformHandle.TopRight;
            if (handles[3].Contains(p)) return TransformHandle.MidLeft;
            if (handles[4].Contains(p)) return TransformHandle.MidRight;
            if (handles[5].Contains(p)) return TransformHandle.BottomLeft;
            if (handles[6].Contains(p)) return TransformHandle.BottomCenter;
            if (handles[7].Contains(p)) return TransformHandle.BottomRight;

            if (screenRect.Contains(p)) return TransformHandle.Body;
            return TransformHandle.None;
        }

        private void OnPreviewMouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right) {
                float x, y; ScreenToCanvasCoords(e.X, e.Y, out x, out y);
                selectedLayer = scene.HitTest(x, y);
                RefreshSourcesList();
                if (selectedLayer != null) sourceContextMenu.Show(previewPanel, e.Location);
                return;
            }
            if (e.Button != MouseButtons.Left) return;

            float canvasX, canvasY;
            ScreenToCanvasCoords(e.X, e.Y, out canvasX, out canvasY);

            if (selectedLayer != null)
            {
                RectangleF sRect = CanvasToScreenRect(selectedLayer.GetBounds());
                TransformHandle handle = HitTestHandles(e.Location, sRect);
                if (handle != TransformHandle.None && handle != TransformHandle.Body)
                {
                    activeHandle = handle;
                    isDraggingLayer = true;
                    dragStartMouse = new PointF(canvasX, canvasY);
                    dragStartBounds = selectedLayer.GetBounds();
                    return;
                }
            }

            ObsLayer hit = scene.HitTest(canvasX, canvasY);
            if (hit != null)
            {
                selectedLayer = hit;
                activeHandle = TransformHandle.Body;
                isDraggingLayer = true;
                dragStartMouse = new PointF(canvasX, canvasY);
                dragStartBounds = selectedLayer.GetBounds();
                RefreshSourcesList();
            }
            else
            {
                selectedLayer = null;
                isDraggingLayer = false;
                RefreshSourcesList();
            }
        }

        private void OnPreviewMouseMove(object sender, MouseEventArgs e)
        {
            float canvasX, canvasY;
            ScreenToCanvasCoords(e.X, e.Y, out canvasX, out canvasY);

            if (isDraggingLayer && selectedLayer != null)
            {
                float dx = canvasX - dragStartMouse.X;
                float dy = canvasY - dragStartMouse.Y;

                RectangleF newB = dragStartBounds;

                switch (activeHandle)
                {
                    case TransformHandle.Body:
                        newB.X += dx;
                        newB.Y += dy;
                        break;
                    case TransformHandle.BottomRight:
                        newB.Width = Math.Max(20, dragStartBounds.Width + dx);
                        newB.Height = Math.Max(20, dragStartBounds.Height + dy);
                        break;
                    case TransformHandle.BottomLeft:
                        newB.X = dragStartBounds.X + dx;
                        newB.Width = Math.Max(20, dragStartBounds.Width - dx);
                        newB.Height = Math.Max(20, dragStartBounds.Height + dy);
                        break;
                    case TransformHandle.TopRight:
                        newB.Y = dragStartBounds.Y + dy;
                        newB.Width = Math.Max(20, dragStartBounds.Width + dx);
                        newB.Height = Math.Max(20, dragStartBounds.Height - dy);
                        break;
                    case TransformHandle.TopLeft:
                        newB.X = dragStartBounds.X + dx;
                        newB.Y = dragStartBounds.Y + dy;
                        newB.Width = Math.Max(20, dragStartBounds.Width - dx);
                        newB.Height = Math.Max(20, dragStartBounds.Height - dy);
                        break;
                    case TransformHandle.MidRight:
                        newB.Width = Math.Max(20, dragStartBounds.Width + dx);
                        break;
                    case TransformHandle.MidLeft:
                        newB.X = dragStartBounds.X + dx;
                        newB.Width = Math.Max(20, dragStartBounds.Width - dx);
                        break;
                    case TransformHandle.BottomCenter:
                        newB.Height = Math.Max(20, dragStartBounds.Height + dy);
                        break;
                    case TransformHandle.TopCenter:
                        newB.Y = dragStartBounds.Y + dy;
                        newB.Height = Math.Max(20, dragStartBounds.Height - dy);
                        break;
                }

                if(selectedLayer.Type==SourceType.Camera &&
                    (activeHandle==TransformHandle.TopLeft || activeHandle==TransformHandle.TopRight ||
                     activeHandle==TransformHandle.BottomLeft || activeHandle==TransformHandle.BottomRight)) {
                    newB.Height=newB.Width * dragStartBounds.Height / Math.Max(1,dragStartBounds.Width);
                    if(activeHandle==TransformHandle.TopLeft || activeHandle==TransformHandle.TopRight)
                        newB.Y=dragStartBounds.Bottom-newB.Height;
                }
                selectedLayer.SetBounds(newB);
                previewPanel.Invalidate();
                return;
            }

            if (selectedLayer != null)
            {
                RectangleF sRect = CanvasToScreenRect(selectedLayer.GetBounds());
                TransformHandle handle = HitTestHandles(e.Location, sRect);
                switch (handle)
                {
                    case TransformHandle.TopLeft:
                    case TransformHandle.BottomRight:
                        previewPanel.Cursor = Cursors.SizeNWSE;
                        return;
                    case TransformHandle.TopRight:
                    case TransformHandle.BottomLeft:
                        previewPanel.Cursor = Cursors.SizeNESW;
                        return;
                    case TransformHandle.MidLeft:
                    case TransformHandle.MidRight:
                        previewPanel.Cursor = Cursors.SizeWE;
                        return;
                    case TransformHandle.TopCenter:
                    case TransformHandle.BottomCenter:
                        previewPanel.Cursor = Cursors.SizeNS;
                        return;
                    case TransformHandle.Body:
                        previewPanel.Cursor = Cursors.SizeAll;
                        return;
                }
            }

            previewPanel.Cursor = Cursors.Default;
        }

        private void OnPreviewMouseUp(object sender, MouseEventArgs e)
        {
            isDraggingLayer = false;
            activeHandle = TransformHandle.None;
        }

        // ==========================================
        // ENGINE & VIDEO PIPELINE
        // ==========================================
        private void InitializeEngine()
        {
            try
            {
                vcamBridge = new VirtualCameraBridge(currentResWidth, currentResHeight);

                try
                {
                    updateServer = new UpdateServer(8557);
                    updateServer.OnPhonePing = (phoneIp) =>
                    {
                        SafeBeginInvoke(() =>
                        {
                            if (txtPhoneIp != null && txtPhoneIp.Text != phoneIp)
                            {
                                txtPhoneIp.Text = phoneIp;
                            }
                        });
                    };
                    updateServer.OnTriggerUpdateRequested = () =>
                    {
                        SafeBeginInvoke(() => TriggerPhoneUpdate());
                    };
                    updateServer.Start();
                }
                catch { }

                string myIp = GetLocalIp();
                SafeBeginInvoke(() => { lblIpAddress.Text = "PC: " + myIp + ":8556"; });

                try
                {
                    beacon = new DiscoveryBeacon(myIp);
                    beacon.OnPhoneFound += (phoneIp, port) =>
                    {
                        SafeBeginInvoke(() =>
                        {
                            if (txtPhoneIp.Text != phoneIp) txtPhoneIp.Text = phoneIp;
                            if (!isConnected && (receiver == null || !receiver.IsConnected) && (mjpegReceiver == null || !mjpegReceiver.IsRunning))
                            {
                                lblStatusPill.Text = "Phone detected · connecting automatically...";
                            }
                        });
                    };
                    beacon.Start();
                }
                catch { }

                mjpegReceiver = new MjpegReceiver();
                mjpegReceiver.OnConnectionStateChanged += (connected) =>
                {
                    SafeBeginInvoke(() =>
                    {
                        if (receiver != null && receiver.IsConnected) return;
                        isConnected = connected;
                        lblStatusPill.Text = connected ? " CONNECTED (Streaming)" : " DISCONNECTED (Waiting)";
                        lblStatusPill.ForeColor = connected ? ColorAccentEmerald : ColorAccentRed;
                        btnConnectPhone.Text = connected ? "Disconnect" : "Connect";
                        btnConnectPhone.BackColor = connected ? ColorAccentRed : ColorAccentCyan;
                    });
                };
                mjpegReceiver.OnFrameReceived += (rawBmp) =>
                {
                    if (receiver != null && receiver.IsConnected) return;
                    ProcessIncomingFrame(rawBmp, 95, false, 31.0f, mjpegReceiver.CurrentFps, 8);
                };

                receiver = new VideoReceiver(8556);
                receiver.OnDeviceConnected += info => SafeBeginInvoke(() => HandlePhoneConnected(info));
                receiver.OnDeviceConnectionStateChanged += (id,connected) => {
                    if(!connected) SafeBeginInvoke(() => HandlePhoneDisconnected(id));
                };
                receiver.OnConnectionStateChanged += (connected) =>
                {
                    SafeBeginInvoke(() =>
                    {
                        isConnected = connected;
                        if (beacon != null) beacon.IsConnected = connected;
                        if (!connected)
                        {
                            connectedPhoneHost = null;
                            cameraCatalogGeneration++;
                            cameraPicker.Text = "Choose phone camera...";
                            lblStatusPill.Text = " DISCONNECTED (Waiting)";
                            lblStatusPill.ForeColor = ColorAccentRed;
                        }
                        else
                        {
                            string resLabel = (currentResWidth == 1920) ? "1080p FHD" : "720p HD";
                            lblStatusPill.Text = " CONNECTED (" + resLabel + ")";
                            lblStatusPill.ForeColor = ColorAccentEmerald;
                        }

                        if (connected && mjpegReceiver != null && mjpegReceiver.IsRunning)
                        {
                            mjpegReceiver.Stop();
                        }
                    });
                };
                receiver.OnDeviceFrameReceived += ProcessDeviceFrame;

                receiver.Start();
                try { webCameraBridge=new WebCameraBridge(receiver.BoundPort);webCameraBridge.Start();
                    SafeBeginInvoke(()=> {try {System.Diagnostics.Process.Start(webCameraBridge.ReceiverUrl);}catch{}});
                }
                catch { if(webCameraBridge!=null)webCameraBridge.Dispose();webCameraBridge=null; }
                try { AdbTunnel.SetupPortForwarding(); } catch { }
            }
            catch (Exception ex)
            {
                try { File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.log"), DateTime.Now + " - InitializeEngine FATAL: " + ex.ToString() + "\n"); } catch { }
            }
        }

        private void ConnectToPhone(string phoneIp)
        {
            if (string.IsNullOrEmpty(phoneIp)) phoneIp = "";
            string url = string.Format("http://{0}:8080/video", phoneIp);
            lblStatusPill.Text = " CONNECTING TO " + phoneIp + "...";
            lblStatusPill.ForeColor = ColorAccentAmber;
            if (receiver != null && receiver.IsConnected) return;
            mjpegReceiver.Connect(url);
        }

        private void ProcessIncomingFrame(Bitmap rawBmp, int battery, bool isCharging, float temp, int fps, int rtt)
        {
            Bitmap processed = VideoFilters.ApplyFiltersAndTransform(
                rawBmp,
                currentFilter,
                receiver != null && receiver.IsConnected ? 1.0f : currentZoom,
                mirrorH,
                flipV,
                brightnessVal,
                contrastVal,
                saturationVal,
                exposureVal,
                isAutoMode,
                currentResWidth,
                currentResHeight,
                scene
            );

            if (processed == null) return;

            if (isVirtualCamActive && vcamBridge != null)
            {
                vcamBridge.WriteFrame(processed);
            }

            Bitmap oldFrame = null;
            lock (frameLock)
            {
                oldFrame = currentFrame;
                currentFrame = processed;
            }
            if (oldFrame != null)
            {
                try { oldFrame.Dispose(); } catch { }
            }

            bool telemetryChanged = (batteryPercent != battery) || (isPhoneCharging != isCharging) || (Math.Abs(deviceTemp - temp) >= 0.5f);
            batteryPercent = battery;
            isPhoneCharging = isCharging;
            deviceTemp = temp;
            phoneFps = fps;
            latencyMs = rtt > 0 ? rtt : 8;

            // Coalesce preview refreshes: slow UI rendering must not accumulate delegates.
            if (Interlocked.CompareExchange(ref previewRefreshPending, 1, 0) != 0) return;
            SafeBeginInvoke(() =>
            {
                Interlocked.Exchange(ref previewRefreshPending, 0);
                DateTime now = DateTime.UtcNow;
                if (telemetryChanged || (now - lastTelemetryRender).TotalMilliseconds >= 500)
                {
                    lastTelemetryRender = now;
                    if (panelTelemetry != null) panelTelemetry.Invalidate();
                }
                if (previewPanel != null) previewPanel.Invalidate();
            });
        }

        public void RenderPreview(Graphics g, int width, int height)
        {
            lock (frameLock)
            {
                if (currentFrame != null)
                {
                    try
                    {
                        float scale = Math.Min((float)width / currentFrame.Width, (float)height / currentFrame.Height);
                        int drawW = (int)(currentFrame.Width * scale);
                        int drawH = (int)(currentFrame.Height * scale);
                        int drawX = (width - drawW) / 2;
                        int drawY = (height - drawH) / 2;

                        if (drawX > 0)
                        {
                            g.FillRectangle(Brushes.Black, 0, 0, drawX, height);
                            g.FillRectangle(Brushes.Black, drawX + drawW, 0, width - (drawX + drawW), height);
                        }
                        if (drawY > 0)
                        {
                            g.FillRectangle(Brushes.Black, 0, 0, width, drawY);
                            g.FillRectangle(Brushes.Black, 0, drawY + drawH, width, height - (drawY + drawH));
                        }

                        g.InterpolationMode = InterpolationMode.Bilinear;
                        g.PixelOffsetMode = PixelOffsetMode.HighSpeed;
                        g.SmoothingMode = SmoothingMode.HighSpeed;
                        g.CompositingQuality = CompositingQuality.HighSpeed;

                        if(drawW>0 && drawH>0) {
                            if(previewScaler==null)previewScaler=new CameraFrameScaler();
                            if(!object.ReferenceEquals(previewSource,currentFrame) || previewScaler.Output==null ||
                                previewScaler.Output.Width!=drawW || previewScaler.Output.Height!=drawH) {
                                previewScaler.Scale(currentFrame,drawW,drawH,1f);
                                previewSource=currentFrame;
                            }
                            g.DrawImageUnscaled(previewScaler.Output,drawX,drawY);
                        }

                        DrawCameraGuides(g);

                        // Draw OBS Red Bounding Box & 8 Handles on PC monitor only!
                        if (selectedLayer != null && !isFullscreen && selectedLayer.Visible)
                        {
                            RectangleF sRect = CanvasToScreenRect(selectedLayer.GetBounds());
                            using (Pen redPen = new Pen(ColorAccentRed, 2f))
                            {
                                g.DrawRectangle(redPen, sRect.X, sRect.Y, sRect.Width, sRect.Height);
                            }

                            const float hSize = 8f;
                            PointF[] handlePts = new PointF[]
                            {
                                new PointF(sRect.Left, sRect.Top),
                                new PointF(sRect.Left + sRect.Width/2, sRect.Top),
                                new PointF(sRect.Right, sRect.Top),
                                new PointF(sRect.Left, sRect.Top + sRect.Height/2),
                                new PointF(sRect.Right, sRect.Top + sRect.Height/2),
                                new PointF(sRect.Left, sRect.Bottom),
                                new PointF(sRect.Left + sRect.Width/2, sRect.Bottom),
                                new PointF(sRect.Right, sRect.Bottom)
                            };

                            using (SolidBrush hBrush = new SolidBrush(Color.White))
                            using (Pen hPen = new Pen(ColorAccentRed, 1.5f))
                            {
                                foreach (var pt in handlePts)
                                {
                                    RectangleF rH = new RectangleF(pt.X - hSize/2, pt.Y - hSize/2, hSize, hSize);
                                    g.FillRectangle(hBrush, rH);
                                    g.DrawRectangle(hPen, rH.X, rH.Y, rH.Width, rH.Height);
                                }
                            }
                        }

                        return;
                    }
                    catch { return; }
                }
            }

            if (isConnected) return;

            g.Clear(ColorBgObsidian);
            using (SolidBrush textBrush = new SolidBrush(ColorTextSecondary))
            using (SolidBrush accentBrush = new SolidBrush(ColorAccentLightCyan))
            using (Font titleFont = new Font("Segoe UI", 15, FontStyle.Bold))
            using (Font subFont = new Font("Segoe UI", 10))
            {
                string title = "LensioStudio";
                string sub = "Open LensioCam on your phone.\nConnect over Wi-Fi or USB.";
                using (var format = new StringFormat { Alignment = StringAlignment.Center,
                    LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter })
                {
                    if (height >= 50)
                        g.DrawString(title, titleFont, accentBrush, new RectangleF(8, Math.Max(0, height / 2 - 45), Math.Max(1, width - 16), 32), format);
                    if (height >= 130)
                        g.DrawString(sub, subFont, textBrush, new RectangleF(12, height / 2 - 8, Math.Max(1, width - 24), 60), format);
                }
            }
        }

        // ==========================================
        // CONTROLS & CAMERA ACTIONS
        // ==========================================
        private void SelectResolution(int width, int height)
        {
            currentResWidth = width;
            currentResHeight = height;

            if (btnRes1080p != null)
            {
                bool is1080 = (width == 1920);
                btnRes1080p.BackColor = is1080 ? ColorAccentCyan : ColorButtonDark;
            }
            if (btnRes720p != null)
            {
                bool is720 = (width == 1280);
                btnRes720p.BackColor = is720 ? ColorAccentCyan : ColorButtonDark;
            }

            if (vcamBridge != null) vcamBridge.SetResolution(width, height);
            if (receiver != null)
            {
                string resCmd = width == 1920 ? "{\"resolution\":\"1080p\"}" : "{\"resolution\":\"720p\"}";
                SendPhoneControl(resCmd);
            }
        }

        private void SelectCameraLens(string lens)
        {
            if (btnCamUltraWide != null) btnCamUltraWide.BackColor = ColorButtonDark;
            if (btnCamMain != null) btnCamMain.BackColor = ColorButtonDark;
            if (btnCamSelfie != null) btnCamSelfie.BackColor = ColorButtonDark;

            if (lens == "ultrawide")
            {
                if (btnCamUltraWide != null) btnCamUltraWide.BackColor = ColorAccentCyan;
                isFrontCam = false;
            }
            else if (lens == "selfie")
            {
                if (btnCamSelfie != null) btnCamSelfie.BackColor = ColorAccentCyan;
                isFrontCam = true;
                if (isTorchOn)
                {
                    isTorchOn = false;
                    if (btnTorch != null)
                    {
                        btnTorch.Text = " Flashlight: OFF";
                        btnTorch.BackColor = ColorButtonDark;
                    }
                }
            }
            else
            {
                if (btnCamMain != null) btnCamMain.BackColor = ColorAccentCyan;
                isFrontCam = false;
            }

            if (receiver != null)
            {
                SendPhoneControl("{\"cameraLens\":\"" + lens + "\"}");
            }
        }

        private void OnToggleVcam(object sender, EventArgs e)
        {
            isVirtualCamActive = !isVirtualCamActive;
            if (btnQuickVcam != null)
            {
                btnQuickVcam.Text = isVirtualCamActive ? " VCam: ON" : " VCam: OFF";
                btnQuickVcam.BackColor = isVirtualCamActive ? ColorAccentEmerald : ColorButtonDark;
            }
        }

        private void OnTakeSnapshot(object sender, EventArgs e)
        {
            Bitmap snapshot = null;
            lock (frameLock)
            {
                if (currentFrame != null) snapshot = new Bitmap(currentFrame);
            }

            if (snapshot != null)
            {
                try
                {
                    string pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
                    string targetDir = Path.Combine(pictures, "LensioStudio");
                    if (!Directory.Exists(targetDir)) Directory.CreateDirectory(targetDir);

                    string fileName = string.Format("LensioStudio_{0:yyyyMMdd_HHmmss}.png", DateTime.Now);
                    string path = Path.Combine(targetDir, fileName);
                    snapshot.Save(path, ImageFormat.Png);
                    snapshot.Dispose();

                    MessageBox.Show("Snapshot saved to Pictures\\LensioStudio!\n\n" + path, "Snapshot Saved", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Unable to save snapshot: " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void ToggleFullscreen()
        {
            isFullscreen = !isFullscreen;
            if (isFullscreen)
            {
                previousWindowState = this.WindowState;
                previousBorderStyle = this.FormBorderStyle;

                topBarPanel.Visible = false;
                bottomDocksPanel.Visible = false;
                studioSplit.Panel2Collapsed = true;
                studioStatus.Visible = false;
                dockControls.Visible = false;
                quickBarPanel.Visible = false;
                previewStage.Padding = new Padding(0);

                this.FormBorderStyle = FormBorderStyle.None;
                this.WindowState = FormWindowState.Maximized;
            }
            else
            {
                this.FormBorderStyle = previousBorderStyle;
                this.WindowState = previousWindowState;

                topBarPanel.Visible = true;
                bottomDocksPanel.Visible = true;
                studioSplit.Panel2Collapsed = docksHidden;
                studioStatus.Visible = true;
                quickBarPanel.Visible = false;
                dockControls.Visible = true;
                LayoutStudioDocks();
                previewStage.Padding = new Padding(12, 10, 12, 0);
            }
        }

        private void OnFormKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.F11)
            {
                ToggleFullscreen();
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.Escape && isFullscreen)
            {
                ToggleFullscreen();
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.Delete && selectedLayer != null && !(ActiveControl is TextBoxBase))
            {
                scene.RemoveLayer(selectedLayer);
                selectedLayer = null;
                RefreshSourcesList();
                e.Handled = true;
            }
        }

        // ==========================================
        // OTA PHONE UPDATE (WI-FI & ADB)
        // ==========================================
        private void TriggerPhoneUpdate()
        {
            string myIp = GetLocalIp();
            try
            {
                if (receiver != null && receiver.IsConnected)
                {
                    SendPhoneControl("{\"triggerUpdate\":true,\"host\":\"" + myIp + "\"}");
                }
            }
            catch { }

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    string adbExe = "adb";
                    System.Diagnostics.ProcessStartInfo psi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = adbExe,
                        Arguments = "install -r \"" + Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "LensioCam.apk") + "\"",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    using (var p = System.Diagnostics.Process.Start(psi))
                    {
                        if (p != null) p.WaitForExit(15000);
                    }
                }
                catch { }
            });

            MessageBox.Show(
                "The LensioCam update request was sent to your phone.\n\n" +
                "1. If LensioCam is open on your phone, use the update card to install the update.\n\n" +
                "2. You can also download the APK in a browser:\n" +
                "http://" + myIp + ":8557/download/LensioCam.apk",
                "LensioCam Update",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information
            );
        }

        // ==========================================
        // AUDIO & MICROPHONE CONTROL
        // ==========================================
        private void TogglePcAudio()
        {
            if(!isPcAudioStreaming && mainDeviceId!=null && mainDeviceId.StartsWith("web:"))return;
            restorePcAudioPending=false;
            isPcAudioStreaming = !isPcAudioStreaming;
            if (isPcAudioStreaming)
            {
                try
                {
                    if (audioStreamer == null)
                    {
                        audioStreamer = new UdpStreamer(44100);
                        audioStreamer.Start();
                    }
                    if (wasapiCapture == null)
                    {
                        wasapiCapture = new WasapiCapture();
                        wasapiCapture.OnAudioDataAvailable += (pcm, len) =>
                        {
                            if (audioStreamer != null && isPcAudioStreaming)
                            {
                                audioStreamer.SendAudioFrame(pcm, len);
                            }
                        };
                        wasapiCapture.Start();
                    }
                    btnTogglePcAudio.Text = "PC Audio: ON";
                    btnTogglePcAudio.BackColor = ColorAccentEmerald;
                    if (receiver != null) SendPhoneControl(mainDeviceId,"{\"streamPcAudio\":true}");
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Unable to start PC audio capture: " + ex.Message, "LensioStudio Audio", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    isPcAudioStreaming = false;
                    btnTogglePcAudio.Text = "PC Audio: OFF";
                    btnTogglePcAudio.BackColor = ColorButtonDark;
                }
            }
            else
            {
                try
                {
                    if (wasapiCapture != null)
                    {
                        wasapiCapture.Stop();
                        wasapiCapture.Dispose();
                        wasapiCapture = null;
                    }
                    if (receiver != null) SendPhoneControl(mainDeviceId,"{\"streamPcAudio\":false}");
                }
                catch { }
                btnTogglePcAudio.Text = "PC Audio: OFF";
                btnTogglePcAudio.BackColor = ColorButtonDark;
            }
        }

        private void ToggleMic()
        {
            if(!isMicStreaming && mainDeviceId!=null && mainDeviceId.StartsWith("web:"))return;
            restoreMicPending=false;
            isMicStreaming = !isMicStreaming;
            if (isMicStreaming)
            {
                try
                {
                    if (micReceiver == null)
                    {
                        micReceiver = new MicReceiver(44101);
                        micReceiver.Start();
                    }
                    btnToggleMic.Text = "Microphone: ON";
                    btnToggleMic.BackColor = ColorAccentEmerald;
                    if (receiver != null) SendPhoneControl(mainDeviceId,"{\"enableMic\":true}");
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Unable to start microphone receiver: " + ex.Message, "LensioStudio Mic", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    isMicStreaming = false;
                    btnToggleMic.Text = "Microphone: OFF";
                    btnToggleMic.BackColor = ColorButtonDark;
                }
            }
            else
            {
                try
                {
                    if (micReceiver != null)
                    {
                        micReceiver.Stop();
                        micReceiver.Dispose();
                        micReceiver = null;
                    }
                    if (receiver != null) SendPhoneControl(mainDeviceId,"{\"enableMic\":false}");
                }
                catch { }
                btnToggleMic.Text = "Microphone: OFF";
                btnToggleMic.BackColor = ColorButtonDark;
            }
        }

        private void UpdateAudioStatusLabel()
        {
            if (lblAudioStatus == null) return;
            string pcAudioText = isPcAudioStreaming ? "PC Audio: ON" : "PC Audio: Off";
            string micText = isMicStreaming
                ? (micReceiver != null && micReceiver.IsDeviceActive ? "Mic: VB-Cable OK" : "Mic: Waiting")
                : "Mic: Off";
            lblAudioStatus.Text = pcAudioText + " | " + micText;
        }

        private Button CreateModernButton(string text, Color bg, int x, int y, int w, int h)
        {
            Button btn = new StudioButton
            {
                Text = text,
                BackColor = bg,
                ForeColor = ColorTextPrimary,
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand,
                Location = new Point(x, y),
                Size = new Size(w, h),
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleCenter
            };
            btn.FlatAppearance.BorderSize = 1;
            btn.FlatAppearance.BorderColor = ColorCardBorder;
            btn.FlatAppearance.MouseOverBackColor = ControlPaint.Light(bg, 0.20f);
            btn.FlatAppearance.MouseDownBackColor = ControlPaint.Dark(bg, 0.15f);
            return btn;
        }

        private string GetLocalIp()
        {
            try
            {
                var interfaces = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces();
                string bestLanIp = null;

                foreach (var ni in interfaces)
                {
                    if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;

                    string desc = (ni.Description ?? "").ToLower();
                    string name = (ni.Name ?? "").ToLower();
                    if (desc.Contains("radmin") || desc.Contains("virtual") || desc.Contains("vpn") ||
                        desc.Contains("vmware") || desc.Contains("tap") || desc.Contains("hyper-v") ||
                        name.Contains("radmin") || name.Contains("vpn"))
                    {
                        continue;
                    }

                    var ipProps = ni.GetIPProperties();
                    foreach (var ua in ipProps.UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily == AddressFamily.InterNetwork)
                        {
                            string ip = ua.Address.ToString();
                            if (ip.StartsWith("192.168.") || ip.StartsWith("10.") || ip.StartsWith("172."))
                            {
                                return ip;
                            }
                            if (bestLanIp == null && !ip.StartsWith("127."))
                            {
                                bestLanIp = ip;
                            }
                        }
                    }
                }

                if (!string.IsNullOrEmpty(bestLanIp)) return bestLanIp;

                using (Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0))
                {
                    socket.Connect("8.8.8.8", 65530);
                    IPEndPoint endPoint = socket.LocalEndPoint as IPEndPoint;
                    return endPoint.Address.ToString();
                }
            }
            catch { return "127.0.0.1"; }
        }

        private void SetupSystemTray()
        {
            trayMenu = new ContextMenuStrip();
            trayMenu.BackColor = ColorHeaderBg;
            trayMenu.ForeColor = ColorTextPrimary;
            trayMenu.Font = new Font("Segoe UI", 9.5f);
            trayMenu.ShowImageMargin = false;
            trayMenu.Renderer = new StudioMenuRenderer();
            trayMenu.Items.Add(new ToolStripMenuItem("LensioStudio") { Enabled = false });
            trayMenu.Items.Add(new ToolStripSeparator());

            var itemOpen = new ToolStripMenuItem(" Open LensioStudio", null, (s, e) => RestoreFromTray());
            itemOpen.Font = new Font("Segoe UI", 9.5f, FontStyle.Bold);
            trayMenu.Items.Add(itemOpen);

            trayMenu.Items.Add("Check for app updates", null, (s,e) => CheckGitHubUpdate(true));
            trayMenu.Items.Add("Check for phone update", null, (s,e) => TriggerPhoneUpdate());
            var cameraItem = new ToolStripMenuItem("Virtual camera", null, OnToggleVcam);
            trayMenu.Items.Add(cameraItem);
            trayMenu.Opening += (s,e) => { cameraItem.Checked = isVirtualCamActive; };
            trayMenu.Items.Add(new ToolStripSeparator());

            var itemExit = new ToolStripMenuItem(" Exit LensioStudio", null, (s, e) => FullExit());
            itemExit.ForeColor = ColorAccentRed;
            trayMenu.Items.Add(itemExit);

            trayIcon = new NotifyIcon
            {
                Text = "LensioStudio",
                ContextMenuStrip = trayMenu,
                Visible = true
            };

            try
            {
                Icon appIcon = CreateAppIcon();
                trayIcon.Icon = appIcon;
                this.Icon = appIcon;
            }
            catch { }

            trayIcon.DoubleClick += (s, e) => RestoreFromTray();
        }

        private void RestoreFromTray()
        {
            this.Show();
            this.WindowState = FormWindowState.Normal;
            this.ShowInTaskbar = true;
            this.Activate();
            this.BringToFront();
        }

        private void FullExit()
        {

            if (trayIcon != null)
            {
                trayIcon.Visible = false;
                trayIcon.Dispose();
                trayIcon = null;
            }
            this.Close();
            Application.Exit();
        }

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool DestroyIcon(IntPtr handle);

        private Icon CreateAppIcon()
        {
            try
            {
                using (Bitmap bmp = new Bitmap(32, 32))
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    using (SolidBrush bg = new SolidBrush(ColorAccentCyan))
                    {
                        g.FillEllipse(bg, 1, 1, 30, 30);
                    }
                    using (SolidBrush cam = new SolidBrush(Color.White))
                    {
                        g.FillRectangle(cam, 7, 12, 18, 12);
                        g.FillPolygon(cam, new Point[] { new Point(11, 12), new Point(13, 9), new Point(19, 9), new Point(21, 12) });
                    }
                    using (SolidBrush lens = new SolidBrush(ColorHeaderBg))
                    {
                        g.FillEllipse(lens, 11, 13, 10, 10);
                    }
                    using (SolidBrush lensCenter = new SolidBrush(ColorAccentLightCyan))
                    {
                        g.FillEllipse(lensCenter, 13, 15, 6, 6);
                    }

                    IntPtr hIcon = bmp.GetHicon();
                    Icon icon = (Icon)Icon.FromHandle(hIcon).Clone();
                    DestroyIcon(hIcon);
                    return icon;
                }
            }
            catch { return SystemIcons.Application; }
        }

        private static void Cleanup(Action action) { try { action(); } catch { } }
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if(workspaceSaveTimer!=null)workspaceSaveTimer.Stop();
            SaveWorkspace();
            StopDeviceComposition();
            Cleanup(() => { if (heartbeatTimer != null) { heartbeatTimer.Stop(); heartbeatTimer.Dispose(); } });
            // Closing the desktop releases local workers; phones keep their foreground camera session
            // and reconnect automatically when LensioStudio opens again.
            Cleanup(() => { if (trayIcon != null) { trayIcon.Visible = false; trayIcon.Dispose(); } });
            Cleanup(() => { if (wasapiCapture != null) wasapiCapture.Dispose(); });
            Cleanup(() => { if (audioStreamer != null) audioStreamer.Dispose(); });
            Cleanup(() => { if (micReceiver != null) micReceiver.Dispose(); });
            Cleanup(() => { if (updateServer != null) updateServer.Dispose(); });
            Cleanup(() => { if (mjpegReceiver != null) mjpegReceiver.Dispose(); });
            Cleanup(() => { if (beacon != null) beacon.Dispose(); });
            Cleanup(() => { if (webCameraBridge != null) webCameraBridge.Dispose(); });
            Cleanup(() => { if (receiver != null) receiver.Dispose(); });
            Cleanup(() => { if (vcamBridge != null) vcamBridge.Dispose(); });
            Cleanup(() => AdbTunnel.RemovePortForwarding());
            base.OnFormClosing(e);
        }
        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);
            Environment.Exit(0);
        }

        private class VideoPreviewCanvas : Panel
        {
            private readonly MainForm parent;

            public VideoPreviewCanvas(MainForm parent)
            {
                this.parent = parent;
                this.DoubleBuffered = true;
                this.SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
                this.SetStyle(ControlStyles.Selectable, false);
                this.UpdateStyles();
            }

            protected override void OnPaintBackground(PaintEventArgs e) { }

            protected override void OnPaint(PaintEventArgs e)
            {
                parent.RenderPreview(e.Graphics, this.Width, this.Height);
            }
        }
    }
}
