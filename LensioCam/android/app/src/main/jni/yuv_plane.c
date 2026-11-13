#include <jni.h>
#include "yuv_plane.h"

JNIEXPORT jboolean JNICALL
Java_com_apexcam_camera_NativeYuvPlane_copyPlane(JNIEnv *env, jobject self,
        jobject source, jint row_stride, jint pixel_stride, jobject destination,
        jint dst_stride, jint width, jint height) {
    (void)self;
    if (!source || !destination || width<=0 || height<=0 || width>1920 || height>1920 ||
        (int64_t)width*height>1920*1080 || row_stride<=0 || pixel_stride<=0 || dst_stride<width)
        return JNI_FALSE;
    int64_t span=(int64_t)(width-1)*pixel_stride+1;
    int64_t source_size=(int64_t)(height-1)*row_stride+span;
    int64_t target_size=(int64_t)(height-1)*dst_stride+width;
    jlong source_capacity=(*env)->GetDirectBufferCapacity(env,source);
    jlong target_capacity=(*env)->GetDirectBufferCapacity(env,destination);
    if (span>row_stride || source_size>source_capacity || target_size>target_capacity)
        return JNI_FALSE;
    const uint8_t *src=(*env)->GetDirectBufferAddress(env,source);
    uint8_t *dst=(*env)->GetDirectBufferAddress(env,destination);
    if (!src || !dst) return JNI_FALSE;
    uintptr_t src_address=(uintptr_t)src, dst_address=(uintptr_t)dst;
    if (src_address<dst_address+(uintptr_t)target_size && dst_address<src_address+(uintptr_t)source_size)
        return JNI_FALSE;
    lensio_copy_plane(src,(size_t)row_stride,pixel_stride,dst,(size_t)dst_stride,width,height);
    return JNI_TRUE;
}
