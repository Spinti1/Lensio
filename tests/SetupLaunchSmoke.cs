using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using ApexCam;
class SetupLaunchSmoke {
 delegate bool EnumWindow(IntPtr hwnd,IntPtr value);
 [DllImport("user32.dll")]static extern bool EnumWindows(EnumWindow callback,IntPtr value);
 [DllImport("user32.dll")]static extern bool EnumChildWindows(IntPtr window,EnumWindow callback,IntPtr value);
 [DllImport("user32.dll")]static extern uint GetWindowThreadProcessId(IntPtr window,out uint process);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern int GetWindowText(IntPtr window,StringBuilder text,int count);
 [DllImport("user32.dll")]static extern bool IsWindowEnabled(IntPtr window);
 [DllImport("user32.dll")]static extern bool PostMessage(IntPtr window,uint message,IntPtr w,IntPtr l);
 static string Text(IntPtr window){var text=new StringBuilder(1024);GetWindowText(window,text,text.Capacity);return text.ToString();}
 static string Hash(string file){using(var stream=File.OpenRead(file))using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-","");}
 static int Main(string[] args){
  string root=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"test-data","setup-launch-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
  string source=typeof(MainForm).Assembly.Location,setup=Path.Combine(root,"LensioStudio.exe"),target=Path.Combine(root,"install");Directory.CreateDirectory(target);File.Copy(source,setup);
  int parent;using(var exited=Process.Start(new ProcessStartInfo{FileName=Path.Combine(Environment.SystemDirectory,"cmd.exe"),Arguments="/c exit 0",UseShellExecute=false,CreateNoWindow=true})){parent=exited.Id;exited.WaitForExit();}
  using(var process=Process.Start(new ProcessStartInfo{FileName=setup,Arguments="--setup "+UpdateSetup.QuoteArgument(target)+" "+parent+" "+Hash(setup),WorkingDirectory=root,UseShellExecute=false})){
   try{bool ready=false;string seen="";IntPtr main=IntPtr.Zero;var watch=Stopwatch.StartNew();
    while(watch.ElapsedMilliseconds<12000&&!process.HasExited){
     EnumWindows((window,value)=>{uint id;GetWindowThreadProcessId(window,out id);if(id!=(uint)process.Id)return true;
      if(Text(window)=="LensioStudio Setup"){main=window;EnumChildWindows(window,(child,unused)=>{string text=Text(child);seen+=text+" | ";if(text=="Install and open"&&IsWindowEnabled(child))ready=true;return true;},IntPtr.Zero);}return true;},IntPtr.Zero);
     if(ready)break;Thread.Sleep(100);
    }
    if(!ready)throw new Exception("Setup did not enable verified installation: "+seen);
    if(seen.Contains("without administrator rights"))throw new Exception("Administrator rejection remains");
    if(main!=IntPtr.Zero)PostMessage(main,0x10,IntPtr.Zero,IntPtr.Zero);
    if(!process.WaitForExit(4000))throw new Exception("Setup did not close");
    bool elevated=new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
    string result="PASS real Setup launch (administrator="+elevated+"): verified Install and open becomes enabled; no permissions rejection";
    Console.WriteLine(result);if(args.Length>0)File.WriteAllText(args[0],result);return 0;
   }finally{if(!process.HasExited)process.Kill();}
  }
 }
}
