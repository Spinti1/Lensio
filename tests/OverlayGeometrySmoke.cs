using System;
using System.Drawing;
using ApexCam;
class OverlayGeometrySmoke {
    static int Main() {
        foreach(int width in new[] {1280,1920}) {
            int height=width*9/16;
            var scene=new ObsScene();
            using(var image=new Bitmap(30,30))
            using(var output=new Bitmap(width,height)) {
                using(var g=Graphics.FromImage(image))g.Clear(Color.Lime);
                scene.AddLayer(new ObsLayer {Type=SourceType.Image,BitmapImage=image,X=300,Y=240,Width=600,Height=300});
                using(var g=Graphics.FromImage(output)){g.Clear(Color.Black);scene.RenderScene(g,width,height);}
                float scale=width/1920f;
                if(output.GetPixel((int)(600*scale),(int)(390*scale)).G<200)throw new Exception("Image not inside expected selection");
                if(output.GetPixel((int)(950*scale),(int)(390*scale)).G>20)throw new Exception("Image extends past expected selection");
                if(output.GetPixel((int)(250*scale),(int)(390*scale)).G>20)throw new Exception("Image starts before expected selection");
            }
            Console.WriteLine("PASS overlay geometry "+width);
        }
        return 0;
    }
}
