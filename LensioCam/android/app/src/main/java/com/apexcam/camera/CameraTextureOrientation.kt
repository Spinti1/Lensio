package com.apexcam.camera

/** Undo the camera's SurfaceTexture sensor transform before applying display-rotation metadata. */
internal object CameraTextureOrientation {
    fun correction(sensorDegrees: Int, frontFacing: Boolean): FloatArray {
        require(sensorDegrees in listOf(0,90,180,270))
        val (c,s)=when(sensorDegrees){90->0f to -1f;180->-1f to 0f;270->0f to 1f;else->1f to 0f}
        val mirror=if(frontFacing)-1f else 1f
        val a=mirror*c;val b=-mirror*s
        return floatArrayOf(a,b,(1-a-b)/2,s,c,(1-s-c)/2,0f,0f,1f)
    }
}
