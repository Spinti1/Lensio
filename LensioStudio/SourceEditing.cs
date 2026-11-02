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
        private void InitializeSourceEditing(){
            foreach(ToolStripItem item in sourceContextMenu.Items)if(item.Text=="Show / hide")item.Text="Show / hide (Ctrl+Shift+H)";
            sourceToolTips.SetToolTip(previewPanel,"Drag to move. Drag a handle to resize. Alt + drag a handle crops. Alt + move bypasses snapping. Ctrl+Shift+H shows/hides the selected source.");
        }
        private Control BuildTransformProperties(ObsLayer layer){
            var panel=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=2,RowCount=7,Padding=new Padding(0),BackColor=ColorCardBg};
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,50));panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,50));
            for(int i=0;i<7;i++)panel.RowStyles.Add(new RowStyle(SizeType.Percent,100f/7));
            Action<string,int,int,Action> button=(text,col,row,action)=>{var control=CreateModernButton(text,ColorButtonDark,0,0,120,28);control.Dock=DockStyle.Fill;control.Margin=new Padding(3);control.Click+=(s,e)=>{action();CommitSceneEdit();var shortcut=panel.Controls.Find("SourceShortcut",true);if(shortcut.Length>0)shortcut[0].Text=layer.VisibilityShortcut==0?"No shortcut":new KeysConverter().ConvertToString((Keys)layer.VisibilityShortcut);};panel.Controls.Add(control,col,row);};
            button("Fit to canvas",0,0,()=>layer.SetBounds(new RectangleF(0,0,1920,1080)));
            button("Center in frame",1,0,()=>{var bounds=layer.VisualBounds;layer.X+=960-bounds.X-bounds.Width/2;layer.Y+=540-bounds.Y-bounds.Height/2;});
            var label=new Label{Text="Rotation (degrees)",Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft,ForeColor=ColorTextPrimary};
            var angle=new NumericUpDown{Name="SourceRotation",Dock=DockStyle.Fill,Minimum=-360,Maximum=360,DecimalPlaces=1,Value=(decimal)layer.Rotation,BackColor=ColorButtonDark,ForeColor=ColorTextPrimary,Margin=new Padding(3,3,3,3)};
            angle.ValueChanged+=(s,e)=>{layer.Rotation=(float)angle.Value;CommitSceneEdit();};panel.Controls.Add(label,0,1);panel.Controls.Add(angle,1,1);
            button("Rotate 90° left",0,2,()=>angle.Value=(decimal)((layer.Rotation+270)%360));
            button("Rotate 90° right",1,2,()=>angle.Value=(decimal)((layer.Rotation+90)%360));
            button("Reset rotation",0,3,()=>angle.Value=0);
            button("Reset crop",1,3,()=>layer.CropLeft=layer.CropRight=layer.CropTop=layer.CropBottom=0);
            var locked=new CheckBox{Name="SourceLocked",Text="Lock position and size",Checked=layer.Locked,Dock=DockStyle.Fill,ForeColor=ColorTextPrimary};
            locked.CheckedChanged+=(s,e)=>{layer.Locked=locked.Checked;CommitSceneEdit();};panel.Controls.Add(locked,0,4);panel.SetColumnSpan(locked,2);
            var help=new Label{Text="Alt + drag handle: crop  ·  Alt + move: free positioning",Dock=DockStyle.Fill,ForeColor=ColorTextSecondary,TextAlign=ContentAlignment.MiddleLeft};panel.Controls.Add(help,0,5);panel.SetColumnSpan(help,2);
            button("Visibility shortcut...",0,6,()=>ShowVisibilityShortcut());
            var key=new Label{Name="SourceShortcut",Dock=DockStyle.Fill,ForeColor=ColorTextSecondary,TextAlign=ContentAlignment.MiddleLeft};Action refresh=()=>key.Text=layer.VisibilityShortcut==0?"No shortcut":new KeysConverter().ConvertToString((Keys)layer.VisibilityShortcut);refresh();key.VisibleChanged+=(s,e)=>refresh();panel.Controls.Add(key,1,6);
            return panel;
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
        private static bool ValidSourceShortcut(Keys key){
            Keys code=key&Keys.KeyCode;
            return Enum.IsDefined(typeof(Keys),code)&&code>Keys.XButton2&&code!=Keys.ShiftKey&&code!=Keys.ControlKey&&code!=Keys.Menu&&code!=Keys.LShiftKey&&code!=Keys.RShiftKey&&code!=Keys.LControlKey&&code!=Keys.RControlKey&&code!=Keys.LMenu&&code!=Keys.RMenu;
        }
        private sealed class ShortcutCaptureDialog:Form {
            public bool Listening;public Action<Keys> Captured;private Keys held;
            private bool Record(Keys key){
                if(held==(key&Keys.KeyCode)&&held!=Keys.None)return true;
                if(Listening&&ValidSourceShortcut(key)){held=key&Keys.KeyCode;Listening=false;if(Captured!=null)Captured(key);return true;}
                return false;
            }
            protected override bool ProcessCmdKey(ref Message message,Keys key){return Record(key)||base.ProcessCmdKey(ref message,key);}
            protected override void OnKeyDown(KeyEventArgs e){if(Record(e.KeyData))e.SuppressKeyPress=true;else base.OnKeyDown(e);}
            protected override void OnKeyUp(KeyEventArgs e){if((e.KeyCode&Keys.KeyCode)==held)held=Keys.None;base.OnKeyUp(e);}
        }
        protected override bool ProcessCmdKey(ref Message message,Keys key){
            if(HandleSourceShortcut(new KeyEventArgs(key)))return true;
            return base.ProcessCmdKey(ref message,key);
        }
        private void ShowVisibilityShortcut(){
            if(selectedLayer==null)return;var layer=selectedLayer;Keys chosen=(Keys)layer.VisibilityShortcut;
            using(var dialog=new ShortcutCaptureDialog{Text="Visibility shortcut",ClientSize=new Size(420,180),StartPosition=FormStartPosition.CenterParent,BackColor=ColorCardBg,ForeColor=ColorTextPrimary,ShowInTaskbar=false,KeyPreview=true,FormBorderStyle=FormBorderStyle.FixedDialog,MaximizeBox=false,MinimizeBox=false}){
                var label=new Label{Left=16,Top=12,Width=388,Height=60,Text="Press any key, with or without Ctrl, Alt or Shift.\nShortcuts work while LensioStudio is focused.\nWindows-reserved shortcuts may be handled by Windows."};
                var value=new Label{Left=16,Top=78,Width=388,Height=26,Text=chosen==Keys.None?"No shortcut":new KeysConverter().ConvertToString(chosen)};
                var clear=new Button{Left=16,Top=138,Width=100,Text="Clear"};var save=new Button{Left=284,Top=138,Width=120,Text="Save"};
                var change=new Button{Left=128,Top=138,Width=140,Text="Record shortcut"};
                change.Click+=(s,e)=>{dialog.Listening=true;value.Text="Press a key or combination...";dialog.Focus();};
                clear.Click+=(s,e)=>{dialog.Listening=false;chosen=Keys.None;value.Text="No shortcut";};
                dialog.Captured=key=>{chosen=key;value.Text=new KeysConverter().ConvertToString(chosen);save.Focus();};
                dialog.Shown+=(s,e)=>{dialog.Listening=true;};
                save.Click+=(s,e)=>{foreach(var other in scene.GetLayersSnapshot())if(other!=layer&&chosen!=Keys.None&&other.VisibilityShortcut==(int)chosen){value.Text="Already assigned to "+other.Name;return;}layer.VisibilityShortcut=(int)chosen;CommitSceneEdit();dialog.Close();};
                dialog.Controls.AddRange(new Control[]{label,value,clear,change,save});StyleSourceDialog(dialog);dialog.ShowDialog(this);
            }
        }
        private bool EditingText(){Control c=ActiveControl;while(c is ContainerControl&&((ContainerControl)c).ActiveControl!=null)c=((ContainerControl)c).ActiveControl;return c is TextBoxBase||c is NumericUpDown||c is ComboBox;}
        private bool HandleSourceShortcut(KeyEventArgs e){
            if(EditingText())return false;
            ObsLayer target=null;
            // Explicit source bindings take precedence over built-in editor shortcuts.
            foreach(var layer in scene.GetLayersSnapshot())if(layer.VisibilityShortcut!=0&&layer.VisibilityShortcut==(int)e.KeyData){target=layer;break;}
            if(target==null&&e.KeyData==(Keys.Control|Keys.Shift|Keys.H))target=selectedLayer;
            if(target==null&&(e.KeyData==(Keys.Control|Keys.R)||e.KeyData==(Keys.Control|Keys.Shift|Keys.R))){if(shortcutHeld!=e.KeyData)RotateSelected(e.Shift?-90:90);shortcutHeld=e.KeyData;e.SuppressKeyPress=true;return true;}
            if(target==null)return false;
            if(shortcutHeld!=e.KeyData){target.Visible=!target.Visible;RefreshSourcesList();}
            shortcutHeld=e.KeyData;e.SuppressKeyPress=true;return true;
        }
    }
}
