package com.apexcam.network
import com.apexcam.audio.AudioPacketIO
import org.junit.Assert.*
import org.junit.Test
import java.io.InputStream
import java.net.*

class AudioPacketIOTest {
    @Test fun shortHandshakeDoesNotTruncateSubsequentAudioPackets(){
        DatagramSocket(0,InetAddress.getLoopbackAddress()).use {receiver->
            receiver.soTimeout=2000
            DatagramSocket().use {sender->
                val packet=DatagramPacket(ByteArray(968),968)
                for(data in listOf("CONNECTED_OK".toByteArray(),ByteArray(968){it.toByte()},"PONG:123".toByteArray(),ByteArray(968){(it+1).toByte()})){
                    sender.send(DatagramPacket(data,data.size,InetAddress.getLoopbackAddress(),receiver.localPort))
                    AudioPacketIO.receive(receiver,packet)
                    assertEquals(data.size,packet.length);assertArrayEquals(data,packet.data.copyOfRange(packet.offset,packet.offset+packet.length))
                }
            }
        }
    }
    @Test fun timeoutDuringSplitTcpFramePreservesItsPrefixAndNextFrame(){
        val expected=ByteArray(1936){it.toByte()}
        val input=object:InputStream(){var offset=0;var timeout=false
            override fun read():Int=throw UnsupportedOperationException()
            override fun read(b:ByteArray,off:Int,len:Int):Int{
                if(offset==117&&!timeout){timeout=true;throw SocketTimeoutException()}
                if(offset>=expected.size)return -1
                val count=minOf(len,if(offset==0)117 else 211,expected.size-offset)
                System.arraycopy(expected,offset,b,off,count);offset+=count;return count
            }
        }
        repeat(2){val frame=ByteArray(968);assertTrue(AudioPacketIO.readFrame(input,frame){true});assertArrayEquals(expected.copyOfRange(it*968,(it+1)*968),frame)}
    }
    @Test fun cancelledReadNeverPublishesPartialAudio(){
        var running=true
        val input=object:InputStream(){override fun read():Int=-1;override fun read(b:ByteArray,off:Int,len:Int):Int {b[off]=7;running=false;return 1}}
        assertFalse(AudioPacketIO.readFrame(input,ByteArray(968)){running})
    }
    @Test fun partialPlaybackWritesFinishTheFrameAndDeadTracksNeverSpin(){
        var written=0
        assertTrue(AudioPacketIO.writeFrame(ByteArray(968),{true}){offset,length->assertEquals(written,offset);minOf(137,length).also{written+=it}})
        assertEquals(968,written)
        var calls=0
        try{AudioPacketIO.writeFrame(ByteArray(968),{true}){_,_->calls++;-6};fail("Dead AudioTrack must trigger recreation")}
        catch(_:java.io.IOException){assertEquals(1,calls)}
    }
}
