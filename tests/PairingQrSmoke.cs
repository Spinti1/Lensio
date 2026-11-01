using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using ApexCam;
class QrForm:MainForm {
 public QrForm(string directory):base(directory){}
 protected override void OnShown(EventArgs e){}
 protected override void OnFormClosing(FormClosingEventArgs e){}
 protected override void OnFormClosed(FormClosedEventArgs e){}
}
class PairingQrSmoke {
 static object Get(MainForm f,string n){return typeof(MainForm).GetField(n,BindingFlags.Instance|BindingFlags.NonPublic).GetValue(f);}
 static object Call(MainForm f,string n,params object[] args){return typeof(MainForm).GetMethod(n,BindingFlags.Instance|BindingFlags.NonPublic).Invoke(f,args);}
 static void Check(bool value,string text){if(!value)throw new Exception(text);}
 [STAThread]static int Main(string[] args){
  string fixture=args.Length>0?args[0]:"tests/pairing-qr-reference.json";
  var cases=new JavaScriptSerializer().Deserialize<List<Dictionary<string,object>>>(File.ReadAllText(fixture));
  foreach(var row in cases){string text=(string)row["text"];var hashes=(ArrayList)row["hashes"];
   for(int mask=0;mask<8;mask++){var modules=PairingQrCode.Modules(text,mask);var bits=new StringBuilder();for(int r=0;r<45;r++)for(int c=0;c<45;c++)bits.Append(modules[r,c]?'1':'0');using(var sha=SHA256.Create()){string hash=BitConverter.ToString(sha.ComputeHash(Encoding.ASCII.GetBytes(bits.ToString()))).Replace("-","").ToLowerInvariant();Check(hash==(string)hashes[mask],"QR differs from independent JavaScript encoder, mask "+mask);}}
   using(var image=PairingQrCode.Render(text)){Check(image.Width==212&&image.Height==212,"Unexpected QR dimensions");for(int i=0;i<image.Width;i++)Check(image.GetPixel(i,0).ToArgb()==Color.White.ToArgb()&&image.GetPixel(0,i).ToArgb()==Color.White.ToArgb(),"Quiet zone missing");}
  }
  bool rejected=false;try{PairingQrCode.Render(new string('x',123));}catch(ArgumentException){rejected=true;}Check(rejected,"Oversized QR input accepted");
  string directory=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"test-data","pairing-"+Guid.NewGuid().ToString("N"));
  Application.EnableVisualStyles();var f=new QrForm(directory);Call(f,"InitializePairingConnection");
  var bridge=(WebCameraBridge)Get(f,"webCameraBridge");Check(!bridge.IsRunning&&Get(f,"embeddedWebReceiver")==null,"QR requires an active browser or receiver");
  Check(Get(f,"pairingQrImage") is Bitmap,"QR absent before media engine starts");string link=bridge.PairingLink;
  Call(f,"StartEmbeddedReceiver");Check(((Label)Get(f,"pairingQrStatus")).Text.Contains("unavailable"),"Missing receiver leaves an endless startup label");
  Call(f,"InitializePairingConnection");Check(bridge.PairingLink==link,"Retry changed pairing link");
  using(var restored=new WebCameraBridge(0,directory))Check(restored.PairingLink==link,"Pairing identity changed across launches");
  Call(f,"StopDeviceComposition");((System.Windows.Forms.Timer)Get(f,"heartbeatTimer")).Stop();((NotifyIcon)Get(f,"trayIcon")).Dispose();bridge.Dispose();f.Dispose();
  Console.WriteLine("PASS 32 independent QR matrices, quiet zone, capacity bound, QR without WebView2/listeners, stable pairing and explicit startup failure");return 0;
 }
}
