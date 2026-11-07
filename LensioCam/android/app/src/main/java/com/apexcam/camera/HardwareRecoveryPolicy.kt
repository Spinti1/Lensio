package com.apexcam.camera

/** Main-thread policy; evaluated by the existing health tick, never a retry timer per frame. */
internal class HardwareRecoveryPolicy {
    var failures=0;private set
    var retryAt=Long.MAX_VALUE;private set
    private var healthySince:Long?=null
    private var unavailable=false
    val blocked:Boolean get()=unavailable
    val pending:Boolean get()=retryAt!=Long.MAX_VALUE
    fun reset(){failures=0;retryAt=Long.MAX_VALUE;healthySince=null;unavailable=false}
    /** Two bounded hardware restarts before a temporary JPEG cooldown. */
    fun failed(now:Long,unsupported:Boolean=false):Boolean {
        healthySince=null;failures++
        unavailable=unavailable||unsupported
        val fallback=unavailable||failures>2
        val delay=if(failures<=2)failures*1000L else 30_000L*(1L shl (failures-3).coerceAtMost(2))
        retryAt=if(unavailable)Long.MAX_VALUE else now+delay
        return fallback
    }
    fun takeRetry(now:Long):Boolean {
        if(unavailable||!pending||now<retryAt)return false
        retryAt=Long.MAX_VALUE;healthySince=null;return true
    }
    fun observe(now:Long,healthy:Boolean){
        if(!healthy){healthySince=null;return}
        if(healthySince==null)healthySince=now
        if(now-healthySince!!>=30_000){failures=0;retryAt=Long.MAX_VALUE}
    }
}
