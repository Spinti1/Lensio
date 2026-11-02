using System;
using System.Drawing;
using System.Reflection;
using ApexCam;
class BrandingSmoke {
 static void Check(bool value,string message){if(!value)throw new Exception(message);}
 static int Main(){
  using(var stream=typeof(MainForm).Assembly.GetManifestResourceStream("LensioBrand.noSignal"))
  using(var expected=new Bitmap(stream))
  using(var actual=new Bitmap(1280,720))
  using(var layer=new ObsLayer{Type=SourceType.Camera,X=0,Y=0,Width=1920,Height=1080,Opacity=1,Visible=true}){
   using(var g=Graphics.FromImage(actual)){g.Clear(Color.Black);layer.RenderCamera(g,1280f/1920,720f/1080);}
   foreach(var point in new[]{new Point(0,0),new Point(100,360),new Point(640,360),new Point(1279,719)})Check(actual.GetPixel(point.X,point.Y)==expected.GetPixel(point.X,point.Y),"Missing-camera artwork mismatch: "+actual.GetPixel(point.X,point.Y)+" expected "+expected.GetPixel(point.X,point.Y));
   var live=new Bitmap(1280,720);using(var g=Graphics.FromImage(live))g.Clear(Color.Red);layer.SetCameraFrame(live);
   using(var g=Graphics.FromImage(actual))layer.RenderCamera(g,1280f/1920,720f/1080);
   Check(actual.GetPixel(640,360).R==255,"Live frame did not replace standby");
   layer.SetCameraFrame(null);
   using(var g=Graphics.FromImage(actual))layer.RenderCamera(g,1280f/1920,720f/1080);
   Check(actual.GetPixel(640,360)==expected.GetPixel(640,360),"Disconnect did not restore standby");
  }
  Console.WriteLine("PASS offline artwork, live frame replacement and disconnect restoration");return 0;
 }
}
