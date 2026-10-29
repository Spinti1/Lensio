using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Web.Script.Serialization;

namespace ApexCam
{
    public sealed class PhoneDeviceInfo
    {
        public string Id, Name, Host;
    }

    // One bounded latest-frame decoder and command channel per phone.
    public sealed class VideoReceiver : IDisposable
    {
        private sealed class Entry { public PhoneDeviceInfo Info; public VideoReceiverCore Core; public long Ticket; }
        private readonly object gate=new object();
        private readonly Dictionary<string,Entry> devices=new Dictionary<string,Entry>();
        private readonly HashSet<TcpClient> pending=new HashSet<TcpClient>();
        private TcpListener listener;
        private volatile bool running;
        private string activeId;
        private long connectionSerial;
        private readonly int port;
        public int BoundPort {get; private set;}
        public string ActiveDeviceId { get {lock(gate)return activeId;} set {lock(gate)activeId=value;} }
        public int CurrentFps {get {lock(gate){Entry e; return activeId!=null && devices.TryGetValue(activeId,out e)?e.Core.CurrentFps:0;}}}
        public bool IsConnected {get {lock(gate)return devices.Count>0;}}
        public event Action<PhoneDeviceInfo> OnDeviceConnected;
        public event Action<string,bool> OnDeviceConnectionStateChanged;
        public event Action<string,Bitmap,int,bool,float,int,int> OnDeviceFrameReceived;
        public event Action<Bitmap,int,bool,float,int,int> OnFrameReceived;
        public event Action<bool> OnConnectionStateChanged;
        public event Action<string,int> OnClientConnected;
        [DllImport("kernel32.dll",SetLastError=true)]
        private static extern bool SetHandleInformation(IntPtr handle,uint mask,uint flags);
        public VideoReceiver(int port=8556) {this.port=port;}
        public void Start() {
            if(running)return;
            listener=new TcpListener(IPAddress.Any,port);
            listener.Server.SetSocketOption(SocketOptionLevel.Socket,SocketOptionName.ReuseAddress,true);
            SetHandleInformation(listener.Server.Handle,1,0);
            listener.Start(8); BoundPort=((IPEndPoint)listener.LocalEndpoint).Port; running=true;
            new Thread(AcceptLoop){IsBackground=true,Name="Lensio-devices"}.Start();
        }
        private void AcceptLoop() {
            while(running) {
                try {
                    var client=listener.AcceptTcpClient();
                    SetHandleInformation(client.Client.Handle,1,0);
                    client.NoDelay=true; client.ReceiveBufferSize=256*1024;
                    client.ReceiveTimeout=15000; client.SendTimeout=2500;
                    client.Client.SetSocketOption(SocketOptionLevel.Socket,SocketOptionName.KeepAlive,true);
                    lock(gate) {
                        if(!running || pending.Count>=8) {client.Close();continue;}
                        pending.Add(client);
                    }
                    long ticket=Interlocked.Increment(ref connectionSerial);
                    ThreadPool.QueueUserWorkItem(_ => Register(client,ticket));
                } catch { if(!running)return; Thread.Sleep(100); }
            }
        }
        private static byte[] ReadExact(Stream stream,int length) {
            var result=new byte[length]; int at=0;
            while(at<length) {int n=stream.Read(result,at,length-at);if(n==0)throw new EndOfStreamException();at+=n;}
            return result;
        }
        private bool Owns(Entry entry) { lock(gate){Entry current;return running && devices.TryGetValue(entry.Info.Id,out current) && object.ReferenceEquals(current,entry);} }
        private void Register(TcpClient client,long ticket) {
            Entry entry=null;
            try {
                var endpoint=(IPEndPoint)client.Client.RemoteEndPoint;
                string host=endpoint.Address.ToString();
                var stream=client.GetStream(); var magic=ReadExact(stream,4);
                string id="legacy:"+host, name="Phone "+host;
                byte[] first=magic;
                if(magic[0]==65 && magic[1]==80 && magic[2]==67 && magic[3]==73) {
                    int length=BitConverter.ToInt32(ReadExact(stream,4),0);
                    if(length<1 || length>4096)throw new InvalidDataException();
                    var json=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(System.Text.Encoding.UTF8.GetString(ReadExact(stream,length)));
                    object value;
                    if(!json.TryGetValue("id",out value) || !(value is string) || ((string)value).Length<1 || ((string)value).Length>128)throw new InvalidDataException();
                    id=(string)value;
                    if(json.TryGetValue("name",out value) && value is string)name=((string)value).Substring(0,Math.Min(80,((string)value).Length));
                    first=null;
                } else if(magic[0]!=65 || magic[1]!=80 || magic[2]!=67 || magic[3]!=77)throw new InvalidDataException();
                entry=new Entry {Info=new PhoneDeviceInfo{Id=id,Name=name,Host=host},Core=new VideoReceiverCore {DeviceLabel=id},Ticket=ticket};
                Entry old=null;
                lock(gate) {
                    pending.Remove(client);
                    if(!running || (!devices.ContainsKey(id) && devices.Count>=4))throw new InvalidOperationException("Four phones maximum");
                    devices.TryGetValue(id,out old);
                    if(old!=null && old.Ticket>ticket)throw new InvalidDataException("Superseded handshake");
                    devices[id]=entry;
                    if(activeId==null)activeId=id;
                }
                if(old!=null)old.Core.Dispose();
                var owner=entry;
                owner.Core.OnFrameReceived += (bmp,battery,charging,temp,fps,rtt) => {
                    if(!Owns(owner))return;
                    var all=OnDeviceFrameReceived;if(all!=null)all(id,bmp,battery,charging,temp,fps,rtt);
                    if(ActiveDeviceId==id) {var selected=OnFrameReceived;if(selected!=null)selected(bmp,battery,charging,temp,fps,rtt);}
                };
                owner.Core.OnConnectionStateChanged += connected => {
                    if(connected)return;
                    bool removed=false, any=false;
                    lock(gate) {
                        Entry current;
                        if(devices.TryGetValue(id,out current) && object.ReferenceEquals(current,owner)) {
                            devices.Remove(id);removed=true;
                            // Keep control ownership pinned to the selected phone, even while it is offline.
                        }
                        any=devices.Count>0;
                    }
                    owner.Core.Stop();
                    if(removed) {
                        var change=OnDeviceConnectionStateChanged;if(change!=null)change(id,false);
                        var state=OnConnectionStateChanged;if(state!=null)state(any);
                    }
                };
                owner.Core.Adopt(client,first);
                var added=OnDeviceConnected;if(added!=null)added(owner.Info);
                var connectedEvent=OnDeviceConnectionStateChanged;if(connectedEvent!=null)connectedEvent(id,true);
                var legacy=OnClientConnected;if(legacy!=null)legacy(host,endpoint.Port);
                var connection=OnConnectionStateChanged;if(connection!=null)connection(true);
            } catch {
                lock(gate)pending.Remove(client);
                try{client.Close();}catch{}
                if(entry!=null) {
                    lock(gate) {
                        Entry registered;
                        if(devices.TryGetValue(entry.Info.Id,out registered) && object.ReferenceEquals(entry,registered))
                            devices.Remove(entry.Info.Id);
                    }
                    entry.Core.Dispose();
                }
            }
        }
        public void SendControl(string payload) {SendControl(ActiveDeviceId,payload);}
        public void SendControl(string id,string payload) {
            Entry entry;
            lock(gate){if(id==null || !devices.TryGetValue(id,out entry))return;}
            entry.Core.SendControl(payload);
        }
        public void SendControlToAll(string payload) {
            string[] ids;lock(gate){ids=new string[devices.Count];devices.Keys.CopyTo(ids,0);}
            foreach(string id in ids)SendControl(id,payload);
        }
        public void Stop() {
            running=false;try{if(listener!=null)listener.Stop();}catch{}
            Entry[] entries;
            lock(gate){
                foreach(var client in pending)try{client.Close();}catch{}
                pending.Clear();
                entries=new Entry[devices.Count];devices.Values.CopyTo(entries,0);
                devices.Clear();activeId=null;
            }
            foreach(var entry in entries)entry.Core.Dispose();
        }
        public void Dispose(){Stop();}
    }
}
