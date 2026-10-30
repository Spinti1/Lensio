using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Runtime.InteropServices;
namespace ApexCam {
 public sealed class DiscoveryIdentityServer : IDisposable {
  [DllImport("kernel32.dll",SetLastError=true)]private static extern bool SetHandleInformation(IntPtr handle,uint mask,uint flags);
  private readonly int port;private TcpListener listener;private volatile bool running;private int activeClients;
  public int BoundPort {get;private set;}
  public DiscoveryIdentityServer(int port=8557){this.port=port;}
  public void Start(){listener=new TcpListener(IPAddress.Any,port);SetHandleInformation(listener.Server.Handle,1,0);listener.Start(8);BoundPort=((IPEndPoint)listener.LocalEndpoint).Port;running=true;ThreadPool.QueueUserWorkItem(_=>Accept());}
  private void Accept(){while(running){try{var client=listener.AcceptTcpClient();SetHandleInformation(client.Client.Handle,1,0);if(Interlocked.Increment(ref activeClients)>8){Interlocked.Decrement(ref activeClients);client.Close();continue;}ThreadPool.QueueUserWorkItem(_=>{try{Serve(client);}catch{}finally{client.Close();Interlocked.Decrement(ref activeClients);}});}catch{if(!running)return;}}}
  private static string ReadBoundedLine(TextReader reader){var b=new StringBuilder();int c;while((c=reader.Read())!=-1){if(c=='\n')return b.ToString().TrimEnd('\r');if(b.Length>=4096)throw new InvalidDataException("HTTP line too long");b.Append((char)c);}return b.Length==0?null:b.ToString();}
  private static void Serve(TcpClient client){
   client.ReceiveTimeout=2000;client.SendTimeout=2000;
   using(var stream=client.GetStream())using(var reader=new StreamReader(stream,Encoding.ASCII,false,1024,true)){
    string first=ReadBoundedLine(reader);if(first==null)return;var parts=first.Split(' ');if(parts.Length!=3)return;
    int total=0;string line;while(!string.IsNullOrEmpty(line=ReadBoundedLine(reader))){total+=line.Length;if(total>8192)return;}
    bool ok=(parts[0]=="GET"||parts[0]=="HEAD")&&parts[1]=="/api/identity";
    string body=ok?"{\"application\":\"LensioStudio\",\"streamPort\":8556}":"Not found";
    byte[] payload=Encoding.UTF8.GetBytes(body);string headers="HTTP/1.1 "+(ok?"200 OK":"404 Not Found")+"\r\nContent-Type: "+(ok?"application/json":"text/plain")+"\r\nContent-Length: "+payload.Length+"\r\nConnection: close\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\n\r\n";
    byte[] head=Encoding.ASCII.GetBytes(headers);stream.Write(head,0,head.Length);if(parts[0]!="HEAD")stream.Write(payload,0,payload.Length);
   }
  }
  public void Dispose(){running=false;if(listener!=null)listener.Stop();}
 }
}
