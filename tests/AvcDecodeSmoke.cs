using System;
using System.Drawing;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using ApexCam;
class AvcDecodeSmoke {
 static void Check(bool b,string s){if(!b)throw new Exception(s);}
 static int Main(string[] args){
  var type=typeof(MainForm).Assembly.GetType("ApexCam.AvcVideoDecoder");
  foreach(bool gpu in new[]{false,true})foreach(int rotation in new[]{0,90,180,270}){
   int width=args.Length>1?int.Parse(args[1]):1280,height=args.Length>2?int.Parse(args[2]):720;
   using(var instance=(IDisposable)Activator.CreateInstance(type,BindingFlags.Instance|BindingFlags.Public,null,new object[]{width,height,gpu},null)){
    int count=0;var watch=Stopwatch.StartNew();
    Action<Bitmap> callback=b=>{
     Check(b.Width==(rotation==90||rotation==270?height:width)&&b.Height==(rotation==90||rotation==270?width:height),"Decoded dimensions/rotation wrong");
     Point left,right;if(rotation==0){left=new Point(b.Width/4,b.Height/2);right=new Point(b.Width*3/4,b.Height/2);}else if(rotation==180){right=new Point(b.Width/4,b.Height/2);left=new Point(b.Width*3/4,b.Height/2);}else if(rotation==90){left=new Point(b.Width/2,b.Height/4);right=new Point(b.Width/2,b.Height*3/4);}else{right=new Point(b.Width/2,b.Height/4);left=new Point(b.Width/2,b.Height*3/4);}
     var red=b.GetPixel(left.X,left.Y);var green=b.GetPixel(right.X,right.Y);Check(red.R>200&&red.G<40&&green.G>200&&green.R<40,"Decoded colors incorrect: "+red+" "+green);count++;
    };
    using(var reader=new BinaryReader(File.OpenRead(args[0]))){int index=0;while(reader.BaseStream.Position<reader.BaseStream.Length){int length=reader.ReadInt32();reader.ReadInt32();var bytes=reader.ReadBytes(length);type.GetMethod("Decode").Invoke(instance,new object[]{bytes,index++*333333L,rotation,callback});}}
    Check(count>=58,"Decoder lost frames: "+count);Console.WriteLine("PASS AVC "+width+"x"+height+" rotation="+rotation+" hardware="+type.GetProperty("IsHardware").GetValue(instance,null)+" frames="+count+" ms="+watch.ElapsedMilliseconds);
   }
  }return 0;
 }
}
