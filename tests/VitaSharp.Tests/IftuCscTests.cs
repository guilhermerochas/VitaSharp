using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// ksceIftuCsc (SceIftuForDriver, lowio 3.74): the memory-to-memory IFTU conversion. The destination's format set (bit
/// 31 ignored) and the source's are checked (0x803F0703); packed RGB formats keep or swap their channel order; YCbCr
/// (YUV420, NV12) goes to RGB through the CSC block SceAvcodec passes (BT.601, offsets taken from the YCbCr input) or,
/// with none kept, the default BT.601; the 16.16 step scales; source format 0 draws the background colour; a NULL
/// block keeps the last one given.
/// </summary>
[Collection("guest")]
public sealed class IftuCscTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int Format = unchecked((int)0x803F0703);
    private const uint Rgbx8888 = 0x20, Bgrx8888 = 0x10, Rgb565 = 0x2, Rgba1010102 = 0x80, Nv12 = 0x10000, Yuv420 = 0x20000;

    /// <summary>SceAvcodec's BT.601 limited range YCbCr to RGB block (its table, post offsets 0x40/0x202).</summary>
    private static readonly uint[] Bt601 = [0x40, 0x202, 0x3FF, 0, 0, 0, 0x254, 0, 0x331, 0x254, 0xF38, 0xE60, 0x254, 0x408, 0];

    private readonly HleHarness _harness = new(fixture, "iftucsc");

    public void Dispose() => _harness.Dispose();

    private SystemServicesVsh Services => _harness.Module<SystemServicesVsh>();

    private uint FrameBuffer(uint format, uint width, uint height, uint plane0, uint plane1 = 0, uint plane2 = 0, uint leftover = 0)
    {
        uint descriptor = _harness.Alloc(0x20);
        uint[] words = [format, width, height, leftover, (width >> 1) & 0xF, plane0, plane1, plane2];
        for (int i = 0; i < words.Length; i++)
        {
            GuestMemory.W32(descriptor + (uint)i * 4, words[i]);
        }

        return descriptor;
    }

    private uint PlaneState(uint format, uint width, uint height, uint plane0, uint plane1 = 0, uint plane2 = 0, uint step = 0x10000)
    {
        uint state = _harness.Alloc(0x54);
        uint frame = FrameBuffer(format, width, height, plane0, plane1, plane2);
        for (uint i = 0; i < 0x20; i += 4)
        {
            GuestMemory.W32(state + i, GuestMemory.R32(frame + i));
        }

        GuestMemory.W32(state + 0x2C, step);
        GuestMemory.W32(state + 0x30, step);
        return state;
    }

    private uint Params(uint filter, uint block1, uint block2, uint alpha = 0xFF, uint background0 = 0, uint background1 = 0, uint background2 = 0)
    {
        uint parameters = _harness.Alloc(0x28);
        uint[] words = [0x28, filter, block1, block2, 0, background0, background1, background2, alpha, 0];
        for (int i = 0; i < words.Length; i++)
        {
            GuestMemory.W32(parameters + (uint)i * 4, words[i]);
        }

        return parameters;
    }

    private uint Block(uint[] words)
    {
        uint block = _harness.Alloc(0x3C);
        for (int i = 0; i < words.Length; i++)
        {
            GuestMemory.W32(block + (uint)i * 4, words[i]);
        }

        return block;
    }

    private int Csc(uint destination, uint source, uint parameters) => Call(Services, "ksceIftuCsc", destination, source, parameters);

    [Fact]
    public void TheExportHasAnEntry()
    {
        var bindings = HleBindings.Modules.SelectMany(m => m.Bindings())
            .Where(b => b.Nid == 0x67E37EFC && b.Library == "SceIftuForDriver").ToList();
        Assert.Single(bindings);
        Assert.False(bindings[0].Default);
    }

    [Fact]
    public void TheFormatSetsAreChecked()
    {
        uint pixels = _harness.Alloc(16 * 4 * 4);
        uint source = PlaneState(Rgbx8888, 4, 4, pixels);
        Assert.Equal(Format, Csc(FrameBuffer(3, 4, 4, pixels), source, 0));
        Assert.Equal(Format, Csc(FrameBuffer(0, 4, 4, pixels), source, 0));
        Assert.Equal(0, Csc(FrameBuffer(0x80000000 | Rgbx8888, 4, 4, pixels), source, 0));
        Assert.Equal(Format, Csc(FrameBuffer(Rgbx8888, 4, 4, pixels), PlaneState(3, 4, 4, pixels), 0));
        Assert.Equal(0, Csc(FrameBuffer(Rgbx8888, 4, 4, pixels), PlaneState(0x80140000, 4, 4, pixels), 0));
    }

    [Fact]
    public void PackedRgbKeepsOrSwapsItsChannels()
    {
        uint input = _harness.Alloc(4 * 2 * 4);
        uint output = _harness.Alloc(4 * 2 * 4);
        for (uint i = 0; i < 8; i++)
        {
            GuestMemory.W32(input + i * 4, 0x00102030u + i * 0x010101u);
        }

        Assert.Equal(0, Csc(FrameBuffer(Bgrx8888, 4, 2, output), PlaneState(Rgbx8888, 4, 2, input), Params(0, 0, 0, 0x80)));
        for (uint i = 0; i < 8; i++)
        {
            uint value = 0x00102030u + i * 0x010101u;
            uint swapped = ((value & 0xFF) << 16) | (value & 0xFF00) | ((value >> 16) & 0xFF) | 0x80000000;
            Assert.Equal(swapped, GuestMemory.R32(output + i * 4));
        }

        // RGB565 to RGBA1010102: each channel widened
        uint small = _harness.Alloc(2 * 2);
        GuestMemory.Write(small, (ushort)(0x1F | (0x20 << 5) | (0x00 << 11)));
        GuestMemory.Write(small + 2, (ushort)0xFFFF);
        uint wide = _harness.Alloc(2 * 4);
        Assert.Equal(0, Csc(FrameBuffer(Rgba1010102, 2, 1, wide), PlaneState(Rgb565, 2, 1, small), 0));
        Assert.Equal(0x3FFu | (0x208u << 10) | (3u << 30), GuestMemory.R32(wide));
        Assert.Equal(0xFFFFFFFFu, GuestMemory.R32(wide + 4));
    }

    /// <summary>One 2x2 colour in YCbCr (8 bits) and the RGB it is (±2).</summary>
    public static TheoryData<byte, byte, byte, int, int, int> Colours => new()
    {
        { 235, 128, 128, 255, 255, 255 },
        { 16, 128, 128, 0, 0, 0 },
        { 81, 90, 240, 255, 0, 0 },
        { 145, 54, 34, 0, 255, 0 },
        { 41, 240, 110, 0, 0, 255 },
    };

    [Theory]
    [MemberData(nameof(Colours))]
    public void Yuv420AndNv12BecomeRgbThroughBt601(byte luma, byte blue, byte red, int r, int g, int b)
    {
        const uint width = 32, height = 4;
        uint y = _harness.Alloc(width * height);
        GuestMemory.Span(y, (int)(width * height)).Fill(luma);
        uint cb = _harness.Alloc(16 * 2);
        uint cr = _harness.Alloc(16 * 2);
        GuestMemory.Span(cb, 32).Fill(blue);
        GuestMemory.Span(cr, 32).Fill(red);
        uint interleaved = _harness.Alloc(width * 2);
        for (uint i = 0; i < width * 2; i += 2)
        {
            GuestMemory.Write(interleaved + i, blue);
            GuestMemory.Write(interleaved + i + 1, red);
        }

        foreach ((uint source, uint parameters) in new[]
        {
            (PlaneState(Yuv420, width, height, y, cb, cr), Params(1, 0, Block(Bt601))),
            (PlaneState(Nv12, width, height, y, interleaved), Params(1, 0, Block(Bt601))),
        })
        {
            uint output = _harness.Alloc(width * height * 4);
            Assert.Equal(0, Csc(FrameBuffer(Rgbx8888, width, height, output), source, parameters));
            for (uint pixel = 0; pixel < width * height; pixel++)
            {
                uint value = GuestMemory.R32(output + pixel * 4);
                Assert.InRange((int)(value & 0xFF), r - 2, r + 2);
                Assert.InRange((int)((value >> 8) & 0xFF), g - 2, g + 2);
                Assert.InRange((int)((value >> 16) & 0xFF), b - 2, b + 2);
                Assert.Equal(0xFFu, value >> 24);
            }
        }
    }

    [Fact]
    public void WithNoBlockKeptBt601IsTheDefaultAndAGivenBlockIsKept()
    {
        uint y = _harness.Alloc(16 * 2);
        GuestMemory.Span(y, 32).Fill(235);
        uint chroma = _harness.Alloc(16 * 2);
        GuestMemory.Span(chroma, 32).Fill(128);
        uint output = _harness.Alloc(16 * 2 * 4);
        uint destination = FrameBuffer(Rgbx8888, 16, 2, output);
        uint source = PlaneState(Yuv420, 16, 2, y, chroma, chroma);
        Assert.Equal(0, Csc(destination, source, 0));
        Assert.Equal(0xFFFFFFFFu, GuestMemory.R32(output));
        // a block that keeps only component 0 (as R): kept for the next call that passes NULL blocks
        uint[] lumaOnly = [0, 0, 0, 0, 0, 0, 0x200, 0, 0, 0, 0, 0, 0, 0, 0];
        Assert.Equal(0, Csc(destination, source, Params(0, 0, Block(lumaOnly))));
        Assert.Equal(0xFF0000EBu, GuestMemory.R32(output));
        GuestMemory.W32(output, 0);
        Assert.Equal(0, Csc(destination, source, Params(0, 0, 0)));
        Assert.Equal(0xFF0000EBu, GuestMemory.R32(output));
    }

    [Fact]
    public void TheStepScalesTheSource()
    {
        uint input = _harness.Alloc(8 * 8 * 4);
        for (uint i = 0; i < 64; i++)
        {
            GuestMemory.W32(input + i * 4, i);
        }

        uint output = _harness.Alloc(4 * 4 * 4);
        Assert.Equal(0, Csc(FrameBuffer(Rgbx8888, 4, 4, output), PlaneState(Rgbx8888, 8, 8, input, step: 0x20000), Params(0, 0, 0)));
        for (uint row = 0; row < 4; row++)
        {
            for (uint column = 0; column < 4; column++)
            {
                Assert.Equal(0xFF000000u | (row * 2 * 8 + column * 2), GuestMemory.R32(output + (row * 4 + column) * 4));
            }
        }
    }

    [Fact]
    public void SourceFormatZeroDrawsTheBackground()
    {
        uint output = _harness.Alloc(4 * 4);
        Assert.Equal(0, Csc(FrameBuffer(Rgbx8888, 4, 1, output), PlaneState(0, 4, 1, 0), Params(0, 0, 0, 0xFF, 0x3FF, 0, 0x100)));
        for (uint i = 0; i < 4; i++)
        {
            Assert.Equal(0xFF4000FFu, GuestMemory.R32(output + i * 4));
        }
    }
}
