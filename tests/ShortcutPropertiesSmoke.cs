using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using ApexCam;
class ShortcutForm:MainForm {
 public ShortcutForm():base(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"test-data","shortcuts-"+Guid.NewGuid().ToString("N"))){}
 protected override void OnShown(EventArgs e){}
 protected override void OnFormClosing(FormClosingEventArgs e){}
 protected override void OnFormClosed(FormClosedEventArgs e){}
}
class ShortcutPropertiesSmoke {
 static object Get(MainForm f,string n){return typeof(MainForm).GetField(n,BindingFlags.Instance|BindingFlags.NonPublic).GetValue(f);}
 static void Set(MainForm f,string n,object v){typeof(MainForm).GetField(n,BindingFlags.Instance|BindingFlags.NonPublic).SetValue(f,v);}
 static object Call(MainForm f,string n,params object[] args){return typeof(MainForm).GetMethod(n,BindingFlags.Instance|BindingFlags.NonPublic).Invoke(f,args);}
 static void Check(bool ok,string message){if(!ok)throw new Exception(message);}
 static Button Button(Control parent,string name){foreach(Control c in parent.Controls){if(c is Button&&c.Text==name)return (Button)c;var child=Button(c,name);if(child!=null)return child;}return null;}
 [STAThread]static int Main(){
  Application.EnableVisualStyles();var f=new ShortcutForm();f.Location=new Point(-10000,-10000);f.ShowInTaskbar=false;f.Show();Call(f,"StopDeviceComposition");
  var scene=(ObsScene)Get(f,"scene");var layer=new ObsLayer{Type=SourceType.Text,Name="Test",TextContent="Shortcut source",X=100,Y=120,Width=400,Height=100,Rotation=30};scene.AddLayer(layer);Set(f,"selectedLayer",layer);
  Keys[] keys={Keys.A,Keys.Space,Keys.Tab,Keys.Enter,Keys.Escape,Keys.Delete,Keys.Left,Keys.NumPad7,Keys.F1,Keys.F11,Keys.Shift|Keys.A,Keys.Control|Keys.R,Keys.Alt|Keys.Subtract,Keys.Control|Keys.Shift|Keys.H};
  var captureType=typeof(MainForm).GetNestedType("ShortcutCaptureDialog",BindingFlags.NonPublic);
  foreach(var key in keys){
   Check((bool)typeof(MainForm).GetMethod("ValidSourceShortcut",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[]{key}),"Rejected key "+key);
  }
  return Run(f,scene,layer,keys,captureType);
 }
 static int Run(MainForm f,ObsScene scene,ObsLayer layer,Keys[] keys,Type captureType){
  foreach(var key in keys){
   layer.VisibilityShortcut=(int)key;Set(f,"shortcutHeld",Keys.None);layer.Visible=true;float rotation=layer.Rotation;
   Check((bool)Call(f,"ProcessCmdKey",new Message(),key),"Command key not handled: "+key);Check(!layer.Visible&&layer.Rotation==rotation,"Binding did not override default: "+key);
   Call(f,"ProcessCmdKey",new Message(),key);Check(!layer.Visible,"Held key repeated: "+key);
   using(var dialog=(Form)Activator.CreateInstance(captureType,true)){
    Keys captured=Keys.None;captureType.GetField("Listening").SetValue(dialog,true);captureType.GetField("Captured").SetValue(dialog,(Action<Keys>)(v=>captured=v));
    captureType.GetMethod("OnKeyDown",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(dialog,new object[]{new KeyEventArgs(key)});Check(captured==key,"Input key capture failed: "+key);
    captureType.GetMethod("OnKeyUp",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(dialog,new object[]{new KeyEventArgs(key&Keys.KeyCode)});captureType.GetField("Listening").SetValue(dialog,true);captured=Keys.None;
    captureType.GetMethod("ProcessCmdKey",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(dialog,new object[]{new Message(),key});Check(captured==key,"Navigation key capture failed: "+key);
   }
  }
  var menu=(ContextMenuStrip)Get(f,"sourceContextMenu");foreach(ToolStripItem item in menu.Items)Check(item.Text!="Transform"&&item.Text!="Fit to canvas"&&item.Text!="Center in frame","Transform remains in source menu");
  layer.Visible=true;Exception error=null;
  using(var timer=new System.Windows.Forms.Timer{Interval=80}){
   timer.Tick+=(s,e)=>{var dialog=(Form)Get(f,"sourcePropertiesWindow");if(dialog==null)return;timer.Stop();try{
    Check(dialog.Controls.Find("SourcePropertiesTabs",true).Length==0,"Separate transform tabs remain");var transform=dialog.Controls.Find("SourceTransformControls",true)[0];Check(transform.Parent==Get(f,"propContentPanel"),"Transform is not inline with source properties");
    bool belowOpacity=false;foreach(Control c in transform.Parent.Controls)if(c is TrackBar&&c.Bottom<=transform.Top)belowOpacity=true;Check(belowOpacity,"Transforms are not below opacity");
    Check(!((Panel)transform.Parent).AutoScroll,"Properties introduced a scrollbar");
    var angle=(NumericUpDown)dialog.Controls.Find("SourceRotation",true)[0];angle.Value=45;Check(layer.Rotation==45,"Property rotation failed");layer.CropLeft=.2f;Button(transform,"Reset crop").PerformClick();Check(layer.CropLeft==0,"Property crop reset failed");
    Button(transform,"Fit to canvas").PerformClick();Check(layer.Width==1920&&layer.Height==1080,"Property fit failed");
    Directory.CreateDirectory("build");using(var image=new Bitmap(dialog.Width,dialog.Height)){dialog.DrawToBitmap(image,new Rectangle(Point.Empty,image.Size));image.Save("build/properties-transform.png");}
   }catch(Exception ex){error=ex;}finally{dialog.Close();}};timer.Start();Call(f,"ShowSourceProperties");
  }
  ((System.Windows.Forms.Timer)Get(f,"heartbeatTimer")).Stop();((NotifyIcon)Get(f,"trayIcon")).Dispose();f.Dispose();if(error!=null)throw error;
  Console.WriteLine("PASS single/special/numpad/modifier shortcuts, command/input capture, repeat suppression, default overrides and Transform controls inside Properties");return 0;
 }
}
