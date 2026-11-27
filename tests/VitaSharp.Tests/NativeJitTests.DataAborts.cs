using VitaSharp.Core.Cpu;
using VitaSharp.Core.Cpu.Backends;
using VitaSharp.Core.Cpu.Native;
using VitaSharp.Core.Memory;

namespace VitaSharp.Tests;

/// <summary>
/// Data aborts in every CPU backend (<see cref="DataAborts"/>): with the guards armed (a data abort handler exists), an
/// access to a page no block maps stops the block at the faulting instruction with <see cref="ExitReason.DataAbort"/>,
/// R15/SvcPc at the instruction, the fault address and status, and every register and flag as they were before the
/// instruction (a base register written back by it keeps its old value), also when earlier instructions of the block
/// left values only in the translator's cache. Accesses to mapped pages give exactly the unarmed results, and unarmed
/// translation is unchanged (no guard in the IR).
/// </summary>
public sealed unsafe partial class NativeJitTests
{
    /// <summary>A page no block maps (the guest window is reserved there, an unguarded access reads zeros).</summary>
    private const uint AbortTestAddress = 0xFFD0_0000;

    // mov r3, #9; adds r2, r2, #1; ldr r0, [r1, #4]!; mov r5, #1; b .
    private static readonly uint[] s_abortLoadArm = [0xE3A03009, 0xE2922001, 0xE5B10004, 0xE3A05001, 0xEAFFFFFE];

    // movs r0, #7; cmp r0, r0; it eq; streq r0, [r1]; movs r1, #9; b .
    private static readonly ushort[] s_abortStoreThumb = [0x2007, 0x4280, 0xBF08, 0x6008, 0x2109, 0xE7FE];

    // mov r6, #3; ldmia r1!, {r2-r5}; b .   (r1 = 8 bytes before an unmapped page: the third word faults)
    private static readonly uint[] s_abortLdmArm = [0xE3A06003, 0xE8B1003C, 0xEAFFFFFE];

    // ldrh r0, [r1, #2]!; b .   and   vldmia r1!, {d0-d1}; b .   (forms whose handlers write the base back first)
    private static readonly uint[][] s_abortWritebackArm = [[0xE1F100B2, 0xEAFFFFFE], [0xECB10B04, 0xEAFFFFFE]];

    private delegate void BlockRunner(CpuState* s, Block b);

    private static Block AbortBlock(bool thumb, bool armed, uint[]? arm = null)
    {
        Span<byte> code = GuestMemory.Span(CodeBase, 0x1000);
        code.Clear();
        if (arm != null)
        {
            for (int i = 0; i < arm.Length; i++)
            {
                BitConverter.TryWriteBytes(code[(4 * i)..], arm[i]);
            }
        }
        else if (thumb)
        {
            for (int i = 0; i < s_abortStoreThumb.Length; i++)
            {
                BitConverter.TryWriteBytes(code[(2 * i)..], s_abortStoreThumb[i]);
            }
        }
        else
        {
            for (int i = 0; i < s_abortLoadArm.Length; i++)
            {
                BitConverter.TryWriteBytes(code[(4 * i)..], s_abortLoadArm[i]);
            }
        }

        var cache = new BlockCache();
        if (armed)
        {
            cache.ArmDataAborts();
        }

        return cache.Get(CodeBase, thumb);
    }

    private static void AbortState(CpuState* s, bool thumb, bool armed, uint baseAddress)
    {
        *s = default;
        s->MemBase = GuestMemory.Base;
        s->Fpscr = 0x0300_0000;
        s->R[0] = 0xA0A0_A0A0;
        s->R[1] = baseAddress;
        s->R[2] = 0x7FFF_FFFF; // adds r2, #1 overflows: N and V set
        s->R[5] = 0x5555_5555;
        s->R[13] = DataBase + 0x100;
        s->R[15] = CodeBase;
        s->Thumb = thumb ? 1u : 0u;
        s->AbortGuard = armed ? 1u : 0u;
    }

    /// <summary>The armed block faults with the state from before the faulting instruction; with a mapped base it
    /// matches the interpreter without guards exactly.</summary>
    private void CheckDataAborts(string backend, BlockRunner run)
    {
        EnsureGuestPages();
        Assert.Equal(0, GuestMemory.MappedTable(GuestMemory.Base)[AbortTestAddress >> 12]);
        CpuState* s = CpuState.Allocate();
        try
        {
            // ARM: a load with base writeback after instructions whose results are still only cached
            Block load = AbortBlock(thumb: false, armed: true);
            AbortState(s, thumb: false, armed: true, AbortTestAddress);
            run(s, load);
            Assert.True(ExitReason.DataAbort == s->Exit, $"{backend}: exit {s->Exit}");
            Assert.Equal(CodeBase + 8, s->R[15]);
            Assert.Equal(CodeBase + 8, s->SvcPc);
            Assert.Equal(AbortTestAddress + 4, s->AbortAddr);
            Assert.Equal(DataAborts.TranslationFault, s->AbortStatus);
            Assert.Equal(9u, s->R[3]);
            Assert.Equal(0x8000_0000u, s->R[2]);
            Assert.Equal((1u, 0u, 0u, 1u), (s->N, s->Z, s->C, s->V));
            Assert.Equal(AbortTestAddress, s->R[1]); // the writeback did not happen
            Assert.Equal(0xA0A0_A0A0u, s->R[0]);
            Assert.Equal(0x5555_5555u, s->R[5]);

            // Thumb: a conditional store inside an IT block
            Block store = AbortBlock(thumb: true, armed: true);
            AbortState(s, thumb: true, armed: true, AbortTestAddress);
            run(s, store);
            Assert.True(ExitReason.DataAbort == s->Exit, $"{backend}: exit {s->Exit}");
            Assert.Equal(CodeBase + 6, s->R[15]);
            Assert.Equal(CodeBase + 6, s->SvcPc);
            Assert.Equal(AbortTestAddress, s->AbortAddr);
            Assert.Equal(DataAborts.TranslationFault | DataAborts.WriteNotRead, s->AbortStatus);
            Assert.Equal(7u, s->R[0]);
            Assert.Equal(AbortTestAddress, s->R[1]);
            Assert.Equal((0u, 1u, 1u, 0u), (s->N, s->Z, s->C, s->V));
            Assert.Equal(0x08u, BlockBuilder.ItStateAt(store, CodeBase + 6)); // IT EQ, one instruction
            Assert.Equal(0u, BlockBuilder.ItStateAt(store, CodeBase + 8));

            // LDM running from a mapped page into an unmapped one: the fault address is the start of that page and the
            // base is not written back (the registers loaded before the fault are UNKNOWN in the ARM ARM: not checked)
            if (GuestMemory.MappedTable(GuestMemory.Base)[(DataBase + DataSize) >> 12] == 0)
            {
                Block ldm = AbortBlock(thumb: false, armed: true, s_abortLdmArm);
                AbortState(s, thumb: false, armed: true, DataBase + DataSize - 8);
                run(s, ldm);
                Assert.True(ExitReason.DataAbort == s->Exit, $"{backend}: LDM exit {s->Exit}");
                Assert.Equal(CodeBase + 4, s->SvcPc);
                Assert.Equal(DataBase + DataSize, s->AbortAddr);
                Assert.Equal(DataAborts.TranslationFault, s->AbortStatus);
                Assert.Equal(DataBase + DataSize - 8, s->R[1]);
                Assert.Equal(3u, s->R[6]);
            }

            // the base of a faulting access is never written back
            foreach (uint[] program in s_abortWritebackArm)
            {
                AbortState(s, thumb: false, armed: true, AbortTestAddress);
                run(s, AbortBlock(thumb: false, armed: true, program));
                Assert.True(ExitReason.DataAbort == s->Exit, $"{backend}: {program[0]:X8} exit {s->Exit}");
                Assert.Equal(CodeBase, s->SvcPc);
                Assert.Equal(AbortTestAddress, s->R[1]);
            }

            // mapped pages: the armed block gives what the unguarded interpreter gives
            foreach (bool thumb in new[] { false, true })
            {
                uint mapped = DataBase + 0x400;
                AbortState(s, thumb, armed: false, mapped);
                CpuCore.Interpret(s, AbortBlock(thumb, armed: false));
                Snapshot expected = Take(s, null);
                AbortState(s, thumb, armed: true, mapped);
                run(s, AbortBlock(thumb, armed: true));
                Snapshot armed = Take(s, null);
                Assert.Equal("", Diff(expected, armed));
            }
        }
        finally
        {
            CpuState.Free(s);
        }
    }

    [Fact]
    public void InterpreterRaisesPreciseDataAborts() => CheckDataAborts("interpreter", static (s, b) => CpuCore.Interpret(s, b));

    [Fact]
    public void IlBackendRaisesPreciseDataAborts()
    {
        if (!IlJitCompiler.Instance.IsSupported)
        {
            return; // no dynamic code (Native AOT): there is no IL backend to check
        }

        CheckDataAborts("il", static (s, b) => IlJitCompiler.Instance.Compile(b)!.Managed!(s));
    }

    [NativeFact]
    public void NativeX64RaisesPreciseDataAborts() =>
        CheckDataAborts("x64", static (s, b) => RunNative(s, NativeX64Compiler.Instance.Compile(b)!.NativeEntry));

    [Arm64SandboxFact]
    public void NativeArm64RaisesPreciseDataAborts()
    {
        using IArm64Runner sb = Arm64Runner.Create();
        CheckDataAborts("arm64", (s, b) => RunArm64(sb, s, NativeArm64Compiler.Instance.Compile(b, sb.Stubs)!.NativeEntry));
    }

    /// <summary>Without a data abort handler the translation is what it always was: no guard op in the IR (no cost),
    /// and an unmapped access reads zeros in every backend, as before.</summary>
    [Fact]
    public void WithoutDataAbortHandlersTranslationHasNoGuards()
    {
        EnsureGuestPages();
        foreach (bool thumb in new[] { false, true })
        {
            IrBlock plain = NativeArm64Compiler.NewFrontend().Translate(AbortBlock(thumb, armed: false));
            Assert.DoesNotContain(plain.Insts, x => x.Op == IrOp.PageMapped);
            int accesses = plain.Insts.Count(x => x.Op is IrOp.Load32 or IrOp.Store32);
            IrBlock guarded = NativeArm64Compiler.NewFrontend().Translate(AbortBlock(thumb, armed: true));
            Assert.Equal(accesses, guarded.Insts.Count(x => x.Op == IrOp.PageMapped));
        }

        CpuState* s = CpuState.Allocate();
        try
        {
            AbortState(s, thumb: false, armed: false, AbortTestAddress);
            CpuCore.Interpret(s, AbortBlock(thumb: false, armed: false));
            Assert.Equal(ExitReason.None, s->Exit);
            Assert.Equal(0u, s->R[0]); // the unmapped page reads zero
            Assert.Equal(AbortTestAddress + 4, s->R[1]);
        }
        finally
        {
            CpuState.Free(s);
        }
    }

    /// <summary>
    /// What the data abort guards cost on the code-quality loops (<see cref="s_perfBlocks"/>): ARM64 host instructions
    /// per iteration and static size with the guards off and on (sandbox count), and x64 time per iteration (host run).
    /// Measurement only: it fails only when a guarded run differs from the interpreter.
    /// </summary>
    [Fact]
    [Trait("Category", "Slow")]
    public void DataAbortGuardCostTable()
    {
        EnsureGuestPages();
        using var arena = new ArenaScope();
        var diffs = new List<string>();
        if (Arm64Runner.SkipReason == null && !Arm64Runner.Native)
        {
            using var sb = new Arm64Sandbox(countInstructions: true);
            output.WriteLine($"{"arm64 block",-16} | {"off",13} | {"on",13} | cost");
            foreach ((string name, bool thumb, string words) in s_perfBlocks)
            {
                (double off, int offWords, string offDiff) = MeasureLoop(sb, thumb, words, 40);
                (double on, int onWords, string onDiff) = MeasureLoop(sb, thumb, words, 40, guards: true);
                diffs.AddRange(new[] { offDiff, onDiff }.Where(d => d.Length > 0).Select(d => $"arm64 {name}:{d}"));
                output.WriteLine($"{name,-16} | {off,6:F1} {offWords,5}w | {on,6:F1} {onWords,5}w | {100 * (on / off - 1),5:F1}%");
            }
        }

        if (NativeX64Compiler.Instance.IsSupported)
        {
            output.WriteLine($"{"x64 block",-16} | {"off ns/it",10} | {"on ns/it",10} | cost");
            foreach ((string name, bool thumb, string words) in s_perfBlocks)
            {
                double off = TimeX64Loop(thumb, words, guards: false), on = TimeX64Loop(thumb, words, guards: true);
                output.WriteLine($"{name,-16} | {off,10:F2} | {on,10:F2} | {100 * (on / off - 1),5:F1}%");
            }
        }

        Assert.True(diffs.Count == 0, string.Join('\n', diffs));
    }

    /// <summary>x64: nanoseconds per iteration of a perf loop linked to itself (best of 200 runs of 2,000 iterations: the
    /// loops walk their data, which must stay inside the mapped test pages).</summary>
    private static double TimeX64Loop(bool thumb, string words, bool guards)
    {
        Span<byte> code = GuestMemory.Span(CodeBase, 0x1000);
        code.Clear();
        GuestBytes(thumb, words).CopyTo(code);
        var cache = new BlockCache();
        if (guards)
        {
            cache.ArmDataAborts();
        }

        Block b = cache.Get(CodeBase, thumb);
        CompiledBlock cb = NativeX64Compiler.Instance.Compile(b)!;
        const int Iterations = 2_000;
        double best = double.MaxValue;
        CpuState* s = CpuState.Allocate();
        nint backEdge = 0;
        try
        {
            // one iteration: the exit back to the loop returns unlinked; link it to the block itself
            PerfState(s, thumb, Iterations);
            s->CycleAcc = 0;
            s->CheckWindow = 1L << 40;
            s->LinkBreak = 0;
            NativeRuntime.Instance.Run(s, cb.NativeEntry);
            backEdge = s->LastExitSlot;
            Assert.True(backEdge != 0 && *(nint*)backEdge == 0);
            *(nint*)backEdge = cb.NativeEntry;
            for (int run = 0; run < 200; run++)
            {
                PerfState(s, thumb, Iterations);
                s->CycleAcc = 0;
                s->CheckWindow = 1L << 40;
                s->LinkBreak = 0;
                s->NBlocks = 0;
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                NativeRuntime.Instance.Run(s, cb.NativeEntry);
                double ns = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalNanoseconds / Iterations;
                Assert.True(s->Exit == ExitReason.None && s->NBlocks >= Iterations - 2, $"x64 loop stopped: exit {s->Exit} after {s->NBlocks} blocks");
                if (run > 0)
                {
                    best = Math.Min(best, ns);
                }
            }
        }
        finally
        {
            if (backEdge != 0)
            {
                *(nint*)backEdge = 0;
            }

            CpuState.Free(s);
        }

        return best;
    }

    /// <summary>
    /// ARM64 data abort guards keep their abort exits out of the hot path (<see cref="Arm64Emitter.A64Opt.ColdGuards"/>):
    /// with the guards armed, the code up to the last of a block's four guest stores grows by the guards alone (the
    /// mapping-table load and a <c>cbz</c> per access; 34 words here), not by an inline exit per access (72 more).
    /// FIFA 13 on the phone (a kernel module registers a data abort handler, so every access is guarded) ran blocks
    /// twice their size with a taken branch over each exit. <c>VITASHARP_NATIVE_A64_NOOPT=guard</c> puts them inline.
    /// </summary>
    [Fact]
    public void Arm64GuardExitsAreLaidOutAfterTheBlock()
    {
        EnsureGuestPages();
        // str r1,[r0]; str r2,[r0,#4]; str r3,[r0,#8]; str r4,[r0,#12]; b .
        uint[] stores = [0xE5801000, 0xE5802004, 0xE5803008, 0xE580400C, 0xEAFFFFFE];
        int plain = LastGuestAccessWord(AbortBlock(thumb: false, armed: false, stores), Arm64Emitter.Opts);
        int cold = LastGuestAccessWord(AbortBlock(thumb: false, armed: true, stores), Arm64Emitter.Opts);
        int inline = LastGuestAccessWord(AbortBlock(thumb: false, armed: true, stores), Arm64Emitter.Opts & ~Arm64Emitter.A64Opt.ColdGuards);
        output.WriteLine($"word of the last guest store: unguarded {plain}, cold guard exits {cold}, inline exits {inline}");
        // per access: the guard (lsr, sub, ldrb, cbz) and its own dirty-page mark (guarded stores do not share marks);
        // an inline exit adds a dozen words and a taken branch per access on top
        Assert.True(cold - plain <= 4 * 10, $"guarded hot path grew by {cold - plain} words for 4 accesses");
        Assert.True(inline - cold >= 4 * 12, $"inline exits: {inline - cold} words more than cold ones");
    }

    /// <summary>The index of the last guest store word (<c>str wT, [x20, wN, uxtw]</c>) in the ARM64 code of a block.</summary>
    private static int LastGuestAccessWord(Block b, Arm64Emitter.A64Opt opts)
    {
        Arm64Emitter.A64Opt saved = Arm64Emitter.Opts;
        Arm64Emitter.Opts = opts;
        nint* slots = (nint*)System.Runtime.InteropServices.NativeMemory.AllocZeroed(64 * 8);
        try
        {
            ArmFrontend frontend = NativeArm64Compiler.NewFrontend();
            frontend.DynamicExitOperands = true;
            IrBlock ir = frontend.Translate(b);
            IrPasses.DeadCode(ir);
            var stubs = new Arm64Emitter.Runtime { Epilogue = 0x7F00_0010_0000, Callout = 0x7F00_0020_0000, DispatchTable = 0x7F00_0030_0000 };
            var emitter = new Arm64Emitter();
            Assert.True(emitter.Prepare(ir, stubs, k => (nint)(slots + (k & 63))));
            ReadOnlySpan<byte> code = emitter.AssembleSpan();
            int last = -1;
            for (int w = 0; w + 4 <= code.Length; w += 4)
            {
                uint word = BitConverter.ToUInt32(code[w..]);
                // STR (register, 32-bit), option UXTW, S = 0, base x20 (the guest window)
                if ((word & 0xFFE0FC00) == 0xB8204800 && ((word >> 5) & 31) == 20)
                {
                    last = w / 4;
                }
            }

            Assert.True(last >= 0, "no guest store in the code");
            return last;
        }
        finally
        {
            Arm64Emitter.Opts = saved;
            System.Runtime.InteropServices.NativeMemory.Free(slots);
        }
    }
}
