using System;
using System.Collections;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using ApexCam;
class LocalStudioForm:MainForm {
 public LocalStudioForm(string path):base(path){}
 protected override void OnShown(EventArgs e){}
 protected override void OnFormClosing(FormClosingEventArgs e){}
 protected override void OnFormClosed(FormClosedEventArgs e){}
}
class LocalCameraStudioSmoke {
 static object Get(MainForm f,string n){return typeof(MainForm).GetField(n,BindingFlags.Instance|BindingFlags.NonPublic).GetValue(f);}
 static void Set(MainForm f,string n,object v){typeof(MainForm).GetField(n,BindingFlags.Instance|BindingFlags.NonPublic).SetValue(f,v);}
 static object Call(MainForm f,string n,params object[] args){return typeof(MainForm).GetMethod(n,BindingFlags.Instance|BindingFlags.NonPublic).Invoke(f,args);}
 static void Check(bool ok,string message){if(!ok)throw new Exception(message);}
 static void Pump(int ms){var watch=System.Diagnostics.Stopwatch.StartNew();while(watch.ElapsedMilliseconds<ms){Application.DoEvents();Thread.Sleep(10);}}
 static int Fps(MainForm f){object meter=Get(f,"cameraFrameRate");return (int)meter.GetType().GetProperty("Value").GetValue(meter,null);}
 static void Cleanup(MainForm f){Call(f,"StopLocalCameras");Call(f,"StopDeviceComposition");((System.Windows.Forms.Timer)Get(f,"heartbeatTimer")).Stop();((NotifyIcon)Get(f,"trayIcon")).Dispose();f.Dispose();}
 static void Show(MainForm f){f.Location=new Point(-10000,-10000);f.ShowInTaskbar=false;f.Show();}
 [STAThread]static int Main(){
  Application.EnableVisualStyles();var devices=LocalCameraCapture.GetDevices();if(devices.Count==0){Console.WriteLine("SKIP no webcam attached");return 0;}
  string directory=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"test-data","local-studio-"+Guid.NewGuid().ToString("N"));
  var f=new LocalStudioForm(directory);Show(f);Call(f,"AddLocalCamera",devices[0]);Call(f,"AddLocalCamera",devices[0]);Pump(4000);
  Check(((IDictionary)Get(f,"localCameras")).Count==1,"Duplicate capture opened");Check(Fps(f)>0,"Webcam did not reach scene compositor");
  Check(!((Button)Get(f,"btnTorch")).Enabled&&!((TrackBar)Get(f,"tbZoom")).Enabled,"Phone controls active on webcam");
  var scene=(ObsScene)Get(f,"scene");var title=new ObsLayer{Type=SourceType.Text,TextContent="Local source overlay",Name="Selected title",X=10,Y=10,Width=400,Height=60};scene.AddLayer(title);Set(f,"selectedLayer",title);Call(f,"SaveWorkspace");Cleanup(f);
  f=new LocalStudioForm(directory);Show(f);Call(f,"RestoreLocalCameras");Pump(3000);
  Check(Fps(f)>0,"Saved webcam did not reconnect");Check(((ObsLayer)Get(f,"selectedLayer")).Name=="Selected title","Webcam restore stole selection");
  scene=(ObsScene)Get(f,"scene");var camera=scene.GetLayersSnapshot().Find(l=>l.Type==SourceType.Camera);Call(f,"RemoveSource",camera);Check(((IDictionary)Get(f,"localCameras")).Count==0&&camera.IsDisposed,"Removing webcam did not release capture");Cleanup(f);
  Console.WriteLine("PASS real webcam scene ingestion, duplicate prevention, phone-control isolation, saved reconnect, selected-overlay restore and capture release");return 0;
 }
}
