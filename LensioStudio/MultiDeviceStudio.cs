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
            public int Battery=-1, Fps;
            public bool? Portrait;
            public float Temperature;
            public bool Charging;
            public readonly ActualFrameRate DecodedRate=new ActualFrameRate(),RenderedRate=new ActualFrameRate();
            public long RenderedRevision=-1;
        }
        private readonly object devicesGate=new object();
        private readonly Dictionary<string,PhoneSlot> phoneSlots=new Dictionary<string,PhoneSlot>();
        private string mainDeviceId, controlledDeviceId;
        private Panel phoneCards;
        private FramePump compositionTimer;
        private int composing;
        private Bitmap compositionSpare;
        private sealed class ActualFrameRate {
            private readonly object gate=new object();private long started=System.Diagnostics.Stopwatch.GetTimestamp(),last;private int count,rate;
            public void Tick(){lock(gate){long now=System.Diagnostics.Stopwatch.GetTimestamp();last=now;count++;long elapsed=now-started;if(elapsed>=System.Diagnostics.Stopwatch.Frequency){rate=(int)Math.Round(count*(double)System.Diagnostics.Stopwatch.Frequency/elapsed);count=0;started=now;}}}
            public int Value {get{lock(gate)return last==0||System.Diagnostics.Stopwatch.GetTimestamp()-last>2*System.Diagnostics.Stopwatch.Frequency?0:rate;}}
            public void Reset(){lock(gate){started=System.Diagnostics.Stopwatch.GetTimestamp();last=0;count=rate=0;}}
        }
        private readonly ActualFrameRate cameraFrameRate=new ActualFrameRate(),sceneFrameRate=new ActualFrameRate();
        private string measuredMainId;private long measuredMainRevision=-1;
        private bool hasSceneComposition;
        private volatile bool compositionStopped;
        private void InitializeDeviceComposition()
        {
            LoadPhoneNames();
            phoneCards=new Panel {BackColor=ColorCardBg};
            sourceToolTips.SetToolTip(btnTogglePcAudio,"PC audio is sent to the Main phone.");
            sourceToolTips.SetToolTip(btnToggleMic,"Microphone audio is received from the Main phone.");
            ctrlContent.Controls.Add(phoneCards);
            InitializePhonePairingUi();
            compositionTimer=new FramePump(30,ComposeDevices);
            Disposed += (s,e) => StopDeviceComposition();
            Disposed += (s,e) => {lock(frameLock){if(previewScaler!=null)previewScaler.Dispose();previewSource=null;if(compositionSpare!=null){compositionSpare.Dispose();compositionSpare=null;}if(currentFrame!=null){currentFrame.Dispose();currentFrame=null;}}};
        }
        private void StopDeviceComposition() {
            compositionStopped=true;
            if(compositionTimer!=null) {
                var timer=compositionTimer;compositionTimer=null;
                timer.Dispose();
            }
        }
        private ObsLayer CreatePhoneLayer(PhoneDeviceInfo info,bool main,int index) {
            return new ObsLayer {Type=SourceType.Camera,DeviceId=info.Id,Name=FriendlyPhoneName(info),
                X=main?0:1392,Y=main?0:36+index*290,Width=main?1920:480,Height=main?1080:270};
        }
        private void AddPhoneLayer(ObsLayer layer,bool main){
            var existing=scene.GetLayersSnapshot();scene.AddLayer(layer);
            int behind=main?existing.Count:0;
            for(int i=0;i<behind;i++)scene.MoveDown(layer);
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
                    AddPhoneLayer(slot.Layer,main);
                } else {
                    slot.Info=info;slot.Connected=true;
                    if(slot.Layer.IsDisposed) {slot.Layer=CreatePhoneLayer(info,mainDeviceId==info.Id,0);AddPhoneLayer(slot.Layer,mainDeviceId==info.Id);}
                }
            }
            RestorePhonePreferences(info.Id);
            if(controlledDeviceId==null || controlledDeviceId==info.Id) SelectControlledPhone(info.Id);
            RestoreAudioPreferences(info.Id);
            if(info.Id==mainDeviceId) {
                if(isPcAudioStreaming)SendPhoneControl(info.Id,"{\"streamPcAudio\":true}");
                if(isMicStreaming)SendPhoneControl(info.Id,"{\"enableMic\":true}");
            }
            try { System.IO.File.AppendAllText(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"app.log"),
                DateTime.Now+" - [Phone] "+info.Name+" id="+info.Id+" host="+info.Host+Environment.NewLine); } catch {}
            showPairingQr=false;
            RefreshPhoneCards();
            RefreshSourcesList();
        }
        private void HandlePhoneDisconnected(string id)
        {
            lock(devicesGate) {PhoneSlot slot;if(phoneSlots.TryGetValue(id,out slot)){slot.Connected=false;slot.Battery=-1;slot.Charging=false;slot.Layer.SetCameraFrame(null);slot.DecodedRate.Reset();slot.RenderedRate.Reset();slot.RenderedRevision=-1;}}
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
            bool local=IsLocalCamera(id);btnTorch.Enabled=tbZoom.Enabled=btnToggleMic.Enabled=btnTogglePcAudio.Enabled=!local;
            if(IsLocalCamera(id)){
                connectedPhoneHost=null;cameraCatalogGeneration++;selectedLayer=slot.Layer;
                cameraPicker.Text="PC / laptop webcam";RefreshSourcesList();RefreshPhoneCards();return;
            }
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
            var order=scene.GetLayersSnapshot();
            Array.Sort(slots,(a,b)=>order.IndexOf(a.Layer).CompareTo(order.IndexOf(b.Layer)));
            int at=0;
            foreach(var slot in slots) {
                var row=new Panel {BackColor=ColorHeaderBg,Tag=slot.Info.Id};
                var name=CreateModernButton(FriendlyPhoneName(slot.Info),
                    controlledDeviceId==slot.Info.Id?ColorAccentCyan:ColorButtonDark,0,0,100,27);
                bool dragged=false;Point dragOrigin=Point.Empty;
                name.MouseDown+=(s,e)=>{if(e.Button==MouseButtons.Left){dragOrigin=e.Location;dragged=false;}};
                name.MouseMove+=(s,e)=>{
                    if(e.Button!=MouseButtons.Left||dragged)return;
                    var threshold=new Rectangle(dragOrigin.X-SystemInformation.DragSize.Width/2,dragOrigin.Y-SystemInformation.DragSize.Height/2,SystemInformation.DragSize.Width,SystemInformation.DragSize.Height);
                    if(threshold.Contains(e.Location))return;
                    dragged=true;name.DoDragDrop(new CameraOrderDrag{Owner=this,Id=slot.Info.Id},DragDropEffects.Move);
                };
                BindCameraDrop(row,slot.Info.Id);BindCameraDrop(name,slot.Info.Id);
                name.Click += (s,e) => {
                    if(dragged)return;
                    if(slot.Layer.IsDisposed) {slot.Layer=CreatePhoneLayer(slot.Info,slot.Info.Id==mainDeviceId,0);scene.AddLayer(slot.Layer);}
                    SelectControlledPhone(slot.Info.Id);
                };
                name.FlatAppearance.BorderColor=mainDeviceId==slot.Info.Id ? ColorAccentEmerald : ColorCardBorder;
                name.FlatAppearance.BorderSize=mainDeviceId==slot.Info.Id ? 2 : 1;
                name.AutoEllipsis=true;
                name.TextAlign=ContentAlignment.MiddleLeft;
                sourceToolTips.SetToolTip(name,(slot.Connected?"":"Offline. ")+
                    "Drag to reorder cameras: top is in front. Click to select. Right-click for properties and controls.");
                row.Controls.Add(name);
                var battery=new PhoneBatteryIndicator {Name="PhoneBattery",BackColor=ColorHeaderBg,ForeColor=ColorTextPrimary,Font=name.Font,Tag=slot.Info.Id};
                row.Controls.Add(battery);BindCameraDrop(battery,slot.Info.Id);
                var menu=new ContextMenuStrip();
                var mainItem=new ToolStripMenuItem("Use as main camera") {Checked=mainDeviceId==slot.Info.Id,Enabled=slot.Connected};
                mainItem.Click += (s,e) => {MakeMainPhone(slot.Info.Id);SelectControlledPhone(slot.Info.Id);};
                menu.Items.Add("Properties...",null,(s,e)=>{SelectControlledPhone(slot.Info.Id);ShowSourceProperties();});
                menu.Items.Add("Show / hide",null,(s,e)=>{slot.Layer.Visible=!slot.Layer.Visible;CommitSceneEdit();RefreshPhoneCards();});
                menu.Items.Add(mainItem);
                menu.Items.Add("Rename phone...",null,(s,e) => RenamePhone(slot.Info));
                menu.Items.Add("Camera / lens...",null,(s,e) => {
                    SelectControlledPhone(slot.Info.Id);
                    RefreshCameraCatalog(true);
                }).Enabled=slot.Connected && !IsLocalCamera(slot.Info.Id);
                var resolution=new ToolStripMenuItem("Capture resolution") {Enabled=slot.Connected && !IsLocalCamera(slot.Info.Id)};
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
                ApplyMenuTheme(menu);row.ContextMenuStrip=name.ContextMenuStrip=battery.ContextMenuStrip=menu;
                row.Disposed += (s,e) => menu.Dispose();
                row.Top=at++*32;row.Height=29;
                phoneCards.Controls.Add(row);
            }
            int required=slots.Length==0?480:Math.Max(480,515+slots.Length*32);
            MinimumSize=new Size(640,required);
            LayoutStudioDocks();RefreshPhoneBatteryIndicators();
        }
        private sealed class PhoneBatteryIndicator : Control {
            public int Level {get;private set;}
            public bool Charging {get;private set;}
            public bool Connected {get;private set;}
            public int CameraFps {get;private set;}
            public bool HasBattery {get;private set;}
            public PhoneBatteryIndicator(){SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer,true);Level=-2;AccessibleRole=AccessibleRole.StaticText;}
            public bool UpdateStatus(int level,bool charging,bool connected,int fps,bool hasBattery){
                level=level<0?-1:Math.Min(100,level);
                fps=Math.Max(0,fps);
                if(Level==level&&Charging==charging&&Connected==connected&&CameraFps==fps&&HasBattery==hasBattery)return false;
                Level=level;Charging=charging;Connected=connected;CameraFps=fps;HasBattery=hasBattery;
                AccessibleName=!connected?"Phone offline":fps+" camera FPS"+(hasBattery?", "+(level<0?"battery unavailable":level+" percent battery"+(charging?", charging":"")):"");
                Invalidate();return true;
            }
            protected override void OnPaint(PaintEventArgs e){
                var g=e.Graphics;g.Clear(BackColor);g.SmoothingMode=SmoothingMode.AntiAlias;
                var textFlags=TextFormatFlags.VerticalCenter|TextFormatFlags.Right|TextFormatFlags.NoPadding;
                if(!Connected){TextRenderer.DrawText(g,"Offline",Font,ClientRectangle,ColorTextSecondary,textFlags);return;}
                int fpsWidth=TextRenderer.MeasureText(g,"30 FPS",Font,Size.Empty,TextFormatFlags.NoPadding).Width+8;
                TextRenderer.DrawText(g,CameraFps+" FPS",Font,new Rectangle(0,0,HasBattery?fpsWidth:Width,Height),ForeColor,textFlags);
                if(!HasBattery)return;
                int offset=fpsWidth+4;
                Color color=!Connected||Level<0?ColorTextSecondary:Level<=15?ColorAccentRed:Level<=30?ColorAccentAmber:ColorAccentEmerald;
                int y=(Height-12)/2;
                using(var outline=new Pen(color,1.3f))using(var fill=new SolidBrush(color)){
                    g.DrawRectangle(outline,offset+3,y,21,12);g.FillRectangle(fill,offset+25,y+4,2,4);
                    if(Connected&&Level>=0)g.FillRectangle(fill,offset+5,y+2,17*Level/100f,8);
                }
                if(Connected&&Charging)using(var bolt=new SolidBrush(Color.White))using(var shadow=new Pen(ColorHeaderBg,2)){
                    var points=new[]{new Point(offset+16,y-2),new Point(offset+9,y+7),new Point(offset+14,y+7),new Point(offset+11,y+15),new Point(offset+20,y+4),new Point(offset+15,y+4)};
                    g.DrawPolygon(shadow,points);g.FillPolygon(bolt,points);
                }
                TextRenderer.DrawText(g,Level<0?"N/A":Level+"%",Font,new Rectangle(offset+31,0,Math.Max(1,Width-offset-31),Height),ForeColor,textFlags);
            }
        }
        private void RefreshPhoneBatteryIndicators(){
            if(phoneCards==null)return;
            foreach(Control row in phoneCards.Controls){
                if(row.Controls.Count<2)continue;
                var indicator=row.Controls[1] as PhoneBatteryIndicator;if(indicator==null||!indicator.Visible)continue;
                int level,fps,decoded;bool charging,connected;PhoneSlot slot;
                lock(devicesGate){if(!phoneSlots.TryGetValue((string)row.Tag,out slot))continue;level=slot.Battery;charging=slot.Charging;connected=slot.Connected;fps=connected&&slot.Layer.Visible?slot.RenderedRate.Value:0;decoded=slot.DecodedRate.Value;}
                if(slot.Info.Id.StartsWith("web:"))level=-1; // Safari does not expose device battery telemetry.
                indicator.UpdateStatus(level,charging,connected,fps,!IsLocalCamera(slot.Info.Id));
                int received=connected&&receiver!=null&&!IsLocalCamera(slot.Info.Id)?receiver.DeviceReceivedFps(slot.Info.Id):0;
                string details=indicator.AccessibleName+(connected?"\n"+(!IsLocalCamera(slot.Info.Id)?"Received: "+received+" FPS. ":"")+"Decoded: "+decoded+" FPS. Displayed: "+fps+" FPS. Counts new camera frames only.":"");
                if(sourceToolTips.GetToolTip(indicator)!=details)sourceToolTips.SetToolTip(indicator,details);
            }
        }
        private sealed class CameraOrderDrag {public MainForm Owner;public string Id;}
        private bool ReorderPhoneCards(string moving,string target,bool after) {
            PhoneSlot source,destination;
            lock(devicesGate){if(!phoneSlots.TryGetValue(moving,out source)||!phoneSlots.TryGetValue(target,out destination))return false;}
            return scene.ReorderCamera(source.Layer,destination.Layer,after);
        }
        private void BindCameraDrop(Control control,string target) {
            control.AllowDrop=true;
            Action<int> marker=edge=>{
                var row=control is Panel?control:control.Parent;
                var button=row!=null&&row.Controls.Count>0?row.Controls[0] as StudioButton:null;
                if(button!=null&&button.DragInsertionEdge!=edge){button.DragInsertionEdge=edge;button.Invalidate();}
            };
            control.DragLeave+=(s,e)=>marker(0);
            control.DragEnter+=(s,e)=>{
                var data=e.Data.GetData(typeof(CameraOrderDrag)) as CameraOrderDrag;
                e.Effect=data!=null&&data.Owner==this&&data.Id!=target?DragDropEffects.Move:DragDropEffects.None;
            };
            control.DragOver+=(s,e)=>{
                var data=e.Data.GetData(typeof(CameraOrderDrag)) as CameraOrderDrag;
                e.Effect=data!=null&&data.Owner==this&&data.Id!=target?DragDropEffects.Move:DragDropEffects.None;
                marker(e.Effect==DragDropEffects.None?0:control.PointToClient(new Point(e.X,e.Y)).Y<control.Height/2?-1:1);
            };
            control.DragDrop+=(s,e)=>{
                marker(0);
                var data=e.Data.GetData(typeof(CameraOrderDrag)) as CameraOrderDrag;
                if(data==null||data.Owner!=this)return;
                bool after=control.PointToClient(new Point(e.X,e.Y)).Y>=control.Height/2;
                if(!ReorderPhoneCards(data.Id,target,after))return;
                // Commit once on drop; dragging does not issue network commands or save repeatedly.
                BeginInvoke((Action)(()=>{RefreshPhoneCards();CommitSceneEdit();}));
            };
        }

        private void RemoveOfflinePhone(string id)
        {
            PhoneSlot slot;
            lock(devicesGate) {if(!phoneSlots.TryGetValue(id,out slot) || slot.Connected)return;}
            if(id==mainDeviceId)MakeSecondaryPhone(id);
            lock(devicesGate)phoneSlots.Remove(id);
            if(!slot.Layer.IsDisposed)RemoveSource(slot.Layer);
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

            phoneCards.SetBounds(4,167,width,count*32);
            foreach(Control row in phoneCards.Controls) {
                row.Width=width;
                int fpsWidth=TextRenderer.MeasureText("30 FPS",row.Controls[0].Font,Size.Empty,TextFormatFlags.NoPadding).Width+8;
                int batteryWidth=TextRenderer.MeasureText("100%",row.Controls[0].Font,Size.Empty,TextFormatFlags.NoPadding).Width+40;
                int badge=row.Controls.Count>1?fpsWidth+(IsLocalCamera((string)row.Tag)?0:batteryWidth):0;
                row.Controls[0].SetBounds(0,1,Math.Max(40,width-badge-4),27);
                if(row.Controls.Count>1)row.Controls[1].SetBounds(width-badge,1,badge,27);
            }
            LayoutPairingQr(width,count);
        }
        private void ProcessDeviceFrame(string id,Bitmap raw,int battery,bool charging,float temp,int fps,int rtt)
        {
            PhoneSlot slot;lock(devicesGate){if(!phoneSlots.TryGetValue(id,out slot))return;}
            if(compositionStopped)return;
            Bitmap filtered=VideoFilters.ApplyFiltersAndTransform(raw,currentFilter,1f,mirrorH,flipV,
                brightnessVal,contrastVal,saturationVal,exposureVal,isAutoMode,raw.Width,raw.Height);
            bool portrait=raw.Height>raw.Width;
            lock(devicesGate){
                if(slot.Portrait!=portrait){
                    var bounds=slot.Layer.GetBounds();float centerX=bounds.X+bounds.Width/2,centerY=bounds.Y+bounds.Height/2;
                    float aspect=raw.Width/(float)raw.Height;
                    float width=bounds.Width,height=bounds.Height;
                    if(Math.Abs(width/Math.Max(1,height)-aspect)>.01f){
                        width=height*aspect;
                        if(width>1920){height*=1920/width;width=1920;}
                        if(height>1080){width*=1080/height;height=1080;}
                        slot.Layer.SetBounds(new RectangleF(centerX-width/2,centerY-height/2,width,height));
                    }
                    slot.Portrait=portrait;
                }
                slot.Layer.SetCameraFrame(filtered);
            }
            lock(devicesGate){slot.Battery=battery;slot.Fps=fps;slot.Temperature=temp;slot.Charging=charging;slot.DecodedRate.Tick();}
            if(id==mainDeviceId){batteryPercent=battery;phoneFps=fps;deviceTemp=temp;isPhoneCharging=charging;latencyMs=rtt;}
        }
        private void ComposeDevices()
        {
            if(!workspaceReady || compositionStopped || Interlocked.CompareExchange(ref composing,1,0)!=0)return;
            Bitmap image=null;
            try {
                int count=scene.LayerCount;
                if(count==0 && !hasSceneComposition)return;
                hasSceneComposition=count>0;
                lock(frameLock){image=compositionSpare;compositionSpare=null;}
                if(image!=null&&(image.Width!=currentResWidth||image.Height!=currentResHeight)){image.Dispose();image=null;}
                if(image==null)image=new Bitmap(currentResWidth,currentResHeight,System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                using(var g=Graphics.FromImage(image)) {g.Clear(Color.Black);scene.RenderScene(g,image.Width,image.Height);}
                if(compositionStopped)return;
                if(isVirtualCamActive && vcamBridge!=null)vcamBridge.WriteFrame(image);
                sceneFrameRate.Tick();
                lock(devicesGate){
                    foreach(var slot in phoneSlots.Values){
                        if(!slot.Connected||!slot.Layer.Visible)continue;
                        long revision=slot.Layer.RenderedRevision;
                        if(revision>=0&&revision!=slot.RenderedRevision){slot.RenderedRevision=revision;slot.RenderedRate.Tick();}
                    }
                    if(measuredMainId!=mainDeviceId){measuredMainId=mainDeviceId;measuredMainRevision=-1;cameraFrameRate.Reset();}
                    PhoneSlot main;
                    if(mainDeviceId!=null&&phoneSlots.TryGetValue(mainDeviceId,out main)&&main.Connected&&main.Layer.Visible){
                        long revision=main.Layer.RenderedRevision;
                        if(revision>=0 && revision!=measuredMainRevision){measuredMainRevision=revision;cameraFrameRate.Tick();}
                    }
                }
                PublishSceneFrame(image);image=null;
                RequestPreviewRefresh();
            } catch(Exception) { /* Connection teardown must not terminate the compositor. */ }
            finally {if(image!=null)image.Dispose();Interlocked.Exchange(ref composing,0);}
        }
        private void RequestPreviewRefresh(){
            if(!IsHandleCreated || IsDisposed || Interlocked.CompareExchange(ref previewRefreshPending,1,0)!=0)return;
            try{BeginInvoke((Action)(()=>{Interlocked.Exchange(ref previewRefreshPending,0);if(!compositionStopped)previewPanel.Invalidate();}));}
            catch{Interlocked.Exchange(ref previewRefreshPending,0);}
        }
        private void CommitSceneEdit(){SaveWorkspace();RequestPreviewRefresh();}
        private void DrawCameraGuides(Graphics g)
        {
            if(isFullscreen)return;
            var canvas=CanvasToScreenRect(new RectangleF(0,0,1920,1080));
            using(var pen=new Pen(Color.FromArgb(170,160,176,196),1f))g.DrawRectangle(pen,canvas.X,canvas.Y,canvas.Width-1,canvas.Height-1);
            if(isDraggingLayer && (!float.IsNaN(snapGuideX)||!float.IsNaN(snapGuideY)))using(var guide=new Pen(ColorAccentCyan,1.5f)){
                if(!float.IsNaN(snapGuideX)){float x=canvas.X+snapGuideX*canvas.Width/1920;g.DrawLine(guide,x,canvas.Top,x,canvas.Bottom);}
                if(!float.IsNaN(snapGuideY)){float y=canvas.Y+snapGuideY*canvas.Height/1080;g.DrawLine(guide,canvas.Left,y,canvas.Right,y);}
            }
            if(selectedLayer==null || selectedLayer.Type!=SourceType.Camera)return;
            var bounds=selectedLayer.VisibleBounds;
            var state=g.Save();g.SetClip(canvas);
            using(var pen=new Pen(Color.FromArgb(160,255,255,255),1f)) {
                pen.DashStyle=DashStyle.Dash;
                for(int i=1;i<3;i++){
                    float x=bounds.X+bounds.Width*i/3,y=bounds.Y+bounds.Height*i/3;
                    PointF a=selectedLayer.RotatePoint(new PointF(x,bounds.Top),false),b=selectedLayer.RotatePoint(new PointF(x,bounds.Bottom),false);
                    PointF c=selectedLayer.RotatePoint(new PointF(bounds.Left,y),false),d=selectedLayer.RotatePoint(new PointF(bounds.Right,y),false);
                    a=CanvasToScreenRect(new RectangleF(a,SizeF.Empty)).Location;b=CanvasToScreenRect(new RectangleF(b,SizeF.Empty)).Location;
                    c=CanvasToScreenRect(new RectangleF(c,SizeF.Empty)).Location;d=CanvasToScreenRect(new RectangleF(d,SizeF.Empty)).Location;
                    g.DrawLine(pen,a,b);g.DrawLine(pen,c,d);
                }
            }
            g.Restore(state);
        }
    }
}
