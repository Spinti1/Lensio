using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using ApexCam;
class AvcReceiverSmoke {
 static void Check(bool b,string s){if(!b)throw new Exception(s);}
 static List<Tuple<byte[],bool>> Load(string path){var packets=new List<Tuple<byte[],bool>>();using(var reader=new BinaryReader(File.OpenRead(path)))while(reader.BaseStream.Position<reader.BaseStream.Length){int length=reader.ReadInt32();bool key=reader.ReadInt32()!=0;packets.Add(Tuple.Create(reader.ReadBytes(length),key));}return packets;}
 static TcpClient Connect(VideoReceiver receiver,string id){var client=new TcpClient("127.0.0.1",receiver.BoundPort);client.ReceiveTimeout=5000;var body=Encoding.UTF8.GetBytes("{\"id\":\""+id+"\",\"name\":\"AVC test\",\"videoProtocol\":1,\"codecs\":[\"h264\",\"jpeg\"]}");var w=new BinaryWriter(client.GetStream(),Encoding.UTF8,true);w.Write(new byte[]{65,80,67,73});w.Write(body.Length);w.Write(body);Check(new StreamReader(client.GetStream(),Encoding.UTF8,false,1024,true).ReadLine().Contains("h264"),"AVC was not negotiated");return client;}
 static void Packet(TcpClient client,byte[] bytes,bool key,int frame,int rotation,int width=1280,int height=720){using(var w=new BinaryWriter(client.GetStream(),Encoding.UTF8,true)){w.Write(new byte[]{76,72,50,52});w.Write(width);w.Write(height);w.Write(bytes.Length);w.Write(30);w.Write(74|0x10000);w.Write(290);w.Write(0x01000000|(rotation<<8)|(key?1:0));w.Write(frame*333333L);w.Write(bytes);}}
 static byte[] Jpeg(){using(var bitmap=new Bitmap(16,9))using(var ms=new MemoryStream()){using(var g=Graphics.FromImage(bitmap))g.Clear(Color.Blue);bitmap.Save(ms,ImageFormat.Jpeg);return ms.ToArray();}}
 static void SendJpeg(TcpClient client){byte[] bytes=Jpeg();using(var w=new BinaryWriter(client.GetStream(),Encoding.UTF8,true)){w.Write(new byte[]{65,80,67,77});w.Write(16);w.Write(9);w.Write(bytes.Length);w.Write(30);w.Write(74);w.Write(290);w.Write(0);w.Write(bytes);}}
 static int Main(string[] args){
  var units=Load(args[0]);int total=0;Exception failure=null;
  using(var receiver=new VideoReceiver(0)){
   int expectedRotation=0;var arrived=new AutoResetEvent(false);
   receiver.OnDeviceFrameReceived+=(id,b,battery,charging,temp,fps,rtt)=>{try{if(id=="bad")return;Check(b.Width==(expectedRotation==90?720:1280)&&b.Height==(expectedRotation==90?1280:720),"Rotation lost over network");Check(battery==74&&charging&&rtt==0,"AVC telemetry corrupted");Interlocked.Increment(ref total);arrived.Set();}catch(Exception e){failure=e;}};
   receiver.Start();
   foreach(int rotation in new[]{0,90})using(var client=Connect(receiver,"phone")){
    expectedRotation=rotation;int first=total,index=0;var sent=new ManualResetEvent(false);var pumpType=typeof(MainForm).Assembly.GetType("ApexCam.FramePump");
    using(var pump=(IDisposable)Activator.CreateInstance(pumpType,new object[]{30,(Action)(()=>{if(index>=units.Count){sent.Set();return;}var unit=units[index];Packet(client,unit.Item1,unit.Item2,index++,rotation);})})){Check(sent.WaitOne(5000),"Paced sender stalled");}
    int tries=0;while(total-first<58&&tries++<10)arrived.WaitOne(100);
    Check(failure==null,failure==null?"":failure.Message);Check(total-first>=58,"AVC network lost frames: "+(total-first));Console.WriteLine("PASS AVC TCP capture/reconnect/rotation "+rotation+" frames="+(total-first));
   }
   // Missing sync frame requests a keyframe instead of decoding damaged references.
   using(var client=Connect(receiver,"sync")){Packet(client,units[1].Item1,false,0,0);string command=new StreamReader(client.GetStream(),Encoding.UTF8,false,1024,true).ReadLine();Check(command.Contains("videoKeyframe"),"Missing keyframe was not requested");}
   // Invalid geometry must close the peer before allocating a payload/decoder.
   using(var client=Connect(receiver,"bad")){Packet(client,new byte[]{0},true,0,0,8192,8192);bool closed=false;try{closed=client.GetStream().ReadByte()==-1;}catch(IOException e){var socket=e.InnerException as SocketException;closed=socket!=null&&(socket.SocketErrorCode==SocketError.ConnectionAborted||socket.SocketErrorCode==SocketError.ConnectionReset);}Check(closed,"Oversized AVC accepted");}
   // Malformed AVC eventually requests JPEG without killing the receiver.
   using(var client=Connect(receiver,"bad")){for(int i=0;i<20;i++){Packet(client,new byte[]{0,0,0,1,0x65,0},true,i,0);Thread.Sleep(5);}string command=new StreamReader(client.GetStream(),Encoding.UTF8,false,1024,true).ReadLine();Check(command.Contains("jpeg"),"Invalid AVC did not fall back to JPEG");SendJpeg(client);}
  }
  Console.WriteLine("PASS negotiation, bounded decoding, telemetry, malformed packet rejection and JPEG fallback");return 0;
 }
}
