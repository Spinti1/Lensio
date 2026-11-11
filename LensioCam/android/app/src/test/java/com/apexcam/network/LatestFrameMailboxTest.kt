package com.apexcam.network
import com.apexcam.camera.LatestFrameMailbox
import com.apexcam.camera.HardwareRecoveryPolicy
import org.junit.Assert.*
import org.junit.Test
class LatestFrameMailboxTest {
    @Test fun replacingQueuedFramesNeverKeepsOldCameraBuffers(){
        val released=mutableListOf<Int>();val box=LatestFrameMailbox<Int>{released.add(it)}
        assertTrue(box.offer(1));assertTrue(box.offer(2));assertEquals(listOf(1),released)
        assertEquals(2,box.take());assertFalse(box.hasPending)
        box.offer(3);box.close();box.close();assertFalse(box.offer(4))
        assertEquals(listOf(1,3,4),released)
    }
    @Test fun concurrentProducerConsumerAndShutdownReleaseEachFrameOnce(){
        val released=java.util.concurrent.ConcurrentHashMap.newKeySet<Int>()
        val duplicates=java.util.concurrent.atomic.AtomicInteger()
        val release:(Int)->Unit={if(!released.add(it))duplicates.incrementAndGet()}
        val box=LatestFrameMailbox(release)
        val consumer=Thread{repeat(10000){box.take()?.let(release)}}
        consumer.start();repeat(10000){box.offer(it);if(it==7000)box.close()};consumer.join();box.close()
        assertEquals(10000,released.size);assertEquals(0,duplicates.get())
    }
    @Test fun resumedOutputCancelsPendingRestartWithoutErasingFailureHistory(){
        val policy=HardwareRecoveryPolicy();assertFalse(policy.failed(1000))
        assertTrue(policy.pending);policy.observe(1500,true)
        assertFalse(policy.pending);assertFalse(policy.takeRetry(3000));assertEquals(1,policy.failures)
        policy.observe(31500,true);assertEquals(0,policy.failures)
    }
}
