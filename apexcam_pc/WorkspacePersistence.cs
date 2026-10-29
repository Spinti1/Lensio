using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Security.Cryptography;
using System.Web.Script.Serialization;
using System.Windows.Forms;
namespace ApexCam {
    public sealed class SavedSource {
        public string Id,Name,DeviceId,PhoneName,Host,Asset,ImagePath,Text,ClockFormat;
        public SourceType Type;
        public bool Visible=true,Locked,ShowBackground=true;
        public float X,Y,Width=320,Height=180,Opacity=1,FontSize=26;
        public int TextColor=Color.White.ToArgb(),BgColor=Color.Black.ToArgb(),BorderColor=Color.Cyan.ToArgb(),SolidColor=Color.Gray.ToArgb();
    }
    public sealed class SavedWorkspace {
        public int Version=1,ResolutionWidth=1280,Filter,WindowX,WindowY,WindowWidth=1280,WindowHeight=760;
        public float Exposure,Brightness,Contrast,Saturation;
        public bool Mirror,Flip,AutoLevels,VirtualCamera=true,Maximized,PcAudio,Microphone;
        public string MainPhone,ControlledPhone,SelectedSource;
        public List<SavedSource> Sources=new List<SavedSource>();
        public Dictionary<string,Dictionary<string,object>> PhoneControls=new Dictionary<string,Dictionary<string,object>>();
    }
    public partial class MainForm {
        private readonly string workspaceDirectory;
        private readonly object workspaceGate=new object();
        private readonly Dictionary<string,Dictionary<string,object>> phonePreferences=new Dictionary<string,Dictionary<string,object>>();
        private readonly HashSet<string> restoreTorchPending=new HashSet<string>();
        private bool restorePcAudioPending,restoreMicPending,workspaceMaximized,workspaceReady;
        private System.Windows.Forms.Timer workspaceSaveTimer;
        private string lastWorkspaceJson;
        private readonly JavaScriptSerializer workspaceJson=new JavaScriptSerializer {MaxJsonLength=4*1024*1024};
        private sealed class SavedAsset {public Image Image;public string File;}
        private readonly Dictionary<string,SavedAsset> savedAssets=new Dictionary<string,SavedAsset>();
        private string WorkspacePath {get{return Path.Combine(workspaceDirectory,"workspace.json");}}
        private static float SafeFloat(float value,float fallback,float min,float max){return float.IsNaN(value)||float.IsInfinity(value)?fallback:Math.Max(min,Math.Min(max,value));}
        private void InitializeWorkspace() {
            try {
                if(File.Exists(WorkspacePath)) {
                    var state=workspaceJson.Deserialize<SavedWorkspace>(File.ReadAllText(WorkspacePath));
                    if(state==null || state.Version!=1 || state.Sources==null || state.Sources.Count>256)throw new InvalidDataException("Invalid workspace format");
                    RestoreWorkspace(state);
                }
            } catch(Exception error) {LogWorkspaceError("restore",error);}
            workspaceReady=true;
            workspaceSaveTimer=new System.Windows.Forms.Timer {Interval=1000};
            workspaceSaveTimer.Tick += (s,e) => SaveWorkspace();
            workspaceSaveTimer.Start();
            Disposed += (s,e) => {if(workspaceSaveTimer!=null)workspaceSaveTimer.Dispose();};
        }
        private void LogWorkspaceError(string action,Exception error) {
            try{File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"app.log"),DateTime.Now+" - [Workspace] "+action+": "+error.Message+Environment.NewLine);}catch{}
        }
        private string SaveSourceAsset(ObsLayer layer) {
            if(layer.BitmapImage==null)return null;
            SavedAsset cached;
            if(savedAssets.TryGetValue(layer.Id,out cached) && object.ReferenceEquals(cached.Image,layer.BitmapImage) && File.Exists(Path.Combine(workspaceDirectory,"assets",cached.File)))return cached.File;
            var bytes=layer.ExportImagePng();if(bytes==null)return null;
            string hash;using(var sha=SHA256.Create())hash=BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-","").ToLowerInvariant()+".png";
            string directory=Path.Combine(workspaceDirectory,"assets");Directory.CreateDirectory(directory);
            string path=Path.Combine(directory,hash);
            if(!File.Exists(path)) {File.WriteAllBytes(path+".tmp",bytes);File.Move(path+".tmp",path);}
            savedAssets[layer.Id]=new SavedAsset {Image=layer.BitmapImage,File=hash};
            return hash;
        }
        private SavedWorkspace CaptureWorkspace() {
            Rectangle bounds=isFullscreen?RestoreBounds:(WindowState==FormWindowState.Normal?Bounds:RestoreBounds);
            var state=new SavedWorkspace {ResolutionWidth=currentResWidth,Filter=(int)currentFilter,
                Exposure=exposureVal,Brightness=brightnessVal,Contrast=contrastVal,Saturation=saturationVal,
                Mirror=mirrorH,Flip=flipV,AutoLevels=isAutoMode,VirtualCamera=isVirtualCamActive,
                PcAudio=isPcAudioStreaming||restorePcAudioPending,Microphone=isMicStreaming||restoreMicPending,
                MainPhone=mainDeviceId,ControlledPhone=controlledDeviceId,SelectedSource=selectedLayer==null?null:selectedLayer.Id,
                WindowX=bounds.X,WindowY=bounds.Y,WindowWidth=bounds.Width,WindowHeight=bounds.Height,
                Maximized=isFullscreen?previousWindowState==FormWindowState.Maximized:WindowState==FormWindowState.Maximized};
            foreach(var layer in scene.GetLayersSnapshot()) {
                if(layer.IsDisposed)continue;
                var source=new SavedSource {Id=layer.Id,Name=layer.Name,Type=layer.Type,DeviceId=layer.DeviceId,
                    X=layer.X,Y=layer.Y,Width=layer.Width,Height=layer.Height,Opacity=layer.Opacity,Visible=layer.Visible,Locked=layer.Locked,
                    Text=layer.TextContent,FontSize=layer.FontSize,ClockFormat=layer.ClockFormat,ShowBackground=layer.ShowBackground,
                    TextColor=layer.TextColor.ToArgb(),BgColor=layer.BgColor.ToArgb(),BorderColor=layer.BorderColor.ToArgb(),SolidColor=layer.SolidColor.ToArgb(),ImagePath=layer.ImagePath};
                if(layer.Type==SourceType.Image)source.Asset=SaveSourceAsset(layer);
                if(layer.Type==SourceType.Camera) {lock(devicesGate){PhoneSlot slot;if(phoneSlots.TryGetValue(layer.DeviceId??"",out slot)){source.PhoneName=slot.Info.Name;source.Host=slot.Info.Host;}}}
                state.Sources.Add(source);
            }
            lock(workspaceGate)foreach(var pair in phonePreferences)state.PhoneControls[pair.Key]=new Dictionary<string,object>(pair.Value);
            return state;
        }
        private void SaveWorkspace() {
            if(!workspaceReady)return;
            try {
                var state=CaptureWorkspace();string json=workspaceJson.Serialize(state);
                if(json==lastWorkspaceJson)return;
                Directory.CreateDirectory(workspaceDirectory);
                string temporary=WorkspacePath+".tmp";
                File.WriteAllText(temporary,json,new System.Text.UTF8Encoding(false));
                if(File.Exists(WorkspacePath))File.Replace(temporary,WorkspacePath,null);else File.Move(temporary,WorkspacePath);
                lastWorkspaceJson=json;
                var keep=new HashSet<string>();foreach(var source in state.Sources)if(source.Asset!=null)keep.Add(source.Asset);
                string assets=Path.Combine(workspaceDirectory,"assets");
                if(Directory.Exists(assets))foreach(string file in Directory.GetFiles(assets,"*.png")) {
                    string name=Path.GetFileName(file);
                    if(System.Text.RegularExpressions.Regex.IsMatch(name,@"\A[0-9a-f]{64}\.png\z") && !keep.Contains(name))File.Delete(file);
                }
            } catch(Exception error){LogWorkspaceError("save",error);}
        }
        private void RestoreWorkspace(SavedWorkspace state) {
            SelectResolution(state.ResolutionWidth==1920?1920:1280,state.ResolutionWidth==1920?1080:720);
            cbPresets.SelectedIndex=Math.Max(0,Math.Min(cbPresets.Items.Count-1,state.Filter));
            tbExposure.Value=(int)SafeFloat(state.Exposure,0,-50,50);tbBrightness.Value=(int)SafeFloat(state.Brightness,0,-50,50);
            tbContrast.Value=(int)SafeFloat(state.Contrast,0,-50,50);tbSaturation.Value=(int)SafeFloat(state.Saturation,0,-100,100);
            mirrorH=state.Mirror;flipV=state.Flip;isAutoMode=state.AutoLevels;
            btnAutoMode.Text=isAutoMode?"Auto Levels: ON":"Auto Levels: OFF";btnAutoMode.BackColor=isAutoMode?ColorAccentEmerald:ColorButtonDark;
            if(isVirtualCamActive!=state.VirtualCamera)OnToggleVcam(null,EventArgs.Empty);
            restorePcAudioPending=state.PcAudio;restoreMicPending=state.Microphone;
            mainDeviceId=state.MainPhone;controlledDeviceId=state.ControlledPhone;
            if(state.PhoneControls!=null)foreach(var pair in state.PhoneControls)if(pair.Value!=null)phonePreferences[pair.Key]=new Dictionary<string,object>(pair.Value);
            for(int i=state.Sources.Count-1;i>=0;i--) {
                var s=state.Sources[i];if(s==null || !Enum.IsDefined(typeof(SourceType),s.Type))continue;
                var layer=new ObsLayer {Id=string.IsNullOrEmpty(s.Id)?Guid.NewGuid().ToString():s.Id,Name=s.Name??"Source",Type=s.Type,DeviceId=s.DeviceId,
                    X=SafeFloat(s.X,0,-16384,16384),Y=SafeFloat(s.Y,0,-16384,16384),Width=SafeFloat(s.Width,320,1,8192),Height=SafeFloat(s.Height,180,1,8192),
                    Opacity=SafeFloat(s.Opacity,1,0,1),Visible=s.Visible,Locked=s.Locked,TextContent=s.Text??"",FontSize=SafeFloat(s.FontSize,26,8,200),
                    ClockFormat=s.ClockFormat??"HH:mm:ss",ShowBackground=s.ShowBackground,TextColor=Color.FromArgb(s.TextColor),BgColor=Color.FromArgb(s.BgColor),
                    BorderColor=Color.FromArgb(s.BorderColor),SolidColor=Color.FromArgb(s.SolidColor),ImagePath=s.ImagePath};
                if(s.Type==SourceType.Image) {
                    try {
                        if(string.IsNullOrEmpty(s.Asset) || s.Asset!=Path.GetFileName(s.Asset))throw new InvalidDataException("Invalid image asset");
                        using(var image=Image.FromFile(Path.Combine(workspaceDirectory,"assets",s.Asset)))layer.BitmapImage=new Bitmap(image);
                        savedAssets[layer.Id]=new SavedAsset {Image=layer.BitmapImage,File=s.Asset};
                    } catch(Exception error){LogWorkspaceError("image "+s.Name,error);layer.Dispose();continue;}
                }
                if(s.Type==SourceType.Camera) {
                    if(string.IsNullOrEmpty(s.DeviceId) || phoneSlots.ContainsKey(s.DeviceId)){layer.Dispose();continue;}
                    phoneSlots[s.DeviceId]=new PhoneSlot {Info=new PhoneDeviceInfo {Id=s.DeviceId,Name=s.PhoneName??s.Name,Host=s.Host},Layer=layer,Connected=false};
                }
                scene.AddLayer(layer);if(layer.Id==state.SelectedSource)selectedLayer=layer;
            }
            if(mainDeviceId!=null && !phoneSlots.ContainsKey(mainDeviceId))mainDeviceId=null;
            if(controlledDeviceId!=null && !phoneSlots.ContainsKey(controlledDeviceId))controlledDeviceId=null;
            RefreshPhoneCards();RefreshSourcesList();
            Rectangle target=new Rectangle(state.WindowX,state.WindowY,Math.Max(MinimumSize.Width,Math.Min(7680,state.WindowWidth)),Math.Max(MinimumSize.Height,Math.Min(4320,state.WindowHeight)));
            var screen=Screen.FromRectangle(target).WorkingArea;
            target.Width=Math.Min(screen.Width,target.Width);target.Height=Math.Min(screen.Height,target.Height);
            target.X=Math.Max(screen.Left,Math.Min(screen.Right-target.Width,target.X));target.Y=Math.Max(screen.Top,Math.Min(screen.Bottom-target.Height,target.Y));
            StartPosition=FormStartPosition.Manual;Bounds=target;workspaceMaximized=state.Maximized;
        }
        private void SendPhoneControl(string payload){SendPhoneControl(controlledDeviceId,payload);}
        private void SendPhoneControl(string id,string payload) {
            if(!string.IsNullOrEmpty(id)) {
                try {
                    var values=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(payload);
                    lock(workspaceGate)foreach(var key in new[]{"cameraId","cameraLens","resolution","zoom","stabilization","toggleTorch"}) {
                        object value;if(!values.TryGetValue(key,out value))continue;
                        Dictionary<string,object> saved;if(!phonePreferences.TryGetValue(id,out saved))phonePreferences[id]=saved=new Dictionary<string,object>();
                        saved[key=="toggleTorch"?"torch":key]=key=="toggleTorch"?(object)isTorchOn:value;
                    }
                }catch{}
            }
            if(receiver!=null)receiver.SendControl(id,payload);
        }
        private void RestorePhonePreferences(string id) {
            Dictionary<string,object> values;
            lock(workspaceGate){if(!phonePreferences.TryGetValue(id,out values))return;values=new Dictionary<string,object>(values);}
            bool restoreTorch=values.ContainsKey("torch");
            values.Remove("torch");
            if(values.Count>0 && receiver!=null)receiver.SendControl(id,workspaceJson.Serialize(values));
            if(!restoreTorch)return;
            restoreTorchPending.Add(id);
            if(id.StartsWith("web:"))return; // The next browser catalog carries torch capabilities.
            PhoneSlot slot;lock(devicesGate)if(!phoneSlots.TryGetValue(id,out slot))return;
            string host=slot.Info.Host;if(string.IsNullOrEmpty(host))return;
            System.Threading.ThreadPool.QueueUserWorkItem(_ => {
                try {
                    int port=host=="127.0.0.1"?AdbTunnel.FindCameraCatalogPort(id):8080;
                    if(port<=0)return;
                    Dictionary<string,object> catalog;
                    using(var client=new CameraWebClient())catalog=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(client.DownloadString("http://"+host+":"+port+"/cameras"));
                    string active=CameraText(catalog,"activeId");bool flash=false;object cameras;
                    if(catalog.TryGetValue("cameras",out cameras) && cameras is System.Collections.IEnumerable)
                        foreach(object item in (System.Collections.IEnumerable)cameras) {var row=item as Dictionary<string,object>;if(row!=null && CameraText(row,"id")==active)flash=CameraFlag(row,"flash");}
                    bool hasFlash=flash;
                    SafeBeginInvoke(() => {
                        PhoneSlot current;lock(devicesGate)if(!phoneSlots.TryGetValue(id,out current) || !current.Connected || current.Info.Host!=host)return;
                        RestorePhoneTorch(id,CameraFlag(catalog,"torch"),hasFlash);
                    });
                }catch { /* The selected phone's regular catalog poll can retry this restoration. */ }
            });
        }
        private void RestorePhoneTorch(string id,bool actual,bool hasFlash) {
            if(!restoreTorchPending.Remove(id))return;
            Dictionary<string,object> values;object desired;
            lock(workspaceGate)if(phonePreferences.TryGetValue(id??"",out values) && values.TryGetValue("torch",out desired) && Convert.ToBoolean(desired)!=actual && (hasFlash || actual))
                receiver.SendControl(id,"{\"toggleTorch\":true}");
        }
        private void RestoreAudioPreferences(string id) {
            if(id!=null && id.StartsWith("web:"))return;
            if(id!=mainDeviceId)return;
            if(restorePcAudioPending && !isPcAudioStreaming)TogglePcAudio();
            if(restoreMicPending && !isMicStreaming)ToggleMic();
        }
    }
}
