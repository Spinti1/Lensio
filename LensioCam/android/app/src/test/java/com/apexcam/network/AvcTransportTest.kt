package com.apexcam.network
import org.junit.Assert.*
import org.junit.Test
import java.net.ServerSocket
import java.io.DataInputStream
import java.nio.ByteBuffer
import java.nio.ByteOrder

class AvcTransportTest {
 private fun awaitConnected(sender:NativeStreamSender){val until=System.nanoTime()+3_000_000_000;while(!sender.isConnected&&System.nanoTime()<until)Thread.sleep(5);assertTrue(sender.isConnected)}
 @Test fun avcHeaderPreservesOrientationTelemetryAndSessionBoundaries(){
  val sender=NativeStreamSender {System.nanoTime()/1_000_000}
  ServerSocket(0).use { server ->
   server.soTimeout=3000
   try { repeat(3){
    sender.connect("127.0.0.1",server.localPort,true)
    server.accept().use { peer ->
     peer.soTimeout=3000;awaitConnected(sender)
     val syncRequests=java.util.concurrent.atomic.AtomicInteger()
     sender.onVideoKeyframeRequested={syncRequests.incrementAndGet();Unit}
     sender.setAvcEnabled(true)
     sender.sendAvc(byteArrayOf(0,0,0,1,0x41),1280,720,90,false,1000,54,true,31f)
     assertEquals("Dropped sync input must be requested again",2,syncRequests.get())
     Thread.sleep(20);assertEquals("Delta sent before sync frame",0,peer.getInputStream().available())
     val payload=byteArrayOf(0,0,0,1,0x65,1,2,3)
     sender.sendAvc(payload,1280,720,90,true,1000000,54,true,31f)
     val input=DataInputStream(peer.getInputStream());val raw=ByteArray(40);input.readFully(raw)
     val header=ByteBuffer.wrap(raw).order(ByteOrder.LITTLE_ENDIAN)
     assertEquals(0x3432484c,header.int);assertEquals(1280,header.int);assertEquals(720,header.int);assertEquals(payload.size,header.int);header.int
     assertEquals(54 or 0x10000,header.int);assertEquals(310,header.int);assertEquals(0x01000000 or (90 shl 8) or 1,header.int);assertEquals(1000000L,header.long)
     val actual=ByteArray(payload.size);input.readFully(actual);assertArrayEquals(payload,actual)
     sender.setAvcEnabled(false);sender.sendAvc(payload,1280,720,0,true,1000001,54,false,31f)
     Thread.sleep(20);assertEquals("AVC continued after JPEG fallback",0,input.available())
    }
   }}finally{sender.close()}
  }
 }
 @Test fun slowPeerDoesNotBlockEncoderAndWriterCloses(){
  val sender=NativeStreamSender {System.nanoTime()/1_000_000}
  ServerSocket(0).use { server ->
   try{sender.connect("127.0.0.1",server.localPort);server.accept().use { peer ->
    awaitConnected(sender);peer.receiveBufferSize=1024;sender.setAvcEnabled(true)
    val frame=ByteArray(256*1024);val started=System.nanoTime()
    repeat(150){sender.sendAvc(frame,1280,720,0,it%10==0,it*333333L,80,false,25f)}
    assertTrue("Encoder blocked by slow socket",System.nanoTime()-started<1_000_000_000)
    sender.close()
   }}finally{sender.close()}
  }
  val until=System.nanoTime()+3_000_000_000;while(Thread.getAllStackTraces().keys.any{it.isAlive&&it.name=="Lensio-AVC-network"}&&System.nanoTime()<until)Thread.sleep(10)
  assertFalse(Thread.getAllStackTraces().keys.any{it.isAlive&&it.name=="Lensio-AVC-network"})
 }
}
