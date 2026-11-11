using VitaSharp.Core.Cpu;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;
using Mutex = VitaSharp.Core.Kernel.Mutex;
using Semaphore = VitaSharp.Core.Kernel.Semaphore;

namespace VitaSharp.Tests;

/// <summary>A type covering the field kinds the snapshot generator supports (VitaSharp.Core.Generators.SnapshotGenerator).</summary>
[GenerateSnapshot, SnapshotIgnoreFields("Ignored")]
public sealed partial class SnapshotProbe
{
    public enum Kind : byte
    {
        A, B, C
    }
    public int I;
    public uint U;
    public long L;
    public double D;
    public bool Flag;
    public Kind K;
    public string? S;
    public int[] Ints = [];
    public string[]? Names;
    public List<uint> List = new();
    public Dictionary<uint, string> Map = new();
    public readonly Dictionary<string, int> Ci = new(StringComparer.OrdinalIgnoreCase);
    public (uint Base, uint Size) Range;
    public int? Maybe;
    public SnapshotChild? Child;
    public readonly SnapshotChild Fixed = new();
    public List<SnapshotChild> Children = new();
    public object? Ignored;
    [SnapshotIgnore] public Action? Hook;
    public readonly int Config = 7;
    public int Extra;

    private void SaveExtra(SnapshotWriter w) => w.Write(Extra * 2);
    private void LoadExtra(SnapshotReader r) => Extra = r.ReadInt32() / 2;
}

[GenerateSnapshot]
public sealed partial class SnapshotChild
{
    public int Value;
    public Queue<byte> Bytes = new();
}

public sealed class SaveStateFormatTests
{
    private static SaveStateHeader Header(string title = "PCSE01444") => new()
    {
        FormatVersion = SaveStateFile.FormatVersion,
        EmulatorVersion = "test",
        Schema = SnapshotSchema.Hash,
        TitleId = title,
        Title = "Knightin'+",
        AppVersion = "01.00",
        CreatedUtc = new DateTime(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc),
        GuestSeconds = 12.5,
        Thumbnail = [1, 2, 3],
    };

    private static byte[] File(SaveStateHeader h, params (string, byte[])[] sections)
    {
        var ms = new MemoryStream();
        using (var w = new SaveStateWriter(ms, h))
        {
            foreach ((string? n, byte[]? d) in sections)
            {
                w.Section(n, d);
            }
        }

        return ms.ToArray();
    }

    [Fact]
    public void HeaderAndSectionsRoundTrip()
    {
        byte[] big = new byte[300_000];
        new Random(1).NextBytes(big);
        byte[] bytes = File(Header(), ("a", [9, 8, 7]), ("empty", []), ("big", big));
        SaveStateHeader h = SaveStateFile.ReadHeader(new MemoryStream(bytes));
        Assert.Equal("PCSE01444", h.TitleId);
        Assert.Equal("01.00", h.AppVersion);
        Assert.Equal(12.5, h.GuestSeconds);
        Assert.Equal([1, 2, 3], h.Thumbnail);
        Assert.Null(SaveStateFile.Incompatibility(h, "pcse01444"));
        using var r = new SaveStateReader(new MemoryStream(bytes));
        var got = new Dictionary<string, byte[]>();
        while (r.Next(out string? n, out _))
        {
            got[n] = r.ReadAll();
        }

        Assert.Equal([9, 8, 7], got["a"]);
        Assert.Empty(got["empty"]);
        Assert.Equal(big, got["big"]);
    }

    [Fact]
    public void IncompatibleStatesAreRefusedWithAMessage()
    {
        SaveStateHeader h = Header();
        Assert.Equal(VitaSharp.Core.Localization.Loc.Format("StateOtherGame", h.TitleId, h.Title, "PCSA00029"), SaveStateFile.Incompatibility(h, "PCSA00029"));
        h.Schema = "0-0";
        h.EmulatorVersion = "1.0.0+60d6517ede5e64570ae60683e2389471dd150a6f";
        Assert.Equal(VitaSharp.Core.Localization.Loc.Format("StateOtherVersion", "1.0.0+60d6517"), SaveStateFile.Incompatibility(h, "PCSE01444"));
        h = Header();
        h.FormatVersion = SaveStateFile.FormatVersion + 1;
        Assert.Contains((SaveStateFile.FormatVersion + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), SaveStateFile.Incompatibility(h, "PCSE01444"));
        SnapshotException e = Assert.Throws<SnapshotException>(() => SaveStateFile.ReadHeader(new MemoryStream("not a state at all"u8.ToArray())));
        Assert.Contains("not a VitaSharp save state", e.Message);
    }

    [Fact]
    public void TruncatedFileIsReported()
    {
        byte[] bytes = File(Header(), ("x", new byte[100_000]));
        using var r = new SaveStateReader(new MemoryStream(bytes[..(bytes.Length - 20)]));
        Assert.Throws<SnapshotException>(() =>
        {
            while (r.Next(out _, out _))
            {
                r.ReadAll();
            }
        });
    }

    [Fact]
    public void WriterReaderPrimitives()
    {
        var w = new SnapshotWriter();
        w.Write(true);
        w.Write((byte)5);
        w.Write(-3);
        w.Write(uint.MaxValue);
        w.Write(long.MinValue);
        w.Write(1.5f);
        w.Write(2.25);
        w.Write("héllo");
        w.Write((string?)null);
        w.Write(new byte[] { 1, 2 });
        w.WriteArray(new[] { 1f, 2f });
        int at = w.BeginBlock();
        w.Write(42);
        w.EndBlock(at);
        var r = new SnapshotReader(w.Data.ToArray());
        Assert.True(r.ReadBool());
        Assert.Equal(5, r.ReadByte());
        Assert.Equal(-3, r.ReadInt32());
        Assert.Equal(uint.MaxValue, r.ReadUInt32());
        Assert.Equal(long.MinValue, r.ReadInt64());
        Assert.Equal(1.5f, r.ReadSingle());
        Assert.Equal(2.25, r.ReadDouble());
        Assert.Equal("héllo", r.ReadString());
        Assert.Null(r.ReadString());
        Assert.Equal([1, 2], r.ReadBytes());
        Assert.Equal([1f, 2f], r.ReadArray<float>()!);
        Assert.Equal(42, r.ReadBlock().ReadInt32());
        Assert.True(r.AtEnd);
        Assert.Throws<SnapshotException>(() => r.ReadInt32());
    }

    [Fact]
    public void GeneratedSnapshotRoundTrips()
    {
        var a = new SnapshotProbe
        {
            I = -1,
            U = 0xDEADBEEF,
            L = 1L << 40,
            D = 0.125,
            Flag = true,
            K = SnapshotProbe.Kind.C,
            S = "s",
            Ints = [1, 2, 3],
            Names = ["x", null!],
            Range = (0x81000000, 0x1000),
            Maybe = 9,
            Child = new SnapshotChild { Value = 3 },
            Ignored = new object(),
            Hook = () => { },
            Extra = 21,
        };
        a.List.Add(4);
        a.Map[1] = "one";
        a.Ci["Key"] = 5;
        a.Fixed.Value = 8;
        a.Fixed.Bytes.Enqueue(6);
        a.Children.Add(new SnapshotChild { Value = 11 });
        var w = new SnapshotWriter();
        a.SaveState(w);
        var b = new SnapshotProbe { Ignored = "kept", Map = { [7] = "stale" } };
        b.LoadState(new SnapshotReader(w.Data.ToArray()));
        Assert.Equal((a.I, a.U, a.L, a.D, a.Flag, a.K, a.S), (b.I, b.U, b.L, b.D, b.Flag, b.K, b.S));
        Assert.Equal(a.Ints, b.Ints);
        Assert.Equal(a.Names, b.Names);
        Assert.Equal(a.List, b.List);
        Assert.Equal(a.Map, b.Map);
        Assert.Equal(5, b.Ci["KEY"]); // the readonly dictionary was filled in place: its comparer stays
        Assert.Equal(a.Range, b.Range);
        Assert.Equal(9, b.Maybe);
        Assert.Equal(3, b.Child!.Value);
        Assert.Equal(8, b.Fixed.Value);
        Assert.Equal(new byte[] { 6 }, b.Fixed.Bytes.ToArray());
        Assert.Equal(11, Assert.Single(b.Children).Value);
        Assert.Equal("kept", b.Ignored);
        Assert.Null(b.Hook);
        Assert.Equal(21, b.Extra);
    }

    [Fact]
    public void SchemaHashIsStable()
    {
        Assert.StartsWith($"{SnapshotSchema.ManualVersion}-", SnapshotSchema.Hash);
        Assert.Equal(SnapshotSchema.Hash, SnapshotSchema.Hash);
    }
}

[Collection("guest")]
public sealed unsafe class SaveStateGuestTests(GuestMemoryFixture fixture)
{
    [Fact]
    public void CpuStateRoundTripsAndDropsTheReservation()
    {
        CpuState* a = CpuState.Allocate();
        CpuState* b = CpuState.Allocate();
        try
        {
            for (int i = 0; i < 16; i++)
            {
                a->R[i] = (uint)(i * 0x1111);
            }

            for (int i = 0; i < 32; i++)
            {
                a->D[i] = (ulong)i * 0x0101010101010101UL;
            }

            a->N = 1;
            a->C = 1;
            a->Q = 1;
            a->GE = 0xA;
            a->Thumb = 1;
            a->ITState = 0x18;
            a->Fpscr = 0x03000010;
            a->TpidrUro = 0x81001000;
            a->ExclusiveAddr = 0x81002000;
            a->ExclusiveValue = 77;
            a->ExclusiveValid = 1;
            a->SvcPc = 0x81003000;
            var w = new SnapshotWriter();
            GuestThread.WriteCpu(w, a, reissue: true);
            GuestThread.ReadCpu(new SnapshotReader(w.Data.ToArray()), b);
            for (int i = 0; i < 15; i++)
            {
                Assert.Equal(a->R[i], b->R[i]);
            }

            Assert.Equal(0x81003000u, b->R[15]); // a reissued HLE call restarts at its SVC
            for (int i = 0; i < 32; i++)
            {
                Assert.Equal(a->D[i], b->D[i]);
            }

            Assert.Equal((a->N, a->C, a->Q, a->GE, a->Thumb, a->ITState, a->Fpscr, a->TpidrUro), (b->N, b->C, b->Q, b->GE, b->Thumb, b->ITState, b->Fpscr, b->TpidrUro));
            Assert.Equal(0u, b->ExclusiveValid);
            Assert.Equal(77ul, b->ExclusiveValue);
        }
        finally
        {
            CpuState.Free(a);
            CpuState.Free(b);
        }
    }

    [Fact]
    public void MemoryBlockContentsRoundTrip()
    {
        uint addr = fixture.Memory.Alloc(0x10000, "savestate test");
        try
        {
            GuestMemory.Fill(addr, 0, 0x10000);
            GuestMemory.W32(addr + 0x10, 0x12345678);
            GuestMemory.W32(addr + 0xF000, 0xCAFEBABE);
            GuestMemory.Block block = fixture.Memory.FindBlock(addr)!;
            byte[] data = fixture.Memory.CopyContents(block);
            Assert.Equal(2 + 2 * (int)GuestMemory.PageSize, data.Length); // bitmap + the two non-zero pages
            GuestMemory.Fill(addr, 0xEE, 0x10000);
            fixture.Memory.RestoreContents(block, data);
            Assert.Equal(0x12345678u, GuestMemory.R32(addr + 0x10));
            Assert.Equal(0xCAFEBABEu, GuestMemory.R32(addr + 0xF000));
            Assert.Equal(0u, GuestMemory.R32(addr + 0x5000));
            var bt = new SnapshotWriter();
            GuestMemory.WriteBlockTable(bt, [block]);
            GuestMemory.Block back = Assert.Single(GuestMemory.ReadBlockTable(new SnapshotReader(bt.Data.ToArray())));
            Assert.Equal((block.Address, block.Size, block.Name, block.Region), (back.Address, back.Size, back.Name, back.Region));
        }
        finally
        {
            fixture.Memory.Free(addr);
        }
    }

    [Fact]
    public void KernelObjectsRoundTrip()
    {
        var table = new ObjectTable();
        var sema = new Semaphore { Name = "s", InitCount = 1, Count = 2, MaxCount = 5, Attr = 3 };
        var evf = new EventFlag { Name = "e", InitPattern = 1, Pattern = 0xF0 };
        var mtx = new Mutex { Name = "m", LockCount = 2, Attr = Mutex.AttrRecursive };
        var cond = new CondVar { Name = "c", Mutex = mtx };
        var pipe = new MsgPipe { Name = "p", BufferSize = 64 };
        table.Register(sema);
        table.Register(evf);
        table.Register(mtx);
        table.Register(cond);
        table.Register(pipe);
        uint buf = fixture.Memory.Alloc(0x1000, "pipe data");
        try
        {
            "abc"u8.CopyTo(GuestMemory.Span(buf, 3));
            Assert.Equal(0, pipe.Send(buf, 3, MsgPipe.WaitAsap, default, default, poll: true));

            SnapshotWriter W(Action<SnapshotWriter> save)
            {
                var w = new SnapshotWriter();
                save(w);
                return w;
            }
            SnapshotReader R(SnapshotWriter w) => new(w.Data.ToArray())
            {
                Objects = table
            };
            var s2 = new Semaphore { Name = "s" };
            s2.Load(R(W(sema.Save)));
            Assert.Equal((1, 2, 5, 3u), (s2.InitCount, s2.Count, s2.MaxCount, s2.Attr));
            var e2 = new EventFlag { Name = "e" };
            e2.Load(R(W(evf.Save)));
            Assert.Equal(0xF0u, e2.Pattern);
            var m2 = new Mutex { Name = "m" };
            m2.Load(R(W(mtx.Save)));
            m2.Resolve(table);
            Assert.Equal(2, m2.LockCount);
            var c2 = new CondVar { Name = "c" };
            c2.Load(R(W(cond.Save)));
            c2.Resolve(table);
            Assert.Same(mtx, c2.Mutex);
            var p2 = new MsgPipe { Name = "p" };
            p2.Load(R(W(pipe.Save)));
            Assert.Equal(61, p2.FreeSize);
        }
        finally
        {
            fixture.Memory.Free(buf);
        }
    }
}
