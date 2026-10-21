# Killzone Mercenary (PCSC00045) limbo bug: resolved (branch fix/kz-limbo)

## Root cause
VRSQRTE.F32 (Advanced SIMD reciprocal square root estimate, Cpu/Neon.Common.cs RSqrtEstimate) used the exponent
parity the wrong way round: an even exponent must scale the operand into [0.5, 1.0) and an odd one into [0.25, 0.5).
Every estimate was off by a factor of about sqrt(2), e.g. VRSQRTE(1.0) gave 0.705 instead of 0.998.
Havok normalises vectors as VRSQRTE followed by two VRSQRTS Newton steps, which do not fully correct a sqrt(2) error,
so "unit" vectors were 2 to 12 % off. Collision normals and plane distances in the character controller were wrong:
the player was never stably supported (vertical speed kept building up and resetting), sank a little more on each
time-of-impact sub-step and finally fell through the stairs. The game then triggered FellThroughWorld/SUICIDE.

Why the timing experiments looked related: the Havok step loop at 0x81726058 sub-steps the remaining time after
a time-of-impact (TOI) while less than 1.5 ms has elapsed. Faking a 4x faster Havok clock (VITASHARP_TSCALE_PROC=4 with
LR=818BD671), or a slow host (heavy tracing), stopped it after the first sub-step. That removed the extra downward
motion of the second sub-step, so the broken support was survivable. Timing was never the cause. Measured with the
cycle ledger, the first sub-step is about 36K instructions and 110K weighted cycles, touching only about 330 cache
lines, so the hardware also runs the second sub-step.

## How it was found
- tests: FuzzSimdAgainstUnicorn targets VFP/NEON with special operands. Purely random encodings almost never hit
  VRSQRTE, so it also generates the estimate and step instructions directly.
- Lockstep tool (src/VitaSharp.Desktop/Lockstep.cs). Build it with `dotnet build src/VitaSharp.Desktop -c Release
  -p:Lockstep=true -o <dir>` and run with `VITASHARP_LOCKSTEP=81726058:60000:4000`. It runs every instruction of the
  thread on Unicorn and on our interpreter and logs each mismatch with its full input state. It reported
  VRSQRTE mismatches in the first few blocks. CET must be off for Unicorn: the opt-in build property does that.

## Other VFP fixes found by the fuzz (default FPSCR: FZ + DN)
- VNMUL negates after default-NaN substitution (its NaN result is 0xFFC00000).
- VPMAX/VPMIN/VMAX/VMIN return the default NaN.
- VCVTB/VCVTT half conversions honour FZ (input) and DN.
- VCVT float to fixed-point sign- or zero-extends the result to the register: 32 bits, or 64 for a D register.

## Evidence
- kzl_idle2 (10500 frames, idle): gameplay from about f4200, no black frames or SUICIDE through f10500 (6300+ frames).
  The player stays standing in the same spot. Before the fix, kz_idle died every about 220 frames.
- kzl_walk3 (10500 frames, walking back and forth, strafing and turning from f4300): no deaths.
- kzl_walk2 (long scripted walk): one SUICIDE at f5520, after strafing out of the building onto the exterior
  structure (frames 5310-5400 show the outside). Probably a real fall off the level. No other deaths.

## After merging main (b13f95c)
- main got the same VRSQRTE fix independently (fix/uc-anim, 09d2cff) and the ledger-based guest clock (perf/mania).
  Merged: identical code, the fuzz generators were combined into one FuzzSimdAgainstUnicorn (ARM + Thumb, special
  operands, estimate/step, shift/immediate and two-register-misc encodings).
- main at ac66d9b (ledger guest clock, *without* the VRSQRTE fix): kzl_idle still died every ~250 frames
  (black at 4150, 4360, 4590, 5150, ...). So the clock change alone does not fix limbo, and no further clock or
  pacing change is needed on this branch.
- Merged branch: kzl_idle2 and kzl_walk3 (each 10500 frames, gameplay from about f4200) have no deaths. dotnet test:
  137/137 tests pass. The uc_game, dd_boot, f1_trees_lod and gow_play regression runs were all OK. They ran on a scratch copy of the
  data root (junctions to the app folders, copied savedata), so nothing was written under AppData\Roaming\VitaSharp\fs.

## Not done / remaining
- The generic "guest clock follows the CPU ledger" change was implemented and tested (it did not fix the bug by itself),
  then dropped as unnecessary. Patch kept in the session scratchpad (guestclock-ledger.patch).
- The VFP rounding modes other than nearest, and NaN propagation with DN=0, are not modelled (manual
  FuzzSimdAgainstUnicorn anyFpscr=true rows show the differences). Vita games run with FPSCR 0x03000000.
