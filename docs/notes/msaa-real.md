# Real Vulkan MSAA for GXM MSAA surfaces (branch perf/msaa-real)

Status notes, kept up to date in case the work is interrupted.

## Design

- `VulkanRenderer.MsaaNative.cs`: GXM MSAA + DOWNSCALE colour surfaces render into a 2/4-sample colour image
  (`_msImages`, per render target) and a multisampled depth/stencil target (`_depthTargets` keyed by sample count).
  Each rendering resolves into the 1x surface with a dynamic-rendering resolve attachment (AVERAGE; SAMPLE_ZERO
  through integer views once the scene wrote raw integer outputs).
- Renderings are recorded into secondary command buffers and begun in the primary only when they end, so the store
  ops are chosen knowing what follows: colour samples STORE while the scene continues, DONT_CARE at scene end; depth
  DONT_CARE at scene end unless SCE_GXM_DEPTH_STENCIL_FORCE_STORE.
- Scene start: the first rendering loads with DONT_CARE and draws a full-screen copy of the scene-start copy of the
  surface into every sample (`RecordMsaaLoad`). The surface itself cannot be sampled: it is the resolve target.
- Renderings interrupted only for a programmable-blending copy or an integer-view switch skip the resolve
  (`_msUnresolved`); the next rendering resolves, or `ResolveMsaa` (empty rendering with only a resolve) runs when
  something reads the surface first (any `EndRendering()` with resolve).
- Programmable blending: the samples are copied into a multisampled image (`_msFetchCopies`) bound at set 3 binding 1
  (`sampler2DMS last_frag_ms`); shaders read `texelFetch(last_frag_ms, coord, gl_SampleID)` (sample-rate shading)
  when `rinfo.fb_samples > 1`. Without sampleRateShading they read sample 0 (`ShaderGenerator.PerSampleFramebufferFetch`,
  part of the shader-pack key). GeneratorVersion 7.
- Depth textures / CDRAM mirror reads of a multisampled depth target read guest memory (the samples are not stored,
  as on hardware without FORCE_STORE). A FORCE_STORE 4X depth read as a depth texture (LittleBigPlanet's depth of
  field reads its 720x408 4X depth as a 1440x816 U16 texture) is unpacked by a full-screen pass into a 2x single-sample
  depth image (`UnpackedDepthTexture`, sample (x&1, y&1) of pixel (x/2, y/2)); other forced-stored reads (2X, stencil,
  CDRAM mirror) are still warned.
- MSAA without downscale (Uncharted's 360x204 4X pass onto 720x408) keeps the 2x path: the surface already stores
  one pixel per sample, so rendering at 2x costs exactly the samples (no extra fill), and its depth/stencil surface is
  shared with a 1x scene's depth at the same address (a multisampled image would need per-sample depth AND stencil
  repacking, i.e. VK_EXT_shader_stencil_export, not available on many mobile drivers).
- `VITASHARP_MSAA=ss` = old 2x supersampling (also the automatic fallback when a format/sample count is not
  multisampleable), `VITASHARP_MSAA=off` = one sample.
- `VulkanRenderer.GpuTime.cs`: timestamp queries around each submitted command buffer, `PERF gpu:` line in report.

## Input attachments (done, commit 6a28046)

Multisampled scenes render in classic render passes (`MsaaRenderPass`, one subpass: samples = colour + input
attachment, by-region self-dependency; depth; optional resolve). Pipelines with Samples > 1 are created against
`MsaaCompatPass(format, samples)` (single subpass: resolve/load/store do not affect compatibility). Fetch shaders get a
variant (`ShaderHints.FetchInputAttachment`, `subpassLoad(last_frag_in, gl_SampleID)`, set 3 binding 2) and
`InputFetchBarrier` before the draw. Raw integer renderings and `VITASHARP_MSAA_FETCH=copy` use the copy path.
VK_KHR_dynamic_rendering_local_read was not used: not exposed by the dev drivers (GTX 1650 546.17, Intel 101.4502).

## Status (2026-10-22, done; branch not merged, not pushed)

Commits: cef37ba (real MSAA, copy fetch), 6a28046 (input attachments), 9f10d00 (resolve skipped on breaks that do not
read the surface: `PauseRendering(reads)`), 193d274 (input-attachment descriptor set reused within a frame), merged
with main 3fa9cd8. Fast unit tests 335/335.

Measurements (GTX 1650, warm caches, GPU shared with other agents: GPU worker CPU varies +-1.5 ms between runs):

| run | GPU ms/frame | GPU worker ms/frame | fps |
|---|---|---|---|
| kz_skin main (2d4c5c3) | n/a | 6.14 | 27.4 |
| kz_skin VITASHARP_MSAA=ss | 5.66 | 5.11 | 28.3 |
| kz_skin native (9f10d00) | 4.78 (3.98 in an earlier run) | 4.87 | 27.9 |
| kzl_walk3 VITASHARP_MSAA=ss | 8.49 | 13.68 | 26.6 |
| kzl_walk3 native (193d274) | 5.34 | 10.38 | 27.2 |

The "GPU worker 4.4 -> 6.7 ms" increase seen at the checkpoint was run-to-run noise: in back-to-back runs native is
below ss in both scenarios. Fps is CPU-bound (~27-28). Saved frames: .vitadata/tests/msaa_frames.

Validation (frames compared by eye against earlier runs): kz_skin and kzl_walk3 OK (text smooth, gun/arms dark and
textured, light shafts, no quarter-screen artifact; METRICS black/flicker only at boot, cutscene explosions and scene
cuts); uc_walk identical to the 00:47 main run (non-downscale MSAA path unchanged); ucx_title, f1_trees_lod, dd_boot,
mania_boot metrics match earlier runs; Resistance with tap@f1900:0.28:0.24 reaches the in-game axe/door scene with
normal colours (rbs_boot as committed no longer reaches gameplay because a save exists).

## Possible follow-ups

- VK_KHR_dynamic_rendering_local_read would allow the same input-attachment fetch in 1x scenes (their copies are still
  ~100% of the target per programmable-blending draw); not exposed by the dev drivers.
- FORCE_STORE of 2X depth, of stencil, or read through the CDRAM mirror is warned, not unpacked.
- Programmable blending reads the framebuffer as of the start of the draw (copy, or input attachment behind a
  barrier). With input attachments, triangle-list draws of <= 256 triangles are drawn one triangle at a time with a
  barrier in between, so overlapping primitives of one draw see each other (LittleBigPlanet's text glyph quads). The
  copy path (1x and supersampled scenes) does not split: a copy per triangle cost Uncharted 6.7 -> 32 ms of GPU time
  per frame. Larger or copied self-overlapping draws would need VK_EXT_fragment_shader_interlock (Vita3K's approach). `VITASHARP_FETCH_SPLIT=0` disables the split.
- MoltenVK: render passes with input attachments, secondary command buffers, sampleRateShading and resolve
  attachments are supported features of MoltenVK; not tested on macOS.

## Testing

Scripts in the session scratchpad `msaa/` (`run.sh <bin> <prefix> <scenario> [ENV=..]`, `mkscen.sh`).