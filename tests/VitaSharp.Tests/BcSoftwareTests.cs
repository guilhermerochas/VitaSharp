using Silk.NET.Vulkan;
using VitaSharp.Core.Gpu;
using VitaSharp.Core.Gxm;
using VitaSharp.Renderer.Vulkan;

namespace VitaSharp.Tests;

/// <summary>
/// The CPU decode of GXM's block-compressed formats (<see cref="BcSoftware"/>, for GPUs without textureCompressionBC)
/// and their ASTC 4x4 transcode (<see cref="AstcTranscoder"/>), against the BC1-BC5 definitions of the Khronos Data
/// Format Specification (S3TC and RGTC chapters) that the hardware formats sample: 5:6:5 endpoints expanded by bit
/// replication, BC1's four-colour and three-colour (punch-through) modes, BC2's explicit 4-bit alpha, the 8-value and
/// 6-value alpha/RGTC modes, signed RGTC with -128 read as -127, and the (R, 0, 0, 1) / (R, G, 0, 1) layout of BC4/BC5.
/// One class: both helpers keep process-wide caches and counters, so these tests must not run in parallel.
/// </summary>
// AstcTranscoder is process-wide (its disk cache and counters): the tests that reset it run on their own, not beside
// renderers that transcode (a parallel replay counted its encodes into TranscodedLevelsAreReadBackFromTheDiskCache)
[Collection("AstcTranscoder")]
public sealed class BcSoftwareTests
{
    private const ushort Red565 = 0xF800;
    private const ushort Blue565 = 0x001F;

    /// <summary>A BC1 block (or the colour half of BC2/BC3): two 5:6:5 endpoints and 16 2-bit indices, texel 0 lowest.</summary>
    private static byte[] ColourBlock(ushort c0, ushort c1, params int[] indices)
    {
        uint bits = 0;
        for (int i = 0; i < indices.Length; i++)
        {
            bits |= (uint)indices[i] << (2 * i);
        }

        return [(byte)c0, (byte)(c0 >> 8), (byte)c1, (byte)(c1 >> 8), (byte)bits, (byte)(bits >> 8), (byte)(bits >> 16), (byte)(bits >> 24)];
    }

    /// <summary>A BC3 alpha / BC4 / BC5 channel block: two endpoints and 16 3-bit codes, texel 0 lowest.</summary>
    private static byte[] ChannelBlock(byte e0, byte e1, params int[] codes)
    {
        ulong bits = 0;
        for (int i = 0; i < codes.Length; i++)
        {
            bits |= (ulong)codes[i] << (3 * i);
        }

        byte[] b = new byte[8];
        b[0] = e0;
        b[1] = e1;
        for (int i = 0; i < 6; i++)
        {
            b[2 + i] = (byte)(bits >> (8 * i));
        }

        return b;
    }

    private static byte[] Texel(Texels t, int i) => t.Span.Slice(i * 4, 4).ToArray();

    [Fact]
    public void Bc1FourColourModeInterpolatesTwoThirds()
    {
        BcSoftware.ClearCache();
        // c0 > c1: colour 2 = (2 c0 + c1) / 3, colour 3 = (c0 + 2 c1) / 3, all opaque
        using Texels t = BcSoftware.Decode(TexBase.UBC1, ColourBlock(Red565, Blue565, 0, 1, 2, 3), 4, 4);
        Assert.Equal(64, t.Length);
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, Texel(t, 0));
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, Texel(t, 1));
        Assert.Equal(new byte[] { 170, 0, 85, 255 }, Texel(t, 2));
        Assert.Equal(new byte[] { 85, 0, 170, 255 }, Texel(t, 3));
        // the other 12 texels have index 0
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, Texel(t, 15));
    }

    [Fact]
    public void Bc1ThreeColourModeHasTheMidpointAndTransparentBlack()
    {
        BcSoftware.ClearCache();
        // c0 <= c1: colour 2 = (c0 + c1) / 2, colour 3 = transparent black (the punch-through mode)
        using Texels t = BcSoftware.Decode(TexBase.UBC1, ColourBlock(Blue565, Red565, 0, 1, 2, 3), 4, 4);
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, Texel(t, 0));
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, Texel(t, 1));
        Assert.Equal(new byte[] { 128, 0, 128, 255 }, Texel(t, 2));
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, Texel(t, 3));
    }

    [Fact]
    public void Bc1EndpointsExpandByBitReplication()
    {
        BcSoftware.ClearCache();
        // r = 16 (5 bits), g = 32 (6 bits), b = 16: (16 << 3 | 16 >> 2) = 132, (32 << 2 | 32 >> 4) = 130;
        // full-scale 31/63 must reach 255, not 248/252
        using Texels mid = BcSoftware.Decode(TexBase.UBC1, ColourBlock(0x8410, 0x0000), 4, 4);
        Assert.Equal(new byte[] { 132, 130, 132, 255 }, Texel(mid, 7));
        using Texels white = BcSoftware.Decode(TexBase.UBC1, ColourBlock(0xFFFF, 0x0000), 4, 4);
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, Texel(white, 0));
    }

    [Fact]
    public void Bc2HasExplicitFourBitAlphaAndAlwaysFourColours()
    {
        BcSoftware.ClearCache();
        // alpha nibbles, texel 0 in the low nibble of byte 0; each 4-bit value is scaled by 17 (15 -> 255)
        byte[] block = new byte[16];
        block[0] = 0x10;
        block[1] = 0xF8;
        // c0 <= c1 would be BC1's three-colour mode, but BC2 colour blocks are always four-colour
        ColourBlock(Blue565, Red565, 0, 1, 2, 3).CopyTo(block, 8);
        using Texels t = BcSoftware.Decode(TexBase.UBC2, block, 4, 4);
        Assert.Equal(new byte[] { 0, 0, 255, 0 }, Texel(t, 0));
        Assert.Equal(new byte[] { 255, 0, 0, 17 }, Texel(t, 1));
        Assert.Equal(new byte[] { 85, 0, 170, 136 }, Texel(t, 2));
        Assert.Equal(new byte[] { 170, 0, 85, 255 }, Texel(t, 3));
    }

    [Fact]
    public void Bc3AlphaUsesTheEightValueRampWhenTheFirstEndpointIsLarger()
    {
        BcSoftware.ClearCache();
        // a0 = 255 > a1 = 0: code 2 = (6 a0 + a1) / 7 = 219 (rounded), code 7 = (a0 + 6 a1) / 7 = 36
        byte[] block = new byte[16];
        ChannelBlock(255, 0, 0, 1, 2, 7).CopyTo(block, 0);
        ColourBlock(0xFFFF, 0xFFFF).CopyTo(block, 8);
        using Texels t = BcSoftware.Decode(TexBase.UBC3, block, 4, 4);
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, Texel(t, 0));
        Assert.Equal(new byte[] { 255, 255, 255, 0 }, Texel(t, 1));
        Assert.Equal(new byte[] { 255, 255, 255, 219 }, Texel(t, 2));
        Assert.Equal(new byte[] { 255, 255, 255, 36 }, Texel(t, 3));
    }

    [Fact]
    public void UnsignedRgtcSixValueModeAddsZeroAndOne()
    {
        // a0 <= a1: codes 2-5 interpolate in fifths, code 6 is 0 and code 7 is 255 whatever the endpoints are
        Span<byte> v = stackalloc byte[16];
        BcSoftware.UnsignedBlock(ChannelBlock(100, 200, 0, 1, 2, 5, 6, 7), v);
        Assert.Equal(100, v[0]);
        Assert.Equal(200, v[1]);
        Assert.Equal(120, v[2]); // (4 * 100 + 200 + 2) / 5
        Assert.Equal(180, v[3]); // (100 + 4 * 200 + 2) / 5
        Assert.Equal(0, v[4]);
        Assert.Equal(255, v[5]);
        // the remaining texels have code 0
        Assert.Equal(100, v[15]);
    }

    [Fact]
    public void SignedRgtcReadsMinus128AsMinus127AndRoundsAwayFromZero()
    {
        Span<byte> v = stackalloc byte[16];
        // r0 = 127 > r1 = -127: code 2 = (6 * 127 - 127) / 7 = 90.7 -> 91, code 7 = (127 - 6 * 127) / 7 -> -91
        BcSoftware.SignedBlock(ChannelBlock(0x7F, 0x81, 0, 1, 2, 7), v);
        Assert.Equal(127, (sbyte)v[0]);
        Assert.Equal(-127, (sbyte)v[1]);
        Assert.Equal(91, (sbyte)v[2]);
        Assert.Equal(-91, (sbyte)v[3]);
        // -128 is the same value as -127 (both -1.0): an endpoint pair (-128, -127) is the six-value mode with
        // equal endpoints, so code 1 is -127 and the extra codes are -1.0 and +1.0
        BcSoftware.SignedBlock(ChannelBlock(0x80, 0x81, 0, 1, 6, 7), v);
        Assert.Equal(-127, (sbyte)v[0]);
        Assert.Equal(-127, (sbyte)v[1]);
        Assert.Equal(-127, (sbyte)v[2]);
        Assert.Equal(127, (sbyte)v[3]);
    }

    [Fact]
    public void Bc4AndBc5FillTheMissingChannelsWithZeroAndOne()
    {
        BcSoftware.ClearCache();
        using Texels u4 = BcSoftware.Decode(TexBase.UBC4, ChannelBlock(200, 200), 4, 4);
        Assert.Equal(new byte[] { 200, 0, 0, 255 }, Texel(u4, 5));
        byte[] bc5 = [.. ChannelBlock(10, 10), .. ChannelBlock(250, 250)];
        using Texels u5 = BcSoftware.Decode(TexBase.UBC5, bc5, 4, 4);
        Assert.Equal(new byte[] { 10, 250, 0, 255 }, Texel(u5, 9));
        // signed: one is 127 (+1.0 SNORM), the channels are two's complement bytes
        using Texels s4 = BcSoftware.Decode(TexBase.SBC4, ChannelBlock(0x81, 0x81), 4, 4);
        Assert.Equal(new byte[] { 0x81, 0, 0, 127 }, Texel(s4, 0));
        byte[] sbc5 = [.. ChannelBlock(0x40, 0x40), .. ChannelBlock(0xC0, 0xC0)];
        using Texels s5 = BcSoftware.Decode(TexBase.SBC5, sbc5, 4, 4);
        Assert.Equal(new byte[] { 0x40, 0xC0, 0, 127 }, Texel(s5, 3));
    }

    [Fact]
    public void FormatForPicksSnormOnlyForTheSignedFormats()
    {
        Assert.Equal(Format.R8G8B8A8SNorm, BcSoftware.FormatFor(TexBase.SBC4));
        Assert.Equal(Format.R8G8B8A8SNorm, BcSoftware.FormatFor(TexBase.SBC5));
        foreach (TexBase f in new[] { TexBase.UBC1, TexBase.UBC2, TexBase.UBC3, TexBase.UBC4, TexBase.UBC5 })
        {
            Assert.Equal(Format.R8G8B8A8Unorm, BcSoftware.FormatFor(f));
        }
    }

    [Fact]
    public void LevelsThatAreNotAMultipleOfFourKeepOnlyTheirTexels()
    {
        BcSoftware.ClearCache();
        // 6x2: two blocks in a row; the second block's columns 2-3 and every block's rows 2-3 are outside the level
        byte[] blocks = [.. ColourBlock(Red565, Red565), .. ColourBlock(Blue565, Blue565)];
        using Texels t = BcSoftware.Decode(TexBase.UBC1, blocks, 6, 2);
        Assert.Equal(6 * 2 * 4, t.Length);
        for (int y = 0; y < 2; y++)
        {
            for (int x = 0; x < 6; x++)
            {
                byte[] expected = x < 4 ? [255, 0, 0, 255] : [0, 0, 255, 255];
                Assert.Equal(expected, Texel(t, y * 6 + x));
            }
        }
    }

    [Fact]
    public void ATruncatedLevelLeavesTheMissingBlocksAtTheirDefault()
    {
        BcSoftware.ClearCache();
        // 8x4 needs two blocks; only one is there: the second stays zero (BC1) or (0, 0, 0, 1) (BC4)
        using Texels bc1 = BcSoftware.Decode(TexBase.UBC1, ColourBlock(Red565, Red565), 8, 4);
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, Texel(bc1, 3));
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, Texel(bc1, 4));
        using Texels bc4 = BcSoftware.Decode(TexBase.UBC4, ChannelBlock(99, 99), 8, 4);
        Assert.Equal(new byte[] { 99, 0, 0, 255 }, Texel(bc4, 3));
        Assert.Equal(new byte[] { 0, 0, 0, 255 }, Texel(bc4, 4));
        // a base format that is not block-compressed decodes to an all-zero level of the right size
        using Texels none = BcSoftware.Decode(TexBase.ETC1, new byte[8], 4, 4);
        Assert.Equal(64, none.Length);
        Assert.All(none.Span.ToArray(), b => Assert.Equal(0, b));
    }

    [Fact]
    public void UnchangedLevelsComeFromTheCacheAndTheOldestIsEvicted()
    {
        long budget = BcSoftware.CacheBudget;
        try
        {
            BcSoftware.ClearCache();
            // a 4x4 level is 64 bytes; levels up to budget / 8 are cached, so 512 bytes hold eight of them
            BcSoftware.CacheBudget = 512;
            byte[] first = ColourBlock(Red565, Red565);
            using (Texels a = BcSoftware.Decode(TexBase.UBC1, first, 4, 4))
            {
                long decodes = BcSoftware.Decodes;
                long hits = BcSoftware.CacheHits;
                using Texels b = BcSoftware.Decode(TexBase.UBC1, first, 4, 4);
                Assert.Equal(decodes, BcSoftware.Decodes);
                Assert.Equal(hits + 1, BcSoftware.CacheHits);
                Assert.Equal(a.Span.ToArray(), b.Span.ToArray());
                // the same blocks under another format or size are another entry
                using Texels c = BcSoftware.Decode(TexBase.UBC4, first, 4, 4);
                Assert.Equal(decodes + 1, BcSoftware.Decodes);
            }

            for (ushort i = 1; i <= 8; i++)
            {
                using Texels t = BcSoftware.Decode(TexBase.UBC1, ColourBlock(i, i), 4, 4);
            }

            Assert.Contains("entries", BcSoftware.Summary());
            long before = BcSoftware.Decodes;
            using (Texels again = BcSoftware.Decode(TexBase.UBC1, first, 4, 4))
            {
                Assert.Equal(before + 1, BcSoftware.Decodes);
                Assert.Equal(new byte[] { 255, 0, 0, 255 }, Texel(again, 0));
            }

            // a level larger than budget / 8 (8x8 = 256 bytes) is decoded every time
            byte[] large = [.. first, .. first, .. first, .. first];
            BcSoftware.Decode(TexBase.UBC1, large, 8, 8).Dispose();
            long afterLarge = BcSoftware.Decodes;
            using (Texels l2 = BcSoftware.Decode(TexBase.UBC1, large, 8, 8))
            {
                Assert.Equal(afterLarge + 1, BcSoftware.Decodes);
            }
        }
        finally
        {
            BcSoftware.CacheBudget = budget;
            BcSoftware.ClearCache();
        }
    }

    [Fact]
    public void TranscoderChannelsFollowTheSourceFormat()
    {
        foreach (TexBase f in new[] { TexBase.UBC1, TexBase.UBC2, TexBase.UBC3, TexBase.PVRT2BPP, TexBase.PVRT4BPP, TexBase.PVRTII2BPP, TexBase.PVRTII4BPP })
        {
            Assert.Equal(AstcChannels.Rgba, AstcTranscoder.ChannelsFor(f, false));
            Assert.Equal(AstcChannels.Rgba, AstcTranscoder.ChannelsFor(f, true));
        }

        Assert.Equal(AstcChannels.R, AstcTranscoder.ChannelsFor(TexBase.UBC4, false));
        Assert.Equal(AstcChannels.Rg, AstcTranscoder.ChannelsFor(TexBase.UBC5, false));
        // sRGB ASTC applies the curve to luminance only, and ASTC LDR has no signed data: those stay on RGBA8
        Assert.Null(AstcTranscoder.ChannelsFor(TexBase.UBC4, true));
        Assert.Null(AstcTranscoder.ChannelsFor(TexBase.UBC5, true));
        Assert.Null(AstcTranscoder.ChannelsFor(TexBase.SBC4, false));
        Assert.Null(AstcTranscoder.ChannelsFor(TexBase.SBC5, false));
        Assert.Null(AstcTranscoder.ChannelsFor(TexBase.ETC1, false));
        Assert.Equal(Format.Astc4x4SrgbBlock, AstcTranscoder.FormatFor(true));
        Assert.Equal(Format.Astc4x4UnormBlock, AstcTranscoder.FormatFor(false));
    }

    [Fact]
    public void TranscoderSwizzleRestoresTheBcLayoutUnderTheTextureSwizzle()
    {
        // luminance blocks decode (L, L, L, 1): BC4 samples (R, 0, 0, 1); luminance+alpha holds BC5's G in A
        Assert.Equal((ComponentSwizzle.R, ComponentSwizzle.Zero, ComponentSwizzle.Zero, ComponentSwizzle.One), AstcTranscoder.ChannelSwizzle(AstcChannels.R));
        Assert.Equal((ComponentSwizzle.R, ComponentSwizzle.A, ComponentSwizzle.Zero, ComponentSwizzle.One), AstcTranscoder.ChannelSwizzle(AstcChannels.Rg));
        Assert.Equal((ComponentSwizzle.R, ComponentSwizzle.G, ComponentSwizzle.B, ComponentSwizzle.A), AstcTranscoder.ChannelSwizzle(AstcChannels.Rgba));

        // a texture swizzle reading (G, R, 1, identity) of a BC5 texture: G comes from ASTC alpha, identity alpha is 1
        var texture = new ComponentMapping(ComponentSwizzle.G, ComponentSwizzle.R, ComponentSwizzle.One, ComponentSwizzle.Identity);
        ComponentMapping m = AstcTranscoder.Compose(texture, AstcChannels.Rg);
        Assert.Equal(ComponentSwizzle.A, m.R);
        Assert.Equal(ComponentSwizzle.R, m.G);
        Assert.Equal(ComponentSwizzle.One, m.B);
        Assert.Equal(ComponentSwizzle.One, m.A);
        var identity = new ComponentMapping(ComponentSwizzle.Identity, ComponentSwizzle.Identity, ComponentSwizzle.Identity, ComponentSwizzle.Identity);
        ComponentMapping r = AstcTranscoder.Compose(identity, AstcChannels.R);
        Assert.Equal(ComponentSwizzle.R, r.R);
        Assert.Equal(ComponentSwizzle.Zero, r.G);
        Assert.Equal(ComponentSwizzle.Zero, r.B);
        Assert.Equal(ComponentSwizzle.One, r.A);
    }

    [Fact]
    public void TranscodedBcDecodesToTheSameTexels()
    {
        AstcTranscoder.Reset();
        TranscodeDiskCache? disk = AstcTranscoder.Disk;
        AstcTranscoder.Disk = null;
        try
        {
            // 8x8 BC1: four uniform blocks (red, blue, white, black) become exact void-extent ASTC blocks
            byte[] bc1 = [.. ColourBlock(Red565, Red565), .. ColourBlock(Blue565, Blue565), .. ColourBlock(0xFFFF, 0xFFFF), .. ColourBlock(0, 0)];
            using Texels astc = AstcTranscoder.FromBc(TexBase.UBC1, bc1, 8, 8, AstcChannels.Rgba);
            Assert.Equal(4 * AstcEncoder.BlockBytes, astc.Length);
            byte[] rgba = new byte[8 * 8 * 4];
            AstcDecoder.Decode(astc.Span, 8, 8, rgba);
            using Texels reference = BcSoftware.Decode(TexBase.UBC1, bc1, 8, 8);
            Assert.Equal(reference.Span.ToArray(), rgba);
            Assert.Equal(1, AstcTranscoder.Encoded);
            Assert.Equal(64, AstcTranscoder.EncodedTexels);

            // the same level again comes from memory, byte for byte
            using Texels again = AstcTranscoder.FromBc(TexBase.UBC1, bc1, 8, 8, AstcChannels.Rgba);
            Assert.Equal(1, AstcTranscoder.MemoryHits);
            Assert.Equal(1, AstcTranscoder.Encoded);
            Assert.Equal(astc.Span.ToArray(), again.Span.ToArray());

            // BC4 as luminance: R in L, sampled through (R, 0, 0, 1)
            using Texels l = AstcTranscoder.FromBc(TexBase.UBC4, ChannelBlock(200, 200), 4, 4, AstcChannels.R);
            byte[] lum = new byte[64];
            AstcDecoder.Decode(l.Span, 4, 4, lum);
            Assert.Equal(new byte[] { 200, 200, 200, 255 }, lum[..4]);
            Assert.Contains("no disk cache", AstcTranscoder.Summary());
        }
        finally
        {
            AstcTranscoder.Disk = disk;
            AstcTranscoder.Reset();
            BcSoftware.ClearCache();
        }
    }

    [Fact]
    public void TranscodedLevelsAreReadBackFromTheDiskCache()
    {
        string dir = Path.Combine(Path.GetTempPath(), "vitasharp-astc-" + Guid.NewGuid().ToString("N"));
        TranscodeDiskCache? disk = AstcTranscoder.Disk;
        try
        {
            AstcTranscoder.Reset();
            AstcTranscoder.Disk = new TranscodeDiskCache(dir, 1 << 20);
            byte[] bc3 = [.. ChannelBlock(255, 0, 0, 1, 2, 7), .. ColourBlock(Red565, Blue565, 0, 1, 2, 3)];
            using Texels first = AstcTranscoder.FromBc(TexBase.UBC3, bc3, 4, 4, AstcChannels.Rgba);
            Assert.Equal(1, AstcTranscoder.Encoded);
            Assert.True(AstcTranscoder.Disk.Flush(TimeSpan.FromSeconds(30)));
            Assert.Equal(1, AstcTranscoder.Disk.Writes);

            // a new run (memory cleared) finds the level on disk instead of encoding it again
            AstcTranscoder.Reset();
            using Texels second = AstcTranscoder.FromBc(TexBase.UBC3, bc3, 4, 4, AstcChannels.Rgba);
            Assert.Equal(0, AstcTranscoder.Encoded);
            Assert.Equal(1, AstcTranscoder.DiskHits);
            Assert.Equal(first.Span.ToArray(), second.Span.ToArray());
            Assert.Contains("disk cache 1 hits", AstcTranscoder.Summary());

            // the same data as another size is another entry (the key includes format and size)
            using Texels other = AstcTranscoder.FromBc(TexBase.UBC3, bc3, 4, 2, AstcChannels.Rgba);
            Assert.Equal(1, AstcTranscoder.Encoded);
        }
        finally
        {
            // the cache's writer thread may still be writing the last entry into the folder deleted below
            AstcTranscoder.Disk?.Flush(TimeSpan.FromSeconds(30));
            AstcTranscoder.Disk = disk;
            AstcTranscoder.Reset();
            BcSoftware.ClearCache();
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
    }

    [Fact]
    public void PvrtcIsTranscodedFromItsDecodedTexels()
    {
        TranscodeDiskCache? disk = AstcTranscoder.Disk;
        AstcTranscoder.Disk = null;
        try
        {
            AstcTranscoder.Reset();
            // 8x8 PVRTC 4bpp: 4 words of 8 bytes; the transcode is the ASTC encoding of what Pvrtc.Decode gives
            byte[] data = new byte[32];
            for (int i = 0; i < data.Length; i++)
            {
                data[i] = (byte)(i * 37 + 11);
            }

            using Texels astc = AstcTranscoder.FromPvrtc(TexBase.PVRT4BPP, data, 8, 8, false, false);
            using Texels rgba = Pvrtc.Decode(data, 8, 8, false, false);
            byte[] expected = new byte[4 * AstcEncoder.BlockBytes];
            AstcEncoder.Encode(rgba.Span, 8, 8, expected, AstcChannels.Rgba);
            Assert.Equal(expected, astc.Span.ToArray());
            Assert.Equal(1, AstcTranscoder.Encoded);
        }
        finally
        {
            AstcTranscoder.Disk = disk;
            AstcTranscoder.Reset();
        }
    }
}
