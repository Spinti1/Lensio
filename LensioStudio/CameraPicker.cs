using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace ApexCam
{
    public partial class MainForm
    {
        private Button cameraPicker;
        private string connectedPhoneHost;
        private int cameraFetchPending;
        private int cameraPollTicks;
        private int cameraCatalogGeneration;
        private string confirmedCameraId;
        private bool updatingCameraZoom;
        private ContextMenuStrip cameraMenu;
        private readonly JavaScriptSerializer cameraJson = new JavaScriptSerializer { MaxJsonLength = 128*1024 };

        private sealed class CameraWebClient : WebClient {
            protected override WebRequest GetWebRequest(Uri uri) {
                var request=base.GetWebRequest(uri);
                request.Timeout=2500;
                var http=request as HttpWebRequest;
                if(http!=null) { http.ReadWriteTimeout=2500; http.Proxy=null; }
                return request;
            }
        }
        private static string CameraText(Dictionary<string,object> row,string name) {
            object value;
            return row.TryGetValue(name,out value) && value!=null ? Convert.ToString(value,System.Globalization.CultureInfo.InvariantCulture) : "";
        }
        private static bool CameraFlag(Dictionary<string,object> row,string name) {
            object value; return row.TryGetValue(name,out value) && value is bool && (bool)value;
        }
        private void InitializeCameraPicker()
        {
            cameraPicker=CreateModernButton("Choose phone camera...",ColorButtonDark,4,26,220,23);
            ctrlContent.Controls.Add(cameraPicker);
            cameraPicker.Click += (s,e) => RefreshCameraCatalog(true);
            Disposed += (s,e) => { if(cameraMenu!=null) cameraMenu.Dispose(); };
        }
        private void RefreshCameraCatalog(bool showMenu)
        {
            if(!showMenu && cameraMenu!=null && cameraMenu.Visible) return;
            if(controlledDeviceId!=null && controlledDeviceId.StartsWith("web:")) {
                var webCatalog=webCameraBridge==null?null:webCameraBridge.GetCatalog(controlledDeviceId);
                if(webCatalog==null){cameraPicker.Text="Pair iPhone / Web to list cameras";return;}
                BuildCameraMenu(webCatalog);
                if(showMenu)cameraMenu.Show(cameraPicker,new System.Drawing.Point(0,cameraPicker.Height));
                return;
            }
            string host=connectedPhoneHost;
            if(string.IsNullOrEmpty(host) || receiver==null || !receiver.IsConnected) {
                cameraPicker.Text="Connect phone to list cameras";
                return;
            }
            if(Interlocked.CompareExchange(ref cameraFetchPending,1,0)!=0) return;
            int generation=cameraCatalogGeneration;
            string target=receiver.ActiveDeviceId;
            ThreadPool.QueueUserWorkItem(_ => {
                try {
                    string json;
                    int catalogPort=host=="127.0.0.1" ? AdbTunnel.FindCameraCatalogPort(target) : 8080;
                    if(catalogPort<=0)throw new InvalidOperationException("No USB catalog tunnel for selected device");
                    using(var client=new CameraWebClient()) {
                        client.Encoding=System.Text.Encoding.UTF8;
                        json=client.DownloadString("http://"+host+":"+catalogPort+"/cameras");
                    }
                    var catalog=cameraJson.Deserialize<Dictionary<string,object>>(json);
                    SafeBeginInvoke(() => {
                        if(generation!=cameraCatalogGeneration || host!=connectedPhoneHost || !receiver.IsConnected) return;
                        BuildCameraMenu(catalog);
                        if(showMenu) cameraMenu.Show(cameraPicker,new System.Drawing.Point(0,cameraPicker.Height));
                    });
                } catch(Exception) {
                    SafeBeginInvoke(() => {
                        if(generation!=cameraCatalogGeneration) return;
                        cameraPicker.Text="Camera list unavailable - retry";
                        sourceToolTips.SetToolTip(cameraPicker,"Install the matching LensioCam update, then click to retry. Phone HTTP port 8080 must be reachable.");
                    });
                } finally { Interlocked.Exchange(ref cameraFetchPending,0); }
            });
        }
        private void BuildCameraMenu(Dictionary<string,object> catalog)
        {
            if(cameraMenu!=null) cameraMenu.Dispose();
            cameraMenu=new ContextMenuStrip {
                BackColor=ColorHeaderBg, ForeColor=ColorTextPrimary, ShowCheckMargin=true, ShowImageMargin=false,
                Renderer=new StudioMenuRenderer()
            };
            string menuDevice=controlledDeviceId;
            string active=CameraText(catalog,"activeId");
            string error=CameraText(catalog,"error");
            object raw;
            if(!catalog.TryGetValue("cameras",out raw) || !(raw is System.Collections.IEnumerable))
                throw new InvalidOperationException("Invalid camera catalog");
            Dictionary<string,object> selected=null;
            cameraPicker.Text="Choose phone camera...";
            foreach(object value in (System.Collections.IEnumerable)raw) {
                var row=value as Dictionary<string,object>; if(row==null) continue;
                string id=CameraText(row,"id"), label=CameraText(row,"label");
                bool enabled=CameraFlag(row,"selectable");
                var item=new ToolStripMenuItem(label) { Enabled=enabled, Checked=id==active };
                item.ToolTipText=CameraText(row,"megapixels")+" MP | "+CameraText(row,"focalMm")+" mm"+
                    " | OIS: "+(CameraFlag(row,"ois")?"reported by phone":"unavailable")+
                    " | EIS: "+(CameraFlag(row,"eis")?"reported by phone":"unavailable")+
                    (enabled ? "" : "\n"+CameraText(row,"reason"));
                item.Click += (s,e) => {
                    string target=menuDevice;
                    SendPhoneControl(target,cameraJson.Serialize(new Dictionary<string,object> { {"cameraId",id} }));
                    cameraPicker.Text="Switching camera...";
                    // Read the active camera reported by the phone instead of guessing from its numeric ID.
                    var timer=new System.Windows.Forms.Timer { Interval=1200 };
                    int checks=0;
                    timer.Tick += (ts,te) => {
                        if(IsDisposed || ++checks>=4) { timer.Stop(); timer.Dispose(); }
                        if(!IsDisposed) RefreshCameraCatalog(false);
                    };
                    timer.Start();
                };
                cameraMenu.Items.Add(item);
                if(id==active) {
                    selected=row;
                    cameraPicker.Text=label;
                    sourceToolTips.SetToolTip(cameraPicker,label+"\n"+item.ToolTipText+"\nClick to choose another camera.");
                    isFrontCam=CameraText(row,"facing")=="Front";
                    if(confirmedCameraId!=id) {
                        updatingCameraZoom=true;
                        try {
                            double maximum;
                            if(!double.TryParse(CameraText(row,"maxDigitalZoom"),System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture,out maximum)) maximum=1;
                            tbZoom.Maximum=(int)Math.Max(10,Math.Min(1000,maximum*10));
                            double zoom;
                            if(!double.TryParse(CameraText(catalog,"zoom"),System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture,out zoom)) zoom=1;
                            tbZoom.Value=Math.Max(tbZoom.Minimum,Math.Min(tbZoom.Maximum,(int)(zoom*10)));
                            currentZoom=tbZoom.Value/10f;
                            lblZoomVal.Text=currentZoom.ToString("0.0")+"x";
                            isTorchOn=CameraFlag(catalog,"torch");
                            btnTorch.Text=isTorchOn ? "Flashlight: ON" : "Flashlight: OFF";
                            btnTorch.BackColor=ColorButtonDark;
                            btnTorch.Enabled=CameraFlag(row,"flash");
                            confirmedCameraId=id;
                        } finally {updatingCameraZoom=false;}
                    }
                }
            }
            cameraMenu.Items.Add(new ToolStripSeparator());
            var stability=new ToolStripMenuItem("Stabilization (reported by phone)") { Enabled=selected!=null };
            string mode=CameraText(catalog,"stabilization");
            RestorePhoneTorch(menuDevice,CameraFlag(catalog,"torch"),selected!=null && CameraFlag(selected,"flash"));
            string[] modes={"off","eis","ois"}, labels={"Off","Electronic (EIS)","Optical (OIS)"};
            for(int i=0;i<modes.Length;i++) {
                string key=modes[i];
                var item=new ToolStripMenuItem(labels[i]) {
                    Enabled=key=="off" || (selected!=null && CameraFlag(selected,key)), Checked=mode==key
                };
                item.Click += (s,e) => {
                    SendPhoneControl(menuDevice,cameraJson.Serialize(new Dictionary<string,object>{{"stabilization",key}}));
                };
                stability.DropDownItems.Add(item);
            }
            cameraMenu.Items.Add(stability);
            ApplyMenuTheme(cameraMenu);
            cameraMenu.Items.Add(new ToolStripMenuItem("~ = approximate field of view vs main camera") { Enabled=false });
            if(!string.IsNullOrEmpty(error)) cameraMenu.Items.Add(new ToolStripMenuItem(error) {Enabled=false});
        }
    }
}
