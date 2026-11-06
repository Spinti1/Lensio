using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;
namespace ApexCam {
 // One dedicated paced worker; overruns skip deadlines rather than queueing scene renders.
 internal sealed class FramePump:IDisposable {
  [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern IntPtr CreateWaitableTimerEx(IntPtr attributes,string name,uint flags,uint access);
  [DllImport("kernel32.dll",SetLastError=true)]static extern bool SetWaitableTimer(IntPtr timer,ref long due,int period,IntPtr callback,IntPtr arg,bool resume);
  [DllImport("winmm.dll")]static extern uint timeBeginPeriod(uint period);
  [DllImport("winmm.dll")]static extern uint timeEndPeriod(uint period);
  readonly ManualResetEvent stop=new ManualResetEvent(false);readonly Thread thread;readonly int fps;readonly Action tick;
  public FramePump(int fps,Action tick){this.fps=fps;this.tick=tick;thread=new Thread(Run){IsBackground=true,Name="Lensio-scene-30fps"};thread.Start();}
  void Run(){bool raised=false;EventWaitHandle timer=null;
   try{
    IntPtr handle=CreateWaitableTimerEx(IntPtr.Zero,null,2,0x1F0003);
    if(handle==IntPtr.Zero){raised=timeBeginPeriod(1)==0;handle=CreateWaitableTimerEx(IntPtr.Zero,null,0,0x1F0003);}
    if(handle==IntPtr.Zero)throw new InvalidOperationException("Frame timer unavailable");
    timer=new EventWaitHandle(false,EventResetMode.AutoReset);timer.SafeWaitHandle=new SafeWaitHandle(handle,true);
    var waits=new WaitHandle[]{stop,timer};double interval=Stopwatch.Frequency/(double)fps,next=Stopwatch.GetTimestamp();
    while(!stop.WaitOne(0)){
     double remaining=next-Stopwatch.GetTimestamp();
     if(remaining>0){long due=-Math.Max(1,(long)(remaining*10000000/Stopwatch.Frequency));if(!SetWaitableTimer(handle,ref due,0,IntPtr.Zero,IntPtr.Zero,false))break;if(WaitHandle.WaitAny(waits)==0)break;}
     try{tick();}catch{}
     next+=interval;long now=Stopwatch.GetTimestamp();if(next<now-interval)next=now+interval;
    }
   }catch(Exception){while(!stop.WaitOne(33)){try{tick();}catch{}}}
   finally{if(timer!=null)timer.Dispose();if(raised)timeEndPeriod(1);}
  }
  public void Dispose(){stop.Set();if(Thread.CurrentThread!=thread&&thread.Join(2000))stop.Dispose();}
 }
}
