namespace VitaSharp.Core.Util;

/// <summary>
/// Every <c>VITASHARP_*</c> environment variable the emulator, its tools and its tests read, in one place. Most are
/// debugging or testing switches; what each one does is documented where it is read (and in docs/notes/*.md).
/// </summary>
public static class EnvVars
{
    public const string Prefix = "VITASHARP_";

    /// <summary>=0: BC/PVRTC textures are decoded to RGBA8 even on GPUs that sample ASTC (no ASTC transcoding).</summary>
    public const string Astc = "VITASHARP_ASTC";
    /// <summary>Size limit in MB of a title's ASTC transcode disk cache (default 512).</summary>
    public const string AstcCacheMb = "VITASHARP_ASTC_CACHE_MB";
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
    /// <summary>Index or part of the name of the Vulkan physical device to render with (default: the discrete GPU).</summary>
    public const string GpuDevice = "VITASHARP_GPU_DEVICE";
    public const string GpuQuirks = "VITASHARP_GPU_QUIRKS";
    public const string HleModules = "VITASHARP_HLE_MODULES";
    /// <summary>0/off: guest threads and the GPU worker keep normal host priority (HostThreads; overrides --host-prio).</summary>
    public const string HostPrio = "VITASHARP_HOST_PRIO";
    public const string Hunt = "VITASHARP_HUNT";
    public const string HuntNative = "VITASHARP_HUNT_NATIVE";
    public const string LleModules = "VITASHARP_LLE_MODULES";
    public const string Lockstep = "VITASHARP_LOCKSTEP";
    public const string Msaa = "VITASHARP_MSAA";
    public const string MsaaFetch = "VITASHARP_MSAA_FETCH";
    /// <summary>Default of --net=off|real (real internet for SceNet/SceHttp, off by default; see Hle/NetConfig.cs).</summary>
    public const string Net = "VITASHARP_NET";
    /// <summary>Default of --net-allow=host,host: with real internet, only these hosts (names or addresses) are reachable.</summary>
    public const string NetAllow = "VITASHARP_NET_ALLOW";
    /// <summary>File the native backends append every compiled block's instructions to (a corpus for the compile-cost
    /// benchmark NativeJitTests.CompileCostBenchmark, which reads it from <see cref="BenchCorpus"/>).</summary>
    public const string NativeCorpus = "VITASHARP_NATIVE_CORPUS";
    /// <summary>Tests: the block corpus (recorded with <see cref="NativeCorpus"/>) the compile-cost benchmark compiles.</summary>
    public const string BenchCorpus = "VITASHARP_BENCH_CORPUS";
    /// <summary>=1: x64 native blocks count the VFP/NEON ops and conditional branches they execute (profiling of
    /// register pinning / compare-branch fusion; reported on the PERF cycles lines).</summary>
    public const string NativeCounters = "VITASHARP_NATIVE_COUNTERS";
    public const string NativeDump = "VITASHARP_NATIVE_DUMP";
    public const string NativeNoinline = "VITASHARP_NATIVE_NOINLINE";
    public const string NativeNolink = "VITASHARP_NATIVE_NOLINK";
    public const string NativeTierup = "VITASHARP_NATIVE_TIERUP";
    public const string NativeValidate = "VITASHARP_NATIVE_VALIDATE";
    public const string NativeVerify = "VITASHARP_NATIVE_VERIFY";
    public const string PeekEnd = "VITASHARP_PEEK_END";
    public const string Replay = "VITASHARP_REPLAY";
    /// <summary>Password of the RPCN account (--rpcn-user): the launcher passes it here instead of on the command line,
    /// where other processes could read it.</summary>
    public const string RpcnPassword = "VITASHARP_RPCN_PASSWORD";
    public const string SamplerDebug = "VITASHARP_SAMPLER_DEBUG";
    /// <summary>=strict: every texture upload or copy in a scene ends the rendering, and single-sampled renderings are
    /// begun directly on desktop GPUs (the renderer before scene-break hoisting; VulkanRenderer.SceneBreaks.cs).</summary>
    public const string SceneBreaks = "VITASHARP_SCENE_BREAKS";
    /// <summary>1 = every process runs in the first process's address space (the model before per-process address
    /// spaces: executables linked at addresses another process uses cannot run next to it).</summary>
    public const string SharedAddressSpace = "VITASHARP_SHARED_ADDRESS_SPACE";
    public const string ShadercOpt = "VITASHARP_SHADERC_OPT";
    public const string ShaderRebuild = "VITASHARP_SHADER_REBUILD";
    public const string ShaderWorkers = "VITASHARP_SHADER_WORKERS";
    public const string ShaderWorkerPrio = "VITASHARP_SHADER_WORKER_PRIO";
    /// <summary>1 = an application the shell launches replaces the shell instead of running as a second process.</summary>
    public const string SingleProcess = "VITASHARP_SINGLE_PROCESS";
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

    /// <summary>Android: =off turns off both the pinning of the hot threads (guest main thread, GPU worker) to the
    /// fast cores and the Performance Hint (ADPF) session, to compare with them on (docs/android/README.md).</summary>
    public const string AndroidAffinity = "VITASHARP_ANDROID_AFFINITY";
    /// <summary>Android: =info, =debug or =all keeps forwarding info/debug lines to logcat while a game runs (by
    /// default only warnings and errors reach logcat once the first frame is shown; the log file keeps everything).</summary>
    public const string AndroidLogcat = "VITASHARP_ANDROID_LOGCAT";
    /// <summary>Android: =1 asks for Window.setSustainedPerformanceMode where the device supports it (lower, steadier
    /// clocks for long sessions; off by default because it lowers peak speed).</summary>
    public const string AndroidSustained = "VITASHARP_ANDROID_SUSTAINED";
}
