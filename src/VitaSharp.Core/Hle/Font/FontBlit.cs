using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Font;

/// <summary>
/// Glyph blitter shared by libpgf and libpvf (both use the same routine, read from their disassembly): the glyph is
/// ADDED to the buffer with saturation (games clear the buffer first), and the 1/64 fraction of xPos64 spreads
/// each pixel horizontally into its right neighbour (coverage * frac / 64 moves to x + 1). Only buffer formats
/// 4 bpp (two pixels per byte, even x in the low nibble) and 8 bpp exist. The clip rectangle is in buffer pixels;
/// pixels left of it still spill into x + 1, the spill is limited by the buffer width only.
/// </summary>
internal static class FontBlit
{
    public const uint Format4 = 0, Format8 = 2;

    /// <summary>Glyph image descriptor (SceFontGlyphImage / ScePvfUserImageBufferRec, both 0x18 bytes):
    /// +0 pixelFormat, +4 xPos64, +8 yPos64, +0xC width u16, +0xE height u16, +0x10 bytesPerLine u16, +0x14 buffer.</summary>
    public readonly record struct Target(uint Format, int XPos64, int YPos64, int BufWidth, int BufHeight, int BytesPerLine, uint Buffer)
    {
        public static Target Read(uint addr) => new(GuestMemory.R32(addr), GuestMemory.Read<int>(addr + 4), GuestMemory.Read<int>(addr + 8),
            GuestMemory.Read<ushort>(addr + 0xC), GuestMemory.Read<ushort>(addr + 0xE), GuestMemory.Read<ushort>(addr + 0x10), GuestMemory.R32(addr + 0x14));
    }

    /// <summary>Draws a w x h coverage bitmap (0..255, row stride <paramref name="pitch"/>) with its top-left pixel at
    /// (x0, y0), horizontal subpixel fraction <paramref name="frac"/> (0..63).</summary>
    public static void Draw(ReadOnlySpan<byte> src, int pitch, int w, int h, in Target t, int x0, int y0, int frac,
        int clipX, int clipY, int clipW, int clipH)
    {
        if (w <= 0 || h <= 0 || t.Buffer == 0) return;
        int xEnd = Math.Min(clipX + clipW, t.BufWidth), yEnd = Math.Min(clipY + clipH, t.BufHeight);
        int xStart = Math.Max(clipX, 0), yStart = Math.Max(clipY, 0);
        bool four = t.Format == Format4;
        for (int j = 0; j < h; j++)
        {
            int y = y0 + j;
            if (y < yStart) continue;
            if (y >= yEnd) break;
            uint row = t.Buffer + (uint)(y * t.BytesPerLine);
            for (int i = 0; i < w; i++)
            {
                int x = x0 + i;
                if (x >= xEnd) break;
                int v = src[j * pitch + i];
                if (four) Put4(row, t, x, x >= xStart, v >> 4, frac);
                else Put8(row, t, x, x >= xStart, v, frac);
            }
        }
    }

    static void Put8(uint row, in Target t, int x, bool inClip, int v, int frac)
    {
        int spill = frac * v >> 6;
        if (inClip) Add8(row + (uint)x, v - spill);
        else if (x + 1 >= t.BufWidth || x + 1 < 0) return;
        if (x + 1 < t.BufWidth) Add8(row + (uint)(x + 1), spill);
    }

    static void Put4(uint row, in Target t, int x, bool inClip, int v, int frac)
    {
        int spill = frac * v >> 6, b = x >> 1;
        bool odd = (x & 1) != 0;
        if (inClip && b >= 0)
        {
            if (odd)
            {
                AddNibble(row + (uint)b, true, v - spill);
                if (b + 1 < t.BytesPerLine) AddNibble(row + (uint)(b + 1), false, spill);
            }
            else
            {
                AddNibble(row + (uint)b, false, v - spill);
                AddNibble(row + (uint)b, true, spill);
            }
        }
        else if (odd && x + 1 < t.BufWidth && b + 1 >= 0 && b + 1 < t.BytesPerLine)
            AddNibble(row + (uint)(b + 1), false, spill);
    }

    static void Add8(uint a, int v)
    {
        if (v == 0) return;
        GuestMemory.Write(a, (byte)Math.Min(255, GuestMemory.Read<byte>(a) + v));
    }

    static void AddNibble(uint a, bool high, int v)
    {
        if (v == 0) return;
        int b = GuestMemory.Read<byte>(a);
        int n = Math.Min(15, (high ? b >> 4 : b & 0xF) + v);
        GuestMemory.Write(a, (byte)(high ? (b & 0x0F) | n << 4 : (b & 0xF0) | n));
    }
}
