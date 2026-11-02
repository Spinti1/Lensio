using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Threading;
namespace ApexCam {
    public sealed class LocalCameraInfo {
        public string Id,Name;
        public override string ToString(){return Name;}
    }
    // Capture owns its COM graph on one worker. The callback only replaces one pending
    // frame: no growing queue, network connection, JPEG encode or UI invocation per sample.
    public sealed class LocalCameraCapture : IDisposable {
        [ComImport,Guid("29840822-5b84-11d0-bd3b-00a0c911ce86"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] interface ICreateDevEnum {
            [PreserveSig]int CreateClassEnumerator(ref Guid category,out IEnumMoniker devices,int flags);
        }
        [ComImport,Guid("55272a00-42cb-11ce-8135-00aa004bb851"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] interface IPropertyBag {
            [PreserveSig]int Read([MarshalAs(UnmanagedType.LPWStr)]string name,[MarshalAs(UnmanagedType.Struct)]out object value,IntPtr log);
        }
        [ComImport,Guid("56a86895-0ad4-11ce-b03a-0020af0ba770"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] interface IBaseFilter {}
        [ComImport,Guid("56a868a9-0ad4-11ce-b03a-0020af0ba770"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] interface IGraphBuilder {
            [PreserveSig]int AddFilter(IBaseFilter filter,[MarshalAs(UnmanagedType.LPWStr)]string name);
        }
        [ComImport,Guid("93e5a4e0-2d50-11d2-abfa-00a0c9c6e38d"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] interface ICaptureGraphBuilder2 {
            [PreserveSig]int SetFiltergraph(IGraphBuilder graph);
            [PreserveSig]int GetFiltergraph(out IGraphBuilder graph);
            [PreserveSig]int SetOutputFileName(ref Guid type,[MarshalAs(UnmanagedType.LPWStr)]string name,out IBaseFilter filter,out IntPtr sink);
            [PreserveSig]int FindInterface(ref Guid category,ref Guid type,IBaseFilter source,ref Guid iid,out IntPtr result);
            [PreserveSig]int RenderStream(ref Guid category,ref Guid type,[MarshalAs(UnmanagedType.IUnknown)]object source,IBaseFilter middle,IBaseFilter sink);
        }
        [ComImport,Guid("56a868b1-0ad4-11ce-b03a-0020af0ba770"),InterfaceType(ComInterfaceType.InterfaceIsDual)] interface IMediaControl {
            [PreserveSig]int Run();[PreserveSig]int Pause();[PreserveSig]int Stop();
        }
        [ComImport,Guid("c6e13340-30ac-11d0-a18c-00a0c9118956"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] interface IAMStreamConfig {
            [PreserveSig]int SetFormat([In,MarshalAs(UnmanagedType.LPStruct)]MediaType type);
            [PreserveSig]int GetFormat(out IntPtr type);
            [PreserveSig]int GetNumberOfCapabilities(out int count,out int size);
            [PreserveSig]int GetStreamCaps(int index,out IntPtr type,IntPtr caps);
        }
        [StructLayout(LayoutKind.Sequential)] sealed class MediaType {
            public Guid Major,Subtype;[MarshalAs(UnmanagedType.Bool)]public bool FixedSize;[MarshalAs(UnmanagedType.Bool)]public bool Temporal;
            public int SampleSize;public Guid FormatType;public IntPtr Unknown;public int FormatSize;public IntPtr Format;
        }
        [ComImport,Guid("6b652fff-11fe-4fce-92ad-0266b5d7c78f"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] interface ISampleGrabber {
            [PreserveSig]int SetOneShot([MarshalAs(UnmanagedType.Bool)]bool one);
            [PreserveSig]int SetMediaType([In,MarshalAs(UnmanagedType.LPStruct)]MediaType type);
            [PreserveSig]int GetConnectedMediaType([Out]MediaType type);
            [PreserveSig]int SetBufferSamples([MarshalAs(UnmanagedType.Bool)]bool enabled);
            [PreserveSig]int GetCurrentBuffer(ref int size,IntPtr data);
            [PreserveSig]int GetCurrentSample(out IntPtr sample);
            [PreserveSig]int SetCallback(ICallback callback,int method);
        }
        [ComVisible(true),Guid("0579154a-2b53-4994-b0d0-e773148eff85"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] public interface ICallback {
            [PreserveSig]int SampleCB(double time,IntPtr sample);
            [PreserveSig]int BufferCB(double time,IntPtr buffer,int length);
        }
        [ComVisible(true),ClassInterface(ClassInterfaceType.None)] sealed class Callback : ICallback {
            readonly LocalCameraCapture owner;public Callback(LocalCameraCapture owner){this.owner=owner;}
            public int SampleCB(double t,IntPtr s){return 0;}
            public int BufferCB(double t,IntPtr p,int n){owner.Accept(p,n);return 0;}
        }
        static readonly Guid Video=new Guid("73646976-0000-0010-8000-00aa00389b71"),VideoInfo=new Guid("05589f80-c356-11ce-bf01-00aa0055595a"),Rgb24=new Guid("e436eb7d-524f-11ce-9f53-0020af0ba770");
        readonly string id;readonly int desiredWidth,desiredHeight;readonly Action<Bitmap> frame;readonly Action<Exception> failed;
        readonly object gate=new object();readonly AutoResetEvent ready=new AutoResetEvent(false);readonly ManualResetEvent stop=new ManualResetEvent(false);
        Thread worker;Bitmap pending;volatile bool stopping;int width,height,stride;bool bottomUp;
        public LocalCameraCapture(string id,Action<Bitmap> frame,Action<Exception> failed,int width=1280,int height=720){this.id=id;this.frame=frame;this.failed=failed;desiredWidth=width;desiredHeight=height;}
        static object Create(string id){return Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid(id),true));}
        static void Release(object o){if(o!=null&&Marshal.IsComObject(o))Marshal.ReleaseComObject(o);}
        static void Check(int hr){if(hr<0)Marshal.ThrowExceptionForHR(hr);}
        static bool Allowed(string name,string clsid){return !string.Equals(clsid,BundledSetup.CameraClassId,StringComparison.OrdinalIgnoreCase) && !string.Equals(clsid,"{A3FCE0F5-3493-419F-958A-ABA1250EC20B}",StringComparison.OrdinalIgnoreCase) && !string.Equals(name,"LensioCam",StringComparison.OrdinalIgnoreCase) && !string.Equals(name,"OBS Virtual Camera",StringComparison.OrdinalIgnoreCase) && !string.Equals(name,"ApexCam",StringComparison.OrdinalIgnoreCase);}
        static void Enumerate(Action<IMoniker,string,string> visit){
            object factory=null;IEnumMoniker items=null;
            try{factory=Create("62be5d10-60eb-11d0-bd3b-00a0c911ce86");Guid category=new Guid("860bb310-5d01-11d0-bd3b-00a0c911ce86");
                int hr=((ICreateDevEnum)factory).CreateClassEnumerator(ref category,out items,0);Check(hr);if(items==null)return;
                var next=new IMoniker[1];while(items.Next(1,next,IntPtr.Zero)==0){object bag=null;try{Guid iid=typeof(IPropertyBag).GUID;next[0].BindToStorage(null,null,ref iid,out bag);object n,c;((IPropertyBag)bag).Read("FriendlyName",out n,IntPtr.Zero);((IPropertyBag)bag).Read("CLSID",out c,IntPtr.Zero);string name=Convert.ToString(n),display;next[0].GetDisplayName(null,null,out display);if(Allowed(name,Convert.ToString(c)))visit(next[0],display,name);}finally{Release(bag);Release(next[0]);}}
            }finally{Release(items);Release(factory);}
        }
        public static List<LocalCameraInfo> GetDevices(){var result=new List<LocalCameraInfo>();Enumerate((m,id,name)=>result.Add(new LocalCameraInfo{Id="local:"+id,Name=name}));return result;}
        public int NegotiatedFrameRate {get;private set;}
        public void Start(){if(worker!=null)return;worker=new Thread(Run){IsBackground=true,Name="Lensio local camera"};worker.SetApartmentState(ApartmentState.MTA);worker.Start();}
        unsafe void Accept(IntPtr source,int length){
            if(stopping||width<=0||height<=0||length<(long)stride*height)return;
            Bitmap image=null;try{
                image=new Bitmap(width,height,PixelFormat.Format24bppRgb);var data=image.LockBits(new Rectangle(0,0,width,height),ImageLockMode.WriteOnly,PixelFormat.Format24bppRgb);
                try{for(int y=0;y<height;y++){byte* src=(byte*)source+(bottomUp?height-y-1:y)*stride;byte* dst=(byte*)data.Scan0+y*data.Stride;int bytes=width*3,x=0;for(;x+8<=bytes;x+=8)*(long*)(dst+x)=*(long*)(src+x);for(;x<bytes;x++)dst[x]=src[x];}}finally{image.UnlockBits(data);}
                lock(gate){if(stopping)return;var old=pending;pending=image;image=null;if(old!=null)old.Dispose();ready.Set();}
            }catch{}finally{if(image!=null)image.Dispose();}
        }
        static void FreeType(MediaType mt){if(mt==null)return;if(mt.Format!=IntPtr.Zero)Marshal.FreeCoTaskMem(mt.Format);if(mt.Unknown!=IntPtr.Zero)Marshal.Release(mt.Unknown);}
        void SelectFormat(ICaptureGraphBuilder2 builder,IBaseFilter camera){
            IntPtr configPointer=IntPtr.Zero;object configObject=null;IntPtr caps=IntPtr.Zero;MediaType selected=null;double best=double.MaxValue;
            try{
                var category=new Guid("fb6c4281-0353-11d1-905f-0000c0cc16ba");var video=Video;Guid iid=typeof(IAMStreamConfig).GUID;
                if(builder.FindInterface(ref category,ref video,camera,ref iid,out configPointer)<0||configPointer==IntPtr.Zero)return;
                configObject=Marshal.GetObjectForIUnknown(configPointer);var config=(IAMStreamConfig)configObject;int count,size;
                if(config.GetNumberOfCapabilities(out count,out size)<0||count<1||count>512||size<128||size>4096)return;
                caps=Marshal.AllocCoTaskMem(size);
                for(int i=0;i<count;i++){
                    IntPtr ptr=IntPtr.Zero;MediaType mt=null;
                    try{
                        if(config.GetStreamCaps(i,out ptr,caps)<0||ptr==IntPtr.Zero)continue;
                        mt=(MediaType)Marshal.PtrToStructure(ptr,typeof(MediaType));
                        if(mt.FormatType!=VideoInfo||mt.Format==IntPtr.Zero||mt.FormatSize<88)continue;
                        int w=Marshal.ReadInt32(mt.Format,52),h=Math.Abs(Marshal.ReadInt32(mt.Format,56));if(w<1||h<1||w>4096||h>4096)continue;
                        long min=Marshal.ReadInt64(caps,104),max=Marshal.ReadInt64(caps,112);if(min<=0||max<min)continue;
                        long interval=Math.Max(min,Math.Min(max,333333));
                        double score=Math.Abs(Math.Log(w/(double)desiredWidth))+Math.Abs(Math.Log(h/(double)desiredHeight));
                        if(interval>340000)score+=10+interval/333333.0;
                        if(mt.Subtype==new Guid("47504a4d-0000-0010-8000-00aa00389b71"))score-=.0001;
                        if(score<best){FreeType(selected);selected=mt;mt=null;Marshal.WriteInt64(selected.Format,40,interval);best=score;}
                    }finally{FreeType(mt);if(ptr!=IntPtr.Zero)Marshal.FreeCoTaskMem(ptr);}
                }
                if(selected!=null)config.SetFormat(selected); // A rejecting driver keeps its existing format.
            }finally{FreeType(selected);if(caps!=IntPtr.Zero)Marshal.FreeCoTaskMem(caps);Release(configObject);if(configPointer!=IntPtr.Zero)Marshal.Release(configPointer);}
        }
        void Run(){object camera=null,graph=null,builder=null,grabber=null,sink=null;IMediaControl control=null;Callback callback=null;
            try{
                Enumerate((m,key,name)=>{if("local:"+key==id&&camera==null){Guid iid=typeof(IBaseFilter).GUID;m.BindToObject(null,null,ref iid,out camera);}});
                if(camera==null)throw new InvalidOperationException("This webcam is unavailable. Reconnect it and choose it again.");
                graph=Create("e436ebb3-524f-11ce-9f53-0020af0ba770");builder=Create("bf87b6e1-8c27-11d0-b3f0-00aa003761c5");grabber=Create("c1f400a0-3f08-11d3-9f0b-006008039e37");sink=Create("c1f400a4-3f08-11d3-9f0b-006008039e37");
                var g=(IGraphBuilder)graph;var capture=(ICaptureGraphBuilder2)builder;var samples=(ISampleGrabber)grabber;Check(capture.SetFiltergraph(g));Check(g.AddFilter((IBaseFilter)camera,"Webcam"));Check(g.AddFilter((IBaseFilter)grabber,"Frames"));Check(g.AddFilter((IBaseFilter)sink,"Null renderer"));Check(samples.SetMediaType(new MediaType{Major=Video,Subtype=Rgb24,FormatType=VideoInfo}));
                SelectFormat(capture,(IBaseFilter)camera);
                var category=new Guid("fb6c4281-0353-11d1-905f-0000c0cc16ba");var video=Video;Check(capture.RenderStream(ref category,ref video,camera,(IBaseFilter)grabber,(IBaseFilter)sink));
                var mt=new MediaType();try{Check(samples.GetConnectedMediaType(mt));if(mt.FormatType!=VideoInfo||mt.Subtype!=Rgb24||mt.Format==IntPtr.Zero||mt.FormatSize<88)throw new InvalidOperationException("Unsupported webcam format.");
                    width=Marshal.ReadInt32(mt.Format,52);int h=Marshal.ReadInt32(mt.Format,56);bottomUp=h>0;height=Math.Abs(h);if(width<1||width>4096||height<1||height>4096||Marshal.ReadInt16(mt.Format,62)!=24)throw new InvalidOperationException("Invalid webcam dimensions.");stride=(width*3+3)&~3;long interval=Marshal.ReadInt64(mt.Format,40);NegotiatedFrameRate=interval>0?(int)Math.Round(10000000.0/interval):0;
                }finally{if(mt.Format!=IntPtr.Zero)Marshal.FreeCoTaskMem(mt.Format);if(mt.Unknown!=IntPtr.Zero)Marshal.Release(mt.Unknown);}
                callback=new Callback(this);Check(samples.SetBufferSamples(false));Check(samples.SetCallback(callback,1));control=(IMediaControl)graph;Check(control.Run());
                var waits=new WaitHandle[]{stop,ready};while(WaitHandle.WaitAny(waits)!=0){Bitmap image;lock(gate){image=pending;pending=null;}if(image!=null)using(image){if(!stopping)frame(image);}}
            }catch(Exception e){if(!stopping&&failed!=null)failed(e);}finally{stopping=true;try{if(control!=null)control.Stop();if(grabber!=null)((ISampleGrabber)grabber).SetCallback(null,1);}catch{}lock(gate){if(pending!=null)pending.Dispose();pending=null;}foreach(var o in new[]{builder,graph,grabber,sink,camera})Release(o);GC.KeepAlive(callback);ready.Dispose();stop.Dispose();}
        }
        public void Dispose(){lock(gate){if(stopping)return;stopping=true;stop.Set();}if(worker!=null&&Thread.CurrentThread!=worker)worker.Join(2500);}
    }
}
