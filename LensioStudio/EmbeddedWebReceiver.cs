using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace ApexCam {
    public static class WebReceiverRuntime {
        private static bool initialized;
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern IntPtr LoadLibrary(string name);
        public static void Initialize(){
            if(initialized)return;
            AppDomain.CurrentDomain.AssemblyResolve+=(s,e)=>{
                string name=new AssemblyName(e.Name).Name;
                if(name!="Microsoft.Web.WebView2.Core"&&name!="Microsoft.Web.WebView2.WinForms")return null;
                return Assembly.Load(Resource(name+".dll"));
            };
            string directory=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"LensioStudio","runtime");
            Directory.CreateDirectory(directory);string loader=Path.Combine(directory,"WebView2Loader.dll");byte[] bytes=Resource("WebView2Loader.dll");
            bool same=false;
            if(File.Exists(loader))using(var sha=SHA256.Create())same=Convert.ToBase64String(sha.ComputeHash(File.ReadAllBytes(loader)))==Convert.ToBase64String(sha.ComputeHash(bytes));
            if(!same)File.WriteAllBytes(loader,bytes);
            if(LoadLibrary(loader)==IntPtr.Zero)throw new InvalidOperationException("The WebView2 loader could not start.");
            initialized=true;
        }
        private static byte[] Resource(string name){using(var s=typeof(WebReceiverRuntime).Assembly.GetManifestResourceStream("LensioRuntime."+name)){if(s==null)throw new InvalidOperationException("Missing embedded receiver dependency.");using(var m=new MemoryStream()){s.CopyTo(m);return m.ToArray();}}}
    }
    public sealed class EmbeddedWebReceiver : IDisposable {
        private WebView2 view;
        private bool stopped;
        public event Action<string> Status;
        public event Action<Bitmap> QrReady;
        public event Action Ready;
        public string CaptureMode {get;private set;}
        private static async Task Deadline(Task task,int milliseconds){if(await Task.WhenAny(task,Task.Delay(milliseconds))!=task)throw new TimeoutException("Web receiver startup timed out. Click connection status to retry.");await task;}
        public async Task StartAsync(Form owner,string url,string profileDirectory){
            try {
                string room=null;foreach(string part in new Uri(url).Fragment.TrimStart('#').Split('&'))if(part.StartsWith("room="))room=part.Substring(5);
                if(room!=null&&System.Text.RegularExpressions.Regex.IsMatch(room,"\\A[a-f0-9]{64}\\z")&&QrReady!=null)QrReady(PairingQrCode.Render(WebCameraBridge.PairingUrl(room)));
                WebReceiverRuntime.Initialize();
                view=new WebView2 {Location=new Point(-4,-4),Size=new Size(2,2),TabStop=false};owner.Controls.Add(view);view.SendToBack();
                bool missingRuntime=false;
                try {CoreWebView2Environment.GetAvailableBrowserVersionString();}
                catch(WebView2RuntimeNotFoundException){missingRuntime=true;}
                if(missingRuntime){
                    if(MessageBox.Show(owner,"Install Microsoft WebView2 to receive iPhone and web cameras? The installer needs internet access.","Lensio web camera setup",MessageBoxButtons.YesNo,MessageBoxIcon.Information)!=DialogResult.Yes)throw new InvalidOperationException("WebView2 is required for web cameras. Native Android cameras remain available.");
                    if(Status!=null)Status("Installing Microsoft WebView2...");
                    if(!await Task.Run(()=>BundledSetup.InstallWebViewRuntime()))throw new InvalidOperationException("WebView2 installation did not complete. Restart LensioStudio to retry.");
                }
                var options=new CoreWebView2EnvironmentOptions();
                var environmentTask=CoreWebView2Environment.CreateAsync(null,profileDirectory,options);
                await Deadline(environmentTask,30000);var environment=await environmentTask;
                if(stopped)return;
                await Deadline(view.EnsureCoreWebView2Async(environment),30000);
                if(stopped)return;
                view.CoreWebView2.Settings.AreDevToolsEnabled=false;
                view.CoreWebView2.Settings.AreDefaultContextMenusEnabled=false;
                view.CoreWebView2.Settings.AreBrowserAcceleratorKeysEnabled=false;
                view.CoreWebView2.Settings.IsStatusBarEnabled=false;
                view.CoreWebView2.Settings.IsGeneralAutofillEnabled=false;
                view.CoreWebView2.Settings.IsPasswordAutosaveEnabled=false;
                view.CoreWebView2.PermissionRequested+=(s,e)=>e.State=CoreWebView2PermissionState.Deny;
                view.CoreWebView2.NewWindowRequested+=(s,e)=>e.Handled=true;
                var expected=new Uri(url);
                view.CoreWebView2.NavigationStarting+=(s,e)=>{Uri next;if(!Uri.TryCreate(e.Uri,UriKind.Absolute,out next)||next.Scheme!=expected.Scheme||next.Authority!=expected.Authority||next.AbsolutePath!="/engine")e.Cancel=true;};
                var loaded=new TaskCompletionSource<bool>();
                view.CoreWebView2.WebMessageReceived+=(s,e)=>{
                    try {
                        var data=new JavaScriptSerializer {MaxJsonLength=512*1024}.Deserialize<System.Collections.Generic.Dictionary<string,object>>(e.WebMessageAsJson);
                        string kind=Convert.ToString(data["kind"]);
                        if(kind=="engine-ready"){loaded.TrySetResult(true);}
                        else if(kind=="status"){if(Status!=null)Status(Convert.ToString(data["text"]));}
                        else if(kind=="ready"){object capture;if(data.TryGetValue("capture",out capture))CaptureMode=Convert.ToString(capture);if(Ready!=null)Ready();}
                    }catch{if(Status!=null)Status("Web receiver sent invalid status information.");}
                };
                view.CoreWebView2.ProcessFailed+=(s,e)=>{if(Status!=null)Status("Web receiver stopped. Restart LensioStudio to reconnect web phones.");};
                view.CoreWebView2.NavigationCompleted+=(s,e)=>{if(!e.IsSuccess)loaded.TrySetException(new InvalidOperationException("Web receiver page failed: "+e.WebErrorStatus));};
                view.CoreWebView2.Navigate(url);
                await Deadline(loaded.Task,15000);
            }catch(Exception e){if(!stopped&&Status!=null)Status("Web receiver unavailable: "+e.Message);Dispose();}
        }
        public void Dispose(){stopped=true;if(view!=null){view.Dispose();view=null;}}
    }
}
