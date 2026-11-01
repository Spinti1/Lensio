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
        private bool webReceiverStarting;private int webReceiverGeneration;private string pairingConnectionDetails="Starting phone connection...";
        private sealed class PairingQrCanvas : Panel {
            public PairingQrCanvas(){SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw,true);}
        }
        private void InitializePhonePairingUi(){
            pairingQrPanel=new Panel{BackColor=ColorCardBg,Padding=new Padding(2)};
            var title=new Label{Text="Scan with Android / iPhone",Dock=DockStyle.Top,Height=20,ForeColor=ColorTextPrimary,TextAlign=ContentAlignment.MiddleCenter};
            pairingQrStatus=new Label{Text="Starting phone connection...",Dock=DockStyle.Bottom,Height=20,AutoEllipsis=true,ForeColor=ColorTextSecondary,TextAlign=ContentAlignment.MiddleCenter};
            qrCanvas=new PairingQrCanvas{Dock=DockStyle.Fill,BackColor=ColorCardBg,Cursor=Cursors.Hand};
            qrCanvas.Paint+=(s,e)=>DrawPairingQr(e.Graphics,qrCanvas.ClientRectangle);
            qrCanvas.Click+=(s,e)=>ShowPairingQrLarge();
            pairingQrStatus.Cursor=Cursors.Hand;pairingQrStatus.Click+=(s,e)=>ShowPairingConnectionDetails();
            var repairMenu=new ContextMenuStrip();repairMenu.Items.Add("Connection details / retry...",null,(s,e)=>ShowPairingConnectionDetails());repairMenu.Items.Add("Install / repair Microsoft WebView2...",null,(s,e)=>RepairWebReceiverRuntime());ApplyMenuTheme(repairMenu);qrCanvas.ContextMenuStrip=repairMenu;qrCanvas.Disposed+=(s,e)=>repairMenu.Dispose();
            pairingQrPanel.Controls.Add(qrCanvas);pairingQrPanel.Controls.Add(title);pairingQrPanel.Controls.Add(pairingQrStatus);
            qrViewButton=CreateModernButton("Camera",ColorButtonDark,0,3,76,22);
            qrViewButton.Click+=(s,e)=>{showPairingQr=!showPairingQr;LayoutPhoneCards(Math.Max(180,ctrlContent.ClientSize.Width-8));};
            dockControls.Controls.Add(pairingQrPanel);dockControls.Controls.Add(qrViewButton);
            sourceToolTips.SetToolTip(qrCanvas,"Scan this QR with any phone. Click to enlarge. The same QR supports several devices.");
            Disposed+=(s,e)=>{if(embeddedWebReceiver!=null)embeddedWebReceiver.Dispose();if(pairingQrImage!=null)pairingQrImage.Dispose();};
        }
        private void DrawPairingQr(Graphics graphics,Rectangle bounds){
            if(pairingQrImage==null){TextRenderer.DrawText(graphics,"Pairing QR unavailable. Click status for details.",Font,bounds,ColorTextSecondary,TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter);return;}
            // The generator uses four pixels per module, including the quiet zone.
            // Integer module sizes keep small QR codes sharp and scannable.
            graphics.Clear(ColorCardBg);
            int modules=pairingQrImage.Width/4;
            int edge=modules*Math.Max(1,Math.Min(bounds.Width,bounds.Height)/modules);var target=new Rectangle(bounds.X+(bounds.Width-edge)/2,bounds.Y+(bounds.Height-edge)/2,edge,edge);
            graphics.InterpolationMode=InterpolationMode.NearestNeighbor;graphics.PixelOffsetMode=PixelOffsetMode.Half;graphics.DrawImage(pairingQrImage,target);
        }
        private void ShowPairingQrLarge(){
            if(pairingQrImage==null)return;
            using(var dialog=new Form{Text="Connect phones to LensioStudio",StartPosition=FormStartPosition.CenterParent,ClientSize=new Size(390,440),BackColor=ColorCardBg,ForeColor=ColorTextPrimary,ShowInTaskbar=false,MinimizeBox=false,MaximizeBox=false,FormBorderStyle=FormBorderStyle.FixedDialog}){
                var instruction=new Label{Dock=DockStyle.Top,Height=48,Text="Scan with Android or iPhone, then press Start camera.\nThe same QR connects multiple phones.",TextAlign=ContentAlignment.MiddleCenter};
                var canvas=new PairingQrCanvas{Dock=DockStyle.Fill,Padding=new Padding(12)};canvas.Paint+=(s,e)=>DrawPairingQr(e.Graphics,Rectangle.Inflate(canvas.ClientRectangle,-12,-12));
                var done=new Button{Dock=DockStyle.Bottom,Height=32,Text="Done",DialogResult=DialogResult.OK};dialog.Controls.Add(canvas);dialog.Controls.Add(instruction);dialog.Controls.Add(done);dialog.AcceptButton=done;dialog.ShowDialog(this);
            }
        }
        private void LayoutPairingQr(int width,int phones){
            if(pairingQrPanel==null)return;
            int top=167+phones*32,remaining=ctrlContent.ClientSize.Height-top;
            int minimumQrEdge=pairingQrImage==null?106:pairingQrImage.Width/2;
            bool inline=remaining>=minimumQrEdge+44;
            if(inline){
                if(pairingQrPanel.Parent!=ctrlContent){pairingQrPanel.Dock=DockStyle.None;ctrlContent.Controls.Add(pairingQrPanel);}
                pairingQrPanel.SetBounds(4,top,width,remaining);pairingQrPanel.Visible=true;ctrlContent.Visible=true;qrViewButton.Visible=false;
            }else{
                if(pairingQrPanel.Parent!=dockControls){dockControls.Controls.Add(pairingQrPanel);pairingQrPanel.Dock=DockStyle.Fill;}
                ctrlContent.Visible=!showPairingQr;pairingQrPanel.Visible=showPairingQr;qrViewButton.Visible=true;qrViewButton.Text=showPairingQr?"Camera":"Scan QR";
                qrViewButton.SetBounds(Math.Max(8,dockControls.ClientSize.Width-84),3,76,22);qrViewButton.BringToFront();
            }
        }
        private void SetPairingStatus(string text){
            if(IsDisposed)return;pairingConnectionDetails=text;pairingQrStatus.Text=text;sourceToolTips.SetToolTip(pairingQrStatus,text+"\nClick for details and retry.");
        }
        private void SetPairingQr(Bitmap image){var previous=pairingQrImage;pairingQrImage=image;LayoutPhoneCards(Math.Max(180,ctrlContent.ClientSize.Width-8));if(previous!=null)previous.Dispose();qrCanvas.Invalidate();}
        private void InitializePairingConnection(){
            try{if(webCameraBridge==null)webCameraBridge=new WebCameraBridge(0,workspaceDirectory);SetPairingQr(PairingQrCode.Render(webCameraBridge.PairingLink));SetPairingStatus("Starting web receiver...");}
            catch(Exception e){SetPairingStatus("Pairing unavailable: "+e.Message);}
        }
        private void ShowPairingConnectionDetails(){
            bool retry=MessageBox.Show(this,pairingConnectionDetails+"\n\nRetry the web receiver? The QR is generated locally; web-camera reception requires Microsoft WebView2 and internet access for discovery.","Phone connection",MessageBoxButtons.YesNo,MessageBoxIcon.Information)==DialogResult.Yes;
            if(retry){if(webCameraBridge==null)InitializePairingConnection();StartEmbeddedReceiver();}
        }
        private async void RepairWebReceiverRuntime(){
            if(webReceiverStarting||IsDisposed)return;
            if(MessageBox.Show(this,"Install or repair Microsoft WebView2? The signed Microsoft installer needs internet access and runs for this Windows user.","Web camera setup",MessageBoxButtons.YesNo,MessageBoxIcon.Information)!=DialogResult.Yes)return;
            webReceiverStarting=true;bool installed=false;
            try{SetPairingStatus("Installing Microsoft WebView2...");installed=await System.Threading.Tasks.Task.Run(()=>BundledSetup.InstallWebViewRuntime());if(!installed)SetPairingStatus("WebView2 setup did not complete. Check internet access and retry.");}
            catch(Exception error){SetPairingStatus("WebView2 setup failed: "+error.Message);}
            finally{webReceiverStarting=false;}
            if(installed&&!IsDisposed)StartEmbeddedReceiver();
        }
        private async void StartEmbeddedReceiver(){
            if(webReceiverStarting||IsDisposed)return;
            if(webCameraBridge==null||!webCameraBridge.IsRunning){SetPairingStatus("Camera receiver unavailable. Restart LensioStudio and check whether another app is using port 8556.");return;}
            webReceiverStarting=true;int generation=++webReceiverGeneration;
            try {
                if(embeddedWebReceiver!=null)embeddedWebReceiver.Dispose();
                embeddedWebReceiver=new EmbeddedWebReceiver();
                embeddedWebReceiver.Status+=text=>{if(!IsDisposed&&generation==webReceiverGeneration)SetPairingStatus(text);};
                SetPairingStatus("Starting web receiver...");
                await embeddedWebReceiver.StartAsync(this,webCameraBridge.EngineUrl,Path.Combine(workspaceDirectory,"web-receiver"));
            }catch(Exception e){if(!IsDisposed)SetPairingStatus("Web receiver unavailable: "+e.Message);}
            finally{webReceiverStarting=false;}
        }
    }
}
