package com.apexcam.network
import com.apexcam.camera.HardwareRecoveryPolicy
import org.junit.Assert.*
import org.junit.Test
class HardwareRecoveryPolicyTest {
    @Test fun transientFailuresRestartHardwareBeforeTemporaryFallback(){
        val p=HardwareRecoveryPolicy()
        assertFalse(p.failed(1000));assertFalse(p.takeRetry(1999));assertTrue(p.takeRetry(2000));assertFalse(p.takeRetry(2000))
        assertFalse(p.failed(3000));assertFalse(p.takeRetry(4999));assertTrue(p.takeRetry(5000))
        assertTrue(p.failed(6000));assertFalse(p.takeRetry(35999));assertTrue(p.takeRetry(36000))
        assertTrue(p.failed(37000));assertFalse(p.takeRetry(96999));assertTrue(p.takeRetry(97000))
        assertTrue(p.failed(98000));assertEquals(218000,p.retryAt)
        repeat(20){assertTrue(p.failed(300000L+it));assertTrue(p.retryAt<=420000L+it)}
    }
    @Test fun OnlySustainedOutputResetsTheFailureBudget(){
        val p=HardwareRecoveryPolicy();p.failed(1000);p.takeRetry(2000)
        p.observe(3000,true);p.observe(20000,true);assertEquals(1,p.failures)
        p.observe(21000,false);p.observe(22000,true);p.observe(51000,true);assertEquals(1,p.failures)
        p.observe(52000,true);assertEquals(0,p.failures);assertFalse(p.pending)
        assertFalse(p.failed(53000))
    }
    @Test fun unavailableCodecDoesNotLoopAndExplicitNewSessionResetsPolicy(){
        val p=HardwareRecoveryPolicy();assertTrue(p.failed(1000,true));assertFalse(p.takeRetry(Long.MAX_VALUE));assertFalse(p.pending)
        p.reset();assertFalse(p.failed(2000));assertTrue(p.takeRetry(3000))
    }
}
