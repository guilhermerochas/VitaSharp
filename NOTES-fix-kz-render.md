# NOTES fix/kz-render (Killzone Mercenary rendering), 2026-10-19

## Fixed (commits e0d03dd, 2221c9d)

Root cause of the glowing white/pink weapon and white-blob arms: the viewmodel shaders add an environment
reflection (x6) sampled from texture 0x66362000. That texture is a 384x64, 5-level linear texture whose level 0 is
rows 128-191 of render target 0x66332000. Four emulation gaps made the reflection far too bright:

1. LOD min ignored for render-target-aliasing textures. The gun and sight use LOD min 12, i.e. the last mip; we
   sampled the full-resolution surface. Fixed by `BaseLevelView` (VulkanRenderer.Textures.cs).
2. Mip chain missing. A mipmapped texture over a surface now gets an image whose level 0 is the surface and whose
   other levels come from memory (VulkanRenderer.RtMips.cs).
3. Wrong level spacing. Mipmapped linear/tiled levels are reserved at power-of-two size, as in Vita3K's
   texture/cache.cpp. The game fills the env mips with sceGxmTransferDownscale at 0x66382000, 0x6638A000,
   0x6638C000 and 0x6638C800, which matches that layout. Our packed layout read zeros at 0x6637A000.
4. Feedback read inside a scene. The env pass clears the target, then blends last frame's map from the same
   surface. A TBDR chip cannot see in-tile content, so that read returns memory, i.e. the scene-start content. We
   returned the cleared white.
   - Now, a draw that samples the surface it renders, overlapping its own clip area, reads a copy taken at
     BeginScene and refreshed on sceGxmMidSceneFlush (VulkanRenderer.SceneStart.cs).
   - Reads of other regions still see live data. This matters because the 29-pass post atlas 0x66272000 is
     ONE scene with no flush, and making every self-read stale broke it: rainbow blocks, exploded characters and
     streaks.

Also: the stair "marble swirl" reflections are gone. They came from level 0 being used where the hardware samples
the downscaled mips.

## Debug tools added
- `VITASHARP_TRACE_SNAP=id,id|rt:ADDR` writes snapshots of the colour target (plus alpha) before draws of the traced
  frame to `<shadercache>/trace/targets/snap_*`.
- `[scene] begin` and `[scene] mid-scene flush` lines are logged around the traced frame.
- The sceGxmTransferDownscale call is logged once at debug level.

## Ruled out
- V16NMAD dot with lane 4 = 1.0 (arms fs 8DF6CB, instruction 19). It decodes to src1 swizzle XYZ1 and src2 swizzle
  XYZ1, the same as in Vita3K. It is the game's R.V+1 specular term, not a bug.
- Grading LUT indexing (composite 73F33F57 instructions 43-49) is correct.
- S8S8S8 alpha and the swizzle tables match Vita3K.
- The magenta/yellow 60x34 atlas blocks are a depth-of-field CoC map, which is legitimate.
- The purple tints are lens-flare ghost tint constants (fs 479CA35C uniforms), not a decode error.
- GPU stores (fs 5BE02575, "buffer store unsupported") target 0x65200000. The CPU never reads that range in
  gameplay (checked with --watch-read), so it is not the exposure source.

## Remaining
- After spawn and respawn, the first ~50 frames facing the sun are very bright, white-purple. This may be the game's
  spawn flash or exposure adaptation; it is unverified without a hardware reference.
- Lens-flare ghosts tint dark foreground objects slightly purple when looking at the sun. The intensity is
  unverified.
- Runs are non-deterministic and the player often dies (the physics/limbo bug handled by the other agent). Run tests
  one at a time: parallel instances and other agents' runs share the GTX 1650.

## Scenarios
- kzr_walk: walks into gameplay, uses a private shader cache, frames to 4400.
- kzr_idle: no movement, trace at 4280.
- kzr_trace: trace at 4300.
