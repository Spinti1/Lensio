using System;
using System.Drawing;
using System.IO.MemoryMappedFiles;
using System.Threading;
using ApexCam;
class VirtualCameraQueueSmoke {
 static void Check(bool ok,string message){if(!ok)throw new Exception(message);}
 static int Main(){
  using(var obs=MemoryMappedFile.CreateOrOpen("OBSVirtualCamVideo",32*1024*1024))using(var sentinel=obs.CreateViewAccessor()){
   sentinel.Write(100,(uint)0x13579bdf);
   using(var bridge=new VirtualCameraBridge(1280,720))using(var source=new Bitmap(1280,720)){
    using(var graphics=Graphics.FromImage(source))graphics.Clear(Color.Red);bridge.WriteFrame(source);Thread.Sleep(50);
    using(var lensio=MemoryMappedFile.OpenExisting("LensioVirtualCamVideo"))using(var data=lensio.CreateViewAccessor()){
     Check(data.ReadUInt32(28)==1280&&data.ReadUInt32(32)==720,"Lensio queue dimensions invalid");
     uint index=data.ReadUInt32(0);uint offset=data.ReadUInt32(12+(index%3)*4);
     Check(offset>=96,"Lensio queue offset invalid");Check(Math.Abs(data.ReadByte(offset+32)-82)<=3,"Lensio frame pixels missing");
    }
    Check(sentinel.ReadUInt32(100)==0x13579bdf,"Lensio wrote into OBS queue");
   }
   Check(sentinel.ReadUInt32(100)==0x13579bdf,"Lensio shutdown modified OBS queue");
  }
  Check(BundledSetup.CameraClassId!="{A3FCE0F5-3493-419F-958A-ABA1250EC20B}","Lensio claims OBS camera class");
  Console.WriteLine("PASS independent Lensio frame queue, correct pixels, OBS queue preserved during streaming/shutdown and distinct class");return 0;
 }
}
