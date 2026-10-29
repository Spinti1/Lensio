using System;
using System.Runtime.InteropServices;
using System.Threading;
// Opens the same DirectShow source used by Discord/Chromium and waits for a real sample.
class DirectShowCaptureSmoke {
 [ComImport,Guid("29840822-5b84-11d0-bd3b-00a0c911ce86"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] interface ICreateDevEnum {
  [PreserveSig]int CreateClassEnumerator(ref Guid category,out System.Runtime.InteropServices.ComTypes.IEnumMoniker devices,int flags);
 }
 [ComImport,Guid("55272a00-42cb-11ce-8135-00aa004bb851"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] interface IPropertyBag {
  [PreserveSig]int Read([MarshalAs(UnmanagedType.LPWStr)]string name,[MarshalAs(UnmanagedType.Struct)]out object value,IntPtr log);
 }
 static void VerifyDeviceName(){
  var enumerator=Create("62be5d10-60eb-11d0-bd3b-00a0c911ce86");
  System.Runtime.InteropServices.ComTypes.IEnumMoniker devices=null;int count=0;
  try {
   var category=new Guid("860bb310-5d01-11d0-bd3b-00a0c911ce86");
   Check(((ICreateDevEnum)enumerator).CreateClassEnumerator(ref category,out devices,0),"Enumerate cameras");
   if(devices==null)throw new Exception("No cameras enumerated");
   var next=new System.Runtime.InteropServices.ComTypes.IMoniker[1];
   while(devices.Next(1,next,IntPtr.Zero)==0){
    object bag=null;
    try {
     var iid=typeof(IPropertyBag).GUID;next[0].BindToStorage(null,null,ref iid,out bag);
     object name,id;((IPropertyBag)bag).Read("FriendlyName",out name,IntPtr.Zero);((IPropertyBag)bag).Read("CLSID",out id,IntPtr.Zero);
     if(string.Equals(Convert.ToString(id),"{A3FCE0F5-3493-419F-958A-ABA1250EC20B}",StringComparison.OrdinalIgnoreCase)){
      count++;if(Convert.ToString(name)!="LensioCam")throw new Exception("Unexpected Windows camera name: "+name);
     }
    }finally{if(bag!=null)Marshal.ReleaseComObject(bag);Marshal.ReleaseComObject(next[0]);}
   }
   if(count!=1)throw new Exception("Expected exactly one LensioCam device, found "+count);
   Console.WriteLine("PASS Windows enumerates one LensioCam device without legacy aliases");
  }finally{if(devices!=null)Marshal.ReleaseComObject(devices);Marshal.ReleaseComObject(enumerator);}
 }
 [ComImport,Guid("56a86895-0ad4-11ce-b03a-0020af0ba770"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] interface IBaseFilter {}
 [ComImport,Guid("56a868a9-0ad4-11ce-b03a-0020af0ba770"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] interface IGraphBuilder {
  [PreserveSig] int AddFilter(IBaseFilter filter,[MarshalAs(UnmanagedType.LPWStr)]string name);
 }
 [ComImport,Guid("93e5a4e0-2d50-11d2-abfa-00a0c9c6e38d"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] interface ICaptureGraphBuilder2 {
  [PreserveSig] int SetFiltergraph(IGraphBuilder graph);
  [PreserveSig] int GetFiltergraph(out IGraphBuilder graph);
  [PreserveSig] int SetOutputFileName(ref Guid type,[MarshalAs(UnmanagedType.LPWStr)]string name,out IBaseFilter filter,out IntPtr sink);
  [PreserveSig] int FindInterface(ref Guid category,ref Guid type,IBaseFilter source,ref Guid iid,out IntPtr result);
  [PreserveSig] int RenderStream(ref Guid category,ref Guid type,[MarshalAs(UnmanagedType.IUnknown)]object source,IBaseFilter middle,IBaseFilter sink);
 }
 [ComImport,Guid("56a868b1-0ad4-11ce-b03a-0020af0ba770"),InterfaceType(ComInterfaceType.InterfaceIsDual)] interface IMediaControl {
  [PreserveSig] int Run(); [PreserveSig] int Pause(); [PreserveSig] int Stop();
 }
 [StructLayout(LayoutKind.Sequential)] class MediaType {
  public Guid Major,Subtype;
  [MarshalAs(UnmanagedType.Bool)]public bool FixedSize;
  [MarshalAs(UnmanagedType.Bool)]public bool Temporal;
  public int SampleSize;public Guid FormatType;public IntPtr Unknown;public int FormatSize;public IntPtr Format;
 }
 [ComImport,Guid("6b652fff-11fe-4fce-92ad-0266b5d7c78f"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] interface ISampleGrabber {
  [PreserveSig]int SetOneShot([MarshalAs(UnmanagedType.Bool)]bool one);
  [PreserveSig]int SetMediaType([In,MarshalAs(UnmanagedType.LPStruct)]MediaType type);
  [PreserveSig]int GetConnectedMediaType([Out]MediaType type);
  [PreserveSig]int SetBufferSamples([MarshalAs(UnmanagedType.Bool)]bool enabled);
  [PreserveSig]int GetCurrentBuffer(ref int size,IntPtr data);
 }
 static object Create(string guid){return Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid(guid),true));}
 static void Check(int hr,string action){if(hr<0)throw new COMException(action,hr);}
 [STAThread]static int Main(){
  object camera=null,graph=null,builder=null,grabber=null,sink=null;IMediaControl control=null;
  try {
   VerifyDeviceName();
   camera=Create("A3FCE0F5-3493-419F-958A-ABA1250EC20B");
   graph=Create("E436EBB3-524F-11CE-9F53-0020AF0BA770");
   builder=Create("BF87B6E1-8C27-11D0-B3F0-00AA003761C5");
   grabber=Create("C1F400A0-3F08-11D3-9F0B-006008039E37");
   sink=Create("C1F400A4-3F08-11D3-9F0B-006008039E37");
   var g=(IGraphBuilder)graph;var capture=(ICaptureGraphBuilder2)builder;var samples=(ISampleGrabber)grabber;
   Check(capture.SetFiltergraph(g),"Set graph");
   Check(g.AddFilter((IBaseFilter)camera,"LensioCam"),"Add camera");
   Check(g.AddFilter((IBaseFilter)grabber,"Sample buffer"),"Add samples");
   Check(g.AddFilter((IBaseFilter)sink,"Null renderer"),"Add sink");
   var video=new Guid("73646976-0000-0010-8000-00AA00389B71");
   Check(samples.SetMediaType(new MediaType{Major=video}),"Select video");
   Check(samples.SetBufferSamples(true),"Buffer samples");
   var category=new Guid("FB6C4281-0353-11D1-905F-0000C0CC16BA");
   Check(capture.RenderStream(ref category,ref video,camera,(IBaseFilter)grabber,(IBaseFilter)sink),"Connect stream");
   control=(IMediaControl)graph;Check(control.Run(),"Start camera");
   int size=0,hr=-1;for(int attempt=0;attempt<40;attempt++){Thread.Sleep(100);hr=samples.GetCurrentBuffer(ref size,IntPtr.Zero);if(hr>=0 && size>0)break;}
   Check(hr,"Read frame");if(size<=0)throw new Exception("Camera returned no sample");
   IntPtr data=Marshal.AllocCoTaskMem(size);
   try{Check(samples.GetCurrentBuffer(ref size,data),"Copy sample");Console.WriteLine("PASS DirectShow "+(IntPtr.Size*8)+"-bit: camera started and delivered "+size+" video bytes");}
   finally{Marshal.FreeCoTaskMem(data);}
   return 0;
  }catch(Exception e){Console.WriteLine("FAIL "+e.Message+" (0x"+e.HResult.ToString("X8")+")");return 1;}
  finally{if(control!=null)control.Stop();foreach(var o in new[]{builder,graph,grabber,sink,camera})if(o!=null && Marshal.IsComObject(o))Marshal.ReleaseComObject(o);}
 }
}
