using System;
using System.Drawing;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Forms;
using ApexCam;
class EmbeddedReceiverSmoke {
    [STAThread]static int Main(){WebReceiverRuntime.Initialize();return Run();}
    [MethodImpl(MethodImplOptions.NoInlining)]static int Run(){
        Application.EnableVisualStyles();bool qr=false,ready=false;string last="";
        using(var receiver=new VideoReceiver(0))using(var form=new Form{Size=new Size(240,160),ShowInTaskbar=false,StartPosition=FormStartPosition.Manual,Location=new Point(-10000,-10000)}){
            receiver.Start();using(var bridge=new WebCameraBridge(receiver.BoundPort))using(var engine=new EmbeddedWebReceiver()){
                bridge.Start();engine.QrReady+=image=>{qr=image.Width>100&&image.Height>100;Console.WriteLine("Native QR rendered: "+image.Width+"x"+image.Height);image.Dispose();};
                engine.Ready+=()=>{ready=true;Console.WriteLine("Background capture mode: "+engine.CaptureMode);form.Close();};engine.Status+=text=>last=text;
                var timer=new Timer{Interval=25000};timer.Tick+=(s,e)=>form.Close();
                form.Shown+=async(s,e)=>{timer.Start();await engine.StartAsync(form,bridge.EngineUrl,Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"build","webview-test",Guid.NewGuid().ToString()));};
                Application.Run(form);timer.Dispose();
            }
        }
        if(!qr||!ready)throw new Exception("Embedded receiver failed: QR="+qr+" ready="+ready+" status="+last);
        Console.WriteLine("PASS: embedded WebView2 starts, QR renders locally and private-room discovery connects without an external browser");return 0;
    }
}
