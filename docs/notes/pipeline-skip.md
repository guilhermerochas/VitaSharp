# Skipping draws while their pipeline compiles (perf/pipeline-skip): analysed, NOT implemented

Roadmap item: "Opção de pular o desenho enquanto o pipeline não fica pronto (tira os últimos engasgos da primeira
vez)". Decision: **not implemented**. Draws that are safe to skip account for almost none of the remaining
first-run stalls in the 3D games, and the draws that do stall are the ones a game depends on. This note records the
evidence and the alternatives worth doing instead.

## What happens today (main 45edc3d, GeneratorVersion 16)
- `IGpuBackend.Prefetch` runs on the submitting guest thread. It queues shader translate+compile jobs and one
  pipeline job per new draw signature to the shader workers (`VulkanRenderer.AsyncShaders.cs`).
- The GPU thread never skips. In `GetModule`, a shader that is in flight is waited for, or run on the GPU thread if
  no worker started it yet. In `ResolvePipeline`, a pipeline is taken from the shared table, waited for if a worker
  is building it (`WaitPrefetchedPipeline`), or built on the GPU thread.
- Warm runs replay `pipelines.list` and `programs.pack` (prewarm and the boot rebuild), so the stalls below are
  first-run only.

## Instrumentation added on this branch (kept)
- The GPU thread's waits for a pipeline a worker is building are now counted as frame stall. Before, they only went
  to the ledger and were missing from "frames stalled" and "worst".
- The session line now reports frames stalled >= 16.7 ms (a whole 60 Hz frame) and the time spent waiting for
  pipelines.
- New `PERF stalls` line (`VulkanRenderer.StallStats.cs`, logged at title exit). Each draw that stalled on
  shader/pipeline work is classified by what it writes: visibility test, stencil write, tile-word (TileWord1)
  write, colour into a surface that is presented, off-screen target, write-back-sized target (CPU readback), MSAA,
  programmable blending. The line then gives "stalled frames before -> after" if exactly the skip candidates had
  been skipped.

  A **skip candidate** has no visibility test, no stencil write and no tile-word write, and draws colour into a
  surface that has been presented. This is the strictest class anyone proposed for skipping. Everything else has
  effects beyond the presented image.

## Measurements
Cold caches: fresh `--shader-cache` and an isolated NVIDIA driver cache (`__GL_SHADER_DISK_CACHE_PATH`).
GTX 1650. The process was pinned to 4 logical CPUs at BelowNormal priority (CPU rules for parallel agents), which
makes worker compile times longer than on a free 8-thread machine. The proportions are what matter.

| scenario | stalled draws (ms) | frames >= 4 ms | frames >= 16.7 ms | worst frame | skip candidates (ms) | >= 4 ms after skip | >= 16.7 ms after skip |
|---|---|---|---|---|---|---|---|
| skullgirls_boot (1800 f) | 8 (389) | 6 | 4 | 157 ms | 8 (389) | 0 | 0 |
| tearaway_boot (2400 f) | 60 (3610) | 18 | 14 | 1262 ms | 5 (192) | 15 | 13 |
| lbp_boot (4000 f) | 41 (2174) | 10 | 8 | 775 ms | 7 (298) | 5 | 5 |
| kz_skin (4500 f) | 249 (12181) | 108 | 91 | 2513 ms | 3 (23) | 107 | 91 |

Draws that must run, by class. The classes overlap, so one draw can count in several.
- **Tearaway:** tile-word write 49 draws (3186 ms), stencil write 25 (2671 ms), visibility 7 (1029 ms), depth/stencil
  only 6 (232 ms). The hand screen and arena rely on all of them. The visibility results are the merged fix for
  objects that alternated frames. The stencil masks are drawn by OUTPUT_UNDEFINED programs.
- **Killzone:** stencil write 214 draws (11423 ms). Its scene draws mark stencil. Off-screen targets account for 230
  draws (11687 ms): the HDR scene 0x65C78000, light/shadow targets, the post atlas. 25 of them (870 ms) go into
  write-back-sized targets, which feed the CPU auto-exposure luminance. Visibility 6, depth only 12.
- **LBP:** 34 of 41 stalled draws (1876 ms) go to off-screen targets: shadow map, DoF, 9 into write-back-sized
  targets. Stencil 9, tile-word 4.
- **Skullgirls:** 2D, everything goes straight into the display buffer. All 8 stalls are candidates. They are
  first-use stalls at boot/menu (6 frames, 389 ms in total).

Where the time goes (session lines). Killzone: 194 pipelines waited for (7.7 s) and 42 built on the GPU thread
(3.0 s, worst 378 ms), plus 1.4 s of shader waits. Tearaway: 56 pipelines built on the GPU thread (2.9 s). Prediction
misses are mostly scenes whose state the prefetch cannot see, such as tile-word variants and integer views ("left
to the GPU thread"). The rest is the mission-load burst, where workers are still busy when the GPU thread arrives.

## Risk analysis (why the rule cannot be widened)
- **Visibility queries** (Tearaway): the game reads the result next frame and hides objects that report 0. A
  skipped draw would need a forced "visible" result. That is doable, but those draws are only 7 of Tearaway's 60.
- **Stencil writes / shadow volumes / stencil masks** (Tearaway, Killzone): a missing mask changes every later draw
  of the scene and is not limited to one object. These are the bulk of Killzone's stalls, so excluding them leaves
  nothing to gain there.
- **TileWord1**: a skipped writer leaves the second register word at its cleared value for later programs of the
  scene. It is also unknowable before the fragment module exists (`GeneratedShader.TileWord1`), so a draw waiting on
  its *shader* could not even be classified.
- **Off-screen / reused render targets**: there is no way to know at draw time whether the target is redrawn next
  frame. One-shot render-to-texture (LBP stickers/paper, Tearaway papercraft, cached UI) would stay wrong until the
  game redraws it, possibly forever.
- **CPU readback / writeback** (Gravity Rush and Killzone auto-exposure): a missing luminance draw feeds wrong
  exposure into the game's state, and the error persists through its temporal adaptation.
- **MSAA / programmable blending split**: skipping one is visual-only for that frame (candidate MSAA time: Tearaway
  192 ms). It is not a correctness problem, but too little time to matter.
- **Presented surface**: even here, a game that renders a loading/static screen once and re-presents it would keep
  the hole. Skipped draws in a presented surface would therefore have to be limited to surfaces that were
  presented in earlier frames.

The safe class removes 0 of Killzone's 91 frame-long hitches, 1 of Tearaway's 14 and 3 of LBP's 8. It removes all
4 of Skullgirls', but those are 389 ms in total at boot. This does not justify an option whose failure mode is
missing or persistent-wrong content.

## Other emulators
- **Vita3K** (Vulkan): asynchronous, multithreaded pipeline compilation, on by default. "If a pipeline has been
  estimated to not be essential, another thread handles it and the draw is skipped until the pipeline is compiled"
  (Vita3K 3-year progress report, 2024-07-31). It trades correctness for smoothness, which is what this note rejects
  as a default.
- **yuzu / Citra** (Vulkan): "asynchronous shader building" skips draws until the pipeline is ready. It was a hack
  option with known transient glitches. Citra's code has the same wait-or-skip choice.
- **Dolphin**: "Asynchronous (Skip Drawing)" was the old mode with missing objects. The current recommendation is
  ubershaders. In "Hybrid" mode, an always-ready uber pipeline draws while the specialised one compiles: nothing is
  skipped and nothing stalls.
- **Ryujinx**: does not skip. It relies on a multi-threaded disk shader cache and background translation.
- **DXVK**: `VK_EXT_graphics_pipeline_library`. Shader stages are compiled ahead and then fast-linked in about a
  millisecond at draw time, with the optimised pipeline built in the background.

## Alternatives, by expected value
1. **`VK_EXT_graphics_pipeline_library` fast-link** (no skip, no visual risk). The vertex-input, pre-rasterisation,
   fragment and output stages become libraries compiled when the module is ready. A missed pipeline is then linked
   on the GPU thread without optimisation (`VK_PIPELINE_CREATE_LINK_TIME_OPTIMIZATION` off), and the optimised one
   replaces it from a worker. This addresses exactly the 42+56 pipelines built on the GPU thread and most of the 7.7
   s of worker waits in Killzone. The GTX 1650 (546.17) and the Iris Xe both expose the extension. Fall back to
   today's path where it is missing (mobile).
2. **A job the GPU thread is waiting on goes first.** Today a pipeline job queued but not started is rebuilt on the
   GPU thread, and a started one is waited for. Shader jobs behind a long queue are run by the GPU thread itself.
   Worker priority (`VITASHARP_SHADER_WORKER_PRIO`) had no measurable effect in perf/kz-stutter. Limited headroom.
3. **Better prediction.** Tile-word variants and integer-view (raw output) pipelines are left to the GPU thread by
   `RunPipelineJob`. Predicting them would move Tearaway's tile-word stalls to the workers.
4. An uber-shader is not practical: GXP programs are arbitrary USSE code, not a fixed-function state space.
   `--shaderc-opt=zero` is already the default.

## Reproducing
`VitaSharp.Desktop test tests/scenarios/<name>.txt --shader-cache=<fresh dir> [--data=<scratch root>]`, with
`__GL_SHADER_DISK_CACHE_PATH=<fresh dir>`. Read the `[shadercache] session` and `PERF stalls` lines in the
scenario's log.txt.

## Graphics pipeline libraries (perf/desktop-res, 7 November 2026): tried, no gain, removed
`VK_EXT_graphics_pipeline_library` the way DXVK uses it: a first-use pipeline linked without link-time optimisation
from four libraries (vertex input, pre-rasterisation with the vertex module, fragment shader, fragment output), the
optimised pipeline built on a shader worker and swapped in when ready. Render-pass pipelines (MSAA, programmable
blending) included. Pixel-identical on the Killzone dump.

Killzone `kz_skin`, cold `--shader-cache` and a fresh `__GL_SHADER_DISK_CACHE_PATH` per run, GTX 1650, pinned to 4
logical CPUs at BelowNormal, interleaved off/on/on/off:

| run | pipelines built on the GPU thread | frames stalled >= 16.7 ms | worst stall |
|---|---|---|---|
| off 1 | 175 (10734 ms) | 90 | 14973 ms |
| on 2 | 359 (21397 ms), 44 linked | 90 | 10972 ms |
| on 3 | 352 (18788 ms) | 97 | 7794 ms |
| off 4 | 126 (5031 ms) | 90 | 4888 ms |

- With a cold NVIDIA driver cache a shader library costs about as much as a whole pipeline: the driver compiles the
  shader either way, and Killzone has about as many shaders as pipelines (~670 / ~500). Libraries built on the GPU
  thread moved the compile there (a first version: 13 s in libraries); libraries prebuilt on the two workers took the
  workers' time from the pipeline prefetch (166 prefetched instead of 351), so more pipelines were built on the GPU
  thread. Linking itself is cheap (~0.03 ms per pipeline).
- With a warm driver cache and a cold emulator cache (the replay of the Killzone dump), linking did cut the GPU
  thread's pipeline time from 502 to 91 ms, but those pipelines were already only 2.6 ms each.
- The first-run stall here is compile throughput (two workers, about 10-13 s of shaderc plus the driver), not the
  pipeline step. More workers or prewarming from a shipped `pipelines.list` would address it; GPL does not.
- The patch is kept out of the tree (agent scratchpad `and10_gpl_wip.patch`).

## Compile throughput (perf/desktop-12, 8 November 2026)
Injustice `inj_fight` (Training fight) and Killzone `kz_skin`, cold caches, GTX 1650, pinned to 4 logical CPUs:
- The NVIDIA driver takes 60-90 ms per pipeline (83 ms on average when replaying the Injustice capture with nothing
  else running), and nearly every pipeline is a new shader pair (Injustice 115 pipelines / 105 pairs, Killzone 552 /
  533). shaderc at `zero` is 2-12 ms per shader. The prefetch sees a draw about one frame before the GPU thread needs
  it, so each new pipeline still costs most of its compile time.
- `--shaderc-opt=perf`: driver 83 -> 59 ms per pipeline, shaderc 12 -> 80 ms per shader. Net loss.
- No `VkPipelineCache` on pipeline creation: no gain (the shared cache does not serialise the workers).
- 4 workers instead of 2 on 4 CPUs: same stalls, shaderc time doubled (the CPUs are full).
- Pipeline key normalisation (blend state when blending is off, colour state without a colour attachment, vertex
  layout): 2 duplicates in 115 pipelines, 3 in 552. Not worth it.
- Done: glslang and the translator warmed up in the background (first new shader of a warm session ~20 ms instead of
  ~150-190 ms), the prewarm shared with the shader workers (cold driver cache: Killzone 21-22 s -> 11 s), and the
  title caches opened again by a renderer replaced for the same title (F1 / F3 / load state).
- Left: the driver's per-pipeline cost on new content. Smaller generated GLSL is the remaining lever (optimised SPIR-V
  showed ~30% less driver time).

## Smaller GLSL (perf/desktop-13, 8 November 2026): started, not measured
- The bundled shaderc_shared.dll does not export the SPIRV-Tools optimizer C API (no spvOptimizerCreate), so a cheap
  custom pass list (mem2reg/scalar replacement only) is not available; only shaderc's zero/size/perf levels.
- Experiment (removed after the measurement below): `VITASHARP_SCALAR_BANKS=1` (`ShaderGenerator.ScalarizeBanks`) turns
  register banks addressed only with constant indices into one variable per used register and clears only those, so
  the driver no longer has to split private arrays. Changes the GLSL without a key change: use a fresh shader cache.
- Measured (8 November 2026, Injustice capture replay, cold emulator and driver caches, 103 pipelines and 147
  shaders built on the GPU thread, runs alternated off/on/on/off/on/off/on/off): GPU-thread pipeline time off 10109
  (first run, warm-up), 8572, 8032, 7227 ms; on 8130, 7189, 7644, 7626 ms. Without the first run: off 7944 ms, on
  7647 ms on average (-4%, inside the run-to-run spread); shader time the same (~2.0-2.1 s). And the output is not
  pixel-identical (mean difference up to 0.005 on every compared frame, while off-vs-off and on-vs-on are identical).
  Not worth it: removed (perf/desktop-13).

## Not continued (perf/desktop-14, 8 November 2026)
- Nothing left in the list above promises a measurable win: GPL fast-link and a pipeline cache were measured without
  gain, more workers do not help on the 4 pinned CPUs, scalar banks and `--shaderc-opt=perf` did not pay off, and
  `VK_EXT_shader_object` would hit the same per-shader driver compile that dominates here. The remaining lever is
  shipping prewarm data (`pipelines.list`), which is per game and out of scope for a generic fix.
