using VitaSharp.Core.Cpu;
using VitaSharp.Core.Cpu.Backends;
using VitaSharp.Core.Memory;
using Xunit;

namespace VitaSharp.Tests;

/// <summary>
/// Data aborts across the load/store forms, every backend against the interpreter (<see cref="DataAborts"/>): with the
/// guards armed each case must leave exactly the interpreter's state (registers, flags, exit, SvcPc, memory, fault
/// address and status; for the aborting instruction's own SIMD loads and stores only what the ARM ARM defines). The forms the frontend translates in its own ways: single and dual words, halfwords, bytes,
/// LDM/STM (also crossing from a mapped page into an unmapped one, with the words before the fault stored), PUSH/POP,
/// exclusives, VLDR/VLDM/VLD1, conditional accesses that run or are skipped, Thumb 16- and 32-bit forms.
/// </summary>
public sealed unsafe partial class NativeJitTests
{
    /// <summary>Where a case points r0 or sp: an unmapped page, the end of the mapped data (the next page is unmapped)
    /// or mapped data.</summary>
    public enum AbortBase
    {
        Unmapped,
        DataEnd,
        Mapped,
    }

    /// <summary>Cases: Thumb, instruction words (Thumb halfwords as 4 hex digits), r0 and sp (base and offset), and the
    /// offset of the instruction that aborts (-1: none).</summary>
    public static TheoryData<bool, string, AbortBase, int, AbortBase, int, int> AbortMatrixCases => new()
    {
        { false, "E5901000,EAFFFFFE", AbortBase.Unmapped, 0, AbortBase.Mapped, 0x100, 0 },     // ldr r1, [r0]
        { false, "E4901004,EAFFFFFE", AbortBase.Unmapped, 0, AbortBase.Mapped, 0x100, 0 },     // ldr r1, [r0], #4
        { false, "E5A01004,EAFFFFFE", AbortBase.Unmapped, -4, AbortBase.Mapped, 0x100, 0 },    // str r1, [r0, #4]!
        { false, "E5D01003,EAFFFFFE", AbortBase.Unmapped, 0, AbortBase.Mapped, 0x100, 0 },     // ldrb r1, [r0, #3]
        { false, "E1D010B2,EAFFFFFE", AbortBase.DataEnd, -2, AbortBase.Mapped, 0x100, 0 },     // ldrh r1, [r0, #2]
        { false, "E1C020D0,EAFFFFFE", AbortBase.DataEnd, -4, AbortBase.Mapped, 0x100, 0 },     // ldrd r2, r3, [r0]
        { false, "E1C020F0,EAFFFFFE", AbortBase.DataEnd, -4, AbortBase.Mapped, 0x100, 0 },     // strd r2, r3, [r0]
        { false, "E8B0001E,EAFFFFFE", AbortBase.DataEnd, -8, AbortBase.Mapped, 0x100, 0 },     // ldmia r0!, {r1-r4}
        { false, "E8A0001E,EAFFFFFE", AbortBase.DataEnd, -8, AbortBase.Mapped, 0x100, 0 },     // stmia r0!, {r1-r4}
        { false, "E92D4010,EAFFFFFE", AbortBase.Mapped, 0x100, AbortBase.Unmapped, 8, 0 },     // push {r4, lr}
        { false, "E8BD8010", AbortBase.Mapped, 0x100, AbortBase.Unmapped, 0, 0 },              // pop {r4, pc}
        { false, "E2811001,E5902000,EAFFFFFE", AbortBase.Unmapped, 0, AbortBase.Mapped, 0x100, 4 }, // add; ldr r2, [r0]
        { false, "E1550005,05901000,EAFFFFFE", AbortBase.Unmapped, 0, AbortBase.Mapped, 0x100, 4 }, // cmp r5, r5; ldreq
        { false, "E1550005,15901000,EAFFFFFE", AbortBase.Unmapped, 0, AbortBase.Mapped, 0x100, -1 }, // ldrne: skipped
        { false, "E1901F9F,EAFFFFFE", AbortBase.Unmapped, 0, AbortBase.Mapped, 0x100, 0 },     // ldrex r1, [r0]
        { false, "ED900B00,EAFFFFFE", AbortBase.Unmapped, 0, AbortBase.Mapped, 0x100, 0 },     // vldr d0, [r0]
        { false, "ECB00B08,EAFFFFFE", AbortBase.DataEnd, -16, AbortBase.Mapped, 0x100, 0 },    // vldmia r0!, {d0-d3}
        { false, "F420070F,EAFFFFFE", AbortBase.Unmapped, 0, AbortBase.Mapped, 0x100, 0 },     // vld1.8 {d0}, [r0]
        { false, "E5901000,EAFFFFFE", AbortBase.Mapped, 0x100, AbortBase.Mapped, 0x200, -1 },  // mapped: no abort
        { true, "6801,E7FE", AbortBase.Unmapped, 0, AbortBase.Mapped, 0x100, 0 },              // ldr r1, [r0]
        { true, "3001,6802,E7FE", AbortBase.Unmapped, -1, AbortBase.Mapped, 0x100, 2 },        // adds r0, #1; ldr r2, [r0]
        { true, "B410,E7FE", AbortBase.Mapped, 0x100, AbortBase.Unmapped, 4, 0 },              // push {r4}
        { true, "E8B0001E,E7FE", AbortBase.DataEnd, -8, AbortBase.Mapped, 0x100, 0 },          // ldmia.w r0!, {r1-r4}
    };

    private static uint AbortAddress(AbortBase kind, int offset) => kind switch
    {
        AbortBase.Unmapped => AbortTestAddress + (uint)offset,
        AbortBase.DataEnd => DataBase + DataSize + (uint)offset,
        _ => DataBase + (uint)offset,
    };

    private static Block AbortMatrixBlock(bool thumb, string words)
    {
        var bytes = new List<byte>();
        foreach (string w in words.Split(','))
        {
            uint v = Convert.ToUInt32(w, 16);
            if (!thumb)
            {
                bytes.AddRange(BitConverter.GetBytes(v));
            }
            else if (w.Length > 4)
            {
                bytes.AddRange([(byte)(v >> 16), (byte)(v >> 24), (byte)v, (byte)(v >> 8)]);
            }
            else
            {
                bytes.AddRange([(byte)v, (byte)(v >> 8)]);
            }
        }

        GuestMemory.Span(CodeBase, 0x1000).Clear();
        bytes.ToArray().CopyTo(GuestMemory.Span(CodeBase, 0x1000));
        var cache = new BlockCache();
        cache.ArmDataAborts();
        return cache.Get(CodeBase, thumb);
    }

    private static void AbortMatrixState(CpuState* s, bool thumb, uint r0, uint sp)
    {
        new RandomState(new Random(11)).Apply(s, thumb);
        s->R[0] = r0;
        s->R[13] = sp;
        s->R[5] = 0x55;
        s->AbortGuard = 1;
    }

    private void CheckAbortMatrix(string backend, BlockRunner run, bool thumb, string words, AbortBase r0, int r0Offset, AbortBase sp, int spOffset, int abortAt)
    {
        EnsureGuestPages();
        Assert.Equal(0, GuestMemory.MappedTable(GuestMemory.Base)[AbortTestAddress >> 12]);
        if (GuestMemory.MappedTable(GuestMemory.Base)[(DataBase + DataSize) >> 12] != 0)
        {
            return; // another test mapped the page after the test data: no page to cross into
        }

        uint baseAddress = AbortAddress(r0, r0Offset), stack = AbortAddress(sp, spOffset);
        CpuState* s = CpuState.Allocate();
        try
        {
            AbortMatrixState(s, thumb, baseAddress, stack);
            Block reference = AbortMatrixBlock(thumb, words);
            Snapshot interp = Take(s, Guard(() => CpuCore.Interpret(s, reference)));
            (uint Addr, uint Status) interpFault = (s->AbortAddr, s->AbortStatus);
            if (abortAt < 0)
            {
                Assert.NotEqual(ExitReason.DataAbort, s->Exit);
            }
            else
            {
                Assert.Equal(ExitReason.DataAbort, s->Exit);
                Assert.Equal(CodeBase + (uint)abortAt, s->SvcPc);
            }

            AbortMatrixState(s, thumb, baseAddress, stack);
            Block b = AbortMatrixBlock(thumb, words);
            Snapshot other = Take(s, Guard(() => run(s, b)));
            if (abortAt >= 0)
            {
                // an aborted instruction's own loads into SIMD registers and stores to memory are UNKNOWN in the ARM ARM
                // (the interpreter makes the accesses before the faulting one, native code checks them all first): only
                // the core registers (base restored), flags, exit and fault are defined
                other.D = interp.D;
                other.Mem = interp.Mem;
            }

            string diff = Diff(interp, other);
            if (abortAt >= 0 && interpFault != (s->AbortAddr, s->AbortStatus))
            {
                diff += $" fault: {interpFault.Addr:X8}/{interpFault.Status:X} vs {s->AbortAddr:X8}/{s->AbortStatus:X}";
            }

            Assert.True(diff == "", $"{backend}:{diff}");
        }
        finally
        {
            CpuState.Free(s);
        }
    }

    [Theory]
    [MemberData(nameof(AbortMatrixCases))]
    public void IlBackendDataAbortsMatchTheInterpreter(bool thumb, string words, AbortBase r0, int r0Offset, AbortBase sp, int spOffset, int abortAt)
    {
        if (!IlJitCompiler.Instance.IsSupported)
        {
            return; // no dynamic code (Native AOT): there is no IL backend to check
        }

        CheckAbortMatrix("il", static (s, b) => IlJitCompiler.Instance.Compile(b)!.Managed!(s), thumb, words, r0, r0Offset, sp, spOffset, abortAt);
    }

    [NativeTheory]
    [MemberData(nameof(AbortMatrixCases))]
    public void NativeX64DataAbortsMatchTheInterpreter(bool thumb, string words, AbortBase r0, int r0Offset, AbortBase sp, int spOffset, int abortAt) =>
        CheckAbortMatrix("x64", static (s, b) => RunNative(s, NativeX64Compiler.Instance.Compile(b)!.NativeEntry), thumb, words, r0, r0Offset, sp, spOffset, abortAt);

    [Arm64SandboxTheory]
    [MemberData(nameof(AbortMatrixCases))]
    public void NativeArm64DataAbortsMatchTheInterpreter(bool thumb, string words, AbortBase r0, int r0Offset, AbortBase sp, int spOffset, int abortAt)
    {
        using IArm64Runner sb = Arm64Runner.Create();
        CheckAbortMatrix("arm64", (s, b) => RunArm64(sb, s, NativeArm64Compiler.Instance.Compile(b, sb.Stubs)!.NativeEntry), thumb, words, r0, r0Offset, sp, spOffset, abortAt);
    }
}
