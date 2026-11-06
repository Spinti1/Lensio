using System;
using System.Diagnostics;
using System.Threading;
class FramePumpSmoke {
 static int Main(){int count=0;var type=typeof(ApexCam.MainForm).Assembly.GetType("ApexCam.FramePump");var watch=Stopwatch.StartNew();
  using(var pump=(IDisposable)Activator.CreateInstance(type,new object[]{30,(Action)(()=>Interlocked.Increment(ref count))})){Thread.Sleep(4000);}
  double fps=count*1000.0/watch.ElapsedMilliseconds;int stopped=count;Thread.Sleep(100);if(count!=stopped||fps<28||fps>32)throw new Exception("Frame pacing incorrect: "+fps);Console.WriteLine("PASS paced scene worker "+fps.ToString("0.00")+" FPS and prompt shutdown");return 0;
 }
}
