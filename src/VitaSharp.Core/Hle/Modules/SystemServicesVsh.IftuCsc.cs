using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

// ksceIftuCsc (SceIftuForDriver, os0:kd/lowio.skprx 3.74, read in the module, study only; the structures as in the
// vita-headers psp2kern/lowio/iftu.h): the standalone IFTU engine converting one frame memory to memory, which
// SceAvcodec (decoded YUV to RGB) and SceCamera use. The module checks the destination's pixel format (bit 31 ignored)
// and the source's against fixed sets (else 0x803F0703), then programs the engine from the source plane state
// (SceIftuPlaneState: the frame buffer, the sampling start in 8.8 and step per destination pixel in 16.16, the
// destination position and size), the destination frame buffer (SceIftuFrameBuf: format, width, height, the bytes
// after each row, the plane addresses) and the optional SceIftuConvParams (+4 filter: 0 nearest, 1 bilinear; +8 and
// +0xC the two 15-word CSC blocks, kept by the engine when NULL; +0x10 the CSC control word; +0x14..+0x1C the
// background colour in the source's components; +0x20 alpha; +0x24 the field mode). NULL params: bilinear, black
// background (0, 0, 0), alpha 0xFF. It waits for the engine and returns 0.
// The CSC block: post offset of component 0 and of 1-2, the clamps of 0 and 1-2 (10 bits; a maximum of 0 leaves the
// full range), then the 3x3 matrix in S3.9 (12-bit fields), rows the output components and columns the input ones;
// the offsets are taken from YCbCr input before the matrix and added to YCbCr output after it, which is how the
// module's own tables (BT.601 in the firmware's SceAvcodec) read. Emulated: the engine's arithmetic in 10 bits per
// component, done here in C#. Which kept block applies when both are set (the hardware's control word decides) is
// not known: the second (+0xC, the one SceAvcodec passes) wins; with none set, a YCbCr/RGB change uses BT.601
// limited range. Source format 0 and the three formats of bit 31 (purpose unknown) draw the background. The field
// modes (2, 3: one field of an interlaced frame) and the paddings are not modelled: the frame is progressive.
public sealed partial class SystemServicesVsh
{
    const int IftuCscWords = 15;

    /// <summary>The engine's two CSC blocks as last given (the module keeps them when a call passes NULL).</summary>
    [SnapshotMissing] // states of builds before it: no conversion ran, nothing kept
    readonly uint[] _iftuCscBlocks = new uint[IftuCscWords * 2];

    [SnapshotMissing] // states of builds before it: no conversion ran, nothing kept
    readonly bool[] _iftuCscBlockSet = new bool[2];

    /// <summary>BT.601 limited range YCbCr to RGB, in the engine's block layout.</summary>
    static readonly uint[] IftuBt601ToRgb =
    [
        0x40, 0x200, 0x3FF, 0, 0, 0,
        0x254, 0, 0x331,
        0x254, 0xF38, 0xE60,
        0x254, 0x408, 0,
    ];

    /// <summary>RGB to BT.601 limited range YCbCr, in the engine's block layout.</summary>
    static readonly uint[] IftuRgbToBt601 =
    [
        0x40, 0x200, 0x3AC, 0x40, 0x3C0, 0x40,
        0x84, 0x102, 0x32,
        0xFB4, 0xF6B, 0xE1,
        0xE1, 0xF44, 0xFDC,
    ];

    /// <summary>The pixel formats the destination may have (bit 31 ignored).</summary>
    static bool IftuCscOutputFormat(uint format)
    {
        return (format & 0x7FFFFFFF) is 1 or 2 or 4 or 8 or 0x10 or 0x20 or 0x40 or 0x80 or 0x100 or 0x1000 or 0x2000
            or 0x4000 or 0x8000 or 0x10000 or 0x20000 or 0x200000;
    }

    /// <summary>A frame buffer as the engine reads or writes it.</summary>
    readonly struct IftuSurface
    {
        public readonly uint Format;
        public readonly int Width;
        public readonly int Height;
        public readonly uint Plane0;
        public readonly uint Plane1;
        public readonly uint Plane2;
        public readonly int Pitch;
        public readonly int ChromaPitch;

        public IftuSurface(uint descriptor)
        {
            Format = GuestMemory.R32(descriptor) & 0x7FFFFFFF;
            Width = (int)GuestMemory.R32(descriptor + 4);
            Height = (int)GuestMemory.R32(descriptor + 8);
            int leftoverStride = (int)GuestMemory.R32(descriptor + 0xC);
            Plane0 = GuestMemory.R32(descriptor + 0x14);
            Plane1 = GuestMemory.R32(descriptor + 0x18);
            Plane2 = GuestMemory.R32(descriptor + 0x1C);
            Pitch = Width * IftuBytesPerPixel(Format) + leftoverStride;
            // NV12 interleaves Cb and Cr: a chroma row is as long as a luma row; the planar formats' chroma rows are
            // half a luma row, 16-pixel aligned
            ChromaPitch = Format == 0x10000 ? Pitch : ((Width / 2 + 15) & ~15) + leftoverStride / 2;
        }

        public bool IsYcbcr => Format is 0x10000 or 0x20000 or 0x200000;

        /// <summary>The chroma planes' subsampling: horizontal and vertical shifts.</summary>
        public int ChromaShiftX => IsYcbcr ? 1 : 0;

        public int ChromaShiftY => Format is 0x10000 or 0x20000 ? 1 : 0;
    }

    static int IftuBytesPerPixel(uint format)
    {
        return format switch
        {
            1 or 2 or 4 or 8 => 2,
            0x10 or 0x20 or 0x40 or 0x80 or 0x1000 or 0x2000 or 0x4000 or 0x8000 => 4,
            _ => 1,
        };
    }

    /// <summary>
    /// ksceIftuCsc(SceIftuFrameBuf *dst, SceIftuPlaneState *src, SceIftuConvParams *params): converts the source plane
    /// into the destination frame buffer and waits for it; 0, or 0x803F0703 for a pixel format outside the sets.
    /// </summary>
    [Hle(Nid = 0x67E37EFC, Library = "SceIftuForDriver")]
    int ksceIftuCsc(uint destination, uint source, uint parameters)
    {
        if (!IftuCscOutputFormat(GuestMemory.R32(destination)) || !IftuInputFormat(GuestMemory.R32(source)))
        {
            return IFTU_ERR_FORMAT;
        }

        bool bilinear = true;
        Span<int> background = stackalloc int[3];
        uint alpha = 0xFF;
        if (parameters != 0)
        {
            bilinear = GuestMemory.R32(parameters + 4) == 1;
            IftuKeepCscBlock(GuestMemory.R32(parameters + 8), 0);
            IftuKeepCscBlock(GuestMemory.R32(parameters + 0xC), 1);
            background[0] = (int)(GuestMemory.R32(parameters + 0x14) & 0x3FF);
            background[1] = (int)(GuestMemory.R32(parameters + 0x18) & 0x3FF);
            background[2] = (int)(GuestMemory.R32(parameters + 0x1C) & 0x3FF);
            alpha = GuestMemory.R32(parameters + 0x20) & 0xFF;
        }

        var input = new IftuSurface(source);
        var output = new IftuSurface(destination);
        ReadOnlySpan<uint> block = IftuCscBlockFor(input, output);
        uint sourceX = GuestMemory.R32(source + 0x24);
        uint sourceY = GuestMemory.R32(source + 0x28);
        uint stepX = GuestMemory.R32(source + 0x2C);
        uint stepY = GuestMemory.R32(source + 0x30);
        int destinationX = (int)GuestMemory.R32(source + 0x34);
        int destinationY = (int)GuestMemory.R32(source + 0x38);
        int width = (int)GuestMemory.R32(source + 0x3C);
        int height = (int)GuestMemory.R32(source + 0x40);
        width = Math.Min(width == 0 ? output.Width - destinationX : width, output.Width - destinationX);
        height = Math.Min(height == 0 ? output.Height - destinationY : height, output.Height - destinationY);
        bool drawsInput = input.Format != 0 && (input.Format & 0x80000000) == 0 && input.Width > 0 && input.Height > 0;

        Span<int> components = stackalloc int[4];
        Span<int> converted = stackalloc int[3];
        for (int row = 0; row < height; row++)
        {
            long fixedY = ((long)sourceY << 8) + (long)row * stepY;
            for (int column = 0; column < width; column++)
            {
                long fixedX = ((long)sourceX << 8) + (long)column * stepX;
                if (drawsInput)
                {
                    for (int component = 0; component < 4; component++)
                    {
                        components[component] = IftuSample(input, component, fixedX, fixedY, bilinear);
                    }
                }
                else
                {
                    background.CopyTo(components);
                    components[3] = 0xFF;
                }

                IftuConvert(block, input.IsYcbcr, output.IsYcbcr, components, converted);
                int pixelAlpha = components[3] * (int)alpha / 0xFF;
                IftuStore(output, destinationX + column, destinationY + row, converted, pixelAlpha);
            }
        }

        return 0;
    }

    void IftuKeepCscBlock(uint block, int which)
    {
        if (block == 0)
        {
            return;
        }

        for (int word = 0; word < IftuCscWords; word++)
        {
            _iftuCscBlocks[which * IftuCscWords + word] = GuestMemory.R32(block + (uint)word * 4);
        }

        _iftuCscBlockSet[which] = true;
    }

    /// <summary>The CSC block of a conversion: a kept one (the second first), else BT.601 for a YCbCr/RGB change,
    /// else none (empty: the components pass unchanged).</summary>
    ReadOnlySpan<uint> IftuCscBlockFor(in IftuSurface input, in IftuSurface output)
    {
        if (_iftuCscBlockSet[1])
        {
            return _iftuCscBlocks.AsSpan(IftuCscWords, IftuCscWords);
        }

        if (_iftuCscBlockSet[0])
        {
            return _iftuCscBlocks.AsSpan(0, IftuCscWords);
        }

        if (input.IsYcbcr == output.IsYcbcr)
        {
            return [];
        }

        return input.IsYcbcr ? IftuBt601ToRgb : IftuRgbToBt601;
    }

    /// <summary>The CSC of one pixel: offsets taken from YCbCr input, the S3.9 matrix, offsets added to YCbCr output,
    /// the clamps.</summary>
    static void IftuConvert(ReadOnlySpan<uint> block, bool inputYcbcr, bool outputYcbcr, ReadOnlySpan<int> components, Span<int> converted)
    {
        if (block.IsEmpty)
        {
            converted[0] = components[0];
            converted[1] = components[1];
            converted[2] = components[2];
            return;
        }

        int offset0 = (int)(block[0] & 0x3FF);
        int offset12 = (int)(block[1] & 0x3FF);
        Span<int> centred = stackalloc int[3];
        for (int component = 0; component < 3; component++)
        {
            int offset = component == 0 ? offset0 : offset12;
            centred[component] = inputYcbcr ? components[component] - offset : components[component];
        }

        for (int row = 0; row < 3; row++)
        {
            int sum = 0;
            for (int column = 0; column < 3; column++)
            {
                int coefficient = (int)(block[6 + row * 3 + column] << 20) >> 20;
                sum += coefficient * centred[column];
            }

            int value = (sum + 256) >> 9;
            if (outputYcbcr)
            {
                value += row == 0 ? offset0 : offset12;
            }

            int maximum = (int)(block[row == 0 ? 2 : 4] & 0x3FF);
            int minimum = (int)(block[row == 0 ? 3 : 5] & 0x3FF);
            if (maximum <= minimum)
            {
                maximum = 0x3FF;
                minimum = 0;
            }

            converted[row] = Math.Clamp(value, minimum, maximum);
        }
    }

    /// <summary>Component <paramref name="component"/> (0..2 in 10 bits, 3 alpha in 8) of the source at the 16.16
    /// position, nearest or bilinear, the edges repeated.</summary>
    static int IftuSample(in IftuSurface surface, int component, long fixedX, long fixedY, bool bilinear)
    {
        bool chroma = component is 1 or 2 && surface.IsYcbcr;
        int shiftX = chroma ? surface.ChromaShiftX : 0;
        int shiftY = chroma ? surface.ChromaShiftY : 0;
        int width = Math.Max(1, surface.Width >> shiftX);
        int height = Math.Max(1, surface.Height >> shiftY);
        long x = fixedX >> shiftX;
        long y = fixedY >> shiftY;
        int x0 = (int)Math.Clamp(x >> 16, 0, width - 1);
        int y0 = (int)Math.Clamp(y >> 16, 0, height - 1);
        if (!bilinear)
        {
            return IftuFetch(surface, component, x0, y0);
        }

        int x1 = Math.Min(x0 + 1, width - 1);
        int y1 = Math.Min(y0 + 1, height - 1);
        int fractionX = (int)((x >> 8) & 0xFF);
        int fractionY = (int)((y >> 8) & 0xFF);
        int top = IftuFetch(surface, component, x0, y0) * (256 - fractionX) + IftuFetch(surface, component, x1, y0) * fractionX;
        int bottom = IftuFetch(surface, component, x0, y1) * (256 - fractionX) + IftuFetch(surface, component, x1, y1) * fractionX;
        return (int)(((long)top * (256 - fractionY) + (long)bottom * fractionY + 32768) >> 16);
    }

    /// <summary>Component <paramref name="component"/> of one source pixel (in its plane's resolution).</summary>
    static int IftuFetch(in IftuSurface surface, int component, int x, int y)
    {
        switch (surface.Format)
        {
            case 0x10000:
            {
                if (component == 0)
                {
                    return Expand8(GuestMemory.Read<byte>(surface.Plane0 + (uint)(y * surface.Pitch + x)));
                }

                if (component == 3)
                {
                    return 0xFF;
                }

                uint pair = surface.Plane1 + (uint)(y * surface.ChromaPitch + x * 2);
                return Expand8(GuestMemory.Read<byte>(pair + (uint)(component - 1)));
            }
            case 0x20000 or 0x200000:
            {
                return component switch
                {
                    0 => Expand8(GuestMemory.Read<byte>(surface.Plane0 + (uint)(y * surface.Pitch + x))),
                    1 => Expand8(GuestMemory.Read<byte>(surface.Plane1 + (uint)(y * surface.ChromaPitch + x))),
                    2 => Expand8(GuestMemory.Read<byte>(surface.Plane2 + (uint)(y * surface.ChromaPitch + x))),
                    _ => 0xFF,
                };
            }
            case 0x100:
            {
                // three planes, R, G and B
                return component switch
                {
                    0 => Expand8(GuestMemory.Read<byte>(surface.Plane0 + (uint)(y * surface.Pitch + x))),
                    1 => Expand8(GuestMemory.Read<byte>(surface.Plane1 + (uint)(y * surface.Pitch + x))),
                    2 => Expand8(GuestMemory.Read<byte>(surface.Plane2 + (uint)(y * surface.Pitch + x))),
                    _ => 0xFF,
                };
            }
        }

        uint address = surface.Plane0 + (uint)(y * surface.Pitch + x * IftuBytesPerPixel(surface.Format));
        uint pixel = IftuBytesPerPixel(surface.Format) == 2 ? GuestMemory.Read<ushort>(address) : GuestMemory.R32(address);
        (int red, int green, int blue, int alpha) = IftuUnpack(surface.Format, pixel);
        return component switch
        {
            0 => red,
            1 => green,
            2 => blue,
            _ => alpha,
        };
    }

    static int Expand8(int value) => (value << 2) | (value >> 6);

    /// <summary>A packed pixel's components: R, G, B in 10 bits, alpha in 8. "RGB" formats keep R in the low bits,
    /// "BGR" ones B; the _MULT forms (premultiplied) are laid out as the plain ones.</summary>
    static (int Red, int Green, int Blue, int Alpha) IftuUnpack(uint format, uint pixel)
    {
        int low;
        int middle;
        int high;
        int alpha;
        switch (format)
        {
            case 1 or 2:
                low = (int)(pixel & 0x1F);
                middle = (int)((pixel >> 5) & 0x3F);
                high = (int)((pixel >> 11) & 0x1F);
                low = (low << 5) | low;
                middle = (middle << 4) | (middle >> 2);
                high = (high << 5) | high;
                alpha = 0xFF;
                break;
            case 4 or 8:
                low = (int)(pixel & 0x1F);
                middle = (int)((pixel >> 5) & 0x1F);
                high = (int)((pixel >> 10) & 0x1F);
                low = (low << 5) | low;
                middle = (middle << 5) | middle;
                high = (high << 5) | high;
                alpha = (pixel & 0x8000) != 0 ? 0xFF : 0;
                break;
            case 0x40 or 0x80 or 0x4000 or 0x8000:
                low = (int)(pixel & 0x3FF);
                middle = (int)((pixel >> 10) & 0x3FF);
                high = (int)((pixel >> 20) & 0x3FF);
                alpha = (int)(pixel >> 30) * 0x55;
                break;
            default:
                low = Expand8((int)(pixel & 0xFF));
                middle = Expand8((int)((pixel >> 8) & 0xFF));
                high = Expand8((int)((pixel >> 16) & 0xFF));
                alpha = 0xFF;
                break;
        }

        return IftuRedLow(format) ? (low, middle, high, alpha) : (high, middle, low, alpha);
    }

    /// <summary>True for the formats with R in the low bits (the "RGB" names).</summary>
    static bool IftuRedLow(uint format) => format is 2 or 8 or 0x20 or 0x80 or 0x1000 or 0x4000;

    /// <summary>Writes one destination pixel (components in 10 bits, alpha in 8).</summary>
    static void IftuStore(in IftuSurface surface, int x, int y, ReadOnlySpan<int> components, int alpha)
    {
        switch (surface.Format)
        {
            case 0x10000 or 0x20000 or 0x200000:
            {
                GuestMemory.Write(surface.Plane0 + (uint)(y * surface.Pitch + x), (byte)(components[0] >> 2));
                if ((x & 1) != 0 || ((y & 1) != 0 && surface.ChromaShiftY != 0))
                {
                    return;
                }

                int chromaX = x >> 1;
                int chromaY = y >> surface.ChromaShiftY;
                if (surface.Format == 0x10000)
                {
                    uint pair = surface.Plane1 + (uint)(chromaY * surface.ChromaPitch + chromaX * 2);
                    GuestMemory.Write(pair, (byte)(components[1] >> 2));
                    GuestMemory.Write(pair + 1, (byte)(components[2] >> 2));
                }
                else
                {
                    GuestMemory.Write(surface.Plane1 + (uint)(chromaY * surface.ChromaPitch + chromaX), (byte)(components[1] >> 2));
                    GuestMemory.Write(surface.Plane2 + (uint)(chromaY * surface.ChromaPitch + chromaX), (byte)(components[2] >> 2));
                }

                return;
            }
            case 0x100:
            {
                uint at = (uint)(y * surface.Pitch + x);
                GuestMemory.Write(surface.Plane0 + at, (byte)(components[0] >> 2));
                GuestMemory.Write(surface.Plane1 + at, (byte)(components[1] >> 2));
                GuestMemory.Write(surface.Plane2 + at, (byte)(components[2] >> 2));
                return;
            }
        }

        bool redLow = IftuRedLow(surface.Format);
        int low = redLow ? components[0] : components[2];
        int middle = components[1];
        int high = redLow ? components[2] : components[0];
        uint address = surface.Plane0 + (uint)(y * surface.Pitch + x * IftuBytesPerPixel(surface.Format));
        switch (surface.Format)
        {
            case 1 or 2:
                GuestMemory.Write(address, (ushort)((low >> 5) | ((middle >> 4) << 5) | ((high >> 5) << 11)));
                break;
            case 4 or 8:
                GuestMemory.Write(address, (ushort)((low >> 5) | ((middle >> 5) << 5) | ((high >> 5) << 10) | (alpha >= 0x80 ? 0x8000 : 0)));
                break;
            case 0x40 or 0x80 or 0x4000 or 0x8000:
                GuestMemory.W32(address, (uint)low | ((uint)middle << 10) | ((uint)high << 20) | ((uint)(alpha >> 6) << 30));
                break;
            default:
                GuestMemory.W32(address, (uint)(low >> 2) | ((uint)(middle >> 2) << 8) | ((uint)(high >> 2) << 16) | ((uint)alpha << 24));
                break;
        }
    }
}
