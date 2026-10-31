# Zero Escape: Zero Time Dilemma (PCSE00781)

Does not boot on Vita3K (`sceGxmColorSurfaceInit returned SCE_GXM_ERROR_INVALID_POINTER`).

## Boot crash (fixed, branch fix/ztd, a662f3b)

`main_th` hit `BKPT #0` at vita_game+4376 right after the first CPU framebuffer was set. With `--trace-hle` the
last call before it was `sceGxmColorSurfaceInit(81621EF0, ...) -> 0x805B0004` (INVALID_POINTER).

The caller (vita_game+4294) builds a colour surface that matches a texture: GetFormat/Width/Height/Type, a switch on
the type (linear strided `0xC0000000` -> surface type linear), the stride from `sceGxmTextureGetStride` divided by
the bytes per pixel, and then `sceGxmColorSurfaceInit(surface, fmt, 0, 0, ors, 320, 204, 512, data)` where `data`
is register r8, set to the constant 0 a few instructions earlier. Any error goes to the assert. The game ships
and runs on hardware, so the firmware accepts `data == NULL`; only the surface pointer is required (the memory can
be attached later with `sceGxmColorSurfaceSetData`). VitaSharp and Vita3K both refused NULL data.

Fix: `Gxm.sceGxmColorSurfaceInit` checks only `surface`. Unit test `GxmHleTests.ColorSurfaceInitAcceptsNullData`;
scenario `tests/scenarios/ztd_boot.txt`.

## How far it gets (31/10/2026)

Aksys and Spike Chunsoft logos, title menu at 60 fps, New Game (Cross at f1000), the opening text, "Now Loading",
the Nevada Desert intro and the story cutscenes (prison cells, Zero in the plague-doctor mask, the coin-toss scene)
at ~30 fps up to frame ~9500 with Cross pressed every 250 frames. Rendering looks right. Not seen yet: the first
decision screen and the escape (puzzle room) gameplay. The run at frame 9300 showed a black frame (one dump only,
probably a transition); a longer run was stopped at end of day.

## Open

- 40 of the game's 105 fragment shaders read global register G16 (special bank, bit 0x40). The USSE translator
  does not know it and reads 0 (`load from unsupported bank Global register 16`, from `shadertest PCSE00781`).
  Vita3K does not handle the GLOBAL bank either. No visible error so far; find out what G16 holds.
- Around frame 6200: `scene without colour surface`, and a 1024x1024 render target sampled while it is rendered.
- FPS dips to 19-23 for a few seconds during cutscenes (that run overlapped with the unit tests, so recheck alone).

## Next steps

1. Run `ztd_boot` plus a longer input script (Cross every 250 frames up to ~f12000) and dump every 60 frames
   after f9000 to see the first decision screen and the first escape room.
2. Look into G16 (the shaders using it; compare a frame with G16 = 0 against an expected value).
