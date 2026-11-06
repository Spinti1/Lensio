using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using ApexCam;
class PipelineForm:MainForm {
 public PipelineForm():base(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"test-data","pipeline-"+Guid.NewGuid().ToString("N"))){}
 protected override void OnShown(EventArgs e){}
 protected override void OnFormClosing(FormClosingEventArgs e){}
 protected override void OnFormClosed(FormClosedEventArgs e){}
}
class ScenePipelineSmoke {
 static object Get(MainForm f,string n){return typeof(MainForm).GetField(n,BindingFlags.Instance|BindingFlags.NonPublic).GetValue(f);}
 static object Call(MainForm f,string n,params object[] a){return typeof(MainForm).GetMethod(n,BindingFlags.Instance|BindingFlags.NonPublic).Invoke(f,a);}
 static void Check(bool b,string m){if(!b)throw new Exception(m);}
 static int Rate(MainForm f,string n){object rate=Get(f,n);return (int)rate.GetType().GetProperty("Value").GetValue(rate,null);}
 [STAThread]static int Main(string[] args){
  if(args.Length==1&&args[0]=="--managed"){
   typeof(MainForm).Assembly.GetType("ApexCam.CameraFrameScaler").GetField("nativeScale",BindingFlags.NonPublic|BindingFlags.Static).SetValue(null,null);
   typeof(VirtualCameraBridge).GetField("nativeNv12",BindingFlags.NonPublic|BindingFlags.Static).SetValue(null,null);
  }
  Application.EnableVisualStyles();var f=new PipelineForm();f.ShowInTaskbar=false;f.StartPosition=FormStartPosition.Manual;f.Location=new Point(-10000,-10000);f.Show();
  foreach(string n in new[]{"tbExposure","tbBrightness","tbContrast","tbSaturation"})Check(((TrackBar)Get(f,n)).Value==0,"Non-neutral filter after creating window: "+n);
  foreach(var size in new[]{new Size(640,540),new Size(1400,850)}){f.Size=size;Application.DoEvents();foreach(string n in new[]{"tbExposure","tbBrightness","tbContrast","tbSaturation"})Check(((TrackBar)Get(f,n)).Value==0,"Resize changed filter: "+n);}
  foreach(string n in new[]{"tbExposure","tbBrightness","tbContrast","tbSaturation"}){var slider=(TrackBar)Get(f,n);slider.Value=slider.Minimum;}
  ((Button)Get(f,"btnResetF")).PerformClick();
  string saved=Path.Combine((string)Get(f,"workspaceDirectory"),"workspace.json");
  var state=new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<SavedWorkspace>(File.ReadAllText(saved));
  Check(state.Saturation==0&&state.Exposure==0&&state.Brightness==0&&state.Contrast==0,"Filter reset was not saved immediately");
  Call(f,"SelectResolution",1920,1080);Call(f,"HandlePhoneConnected",new PhoneDeviceInfo{Id="test",Name="Synthetic camera",Host=""});
  var scene=(ObsScene)Get(f,"scene");
  for(int i=0;i<8;i++){var image=new Bitmap(2400,1800,PixelFormat.Format32bppArgb);using(var g=Graphics.FromImage(image)){g.Clear(Color.FromArgb(180,40+i*20,150,70));using(var pen=new Pen(Color.Cyan,15))g.DrawEllipse(pen,10,10,2100,1600);}scene.AddLayer(new ObsLayer{Type=SourceType.Image,Name="Overlay",BitmapImage=image,X=i<4?0:1500,Y=i%4*270,Width=420,Height=300,Opacity=1});}
  var source=new Bitmap(1280,720,PixelFormat.Format32bppArgb);using(var g=Graphics.FromImage(source)){g.Clear(Color.Red);g.FillRectangle(Brushes.Blue,640,0,640,720);}
  Call(f,"ProcessDeviceFrame","test",source,80,false,27f,30,0);
  var warmup=Stopwatch.StartNew();while(warmup.ElapsedMilliseconds<2000){Application.DoEvents();Thread.Sleep(5);}
  var lease=(Bitmap)Call(f,"AcquirePreviewFrame",0L);Check(lease!=null,"Missing preview surface");
  var pixel=lease.GetPixel(100,100);long revision=(long)Get(f,"currentFrameRevision");
  Thread.Sleep(500);
  Check((long)Get(f,"currentFrameRevision")-revision>=3,"Slow preview blocked scene publication");
  Check(lease.GetPixel(100,100).ToArgb()==pixel.ToArgb(),"Retained preview surface was overwritten");
  Call(f,"ReleasePreviewFrame",lease);
  Exception error=null;int submitted=0;double filterMs=0;var sender=new Thread(()=>{try{var clock=Stopwatch.StartNew();for(int i=0;i<450;i++){double next=i*1000.0/30;while(clock.Elapsed.TotalMilliseconds<next)Thread.Sleep(1);long at=Stopwatch.GetTimestamp();Call(f,"ProcessDeviceFrame","test",source,80,false,27f,30,0);filterMs+=(Stopwatch.GetTimestamp()-at)*1000.0/Stopwatch.Frequency;submitted++;}}catch(Exception e){error=e;}}){IsBackground=true};sender.Start();
  int sceneLow=100,cameraLow=100;var process=Process.GetCurrentProcess();var cpuStart=process.TotalProcessorTime;var watch=Stopwatch.StartNew();long sampled=0;
  while(sender.IsAlive){Application.DoEvents();if(watch.ElapsedMilliseconds>5000&&watch.ElapsedMilliseconds-sampled>1000){sampled=watch.ElapsedMilliseconds;int s=Rate(f,"sceneFrameRate"),c=Rate(f,"cameraFrameRate");sceneLow=Math.Min(sceneLow,s);cameraLow=Math.Min(cameraLow,c);Console.WriteLine("Scene="+s+" Camera="+c);}Thread.Sleep(2);}
  Console.WriteLine("Pipeline CPU="+((process.TotalProcessorTime-cpuStart).TotalMilliseconds/watch.Elapsed.TotalMilliseconds*100/Environment.ProcessorCount).ToString("F2")+"% across "+Environment.ProcessorCount+" logical CPUs; private="+(process.PrivateMemorySize64/1048576.0).ToString("F1")+"MiB");process.Dispose();
  Call(f,"StopDeviceComposition");((System.Windows.Forms.Timer)Get(f,"heartbeatTimer")).Stop();((NotifyIcon)Get(f,"trayIcon")).Dispose();f.Dispose();source.Dispose();if(error!=null)throw error;
  Console.WriteLine("Frames="+submitted+" mean filter/copy ms="+(filterMs/submitted).ToString("F2"));Check(sceneLow>=28,"Scene too slow: "+sceneLow);Check(cameraLow>=28,"Camera too slow: "+cameraLow);Console.WriteLine("PASS neutral startup/resize and 15s 1080p scene with eight large image overlays");return 0;
 }
}
