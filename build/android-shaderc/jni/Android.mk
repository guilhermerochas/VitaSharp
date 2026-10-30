# libshaderc_shared.so for Android, from the shaderc sources the NDK ships (sources/third_party/shaderc): the same
# shared library Silk.NET.Shaderc loads on the desktop (shaderc's C API), with libc++ linked in statically so the
# APK needs nothing else. Built by build/build-android-shaderc.ps1 or .sh; see docs/android/README.md.
LOCAL_PATH := $(call my-dir)

include $(CLEAR_VARS)
LOCAL_MODULE := shaderc_shared
LOCAL_SRC_FILES := exports.c
LOCAL_WHOLE_STATIC_LIBRARIES := shaderc
# 16 KiB pages (Android 15+ devices): align the segments so the library loads there too
LOCAL_LDFLAGS := -Wl,-z,max-page-size=16384 -Wl,--gc-sections
include $(BUILD_SHARED_LIBRARY)

$(call import-add-path,$(NDK_ROOT)/sources/third_party)
$(call import-module,shaderc)
