using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using System.Windows.Forms;
using ApexCam;

class LayoutOnlyForm : MainForm {
    public LayoutOnlyForm() : base(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"test-data",Guid.NewGuid().ToString())) {}
    protected override void OnShown(EventArgs e) { } // Never start camera/network engines.
    protected override void OnFormClosed(FormClosedEventArgs e) { }
    protected override void OnFormClosing(FormClosingEventArgs e) { }
}
class StudioLayoutSmoke {
    static object Field(MainForm f, string name) { return typeof(MainForm).GetField(name, BindingFlags.Instance|BindingFlags.NonPublic).GetValue(f); }
    static void Call(MainForm f, string name) { typeof(MainForm).GetMethod(name, BindingFlags.Instance|BindingFlags.NonPublic).Invoke(f, null); }
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    static void CaptureMenu(ToolStripDropDown menu,string name) {
        Check(menu.Renderer.GetType().Name=="StudioMenuRenderer","Menu renderer missing: "+name);
        menu.Show(new Point(-10000,-10000)); Application.DoEvents();
        using(var bmp=new Bitmap(menu.Width,menu.Height)) {
            menu.DrawToBitmap(bmp,new Rectangle(Point.Empty,bmp.Size));
            bmp.Save("build/menu-"+name+".png",ImageFormat.Png);
            var margin=bmp.GetPixel(8,bmp.Height/2);
            Check(margin.R<120 && margin.G<120 && margin.B<120,"Light menu margin: "+name);
        }
        menu.Close();
    }
    [STAThread] static int Main() {
        System.IO.Directory.CreateDirectory("build");
        Application.EnableVisualStyles();
        using (var form = new LayoutOnlyForm()) {
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(-10000, -10000);
            form.ShowInTaskbar = false;
            form.Show();
            Check(!((Control)Field(form,"txtPhoneIp")).Visible, "Manual IP must be hidden");
            Check(!((Control)Field(form,"btnConnectPhone")).Visible, "Manual connect must be hidden");
            Check(typeof(MainForm).GetField("cbBlur",BindingFlags.Instance|BindingFlags.NonPublic)==null,"Blur UI still exists");
            Check(typeof(MainForm).GetField("btnUpdatePhone",BindingFlags.Instance|BindingFlags.NonPublic)==null,"Duplicate updater still exists");
            Check(typeof(MainForm).GetField("addPhone",BindingFlags.Instance|BindingFlags.NonPublic)==null,"Add phone button remains");
            Check(Field(form,"pairingQrPanel")!=null,"Native pairing QR is missing");
            var tray=(ContextMenuStrip)Field(form,"trayMenu");
            Check(tray.Renderer is ToolStripProfessionalRenderer,"Tray renderer missing");
            bool open=false,exit=false,update=false;
            foreach(ToolStripItem item in tray.Items) {
                if(item.Text.Trim()=="Open LensioStudio")open=true;
                if(item.Text.Trim()=="Exit LensioStudio")exit=true;
                if(item.Text=="Check for app updates")update=true;
                Check(item.Text!="Check for phone update","Legacy phone updater remains in tray");
            }
            Check(open && exit && update,"English tray actions missing");
            Check(((Button)Field(form,"btnTopUpdate")).Text=="Check for updates","Header does not check desktop updates");
            CaptureMenu(tray,"tray");
            var status=(StatusStrip)Field(form,"studioStatus");
            foreach(ToolStripItem item in ((ToolStripDropDownItem)status.Items[0]).DropDownItems)Check(!item.Text.Contains("Add phone"),"Add phone menu remains");
            CaptureMenu(((ToolStripDropDownItem)status.Items[0]).DropDown,"panels");
            CaptureMenu(((ToolStripDropDownItem)status.Items[1]).DropDown,"image");
            var sources=(Panel)Field(form,"dockSources");
            using(var bmp=new Bitmap(sources.Width,sources.Height)) {
                sources.DrawToBitmap(bmp,new Rectangle(Point.Empty,bmp.Size));
                bmp.Save("build/disabled-source-buttons.png",ImageFormat.Png);
            }
            var cameraCatalog = new System.Collections.Generic.Dictionary<string,object> {
                {"activeId","52"}, {"stabilization","auto"}, {"error",""},
                {"cameras",new object[] {
                    new System.Collections.Generic.Dictionary<string,object> {{"id","0"},{"label","1x Back Main [ID 0]"},{"selectable",true},{"facing","Back"},{"maxDigitalZoom",8.0},{"flash",true}},
                    new System.Collections.Generic.Dictionary<string,object> {{"id","52"},{"label","Front Selfie [ID 52]"},{"selectable",true},{"facing","Front"},{"maxDigitalZoom",4.0},{"eis",true}},
                    new System.Collections.Generic.Dictionary<string,object> {{"id","hidden"},{"label","Depth [ID hidden]"},{"selectable",false},{"reason","Not independently openable"}}
                }}
            };
            typeof(MainForm).GetMethod("BuildCameraMenu",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(form,new object[]{cameraCatalog});
            var cameraMenu=(ContextMenuStrip)Field(form,"cameraMenu");
            CaptureMenu(cameraMenu,"cameras");
            Check(cameraMenu.Items[0].Enabled && cameraMenu.Items[1].Enabled && !cameraMenu.Items[2].Enabled,"Camera availability mapping failed");
            Check(((ToolStripMenuItem)cameraMenu.Items[1]).Checked,"Active camera must be reported by ID");
            Check((bool)Field(form,"isFrontCam"),"Camera facing must not be inferred from numeric IDs");
            Check(((TrackBar)Field(form,"tbZoom")).Maximum==40,"Hardware digital zoom limit ignored");
            Console.WriteLine("PASS camera catalog, arbitrary IDs, availability and zoom");
            var scene = (ObsScene)Field(form, "scene");
            var layer = new ObsLayer { Name="Live title", Type=SourceType.Text, TextContent="LensioStudio", FontSize=24f, Width=400, Height=70 };
            scene.AddLayer(layer);
            typeof(MainForm).GetField("selectedLayer", BindingFlags.Instance|BindingFlags.NonPublic).SetValue(form,layer);
            Call(form,"RefreshSourcesList");
            Check(object.ReferenceEquals(layer,Field(form,"selectedLayer")),"Source selection lost");
            foreach (Size size in new [] { new Size(1440,900),new Size(1280,720),new Size(1024,600),new Size(800,600),new Size(640,480),new Size(1920,1080) }) {
                form.Size=size;
                form.PerformLayout();
                Application.DoEvents();
                Call(form,"LayoutStudioDocks");
                var split=(SplitContainer)Field(form,"studioSplit");
                Check(split.Panel1.Height>=40,"Preview collapsed at "+size);
                Check(split.Panel2.Height>=70,"Docks collapsed at "+size);
                Check(split.Bounds.Bottom<=form.ClientSize.Height,"Workspace exceeds window at "+size);
                foreach (string dockName in new [] {"dockSources","dockFilters","dockControls"}) {
                    var dock=(Panel)Field(form,dockName);
                    var page=dock.Parent as TabPage;
                    if(page!=null) ((TabControl)page.Parent).SelectedTab=page;
                    Application.DoEvents();
                    Check(dock.Visible,"Dock hidden at "+size);
                }
                Check(!((Control)Field(form,"dockProperties")).Visible,"Persistent properties panel remains");
                foreach(string name in new [] {"filterContent","ctrlContent"}) {
                    var panel=(Panel)Field(form,name);
                    Check(!panel.AutoScroll && !panel.HorizontalScroll.Visible && !panel.VerticalScroll.Visible,"Scrollbar: "+name);
                    foreach(Control c in panel.Controls) {
                        if(c.Visible) Check(panel.ClientRectangle.Contains(c.Bounds),"Clipped control "+c.Text+" at "+size+" "+c.Bounds+" in "+panel.ClientRectangle);
                    }
                }
                Check(((Control)Field(form,"studioStatus")).Parent==((Control)Field(form,"topBarPanel")),"Status is not at the top");
                using(var bitmap=new Bitmap(form.Width,form.Height)) {
                    form.DrawToBitmap(bitmap,new Rectangle(Point.Empty,form.Size));
                    bitmap.Save("build/layout-"+size.Width+"x"+size.Height+".png",ImageFormat.Png);
                }
                Console.WriteLine("PASS layout "+size.Width+"x"+size.Height);
            }
            // A QR must fully repaint when phone rows make its canvas smaller.
            var qrImage=new Bitmap(212,212);
            using(var g=Graphics.FromImage(qrImage)){g.Clear(Color.White);g.FillRectangle(Brushes.Black,16,16,180,180);}
            typeof(MainForm).GetField("pairingQrImage",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(form,qrImage);
            var qrCanvas=(Panel)Field(form,"qrCanvas");
            Check((bool)typeof(Control).GetMethod("GetStyle",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(qrCanvas,new object[]{ControlStyles.ResizeRedraw}),"QR does not repaint on resize");
            var drawQr=typeof(MainForm).GetMethod("DrawPairingQr",BindingFlags.Instance|BindingFlags.NonPublic);
            using(var target=new Bitmap(320,320))using(var graphics=Graphics.FromImage(target)){
                drawQr.Invoke(form,new object[]{graphics,new Rectangle(0,0,320,320)});
                drawQr.Invoke(form,new object[]{graphics,new Rectangle(0,0,152,152)});
                Check(target.GetPixel(180,100).ToArgb()==qrCanvas.BackColor.ToArgb(),"Old QR pixels remain after shrinking");
                Check(target.GetPixel(23,23).ToArgb()==Color.White.ToArgb(),"QR quiet zone missing");
                Check(target.GetPixel(30,70).ToArgb()==Color.White.ToArgb() && target.GetPixel(31,70).ToArgb()==Color.Black.ToArgb(),"QR modules were scaled to fractional pixels");
            }
            var cards=(Panel)Field(form,"phoneCards");
            for(int phoneCount=0;phoneCount<=4;phoneCount++){
                if(phoneCount>0){var row=new Panel();row.Controls.Add(new Button());cards.Controls.Add(row);}
                typeof(MainForm).GetMethod("LayoutPhoneCards",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(form,new object[]{250});
                Application.DoEvents();
                var qrPanel=(Panel)Field(form,"pairingQrPanel");
                if(qrPanel.Parent==Field(form,"ctrlContent"))Check(qrCanvas.ClientSize.Height>=106,"Inline QR clipped with "+phoneCount+" phones");
            }
            Console.WriteLine("PASS QR full redraw, integer modules, quiet zone and layout for zero to four phones");
            form.Size = new Size(640,480);
            for(int i=0;i<30;i++) scene.AddLayer(new ObsLayer {Name="Layer "+i,Type=SourceType.Text,TextContent="Sample"});
            typeof(MainForm).GetField("selectedLayer",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(form,null);
            Call(form,"RefreshSourcesList");
            var list=(ListBox)Field(form,"lbSources");
            Check(list.Items.Count*list.ItemHeight<=list.ClientSize.Height,"Source list needs a scrollbar");
            var next=(Button)Field(form,"sourceNext");
            Check(next.Visible && next.Enabled,"Source pagination missing");
            next.PerformClick();
            Check((int)Field(form,"sourcePage")==1,"Next page failed");
            typeof(Control).GetMethod("OnMouseDown",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(list,new object[]{new MouseEventArgs(MouseButtons.Right,1,5,5,0)});
            var context=(ContextMenuStrip)Field(form,"sourceContextMenu");
            Check(context.Visible,"Source right click menu did not open");
            Check(object.ReferenceEquals(Field(form,"selectedLayer"),scene.GetLayersSnapshot()[(int)Field(form,"sourcePageSize")]),"Wrong source selected on second page");
            context.Close();
            Console.WriteLine("PASS source pagination and right click selection");
            var timer=new System.Windows.Forms.Timer {Interval=100};
            timer.Tick += (s,e) => {
                var dialog=(Form)Field(form,"sourcePropertiesWindow");
                if(dialog!=null) {
                    var panel=(Panel)Field(form,"propContentPanel");
                    foreach(Control c in panel.Controls) Check(panel.ClientRectangle.Contains(c.Bounds),"Clipped properties");
                    dialog.Close(); timer.Stop();
                }
            };
            timer.Start();
            Call(form,"ShowSourceProperties");
            timer.Dispose();
            Console.WriteLine("PASS source properties dialog");
            Call(form,"ToggleFullscreen"); Application.DoEvents();
            Check(((SplitContainer)Field(form,"studioSplit")).Panel2Collapsed,"Fullscreen did not collapse docks");
            Call(form,"ToggleFullscreen"); Application.DoEvents();
            ((System.Windows.Forms.Timer)Field(form,"heartbeatTimer")).Stop();
            ((NotifyIcon)Field(form,"trayIcon")).Dispose();
            form.Hide();
        }
        return 0;
    }
}
