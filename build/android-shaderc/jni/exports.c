/* libshaderc_shared.so is shaderc's static library linked whole (LOCAL_WHOLE_STATIC_LIBRARIES): every public
   shaderc_* function is exported from there. ndk-build needs at least one source file for a shared module. */
int vitasharp_shaderc_android_build = 1;
