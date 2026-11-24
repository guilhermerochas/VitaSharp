using System.Buffers.Binary;

namespace VitaSharp.Core.Compat.Ge;

/// <summary>
/// The GE's pixel formats and RGBA8 (R in the low byte, A in the high one: the 8888 format's own layout). 16-bit
/// formats put red in the low bits: 565 (no alpha), 5551 (alpha in bit 15), 4444; wider channels repeat their top
/// bits, narrower ones keep the top bits.
/// </summary>
public static class GePixels
{
    public const int Format565 = 0, Format5551 = 1, Format4444 = 2, Format8888 = 3;

    public static uint From565(ushort pixel)
    {
        uint value = pixel;
        uint red = Expand5(value & 0x1Fu);
        uint green = Expand6((value >> 5) & 0x3Fu);
        uint blue = Expand5((value >> 11) & 0x1Fu);
        return red | (green << 8) | (blue << 16);
    }

    public static uint From5551(ushort pixel)
    {
        uint value = pixel;
        uint red = Expand5(value & 0x1Fu);
        uint green = Expand5((value >> 5) & 0x1Fu);
        uint blue = Expand5((value >> 10) & 0x1Fu);
        uint alpha = (value & 0x8000) != 0 ? 0xFFu : 0;
        return red | (green << 8) | (blue << 16) | (alpha << 24);
    }

    public static uint From4444(ushort pixel)
    {
        uint value = pixel;
        uint red = (value & 0xFu) * 17;
        uint green = ((value >> 4) & 0xFu) * 17;
        uint blue = ((value >> 8) & 0xFu) * 17;
        uint alpha = ((value >> 12) & 0xFu) * 17;
        return red | (green << 8) | (blue << 16) | (alpha << 24);
    }

    public static ushort To565(uint rgba)
    {
        uint red = (rgba & 0xFF) >> 3;
        uint green = ((rgba >> 8) & 0xFF) >> 2;
        uint blue = ((rgba >> 16) & 0xFF) >> 3;
        return (ushort)(red | (green << 5) | (blue << 11));
    }

    public static ushort To5551(uint rgba)
    {
        uint red = (rgba & 0xFF) >> 3;
        uint green = ((rgba >> 8) & 0xFF) >> 3;
        uint blue = ((rgba >> 16) & 0xFF) >> 3;
        uint alpha = (rgba >> 31) & 1;
        return (ushort)(red | (green << 5) | (blue << 10) | (alpha << 15));
    }

    public static ushort To4444(uint rgba)
    {
        uint red = (rgba & 0xFF) >> 4;
        uint green = ((rgba >> 8) & 0xFF) >> 4;
        uint blue = ((rgba >> 16) & 0xFF) >> 4;
        uint alpha = (rgba >> 28) & 0xF;
        return (ushort)(red | (green << 4) | (blue << 8) | (alpha << 12));
    }

    /// <summary>A 16-bit pixel of a format to RGBA8.</summary>
    public static uint From16(ushort value, int format)
    {
        return format switch
        {
            Format565 => From565(value),
            Format5551 => From5551(value),
            _ => From4444(value),
        };
    }

    /// <summary>Memory pixels of a format (<paramref name="source"/>: their bytes) to RGBA8.</summary>
    public static void ToRgba(ReadOnlySpan<byte> source, int format, Span<uint> destination)
    {
        if (format == Format8888)
        {
            for (int i = 0; i < destination.Length; i++)
            {
                destination[i] = BinaryPrimitives.ReadUInt32LittleEndian(source[(i * 4)..]);
            }

            return;
        }

        for (int i = 0; i < destination.Length; i++)
        {
            destination[i] = From16(BinaryPrimitives.ReadUInt16LittleEndian(source[(i * 2)..]), format);
        }
    }

    /// <summary>RGBA8 pixels to memory pixels of a format.</summary>
    public static void FromRgba(ReadOnlySpan<uint> source, int format, Span<byte> destination)
    {
        for (int i = 0; i < source.Length; i++)
        {
            switch (format)
            {
                case Format8888:
                    BinaryPrimitives.WriteUInt32LittleEndian(destination[(i * 4)..], source[i]);
                    break;
                case Format565:
                    BinaryPrimitives.WriteUInt16LittleEndian(destination[(i * 2)..], To565(source[i]));
                    break;
                case Format5551:
                    BinaryPrimitives.WriteUInt16LittleEndian(destination[(i * 2)..], To5551(source[i]));
                    break;
                default:
                    BinaryPrimitives.WriteUInt16LittleEndian(destination[(i * 2)..], To4444(source[i]));
                    break;
            }
        }
    }

    private static uint Expand5(uint value)
    {
        return (value << 3) | (value >> 2);
    }

    private static uint Expand6(uint value)
    {
        return (value << 2) | (value >> 4);
    }
}
