using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using ApexCam;
class EditingForm:MainForm {
 public EditingForm(string path):base(path){}
 protected override void OnShown(EventArgs e){}
 protected override void OnFormClosing(FormClosingEventArgs e){}
 protected override void OnFormClosed(FormClosedEventArgs e){}
}
class SourceEditingSmoke {
 static void Check(bool ok,string message){if(!ok)throw new Exception(message);}
 static object Get(MainForm f,string n){return typeof(MainForm).GetField(n,BindingFlags.Instance|BindingFlags.NonPublic).GetValue(f);}
 static void Set(MainForm f,string n,object v){typeof(MainForm).GetField(n,BindingFlags.Instance|BindingFlags.NonPublic).SetValue(f,v);}
 static object Call(MainForm f,string n,params object[] a){return typeof(MainForm).GetMethod(n,BindingFlags.Instance|BindingFlags.NonPublic).Invoke(f,a);}
 static void Cleanup(MainForm f){Call(f,"StopDeviceComposition");((System.Windows.Forms.Timer)Get(f,"heartbeatTimer")).Stop();((NotifyIcon)Get(f,"trayIcon")).Dispose();f.Dispose();}
 [STAThread]static int Main(){
  float gx,gy;var targets=new[]{new RectangleF(300,200,400,200)};
  var snapped=MainForm.MagneticSnap(new RectangleF(704,203,100,80),targets,8,out gx,out gy);Check(snapped.X==700&&snapped.Y==200,"Source edges failed to align");
  snapped=MainForm.MagneticSnap(new RectangleF(1817,997,100,80),null,8,out gx,out gy);Check(snapped.Right==1920&&snapped.Bottom==1080,"Canvas edges failed to align");
  var free=new RectangleF(1120,720,120,120);Check(MainForm.MagneticSnap(free,targets,5,out gx,out gy)==free,"Distant source was magnetized");
  foreach(int width in new[]{1280,1920})using(var layer=new ObsLayer{Type=SourceType.ColorBox,X=300,Y=200,Width=400,Height=200,SolidColor=Color.Lime})using(var bitmap=new Bitmap(width,width*9/16)){
   var scene=new ObsScene();scene.AddLayer(layer);layer.CropLeft=.25f;layer.CropRight=.25f;
   using(var g=Graphics.FromImage(bitmap)){g.Clear(Color.Black);scene.RenderScene(g,bitmap.Width,bitmap.Height);}float s=width/1920f;
   Check(bitmap.GetPixel((int)(350*s),(int)(300*s)).G<20&&bitmap.GetPixel((int)(450*s),(int)(300*s)).G>200,"Crop pixels wrong");
   Check(scene.HitTest(350,300)==null&&scene.HitTest(450,300)==layer,"Cropped source hit test wrong");
   layer.CropLeft=layer.CropRight=0;layer.Rotation=90;
   using(var g=Graphics.FromImage(bitmap)){g.Clear(Color.Black);scene.RenderScene(g,bitmap.Width,bitmap.Height);}
   Check(bitmap.GetPixel((int)(500*s),(int)(140*s)).G>200&&bitmap.GetPixel((int)(350*s),(int)(300*s)).G<20,"Rotated source pixels wrong");
   Check(scene.HitTest(500,140)==layer&&scene.HitTest(350,300)==null,"Rotated hit test wrong");
   layer.Rotation=37;var point=layer.RotatePoint(new PointF(420,240),false);var back=layer.RotatePoint(point,true);Check(Math.Abs(back.X-420)<.001&&Math.Abs(back.Y-240)<.001,"Rotation inverse inaccurate");
   layer.CropLeft=.1f;layer.CropTop=.2f;layer.SetVisibleBounds(new RectangleF(400,300,360,160));Check(Math.Abs(layer.VisibleBounds.X-400)<.001&&Math.Abs(layer.VisibleBounds.Height-160)<.001,"Crop resize changed visible placement");
   layer.CropTo(new RectangleF(-200,-200,10000,10000));Check(layer.CropLeft==0&&layer.CropTop==0&&layer.CropRight==0&&layer.CropBottom==0,"Uncrop did not restore source");
  }
  foreach(var type in new[]{SourceType.Image,SourceType.Camera})using(var layer=new ObsLayer{Type=type,X=300,Y=200,Width=400,Height=200,Rotation=90,CropLeft=.25f,CropRight=.25f})using(var image=new Bitmap(80,40))using(var bitmap=new Bitmap(1280,720)){
   using(var g=Graphics.FromImage(image))g.Clear(Color.Lime);layer.SetCameraFrame(new Bitmap(image));var scene=new ObsScene();scene.AddLayer(layer);
   using(var g=Graphics.FromImage(bitmap)){g.Clear(Color.Black);scene.RenderScene(g,1280,720);}
   Check(bitmap.GetPixel(333,200).G>200&&bitmap.GetPixel(333,100).G<20,type+" cached pixels ignored rotation/crop");
  }
  Application.EnableVisualStyles();string path=System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"test-data","editing-"+Guid.NewGuid().ToString("N"));
  var f=new EditingForm(path);Call(f,"StopDeviceComposition");Set(f,"compositionStopped",false);
  var scene2=(ObsScene)Get(f,"scene");var text=new ObsLayer{Type=SourceType.Text,Name="Title",TextContent="Test",X=100,Y=100,Width=400,Height=120,Rotation=37,CropLeft=.1f,CropTop=.2f,CropRight=.15f,CropBottom=.05f,VisibilityShortcut=(int)(Keys.Control|Keys.Alt|Keys.D1)};scene2.AddLayer(text);Set(f,"selectedLayer",text);
  var toggle=new KeyEventArgs(Keys.Control|Keys.Alt|Keys.D1);Call(f,"OnFormKeyDown",f,toggle);Check(!text.Visible&&toggle.Handled,"Source visibility shortcut failed");
  Call(f,"OnFormKeyDown",f,new KeyEventArgs(Keys.Control|Keys.Alt|Keys.D1));Check(!text.Visible,"Held shortcut toggled repeatedly");Set(f,"shortcutHeld",Keys.None);
  Call(f,"OnFormKeyDown",f,new KeyEventArgs(Keys.Control|Keys.Shift|Keys.H));Check(text.Visible,"Selected visibility shortcut failed");Set(f,"shortcutHeld",Keys.None);
  Call(f,"OnFormKeyDown",f,new KeyEventArgs(Keys.Control|Keys.R));Check(text.Rotation==127,"Rotate shortcut failed");text.Rotation=37;
  Call(f,"ComposeDevices");var first=Get(f,"currentFrame");Call(f,"ComposeDevices");var second=Get(f,"currentFrame");Call(f,"ComposeDevices");Check(object.ReferenceEquals(first,Get(f,"currentFrame"))&&!object.ReferenceEquals(first,second),"Composition surfaces not reused");
  using(var bitmap=new Bitmap(800,450))using(var g=Graphics.FromImage(bitmap)){
   f.RenderPreview(g,800,450);var gate=Get(f,"frameLock");var locked=new ManualResetEvent(false);var release=new ManualResetEvent(false);
   var worker=new Thread(()=>{lock(gate){locked.Set();release.WaitOne();}});worker.Start();locked.WaitOne();var watch=Stopwatch.StartNew();try{f.RenderPreview(g,800,450);watch.Stop();Check(watch.ElapsedMilliseconds<100,"Outline waited on compositor lock");}finally{release.Set();worker.Join();locked.Dispose();release.Dispose();}
  }
  Call(f,"SaveWorkspace");Cleanup(f);f=new EditingForm(path);var restored=((ObsScene)Get(f,"scene")).GetLayersSnapshot().Find(l=>l.Name=="Title");Check(restored!=null&&restored.Rotation==37&&restored.CropLeft==.1f&&restored.CropBottom==.05f&&restored.VisibilityShortcut==text.VisibilityShortcut,"Transforms/shortcut not persisted");Cleanup(f);
  var devices=LocalCameraCapture.GetDevices();foreach(var device in devices)Check(device.Name!="LensioCam"&&device.Name!="OBS Virtual Camera","Virtual output could feed back into capture");
  Console.WriteLine("PASS magnetic edges, crop/rotation rendering and hit testing at 720p/1080p, shortcuts, frame reuse, nonblocking outline, persistence, local-camera enumeration ("+devices.Count+" devices)");return 0;
 }
}
