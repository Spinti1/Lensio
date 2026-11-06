using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
namespace ApexCam {
 internal sealed class AvcVideoDecoder:IDisposable {
  [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern IntPtr LoadLibraryEx(string path,IntPtr file,uint flags);
  [DllImport("kernel32.dll",CharSet=CharSet.Ansi)]static extern IntPtr GetProcAddress(IntPtr module,string name);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)]delegate int Create(int w,int h,int gpu,out IntPtr instance);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)]delegate int Push(IntPtr instance,byte[] bytes,int length,long time);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)]delegate int Pull(IntPtr instance,IntPtr output,int capacity,int rotation,out uint w,out uint h);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)]delegate int Hardware(IntPtr instance);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)]delegate void Close(IntPtr instance);
  static readonly object gate=new object();static IntPtr module;static Create create;static Push push;static Pull pull;static Hardware hardware;static Close close;
  int inputsWithoutOutput;IntPtr handle,output;const int Capacity=1920*1080*4;
  public bool IsHardware {get{return hardware(handle)!=0;}}
  public static bool Available {get{try{EnsureLoaded();return true;}catch{return false;}}}
  static T Bind<T>(string name){IntPtr symbol=GetProcAddress(module,name);if(symbol==IntPtr.Zero)throw new InvalidDataException("Missing video codec entry point");return (T)(object)Marshal.GetDelegateForFunctionPointer(symbol,typeof(T));}
  static void EnsureLoaded(){lock(gate){if(module!=IntPtr.Zero)return;
   byte[] data;using(var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("LensioRuntime.lensio-codec64.dll")){if(stream==null)throw new NotSupportedException("AVC runtime unavailable");using(var bytes=new MemoryStream()){stream.CopyTo(bytes);data=bytes.ToArray();}}
   string hash;using(var sha=SHA256.Create())hash=BitConverter.ToString(sha.ComputeHash(data)).Replace("-","");
   string dir=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"LensioStudio","runtime","codec",hash);
   BundledSetup.RejectReparsePath(dir);Directory.CreateDirectory(dir);string path=Path.Combine(dir,"lensio-codec64.dll");BundledSetup.RejectReparsePath(path);
   if(!File.Exists(path))try{using(var file=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None))file.Write(data,0,data.Length);}catch(IOException){if(!File.Exists(path))throw;}
   using(var file=File.OpenRead(path))using(var sha=SHA256.Create())if(file.Length!=data.Length||BitConverter.ToString(sha.ComputeHash(file)).Replace("-","")!=hash)throw new InvalidDataException("Video codec integrity check failed");
   module=LoadLibraryEx(path,IntPtr.Zero,0x1100);if(module==IntPtr.Zero)throw new NotSupportedException("Windows AVC decoding is unavailable");
   create=Bind<Create>("LensioAvcCreate");push=Bind<Push>("LensioAvcPush");pull=Bind<Pull>("LensioAvcPull");hardware=Bind<Hardware>("LensioAvcHardware");close=Bind<Close>("LensioAvcClose");
  }}
  public AvcVideoDecoder(int width,int height,bool useHardware){EnsureLoaded();int result=create(width,height,useHardware?1:0,out handle);if(result<0)Marshal.ThrowExceptionForHR(result);output=Marshal.AllocHGlobal(Capacity);}
  public void Decode(byte[] bytes,long timestamp,int rotation,Action<Bitmap> frame){
   int result=push(handle,bytes,bytes.Length,timestamp);
   if(result==unchecked((int)0xC00D36B0)){Drain(rotation,frame);result=push(handle,bytes,bytes.Length,timestamp);}
   if(result<0)Marshal.ThrowExceptionForHR(result);inputsWithoutOutput++;Drain(rotation,frame);
   if(inputsWithoutOutput>15)throw new InvalidDataException("H.264 decoder did not produce frames");
  }
  void Drain(int rotation,Action<Bitmap> frame){for(int i=0;i<32;i++){
   uint w,h;int result=pull(handle,output,Capacity,rotation,out w,out h);if(result==1)return;if(result<0)Marshal.ThrowExceptionForHR(result);
   if(w<2||h<2||(long)w*h>1920*1080)throw new InvalidDataException("Invalid decoded dimensions");
   // Source bytes are already rotated in the native YUV-to-RGB pass.
   inputsWithoutOutput=0;
   using(var view=new Bitmap((int)w,(int)h,(int)w*4,PixelFormat.Format32bppArgb,output))frame(view);
  }throw new InvalidDataException("Decoder produced excessive output");}
  public void Dispose(){if(handle!=IntPtr.Zero){close(handle);handle=IntPtr.Zero;}if(output!=IntPtr.Zero){Marshal.FreeHGlobal(output);output=IntPtr.Zero;}}
 }
}
