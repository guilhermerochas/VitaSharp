using System.Collections.Concurrent;
using VitaSharp.Core.Hle.Font;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// ScePvf (libpvf): the Vita's own font API over the OpenType fonts in sa0:data/font/pvf. The real library is a
/// wrapper over FreeType (libSceFt2); here <see cref="PvfFace"/> (stb_truetype outlines + FreeType's metric and
/// bitmap-box rules) stands in for it. What does not depend on the rasterizer follows the disassembly of the
/// firmware's libpvf.suprx: argument checks and error codes, the 18-entry system font list (jpn0-3, ltn0-7,
/// kr0-3, cn0-1), the matching rules, the defaults (128 dpi, 10.125 pt, alt char 0x5F, EM 1000), the char-size /
/// embolden / skew ranges, GetCharInfo filling only the glyph metrics (the bitmap fields stay 0), the glyph image
/// placed at pen + (bitmapLeft, -bitmapTop) with libpgf's additive subpixel blit, and the FT_Outline-shaped outline
/// struct. Approximate: anti-aliasing (no hinting), the embolden/skew units, GetFontInfo's maxima, the vertical
/// image origin and the shared-memory default fonts (opened from the same files).
/// </summary>
public sealed class Pvf : HleModule
{
    internal const int ERR_NOMEMORY = unchecked((int)0x80460001), ERR_LIBID = unchecked((int)0x80460002),
        ERR_ARG = unchecked((int)0x80460003), ERR_NOFILE = unchecked((int)0x80460004), ERR_FILEOPEN = unchecked((int)0x80460005),
        ERR_TOOMANYOPENED = unchecked((int)0x80460009), ERR_ILLEGALVERSION = unchecked((int)0x8046000A),
        ERR_NOSUPPORT = unchecked((int)0x8046000E),
        /// <summary>Returned when a character is missing and the alt char is 0 (no name in pvf.h).</summary>
        ERR_NO_ALT_CHAR = unchecked((int)0x8046000D);
    internal const string SystemFontDir = "sa0:data/font/pvf/";
    const uint LibSize = 0x460, FontEntrySize = 0x84, FontInternalSize = 0x114, StyleSize = 0xD8;

    /// <summary>libpvf's built-in font list (from the library's data segment): weight 0, names empty, file names only.</summary>
    internal static readonly (string File, ushort Family, ushort Style, ushort Language, ushort Country)[] SystemFonts =
    [
        ("jpn0.pvf", 1, 1, 1, 1), ("jpn1.pvf", 2, 1, 1, 1), ("jpn2.pvf", 1, 5, 1, 1), ("jpn3.pvf", 2, 5, 1, 1),
        ("ltn0.pvf", 1, 1, 2, 1), ("ltn1.pvf", 2, 1, 2, 1), ("ltn2.pvf", 1, 2, 2, 1), ("ltn3.pvf", 2, 2, 2, 1),
        ("ltn4.pvf", 1, 5, 2, 1), ("ltn5.pvf", 2, 5, 2, 1), ("ltn6.pvf", 1, 6, 2, 1), ("ltn7.pvf", 2, 6, 2, 1),
        ("kr0.pvf", 1, 1, 3, 3), ("kr1.pvf", 2, 1, 3, 3), ("kr2.pvf", 1, 5, 3, 3), ("kr3.pvf", 2, 5, 3, 3),
        ("cn0.pvf", 1, 1, 4, 1), ("cn1.pvf", 2, 1, 4, 1),
    ];
    const int DefaultLatinIndex = 4, DefaultJapaneseIndex = 0;

    sealed class PvfLib
    {
        public uint Handle, UserData, MaxFonts, Alloc, Realloc, Free, FontTable, InternalTable;
        public float HRes = 128f, VRes = 128f, Em = 1000f;
        public ushort AltChar = 0x5F;
        public OpenFont?[] Slots = [];
    }

    sealed class OpenFont
    {
        public required PvfLib Lib;
        public required PvfFace Face;
        public bool OwnsFace;
        public int Slot, SystemIndex;
        public uint Handle;
        public float CharH = 10.125f, CharV = 10.125f, Embolden, SkewX, SkewY;
    }

    static readonly ConcurrentDictionary<string, PvfFace> s_files = new();
    readonly object _lock = new();
    readonly Dictionary<uint, PvfLib> _libs = new();
    readonly Dictionary<uint, OpenFont> _fonts = new();


    /// <summary>Test hooks: guest calls (allocFunc/freeFunc) and system font file contents.</summary>
    internal Func<uint, uint[], uint>? GuestCallOverride;
    internal Func<string, byte[]?>? SystemFontOverride;

    uint CallGuest(uint fn, params uint[] args) =>
        GuestCallOverride != null ? GuestCallOverride(fn, args) : GuestThread.Current?.CallGuest(fn, args) ?? 0;

    PvfLib? Lib(uint handle) { lock (_lock) return handle != 0 && _libs.TryGetValue(handle, out var l) ? l : null; }
    OpenFont? Font(uint handle) { lock (_lock) return handle != 0 && _fonts.TryGetValue(handle, out var f) ? f : null; }

    static uint Fail(Ptr<int> errorCode, int err)
    {
        errorCode.Value = err;
        return 0;
    }

    // ---- libc hooks and library ----------------------------------------------------------------------------------

    /// <summary>SDK-internal: hands libSceFt2 the process's libc entry points (layout unknown, only kept for the log);
    /// the host renderer needs none.</summary>
    [Hle]
    int __scePvfSetFt2LibCHook(uint hooks)
    {

        Log.Debug($"ScePvf: libc hook table 0x{hooks:X8}");
        return 0;
    }

    [Hle]
    int __scePvfSetFt2DoneLibCHook(uint hook)
    {
        Log.Debug($"ScePvf: libc done hook 0x{hook:X8}");
        return 0;
    }

    /// <summary>ScePvfInitRec (0x1C): +0 userData, +4 maxNumFonts (1..18), +8 cache, +0xC reserved (must be 0),
    /// +0x10 allocFunc, +0x14 reallocFunc, +0x18 freeFunc.</summary>
    [Hle]
    uint scePvfNewLib(uint initParam, Ptr<int> errorCode)
    {
        if (errorCode.IsNull) return 0;
        if (initParam == 0 || GuestMemory.R32(initParam + 0xC) != 0) return Fail(errorCode, ERR_ARG);
        errorCode.Value = 0;
        var lib = new PvfLib
        {
            UserData = GuestMemory.R32(initParam), MaxFonts = GuestMemory.R32(initParam + 4),
            Alloc = GuestMemory.R32(initParam + 0x10), Realloc = GuestMemory.R32(initParam + 0x14), Free = GuestMemory.R32(initParam + 0x18),
        };
        if (lib.Alloc == 0 || lib.Realloc == 0 || lib.Free == 0) return Fail(errorCode, ERR_ARG);
        lib.Handle = CallGuest(lib.Alloc, lib.UserData, LibSize);
        if (lib.Handle == 0) return Fail(errorCode, ERR_NOMEMORY);
        if (lib.MaxFonts - 1 > 0x11)
        {
            CallGuest(lib.Free, lib.UserData, lib.Handle);
            return Fail(errorCode, ERR_ARG);
        }
        lib.FontTable = CallGuest(lib.Alloc, lib.UserData, lib.MaxFonts * FontEntrySize);
        lib.InternalTable = lib.FontTable == 0 ? 0 : CallGuest(lib.Alloc, lib.UserData, lib.MaxFonts * FontInternalSize);
        if (lib.InternalTable == 0)
        {
            if (lib.FontTable != 0) CallGuest(lib.Free, lib.UserData, lib.FontTable);
            CallGuest(lib.Free, lib.UserData, lib.Handle);
            return Fail(errorCode, ERR_NOMEMORY);
        }
        GuestMemory.Fill(lib.Handle, 0, LibSize);
        GuestMemory.Copy(lib.Handle + 8, initParam, 0x1C);
        GuestMemory.Fill(lib.FontTable, 0, lib.MaxFonts * FontEntrySize);
        lib.Slots = new OpenFont?[lib.MaxFonts];
        lock (_lock) _libs[lib.Handle] = lib;
        return lib.Handle;
    }

    [Hle]
    int scePvfDoneLib(uint libId)
    {
        PvfLib? lib;
        lock (_lock)
        {
            if (!_libs.Remove(libId, out lib)) return ERR_LIBID;
            foreach (var f in lib.Slots)
                if (f != null) { _fonts.Remove(f.Handle); if (f.OwnsFace) f.Face.Dispose(); }
        }
        CallGuest(lib.Free, lib.UserData, lib.FontTable);
        CallGuest(lib.Free, lib.UserData, lib.InternalTable);
        CallGuest(lib.Free, lib.UserData, lib.Handle);
        return 0;
    }

    /// <summary>Stored only: the library uses it in GetFontInfo's scaling, not when rendering (0 is accepted).</summary>
    [Hle]
    int scePvfSetEM(uint libId, float emValue)
    {
        var lib = Lib(libId);
        if (lib == null) return ERR_LIBID;
        if (emValue < 0 || emValue * 0.999f < 0 || float.IsNaN(emValue)) return ERR_ARG;
        lib.Em = emValue;
        return 0;
    }

    [Hle]
    int scePvfSetResolution(uint libId, float hResolution, float vResolution)
    {
        var lib = Lib(libId);
        if (lib == null) return ERR_LIBID;
        if (!(hResolution > 0) || !(vResolution > 0)) return ERR_ARG;
        lib.HRes = hResolution;
        lib.VRes = vResolution;
        return 0;
    }

    [Hle]
    int scePvfSetAltCharacterCode(uint libId, ushort charCode)
    {
        var lib = Lib(libId);
        if (lib == null) return ERR_LIBID;
        lib.AltChar = charCode;
        return 0;
    }

    // ---- system font list ----------------------------------------------------------------------------------------

    [Hle]
    int scePvfGetNumFontList(uint libId, Ptr<int> errorCode)
    {
        if (errorCode.IsNull) return 0;
        errorCode.Value = 0;
        if (Lib(libId) == null) { errorCode.Value = ERR_LIBID; return 0; }
        return SystemFonts.Length;
    }

    [Hle]
    int scePvfGetFontList(uint libId, uint fontStyleInfo, int arraySize)
    {
        if (Lib(libId) == null) return ERR_LIBID;
        if (fontStyleInfo == 0) return ERR_ARG;
        int n = Math.Min(arraySize, SystemFonts.Length);
        for (int i = 0; i < n; i++) WriteSystemStyle(fontStyleInfo + (uint)i * StyleSize, i);
        return 0;
    }

    [Hle]
    int scePvfGetFontInfoByIndexNumber(uint libId, uint fontStyleInfo, int fontIndex)
    {
        if (Lib(libId) == null) return ERR_LIBID;
        if (fontStyleInfo == 0 || fontIndex < 0 || fontIndex >= SystemFonts.Length) return ERR_ARG;
        WriteSystemStyle(fontStyleInfo, fontIndex);
        return 0;
    }

    /// <summary>ScePvfFontStyleInfo (0xD8): +0 weight f32, +4 family, +6 style, +8 subStyle, +0xA language,
    /// +0xC region, +0xE country (u16), +0x10 fontName[64], +0x50 styleName[64], +0x90 fileName[64],
    /// +0xD0 extraAttributes, +0xD4 expireDate.</summary>
    static void WriteSystemStyle(uint p, int index)
    {
        var e = SystemFonts[index];
        GuestMemory.Fill(p, 0, StyleSize);
        GuestMemory.Write(p + 4, e.Family);
        GuestMemory.Write(p + 6, e.Style);
        GuestMemory.Write(p + 0xA, e.Language);
        GuestMemory.Write(p + 0xE, e.Country);
        GuestMemory.WriteCString(p + 0x90, e.File, 64);
    }

    /// <summary>Criteria bits (libpvf): 0x10 weight, 0x20 family, 0x40 style, 0x80 subStyle, 0x100 language,
    /// 0x200 region, 0x400 country, 0x800 fontName, 0x1000 fileName.</summary>
    static int Criteria(uint s)
    {
        int m = GuestMemory.Read<float>(s) > 0 ? 0x10 : 0;
        for (int i = 0; i < 6; i++)
            if (GuestMemory.Read<ushort>(s + 4 + (uint)i * 2) != 0) m |= 0x20 << i;
        if (GuestMemory.Read<byte>(s + 0x10) != 0) m |= 0x800;
        if (GuestMemory.Read<byte>(s + 0x90) != 0) m |= 0x1000;
        return m;
    }

    static bool Matches(uint s, int mask, int i)
    {
        var e = SystemFonts[i];
        if ((mask & 0x10) != 0)
        {
            // weights compared to within 0.1 %; every system entry has weight 0
            float w = GuestMemory.Read<float>(s);
            if (!(0 > w * 0.999f)) return false;
        }
        ushort[] codes = [e.Family, e.Style, 0, e.Language, 0, e.Country];
        for (int k = 0; k < 6; k++)
            if ((mask & (0x20 << k)) != 0 && GuestMemory.Read<ushort>(s + 4 + (uint)k * 2) != codes[k]) return false;
        if ((mask & 0x800) != 0) return false; // system entries have empty names
        if ((mask & 0x1000) != 0 && Truncate(GuestMemory.ReadCString(s + 0x90, 64)) != e.File) return false;
        return true;
    }

    static string Truncate(string s) => s.Length > 64 ? s[..64] : s;

    /// <summary>First system font matching every set criterion; with no match, a language-only search; else 0.</summary>
    [Hle]
    int scePvfFindOptimumFont(uint libId, uint fontStyleInfo, Ptr<int> errorCode)
    {
        if (errorCode.IsNull) return 0;
        errorCode.Value = 0;
        if (Lib(libId) == null) { errorCode.Value = ERR_LIBID; return 0; }
        if (fontStyleInfo == 0) { errorCode.Value = ERR_ARG; return 0; }
        int mask = Criteria(fontStyleInfo);
        if (mask == 0) return 0;
        for (int i = 0; i < SystemFonts.Length; i++)
            if (Matches(fontStyleInfo, mask, i)) return i;
        ushort language = GuestMemory.Read<ushort>(fontStyleInfo + 0xA);
        if ((mask & 0x100) == 0 || (mask & ~0x100) == 0 || language == 0) return 0;
        for (int i = 0; i < SystemFonts.Length; i++)
            if (SystemFonts[i].Language == language) return i;
        return 0;
    }

    /// <summary>First system font matching every set criterion, -1 when none (0 when the style sets nothing).</summary>
    [Hle]
    int scePvfFindFont(uint libId, uint fontStyleInfo, Ptr<int> errorCode)
    {
        if (errorCode.IsNull) return 0;
        errorCode.Value = 0;
        if (Lib(libId) == null) { errorCode.Value = ERR_LIBID; return 0; }
        if (fontStyleInfo == 0) { errorCode.Value = ERR_ARG; return 0; }
        int mask = Criteria(fontStyleInfo);
        if (mask == 0) return 0;
        for (int i = 0; i < SystemFonts.Length; i++)
            if (Matches(fontStyleInfo, mask, i)) return i;
        return -1;
    }

    // ---- opening fonts -------------------------------------------------------------------------------------------

    /// <summary>The face of a system font file (shared by every library; the files never change).</summary>
    PvfFace? SystemFace(int index, out int error)
    {
        error = 0;
        string file = SystemFonts[index].File, key = SystemFontDir + file;
        if (SystemFontOverride == null && s_files.TryGetValue(key, out var cached)) return cached;
        byte[]? data = SystemFontOverride != null ? SystemFontOverride(file) : ReadVfs(key);
        if (data == null) { error = ERR_FILEOPEN; return null; }
        var face = PvfFace.Open(data, 0);
        if (face == null) { error = ERR_ILLEGALVERSION; return null; }
        if (SystemFontOverride == null) face = s_files.GetOrAdd(key, face);
        return face;
    }

    byte[]? ReadVfs(string vitaPath)
    {
        string? host = Emu?.Vfs.Resolve(vitaPath);
        if (host == null || !File.Exists(host))
        {
            Log.WarnOnce($"pvf-missing-{vitaPath}", $"ScePvf: font file {vitaPath} not found (install the firmware fonts into sa0:data/font/pvf)");
            return null;
        }
        return File.ReadAllBytes(host);
    }

    bool HasFreeSlot(PvfLib lib) { lock (_lock) return Array.IndexOf(lib.Slots, null) >= 0; }

    uint Register(PvfLib lib, PvfFace face, bool owns, int systemIndex, Ptr<int> errorCode)
    {
        lock (_lock)
        {
            int slot = Array.IndexOf(lib.Slots, null);
            if (slot < 0)
            {
                if (owns) face.Dispose();
                return Fail(errorCode, ERR_TOOMANYOPENED);
            }
            var f = new OpenFont { Lib = lib, Face = face, OwnsFace = owns, Slot = slot, SystemIndex = systemIndex, Handle = lib.FontTable + (uint)slot * FontEntrySize };
            lib.Slots[slot] = f;
            _fonts[f.Handle] = f;
            GuestMemory.Write<byte>(f.Handle, 1);
            GuestMemory.W32(f.Handle + 0x28, lib.InternalTable + (uint)slot * FontInternalSize);
            return f.Handle;
        }
    }

    uint OpenSystem(PvfLib lib, int index, Ptr<int> errorCode)
    {
        if (!HasFreeSlot(lib)) return Fail(errorCode, ERR_TOOMANYOPENED);
        var face = SystemFace(index, out int err);
        return face == null ? Fail(errorCode, err) : Register(lib, face, false, index, errorCode);
    }

    /// <summary>mode: 0 = SCE_PVF_FILEBASEDSTREAM, 1 = SCE_PVF_MEMORYBASEDSTREAM (the HLE always loads the whole file).</summary>
    [Hle]
    uint scePvfOpen(uint libId, int fontIndex, uint mode, Ptr<int> errorCode)
    {
        if (errorCode.IsNull) return 0;
        errorCode.Value = 0;
        if (mode > 1 || fontIndex < 0) return Fail(errorCode, ERR_ARG);
        var lib = Lib(libId);
        if (lib == null) return Fail(errorCode, ERR_LIBID);
        if (fontIndex >= SystemFonts.Length) return Fail(errorCode, ERR_ARG);
        return OpenSystem(lib, fontIndex, errorCode);
    }

    /// <summary>The real library opens these from a system-wide shared memory block ("SceSysDefaultJpnFont" /
    /// "SceSysDefaultLtnFont"); here they are the jpn0/ltn0 files.</summary>
    [Hle]
    uint scePvfOpenDefaultJapaneseFontOnSharedMemory(uint libId, Ptr<int> errorCode) => OpenDefault(libId, DefaultJapaneseIndex, errorCode);

    [Hle]
    uint scePvfOpenDefaultLatinFontOnSharedMemory(uint libId, Ptr<int> errorCode) => OpenDefault(libId, DefaultLatinIndex, errorCode);

    uint OpenDefault(uint libId, int index, Ptr<int> errorCode)
    {
        if (errorCode.IsNull) return 0;
        var lib = Lib(libId);
        if (lib == null) return Fail(errorCode, ERR_LIBID);
        errorCode.Value = 0;
        return OpenSystem(lib, index, errorCode);
    }

    uint OpenData(uint libId, uint mode, Func<byte[]?> read, uint subFontIndex, Ptr<int> errorCode)
    {
        if (errorCode.IsNull) return 0;
        if (mode > 1) return Fail(errorCode, ERR_ARG);
        var lib = Lib(libId);
        if (lib == null) return Fail(errorCode, ERR_LIBID);
        errorCode.Value = 0;
        if (!HasFreeSlot(lib)) return Fail(errorCode, ERR_TOOMANYOPENED);
        byte[]? data = read();
        if (data == null) return Fail(errorCode, ERR_FILEOPEN);
        var face = PvfFace.Open(data, (int)subFontIndex);
        if (face == null) return Fail(errorCode, subFontIndex != 0 ? ERR_ARG : ERR_ILLEGALVERSION);
        return Register(lib, face, true, -1, errorCode);
    }

    [Hle]
    uint scePvfOpenUserFile(uint libId, uint filename, uint mode, Ptr<int> errorCode) =>
        OpenUserFile(libId, filename, mode, 0, errorCode);

    [Hle]
    uint scePvfOpenUserFileWithSubfontIndex(uint libId, uint filename, uint mode, uint subFontIndex, Ptr<int> errorCode) =>
        OpenUserFile(libId, filename, mode, subFontIndex, errorCode);

    uint OpenUserFile(uint libId, uint filename, uint mode, uint subFontIndex, Ptr<int> errorCode)
    {
        if (errorCode.IsNull) return 0;
        if (filename == 0) return Fail(errorCode, ERR_ARG);
        string path = GuestMemory.ReadCString(filename);
        return OpenData(libId, mode, () => ReadVfs(path), subFontIndex, errorCode);
    }

    [Hle]
    uint scePvfOpenUserMemory(uint libId, uint addr, uint size, Ptr<int> errorCode) =>
        OpenUserMemory(libId, addr, size, 0, errorCode);

    [Hle]
    uint scePvfOpenUserMemoryWithSubfontIndex(uint libId, uint addr, uint size, uint subFontIndex, Ptr<int> errorCode) =>
        OpenUserMemory(libId, addr, size, subFontIndex, errorCode);

    /// <summary>The real library reads the game's buffer in place; the font data never changes, so a copy behaves the same.</summary>
    uint OpenUserMemory(uint libId, uint addr, uint size, uint subFontIndex, Ptr<int> errorCode)
    {
        if (errorCode.IsNull) return 0;
        if (addr == 0 || size == 0) return Fail(errorCode, ERR_ARG);
        return OpenData(libId, 1, () => GuestMemory.ReadSpan(addr, (int)Math.Min(size, 64u << 20)).ToArray(), subFontIndex, errorCode);
    }

    [Hle]
    int scePvfClose(uint fontId)
    {
        lock (_lock)
        {
            if (!_fonts.Remove(fontId, out var f)) return ERR_ARG;
            f.Lib.Slots[f.Slot] = null;
            GuestMemory.Write<byte>(f.Handle, 0);
            if (f.OwnsFace) f.Face.Dispose();
        }
        return 0;
    }

    [Hle] int scePvfFlush(uint fontId) => Font(fontId) == null ? ERR_ARG : 0;

    // ---- per-font style ------------------------------------------------------------------------------------------

    [Hle]
    int scePvfSetCharSize(uint fontId, float hSize, float vSize)
    {
        var f = Font(fontId);
        if (f == null || !(hSize > 0) || !(vSize > 0)) return ERR_ARG;
        f.CharH = hSize;
        f.CharV = vSize;
        return 0;
    }

    [Hle]
    int scePvfSetEmboldenRate(uint fontId, float emboldenRate)
    {
        var f = Font(fontId);
        if (f == null || !(emboldenRate >= -20f && emboldenRate <= 40f)) return ERR_ARG;
        f.Embolden = emboldenRate;
        return 0;
    }

    [Hle]
    int scePvfSetSkewValue(uint fontId, float angleX, float angleY)
    {
        var f = Font(fontId);
        if (f == null || !(angleX >= -30f && angleX <= 30f) || !(angleY >= -30f && angleY <= 30f)) return ERR_ARG;
        f.SkewX = angleX;
        f.SkewY = angleY;
        return 0;
    }

    /// <summary>Pixels per em from the point size and the library resolution; the embolden strength is
    /// rate x size(pt) x 64 / 100 in 26.6 (the value libpvf hands libSceFt2; its exact unit is unconfirmed).</summary>
    static PvfStyle Style(OpenFont f) =>
        new(f.CharH * f.Lib.HRes / 72f, f.CharV * f.Lib.VRes / 72f, f.SkewX, f.SkewY, f.Embolden * f.CharH * 64f / 100f);

    // ---- character queries ---------------------------------------------------------------------------------------

    [Hle]
    int scePvfIsElement(uint fontId, ushort charCode)
    {
        var f = Font(fontId);
        return f != null && charCode != 0xFFFF && f.Face.GlyphIndex(charCode) != 0 ? 1 : 0;
    }

    /// <summary>1 when the character exists and the font has a vertical form for it (GSUB 'vert'); the real library
    /// asks libSceFt2 a separate question whose exact semantics are unconfirmed.</summary>
    [Hle]
    int scePvfIsVertElement(uint fontId, ushort charCode)
    {
        var f = Font(fontId);
        if (f == null || charCode == 0xFFFF) return 0;
        int g = f.Face.GlyphIndex(charCode);
        return g != 0 && f.Face.HasVerticalForm(g) ? 1 : 0;
    }

    /// <summary>Glyph index for a code: missing codes (and 0xFFFF) use the library's alt char, which may itself be
    /// missing (then .notdef is used, as FreeType would); an alt char of 0 is an error.</summary>
    static int Glyph(OpenFont f, ushort code, bool vertical, out int error)
    {
        error = 0;
        int g = code != 0xFFFF ? f.Face.GlyphIndex(code) : 0;
        if (g == 0)
        {
            if (f.Lib.AltChar == 0) { error = ERR_NO_ALT_CHAR; return 0; }
            g = f.Face.GlyphIndex(f.Lib.AltChar);
        }
        return vertical ? f.Face.VerticalGlyph(g) : g;
    }

    /// <summary>ScePvfCharInfo (0x40): bitmapWidth/Height/Pitch, bitmapLeft/Top, then ScePvfIGlyphMetricsInfo (26.6:
    /// width, height, ascender, descender, horizontal bearing X/Y, vertical bearing X/Y, horizontal/vertical advance).
    /// libpvf only loads the metrics here: the bitmap fields are left 0.</summary>
    int CharInfo(uint fontId, ushort charCode, uint charInfo, bool vertical)
    {
        var f = Font(fontId);
        if (f == null || charInfo == 0) return ERR_ARG;
        int gi = Glyph(f, charCode, vertical, out int err);
        if (err != 0) return err;
        var g = f.Face.Load(gi, Style(f), vertical, false);
        GuestMemory.Fill(charInfo, 0, 0x40);
        int[] m = [g.Width, g.Height, g.HoriBearingY, g.HoriBearingY - g.Height, g.HoriBearingX, g.HoriBearingY,
            g.VertBearingX, g.VertBearingY, g.HoriAdvance, g.VertAdvance];
        for (int i = 0; i < m.Length; i++) GuestMemory.Write(charInfo + 0x14 + (uint)i * 4, m[i]);
        return 0;
    }

    [Hle] int scePvfGetCharInfo(uint fontId, ushort charCode, uint charInfo) => CharInfo(fontId, charCode, charInfo, false);
    [Hle] int scePvfGetVertCharInfo(uint fontId, ushort charCode, uint charInfo) => CharInfo(fontId, charCode, charInfo, true);

    int ImageRect(uint fontId, ushort charCode, uint rect, bool vertical)
    {
        var f = Font(fontId);
        if (f == null || rect == 0) return ERR_ARG;
        int gi = Glyph(f, charCode, vertical, out int err);
        if (err != 0) return err;
        var g = f.Face.Load(gi, Style(f), vertical, true);
        GuestMemory.Write(rect, (ushort)g.BitmapWidth);
        GuestMemory.Write(rect + 2, (ushort)g.BitmapHeight);
        return 0;
    }

    [Hle] int scePvfGetCharImageRect(uint fontId, ushort charCode, uint rect) => ImageRect(fontId, charCode, rect, false);
    [Hle] int scePvfGetVertCharImageRect(uint fontId, ushort charCode, uint rect) => ImageRect(fontId, charCode, rect, true);

    /// <summary>Horizontal: the bitmap's top-left goes at pen + (bitmapLeft, -bitmapTop), pen = (xPos64, yPos64) >> 6
    /// on the baseline. Vertical: at pen + (vertBearingX, vertBearingY) from the vertical origin (top centre).</summary>
    int GlyphImage(uint fontId, ushort charCode, uint imageBuffer, bool vertical, int clipX, int clipY, int clipW, int clipH, bool clipped)
    {
        var f = Font(fontId);
        if (f == null || imageBuffer == 0) return ERR_ARG;
        var t = FontBlit.Target.Read(imageBuffer);
        if (clipped)
        {
            if (clipW == 0 || clipH == 0 || clipX + clipW == 0 || clipY + clipH == 0) return 0;
            if (clipX >= t.BufWidth || clipY >= t.BufHeight) return 0;
        }
        else { clipX = clipY = 0; clipW = t.BufWidth; clipH = t.BufHeight; }
        int gi = Glyph(f, charCode, vertical, out int err);
        if (err != 0) return err;
        var g = f.Face.Load(gi, Style(f), vertical, true);
        if (g.BitmapWidth == 0 || g.BitmapHeight == 0) return 0;
        if (t.Format != FontBlit.Format4 && t.Format != FontBlit.Format8) return ERR_ARG;
        int left = g.Left, top = g.Top;
        if (vertical)
        {
            // shift from the horizontal origin to the vertical one
            left += (g.VertBearingX - g.HoriBearingX) >> 6;
            top += (-g.VertBearingY - g.HoriBearingY) >> 6;
        }
        FontBlit.Draw(g.Bitmap, g.BitmapWidth, g.BitmapWidth, g.BitmapHeight, t, (t.XPos64 >> 6) + left, (t.YPos64 >> 6) - top,
            t.XPos64 & 63, clipX, clipY, clipW, clipH);
        return 0;
    }

    [Hle]
    int scePvfGetCharGlyphImage(uint fontId, ushort charCode, uint imageBuffer) =>
        GlyphImage(fontId, charCode, imageBuffer, false, 0, 0, 0, 0, false);

    [Hle]
    int scePvfGetCharGlyphImage_Clip(uint fontId, ushort charCode, uint imageBuffer, int clipX, int clipY, uint clipWidth, uint clipHeight) =>
        GlyphImage(fontId, charCode, imageBuffer, false, clipX, clipY, (int)clipWidth, (int)clipHeight, true);

    [Hle]
    int scePvfGetVertCharGlyphImage(uint fontId, ushort charCode, uint imageBuffer) =>
        GlyphImage(fontId, charCode, imageBuffer, true, 0, 0, 0, 0, false);

    [Hle]
    int scePvfGetVertCharGlyphImage_Clip(uint fontId, ushort charCode, uint imageBuffer, int clipX, int clipY, uint clipWidth, uint clipHeight) =>
        GlyphImage(fontId, charCode, imageBuffer, true, clipX, clipY, (int)clipWidth, (int)clipHeight, true);

    /// <summary>ScePvfKerningInfo (0x10): 26.6 x/y offsets then the same as floats. Only the 'kern' table is read and the
    /// value is rounded to whole pixels (FT_Get_Kerning, FT_KERNING_DEFAULT); vertical kerning is always 0.</summary>
    [Hle]
    int scePvfGetKerningInfo(uint fontId, ushort leftCharCode, ushort rightCharCode, uint kerningInfo)
    {
        var f = Font(fontId);
        if (f == null || kerningInfo == 0) return ERR_ARG;
        var face = f.Face;
        int units = face.KernUnits(face.GlyphIndex(leftCharCode), face.GlyphIndex(rightCharCode));
        float scale = Style(f).PpemX * 64f / face.UnitsPerEm;
        int x = (int)MathF.Floor((units * scale + 32f) / 64f) * 64;
        GuestMemory.Write(kerningInfo, x);
        GuestMemory.Write(kerningInfo + 4, 0);
        GuestMemory.Write(kerningInfo + 8, x / 64f);
        GuestMemory.Write(kerningInfo + 0xC, 0f);
        return 0;
    }

    /// <summary>ScePvfFontInfo (0x130): max metrics (26.6, then float), numChars, the style (system fonts only).
    /// As in FW 3.74 libpvf, the metrics do not depend on the character size: they are the face's design values
    /// (FreeType's ascender, descender, height = ascender - descender + line gap, max advance width / height; width
    /// and the bearings are left 0) scaled by 64 / (unitsPerEm x EM), i.e. at 1/EM pixels per em (libpaf sets
    /// EM = 1 / its text size before asking), rounded away from zero. The library's default EM is 1000.</summary>
    [Hle]
    int scePvfGetFontInfo(uint fontId, uint fontInfo)
    {
        var f = Font(fontId);
        if (f == null || fontInfo == 0) return ERR_ARG;
        var face = f.Face;
        float scale = 1f / (face.UnitsPerEm * f.Lib.Em);
        int Metric(int units)
        {
            float v = (units << 6) * scale;
            if (float.IsNaN(v)) return 0;
            if (v >= 0) return v >= 2147483648f ? int.MaxValue : (int)MathF.Ceiling(v);
            return v <= -2147483648f ? int.MinValue : (int)MathF.Floor(v);
        }
        int height = face.Ascender - face.Descender + face.LineGap;
        int[] m = [0, Metric(height), Metric(face.Ascender), Metric(face.Descender), 0, 0, 0, 0,
            Metric(face.AdvanceWidthMax), Metric(face.MaxVerticalAdvanceUnits)];
        GuestMemory.Fill(fontInfo, 0, 0x130);
        for (int i = 0; i < m.Length; i++)
        {
            GuestMemory.Write(fontInfo + (uint)i * 4, m[i]);
            GuestMemory.Write(fontInfo + 0x28 + (uint)i * 4, m[i] / 64f);
        }
        GuestMemory.W32(fontInfo + 0x50, (uint)face.CharCount);
        if (f.SystemIndex >= 0) WriteSystemStyle(fontInfo + 0x54, f.SystemIndex);
        return 0;
    }

    // ---- outlines ------------------------------------------------------------------------------------------------

    /// <summary>Fills a caller-provided FT_Outline-shaped struct (0x14 bytes: s16 n_contours, s16 n_points, points*
    /// (s32 x, y in 26.6), tags* (u8: 1 on, 0 conic, 2 cubic), contours* (s16 end indices), s32 flags) whose arrays
    /// are allocated with the library's allocFunc; scePvfReleaseCharGlyphOutline frees them. Prototype inferred from
    /// the disassembly of libpvf (no header).</summary>
    int Outline(uint fontId, ushort charCode, uint outline, bool vertical)
    {
        var f = Font(fontId);
        if (f == null || outline == 0) return ERR_ARG;
        int gi = Glyph(f, charCode, vertical, out int err);
        if (err != 0) return err;
        var g = f.Face.Load(gi, Style(f), vertical, false);
        var lib = f.Lib;
        int n = g.X.Length, nc = g.ContourEnds.Length;
        uint points = n > 0 ? CallGuest(lib.Alloc, lib.UserData, (uint)n * 8) : 0;
        uint tags = n > 0 ? CallGuest(lib.Alloc, lib.UserData, (uint)n) : 0;
        uint contours = nc > 0 ? CallGuest(lib.Alloc, lib.UserData, (uint)nc * 2) : 0;
        if ((n > 0 && (points == 0 || tags == 0)) || (nc > 0 && contours == 0))
        {
            foreach (uint p in new[] { points, tags, contours })
                if (p != 0) CallGuest(lib.Free, lib.UserData, p);
            return ERR_NOMEMORY;
        }
        for (int i = 0; i < n; i++)
        {
            GuestMemory.Write(points + (uint)i * 8, (int)MathF.Round(g.X[i]));
            GuestMemory.Write(points + (uint)i * 8 + 4, (int)MathF.Round(g.Y[i]));
            GuestMemory.Write(tags + (uint)i, g.Tags[i]);
        }
        for (int i = 0; i < nc; i++) GuestMemory.Write(contours + (uint)i * 2, g.ContourEnds[i]);
        GuestMemory.Write(outline, (short)nc);
        GuestMemory.Write(outline + 2, (short)n);
        GuestMemory.W32(outline + 4, points);
        GuestMemory.W32(outline + 8, tags);
        GuestMemory.W32(outline + 0xC, contours);
        GuestMemory.Write(outline + 0x10, g.ReverseFill ? 4 : 0); // FT_OUTLINE_REVERSE_FILL for PostScript outlines
        return 0;
    }

    [Hle] int scePvfGetCharGlyphOutline(uint fontId, ushort charCode, uint outline) => Outline(fontId, charCode, outline, false);
    [Hle] int scePvfGetVertCharGlyphOutline(uint fontId, ushort charCode, uint outline) => Outline(fontId, charCode, outline, true);

    [Hle]
    int scePvfReleaseCharGlyphOutline(uint fontId, uint outline)
    {
        var f = Font(fontId);
        if (f == null || outline == 0) return ERR_ARG;
        foreach (uint off in new uint[] { 4, 8, 0xC })
        {
            uint p = GuestMemory.R32(outline + off);
            if (p != 0) CallGuest(f.Lib.Free, f.Lib.UserData, p);
            GuestMemory.W32(outline + off, 0);
        }
        return 0;
    }

    // ---- point / pixel conversions -------------------------------------------------------------------------------

    float Convert(uint libId, float value, Ptr<int> errorCode, bool horizontal, bool toPoint)
    {
        if (errorCode.IsNull) return 0;
        errorCode.Value = 0;
        var lib = Lib(libId);
        if (lib == null) { errorCode.Value = ERR_LIBID; return 0; }
        float res = horizontal ? lib.HRes : lib.VRes;
        if (toPoint && res * 0.999f < 0) { errorCode.Value = ERR_NOSUPPORT; return 0; }
        float r = toPoint ? value * 72f / res : value * res * (1f / 72f);
        if (float.IsInfinity(r) || float.IsNaN(r)) { errorCode.Value = ERR_NOSUPPORT; return 0; }
        return r;
    }

    [Hle] float scePvfPixelToPointH(uint libId, float pixel, Ptr<int> errorCode) => Convert(libId, pixel, errorCode, true, true);
    [Hle] float scePvfPixelToPointV(uint libId, float pixel, Ptr<int> errorCode) => Convert(libId, pixel, errorCode, false, true);
    [Hle] float scePvfPointToPixelH(uint libId, float point, Ptr<int> errorCode) => Convert(libId, point, errorCode, true, false);
    [Hle] float scePvfPointToPixelV(uint libId, float point, Ptr<int> errorCode) => Convert(libId, point, errorCode, false, false);
}
