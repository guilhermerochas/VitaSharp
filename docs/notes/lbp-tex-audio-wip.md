LittleBigPlanet (PCSA00017) textures/audio/perf: WIP notes (paused)

Status: investigation started, no code changes yet. Paused on 2026-10-30 at the user's request (JIT and Android come first).

Evidence
- Log: %APPDATA%\VitaSharp\logs\last.log (session 23:59:16, build 4193a9d).
- Capture A: diagnostics\PCSA00017_20261030_000117 (F10, frame 3116). Dialog text has wrong glyphs: "d€feat", "ways cf".
- Capture B: diagnostics\PCSA00017_20261030_000148 (F10, frame 4018, debug mode, has replay/ and drawring/). The level-complete
  scoreboard background is garbled and posterised (purple/green).

Findings so far
- Format 0x09000000 is SCE_GXM_TEXTURE_BASE_FORMAT_U16, swizzle 0.
- Log line at 00:00:23.862: texture 0x62C9F504 384x384 fmt U16 reinterprets render target 0x62C57204 768x1024 U8U8U8U8.
  The texture starts at offset 0x48300 inside that render target.
  Earlier, at 00:00:12.738, a texture 0x62C9F200 1x1 F16 also reinterprets the same render target.
- targets.txt in capture B also lists a 768x768 R16 render target at 0x62C9F204, fmt 0x40800000. It overlaps the
  768x1024 RGBA8 target 0x62C57204.
  - Lead: the U16 texture lies inside the region of two overlapping surfaces, an RGBA8 one and an R16 one.
  - The reinterpretation likely picks the wrong (RGBA8) surface. It may also convert RGBA8 bytes into U16 instead of
    reading the R16 surface or guest memory.
  - Check the overlap and newest-surface choice in ResolveTexture: fix/rt-overlap, plus the per-process space tags
    added in ce4ce5a.
- In the traced frame 4019, the scoreboard background is draw 4 (fs 461E121DB6676F80).
  - It samples t0 = 0x61000000 (720x408 RGBA8, rendered with 4x MSAA and downscale in the same frame) and
    t2 = 0x62F37600 (32x32 U8).
  - 0x61000000 is drawn by draw 1 (fs B3A1942956A8C1A7), which samples t0 = 0x6112B000 (720x408). The garbled image is
    probably already in 0x6112B000 or 0x61000000.
  - The capture's targets/ folder has rt_6112B000_720x408.png and rt_61000000_720x408.png. Find out which producer pass
    writes 0x6112B000, probably the one that samples the U16 texture.
- Audio (audio.txt in capture B):
  - port 1 MAIN grain 256 48 kHz: 187.6 outputs/s, matching the expected rate, with 0 underruns.
  - 15459 of 25421 grains were silent.
  - FMOD over NGS, 40 voices, ATRAC9 decoders.
  - The FMOD nonblocking thread opens and releases an ATRAC9 decoder every ~2 frames (log f3139+). That is constant
    decoder churn; check whether decoder state resets per sound and cuts the start of each sample.
  - Not measured yet. The plan is an --audio-dump run plus wavstat.
- Performance (extra scope from coordinator):
  - PERF cpu 81 ms/frame, with kernel time at 46%.
  - Suspects:
    - the per-process address spaces (ce4ce5a): lazy commit, exception storms, view mapping;
    - dirty-page tracking;
    - host thread priority.
  - Plan: compare the user/kernel split on 4193a9d against 22e1797 and 75b3875, with VITASHARP_SHARED_ADDRESS_SPACE=1 and
    VITASHARP_HOST_PRIO=0.

Next steps
1. Scratch data root lbp_data in the session scratchpad.
   - Junction fs/ux0/app/PCSA00017 to the user's game folder.
   - Copy ux0/license and ux0/user/00/savedata/PCSA00017.
2. Build to scratch with -m:2. Run lbp_boot with --audio-dump, following the CPU rules (affinity 0x0F, BelowNormal, one
   emulator at a time).
3. Reaching the scoreboard headless needs a scenario that finishes "First Lessons In Loco-Motion". Alternatively, record
   a GPU dump (--gpu-dump-at) near the level end and A/B it with replay-gpu.
