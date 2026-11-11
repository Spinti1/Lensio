package com.apexcam.camera

/** One waiting frame; replacement/close releases ownership exactly once. */
internal class LatestFrameMailbox<T:Any>(private val release:(T)->Unit){
    private val lock=Any()
    private var pending:T?=null
    private var closed=false
    val hasPending:Boolean get()=synchronized(lock){pending!=null}
    fun offer(value:T):Boolean {
        val previous:T?;val accepted:Boolean
        synchronized(lock){accepted=!closed;previous=if(closed)value else pending;if(accepted)pending=value}
        previous?.let(release);return accepted
    }
    fun take():T?=synchronized(lock){pending.also{pending=null}}
    fun close(){val previous=synchronized(lock){closed=true;pending.also{pending=null}};previous?.let(release)}
}
