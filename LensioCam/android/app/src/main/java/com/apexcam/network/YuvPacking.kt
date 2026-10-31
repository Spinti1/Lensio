package com.apexcam.network

import java.nio.ByteBuffer

/** Copies only active pixels, respecting both planar and interleaved vendor YUV strides. */
internal object YuvPacking {
    fun copyPlane(source: ByteBuffer, rowStride: Int, pixelStride: Int,
                  width: Int, height: Int, destination: ByteArray, offset: Int, step: Int) {
        require(width > 0 && height > 0 && rowStride > 0 && pixelStride > 0)
        val plane = source.duplicate()
        val base = plane.position()
        require(base.toLong() + (height-1L)*rowStride + (width-1L)*pixelStride < plane.limit())
        require(offset.toLong() + (width.toLong()*height-1)*step < destination.size)
        val rowBytes = ByteArray((width-1)*pixelStride+1)
        for (row in 0 until height) {
            val srcRow = base + row * rowStride
            val dstRow = offset + row * width * step
            if (pixelStride == 1 && step == 1) {
                plane.position(srcRow)
                plane.get(destination, dstRow, width)
            } else {
                // Bulk-copy direct buffers: per-pixel ByteBuffer.get crosses a slow native boundary.
                plane.position(srcRow)
                plane.get(rowBytes)
                for (col in 0 until width)
                    destination[dstRow + col*step] = rowBytes[col*pixelStride]
            }
        }
    }
    /** Rotate luma and interleaved VU as a single frame before the one JPEG encode. */
    fun rotateNv21(source: ByteArray, destination: ByteArray, width: Int, height: Int, degrees: Int) {
        require(width>0 && height>0 && width%2==0 && height%2==0 && (degrees==0||degrees==90||degrees==180||degrees==270))
        val size=width*height
        require(source.size>=size*3/2 && destination.size>=size*3/2 && source!==destination)
        if(degrees==0){source.copyInto(destination,0,0,size*3/2);return}
        val outWidth=if(degrees==180)width else height
        for(y in 0 until height)for(x in 0 until width){
            val index=when(degrees){90->x*outWidth+height-1-y;180->(height-1-y)*outWidth+width-1-x;else->(width-1-x)*outWidth+y}
            destination[index]=source[y*width+x]
        }
        val chromaW=width/2;val chromaH=height/2;val outChromaW=outWidth/2
        for(y in 0 until chromaH)for(x in 0 until chromaW){
            val index=when(degrees){90->x*outChromaW+chromaH-1-y;180->(chromaH-1-y)*outChromaW+chromaW-1-x;else->(chromaW-1-x)*outChromaW+y}
            destination[size+index*2]=source[size+y*width+x*2]
            destination[size+index*2+1]=source[size+y*width+x*2+1]
        }
    }
}
