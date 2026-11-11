package com.apexcam.camera

import androidx.test.ext.junit.runners.AndroidJUnit4
import com.apexcam.audio.AudioReceiverEngine
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.util.concurrent.atomic.AtomicReference

@RunWith(AndroidJUnit4::class)
class AudioUdpDeviceTest {
    @Test fun udpFallbackPlaysAfterShortAcknowledgementsAndPings(){
        // A loopback alias exercises Wi-Fi/UDP policy without any external network.
        val address=InetAddress.getByName("127.0.0.2")
        DatagramSocket(0,address).use {server->
            server.soTimeout=5000
            val error=AtomicReference<Throwable?>()
            val sender=Thread{
                try{
                    val hello=DatagramPacket(ByteArray(128),128);server.receive(hello)
                    fun send(bytes:ByteArray){server.send(DatagramPacket(bytes,bytes.size,hello.address,hello.port))}
                    send("CONNECTED_OK".toByteArray())
                    for(sequence in 0 until 240){
                        if(sequence%20==0)send("PONG:1".toByteArray())
                        val frame=ByteArray(968);frame[0]=0x53;frame[1]=0x41;frame[2]=(sequence shr 8).toByte();frame[3]=sequence.toByte()
                        send(frame);Thread.sleep(5)
                    }
                }catch(failure:Throwable){error.set(failure)}
            }
            val engine=AudioReceiverEngine("127.0.0.2",server.localPort)
            try{
                sender.start();engine.start();sender.join(7000)
                error.get()?.let{throw AssertionError("Audio fixture failed",it)}
                Thread.sleep(300)
                val state=engine.diagnostics
                assertTrue("UDP PCM frames truncated after handshake: $state",state["receivedFrames"] as Long>=220)
                assertTrue("AudioTrack did not play: $state",state["playedFrames"] as Long>100)
                assertEquals(0,state["playbackRestarts"])
            }finally{engine.stop();sender.join(1000)}
        }
    }
}
