using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading;
using System.Windows.Forms;

namespace ApexCam
{
    public partial class MainForm
    {
        private sealed class PhoneSlot {
            public PhoneDeviceInfo Info;
            public ObsLayer Layer;
            public bool Connected;
            public int Battery, Fps;
            public float Temperature;
            public bool Charging;
        }
        private readonly object devicesGate=new object();
        private readonly Dictionary<string,PhoneSlot> phoneSlots=new Dictionary<string,PhoneSlot>();
        private string mainDeviceId, controlledDeviceId;
        private Panel phoneCards;
        private Button addPhone;
        private System.Threading.Timer compositionTimer;
        private int composing;
        private volatile bool compositionStopped;
        private void InitializeDeviceComposition()
        {
            LoadPhoneNames();
            phoneCards=new Panel {BackColor=ColorCardBg};
            addPhone=CreateModernButton("Add phone...",ColorButtonDark,0,0,220,25);
            addPhone.Click += (s,e) => MessageBox.Show(
                "Open LensioCam on another phone on the same Wi-Fi network. It will appear here automatically.\n\n"+
                "For a manual connection, enter this PC address in LensioCam: "+GetLocalIp()+
                "\n\nUp to four phones can stream together. Click a phone name to make it the main camera, then drag or resize cameras in the preview.",
                "Add phone",MessageBoxButtons.OK,MessageBoxIcon.Information);
            addPhone.Visible=false;
            sourceToolTips.SetToolTip(btnTogglePcAudio,"PC audio is sent to the Main phone.");
            sourceToolTips.SetToolTip(btnToggleMic,"Microphone audio is received from the Main phone.");
            ctrlContent.Controls.Add(phoneCards);
            ctrlContent.Controls.Add(addPhone);
            compositionTimer=new System.Threading.Timer(_ => ComposeDevices(),null,33,33);
            Disposed += (s,e) => StopDeviceComposition();
            Disposed += (s,e) => {lock(frameLock){if(previewScaler!=null)previewScaler.Dispose();previewSource=null;}};
        }
        private void StopDeviceComposition() {
            compositionStopped=true;
            if(compositionTimer!=null) {
                var timer=compositionTimer;compositionTimer=null;
                var done=new ManualResetEvent(false);
                if(timer.Dispose(done) && done.WaitOne(2000))done.Dispose();
            }
        }
        private ObsLayer CreatePhoneLayer(PhoneDeviceInfo info,bool main,int index) {
            return new ObsLayer {Type=SourceType.Camera,DeviceId=info.Id,Name=FriendlyPhoneName(info),
                X=main?0:1392,Y=main?0:36+index*290,Width=main?1920:480,Height=main?1080:270};
        }
        private void HandlePhoneConnected(PhoneDeviceInfo info)
        {
            if(compositionStopped)return;
            PhoneSlot slot;
            lock(devicesGate) {
                if(!phoneSlots.TryGetValue(info.Id,out slot)) {
                    bool main=mainDeviceId==null;
                    if(main)mainDeviceId=info.Id;
                    slot=new PhoneSlot {Info=info,Connected=true,Layer=CreatePhoneLayer(info,main,Math.Max(0,phoneSlots.Count-1))};
                    phoneSlots.Add(info.Id,slot);
                    scene.AddLayer(slot.Layer);
                } else {
                    slot.Info=info;slot.Connected=true;
                    if(slot.Layer.IsDisposed) {slot.Layer=CreatePhoneLayer(info,mainDeviceId==info.Id,0);scene.AddLayer(slot.Layer);}
                }
            }
            RestorePhonePreferences(info.Id);
            if(controlledDeviceId==null || controlledDeviceId==info.Id) SelectControlledPhone(info.Id);
            RestoreAudioPreferences(info.Id);
            SendPhoneControl(info.Id,"{\"check_update\":true,\"host\":\""+GetLocalIp()+"\"}");
            if(info.Id==mainDeviceId) {
                if(isPcAudioStreaming)SendPhoneControl(info.Id,"{\"streamPcAudio\":true}");
                if(isMicStreaming)SendPhoneControl(info.Id,"{\"enableMic\":true}");
            }
            try { System.IO.File.AppendAllText(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"app.log"),
                DateTime.Now+" - [Phone] "+info.Name+" id="+info.Id+" host="+info.Host+Environment.NewLine); } catch {}
            RefreshPhoneCards();
            RefreshSourcesList();
        }
        private void HandlePhoneDisconnected(string id)
        {
            lock(devicesGate) {PhoneSlot slot;if(phoneSlots.TryGetValue(id,out slot)){slot.Connected=false;slot.Layer.SetCameraFrame(null);}}
            if(controlledDeviceId==id) {
                connectedPhoneHost=null;cameraCatalogGeneration++;
                cameraPicker.Text="Phone disconnected";
            }
            RefreshPhoneCards();
        }
        private void SelectControlledPhone(string id)
        {
            PhoneSlot slot;lock(devicesGate){if(!phoneSlots.TryGetValue(id,out slot))return;}
            controlledDeviceId=id;
            if(receiver!=null)receiver.ActiveDeviceId=id;
            connectedPhoneHost=slot.Connected?slot.Info.Host:null;
            txtPhoneIp.Text=slot.Info.Host;
            confirmedCameraId=null;cameraCatalogGeneration++;
            selectedLayer=slot.Layer;
            cameraPicker.Text="Choose phone camera...";
            if(slot.Connected)RefreshCameraCatalog(false);
            RefreshSourcesList();
            RefreshPhoneCards();
        }
        private void MakeMainPhone(string id)
        {
            if(id==mainDeviceId)return;
            PhoneSlot next,previous;
            lock(devicesGate) {
                if(!phoneSlots.TryGetValue(id,out next) || !next.Connected)return;
                phoneSlots.TryGetValue(mainDeviceId??"",out previous);
            }
            // Stop audio on the former main before moving its ownership.
            bool audio=isPcAudioStreaming,mic=isMicStreaming;
            if(audio)TogglePcAudio();
            if(mic)ToggleMic();
            var bounds=next.Layer.GetBounds();
            if(previous!=null) {next.Layer.SetBounds(previous.Layer.GetBounds());previous.Layer.SetBounds(bounds);}
            else next.Layer.SetBounds(new RectangleF(0,0,1920,1080));
            mainDeviceId=id;
            var layers=scene.GetLayersSnapshot();
            for(int i=0;i<layers.Count;i++)scene.MoveDown(next.Layer);
            if(audio)TogglePcAudio();
            if(mic)ToggleMic();
            RestoreAudioPreferences(id);
            RefreshPhoneCards();
            previewPanel.Invalidate();
        }
        private void MakeSecondaryPhone(string id) {
            if(id!=mainDeviceId)return;
            string other=null;
            lock(devicesGate)foreach(var pair in phoneSlots)if(pair.Key!=id && pair.Value.Connected){other=pair.Key;break;}
            if(other!=null)MakeMainPhone(other);
        }
        private void RefreshPhoneCards()
        {
            if(phoneCards==null)return;
            while(phoneCards.Controls.Count>0)phoneCards.Controls[0].Dispose();
            PhoneSlot[] slots;lock(devicesGate){slots=new PhoneSlot[phoneSlots.Count];phoneSlots.Values.CopyTo(slots,0);}
            int at=0;
            foreach(var slot in slots) {
                var row=new Panel {BackColor=ColorHeaderBg,Tag=slot.Info.Id};
                var name=CreateModernButton(FriendlyPhoneName(slot.Info),
                    controlledDeviceId==slot.Info.Id?ColorAccentCyan:ColorButtonDark,0,0,100,27);
                name.Click += (s,e) => {
                    if(slot.Layer.IsDisposed) {slot.Layer=CreatePhoneLayer(slot.Info,slot.Info.Id==mainDeviceId,0);scene.AddLayer(slot.Layer);}
                    if(slot.Connected)MakeMainPhone(slot.Info.Id);
                    SelectControlledPhone(slot.Info.Id);
                };
                name.FlatAppearance.BorderColor=mainDeviceId==slot.Info.Id ? ColorAccentEmerald : ColorCardBorder;
                name.FlatAppearance.BorderSize=mainDeviceId==slot.Info.Id ? 2 : 1;
                name.AutoEllipsis=true;
                name.TextAlign=ContentAlignment.MiddleLeft;
                sourceToolTips.SetToolTip(name,(slot.Connected?"":"Offline. ")+
                    "Click to make "+FriendlyPhoneName(slot.Info)+" the main camera. Right-click for camera controls and rename.");
                row.Controls.Add(name);
                var menu=new ContextMenuStrip();
                var mainItem=new ToolStripMenuItem("Use as main camera") {Checked=mainDeviceId==slot.Info.Id,Enabled=slot.Connected};
                mainItem.Click += (s,e) => {MakeMainPhone(slot.Info.Id);SelectControlledPhone(slot.Info.Id);};
                menu.Items.Add(mainItem);
                menu.Items.Add("Rename phone...",null,(s,e) => RenamePhone(slot.Info));
                menu.Items.Add("Camera / lens...",null,(s,e) => {
                    SelectControlledPhone(slot.Info.Id);
                    RefreshCameraCatalog(true);
                }).Enabled=slot.Connected;
                var resolution=new ToolStripMenuItem("Capture resolution") {Enabled=slot.Connected};
                resolution.DropDownItems.Add("720p HD",null,(s,e) => SendPhoneControl(slot.Info.Id,"{\"resolution\":\"720p\"}"));
                resolution.DropDownItems.Add("1080p Full HD",null,(s,e) => SendPhoneControl(slot.Info.Id,"{\"resolution\":\"1080p\"}"));
                menu.Items.Add(resolution);
                menu.Items.Add("Fit to canvas",null,(s,e) => {
                    slot.Layer.SetBounds(new RectangleF(0,0,1920,1080)); selectedLayer=slot.Layer; RefreshSourcesList();
                });
                menu.Items.Add("Show in scene",null,(s,e) => {
                    if(slot.Layer.IsDisposed) {slot.Layer=CreatePhoneLayer(slot.Info,slot.Info.Id==mainDeviceId,0);scene.AddLayer(slot.Layer);}
                    slot.Layer.Visible=true;SelectControlledPhone(slot.Info.Id);
                });
                var remove=menu.Items.Add("Remove offline phone",null,(s,e) => RemoveOfflinePhone(slot.Info.Id));
                remove.Enabled=!slot.Connected;
                ApplyMenuTheme(menu);row.ContextMenuStrip=name.ContextMenuStrip=menu;
                row.Disposed += (s,e) => menu.Dispose();
                row.Top=at++*32;row.Height=29;
                phoneCards.Controls.Add(row);
            }
            int required=slots.Length==0?480:Math.Max(480,515+slots.Length*32);
            MinimumSize=new Size(640,required);
            LayoutStudioDocks();
        }
        private void RemoveOfflinePhone(string id)
        {
            PhoneSlot slot;
            lock(devicesGate) {if(!phoneSlots.TryGetValue(id,out slot) || slot.Connected)return;}
            if(id==mainDeviceId)MakeSecondaryPhone(id);
            lock(devicesGate)phoneSlots.Remove(id);
            if(!slot.Layer.IsDisposed)scene.RemoveLayer(slot.Layer);
            if(mainDeviceId==id)mainDeviceId=null;
            if(controlledDeviceId==id) {
                controlledDeviceId=null;connectedPhoneHost=null;
                if(receiver!=null)receiver.ActiveDeviceId=null;
                selectedLayer=null;
            }
            RefreshPhoneCards();RefreshSourcesList();
        }

        private void LayoutPhoneCards(int width)
        {
            if(phoneCards==null)return;
            int count=phoneCards.Controls.Count;
            addPhone.Visible=count>0;
            phoneCards.SetBounds(4,167,width,count*32);
            foreach(Control row in phoneCards.Controls) {
                row.Width=width;
                row.Controls[0].SetBounds(0,1,width,27);
            }
            addPhone.SetBounds(4,167+count*32,width,25);
        }
        private void ProcessDeviceFrame(string id,Bitmap raw,int battery,bool charging,float temp,int fps,int rtt)
        {
            PhoneSlot slot;lock(devicesGate){if(!phoneSlots.TryGetValue(id,out slot))return;}
            if(compositionStopped)return;
            Bitmap filtered=VideoFilters.ApplyFiltersAndTransform(raw,currentFilter,1f,mirrorH,flipV,
                brightnessVal,contrastVal,saturationVal,exposureVal,isAutoMode,raw.Width,raw.Height);
            slot.Layer.SetCameraFrame(filtered);
            slot.Battery=battery;slot.Fps=fps;slot.Temperature=temp;slot.Charging=charging;
            if(id==mainDeviceId){batteryPercent=battery;phoneFps=fps;deviceTemp=temp;isPhoneCharging=charging;latencyMs=rtt;}
        }
        private void ComposeDevices()
        {
            if(compositionStopped || Interlocked.CompareExchange(ref composing,1,0)!=0)return;
            Bitmap image=null;
            try {
                lock(devicesGate)if(phoneSlots.Count==0)return;
                image=new Bitmap(currentResWidth,currentResHeight,System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                using(var g=Graphics.FromImage(image)) {g.Clear(Color.Black);scene.RenderScene(g,image.Width,image.Height);}
                if(compositionStopped)return;
                if(isVirtualCamActive && vcamBridge!=null)vcamBridge.WriteFrame(image);
                lock(frameLock){var old=currentFrame;currentFrame=image;image=null;if(old!=null)old.Dispose();}
                if(Interlocked.CompareExchange(ref previewRefreshPending,1,0)==0)SafeBeginInvoke(() => {
                    Interlocked.Exchange(ref previewRefreshPending,0);
                    if(!compositionStopped)previewPanel.Invalidate();
                });
            } catch(Exception) { /* Connection teardown must not terminate the compositor. */ }
            finally {if(image!=null)image.Dispose();Interlocked.Exchange(ref composing,0);}
        }
        private void DrawCameraGuides(Graphics g)
        {
            if(isFullscreen)return;
            var canvas=CanvasToScreenRect(new RectangleF(0,0,1920,1080));
            using(var pen=new Pen(Color.FromArgb(170,160,176,196),1f))g.DrawRectangle(pen,canvas.X,canvas.Y,canvas.Width-1,canvas.Height-1);
            if(selectedLayer==null || selectedLayer.Type!=SourceType.Camera)return;
            var bounds=CanvasToScreenRect(selectedLayer.GetBounds());
            var state=g.Save();g.SetClip(canvas);
            using(var pen=new Pen(Color.FromArgb(160,255,255,255),1f)) {
                pen.DashStyle=DashStyle.Dash;
                for(int i=1;i<3;i++){
                    float x=bounds.X+bounds.Width*i/3,y=bounds.Y+bounds.Height*i/3;
                    g.DrawLine(pen,x,bounds.Top,x,bounds.Bottom);
                    g.DrawLine(pen,bounds.Left,y,bounds.Right,y);
                }
            }
            g.Restore(state);
        }
    }
}
