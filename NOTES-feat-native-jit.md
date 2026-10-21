# feat/native-jit — native x86-64 CPU backend

Branch `feat/native-jit` (from main ff80a08). Not merged, not pushed.

## Status

| Phase | State |
|---|---|
| 1. Pluggable backends (`IBlockCompiler`, chain, `--cpu=`, `VITASHARP_CPU`, launcher setting) | done, commit b3fe8bb |
| 2a. Native x64 backend: integer core, fastmem, linking, indirect-exit table | done, commit 0874fa7 |
| 2b. VFP inline (transfers + scalar arithmetic), tier-up to IL, exact mid-block exits | done, commit d36cd84 |
| 2b'. Dispatch-table race fix, in-game verify mode (VITASHARP_NATIVE_VERIFY), fault reporter, merge of main b1f371a | done, commit 4332e9d |
| Merge of main cf72b18 (quarter-cycle CycleCost, pacing, alloc pooling, Linux/macOS) | done, commit d6cc00d |
| 2c. LDREX/STREX (word) inline | done, commit 56e752a |
| 2d. NEON subset inline (three-same bitwise/int add-sub/f32 add-sub-mul-mla-mls, VDUP scalar, VMOV/VMVN/VORR/VBIC imm, VLD1/VST1 multiple) | done, commits 7e2a31f, 3320ff1 |
| ARM64 emitter | not started (design below) |

Results and measurements: see "Verification" and "Performance" (filled in from the final runs).

## Design

```
Block (Decoder/BlockBuilder, shared)          CpuCore.Run (shared dispatcher, pacing ledger, HLE exits)
   |                                                |  b.NativeEntry -> NativeRuntime.Run (trampoline)
   v                                                |  b.Compiled    -> IL delegate
Jit chain: native > il > interp                     |  else          -> Interpret (+ tiering counters)
   |  (IBlockCompiler.Compile -> CompiledBlock?, null = next backend at its own threshold)
   v
NativeX64Compiler
   ArmFrontend  : DecodedInst -> IR (exact interpreter semantics, callouts for the rest)
   IrBuilder    : guest-state cache (R0-R15, NZCV, Thumb in temps; flushed at regions/callouts/exits)
   IrPasses     : DeadCode (lazy flags: superseded flag computations vanish), Validate (tests)
   X64Emitter   : linear-scan allocation + lowering with Iced; trampoline
   NativeRuntime: W^X arena, callout bridge, link slots, dispatch table, tier-up/retire
```

### Backend chain (phase 1)
- `IBlockCompiler { Name, IsSupported, HotInstructions, Compile(Block) -> CompiledBlock?, Stats }`.
- `Jit` holds the chain (`--cpu=native` = native > il > interp, `il` = il > interp, `interp` = none), the background
  compile workers and the install step. Each block carries `Tier` (next backend) and `HotAt` (that backend's
  threshold), so the hot loop is a field compare. Thresholds: native 2,000 executed instructions, IL 400,000.
- Default: best supported backend (native on x64 without watchpoints, else IL; interpreter under Native AOT if IL
  is unavailable).

### IR (backend neutral)
- 32-bit SSA temps; constants are temps flagged constant (folded in the builder, immediates in the emitter).
- Forward-only control flow: `CondBranch`/`BranchIf` to a later `Label` (conditional instructions, CBZ), every path
  ends in `ExitStatic` (known PC/Thumb: linkable) or `ExitDynamic` (indirect).
- Guest state ops (`GetReg/SetReg/GetFlag/SetFlag/LoadState/StoreState`), ALU incl. ARM flag ops (`AddC`/`SubC`
  with NZCV outputs as ARM AddWithCarry, `TestNZ`, `ShiftReg` with ARM register-shift semantics), 64-bit products and
  adds, ARM-semantics division, memory ops on guest addresses, `Callout`, `FpOp` (VFP scalar on the register file).
- The builder keeps guest registers/flags in temps and only stores them at flush points (before conditional
  regions, callouts that touch core state, exits). Region rules (validated in tests): temps defined inside a region
  are not used after it; after the label the cache keeps only values valid on both paths.

### x64 emitter
- Fixed registers: rbx = `CpuState*`, r15 = guest memory base, rsp = trampoline frame (32-byte shadow + 64 spill
  slots); rax/rcx/rdx scratch. Temps: r8-r11 (+rsi/rdi on System V) and rbp/r12-r14 (+rsi/rdi on Windows).
- Linear scan over the linear IR; temps live across a callout go to callee-saved registers or spill slots; spill
  slots are free for a whole live range (victims are spilled retroactively from their definition).
- Values are kept zero-extended to 64 bits, so `[r15 + reg]` is the guest access (fastmem: GuestMemory is one
  4 GiB+64 KiB host reservation with a lazy-commit vectored handler). Address arithmetic is a 32-bit `lea`, so it
  wraps modulo 2^32 exactly like the interpreter.
- Windows x64 (rcx/rdx/r8/r9 + shadow space) and System V (rdi/rsi/rdx/rcx) for the trampoline and callouts.
- Flags: x64 flags map to ARM (N=SF, Z=ZF, V=OF, C=CF for add, !CF for subtract; adc/sbb with the carry loaded by
  bt/cmp). Only flag temps still live after dead-code elimination get a setcc.

### Unsupported instructions: callouts (the chosen option)
Every block compiles. An instruction without an inline translation calls its interpreter handler through an
`[UnmanagedCallersOnly]` bridge (state flushed first, cache invalidated after; SIMD-only handlers keep the core
register cache). This keeps semantics identical by construction and lets coverage grow op by op. A callout only
aborts the block if the handler throws (the exception is rethrown by the dispatcher); an exit a handler raises
mid-block (e.g. UDF from an undefined VFP encoding) lets the block finish like the interpreter, and the exits of
blocks containing callouts do not chain while `Exit` is set. Blocks dominated by NEON callouts get an execution
budget and are recompiled by the IL backend when it runs out (tier-up; native code retired and unlinked).

### Shared infrastructure, unchanged semantics
- Cycle accounting: each exit adds the block's `Cycles` to `CpuState.CycleAcc` and counts blocks/instructions;
  chained execution returns to the dispatcher when `CycleAcc` reaches the pacing window (same check point as the
  interpreter loop), when `LinkBreak` is set (InterruptRequested/StopRequested setters) or when `Exit` is set.
  Dispatcher counters (BlocksExecuted, InstructionsExecuted, BlockCycles) are bit-identical to the interpreter
  (asserted in tests).
- HLE/SVC: SVC is inline (Exit/SvcImm/SvcPc/R15), never chained.
- Probes: the dispatcher sets the window to 0, so native code returns after every block. BlockHook (lockstep)
  disables native execution. Watchpoints (`VITASHARP_WATCH`) make the native backend unsupported (IL takes over).
- Invalidation (`BlockCache.Invalidate/Clear`): retired blocks are marked `Invalidated`, their incoming link slots
  cleared and their dispatch-table entry removed. The per-core fast lookup now also skips invalidated blocks.
- Exclusive monitor: LDREX/STREX word are inline with the interpreter generation-counter protocol (the generation
  table is pinned so generated code addresses it); other sizes are callouts; CLREX inline.

### Linking
- Static exits own an 8-byte link slot in data memory: the exit jumps through it when non-zero, else returns and
  the dispatcher fills it once the target has native code. Linking/unlinking never writes code pages.
- Indirect exits look the target up in a 64K-entry direct-mapped table `{key = pc | thumb, code}`: the stub reads
  key, code, key (publish writes key-invalid, code, key), so a concurrent republish of the slot cannot pair a
  stale code pointer with a new key. (An earlier code-then-key order had exactly that race: Killzone crashes.)

### Executable memory (W^X)
- Windows: one SEC_RESERVE section mapped twice (RW view for writing, RX view for executing), committed in 1 MiB
  steps; Linux: memfd mapped twice; macOS: MAP_JIT with `pthread_jit_write_protect_np` per thread (Apple silicon)
  and `sys_icache_invalidate`; RWX mmap only as a last resort. Flipping protections in place was rejected: pages are
  shared by blocks other threads may be executing.
- Code is called through `delegate* unmanaged<CpuState*, nint, void>` (trampoline), no Reflection.Emit: the
  backend is Native AOT compatible. Iced (MIT, pure C#) encodes x64.

### VFP fast paths
`FpOp` runs natively only in the Vita's FPSCR mode (DN=1, FZ=1, round to nearest, LEN/STRIDE 0), checked at run
time: SSE under FTZ+DAZ (MXCSR switched around the operation), NaN -> default NaN, VNMUL negates after the
substitution. Results equal to +/- the smallest normal (ARM detects tininess before rounding, x86 after),
x86 "indefinite" conversion results and every other FPSCR mode run the interpreter handler. VFMA (VFPv4) and
half/fixed-point conversions are callouts.

## Instruction coverage (inline in native code)
- ARM: data processing (imm, imm-shift, reg-shift; all 16 opcodes; S; PC destinations), MOVW/MOVT, MUL/MLA/MLS/
  UMULL/UMLAL/SMULL/SMLAL/UMAAL (+S), CLZ, REV/REV16/REVSH/RBIT, SXTB/SXTH/UXTB/UXTH (+accumulate), SBFX/UBFX/BFI/
  BFC, B/BL/BLX(imm)/BX/BLX(reg), SVC, LDR/STR/LDRB/STRB (imm/reg, all addressing modes), LDRH/STRH/LDRSB/LDRSH/
  LDRD/STRD, LDM/STM (all modes, PC in list), CLREX, DMB/DSB/ISB, MRC/MCR (TPIDRURO/TPIDRURW, CP15 barriers).
- Thumb-16: everything except BKPT/UDF/YIELD-type hints (callouts).
- Thumb-32: data processing (modified imm, shifted reg, plain imm: ADDW/SUBW/MOVW/MOVT/BFX/BFI), register
  shifts, extends, REV/RBIT/CLZ, MUL/MLA/MLS, SMULL/UMULL/SMLAL/UMLAL, SDIV/UDIV, B/B<c>/BL/BLX, TBB/TBH,
  LDM/STM/PUSH/POP, LDRD/STRD, all single loads/stores, MRC/MCR, barriers, CLREX, IT blocks.
- VFP: VLDR/VSTR/VLDM/VSTM/VPUSH/VPOP, VMOV (all core<->S/D/scalar forms, immediate, register), VDUP (core),
  VMRS/VMSR, VABS/VNEG, VADD/VSUB/VMUL/VNMUL/VDIV/VMLA/VMLS/VNMLA/VNMLS/VSQRT/VCMP(E)/VCVT (f32<->f64, int<->float)
  with the fast-path rules above.
- Exclusives: LDREX/STREX word (ARM, Thumb-2) with the interpreter's generation-counter monitor; CLREX.
- NEON: VAND/VBIC/VORR/VORN/VEOR/VBSL/VBIT/VBIF, VADD/VSUB.I8-64, VADD/VSUB/VMUL/VMLA/VMLS.F32 (D and Q), VDUP
  (scalar), VMOV/VMVN/VORR/VBIC (immediate), VLD1/VST1 (multiple, 1-4 registers, all writeback forms).
- Callouts: other NEON (VLD2-4/VST2-4, single/all-lane loads, shifts, scalar-by-element, misc, VEXT, VTBL...),
  byte/halfword/doubleword exclusives, SWP, saturating and parallel add/sub, SSAT/USAT, PKH, SEL, USAD8, halfword and
  dual multiplies, MRS/MSR, SDIV/UDIV in ARM state (SignedMultiplyMedia), VFMA, half/fixed-point VCVT, BKPT/UDF/
  YIELD/WFE/WFI, UNPREDICTABLE odd forms (e.g. PC destinations that do not end a block).

## Verification
- Unit tests: `NativeJitTests` (20 tests: random multi-instruction blocks native vs interpreter incl. a
  2-register spill stress, VFP with special operands, IL+native vs interpreter, native vs Unicorn, dispatcher end to
  end with identical counters, tier-up, invalidation under a live cache) + the full suite (see below).
- In-game: `VITASHARP_NATIVE_VERIFY=1` on Killzone to frame 3800: 272M native block executions verified against the
  interpreter, 0 mismatches.
- Bug found by game runs, not by the per-block tests: dispatch-table read race (fixed in 4332e9d). Lesson: block-level
  differential tests cannot see cross-thread races in the chaining machinery; run games several times and bisect
  with VITASHARP_NATIVE_NOLINK.
- Game runs and performance: see "Performance" below (latest batch).

## Performance (final batch, commit 3320ff1 + main cf72b18, Windows x64, 8 logical CPUs)
Each game ran once with `--cpu=native` and once with `--cpu=il`, back to back, one emulator at a time, private shader
cache. Other agents were using the machine at the same time, so treat single-digit differences as noise; CPU times
are process/thread CPU time per presented frame from the PERF lines.

| Game (scenario) | fps native / IL | process CPU ms/frame native / IL | busiest guest thread ms/frame native / IL |
|---|---|---|---|
| Devious Dungeon (dd_boot, 60 s) | 51.6 / 43.5 | 11.6 / 20.3 (-43%) | Unity main 4.74 / 11.70 (-59%) |
| Sonic Mania (mania_boot) | 47.4 / 44.5 | 9.6 / 15.5 (-38%) | main 4.85 / 7.42 (-35%); pthread 1.99 / 4.28 (-54%) |
| Uncharted (uc_walk) | 34.6 / 34.0 | 23.1 / 34.0 (-32%) | main 7.23 / 13.02 (-44%); workers 2.8 / 4.0 (-30%) |
| Killzone (kzl_walk3, 10,500 frames) | 24.2 / 22.4 | 80.4 / 101.3 (-21%) | main 30.4 / 33.9 (-10%); job threads 8.2-9.1 / 10.4-11.6 (-21%) |
| F1 2011 (f1_trees_lod) | 38.3 / 35.9 | 17.1 / 31.0 (-45%) | main 7.90 / 20.63 (-62%) |
| God of War (gow_play) | 59.1 / 59.1 (vsync) | 2.2 / 2.3 | main 0.26 / 0.46 (-43%) |

fps is mostly capped (vsync/pacing at 30/60) or GPU-bound, so the gain shows as CPU time. Killzone's long walk: no
deaths/limbo (no black frames after gameplay starts; player alive at frame 10,500, see
`C:\Users\guisa\VitaSharp\.vitadata\tests\nj_b3_kzl_walk3_native\sheet_35.png`), 0 crashes.
Remaining Killzone/Uncharted cost is NEON callouts (~470-480M executed per run: three-same forms outside the inline
subset, two-register misc, scalar-by-element, VEXT, shifts) — next step 1.

Earlier batch (before LDREX/STREX and NEON inline): Killzone's long walk ran at 5.4 fps natively (180M exclusive
callouts in lock loops plus NEON callouts); IL 24.6. That regression is what the exclusive/NEON work fixed.

## Debugging knobs
- `--cpu=native|il|interp`, `VITASHARP_CPU`, launcher Settings > CPU.
- `VITASHARP_NATIVE_DUMP=1`: IR + disassembly of every compiled block (stderr).
- `VITASHARP_NATIVE_VERIFY=1`: in-game differential mode (native vs interpreter per block execution, store log + undo;
  mismatches logged as NATIVE VERIFY MISMATCH with IR/disassembly). Halves CPU speed.
- Native faults inside generated code are reported (stderr and vitasharp-native-fault.txt in %TEMP%) with registers,
  the guest block, IR and disassembly.
- `VITASHARP_NATIVE_NOINLINE=all|Op,Op,...`: force interpreter callouts for those ops (bisection).
- `VITASHARP_NATIVE_NOLINK=1`: no exit linking / dispatch table.
- `VITASHARP_NATIVE_TIERUP=0`: no IL tier-up.
- `VITASHARP_NATIVE_VALIDATE=1`: validate IR of every block.
- Tests: `NativeJitTests` (`VITASHARP_FUZZ_ITER=N` shortens the fuzz, `VITASHARP_TEST_TRACE=file` logs blocks,
  `VITASHARP_HUNT=T|A:words` + `VITASHARP_HUNT_NATIVE=1` searches states for a native/interpreter mismatch).

## Next steps
1. More NEON natively (remaining callouts in Uncharted/Killzone, see the report line "executed callouts"): shifts
   (Neon_Shift), scalar-by-element (Neon_Scalar), two-register misc (Neon_Misc), VLD2-4/VST2-4 and lane loads, VEXT,
   VPADD/VMAX/VMIN/VCGT float compares; byte/halfword/doubleword exclusives. Then tier-up to IL can likely go.
2. Keep SIMD registers in XMM across a block (today every VFP op loads/stores the register file).
3. Fold `[base + imm]` into the addressing mode when the frontend can prove no 32-bit wrap; CMP+branch fusion
   (use host flags directly instead of materialised Z/N/C/V temps).
4. Reduce compile allocations (IR lists, closures) — native compile is ~80-110 us/block today.
5. Windows unwind info for the arena (RtlAddGrowableFunctionTable) so profilers/debuggers walk through JIT frames.

## ARM64 emitter plan
The IR and runtime are host-neutral except `X64Emitter` and the trampoline:
- New `Arm64Emitter` lowering the same IR (an assembler: hand-written encoder for the ~60 instructions needed, or
  a library), with x19 = state, x20 = memory base, temps in x21-x28 (callee-saved) and x9-x15 (caller-saved).
- Flags map 1:1 (ARM64 C after subtraction is NOT-borrow, like ARM32): `adds/subs/adcs/sbcs` + `cset`, and
  conditional regions can often become `csel` instead of branches. Register shifts need explicit range handling
  (lslv masks to 6 bits).
- FpOp: ARM64 FPCR has FZ/DN with ARM semantics (tininess before rounding), so the min-normal fallback disappears;
  set FPCR around fast paths exactly like MXCSR today.
- ExecutableMemory already has the Apple MAP_JIT path; Linux/Android ARM64 needs an instruction-cache flush helper
  (`dc cvau`/`ic ivau`, emitted as a tiny generated function) in `ExecutableMemory.FlushInstructionCache`.
- Callouts: AAPCS64 (x0-x3), no shadow space; the `[UnmanagedCallersOnly]` bridge is unchanged.
- `NativeX64Compiler` becomes a factory choosing the emitter by `RuntimeInformation.ProcessArchitecture`.
