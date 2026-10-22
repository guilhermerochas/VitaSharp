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

## Input attachments (done, commit 6a28046)

Multisampled scenes render in classic render passes (`MsaaRenderPass`, one subpass: samples = colour + input
attachment, by-region self-dependency; depth; optional resolve). Pipelines with Samples > 1 are created against
`MsaaCompatPass(format, samples)` (single subpass: resolve/load/store do not affect compatibility). Fetch shaders get a
variant (`ShaderHints.FetchInputAttachment`, `subpassLoad(last_frag_in, gl_SampleID)`, set 3 binding 2) and
`InputFetchBarrier` before the draw. Raw integer renderings and `VITASHARP_MSAA_FETCH=copy` use the copy path.
VK_KHR_dynamic_rendering_local_read was not used: not exposed by the dev drivers (GTX 1650 546.17, Intel 101.4502).

## Status (checkpoint 2026-10-22 ~02:55)

Commits: cef37ba (real MSAA, copy fetch), 6a28046 (input attachments). WIP commit on top: `PauseRendering(reads)` —
renderings broken for texture uploads / reads of OTHER surfaces skip the resolve in MSAA scenes (Killzone: ~6
renderings per MSAA scene, mostly ResolveTexture/RenderTargetRegion/RenderTargetMipmapped/UploadTexture breaks).
WIP compiles (0 warnings) but is UNTESTED.

Measured (kz_skin, GTX 1650, warm cache, other agents sharing the GPU so noisy):
- main (cold cache): 24.1 fps, GPU worker 11.1 ms/frame (cold compile).
- VITASHARP_MSAA=ss (old path, same build): GPU 6.80 ms/frame, GPU worker 4.39 ms, 28.8 fps.
- native + copy fetch (cef37ba): GPU 6.56 ms, worker 6.47 ms, 27.3 fps.
- native + input attachments (6a28046): GPU 3.98 ms, worker 6.69 ms, 27.8 fps. Frames identical to ss/main by eye
  (text smooth, gun dark/textured, light shafts OK, no artifacts). Frames: .vitadata/tests/n_kz_skin.
- ucx_title (no MSAA) and rbs_boot (only MSAA without downscale) unaffected. rbs_boot no longer reaches gameplay
  because a save now exists ("Continue" menu): tap@f1900 hits Intel. Use tap@f1900:0.28:0.24 (Continue) instead.

## Next steps

1. Test the WIP commit on kz_skin (renderings/resolves count in `PERF gpu` line should drop; frames unchanged).
2. Validation one game at a time: kzl_walk3, uc_walk, ucx_title, rbs (Continue variant), f1_trees_lod, dd_boot,
   mania_boot; fast unit tests `--filter Category!=Slow`.
3. Re-measure main with warm cache (bin_base, sc_b_kz_skin) for a fair before number.
4. GPU worker CPU ms/frame rose (~4.4 -> 6.7): secondary command buffers + per-draw fetch set; look at it.
5. Remove the WIP prefix (squash or new commit message) once tested. Do not merge/push.

## Testing

Scripts in the session scratchpad `msaa/` (`run.sh <bin> <prefix> <scenario> [ENV=..]`, `mkscen.sh`).
Outputs: <data>/tests/<prefix>_<scenario>; delete test dirs (n_*, s_*, b_*, g_*) when done except cited frames.
