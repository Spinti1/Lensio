using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
namespace ApexCam {
    public partial class MainForm {
        private bool croppingDrag;
        private float dragCropLeft,dragCropTop,dragCropRight,dragCropBottom;
        private float snapGuideX=float.NaN,snapGuideY=float.NaN;
        private List<RectangleF> snapTargets;
        private Keys shortcutHeld;
        public static RectangleF MagneticSnap(RectangleF b,IEnumerable<RectangleF> targets,float threshold,out float guideX,out float guideY){
            guideX=guideY=float.NaN;float bestX=threshold+.001f,bestY=bestX,dx=0,dy=0;
            MatchAxis(b.Left,b.Width,0,ref bestX,ref dx,ref guideX);MatchAxis(b.Left,b.Width,960,ref bestX,ref dx,ref guideX);MatchAxis(b.Left,b.Width,1920,ref bestX,ref dx,ref guideX);
            MatchAxis(b.Top,b.Height,0,ref bestY,ref dy,ref guideY);MatchAxis(b.Top,b.Height,540,ref bestY,ref dy,ref guideY);MatchAxis(b.Top,b.Height,1080,ref bestY,ref dy,ref guideY);
            if(targets!=null)foreach(var t in targets){
                MatchAxis(b.Left,b.Width,t.Left,ref bestX,ref dx,ref guideX);MatchAxis(b.Left,b.Width,t.Left+t.Width/2,ref bestX,ref dx,ref guideX);MatchAxis(b.Left,b.Width,t.Right,ref bestX,ref dx,ref guideX);
                MatchAxis(b.Top,b.Height,t.Top,ref bestY,ref dy,ref guideY);MatchAxis(b.Top,b.Height,t.Top+t.Height/2,ref bestY,ref dy,ref guideY);MatchAxis(b.Top,b.Height,t.Bottom,ref bestY,ref dy,ref guideY);
            }
            b.Offset(dx,dy);return b;
        }
        private static void MatchAxis(float start,float size,float target,ref float best,ref float delta,ref float guide){
            for(int i=0;i<3;i++){float d=target-start-size*i/2,dist=Math.Abs(d);if(dist<best){best=dist;delta=d;guide=target;}}
        }
        private void BeginSourceDrag(float x,float y){
            dragStartMouse=new PointF(x,y);dragStartBounds=selectedLayer.VisibleBounds;
            dragCropLeft=selectedLayer.CropLeft;dragCropTop=selectedLayer.CropTop;dragCropRight=selectedLayer.CropRight;dragCropBottom=selectedLayer.CropBottom;
            croppingDrag=activeHandle!=TransformHandle.Body&&(ModifierKeys&Keys.Alt)!=0;
            snapTargets=new List<RectangleF>();foreach(var layer in scene.GetLayersSnapshot())if(layer!=selectedLayer&&layer.Visible&&!layer.IsDisposed)snapTargets.Add(layer.VisualBounds);
            previewPanel.Capture=true;
        }
        private Point LocalHandlePoint(Point p){
            float x,y;ScreenToCanvasCoords(p.X,p.Y,out x,out y);var local=selectedLayer.RotatePoint(new PointF(x,y),true);
            var rect=CanvasToScreenRect(new RectangleF(local.X,local.Y,0,0));return new Point((int)rect.X,(int)rect.Y);
        }
        private void DrawSelection(Graphics g){
            DrawCameraGuides(g);
            if(selectedLayer==null||isFullscreen||!selectedLayer.Visible||selectedLayer.IsDisposed)return;
            var c=selectedLayer.VisibleCorners();for(int i=0;i<c.Length;i++){var r=CanvasToScreenRect(new RectangleF(c[i].X,c[i].Y,0,0));c[i]=r.Location;}
            Color color=selectedLayer.Locked?ColorTextSecondary:(croppingDrag?ColorAccentEmerald:ColorAccentRed);
            using(var pen=new Pen(color,2f))g.DrawPolygon(pen,c);
            if(selectedLayer.Locked)return;
            using(var pen=new Pen(color,1.5f))using(var brush=new SolidBrush(Color.White)){
                for(int i=0;i<4;i++){var next=c[(i+1)%4];var mid=new PointF((c[i].X+next.X)/2,(c[i].Y+next.Y)/2);foreach(var p in new[]{c[i],mid}){g.FillRectangle(brush,p.X-4,p.Y-4,8,8);g.DrawRectangle(pen,p.X-4,p.Y-4,8,8);}}
            }
        }
        private void InitializeTransformMenu(){
            // One place for common transforms; source-specific properties stay separate.
            var transform=new ToolStripMenuItem("Transform");
            transform.DropDownItems.Add("Rotate 90° clockwise",null,(s,e)=>RotateSelected(90));
            transform.DropDownItems.Add("Rotate 90° counterclockwise",null,(s,e)=>RotateSelected(-90));
            transform.DropDownItems.Add("Rotation angle...",null,(s,e)=>ShowRotationAngle());
            transform.DropDownItems.Add("Reset rotation",null,(s,e)=>{if(selectedLayer!=null){selectedLayer.Rotation=0;CommitSceneEdit();}});
            transform.DropDownItems.Add("Reset crop",null,(s,e)=>{if(selectedLayer!=null){selectedLayer.CropLeft=selectedLayer.CropRight=selectedLayer.CropTop=selectedLayer.CropBottom=0;CommitSceneEdit();}});
            transform.DropDownItems.Add("Lock / unlock",null,(s,e)=>{if(selectedLayer!=null){selectedLayer.Locked=!selectedLayer.Locked;CommitSceneEdit();}});
            // Move existing fit/center actions into the same submenu, rather than duplicate them.
            var fit=sourceContextMenu.Items[1];sourceContextMenu.Items.Remove(fit);transform.DropDownItems.Insert(0,fit);
            var center=sourceContextMenu.Items[1];sourceContextMenu.Items.Remove(center);transform.DropDownItems.Insert(1,center);
            sourceContextMenu.Items.Insert(1,transform);
            sourceContextMenu.Items.Add("Visibility shortcut...",null,(s,e)=>ShowVisibilityShortcut());
            foreach(ToolStripItem item in sourceContextMenu.Items)if(item.Text=="Show / hide")item.Text="Show / hide (Ctrl+Shift+H)";
            sourceToolTips.SetToolTip(previewPanel,"Drag to move. Drag a handle to resize. Alt + drag a handle crops. Alt + move bypasses snapping. Ctrl+Shift+H shows/hides the selected source.");
        }
        private void StyleSourceDialog(Control parent){
            parent.BackColor=ColorCardBg;parent.ForeColor=ColorTextPrimary;
            foreach(Control c in parent.Controls){
                StyleSourceDialog(c);
                if(c is Button){var b=(Button)c;b.UseVisualStyleBackColor=false;b.BackColor=ColorButtonDark;b.FlatStyle=FlatStyle.Flat;b.FlatAppearance.BorderColor=ColorCardBorder;b.FlatAppearance.BorderSize=1;}
                if(c is NumericUpDown || c is TextBoxBase || c is ComboBox)c.BackColor=ColorButtonDark;
            }
            ApplyStudioControlStyle(parent);
        }
        private void RotateSelected(float degrees){if(selectedLayer==null||selectedLayer.Locked)return;selectedLayer.Rotation=(selectedLayer.Rotation+degrees+360)%360;CommitSceneEdit();}
        private void ShowRotationAngle(){
            if(selectedLayer==null)return;var layer=selectedLayer;
            using(var dialog=new Form{Text="Source rotation",ClientSize=new Size(300,105),StartPosition=FormStartPosition.CenterParent,BackColor=ColorCardBg,ForeColor=ColorTextPrimary,ShowInTaskbar=false,FormBorderStyle=FormBorderStyle.FixedDialog,MaximizeBox=false,MinimizeBox=false}){
                var angle=new NumericUpDown{Left=16,Top=16,Width=268,Minimum=-360,Maximum=360,DecimalPlaces=1,Value=(decimal)layer.Rotation};
                var apply=new Button{Left=164,Top=62,Width=120,Text="Apply",DialogResult=DialogResult.OK};dialog.Controls.AddRange(new Control[]{angle,apply});StyleSourceDialog(dialog);dialog.AcceptButton=apply;
                if(dialog.ShowDialog(this)==DialogResult.OK){layer.Rotation=(float)angle.Value;CommitSceneEdit();}
            }
        }
        private static bool ValidSourceShortcut(Keys key){Keys code=key&Keys.KeyCode;return ((key&(Keys.Control|Keys.Alt))!=0)&&(code>=Keys.A&&code<=Keys.Z||code>=Keys.D0&&code<=Keys.D9||code>=Keys.F1&&code<=Keys.F12)&&key!=(Keys.Control|Keys.Shift|Keys.H)&&key!=(Keys.Control|Keys.R)&&key!=(Keys.Control|Keys.Shift|Keys.R);}
        private void ShowVisibilityShortcut(){
            if(selectedLayer==null)return;var layer=selectedLayer;Keys chosen=(Keys)layer.VisibilityShortcut;
            using(var dialog=new Form{Text="Visibility shortcut",ClientSize=new Size(420,150),StartPosition=FormStartPosition.CenterParent,BackColor=ColorCardBg,ForeColor=ColorTextPrimary,ShowInTaskbar=false,KeyPreview=true,FormBorderStyle=FormBorderStyle.FixedDialog,MaximizeBox=false,MinimizeBox=false}){
                var label=new Label{Left=16,Top=12,Width=388,Height=48,Text="Press Ctrl or Alt with a letter, number or F key.\nShortcuts work while LensioStudio is focused."};
                var value=new Label{Left=16,Top=66,Width=388,Height=26,Text=chosen==Keys.None?"No shortcut":new KeysConverter().ConvertToString(chosen)};
                var clear=new Button{Left=16,Top=108,Width=100,Text="Clear"};var save=new Button{Left=284,Top=108,Width=120,Text="Save"};
                clear.Click+=(s,e)=>{chosen=Keys.None;value.Text="No shortcut";};
                dialog.KeyDown+=(s,e)=>{if(ValidSourceShortcut(e.KeyData)){chosen=e.KeyData;value.Text=new KeysConverter().ConvertToString(chosen);e.SuppressKeyPress=true;}};
                save.Click+=(s,e)=>{foreach(var other in scene.GetLayersSnapshot())if(other!=layer&&chosen!=Keys.None&&other.VisibilityShortcut==(int)chosen){value.Text="Already assigned to "+other.Name;return;}layer.VisibilityShortcut=(int)chosen;CommitSceneEdit();dialog.Close();};
                dialog.Controls.AddRange(new Control[]{label,value,clear,save});StyleSourceDialog(dialog);dialog.ShowDialog(this);
            }
        }
        private bool EditingText(){Control c=ActiveControl;while(c is ContainerControl&&((ContainerControl)c).ActiveControl!=null)c=((ContainerControl)c).ActiveControl;return c is TextBoxBase||c is NumericUpDown||c is ComboBox;}
        private bool HandleSourceShortcut(KeyEventArgs e){
            if(EditingText())return false;
            if(e.KeyData==(Keys.Control|Keys.R)||e.KeyData==(Keys.Control|Keys.Shift|Keys.R)){if(shortcutHeld!=e.KeyData)RotateSelected(e.Shift?-90:90);shortcutHeld=e.KeyData;e.SuppressKeyPress=true;return true;}
            ObsLayer target=null;
            if(e.KeyData==(Keys.Control|Keys.Shift|Keys.H))target=selectedLayer;
            else foreach(var layer in scene.GetLayersSnapshot())if(layer.VisibilityShortcut!=0&&layer.VisibilityShortcut==(int)e.KeyData){target=layer;break;}
            if(target==null)return false;
            if(shortcutHeld!=e.KeyData){target.Visible=!target.Visible;RefreshSourcesList();}
            shortcutHeld=e.KeyData;e.SuppressKeyPress=true;return true;
        }
    }
}
