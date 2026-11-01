using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
namespace ApexCam {
    public partial class MainForm {
        private readonly Dictionary<string,LocalCameraCapture> localCameras=new Dictionary<string,LocalCameraCapture>();
        private static bool IsLocalCamera(string id){return id!=null&&id.StartsWith("local:",StringComparison.Ordinal);}
        private void ShowLocalCameraPicker(){
            using(var dialog=new Form{Text="Add PC / laptop webcam",ClientSize=new Size(420,150),MinimumSize=new Size(420,185),StartPosition=FormStartPosition.CenterParent,BackColor=ColorCardBg,ForeColor=ColorTextPrimary,ShowInTaskbar=false}){
                var list=new ComboBox{Left=16,Top=16,Width=388,DropDownStyle=ComboBoxStyle.DropDownList};
                var status=new Label{Left=16,Top=52,Width=388,Height=42,Text="Looking for available webcams..."};
                var add=new Button{Left=284,Top=106,Width=120,Height=28,Text="Add webcam",Enabled=false};
                dialog.Controls.AddRange(new Control[]{list,status,add});StyleSourceDialog(dialog);
                dialog.Shown+=(s,e)=>System.Threading.ThreadPool.QueueUserWorkItem(_=>{
                    try{var devices=LocalCameraCapture.GetDevices();if(!dialog.IsDisposed)dialog.BeginInvoke((Action)(()=>{foreach(var d in devices)list.Items.Add(d);if(list.Items.Count>0){list.SelectedIndex=0;add.Enabled=true;status.Text="Windows camera permissions apply. Each webcam is opened once.";}else status.Text="No webcam found. Connect one and check Windows camera privacy settings.";}));}catch{if(!dialog.IsDisposed)try{dialog.BeginInvoke((Action)(()=>status.Text="Unable to list webcams. Check Windows camera privacy settings."));}catch{}}
                });
                add.Click+=(s,e)=>{var info=list.SelectedItem as LocalCameraInfo;if(info==null)return;AddLocalCamera(info);dialog.Close();};
                dialog.AcceptButton=add;dialog.ShowDialog(this);
            }
        }
        private void AddLocalCamera(LocalCameraInfo info){
            PhoneSlot slot;lock(devicesGate){
                if(!phoneSlots.TryGetValue(info.Id,out slot)){
                    bool main=mainDeviceId==null;if(main)mainDeviceId=info.Id;
                    slot=new PhoneSlot{Info=new PhoneDeviceInfo{Id=info.Id,Name=info.Name,Host=""},Layer=CreatePhoneLayer(new PhoneDeviceInfo{Id=info.Id,Name=info.Name},main,phoneSlots.Count)};
                    phoneSlots.Add(info.Id,slot);AddPhoneLayer(slot.Layer,main);
                }else if(slot.Layer.IsDisposed){slot.Layer=CreatePhoneLayer(slot.Info,mainDeviceId==info.Id,0);AddPhoneLayer(slot.Layer,mainDeviceId==info.Id);}
            }
            if(mainDeviceId==null)mainDeviceId=info.Id;
            selectedLayer=slot.Layer;
            if(!localCameras.ContainsKey(info.Id)){
                slot.Connected=true;
                LocalCameraCapture capture=null;capture=new LocalCameraCapture(info.Id,frame=>ProcessDeviceFrame(info.Id,frame,0,false,0,30,0),error=>SafeBeginInvoke(()=>{
                    LocalCameraCapture active;if(!localCameras.TryGetValue(info.Id,out active)||!object.ReferenceEquals(active,capture))return;
                    PhoneSlot current;lock(devicesGate)if(phoneSlots.TryGetValue(info.Id,out current)){current.Connected=false;current.Layer.SetCameraFrame(null);}
                    LocalCameraCapture old;if(localCameras.TryGetValue(info.Id,out old)){localCameras.Remove(info.Id);old.Dispose();}
                    RefreshPhoneCards();lblStatusPill.Text="Webcam unavailable";
                    sourceToolTips.SetToolTip(lblStatusPill,"Close other apps using the webcam and check Windows camera privacy settings. "+error.Message);
                }),currentResWidth,currentResHeight);
                localCameras.Add(info.Id,capture);capture.Start();
            }
            SelectControlledPhone(info.Id);RefreshPhoneCards();RefreshSourcesList();
        }
        private void RestoreLocalCameras(){
            var selected=selectedLayer;string controlled=controlledDeviceId;
            var sources=scene.GetLayersSnapshot();foreach(var layer in sources)if(IsLocalCamera(layer.DeviceId))AddLocalCamera(new LocalCameraInfo{Id=layer.DeviceId,Name=layer.Name});
            if(controlled!=null)SelectControlledPhone(controlled);selectedLayer=selected;RefreshSourcesList();
        }
        private void StopLocalCameras(){foreach(var camera in localCameras.Values)camera.Dispose();localCameras.Clear();}
        private void RemoveSource(ObsLayer layer){
            if(layer==null)return;
            if(IsLocalCamera(layer.DeviceId)){
                LocalCameraCapture camera;if(localCameras.TryGetValue(layer.DeviceId,out camera)){localCameras.Remove(layer.DeviceId);camera.Dispose();}
                lock(devicesGate)phoneSlots.Remove(layer.DeviceId);
                if(mainDeviceId==layer.DeviceId){mainDeviceId=null;lock(devicesGate)foreach(var pair in phoneSlots)if(pair.Value.Connected){mainDeviceId=pair.Key;break;}}
                if(controlledDeviceId==layer.DeviceId){controlledDeviceId=null;connectedPhoneHost=null;}
                RefreshPhoneCards();
            }
            scene.RemoveLayer(layer);
        }
    }
}
