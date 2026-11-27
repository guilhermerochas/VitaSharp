using VitaSharp.Core.Cpu;
using VitaSharp.Core.Cpu.Backends;
using VitaSharp.Core.Cpu.Native;
using VitaSharp.Core.Memory;

namespace VitaSharp.Tests;

/// <summary>
/// The ARM64 return stack (<see cref="Arm64Emitter.A64Opt.ReturnStack"/>): guest calls push {return key, host landing},
/// guest returns whose target matches the top entry come back with a host RET instead of a dispatch-table lookup. The
/// programs run through a driver that links exits like <see cref="CpuCore"/> (slot of the static exit that returned,
/// patch site included) and never publishes anything in the indirect-exit table, so a return that misses the stack
/// goes back to the dispatcher.
/// </summary>
public sealed unsafe partial class NativeJitTests
{
    /// <summary>
    /// Two calls to a leaf function (ARM BL, BX LR): with the return stack the linked program runs in one entry into
    /// native code; without it each return misses the (empty) dispatch table and goes through the dispatcher. Registers,
    /// cycles and executed blocks equal the interpreter's either way.
    /// </summary>
    [Arm64SandboxFact]
    public void Arm64ReturnStackReturnsWithoutTheDispatcher()
    {
        uint[] prog =
        [
            0xE3A00000, // 00 mov r0, #0
            0xEB000003, // 04 bl f (0x18)
            0xEB000002, // 08 bl f
            0xE2801064, // 0C add r1, r0, #100
            0xEF000042, // 10 svc #0x42
            0xE320F000, // 14 nop
            0xE2800001, // 18 f: add r0, r0, #1
            0xE12FFF1E, // 1C bx lr
        ];
        long calls = NativeX64Compiler.ReturnStackCalls, returns = NativeX64Compiler.ReturnStackReturns;
        int with = RunCallProgram(prog, thumb: false, Arm64Emitter.Opts | Arm64Emitter.A64Opt.ReturnStack);
        // the F8 report's backend line counts them (two call exits, one return)
        Assert.True(NativeX64Compiler.ReturnStackCalls - calls >= 2 && NativeX64Compiler.ReturnStackReturns - returns >= 1);
        Assert.Contains("return stack:", NativeX64Compiler.Report());
        int without = RunCallProgram(prog, thumb: false, Arm64Emitter.Opts & ~Arm64Emitter.A64Opt.ReturnStack);
        output.WriteLine($"entries into native code on the linked run: return stack {with}, without {without}");
        Assert.Equal(1, with);
        Assert.Equal(3, without);
    }

    /// <summary>
    /// The contract around the stack: Thumb recursion deeper than its entries (POP {pc} returns, the outermost entry
    /// overwritten), a callee that returns somewhere else (LR changed: the key does not match), an ARM -> Thumb BLX and
    /// a conditional BL. Each program equals the interpreter (registers, flags, cycles, blocks) with the stack on and off.
    /// </summary>
    [Arm64SandboxFact]
    public void Arm64ReturnStackMatchesInterpreter()
    {
        // Thumb: r0 = 12; f recurses r0 times (push {r4, lr} ... pop {r4, pc}), r1 counts the calls
        ushort[] recursion =
        [
            0x200C, 0x2100,          // 00 movs r0, #12; movs r1, #0
            0, 0,                    // 04 bl f (0x10)
            0x2205, 0xDF42,          // 08 movs r2, #5; svc #0x42
            0xBF00, 0xBF00,          // 0C nop; nop
            0xB510, 0x3101,          // 10 f: push {r4, lr}; adds r1, #1
            0x3801, 0xD002,          // 14 subs r0, #1; beq done (0x1E)
            0, 0,                    // 18 bl f
            0xBF00,                  // 1C nop
            0xBD10,                  // 1E done: pop {r4, pc}
        ];
        ThumbBl(recursion, 0x04, 0x10);
        ThumbBl(recursion, 0x18, 0x10);
        // ARM: g returns 4 bytes past its return address (skips the mov r1), then a conditional BL (taken, then not)
        // and an ARM -> Thumb BLX to a leaf that returns with BX LR
        uint[] mixed =
        [
            0xEB000006, // 00 bl g (0x20)
            0xE3A01063, // 04 mov r1, #99 (skipped)
            0xE3500000, // 08 cmp r0, #0
            0x1B000006, // 0C blne h (0x2C)
            0x0B000005, // 10 bleq h (not taken)
            0xFA000007, // 14 blx t (0x38, Thumb)
            0xEF000042, // 18 svc #0x42
            0xE320F000, // 1C nop
            0xE2800001, // 20 g: add r0, r0, #1
            0xE28EE004, // 24 add lr, lr, #4
            0xE12FFF1E, // 28 bx lr
            0xE2822001, // 2C h: add r2, r2, #1
            0xE12FFF1E, // 30 bx lr
            0xE320F000, // 34 nop
            0x47703301, // 38 t: adds r3, #1; bx lr
        ];
        foreach (bool on in new[] { true, false })
        {
            Arm64Emitter.A64Opt opts = on ? Arm64Emitter.Opts | Arm64Emitter.A64Opt.ReturnStack : Arm64Emitter.Opts & ~Arm64Emitter.A64Opt.ReturnStack;
            int r1 = RunCallProgram(ToWords(recursion), thumb: true, opts);
            int r2 = RunCallProgram(mixed, thumb: false, opts);
            output.WriteLine($"return stack {(on ? "on" : "off")}: recursion {r1} entries, mixed {r2} entries");
            if (on)
            {
                // only the returns past the stack's depth and the one LR changed go through the dispatcher
                Assert.True(r1 <= 2, $"recursion: {r1} entries");
                Assert.True(r2 <= 2, $"mixed: {r2} entries");
            }
        }
    }

    /// <summary>Writes a T32 BL at halfword offset <paramref name="at"/> of <paramref name="code"/> to <paramref name="target"/> (byte offsets).</summary>
    private static void ThumbBl(ushort[] code, int at, int target)
    {
        int off = target - (at + 4);
        uint s = (uint)(off >> 24) & 1;
        uint i1 = (uint)(off >> 23) & 1, i2 = (uint)(off >> 22) & 1;
        uint j1 = (~(i1 ^ s)) & 1, j2 = (~(i2 ^ s)) & 1;
        code[at / 2] = (ushort)(0xF000 | s << 10 | (((uint)off >> 12) & 0x3FF));
        code[at / 2 + 1] = (ushort)(0xD000 | j1 << 13 | j2 << 11 | (((uint)off >> 1) & 0x7FF));
    }

    private static uint[] ToWords(ushort[] halves)
    {
        var words = new uint[(halves.Length + 1) / 2];
        for (int i = 0; i < halves.Length; i++)
        {
            words[i / 2] |= (uint)halves[i] << (16 * (i & 1));
        }

        return words;
    }

    /// <summary>
    /// Runs <paramref name="prog"/> (at CodeBase, stack in the data area) to its SVC in the interpreter, block by block,
    /// then natively with <paramref name="opts"/> until a run needs no new link, and compares the state. Returns the
    /// entries into native code of the last (fully linked) run.
    /// </summary>
    private int RunCallProgram(uint[] prog, bool thumb, Arm64Emitter.A64Opt opts)
    {
        EnsureGuestPages();
        Arm64Emitter.A64Opt saved = Arm64Emitter.Opts;
        Arm64Emitter.Opts = opts;
        CpuState* s = CpuState.Allocate();
        try
        {
            using IArm64Runner sb = Arm64Runner.Create();
            Span<byte> code = GuestMemory.Span(CodeBase, 0x1000);
            code.Clear();
            for (int i = 0; i < prog.Length; i++)
            {
                BitConverter.GetBytes(prog[i]).CopyTo(code[(i * 4)..]);
            }

            var blocks = new Dictionary<(uint, bool), Block>();
            Block BlockAt(uint pc, bool t)
            {
                if (!blocks.TryGetValue((pc, t), out Block? b))
                {
                    b = BlockBuilder.Build(pc, t);
                    blocks[(pc, t)] = b;
                }

                return b;
            }

            void Reset()
            {
                *s = default;
                s->MemBase = GuestMemory.Base;
                s->R[13] = DataBase + 0x1000;
                s->R[15] = CodeBase;
                s->Thumb = thumb ? 1u : 0u;
                GuestMemory.Span(DataBase, 0x1000).Clear();
            }

            Reset();
            long cycles = 0, executed = 0;
            for (int guard = 0; s->Exit == ExitReason.None; guard++)
            {
                Assert.True(guard < 10_000, "the interpreter run does not end");
                Block b = BlockAt(s->R[15], s->Thumb != 0);
                CpuCore.Interpret(s, b);
                executed++;
                cycles += b.Cycles;
            }
            CpuState reference = *s;

            var compiled = new Dictionary<Block, CompiledBlock>();
            nint EntryAt(uint pc, bool t)
            {
                Block b = BlockAt(pc, t);
                if (!compiled.TryGetValue(b, out CompiledBlock? cb))
                {
                    cb = NativeArm64Compiler.Instance.Compile(b, sb.Stubs)!;
                    Assert.NotNull(cb);
                    compiled[b] = cb;
                }

                return cb.NativeEntry;
            }

            int runs = 0;
            for (int pass = 0; pass < 8; pass++)
            {
                Reset();
                runs = 0;
                int links = 0;
                long nb = 0, cyc = 0;
                while (s->Exit == ExitReason.None)
                {
                    Assert.True(runs < 10_000, "the native run does not end");
                    s->CycleAcc = 0;
                    s->CheckWindow = 1L << 40;
                    s->LinkBreak = 0;
                    s->NBlocks = 0;
                    s->NInstr = 0;
                    sb.Run(s, EntryAt(s->R[15], s->Thumb != 0));
                    runs++;
                    nb += s->NBlocks;
                    cyc += s->CycleAcc;
                    if (s->LastExitSlot != 0 && *(nint*)s->LastExitSlot == 0)
                    {
                        LinkSlot(sb, s->LastExitSlot, EntryAt(s->R[15], s->Thumb != 0));
                        links++;
                    }
                }
                for (int r = 0; r < 16; r++)
                {
                    Assert.True(reference.R[r] == s->R[r], $"pass {pass}: r{r} {s->R[r]:X} != interpreter {reference.R[r]:X}");
                }

                Assert.Equal(reference.Cpsr, s->Cpsr);
                Assert.Equal(cycles, cyc);
                if (sb.Stubs.NoBlockCounts == false)
                {
                    Assert.Equal(executed, nb);
                }

                if (links == 0)
                {
                    return runs;
                }
            }
            Assert.Fail("the program kept needing new links");
            return runs;
        }
        finally
        {
            Arm64Emitter.Opts = saved;
            CpuState.Free(s);
        }
    }

    /// <summary>Links an exit slot the way <see cref="NativeRuntime.Link"/> does: the slot and, when the exit has a
    /// patch site, the site (a BL at a call site).</summary>
    private static void LinkSlot(IArm64Runner sb, nint slot, nint entry)
    {
        *(nint*)slot = entry;
        nint site = ((nint*)slot)[1];
        if (!sb.Stubs.PatchLinks || site == 0)
        {
            return;
        }

        int kind = (int)(site & Arm64Emitter.SiteKindMask);
        site &= ~(nint)Arm64Emitter.SiteKindMask;
        uint word = Arm64Emitter.SiteWord(site, kind, entry);
        if (word != 0)
        {
            NativeRuntime.Instance.Code.PatchWord((byte*)site, word);
        }
    }
}
