package com.apexcam.audio

import java.io.EOFException
import java.io.InputStream
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.SocketTimeoutException

internal object AudioPacketIO {
    fun readFrame(input:InputStream,frame:ByteArray,running:()->Boolean):Boolean {
        var offset=0
        while(offset<frame.size&&running()){
            val count=try{input.read(frame,offset,frame.size-offset)}catch(_:SocketTimeoutException){continue}
            if(count<0)throw EOFException("Audio TCP connection closed")
            if(count==0)throw java.io.IOException("Audio input made no progress")
            offset+=count
        }
        return offset==frame.size
    }
    fun receive(socket:DatagramSocket,packet:DatagramPacket){
        // receive() changes length to the received size. A short handshake otherwise
        // truncates all following PCM datagrams to that handshake's length.
        packet.length=packet.data.size-packet.offset
        socket.receive(packet)
    }
    fun writeFrame(frame:ByteArray,running:()->Boolean,write:(Int,Int)->Int):Boolean {
        var offset=0
        while(offset<frame.size&&running()){
            val count=write(offset,frame.size-offset)
            if(count<=0||count>frame.size-offset)throw java.io.IOException("AudioTrack write failed: $count")
            offset+=count
        }
        return offset==frame.size
    }
}
