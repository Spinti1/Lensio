using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using ApexCam;
class PipelineBenchmark {
    static void Run(string label,int width,int count,int sourceWidth) {
        int height=width*9/16;
        var scene=new ObsScene();
        var main=new ObsLayer {Type=SourceType.Camera,X=0,Y=0,Width=1920,Height=1080};
        var secondary=new ObsLayer {Type=SourceType.Camera,X=1392,Y=36,Width=480,Height=270};
        var a=new Bitmap(sourceWidth,sourceWidth*9/16,PixelFormat.Format32bppArgb);
        var b=new Bitmap(640,360,PixelFormat.Format32bppArgb);
        using(var g=Graphics.FromImage(a))g.Clear(Color.FromArgb(120,80,40));
        using(var g=Graphics.FromImage(b))g.Clear(Color.FromArgb(40,120,80));
        main.SetCameraFrame(new Bitmap(a));secondary.SetCameraFrame(new Bitmap(b));
        scene.AddLayer(main);scene.AddLayer(secondary);
        double total=0,max=0;
        for(int i=0;i<count+10;i++) {
            main.SetCameraFrame(new Bitmap(a));secondary.SetCameraFrame(new Bitmap(b));
            var clock=Stopwatch.StartNew();
            using(var bitmap=new Bitmap(width,height,PixelFormat.Format32bppArgb))
            using(var g=Graphics.FromImage(bitmap)){g.Clear(Color.Black);scene.RenderScene(g,width,height);}
            clock.Stop();if(i>=10){total+=clock.Elapsed.TotalMilliseconds;max=Math.Max(max,clock.Elapsed.TotalMilliseconds);}
        }
        main.Dispose();secondary.Dispose();a.Dispose();b.Dispose();
        Console.WriteLine(label+" "+width+"x"+height+" avg_ms="+(total/count).ToString("0.00")+" worst_ms="+max.ToString("0.00"));
    }
    static int Main(){Run("720p main",1280,90,1280);Run("720p main",1920,60,1280);Run("360p main",1280,90,640);Run("360p main",1920,60,640);return 0;}
}
