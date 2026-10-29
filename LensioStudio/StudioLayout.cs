using System;
using System.Drawing;
using System.Windows.Forms;

namespace ApexCam
{
    internal sealed class StudioMenuColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground { get { return Color.FromArgb(33,35,39); } }
        public override Color MenuItemSelected { get { return Color.FromArgb(54,57,63); } }
        public override Color MenuItemBorder { get { return Color.FromArgb(14,165,233); } }
        public override Color MenuBorder { get { return Color.FromArgb(61,64,70); } }
        public override Color SeparatorDark { get { return Color.FromArgb(61,64,70); } }
        public override Color SeparatorLight { get { return Color.FromArgb(61,64,70); } }
    }

    internal sealed class StudioMenuRenderer : ToolStripProfessionalRenderer
    {
        private static readonly Color Background = Color.FromArgb(33,35,39);
        private static readonly Color Muted = Color.FromArgb(157,164,175);
        public StudioMenuRenderer() : base(new StudioMenuColors()) { RoundedEdges=false; }
        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e) {
            using(var brush=new SolidBrush(Background)) e.Graphics.FillRectangle(brush,e.AffectedBounds);
        }
        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) {
            using(var brush=new SolidBrush(Background)) e.Graphics.FillRectangle(brush,e.AffectedBounds);
        }
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e) {
            e.TextColor=e.Item.Enabled ? Color.FromArgb(235,238,243) : Muted;
            base.OnRenderItemText(e);
        }
        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e) {
            e.ArrowColor=e.Item.Enabled ? Color.FromArgb(210,218,228) : Muted;
            base.OnRenderArrow(e);
        }
        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e) {
            var rect=new Rectangle(1,1,Math.Max(0,e.Item.Width-2),Math.Max(0,e.Item.Height-2));
            using(var brush=new SolidBrush(e.Item.Selected && e.Item.Enabled ? Color.FromArgb(53,61,73) : Background))
                e.Graphics.FillRectangle(brush,rect);
        }
        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e) {
            var r=e.ImageRectangle;
            using(var pen=new Pen(Color.FromArgb(56,189,248),2f)) {
                e.Graphics.DrawLines(pen,new [] { new Point(r.Left+2,r.Top+r.Height/2),
                    new Point(r.Left+r.Width/2-1,r.Bottom-3),new Point(r.Right-2,r.Top+3) });
            }
        }
    }

    internal sealed class StudioButton : Button
    {
        protected override void OnPaint(PaintEventArgs e) {
            if(Enabled) { base.OnPaint(e); return; }
            using(var brush=new SolidBrush(Color.FromArgb(44,47,53)))
                e.Graphics.FillRectangle(brush,ClientRectangle);
            using(var pen=new Pen(Color.FromArgb(66,70,78)))
                e.Graphics.DrawRectangle(pen,0,0,Math.Max(0,Width-1),Math.Max(0,Height-1));
            TextRenderer.DrawText(e.Graphics,Text,Font,ClientRectangle,Color.FromArgb(161,170,184),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        }
    }

    public partial class MainForm
    {
        private SplitContainer studioSplit;
        private StatusStrip studioStatus;
        private ToolStripStatusLabel streamStatus;
        private ContextMenuStrip sourceContextMenu;
        private Form sourcePropertiesWindow;
        private int previewRefreshPending;
        private bool arrangingDocks;
        private bool docksHidden = false;
        private int sourcePage;
        private int sourcePageSize = 1;
        private bool refreshingSources;
        private Button sourcePrevious, sourceNext;
        private readonly ToolTip sourceToolTips = new ToolTip { ShowAlways = true };

        private void InitializeStudioLayout()
        {
            SuspendLayout();
            AutoScaleMode = AutoScaleMode.Dpi;
            MinimumSize = new Size(640, 480);
            Rectangle screen = Screen.FromControl(this).WorkingArea;
            Size = new Size(Math.Min(1280, screen.Width), Math.Min(760, screen.Height));
            Font = new Font("Segoe UI", 9f);
            txtPhoneIp.Visible = btnConnectPhone.Visible = panelTelemetry.Visible = false;
            lblIpAddress.Visible = lblStatusPill.Visible = false;
            lblBrand.ForeColor = ColorTextPrimary;
            quickBarPanel.Visible = false;
            topBarPanel.Height = 64;
            studioStatus = new StatusStrip {
                Dock = DockStyle.None, AutoSize = false, Height = 26,
                BackColor = ColorHeaderBg, ForeColor = ColorTextSecondary,
                SizingGrip = false, Renderer = new StudioMenuRenderer()
            };
            streamStatus = new ToolStripStatusLabel { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
            var panels = new ToolStripDropDownButton("Panels");
            panels.DropDownItems.Add("Sources", null, (s,e) => lbSources.Focus());
            panels.DropDownItems.Add("Filters", null, (s,e) => cbPresets.Focus());
            panels.DropDownItems.Add("Camera / Audio", null, (s,e) => btnRes720p.Focus());

            var actions = new ToolStripDropDownButton("Image");
            actions.DropDownItems.Add("Mirror", null, (s,e) => { mirrorH = !mirrorH; });
            actions.DropDownItems.Add("Flip", null, (s,e) => { flipV = !flipV; });
            actions.DropDownItems.Add("Save snapshot", null, OnTakeSnapshot);
            actions.DropDownItems.Add("Fullscreen (F11)", null, (s,e) => ToggleFullscreen());
            studioStatus.Items.Add(panels);
            studioStatus.Items.Add(actions);
            studioStatus.Items.Add(streamStatus);
            topBarPanel.Controls.Add(studioStatus);

            studioSplit = new SplitContainer {
                Dock = DockStyle.Fill, Orientation = Orientation.Horizontal,
                BackColor = ColorBgObsidian, SplitterWidth = 4,
                Size = new Size(1000,600), SplitterDistance = 400,
                Panel1MinSize = 190, Panel2MinSize = 170, IsSplitterFixed = true
            };
            previewStage.Dock = DockStyle.Fill;
            previewStage.Padding = new Padding(4);
            dockControls.Dock = DockStyle.Right;
            dockControls.Width = 260;
            studioSplit.Panel1.Controls.Add(previewStage);
            studioSplit.Panel1.Controls.Add(dockControls);
            bottomDocksPanel.Dock = DockStyle.Fill;
            studioSplit.Panel2.Controls.Add(bottomDocksPanel);
            Controls.Add(studioSplit);
            studioSplit.BringToFront();

            dockProperties.Visible = false;
            propContentPanel.Dock = DockStyle.Fill;
            propContentPanel.AutoScroll = filterContent.AutoScroll = ctrlContent.AutoScroll = false;
            ApplyStudioControlStyle(this);
            ApplyMenuTheme(studioStatus);
            ApplyMenuTheme(menuAddSource);
            ApplyMenuTheme(trayMenu);
            InitializeSourceMenu();
            InitializeCameraPicker();
            InitializeDeviceComposition();
            Disposed += (s,e) => sourceToolTips.Dispose();
            sourcePrevious = CreateModernButton("<", ColorButtonDark, 0, 2, 22, 22);
            sourceNext = CreateModernButton(">", ColorButtonDark, 0, 2, 22, 22);
            dockSources.Controls.Add(sourcePrevious); dockSources.Controls.Add(sourceNext);
            sourcePrevious.Click += (s,e) => { selectedLayer = null; sourcePage--; RefreshSourcesList(); };
            sourceNext.Click += (s,e) => { selectedLayer = null; sourcePage++; RefreshSourcesList(); };
            Resize += (s,e) => { LayoutStudioHeader(); LayoutStudioDocks(); };
            studioSplit.SizeChanged += (s,e) => LayoutStudioDocks();
            RefreshStudioStatus();
            ResumeLayout(true);
            LayoutStudioHeader();
            LayoutStudioDocks();
        }

        private void BuildStudioDocks()
        {
            bottomDocksPanel = new Panel { BackColor = ColorBgObsidian, Padding = new Padding(0) };
            dockSources = CreateDockContainer("SOURCES", 0, 250);
            dockProperties = CreateDockContainer("PROPERTIES", 1, 400);
            dockFilters = CreateDockContainer("VIDEO FILTERS", 2, 600);
            dockControls = CreateDockContainer("CAMERA AND AUDIO", 3, 260);
            BuildDockSourcesContent();
            BuildDockPropertiesContent();
            BuildDockFiltersContent();
            BuildDockControlsContent();
            bottomDocksPanel.Controls.Add(dockSources);
            bottomDocksPanel.Controls.Add(dockFilters);
            bottomDocksPanel.Resize += (s,e) => LayoutStudioDocks();
        }

        private void InitializeSourceMenu()
        {
            sourceContextMenu = new ContextMenuStrip {
                BackColor = ColorHeaderBg, ForeColor = ColorTextPrimary,
                Renderer = new StudioMenuRenderer(), ShowImageMargin = false
            };
            sourceContextMenu.Items.Add("Properties...", null, (s,e) => ShowSourceProperties());
            sourceContextMenu.Items.Add("Fit to canvas", null, (s,e) => {
                if(selectedLayer==null)return;
                selectedLayer.SetBounds(new RectangleF(0,0,1920,1080)); previewPanel.Invalidate();
            });
            sourceContextMenu.Items.Add("Center in frame", null, (s,e) => {
                if (selectedLayer == null) return;
                selectedLayer.X = (1920-selectedLayer.Width)/2;
                selectedLayer.Y = (1080-selectedLayer.Height)/2;
                previewPanel.Invalidate();
            });
            sourceContextMenu.Items.Add("Show / hide", null, (s,e) => {
                if (selectedLayer == null) return;
                selectedLayer.Visible = !selectedLayer.Visible; RefreshSourcesList();
            });
            sourceContextMenu.Items.Add("Remove", null, (s,e) => {
                if (selectedLayer == null) return;
                scene.RemoveLayer(selectedLayer); selectedLayer = null; RefreshSourcesList();
            });
            ApplyMenuTheme(sourceContextMenu);
            lbSources.MouseDown += (s,e) => {
                if (e.Button != MouseButtons.Right) return;
                int index = lbSources.IndexFromPoint(e.Location);
                if (index < 0) return;
                lbSources.SelectedIndex = index;
                sourceContextMenu.Show(lbSources, e.Location);
            };
            lbSources.DoubleClick += (s,e) => ShowSourceProperties();
            lbSources.KeyDown += (s,e) => {
                if (e.KeyCode == Keys.Enter) { ShowSourceProperties(); e.Handled = true; }
                if (e.KeyCode == Keys.Apps && selectedLayer != null) sourceContextMenu.Show(lbSources, new Point(8,8));
            };
        }

        private void ShowSourceProperties()
        {
            if (selectedLayer == null) return;
            using (var dialog = new Form {
                Text = "Source properties", StartPosition = FormStartPosition.CenterParent,
                ClientSize = new Size(400,250), MinimumSize = new Size(320,280),
                FormBorderStyle = FormBorderStyle.SizableToolWindow,
                BackColor = ColorCardBg, ForeColor = ColorTextPrimary, ShowInTaskbar = false
            }) {
                sourcePropertiesWindow = dialog;
                var close = new Button { Text = "Done", Dock = DockStyle.Bottom, Height = 30, DialogResult = DialogResult.OK };
                dialog.Controls.Add(propContentPanel);
                dialog.Controls.Add(close);
                propContentPanel.Visible = true;
                UpdatePropertiesDock();
                ApplyStudioControlStyle(propContentPanel);
                LayoutPropertiesContent();
                dialog.Resize += (s,e) => LayoutPropertiesContent();
                dialog.AcceptButton = close;
                dialog.CancelButton = close;
                try { dialog.ShowDialog(this); }
                finally {
                    dialog.Controls.Remove(propContentPanel);
                    dockProperties.Controls.Add(propContentPanel);
                    propContentPanel.Visible = false;
                    sourcePropertiesWindow = null;
                }
            }
        }

        private void ApplyMenuTheme(ToolStrip strip)
        {
            if(strip==null) return;
            strip.Renderer=new StudioMenuRenderer();
            strip.BackColor=ColorHeaderBg;
            strip.ForeColor=ColorTextPrimary;
            foreach(ToolStripItem item in strip.Items) {
                item.ForeColor=ColorTextPrimary;
                var dropdown=item as ToolStripDropDownItem;
                if(dropdown!=null) ApplyMenuTheme(dropdown.DropDown);
            }
        }

        private void ApplyStudioControlStyle(Control parent)
        {
            foreach (Control control in parent.Controls) {
                var track = control as TrackBar;
                if (track != null) { track.AutoSize = false; track.Height = 24; track.TickStyle = TickStyle.None; }
                var button = control as Button;
                if (button != null) { button.AutoEllipsis = true; button.UseMnemonic = false; }
                ApplyStudioControlStyle(control);
            }
        }

        private void LayoutStudioHeader()
        {
            if (studioStatus == null) return;
            int w = topBarPanel.ClientSize.Width;
            topBarPanel.Height = 64;
            lblBrand.SetBounds(10,7,130,24);
            btnTopUpdate.SetBounds(Math.Max(150,w-222),5,104,26);
            btnQuickVcam.SetBounds(Math.Max(258,w-112),5,102,26);
            studioStatus.SetBounds(6,35,Math.Max(100,w-12),25);
            quickBarPanel.Visible = false;
        }

        private void LayoutStudioDocks()
        {
            if (studioSplit == null || arrangingDocks || WindowState == FormWindowState.Minimized) return;
            arrangingDocks = true;
            try {
                int w = bottomDocksPanel.ClientSize.Width;
                if (!isFullscreen && studioSplit.Height >= 364) {
                    int bottom = Math.Min(194, Math.Max(170,studioSplit.Height / 3));
                    studioSplit.SplitterDistance = Math.Max(190,studioSplit.Height-bottom-4);
                }
                dockControls.Width = ClientSize.Width < 850 ? 236 : 270;
                int sourceW = Math.Max(182,Math.Min(300,w/4));
                dockSources.SetBounds(4,0,sourceW-4,bottomDocksPanel.Height);
                dockFilters.SetBounds(sourceW+4,0,Math.Max(100,w-sourceW-8),bottomDocksPanel.Height);
                if (sourcePrevious != null) {
                    sourcePrevious.Left = dockSources.Width-52;
                    sourceNext.Left = dockSources.Width-28;
                    RefreshSourcesList();
                    foreach(Control bar in dockSources.Controls) {
                        if(bar is Panel && bar.Dock == DockStyle.Bottom) {
                            bar.Height = 60;
                            string[] labels = { "Add", "Remove", "Up", "Down", selectedLayer != null && !selectedLayer.Visible ? "Show" : "Hide" };
                            string[] hints = { "Add an image, text, clock or color source", "Remove the selected source",
                                "Move selected source in front of other sources", "Move selected source behind other sources",
                                "Show or hide the selected source" };
                            int i = 0, width = bar.ClientSize.Width;
                            foreach(Control button in bar.Controls) {
                                button.Text = labels[i];
                                sourceToolTips.SetToolTip(button,hints[i]);
                                if(i<2) button.SetBounds(i*(width/2),2,width/2-3,26);
                                else button.SetBounds((i-2)*(width/3),31,width/3-3,26);
                                button.Enabled = i == 0 || selectedLayer != null;
                                i++;
                            }
                        }
                    }
                }
                RefreshSourcesList();
                LayoutDockFilters();
                LayoutDockControls();
            } finally { arrangingDocks = false; }
        }

        private void PlaceSlider(Label title, TrackBar track, Label value, int x, int y, int w)
        {
            title.AutoSize = false; title.SetBounds(x,y,Math.Max(65,w-35),16);
            value.SetBounds(x+w-34,y,34,16);
            track.AutoSize = false; track.TickStyle = TickStyle.None;
            track.SetBounds(x,y+16,w,22);
        }

        private void LayoutDockFilters()
        {
            if (tbSaturation == null || btnResetF == null) return;
            int w = Math.Max(200,filterContent.ClientSize.Width-8);
            int half = (w-8)/2;
            lblPreset.Visible = false;
            cbPresets.SetBounds(4,0,half,24);
            btnAutoMode.SetBounds(12+half,0,half,24);
            PlaceSlider(lblExposureTitle,tbExposure,lblExposureVal,4,28,half);
            PlaceSlider(lblBrightnessTitle,tbBrightness,lblBrightnessVal,12+half,28,half);
            PlaceSlider(lblContrastTitle,tbContrast,lblContrastVal,4,68,half);
            PlaceSlider(lblSaturationTitle,tbSaturation,lblSaturationVal,12+half,68,half);
            btnResetF.SetBounds(4,108,w,24);
        }

        private void LayoutDockControls()
        {
            if (lblAudioStatus == null) return;
            int w = Math.Max(180,ctrlContent.ClientSize.Width-8);
            int half = (w-4)/2, third = (w-8)/3;
            btnRes1080p.SetBounds(4,0,half,23);
            btnRes720p.SetBounds(8+half,0,half,23);
            btnCamUltraWide.Visible = btnCamMain.Visible = btnCamSelfie.Visible = false;
            if(cameraPicker!=null) cameraPicker.SetBounds(4,26,w,23);
            PlaceSlider(lblZoom,tbZoom,lblZoomVal,4,52,w);
            btnTorch.SetBounds(4,91,half,23);
            btnUsb.Text = "USB (ADB)";
            btnUsb.SetBounds(8+half,91,half,23);
            btnTogglePcAudio.SetBounds(4,117,half,23);
            btnToggleMic.SetBounds(8+half,117,half,23);
            btnTogglePcAudio.Text = isPcAudioStreaming ? "PC Audio: ON" : "PC Audio: OFF";
            btnToggleMic.Text = isMicStreaming ? "Mic: ON" : "Mic: OFF";
            bool webMain=mainDeviceId!=null && mainDeviceId.StartsWith("web:");
            btnTogglePcAudio.Enabled=btnToggleMic.Enabled=!webMain;
            btnUsb.Enabled=controlledDeviceId==null || !controlledDeviceId.StartsWith("web:");
            if(webMain) {btnTogglePcAudio.Text="PC Audio: Android";btnToggleMic.Text="Mic: Android";}
            lblAudioStatus.AutoSize = false;
            lblAudioStatus.AutoEllipsis = true;
            lblAudioStatus.SetBounds(4,143,w,18);
            LayoutPhoneCards(w);
        }

        private void LayoutPropertiesContent()
        {
            if (propContentPanel == null) return;
            foreach (Control c in propContentPanel.Controls) {
                var label = c as Label;
                if (label != null) { label.AutoSize = false; label.AutoEllipsis = true; }
                c.Width = Math.Max(80,propContentPanel.ClientSize.Width-16);
            }
        }

        private void RefreshStudioStatus()
        {
            if (streamStatus == null) return;
            if(isConnected && mainDeviceId!=null && mainDeviceId.StartsWith("web:")) {
                streamStatus.Text=string.Format("{0} FPS  |  {1}x{2}  |  Web camera",phoneFps,currentResWidth,currentResHeight);return;
            }
            streamStatus.Text = isConnected
                ? string.Format("{0} FPS  |  {1}x{2}  |  Battery {3}%  |  {4:0.0} C",
                    phoneFps,currentResWidth,currentResHeight,batteryPercent,deviceTemp)
                : "Waiting for LensioCam | Wi-Fi / USB";
        }
    }
}
