using System;
using System.Diagnostics;
using System.Drawing;
using System.Threading;
using ApexCam;
class LocalCameraSmoke {
 static int Main(){
  var devices=LocalCameraCapture.GetDevices();Console.WriteLine("Eligible local webcams: "+devices.Count);if(devices.Count==0){Console.WriteLine("SKIP physical webcam capture: none connected");return 0;}
  for(int pass=0;pass<2;pass++){
   int count=0,w=0,h=0,nominal=0;Exception error=null;var watch=Stopwatch.StartNew();
   using(var camera=new LocalCameraCapture(devices[0].Id,b=>{w=b.Width;h=b.Height;Interlocked.Increment(ref count);},e=>error=e)){
    camera.Start();while(watch.ElapsedMilliseconds<6000&&error==null)Thread.Sleep(50);nominal=camera.NegotiatedFrameRate;
   }
   if(error!=null){Console.WriteLine("FAIL local capture: "+error.Message+" 0x"+error.HResult.ToString("X8"));return 1;}
   if(count<10){Console.WriteLine("FAIL local camera delivered too few frames: "+count);return 1;}
   int stopped=count;Thread.Sleep(250);if(count!=stopped)throw new Exception("Capture callbacks continued after disposal");
   Console.WriteLine("PASS local webcam open/capture/close cycle "+(pass+1)+": "+count+" fresh frames, "+w+"x"+h+", negotiated "+nominal+" FPS");
  }
  return 0;
 }
}
