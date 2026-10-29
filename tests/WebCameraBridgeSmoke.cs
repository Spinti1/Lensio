using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using ApexCam;
class WebCameraBridgeSmoke {
    static void Check(bool b,string text){if(!b)throw new Exception(text);}
    static void Await(Func<bool> predicate){var end=DateTime.UtcNow.AddSeconds(6);while(DateTime.UtcNow<end){if(predicate())return;Thread.Sleep(30);}throw new Exception("Timed out");}
    static string root,token,session="test-session-one";
    static string Post(string path,byte[] bytes,bool auth=true,string origin=null,bool frame=false){
        var r=(HttpWebRequest)WebRequest.Create(root+path);r.Method="POST";r.Proxy=null;r.Timeout=5000;
        if(auth)r.Headers["Authorization"]="Bearer "+token;r.Headers["X-Session"]=session;if(origin!=null)r.Headers["Origin"]=origin;
        if(frame){r.Headers["X-Device"]="web:test";r.Headers["X-Width"]="32";r.Headers["X-Height"]="18";r.Headers["X-Fps"]="30";}
        r.ContentLength=bytes.Length;using(var s=r.GetRequestStream())s.Write(bytes,0,bytes.Length);
        using(var response=r.GetResponse())using(var sr=new StreamReader(response.GetResponseStream()))return sr.ReadToEnd();
    }
    static string Post(string path,string data){return Post(path,Encoding.UTF8.GetBytes(data));}
    static void Reject(Action call,int code){try{call();throw new Exception("Request unexpectedly accepted");}catch(WebException e){Check((int)((HttpWebResponse)e.Response).StatusCode==code,"Wrong rejection status");e.Response.Close();}}
    static int Main(){
        using(var receiver=new VideoReceiver(0)){
            int frames=0;receiver.OnDeviceFrameReceived+=(id,b,a,c,t,f,r)=>{Check(id=="web:test","Wrong device");Check(b.Width==32&&b.Height==18,"Wrong dimensions");Interlocked.Increment(ref frames);};receiver.Start();
            using(var bridge=new WebCameraBridge(receiver.BoundPort)){
                bridge.Start();var url=new Uri(bridge.ReceiverUrl);root=url.GetLeftPart(UriPartial.Authority);token=url.Fragment.Substring("#token=".Length);
                using(var wc=new WebClient()){
                    Check(wc.DownloadString(root+"/receiver").Contains("Connect iPhone"),"Embedded receiver missing");
                    Check(wc.ResponseHeaders["Content-Security-Policy"].Contains("wss://lensio-connect.pex-am.workers.dev"),"Discovery blocked by receiver CSP");
                    Check(wc.DownloadString(root+"/app.js").Contains("RTCPeerConnection"),"Embedded JS missing");
                    Check(wc.DownloadString(root+"/auto-connect.js").Contains("class AutoConnect"),"Discovery client missing");
                    Check(wc.DownloadString(root+"/config.json").Contains("https://lensio-connect.pex-am.workers.dev"),"Discovery endpoint missing");
                }
                string catalog="{\"id\":\"web:test\",\"name\":\"Test iPhone\",\"activeId\":\"default\",\"cameras\":[{\"id\":\"default\",\"label\":\"Back camera\",\"selectable\":true}]}";
                Reject(()=>Post("/register",Encoding.UTF8.GetBytes(catalog),false),401);
                Reject(()=>Post("/register",Encoding.UTF8.GetBytes(catalog),true,"https://untrusted.example"),401);
                Reject(()=>Post("/register","{\"id\":\"native:test\",\"cameras\":[]}"),400);
                Post("/register",catalog);Await(()=>receiver.IsConnected);
                Check(bridge.GetCatalog("web:test")!=null,"Catalog missing");
                byte[] jpeg;using(var image=new Bitmap(32,18))using(var ms=new MemoryStream()){using(var g=Graphics.FromImage(image))g.Clear(Color.Coral);image.Save(ms,ImageFormat.Jpeg);jpeg=ms.ToArray();}
                Reject(()=>Post("/frame",new byte[]{1,2,3,4},true,null,true),400);
                for(int i=0;i<8;i++){Post("/frame",jpeg,true,null,true);Thread.Sleep(40);}Await(()=>frames>1);
                receiver.SendControl("web:test","{\"zoom\":2}");Thread.Sleep(100);
                Check(Post("/commands","{\"id\":\"web:test\"}").Contains("zoom"),"Control did not reach browser");
                Check(!Post("/commands","{\"id\":\"web:test\"}").Contains("zoom"),"Command repeated");
                session="test-session-two";Post("/register",catalog);Thread.Sleep(100);
                session="test-session-one";
                Reject(()=>Post("/disconnect","{\"id\":\"web:test\"}"),400);
                Reject(()=>Post("/commands","{\"id\":\"web:test\"}"),400);
                Reject(()=>Post("/frame",jpeg,true,null,true),400);
                Check(bridge.GetCatalog("web:test")!=null,"Old tab closed new session");
                session="test-session-two";Post("/frame",jpeg,true,null,true);
                Post("/disconnect","{\"id\":\"web:test\"}");Await(()=>!receiver.IsConnected);
                Check(bridge.GetCatalog("web:test")==null,"Disconnected catalog retained");
                Reject(()=>Post("/frame",jpeg,true,null,true),400);
                Post("/register",catalog);Await(()=>receiver.IsConnected);
            }
            Await(()=>!receiver.IsConnected);
        }
        Console.WriteLine("PASS: embedded assets, authentication, cross-origin rejection, valid frames, controls, disconnect and shutdown");return 0;
    }
}
