using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using System.Web.Script.Serialization;
using ApexCam;
class WorkspaceForm:MainForm {
 public WorkspaceForm(string folder):base(folder){}
 protected override void OnShown(EventArgs e){}
 protected override void OnFormClosed(FormClosedEventArgs e){}
 protected override void OnFormClosing(FormClosingEventArgs e){}
}
class WorkspacePersistenceSmoke {
 static object Get(MainForm f,string name){return typeof(MainForm).GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).GetValue(f);}
 static void Set(MainForm f,string name,object value){typeof(MainForm).GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).SetValue(f,value);}
 static object Call(MainForm f,string name,params object[] values){foreach(var method in typeof(MainForm).GetMethods(BindingFlags.Instance|BindingFlags.NonPublic))if(method.Name==name && method.GetParameters().Length==values.Length)return method.Invoke(f,values);throw new MissingMethodException(name);}
 static void Check(bool ok,string text){if(!ok)throw new Exception(text);}
 static void Cleanup(MainForm f){Call(f,"StopDeviceComposition");((System.Windows.Forms.Timer)Get(f,"heartbeatTimer")).Stop();((NotifyIcon)Get(f,"trayIcon")).Dispose();f.Dispose();}
 static void Pump(int milliseconds){var end=DateTime.UtcNow.AddMilliseconds(milliseconds);while(DateTime.UtcNow<end){Application.DoEvents();Thread.Sleep(10);}}
 [STAThread]static int Main(){
  Application.EnableVisualStyles();string folder=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"test-data","workspace-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
  string original=Path.Combine(folder,"original.png");using(var b=new Bitmap(12,8)){using(var g=Graphics.FromImage(b))g.Clear(Color.Magenta);b.Save(original,ImageFormat.Png);}
  string imageId,cameraId;
  var f=new WorkspaceForm(folder);var receiver=new VideoReceiver(0);Set(f,"receiver",receiver);
  Call(f,"HandlePhoneConnected",new PhoneDeviceInfo{Id="test-phone",Name="My phone",Host=""});
  Call(f,"AddImageLayerFromFile",original,101f,77f);
  var scene=(ObsScene)Get(f,"scene");var layers=scene.GetLayersSnapshot();var image=layers.Find(l=>l.Type==SourceType.Image);var camera=layers.Find(l=>l.Type==SourceType.Camera);
  imageId=image.Id;cameraId=camera.Id;image.Opacity=.65f;image.Width=432;image.Height=288;image.Locked=true;
  camera.SetBounds(new RectangleF(50,80,800,450));
  var text=new ObsLayer{Type=SourceType.Text,Name="Title",TextContent="Saved title",X=20,Y=30,Width=300,Height=60,FontSize=32,TextColor=Color.Gold,Visible=false};scene.AddLayer(text);
  ((TrackBar)Get(f,"tbSaturation")).Value=42;((TrackBar)Get(f,"tbExposure")).Value=-7;
  ((ComboBox)Get(f,"cbPresets")).SelectedIndex=(int)FilterMode.WarmStudio;Set(f,"mirrorH",true);Set(f,"flipV",true);
  Call(f,"SelectResolution",1920,1080);Call(f,"OnToggleVcam",null,EventArgs.Empty);
  Call(f,"SendPhoneControl", "test-phone","{\"cameraId\":\"2\",\"zoom\":1.8,\"stabilization\":\"off\"}");
  // No explicit save: this exercises the automatic UI timer.
  Pump(1250);string manifest=Path.Combine(folder,"workspace.json");Check(File.Exists(manifest),"Autosave did not create workspace");
  Check(Directory.GetFiles(Path.Combine(folder,"assets"),"*.png").Length==1,"Image asset was duplicated");
  var savedAt=File.GetLastWriteTimeUtc(manifest);Pump(1100);Check(File.GetLastWriteTimeUtc(manifest)==savedAt,"Unchanged workspace was rewritten");
  Cleanup(f);receiver.Dispose();File.Delete(original);
  var restored=new WorkspaceForm(folder);
  try {
   layers=((ObsScene)Get(restored,"scene")).GetLayersSnapshot();Check(layers.Count==3,"Sources were lost or duplicated");
   Check(layers[0].Name=="Title" && !layers[0].Visible && layers[0].TextColor.ToArgb()==Color.Gold.ToArgb(),"Layer order/text style not restored");
   image=layers.Find(l=>l.Id==imageId);Check(image!=null && image.Locked && image.X==101 && image.Width==432 && Math.Abs(image.Opacity-.65f)<.001,"Image placement/properties not restored");
   Check(((Bitmap)image.BitmapImage).GetPixel(4,4).R==255 && ((Bitmap)image.BitmapImage).GetPixel(4,4).B==255,"Managed image failed after original deletion");
   camera=layers.Find(l=>l.Id==cameraId);Check(camera!=null && camera.DeviceId=="test-phone" && camera.Width==800,"Phone placement/identity not restored");
   Check((int)Get(restored,"currentResWidth")==1920 && !(bool)Get(restored,"isVirtualCamActive"),"Output settings not restored");
   Check((float)Get(restored,"saturationVal")==42 && (float)Get(restored,"exposureVal")==-7 && (bool)Get(restored,"mirrorH") && (bool)Get(restored,"flipV"),"Filters not restored");
   Check((FilterMode)Get(restored,"currentFilter")==FilterMode.WarmStudio,"Preset not restored");
   var state=new JavaScriptSerializer().Deserialize<SavedWorkspace>(File.ReadAllText(manifest));
   Check(state.MainPhone=="test-phone" && state.PhoneControls["test-phone"]["cameraId"].ToString()=="2","Phone controls not saved");
   ((ObsScene)Get(restored,"scene")).RemoveLayer(image);Call(restored,"SaveWorkspace");
   Check(Directory.GetFiles(Path.Combine(folder,"assets"),"*.png").Length<=1,"Asset backup grew without bounds");
  }finally{Cleanup(restored);}
  var third=new WorkspaceForm(folder);try{Check(((ObsScene)Get(third,"scene")).LayerCount==2,"Deleted source returned after restart");}finally{Cleanup(third);}
  File.WriteAllText(manifest,"{broken");var corrupt=new WorkspaceForm(folder);try{Check(((ObsScene)Get(corrupt,"scene")).LayerCount>=2,"Last valid workspace backup was not restored");}finally{Cleanup(corrupt);}
  string offlineFolder=Path.Combine(folder,"offline");
  var offline=new WorkspaceForm(offlineFolder);
  using(var bitmap=new Bitmap(20,20)){using(var g=Graphics.FromImage(bitmap))g.Clear(Color.Lime);bitmap.Save(original,ImageFormat.Png);}
  Call(offline,"AddImageLayerFromFile",original,400f,300f);
  Check(File.Exists(Path.Combine(offlineFolder,"workspace.json")),"New image was not saved immediately");
  var offlineScene=(ObsScene)Get(offline,"scene");
  offlineScene.AddLayer(new ObsLayer{Type=SourceType.Text,Name="Offline title",TextContent="Visible without a phone",X=10,Y=10,Width=500,Height=60});
  var titleLayer=offlineScene.GetLayersSnapshot().Find(l=>l.Type==SourceType.Text);
  Set(offline,"selectedLayer",titleLayer);Call(offline,"RefreshSourcesList");
  foreach(Control control in ((Panel)Get(offline,"propContentPanel")).Controls)if(control is TextBox)((TextBox)control).Text="Edited and saved immediately";
  var immediate=new JavaScriptSerializer().Deserialize<SavedWorkspace>(File.ReadAllText(Path.Combine(offlineFolder,"workspace.json")));
  Check(immediate.Sources.Exists(l=>l.Text=="Edited and saved immediately"),"Text edit was not saved immediately");
  Cleanup(offline);File.Delete(original);
  var reopened=new WorkspaceForm(offlineFolder);
  try{
   Call(reopened,"ComposeDevices");
   if(!reopened.IsHandleCreated)Check((int)Get(reopened,"previewRefreshPending")==0,"Pre-handle refresh blocked future paints");
   reopened.Show();Pump(120);
   lock(Get(reopened,"frameLock")){
    var frame=(Bitmap)Get(reopened,"currentFrame");Check(frame!=null,"Offline restored sources were never composed");
    Check(frame.GetPixel(320,230).G>150,"Restored image is not visible before selecting Sources");
   }
   var restoredScene=(ObsScene)Get(reopened,"scene");
   Check(restoredScene.LayerCount==2,"Offline image/text were not restored");
   Call(reopened,"HandlePhoneConnected",new PhoneDeviceInfo{Id="new-phone",Name="New phone",Host=""});
   Check(restoredScene.GetLayersSnapshot().Exists(l=>l.DeviceId=="new-phone"),"Camera connection was not exercised");
   Check(restoredScene.GetLayersSnapshot()[0].Type!=SourceType.Camera,"Automatic camera connection covered saved overlays");
   Call(reopened,"StopDeviceComposition");
  }finally{Cleanup(reopened);}
  Console.WriteLine("PASS automatic save, restart restore, managed images, order, properties, filters, phone identity/controls, deletion and corrupt-file recovery");return 0;
 }
}
