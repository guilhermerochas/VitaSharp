namespace VitaSharp.Core.Compat.Ge;

/// <summary>
/// Level 0 of a GE texture to RGBA8: the direct formats (TPSM 0 565, 1 5551, 2 4444, 3 8888) and the indexed ones
/// (4 CLUT4, 5 CLUT8, 6 CLUT16, 7 CLUT32) through the loaded CLUT. An index becomes
/// <c>((index &gt;&gt; shift) &amp; mask) | (start &lt;&lt; 4)</c> with CLUTFORMAT's shift (bits 2..6), mask (8..15) and
/// start (16..20); the CLUT's entries are 16-bit (CLUTFORMAT 0..2) or 32-bit (3). A swizzled texture (TMODE bit 0)
/// is stored in blocks of 16 bytes by 8 rows, the blocks of a row of blocks one after the other.
/// </summary>
public static class GeTextureDecoder
{
    /// <summary>Decodes into <paramref name="destination"/> (width * height); false when the memory is not readable.</summary>
    public static bool Decode(IGeMemory memory, uint address, int bufferWidth, int width, int height, int format,
        bool swizzled, ReadOnlySpan<uint> clut, uint clutFormat, Span<uint> destination)
    {
        int bitsPerTexel = format switch
        {
            0 or 1 or 2 or 6 => 16,
            3 or 7 => 32,
            4 => 4,
            _ => 8,
        };
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
