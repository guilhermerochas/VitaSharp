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
  as on hardware without FORCE_STORE). FORCE_STORE + read is warned, not implemented (would need per-sample unpack).
- MSAA without downscale (Uncharted's 360x204 4X pass onto 720x408) keeps the 2x path: the surface already stores
  one pixel per sample, so rendering at 2x costs exactly the samples (no extra fill), and its depth/stencil surface is
  shared with a 1x scene's depth at the same address (a multisampled image would need per-sample depth AND stencil
  repacking, i.e. VK_EXT_shader_stencil_export, not available on many mobile drivers).
- `VITASHARP_MSAA=ss` = old 2x supersampling (also the automatic fallback when a format/sample count is not
  multisampleable), `VITASHARP_MSAA=off` = one sample.
- `VulkanRenderer.GpuTime.cs`: timestamp queries around each submitted command buffer, `PERF gpu:` line in report.

## Input attachments (not done)

Framebuffer fetch via input attachments needs either classic render passes (renderer uses dynamic rendering) or
VK_KHR_dynamic_rendering_local_read. The dev machine (GTX 1650, driver 546.17; Intel UHD 101.4502) exposes neither
local_read, so such a path could not be tested; the copy path is used everywhere.

## Testing

Scripts in the session scratchpad `msaa/` (`run.sh <bin> <prefix> <scenario> [ENV=..]`, `mkscen.sh`).
