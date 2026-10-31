package com.apexcam.network
import com.apexcam.camera.DeviceRotation
import org.junit.Assert.*
import org.junit.Test
class OrientationTest {
 @Test fun physicalOrientationAndHysteresis(){
  assertEquals(0,DeviceRotation.fromDegrees(-1,0))
  assertEquals(0,DeviceRotation.fromDegrees(50,0))
  assertEquals(3,DeviceRotation.fromDegrees(90,0))
  assertEquals(2,DeviceRotation.fromDegrees(180,3))
  assertEquals(1,DeviceRotation.fromDegrees(270,2))
  assertEquals(0,DeviceRotation.fromDegrees(359,1))
 }
 @Test fun nv21RotationPreservesLumaAndChromaAtEveryAngle(){
  val input=byteArrayOf(0,1,2,3,4,5,6,7,20,21,22,23)
  val expected=mapOf(
   0 to input,
   90 to byteArrayOf(4,0,5,1,6,2,7,3,20,21,22,23),
   180 to byteArrayOf(7,6,5,4,3,2,1,0,22,23,20,21),
   270 to byteArrayOf(3,7,2,6,1,5,0,4,22,23,20,21)
  )
  for(angle in listOf(0,90,180,270)){
   val output=ByteArray(input.size);YuvPacking.rotateNv21(input,output,4,2,angle)
   assertArrayEquals("Incorrect rotation $angle",expected[angle],output)
   val recovered=ByteArray(input.size)
   YuvPacking.rotateNv21(output,recovered,if(angle%180==0)4 else 2,if(angle%180==0)2 else 4,(360-angle)%360)
   assertArrayEquals("Rotation loses image/chroma",input,recovered)
  }
 }
}
