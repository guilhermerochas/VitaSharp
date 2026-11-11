using VitaSharp.Core.Gpu;
using VitaSharp.Core.Memory;

namespace VitaSharp.Tests;

/// <summary>The decoder picture history the renderer samples instead of memory (Gpu/DecoderOutputs.cs).</summary>
[Collection("guest")]
public sealed class DecoderOutputsTests(GuestMemoryFixture fx)
{
    const uint W = 8, H = 8;

    static byte[] Picture(int n) => Enumerable.Repeat((byte)n, (int)(W * H)).ToArray();

    static void Decode(uint addr, int n)
    {
        var p = Picture(n);
        p.CopyTo(GuestMemory.Span(addr, p.Length));
        DecoderOutputs.Record(addr, W, H, p);
    }

    [Fact]
    public void PictureOneUploadBehindComesFromTheHistory()
    {
        uint a = fx.Memory.Alloc(W * H, "dec-hist-single");
        Decode(a, 1);
        Decode(a, 2); // the render thread is one picture behind: picture 1 is drawn after 2 overwrote it
        Assert.Equal((byte)1, DecoderOutputs.EarlierFor(a, W, H)?.Data[0]);
        Assert.Null(DecoderOutputs.EarlierFor(a, W, H)); // then picture 2, from memory
    }

    [Fact]
    public void RingBufferPictureTheGameNeverDrewIsNotShownLater()
    {
        // a movie decoded into a ring of three buffers; picture 0 (buffer a) is never drawn
        uint a = fx.Memory.Alloc(W * H, "dec-ring-a"), b = fx.Memory.Alloc(W * H, "dec-ring-b"), c = fx.Memory.Alloc(W * H, "dec-ring-c");
        uint[] ring = [a, b, c];
        for (int n = 0; n < 3; n++)
        {
            Decode(ring[n], 100 + n);
        }

        for (int n = 1; n < 12; n++)
        {
            if (n >= 3)
            {
                Decode(ring[n % 3], 100 + n);
            }

            var served = DecoderOutputs.EarlierFor(ring[n % 3], W, H);
            int shown = served?.Data[0] ?? GuestMemory.Read<byte>(ring[n % 3]);
            Assert.Equal(100 + n, shown); // before the fix buffer a kept serving its previous picture: 102, 103, 101, ...
        }
    }

    [Fact]
    public void AvcdecFindsTheParameterSetsBeforeTheFirstSlice()
    {
        byte[] au = [0, 0, 0, 1, 0x09, 0xF0, 0, 0, 0, 1, 0x67, 1, 2, 3, 0, 0, 1, 0x68, 4, 5, 0, 0, 1, 0x65, 9, 9, 0, 0, 1, 0x67, 7];
        Assert.Equal(new byte[] { 0, 0, 0, 1, 0x67, 1, 2, 3, 0, 0, 0, 1, 0x68, 4, 5 }, VitaSharp.Core.Hle.Modules.Videodec.ParameterSets(au));
        Assert.Null(VitaSharp.Core.Hle.Modules.Videodec.ParameterSets([0, 0, 0, 1, 0x41, 1, 2]));
    }

    [Fact]
    public void AvcdecTellsAnIdrUnitByItsFirstSlice()
    {
        // AUD, SPS, PPS, IDR slice: an IDR access unit (the replay kept for save states restarts there)
        Assert.True(VitaSharp.Core.Hle.Modules.Videodec.StartsIdr([0, 0, 0, 1, 0x09, 0xF0, 0, 0, 0, 1, 0x67, 1, 0, 0, 1, 0x68, 4, 0, 0, 1, 0x65, 9]));
        // a P slice, even with 0x65 bytes in its data
        Assert.False(VitaSharp.Core.Hle.Modules.Videodec.StartsIdr([0, 0, 0, 1, 0x41, 0x65, 0, 0, 1, 0x65]));
        Assert.False(VitaSharp.Core.Hle.Modules.Videodec.StartsIdr([1, 2, 3]));
    }
}
