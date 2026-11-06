using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using ApexCam;
class AvcSoakForm:MainForm {
 public AvcSoakForm():base(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"test-data","avc-soak-"+Guid.NewGuid().ToString("N"))){}
 protected override void OnShown(EventArgs e){}
 protected override void OnFormClosing(FormClosingEventArgs e){}
 protected override void OnFormClosed(FormClosedEventArgs e){}
}
class AvcSceneSoak {
 static object Get(MainForm f,string n){return typeof(MainForm).GetField(n,BindingFlags.Instance|BindingFlags.NonPublic).GetValue(f);}
 static object Call(MainForm f,string n,params object[] a){return typeof(MainForm).GetMethod(n,BindingFlags.Instance|BindingFlags.NonPublic).Invoke(f,a);}
 static void Check(bool b,string m){if(!b)throw new Exception(m);}
 [STAThread]static int Main(string[] args){
  var units=new List<byte[]>();using(var reader=new BinaryReader(File.OpenRead(args[0])))while(reader.BaseStream.Position<reader.BaseStream.Length){int length=reader.ReadInt32();reader.ReadInt32();units.Add(reader.ReadBytes(length));}
  Application.EnableVisualStyles();var f=new AvcSoakForm();f.ShowInTaskbar=false;f.Location=new Point(-10000,-10000);f.Show();Call(f,"SelectResolution",1920,1080);
  Call(f,"HandlePhoneConnected",new PhoneDeviceInfo{Id="test",Name="Synthetic AVC",Host=""});var scene=(ObsScene)Get(f,"scene");
  for(int i=0;i<3;i++){var image=new Bitmap(1600,1200);using(var g=Graphics.FromImage(image))g.Clear(Color.FromArgb(70,150,40+i*20));scene.AddLayer(new ObsLayer{Type=SourceType.Image,Name="Overlay",BitmapImage=image,X=70+i*500,Y=30,Width=450,Height=338,Opacity=.7f});}
  var type=typeof(MainForm).Assembly.GetType("ApexCam.AvcVideoDecoder");Exception error=null;int decoded=0;
  var sender=new Thread(()=>{try{
   using(var decoder=(IDisposable)Activator.CreateInstance(type,BindingFlags.Public|BindingFlags.Instance,null,new object[]{1920,1080,true},null)){
    var watch=Stopwatch.StartNew();long before=Process.GetCurrentProcess().PrivateMemorySize64;Action<Bitmap> frame=b=>{Call(f,"ProcessDeviceFrame","test",b,80,false,27f,30,0);decoded++;};
    for(int i=0;i<600;i++){
     double next=i*1000.0/30;while(watch.Elapsed.TotalMilliseconds<next)Thread.Sleep(1);
     type.GetMethod("Decode").Invoke(decoder,new object[]{units[i%units.Count],i*333333L,0,frame});
    }
    Console.WriteLine("Decoded "+decoded+" frames in "+watch.ElapsedMilliseconds+"ms; private memory delta "+((Process.GetCurrentProcess().PrivateMemorySize64-before)/1024/1024)+" MB");
   }
  }catch(Exception e){error=e;}}){IsBackground=true};sender.Start();
  int low=100,high=0;var clock=Stopwatch.StartNew();while(sender.IsAlive){Application.DoEvents();if(clock.ElapsedMilliseconds>5000){var rate=Get(f,"cameraFrameRate");int fps=(int)rate.GetType().GetProperty("Value").GetValue(rate,null);low=Math.Min(low,fps);high=Math.Max(high,fps);}Thread.Sleep(4);}
  Call(f,"StopDeviceComposition");((System.Windows.Forms.Timer)Get(f,"heartbeatTimer")).Stop();((NotifyIcon)Get(f,"trayIcon")).Dispose();f.Dispose();
  if(error!=null)throw error;Check(decoded>=598,"Decoded frames dropped");Check(low>=28,"Scene camera FPS dipped: "+low);Console.WriteLine("PASS 20s 1080p H.264 plus three image overlays: actual camera composition "+low+"-"+high+" FPS");return 0;
 }
}
