using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Threading;
using ApexCam;
class VirtualCameraIsolationSmoke {
 static int Main(){
  using(var obs=MemoryMappedFile.CreateOrOpen("OBSVirtualCamVideo",32*1024*1024))using(var data=obs.CreateViewAccessor())using(var bridge=new VirtualCameraBridge(1280,720))using(var red=new Bitmap(1280,720)){
   using(var g=Graphics.FromImage(red))g.Clear(Color.Red);
   int size=1280*720*3/2,offset=96;var blue=new byte[size];for(int i=0;i<1280*720;i++)blue[i]=41;for(int i=1280*720;i<size;i+=2){blue[i]=240;blue[i+1]=110;}
   data.Write(0,(uint)0);data.Write(4,(uint)0);data.Write(8,(uint)2);for(int i=0;i<3;i++){int at=offset+i*((size+63)&~31);data.Write(12+i*4,(uint)at);data.Write(at,(ulong)1);data.WriteArray(at+32,blue,0,blue.Length);}data.Write(24,(uint)0);data.Write(28,(uint)1280);data.Write(32,(uint)720);data.Write(40,(ulong)333333);
   using(var timer=new System.Threading.Timer(_=>bridge.WriteFrame(red),null,0,33)){
    Thread.Sleep(200);
    foreach(string bits in new[]{"64","32"})foreach(string stream in new[]{"lensio","obs"}){
     string executable=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"DirectShowCapture"+bits+".exe");
     using(var p=Process.Start(new ProcessStartInfo{FileName=executable,Arguments=stream+" "+(stream=="lensio"?"82":"41"),UseShellExecute=false,CreateNoWindow=true})){
      if(!p.WaitForExit(15000)){p.Kill();throw new Exception("Capture test timed out");}if(p.ExitCode!=0)throw new Exception("Independent capture failed: "+stream+bits);
     }
    }
    if(data.ReadUInt32(28)!=1280||data.ReadByte(offset+32)!=41)throw new Exception("Lensio modified OBS shared memory");
   }
   data.Write(8,(uint)0);
  }
  Console.WriteLine("PASS separate Lensio/OBS classes and simultaneous independent red/blue streams, x86/x64; OBS queue unchanged");return 0;
 }
}
