package com.apexcam.network

import org.junit.Assert.*
import org.junit.Test
import java.net.ServerSocket
import java.net.Socket
import java.util.concurrent.atomic.AtomicLong

class StreamingLifecycleTest {
    private fun awaitCondition(condition: () -> Boolean) {
        val deadline = System.nanoTime() + 5_000_000_000L
        while (!condition() && System.nanoTime() < deadline) Thread.sleep(10)
        assertTrue("Timed out waiting for lifecycle state", condition())
    }

    @Test fun identityIsSentExactlyOnceBeforeFramesOnEachConnection() {
        ServerSocket(0).use { listener ->
            listener.soTimeout=3000
            val sender=NativeStreamSender { System.nanoTime()/1_000_000 }
            sender.deviceIdentityJson="{\"id\":\"phone-a\",\"name\":\"Phone A\"}"
            try {
                repeat(3) {
                    sender.connect("127.0.0.1",listener.localPort,force=true)
                    listener.accept().use { socket ->
                        socket.soTimeout=3000
                        val input=java.io.DataInputStream(socket.getInputStream())
                        val magic=ByteArray(4); input.readFully(magic)
                        assertArrayEquals(byteArrayOf(65,80,67,73),magic)
                        val raw=ByteArray(4); input.readFully(raw)
                        val length=java.nio.ByteBuffer.wrap(raw).order(java.nio.ByteOrder.LITTLE_ENDIAN).int
                        val body=ByteArray(length);input.readFully(body)
                        assertEquals(sender.deviceIdentityJson,String(body,Charsets.UTF_8))
                        awaitCondition {sender.isConnected}
                        assertEquals(0,input.available())
                    }
                }
            } finally {sender.close()}
        }
    }

    @Test fun repeatedReconnectDoesNotLetOldReadersDisconnectNewSocket() {
        ServerSocket(0).use { listener ->
            listener.soTimeout = 3000
            val sender = NativeStreamSender { System.nanoTime() / 1_000_000 }
            var previous: Socket? = null
            try {
                repeat(40) {
                    sender.connect("127.0.0.1", listener.localPort, force = true)
                    val current = listener.accept()
                    current.soTimeout = 3000
                    awaitCondition { sender.isConnected }
                    previous?.let { old ->
                        assertEquals("Superseded socket must close", -1, old.getInputStream().read())
                        old.close()
                    }
                    Thread.sleep(10)
                    assertTrue("Stale command reader disconnected new session", sender.isConnected)
                    previous = current
                }
                sender.close()
                assertEquals(-1, previous!!.getInputStream().read())
                assertFalse(sender.isConnected)
                sender.connect("127.0.0.1", listener.localPort)
                assertFalse(sender.isConnected)
            } finally { sender.close(); previous?.close() }
        }
        awaitCondition { Thread.getAllStackTraces().keys.none { it.isAlive && it.name in setOf("Lensio-connect", "Lensio-commands", "Lensio-encoder") } }
    }

    @Test fun stalledStreamWatchdogClosesSocketAndCanReconnect() {
        val time = AtomicLong(1000)
        val sender = NativeStreamSender { time.get() }
        ServerSocket(0).use { listener ->
            listener.soTimeout = 3000
            try {
                sender.connect("127.0.0.1", listener.localPort)
                listener.accept().use { client ->
                    client.soTimeout = 3000
                    awaitCondition { sender.isConnected }
                    time.addAndGet(5000)
                    sender.checkHealth()
                    assertTrue("Allow CameraX recovery without reconnect churn", sender.isConnected)
                    time.addAndGet(7001)
                    sender.checkHealth()
                    assertFalse(sender.isConnected)
                    assertEquals(-1, client.getInputStream().read())
                }
                sender.connect("127.0.0.1", listener.localPort)
                listener.accept().use {
                    awaitCondition { sender.isConnected }
                    assertTrue(sender.isConnected)
                }
            } finally { sender.close() }
        }
    }

    @Test fun httpClientsAreBoundedAndNeverBlockEncoder() {
        val port = ServerSocket(0).use { it.localPort }
        val server = HttpStreamServer(port)
        val clients = mutableListOf<Socket>()
        server.start()
        try {
            awaitCondition {
                try { Socket("127.0.0.1",port).use { }; true } catch (_: Exception) { false }
            }
            repeat(30) {
                Socket("127.0.0.1",port).use {
                    it.soTimeout = 3000
                    it.getOutputStream().write("GET /status HTTP/1.1\r\nHost: localhost\r\n\r\n".toByteArray())
                    assertTrue(it.getInputStream().bufferedReader().readText().contains("\"status\":\"ok\""))
                }
            }
            repeat(4) {
                val client = Socket("127.0.0.1",port)
                client.receiveBufferSize = 1024
                client.getOutputStream().write("GET /video HTTP/1.1\r\nHost: localhost\r\n\r\n".toByteArray())
                clients.add(client)
            }
            awaitCondition { server.activeClientCount == 4 }
            Socket("127.0.0.1",port).use {
                it.soTimeout = 3000
                it.getOutputStream().write("GET /video HTTP/1.1\r\nHost: localhost\r\n\r\n".toByteArray())
                assertTrue(it.getInputStream().bufferedReader().readLine().contains("503"))
            }
            val start = System.nanoTime()
            val jpeg = ByteArray(1024 * 1024)
            repeat(300) { server.broadcastJpeg(jpeg) }
            assertTrue("Broadcast blocked behind slow viewer", System.nanoTime() - start < 1_000_000_000L)
            awaitCondition { server.activeClientCount == 0 }
        } finally {
            clients.forEach { it.close() }
            server.stop()
        }
        awaitCondition { Thread.getAllStackTraces().keys.none { it.isAlive && it.name.startsWith("Lensio-http-") } }
    }
}
