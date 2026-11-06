package com.apexcam.camera

/** Advertised AE ranges only: the encoder target must not force a short sensor exposure. */
internal object VideoFpsPolicy {
    fun select(ranges:List<Pair<Int,Int>>,target:Int,allowFixed:Boolean=true):Pair<Int,Int>? {
        val supported=ranges.filter { (low,high) -> low>0 && high>=low && high<=target && (allowFixed || low<high) }
        return supported.filter { it.second==target && it.first<it.second }.maxByOrNull { it.first }
            ?: supported.sortedWith(compareByDescending<Pair<Int,Int>> { it.second }.thenBy { if(it.first<it.second)0 else 1 }.thenByDescending { it.first }).firstOrNull()
    }
}
