namespace VitaSharp.Core.Compat.Ge;

/// <summary>
/// Level 0 of a GE texture to RGBA8: the direct formats (TPSM 0 565, 1 5551, 2 4444, 3 8888) and the indexed ones
/// (4 CLUT4, 5 CLUT8, 6 CLUT16, 7 CLUT32) through the loaded CLUT. An index becomes
/// <c>((index &gt;&gt; shift) &amp; mask) | (start &lt;&lt; 4)</c> with CLUTFORMAT's shift (bits 2..6), mask (8..15) and
/// start (16..20); the CLUT's entries are 16-bit (CLUTFORMAT 0..2) or 32-bit (3). A swizzled texture (TMODE bit 0)
/// is stored in blocks of 16 bytes by 8 rows, the blocks of a row of blocks one after the other. DXT1, DXT3 and DXT5
/// (TPSM 8, 9, 10): see <see cref="DecodeDxt"/>.
/// </summary>
public static class GeTextureDecoder
{
    /// <summary>The bits of one texel of a format.</summary>
    public static int BitsPerTexel(int format)
    {
        return format switch
        {
            0 or 1 or 2 or 6 => 16,
            3 or 7 => 32,
            4 => 4,
            _ => 8,
        };
    }

    /// <summary>The bytes of memory a texture of <paramref name="height"/> rows reads (swizzled: whole 8-row blocks).</summary>
    public static int SourceBytes(int bufferWidth, int height, int format, bool swizzled)
    {
        if (format >= FormatDxt1)
        {
            int blockBytes = format == FormatDxt1 ? 8 : 16;
            return Math.Max(bufferWidth / 4, 1) * ((height + 3) / 4) * blockBytes;
        }

        int rowBytes = Math.Max(bufferWidth * BitsPerTexel(format) / 8, swizzled ? 16 : 1);
        int rows = swizzled ? (height + 7) & ~7 : height;
        return rowBytes * rows;
    }

    /// <summary>Decodes into <paramref name="destination"/> (width * height); false when the memory is not readable.</summary>
    public static bool Decode(IGeMemory memory, uint address, int bufferWidth, int width, int height, int format,
        bool swizzled, ReadOnlySpan<uint> clut, uint clutFormat, Span<uint> destination)
    {
        if (format >= FormatDxt1)
        {
            return format <= FormatDxt5 && DecodeDxt(memory, address, bufferWidth, width, height, format, destination);
        }

        int bitsPerTexel = BitsPerTexel(format);
        int rowBytes = Math.Max(bufferWidth * bitsPerTexel / 8, swizzled ? 16 : 1);
        int rows = swizzled ? (height + 7) & ~7 : height;
        Span<byte> texels = memory.Span(address, rowBytes * rows);
        if (texels.Length != rowBytes * rows)
        {
            return false;
        }

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int bitOffset = x * bitsPerTexel;
                int byteX = bitOffset / 8;
                int offset = swizzled ? SwizzledOffset(byteX, y, rowBytes) : y * rowBytes + byteX;
                destination[y * width + x] = Texel(texels, offset, bitOffset, format, clut, clutFormat);
            }
        }

        return true;
    }

    /// <summary>
    /// A DXT texture (TPSM 8 DXT1, 9 DXT3, 10 DXT5) to RGBA8. The PSP stores the S3TC blocks in its own order (PPSSPP's
    /// texture decoder, studied for the layout): a colour block is the four rows of 2-bit indices (one byte each,
    /// texel x at bits 2x), then colour 0 and colour 1 (565, blue in the low bits); DXT3 adds four rows of 4-bit alpha
    /// after the colour block, DXT5 a 48-bit index field (32 bits then 16) and the two alpha values. Blocks of 4x4
    /// texels, <paramref name="bufferWidth"/> / 4 a row. DXT1's colour 1 not above colour 0 gives the three-colour mode
    /// with transparent black.
    /// </summary>
    public static bool DecodeDxt(IGeMemory memory, uint address, int bufferWidth, int width, int height, int format, Span<uint> destination)
    {
        int blockBytes = format == FormatDxt1 ? 8 : 16;
        int blocksAcross = Math.Max(bufferWidth / 4, 1);
        int blocksDown = (height + 3) / 4;
        Span<byte> blocks = memory.Span(address, blocksAcross * blocksDown * blockBytes);
        if (blocks.Length != blocksAcross * blocksDown * blockBytes)
        {
            return false;
        }

        Span<uint> colors = stackalloc uint[4];
        Span<uint> alphas = stackalloc uint[8];
        for (int blockY = 0; blockY < blocksDown; blockY++)
        {
            for (int blockX = 0; blockX < (width + 3) / 4 && blockX < blocksAcross; blockX++)
            {
                ReadOnlySpan<byte> block = blocks.Slice((blockY * blocksAcross + blockX) * blockBytes, blockBytes);
                ReadOnlySpan<byte> colorBlock = format == FormatDxt1 ? block : block[..8];
                DxtColors(colorBlock, format == FormatDxt1, colors);
                if (format == FormatDxt5)
                {
                    Dxt5Alphas(block[14], block[15], alphas);
                }

                ulong alphaBits = format == FormatDxt5
                    ? ((ulong)(block[12] | (block[13] << 8)) << 32) | (uint)(block[8] | (block[9] << 8) | (block[10] << 16) | (block[11] << 24))
                    : 0;
                for (int y = 0; y < 4; y++)
                {
                    int textureY = blockY * 4 + y;
                    if (textureY >= height)
                    {
                        break;
                    }

                    for (int x = 0; x < 4; x++)
                    {
                        int textureX = blockX * 4 + x;
                        if (textureX >= width)
                        {
                            break;
                        }

                        uint color = colors[(colorBlock[y] >> (x * 2)) & 3];
                        if (format == FormatDxt3)
                        {
                            int row = block[8 + y * 2] | (block[9 + y * 2] << 8);
                            uint alpha = (uint)((row >> (x * 4)) & 0xF) * 17;
                            color = (color & 0x00FFFFFF) | (alpha << 24);
                        }
                        else if (format == FormatDxt5)
                        {
                            uint alpha = alphas[(int)((alphaBits >> ((y * 4 + x) * 3)) & 7)];
                            color = (color & 0x00FFFFFF) | (alpha << 24);
                        }

                        destination[textureY * width + textureX] = color;
                    }
                }
            }
        }

        return true;
    }

    /// <summary>The TPSM values of the DXT formats.</summary>
    public const int FormatDxt1 = 8, FormatDxt3 = 9, FormatDxt5 = 10;

    private static void DxtColors(ReadOnlySpan<byte> block, bool dxt1, Span<uint> colors)
    {
        ushort first = (ushort)(block[4] | (block[5] << 8));
        ushort second = (ushort)(block[6] | (block[7] << 8));
        (int r0, int g0, int b0) = Dxt565(first);
        (int r1, int g1, int b1) = Dxt565(second);
        colors[0] = Rgba(r0, g0, b0, 255);
        colors[1] = Rgba(r1, g1, b1, 255);
        if (!dxt1 || first > second)
        {
            colors[2] = Rgba((2 * r0 + r1) / 3, (2 * g0 + g1) / 3, (2 * b0 + b1) / 3, 255);
            colors[3] = Rgba((r0 + 2 * r1) / 3, (g0 + 2 * g1) / 3, (b0 + 2 * b1) / 3, 255);
        }
        else
        {
            colors[2] = Rgba((r0 + r1) / 2, (g0 + g1) / 2, (b0 + b1) / 2, 255);
            colors[3] = 0;
        }
    }

    private static void Dxt5Alphas(byte first, byte second, Span<uint> alphas)
    {
        alphas[0] = first;
        alphas[1] = second;
        if (first > second)
        {
            for (int i = 1; i < 7; i++)
            {
                alphas[i + 1] = (uint)(((7 - i) * first + i * second) / 7);
            }
        }
        else
        {
            for (int i = 1; i < 5; i++)
            {
                alphas[i + 1] = (uint)(((5 - i) * first + i * second) / 5);
            }

            alphas[6] = 0;
            alphas[7] = 255;
        }
    }

    private static (int, int, int) Dxt565(ushort value)
    {
        int red = (value >> 11) & 0x1F;
        int green = (value >> 5) & 0x3F;
        int blue = value & 0x1F;
        return ((red << 3) | (red >> 2), (green << 2) | (green >> 4), (blue << 3) | (blue >> 2));
    }

    private static uint Rgba(int red, int green, int blue, int alpha)
    {
        return (uint)(red | (green << 8) | (blue << 16) | (alpha << 24));
    }

    private static int SwizzledOffset(int byteX, int y, int rowBytes)
    {
        int blocksPerRow = rowBytes / 16;
        int block = (y / 8) * blocksPerRow + byteX / 16;
        return block * 128 + (y % 8) * 16 + byteX % 16;
    }

    private static uint Texel(ReadOnlySpan<byte> texels, int offset, int bitOffset, int format, ReadOnlySpan<uint> clut, uint clutFormat)
    {
        if (offset >= texels.Length)
        {
            return 0;
        }

        switch (format)
        {
            case 0:
            case 1:
            case 2:
                return GePixels.From16(Read16(texels, offset), format);
            case 3:
                return Read32(texels, offset);
            case 4:
            {
                int value = (bitOffset & 4) != 0 ? texels[offset] >> 4 : texels[offset] & 0xF;
                return Lookup(value, clut, clutFormat);
            }

            case 5:
                return Lookup(texels[offset], clut, clutFormat);
            case 6:
                return Lookup(Read16(texels, offset), clut, clutFormat);
            default:
                return Lookup((int)Read32(texels, offset), clut, clutFormat);
        }
    }

    private static uint Lookup(int index, ReadOnlySpan<uint> clut, uint clutFormat)
    {
        int shift = (int)((clutFormat >> 2) & 0x1F);
        int mask = (int)((clutFormat >> 8) & 0xFF);
        int start = (int)((clutFormat >> 16) & 0x1F);
        int entry = ((index >> shift) & mask) | (start << 4);
        int entryFormat = (int)(clutFormat & 3);
        if (entryFormat == GePixels.Format8888)
        {
            return clut[entry & (clut.Length - 1)];
        }

        uint word = clut[(entry >> 1) & (clut.Length - 1)];
        ushort value = (ushort)((entry & 1) != 0 ? word >> 16 : word);
        return GePixels.From16(value, entryFormat);
    }

    private static ushort Read16(ReadOnlySpan<byte> bytes, int offset)
    {
        return (ushort)(bytes[offset] | (bytes[offset + 1] << 8));
    }

    private static uint Read32(ReadOnlySpan<byte> bytes, int offset)
    {
        return (uint)(bytes[offset] | (bytes[offset + 1] << 8) | (bytes[offset + 2] << 16) | (bytes[offset + 3] << 24));
    }
}
