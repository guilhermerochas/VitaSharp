namespace VitaSharp.Core.Util;

/// <summary>
/// Every <c>VITASHARP_*</c> environment variable the emulator, its tools and its tests read, in one place. Most are
/// debugging or testing switches; what each one does is documented where it is read (and in docs/notes/*.md).
/// </summary>
public static class EnvVars
{
    public const string Prefix = "VITASHARP_";

    /// <summary>Developer self-test of the native crash log (Util/NativeCrashLog.cs, Windows x64): =heap ends the
    /// desktop front-end with native heap corruption (0xC0000374) right after the log opens, =access with an access
    /// violation in native code (0xC0000005) after a caught managed null reference.</summary>
    public const string DebugNativeCrash = "VITASHARP_DEBUG_NATIVE_CRASH";

    /// <summary>=0: BC/PVRTC textures are decoded to RGBA8 even on GPUs that sample ASTC (no ASTC transcoding).</summary>
    /// <summary>Android: scripted buttons/sticks for unattended device runs (ScriptedPad: "cross@12,start@20:0.5").</summary>
    public const string AndroidInput = "VITASHARP_ANDROID_INPUT";
    /// <summary>Android: a file polled while the game runs; its ScriptedPad steps (T relative to when it is read) are added and the file deleted, so a PC script can react to the screen (adb push).</summary>
    public const string AndroidInputLive = "VITASHARP_ANDROID_INPUT_LIVE";
    /// <summary>Android developer builds: a file polled every 250 ms on the UI thread, also while the game is paused (the app screens poll the path, the game screen the path + ".game"; Platforms/Android/Developer/UiScript.cs); each line ("key:dpad_down", "key:button_a", "touch:x:y" in 0..1 of the screen, "drag:x1:y1:x2:y2:ms") is injected into the game activity as a controller key or a touch, then the file is deleted (drives the pause menu where the phone refuses adb shell input).</summary>
    public const string AndroidUiInput = "VITASHARP_ANDROID_UI_INPUT";
    /// <summary>Android A/B switch for the Trofeus screen's opening (measured on the phone, phases in the "Trophies:" log
    /// line): =async loads the list on a worker thread behind a spinner (the old way); default: from the cache on the UI
    /// thread before the first render; =noanim also pushes the page without the transition animation.</summary>
    public const string AndroidTrophiesOpen = "VITASHARP_ANDROID_TROPHIES_OPEN";
    /// <summary>A/B switch: =old builds the per-draw texture sets the way main b29e26a did (all 16 units visited, render
    /// targets scanned through the dictionary); default new (only the sampled units, flat render-target range table).</summary>
    public const string TexLookup = "VITASHARP_TEXLOOKUP";
    /// <summary>=0: no memo of resolved texture units in the GPU worker (Renderer.Vulkan VulkanRenderer.UnitMemo, A/B).</summary>
    public const string UnitMemo = "VITASHARP_UNIT_MEMO";
    /// <summary>=0: no memo of the shader entry per program and texture formats in the GPU worker (A/B).</summary>
    public const string ShaderMemo = "VITASHARP_SHADER_MEMO";
    /// <summary>=0: texture images are not kept for earlier contents (Renderer.Vulkan VulkanRenderer.TexContentCache.cs, A/B).</summary>
    public const string TexContentCache = "VITASHARP_TEX_CONTENT_CACHE";
    /// <summary>=legacy: swizzled and tiled textures are converted to linear rows texel by texel, as before the table and
    /// run copies of Gxm/TextureLayout.cs (A/B).</summary>
    public const string TexLayout = "VITASHARP_TEX_LAYOUT";
    /// <summary>=0: a retired frame's deferred actions (destruction, uniform chunk references) wait until its slot is
    /// reused, as before (Renderer.Vulkan VulkanRenderer.RetireFrame, A/B).</summary>
    public const string RetireDeferred = "VITASHARP_RETIRE_DEFERRED";
    /// <summary>Developer builds: presented frame from which VITASHARP_WORKER_PROF measures (default 30).</summary>
    public const string WorkerProfFrom = "VITASHARP_WORKER_PROF_FROM";
    /// <summary>=1: every HLE call is timed (wall clock, blocking included) per guest thread, and each periodic summary
    /// adds "PERF hle time" lines: where the busiest guest threads spend the time since the previous summary, per HLE
    /// function (Util/HleTimeReport.cs). Two timestamps per call; works on every platform (Android included).</summary>
    public const string PerfHleTime = "VITASHARP_PERF_HLE_TIME";
    /// <summary>A/B switch: =old scans draw indices for their min/max one at a time; default new (128-bit vectors).</summary>
    public const string MinMaxScan = "VITASHARP_MINMAX";
    /// <summary>A/B switch: =1 records the hot vkCmd* calls through device-level function pointers loaded once with
    /// vkGetDeviceProcAddr (no Silk.NET dispatch, no GC transition); default 0 (Silk.NET).</summary>
    public const string VkDirect = "VITASHARP_VK_DIRECT";
    /// <summary>=0: every draw binds its vertex/index buffers, descriptor set 0 and push constants again; default on (a
    /// call that would bind what the command buffer has bound is skipped, the index buffer rebased by the first index).</summary>
    public const string DrawDedup = "VITASHARP_DRAW_DEDUP";
    /// <summary>Android: =0 tries the hardware H.264 decoders first even for pictures up to 960x544 (default: Android's own first there), =1 always Android's own first.</summary>
    public const string MediaCodecSoftware = "VITASHARP_MEDIACODEC_SOFTWARE";
    /// <summary>Android: MediaCodec H.264 output timing (Mobile.Core/Media/MediaCodecSupport.cs, SynchronousOutput).
    /// Default: the first access unit of a stream waits for its picture (sceAvcdecDecode answers it with one), later ones
    /// take what is done with a 5 ms grace, and SPSs without a reorder limit get one; =all: every unit waits for its
    /// picture; =0: the old timing (no wait before the first picture, no reorder limit), for A/B runs.</summary>
    public const string MediaCodecSync = "VITASHARP_MEDIACODEC_SYNC";
    /// <summary>Android: the longest wait, in microseconds, for the first picture of a MediaCodec H.264 stream (default
    /// 250000).</summary>
    public const string MediaCodecFirstUs = "VITASHARP_MEDIACODEC_FIRST_US";
    /// <summary>Android: =1 makes a new MediaCodec H.264 decoder decode one synthetic picture first, so its threads and
    /// buffers exist before the game's first access unit.</summary>
    public const string MediaCodecWarmUp = "VITASHARP_MEDIACODEC_WARMUP";
    /// <summary>Android: =all drains the first picture of every MediaCodec H.264 stream out early (default: only of a
    /// stream that does not reorder; a reordering one gets its first picture after its reorder depth, as on the Vita).</summary>
    public const string MediaCodecPrime = "VITASHARP_MEDIACODEC_PRIME";
    /// <summary>Android, A/B: =N keeps at most N access units in flight in a MediaCodec H.264 decoder (their pictures not
    /// out yet): the next unit waits, draining the output, up to 100 ms (Mobile.Core/Media/MediaCodecSupport.cs,
    /// InputPacing). Set it above the streams' reorder depth (Vita movies: 4 or fewer). Default 0: no pacing.</summary>
    public const string MediaCodecMaxInFlight = "VITASHARP_MEDIACODEC_MAX_IN_FLIGHT";
    /// <summary>Android, A/B: =refs gives an H.264 SPS without a reorder limit max_num_reorder_frames = its
    /// max_num_ref_frames from the first unit (H264ReorderLimit.ReorderUpToRefFrames), instead of 0 until a B slice
    /// restarts the decoder with the stream's own SPS (the whole DPB). Default: unset.</summary>
    public const string MediaCodecReorder = "VITASHARP_MEDIACODEC_REORDER";
    /// <summary>A/B: =old makes sceAvPlayerJumpToTime hand out the pictures before the jump time and accept a jump
    /// while another is in progress (the code before Media/MediaPlayerBase.JumpTargetUs). Default: unset.</summary>
    public const string AvPlayerJump = "VITASHARP_AVPLAYER_JUMP";
    /// <summary>A/B: =newest makes sceAvPlayerGetVideoData hand out the newest picture due on the player's clock and drop
    /// the older ones (the code before the firmware's one-picture-per-call pacing). Default: unset.</summary>
    public const string AvPlayerPacing = "VITASHARP_AVPLAYER_PACING";
    /// <summary>Tests: path of a media parity trace pulled from an Android device or emulator
    /// (build/android-media-parity.sh); MediaParityTests compares it with the desktop's (skipped when unset).</summary>
    public const string AndroidMediaTrace = "VITASHARP_ANDROID_MEDIA_TRACE";
    /// <summary>Android: AAudio buffer capacity requested in frames (default 0 = AAudio's own; 8192 = the earlier request).</summary>
    public const string AAudioCapacity = "VITASHARP_AAUDIO_CAPACITY";
    public const string Astc = "VITASHARP_ASTC";
    /// <summary>Size limit in MB of a title's ASTC transcode disk cache (default 512).</summary>
    public const string AstcCacheMb = "VITASHARP_ASTC_CACHE_MB";
    /// <summary>Where a BC/PVRTC level missing from the ASTC caches is encoded: =0 (gpu) on the GPU thread (desktop
    /// default), =wait by the texture transcoder threads while the GPU thread waits (Android default: exact textures),
    /// =placeholder by the transcoder threads while the texture shows a coarse placeholder and is uploaded again when
    /// ready (opt-in, not the console's behaviour) (Renderer.Vulkan AstcTranscoder.cs).</summary>
    public const string AstcAsync = "VITASHARP_ASTC_ASYNC";
    /// <summary>Texture transcoder threads of the wait/placeholder modes (default: a quarter of the processors, 1-2).</summary>
    public const string AstcWorkers = "VITASHARP_ASTC_WORKERS";
    /// <summary>=0: in the wait mode the GPU thread only waits for the texture transcoders instead of running their
    /// queued row jobs too (Renderer.Vulkan AstcTranscoder.Help).</summary>
    public const string AstcHelp = "VITASHARP_ASTC_HELP";
    /// <summary>=0: BC1 levels go through the ASTC encoder's search like the other formats instead of the direct
    /// block mapping (A/B; Renderer.Vulkan AstcTranscoder.Bc1Direct).</summary>
    public const string AstcBc1Direct = "VITASHARP_ASTC_BC1_DIRECT";
    public const string AsyncPipelines = "VITASHARP_ASYNC_PIPELINES";
    public const string AsyncShaders = "VITASHARP_ASYNC_SHADERS";
    public const string AudioDevice = "VITASHARP_AUDIO_DEVICE";
    /// <summary>0|1: "compilar shaders ao carregar o programa" (VulkanRendererOptions.CompileOnProgramLoad, --compile-on-program-load).</summary>
    public const string CompileOnProgramLoad = "VITASHARP_COMPILE_ON_PROGRAM_LOAD";
    /// <summary>Android: folder (relative to the data folder) for per-port WAVs of the guest's audio (desktop: --audio-dump).</summary>
    public const string AudioDump = "VITASHARP_AUDIO_DUMP";
    /// <summary>Fraction of the rest of a grain sceAudioOutOutput waits when a host device hands two grains back at
    /// once (default 0.5, as Vita3K; 0 = off).</summary>
    public const string AudioSpacing = "VITASHARP_AUDIO_SPACING";
    /// <summary>=0: a depth/stencil image created for a scene that loads its surface keeps Vulkan's undefined content
    /// instead of the surface's earlier image (A/B of VulkanRenderer.SeedLoadedDepth).</summary>
    public const string DepthSeed = "VITASHARP_DEPTH_SEED";
    /// <summary>=1: FFmpeg video decoders hand pictures out as soon as decoded (decode order with B-frames) instead of in
    /// display order (A/B).</summary>
    public const string VideoLowDelay = "VITASHARP_VIDEO_LOW_DELAY";
    /// <summary>=1: logs every NGS source voice event (play, key off, buffer callbacks, player/ATRAC9 buffer table
    /// changes) with the reported play position; with --audio-dump each source voice's output also gets its own WAV.</summary>
    public const string NgsTrace = "VITASHARP_NGS_TRACE";
    /// <summary>=0: the NGS envelope module is a passthrough and a key off stops the voice at once (as before; A/B).</summary>
    public const string NgsEnvelope = "VITASHARP_NGS_ENVELOPE";
    /// <summary>=picture: an asynchronous H.264 decoder's pictures keep their own pts in sceAvcdecDecode (default: the pts of
    /// the access unit of the call; Hle/Modules/Videodec.cs).</summary>
    public const string AvcdecPts = "VITASHARP_AVCDEC_PTS";
    /// <summary>=1: an asynchronous H.264 decoder's first picture is repeated while the decoder fills up (default: no
    /// picture in those calls, as on the Vita; Hle/Modules/Videodec.cs).</summary>
    public const string AvcdecFillRepeat = "VITASHARP_AVCDEC_FILL_REPEAT";
    /// <summary>=1: a new or flushed asynchronous H.264 decoder answers its first calls with the previous decoder's or
    /// pass's last picture (default: no picture; Hle/Modules/Videodec.cs).</summary>
    public const string AvcdecCarryOver = "VITASHARP_AVCDEC_CARRYOVER";
    /// <summary>Tests: =1 opens the game window hidden (no focus, nothing on screen) while it keeps its Vulkan surface and
    /// swapchain, so the windowed path (presents, renderer swaps on state loads) runs on a PC that is in use; no audio
    /// output either.</summary>
    public const string HiddenWindow = "VITASHARP_HIDDEN_WINDOW";
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
    /// <summary>The PSP compat core's GE: =own draws on a Vulkan device of its own; unset or anything else, on the
    /// renderer's device when it has a second graphics queue (GeVulkanRasterizerDevice).</summary>
    public const string GeDevice = "VITASHARP_GE_DEVICE";
    /// <summary>Megabytes: lowers the storage-buffer range the CDRAM mirror is bound with (default: the device's
    /// maxStorageBufferRange), to test the windowed binding of drivers below 288 MB (MappedWindow.cs).</summary>
    public const string GpuMaxStorageRange = "VITASHARP_GPU_MAX_STORAGE_RANGE";
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
    /// <summary>=1: the emulator option "Enable Kernel Plugins" (--kernel-plugins=on|off, the launcher's and the Android
    /// app's switch win over it): the plugins of ur0:kernel_plugins/config.txt (else ur0:tai / ux0:tai config.txt) are loaded. Default off: the pure firmware,
    /// no plugin.</summary>
    public const string KernelPlugins = "VITASHARP_KERNEL_PLUGINS";
    /// <summary>The first calls of each plugin hook whose action the log shows (arguments, continuation, the original's
    /// and the hook's returns, writes to the memory the arguments point to): 8 by default, 0 for none, at most 256; after
    /// them a hook costs only its counters.</summary>
    public const string PluginHookTrace = "VITASHARP_PLUGIN_HOOK_TRACE";
    /// <summary>Default of --net-allow=host,host: with real internet, only these hosts (names or addresses) are reachable.</summary>
    public const string NetAllow = "VITASHARP_NET_ALLOW";
    /// <summary>=folder: the IL backend also writes the IL of every block it compiles to assemblies there
    /// (il_blocks_NNN.dll, for ILSpy) plus index.tsv, hottest first (Cpu/Backends/IlDump.cs, docs/notes/il-jit.md).</summary>
    public const string IlDump = "VITASHARP_IL_DUMP";
    /// <summary>File the native backends append every compiled block's instructions to (a corpus for the compile-cost
    /// benchmark NativeJitTests.CompileCostBenchmark, which reads it from <see cref="BenchCorpus"/>).</summary>
    public const string NativeCorpus = "VITASHARP_NATIVE_CORPUS";
    /// <summary>Tests: the block corpus (recorded with <see cref="NativeCorpus"/>) the compile-cost benchmark compiles.</summary>
    public const string BenchCorpus = "VITASHARP_BENCH_CORPUS";
    /// <summary>tests/VitaSharp.Benchmarks: the GPU dump (.vsgd) the GPU worker and shader translation benchmarks replay
    /// (a short dump of a game, never in git; build/bench.* -Dump / --dump).</summary>
    public const string BenchDump = "VITASHARP_BENCH_DUMP";
    /// <summary>tests/VitaSharp.Benchmarks: an .at9 file the ATRAC9 benchmark decodes (without it, generated
    /// superframes that the decoder accepts).</summary>
    public const string BenchAt9 = "VITASHARP_BENCH_AT9";
    /// <summary>tests/VitaSharp.Benchmarks: variants of the GPU worker benchmark besides "base", '|'-separated, each a
    /// ','-separated list of KEY=VALUE switches (build/bench.* --variants), measured in one run on the same build.</summary>
    public const string BenchVariants = "VITASHARP_BENCH_VARIANTS";

    /// <summary>=1: x64 native blocks count the VFP/NEON ops and conditional branches they execute (profiling of
    /// register pinning / compare-branch fusion; reported on the PERF cycles lines). ARM64 native blocks count the
    /// executed blocks and guest instructions (the PERF cycles line's "K blocks") only with it (Arm64Emitter.A64Opt.NoBlockCounts).</summary>
    public const string NativeCounters = "VITASHARP_NATIVE_COUNTERS";
    /// <summary>=1: loading a save state keeps the translated blocks whose guest code the state did not change
    /// (BlockCache.Revalidate) instead of dropping the whole translation cache (off until measured on the phone).</summary>
    public const string LoadKeepCode = "VITASHARP_LOAD_KEEP_CODE";
    public const string NativeDump = "VITASHARP_NATIVE_DUMP";
    /// <summary>=0: VCVT between floating-point and fixed-point stays an interpreter callout in native code (A/B
    /// switch of the inline form, ArmFrontend.VfpOther).</summary>
    public const string NativeVcvtFixed = "VITASHARP_NATIVE_VCVT_FIXED";
    /// <summary>=file: the native backends write one line per generated block (host address, size, guest pc, code
    /// bytes) for attributing CPU-profiler samples in the code arena (Cpu/Native/NativeCodeMap).</summary>
    public const string NativeMap = "VITASHARP_NATIVE_MAP";
    /// <summary>Measurement of the data abort guards (Cpu/DataAborts.cs): =1 every process checks its guest accesses from
    /// the start, as when a kernel module registered a data abort handler (the guards' cost on any title); =0 they are
    /// never switched on, also when a handler is registered (unmapped accesses read zeros, as before).</summary>
    public const string DataAbortGuards = "VITASHARP_DATA_ABORT_GUARDS";
    /// <summary>=0: native code checks every guest access with a guard while data aborts are delivered, also where the
    /// host's memory protection could find them at no cost (Windows x64, <see cref="Cpu.Native.HostFaultAborts"/>).</summary>
    public const string DataAbortFaults = "VITASHARP_DATA_ABORT_FAULTS";
    /// <summary>=off: ARM64 code leaves native code for every HLE call (SVC) instead of calling the HLE dispatch from it
    /// (Cpu/Native/ArmFrontend.HleCallouts).</summary>
    public const string NativeHleCallout = "VITASHARP_NATIVE_HLE_CALLOUT";
    /// <summary>=off: the GPU queue releases its semaphore and pulses its progress monitor for every command, as before
    /// (Gpu/GpuQueue.LightSignals; default: only a sleeping worker or a waiting thread is signalled).</summary>
    public const string GpuQueueLight = "VITASHARP_GPU_QUEUE_LIGHT";
    /// <summary>=0: one process's GPU scene at a time (the others' submissions wait); default: processes sharing the GPU
    /// (the shell and an application) keep a scene open each and the backend interleaves their renderings
    /// (Gpu/GpuQueue.InterleaveScenes).</summary>
    public const string GpuSceneInterleave = "VITASHARP_GPU_SCENE_INTERLEAVE";
    /// <summary>=0: the HLE functions that have a guest-code version (Hle/HleGuestCode: libgxm's precomputed-state
    /// packing, SceLibKernel's TLS lookups and small Clib copies) are bound to their C# versions through SVC stubs, as before;
    /// =gxm: only libgxm's routines (SceLibKernel's through SVCs); =notex: without libgxm's texture-control-word and
    /// program-header routines; =nolw: without the lightweight mutex/cond routines; =nogxmctx: without the libgxm context
    /// state setters (contexts keep that state in C#); =precomputed: only libgxm's precomputed-state ones.</summary>
    public const string HleGuestCode = "VITASHARP_HLE_GUEST_CODE";
    /// <summary>Mono only (Android): =on enters native code with the runtime's GC transition, as CoreCLR always does;
    /// the default skips it (SuppressGCTransition: the thread stays in managed mode while guest code runs, which Mono's
    /// hybrid suspend handles; callouts from that mode are allowed).</summary>
    public const string NativeGcTransition = "VITASHARP_NATIVE_GC_TRANSITION";
    /// <summary>=off: bulk copies (Util/FastCopy) use Span.CopyTo; by default Mono and CoreCLR on ARM64 Linux/Android call
    /// the C library's memmove for copies of 128 bytes or more.</summary>
    public const string NativeMemmove = "VITASHARP_NATIVE_MEMMOVE";
    /// <summary>ARM64 backend: comma-separated optimisations to turn off for bisection on a device (all, or any of
    /// pin, backedge, fuse, pair, vcache, fpcr, fpcheck, dirty, lit, patch, cond, stub, succ, count, fence, group, fpentry,
    /// packed, chain, vlazy, assoc, scache, guard; see Arm64Emitter.A64Opt and docs/notes/native-jit.md).</summary>
    public const string NativeA64NoOpt = "VITASHARP_NATIVE_A64_NOOPT";
    public const string NativeNoinline = "VITASHARP_NATIVE_NOINLINE";
    public const string NativeNolink = "VITASHARP_NATIVE_NOLINK";
    public const string NativeTierup = "VITASHARP_NATIVE_TIERUP";
    public const string NativeValidate = "VITASHARP_NATIVE_VALIDATE";
    public const string NativeVerify = "VITASHARP_NATIVE_VERIFY";
    public const string PeekEnd = "VITASHARP_PEEK_END";
    /// <summary>=1: the end-of-run report lists every HLE function called, of every process (not only the 40 busiest),
    /// as "hle-call PROCESS COUNT NAME" lines (proves which HLE paths a run exercised).</summary>
    public const string HleCalls = "VITASHARP_HLE_CALLS";
    /// <summary>TITLEID:param (developer): the launch parameter an application started directly (run ... --no-shell)
    /// reads with sceAppMgrGetAppParam, as if the shell had launched it with it (e.g. the PSP emulator NPXS10028 with the
    /// "originalpath=...&amp;selfpath=...&amp;discid=..." string the shell builds for a PSP/PSone title).</summary>
    public const string AppParam = "VITASHARP_APP_PARAM";
    public const string Replay = "VITASHARP_REPLAY";
    /// <summary>Internal resolution scale of colour/depth targets (0.5, 0.75 below native; 2, 3, 4 above it; 1 = native, the default): see VulkanRenderer.ResScale.cs.</summary>
    public const string ResScale = "VITASHARP_RES_SCALE";
    /// <summary>1: a stage binding the same texture descriptors as an earlier one in the frame reuses its descriptor set (VulkanRenderer.TexSetReuse.cs).</summary>
    public const string TexSetReuse = "VITASHARP_TEXSET_REUSE";
    /// <summary>1: reused texture sets live in a pool of their own and survive the frame's pool resets (a stage bound in an
    /// earlier submit reuses its set after a per-frame check of its memory textures; implies TexSetReuse).</summary>
    public const string TexSetPersist = "VITASHARP_TEXSET_PERSIST";
    /// <summary>1: vkQueuePresentKHR runs on a thread of its own, off the GPU worker (VulkanRenderer.PresentThread.cs).</summary>
    public const string PresentThread = "VITASHARP_PRESENT_THREAD";
    /// <summary>A deferred single-sampled scene rendering is begun over the tiles its draws can write (the union of their
    /// region clips and viewports) instead of the whole surface, so a tiler loads and stores only those tiles
    /// (post-processing passes into regions of one surface; VulkanRenderer.Tiler.cs). Default on for tile-based GPUs
    /// (0 turns it off, 1 forces it elsewhere).</summary>
    public const string RenderAreaTrim = "VITASHARP_RENDER_AREA_TRIM";
    /// <summary>Milliseconds between two presents (emulation time) that log a <c>[hitch]</c> line with the host time of
    /// the interval by cause (Util/FrameLedger.cs); default 100, 0 turns it off.</summary>
    public const string HitchMs = "VITASHARP_HITCH_MS";
    /// <summary>Megabytes of the title's ASTC disk cache read into memory at the title start, most recently used first,
    /// so the first use of a known texture costs no file read on the GPU thread (VulkanRenderer.Astc.cs,
    /// TranscodeDiskCache.StartPreload); default 160, 0 off.</summary>
    public const string AstcPreloadMb = "VITASHARP_ASTC_PRELOAD_MB";
    /// <summary>A mapped (memory) read of the colour or depth/stencil surface of the scene being rendered keeps the copy
    /// of it already taken in that scene instead of refreshing it after each of the scene's renderings, as the Vita's
    /// memory holds the surface as it was before the scene until the scene stores it (VulkanRenderer.Mapped.cs); every
    /// such read used to end the rendering. Default on for tile-based GPUs (0 turns it off, 1 forces it elsewhere).</summary>
    public const string MappedSceneStart = "VITASHARP_MAPPED_SCENE_START";
    /// <summary>1: native MSAA scenes render with VK_EXT_multisampled_render_to_single_sampled where the device has it:
    /// the samples live in tile memory only, the surface itself is loaded into every sample and resolved on store, with
    /// no multisampled colour image, scene-start copy or sample-load draw (VulkanRenderer.MsaaNative.cs). Off by default;
    /// without the extension the current path.</summary>
    public const string MsaaRtss = "VITASHARP_MSAA_RTSS";
    /// <summary>1: logs, per render-target format, the framebuffer compression the driver applied (Mali AFBC) through
    /// VK_EXT_image_compression_control, where the device has it.</summary>
    public const string AfbcLog = "VITASHARP_AFBC_LOG";
    /// <summary>A/B, Mali framebuffer compression (AFBC): 1 = render targets carry the list of every format they are
    /// viewed with (VkImageFormatListCreateInfo); srgb = only their format and its sRGB twin; immutable = no
    /// MUTABLE_FORMAT at all. Default: mutable without a list.</summary>
    public const string RtFormatList = "VITASHARP_RT_FORMAT_LIST";
    /// <summary>Password of the RPCN account (--rpcn-user): the launcher passes it here instead of on the command line,
    /// where other processes could read it.</summary>
    public const string RpcnPassword = "VITASHARP_RPCN_PASSWORD";
    public const string SamplerDebug = "VITASHARP_SAMPLER_DEBUG";
    /// <summary>=strict: every texture upload or copy in a scene ends the rendering (the renderer before scene-break
    /// hoisting); =defer: desktop GPUs record single-sampled renderings into secondaries too, as tile-based GPUs do
    /// (A/B; by default they begin them directly). VulkanRenderer.SceneBreaks.cs.</summary>
    public const string SceneBreaks = "VITASHARP_SCENE_BREAKS";
    /// <summary>1 = every process runs in the first process's address space (the model before per-process address
    /// spaces: executables linked at addresses another process uses cannot run next to it).</summary>
    public const string SharedAddressSpace = "VITASHARP_SHARED_ADDRESS_SPACE";
    public const string ShadercOpt = "VITASHARP_SHADERC_OPT";
    /// <summary>0 (A/B): no background shaderc warm-up at renderer start (ShaderCompiler.WarmProcessInBackground).</summary>
    public const string ShadercWarm = "VITASHARP_SHADERC_WARM";
    public const string ShaderRebuild = "VITASHARP_SHADER_REBUILD";
    public const string ShaderWorkers = "VITASHARP_SHADER_WORKERS";
    public const string ShaderWorkerPrio = "VITASHARP_SHADER_WORKER_PRIO";
    /// <summary>N (A/B): the spare shader workers above the steady share, at below-normal priority, that run while the
    /// GPU thread waits for the workers (default: the CPU budget's, ShaderWorkerBudget: 90% of the processors in all;
    /// none when VITASHARP_SHADER_WORKERS / --shader-workers fixes the pool, unless this sets them).</summary>
    public const string ShaderSpareWorkers = "VITASHARP_SHADER_SPARE_WORKERS";
    /// <summary>1 = an application the shell launches replaces the shell instead of running as a second process.</summary>
    public const string SingleProcess = "VITASHARP_SINGLE_PROCESS";
    public const string SkipUnicorn = "VITASHARP_SKIP_UNICORN";
    public const string SnapFs = "VITASHARP_SNAP_FS";
    public const string Swz1 = "VITASHARP_SWZ1";
    public const string SyscallLatencyUs = "VITASHARP_SYSCALL_LATENCY_US";
    /// <summary>Tests: a VitaSharp data folder (with fs/) holding installed games and firmware, read only, that the
    /// tests of real installed modules take them from (LleLibrariesTests, the Piglet compiler tests). Unset: those
    /// tests are skipped. The suite itself always runs on a temporary data folder of its own (TestDataIsolation).</summary>
    public const string TestInstalledData = "VITASHARP_TEST_INSTALLED_DATA";
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
    /// <summary>=SECONDS (0 = no limit; default 60): "Gravar tudo" stops and saves by itself after that long
    /// (--capture-max=SECONDS; SessionCaptureOptions.MaxSeconds).</summary>
    public const string CaptureMaxSeconds = "VITASHARP_CAPTURE_MAX_SECONDS";
    /// <summary>=SECONDS (default 5): the time between the pre-roll's save states, kept in memory
    /// (SessionCaptureOptions.PreRollStateInterval; each stops the game 0.1-0.35 s).</summary>
    public const string CaptureStateInterval = "VITASHARP_CAPTURE_STATE_INTERVAL";
    /// <summary>=MB: most bytes of distinct file contents "Gravar tudo" keeps of what the game read from its own mounts
    /// (files/; default 0 = only paths, offsets, lengths and hashes, no file of the game; --capture-files-mb=MB).</summary>
    public const string CaptureFilesMegabytes = "VITASHARP_CAPTURE_FILES_MB";
    /// <summary>=1: "Gravar tudo" keeps the bytes the game read from its save data too (by default only their paths,
    /// positions and hashes: a save is the player's data; --capture-savedata).</summary>
    public const string CaptureSaveData = "VITASHARP_CAPTURE_SAVEDATA";
    /// <summary>=1: "Gravar tudo" also records what the microphone port captured (by default only its format and pace:
    /// the microphone is what the player and the people near them say; --capture-mic-audio).</summary>
    public const string CaptureMicrophoneAudio = "VITASHARP_CAPTURE_MIC_AUDIO";
    /// <summary>=1: the location source keeps answering while "Gravar tudo" is armed or recording, and session.txt keeps
    /// the --location= value (by default the position is withheld: it is where the player is; --capture-location).</summary>
    public const string CaptureLocation = "VITASHARP_CAPTURE_LOCATION";
    /// <summary>The console model when --model= is not given: "pstv" makes the unit a PlayStation TV (ports 1-4, vibration;
    /// <see cref="Hle.ConsoleModel"/>); anything else, or unset, the PCH-1000 handheld.</summary>
    public const string Model = "VITASHARP_MODEL";



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
    /// <summary>Default: the arena's chunks are not cleared; each uniform slot is written whole (the buffer's bytes, zeros
    /// for the rest). =recycle (A/B): the arena clears what was written when a chunk is recycled and slots write only
    /// their copies (the old invariant). =full: every draw's uniform slice is cleared whole when it is rented (the older
    /// cost). =check: slices start filled with a marker and a draw that still has one when it executes is logged.</summary>
    public const string UbZero = "VITASHARP_UB_ZERO";
    /// <summary>=1 (diagnostic): PERF ubstats, what the GPU worker's late uniform copies are (repeats in a frame, data
    /// equal to an earlier frame's), from VITASHARP_WORKER_PROF_FROM on.</summary>
    public const string UbStats = "VITASHARP_UB_STATS";
    /// <summary>=0 (A/B): every draw copies its large bound uniform buffers (bone palettes, arrays) itself; default: they
    /// are read from bindings of their own (split slots) and the draws of an ended scene that bind the same buffer
    /// share one copy (Hle/Modules/Gxm.CopySplit).</summary>
    public const string UbShare = "VITASHARP_UB_SHARE";
    /// <summary>=all (A/B): a uniform buffer's copy stops at any bound buffer address after it, also at indices the
    /// program does not read (default: only the program's own buffers bound the copy).</summary>
    public const string UbClamp = "VITASHARP_UB_CLAMP";
    /// <summary>=0 (A/B): sceTouchPeek always reports 1 sample, instead of 0 when sceTouchRead already consumed the newest
    /// one (Sonic & All-Stars Racing Transformed hangs draining the panel otherwise).</summary>
    public const string TouchPeekUnread = "VITASHARP_TOUCH_PEEK_UNREAD";
    /// <summary>=1: the "waiting for input" detector runs (Input/InputWaitMonitor.cs; it also runs in debug mode and while
    /// a "Gravar tudo" capture records): the log gets "[input-wait] WAITING FOR INPUT / LOADING / BUSY / ACTIVE" lines.</summary>
    public const string InputWait = "VITASHARP_INPUT_WAIT";
    /// <summary>Presented frames the "waiting for input" detector decides over (default 60).</summary>
    public const string InputWaitFrames = "VITASHARP_INPUT_WAIT_FRAMES";
    /// <summary>The screen knowledge base the agents' tools read (Capture/ScreenKnowledge.cs; default
    /// docs/games/screens.json above the working folder or the program).</summary>
    public const string Screens = "VITASHARP_SCREENS";
    /// <summary>H.264 decoding: auto (default, software), off/sw, hw (the platform's FFmpeg hwaccel) or a device type name.</summary>
    public const string VideoHw = "VITASHARP_VIDEO_HW";
    public const string VisForce = "VITASHARP_VIS_FORCE";
    public const string VramBudgetMb = "VITASHARP_VRAM_BUDGET_MB";
    public const string VramEvict = "VITASHARP_VRAM_EVICT";
    public const string VulkanLib = "VITASHARP_VULKAN_LIB";
    public const string Watch = "VITASHARP_WATCH";
    public const string WatchMax = "VITASHARP_WATCH_MAX";
    public const string WritebackMax = "VITASHARP_WRITEBACK_MAX";
    public const string WritebackRegionMax = "VITASHARP_WRITEBACK_REGION_MAX";
    /// <summary>0 (A/B): memory that shader programs store to (VLDST stores) stays only in the renderer's mirror, as
    /// before; by default it is written into guest memory when the storing scene's frame retires. Default 1.</summary>
    public const string GpuStoreWriteback = "VITASHARP_GPU_STORE_WRITEBACK";
    /// <summary>Path of a raw image file (512-byte sectors) backing the internal eMMC at the block level (SceSdif host 0:
    /// the raw sector reads and writes of sdstor and homebrew). Unset = none. The file is read and written.</summary>
    public const string EmmcImage = "VITASHARP_EMMC_IMAGE";
    /// <summary>1: log every raw sector access of the memory card and the eMMC (op, sectors, caller, result); without
    /// it only the first access of each caller and operation is logged.</summary>
    public const string TraceSectors = "VITASHARP_TRACE_SECTORS";
    /// <summary>A host folder where SceCoredump's process dumps are written (psp2core-*.psp2dmp, ELF cores), as a
    /// unit with the QA flag keeps them. Unset (the default) = none kept: the requests are refused as on a retail unit.</summary>
    public const string CoredumpDir = "VITASHARP_COREDUMP_DIR";

    /// <summary>Android: =off turns off both the pinning of the hot threads (guest main thread, GPU worker) to the
    /// fast cores and the Performance Hint (ADPF) session, to compare with them on (docs/android/README.md).</summary>
    public const string AndroidAffinity = "VITASHARP_ANDROID_AFFINITY";
    /// <summary>Android, A/B: =spare lets the guest threads that are not hot (job, audio, file threads) run on every core
    /// while the hot threads leave at least half a fast core idle, as the shader workers do; default: the slow cores
    /// (Mobile.Core Performance/ThreadPlacement.cs).</summary>
    public const string AndroidHelpers = "VITASHARP_ANDROID_HELPERS";
    /// <summary>Linux and Android: =0 leaves the background threads (speculative JIT precompile, spare shader workers,
    /// texture transcoders) at nice 0 instead of 5-10, to compare (Core Util/HostThreads.cs LowerCurrent).</summary>
    public const string BackgroundNice = "VITASHARP_BG_NICE";
    /// <summary>Android, A/B: =big moves the guest thread recording the GXM scenes (the last one to call
    /// sceGxmBeginScene) to the fast cores as soon as it changes, beyond the hot-thread slots; default: placed by its load
    /// (Mobile.Core Performance/ThreadPlacement.cs, Core Util/GpuSubmitterThread.cs).</summary>
    public const string AndroidGxmThread = "VITASHARP_ANDROID_GXM_THREAD";
    /// <summary>Android: =info, =debug or =all keeps forwarding info/debug lines to logcat while a game runs (by
    /// default only warnings and errors reach logcat once the first frame is shown; the log file keeps everything).</summary>
    public const string AndroidLogcat = "VITASHARP_ANDROID_LOGCAT";
    /// <summary>Android: =1 asks for Window.setSustainedPerformanceMode where the device supports it (lower, steadier
    /// clocks for long sessions; off by default because it lowers peak speed).</summary>
    public const string AndroidSustained = "VITASHARP_ANDROID_SUSTAINED";
    /// <summary>Android (A/B): the game surface's buffer height in pixels ("game" = 544, "half" = half the view's), the
    /// width following the view's aspect; the display hardware scales the buffers up. Less to blit and compose per frame.
    /// Default: the view's own size.</summary>
    public const string AndroidSurfaceSize = "VITASHARP_ANDROID_SURFACE_SIZE";
    /// <summary>Android: what each frame reports to the ADPF hint session: cpu (default: the busiest hot thread's CPU
    /// time), gpu (API 35+ WorkDuration with the frame's CPU time and the renderer's GPU time) or wall (the same with the
    /// frame interval as total).</summary>
    public const string AndroidAdpf = "VITASHARP_ANDROID_ADPF";
    /// <summary>Android: =1 uses the system's game APIs: GameManager.setGameState (loading / gameplay) and the game mode
    /// (battery mode asks the ADPF session for power efficiency). Default off (A/B).</summary>
    public const string AndroidGameState = "VITASHARP_ANDROID_GAME_STATE";
    /// <summary>Android: seconds between the periodic session statistics in the log file (default 30, 0 = off).</summary>
    public const string SessionReportSeconds = "VITASHARP_SESSION_REPORT_SECONDS";
    /// <summary>=1: the periodic summary adds "PERF states" lines, each guest thread's states since the previous one
    /// (waiting on what, running which call, waiting for a core, paced), sampled every 5 ms (Util/ThreadStateSampler).</summary>
    public const string ThreadStates = "VITASHARP_THREAD_STATES";
    /// <summary>Developer builds: the GPU worker spends FACTOR times its real time on each command (busy wait), to
    /// reproduce a phone's worker cost on a PC (default 1).</summary>
    public const string GpuWorkerSlowdown = "VITASHARP_GPU_WORKER_SLOWDOWN";
    /// <summary>1: a display queue entry's flip callback runs once the GPU worker recorded and submitted the frame, not
    /// once the GPU finished it (the callback only flips; presents stay in GPU order). auto: chosen from measured frame
    /// times (Gpu/AdaptiveEarlySignal.cs: tried while the game misses its cadence and waits on the display queue, kept
    /// only when frames got faster; in the native frame rate mode a flip is never sooner than the median interval the
    /// completion signal gave, so early cannot make a game faster than the console). =1 has no such hold: A/B only.
    /// Default 0.</summary>
    public const string DisplayEarlySignal = "VITASHARP_DISPLAY_EARLY_SIGNAL";
    /// <summary>0 (A/B) turns off the wait of a scene's GPU work until the display released its colour buffer (fragment
    /// sync object), as on the hardware; trace also logs the waits. Default on.</summary>
    public const string DisplaySync = "VITASHARP_DISPLAY_SYNC";
    /// <summary>queue (A/B): a GXM display queue flip goes to the tail of the GPU queue, behind the work the game queued
    /// since its frame's fence (the old order); next (A/B): the GPU worker runs it before its next command even inside a
    /// scene (5ce57aa6). Default: the flip keeps its queue position, and the worker runs it early only while it holds a
    /// scene for a sync object (that scene waits for the flip).</summary>
    public const string DisplayFlipOrder = "VITASHARP_DISPLAY_FLIP_ORDER";
    /// <summary>1 (A/B): sceGxmDisplayQueueAddEntry keeps at most displayQueueMaxPendingCount entries queued or flipping.</summary>
    public const string DisplayQueueStrict = "VITASHARP_DISPLAY_QUEUE_STRICT";
    /// <summary>0: sceDisplaySetFrameBuf in IMMEDIATE mode no longer flips at most once per vblank (by default a second
    /// flip in the same vblank waits for the next one: the panel shows no more than that).</summary>
    public const string DisplayImmediatePace = "VITASHARP_DISPLAY_IMMEDIATE_PACE";
    /// <summary>0: sceDisplaySetFrameBuf from a game thread no longer waits for the previous flip's present (by default a
    /// flip waits until the GPU queue has shown the one before it: the picture is never more than a frame behind the
    /// guest, as on the Vita, whose display reads the framebuffer at scanout).</summary>
    public const string DisplayPresentWait = "VITASHARP_DISPLAY_PRESENT_WAIT";
    /// <summary>Frame rate mode: native (default), 30, 60 or unlimited (Hle/Modules/Display.cs FrameRate; desktop
    /// --frame-rate=, the launcher's "Taxa de quadros" per game).</summary>
    public const string FrameRate = "VITASHARP_FRAME_RATE";
    /// <summary>1: first-use pipelines are linked from VK_EXT_graphics_pipeline_library shader libraries where the driver
    /// supports fast linking, and optimised in the background (Renderer.Vulkan VulkanRenderer.PipelineLibrary.cs);
    /// 0 (the default, VulkanRendererOptions.PipelineLibrary): every pipeline is compiled whole.</summary>
    public const string PipelineLibrary = "VITASHARP_PIPELINE_LIBRARY";
    /// <summary>Storage read speed in MB/s (Util/IoTiming.cs): "vita" (the memory card's 7 MB/s, the default), "fast"
    /// or 0 (no speed limit: the launchers' "Carregamento rapido", desktop --fast-loading), or a number (desktop
    /// --io-speed=).</summary>
    public const string IoSpeed = "VITASHARP_IO_SPEED";
    /// <summary>0: textures always read a decoder's output buffer as it is now, never an earlier picture of the
    /// decoder that matches their size (Gpu/DecoderOutputs.cs). Default on.</summary>
    public const string DecoderHistory = "VITASHARP_DECODER_HISTORY";
    /// <summary>=1: every guest delay (sceKernelDelayThread) spins its last half millisecond (the old behaviour), =0: none does; default: only delays of 5 ms or more (Core/Kernel/Sync.cs Wait.Sleep).</summary>
    public const string DelaySpin = "VITASHARP_DELAY_SPIN";
    /// <summary>0 (A/B): timed waits on a sync object (WaitSema/WaitEventFlag/... timeouts, DelayThreadCB) end on the host monitor's whole-millisecond timeout again instead of at their microsecond deadline (Util/HostTimeoutTimer.cs). Default on (Windows, Linux, Android).</summary>
    public const string PreciseTimeouts = "VITASHARP_PRECISE_TIMEOUTS";
    /// <summary>0 (A/B): an ATRAC9 superframe that fails to decode is skipped (no samples) instead of playing as a superframe of silence; its bytes count as consumed either way.</summary>
    public const string At9ErrorSilence = "VITASHARP_AT9_ERROR_SILENCE";
    /// <summary>Vulkan command-buffer slots in flight (2-8, default 3, 8 on Android; each holds a host upload ring, see UploadRingMb).</summary>
    public const string FramesInFlight = "VITASHARP_FRAMES_IN_FLIGHT";
    /// <summary>Size of each command-buffer slot's host upload ring in MB (8-64, default 64, 16 on Android; uploads past it go to
    /// overflow chunks).</summary>
    public const string UploadRingMb = "VITASHARP_UPLOAD_RING_MB";
    /// <summary>1: GXM notifications and display-queue sync points wait for visibility results only, not for render-target
    /// writebacks (published when their frame retires, at the latest by the next flip). Default 0.</summary>
    public const string LazyWritebackSync = "VITASHARP_LAZY_WRITEBACK_SYNC";
    /// <summary>1 (A/B): GXM notifications and display-queue sync points fire once the GPU worker recorded the work
    /// before them, not once the GPU finished it; writebacks and visibility results reach memory when their frame
    /// retires (the guest may read them a frame late). Default 0.</summary>
    public const string RelaxedGpuSync = "VITASHARP_GPU_RELAXED_SYNC";
    /// <summary>=1/0 (default: on for Android, off elsewhere): a scene whose notifications wait for GPU results
    /// (visibility tests, writebacks) is submitted to the GPU at its end, so they arrive when that scene finished
    /// instead of when the whole batch (often the rest of the frame) did. For games that poll notification memory from
    /// guest code instead of sceGxmNotificationWait
    /// (Killzone Mercenary's render job waits for the previous frame's first scene). Renderer.Vulkan EndScene.</summary>
    public const string GpuSubmitAtNotification = "VITASHARP_GPU_SUBMIT_AT_NOTIFICATION";
    /// <summary>half (A/B): the generated shaders declare the GXP's half-register values, F16 varyings and F16/U8/C10
    /// colour outputs mediump (RelaxedPrecision), as Vita3K does. Default off (everything highp).</summary>
    public const string ShaderFp16 = "VITASHARP_SHADER_FP16";
    /// <summary>1: a CPU transfer that reads a writeback still in flight runs when that frame retires (in order with the
    /// later sync points and transfers) instead of the GPU worker waiting for the frame. Copies and downscales of 32-bit
    /// linear pixels from a rendered surface are also made on the GPU in command order, and textures over their
    /// destination read that result meanwhile; other transfers' destinations are read as they are (a frame late).
    /// Default on Android, off on desktop (0/1 overrides).</summary>
    public const string TransferDefer = "VITASHARP_TRANSFER_DEFER";
    /// <summary>0 (A/B): a CPU transfer does not wait for a render-target writeback still in flight over its source (it may
    /// read the previous content, as before 240a8de). Default on.</summary>
    public const string TransferWritebackWait = "VITASHARP_TRANSFER_WRITEBACK_WAIT";
    /// <summary>1: the PERF sync line attributes every blocking GPU fence wait of the worker to its callers (stack).</summary>
    public const string SyncCallers = "VITASHARP_SYNC_CALLERS";
    /// <summary>Tile-based GPUs with VK_*_load_store_op_none: depth/stencil that no draw of a rendering tests or writes is
    /// neither loaded nor stored; only tested, it is not stored. Default on there (0 turns it off).</summary>
    public const string DepthOpsNone = "VITASHARP_DEPTH_OPS_NONE";
    /// <summary>1 (measurement only, wrong picture): render-target regions sampled as textures are copied once and never
    /// refreshed, to bound the GPU cost of those copies (bench).</summary>
    public const string RegionCopySkip = "VITASHARP_REGION_COPY_SKIP";
    /// <summary>0 = a render-target region is copied again after every change of its surface, even when only other parts changed (A/B; VulkanRenderer.Textures.cs).</summary>
    public const string RegionCopyReuse = "VITASHARP_REGION_COPY_REUSE";
    /// <summary>1 (measurement only, wrong picture): MSAA scenes start from undefined samples instead of drawing the
    /// surface's content into them, to bound that load's GPU cost (bench).</summary>
    public const string MsaaLoadSkip = "VITASHARP_MSAA_LOAD_SKIP";
    /// <summary>1/0: the shaders' constant-address uniform loads skip / keep the bounds check (they are always in range).
    /// Default: skipped on tile-based GPUs (Mali -6 ms per Killzone frame), kept on desktop (bit-exact there: the
    /// different code shape moved a few pixels by 1-2/255 in one dump).</summary>
    public const string UniformUnchecked = "VITASHARP_UNIFORM_UNCHECKED";
    /// <summary>1 (A/B, default off): memory loads of four words read them with one 128-bit load where the address is
    /// 16-byte aligned (ShaderGenerator.VectorLoads; same values). Part of the shader cache key.</summary>
    public const string ShaderVec4Loads = "VITASHARP_SHADER_VEC4_LOADS";
    /// <summary>1 (A/B, default off): a float-colour draw without blending on an F32/F32F32 surface writes its bits
    /// through the integer view a raw-bit rendering keeps open instead of ending it (VulkanRenderer.SingleFetch.cs).</summary>
    public const string FloatBitsJoin = "VITASHARP_FLOAT_BITS_JOIN";
    /// <summary>measure: a watcher thread notes when the GPU finished each submitted frame and the PERF sync line
    /// reports how long until the frame was published (writebacks, notifications); 1 (A/B): also retires such frames at
    /// the GPU worker's next draw instead of its next scene (VulkanRenderer.RetireWatch.cs). Default off.</summary>
    public const string RetireWake = "VITASHARP_RETIRE_WAKE";
    /// <summary>Emulator updates: the releases feed to check instead of the GitHub API of the build's repository (a URL
    /// with the API's JSON, or a folder with releases.json whose assets are file:// URLs; tests).</summary>
    public const string UpdateFeed = "VITASHARP_UPDATE_FEED";
    /// <summary>Interface language: "en" or "pt-BR" (default: the system's; Localization/Loc.cs). The launcher sets it
    /// for the games it starts from Configurações > Idioma.</summary>
    public const string Language = "VITASHARP_LANGUAGE";
    /// <summary>=0: the guest clock counts the host's garbage-collector pauses again (A/B); default: they are left out,
    /// as on the console no time passes while the whole emulated machine is stopped (Util/GuestClock.cs HostElapsed).</summary>
    public const string GcPauseClock = "VITASHARP_GC_PAUSE_CLOCK";
    /// <summary>=0: the guest clock goes on while a guest thread holding an emulated core falls behind its emulated CPU
    /// (A/B); default: that time is left out, within a budget (Util/GuestClock.Lag.cs).</summary>
    public const string LagClock = "VITASHARP_LAG_CLOCK";
    /// <summary>The CPU phase profile (Cpu/PhaseProfile.cs): unset = on while an F8 capture records or with --debug-mode
    /// (one dispatcher turn in 31 timed), 1 = on for the whole run with every turn timed, 0 = never on.</summary>
    public const string PhaseProf = "VITASHARP_PHASE_PROF";
    /// <summary>Host nanoseconds per guest cycle above which a guest thread raises a [phase-alert] (default 70, ~200 host
    /// cycles at 3 GHz).</summary>
    public const string PhaseBudgetNs = "VITASHARP_PHASE_BUDGET_NS";
    /// <summary>=1: the desktop runs with what the Android build turns on (Util/PhoneProfile.cs, --phone-profile,
    /// build/phone-profile): waits without spinning, Mali GPU profile, 333 MHz, two host cores, 4 MB GC gen0 budget.</summary>
    public const string PhoneProfile = "VITASHARP_PHONE_PROFILE";
    /// <summary>The phone profile's host cores: a mask, hex (0x30) or decimal; 0 = not pinned; default two cores of the
    /// upper half (CPUs n-4 and n-2).</summary>
    public const string PhoneAffinity = "VITASHARP_PHONE_AFFINITY";
}
