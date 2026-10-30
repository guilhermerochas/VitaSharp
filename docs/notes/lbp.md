# LittleBigPlanet PS Vita (PCSA00017): textures, text, audio, performance

User report (2026-10-30): "as texturas e o audio do lbp estão estranhas", plus "a performance está um pouco afetada".
Evidence: F10 captures `PCSA00017_20261030_000117` (dialog text) and `_000148` (level-complete scoreboard).

## Fixed

### Garbled planet view / scoreboard background (regression of 26d9f6f)

**Symptom.** Posterised purple/green panels appear over the planet view. The level-complete scoreboard renders the
previous scene as its blurred background, so it bakes the panels in.

**How it was found.**
- A GPU dump of the transition to the planet view (`--gpu-dump-at=1300:1340`, scenario: tap the planet at f1300 with
  the user's save) was replayed on several builds:
  - 22e1797, ddfe7d6 and 37931b8: broken;
  - dcf2208, before the fix/tearaway merge: correct.
- `--draw-limit` bisecting points at draws 28-62 of the main scene. These are native-colour UI programs
  (fs 57824E964A7FD2D2) that declare a `uchar4` output, were created with output register format 5 (HALF4), and end
  with VPCK F16->U8 + SOP2 blend on the framebuffer colour + VBW.
- Since 26d9f6f ("Tearaway colours: native-colour output registers"), `OutputType` returned F16 for HALF4. The
  framebuffer colour was therefore preloaded as halves while the SOP2 blend read it as bytes.

**Fix** (`ShaderGenerator.OutputType`). When the program declares a byte colour (U8/S8), a wider output register
format (HALF4/HALF2/FLOAT2/FLOAT) does not override it. UCHAR4/CHAR4/USHORT2/SHORT2 still win, which is the
Tearaway case. Main's flag-0x4 rule (`FetchType`: programs with `IsFragColorDeclared` fetch the framebuffer colour
in their declared type, Tearaway's F16 cross-fade/sky) still applies on top. GeneratorVersion 18.

**Other leads from the WIP notes.**
- The U16 texture 0x62C9F504 reinterpreting the RGBA8 768x1024 target (post-processing atlas, depth-of-field) is a
  real overlap of the R16 depth atlas and the RGBA post atlas, but it is not what garbled the image. It is still
  logged.
- Format 0x09000000 is U16.

### Broken glyphs ("d€feat", "cf", "STCRY")

**Symptom.** LBP draws a text string as one draw of SDF glyph quads. The fragment program blends the glyph over the
framebuffer colour (programmable blending), and each quad's padding overlaps its neighbours.

**Cause.** In one-sample scenes the shader read a copy of the surface taken before the draw. Every later glyph
therefore blended the old background over the right side of the previous glyph. Multisampled scenes already render
small draws one triangle at a time with input attachments; one-sample scenes had no equivalent ("copy path split
not done").

**Fix** (`VulkanRenderer.SingleFetch.cs`).
- Programmable-blending draws of at most 256 pieces (`Core/Gpu/FetchSplit.cs`, shared with the MSAA split) in one-sample scenes end the open rendering (recorded in a
  secondary and begun in the primary, VulkanRenderer.SceneBreaks.cs) and render in a one-subpass render pass of their
  own on the surface, recorded in the primary after the ended renderings.
- That render pass uses the same MSAA render-pass builder with one sample, plus a self-dependency.
- The surface is the input attachment. A new `ShaderHints.FetchInputSingleSample` reads it with a plain
  `subpassInput`; the recipe flag is bit 2.
- The draw renders piece by piece with a by-region barrier in between: one triangle per piece in lists, two in
  strips (from an even index), pieces of only degenerate triangles left out.
- Render targets get INPUT_ATTACHMENT usage.
- Not used with supersampling, the tile-word attachment, or tile-based (mobile) renderings. Disabled together with
  the MSAA split by `VITASHARP_FETCH_SPLIT=0`.

## Not fixed: audio

Measured with `--audio-dump` and `wavstat`. Runs:
- `lbp_a`: boot to the pod;
- `lbp_e`/`lbp_nav`: tap the planet, then the level map;
- `lbp_l`: IL backend;
- 22e1797.

**Results.**
- FMOD (NGS output: one PCM player voice looping a 4096-byte ring at 48 kHz, mixed by the "FMOD mixer thread")
  outputs one sound at about 30-44 s, then silence. That is 80-90% of the session, including the taps on the planet
  and the level map.
- The same happens on 22e1797 and with `--cpu=il`. It is not a recent regression and not the native JIT.

**What is ruled out.**
- ATRAC9 decoding works: 4629 decodes, non-zero PCM.
- The NGS player advances: its state cycles through the ring.
- The port pacing is right: 187.5 outputs/s.
- Port handles do not matter: FMOD opens port 1, the movie player opens port 2.

**What remains.**
- FMOD's own ring buffer (peeked at 0x8B6FC928) is all zeros: FMOD mixes silence.
- Meanwhile FMOD keeps reloading the same short ATRAC9 sound every ~2 frames:
  1. the nonblocking thread creates 3 decoders, decodes 1 superframe each, and deletes them;
  2. it recreates them and decodes 19 superframes;
  3. the main thread releases them.
- The release comes from game code via FMOD:
  ```
  814A5549 <- 814A57B0 <- 814A98E6 <- 814B05FE <- 814B16C0 <- 814A98E6 <- 813B8136 <- 813B68E8 <- 81406380 <- 813B6AB8 <- 814FCBDA
  ```
  (`--bt=sceAudiodecDeleteDecoder`).
- Next step: disassemble 813B8136/813B68E8 to see why the game releases the sound (a failed open state, a channel
  that ends at once, or a NaN volume). Alternatively, lockstep the FMOD mixer thread against Unicorn.

## Performance

**Measurement.** Pod scenario, 1500 frames, warm shader cache, no debug mode, affinity 4 CPUs, BelowNormal.

| build / switch | cpu ms/frame | user s | kernel s | fps |
|---|---|---|---|---|
| this branch | 26.8 | 34.5 | 5.8 | 25.6 |
| main 653299b | 26.6 | 30.0 | 9.9 | 25.6 |
| 22e1797 (before address spaces) | 25.9 | 22.4 | 16.5 | 25.9 |
| 75b3875 | 24.9 | 21.1 | 16.2 | 25.8 |
| this branch, `VITASHARP_SHARED_ADDRESS_SPACE=1` | 25.0 | 21.3 | 16.3 | 25.6 |
| main, `VITASHARP_SHARED_ADDRESS_SPACE=1` | 24.2 | 20.2 | 16.1 | 25.6 |
| this branch, `VITASHARP_HOST_PRIO=0` | 26.2 | 22.4 | 16.9 | 25.6 |

**Findings.**
- Per-process address spaces cost about 1.5-2 ms/frame (+7%) of CPU in LBP.
- Kernel time did not grow. With the default host priority it shrinks, and the work shows up as user time instead.
- The frame rate is unchanged.
- The 46% kernel share in the user's session (81 ms/frame) came from a windowed, debug-mode run with OpenAL. It does
  not reproduce headless.
