using System;
using System.IO;
using System.Media;
using System.Threading;
using ApexCam;
class AudioCaptureSmoke {
 static MemoryStream Tone(){var s=new MemoryStream();var w=new BinaryWriter(s);int size=48000*4;
  w.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));w.Write(36+size);w.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt "));w.Write(16);w.Write((short)1);w.Write((short)2);w.Write(48000);w.Write(192000);w.Write((short)4);w.Write((short)16);w.Write(System.Text.Encoding.ASCII.GetBytes("data"));w.Write(size);
  for(int i=0;i<48000;i++){short sample=(short)(400*Math.Sin(i*2*Math.PI*600/48000));w.Write(sample);w.Write(sample);}w.Flush();s.Position=0;return s;
 }
 static int Main(){using(var tone=Tone())using(var player=new SoundPlayer(tone)){
  player.PlayLooping();try{
   for(int round=0;round<6;round++)using(var capture=new WasapiCapture()){
    int packets=0,peak=0;capture.OnAudioDataAvailable+=(bytes,length)=>{if(length!=960)throw new Exception("Audio packet changed size");Interlocked.Increment(ref packets);for(int i=0;i<length;i+=2)peak=Math.Max(peak,Math.Abs((int)BitConverter.ToInt16(bytes,i)));};
    capture.Start();Thread.Sleep(round==0?4000:700);
    if(!capture.IsCapturing)throw new Exception("Capture unavailable: "+capture.LastError);
    capture.Stop();if(capture.IsCapturing)throw new Exception("Audio worker survived Stop");
    if(packets<30||peak<10)throw new Exception("Missing captured test tone: packets="+packets+" peak="+peak);
    if(capture.SampleRate!=48000||capture.Channels!=2)throw new Exception("Transport PCM format mismatch");
    Console.WriteLine("PASS loopback start/stop "+round+": packets="+packets+" peak="+peak+" format=48000Hz/stereo/PCM16");
   }
  }finally{player.Stop();}
 }return 0;}
}
