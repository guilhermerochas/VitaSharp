# Native CPU backends (x86-64 and ARM64)

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
| 2e. NEON broad inline, LDREXD/STREXD, VCVTB/VCVTT (branch perf/neon-jit) | done, commits c36e70c, f587c79, a3740e2 (+ docs) |
| ARM64 backend (branch feat/arm64-jit): emitter, runtime, W^X + icache on ARM64 hosts, Unicorn/qemu tests | done, see "feat/arm64-jit" below |
| Media instructions, VLD2-4/VST2-4, VSHL.reg.64, VMULL, VCLS, VQMOVN inline on both backends; cheaper compilation (branch perf/jit-complete) | done, see "perf/jit-complete" below |
| ARM64 code quality: pinned block counters, compare + branch fusion, STP/LDP state flushes, SIMD register cache, lazy FPCR (branch perf/arm64-jit) | done in the sandbox, falta medir no aparelho; see "perf/arm64-jit" below |

## perf/arm64-jit (2026-10-30): ARM64 code quality

Branch `perf/arm64-jit` (worktree `.claude/worktrees/a64jit`, from main 4dbe06c, main 241ad19 merged). Not merged, not
pushed. Roadmap item "Ajuste do JIT nativo ARM64" (Android, "Velocidade nativa no celular"). Everything here is counted
in the Unicorn sandbox; **falta medir no aparelho** (Redmi Note 14, Helio G99: 2x Cortex-A76 + 6x A55). The x64 backend
is untouched (no x64 file changed; the IR and frontend are unchanged).

### What changed (each one a commit and a switch)
`VITASHARP_NATIVE_A64_NOOPT=all` or a comma list of the names below turns optimisations off (`Arm64Emitter.A64Opt`,
read once at startup; `Arm64Emitter.Opts` in tests).

| Name | Change |
|---|---|
| `pin` | Block accounting in pinned registers: x26 = NBlocks, x27 = NInstr, x28 = CycleAcc - CheckWindow, loaded by the trampoline and stored back by the epilogue (`Runtime.PinnedCounters` carries the trampoline's mode, so blocks and trampoline always agree). An exit is `add x26; add x27; adds x28, #cycles; b.ge ret` instead of three memory read-modify-writes plus a load and compare of the window. The link slot address is loaded first (it is also the return path's LastExitSlot), the link target right after the accounting. Callee-saved temps drop from 8 to 5 (x21-x25). Linked static exit: 18 -> 10 instructions, 10 -> 3 memory accesses. |
| `backedge` | Static exits to a guest address above their block's start skip the LinkBreak load + CBNZ: every cycle of linked blocks has an exit to an address at or below its block's start, which keeps the check (forward chains end by themselves). Indirect exits keep it. |
| `fuse` | CondBranch tests the host NZCV when every flag its condition reads is a temp of the AddC/SubC/TestNZ whose ADDS/SUBS/ADCS/SBCS/TST last set them and only flag-neutral ops (state moves, plain ALU, loads/stores) were lowered since: one B.cond on the inverse condition (ARM32 and ARM64 condition codes coincide). HI/LS/GT/LE 5 -> 1 instruction, GE/LT 2 -> 1, the rest 1 -> 1 without the CSET dependency. The flag temps are still stored (live out). |
| `pair` | Two consecutive 32-bit state stores/loads of adjacent fields as STP/LDP: the flushes are runs of R0..R15 then N Z C V (which follow R15), so the four flag stores become two STPs and R15 pairs with N. |
| `vcache` | Guest D/Q registers stay in v18-v31 between VecOps of a straight-line region, write-through (results still stored, the register file always current): only reloads disappear. Entries die when their bytes are written another way (StoreState of an S register, LDP/STP pairs, LoadD), at labels and callouts; FpOp keeps them and reloads them on its handler path (which clobbers v16-v31). Accumulating ops rewrite the cached destination in place. |
| `fpcr` | After a callout FPCR stays in the host mode until an op that needs the guest mode (FpOp, VecOp, labels, branches, exits): a run of callouts pays one MSR pair instead of one per call. FpOp's handler path still restores at once. |
| `fpcheck` | VFP fast-path FPSCR check `ubfx w16, w16, #16, #10; cmp w16, #0x300` (4 instructions instead of 6: the old mask is not a logical immediate). Bit 19, reserved on the Vita, is included: when set the op takes the handler, which is always exact. |
| `lit` | Stored constants that need MOVZ + MOVK (every R15 value of Vita code, constant addresses) come from the literal pool with one LDR W. |
| `dirty` | The dirty-page mark after a guest store (DirtyPages is on by default) shifts the address temp's own register instead of copying it to x0 first. |

### Instructions executed per loop iteration (`Arm64CodeQualityTable`)
Loop blocks linked to themselves through their link slot, 40 iterations, host instructions counted by a Unicorn code
hook over the code arena (trampoline/epilogue amortised: the ".7"), divided by the iterations; each switch alone and all
together, every run compared with the interpreter.

| Block (guest instructions) | none | pin | backedge | fuse | pair | vcache | fpcr | fpcheck | dirty | lit | all | per guest instruction none -> all | static words none -> all |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| ARM sum: ldr post-index, add, subs, bne (4) | 42.7 | 35.0 | 42.7 | 42.7 | 39.7 | 42.7 | 42.7 | 42.7 | 42.7 | 42.7 | 31.9 | 10.7 -> 8.0 | 86 -> 58 |
| ARM add, cmp, blt (3) | 38.7 | 31.0 | 38.7 | 37.7 | 36.7 | 38.7 | 38.7 | 38.7 | 38.7 | 37.7 | 26.9 | 12.9 -> 9.0 | 82 -> 56 |
| ARM max: ldr, cmp, movgt, subs, bne (5) | 55.8 | 48.1 | 55.8 | 51.8 | 51.8 | 55.8 | 55.8 | 55.8 | 55.8 | 54.8 | 39.0 | 11.2 -> 7.8 | 102 -> 68 |
| Thumb ldmia/stmia 4 regs, subs, bne (4) | 79.7 | 72.0 | 79.7 | 79.7 | 74.7 | 79.7 | 79.7 | 79.7 | 75.7 | 78.7 | 61.9 | 19.9 -> 15.5 | 134 -> 100 |
| Thumb ldrb/strb, adds, cmp, blt (5) | 50.7 | 43.0 | 50.7 | 49.7 | 48.7 | 50.7 | 50.7 | 50.7 | 49.7 | 49.7 | 37.9 | 10.1 -> 7.6 | 98 -> 68 |
| NEON vld1, vmul, vmla, vadd .f32 q, vst1, subs, bne (7) | 75.7 | 68.0 | 75.7 | 75.7 | 72.7 | 71.7 | 75.7 | 75.7 | 73.7 | 74.7 | 57.9 | 10.8 -> 8.3 | 126 -> 92 |
| Two VQSHL callouts, subs, bne (4) | 66.7 | 59.0 | 66.7 | 66.7 | 64.7 | 66.7 | 62.7 | 66.7 | 66.7 | 66.7 | 52.9 | 16.7 -> 13.2 (MSR FPCR 4 -> 2) | 122 -> 90 |
| VFP vldr, vmul, vadd, add, subs, bne (6) | 64.7 | 57.0 | 64.7 | 64.7 | 62.7 | 64.7 | 64.7 | 60.7 | 64.7 | 63.7 | 49.9 | 10.8 -> 8.3 | 140 -> 110 |

About -25% host instructions on these blocks; the memory accesses drop more (pin: 7 per exit; vcache: one load per reused
SIMD operand). `backedge` does not show on self-loops (their exit is a back edge); it saves 2 instructions and a load on
every forward linked exit. What is left per conditional block exit: the four CSETs + two STPs of the live-out flags, the R15
stores (the frontend stores the fall-through R15 before the conditional region and the taken path stores it again) and
the link-slot double load.

### Verification
- Encodings: every new instruction form (pinned-counter ADDS/ADD/SUB/LDR/STR, STP/LDP W, UBFX/CMP, LDR W literal, vector
  MOV/LDR/STR/arithmetic on v18-v31) is in `Arm64AssemblerTests`, checked against GNU as 2.40 (binutils-aarch64-linux-gnu
  unpacked in WSL with `apt-get download` + `dpkg -x`, ~3 MB, no root).
- The sandbox now clobbers the caller-saved registers (x1-x17, v0-v7, v16-v31, upper halves of v8-v15) after every
  callout, like a real AAPCS64 callee: before, code that wrongly kept a value in them across a handler call passed in
  Unicorn (found while writing the SIMD cache: the reload after FpOp's handler path was untested).
- New tests (fast suite): `Arm64FusedConditionsMatchInterpreter` (9 flag setters x 14 conditions at the flag edges, fused
  and not), `Arm64VfpModeCheckEdges` (each FPSCR bit 15-31 flipped), `Arm64SimdCacheMatchesInterpreter` (blocks of 6-16
  instructions over D0-D7: NEON, VFP in and out of the Vita mode, VMOV core/S, VLDR/VSTR, conditional VFP, MRS/MSR and
  VQSHL callouts; cache and lazy FPCR in all four combinations), `Arm64LinkBreakOnBackEdges`, `Arm64CodeQualityTable`
  (every switch position against the interpreter). Mutations caught: no SIMD-cache invalidation on S stores, no reload
  after the FpOp handler, no guest-FPCR restore before vector ops.
- The existing ARM64 tests (random blocks incl. spill stress, VFP, every NEON class, exclusives, links/dispatch table)
  run with all optimisations on.
- `VITASHARP_NATIVE_VERIFY=1` covers everything inside a block (fusion, pairs, SIMD cache, lazy FPCR, literals) and the
  pinned counters (the verifier runs blocks through the trampoline with CheckWindow 0); it does not chain, so `backedge`
  is covered only by the test above and game runs.

### Device plan (serial, when the phone is free)
1. `VITASHARP_NATIVE_VERIFY=1` session (`config/env.txt`), `--speed=0.25`, Knightin'+ and Devious Dungeon to gameplay:
   expect 0 mismatches in blocks with NEON/VFP ops (shared-counter mismatches of multi-threaded games are known noise).
2. fps / CPU A/B on the same scene: default build vs `VITASHARP_NATIVE_A64_NOOPT=all` (Knightin'+, Devious Dungeon, F1
   2011), PERF lines (busiest guest thread ms/frame, host time in native code).
3. If anything differs or crashes: bisect with `VITASHARP_NATIVE_A64_NOOPT=pin`, `=fuse`, ... one name at a time.

### On the device (branch perf/android-phone, 31/10/2026, Redmi Note 14)
- `VITASHARP_NATIVE_VERIFY=1`: Knightin'+ 25.2 M block runs, Devious Dungeon 19.8 M, **0 mismatches** (0 in NEON/VFP
  blocks). No bisection needed.
- Where the time went (simpleperf with `<profileable android:shell="true">`, `--app`, the unstripped AOT images of a
  `-p:_AndroidAotStripLibraries=false` build; `VITASHARP_NATIVE_MAP=file` maps the anonymous code arena to guest blocks):
  Devious Dungeon's Unity main thread was 61% generated code, 18% `VitaSharp.Core` (the HLE round trip: 5,100
  lwmutex/TLS calls per frame), 7.6% Mono's GC transition, 4.8% `clock_gettime`; IPC 0.65 with 33 M L1i and 21 M dTLB
  misses per second (no transparent huge pages on Android). Samples spread over thousands of blocks (no hot loop).
- Changes, each measured on the phone (Devious Dungeon town, fps): HLE calls handled inside `CpuCore.Run` (27-30 -> 35),
  no GC transition on Mono (`SuppressGCTransition`, -> 37), resume through the dispatch table after an HLE call and
  republish of evicted table entries (-> 45), linked exits patched into direct `B` (`patch`, 64K -> 256K table entries),
  SVC callouts (`VITASHARP_NATIVE_HLE_CALLOUT`, native entries 6,400 -> 1,500 per frame, -> 50-51), and the
  `mono-aot-llvm` runtime (-> 52). The desktop runs the same guest work (2.6 M cycles, 380 K blocks per frame) at 60.
- Still to do: the remaining gap is the generated code itself (per-block state loads/stores and flag materialisation,
  i-cache footprint: 9 MB of code for 188 K guest instructions); cross-block register allocation and smaller exit
  stubs are the next steps.

## perf/jit-complete (2026-10-29)

Branch `perf/jit-complete` (worktree `.claude/worktrees/jit-complete`, from main 9ffaa48). Not merged, not pushed.

### Coverage added (both backends: the work is in the shared frontend or has an ARM64 twin)
- Media instructions in plain IR (`Native/ArmFrontend.Media.cs`, no new IR ops, so x64 and ARM64 lower them alike), ARM
  and Thumb: SMULxy/SMLAxy/SMULWy/SMLAWy/SMLALxy, SMUAD/SMUSD/SMLAD/SMLSD/SMLALD/SMLSLD, SMMUL/SMMLA/SMMLS (+R),
  S/Q/SH/U/UQ/UH ADD16/ASX/SAX/SUB16/ADD8/SUB8 with GE, SEL, QADD/QSUB/QDADD/QDSUB, SSAT/USAT(16), PKH, USAD8/USADA8,
  UMAAL (Thumb), SXTB16/UXTB16 (+A), ARM-state SDIV/UDIV. Q is ORed into `CpuState.Q`, GE stored to `CpuState.GE`;
  operands the handlers read as the raw R15 use the cached R15 (same value). Only PC destinations stay callouts.
- VLD2-4/VST2-4 multiple structures in plain IR (loads build each destination word from its elements; stores go element
  by element in the handler's order). Single lane and all lanes were already inline.
- VSHL (register) 64-bit lanes: x64 in GPRs (two lanes), ARM64 SSHL/USHL d (ARM64 also gets 8/16-bit lanes; x64 keeps
  those as callouts, no variable 8/16-bit SSE shifts).
- New VecKinds: `MullI` (VMULL.S/U8/16/32, VMULL.P8, VMLAL, VMLSL), `Cls` (VCLS), `Qmovn` (VQMOVN.S/U, VQMOVUN; x64 uses
  packs for 16/32-bit sources and GPRs for 64-bit ones; ARM64 SQXTN/UQXTN/SQXTUN). `Arm64Assembler.V3Diff`; every new
  ARM64 encoding checked against GNU as 2.40 (`Arm64AssemblerTests`).

### Callouts executed per run, before -> after
| Scenario | before (main 9ffaa48) | after |
|---|---|---|
| mania_boot | 122.8M (T32_Mul.SMLABB 120.9M, VLD4/VLD2 multiple 1.06M, T32_Parallel/T32_Misc 0.74M) | 5K (LDREXB/STREXB) |
| dd_boot | 2.85M (T32_Parallel 2.56M, VLD4/VST2/VLD2 multiple 0.29M) | 0 |
| kz_skin | 0.20M (VSHL.reg.U64 0.14M, VMULL.U 0.05M) | 6K (VMLA.F32 tininess slow paths) |
| uc_walk | 0.35M (VSHL.reg.U64 0.35M) | 0 |

### Compile cost
`BackendStats` now carries per-phase times (`CompilePhases`: frontend, passes, register allocation, lowering, encoding,
install), printed in the backend summary. Changes: the x64 fast encoder (`X64Emitter.Encode.cs`: each instruction encoded
once with a reused Iced `Encoder`, label branches short/near by layout iteration, fallback to Iced's BlockEncoder for
anything unexpected; `FastEncoderMatchesIced` compares both on 1500 blocks), per-thread reuse of the IR block/builder,
liveness and linear-scan buffers, no closures in the frontend switch, `AggressiveOptimization` on the hot compile
methods (tier-0 code dominated the first thousands of blocks).

Benchmark (`CompileCostBenchmark`, Slow; corpus of 17,730 real blocks recorded with `VITASHARP_NATIVE_CORPUS` in Sonic
Mania, Devious Dungeon and Killzone, replayed with `VITASHARP_BENCH_CORPUS`; idle thread, 7 rounds):

| | x64 before | x64 after | ARM64 before | ARM64 after |
|---|---|---|---|---|
| steady (median of rounds) | 19.4 us/block | 9.8-10.3 us/block | 6.0 us/block | 4.2 us/block |
| cold (first round, tier-0 compiler code) | 74 us/block | 35 us/block | 12.4 us/block | 10.8 us/block |
| allocated per block | 22.8 KB | 1.6 KB | 12.6 KB | 2.0 KB |

x64 steady split after: frontend 0.7, passes 0.2, allocation 1.2, lowering 2.7, encoding 4.3 us (Iced's encoder itself is
the largest remaining part). In-game (wall time on the JIT workers, contended with the emulation threads):
mania_boot 151 -> 96 us/block, dd_boot 93 -> 45, kz_skin 119 -> 61, uc_walk 122-144 -> 49-100 (the old ~188 us figure was also wall time).

### Measured and rejected: NEON register pinning, compare + branch fusion
Data: `VITASHARP_NATIVE_COUNTERS=1` (x64 blocks count executed VFP/NEON ops and conditional branches; PERF cycles lines),
the PERF cycles lines' new "host time in native code", and `NeonRegisterFileBenchmark` (128-op blocks: a dependent
VADD chain through q0 against eight independent chains).
- Uncharted main thread: 140K VFP/NEON ops and 243K conditional branches per frame, 4.4 ms/frame in native code; each
  worker 70K ops, 1.1 ms. Killzone main: 98K ops, 151K branches, 4.2 ms.
- A dependent chain costs 1.54 ns/op (VADD.I32) against 0.32 ns for independent ops: keeping the register in an XMM
  register could save at most ~1.2 ns per *dependent* op (store-forwarding latency); float ops (3.8 ns dependent) are
  dominated by the explicit flush/default-NaN fix-ups, which pinning does not remove. Upper bound for Uncharted's main
  thread: 140K x 1.2 ns = 0.17 ms/frame (4% of its native time, ~1% of process CPU), less in reality since most ops are
  not chained through one register. Compare + branch fusion would remove a `test`/`cmp` of materialised flags per
  conditional exit (the flags still have to be stored, they are live-out): about 0.1 ms/frame for 243K branches.
- Both games are paced/vsync-bound (UC 30/60, KZ 30 fps), so neither change can move fps; CPU savings would be ~1%.
  Not implemented. Cheaper per-block exit accounting (three memory read-modify-writes per block, 200-350K blocks per
  frame on the busiest threads) would be the bigger lever if generated-code time ever matters.

### Verification
- New fuzz: `NativeMediaQuick`/`Arm64MediaQuick` (fast) and `NativeMediaMatchesInterpreter`/`Arm64MediaMatchesInterpreter`
  (Slow: 40,000 x64 and 15,000 ARM64 blocks per state) with operands at the saturation/overflow edges, Q and GE compared,
  and every block required to compile without callouts; NEON class `ThreeDiff` (VMULL/VMLAL/VMLSL/P8) in the x64 and
  ARM64 NEON fuzz (quick and Slow), Thumb LoadStore on ARM64, `NewNeonFormsAreInline`. ARM64 code runs in Unicorn's
  AArch64 model (`Arm64Sandbox`). A mutation (wrong overflow shift in SMLAD) was caught at once.
- Slow variants for the new ops, run alone once at the end (1.8 h, 50 tests): x64 and ARM64 media (40,000 / 15,000 blocks per state), every NEON class on x64 and ARM64 (2,000-6,000 blocks each, ThreeDiff included), native NEON vs Unicorn, the random-block differentials on both backends: 0 native/interpreter differences. One interpreter-vs-Unicorn difference: VTRN.32 with d == m (UNPREDICTABLE; QEMU swaps, the handler duplicates), reached because the new NEON class shifted the random sequence; VTRN/VUZP/VZIP with d == m are now filtered like the other UNPREDICTABLE forms (`CpuFuzzTests.IsUnpredictable`), and the test passes.
- Fast suite (`Category!=Slow`): 987/987 in 5 min, build with 0 warnings.

### Performance (default 333 MHz, one emulator at a time, affinity 4 cores, BelowNormal, other agents active)
| Scenario | fps (whole run) before / after | steady fps | p99 frame interval ms | process CPU ms/frame | busiest guest thread ms/frame |
|---|---|---|---|---|---|
| mania_boot | 37.3 / 44.0 | 55.0 / 55.2 | 93.7 / 28.2 | 11.3 / 10.2 | main 5.28 / 5.11, pthread 2.33 / 1.87 |
| dd_boot | 49.8 / 50.4 | 60.0 / 60.0 | 17.3 / 17.2 | 14.0 / 12.9 | Unity main 5.55 / 5.00 |
| kz_skin | 27.8 / 28.7 | 30.0 / 30.0 | 91.2 / 35.0 | 30.9 / 30.3 | killzone_vita 11.94 / 11.73 |
| uc_walk (back to back) | 35.0 / 35.3 | 36.5 / 36.6 | 35.3 / 33.7 | 17.4 / 17.0 | ProjectUncharted 5.64 / 5.39 |

PERF cycles (guest code M cycles/frame) are unchanged, as expected: the cycle model charges the same instructions, only the
host cost changes. The Sonic Mania menu dip (frames ~1100-1600: 36-52 fps, average 39.1 before / 38.8 after) is not
caused by the CPU backend: 121M SMLABB callouts went inline and the menu frame rate did not move (the roadmap item
stays). The first uc_walk "after" run in the batch measured 31.1 fps / main 5.67 ms with other agents loading the
machine; the back-to-back rerun above is the comparison.

## perf/neon-jit (2026-10-22)

Branch `perf/neon-jit` (worktree `.claude/worktrees/neon-jit`), merged with main 23efe56. Not merged into main, not
pushed.

### What is inline now
- `Native/ArmFrontend.Neon.cs` + `Native/X64Emitter.Neon.cs` (one `IrOp.VecOp` per instruction on the register
  file, SSE2-SSE4.1; AVX2 only for VSHL.reg.32, FMA for VFMA/VFMS, F16C for f16 conversions; every kind is checked
  with `X64Emitter.SupportsVec`, so older hosts fall back to callouts):
  - three-same: bitwise, VADD/VSUB, VMUL/VMLA/VMLS int, VCEQ/VTST/VCGT/VCGE, VMAX/VMIN, VABD/VABA, VHADD/VRHADD/VHSUB,
    VQADD/VQSUB (QC), VPADD/VPMAX/VPMIN, VQDMULH/VQRDMULH.16 (QC), VSHL.reg.32, and the f32 forms VADD/VSUB/VMUL/VMLA/
    VMLS/VFMA/VFMS/VABD/VPADD/VCEQ/VCGE/VCGT/VACGE/VACGT/VMAX/VMIN/VPMAX/VPMIN/VRECPS/VRSQRTS;
  - by scalar: VMUL/VMLA/VMLS .I16/.I32/.F32;
  - shifts: VSHR/VSRA/VRSHR/VRSRA, VSHL/VSLI/VSRI (all sizes incl. 64), VSHRN/VRSHRN, VQ(R)SHRN/VQ(R)SHRUN from 16/32-bit
    lanes (QC), VSHLL/VMOVL;
  - two-register misc: VREV16/32/64, VCNT, VCLZ, VMVN, VPADDL/VPADAL, compares with zero (int and f32), VABS/VNEG,
    VSWP/VTRN/VUZP/VZIP, VMOVN, VSHLL (max shift), VCVT f16<->f32, VRECPE/VRSQRTE (f32 and u32: lookup tables generated
    from the handlers' own estimate functions, so the ARM ARM tables are reproduced bit for bit), VCVT int<->f32;
  - VEXT, VTBL/VTBX (pshufb per 16-byte chunk), VLD1/VST1 single lane and all lanes (plain IR).
- Float semantics are the handlers': MXCSR = DAZ only during runs of vector ops (`CpuState.MxcsrNeon`), explicit result
  flush/default NaN, compares on flushed inputs. ARM detects FZ tininess BEFORE rounding: a product (or fused result)
  that rounds up to +/-2^-126 must flush to zero. The handlers now do that (`Neon.MulFz/FmaFz`, found by the Unicorn
  test: VFMA gave 0x80800000, QEMU 0x80000000) and the native lowering checks the rounded lanes and sends the rare
  match to the handler (`VecFlags.SlowPath`: a call site for register allocation).
- LDREXD/STREXD (ARM and Thumb): `IrOp.LdrEx/StrEx` with Imm = 1, one 64-bit read / `lock cmpxchg qword` with the same
  generation protocol as `Arm.StoreExclusive64`. Fixed in the interpreter at the same time: ARM-state LDREXD wrote the
  high word to R0 (Rt2 was taken from bits 3:0 instead of Rt + 1).
- VCVTB/VCVTT (VFP half <-> single) on the FpOp fast path with F16C (handler fallback outside the Vita FPSCR mode).
- `CalloutClasses` splits executed callouts by encoding class (NEON A/B/U/size, load/store type, exclusive size, VFP
  opc2, T32 multiply form); the CPU backend report ends with "by class: ...".
- `CpuState.Pad2` keeps D 16-byte aligned; `ExTmpHi` is the STREXD scratch.

### Still callouts (by executed count in the runs below)
T32_Mul (SMUL/SMLA<x><y>, SMULW, SMMUL... about 120M in Sonic Mania), T32_Parallel (2.6M in Devious Dungeon),
VSHL.reg.U64 (0.5M in Killzone), VLD2/VLD4/VST2 multiple (under 1M anywhere: `IrOp.VecLdSt` is declared but not
lowered), VMULL/ThreeDiff, VCLS, VQSHL/VRSHL register forms, VQMOVN, VCVT fixed point, LDREXB/H.
(Since perf/jit-complete everything in this list is inline except VQSHL/VRSHL register forms, the other ThreeDiff forms,
VCVT fixed point and LDREXB/H.)

### Callout ranking before -> after (executed callouts per run)
| Scenario | before (main b93da80) | after | top remaining |
|---|---|---|---|
| kz_skin | 145.3M (VTRN 25M, VLD1 all-lanes 17M, VRECPS 10M, VPADD 8M, VMLA.F32 scalar 7M...) | 0.21M | VSHL.reg.U64 |
| kzl_walk3 | 465.2M | 0.59M | VSHL.reg.U64 |
| uc_walk | 486.1M (VMLA.F32 scalar 85M, VTRN 76M, VPADD.F32 47M, VCGE.F32 37M, VEXT 36M, LDREXD 17M, STREXD 11M, VCVTB/T 9M...) | 0.34M | VSHL.reg.U64 |
| mania_boot | 124.5M | 122.2M | T32_Mul (not NEON) |
| dd_boot | 14.1M | 2.9M | T32_Parallel |
| f1_trees_lod | 19.2M | 0 | - |
| rbs_boot | 20.8M | 0 | - |

### Performance
One emulator at a time, private shader caches, branch then main back to back. Other agents shared the machine, so
single-digit differences are noise.

| Scenario | fps after / before | process CPU ms/frame after / before | busiest guest thread ms/frame after / before |
|---|---|---|---|
| kz_skin | 28.7, 28.1, 27.1 / 28.9, 28.9 | 36.5 / 30.9, 30.4 | killzone_vita 11.8, 12.4, 14.8 / 12.5, 12.5 |
| kzl_walk3 (10,500 frames, no deaths) | 26.6 / 27.7 | 66.0 / 65.0 | killzone_vita 27.5 / 26.2 |
| uc_walk | 34.7 / 35.1 | 15.7 / 20.3 (-23%) | ProjectUncharted 4.69 / 7.20 (-35%); workers 1.15 / 2.43 |
| mania_boot | 43.0 / 44.1 | 11.2 / 10.6 | main 5.72 / 5.33 |
| dd_boot | 51.4 / 51.9 | 10.7 / 11.1 | Unity main 4.40 / 4.42 |
| f1_trees_lod | 38.2 / 38.6 | 15.0 / 14.7 | main 7.24 / 7.24 |
| rbs_boot | 29.4 / 29.6 | 80.1 / 79.2 | JQ workers 33.3 / 33.1 |

Killzone's main thread does not get faster although its NEON callouts are gone: its frame is dominated by HLE and
waiting (PERF cycles: guest code 1.9 M cycles/frame against 4.9 M/frame charged for HLE), and main already tiered its
NEON-heavy blocks up to IL. Uncharted's main and worker threads drop by a third. Resistance's two JQ_wrkr threads at
33 ms/frame are the heaviest guest threads in the batch and do not depend on NEON.
One of five Killzone runs of the branch build froze at frame 1275 with GPU commands pending (submitted 120,173,
completed 119,794; the guest main thread idle, not spinning). Not reproduced in four later runs; whether it comes
from the renderer (MSAA, new in main b93da80) or from the CPU is not established.

### Verification
- Tests: per-class NEON native vs interpreter with special values (lane extremes, denormals, NaN, infinities,
  products that round up to the smallest normal), quick versions in the fast suite (`NativeNeonQuick`) and long ones
  under Category=Slow (`NativeNeonMatchesInterpreter`, 2,000-6,000 blocks per class); native vs Unicorn (Slow, 8,000
  blocks, 0 differences after the tininess fix); LDREXD/STREXD monitor scenarios (ARM/Thumb); VCVTB/VCVTT with
  half-range edge values (Vita mode and other FPSCR modes). Fast suite 353/353 after the b93da80 merge (4 min); full
  suite incl. Slow 385/385 after merging main 23efe56 (1 h 27 min). The fuzz tests now reset the JIT code arena when
  they end (`ArenaScope`): before that, the full suite filled the 256 MiB arena and failed with "JIT code arena full".
  Post-merge smoke (main 23efe56): kz_skin 26.4 fps, 0.19M callouts, all GPU commands completed; uc_walk 34.8 fps,
  main 5.22 ms/frame.
- VITASHARP_NATIVE_VERIFY=1 (needs `--speed=0.25` so the slower guest still reaches gameplay):
  - Killzone kzl_walk3, 7,000 frames into gameplay: 3.80 billion block runs verified, 0 mismatches in blocks with
    NEON/VFP arithmetic. 3 NEON/VFP-block reports were plain copies (VLDM/VSTM/VDUP/VLDR/VSTR) of float data other job
    threads were writing at the time; the other 8,050 reports are integer/exclusive blocks reading shared counters and
    flags (LDREX/LDREXD of job-queue indices advanced by another core between the two runs).
  - Uncharted uc_walk: 9.49 billion block runs, 0 NEON/VFP mismatches (12,816 integer spin-wait/flag reads). The
    verify run stalled at frame 1932 (main thread spinning on a flag); main b93da80 with verify stalls even earlier
    (frame 121): the store undo of verify mode can erase a store another thread made in between, so long verify runs
    of multi-threaded games are not expected to finish. Normal runs are unaffected.
  - Verify now reports NEON/VFP-block mismatches separately ("N mismatches (M in blocks with NEON/VFP ops)").

### Next steps
1. Killzone main thread (about 27 ms/frame in kzl_walk3): profile host time (scratchpad cpurep on a nettrace); HLE,
   waits and spins dominate, not guest NEON.
2. T32_Mul halfword/word multiplies (SMULxy/SMLAxy with Q, SMULWy/SMLAWy, SMMUL/SMMLA) inline: about 120M callouts in
   Sonic Mania; T32_Parallel (SADD16/UADD8/...) for Devious Dungeon.
3. VLD2-4/VST2-4 (`IrOp.VecLdSt` with pshufb de/interleave), VSHL.reg.64, VMULL, VCLS: all below 1M per run.
4. Keeping Q/D registers in XMM across ops/blocks: not attempted. With the callouts gone, per-op load/store of the
   register file is the remaining NEON overhead, but no game here is NEON-bound any more; measure before doing it.
5. Investigate the one-off Killzone GPU stall if it shows up again (submitted > completed GPU commands).

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
- Exclusives: LDREX/STREX word and LDREXD/STREXD (ARM, Thumb-2) with the interpreter's generation-counter monitor;
  CLREX.
- VFP also VCVTB/VCVTT (F16C).
- NEON: see "perf/neon-jit" above (nearly all three-same, by-scalar, shift, misc, VEXT, VTBL forms), VDUP (scalar),
  VMOV/VMVN/VORR/VBIC (immediate), VLD1/VST1 (multiple 1-4 registers, single lane, all lanes).
- Media (perf/jit-complete, ARM and Thumb): halfword, dual and most-significant-word multiplies (SMULxy/SMLAxy/SMULWy/
  SMLAWy/SMLALxy, SMUAD/SMUSD/SMLAD/SMLSD/SMLALD/SMLSLD, SMMUL/SMMLA/SMMLS with and without R), parallel add/subtract
  (S/Q/SH/U/UQ/UH ADD16/ASX/SAX/SUB16/ADD8/SUB8) with GE, SEL, QADD/QSUB/QDADD/QDSUB, SSAT/USAT/SSAT16/USAT16, PKH,
  USAD8/USADA8, UMAAL, SXTB16/UXTB16 (+accumulate), SDIV/UDIV in ARM state.
- Callouts: the NEON forms listed under "Still callouts", byte/halfword exclusives, SWP, MRS/MSR, VFMA, half/fixed-point
  VCVT, BKPT/UDF/YIELD/WFE/WFI, UNPREDICTABLE odd forms (e.g. PC destinations that do not end a block).

## Verification
- Unit tests: `NativeJitTests` (20 tests: random multi-instruction blocks native vs interpreter incl. a
  2-register spill stress, VFP with special operands, IL+native vs interpreter, native vs Unicorn, dispatcher end to
  end with identical counters, tier-up, invalidation under a live cache, exclusive monitor scenarios and a 4-thread
  atomic-increment stress). Full suite on the final commit (incl. main cf72b18): 290/290 passed (39 min).
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
- `VITASHARP_NATIVE_A64_NOOPT=all|pin,backedge,fuse,pair,vcache,fpcr,fpcheck,dirty,lit`: ARM64 backend optimisations off
  (bisection on a device, see "perf/arm64-jit").
- `VITASHARP_NATIVE_VALIDATE=1`: validate IR of every block.
- `VITASHARP_NATIVE_COUNTERS=1`: x64 blocks count executed VFP/NEON ops and conditional branches (PERF cycles lines).
- `VITASHARP_NATIVE_CORPUS=file`: append every compiled block (for `CompileCostBenchmark`, read via `VITASHARP_BENCH_CORPUS`).
- Tests: `NativeJitTests` (`VITASHARP_FUZZ_ITER=N` shortens the fuzz, `VITASHARP_TEST_TRACE=file` logs blocks,
  `VITASHARP_HUNT=T|A:words` + `VITASHARP_HUNT_NATIVE=1` searches states for a native/interpreter mismatch).

## Next steps
1. T32 multiplies/parallel ops, VLD2-4, register pinning, CMP+branch fusion and compile cost: done or measured and
   rejected in perf/jit-complete (see there). With NEON inline, tier-up to IL no longer triggers in the tested games and
   can likely go.
2. Cheaper per-block exit accounting (CycleAcc/NBlocks/NInstr are three memory read-modify-writes per block).
3. Fold `[base + imm]` into the addressing mode when the frontend can prove no 32-bit wrap.
4. Windows unwind info for the arena (RtlAddGrowableFunctionTable) so profilers/debuggers walk through JIT frames.

## feat/arm64-jit (2026-10-29) — native ARM64 backend

Branch `feat/arm64-jit` (worktree `.claude/worktrees/arm64-jit`, from main 7610224, main 7f26c85 merged). Not merged
into main, not pushed. The foundation for the Android port; also Apple silicon macOS, Linux ARM64, Windows on ARM.

### Structure
- `Native/Arm64Assembler.cs`: hand-written AArch64 encoder (integer, load/store incl. `[x, w, uxtw]`, exclusives and
  acquire/release, branches with labels, system registers, scalar FP, Advanced SIMD three-same / two-reg misc /
  permute / shift by immediate / EXT / TBL / DUP / INS / MOVI), literal pool after the code (64- and 128-bit),
  position independent. No new dependency (Iced stays x64 only).
- `Native/Arm64Emitter.cs` + `Arm64Emitter.Neon.cs` lower the same IR as `X64Emitter`. `Backends/NativeArm64Compiler.cs`
  is the same pipeline as `NativeX64Compiler` (frontend with the ARM64 SIMD coverage, dead code, tier-up budget,
  arena, crash-report registration). Both backends are named `native`; `Jit.Select` takes the first one the host
  supports, so `--cpu=native`, `VITASHARP_CPU=native`, the launcher setting and the automatic default pick ARM64 when
  `RuntimeInformation.ProcessArchitecture == Arm64`.
- Shared changes (x64 behaviour unchanged): the linear-scan allocator moved verbatim to `Native/LinearScan.cs`;
  `ArmFrontend` takes the target's `SupportsVec` and whether it needs the FZ tininess slow paths (x64 yes, ARM64 no);
  `NativeRuntime` builds the ARM64 trampoline and `Arm64Stubs` on ARM64 hosts (the x64 fault filter stays x64 only).

### Emitter
- x19 = `CpuState*`, x20 = guest base; fastmem is `ldr/str w, [x20, wN, uxtw]` (every W write zero-extends, so any temp
  indexes guest memory directly and address arithmetic wraps mod 2^32). Temps: x9-x15 (caller-saved), x21-x28
  (callee-saved, survive callouts); scratch x0-x8, x16, x17; x18 is never touched (Apple/Windows platform register).
  Frame: host FPCR, guest FPCR, 64 spill slots.
- Flags: ADDS/SUBS/ADCS/SBCS on W registers give exactly ARM32 AddWithCarry NZCV (C after subtraction is NOT borrow on
  both); a carry-in temp becomes `cmp wC, #1`; flag temps are CSETs. Register-controlled shifts use 64-bit
  LSLV/LSRV/ASRV with CSEL clamps for amounts 32-255. Compound conditions (HI/LS/GE/LT/GT/LE) compare the full flag
  values like `Arm.CondPassed` (found by the fuzz: an UNPREDICTABLE LDRD writing R[16], which is N, leaves N outside
  0/1). UDIV/SDIV already have ARM's semantics (x/0 = 0, INT_MIN/-1 = INT_MIN).
- Exits: static exits through the 8-byte link slots, indirect exits through the dispatch table with
  `ldar key; ldar code; ldr key` (ARM64 reorders loads; the publisher's Volatile.Write is a release). The epilogue,
  link slots, tables and the callout bridge are literal-pool addresses (`ldr x16, =addr; br/blr x16`), so nothing
  depends on branch range inside the 256 MiB arena.
- Callouts: AAPCS64 (x0 = state, w1 = op, w2 = word, w3 = pc), FPCR set to the host value around the call.
- FPCR: the trampoline saves the host FPCR and sets the guest mode (FZ, DN, round to nearest: the Vita's FPSCR and the
  Advanced SIMD standard mode) for all generated code; the epilogue restores it. AArch64 flushes with tininess
  detected before rounding like ARMv7, so there are no min-normal slow paths, no explicit result flushing and no
  default-NaN fix-ups (x64 needs all three).
- VFP (`FpOp`): fast path when FPSCR is in the Vita mode (same check as x64), else the handler. Add/Sub/Mul/NMul/Div,
  VMLA/VMLS/VNMLA/VNMLS (product rounded first: FMUL then FADD/FSUB), VSQRT, VCMP(E) (FCMP's NZCV are VCMP's, copied
  into FPSCR[31:28]), f32<->f64, int<->float (FCVTZS/FCVTZU saturate and map NaN to 0 like ARMv7; the R forms use
  FCVTNS/FCVTNU), VCVTB/VCVTT (FCVT h<->s, AHP=0, FZ16=0).
- LDREX/STREX(D): the interpreter's generation-counter protocol, as on x64: LDAR of the granule counter (YIELD spin
  while odd), then the read; STREX = LDAXR/STLXR CAS of the counter snap -> snap+1, LDAXR/STLXR CAS of the value
  against the LDREX value, STLR release of snap+2 / snap. ARMv8.0 only (no LSE). An unaligned address (an alignment
  fault on hardware) takes a plain compare and store instead of faulting the host.
- NEON: one ASIMD instruction per IR kind for nearly everything (AArch32 and AArch64 share the lane pseudocode);
  64-bit lanes of D registers use the scalar encodings; QC comes from FPSR.QC (cleared before, ORed into FPSCR
  after). Explicit exceptions: VMLA/VMLS.F32 and the by-scalar float MLA/MLS are not fused (FMUL then FADD/FSUB);
  VRECPS/VRSQRTS are AArch32's non-fused step functions (FMUL, FSUB from 2 or 3, x0.5, infinity x zero lanes forced
  to 2.0 / 1.5 with a mask); VTBL/VTBX force indices >= 8 * len to 0xFF so the 16-byte table registers cannot read
  past the guest table (table D registers wrap mod 32 like the handlers); VSHL (register) shifts m by n.
- Dirty-page marks, the verify-mode store log and tier-up counters use the x64 layouts.

### Executable memory and faults on ARM64 hosts
- Windows on ARM: the dual-mapped section, `FlushInstructionCache` after every write. Guest-memory lazy commit: the
  vectored exception handler was hand-assembled x64; it now has a generated AArch64 twin
  (`Arm64Emitter.EmitLazyCommitHandler`, installed by `HostVirtualMemory` on Windows ARM64).
- Linux ARM64: memfd dual mapping; `__clear_cache` has no libc export, so `ExecutableMemory` generates the
  DC CVAU / DSB / IC IVAU / DSB / ISB loop (line sizes from CTR_EL0) into a page of its own, made executable with
  mprotect.
- Android: memfd (API 30+) or ASharedMemory (libandroid, API 26+) dual mapping, the same flush helper; RWX only as the
  last resort.
- macOS ARM64: MAP_JIT + `pthread_jit_write_protect_np` around writes + `sys_icache_invalidate` (unchanged).

### Coverage versus x64
Everything the x64 backend inlines is inline on ARM64 too (the frontend and IR are shared): the integer core, flags,
fastmem loads/stores, LDM/STM, linking, the indirect-exit table, SVC/exits, CLREX/barriers, MRC/MCR, LDREX/STREX and
LDREXD/STREXD, VFP transfers and scalar fast paths, VCVTB/VCVTT, and every `VecKind` (ARM64's `SupportsVec` is true for
all kinds; on x64 the FMA/F16C/AVX2 kinds depend on the host). The callouts are the same on both (see "Still
callouts" above: VQSHL/VRSHL register, the remaining ThreeDiff forms, VCVT fixed point, byte/halfword exclusives, SWP,
MRS/MSR, ...), except VSHL (register) on 8/16-bit lanes, inline only on ARM64 (x86 has no variable 8/16-bit shifts).

### Verification (this machine is x64 Windows; no ARM64 hardware)
- Encodings: `Arm64AssemblerTests`, 302 instructions against GNU as (aarch64-linux-gnu-as 2.40 from Debian's
  binutils-aarch64-linux-gnu, installed in WSL without root via `apt-get download` + `dpkg -x`), plus a DecodeBitMasks
  round trip of every 32-bit logical immediate.
- Unicorn (`Arm64Sandbox`, fast suite): the generated code, trampoline, epilogue, links and dispatch table included,
  runs in Unicorn's AArch64 model against the real process memory: guest space and code arena mapped with
  `uc_mem_map_ptr` at their host addresses, other host pages (CpuState, link slots, generation table) mapped on
  demand after VirtualQuery; callouts reach a stub whose hook calls the real callout bridge, so the handlers run
  natively on the same state. `NativeJitTests.Arm64.cs`: smoke blocks; random blocks vs the interpreter (ARM and
  Thumb, incl. 2-register spill stress); VFP with special operands in and out of the Vita FPSCR mode; every NEON
  class with special values (QC included); the exclusive-monitor scenarios (word and doubleword, incl. the
  unaligned path); chaining through link slots and the dispatch table with dispatcher-identical counters; the cache
  flush helper; the Windows-on-ARM lazy-commit handler (VirtualAlloc stubbed). Slow variants run 30,000 blocks per
  mode. Result: no differences (the only failure found, the full-value condition compare, is fixed).
- A real AArch64 process: the same test methods plus the dispatcher tests (`CpuCore.Run` selecting the ARM64
  backend, background compilation, linking, invalidation, IL tier-up, a 4-thread LDREX/STREX increment) in a
  linux-arm64 self-contained build run under qemu-aarch64 7.2 user mode in WSL (Debian arm64 libc/libstdc++/ICU
  unpacked into a sysroot): code arena "dual-mapped W^X" (memfd), generated cache flush, real AAPCS64 callouts into
  managed handlers. Largest run: 15,000 random integer blocks (ARM, Thumb, spill stress), 9,836 VFP blocks, 5,400
  NEON blocks (600 per class), the smoke/exclusive/linking tests and the four dispatcher tests: all identical to the
  interpreter (2 min under qemu). Recipe: publish a scratch console that compiles the test sources (assembly name
  VitaSharp.Tests for internals access) with `-r linux-arm64 --self-contained`, run it with `qemu-aarch64-static -L
  <sysroot>` and `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1`.
- On ARM64 hosts the ARM64 tests run natively instead of in the sandbox (`IArm64Runner`). Caveat found there:
  Unicorn 2.1's AArch64 TCG backend emits an UNDEFINED `neg v.1d` for a 64-bit-lane VNEG, which kills the process, so
  the tests' NEON encoding vetting (`SimdDefined`, Unicorn ARM32) only uses preloaded verdicts on ARM64 hosts (none by
  default: random blocks get no NEON slots there; the qemu run preloaded verdicts computed on x64).
- x64 unchanged: fast suite (`Category!=Slow`) 843/843 after merging main 7f26c85, 0 build warnings; kz_skin on the
  branch build: all 4500 frames, gameplay reached, native x64 backend 100.0% inline (static), 0 native faults.

### Still missing before a real Android device
1. Real hardware (an Android phone, a Raspberry Pi 4/5, Apple silicon): everything so far ran in Unicorn or qemu-user.
   Only hardware shows cross-core ordering under real contention in the link/dispatch/exclusive paths (qemu-user is
   not a weak-memory model), I/D cache behaviour of the dual mapping, and performance.
2. The Android host: the front end exists (`src/VitaSharp.Android`, not run yet) and runs on Mono with its JIT, so the
   IL backend stays available next to this one (Native AOT, not used, would have no IL backend;
   [docs/android/README.md](../android/README.md), "Runtime and CPU backends"); confirm on a device that SELinux allows
   executing the memfd/ashmem RX view (what ART does); 16 KiB-page devices (the arena is page-agnostic on POSIX,
   GuestMemory protections assume 4 KiB).
3. macOS ARM64: untested; hardened builds need `com.apple.security.cs.allow-jit`.
4. Fault reporting inside generated code (the x64 vectored filter with disassembly) has no ARM64 equivalent, and
   `VITASHARP_NATIVE_DUMP` prints ARM64 code as hex words (no bundled disassembler).
5. Performance: done in the sandbox on branch perf/arm64-jit (pinned counters, fusion, SIMD cache, lazy FPCR, see
   above); falta medir no aparelho.

