import java.nio.ByteBuffer;
import com.apexcam.camera.NativeYuvPlane;

/** Run against built Kotlin classes and the host build of the same production JNI source. */
public final class NativeYuvJniSmoke {
    private static void check(boolean ok,String message){if(!ok)throw new AssertionError(message);}
    public static void main(String[] args){
        NativeYuvPlane copy=NativeYuvPlane.INSTANCE;
        check(copy.getAvailable(),"Native library did not load");
        for(int step=1;step<=4;step++)for(int width:new int[]{1,15,16,17,31,32,33,640,960}){
            int height=7,span=(width-1)*step+1,stride=span+9;
            ByteBuffer src=ByteBuffer.allocateDirect(3+(height-1)*stride+span);
            ByteBuffer dst=ByteBuffer.allocateDirect(2+height*(width+5));
            for(int i=0;i<src.capacity();i++)src.put(i,(byte)(i*37));
            for(int i=0;i<dst.capacity();i++)dst.put(i,(byte)0x6a);
            src.position(3);dst.position(2);
            check(copy.copy(src,stride,step,dst,width+5,width,height),"Native copy rejected valid plane");
            for(int y=0;y<height;y++)for(int x=0;x<width+5;x++)
                check(dst.get(2+y*(width+5)+x)==(x<width?src.get(3+y*stride+x*step):(byte)0x6a),"JNI pixels or padding changed");
            check(src.position()==3&&dst.position()==2,"JNI changed buffer positions");
            check(!copy.copy(src,stride,step,dst.asReadOnlyBuffer(),width+5,width,height),"Read-only output accepted");
            check(!copy.copy(src,stride,step,dst,width+5,width,height+1),"Truncated source accepted");
        }
        ByteBuffer same=ByteBuffer.allocateDirect(64);
        check(!copy.copy(same,16,1,same,16,16,4),"Overlapping input/output accepted");
        check(!copy.copy(ByteBuffer.allocate(64),16,1,same,16,16,4),"Heap buffer accepted by JNI");
        System.out.println("PASS production Kotlin/JNI dispatch, direct buffer offsets, exact YUV samples, padding, read-only/heap/truncated/overlap rejection");
    }
}
