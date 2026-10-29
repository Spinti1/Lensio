using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using ApexCam;
class MultiLayoutForm:MainForm {
    public MultiLayoutForm() : base(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"test-data",Guid.NewGuid().ToString())) {}
    protected override void OnShown(EventArgs e){}
    protected override void OnFormClosed(FormClosedEventArgs e){}
    protected override void OnFormClosing(FormClosingEventArgs e){}
}
class MultiDeviceSmoke {
    static void Check(bool ok,string text){if(!ok)throw new Exception(text);}
    static void Await(Func<bool> test){var until=DateTime.UtcNow.AddSeconds(5);while(!test() && DateTime.UtcNow<until)Thread.Sleep(10);Check(test(),"Timed out");}
    static object Field(MainForm form,string name){return typeof(MainForm).GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).GetValue(form);}
    static object Call(MainForm form,string name,params object[] args){return typeof(MainForm).GetMethod(name,BindingFlags.Instance|BindingFlags.NonPublic).Invoke(form,args);}
    static byte[] Packet(Color color) {
        using(var bitmap=new Bitmap(16,9))
        using(var ms=new MemoryStream()) {
            using(var g=Graphics.FromImage(bitmap))g.Clear(color);
            bitmap.Save(ms,ImageFormat.Jpeg);var bytes=ms.ToArray();
            using(var result=new MemoryStream())
            using(var w=new BinaryWriter(result)){
                w.Write(new byte[]{65,80,67,77});w.Write(16);w.Write(9);w.Write(bytes.Length);
                w.Write(30);w.Write(80);w.Write(250);w.Write(0);w.Write(bytes);return result.ToArray();
            }
        }
    }
    static TcpClient Phone(VideoReceiver receiver,string id) {
        var c=new TcpClient("127.0.0.1",receiver.BoundPort);c.NoDelay=true;c.ReceiveTimeout=2000;
        var data=Encoding.UTF8.GetBytes("{\"id\":\""+id+"\",\"name\":\"Test "+id+"\"}");
        using(var w=new BinaryWriter(c.GetStream(),Encoding.UTF8,true)){w.Write(new byte[]{65,80,67,73});w.Write(data.Length);w.Write(data);}
        return c;
    }
    [STAThread] static int Main() {
        using(var receiver=new VideoReceiver(0)) {
            var counts=new Dictionary<string,int>();var gate=new object();
            receiver.OnDeviceFrameReceived += (id,b,c,t,f,r,z) => {lock(gate){if(!counts.ContainsKey(id))counts[id]=0;counts[id]++;}};
            receiver.Start();
            using(var a=Phone(receiver,"a"))
            using(var b=Phone(receiver,"b")) {
                var red=Packet(Color.Red);var green=Packet(Color.Lime);
                for(int i=0;i<8;i++){a.GetStream().Write(red,0,red.Length);b.GetStream().Write(green,0,green.Length);Thread.Sleep(20);}
                Await(()=>{lock(gate)return counts.ContainsKey("a") && counts.ContainsKey("b") && counts["a"]>2 && counts["b"]>2;});
                receiver.SendControl("b","{\"zoom\":2}");
                Check(new StreamReader(b.GetStream(),Encoding.UTF8,false,1024,true).ReadLine()=="{\"zoom\":2}","Targeted control failed");
                using(var a2=Phone(receiver,"a")){
                    Thread.Sleep(100);a2.GetStream().Write(red,0,red.Length);
                    int before;lock(gate)before=counts["b"];
                    for(int i=0;i<6;i++){b.GetStream().Write(green,0,green.Length);Thread.Sleep(20);}
                    Await(()=>{lock(gate)return counts["b"]>before;});
                    receiver.SendControlToAll("{\"shutdown\":true}");
                    Check(new StreamReader(b.GetStream(),Encoding.UTF8,false,1024,true).ReadLine().Contains("shutdown"),"Second phone missed shutdown");
                    Check(new StreamReader(a2.GetStream(),Encoding.UTF8,false,1024,true).ReadLine().Contains("shutdown"),"Reconnected phone missed shutdown");
                }
                using(var legacy=new TcpClient("127.0.0.1",receiver.BoundPort)) {
                    legacy.GetStream().Write(red,0,red.Length);
                    Await(()=>{lock(gate)return counts.ContainsKey("legacy:127.0.0.1");});
                }
                receiver.ActiveDeviceId="missing";
                receiver.SendControl("{\"zoom\":3}");
                Thread.Sleep(50);
                Check(b.Available==0,"Offline selection redirected commands to another phone");
                Console.WriteLine("PASS two simultaneous phones on same IP, targeted controls, independent reconnect, legacy clients and shutdown broadcast");
            }
        }
        Application.EnableVisualStyles();Directory.CreateDirectory("build");
        using(var form=new MultiLayoutForm())
        using(var receiver=new VideoReceiver(0)) {
            form.StartPosition=FormStartPosition.Manual;form.Location=new Point(-10000,-10000);form.ShowInTaskbar=false;form.Show();
            typeof(MainForm).GetField("receiver",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(form,receiver);
            Call(form,"HandlePhoneConnected",new PhoneDeviceInfo{Id="a",Name="Phone A",Host="192.0.2.2"});
            Call(form,"HandlePhoneConnected",new PhoneDeviceInfo{Id="b",Name="Phone B",Host="192.0.2.3"});
            using(var a=new Bitmap(16,9))
            using(var b=new Bitmap(16,9)) {
                using(var g=Graphics.FromImage(a))g.Clear(Color.Red);
                using(var g=Graphics.FromImage(b))g.Clear(Color.Lime);
                Call(form,"ProcessDeviceFrame","a",a,80,false,25f,30,8);
                Call(form,"ProcessDeviceFrame","b",b,70,false,25f,30,8);
            }
            var scene=(ObsScene)Field(form,"scene");
            using(var image=new Bitmap(1920,1080)) {
                using(var g=Graphics.FromImage(image)){g.Clear(Color.Black);scene.RenderScene(g,1920,1080);}
                Check(image.GetPixel(500,500).R>220,"Main camera is missing");
                Check(image.GetPixel(1600,150).G>220,"Secondary camera is missing");
                typeof(MainForm).GetField("currentFrame",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(form,new Bitmap(image));
                using(var previewImage=new Bitmap(800,450)) {
                    using(var g=Graphics.FromImage(previewImage))form.RenderPreview(g,800,450);
                    Check(previewImage.GetPixel(200,225).R>220 && previewImage.GetPixel(670,60).G>220,"Scaled preview lost a camera");
                }
                var cards=(Panel)Field(form,"phoneCards");
                foreach(Control row in cards.Controls)Check(row.Controls.Count==1,"Phone row must contain only its name");
                foreach(Control row in cards.Controls)if((string)row.Tag=="b")((Button)row.Controls[0]).PerformClick();
                using(var g=Graphics.FromImage(image)){g.Clear(Color.Black);scene.RenderScene(g,1920,1080);}
                Check(image.GetPixel(500,500).G>220 && image.GetPixel(1600,150).R>220,"Main/secondary swap failed");
            }
            form.Size=new Size(1280,760);Application.DoEvents();Call(form,"LayoutStudioDocks");
            var secondary=scene.GetLayersSnapshot().Find(l=>l.DeviceId=="a");
            var rect=(RectangleF)Call(form,"CanvasToScreenRect",secondary.GetBounds());
            var preview=(Control)Field(form,"previewPanel");
            var start=new Point((int)(rect.X+rect.Width/2),(int)(rect.Y+rect.Height/2));
            var beforeBounds=secondary.GetBounds();
            Call(form,"OnPreviewMouseDown",preview,new MouseEventArgs(MouseButtons.Left,1,start.X,start.Y,0));
            Call(form,"OnPreviewMouseMove",preview,new MouseEventArgs(MouseButtons.Left,0,start.X-30,start.Y+30,0));
            Call(form,"OnPreviewMouseUp",preview,new MouseEventArgs(MouseButtons.Left,1,start.X-30,start.Y+30,0));
            Check(secondary.X<beforeBounds.X && secondary.Y>beforeBounds.Y,"Camera drag failed");
            rect=(RectangleF)Call(form,"CanvasToScreenRect",secondary.GetBounds());
            int right=(int)rect.Right,bottom=(int)rect.Bottom;
            float priorWidth=secondary.Width;
            Call(form,"OnPreviewMouseDown",preview,new MouseEventArgs(MouseButtons.Left,1,right,bottom,0));
            Call(form,"OnPreviewMouseMove",preview,new MouseEventArgs(MouseButtons.Left,0,right+20,bottom+10,0));
            Call(form,"OnPreviewMouseUp",preview,new MouseEventArgs(MouseButtons.Left,1,right+20,bottom+10,0));
            Check(secondary.Width>priorWidth,"Camera resize handle failed");
            foreach(var size in new[]{new Size(640,480),new Size(800,600),new Size(1280,760)}) {
                form.Size=size;Application.DoEvents();Call(form,"LayoutStudioDocks");
                var panel=(Panel)Field(form,"ctrlContent");
                foreach(Control c in panel.Controls) if(c.Visible)Check(panel.ClientRectangle.Contains(c.Bounds),"Clipped multi-phone control: "+c.Text+" at "+form.Size);
            }
            Call(form,"HandlePhoneDisconnected","a");
            using(var output=new Bitmap(1920,1080)) {
                using(var g=Graphics.FromImage(output)){g.Clear(Color.Black);scene.RenderScene(g,1920,1080);}
                Check(output.GetPixel(500,500).G>220,"Disconnected secondary blanked the main camera");
            }
            Call(form,"HandlePhoneConnected",new PhoneDeviceInfo{Id="a",Name="Phone A",Host="192.0.2.2"});
            Check(scene.GetLayersSnapshot().FindAll(l=>l.DeviceId=="a").Count==1,"Reconnect duplicated a source");
            form.Size=new Size(1280,760);Thread.Sleep(80);Application.DoEvents();
            using(var bitmap=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(bitmap,new Rectangle(Point.Empty,form.Size));bitmap.Save("build/multi-phone-workspace.png");}
            Call(form,"StopDeviceComposition");
            ((System.Windows.Forms.Timer)Field(form,"heartbeatTimer")).Stop();
            ((NotifyIcon)Field(form,"trayIcon")).Dispose();
            Console.WriteLine("PASS camera composition, roles, drag, resize and responsive controls");
        }
        return 0;
    }
}
