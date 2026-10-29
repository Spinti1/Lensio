using System;
using System.Drawing;
using ApexCam;
class FilterBehaviorSmoke {
    static void Check(bool ok,string message) { if(!ok) throw new Exception(message); }
    static Color Render(Color input, FilterMode mode=FilterMode.None, float brightness=0, float contrast=0, float saturation=0, float exposure=0) {
        using(var bitmap=new Bitmap(16,9)) {
            using(var g=Graphics.FromImage(bitmap)) g.Clear(input);
            using(var result=VideoFilters.ApplyFiltersAndTransform(bitmap,mode,1,false,false,brightness,contrast,saturation,exposure,false,16,9))
                return result.GetPixel(8,4);
        }
    }
    static int Spread(Color c) { return Math.Max(c.R,Math.Max(c.G,c.B))-Math.Min(c.R,Math.Min(c.G,c.B)); }
    static int Main() {
        Color input=Color.FromArgb(140,100,80);
        Color neutral=Render(input);
        Check(Math.Abs(neutral.R-input.R)<=1 && Math.Abs(neutral.G-input.G)<=1 && Math.Abs(neutral.B-input.B)<=1,"Neutral filters altered the image");
        foreach(Color color in new[]{Color.Red,Color.Lime,Color.Blue,input,Color.Gray}) {
            Color grey=Render(color,saturation:-100);
            int expected=(int)Math.Round(.2126*color.R+.7152*color.G+.0722*color.B);
            Check(Spread(grey)<=1 && Math.Abs(grey.R-expected)<=2,"Desaturation must preserve luminance: "+color+" -> "+grey);
        }
        Check(Spread(Render(input,saturation:100))>Spread(input),"Saturation did not increase color intensity");
        Check(Spread(Render(Color.Gray,saturation:100))<=1,"Saturation tinted neutral grey");
        Check(Render(Color.Black,exposure:50).R==0,"Exposure raised the black point");
        Check(Math.Abs(Render(Color.FromArgb(64,64,64),exposure:50).R-64*Math.Pow(2,50.0/80))<=2,"Exposure is not a gain");
        Check(Render(input,brightness:20).R>input.R,"Brightness failed");
        Check(Render(Color.FromArgb(64,64,64),contrast:50).R<64,"Contrast did not darken shadows");
        Check(Render(Color.FromArgb(190,190,190),contrast:50).R>190,"Contrast did not brighten highlights");
        foreach(FilterMode mode in Enum.GetValues(typeof(FilterMode))) {
            Check(Spread(Render(input,mode,saturation:-100))<=1,"Preset overrides saturation: "+mode);
        }
        Console.WriteLine("PASS filter behavior: neutral, luminance, saturation, exposure, brightness, contrast and all presets");
        return 0;
    }
}
