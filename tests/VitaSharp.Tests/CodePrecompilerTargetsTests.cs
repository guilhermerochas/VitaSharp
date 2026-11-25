using VitaSharp.Core.Cpu;
using VitaSharp.Core.Loader;
using VitaSharp.Core.Memory;

namespace VitaSharp.Tests;

/// <summary>
/// The targets the code precompilation finds beyond static control flow (<see cref="CodePrecompiler"/>): the cases of a
/// Thumb-2 switch (TBB/TBH [PC, Rm] with the compiler's bound check before it) and the functions a module reaches only
/// through pointers (its relocations into its code: vtables, callback tables, MOVW/MOVT and literal pool addresses,
/// <see cref="ModuleCodeMap"/>).
/// </summary>
[Collection("guest")]
public sealed class CodePrecompilerTargetsTests(GuestMemoryFixture fixture)
{
    private static void W16(uint address, ushort value) => GuestMemory.Write(address, value);

    private static DecodedInst DecodeThumb32(uint address) =>
        Decoder.DecodeThumb32(((uint)GuestMemory.Read<ushort>(address) << 16) | GuestMemory.Read<ushort>(address + 2), address, false, 14);

    private static List<(uint Pc, bool Thumb)> TableTargets(uint tbb)
    {
        var exits = new List<(uint Pc, bool Thumb)>();
        CodePrecompiler.JumpTableTargets(DecodeThumb32(tbb), exits);
        return exits;
    }

    [Fact]
    public void TbbCasesComeFromTheTableBoundedByTheCompareBefore()
    {
        uint code = fixture.Memory.Alloc(0x1000, "precompile tbb");
        W16(code + 0x00, 0x2803); // cmp r0, #3
        W16(code + 0x02, 0xD810); // bhi default
        W16(code + 0x04, 0xE8DF); // tbb [pc, r0]
        W16(code + 0x06, 0xF000);
        // the table at code + 8: 4 byte entries (cases 0-3), halfwords from the table
        GuestMemory.Write(code + 0x08, (byte)2);
        GuestMemory.Write(code + 0x09, (byte)3);
        GuestMemory.Write(code + 0x0A, (byte)4);
        GuestMemory.Write(code + 0x0B, (byte)4);
        GuestMemory.Write(code + 0x0C, (byte)0x7F); // past the bound: not a case
        Assert.Equal([(code + 0x0C, true), (code + 0x0E, true), (code + 0x10, true), (code + 0x10, true)], TableTargets(code + 0x04));

        // BHS/BCS: the bound itself is out of range (n entries)
        W16(code + 0x02, 0xD210); // bcs default
        Assert.Equal(3, TableTargets(code + 0x04).Count);

        // no bound check before it, or one on another register: the table's size is unknown, nothing is taken
        W16(code + 0x00, 0x2903); // cmp r1, #3
        Assert.Empty(TableTargets(code + 0x04));
        W16(code + 0x00, 0x2803);
        W16(code + 0x02, 0xD010); // beq: not a bound check
        Assert.Empty(TableTargets(code + 0x04));
    }

    [Fact]
    public void TheBoundCheckMayBeSpreadOverScheduledInstructions()
    {
        // what a compiler schedules between the CMP, the branch to the default case and the table branch (the shapes
        // seen in a game: loads of other registers, VFP moves, a high-register add)
        uint code = fixture.Memory.Alloc(0x1000, "precompile tbb scheduled");
        W16(code + 0x10, 0x2B09); // cmp r3, #9
        W16(code + 0x12, 0x7A0A); // ldrb r2, [r1, #8]
        W16(code + 0x14, 0x7A89); // ldrb r1, [r1, #10]
        W16(code + 0x16, 0xD81E); // bhi default
        W16(code + 0x18, 0xE8DF); // tbb [pc, r3]
        W16(code + 0x1A, 0xF003);
        Assert.Equal(10, TableTargets(code + 0x18).Count);

        // CMP, then VFP and a high-register add, then the branch
        W16(code + 0x20, 0x2B06); // cmp r3, #6
        W16(code + 0x22, 0xEEBD); // vcvt.s32.f32 s0, s0
        W16(code + 0x24, 0x0AC0);
        W16(code + 0x26, 0xEE10); // vmov r0, s0
        W16(code + 0x28, 0x0A10);
        W16(code + 0x2A, 0x4406); // add r6, r0
        W16(code + 0x2C, 0xD85C); // bhi default
        W16(code + 0x2E, 0xE8DF); // tbb [pc, r3]
        W16(code + 0x30, 0xF003);
        Assert.Equal(7, TableTargets(code + 0x2E).Count);

        // CMP.W, BHI, then stores and a load of other registers before a TBH on LR
        W16(code + 0x60, 0xF1BE); // cmp.w lr, #38
        W16(code + 0x62, 0x0F26);
        W16(code + 0x64, 0xD8ED); // bhi default
        W16(code + 0x66, 0x9119); // str r1, [sp, #100]
        W16(code + 0x68, 0x9812); // ldr r0, [sp, #72]
        W16(code + 0x6A, 0xE8DF); // tbh [pc, lr, lsl #1]
        W16(code + 0x6C, 0xF01E);
        Assert.Equal(39, TableTargets(code + 0x6A).Count);

        // an instruction in between that writes the index, or sets the flags: the bound is not known
        W16(code + 0x12, 0x7A0B); // ldrb r3, [r1, #8]
        Assert.Empty(TableTargets(code + 0x18));
        W16(code + 0x12, 0x3201); // adds r2, #1
        Assert.Empty(TableTargets(code + 0x18));
        W16(code + 0x2A, 0x4403); // add r3, r0
        Assert.Empty(TableTargets(code + 0x2E));
        W16(code + 0x26, 0xEE10); // vmov r3, s0
        W16(code + 0x28, 0x3A10);
        W16(code + 0x2A, 0x4406);
        Assert.Empty(TableTargets(code + 0x2E));
        W16(code + 0x66, 0xF85E); // ldr r1, [lr], #4: writes the index back
        W16(code + 0x68, 0x1B04);
        Assert.Empty(TableTargets(code + 0x6A));
    }

    [Fact]
    public void TbhWithWideCompareAndBranch()
    {
        uint code = fixture.Memory.Alloc(0x1000, "precompile tbh");
        W16(code + 0x00, 0xF1B9); // cmp.w r9, #2
        W16(code + 0x02, 0x0F02);
        W16(code + 0x04, 0xF200); // bhi.w default
        W16(code + 0x06, 0x8100);
        W16(code + 0x08, 0xE8DF); // tbh [pc, r9, lsl #1]
        W16(code + 0x0A, 0xF019);
        W16(code + 0x0C, 0x0003); // halfword entries: target = table + 2 * entry
        W16(code + 0x0E, 0x0010);
        W16(code + 0x10, 0x0100);
        Assert.Equal([(code + 0x12, true), (code + 0x2C, true), (code + 0x20C, true)], TableTargets(code + 0x08));

        // a table addressed by a register (not PC) is not followed
        W16(code + 0x08, 0xE8D3); // tbh [r3, r9, lsl #1]
        Assert.Empty(TableTargets(code + 0x08));
    }

    /// <summary>
    /// A module whose code (segment 0) has three functions in its exception index, F (0x100), G (0x200), H (0x300),
    /// and K (0x400, an ARM function) and S (0x500, a "string" at an odd address in the code segment), and whose data
    /// (segment 1) holds a vtable of G and K. F references the vtable (a MOVW/MOVT of a data address, as a
    /// constructor storing the vtable does) and H (a literal pool word); nothing references the string as code.
    /// </summary>
    private LoadedModule PointerModule(out uint code, out uint data)
    {
        code = fixture.Memory.Alloc(0x1000, "precompile pointers code");
        data = fixture.Memory.Alloc(0x1000, "precompile pointers data");
        var module = new LoadedModule
        {
            Name = "pointers",
            Segments = { [0] = new SegmentInfo(code, 0x81000000, 0x1000), [1] = new SegmentInfo(data, 0x81001000, 0x1000) },
            SegmentFlags = { [0] = (5, 0x1000, 0x1000), [1] = (6, 0x1000, 0x1000) },
            ExidxTop = code + 0x800,
            ExidxEnd = code + 0x818,
        };
        GuestMemory.W32(code + 0x800, unchecked((uint)(0x100 - 0x800)) & 0x7FFFFFFF);
        GuestMemory.W32(code + 0x804, 1);
        GuestMemory.W32(code + 0x808, unchecked((uint)(0x200 - 0x808)) & 0x7FFFFFFF);
        GuestMemory.W32(code + 0x80C, 1);
        GuestMemory.W32(code + 0x810, unchecked((uint)(0x300 - 0x810)) & 0x7FFFFFFF);
        GuestMemory.W32(code + 0x814, 1);
        GuestMemory.W32(code + 0x400, 0xE92D4010); // K: push {r4, lr} (ARM)
        W16(code + 0x500, 0x6548); // S: "He" (not a prologue, not a function start)
        // relocations: F's MOVW (site F + 4) of the vtable in data, F's literal pool word (F + 0x40) = H | 1,
        // the vtable (data + 0x10): G | 1, K, S | 1; a pointer from data to the odd string (data + 0x40)
        module.AbsoluteRelocations.AddRange([
            (code + 0x104, data + 0x10),
            (code + 0x140, code + 0x301),
            (data + 0x10, code + 0x201),
            (data + 0x14, code + 0x400),
            (data + 0x18, code + 0x501),
            (data + 0x40, code + 0x501),
        ]);
        return module;
    }

    [Fact]
    public void FunctionsReferencedByAFunctionAndItsTablesAreFound()
    {
        LoadedModule module = PointerModule(out uint code, out uint data);
        var map = ModuleCodeMap.Build(module);
        var targets = new List<(uint Pc, bool Thumb)>();
        map.PointersOfFunction(code + 0x100, targets);
        // H from the literal pool; G and K from the vtable F references; never the string
        Assert.Equal([(code + 0x200, true), (code + 0x400, false), (code + 0x300, true)], targets);
        Assert.Equal(3, map.CodePointerCount); // G, H, K (the string twice: never)

        // G references nothing; an address that is not a function start finds nothing
        targets.Clear();
        map.PointersOfFunction(code + 0x200, targets);
        Assert.Empty(targets);
        map.PointersOfFunction(code + 0x102, targets);
        Assert.Empty(targets);
        Assert.Equal(code + 0x100, map.FunctionContaining(code + 0x150));
        Assert.False(map.IsFunction(data + 0x11, out _)); // data is never code
    }

    /// <summary>Thumb-2 MOVW (<paramref name="top"/> false) or MOVT (encoding T3/T1) of a half of <paramref name="value"/>.</summary>
    private static void ThumbMoveWide(uint at, bool top, uint register, uint value)
    {
        uint half = top ? value >> 16 : value & 0xFFFF;
        W16(at, (ushort)((top ? 0xF2C0u : 0xF240u) | ((half >> 11) & 1) << 10 | (half >> 12)));
        W16(at + 2, (ushort)(((half >> 8) & 7) << 12 | register << 8 | (half & 0xFF)));
    }

    [Fact]
    public void AFixedAddressExecutableHasTheReferencesOfAFunctionInItsWordsAndMovwMovt()
    {
        // the same module without relocations (an executable loaded at its fixed address): F's references are only in
        // its bytes
        LoadedModule module = PointerModule(out uint code, out uint data);
        module.AbsoluteRelocations.Clear();
        ThumbMoveWide(code + 0x104, false, 1, data + 0x10); // F: movw r1, #:lower16:vtable
        W16(code + 0x108, 0xBF00); //                           nop (scheduled between the pair)
        ThumbMoveWide(code + 0x10A, true, 1, data + 0x10); //   movt r1, #:upper16:vtable
        ThumbMoveWide(code + 0x110, false, 2, data + 0x40); //  movw r2, #:lower16:string pointer
        ThumbMoveWide(code + 0x114, true, 3, data + 0x40); //   movt r3 (another register: not a pair)
        GuestMemory.W32(code + 0x140, code + 0x301); //          F's literal pool: H
        GuestMemory.W32(data + 0x10, code + 0x201); //           the vtable: G, K, then the string ends it
        GuestMemory.W32(data + 0x14, code + 0x400);
        GuestMemory.W32(data + 0x18, code + 0x501);
        GuestMemory.W32(data + 0x1C, code + 0x201);
        GuestMemory.W32(data + 0x40, code + 0x501);
        var targets = new List<(uint Pc, bool Thumb)>();
        ModuleCodeMap.Build(module).PointersOfFunction(code + 0x100, targets);
        Assert.Equal(new HashSet<(uint Pc, bool Thumb)> { (code + 0x200, true), (code + 0x400, false), (code + 0x300, true) }, targets.ToHashSet());
        // the table stops at its first slot that is not a function (G again after the string is not taken)
        Assert.Equal(1, targets.Count(t => t.Pc == code + 0x200));

        // G's range holds no reference
        targets.Clear();
        ModuleCodeMap.Build(module).PointersOfFunction(code + 0x200, targets);
        Assert.Empty(targets);
    }

    [Fact]
    public void ModuleRelocationsRecordTheAbsoluteAddressesTheyWrite()
    {
        uint seg0 = fixture.Memory.Alloc(0x1000, "precompile relocations code");
        uint seg1 = fixture.Memory.Alloc(0x1000, "precompile relocations data");
        var segments = new Dictionary<int, SegmentInfo>
        {
            [0] = new SegmentInfo(seg0, 0x81000000, 0x1000),
            [1] = new SegmentInfo(seg1, 0x81001000, 0x1000),
        };
        // format 0 (12 bytes): symbol segment 0, ABS32, patch segment 1, addend 0x201, offset 0x10: data + 0x10 = code + 0x201
        // then a PC-relative one (REL32, not an address): not recorded
        var stream = new List<byte>();
        stream.AddRange(BitConverter.GetBytes(0u | (0u << 4) | (2u << 8) | (1u << 16)));
        stream.AddRange(BitConverter.GetBytes(0x201u));
        stream.AddRange(BitConverter.GetBytes(0x10u));
        stream.AddRange(BitConverter.GetBytes(0u | (0u << 4) | (3u << 8) | (1u << 16)));
        stream.AddRange(BitConverter.GetBytes(0x40u));
        stream.AddRange(BitConverter.GetBytes(0x20u));
        List<(uint Site, uint Value)> targets = Relocation.RelocateCollectingTargets(stream.ToArray(), segments);
        Assert.Equal([(seg1 + 0x10, seg0 + 0x201)], targets);
        Assert.Equal(seg0 + 0x201, GuestMemory.R32(seg1 + 0x10));
    }
}
