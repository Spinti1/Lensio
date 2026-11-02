using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Reflection;
namespace ApexCam {
 internal static class LensioBrand {
  private static readonly object gate=new object();
  private static readonly Bitmap noSignal=Load("noSignal");
  private static readonly Bitmap logo=Load("logo");
  private static Bitmap Load(string name){using(var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("LensioBrand."+name))using(var image=Image.FromStream(stream))return new Bitmap(image);}
  public static Icon CreateIcon(){using(var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("LensioBrand.icon"))using(var icon=new Icon(stream))return (Icon)icon.Clone();}
  public static void DrawNoSignal(Graphics g,Rectangle area){if(area.Width<1||area.Height<1)return;lock(gate){if(area.Size==noSignal.Size)g.DrawImageUnscaled(noSignal,area.Location);else g.DrawImage(noSignal,area);}}
  public static void DrawNoSignal(Graphics g,Rectangle area,float opacity){
   if(opacity>=1){DrawNoSignal(g,area);return;}
   if(area.Width<1||area.Height<1||opacity<=0)return;
   using(var attributes=new System.Drawing.Imaging.ImageAttributes()){
    var matrix=new System.Drawing.Imaging.ColorMatrix();matrix.Matrix33=opacity;attributes.SetColorMatrix(matrix);
    lock(gate)g.DrawImage(noSignal,area,0,0,noSignal.Width,noSignal.Height,GraphicsUnit.Pixel,attributes);
   }
  }
  public static void DrawLogo(Graphics g,Rectangle area){lock(gate)g.DrawImage(logo,area);}
  public static GraphicsPath Rounded(RectangleF bounds,float radius){var path=new GraphicsPath();float d=Math.Min(radius*2,Math.Min(bounds.Width,bounds.Height));path.AddArc(bounds.X,bounds.Y,d,d,180,90);path.AddArc(bounds.Right-d,bounds.Y,d,d,270,90);path.AddArc(bounds.Right-d,bounds.Bottom-d,d,d,0,90);path.AddArc(bounds.X,bounds.Bottom-d,d,d,90,90);path.CloseFigure();return path;}
 }
}
