using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using ApexCam;
class SoakForm:MainForm {
 public SoakForm():base(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"test-data","soak-"+Guid.NewGuid().ToString("N"))){}
 protected override void OnShown(EventArgs e){}
 protected override void OnFormClosing(FormClosingEventArgs e){}
 protected override void OnFormClosed(FormClosedEventArgs e){}
}
class SceneSoakBenchmark {
 [DllImport("user32.dll")]static extern int GetGuiResources(IntPtr process,int flags);
 static object Get(MainForm f,string n){return typeof(MainForm).GetField(n,BindingFlags.Instance|BindingFlags.NonPublic).GetValue(f);}
 static void Call(MainForm f,string n,params object[] args){typeof(MainForm).GetMethod(n,BindingFlags.Instance|BindingFlags.NonPublic).Invoke(f,args);}
 static long Memory(){GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();using(var p=Process.GetCurrentProcess()){p.Refresh();return p.PrivateMemorySize64;}}
 [STAThread]static int Main(){
  Application.EnableVisualStyles();
  using(var f=new SoakForm())using(var input=new Bitmap(1280,720))using(var preview=new Bitmap(800,450))using(var pg=Graphics.FromImage(preview)){
   f.StartPosition=FormStartPosition.Manual;f.Location=new Point(-10000,-10000);f.ShowInTaskbar=false;f.Show();Call(f,"StopDeviceComposition");typeof(MainForm).GetField("compositionStopped",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(f,false);
   Call(f,"HandlePhoneConnected",new PhoneDeviceInfo{Id="soak-phone",Name="Synthetic camera",Host=""});
   var scene=(ObsScene)Get(f,"scene");
   for(int n=0;n<3;n++){var image=new Bitmap(4000,3000,PixelFormat.Format32bppArgb);using(var g=Graphics.FromImage(image)){g.Clear(Color.Transparent);using(var b=new SolidBrush(Color.FromArgb(170,240,80+n*30,50)))g.FillEllipse(b,0,0,4000,3000);}scene.AddLayer(new ObsLayer{Type=SourceType.Image,BitmapImage=image,X=100+n*560,Y=100,Width=500,Height=375,Opacity=.8f});}
   Call(f,"SaveWorkspace");
   for(int i=0;i<30;i++){using(var g=Graphics.FromImage(input))g.Clear(Color.SteelBlue);Call(f,"ProcessDeviceFrame","soak-phone",input,80,false,25f,30,8);Call(f,"ComposeDevices");f.RenderPreview(pg,800,450);Application.DoEvents();}
   long before=Memory();int handlesBefore=GetGuiResources(Process.GetCurrentProcess().Handle,0);
   var watch=Stopwatch.StartNew();double total=0,max=0;
   for(int i=0;i<1800;i++){
    using(var g=Graphics.FromImage(input))g.Clear(Color.FromArgb(40+i%180,80,120));
    var work=Stopwatch.StartNew();Call(f,"ProcessDeviceFrame","soak-phone",input,80,false,25f,30,8);Call(f,"ComposeDevices");f.RenderPreview(pg,800,450);Application.DoEvents();work.Stop();total+=work.Elapsed.TotalMilliseconds;max=Math.Max(max,work.Elapsed.TotalMilliseconds);
    int delay=(int)((i+1)*1000.0/30-watch.Elapsed.TotalMilliseconds);if(delay>0)Thread.Sleep(delay);
    if(i==899)Console.WriteLine("Soak: 900 changing camera frames with three 12MP image overlays completed.");
   }
   long after=Memory();int handlesAfter=GetGuiResources(Process.GetCurrentProcess().Handle,0);
   var meter=Get(f,"cameraFrameRate");int cameraFps=(int)meter.GetType().GetProperty("Value").GetValue(meter,null);
   Console.WriteLine("60-second full pipeline: measured_camera_fps="+cameraFps+" avg_work_ms="+(total/1800).ToString("F2")+" worst_work_ms="+max.ToString("F2")+" private_delta_mb="+((after-before)/1048576.0).ToString("F1")+" gdi_delta="+(handlesAfter-handlesBefore));
   if(cameraFps<27||after-before>64*1024*1024||handlesAfter-handlesBefore>25)throw new Exception("Sustained performance/resource bound failed");
   Call(f,"StopDeviceComposition");((System.Windows.Forms.Timer)Get(f,"heartbeatTimer")).Stop();((NotifyIcon)Get(f,"trayIcon")).Dispose();foreach(var l in scene.GetLayersSnapshot())l.Dispose();
  }
  return 0;
 }
}
