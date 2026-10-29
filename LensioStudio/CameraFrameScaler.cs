using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Threading.Tasks;
namespace ApexCam {
    // Full-resolution bilinear scaling. Cache coordinate maps and reuse the output surface.
    internal sealed class CameraFrameScaler : IDisposable {
        private Bitmap output;
        public Bitmap Output {get{return output;}}
        private int sourceWidth,sourceHeight;
        private int[] left,right,weight,top,bottom,verticalWeight;
        public unsafe Bitmap Scale(Bitmap source,int width,int height,float opacity) {
            if(output==null || output.Width!=width || output.Height!=height ||
                sourceWidth!=source.Width || sourceHeight!=source.Height) {
                if(output!=null)output.Dispose();
                output=new Bitmap(width,height,PixelFormat.Format32bppPArgb);
                sourceWidth=source.Width;sourceHeight=source.Height;
                Map(source.Width,width,out left,out right,out weight);
                Map(source.Height,height,out top,out bottom,out verticalWeight);
            }
            int alpha=(int)Math.Round(Math.Max(0,Math.Min(1,opacity))*255);
            BitmapData input=null,target=null;
            try {
                input=source.LockBits(new Rectangle(0,0,source.Width,source.Height),ImageLockMode.ReadOnly,PixelFormat.Format32bppArgb);
                target=output.LockBits(new Rectangle(0,0,width,height),ImageLockMode.WriteOnly,PixelFormat.Format32bppPArgb);
                IntPtr src=input.Scan0,dst=target.Scan0;int srcStride=input.Stride,dstStride=target.Stride;
                Parallel.For(0,(height+31)/32,new ParallelOptions {MaxDegreeOfParallelism=Math.Min(4,Environment.ProcessorCount)},batch => {
                    for(int y=batch*32;y<Math.Min(height,batch*32+32);y++) {
                        byte* row0=(byte*)src+top[y]*srcStride;
                        byte* row1=(byte*)src+bottom[y]*srcStride;
                        byte* outputRow=(byte*)dst+y*dstStride;
                        int wy=verticalWeight[y],iy=256-wy;
                        for(int x=0;x<width;x++) {
                            int x0=left[x]*4,x1=right[x]*4,wx=weight[x],ix=256-wx;
                            int at=x*4;
                            for(int c=0;c<3;c++) {
                                int first=row0[x0+c]*ix+row0[x1+c]*wx;
                                int second=row1[x0+c]*ix+row1[x1+c]*wx;
                                int value=(first*iy+second*wy+32768)>>16;
                                outputRow[at+c]=(byte)(alpha==255 ? value : (value*alpha+127)/255);
                            }
                            outputRow[at+3]=(byte)alpha;
                        }
                    }
                });
            } finally {
                if(target!=null)output.UnlockBits(target);
                if(input!=null)source.UnlockBits(input);
            }
            return output;
        }
        private static void Map(int source,int target,out int[] first,out int[] second,out int[] fractions) {
            first=new int[target];second=new int[target];fractions=new int[target];
            for(int i=0;i<target;i++) {
                double position=Math.Max(0,Math.Min(source-1,(i+.5)*source/target-.5));
                first[i]=(int)position;second[i]=Math.Min(source-1,first[i]+1);
                fractions[i]=(int)Math.Round((position-first[i])*256);
            }
        }
        public void Dispose(){if(output!=null){output.Dispose();output=null;}}
    }
}
