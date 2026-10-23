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
1. ~~Rebuild after a GeneratorVersion bump at boot~~: done in perf/cache-rebuild, see "Boot rebuild" below.
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

## Boot rebuild after a generator bump (perf/cache-rebuild)
Before: `shaders.pack` and `pipelines.list` were both keyed by `ShaderGenerator.GeneratorVersion`, so the first
session after an emulator update retranslated every shader during gameplay and had no prewarm at all.

Layout under `<cache>/titles/<ID>/` now:
- `shaders.pack` (unchanged format v3, keyed by generator version): translated metadata + SPIR-V.
- `programs.pack` (new, `ShaderRecipeStore`, format "RC" v1, **not** keyed by the generator): GXP bytes once per
  program (verified against the program hash on read) + one recipe per (program, hint key) = the serialized
  `ShaderHints`, in first-use order. A recipe whose hints no longer produce its key is rejected.
- `pipelines.list` (format v4, `PipelineRecipe`, **not** keyed by the generator): shader identities (program hash +
  hint key), the resolved fixed-function state and the *guest* vertex attributes/streams. The Vulkan vertex layout
  is re-derived from them and the current vertex shader's inputs (v3 stored the resolved layout, which depends on
  the generator). The record also stores the fragment shader's UsesFragColor/RawColorBits the blend/attachment
  state was resolved against; a retranslated shader that disagrees makes the record "stale" (skipped, the draw
  builds its pipeline normally).
- `vk_*.cache`: unchanged.

Recording: both packs are written on the GPU thread at the first use of a shader/pipeline in a session (a lookup
when already on record). Prefetched pipelines the game never used are no longer recorded.

Boot (`BeginTitle`): if some recipes are missing from `shaders.pack` (generator bump, or a partial pack), the
**rebuild** starts: a coordinator thread queues, in first-use order, each recorded pipeline right after its two
shaders, then the remaining recipes, to the existing shader-worker pool (`_rebuildJobs`, served after the
draw-driven prefetch queues). Shader jobs are registered in the in-flight table, so:
- a draw that needs a shader first finds the job: `Prefetch` promotes it to the front queue, or the GPU thread runs
  it itself if no worker started it (counted as "reached by the game first"); nothing is compiled twice;
- pipeline jobs claim their key like prefetched ones (`BuildSharedPipeline`), so the GPU thread waits for an
  in-progress build instead of duplicating it.
Otherwise (pack up to date) the old single-thread prewarm runs on the v4 list. A v3 list of the *current* generator
is still prewarmed once and migrates as the game uses its pipelines (upgrade from main keeps its prewarm).

Settings: with "Compilar shaders e pipelines em segundo plano" off (`--async-shaders=off`) the rebuild runs
**synchronously before the game starts** (BeginTitle joins it; the workers are started just for it). Chosen over
skipping because it costs a few seconds once (3.6 s for Killzone on 4 workers) instead of 10+ s of GPU-thread
compiles and multi-second freezes during play. `VITASHARP_SHADER_REBUILD=0` disables the rebuild.
Test-only: `VITASHARP_GENERATOR_VERSION_SALT=N` makes every version-keyed cache (shader pack and loose `.spv`
names) behave as if the generator had been bumped and every GLSL changed (the SPIR-V itself is unchanged, so the
VkPipelineCache still hits, as for a real bump that leaves most shaders alone).

Logs: `[shadercache] rebuilt N shaders + M pipelines for generator vX in Y s on K workers (...)` and the same text in
the `[shadercache] session` summary (`rebuild: ...`).

Robustness: torn/corrupted records (per-record checksums) are skipped and counted; corrupted program bytes fail the
hash check; malformed recipes/pipeline records are rejected by exact-size decoding; older formats (shader pack
v2, pipeline list v1-v3 of other generators, anything without the right header) are discarded as before. A second
concurrent session now gets a **read-only** view of each pack (`BlobPack.IsReadOnly`: no reset, no truncation,
appends are no-ops) instead of no cache at all. Shutdown during the rebuild cancels queued jobs and logs "stopped
early"; the remaining recipes are rebuilt next boot.

### Measurements 2026-10-23 (Killzone kz_skin, 4500 frames, `--profile`, NVIDIA cache isolated per run)
Cache populated by one cold run of this branch (727 shaders, 541 pipelines recorded). Bump = salt 1 on a copy of
it; "old" = `VITASHARP_SHADER_REBUILD=0`, which behaves like main after a bump (stale pack, no usable prewarm).
| run | hitch frames | excess | p99 | max | GPU-thread shader + pipeline | frames stalled by shader work (worst) |
|---|---|---|---|---|---|---|
| warm (settings on) | 38 | 5.6 s | 48 ms | 1.40 s | 0.17 s + 0 | 1 (167 ms) |
| bump, old behaviour (on) | 34 | 5.8 s | 39 ms | 1.85 s | 1.30 s + 0.39 s (103 pipelines) | 93 (184 ms) |
| bump, rebuild (on) * | 44 | 6.5 s | 51 ms | 1.64 s | 0.01 s + 0 | 1 (5.7 ms) |
| warm (async off) * | 17 | 4.8 s | 34 ms | 1.56 s | 0.16 s + 0.02 s | 1 (172 ms) |
| bump, old behaviour (async off) * | 119 | 17.3 s | 102 ms | 4.95 s | 10.4 s + 1.9 s (506 pipelines) | 104 (3.6 s) |
| bump, rebuild (async off) * | 170 | 17.3 s | 109 ms | 1.39 s | 0 + 0 | 0 |

Rebuild: 727 shaders + 541 pipelines in 4.0-5.4 s on 4 workers in the background, 3.6 s before the game started
with async off; 0 shaders reached by the game first. \* other agents ran games on the machine during these runs
(hitch counts/excess inflated by CPU contention: jit/gpuIdle causes, 0 shader stalls); the GPU-thread and
shader-stall columns are not affected. With background compilation on, the old behaviour already hid most of a
bump behind the prefetch workers (the hitch metrics are dominated by guest-bound loading frames present warm too);
the rebuild removes the remaining GPU-thread compile time (1.7 s -> 0.01 s) and 92 shader-stalled frames, and with
async off it removes 12.3 s of GPU-thread compiles and the 3.6 s worst freeze.

Regressions (scratch data root, cold then bump): mania_boot frames pixel-identical to main (cold and after the
rebuild); uc_walk same METRICS as main, no errors (frames differ only by the usual camera nondeterminism);
Uncharted recreates its renderer once at boot, and the second renderer correctly rebuilds only what the first one
had not finished.
