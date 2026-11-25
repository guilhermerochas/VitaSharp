# Skipping draws while their pipeline compiles (perf/pipeline-skip): analysed, NOT implemented

Roadmap item: "Opção de pular o desenho enquanto o pipeline não fica pronto (tira os últimos engasgos da primeira
vez)". Decision: **not implemented**. Draws that are safe to skip account for almost none of the remaining
first-run stalls in the 3D games, and the draws that do stall are the ones a game depends on. This note records the
evidence and the alternatives worth doing instead.

## What happens today (main 45edc3d, GeneratorVersion 16)
- `IGpuBackend.Prefetch` runs on the submitting guest thread. It queues shader translate+compile jobs and one
  pipeline job per new draw signature to the shader workers (`VulkanRenderer.AsyncShaders.cs`). The workers take
  shader jobs first, except that the oldest pipeline job whose shaders are no longer pending goes ahead of the shader
  jobs queued after it (perf/uber4, 21/11/2026): the work finishes in submission order, as the GPU thread needs it.
  Before, a frame of new draws left every pipeline behind all the shaders and the GPU thread built the first ones
  itself although they had been predicted. In debug mode, "pipeline built on the GPU thread" names the closest
  prediction (program load or draw prefetch) and the jobs still queued, and "... reached a pipeline the GPU thread
  built itself" marks a right prediction that came too late.
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
- The patch is kept out of the tree (local work folder `and10_gpl_wip.patch`).

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

## Graphics pipeline libraries, second attempt (perf/desktop-26, 13 November 2026): kept behind a switch, off
Design, after DXVK (`dxvk_graphics.cpp`: shader libraries plus a fast-linked pipeline, optimised pipelines compiled
in the background at a lower priority) and vkd3d-proton. The 7 November patch compiled the optimised pipeline on the
prefetch workers as well as the libraries, which doubled the workers' driver work. What is different now:
- The prefetch worker and the GPU thread build the four libraries (vertex input, pre-rasterisation with
  the vertex module, fragment shader with the fragment module and sample count, fragment output) and link them. The
  shader libraries are keyed by module and render pass, so MSAA and programmable-blending pipelines (render passes)
  link too. The 7 November patch prebuilt only the single-sample variant, and Killzone renders its scenes with 4x MSAA.
- The whole, optimised pipeline is queued last on the shader workers (after the draw-driven jobs, the boot rebuild
  and the prewarm). The GPU thread swaps it in at its next lookup, and the linked one is destroyed after the GPU has
  finished with it.
- Enabled only where the driver reports `graphicsPipelineLibraryFastLinking` (the DXVK condition), with dynamic
  rendering and the extended dynamic state. Off on the Qualcomm, ARM and Imagination blobs
  (`GpuQuirk.AvoidPipelineLibrary`, precautionary because it has not been measured there) and in the mobile
  `--gpu-profile`s. MoltenVK and Dozen do not have the extension. Off by default: `VITASHARP_PIPELINE_LIBRARY=1`
  (or `VulkanRendererOptions.PipelineLibrary`) turns it on. The prewarm of recorded pipelines stays whole.
- Counters: `VulkanRenderer.PipelineStats` (`CriticalPath` = whole pipelines compiled on the GPU thread + pipelines
  it waited for + shader libraries it compiled), plus a "pipeline library:" part on the `[shadercache] session` line.

Killzone F8 dump replay (`captures/PCSC00045_..._tudo/gpu.vsgd`), cold emulator and driver caches, all work on the
GPU thread (replay default), GTX 1650 546.17, pinned to 4 logical CPUs:

| | GPU thread | worst frame |
|---|---|---|
| off | 244 pipelines compiled whole, 20.0 s | 31.3 s |
| on | 244 linked (123 ms in total) from 346 shader libraries (120 vertex, 226 fragment) | 28.8 s |

All 10 compared frames are pixel-identical. The libraries replace 488 stage compiles with 346 (-29%), but on this
driver a shader library costs about half of a whole pipeline, so the cold-cache stall only shrinks by that share.

Killzone gameplay (`kz_skin` with its taps moved for the current boot timing, 7300 frames, through the mission
briefing to the first level), same machine, pinned to 4 logical CPUs at BelowNormal, 2 shader workers:

| run | caches | GPU thread (critical path) | frames >= 16.7 ms stalled by shader/pipeline work | stall total |
|---|---|---|---|---|
| off | emulator and driver cold | 100 compiled whole + 188 waited for (+42 shader waits) | 62 (worst 5.2 s) | 14.4 s |
| on | emulator and driver cold | 0 whole, 166 waited for, 221 shader libraries, 173 linked (+137 shader waits) | 80 (worst 7.7 s) | 25.6 s |
| off | emulator warm, driver cold (no `vk_*.cache`) | 26 compiled whole, 1 waited for | 16 (worst 116 ms) | 0.9 s |
| on | emulator warm, driver cold (no `vk_*.cache`) | 0 | 0, but the game sat on a black screen for the whole run | - |

- Cold caches: worse. The libraries and the background optimised pipelines take the two workers' time from the
  prefetch, so more shaders and pipelines are waited for. On this driver a shader library costs about half a whole
  pipeline (replay above), and Killzone reuses a module in few pipelines (499 pipelines, 219 vertex and 400
  fragment libraries).
- Prewarm from the recorded pipelines with a cold driver cache: linking the 447 pipelines from libraries took 51.5 s
  on two workers against 19.7 s for whole pipelines, and the session that used the libraries stayed black (GPU
  thread stalls of 6-20 s at every frame dump). The prewarm therefore always builds whole pipelines.
- The existing levers already do what GPL was meant to do here. The prewarm of `pipelines.list` (second session: 16
  stalled frames, worst 116 ms, against 62 / 5.2 s in the first) and the background prefetch (`--async-shaders`, on
  by default in `run`/`test`, off only in `replay-gpu`) never skip a draw, so they cost nothing in the image.
- What is left on the first session is the driver's compile time (about 80 ms per pipeline) and the 2 workers on 4
  pinned CPUs. More workers on the unpinned 8-CPU machine, or shipping the recorded `pipelines.list` as prewarm data,
  are the remaining levers. GPL helps only on drivers where a library is much cheaper than a pipeline (not measured:
  AMD, Intel, Mesa).

GPU-thread stalls of 6-31 s outside shader work (the watchdog's "GPU worker made no progress", 13 November 2026). In
the "emulator warm, driver cold" Killzone run the watchdog reported the GPU worker idle for 11, 19 and 31 s with
commands pending. The stalls were not counted as shader/pipeline work (worst 116 ms), and the audio output also had a
gap of 5.0 s. The same condition, rerun when the machine was quiet (average load 22%), had no watchdog stall and a
1.2 s worst audio gap. The emulator ran at BelowNormal priority, pinned to 4 CPUs, while other jobs kept the CPU at
100%. The most likely cause is that the whole process was starved (BelowNormal + AboveNormal threads stay below
Normal-priority work). That rests on these two runs only: the quiet rerun had no stall, so no stack was taken during
one. Measurements are only valid with the 75% load rule; at the default Normal priority this does not apply to
players.

VUID-VkGraphicsPipelineCreateInfo-renderPass-09652 with pipeline libraries only (replay of the Killzone dump with
`--validation`: 0 with `VITASHARP_PIPELINE_LIBRARY=0`, 2 distinct reports with `=1`). It is raised by a fragment
shader that reads the framebuffer through `last_frag_in` (input attachment 0, programmable blending,
`ShaderGenerator` fetch-input variants) when a fragment-shader library or the link is created with dynamic rendering
and `colorAttachmentCount` 0. Adding the key's colour attachments to the fragment library and to the link did not
remove it, so the call is not identified yet. The frames stay pixel-identical. This has to be fixed before the
libraries can be turned on by default.

## Dynamic blend, write mask and vertex input (perf/stut1, 25 November 2026): counted, not implemented
`VK_EXT_extended_dynamic_state3` (colour blend enable/equation and write mask; polygon mode) and
`VK_EXT_vertex_input_dynamic_state` would let one pipeline serve the variants of a shader pair that differ only in
that state, as in Ryujinx and yuzu. Counted over the recorded pipelines of 70 titles (copies of their
`pipelines.list`, 10,994 recipes, 10,973 distinct): the shaders, attachment format, samples, tile-word and fetch-pass
flavour stay in the key, and the rest is collapsed.

| state made dynamic | pipelines | fewer |
|---|---|---|
| none (today) | 10,973 | - |
| blend + write mask | 10,240 | 6.7% |
| + polygon mode | 10,240 | 6.7% |
| vertex input only | 10,663 | 2.8% |
| blend + mask + polygon + vertex input | 9,928 | 9.5% |
| + topology class (points/lines/triangles) | 9,908 | 9.7% |

- The share is in small 2D/UI-heavy sets (GTAVCECTY 57 -> 31, DEADSPACE 42 -> 29, the shell 343 -> 270). The 3D games
  whose first session stutters keep almost all of theirs: PCSC00045 2,058 -> 2,034 (1.2%), PCSE00333 809 -> 794
  (1.9%), PCSE00053 262 -> 254 (3.1%), PCSA00017 888 -> 888.
- A variant of an existing pair is also the cheapest pipeline for the driver (its stages are already compiled), so
  the time saved is below the count's share.
- With compile-on-program-load (on by default) the cold replays of the dumps already build 0 pipelines on the GPU
  thread and have 0 frames >= 4 ms stalled, so there is no slow frame for it to remove; the only effect left would be
  a few percent less work while loading.
- Not worth the second path (two pipeline key shapes, extension feature checks per driver, the Mali/Adreno path kept).
  Revisit only if a title's recorded list shows a large share of same-pair variants.
