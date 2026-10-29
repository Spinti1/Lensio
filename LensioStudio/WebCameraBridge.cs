using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace ApexCam
{
    // A browser terminates WebRTC locally. Only authenticated loopback requests can feed
    // frames into the existing bounded TCP decoder; no LAN HTTP endpoint is exposed.
    public sealed class WebCameraBridge : IDisposable
    {
        private sealed class Device : IDisposable {
            public string Id,Session; public TcpClient Client; public Dictionary<string,object> Catalog;
            public readonly Queue<object> Commands=new Queue<object>();
            public readonly object Gate=new object(); public DateTime LastFrame=DateTime.UtcNow;
            public void Dispose(){try{Client.Close();}catch{}}
        }
        private readonly object gate=new object();
        private readonly Dictionary<string,Device> devices=new Dictionary<string,Device>();
        private readonly HashSet<TcpClient> requests=new HashSet<TcpClient>();
        private TcpListener listener; private volatile bool running;
        private readonly int videoPort; private readonly string token,pairingRoom,hostProof; private int port;
        private System.Threading.Timer watchdog;
        [DllImport("kernel32.dll",SetLastError=true)] private static extern bool SetHandleInformation(IntPtr h,uint mask,uint flags);
        public WebCameraBridge(int videoPort,string pairingDirectory=null) {
            this.videoPort=videoPort;
            var bytes=new byte[32];using(var rng=RandomNumberGenerator.Create())rng.GetBytes(bytes);
            token=BitConverter.ToString(bytes).Replace("-","").ToLowerInvariant();
            string stored=null;
            if(pairingDirectory!=null){try{string path=Path.Combine(pairingDirectory,"web-pairing.key");if(File.Exists(path)){string text=File.ReadAllText(path).Trim();if(System.Text.RegularExpressions.Regex.IsMatch(text,"^[a-f0-9]{64}$"))stored=text;}if(stored==null){rngFill(bytes);stored=BitConverter.ToString(bytes).Replace("-","").ToLowerInvariant();Directory.CreateDirectory(pairingDirectory);File.WriteAllText(path,stored);}}catch{}}
            if(stored==null){rngFill(bytes);stored=BitConverter.ToString(bytes).Replace("-","").ToLowerInvariant();}
            hostProof=stored;
            using(var sha=SHA256.Create())pairingRoom=BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(hostProof))).Replace("-","").ToLowerInvariant();
        }
        private static void rngFill(byte[] bytes){using(var rng=RandomNumberGenerator.Create())rng.GetBytes(bytes);}
        public string ReceiverUrl {get{return "http://127.0.0.1:"+port+"/receiver#token="+token;}}
        public string PairingLink {get{return "https://spinti1.github.io/Lensio/?v=6#room="+pairingRoom;}}
        public string EngineUrl {get{return "http://127.0.0.1:"+port+"/engine#token="+token+"&room="+pairingRoom+"&host="+hostProof;}}
        public void Start(){
            if(running)return;
            listener=new TcpListener(IPAddress.Loopback,0);SetHandleInformation(listener.Server.Handle,1,0);
            listener.Start(8);port=((IPEndPoint)listener.LocalEndpoint).Port;running=true;
            new Thread(Accept){IsBackground=true,Name="Lensio-web-loopback"}.Start();
            watchdog=new System.Threading.Timer(_=>Expire(),null,5000,5000);
        }
        public Dictionary<string,object> GetCatalog(string id) {
            lock(gate){Device d;return id!=null && devices.TryGetValue(id,out d)?d.Catalog:null;}
        }
        private void Accept(){while(running)try{
            var c=listener.AcceptTcpClient();SetHandleInformation(c.Client.Handle,1,0);c.NoDelay=true;c.ReceiveTimeout=5000;c.SendTimeout=5000;
            lock(gate){if(!running || requests.Count>=8){c.Close();continue;}requests.Add(c);}
            ThreadPool.QueueUserWorkItem(_=>Handle(c));
        }catch{if(!running)return;}}
        private static string Line(Stream s,int max){
            var b=new List<byte>();int c;while((c=s.ReadByte())!=-1){if(c==10)return Encoding.ASCII.GetString(b.ToArray()).TrimEnd('\r');if(b.Count>=max)throw new InvalidDataException();b.Add((byte)c);}throw new EndOfStreamException();
        }
        private static byte[] Exact(Stream s,int length){var b=new byte[length];int at=0;while(at<length){int n=s.Read(b,at,length-at);if(n==0)throw new EndOfStreamException();at+=n;}return b;}
        private static JavaScriptSerializer Json(){return new JavaScriptSerializer{MaxJsonLength=32768,RecursionLimit=12};}
        private static string Text(Dictionary<string,object> d,string key){object v;return d.TryGetValue(key,out v)&&v is string?(string)v:"";}
        private static int HeaderInt(Dictionary<string,string> h,string key,int min,int max){string s;int v;if(!h.TryGetValue(key,out s)||!int.TryParse(s,out v)||v<min||v>max)throw new InvalidDataException();return v;}
        private void Reply(Stream s,int status,string type,byte[] body){
            string head="HTTP/1.1 "+status+" "+(status==200?"OK":status==204?"No Content":status==401?"Unauthorized":"Bad Request")+"\r\nContent-Type: "+type+"\r\nContent-Length: "+body.Length+"\r\nConnection: close\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nReferrer-Policy: no-referrer\r\nContent-Security-Policy: default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; media-src 'self' blob:; connect-src 'self' wss://lensio-connect.pex-am.workers.dev; worker-src 'self'; object-src 'none'; base-uri 'none'; frame-ancestors 'none'; form-action 'none'\r\n\r\n";
            var bytes=Encoding.ASCII.GetBytes(head);s.Write(bytes,0,bytes.Length);if(body.Length>0)s.Write(body,0,body.Length);
        }
        private void Handle(TcpClient c){
            try{var s=c.GetStream();string[] first=Line(s,2048).Split(' ');if(first.Length!=3)throw new InvalidDataException();
                string method=first[0],path=first[1].Split('?')[0];var h=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);int total=0;
                for(int i=0;i<40;i++){string line=Line(s,2048);total+=line.Length;if(total>8192)throw new InvalidDataException();if(line.Length==0)break;int colon=line.IndexOf(':');if(colon<1 || i==39)throw new InvalidDataException();h[line.Substring(0,colon)]=line.Substring(colon+1).Trim();}
                string host;if(!h.TryGetValue("Host",out host)||host!="127.0.0.1:"+port)throw new InvalidDataException();
                if(method=="GET"){
                    string asset=path=="/receiver"?"index.html":path=="/engine"?"engine.html":path.TrimStart('/'),type="";
                    switch(asset){case "engine.html":case "index.html":type="text/html; charset=utf-8";break;case "config.json":type="application/json";break;case "qrcode.js":case "track-receiver.js":case "native-receiver.js":case "auto-connect.js":case "app.js":case "protocol.js":type="text/javascript; charset=utf-8";break;case "style.css":type="text/css";break;case "manifest.webmanifest":type="application/manifest+json";break;case "icon.svg":type="image/svg+xml";break;default:throw new InvalidDataException();}
                    using(var resource=Assembly.GetExecutingAssembly().GetManifestResourceStream("LensioWeb."+asset)){
                        if(resource==null)throw new InvalidDataException();using(var ms=new MemoryStream()){resource.CopyTo(ms);Reply(s,200,type,ms.ToArray());}}
                    return;
                }
                string auth,origin;
                if(method!="POST" || !h.TryGetValue("Authorization",out auth) || auth!="Bearer "+token || (h.TryGetValue("Origin",out origin) && origin!="http://127.0.0.1:"+port)) {Reply(s,401,"text/plain",new byte[0]);return;}
                if(h.ContainsKey("Transfer-Encoding"))throw new InvalidDataException();
                string session;if(!h.TryGetValue("X-Session",out session)||session.Length<8||session.Length>64)throw new InvalidDataException();
                int length=HeaderInt(h,"Content-Length",1,path=="/frame"?10*1024*1024:32768);
                byte[] body=Exact(s,length);
                if(path=="/frame"){
                    string id;if(!h.TryGetValue("X-Device",out id))throw new InvalidDataException();
                    var d=Find(id,session);int width=HeaderInt(h,"X-Width",1,4096),height=HeaderInt(h,"X-Height",1,4096),fps=HeaderInt(h,"X-Fps",1,60);
                    if(body.Length<4||body[0]!=255||body[1]!=216||body[body.Length-2]!=255||body[body.Length-1]!=217)throw new InvalidDataException();
                    lock(d.Gate){
                        // A stale browser tab must not silently reconnect after its native decoder expired.
                        using(var w=new BinaryWriter(d.Client.GetStream(),Encoding.UTF8,true)){
                            w.Write(new byte[]{65,80,67,77});w.Write(width);w.Write(height);w.Write(body.Length);w.Write(fps);w.Write(0);w.Write(0);w.Write(0);w.Write(body);}
                        d.LastFrame=DateTime.UtcNow;
                    }
                    Reply(s,204,"text/plain",new byte[0]);return;
                }
                var values=Json().Deserialize<Dictionary<string,object>>(Encoding.UTF8.GetString(body));
                string deviceId=Text(values,"id");if(deviceId.Length<5||deviceId.Length>100||!deviceId.StartsWith("web:"))throw new InvalidDataException();
                if(path=="/register")Register(deviceId,values,session);
                else if(path=="/catalog"){var d=Find(deviceId,session);lock(gate)d.Catalog=ValidateCatalog(values);}
                else if(path=="/disconnect")Remove(deviceId,Find(deviceId,session));
                else if(path=="/commands"){
                    var d=Find(deviceId,session);object[] commands;lock(d.Commands){commands=d.Commands.ToArray();d.Commands.Clear();}
                    Reply(s,200,"application/json",Encoding.UTF8.GetBytes(Json().Serialize(new{commands=commands})));return;
                }else throw new InvalidDataException();
                Reply(s,204,"text/plain",new byte[0]);
            }catch{try{Reply(c.GetStream(),400,"text/plain",Encoding.UTF8.GetBytes("Receiver unavailable or invalid request. Pair again from LensioStudio."));}catch{}}
            finally{lock(gate)requests.Remove(c);c.Close();}
        }
        private static Dictionary<string,object> ValidateCatalog(Dictionary<string,object> value){
            object cameras;if(!value.TryGetValue("cameras",out cameras)||!(cameras is System.Collections.IList)||((System.Collections.IList)cameras).Count>32)throw new InvalidDataException();return value;
        }
        private Device Find(string id,string session){lock(gate){Device d;if(!devices.TryGetValue(id,out d)||d.Session!=session)throw new InvalidDataException();return d;}}
        private void Register(string id,Dictionary<string,object> catalog,string session){
            ValidateCatalog(catalog);Remove(id,null);
            var client=new TcpClient();SetHandleInformation(client.Client.Handle,1,0);client.NoDelay=true;client.SendTimeout=3000;client.Connect(IPAddress.Loopback,videoPort);
            var d=new Device{Id=id,Session=session,Client=client,Catalog=catalog};
            try{
                lock(gate){if(!running||devices.Count>=4)throw new InvalidOperationException();devices.Add(id,d);}
                var hello=Encoding.UTF8.GetBytes(Json().Serialize(new{id=id,name=Text(catalog,"name")}));
                using(var w=new BinaryWriter(client.GetStream(),Encoding.UTF8,true)){w.Write(new byte[]{65,80,67,73});w.Write(hello.Length);w.Write(hello);}
                new Thread(()=>ReadCommands(d)){IsBackground=true,Name="Lensio-web-controls"}.Start();
            }catch{Remove(id,d);d.Dispose();throw;}
        }
        private void ReadCommands(Device d){try{
            using(var reader=new StreamReader(d.Client.GetStream(),Encoding.UTF8,false,1024,true)){
                while(running){var text=new StringBuilder();int c;while((c=reader.Read())!=-1&&c!=10){if(text.Length>=32768)throw new InvalidDataException();text.Append((char)c);}if(c==-1)break;
                    object command=Json().DeserializeObject(text.ToString());lock(d.Commands){if(d.Commands.Count>=32)d.Commands.Dequeue();d.Commands.Enqueue(command);}
                }
            }
        }catch{}finally{Remove(d.Id,d);}}
        private void Remove(string id,Device expected){Device d;lock(gate){if(!devices.TryGetValue(id,out d)||(expected!=null&&!object.ReferenceEquals(d,expected)))return;devices.Remove(id);}d.Dispose();}
        private void Expire(){Device[] copy;lock(gate){copy=new List<Device>(devices.Values).ToArray();}foreach(var d in copy)if((DateTime.UtcNow-d.LastFrame).TotalSeconds>20)Remove(d.Id,d);}
        public void Dispose(){running=false;if(watchdog!=null)watchdog.Dispose();if(listener!=null)listener.Stop();Device[] copy;TcpClient[] sockets;lock(gate){copy=new List<Device>(devices.Values).ToArray();devices.Clear();sockets=new List<TcpClient>(requests).ToArray();requests.Clear();}foreach(var d in copy)d.Dispose();foreach(var s in sockets)try{s.Close();}catch{}}
    }
}
