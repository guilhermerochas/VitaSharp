using System.Buffers.Binary;
using VitaSharp.Core.Gpu;
using VitaSharp.Core.Gxm;

namespace VitaSharp.Renderer.Vulkan;

/// <summary>
/// The mean colour of each BC1-BC5 block, straight from the block data (endpoints and index counts, no texel decode),
/// written as ASTC void-extent blocks: the placeholder of a texture whose ASTC encode runs in the background
/// (<see cref="AstcTranscoder.Mode"/>). A pass over the compressed data, a fraction of decoding it.
/// </summary>
public static class BcBlockMeans
{
    /// <summary>Writes the placeholder of a w x h level of <paramref name="texBase"/> blocks (row order) into
    /// <paramref name="astcBlocks"/>; false for formats it does not read (signed BC4/BC5, non-BC).</summary>
    public static bool TryEncode(TexBase texBase, ReadOnlySpan<byte> blocks, uint w, uint h, AstcChannels channels, Span<byte> astcBlocks)
    {
        int blockBytes = texBase switch
        {
            TexBase.UBC1 or TexBase.UBC4 => 8,
            TexBase.UBC2 or TexBase.UBC3 or TexBase.UBC5 => 16,
            _ => 0,
        };
        int count = (int)(((w + 3) / 4) * ((h + 3) / 4));
        if (blockBytes == 0 || blocks.Length < count * blockBytes || astcBlocks.Length < count * AstcEncoder.BlockBytes)
        {
            return false;
        }

        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> block = blocks.Slice(i * blockBytes, blockBytes);
            Span<byte> output = astcBlocks.Slice(i * AstcEncoder.BlockBytes, AstcEncoder.BlockBytes);
            switch (texBase)
            {
                case TexBase.UBC1:
                    ColourMean(block, allowPunchThrough: true, out byte r1, out byte g1, out byte b1, out byte a1);
                    AstcEncoder.WriteMean(output, channels, r1, g1, b1, a1);
                    break;
                case TexBase.UBC2:
                    ColourMean(block[8..], allowPunchThrough: false, out byte r2, out byte g2, out byte b2, out _);
                    AstcEncoder.WriteMean(output, channels, r2, g2, b2, ExplicitAlphaMean(block));
                    break;
                case TexBase.UBC3:
                    ColourMean(block[8..], allowPunchThrough: false, out byte r3, out byte g3, out byte b3, out _);
                    AstcEncoder.WriteMean(output, channels, r3, g3, b3, InterpolatedMean(block));
                    break;
                case TexBase.UBC4:
                    AstcEncoder.WriteMean(output, channels, InterpolatedMean(block), 0, 0, 255);
                    break;
                default:
                    AstcEncoder.WriteMean(output, channels, InterpolatedMean(block), InterpolatedMean(block[8..]), 0, 255);
                    break;
            }
        }

        return true;
    }

    /// <summary>The mean of a BC1 colour block: its four-colour (or, for BC1 with c0 &lt;= c1, three-colour plus
    /// transparent black) palette weighted by how many texels use each entry.</summary>
    private static void ColourMean(ReadOnlySpan<byte> block, bool allowPunchThrough, out byte r, out byte g, out byte b, out byte a)
    {
        ushort c0 = BinaryPrimitives.ReadUInt16LittleEndian(block);
        ushort c1 = BinaryPrimitives.ReadUInt16LittleEndian(block[2..]);
        uint indices = BinaryPrimitives.ReadUInt32LittleEndian(block[4..]);
        Span<int> uses = stackalloc int[4];
        uses.Clear(); // (stackalloc is not zeroed under SkipLocalsInit)
        for (int t = 0; t < 16; t++)
        {
            uses[(int)((indices >> (t * 2)) & 3)]++;
        }

        int r0 = Expand5(c0 >> 11), g0 = Expand6((c0 >> 5) & 63), b0 = Expand5(c0 & 31);
        int r1 = Expand5(c1 >> 11), g1 = Expand6((c1 >> 5) & 63), b1 = Expand5(c1 & 31);
        bool threeColour = allowPunchThrough && c0 <= c1;
        // the sums of the palette entries times 6, so that both thirds and halves stay integers
        int sixR, sixG, sixB, opaque;
        if (!threeColour)
        {
            sixR = uses[0] * 6 * r0 + uses[1] * 6 * r1 + uses[2] * (4 * r0 + 2 * r1) + uses[3] * (2 * r0 + 4 * r1);
            sixG = uses[0] * 6 * g0 + uses[1] * 6 * g1 + uses[2] * (4 * g0 + 2 * g1) + uses[3] * (2 * g0 + 4 * g1);
            sixB = uses[0] * 6 * b0 + uses[1] * 6 * b1 + uses[2] * (4 * b0 + 2 * b1) + uses[3] * (2 * b0 + 4 * b1);
            opaque = 16;
        }
        else
        {
            sixR = uses[0] * 6 * r0 + uses[1] * 6 * r1 + uses[2] * 3 * (r0 + r1);
            sixG = uses[0] * 6 * g0 + uses[1] * 6 * g1 + uses[2] * 3 * (g0 + g1);
            sixB = uses[0] * 6 * b0 + uses[1] * 6 * b1 + uses[2] * 3 * (b0 + b1);
            opaque = 16 - uses[3]; // index 3: transparent black
        }

        r = (byte)((sixR + 48) / 96);
        g = (byte)((sixG + 48) / 96);
        b = (byte)((sixB + 48) / 96);
        a = (byte)((opaque * 255 + 8) / 16);
    }

    /// <summary>The mean of BC2's explicit 4-bit alpha values.</summary>
    private static byte ExplicitAlphaMean(ReadOnlySpan<byte> block)
    {
        int sum = 0;
        for (int i = 0; i < 8; i++)
        {
            sum += (block[i] & 15) + (block[i] >> 4);
        }

        return (byte)((sum * 17 + 8) / 16);
    }

    /// <summary>The mean of a BC3 alpha / BC4 block: two endpoints and 3-bit indices into eight levels (or six plus
    /// 0 and 255 when the first endpoint is not above the second).</summary>
    private static byte InterpolatedMean(ReadOnlySpan<byte> block)
    {
        int e0 = block[0], e1 = block[1];
        ulong bits = 0;
        for (int i = 0; i < 6; i++)
        {
            bits |= (ulong)block[2 + i] << (8 * i);
        }

        int sum = 0;
        for (int t = 0; t < 16; t++)
        {
            int index = (int)((bits >> (t * 3)) & 7);
            sum += Level(e0, e1, index);
        }

        return (byte)((sum + 8) / 16);
    }

    private static int Level(int e0, int e1, int index)
    {
        if (index == 0)
        {
            return e0;
        }

        if (index == 1)
        {
            return e1;
        }

        if (e0 > e1)
        {
            return ((8 - index) * e0 + (index - 1) * e1 + 3) / 7;
        }

        if (index == 6)
        {
            return 0;
        }

        if (index == 7)
        {
            return 255;
        }

        return ((6 - index) * e0 + (index - 1) * e1 + 2) / 5;
    }

    private static int Expand5(int value) => (value << 3) | (value >> 2);

    private static int Expand6(int value) => (value << 2) | (value >> 4);
}
