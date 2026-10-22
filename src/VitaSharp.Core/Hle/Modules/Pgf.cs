using System.Collections.Concurrent;
using VitaSharp.Core.Hle.Font;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// ScePgf (libpgf): the PSP font API (sceFont*) over the firmware's pre-rendered PGF fonts in sa0:data/font/pgf
/// (bitmaps are in the file, so the output matches the real library pixel for pixel). Behaviour follows the
/// disassembly of the firmware's libpgf.suprx: argument checks and error codes, the 18-entry system font table
/// (jpn0, ltn0-15, kr0), the font matching rules, the alt char (0x5F) and resolution (128 dpi) defaults, missing
/// glyphs drawn as nothing, and the additive glyph blit with horizontal subpixel spreading (<see cref="FontBlit"/>).
/// Memory is taken from the game's allocFunc with the same three blocks the real NewLib allocates; handles are guest
/// addresses (the library block, and an entry of its font table for each open font). Fonts are read whole on open
/// (the real library streams them through a cache); user files opened in mode 0 are read through the game's I/O
/// callbacks like the real library does. Without the firmware font files sceFontOpen fails with HANDLER_OPEN_FAILED.
/// </summary>
public sealed class Pgf : HleModule
{
    internal const int ERR_OUT_OF_MEMORY = unchecked((int)0x80460001), ERR_INVALID_LIBID = unchecked((int)0x80460002),
        ERR_INVALID_PARAMETER = unchecked((int)0x80460003), ERR_HANDLER_OPEN_FAILED = unchecked((int)0x80460005),
        ERR_TOO_MANY_OPEN_FONTS = unchecked((int)0x80460009), ERR_INVALID_FONT_DATA = unchecked((int)0x8046000A),
        ERR_NOSUPPORT = unchecked((int)0x8046000E);
    internal const string SystemFontDir = "sa0:data/font/pgf/";
    const uint LibSize = 0x4C, FontEntrySize = 0x4C, FontInternalSize = 0x234, StyleSize = 0xA8;
    const ushort DefaultAltChar = 0x5F;

    /// <summary>libpgf's built-in font list (copied from the library's data segment): every entry is 10.125 pt or 7 pt
    /// at 128 dpi, weight 0, no sub-style/region/attributes.</summary>
    internal static readonly (string File, float Size, ushort Family, ushort Style, ushort Language, ushort Country, string Name)[] SystemFonts = BuildSystemFonts();

    static (string, float, ushort, ushort, ushort, ushort, string)[] BuildSystemFonts()
    {
        var list = new List<(string, float, ushort, ushort, ushort, ushort, string)> { ("jpn0.pgf", 10.125f, 1, 103, 1, 1, "FTT-NewRodin Pro DB") };
        ushort[] styles = [1, 1, 2, 2, 5, 5, 6, 6];
        for (int i = 0; i < 16; i++)
        {
            bool serif = (i & 1) != 0;
            list.Add(($"ltn{i}.pgf", i < 8 ? 10.125f : 7f, (ushort)(serif ? 2 : 1), styles[i & 7], 2, 1,
                serif ? "FTT-Matisse Pro Latin" : "FTT-NewRodin Pro Latin"));
        }
        list.Add(("kr0.pgf", 10.125f, 1, 1, 3, 3, "AsiaNHH(512Johab)"));
        return list.ToArray();
    }

    sealed class FontLib
    {
        public uint Handle, UserData, NumFonts, Alloc, Free, OpenFunc, CloseFunc, ReadFunc, SeekFunc;
        public uint FontTable, InternalTable;
        public float HRes = 128f, VRes = 128f;
        public ushort AltChar = DefaultAltChar;
        public OpenFont?[] Slots = [];
    }

    sealed class OpenFont
    {
        public required FontLib Lib;
        public required PgfFont Font;
        public int Slot, SystemIndex;
        public uint Handle;
    }

    static readonly ConcurrentDictionary<string, PgfFont?> s_files = new();
    readonly object _lock = new();
    readonly Dictionary<uint, FontLib> _libs = new();
    readonly Dictionary<uint, OpenFont> _fonts = new();

    /// <summary>Test hooks: guest calls (allocFunc/freeFunc/I/O callbacks) and system font file contents.</summary>
    internal Func<uint, uint[], uint>? GuestCallOverride;
    internal Func<string, byte[]?>? SystemFontOverride;

    uint CallGuest(uint fn, params uint[] args) =>
        GuestCallOverride != null ? GuestCallOverride(fn, args) : GuestThread.Current?.CallGuest(fn, args) ?? 0;

    FontLib? Lib(uint handle) { lock (_lock) return handle != 0 && _libs.TryGetValue(handle, out var l) ? l : null; }
    OpenFont? Font(uint handle) { lock (_lock) return handle != 0 && _fonts.TryGetValue(handle, out var f) ? f : null; }

    // ---- library -------------------------------------------------------------------------------------------------

    /// <summary>SceFontNewLibParams (0x2C): +0 userData, +4 numFonts, +8 cacheData, +0xC allocFunc, +0x10 freeFunc,
    /// +0x14 openFunc, +0x18 closeFunc, +0x1C readFunc, +0x20 seekFunc, +0x24 errorFunc, +0x28 ioFinishFunc.</summary>
    [Hle]
    uint sceFontNewLib(uint param, Ptr<uint> errorCode)
    {
        if (errorCode.IsNull) return 0;
        uint alloc = param != 0 ? GuestMemory.R32(param + 0xC) : 0, free = param != 0 ? GuestMemory.R32(param + 0x10) : 0;
        if (alloc == 0 || free == 0) { errorCode.Value = unchecked((uint)ERR_INVALID_PARAMETER); return 0; }
        errorCode.Value = 0;
        var lib = new FontLib
        {
            UserData = GuestMemory.R32(param), NumFonts = GuestMemory.R32(param + 4), Alloc = alloc, Free = free,
            OpenFunc = GuestMemory.R32(param + 0x14), CloseFunc = GuestMemory.R32(param + 0x18),
            ReadFunc = GuestMemory.R32(param + 0x1C), SeekFunc = GuestMemory.R32(param + 0x20),
        };
        lib.Handle = CallGuest(alloc, lib.UserData, LibSize);
        if (lib.Handle == 0) { errorCode.Value = unchecked((uint)ERR_OUT_OF_MEMORY); return 0; }
        if (lib.NumFonts - 1 > 8)
        {
            CallGuest(free, lib.UserData, lib.Handle);
            errorCode.Value = unchecked((uint)ERR_INVALID_PARAMETER);
            return 0;
        }
        GuestMemory.Copy(lib.Handle, param, 0x2C);
        lib.FontTable = CallGuest(alloc, lib.UserData, lib.NumFonts * FontEntrySize);
        lib.InternalTable = lib.FontTable == 0 ? 0 : CallGuest(alloc, lib.UserData, lib.NumFonts * FontInternalSize);
        if (lib.InternalTable == 0)
        {
            if (lib.FontTable != 0) CallGuest(free, lib.UserData, lib.FontTable);
            CallGuest(free, lib.UserData, lib.Handle);
            errorCode.Value = unchecked((uint)ERR_OUT_OF_MEMORY);
            return 0;
        }
        GuestMemory.Fill(lib.FontTable, 0, lib.NumFonts * FontEntrySize);
        lib.Slots = new OpenFont?[lib.NumFonts];
        lock (_lock) _libs[lib.Handle] = lib;
        return lib.Handle;
    }

    [Hle]
    int sceFontDoneLib(uint libHandle)
    {
        FontLib? lib;
        lock (_lock)
        {
            if (!_libs.Remove(libHandle, out lib)) return ERR_INVALID_LIBID;
            foreach (var f in lib.Slots)
                if (f != null) _fonts.Remove(f.Handle);
        }
        CallGuest(lib.Free, lib.UserData, lib.FontTable);
        CallGuest(lib.Free, lib.UserData, lib.InternalTable);
        CallGuest(lib.Free, lib.UserData, lib.Handle);
        return 0;
    }

    [Hle]
    int sceFontSetResolution(uint libHandle, float hRes, float vRes)
    {
        var lib = Lib(libHandle);
        if (lib == null) return ERR_INVALID_LIBID;
        if (!(hRes > 0) || !(vRes > 0)) return ERR_INVALID_PARAMETER;
        lib.HRes = hRes;
        lib.VRes = vRes;
        return 0;
    }

    [Hle]
    int sceFontSetAltCharacterCode(uint libHandle, uint charCode)
    {
        var lib = Lib(libHandle);
        if (lib == null) return ERR_INVALID_LIBID;
        lib.AltChar = (ushort)charCode;
        return 0;
    }

    // ---- system font list ----------------------------------------------------------------------------------------

    [Hle]
    int sceFontGetNumFontList(uint libHandle, Ptr<uint> errorCode)
    {
        if (errorCode.IsNull) return 0;
        errorCode.Value = 0;
        if (Lib(libHandle) == null) { errorCode.Value = unchecked((uint)ERR_INVALID_LIBID); return 0; }
        return SystemFonts.Length;
    }

    [Hle]
    int sceFontGetFontList(uint libHandle, uint fontStyle, int numFonts)
    {
        if (Lib(libHandle) == null) return ERR_INVALID_LIBID;
        if (fontStyle == 0) return ERR_INVALID_PARAMETER;
        int n = Math.Min(numFonts, SystemFonts.Length);
        for (int i = 0; i < n; i++) WriteSystemStyle(fontStyle + (uint)i * StyleSize, i);
        return 0;
    }

    /// <summary>The index is the THIRD argument (the one vita-headers calls "unknown"); the fourth is not read.</summary>
    [Hle]
    int sceFontGetFontInfoByIndexNumber(uint libHandle, uint fontStyle, int fontIndex, int unused)
    {
        if (Lib(libHandle) == null) return ERR_INVALID_LIBID;
        if (fontStyle == 0 || fontIndex < 0 || fontIndex >= SystemFonts.Length) return ERR_INVALID_PARAMETER;
        WriteSystemStyle(fontStyle, fontIndex);
        return 0;
    }

    /// <summary>SceFontStyle (0xA8): +0 fontH, +4 fontV, +8 fontHRes, +0xC fontVRes, +0x10 fontWeight (f32),
    /// +0x14 family, +0x16 style, +0x18 styleSub, +0x1A language, +0x1C region, +0x1E country (u16),
    /// +0x20 fontName[64], +0x60 fontFileName[64], +0xA0 attributes, +0xA4 expire.</summary>
    static void WriteSystemStyle(uint p, int index)
    {
        var e = SystemFonts[index];
        GuestMemory.Fill(p, 0, StyleSize);
        GuestMemory.Write(p, e.Size);
        GuestMemory.Write(p + 4, e.Size);
        GuestMemory.Write(p + 8, 128f);
        GuestMemory.Write(p + 0xC, 128f);
        GuestMemory.Write(p + 0x14, e.Family);
        GuestMemory.Write(p + 0x16, e.Style);
        GuestMemory.Write(p + 0x1A, e.Language);
        GuestMemory.Write(p + 0x1E, e.Country);
        GuestMemory.WriteCString(p + 0x20, e.Name, 64);
        GuestMemory.WriteCString(p + 0x60, e.File, 64);
    }

    /// <summary>Bit mask of the criteria a style sets (libpgf): 1 fontH, 2 fontV, 4 hRes, 8 vRes, 0x10 weight,
    /// 0x20 family, 0x40 style, 0x80 styleSub, 0x100 language, 0x200 region, 0x400 country, 0x800 name, 0x1000 file.</summary>
    static int Criteria(uint s)
    {
        int m = 0;
        for (int i = 0; i < 5; i++)
            if (GuestMemory.Read<float>(s + (uint)i * 4) > 0) m |= 1 << i;
        for (int i = 0; i < 6; i++)
            if (GuestMemory.Read<ushort>(s + 0x14 + (uint)i * 2) != 0) m |= 0x20 << i;
        if (GuestMemory.Read<byte>(s + 0x20) != 0) m |= 0x800;
        if (GuestMemory.Read<byte>(s + 0x60) != 0) m |= 0x1000;
        return m;
    }

    /// <summary>a and b equal to within 0.1 % (libpgf's size/weight comparison).</summary>
    static bool Near(float a, float b) => a >= b ? b > a * 0.999f : a > b * 0.999f;

    /// <summary>Weight, classification codes and names of system font i against the request.</summary>
    static bool Matches(uint s, int mask, int i)
    {
        var e = SystemFonts[i];
        if ((mask & 0x10) != 0 && !Near(GuestMemory.Read<float>(s + 0x10), 0)) return false;
        ushort[] codes = [e.Family, e.Style, 0, e.Language, 0, e.Country];
        for (int k = 0; k < 6; k++)
            if ((mask & (0x20 << k)) != 0 && GuestMemory.Read<ushort>(s + 0x14 + (uint)k * 2) != codes[k]) return false;
        if ((mask & 0x800) != 0 && Truncate(GuestMemory.ReadCString(s + 0x20, 64)) != e.Name) return false;
        if ((mask & 0x1000) != 0 && Truncate(GuestMemory.ReadCString(s + 0x60, 64)) != e.File) return false;
        return true;
    }

    static string Truncate(string s) => s.Length > 64 ? s[..64] : s;

    /// <summary>Requested pixel sizes (size x resolution, the library's resolution when the style has none); returns
    /// false when the style sets no size and no other criteria (the call then answers 0).</summary>
    static bool RequestedSize(uint s, FontLib lib, ref int mask, out float h, out float v)
    {
        h = v = 0;
        if ((mask & 1) != 0) h = ((mask & 4) != 0 ? GuestMemory.Read<float>(s + 8) : lib.HRes) * GuestMemory.Read<float>(s);
        else mask &= ~4;
        if ((mask & 2) != 0) v = ((mask & 8) != 0 ? GuestMemory.Read<float>(s + 0xC) : lib.VRes) * GuestMemory.Read<float>(s + 4);
        else
        {
            mask &= ~8;
            if (mask == 0) return false;
        }
        return true;
    }

    /// <summary>Index of the best system font: fonts must match every set criterion; among them the first one whose
    /// pixel size equals the request wins, otherwise the closest (H and V distances share one running minimum), and
    /// with no size criteria the LAST matching font. With no match a language-only search is retried.</summary>
    [Hle]
    int sceFontFindOptimumFont(uint libHandle, uint fontStyle, Ptr<uint> errorCode)
    {
        if (errorCode.IsNull) return 0;
        errorCode.Value = 0;
        var lib = Lib(libHandle);
        if (lib == null) { errorCode.Value = unchecked((uint)ERR_INVALID_LIBID); return 0; }
        if (fontStyle == 0) { errorCode.Value = unchecked((uint)ERR_INVALID_PARAMETER); return 0; }
        return FindOptimum(lib, fontStyle);
    }

    int FindOptimum(FontLib lib, uint s)
    {
        int mask = Criteria(s);
        if (!RequestedSize(s, lib, ref mask, out float reqH, out float reqV)) return 0;
        int best = -1;
        float bestDist = 4.2949673e9f;
        for (int i = 0; i < SystemFonts.Length; i++)
        {
            if (!Matches(s, mask, i)) continue;
            if ((mask & 3) == 0) { best = i; continue; }
            float size = SystemFonts[i].Size * 128f;
            if ((mask & 1) != 0)
            {
                if (Near(reqH, size)) return i;
                float d = MathF.Abs(reqH - size);
                if (d < bestDist) { bestDist = d; best = i; }
            }
            if ((mask & 2) != 0)
            {
                if (Near(reqV, size)) return i;
                float d = MathF.Abs(reqV - size);
                if (d < bestDist) { bestDist = d; best = i; }
            }
        }
        if (best >= 0) return best;
        ushort language = GuestMemory.Read<ushort>(s + 0x1A);
        if ((mask & 0x100) == 0 || (mask & ~0x100) == 0 || language == 0) return 0;
        // retry with the language alone (a stack copy of the style in the real library)
        uint tmp = Scratch();
        GuestMemory.Fill(tmp, 0, StyleSize);
        GuestMemory.Write(tmp + 0x1A, language);
        return Math.Max(0, FindOptimum(lib, tmp));
    }

    uint _scratch;
    uint Scratch() => _scratch != 0 ? _scratch : _scratch = (Emu?.Memory ?? GuestMemory.Instance)!.Alloc(StyleSize, "ScePgf style", MemoryRegion.User, 16);

    /// <summary>First system font matching every set criterion, sizes within 0.1 %; -1 when none.</summary>
    [Hle]
    int sceFontFindFont(uint libHandle, uint fontStyle, Ptr<uint> errorCode)
    {
        if (errorCode.IsNull) return 0;
        errorCode.Value = 0;
        var lib = Lib(libHandle);
        if (lib == null) { errorCode.Value = unchecked((uint)ERR_INVALID_LIBID); return 0; }
        if (fontStyle == 0) { errorCode.Value = unchecked((uint)ERR_INVALID_PARAMETER); return 0; }
        int mask = Criteria(fontStyle);
        if (!RequestedSize(fontStyle, lib, ref mask, out float reqH, out float reqV)) return 0;
        for (int i = 0; i < SystemFonts.Length; i++)
        {
            if (!Matches(fontStyle, mask, i)) continue;
            float size = SystemFonts[i].Size * 128f;
            if ((mask & 1) != 0 && !Near(reqH, size)) continue;
            if ((mask & 2) != 0 && !Near(reqV, size)) continue;
            return i;
        }
        return -1;
    }

    // ---- opening fonts -------------------------------------------------------------------------------------------

    [Hle]
    uint sceFontOpen(uint libHandle, int index, int mode, Ptr<uint> errorCode)
    {
        if (errorCode.IsNull) return 0;
        errorCode.Value = 0;
        if (index < 0) return Fail(errorCode, ERR_INVALID_PARAMETER);
        var lib = Lib(libHandle);
        if (lib == null) return Fail(errorCode, ERR_INVALID_LIBID);
        if (index >= SystemFonts.Length) return Fail(errorCode, ERR_INVALID_PARAMETER);
        lock (_lock)
            if (Array.IndexOf(lib.Slots, null) < 0) return Fail(errorCode, ERR_TOO_MANY_OPEN_FONTS);
        string file = SystemFonts[index].File, key = SystemFontDir + file;
        if (SystemFontOverride == null && s_files.TryGetValue(key, out var cached) && cached != null)
            return Register(lib, cached, index, errorCode, 0);
        byte[]? data = SystemFontOverride != null ? SystemFontOverride(file) : ReadVfs(key);
        if (data == null) return Fail(errorCode, ERR_HANDLER_OPEN_FAILED);
        var font = PgfFont.Parse(data);
        if (font != null && SystemFontOverride == null) s_files[key] = font;
        return Register(lib, font, index, errorCode, ERR_INVALID_FONT_DATA);
    }

    uint Register(FontLib lib, PgfFont? font, int systemIndex, Ptr<uint> errorCode, int errorIfInvalid)
    {
        lock (_lock)
        {
            int slot = Array.IndexOf(lib.Slots, null);
            if (slot < 0) return Fail(errorCode, ERR_TOO_MANY_OPEN_FONTS);
            if (font == null) return Fail(errorCode, errorIfInvalid);
            var f = new OpenFont { Lib = lib, Font = font, Slot = slot, SystemIndex = systemIndex, Handle = lib.FontTable + (uint)slot * FontEntrySize };
            lib.Slots[slot] = f;
            _fonts[f.Handle] = f;
            GuestMemory.Write<byte>(f.Handle, 1);
            GuestMemory.W32(f.Handle + 4, lib.InternalTable + (uint)slot * FontInternalSize);
            return f.Handle;
        }
    }

    static uint Fail(Ptr<uint> errorCode, int err)
    {
        errorCode.Value = unchecked((uint)err);
        return 0;
    }

    string? Host(string vitaPath) => Emu?.Vfs.Resolve(vitaPath);

    byte[]? ReadVfs(string vitaPath)
    {
        string? host = Host(vitaPath);
        if (host == null || !File.Exists(host))
        {
            Log.WarnOnce($"pgf-missing-{vitaPath}", $"ScePgf: font file {vitaPath} not found (install the firmware fonts into sa0:data/font/pgf)");
            return null;
        }
        return File.ReadAllBytes(host);
    }

    /// <summary>Mode 1 loads the file with sceIo; any other mode needs the game's open/close/read/seek callbacks
    /// (the real library streams through them, the HLE reads the whole file with them).</summary>
    [Hle]
    uint sceFontOpenUserFile(uint libHandle, uint file, int mode, Ptr<uint> errorCode)
    {
        if (errorCode.IsNull) return 0;
        var lib = Lib(libHandle);
        if (lib == null) return Fail(errorCode, ERR_INVALID_LIBID);
        if (mode != 1 && (lib.OpenFunc == 0 || lib.CloseFunc == 0 || lib.ReadFunc == 0 || lib.SeekFunc == 0))
            return Fail(errorCode, ERR_INVALID_PARAMETER);
        if (file == 0) return Fail(errorCode, ERR_INVALID_PARAMETER);
        errorCode.Value = 0;
        lock (_lock)
            if (Array.IndexOf(lib.Slots, null) < 0) return Fail(errorCode, ERR_TOO_MANY_OPEN_FONTS);
        string path = GuestMemory.ReadCString(file);
        byte[]? data = mode != 1 ? ReadThroughCallbacks(lib, file) : ReadVfs(path);
        if (data == null) return Fail(errorCode, ERR_HANDLER_OPEN_FAILED);
        return Register(lib, PgfFont.Parse(data), -1, errorCode, ERR_INVALID_FONT_DATA);
    }

    /// <summary>openFunc(userData, name, int *err) → handle; readFunc(userData, handle, buf, unitSize, count, int *err)
    /// → units read; seekFunc(userData, handle, offset); closeFunc(userData, handle).</summary>
    byte[]? ReadThroughCallbacks(FontLib lib, uint name)
    {
        const uint Chunk = 0x10000;
        uint buf = CallGuest(lib.Alloc, lib.UserData, Chunk + 4);
        if (buf == 0) return null;
        try
        {
            uint errPtr = buf + Chunk;
            GuestMemory.W32(errPtr, 0);
            uint fh = CallGuest(lib.OpenFunc, lib.UserData, name, errPtr);
            if (fh == 0 || GuestMemory.R32(errPtr) != 0) return null;
            var ms = new MemoryStream();
            CallGuest(lib.SeekFunc, lib.UserData, fh, 0);
            while (true)
            {
                uint got = CallGuest(lib.ReadFunc, lib.UserData, fh, buf, 1, Chunk, errPtr);
                if ((int)got <= 0 || GuestMemory.R32(errPtr) != 0) break;
                ms.Write(GuestMemory.ReadSpan(buf, (int)Math.Min(got, Chunk)));
                if (got < Chunk) break;
            }
            CallGuest(lib.CloseFunc, lib.UserData, fh);
            return ms.Length > 0 ? ms.ToArray() : null;
        }
        finally { CallGuest(lib.Free, lib.UserData, buf); }
    }

    [Hle]
    uint sceFontOpenUserMemory(uint libHandle, uint memoryFont, uint size, Ptr<uint> errorCode)
    {
        if (errorCode.IsNull) return 0;
        var lib = Lib(libHandle);
        if (lib == null) return Fail(errorCode, ERR_INVALID_LIBID);
        if (memoryFont == 0 || size == 0) return Fail(errorCode, ERR_INVALID_PARAMETER);
        errorCode.Value = 0;
        // the real library reads the buffer in place; the font data never changes, so a copy behaves the same
        var font = PgfFont.Parse(GuestMemory.ReadSpan(memoryFont, (int)Math.Min(size, 64u << 20)).ToArray());
        return Register(lib, font, -1, errorCode, ERR_INVALID_FONT_DATA);
    }

    [Hle]
    int sceFontClose(uint fontHandle)
    {
        lock (_lock)
        {
            if (!_fonts.Remove(fontHandle, out var f)) return ERR_INVALID_PARAMETER;
            f.Lib.Slots[f.Slot] = null;
            GuestMemory.Write<byte>(f.Handle, 0);
        }
        return 0;
    }

    /// <summary>Drops the glyph cache of the font; the HLE has none of its own (decoded glyphs are shared per file).</summary>
    [Hle] int sceFontFlush(uint fontHandle) => Font(fontHandle) == null ? ERR_INVALID_PARAMETER : 0;

    // ---- information ---------------------------------------------------------------------------------------------

    /// <summary>SceFontInfo (0x108): ten 26.6 maxima (+0..+0x24), the same as floats (+0x28..+0x4C), maxGlyphWidth/
    /// Height s16 (+0x50/+0x52), charMapLength (+0x54, the number of glyphs), shadowMapLength (+0x58, always 0),
    /// SceFontStyle (+0x5C, only the sizes for user fonts), BPP (+0x104).</summary>
    [Hle]
    int sceFontGetFontInfo(uint fontHandle, uint fontInfo)
    {
        var f = Font(fontHandle);
        if (f == null || fontInfo == 0) return ERR_INVALID_PARAMETER;
        var h = f.Font;
        int[] fixedValues = [h.MaxSizeW, h.MaxSizeH, h.MaxAscender, h.MaxDescender, h.MaxLeftXAdjust, h.MaxBaseYAdjust,
            h.MinCenterXAdjust, h.MaxTopYAdjust, h.MaxAdvanceH, h.MaxAdvanceV];
        GuestMemory.Fill(fontInfo, 0, 0x108);
        for (int i = 0; i < 10; i++)
        {
            GuestMemory.Write(fontInfo + (uint)i * 4, fixedValues[i]);
            // width and height are converted as unsigned, the rest as signed (as libpgf does)
            float v = i < 2 ? (uint)fixedValues[i] : fixedValues[i];
            GuestMemory.Write(fontInfo + 0x28 + (uint)i * 4, v / 64f);
        }
        GuestMemory.Write(fontInfo + 0x50, (short)h.MaxGlyphWidth);
        GuestMemory.Write(fontInfo + 0x52, (short)h.MaxGlyphHeight);
        GuestMemory.W32(fontInfo + 0x54, (uint)h.CharPointerLength);
        uint style = fontInfo + 0x5C;
        if (f.SystemIndex >= 0) WriteSystemStyle(style, f.SystemIndex);
        GuestMemory.Write(style, (uint)h.HSize / 64f);
        GuestMemory.Write(style + 4, (uint)h.VSize / 64f);
        GuestMemory.Write(style + 8, (uint)h.HResolution / 64f);
        GuestMemory.Write(style + 0xC, (uint)h.VResolution / 64f);
        GuestMemory.Write(fontInfo + 0x104, (byte)h.Bpp);
        return 0;
    }

    /// <summary>The glyph libpgf uses for a code: hidden (revision 3 part) codes and missing codes above 0x1F become the
    /// alt char; null (all-zero info, nothing drawn) when neither exists.</summary>
    static PgfGlyph? Resolve(OpenFont f, uint code)
    {
        var font = f.Font;
        if (font.IsHidden((int)code)) return font.GetGlyph(f.Lib.AltChar);
        var g = font.GetGlyph((int)code);
        if (g == null && code > 0x1F) g = font.GetGlyph(f.Lib.AltChar);
        return g;
    }

    /// <summary>SceFontCharInfo (0x3C): bitmap width/height/left/top, 26.6 width, height, ascender, descender,
    /// bearings HX/HY/VX/VY and advances H/V, shadowFlags s16, shadowId s16.</summary>
    [Hle]
    int sceFontGetCharInfo(uint fontHandle, uint charCode, uint charInfo)
    {
        var f = Font(fontHandle);
        if (f == null || charInfo == 0) return ERR_INVALID_PARAMETER;
        var g = Resolve(f, charCode);
        GuestMemory.Fill(charInfo, 0, 0x3C);
        if (g == null) return 0;
        int[] v = [g.Width, g.Height, g.Left, g.Top, g.DimW, g.DimH, g.YAdjH, g.YAdjH - g.DimH, g.XAdjH, g.YAdjH, g.XAdjV, g.YAdjV, g.AdvH, g.AdvV];
        for (int i = 0; i < v.Length; i++) GuestMemory.Write(charInfo + (uint)i * 4, v[i]);
        GuestMemory.Write(charInfo + 0x38, (short)g.ShadowFlags);
        GuestMemory.Write(charInfo + 0x3A, (short)g.ShadowId);
        return 0;
    }

    /// <summary>Bitmap size; for an overlay glyph the union of its parts' boxes.</summary>
    [Hle]
    int sceFontGetCharImageRect(uint fontHandle, uint charCode, uint charRect)
    {
        var f = Font(fontHandle);
        if (f == null || charRect == 0) return ERR_INVALID_PARAMETER;
        var g = Resolve(f, charCode);
        int w = 0, h = 0;
        if (g?.Components is { } parts)
        {
            int minX = 0x7FFF, minY = 0x7FFF, maxX = -0x8000, maxY = -0x8000;
            foreach (int c in parts)
            {
                var p = f.Font.GetGlyph(c);
                int pl = p?.Left ?? 0, pt = p?.Top ?? 0, pw = p?.Width ?? 0, ph = p?.Height ?? 0;
                minX = Math.Min(minX, pl); maxX = Math.Max(maxX, pl + pw);
                minY = Math.Min(minY, -pt); maxY = Math.Max(maxY, ph - pt);
            }
            w = Math.Max(0, maxX - minX);
            h = maxY == minY ? 0 : maxY - minY;
        }
        else if (g != null) { w = g.Width; h = g.Height; }
        GuestMemory.Write(charRect, (ushort)w);
        GuestMemory.Write(charRect + 2, (ushort)h);
        return 0;
    }

    [Hle]
    int sceFontGetCharGlyphImage(uint fontHandle, uint charCode, uint glyphImage)
    {
        var f = Font(fontHandle);
        if (f == null || glyphImage == 0) return ERR_INVALID_PARAMETER;
        var t = FontBlit.Target.Read(glyphImage);
        return DrawChar(f, charCode, t, 0, 0, t.BufWidth, t.BufHeight);
    }

    /// <summary>As GetCharGlyphImage, writing only inside the clip rectangle (buffer pixels); an empty rectangle or
    /// one entirely right of/below the buffer draws nothing and succeeds.</summary>
    [Hle]
    int sceFontGetCharGlyphImage_Clip(uint fontHandle, uint charCode, uint glyphImage, int clipX, int clipY, int clipWidth, int clipHeight)
    {
        var f = Font(fontHandle);
        if (f == null || glyphImage == 0) return ERR_INVALID_PARAMETER;
        var t = FontBlit.Target.Read(glyphImage);
        if (clipWidth == 0 || clipHeight == 0 || clipX + clipWidth == 0 || clipY + clipHeight == 0) return 0;
        if (clipX >= t.BufWidth || clipY >= t.BufHeight) return 0;
        return DrawChar(f, charCode, t, clipX, clipY, clipWidth, clipHeight);
    }

    /// <summary>The glyph's bitmap top-left goes at (xPos64, yPos64) >> 6; overlay parts are offset by their left/top
    /// relative to the composite glyph. Only formats 0 (4 bpp) and 2 (8 bpp) exist on the Vita.</summary>
    static int DrawChar(OpenFont f, uint code, in FontBlit.Target t, int clipX, int clipY, int clipW, int clipH)
    {
        var g = Resolve(f, code);
        if (g == null) return 0;
        int frac = t.XPos64 & 63;
        if (g.Components is { } parts)
        {
            foreach (int c in parts)
            {
                var p = f.Font.GetGlyph(c);
                if (p == null || p.Width == 0 || p.Height == 0) continue;
                if (t.Format != FontBlit.Format4 && t.Format != FontBlit.Format8) return ERR_INVALID_PARAMETER;
                Draw(p, t, (t.XPos64 + ((p.Left - g.Left) << 6)) >> 6, (t.YPos64 + ((g.Top - p.Top) << 6)) >> 6, frac, clipX, clipY, clipW, clipH);
            }
            return 0;
        }
        if (g.Width == 0 || g.Height == 0) return 0;
        if (t.Format != FontBlit.Format4 && t.Format != FontBlit.Format8) return ERR_INVALID_PARAMETER;
        Draw(g, t, t.XPos64 >> 6, t.YPos64 >> 6, frac, clipX, clipY, clipW, clipH);
        return 0;
    }

    static void Draw(PgfGlyph g, in FontBlit.Target t, int x0, int y0, int frac, int clipX, int clipY, int clipW, int clipH)
    {
        // 4-bit values widened to 8 bits (v * 0x11) so the shared blitter sees coverage 0..255
        var cov = new byte[g.Pixels.Length];
        for (int i = 0; i < cov.Length; i++) cov[i] = (byte)(g.Pixels[i] * 0x11);
        FontBlit.Draw(cov, g.Width, g.Width, g.Height, t, x0, y0, frac, clipX, clipY, clipW, clipH);
    }

    // ---- point / pixel conversions -------------------------------------------------------------------------------

    float Convert(uint libHandle, float value, Ptr<uint> errorCode, bool horizontal, bool toPoint)
    {
        if (errorCode.IsNull) return 0;
        errorCode.Value = 0;
        var lib = Lib(libHandle);
        if (lib == null) { errorCode.Value = unchecked((uint)ERR_INVALID_LIBID); return 0; }
        float res = horizontal ? lib.HRes : lib.VRes;
        if (toPoint && res * 0.999f < 0) { errorCode.Value = unchecked((uint)ERR_NOSUPPORT); return 0; }
        float r = toPoint ? value * 72f / res : value * res * (1f / 72f);
        if (float.IsInfinity(r) || float.IsNaN(r)) { errorCode.Value = unchecked((uint)ERR_NOSUPPORT); return 0; }
        return r;
    }

    [Hle] float sceFontPixelToPointH(uint libHandle, float fontPixelsH, Ptr<uint> errorCode) => Convert(libHandle, fontPixelsH, errorCode, true, true);
    [Hle] float sceFontPixelToPointV(uint libHandle, float fontPixelsV, Ptr<uint> errorCode) => Convert(libHandle, fontPixelsV, errorCode, false, true);
    [Hle] float sceFontPointToPixelH(uint libHandle, float fontPointsH, Ptr<uint> errorCode) => Convert(libHandle, fontPointsH, errorCode, true, false);
    [Hle] float sceFontPointToPixelV(uint libHandle, float fontPointsV, Ptr<uint> errorCode) => Convert(libHandle, fontPointsV, errorCode, false, false);
}
