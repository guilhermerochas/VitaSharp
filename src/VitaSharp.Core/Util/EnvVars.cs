namespace VitaSharp.Core.Util;

/// <summary>
/// Every <c>VITASHARP_*</c> environment variable the emulator, its tools and its tests read, in one place. Most are
/// debugging or testing switches; what each one does is documented where it is read (and in docs/notes/*.md).
/// </summary>
public static class EnvVars
{
    public const string Prefix = "VITASHARP_";

    public const string AsyncPipelines = "VITASHARP_ASYNC_PIPELINES";
    public const string AsyncShaders = "VITASHARP_ASYNC_SHADERS";
    public const string AudioDevice = "VITASHARP_AUDIO_DEVICE";
    public const string BtDraw = "VITASHARP_BT_DRAW";
    public const string Cpu = "VITASHARP_CPU";
    public const string CpuMhz = "VITASHARP_CPU_MHZ";
    public const string Data = "VITASHARP_DATA";
    public const string DebugFs = "VITASHARP_DEBUG_FS";
    public const string DebugInject = "VITASHARP_DEBUG_INJECT";
    public const string DebugMode = "VITASHARP_DEBUG_MODE";
    public const string DebugNan = "VITASHARP_DEBUG_NAN";
    public const string DebugTex0 = "VITASHARP_DEBUG_TEX0";
    public const string DyldRelaunched = "VITASHARP_DYLD_RELAUNCHED";
    public const string FetchSplit = "VITASHARP_FETCH_SPLIT";
    public const string FirmwarePup = "VITASHARP_FIRMWARE_PUP";
    /// <summary>Tests: an installed firmware to run firmware modules from (a Vita3K data folder with vs0/, or a vs0
    /// folder itself). Unset: %APPDATA%\Vita3K\Vita3K when it exists, else the PSVUPDAT.PUP of FirmwarePup.</summary>
    public const string FirmwareDir = "VITASHARP_FIRMWARE_DIR";
    public const string Ffmpeg = "VITASHARP_FFMPEG";
    public const string FuzzIter = "VITASHARP_FUZZ_ITER";
    public const string GeneratorVersionSalt = "VITASHARP_GENERATOR_VERSION_SALT";
    public const string GlfwPlatform = "VITASHARP_GLFW_PLATFORM";
    public const string GpuDumpAt = "VITASHARP_GPU_DUMP_AT";
    public const string GpuProfile = "VITASHARP_GPU_PROFILE";
    public const string GpuQuirks = "VITASHARP_GPU_QUIRKS";
    public const string HleModules = "VITASHARP_HLE_MODULES";
    public const string Hunt = "VITASHARP_HUNT";
    public const string HuntNative = "VITASHARP_HUNT_NATIVE";
    public const string LleModules = "VITASHARP_LLE_MODULES";
    public const string Lockstep = "VITASHARP_LOCKSTEP";
    public const string Msaa = "VITASHARP_MSAA";
    public const string MsaaFetch = "VITASHARP_MSAA_FETCH";
    public const string NativeDump = "VITASHARP_NATIVE_DUMP";
    public const string NativeNoinline = "VITASHARP_NATIVE_NOINLINE";
    public const string NativeNolink = "VITASHARP_NATIVE_NOLINK";
    public const string NativeTierup = "VITASHARP_NATIVE_TIERUP";
    public const string NativeValidate = "VITASHARP_NATIVE_VALIDATE";
    public const string NativeVerify = "VITASHARP_NATIVE_VERIFY";
    public const string PeekEnd = "VITASHARP_PEEK_END";
    public const string Replay = "VITASHARP_REPLAY";
    public const string SamplerDebug = "VITASHARP_SAMPLER_DEBUG";
    public const string ShadercOpt = "VITASHARP_SHADERC_OPT";
    public const string ShaderRebuild = "VITASHARP_SHADER_REBUILD";
    public const string ShaderWorkers = "VITASHARP_SHADER_WORKERS";
    public const string ShaderWorkerPrio = "VITASHARP_SHADER_WORKER_PRIO";
    public const string SkipUnicorn = "VITASHARP_SKIP_UNICORN";
    public const string SnapFs = "VITASHARP_SNAP_FS";
    public const string Swz1 = "VITASHARP_SWZ1";
    public const string SyscallLatencyUs = "VITASHARP_SYSCALL_LATENCY_US";
    public const string TestTrace = "VITASHARP_TEST_TRACE";
    public const string Texdirty = "VITASHARP_TEXDIRTY";
    public const string TraceRt = "VITASHARP_TRACE_RT";
    public const string TraceSnap = "VITASHARP_TRACE_SNAP";
    public const string TraceSync = "VITASHARP_TRACE_SYNC";
    public const string TraceVis = "VITASHARP_TRACE_VIS";
    public const string TscaleProc = "VITASHARP_TSCALE_PROC";
    public const string TscaleProcLr = "VITASHARP_TSCALE_PROC_LR";
    public const string TscaleProcThread = "VITASHARP_TSCALE_PROC_THREAD";
    public const string TscaleVblank = "VITASHARP_TSCALE_VBLANK";
    public const string TscaleWait = "VITASHARP_TSCALE_WAIT";
    public const string UbUnbounded = "VITASHARP_UB_UNBOUNDED";
    public const string VideoHw = "VITASHARP_VIDEO_HW";
    public const string VisForce = "VITASHARP_VIS_FORCE";
    public const string VramBudgetMb = "VITASHARP_VRAM_BUDGET_MB";
    public const string VramEvict = "VITASHARP_VRAM_EVICT";
    public const string VulkanLib = "VITASHARP_VULKAN_LIB";
    public const string Watch = "VITASHARP_WATCH";
    public const string WatchMax = "VITASHARP_WATCH_MAX";
    public const string WritebackMax = "VITASHARP_WRITEBACK_MAX";
    public const string WritebackRegionMax = "VITASHARP_WRITEBACK_REGION_MAX";
}
