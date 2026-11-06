# Android performance: what other emulators do about our bottlenecks

Reference survey for the Android port (Redmi Note 14: Helio G99, 2x Cortex-A76 + 6x Cortex-A55, Mali-G57 MC2), written
6 November 2026 (perf/android-8) against the Killzone: Mercenary gameplay measurements in
[android-frame-anatomy.md](android-frame-anatomy.md). Each technique: what it is, where it comes from, why it matters
here, what it would cost in C#, and **what it did in VitaSharp** (measured in Killzone, or why it was not tried).

## What the phone shows (the problem to solve)

Killzone gameplay, native resolution: display median 12-15 fps, mean 68-83 ms per frame. A 2.7 s Perfetto trace of
that scene (`build/android-trace.sh`, scheduler + the emulator's markers) says where the time goes:

- **The GPU is busy only 48% of the time** (union of the "waiting for GPU completion" intervals: 89 submissions,
  ~45 ms each for the frame's big ones). The GPU worker's "record frame" span is 65 ms on average (35 frames in
  2.7 s), and there are 100 ms stretches where the GPU gets only 1 ms submissions while the worker is still receiving
  the frame's commands from the game.
- **The game thread** (`killzone_vita`) runs 51% of the time, is runnable but not running 16.5% (it waits for a fast
  core), and sleeps 32% (its GPU waits: display queue 7-9 ms and notifications 6-7 ms per guest frame, from the PERF
  lines). It and the GPU worker run only on the two A76 cores, which they share with the Mali driver's back-end
  thread, SurfaceFlinger and the trace daemon.
- **The A76 cores averaged 1.36 GHz (max seen 1.8 GHz), the A55 1.38 GHz, the GPU 946 MHz** at 36-38 C: the phone is
  clock-limited, so CPU work costs more than its instruction count suggests.
- The three JobSched threads (~37 ms of CPU per frame together) run on the A55 cores and are not on the critical path.

So the frame is a **serial chain**: the game produces the frame's commands (with GPU waits in the middle), the worker
translates them, the GPU runs them, and the game waits for that GPU work before it goes on. Cutting GPU time alone
(MSAA off, 0.75 scale: GPU 49 -> 34.5 ms) moved the frame from ~77 to ~62 ms, not to 34.5.

## 1. Game/GPU pipelining (the largest expected gain)

| Technique | Where | Why here | Cost in C# | In VitaSharp |
|---|---|---|---|---|
| Run the guest ahead of the host GPU: the guest sees a "GPU done" signal as soon as its commands are queued, real completion is enforced only when the guest reads GPU results | Dolphin ("Sync GPU thread" off, dual core mode), PCSX2/AetherSX2 (MTGS: the EE thread queues GS packets and does not wait), PPSSPP (display lists run without the CPU waiting for them unless it reads GPU results) | the trace: GPU idle 52% while the game waits for it; on a Vita a scene takes a few ms, on this GPU 45 ms | small: existing switches `VITASHARP_DISPLAY_EARLY_SIGNAL` (flip callback after submit) and `VITASHARP_LAZY_WRITEBACK_SYNC` (notifications wait for visibility results only) from perf/serial | Killzone, interleaved with base (78.2 ms mean, 12 fps median, two runs): EARLY 64.0 / 74.8 ms, median 15 / 15 fps; EARLY + LAZY 75.7 ms, 15 fps; LAZY alone 77.9 ms, 12 fps. EARLY is the first switch that moved the median (12 -> 15) in this scene; two runs only, Uncharted was slower with it in perf/serial: candidate, off by default |
| Frames in flight / triple buffering on the host side | Ryujinx and the yuzu forks (several frames queued before the swapchain), Dolphin | lets the worker record frame N+1 while the GPU runs N | already there: `VITASHARP_FRAMES_IN_FLIGHT` (default 3) | default kept; the limit is the guest waits, not the slots |
| Present on its own thread | Ryujinx (presentation thread), Skyline (`PresentationEngine` thread), Dolphin (`Presenter`) | `vkQueuePresent` blocks the worker 3-4 ms per frame | done before: `VITASHARP_PRESENT_THREAD` | Killzone: worse in 3 of 4 runs (85-86 vs 70-83 ms): stays off |

## 2. Tile-based GPU (Mali) rendering

| Technique | Where | Why here | Cost | In VitaSharp |
|---|---|---|---|---|
| Restrict the render area to what is drawn; load/store only those tiles | Arm Mali best-practices guide ("render area"), Skyline's render pass tracking, Dolphin's bounding of EFB copies | Killzone draws ~25 post-processing passes into regions of one surface; each pass loaded and stored the whole surface | done: `VITASHARP_RENDER_AREA_TRIM` (db2d72d), 80 lines | bench of the Killzone frame: post-processing scene 9.4-14.3 -> 7.2-9.2 ms in three sessions; live frame within the noise (75-83 ms vs 70-83). Off by default |
| Merge consecutive render passes on the same attachments; DONT_CARE / CLEAR instead of LOAD when the content is not read | Dolphin (render pass merging on mobile), Vita3K (GXM scene = one pass), Turnip/Freedreno docs | ~32 renderings per frame with colour + depth LOAD/STORE | mostly done before (merge of renderings, hoisting of uploads/copies, depth DONT_CARE at scene end) | remaining breaks are real dependencies (a pass samples what the previous one drew; stencil read as a texture 7 times per frame) |
| MSAA resolved in the tile, samples never stored | Mali/Adreno guides, Dolphin and DuckStation on Android | Killzone's two MSAA 4x scenes | done before (samples discarded at scene end, resolve in the pass) | the main MSAA 4x scene still costs 15-22 ms of GPU; `VITASHARP_MSAA=off` live: 61.5 vs 77 ms, GPU 34.5 ms, edges alias: a per-game quality option candidate, not a default |
| Lower internal resolution | every emulator (PPSSPP, Dolphin, AetherSX2, Vita3K "resolution multiplier") | GPU time scales with pixels | done before: `VITASHARP_RES_SCALE` | Killzone 0.75: 61.7-64.5 ms (GPU -30%); same per-game-option conclusion |
| Optimised SPIR-V | Ryujinx (spirv-opt off for compile time), yuzu (driver optimises) | shaders are compiled with shaderc level Zero | none (switch exists) | `VITASHARP_SHADERC_OPT=perf` in the Killzone bench: 35.6 vs 33.9 ms: no gain, Zero stays |
| Custom driver (Turnip) | Winlator, yuzu forks, Skyline (libadrenotools) | Adreno only | done before (adrenotools for Adreno) | not applicable: this phone is a Mali |

## 3. CPU: the ARM64 JIT and the HLE path

| Technique | Where | Why here | Cost | In VitaSharp |
|---|---|---|---|---|
| Static register allocation of guest registers across blocks, block linking, lazy flags | Box64/FEX (ARM64 dynarecs), ARMeilleure (Ryujinx), Dynarmic | the game thread runs JIT code ~55% of its time | done before (perf/a64-regalloc: pinned guest registers, linked exits, flags only when read) | no new measurement this round |
| Fastmem (guest address = host base + offset, faults handled) | Dolphin, Dynarmic, Box64 | memory accesses without a check | done before (x20 = guest base, UXTW) | — |
| Persistent translation cache (PPTC) | Ryujinx PPTC, FEX AOT cache | removes compile stutter after the first run, not steady-state time | medium-large (serialisable IR / relocatable code) | not tried: Killzone's steady state does not compile (JIT worker ~4 ms/frame early, ~0 later) |
| Inline hot HLE calls in JIT code (TLS, thread id, lightweight mutexes) | Vita3K and Ryujinx (thread-local and thread-id reads served without a full HLE dispatch) | Killzone: `sceKernelGetTLSAddr` ~620, LwMutex lock/unlock ~610 each, `sceKernelDelayThread` ~770 calls per frame | small per call; tried before as `VITASHARP_NATIVE_HLE_INLINE` (Uncharted: game thread -2 ms, frame unchanged) | not retried: those calls are mostly on the JobSched threads, which are off the critical path |
| Big cores for the critical threads, little cores for the rest; ADPF hints | Skyline, yuzu forks, Winlator (thread affinity), Android ADPF guide | game thread and GPU worker need the two A76 | done before (placement + PerformanceHintManager session that follows the threads, also after a state load) | the trace shows the game thread runnable 16.5% of the time on the A76: the Mali back-end, SurfaceFlinger and traced share them |

## Next steps, in order of expected gain

1. Pipelining (section 1, first row): `DISPLAY_EARLY_SIGNAL` helps Killzone in two runs (median 12 -> 15 fps); repeat
   it in 3 cool pairs and in Uncharted/F1. Beyond it, a "signal at submit, fence at read" scheme (Dolphin/PCSX2 style:
   GXM notifications released when the worker submits, the real fence waited only where the guest reads GPU output:
   writebacks, visibility results, mapped surfaces) would let the GPU run while the game builds the next frame.
2. Per-game quality options on Android (resolution scale, MSAA), native by default.
3. RENDER_AREA_TRIM on by default once a longer live series confirms the bench.
