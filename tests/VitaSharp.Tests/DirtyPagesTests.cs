using VitaSharp.Core.Cpu;
using VitaSharp.Core.Cpu.Backends;
using VitaSharp.Core.Cpu.Native;
using VitaSharp.Core.Memory;

namespace VitaSharp.Tests;

/// <summary>
/// The page dirty tracker behind the texture cache: every way of writing guest memory (HLE accessors, interpreted and
/// natively compiled CPU stores, exclusive stores) must mark the written page, read-only access must not, and the
/// sweep must report a page as written for one extra sweep (stores still in flight while it was cleared) and the
/// following page too (stores that cross a page end).
/// </summary>
[Collection("guest")]
public sealed unsafe class DirtyPagesTests(GuestMemoryFixture fx)
{
    const uint CodeBase = 0x0010_0000, DataBase = 0x0020_0000, DataSize = 0x10000;

    void EnsureGuestPages()
    {
        if (fx.Memory.FindBlock(CodeBase) == null) fx.Memory.TryAllocAt(CodeBase, 0x1000, "fuzz code");
        if (fx.Memory.FindBlock(DataBase) == null) fx.Memory.TryAllocAt(DataBase, DataSize, "fuzz data");
    }

    /// <summary>Sweeps twice with no writes in between: a stamp nothing earlier counts against.</summary>
    static uint FreshStamp()
    {
        DirtyPages.Sweep();
        DirtyPages.Sweep();
        return DirtyPages.Epoch;
    }

    static bool Written(uint addr, uint len, uint stamp) => DirtyPages.WrittenSince(addr, len, stamp);

    [Fact]
    public void HleWritesMarkPagesAndReadsDoNot()
    {
        Assert.True(DirtyPages.Enabled);
        EnsureGuestPages();
        uint stamp = FreshStamp();
        GuestMemory.Write<uint>(DataBase + 0x1010, 5);
        _ = GuestMemory.ReadSpan(DataBase + 0x3000, 0x1000)[0];
        _ = GuestMemory.Read<uint>(DataBase + 0x4000);
        GuestMemory.Span(DataBase + 0x6000, 16)[0] = 1;
        new Ptr<uint>(DataBase + 0x8004).Value = 7;
        GuestMemory.Fill(DataBase + 0xA000, 0xFF, 0x1001); // two pages
        DirtyPages.Sweep();
        Assert.True(Written(DataBase + 0x1000, 0x1000, stamp));
        Assert.False(Written(DataBase + 0x3000, 0x1000, stamp));
        Assert.False(Written(DataBase + 0x4000, 4, stamp));
        Assert.True(Written(DataBase + 0x6000, 4, stamp));
        Assert.True(Written(DataBase + 0x8000, 4, stamp));
        Assert.True(Written(DataBase + 0xB000, 1, stamp));
        Assert.False(Written(DataBase + 0xD000, 0x3000, stamp));
    }

    [Fact]
    public void SweepGraceAndPageCrossing()
    {
        EnsureGuestPages();
        uint stamp = FreshStamp();
        GuestMemory.Write<ushort>(DataBase + 0x2FFF, 0xABCD); // crosses into the next page
        uint e = DirtyPages.Sweep();
        Assert.True(Written(DataBase + 0x2000, 1, stamp));
        Assert.True(Written(DataBase + 0x3000, 1, stamp)); // the next page gets the same version
        Assert.False(Written(DataBase + 0x4000, 1, stamp));
        // validated right after that sweep: still counts as written once (the store may have been in flight)
        Assert.True(Written(DataBase + 0x2000, 1, e));
        uint later = DirtyPages.Sweep();
        Assert.False(Written(DataBase + 0x2000, 1, later));
    }

    public static TheoryData<string, uint, int, int> Stores => new()
    {
        // ARM words; r1 = target, r2/r3 = values; the target page must be marked, the probe page must not
        { "E5812008,E12FFF1E", 0x5000, 0x5000, 0x7000 },          // str r2,[r1,#8]; bx lr
        { "E5C12000,E12FFF1E", 0x5003, 0x5000, 0x7000 },          // strb r2,[r1]
        { "E1C120B0,E12FFF1E", 0x6002, 0x6000, 0x8000 },          // strh r2,[r1]
        { "ED810B00,E12FFF1E", 0x9000, 0x9000, 0xB000 },          // vstr d0,[r1] (64-bit store)
        { "E8810006,E12FFF1E", 0xC000, 0xC000, 0xE000 },          // stm r1,{r1,r2}
        { "E1910F9F,E1812F93,E12FFF1E", 0xD000, 0xD000, 0xF000 }, // ldrex r0,[r1]; strex r2,r3,[r1]
    };

    [Theory]
    [MemberData(nameof(Stores))]
    public void CpuStoresMarkPages(string words, uint target, int page, int probe)
    {
        EnsureGuestPages();
        var bytes = new List<byte>();
        foreach (var w in words.Split(',')) bytes.AddRange(BitConverter.GetBytes(Convert.ToUInt32(w, 16)));
        bytes.ToArray().CopyTo(GuestMemory.Span(CodeBase, 0x1000));
        var b = BlockBuilder.Build(CodeBase, false);
        bool native = NativeX64Compiler.Instance.IsSupported;
        foreach (bool useNative in native ? new[] { false, true } : [false])
        {
            var s = CpuState.Allocate();
            try
            {
                s->R[1] = DataBase + target;
                s->R[2] = 0x1234;
                s->R[3] = 0x5678;
                s->R[14] = CodeBase + 0x800;
                uint stamp = FreshStamp();
                if (useNative)
                {
                    var cb = NativeX64Compiler.Instance.Compile(b)!;
                    s->CycleAcc = 0; s->CheckWindow = 0; s->LinkBreak = 1; s->NBlocks = 0; s->NInstr = 0;
                    NativeRuntime.Instance.Run(s, cb.NativeEntry);
                    if (s->CalloutFault != 0) NativeRuntime.ThrowPendingFault(s);
                }
                else CpuCore.Interpret(s, b);
                DirtyPages.Sweep();
                Assert.True(Written(DataBase + (uint)page, 0x1000, stamp), $"{(useNative ? "native" : "interpreter")}: page not marked");
                Assert.False(Written(DataBase + (uint)probe, 0x1000, stamp), $"{(useNative ? "native" : "interpreter")}: unrelated page marked");
            }
            finally { CpuState.Free(s); }
        }
    }
}
