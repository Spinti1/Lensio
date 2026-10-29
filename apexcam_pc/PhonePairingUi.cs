using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;
namespace ApexCam {
    public partial class MainForm {
        private WebCameraBridge webCameraBridge;
        private EmbeddedWebReceiver embeddedWebReceiver;
        private Panel pairingQrPanel,qrCanvas;
        private Button qrViewButton;
        private Label pairingQrStatus;
        private Bitmap pairingQrImage;
        private bool showPairingQr=true;
        private void InitializePhonePairingUi(){
            pairingQrPanel=new Panel{BackColor=ColorCardBg,Padding=new Padding(2)};
            var title=new Label{Text="Scan with Android / iPhone",Dock=DockStyle.Top,Height=20,ForeColor=ColorTextPrimary,TextAlign=ContentAlignment.MiddleCenter};
            pairingQrStatus=new Label{Text="Starting phone connection...",Dock=DockStyle.Bottom,Height=20,AutoEllipsis=true,ForeColor=ColorTextSecondary,TextAlign=ContentAlignment.MiddleCenter};
            qrCanvas=new Panel{Dock=DockStyle.Fill,BackColor=ColorCardBg,Cursor=Cursors.Hand};
            qrCanvas.Paint+=(s,e)=>DrawPairingQr(e.Graphics,qrCanvas.ClientRectangle);
            qrCanvas.Click+=(s,e)=>ShowPairingQrLarge();
            pairingQrPanel.Controls.Add(qrCanvas);pairingQrPanel.Controls.Add(title);pairingQrPanel.Controls.Add(pairingQrStatus);
            qrViewButton=CreateModernButton("Camera",ColorButtonDark,0,3,76,22);
            qrViewButton.Click+=(s,e)=>{showPairingQr=!showPairingQr;LayoutPhoneCards(Math.Max(180,ctrlContent.ClientSize.Width-8));};
            dockControls.Controls.Add(pairingQrPanel);dockControls.Controls.Add(qrViewButton);
            sourceToolTips.SetToolTip(qrCanvas,"Scan this QR with any phone. Click to enlarge. The same QR supports several devices.");
            Disposed+=(s,e)=>{if(embeddedWebReceiver!=null)embeddedWebReceiver.Dispose();if(pairingQrImage!=null)pairingQrImage.Dispose();};
        }
        private void DrawPairingQr(Graphics graphics,Rectangle bounds){
            if(pairingQrImage==null){TextRenderer.DrawText(graphics,"QR is starting...",Font,bounds,ColorTextSecondary,TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter);return;}
            int edge=Math.Max(1,Math.Min(bounds.Width,bounds.Height));var target=new Rectangle(bounds.X+(bounds.Width-edge)/2,bounds.Y+(bounds.Height-edge)/2,edge,edge);
            graphics.InterpolationMode=InterpolationMode.NearestNeighbor;graphics.PixelOffsetMode=PixelOffsetMode.Half;graphics.DrawImage(pairingQrImage,target);
        }
        private void ShowPairingQrLarge(){
            if(pairingQrImage==null)return;
            using(var dialog=new Form{Text="Connect phones to LensioStudio",StartPosition=FormStartPosition.CenterParent,ClientSize=new Size(390,440),BackColor=ColorCardBg,ForeColor=ColorTextPrimary,ShowInTaskbar=false,MinimizeBox=false,MaximizeBox=false,FormBorderStyle=FormBorderStyle.FixedDialog}){
                var instruction=new Label{Dock=DockStyle.Top,Height=48,Text="Scan with Android or iPhone, then press Start camera.\nThe same QR connects multiple phones.",TextAlign=ContentAlignment.MiddleCenter};
                var canvas=new Panel{Dock=DockStyle.Fill,Padding=new Padding(12)};canvas.Paint+=(s,e)=>DrawPairingQr(e.Graphics,Rectangle.Inflate(canvas.ClientRectangle,-12,-12));
                var done=new Button{Dock=DockStyle.Bottom,Height=32,Text="Done",DialogResult=DialogResult.OK};dialog.Controls.Add(canvas);dialog.Controls.Add(instruction);dialog.Controls.Add(done);dialog.AcceptButton=done;dialog.ShowDialog(this);
            }
        }
        private void LayoutPairingQr(int width,int phones){
            if(pairingQrPanel==null)return;
            int top=167+phones*32,remaining=ctrlContent.ClientSize.Height-top;
            bool inline=remaining>=148;
            if(inline){
                if(pairingQrPanel.Parent!=ctrlContent){pairingQrPanel.Dock=DockStyle.None;ctrlContent.Controls.Add(pairingQrPanel);}
                pairingQrPanel.SetBounds(4,top,width,remaining);pairingQrPanel.Visible=true;ctrlContent.Visible=true;qrViewButton.Visible=false;
            }else{
                if(pairingQrPanel.Parent!=dockControls){dockControls.Controls.Add(pairingQrPanel);pairingQrPanel.Dock=DockStyle.Fill;}
                ctrlContent.Visible=!showPairingQr;pairingQrPanel.Visible=showPairingQr;qrViewButton.Visible=true;qrViewButton.Text=showPairingQr?"Camera":"Scan QR";
                qrViewButton.SetBounds(Math.Max(8,dockControls.ClientSize.Width-84),3,76,22);qrViewButton.BringToFront();
            }
        }
        private async void StartEmbeddedReceiver(){
            try {
                embeddedWebReceiver=new EmbeddedWebReceiver();
                embeddedWebReceiver.Status+=text=>{if(IsDisposed)return;pairingQrStatus.Text=text;sourceToolTips.SetToolTip(pairingQrStatus,text);};
                embeddedWebReceiver.QrReady+=image=>{var previous=pairingQrImage;pairingQrImage=image;if(previous!=null)previous.Dispose();qrCanvas.Invalidate();};
                await embeddedWebReceiver.StartAsync(this,webCameraBridge.EngineUrl,Path.Combine(workspaceDirectory,"web-receiver"));
            }catch(Exception e){pairingQrStatus.Text="Web receiver unavailable";sourceToolTips.SetToolTip(pairingQrStatus,e.Message+"\nMicrosoft Edge WebView2 Runtime is required.");}
        }
    }
}
