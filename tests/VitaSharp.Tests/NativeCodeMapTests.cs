using System.Text;
using VitaSharp.Core.Cpu.Native;

namespace VitaSharp.Tests;

/// <summary>The native code map (VITASHARP_NATIVE_MAP) for profilers: its line format and that writing a line allocates
/// nothing (one string per code byte made FIFA 13 allocate 4.9 MB per frame on the phone and the game was killed).</summary>
public sealed class NativeCodeMapTests
{
    [Fact]
    public void LinesHaveTheHostAddressSizeGuestPcKindAndCodeBytes()
    {
        var stream = new MemoryStream();
        using var writer = new NativeCodeMapWriter(stream, flushPeriodMs: 0);
        writer.Add([0x1F, 0x20, 0x03, 0xD5], 0x7F12AB00, 0x81001234, thumb: true);
        writer.Add([0xC0, 0x03, 0x5F, 0xD6], 0x7F12AC00, 0, thumb: false, name: "trampoline");
        writer.Add([0xAA], 0x10, 0x8, thumb: false);
        writer.Flush();
        string text = Encoding.ASCII.GetString(stream.ToArray());
        Assert.Equal("7F12AB00 4 81001234 thumb 1F2003D5\n7F12AC00 4 00000000 trampoline C0035FD6\n10 1 00000008 arm AA\n", text);
    }

    [Fact]
    public void ABlockLargerThanTheLineBufferIsWrittenWhole()
    {
        var stream = new MemoryStream();
        using var writer = new NativeCodeMapWriter(stream, flushPeriodMs: 0);
        byte[] code = new byte[10_000];
        code[^1] = 0xEF;
        writer.Add(code, 0x1000, 0x2000, thumb: false);
        writer.Flush();
        string text = Encoding.ASCII.GetString(stream.ToArray());
        Assert.StartsWith("1000 10000 00002000 arm 0000", text, StringComparison.Ordinal);
        Assert.EndsWith("00EF\n", text, StringComparison.Ordinal);
        Assert.Equal(25 + 20_000, text.Length);
    }

    [Fact]
    public void WritingLinesAllocatesNothing()
    {
        // a stream that keeps nothing, so only the writer's own allocations count
        using var writer = new NativeCodeMapWriter(Stream.Null, flushPeriodMs: 0);
        byte[] code = new byte[400];
        new Random(1).NextBytes(code);
        writer.Add(code, 0x7F000000, 0x81000000, thumb: true); // grows nothing: the first line fits the buffer
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
        {
            writer.Add(code, 0x7F000000 + (ulong)i * 64, 0x81000000 + (uint)i * 4, thumb: (i & 1) != 0);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
    }
}
