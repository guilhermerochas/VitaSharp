namespace VitaSharp.Core.Util;

/// <summary>
/// Every <c>VITASHARP_*</c> environment variable the emulator, its tools and its tests read, in one place. Most are
/// debugging or testing switches; what each one does is documented where it is read (and in docs/notes/*.md).
/// </summary>
public static class EnvVars
{
    public const string Prefix = "VITASHARP_";

    /// <summary>=0: BC/PVRTC textures are decoded to RGBA8 even on GPUs that sample ASTC (no ASTC transcoding).</summary>
    /// <summary>Android: scripted buttons/sticks for unattended device runs (ScriptedPad: "cross@12,start@20:0.5").</summary>
    public const string AndroidInput = "VITASHARP_ANDROID_INPUT";
    /// <summary>Android: a file polled while the game runs; its ScriptedPad steps (T relative to when it is read) are added and the file deleted, so a PC script can react to the screen (adb push).</summary>
    public const string AndroidInputLive = "VITASHARP_ANDROID_INPUT_LIVE";
    /// <summary>Android developer builds: a file polled every 250 ms on the UI thread, also while the game is paused (the app screens poll the path, the game screen the path + ".game"; Platforms/Android/Developer/UiScript.cs); each line ("key:dpad_down", "key:button_a", "touch:x:y" in 0..1 of the screen) is injected into the game activity as a controller key or a touch, then the file is deleted (drives the pause menu where the phone refuses adb shell input).</summary>
    public const string AndroidUiInput = "VITASHARP_ANDROID_UI_INPUT";
    /// <summary>A/B switch: =old builds the per-draw texture sets the way main b29e26a did (all 16 units visited, render
    /// targets scanned through the dictionary); default new (only the sampled units, flat render-target range table).</summary>
    public const string TexLookup = "VITASHARP_TEXLOOKUP";
    /// <summary>A/B switch: =old scans draw indices for their min/max one at a time; default new (128-bit vectors).</summary>
    public const string MinMaxScan = "VITASHARP_MINMAX";
    /// <summary>A/B switch: =1 records the hot vkCmd* calls through device-level function pointers loaded once with
    /// vkGetDeviceProcAddr (no Silk.NET dispatch, no GC transition); default 0 (Silk.NET).</summary>
    public const string VkDirect = "VITASHARP_VK_DIRECT";
    /// <summary>Android: =0 tries the hardware H.264 decoders first even for pictures up to 960x544 (default: Android's own first there), =1 always Android's own first.</summary>
    public const string MediaCodecSoftware = "VITASHARP_MEDIACODEC_SOFTWARE";
    /// <summary>Android: AAudio buffer capacity requested in frames (default 0 = AAudio's own; 8192 = the earlier request).</summary>
    public const string AAudioCapacity = "VITASHARP_AAUDIO_CAPACITY";
    public const string Astc = "VITASHARP_ASTC";
    /// <summary>Size limit in MB of a title's ASTC transcode disk cache (default 512).</summary>
    public const string AstcCacheMb = "VITASHARP_ASTC_CACHE_MB";
    public const string AsyncPipelines = "VITASHARP_ASYNC_PIPELINES";
    public const string AsyncShaders = "VITASHARP_ASYNC_SHADERS";
    public const string AudioDevice = "VITASHARP_AUDIO_DEVICE";
    /// <summary>Android: folder (relative to the data folder) for per-port WAVs of the guest's audio (desktop: --audio-dump).</summary>
    public const string AudioDump = "VITASHARP_AUDIO_DUMP";
    /// <summary>=1: logs every NGS source voice event (play, key off, buffer callbacks, player/ATRAC9 buffer table
    /// changes) with the reported play position; with --audio-dump each source voice's output also gets its own WAV.</summary>
    public const string NgsTrace = "VITASHARP_NGS_TRACE";
    /// <summary>=0: the NGS envelope module is a passthrough and a key off stops the voice at once (as before; A/B).</summary>
    public const string NgsEnvelope = "VITASHARP_NGS_ENVELOPE";
    /// <summary>=picture: an asynchronous H.264 decoder's pictures keep their own pts in sceAvcdecDecode (default: the pts of
    /// the access unit of the call; Hle/Modules/Videodec.cs).</summary>
    public const string AvcdecPts = "VITASHARP_AVCDEC_PTS";
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
    /// <summary>1/0: consecutive programmable-blending draws of a one-sample scene share one fetch pass (the default on
    /// tile-based GPUs) or not (VulkanRenderer.SingleFetch.cs).</summary>

    public const string FetchPassMerge = "VITASHARP_FETCH_PASS_MERGE";
    /// <summary>1/0: with rasterization-order attachment access, programmable-blending draws of any size render in the
    /// one-sample fetch pass instead of reading a copy of the surface (default on tile-based GPUs; VulkanRenderer.SingleFetch.cs).</summary>
    public const string FetchLarge = "VITASHARP_FETCH_LARGE";
    /// <summary>=1: generated shaders copy register-resident uniforms into SA with a loop, as generator v19 did (A/B runs).</summary>
    public const string ShaderUniformLoop = "VITASHARP_SHADER_UNIFORM_LOOP";
    /// <summary>1/0: other draws of the scene keep rendering in an open fetch pass instead of ending it (default on
    /// tile-based GPUs; VulkanRenderer.SingleFetch.cs).</summary>
    public const string FetchPassJoin = "VITASHARP_FETCH_PASS_JOIN";
    /// <summary>1/0: draws that sample the depth buffer they test against read it in place, in a fetch pass whose depth
    /// is read-only, instead of a copy that ends the pass (default on tile-based GPUs; VulkanRenderer.SingleFetch.cs).</summary>
    public const string DepthFeedback = "VITASHARP_DEPTH_FEEDBACK";
    /// <summary>1/0: a vertex shader declares and writes only the varyings its draw's fragment program reads, instead
    /// of all 13 (VulkanRenderer.Draw.cs, ShaderHints.VaryingMask).</summary>
    public const string VaryingTrim = "VITASHARP_VARYING_TRIM";
    public const string FirmwarePup = "VITASHARP_FIRMWARE_PUP";
    /// <summary>=1: the Khronos validation layer when it can be found (Android: an APK built with
    /// -p:VitaSharpValidationLayer=path/to/arm64-v8a/libVkLayer_khronos_validation.so); messages go to the log. =sync adds synchronization validation.</summary>
    public const string VkValidation = "VITASHARP_VK_VALIDATION";
    /// <summary>Tests: an installed firmware to run firmware modules from (a Vita3K data folder with vs0/, or a vs0
    /// folder itself). Unset: %APPDATA%\Vita3K\Vita3K when it exists, else the PSVUPDAT.PUP of FirmwarePup.</summary>
    public const string FirmwareDir = "VITASHARP_FIRMWARE_DIR";
    public const string Ffmpeg = "VITASHARP_FFMPEG";
    public const string FuzzIter = "VITASHARP_FUZZ_ITER";
    public const string G16 = "VITASHARP_G16";
    public const string GeneratorVersionSalt = "VITASHARP_GENERATOR_VERSION_SALT";
    public const string GlfwPlatform = "VITASHARP_GLFW_PLATFORM";
    public const string GpuDumpAt = "VITASHARP_GPU_DUMP_AT";
    /// <summary>=1: undefined Vulkan contents get loud values (new render targets magenta, depth/stencil discarded at a
    /// scene's end cleared to 0 / 0xFF), so tiler-only flicker shows on a desktop GPU (VulkanRenderer.Tiler.cs).</summary>
    public const string GpuPoison = "VITASHARP_GPU_POISON";
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
    /// <summary>replay-gpu --mali-cost: path of the Arm Mali Offline Compiler (malioc.exe) when it is not on PATH or in Arm Performance Studio's folder.</summary>
    public const string Malioc = "VITASHARP_MALIOC";
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
    /// <summary>=file: the native backends write one line per generated block (host address, size, guest pc, code
    /// bytes) for attributing CPU-profiler samples in the code arena (Cpu/Native/NativeCodeMap).</summary>
    public const string NativeMap = "VITASHARP_NATIVE_MAP";
    /// <summary>=off: ARM64 code leaves native code for every HLE call (SVC) instead of calling the HLE dispatch from it
    /// (Cpu/Native/ArmFrontend.HleCallouts).</summary>
    public const string NativeHleCallout = "VITASHARP_NATIVE_HLE_CALLOUT";
    /// <summary>=off: the GPU queue releases its semaphore and pulses its progress monitor for every command, as before
    /// (Gpu/GpuQueue.LightSignals; default: only a sleeping worker or a waiting thread is signalled).</summary>
    public const string GpuQueueLight = "VITASHARP_GPU_QUEUE_LIGHT";
    /// <summary>=0: the HLE functions that have a guest-code version (Hle/HleGuestCode: libgxm's precomputed-state
    /// packing) are bound to their C# versions through SVC stubs, as before.</summary>
    public const string HleGuestCode = "VITASHARP_HLE_GUEST_CODE";
    /// <summary>Mono only (Android): =on enters native code with the runtime's GC transition, as CoreCLR always does;
    /// the default skips it (SuppressGCTransition: the thread stays in managed mode while guest code runs, which Mono's
    /// hybrid suspend handles; callouts from that mode are allowed).</summary>
    public const string NativeGcTransition = "VITASHARP_NATIVE_GC_TRANSITION";
    /// <summary>ARM64 backend: comma-separated optimisations to turn off for bisection on a device (all, or any of
    /// pin, backedge, fuse, pair, vcache, fpcr, fpcheck, dirty, lit, patch, cond, stub; see Arm64Emitter.A64Opt and docs/notes/native-jit.md).</summary>
    public const string NativeA64NoOpt = "VITASHARP_NATIVE_A64_NOOPT";
    public const string NativeNoinline = "VITASHARP_NATIVE_NOINLINE";
    public const string NativeNolink = "VITASHARP_NATIVE_NOLINK";
    public const string NativeTierup = "VITASHARP_NATIVE_TIERUP";
    public const string NativeValidate = "VITASHARP_NATIVE_VALIDATE";
    public const string NativeVerify = "VITASHARP_NATIVE_VERIFY";
    public const string PeekEnd = "VITASHARP_PEEK_END";
    public const string Replay = "VITASHARP_REPLAY";
    /// <summary>Internal resolution scale of colour/depth targets (0.5, 0.75; 1 = native, the default): see VulkanRenderer.ResScale.cs.</summary>
    public const string ResScale = "VITASHARP_RES_SCALE";
    /// <summary>1: a stage binding the same texture descriptors as an earlier one in the frame reuses its descriptor set (VulkanRenderer.TexSetReuse.cs).</summary>
    public const string TexSetReuse = "VITASHARP_TEXSET_REUSE";
    /// <summary>1: reused texture sets live in a pool of their own and survive the frame's pool resets (a stage bound in an
    /// earlier submit reuses its set after a per-frame check of its memory textures; implies TexSetReuse).</summary>
    public const string TexSetPersist = "VITASHARP_TEXSET_PERSIST";
    /// <summary>1: vkQueuePresentKHR runs on a thread of its own, off the GPU worker (VulkanRenderer.PresentThread.cs).</summary>
    public const string PresentThread = "VITASHARP_PRESENT_THREAD";
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
    /// <summary>=1 (developer builds, Android): Perfetto/ATrace markers (Core/Util/Developer/DevTrace.cs, build/android-trace.*).</summary>
    public const string Trace = "VITASHARP_TRACE";
    /// <summary>With <see cref="Trace"/>: one HLE call in N gets a marker (default 64, rounded up to a power of two).</summary>
    public const string TraceHle = "VITASHARP_TRACE_HLE";
    /// <summary>=1[:threshold] (developer APK): in-game flicker check of every presented frame (FlickerDetector).</summary>
    public const string FlickerCheck = "VITASHARP_FLICKER_CHECK";
    /// <summary>KIND@START:STOP (developer APK): the pause menu's capture (tempo or conteudo), started and stopped that many
    /// seconds after the game starts.</summary>
    public const string Capture = "VITASHARP_CAPTURE";
    /// <summary>=SECONDS (0 = off): the session capture's pre-roll ("Pré-gravação", --capture-preroll) keeps the last
    /// frames, timing and input in memory with a periodic save state, so a recording started after a glitch includes
    /// it (docs/notes/session-capture.md).</summary>
    public const string CapturePreRoll = "VITASHARP_CAPTURE_PREROLL";
    /// <summary>=START:STOP (guest frames): records a session capture over those frames (--capture-at; tests).</summary>
    public const string CaptureAt = "VITASHARP_CAPTURE_AT";



    public const string Texdirty = "VITASHARP_TEXDIRTY";
    /// <summary>=0: vertex and index data are uploaded for every draw (no cached copies of unchanged ranges).</summary>
    public const string VertexCache = "VITASHARP_VERTEX_CACHE";
    /// <summary>1: vertex/index ranges below 16 KB are cached too, sub-allocated from large buffers (VulkanRenderer.VertexCache.cs).</summary>
    public const string VertexCacheSmall = "VITASHARP_VERTEX_CACHE_SMALL";
    /// <summary>Smallest range VITASHARP_VERTEX_CACHE_SMALL caches, in bytes (default 512).</summary>
    public const string VertexCacheSmallMin = "VITASHARP_VERTEX_CACHE_SMALL_MIN";
    public const string TraceRt = "VITASHARP_TRACE_RT";
    /// <summary>Directory: draw traces (--trace-draws) also write each draw's uniform blocks there as raw files.</summary>
    public const string TraceUniforms = "VITASHARP_TRACE_UNIFORMS";
    /// <summary>Hex guest address: texture uploads of it (renderer) and JPEG decodes into it are logged with host times.</summary>
    public const string TraceTex = "VITASHARP_TRACE_TEX";
    public const string TraceSnap = "VITASHARP_TRACE_SNAP";
    public const string TraceSync = "VITASHARP_TRACE_SYNC";
    public const string TraceVis = "VITASHARP_TRACE_VIS";
    public const string TscaleProc = "VITASHARP_TSCALE_PROC";
    public const string TscaleProcLr = "VITASHARP_TSCALE_PROC_LR";
    public const string TscaleProcThread = "VITASHARP_TSCALE_PROC_THREAD";
    public const string TscaleVblank = "VITASHARP_TSCALE_VBLANK";
    public const string TscaleWait = "VITASHARP_TSCALE_WAIT";
    public const string UbUnbounded = "VITASHARP_UB_UNBOUNDED";
    /// <summary>=0 (A/B): the GPU worker does not re-read uniform buffers written after the draw call (default: re-read).</summary>
    public const string UbLate = "VITASHARP_UB_LATE";
    /// <summary>=0 (A/B): the late uniform copy happens when the GPU worker gets the draw, not after the guest ended the
    /// draw's scene (default: waits for sceGxmEndScene / sceGxmMidSceneFlush). =frame: waits until the frame's display-queue
    /// entry instead (the hardware runs a scene up to a frame later; experiment). =check (or =frame,check): also logs how
    /// many held draws had their buffers rewritten before the scene's end, and between the scene's end and the flip.
    /// =audit (combinable): samples draws and re-hashes their vertex uniform buffers at the scene's end, at the frame's
    /// flip and at the next flip, per vertex program (does the game write them after sceGxmEndScene?).</summary>
    public const string UbSceneEnd = "VITASHARP_UB_SCENE_END";
    /// <summary>=all (A/B): a uniform buffer's copy stops at any bound buffer address after it, also at indices the
    /// program does not read (default: only the program's own buffers bound the copy).</summary>
    public const string UbClamp = "VITASHARP_UB_CLAMP";
    /// <summary>=0 (A/B): sceTouchPeek always reports 1 sample, instead of 0 when sceTouchRead already consumed the newest
    /// one (Sonic & All-Stars Racing Transformed hangs draining the panel otherwise).</summary>
    public const string TouchPeekUnread = "VITASHARP_TOUCH_PEEK_UNREAD";
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
    /// <summary>Android: seconds between the periodic session statistics in the log file (default 30, 0 = off).</summary>
    public const string SessionReportSeconds = "VITASHARP_SESSION_REPORT_SECONDS";
    /// <summary>Developer builds: the GPU worker spends FACTOR times its real time on each command (busy wait), to
    /// reproduce a phone's worker cost on a PC (default 1).</summary>
    public const string GpuWorkerSlowdown = "VITASHARP_GPU_WORKER_SLOWDOWN";
    /// <summary>1: a display queue entry's flip callback runs once the GPU worker recorded and submitted the frame, not
    /// once the GPU finished it (the callback only flips; presents stay in GPU order). Default 0.</summary>
    public const string DisplayEarlySignal = "VITASHARP_DISPLAY_EARLY_SIGNAL";
    /// <summary>1 (A/B): a scene's GPU work waits until the display released its colour buffer (fragment sync object);
    /// trace also logs the waits. Default off.</summary>
    public const string DisplaySync = "VITASHARP_DISPLAY_SYNC";
    /// <summary>1 (A/B): sceGxmDisplayQueueAddEntry keeps at most displayQueueMaxPendingCount entries queued or flipping.</summary>
    public const string DisplayQueueStrict = "VITASHARP_DISPLAY_QUEUE_STRICT";
    /// <summary>1 (experiment): sceDisplaySetFrameBuf in IMMEDIATE mode flips at most once per vblank (a second flip in the
    /// same vblank waits for the next one). Default off.</summary>
    public const string DisplayImmediatePace = "VITASHARP_DISPLAY_IMMEDIATE_PACE";
    /// <summary>0: textures always read a decoder's output buffer as it is now, never an earlier picture of the
    /// decoder that matches their size (Gpu/DecoderOutputs.cs). Default on.</summary>
    public const string DecoderHistory = "VITASHARP_DECODER_HISTORY";
    /// <summary>=1: every guest delay (sceKernelDelayThread) spins its last half millisecond (the old behaviour), =0: none does; default: only delays of 5 ms or more (Core/Kernel/Sync.cs Wait.Sleep).</summary>
    public const string DelaySpin = "VITASHARP_DELAY_SPIN";
    /// <summary>0 (A/B): an ATRAC9 superframe that fails to decode is skipped (no samples) instead of playing as a superframe of silence; its bytes count as consumed either way.</summary>
    public const string At9ErrorSilence = "VITASHARP_AT9_ERROR_SILENCE";
    /// <summary>Vulkan command-buffer slots in flight (2-6, default 3; each holds a 64 MB host upload ring).</summary>
    public const string FramesInFlight = "VITASHARP_FRAMES_IN_FLIGHT";
    /// <summary>1: GXM notifications and display-queue sync points wait for visibility results only, not for render-target
    /// writebacks (published when their frame retires, at the latest by the next flip). Default 0.</summary>
    public const string LazyWritebackSync = "VITASHARP_LAZY_WRITEBACK_SYNC";
}
