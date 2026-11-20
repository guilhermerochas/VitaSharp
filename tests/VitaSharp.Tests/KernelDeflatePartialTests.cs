using System.IO.Compression;
using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Loader;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// ksceDeflateDecompressPartial (SceKernelUtilsForDriver, sysmem.skprx 3.74): the raw deflate decoder over input given
/// in pieces by a guest callback. The first piece runs from src to pBufEnd; at pBufEnd the decoder calls refill(param,
/// bytes written so far), which returns the next piece and stores its end in pBufEnd; back references reach into the
/// earlier output. A NULL block or one whose size is not 0x10 is 0x80560104; a refill with no input, an empty piece
/// or one of odd length is 0x80560103; bad data 0x80560100; output that does not fit 0x80560101.
/// </summary>
[Collection("guest")]
public sealed class KernelDeflatePartialTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int BadData = unchecked((int)0x80560100), NoRoom = unchecked((int)0x80560101);
    private const int BadInput = unchecked((int)0x80560103), BadParam = unchecked((int)0x80560104);

    private readonly HleHarness _harness = new(fixture, "kdeflp");

    public void Dispose() => _harness.Dispose();

    private SysmemKernel Sysmem => _harness.Module<SysmemKernel>();

    private uint Put(byte[] bytes)
    {
        uint address = _harness.Alloc((uint)Math.Max(bytes.Length, 1) + 16);
        bytes.CopyTo(GuestMemory.Span(address, bytes.Length));
        return address;
    }

    private uint HostStub(string name, Action<HleContext> body)
    {
        uint stub = _harness.Alloc(16);
        Linker.WriteHleStub(stub, _harness.Emu.Hle.RegisterHostFunction(name, body), 0);
        _harness.Emu.Code.Invalidate(stub, 16);
        return stub;
    }

    private static byte[] RawDeflate(byte[] data, CompressionLevel level)
    {
        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, level, true))
        {
            deflate.Write(data);
        }

        return output.ToArray();
    }

    private static byte[] Data(int length)
    {
        byte[] bytes = new byte[length];
        for (int i = 0; i < length; i++)
        {
            bytes[i] = (byte)((i % 61) * 7 + (i >> 9));
        }

        return bytes;
    }

    /// <summary>A parameter block whose refill hands out the stream in the pieces given (each a guest copy), and
    /// records the written counts it was called with.</summary>
    private (uint Param, List<uint> Written, uint First, uint FirstEnd) Pieces(byte[] stream, int[] sizes, string name)
    {
        var pieces = new List<(uint Start, uint End)>();
        int at = 0;
        foreach (int size in sizes)
        {
            uint start = Put(stream[at..(at + size)]);
            pieces.Add((start, start + (uint)size));
            at += size;
        }

        var written = new List<uint>();
        uint param = _harness.Alloc(0x10);
        int nextPiece = 1;
        uint refill = HostStub(name, context =>
        {
            uint block = context.Gpr(0);
            written.Add(context.Gpr(1));
            if (nextPiece >= pieces.Count)
            {
                context.Return(0u);
                return;
            }

            (uint start, uint end) = pieces[nextPiece++];
            GuestMemory.W32(block + 4, end);
            context.Return(start);
        });
        GuestMemory.W32(param, 0x10);
        GuestMemory.W32(param + 4, pieces[0].End);
        GuestMemory.W32(param + 8, 0);
        GuestMemory.W32(param + 0xC, refill);
        return (param, written, pieces[0].Start, pieces[0].End);
    }

    private int Run(uint destination, uint size, uint source, uint next, uint param)
    {
        int result = 0;
        _harness.OnGuest(_ => result = Call(Sysmem, "ksceDeflateDecompressPartial", destination, size, source, next, param));
        return result;
    }

    [Fact]
    public void TheExportHasAnEntry()
    {
        var bindings = HleBindings.Modules.SelectMany(m => m.Bindings())
            .Where(b => b.Nid == 0x3D74CCDF && b.Library == "SceKernelUtilsForDriver").ToList();
        Assert.Single(bindings);
        Assert.False(bindings[0].Default);
    }

    [Theory]
    [InlineData(CompressionLevel.NoCompression, 70000, 1001)]
    [InlineData(CompressionLevel.Optimal, 20000, 3)]
    [InlineData(CompressionLevel.SmallestSize, 30000, 401)]
    public void ThePiecesDecodeAsOneStream(CompressionLevel level, int length, int firstPiece)
    {
        byte[] data = Data(length);
        byte[] stream = [.. RawDeflate(data, level), 0xEE, 0xEE];
        if (((stream.Length - firstPiece) & 1) != 0)
        {
            stream = [.. stream, 0xEE];
        }

        // the first piece of any length, the others even (512 bytes, the last what is left), the last ending after
        // the stream's own end
        var sizes = new List<int> { firstPiece };
        for (int at = firstPiece; at < stream.Length; at += 512)
        {
            sizes.Add(Math.Min(512, stream.Length - at));
        }

        (uint param, List<uint> written, uint first, _) = Pieces(stream, [.. sizes], $"kdeflp:{level}:{length}");
        uint destination = _harness.Alloc((uint)length + 16);
        uint next = _harness.Word(0);
        Assert.Equal(length, Run(destination, (uint)length, first, next, param));
        Assert.Equal(data, GuestMemory.ReadSpan(destination, length).ToArray());
        Assert.True(written.Count > 0);
        Assert.Equal(written.OrderBy(w => w), written);
        // *next is in the last piece given, at the byte after the stream's end
        uint lastEnd = GuestMemory.R32(param + 4);
        Assert.True(GuestMemory.R32(next) <= lastEnd);
        Assert.Equal(0xEE, GuestMemory.Read<byte>(GuestMemory.R32(next)));
    }

    [Fact]
    public void AWholeStreamInTheFirstPieceNeverCallsRefill()
    {
        byte[] data = Data(3000);
        byte[] stream = RawDeflate(data, CompressionLevel.Optimal);
        (uint param, List<uint> written, uint first, uint firstEnd) = Pieces([.. stream, 0xEE], [stream.Length + 1], "kdeflp:whole");
        uint destination = _harness.Alloc(3000);
        uint next = _harness.Word(0);
        Assert.Equal(3000, Run(destination, 3000, first, next, param));
        Assert.Empty(written);
        Assert.Equal(firstEnd - 1, GuestMemory.R32(next));
    }

    [Fact]
    public void TheParameterBlockIsChecked()
    {
        uint destination = _harness.Alloc(64);
        uint source = Put([0x03, 0x00]);
        Assert.Equal(BadParam, Run(destination, 64, source, 0, 0));
        uint param = _harness.Alloc(0x10);
        GuestMemory.W32(param, 0x14);
        GuestMemory.W32(param + 4, source + 2);
        Assert.Equal(BadParam, Run(destination, 64, source, 0, param));
    }

    [Fact]
    public void RunningOutOfInputOrAnOddPieceIsRefused()
    {
        byte[] stream = RawDeflate(Data(5000), CompressionLevel.Optimal);
        uint destination = _harness.Alloc(5000);
        // the callback has nothing more: NULL
        (uint param, _, uint first, _) = Pieces(stream, [stream.Length / 2], "kdeflp:short");
        Assert.Equal(BadInput, Run(destination, 5000, first, 0, param));
        // a second piece of odd length
        int rest = stream.Length - 10;
        if ((rest & 1) == 0)
        {
            stream = [.. stream, 0xEE];
            rest++;
        }

        (param, _, first, _) = Pieces(stream, [10, rest], "kdeflp:odd");
        Assert.Equal(BadInput, Run(destination, 5000, first, 0, param));
    }

    [Fact]
    public void BadDataAndNoRoomAreTheDecodersErrors()
    {
        byte[] data = Data(4000);
        byte[] stream = RawDeflate(data, CompressionLevel.Optimal);
        (uint param, _, uint first, _) = Pieces(stream, [stream.Length], "kdeflp:room");
        uint destination = _harness.Alloc(4000);
        Assert.Equal(NoRoom, Run(destination, 3999, first, 0, param));
        (param, _, first, _) = Pieces([0x07, 0x00], [2], "kdeflp:bad");
        Assert.Equal(BadData, Run(destination, 64, first, 0, param));
    }
}
