package com.apexcam.network

import com.apexcam.camera.CameraTextureOrientation
import org.junit.Assert.*
import org.junit.Test

class CameraTextureOrientationTest {
    @Test fun textureCorrectionCancelsSensorRotationAndFrontMirror() {
        for(sensor in listOf(0,90,180,270))for(front in listOf(false,true)){
            val m=CameraTextureOrientation.correction(sensor,front)
            for((x,y) in listOf(0f to 0f,1f to 0f,0f to 1f,1f to 1f,.5f to .5f)){
                var u=m[0]*x+m[1]*y+m[2]-.5f
                val v=m[3]*x+m[4]*y+m[5]-.5f
                if(front)u=-u
                val (c,s)=when(sensor){90->0f to 1f;180->-1f to 0f;270->0f to -1f;else->1f to 0f}
                assertEquals("Horizontal sensor=$sensor front=$front",x,c*u-s*v+.5f,.00001f)
                assertEquals("Vertical sensor=$sensor front=$front",y,s*u+c*v+.5f,.00001f)
            }
        }
    }
    @Test(expected=IllegalArgumentException::class) fun rejectsUnsupportedSensorRotation(){CameraTextureOrientation.correction(45,false)}
}
