# NOTES fix/kz-render (Killzone Mercenary rendering), checkpoint 2026-10-19 ~03:20

No code changes yet. This commit only adds the trace scenario tests/scenarios/kzr_trace.txt and these notes.

## Current hypothesis
The glowing white/pink viewmodel (gun and arms) comes from the per-pixel lighting maths in the viewmodel
fragment shaders, not from the albedo. The prime suspect has shifted to a USSE translation detail:
- In arms fs 8DF6CBEA602F13BD (GLSL: .vitadata/shadercache/trace/8df6cbea602f13bd.frag), instruction 19,
  `V16NMAD 10C4F106A0047042`, is translated as
  `dot(vec4(R.xyz, 1.0), vec4(V.xyz, 1.0))`, which is R.V **+ 1**. The other dot3 instructions in the same
  shader (8, 10, 11) use `vec4(x, 0.0)` / `vec4(y, 1.0)`, so the w term is 0. Instruction 19 looks like the
  specular R.V term, and if it gets an extra +1 the specular would blow up.
  Check how UsseTranslator.Alu.cs (around lines 131-160, V16NMAD dot path) builds the 4th lane and
  compare with Vita3K's USSE translator (scratchpad ref/vita3k/vita3k/shader/src/translator/...).
- Second suspect: the PVRTC-II (fmt 0x83000000) normal maps that all three glowing draws use.
  The arms use t5=0x63AB1F10, the gun t1=0x63991DA0 and the sight t1=0x63951D60.
  The 32x32 resident dump of 0x63AB1F10 looks like a plausible normal map, so the decoder is probably OK,
  but it is not proven.
- Third: all viewmodel textures are 512x512 with lodMin 4, so only 32x32 is resident (streaming never
  raises lodMin?). That gives a blurry result, not a glow, but it should still be checked.

## Verified
- The draw lines (from .vitadata/tests/kz_hash/log.txt, frame trace) are:
  - arms, fs 8DF6CB: t0 UBC1 albedo, t1 UBC3 64x64 (uv1), t2 UBC3 512 (gamma, uv0, spec/mask in .zw),
    t3 UBC3 64x64 (uv1), t4 = 0x8BA25CC0 S8S8S8 16x16 cube (fmt 0x99001000, a normalisation cube, L dir
    from v_TexCoord2), t5 = PVRTII4BPP normal map sampled at uv0. The arms do **not** sample 0x66362000.
  - gun body, fs 4B6E2E, and sight, fs EC1DE5: t2 = the same cube, t3 = an F16x4 64x1 LUT,
    t5 = 0x66362000 U8U8U8U8 linear 384x64 gamma1.
- In the arms shader, ftex5 is a tangent-space normal map. Instruction 3 is `n = ftex5 + r0*k`,
  instruction 4 is `n*2-1`, followed by normalize, then N.L with the cube sample.
  The specular power is gloss*100+4 (ftex2.x * v_TexCoord4.w), and spec = exp2(-pow*|R.V+..|).
- RT 0x66332000 (384x192 RGBA8) is the first target each frame. It has a full-screen draw, a draw that
  samples its own rows 128-191 (0x66362000), 3132 tris in rows 128-191, and depth-only draws at
  [256,0-383,63]. The dump shows flat purple in rows 0-127 and flat pink (0.83,0.16,0.43) in rows 128-191.
  This is what gun/sight ftex5 reads. It still needs checking whether this flat content is correct.
- The S8S8S8 alpha is already 0x7F in TextureDecoder (line 75). It is also irrelevant because the swizzle
  forces A=1.
- The swizzle1/swizzle3 tables match Vita3K (gxm_to_vulkan.cpp translate_swizzle*).

## Ruled out
- Albedo textures (VITASHARP_DEBUG_TEX0=1 shows a correct gun and arms).
- S8S8S8 alpha (see above).
- The arms glow cannot come from 0x66362000, since the arms draw does not bind it.

## Exact next steps
1. Read the result of the trace run kzr_trace (frame 4300): .vitadata/tests/kzr_trace/log.txt
   (grep `fs=8DF6CB|fs=4B6E2E|fs=EC1DE5|rt=0x66332000`) and the scratchpad sc1/trace/
   {targets,textures} plus *.frag for 4b6e2e / ec1de5.
2. Decode V16NMAD 10C4F106A0047042 by hand (UsseTranslator.Alu.cs dot path) against Vita3K and fix the
   lane-4 handling if it is wrong. Bump ShaderGenerator.GeneratorVersion.
3. Bisect with VITASHARP_DEBUG_FS=8DF6CBEA602F13BD:vec4(<term>) on t-locals, for example
   `vec4(t86*0.25)` (R.V) or `vec4(t99,t101,0,1)` (the exp2 terms), and read rt_65C78000 in the trace.
4. Do the same for the gun 4B6E2E and the sight EC1DE5. Check whether their ftex5 (RT 0x66332000
   rows 128-191) should be flat pink. Look at what draw id=3 (3132 tris, fs 46FCA5DDF927069E) renders.
5. Then look at the purple haze in the post chain (atlas 0x66272000, bloom/DoF blurs).
6. Regression scenarios: f1_trees_lod, dd_boot, gow_play, uc_game (the uc_game sheets must still show
   textured gameplay).

## Commands
- Build:
  `dotnet build src/VitaSharp.Desktop -c Release -o C:\Users\guisa\AppData\Local\Temp\claude\C--Users-guisa\2fba547d-1767-41f4-bd6c-4baac31f8ab5\scratchpad\kzbin`
- Run: `kzbin\VitaSharp.Desktop.exe test tests\scenarios\kzr_trace.txt` (about 3 min).
  It uses `--shader-cache=<scratchpad>\sc1`, so the trace output stays private and does not clash with
  other agents.
- Vita3K source: scratchpad\ref\vita3k.
