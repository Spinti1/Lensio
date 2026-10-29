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
}
