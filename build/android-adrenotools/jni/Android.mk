# libadrenotools (github.com/bylaws/libadrenotools, BSD-2-Clause) for the Android app, arm64-v8a only (the library
# supports nothing else): libadrenotools.so (driver loader, BCeNabler, turbo) and the four hook libraries it loads
# from the app's native library directory (libhook_impl.so, libmain_hook.so, libfile_redirect_hook.so,
# libgsl_alloc_hook.so). The same modules and flags as the upstream CMakeLists, written for ndk-build so no CMake is
# needed. ADRENOTOOLS_SRC is the source checkout (with lib/linkernsbypass); build/build-android-adrenotools.ps1 or .sh
# fetches it and passes it here. See docs/android/README.md.
LOCAL_PATH := $(call my-dir)
SRC := $(ADRENOTOOLS_SRC)
# 16 KiB pages (Android 15+ devices): align the segments so the libraries load there too
PAGE_FLAGS := -Wl,-z,max-page-size=16384

include $(CLEAR_VARS)
LOCAL_MODULE := linkernsbypass
LOCAL_SRC_FILES := $(SRC)/lib/linkernsbypass/android_linker_ns.cpp $(SRC)/lib/linkernsbypass/elf_soname_patcher.cpp
LOCAL_C_INCLUDES := $(SRC)/lib/linkernsbypass
LOCAL_EXPORT_C_INCLUDES := $(SRC)/lib/linkernsbypass
LOCAL_CFLAGS := -Wall -Wextra
LOCAL_EXPORT_LDLIBS := -landroid -ldl
include $(BUILD_STATIC_LIBRARY)

include $(CLEAR_VARS)
LOCAL_MODULE := adrenotools
LOCAL_SRC_FILES := $(SRC)/src/bcenabler.cpp $(SRC)/src/driver.cpp
LOCAL_C_INCLUDES := $(SRC)/include $(SRC) $(SRC)/src
LOCAL_CFLAGS := -Wall -Wextra
LOCAL_STATIC_LIBRARIES := linkernsbypass
LOCAL_LDLIBS := -landroid -llog
LOCAL_LDFLAGS := $(PAGE_FLAGS) -Wl,-s -Wl,--exclude-libs,ALL
include $(BUILD_SHARED_LIBRARY)

include $(CLEAR_VARS)
LOCAL_MODULE := hook_impl
LOCAL_SRC_FILES := $(SRC)/src/hook/hook_impl.cpp
LOCAL_C_INCLUDES := $(SRC)/include $(SRC)/src/hook
LOCAL_CFLAGS := -Wall -Wextra
LOCAL_CPPFLAGS := -fvisibility=hidden
LOCAL_STATIC_LIBRARIES := linkernsbypass
LOCAL_LDLIBS := -llog
LOCAL_LDFLAGS := $(PAGE_FLAGS)
include $(BUILD_SHARED_LIBRARY)

# the three hooks: C, global symbol scope (-z global) so they interpose the driver's imports
define adrenotools-hook
include $$(CLEAR_VARS)
LOCAL_MODULE := $(1)
LOCAL_SRC_FILES := $$(SRC)/src/hook/$(1).c
LOCAL_C_INCLUDES := $$(SRC)/include $$(SRC)/src/hook
LOCAL_CFLAGS := -Wall -Wextra
LOCAL_SHARED_LIBRARIES := hook_impl
LOCAL_LDFLAGS := $$(PAGE_FLAGS) -Wl,-z,global
include $$(BUILD_SHARED_LIBRARY)
endef
$(eval $(call adrenotools-hook,main_hook))
$(eval $(call adrenotools-hook,file_redirect_hook))
$(eval $(call adrenotools-hook,gsl_alloc_hook))
