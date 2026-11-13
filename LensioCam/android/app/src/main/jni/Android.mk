LOCAL_PATH := $(call my-dir)
include $(CLEAR_VARS)
LOCAL_MODULE := lensio-yuv
LOCAL_SRC_FILES := yuv_plane.c
LOCAL_CFLAGS := -O3 -Wall -Wextra -Werror -fvisibility=hidden -fstack-protector-strong
LOCAL_LDFLAGS := -Wl,-z,relro,-z,now
include $(BUILD_SHARED_LIBRARY)
