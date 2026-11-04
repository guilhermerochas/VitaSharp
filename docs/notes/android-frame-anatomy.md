# Android frame anatomy (Redmi Note 14, Helio G99 / Mali-G57 MC2)

Where one frame's time goes on the phone, so the next optimisation goes after the line that sets the frame rate.
Branch perf/vk-direct (a73acbc), LLVM developer APK, VSync on, default switches. 4 November 2026.

## How it was measured

- **Scenes.** Uncharted: Golden Abyss (PCSA00029) gameplay after the journal-tutorial tap. F1 2011 (PCSE00002) Quick
  Race, Albert Park, race start. Navigation uses `VITASHARP_ANDROID_INPUT_LIVE`, a file of ScriptedPad steps that the
  app polls while it runs, pushed from the PC after reading a screenshot. The phone refuses `adb shell input`.
- **Perfetto.** 30 s, `tools/dev/perfetto-vitasharp.pbtx`, with `VITASHARP_TRACE=1` and `VITASHARP_TRACE_HLE=64`.
  Thread states come from the sched events. Frames are the `guest frame` counter (one `sceDisplaySetFrameBuf` each).
- **simpleperf.** `simpleperf record --app io.github.vitasharp -e cpu-clock -f 2000 --call-graph fp`, 30 s, on the
  game's main thread and the GPU worker.
- **GPU.** GPU time and the worker's fence waits are deltas of the `PERF gpu` / `PERF sync` lines over the same
  frames (the log prints them every 30 s).
- **Display.** Frame times are the SurfaceFlinger present timestamps of the game's SurfaceView
  (`dumpsys SurfaceFlinger --latency`).
- **Overhead.** The trace markers cost about 5% of the game thread in F1 (`write` 3.5% plus `DevTrace.BeginHle`
  1.2%). The numbers below include that.

## Uncharted gameplay: GPU-bound

554 guest frames in 29.7 s: mean 53.6 ms, median 52.7 ms, max 86 ms. The display shows a median of 20 fps
(3 vblanks) and a 1% low of 7.5–8.6 fps.

| Line | ms per frame | Notes |
|---|---|---|
| **GPU** | **52.6** | `PERF gpu` delta. The GPU clock sits at 1014–1019 MHz, its maximum, for the whole window |
| Game thread: running | 31.3 | JIT code about 55% (17 ms); HLE, kernel and runtime about 45% (14 ms) |
| Game thread: waiting | 18.6 | almost all of it in `sceGxmDisplayQueueAddEntry` (about 19 ms, from the sampled HLE slices) |
| Game thread: runnable | 3.7 | game thread and GPU worker share the two A76 cores (CPUs 6 and 7) with the Mali back-end thread |
| GPU worker: running | 27.0 | translation and recording. Driver (`libGLES_mali`) is about 19% of it (5 ms); Silk.NET about 4% |
| GPU worker: frame-slot fence wait | 16.8 | `PERF sync` Slot delta: it waits for the GPU |
| Mali back-end thread (`mali-cmar-backe`) | 12.8 running + 5.3 runnable | the driver's own submission thread |

The critical path is the GPU, at 52.6 of 53.6 ms. The game thread waits about 19 ms per frame in the display queue
and the worker waits 17 ms on frame slots, both for the GPU. Faster CPU work alone cannot cross the 33 ms (30 fps)
line here. Earlier notes measured the title screen at 32 ms of GPU time, mostly fetch passes.

## F1 2011 race: CPU-bound on the game thread

544 guest frames in 29.8 s: mean 54.8 ms, median 55.1 ms, max 93 ms. The display shows a median of 20 fps.

| Line | ms per frame | Notes |
|---|---|---|
| **Game thread: running** | **52.3** | on CPU 7 (A76), almost never asleep (0.6 ms). Breakdown below |
| Game thread: JIT code | about 23.5 | 45% of its samples (`unknown`, the native JIT arena) |
| Game thread: HLE and runtime | about 27 | VitaSharp.Core 25%, libc 11% (memcpy and memset, trace markers), Mono 9%, CoreLib 6%. Dominated by the GXM precomputed-state calls: about 1,400 `sceGxmDrawPrecomputed` per frame, each with Set{Vertex,Fragment}State and the uniform-buffer calls; `Gxm.SubmitDraw` 2.7% |
| GPU worker: running | 41.6 | on CPU 6. `BuildTextureSet` and the draw path, driver about 17% (7 ms); Silk.NET about 5% |
| GPU worker: runnable / asleep | 5.8 / 7.5 | slot-fence waits are only about 1.7 ms per frame |
| GPU | 20.3 | the GPU clock moves between 594 and 850 MHz, so it is not saturated |

The critical path is the game thread: CPU-bound at 52 of 55 ms, with HLE plus runtime making up more than half of
it. The worker runs in parallel at 42 ms and becomes the limit as soon as the game thread drops below that.

## Hitches

- **F1, 90–93 ms frames** (4 in 30 s). The game thread was running for the whole frame, with no waits, no JIT
  compiles and no I/O, while the worker recorded a 93–97 ms frame. These are heavy frames on both threads, not stalls.
  The 150–200 ms maxima of earlier runs fell right after the race start (the first frames after loading).
- **Uncharted.** No guest frame passed 90 ms in the traced window (max 86 ms). The 100–116 ms display maxima are
  86 ms guest frames rounded up to 6–7 vblanks by FIFO presentation. A single 266 ms display gap coincided with
  the trace start.

## What this points at

1. **Uncharted: GPU time** (52.6 ms per frame at maximum clock). The fetch passes, the framebuffer-fetch copies
   (1.99 G pixels in 560 frames) and the scene structure. Display-queue and fence work cannot cross 33 ms while the
   GPU needs 52 ms. Test perf/serial's EARLY switch there anyway: some of that GPU time may be bubbles.
2. **F1: the game thread's HLE path** (about 27 ms): the GXM precomputed-state calls, their SVC dispatch, uniform
   copies, and the memcpy/memset inside them. Then the JIT code (23.5 ms).
3. **The GPU worker** (27–42 ms) comes next once the game thread is faster. Its texture-set build was cut in
   a73acbc; Silk.NET overhead is 4–5%.
