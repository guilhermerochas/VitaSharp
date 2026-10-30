# Unit 13 (PCSF00034): scrambled wall posters (WIP)

Status: paused, investigation only, no code changes yet.

## Evidence so far (F10 capture PCSF00034_20261030_024002, guest frame 2658)

- Only UBC1 (0x85000000) and UBC3 (0x87000000) textures are sampled in this frame, plus one UBC2 seen earlier in the log.
  All of them are swizzled (type 0) except two UBC1 cube maps. No PVRTC, palette or ETC textures.
- Every texture in `textures/` is decoded on the CPU from the current guest memory (`DecodeForInspection`), and all
  221 dumps look correct, including the poster candidates. The noise metric shows no broken texture.
- The posters are already wrong in the G-buffer albedo (`targets/rt_60B9FE00_720x408_00000000.png`): purple, green
  and red 4x4 block noise with recognisable shapes (skulls, faces, "PERSON... WILL" text). None of the dumped textures
  shows that image.
- The candidate poster draws are G-buffer fs `A7094EDC5458B2B5` (t0 = albedo UBC3, t1 = normal UBC1, t2 = UBC1/UBC3
  mask used only for alpha-test KILL at 0.1). It writes RGBA8 albedo from t0 and S8x4 normal. Seen pairs are
  t0=0x626E56C0/t2=0x635330C0 and t0=0x63B1F840/t2=0x632EEDC0. This is not yet confirmed per pixel.
- Working hypothesis: the GPU-side texture differs from guest memory. Either the cached image is stale (a write the
  dirty-page tracker missed, such as streamed texture data) or the uploaded mip levels are wrong. A CPU decoder bug is
  unlikely because the CPU dumps are clean. The capture's mip diffs are small, which argues against bad guest mips.

## Next steps

1. Write `tests/scenarios/unit13_boot.txt`. The game reaches the first mission's loading at about f490 with an empty
   savedata and gameplay at about f830, so it may need no input or only cross presses. The posters are in view at
   about f2650.
2. Run once with `VITASHARP_TEXDIRTY=verify` (it logs `texdirty verify` misses) and once with `VITASHARP_TEXDIRTY=0`.
   If the posters are fixed with 0, find the write path that does not mark pages (IO reads, transfers, HLE memcpy).
3. Otherwise, take a GPU dump (`--gpu-dump-at`) and use `replay-gpu --draw-limit/--trace-draws` to find the poster
   draw, then compare the GPU upload (`UploadTexture`, mip offsets in `ComputeLayout`) with the CPU decode.

## Scratch (agent session scratchpad)

- `u13_data`: data root. `fs/ux0/app/PCSF00034` is a junction (remove it with rmdir first), plus copied license and
  (empty) savedata.
- `u13_bin`: Release build of 653299b.
- `u13_sheets`: contact sheets of the capture's textures.
