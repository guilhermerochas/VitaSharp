# feat/diag-nan: NaN/Inf detection in debug mode (WIP checkpoint, 2026-10-23 night)

Worktree `C:\Users\guisa\VitaSharp\.claude\worktrees\diag-nan`, branch `feat/diag-nan` from main 0836830 (= 664cdfb + docs).
Not merged, not pushed. Full description of the feature is in `docs/notes/debug-mode.md` ("NaN/Inf detection").

## What is built (builds with 0 warnings; unit tests NOT run yet)

- `src/VitaSharp.Renderer.Vulkan/VulkanRenderer.NanCheck.cs` (new):
  - Layer 1 (debug mode, follows F9): at each EndScene whose surface is a float format (F16x4, U2F10F10F10 -> host
    F16x4, F11F11F10, F16/F32 variants) a 16x16 compute shader scans every pixel: counts NaN, Inf, huge (>=65000 /
    1e30), negative, first NaN/Inf and first huge pixel. Results in a per-frame-slot 2 KB host-visible buffer, read in
    RetireFrame (non-blocking). Per-target history -> `[nan]` lines (appear / still every 300 presents / clean after
    3), `nan` anomaly -> auto capture (Desktop Diagnostics arms it at the next present, post-roll 0), `nan.txt` in
    captures, `PERF nan` line in the report. Raw-bit targets skipped (via `_rawLogged`).
  - Layer 2 `VITASHARP_DEBUG_NAN=draws` (launch switch): `ShaderGenerator.EmitNanDrawCheck` adds atomics into set 0
    binding 4 (`nan_hits[draw_id*4..]`); renderer binds a 64 KB table per frame slot, remembers each draw
    (programs/hints/rt/textures), logs `[nan-draw]` lines + per-submission summary; counts as a debug override
    (pack/list/recipes bypassed, loose .spv cache off, driver cache not saved).
- Small hooks: `VulkanRenderer.cs` (Frame.Nan, retire, submit barrier, EndScene scan, Dispose, compiler cache dir),
  `Draw.cs` (5th binding only in draws mode, pool size 5*8192, bind + note draw), `Cache.cs` (no vk cache save in
  draws mode), `Textures.cs` (report), `Diagnostics.cs` renderer (nan.txt) + Desktop (post-roll 0 for nan, info.txt),
  `ShaderCompiler.cs` (ShaderKind overload for compute), `ShaderGenerator.cs` (NanDraws flag + emission; normal
  output unchanged, no GeneratorVersion bump).
- Bug fixed during testing: the scan must call BeginFrameIfNeeded() (Idle can submit mid-scene) - crashed in
  CmdPipelineBarrier before.

## Verification done (Killzone, data root .vitadata, scenarios in scratchpad/nansc)

- (a) `VITASHARP_DEBUG_FS=91AB29A92F527931:vec4(uintBitsToFloat(0x7FC00000u))` + --debug-mode: `[nan]` on HDR
  0x65C78000 (13 NaN px) and propagated into the 240x136 luminance 0x663D6000; `nan` anomaly -> auto capture
  PCSC00045_20261023_021915 with replay/ (127 frames) and nan.txt. OK.
- (b) + `VITASHARP_DEBUG_NAN=draws`: `[nan-draw]` names fs=91AB29A92F527931 (vs 8029D59D2F677BE2, n=30, t0
  0x6011B690) draw by draw, then the propagation chain: 87AA82B962069A7A (luminance downsample, rt 0x663D6000),
  94A7330124AABFB7 (RGBM encode) and 1A2ADF5414A841E0 into the post atlas 0x66272000. OK.

## Natural findings (Killzone kz_skin, no injection) - THE LEAD for the green glitch

- With default native MSAA 4x: from ~f3083 (cutscene/gameplay start) the HDR scene 0x65C78000 gets 1-2 NaN or Inf
  pixels in ~5% of presents (49 bad presents f3093-f3945), and the luminance target 0x663D6000 then gets NaN through
  the downsample (fs 87AA82B962069A7A), which feeds auto-exposure and the post atlas. Draws mode: ~30 different
  scene fragment programs each emit 1 NaN/Inf fragment occasionally (A4BEB71A61321C78 most often, vs C8D905EEB7CFE2E6;
  also 66DF0A5D1E0FAA67, A3025415177842D0, F2E38FC6C187C762, BEF0ABB514D82E6B, ...): a common cause, not one shader.
- With `VITASHARP_MSAA=off` (same route to f3700): **0 NaN/Inf draw hits, 0 NaN/Inf in any target**. So the NaNs come
  from the native MSAA path. Hypothesis: varyings evaluated at the pixel centre of partially covered pixels
  (extrapolated outside the triangle; GLSL inputs are not `centroid`) -> normalize/rsq/log of an out-of-range value.
  Next test: declare fragment inputs `centroid` (or run per sample) in MSAA scenes, rerun draws mode; compare
  with Vita3K/hardware behaviour before changing (do not fix on this branch, report).
- MSAA off showed one other thing: `7085 huge` px (>=65000) in 0x65C78000 at f3411 (first at 187,0) - check.
- U2F10F10F10 luminance target regularly holds 1 negative pixel (up to 128); hardware would store 0 (unsigned).
- A first try of the MSAA-off run stayed black after the save-data screen (boot hang, nondeterministic; a rerun
  was fine) - unrelated to this branch.

## Cost so far (other agents ran 3 emulators at the same time: fps noise is large)

kz_skin 4500 frames: debug off 24.2 fps, GPU worker 4.76 ms/frame, GPU 7.8 ms; debug on 26.7 fps, 5.98 ms (incl. 3
auto captures); draws mode + debug 25.9 fps, 5.74 ms. Direct measurement (PERF nan): ~10-12 us per scan recorded
(~2 scans/frame), 22-24 us per frame retire (debug), 32-41 us in draws mode => ~0.05-0.1 ms/frame GPU worker.

## Left to do (exact next steps)

1. `dotnet test tests/VitaSharp.Tests -c Release --filter "Category!=Slow"` (build into a scratch -o is for the app;
   tests run in the worktree) and confirm 0 warnings.
2. Cost reruns when the machine is quiet: kz_skin debug off / on / draws (scenarios in
   `<scratchpad>/nansc/nan_kz_off.txt`, `nan_kz_dbg.txt`, `nan_kz_draws.txt`; env VITASHARP_DEBUG_NAN=draws).
   Add a cost table to docs/notes/debug-mode.md.
3. Natural runs not done yet: kzl_walk3 (long walk), uc_walk, gow_play with --debug-mode and draws mode; for
   uc/gow use data root `<scratchpad>/nrb/data` (contains junctions: rmdir them before deleting anything).
4. Quick MSAA investigation above (centroid test), report only.
5. Cleanup: `.vitadata/tests/nan_*`, `.vitadata/diagnostics/PCSC00045_20261023_02*` (+ .zip; all created by this
   branch's runs 02:15-02:40), scratchpad `nanbin`, `nansc`.
6. Final commit (trailer Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>), do not merge/push; remove this
   NOTES file before merge if not wanted.
