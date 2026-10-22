# Killzone stutter (perf/kz-stutter)

Killzone Mercenary (PCSC00045), scenario = kz_skin inputs, 4700 frames, `--profile`. The NVIDIA driver's own
shader disk cache was isolated per run with `__GL_SHADER_DISK_CACHE_PATH` (without it, "cold" pipeline creation
hits the global driver cache and looks 12x cheaper than for a user after a GeneratorVersion bump).

## Tooling
- `Core/Util/FrameLedger`: host time per cause (shader translate, shaderc, pack load, pipeline create, texture
  upload, fence wait, writeback, guest waits on the GPU, file IO, JIT, GPU idle, pacing sleeps, GC, background
  shader/pipeline work), sampled at every present. With `--profile` the report adds `PROFILE ledger`,
  `PROFILE hitch frames >50 ms` (causes summed over [f-1,f]), `PROFILE hitch list` (every hitch with its top cause)
  and the 30 worst hitches. `gpuIdle` as top cause = the GPU thread had nothing to do (guest-bound: loads).
- Session line `[shadercache] session ...` now also reports shaderc compile count/time, .spv disk hits and prefetch.

## Measurements (excess = sum of (interval - 33.3 ms) over frames > 50 ms)
| run | hitch frames | excess | p99 | max | GPU-thread compile |
|---|---|---|---|---|---|
| main, cold (driver cache cold) | 158 | 46.3 s | 161 ms | 16.6 s | shaders 29.0 s (shaderc 27.9 s, 44 ms each) + pipelines 12.1 s (24 ms each) |
| main, warm 2nd run | 21 | 4.9 s | 34 ms | 1.4 s | 0.4 s (prewarm 535 pipelines in 163 ms) |
| main, version-bump simulation (GLSL unchanged) | 53 | 6.7 s | 52 ms | 1.9 s | .spv hits + VkPipelineCache: 1.4 s |
| shaderc O0 only | 79 | 23.2 s* | 88 ms | 8.9 s | shaders 5.9 s + pipelines 15 s |
| prefetch shaders (perf opt) | 101 | 28.7 s | 133 ms | 8.2 s | 7.4 s + 18.2 s |
| prefetch shaders, O0 | 72 | 23.1 s | 86 ms | 7.1 s | 1.3 s + 19.1 s |
| prefetch shaders + pipelines, perf opt | 86 | 18.8 s | 111 ms | 5.6 s | 6.0 s + 5.6 s |
| **prefetch shaders + pipelines, O0 (branch default)** | 58-64 | **10.6-11.1 s** | 63-72 ms | 3.3 s | ~1.0 s + 1.1-2.1 s |
| same, warm | 21 | 5.9 s | 36 ms | 2.5 s | 0.1 s |

\* that run's driver cache was the shared one. GPU time per frame is unchanged by O0 (5.67 vs 5.71 ms).
Remaining cold hitches: the mission-load burst (~3.3 s, ~30 s of worker CPU for ~470 pipelines) and guest-bound
loading frames that are present warm too (boot, menu transitions, level load: gpuIdle/jit/fileIo/texUpload).

## What the branch changes
- shaderc optimisation level defaults to Zero (`VITASHARP_SHADERC_OPT=perf|size|zero`); the level is part of the
  .spv disk cache name. spirv-opt was ~85% of the GLSL->SPIR-V time; O0 SPIR-V costs the driver ~20% more per
  pipeline.
- Per-thread shaderc compilers (the old global lock serialised all compiles).
- Prefetch (`VulkanRenderer.AsyncShaders.cs`): `IGpuBackend.Prefetch` runs on the submitting thread for every
  command. Draws with unknown modules queue translation+compile jobs to 4 workers; each new draw signature queues a
  pipeline job that predicts the pipeline key from the last BeginScene (format, gamma view, native MSAA samples)
  and builds it. The GPU thread waits for in-flight jobs (or runs a shader job itself if no worker started it).
  Nothing is skipped: a wrong prediction only wastes a pipeline (~95% of pipelines were predicted).
  `VITASHARP_ASYNC_SHADERS=0`, `VITASHARP_ASYNC_PIPELINES=0`, `VITASHARP_SHADER_WORKERS=N`,
  `VITASHARP_SHADER_WORKER_PRIO=below` (no measurable difference).

## Next steps (not done)
1. Rebuild after a GeneratorVersion bump at boot: store GXP bytes + ShaderHints per (program, hints) in a
   version-independent pack, keep pipelines.list across generator bumps, and let the prewarm translate/compile the
   modules in parallel. First session after an update would then behave like a warm one (rebuild ~700 shaders +
   500 pipelines is ~30 s of CPU, ~8 s on 4 workers, well before gameplay).
2. Optional Vita3K-style async pipelines that skip the draw until ready (removes the remaining burst, can lose
   one-shot render-to-texture draws: keep it opt-in).
3. Guest-bound load hitches: the JIT warm-up (interpreting new code) and texture uploads at level load.

## User settings (merge prep)
- Launcher: Configurações > Geral > **Desempenho**: "Compilação rápida de shaders" (shaderc O0, off = Performance)
  and "Compilar shaders e pipelines em segundo plano" (prefetch workers), both on by default, stored in
  `launcher.json` (`FastShaderCompile`, `BackgroundShaderCompile`) and passed to `run` like the CPU backend.
- CLI: `--shaderc-opt=zero|size|perf`, `--async-shaders=on|off`, `--async-pipelines=on|off`, `--shader-workers=N`.
  `test <scenario> [options]` now forwards run options (they win over the scenario's `args=`; this also makes
  `--shader-cache=` work on the `test` command line). Precedence: environment variable > option > default.
- Options live in `VulkanRendererOptions` (per renderer), no longer in statics.

## Review fixes (merge prep)
- `.spv` disk cache written via temp file + rename: two threads compiling the same GLSL could hand a half-written
  file to `vkCreateShaderModule` or fail on a sharing violation.
- GPU thread: in-flight jobs are checked before the shader pack (no second module for a job that already stored
  its SPIR-V), the module table is rechecked after an in-flight miss, and pipelines the GPU thread builds itself are
  claimed in `_pipelineInflight` so a worker never builds the same pipeline concurrently.
- Shutdown: running `Prefetch` calls are counted and drained, queued jobs are cancelled instead of compiled inside
  `Dispose`, running jobs finish before the workers are joined, no pool starts after shutdown began.
- Predicted scene state is one immutable record (several guest threads may submit).
- `VK_ERROR_DEVICE_LOST` from submit, fence wait or pipeline creation stops background work.
- VkPipelineCache: created without `EXTERNALLY_SYNCHRONIZED`, so concurrent `vkCreateGraphicsPipelines` and
  `vkGetPipelineCacheData` are synchronised by the driver; `vkMergePipelineCaches` runs in BeginTitle before the
  guest submits anything and `vkDestroyPipelineCache` after the workers are joined.
- Upgrade from main: shaders.pack/pipelines.list keep their format and GeneratorVersion, so a main cache is reused
  as is (dd_boot on a main-built cache: 0 compiled, 5 pipelines prewarmed). Only `.spv` loose files change name
  (`.o0.spv`), which matters only for pack misses.

## Validation 2026-10-22 (after merging main 257d3d7)
Killzone kz_skin, 4500 frames, cold = fresh `--shader-cache` and NVIDIA driver cache:
| run | hitch frames | excess | p99 | max | GPU-thread shader + pipeline |
|---|---|---|---|---|---|
| settings ON, cold (2 runs) | 67 / 47 | 11.4 / 9.6 s | 81 / 57 ms | 3.5 / 3.1 s | 1.0 + 1.3 s / 1.0 + 1.3 s |
| settings ON, warm | 20 | 5.2 s | 37 ms | 1.5 s | 0.15 s + 0 |
| settings OFF (perf, no async), cold | 86 | 43.6 s | 131 ms | 17.8 s | 29.7 s + 12.6 s |
| settings OFF, warm | 18 | 4.6 s | 34 ms | 1.5 s | 0.2 s + 0 |

A first set taken while the user was playing Killzone on the same GPU (plus another agent's test run) was
fence-bound and is not comparable (ON cold 93.9 s with 128 s of fence waits, OFF cold 70.8 s).
Frames vs main (cold caches): kz_skin, uc_walk, ucx_title, rbs_continue, mania_boot, dd_boot, f1_trees_lod,
gow_play: no crashes or hangs, no new errors, same METRICS; differences are run-to-run timing only
(mania/DD static frames pixel-identical).
