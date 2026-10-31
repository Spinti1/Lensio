using System;
using System.Drawing;
using ApexCam;
class CameraCompositionSmoke {
    static void Check(bool ok,string message){if(!ok)throw new Exception(message);}
    static Bitmap Frame(Color color){var b=new Bitmap(2,2);using(var g=Graphics.FromImage(b))g.Clear(color);return b;}
    static Bitmap Render(ObsLayer layer,int width,int height){var b=new Bitmap(width,height);using(var g=Graphics.FromImage(b)){g.Clear(Color.Blue);layer.RenderCamera(g,1,1);}return b;}
    static int Main(){
        using(var layer=new ObsLayer {Type=SourceType.Camera,Width=4,Height=4}){
            var input=new Bitmap(2,2);input.SetPixel(0,0,Color.Black);input.SetPixel(1,0,Color.Red);input.SetPixel(0,1,Color.Lime);input.SetPixel(1,1,Color.Yellow);
            layer.SetCameraFrame(input);
            using(var output=Render(layer,4,4)){
                Check(output.GetPixel(0,0).ToArgb()==Color.Black.ToArgb(),"Dark border changed");
                Check(output.GetPixel(3,3).ToArgb()==Color.Yellow.ToArgb(),"Bright border changed");
                var sample=output.GetPixel(1,1);
                Check(Math.Abs(sample.R-64)<=1 && Math.Abs(sample.G-64)<=1 && sample.B==0,"Bilinear interpolation lost color detail");
            }
            layer.SetCameraFrame(Frame(Color.Red));
            using(var output=Render(layer,4,4))Check(output.GetPixel(2,2).R==255,"New frame did not invalidate cached output");
            layer.Opacity=.5f;
            using(var output=Render(layer,4,4)){var c=output.GetPixel(2,2);Check(Math.Abs(c.R-128)<=1 && Math.Abs(c.B-127)<=1,"Opacity blending changed");}
            layer.Width=2;layer.Height=2;layer.Opacity=1;
            using(var output=Render(layer,4,4)){Check(output.GetPixel(1,1).R==255 && output.GetPixel(2,2).B==255,"Native pixels or resize bounds changed");}
        }
        using(var portrait=new Bitmap(90,160)){
            using(var g=Graphics.FromImage(portrait)){g.Clear(Color.Lime);g.FillRectangle(Brushes.Red,0,0,90,30);g.FillRectangle(Brushes.Blue,0,130,90,30);}
            using(var output=VideoFilters.ApplyFiltersAndTransform(portrait,FilterMode.None,1,false,false,1,0,0,0,false,90,160)){
                Check(output.GetPixel(45,5).R>200 && output.GetPixel(45,155).B>200,"Portrait filtering cropped the top or bottom of the rotated camera");
            }
        }
        Console.WriteLine("PASS full-resolution interpolation, edge colors, fresh frames, opacity and native-size resize");return 0;
    }
}
