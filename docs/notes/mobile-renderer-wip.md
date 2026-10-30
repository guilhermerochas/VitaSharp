# Mobile renderer items (branch feat/mobile-renderer): work in progress

Paused 2026-10-30, to resume. Base: main 653299b, GeneratorVersion 16. No code changed yet.

## Done
- Worktree `.claude/worktrees/mobile-renderer` created; read `docs/notes/mobile-gpu.md`, `pipeline-skip.md`,
  `docs/roadmap.md` (Android) and `docs/android/README.md` (device checklist).
- Base Release build of `VitaSharp.Desktop` in the worktree compiles (0 warnings).
- Code survey for item 1 (scene breaks): every break goes through `EndRendering` / `PauseRendering` /
  `SyncRenderTargetForRead` (VulkanRenderer.cs). Main sources in a scene: `UploadTexture` (Textures.cs, calls
  `PauseRendering()` before every upload), `RenderTargetRegion`, `ReinterpretRenderTarget`, RtMips, depth/stencil
  textures (DepthTextures.cs), framebuffer-fetch copies (`BuildFramebufferFetchSet`), mapped reads (Mapped.cs),
  tile-word copies, raw-view switches (Draw.cs:760).
- No GPU dumps (.vsgd) exist on the machine: they have to be recorded first (`--gpu-dump-at=F1:F2:file` in the
  scenario `args=`) with the base build, for KZ (.vitadata), LBP, Tearaway, Silent Hill, Skullgirls (scratch
  junction roots).

## Plan (next)
1. Copy the base build to a scratch dir (`mr_base`), record the five dumps, replay them with the base build to get
   reference frames (default and `--gpu-profile=adreno`, always `--shader-cache <scratch>`); measure renderings per
   scene and load/store from `PERF gpu` on kz_skin, lbp_boot, tearaway_tutorial.
2. Item 1 design: renderings are already deferred into secondaries on tile-based GPUs (`_tileCmd`) and for MSAA
   (`_msCmd`), so the primary has not begun the rendering yet. A hoisted upload/copy can be recorded straight into
   `F.Cmd` (a `_hoisting` flag that makes `Cmd` return the primary) and lands before the rendering begins, without
   ending it. Allowed only when the work does not read the current target (`_rt`, its depth, tile word) and its
   destination image was not sampled by the open rendering (track images bound per rendering serial); otherwise
   strict ordering (break as today). Then defer single-sampled renderings on the desktop too (STORE ops unchanged) so
   the PC benefits, and merge consecutive renderings to the same attachments (several secondaries in one
   begin/end). Add renderings/scene and hoisted-break counts to the PERF gpu line.
3. Item 3: render pass + framebuffer path for single-sampled scenes (reuse MsaaNative render-pass code), selected
   without `VK_KHR_dynamic_rendering` or by a quirk flag (`VITASHARP_GPU_QUIRKS=+NoDynamicRendering`); minimum API
   1.1 + required extensions; compare on the GTX 1650.
4. Item 2: BC1-5/PVRTC to ASTC 4x4 transcoding when `textureCompressionASTC_LDR` exists (Iris Xe), disk cache per
   title in the shader cache folder (content-addressed, size limit, LRU), RGBA8 fallback; PSNR tests with a software
   ASTC decoder; report quality and speed.
5. Docs (`mobile-gpu.md`, roadmap, Android checklist), tests, APK build, cleanup of scratch junctions.

## Measurements so far
None.
