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

## Game/GPU pipeline switches (perf/serial eb4506d) on the phone

Display median / 1% low / mean frame ms / max frame ms; 30 s per run, interleaved.

| Variant | F1 race | Uncharted gameplay |
|---|---|---|
| base | 20 / 6.7-7.5 / 55.9 (3 runs) / 150-166 | 20 / 7.5-8.6 / 53.6 / 100-116 (anatomy runs) |
| EARLY () | 20 / 9.1-9.8 / 55.2 (2) / 116 | 15 / 6.6 / 61.8 (1) / 166 |
| EARLY + LAZY | 20 / 8.1-8.6 / 55.7 (2) / 133 | - (runs stuck in the pause menu) |
| EARLY + LAZY + FIF4 | 20 / 7.8-9.3 / 58.2 (2) / 133 | 20 / 5.6-6.1 / 73.2 (3) / 166-200 |

- No variant crosses a vblank step: F1 is game-thread-bound and Uncharted GPU-bound (above).
- EARLY lowers F1's maximum frame time (116 vs 150-166 ms) and raises its 1% low. It is a candidate default once
  the frame rate is above 20.
- In Uncharted, EARLY and FIF4 made gameplay slower: the game waits on notifications instead.
- The switches stay in the tree, off by default.

## What this points at

1. **Uncharted: GPU time** (52.6 ms per frame at maximum clock). The fetch passes, the framebuffer-fetch copies
   (1.99 G pixels in 560 frames) and the scene structure. Display-queue and fence work cannot cross 33 ms while the
   GPU needs 52 ms. Test perf/serial's EARLY switch there anyway: some of that GPU time may be bubbles.
2. **F1: the game thread's HLE path** (about 27 ms): the GXM precomputed-state calls, their SVC dispatch, uniform
   copies, and the memcpy/memset inside them. Then the JIT code (23.5 ms).
3. **The GPU worker** (27–42 ms) comes next once the game thread is faster. Its texture-set build was cut in
   a73acbc; Silk.NET overhead is 4–5%.

## Phone run, 4 November 2026 (evening): F1 HLE switches, game mode, Uncharted s4

Display median / 1% low / mean frame ms from SurfaceFlinger, 30 s per run from the race grid (F1) or after the journal
tap (Uncharted). Game-thread ms from /proc; HLE calls per frame from the `PERF hle calls` deltas (top 12).

**F1 2011 race, perf B's switches** (main + perf B's tree, interleaved, 3 rounds; one base run was lost to a
notification over the Quick Race screen):

| Variant | Median fps | 1% low | Mean ms | Game thread ms/frame | GPU ms | HLE calls/frame |
|---|---|---|---|---|---|---|
| base (GUEST_CODE=0, QUEUE_LIGHT=0), 2 runs | 15/15 | 4.7 | 70.2 | 47.3 | 19.5 | 18,100 |
| GUEST_CODE only | 20/20/15 | 7.2 | 55.1 | 37.6 | 16.7 | 3,400 |
| QUEUE_LIGHT only | 20/15/15 | 5.8 | 62.5 | 44.6 | 19.2 | 16,500 |
| both (the default) | 20/20/20 | 6.7 | 55.8 | 37.6 | 16.8 | 3,350 |

The libgxm setters as guest code are the gain (about 10 ms of game thread per frame, mean frame 70 -> 55 ms);
QUEUE_LIGHT adds little on its own and nothing on top. On main 1251dab one interleaved pair confirmed it before the
phone was released (base 70.1 ms, 47.6 ms game thread, 17,700 HLE calls; both 57.7 ms, 38.5 ms game thread).

The F1 navigation works now: the old F1 APK predated `VITASHARP_ANDROID_INPUT_LIVE` (its presses were never read).
The script waits until the live file is consumed before each press and picks the press from the red highlight bar
(profile list, main menu, Quick Race "Confirm"); the intros take cross+start.

**Game mode.** `cmd game mode performance io.github.vitasharp` is accepted now (game_mode_config.xml). On Uncharted
gameplay (3 rounds interleaved) it changes nothing measurable: standard 20 fps median, 45.5 ms mean, 1% low 10.0, GPU
39.8 ms/frame; performance 20 fps, 46.4 ms, 9.3, 39.8 ms. The A76 clocks read 1.3-1.5 GHz in both modes. MIUI's
game service does not raise the clocks for it.

**Uncharted s4: the G-buffer reinterpretation is not its cost.** A measurement switch that refreshed the two
1440x408 reinterpreted views only once (instead of the render target -> CDRAM mirror -> image copies every frame)
left s4 at 17.8 ms vs 17.9 ms (uc_game bench, 4 rounds x 3 reps). A direct image -> image path (compute shader: an
R32G32_UINT view of the surface into an R32_UINT storage view of each alias; pixel-identical on the ucg dump, 37/37
frames) moved about 2 ms from s4 into s3 and left the frame at 42.2 vs 42.7 ms. Both were reverted. s4's
non-fragment time is elsewhere (its vertex work, or the read-only depth fetch pass itself).

**Save states on the phone** (main 1c0e516, scripted `savestate@T:1` / `loadstate@T:1` steps): Knightin'+ save
select, Uncharted gameplay and the F1 race all came back to the saved point with working input and holding fps over
60 s (Knightin'+ 30 fps locked; Uncharted 30 median; F1 30 median, 27 mean). Game stopped for 542/543/566 ms per save
and 1011/933/968 ms per load (Uncharted/Knightin'+/F1).

## Internal resolution scale on the phone, 5 November 2026 (perf/res-scale)

`VITASHARP_RES_SCALE` (VulkanRenderer.ResScale.cs), LLVM developer APK of perf/res-scale ec78348, VSync on. Uncharted
gameplay after the journal tap, 30 s per run, 3 rounds interleaved (1.0, 0.75, 0.5). Every run was confirmed in
gameplay by a screenshot first. Display numbers are from SurfaceFlinger; GPU is the `PERF gpu` delta.

| Scale | Median fps | 1% low (3 runs) | Mean frame ms (3 runs) | GPU ms/frame (3 runs) |
|---|---|---|---|---|
| 1.0 | 30 | 10.2 / 10.0 / 10.0 | 41.5 / 42.6 / 44.8 | 39.9 / 40.1 / 40.7 |
| 0.75 | 30 | 14.6 / 14.6 / 11.5 | 34.6 / 34.9 / 38.2 | 30.7 / 31.8 / 31.8 |
| 0.5 | 30 | 16.7 / 13.6 / 12.8 | 33.4 / 35.0 / 34.8 | 23.4 / 23.3 / 23.3 |

GPU time falls with the pixel count (-21% at 0.75, -42% at 0.5), and the mean frame drops from about 42.6 ms to
34.9 ms (+22% mean fps), close to the game's own 30 fps (33.3 ms). At 0.5 the GPU is no longer the critical path:
the GPU worker (about 26 ms of CPU per frame) and the game thread (about 21 ms) are, so 0.5 gains almost nothing
over 0.75 in frame time, though the 1% low is a little better in two of three rounds. To go past 30 fps the CPU side
has to shrink.
