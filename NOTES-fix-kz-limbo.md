# Killzone Mercenary (PCSC00045) limbo bug: checkpoint notes (branch fix/kz-limbo)

## Status
NOT fixed. Re-tested on top of main incl. 55e98bb (real LDREX/STREX exclusive monitor): bug unchanged.
kzl_idle (this branch, default 444 MHz pacing, JIT on): gameplay starts ~f4180 on the stairs, "SUICIDE -Y100"
shown at f4280 (identical timing to the old kz_idle run from before 55e98bb). So the exclusive monitor did not fix it.

## What the bug looks like (kz_idle sheets 14/15, .vitadata/tests/kz_idle)
- f4180-4200: player on the stairs (first gameplay frames).
- f4205-4260: camera suddenly outside, high up, looking over skyscrapers and drifting (no input) -> this is most
  likely the FellThroughWorldCamera, i.e. the fall/out-of-world detection already fired around f4200.
- f4270 brief grass scene, f4280 "SUICIDE" banner, f4430 black, respawn f4450; repeats every ~240 frames
  (black frames in kz_idle: 4430, 4660, 4870, 5110, 5370, 5630).
- So the real failure happens within ~20 frames of gameplay start: the character falls through / is outside
  the collision of the stairs. Next investigation should focus on f4180-4210, not the later teleport.

## Hypotheses (ordered)
1. Collision query wrong (Havok char proxy linear cast / MOPP traversal) because of a VFP/NEON edge case
   (FZ before-rounding, NaN propagation, VRECPE/VRSQRTE, VCVT saturation, VMAX/VMIN with NaN, ...).
   Old random fuzz rarely hits NEON in Thumb and almost never uses denormal/NaN/inf operands.
2. Physics sub-stepping: step loop fn 0x81726058 (loop head 0x817262A4) loops while
   remaining dt s16 > 1.19e-7 (0x34000000), steps < [this+0xb8], and lastStepTime [fp+0x1c8] <= budget [fp+0x1c0].
   Scaling only hkSystemClock wrapper 0x818BD664 (LR 0x818BD671) by 1/4 made the bug vanish earlier; note that
   this ALSO shrinks any game dt derived from that clock, so it may act via smaller dt, not via step count.
   Unverified which one.
3. Race between Havok job threads (JobSched_00x, HavokWorkerThread) - exclusive monitor fix did not help.

## Ruled out / evidence
- 55e98bb exclusive monitor: no change (kzl_idle frame 4280 SUICIDE).
- CPU pacing 444/333/200 MHz and --speed=0.8: deaths persist (earlier sessions).
- JIT vs interpreter: JIT (Cpu/Jit.cs) only emits calls to the same handler methods with constant args, so
  semantics are identical to the interpreter; a JIT-only bug is unlikely (kz_nojit scenario exists to confirm).

## Work in this commit
- tests/VitaSharp.Tests/CpuFuzzTests.cs: new FuzzSimdAgainstUnicorn (ARM + Thumb, 40k each) that generates only
  VFP / Advanced SIMD encodings (data processing, core transfers, element load/store) with operands biased to
  denormals, +-0, inf, qNaN/sNaN, near-max, tiny normals, and sometimes FPSCR with FZ/DN off and other RMode.
  NOT yet run to completion (started, interrupted by the shutdown).
- tests/scenarios/kzl_idle.txt: kz_idle to 7000 frames (outputs in .vitadata/tests/kzl_idle).

## Exact next steps
1. `dotnet test tests/VitaSharp.Tests --filter "FullyQualifiedName~FuzzSimd" --logger "console;verbosity=detailed"`
   and fix every handler that disagrees (ignore cases where Unicorn/QEMU itself is known wrong; check ARM ARM).
2. Re-run kzl_idle; count black frames after f4100 in metrics.csv (pt-BR decimals: black = fields 6,7):
   `awk -F, 'NR>1 && $1>4000 {b=($6"."$7)+0; if (b>0.9) print $1}' metrics.csv`.
3. If still failing: find the player transform. Trace f4170-4210 with --dump-mem=4180,4205 + `memdiff` to find
   float3 that jump; or --bt on the FellThroughWorld manager code. Then test hypothesis 2 by forcing one
   step per frame (kz_onestep poke 817262B0:F2802A01) vs scaling dt only.
4. Success = kzl_idle + walking scenario 6000+ gameplay frames with no SUICIDE/black frames.

## Tools
- Build: `dotnet build src/VitaSharp.Desktop -c Release -o <scratchpad>/kzbin`; run `<exe> test tests/scenarios/kzl_idle.txt`.
- Disassembly of the whole eboot: scratchpad dis/kz.asm (Capstone), strings dis/kzstr.txt
  (FellThroughWorld* RTTI names at 0x81966C9C, 0x8196918C, 0x819691AC, 0x819786C8, 0x8198AC04).
- Address 0x81BBC420 (kz_wpos) is a shared scratch vector, NOT the player position.

## USER INPUT (2026-10-19, read first)
The user confirmed that the earlier temporary Killzone-specific hack DID fix the limbo in play:
VITASHARP_TSCALE_PROC=4 VITASHARP_TSCALE_PROC_LR=818BD671, which scales only the process clock as read by the
Havok hkSystemClock wrapper (0x818BD664, called from the step loop 0x81726058 at 0x81726114/0x81726D92).
So the timing theory IS the cause: Havok sub-steps while the measured step time is < 1.5 ms. On the host a step
measures less than on hardware, so it runs 2-3 sub-steps per frame and the player falls through the world.

The user does NOT want anything hardcoded for Killzone (no title/address checks). Required: a GENERIC fix,
with Vita3K as the reference for how guest time is reported. Directions:
- Make the guest clocks (sceKernelGetProcessTimeWide/Low, sceKernelGetSystemTimeWide/Low, sceRtc*Tick, any
  clock the game reads) advance consistently with emulated CPU work instead of raw host wall time, e.g. derived
  from the cycle-paced CPU ledger (CycleCost / --cpu-mhz). That way short guest code sequences measure the
  hardware-like duration. Check Vita3K's implementation (rtc/kernel time, its clock source).
- Verify with kzl_idle (no SUICIDE after frame 4180 for 6000+ frames), and that no other game regresses:
  F1, DD, GoW, Uncharted, timing-sensitive video/audio.
