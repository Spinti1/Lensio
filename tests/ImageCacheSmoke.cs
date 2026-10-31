using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Reflection;
using System.Threading;
using ApexCam;
class ImageCacheSmoke {
 static void Check(bool ok,string text){if(!ok)throw new Exception(text);}
 static Bitmap Render(ObsScene scene){var b=new Bitmap(1920,1080);using(var g=Graphics.FromImage(b)){g.Clear(Color.Blue);scene.RenderScene(g,b.Width,b.Height);}return b;}
 static object Cache(ObsLayer layer){return typeof(ObsLayer).GetField("imageCache",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(layer);}
 static Bitmap Image(Color c){var b=new Bitmap(80,60,PixelFormat.Format32bppArgb);using(var g=Graphics.FromImage(b)){g.Clear(Color.Transparent);using(var brush=new SolidBrush(c))g.FillRectangle(brush,10,10,60,40);}return b;}
 static int Main(){
  using(var layer=new ObsLayer{Type=SourceType.Image,X=100,Y=100,Width=160,Height=120,Opacity=.5f}){
   layer.BitmapImage=Image(Color.FromArgb(128,255,0,0));var scene=new ObsScene();scene.AddLayer(layer);
   using(var output=Render(scene)){
    var c=output.GetPixel(180,160);Check(Math.Abs(c.R-64)<=2&&Math.Abs(c.B-191)<=2,"PNG transparency or source opacity changed");
    Check(output.GetPixel(102,102).ToArgb()==Color.Blue.ToArgb(),"Transparent PNG background became opaque");
   }
   var cached=Cache(layer);using(var repeat=Render(scene))Check(object.ReferenceEquals(cached,Cache(layer)),"Unchanged image cache was recreated");
   layer.X=300;using(var moved=Render(scene)){Check(object.ReferenceEquals(cached,Cache(layer)),"Moving a source unnecessarily rebuilt its cache");Check(moved.GetPixel(380,160).R>50&&moved.GetPixel(180,160).B==255,"Cached image did not move");}
   layer.Opacity=1;using(var changed=Render(scene))Check(changed.GetPixel(380,160).R>120,"Opacity did not invalidate cached pixels");
   layer.SetCameraFrame(Image(Color.FromArgb(255,0,255,0)));using(var changed=Render(scene))Check(changed.GetPixel(380,160).G>245,"Replacement image did not invalidate cache");
   layer.Width=320;using(var changed=Render(scene))Check(((Bitmap)Cache(layer)).Width==320,"Resize did not rebuild at the full output resolution");
  }
  var type=typeof(MainForm).GetNestedType("ActualFrameRate",BindingFlags.NonPublic);var meter=Activator.CreateInstance(type,true);
  var tick=type.GetMethod("Tick");var value=type.GetProperty("Value");
  for(int i=0;i<12;i++){tick.Invoke(meter,null);Thread.Sleep(100);}
  int rate=(int)value.GetValue(meter,null);Check(rate>=8&&rate<=12,"FPS counter did not measure actual frames");
  Thread.Sleep(2100);Check((int)value.GetValue(meter,null)==0,"Frozen camera still reports a positive FPS rate");
  Console.WriteLine("PASS cached PNG alpha, opacity, reuse, movement, replacement, full-resolution resizing and real/frozen FPS");return 0;
 }
}
