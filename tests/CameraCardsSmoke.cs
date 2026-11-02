using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using ApexCam;
class CardsForm:MainForm {
 public CardsForm(string folder):base(folder){}
 protected override void OnShown(EventArgs e){}
 protected override void OnFormClosed(FormClosedEventArgs e){}
 protected override void OnFormClosing(FormClosingEventArgs e){}
}
class CameraCardsSmoke {
 static object Get(object f,string n){return typeof(MainForm).GetField(n,BindingFlags.Instance|BindingFlags.NonPublic).GetValue(f);}
 static void Set(object f,string n,object v){typeof(MainForm).GetField(n,BindingFlags.Instance|BindingFlags.NonPublic).SetValue(f,v);}
 static object Call(MainForm f,string n,params object[] a){return typeof(MainForm).GetMethod(n,BindingFlags.Instance|BindingFlags.NonPublic).Invoke(f,a);}
 static void Check(bool ok,string message){if(!ok)throw new Exception(message);}
 static void Cleanup(MainForm f){Call(f,"StopDeviceComposition");((Timer)Get(f,"heartbeatTimer")).Stop();((NotifyIcon)Get(f,"trayIcon")).Dispose();f.Dispose();}
 static Control Row(MainForm f,string id){foreach(Control row in ((Panel)Get(f,"phoneCards")).Controls)if((string)row.Tag==id)return row;throw new Exception("Missing card");}
 static string First(MainForm f){return (string)((Panel)Get(f,"phoneCards")).Controls[0].Tag;}
 [STAThread]static int Main(){
  Application.EnableVisualStyles();string folder=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"test-data","cards-"+Guid.NewGuid().ToString("N"));
  var f=new CardsForm(folder);f.ShowInTaskbar=false;f.Location=new Point(-10000,-10000);f.Show();Call(f,"StopDeviceComposition");Set(f,"compositionStopped",false);
  foreach(string id in new[]{"a","b","web:c"})Call(f,"HandlePhoneConnected",new PhoneDeviceInfo{Id=id,Name="Phone "+id,Host=""});
  var scene=(ObsScene)Get(f,"scene");var a=scene.GetLayersSnapshot().Find(l=>l.DeviceId=="a");var b=scene.GetLayersSnapshot().Find(l=>l.DeviceId=="b");
  var bounds=a.GetBounds();var main=Get(f,"mainDeviceId");
  var overlay=new ObsLayer{Type=SourceType.ColorBox,Name="Overlay",SolidColor=Color.Magenta,X=0,Y=0,Width=1920,Height=1080};scene.AddLayer(overlay);
  for(int i=0;i<10;i++){scene.MoveDown(overlay);scene.MoveUp(a);}
  Check(scene.GetLayersSnapshot()[0]==overlay,"Camera crossed overlay boundary");
  for(int i=0;i<10;i++)scene.MoveDown(a);Call(f,"RefreshPhoneCards");Check(First(f)!="a","Drag fixture already first");
  Call(f,"RefreshSourcesList");var list=(ListBox)Get(f,"lbSources");Check(list.Items.Count==1&&list.Items[0].ToString().Contains("Overlay"),"Cameras leaked into Sources");
  // Exercise the same drop event used by a mouse drag, including before/after positioning.
  var payloadType=typeof(MainForm).GetNestedType("CameraOrderDrag",BindingFlags.NonPublic);var payload=Activator.CreateInstance(payloadType,true);payloadType.GetField("Owner").SetValue(payload,f);payloadType.GetField("Id").SetValue(payload,"a");
  var data=new DataObject();data.SetData(payloadType,payload);Control target=Row(f,"web:c").Controls[0];var point=target.PointToScreen(new Point(5,2));
  typeof(Control).GetMethod("OnDragDrop",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(target,new object[]{new DragEventArgs(data,0,point.X,point.Y,DragDropEffects.Move,DragDropEffects.Move)});Application.DoEvents();
  Check(First(f)=="a","Drop did not reorder cards");Check(a.GetBounds()==bounds&&Equals(Get(f,"mainDeviceId"),main),"Reordering changed geometry or audio owner");
  Check(scene.GetLayersSnapshot()[0]==overlay&&scene.GetLayersSnapshot()[1]==a,"Drop order differs from compositor order");
  using(var image=new Bitmap(192,108)){using(var g=Graphics.FromImage(image))scene.RenderScene(g,192,108);Check(image.GetPixel(90,50).ToArgb()==Color.Magenta.ToArgb(),"Overlay is obscured by camera");}
  using(var frame=new Bitmap(16,9))Call(f,"ProcessDeviceFrame","a",frame,17,true,25f,30,0);
  Call(f,"RefreshPhoneBatteryIndicators");var badge=Row(f,"a").Controls[1];Check(badge.AccessibleName=="17 percent battery, charging","Battery or charging status incorrect");
  var rowIdentity=Row(f,"a");for(int i=0;i<5;i++)Call(f,"RefreshPhoneBatteryIndicators");Check(ReferenceEquals(rowIdentity,Row(f,"a")),"Telemetry rebuilt row during drag");
  Check(Row(f,"web:c").Controls[1].AccessibleName=="Battery unavailable","Browser battery fabricated");
  Directory.CreateDirectory("build");using(var image=new Bitmap(80,27)){badge.DrawToBitmap(image,new Rectangle(0,0,80,27));image.Save("build/phone-battery-charging.png");}
  Call(f,"HandlePhoneDisconnected","a");Check(Row(f,"a").Controls[1].AccessibleName=="Phone offline","Offline status stale");
  Call(f,"HandlePhoneConnected",new PhoneDeviceInfo{Id="a",Name="Phone a",Host=""});Check(First(f)=="a","Reconnect changed priority");
  Call(f,"SaveWorkspace");Cleanup(f);
  var restored=new CardsForm(folder);try{Check(First(restored)=="a","Priority not restored");Check(((ListBox)Get(restored,"lbSources")).Items.Count==1,"Restored cameras appeared in Sources");var layers=((ObsScene)Get(restored,"scene")).GetLayersSnapshot();Check(layers[0].Name=="Overlay"&&layers[1].DeviceId=="a","Restored layer hierarchy wrong");}finally{Cleanup(restored);}
  Console.WriteLine("PASS camera drop ordering, overlay priority, filtered Sources, battery/charging/offline states, stable row identity, reconnect and restart persistence");return 0;
 }
}
