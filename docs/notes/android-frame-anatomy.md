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

## Uncharted at 0.75: the GPU worker's CPU time, 5 November 2026 (perf/uc-cpu)

LLVM developer APK of perf/uc-cpu, `VITASHARP_RES_SCALE=0.75`, `VITASHARP_WORKER_PROF=1`, Uncharted gameplay after the
journal tap, 30 s per run, a screenshot confirming gameplay before each. Worker phases are deltas of the cumulative
`PERF worker` lines over the measured window (~880 presents). The phone warmed through the session: the same base
configuration measured 33.9 ms mean frame at the start and 37.7–38.5 ms later, so single runs differ by ±2 ms.
Profiling with `VITASHARP_TRACE=1` inflates the worker a lot (the first profile showed 4–6 MB/frame of uploads and
4 ms of Geometry; without it, 2 ms): measure the worker without the trace markers.

**Where the worker's time goes** (base, cool phone; ms per presented frame):

| | ms |
|---|---|
| Execute (GPU worker inside the renderer, fence waits excluded) | 25.3 |
| Draws (~540 per frame) | 19.3 |
| — texture sets (`BuildTextureSet`, both stages) | 6.1 |
| — recording (bind/dynamic state/draw calls) | 5.8 |
| — geometry (index min/max, vertex and index uploads) | 2.2 |
| — shader lookup 1.4, pipeline 1.1, uniforms 0.9, rendering begin 1.1, the rest of the texture phase 0.3 | |
| Present (submit, readback setup) | 3.0 |
| BeginScene (frame start: retire, pool resets, writeback conversion; render target lookups) | 2.1 |
| EndScene | 0.9 |

The worker thread's CPU is 23 ms/frame, the game thread's 20 ms, the GPU 30 ms: at 0.75 Uncharted is at the edge of
both. Display: 30 fps median, mean 33.9–38.5 ms depending on heat.

**Switches tried** (all off by default; the recorder thread was removed later, see below):

| Switch | What it does | Result |
|---|---|---|
| `VITASHARP_VARYING_TRIM=1` (perf/uc-gpu-4) | vertex shaders write only the varyings the fragment program reads | GPU 30.8/30.6/30.9 ms vs base 29.9/30.4/30.9: no gain |
| `VITASHARP_VERTEX_CACHE_SMALL=1` | vertex/index ranges below 16 KB cached in sub-allocated chunks | ~1.5 MB/frame fewer copies, Geometry unchanged (~2 ms): no gain |
| `VITASHARP_REC_THREAD=1` | tiled renderings' vkCmd* calls replayed by a second thread | Record 8.6 → 5.6 ms on the worker, but the recorder thread itself costs ~12 ms of CPU and the worker drains it ~15 times a frame; frame mean 38.5 → 37.0 ms in one hot pair: within noise |
| `VITASHARP_TEXSET_REUSE=1` | a stage with the same texture descriptors as an earlier one in the frame reuses its set | ~88% of stages reused, texture sets 6.1 → 5.3 ms: the per-unit resolution is not most of that phase |

All of them are pixel-identical on the desktop dumps (UC, FIFA 13, Croixleur, God Wars; mali profile).

**Next steps, ranked:**
1. Profile inside `BuildTextureSet` and `Present`: with 88% of the sets reused, 5.3 ms remain in the texture-set
   phase, so the cost is elsewhere in it (descriptor allocation/update for the misses, scene-break hoisting of region
   copies triggered from texture resolution, NoteSampled bookkeeping). Present costs 3 ms of the worker.
2. BeginScene's frame start (writeback conversion ~1.2–1.4 ms/frame on the CPU, retire, pool resets) can move to
   another thread: the writeback conversion runs on retired data only.
3. The recorder thread only pays if its own cost drops: call the driver through `VITASHARP_VK_DIRECT` function
   pointers on it, and merge scene chains so it is drained less often; otherwise remove it.
4. The game thread (~20 ms): 57% JIT code, ~22% HLE/core (sceKernelGetTLSAddr ~2,000 and sceKernelGetThreadId
   ~1,700 calls per frame are candidates for inlining in the JIT).

## Uncharted at 0.75: sub-phase profile and the second round of switches, 5 November 2026 (perf/uc-cpu)

Same setup (LLVM developer APK, `VITASHARP_RES_SCALE=0.75`, `VITASHARP_WORKER_PROF=1`, gameplay after the journal tap
confirmed by a screenshot, 30 s per run). One APK carried every variant behind `config/env.txt`; 3 rounds, each base
run next to the variants. The phone warmed through the session (base 33.8 -> 38.5 -> 41.0 ms), so compare within a
round.

**Inside the worker** (base, ms per presented frame, sub-phases of `VITASHARP_WORKER_PROF`):

| Phase | ms | Notes |
|---|---|---|
| texture sets: resolving the units (`ResolveTextureOrSkip` per unit) | 4.7-5.8 | most of the ~6 ms phase; allocation + descriptor writes 0.9-1.0, the lookup itself ~0.1 |
| Present: `vkQueuePresent` | 2.6-3.6 | of 3.3-4.4 ms in Present; the driver blocks in the present call. Acquire, blit and retire are small |
| BeginScene | 1.8-1.9 | retire poll 0.75-0.8 of it |

**Results** (mean frame ms; the median is 30 fps in every run; worker = GPU worker thread CPU ms/frame):

| Variant | Round 1 | Round 2 | Round 3 | Notes |
|---|---|---|---|---|
| base | 33.8 (1% low 14.6) | 38.5 (12.0) | 41.0 (7.8) | worker Execute 24.3 / 32.4 / 32.0 |
| `VITASHARP_TEXSET_PERSIST=1` | 35.4 (6.1: one 1 s stall during JIT and a level load) | 37.4 (14.0) | 36.3 (14.0) | Execute 23.8 / 28.9 / 28.6; unit resolution 3.0-3.4 ms instead of 4.7-5.8 |
| `VITASHARP_WRITEBACK_THREAD=1` | 34.7 | 38.8 | 38.4 | BeginScene 1.8 -> 1.1 ms, nothing in the frame |
| `VITASHARP_NATIVE_HLE_INLINE=1` | 36.0 | 39.1 | 38.1 | game thread ~2 ms lighter when hot (21.4 vs 23.4, 21.3 vs 25.8), nothing in the frame |
| `VITASHARP_REC_THREAD=1` | 39.5 | - | - | the recorder thread costs 12 ms of CPU, and the worker 26.9 vs 21.6 ms |

- **TEXSET_PERSIST** (reused texture sets in a pool of their own that outlives the frame's pool resets; a set from an
  earlier submit is used after a once-per-frame check of its memory textures) is the only one with a real gain: worker
  Execute -3.5 ms, mean frame -1.1 and -4.7 ms in the hot rounds, 1% low 14 vs 12 / 7.8. Pixel-identical on the six
  desktop dumps (UC x2, God Wars, FIFA 13, Croixleur, PCSF00476; mali profile). **It stays off by default**: the
  rerun with a cool phone (3 short alternating pairs) could not be done before the phone tests were paused.
- WRITEBACK_THREAD, NATIVE_HLE_INLINE and REC_THREAD were removed (no gain; REC_THREAD worse).
- At 0.75 the game sits at its own 30 fps cap (33.3 ms) when cool, and the GPU is at 30-31 ms. CPU savings show up
  mostly in the 1% low and in the hot runs, not in the median.

**Next steps:**
1. Rerun base vs TEXSET_PERSIST with the phone cool (3 short pairs). If the gain holds, propose it as the default.
2. `vkQueuePresent` blocks the worker for 2.6-3.6 ms a frame. Present from a thread of its own, behind a switch, so
   the worker records the next frame meanwhile. The queue then needs a lock shared with `vkQueueSubmit`.
3. Unit resolution is still 3 ms with PERSIST: the memory-texture checks of sets that are not reused, and the region
   and render-target lookups per unit.

## Uncharted at 0.75 with the phone cool: TEXSET_PERSIST and PRESENT_THREAD, 6 November 2026 (perf/android-7)

LLVM developer APK of perf/android-7 (main da8372d + `VITASHARP_PRESENT_THREAD`), every variant behind `config/env.txt`,
`VITASHARP_RES_SCALE=0.75` and `VITASHARP_WORKER_PROF=1`; the Prologue's first gameplay scene after the journal tap
(screenshot of every run). Before each run the app was closed and the battery had to be at 35.0 C or less (it rose to
36-37 C during a run), then 20 s more in gameplay before the 30 s measurement. The series was stopped by the user after
five valid runs (one more run, base, failed its navigation: title menu at 60 fps, discarded).

| Run | Variant | Frame mean ms (median fps) | 1% low | GPU worker CPU ms | Execute | vkQueuePresent / Present | GPU ms |
|---|---|---|---|---|---|---|---|
| sB1 | base | 40.6 (30) | 4.1 | 26.4 | 33.7 | 5.6 / 6.7 | 30.5 |
| sP1 | `VITASHARP_TEXSET_PERSIST=1` | 39.7 (30) | 4.4 | 25.6 | 31.1 | 4.7 / 5.8 | 30.7 |
| sT1 | `VITASHARP_PRESENT_THREAD=1` | 40.1 (30) | 4.4 | 26.8 | 28.4 | - / 0.73 | 30.8 |
| sP2 | `VITASHARP_TEXSET_PERSIST=1` | 39.5 (30) | 4.6 | 25.0 | 30.7 | 4.8 / 5.8 | 30.7 |
| sT2 | `VITASHARP_PRESENT_THREAD=1` | 39.5 (30) | 4.7 | 27.0 | 28.7 | - / 0.75 | 30.4 |

An earlier series the same evening with the main APK, without the cool-down or the settle time (battery 34.0 -> 37.5 C),
gave base 38.7 / 41.4 / 42.9 ms and PERSIST 40.4 / 45.6 ms (one PERSIST run failed its navigation): the frame time
followed the temperature, not the variant. The three base/PERSIST pairs (40.6/39.7 cool, 38.7/40.4 and 42.9/45.6 warm):
base median 40.6 mean 40.7 ms, PERSIST median 40.4 mean 41.9 ms.

- **The scene is GPU-bound at 0.75**: ~30.5 ms of GPU per frame in every run, the median at the game's 30 fps and the
  mean (~40 ms) set by stalls of 100-450 ms (1% low ~4-5 fps: shader and streaming hitches right after the level
  starts). Worker savings do not reach the frame time here.
- **TEXSET_PERSIST**: the unit resolution drops 5.2-5.4 -> 2.8-2.9 ms and Execute about 2.6-3 ms (as on 5 November),
  pixel-identical on 38 desktop replays (the 18 dumps of the user's gpudumps folder and the Uncharted plants dump,
  native and mali profiles: `and7_cmp/summary.txt` in the agent's scratchpad). No frame-time gain in this scene; it
  stays off by default until a CPU-bound scene shows one.
- **PRESENT_THREAD** (`VulkanRenderer.PresentThread.cs`): the worker's Present phase drops from 5.6-6.7 ms to 0.73 ms and
  Execute from 33.7 to 28.4-28.7 ms; the driver's blocking moved to the present thread. Same frame time (GPU-bound), no
  error or visual problem in two runs. Off by default.
- Next: measure both where the GPU worker limits (native resolution scenes are GPU-bound too on the Mali-G57; a
  CPU-heavy game such as F1 2011 or a cutscene-free area at 0.5), and look at the stalls behind the 1% low.

## Killzone: Mercenary gameplay, the Android reference scene, 6 November 2026 (perf/android-8)

LLVM developer APKs of perf/android-8 (main 4a8bb94 and later commits), VSync on, native resolution unless noted,
`VITASHARP_WORKER_PROF=1`. Scene: the first gameplay of the campaign (the staircase, player standing), reached by
loading a save state taken there (slot 1) at the Valor card, then 60 s in gameplay before a 30 s measurement
(screenshot of every run). Before each run the app was closed and the battery had to be at 36.0 C or less (at most
4 min of waiting; it read 35.6-38.8 C). Display numbers are from SurfaceFlinger, GPU is the `PERF gpu` delta, thread
CPU from /proc. Runs on which the phone dropped off the USB, the state did not load or the game froze are left out.

**Where the frame goes** (base, 8 valid runs): display median 12-15 fps, mean 67.8-83.0 ms, 1% low 3.7-5.1 fps.

| Line | ms per frame | Notes |
|---|---|---|
| **GPU** | **41.7-50.9** | two levels: runs sit near 42 or near 50 ms (heat/clock, the scene does not change) |
| GPU worker thread CPU | 28-34 | Execute 33-38: texture-unit resolution 8.2-9.0, recording 7-8, geometry 3.6-4.9, Present 3.3-4.1 (`vkQueuePresent` ~3) |
| Game thread (`killzone_vita`) | 20-26 | waits on the GPU 13-16 ms per guest frame (display queue 7-9 in 2 waits, notifications 6-7) |
| JobSched_000-002 | 37-44 (three threads) | `sceKernelDelayThread` ~770 calls per frame, LwMutex lock/unlock ~610 each, `sceKernelGetTLSAddr` ~620 |
| Worker queue | idle 13-15, frame-slot fence waits 12-16 | the worker waits for both the game and the GPU |

GPU work per frame: 6 scenes, ~32 renderings (5.4 per scene), each loading and storing colour and depth; mid-scene
breaks: RenderTargetRegion ~14, MirrorDepthTarget ~7 (stencil sampled as a texture), BeginSingleFetchPass 2 per
frame; 2 MSAA 4x scenes. The GPU bench of a dump of this scene (`kz_game` fixture, frame present 529, `stamps=1`)
splits the frame: s1, the main MSAA 4x scene, 15.5-22 ms; s4, ~25 post-processing passes of ~0.35 ms each into
regions of one surface, 9.4-14.3 ms; s5 3.6-6.4 ms; the other three 5-6 ms together. The bench frame itself varied
from 33.9 to 49.5 ms between sessions (phone temperature).

**Switches** (mean frame ms, median fps, GPU ms; in run order):

| Variant | Runs | Notes |
|---|---|---|
| base | 79.2/12/49.8, 78.3/12/50.3, 67.8/15/41.7, 78.0/12/50.0, 69.7/15/42.2, 83.0/12/50.9, 76.9/12/49.2 | |
| `VITASHARP_TEXSET_PERSIST=1` | 79.6/12/50.0, 68.3/15/40.7, 67.9/15/40.9, 75.8/15/49.1 | unit resolution 7.0-7.7 vs 8.2-9.0 ms; same frame time |
| `VITASHARP_PRESENT_THREAD=1` | 74.2/15/41.9, 85.3/12/50.0, 86.1/12/50.3, 85.8/12/49.5 | Present 0.5-0.7 ms on the worker, but the frame is 5-10 ms slower in three of four runs: stays off |
| `VITASHARP_RENDER_AREA_TRIM=1` (new) | 75.7/15/48.3, 75.0/15/48.1, 83.4/12/50.2 | live: within the noise. Bench: s4 9.4-14.3 -> 7.2-9.2 ms in all three sessions (frame -0.8 to -11.7 ms, noisy) |
| `VITASHARP_RES_SCALE=0.75` | 63.1/15/34.9, 64.5/15/-, 61.7/15/34.5 | GPU -30% |
| `VITASHARP_MSAA=off` | 61.5/15/34.5 | GPU -30%; edges alias (`and8_tools/shots/msaa_cmp.png`: base left, off right) |

- At 0.75 or without MSAA the GPU drops to ~34.5 ms but the frame stays at ~62 ms (median 15 fps): the CPU side is
  next (game thread + its GPU waits, the three JobSched threads, the worker). Neither quality switch reaches 30 fps
  alone; they are candidates for a per-game quality option on Android (native by default), not a default.
- `VITASHARP_SHADERC_OPT=perf` (optimised SPIR-V) changes nothing on the Mali in the bench (35.6 vs 33.9 ms, same
  picture); `VITASHARP_MSAA=off` in the bench: s1 15.5 -> 12.1 ms but s4/s5 slower, frame +3 ms (the bench replays a
  frame whose MSAA scenes are set up differently from the live game; the live runs are the reference here).
- RENDER_AREA_TRIM (VulkanRenderer.Tiler.cs `ChainRenderArea`) is pixel-identical on 21 desktop replays (the user's
  dumps, the Uncharted plants dump at 0.75, the Killzone dump; native and mali profiles). It stays off until a
  longer live series shows the bench gain in the frame time.
