using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;
using ApexCam;
class PixelKernelSmoke {
 static readonly Type Scaler=typeof(MainForm).Assembly.GetType("ApexCam.CameraFrameScaler");
 static readonly MethodInfo Scale=Scaler.GetMethod("Scale");
 static readonly MethodInfo Convert=typeof(VirtualCameraBridge).GetMethod("ConvertBitmapToNv12",BindingFlags.NonPublic|BindingFlags.Instance);
 static byte[] Pixels(Bitmap b){var d=b.LockBits(new Rectangle(0,0,b.Width,b.Height),ImageLockMode.ReadOnly,b.PixelFormat);try{var p=new byte[b.Width*b.Height*4];for(int y=0;y<b.Height;y++)Marshal.Copy(IntPtr.Add(d.Scan0,y*d.Stride),p,y*b.Width*4,b.Width*4);return p;}finally{b.UnlockBits(d);}}
 static Bitmap Input(int w,int h){var b=new Bitmap(w,h,PixelFormat.Format32bppArgb);var r=new Random(w*97+h);var p=new byte[w*h*4];r.NextBytes(p);for(int i=3;i<p.Length;i+=4)p[i]=255;var d=b.LockBits(new Rectangle(0,0,w,h),ImageLockMode.WriteOnly,b.PixelFormat);try{for(int y=0;y<h;y++)Marshal.Copy(p,y*w*4,IntPtr.Add(d.Scan0,y*d.Stride),w*4);}finally{b.UnlockBits(d);}return b;}
 static void Equal(byte[] a,byte[] b,string name){if(a.Length!=b.Length)throw new Exception(name+" size");for(int i=0;i<a.Length;i++)if(a[i]!=b[i])throw new Exception(name+" differs at "+i+": "+a[i]+" / "+b[i]);}
 static byte[] ReferenceScale(Bitmap src,int w,int h,float opacity){var p=Pixels(src);var o=new byte[w*h*4];int alpha=(int)Math.Round(opacity*255);for(int y=0;y<h;y++){double sy=Math.Max(0,Math.Min(src.Height-1,(y+.5)*src.Height/h-.5));int y0=(int)sy,y1=Math.Min(src.Height-1,y0+1),wy=(int)Math.Round((sy-y0)*256);for(int x=0;x<w;x++){double sx=Math.Max(0,Math.Min(src.Width-1,(x+.5)*src.Width/w-.5));int x0=(int)sx,x1=Math.Min(src.Width-1,x0+1),wx=(int)Math.Round((sx-x0)*256);for(int c=0;c<3;c++){int first=p[(y0*src.Width+x0)*4+c]*(256-wx)+p[(y0*src.Width+x1)*4+c]*wx;int second=p[(y1*src.Width+x0)*4+c]*(256-wx)+p[(y1*src.Width+x1)*4+c]*wx;int value=(first*(256-wy)+second*wy+32768)>>16;o[(y*w+x)*4+c]=(byte)(alpha==255?value:(value*alpha+127)/255);}o[(y*w+x)*4+3]=(byte)alpha;}}return o;}
 static byte[] ReferenceNv12(Bitmap src){var p=Pixels(src);int w=src.Width&~1,h=src.Height&~1;var o=new byte[w*h*3/2];for(int y=0;y<h;y+=2)for(int x=0;x<w;x+=2){int rs=0,gs=0,bs=0;for(int dy=0;dy<2;dy++)for(int dx=0;dx<2;dx++){int at=((y+dy)*src.Width+x+dx)*4,b=p[at],g=p[at+1],r=p[at+2];o[(y+dy)*w+x+dx]=(byte)(((66*r+129*g+25*b+128)>>8)+16);rs+=r;gs+=g;bs+=b;}int uv=w*h+y/2*w+x;rs>>=2;gs>>=2;bs>>=2;o[uv]=(byte)(((-38*rs-74*gs+112*bs+128)>>8)+128);o[uv+1]=(byte)(((112*rs-94*gs-18*bs+128)>>8)+128);}return o;}
 static double[] Measure(Action action,int n){for(int i=0;i<8;i++)action();using(var process=Process.GetCurrentProcess()){var cpu=process.TotalProcessorTime;var watch=Stopwatch.StartNew();for(int i=0;i<n;i++)action();watch.Stop();return new[]{watch.Elapsed.TotalMilliseconds/n,(process.TotalProcessorTime-cpu).TotalMilliseconds/n};}}
 static void Bench(string name,Action action,int n){var t=Measure(action,n);Console.WriteLine(name+" wall="+t[0].ToString("F3")+"ms CPU="+t[1].ToString("F3")+"ms/frame");}
 static int Main(string[] args){
  bool managed=args.Length==1&&args[0]=="--managed";
  if(managed){Scaler.GetField("nativeScale",BindingFlags.NonPublic|BindingFlags.Static).SetValue(null,null);typeof(VirtualCameraBridge).GetField("nativeNv12",BindingFlags.NonPublic|BindingFlags.Static).SetValue(null,null);}
  object scaler=Activator.CreateInstance(Scaler,true);
  // Constructor would create the real camera queue; these stateless conversion tests need no queue.
  var bridge=(VirtualCameraBridge)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(VirtualCameraBridge));
  try{
   if(!managed && (Scaler.GetField("nativeScale",BindingFlags.NonPublic|BindingFlags.Static).GetValue(null)==null || typeof(VirtualCameraBridge).GetField("nativeNv12",BindingFlags.NonPublic|BindingFlags.Static).GetValue(null)==null))throw new Exception("Pixel acceleration did not load from the verified embedded runtime");
   Console.WriteLine(managed?"Managed compatibility path":"Embedded SSE2 path");
   foreach(var size in new[]{new Size(2,2),new Size(7,5),new Size(91,163),new Size(1280,720)})using(var input=Input(size.Width,size.Height)){
    foreach(var target in new[]{new Size(1,1),new Size(17,11),new Size(63,97)})foreach(float alpha in new[]{0f,.003f,.15f,.5f,.999f,1f}){var output=(Bitmap)Scale.Invoke(scaler,new object[]{input,target.Width,target.Height,alpha});Equal(Pixels(output),ReferenceScale(input,target.Width,target.Height,alpha),"Bilinear "+size+"->"+target+" alpha="+alpha);}
    var nv12=new byte[(size.Width&~1)*(size.Height&~1)*3/2];Convert.Invoke(bridge,new object[]{input,nv12});Equal(nv12,ReferenceNv12(input),"NV12 "+size);
   }
   using(var input=Input(33,17))for(int alpha=0;alpha<=255;alpha++){float opacity=alpha/255f;var output=(Bitmap)Scale.Invoke(scaler,new object[]{input,53,29,opacity});Equal(Pixels(output),ReferenceScale(input,53,29,opacity),"Opacity "+alpha);}
   // A padded bottom-up bitmap exercises signed source strides and row boundaries.
   int stride=40,height=6;var memory=Marshal.AllocHGlobal(stride*height);
   try{var bytes=new byte[stride*height];new Random(42).NextBytes(bytes);Marshal.Copy(bytes,0,memory,bytes.Length);using(var input=new Bitmap(8,height,-stride,PixelFormat.Format32bppArgb,IntPtr.Add(memory,stride*(height-1)))){
    var output=(Bitmap)Scale.Invoke(scaler,new object[]{input,19,13,.5f});Equal(Pixels(output),ReferenceScale(input,19,13,.5f),"Negative stride bilinear");
    var nv12=new byte[8*height*3/2];Convert.Invoke(bridge,new object[]{input,nv12});Equal(nv12,ReferenceNv12(input),"Negative stride NV12");
   }}finally{Marshal.FreeHGlobal(memory);}
   using(var input=Input(1280,720))Bench("Scale720to1080",()=>Scale.Invoke(scaler,new object[]{input,1920,1080,1f}),120);
   using(var input=Input(1920,1080)){
    Bench("Scale1080toPreview",()=>Scale.Invoke(scaler,new object[]{input,960,540,1f}),120);
    Bench("Scale1080opacity",()=>Scale.Invoke(scaler,new object[]{input,1280,720,.5f}),120);
    var nv12=new byte[1920*1080*3/2];Bench("NV12-1080",()=>Convert.Invoke(bridge,new object[]{input,nv12}),120);
   }
  }finally{((IDisposable)scaler).Dispose();GC.SuppressFinalize(bridge);}
  Console.WriteLine("PASS byte-exact bilinear/opacity and BT.601 NV12 conversion");return 0;
 }
}
