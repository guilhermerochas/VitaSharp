using System.Text;
using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// The C++ classes of libpaf.suprx (FW 3.74), the system applications' framework, written from the firmware's own
/// behaviour: the object layouts and each method's rules were read from the disassembly of the module, the names of the
/// exports come from the community's ScePaf-RE NID list (DWARF names of prototype test programs; NID = SHA-1 of the
/// mangled name + "PAF"). Only the classes that stand on their own are here: math (rect), date and time, strings and
/// their encodings, paths, files and directories, threads and their locks, timers, hashes and decompression. A system
/// program loads libpaf itself (sceSysmoduleLoadModuleInternalWithArg), so these serve a process that does not, and
/// never take the library over from the firmware.
/// </summary>
public sealed partial class Paf : HleModule
{
    public override bool FirmwareFallbackOnly => true;

    const uint NidEmptyString = 0x610F2F91, NidEmptyWString = 0xB4A712A7;
    uint _emptyString, _emptyWString;
    Libc? _libc;
    Rtc? _rtc;
    Libc C => _libc ??= Emu.Hle.Modules.OfType<Libc>().First();
    Rtc R => _rtc ??= Emu.Hle.Modules.OfType<Rtc>().First();

    public override void Initialize()
    {
        Emu.Hle.RegisterVariable(NidEmptyString, () => EmptyString);
        Emu.Hle.RegisterVariable(NidEmptyWString, () => EmptyWString);
    }

    static float F(uint a) => GuestMemory.Read<float>(a);
    static void F(uint a, float v) => GuestMemory.Write(a, v);
    static uint U(uint a) => GuestMemory.R32(a);
    static void U(uint a, uint v) => GuestMemory.W32(a, v);
    static ushort H(uint a) => GuestMemory.Read<ushort>(a);
    static void H(uint a, ushort v) => GuestMemory.Write(a, v);
    static byte B(uint a) => GuestMemory.Read<byte>(a);
    static void B(uint a, byte v) => GuestMemory.Write(a, v);
    static ulong Q(uint a) => GuestMemory.Read<ulong>(a);
    static void Q(uint a, ulong v) => GuestMemory.Write(a, v);

    /// <summary>libpaf's size rule: a negative (or NaN) width or height is stored as 0.</summary>
    static float NonNeg(float v) => v >= 0 ? v : 0f;

    uint Alloc(uint size) => C.PafHeapAlloc(size);
    void Free(uint p) { if (p != 0) C.PafHeapFree(p); }
    uint Static(uint size) { uint p = C.PafStatic(size, 8); GuestMemory.Fill(p, 0, size); return p; }

    // ---- paf::string / paf::wstring: { T *ptr; size_t count; allocator (empty, padded) }, 12 bytes; an empty string
    // points at the class's static empty_string, any other buffer comes from the heap and is count + 1 elements long.
    internal uint EmptyString => _emptyString != 0 ? _emptyString : _emptyString = Static(4);
    uint EmptyWString => _emptyWString != 0 ? _emptyWString : _emptyWString = Static(4);

    /// <summary>An empty string object at <paramref name="s"/> (the constructor).</summary>
    void StrInit(uint s, bool wide = false) { U(s, wide ? EmptyWString : EmptyString); U(s + 4, 0); U(s + 8, 0); }

    string StrRead(uint s) => U(s) == 0 ? "" : Encoding.UTF8.GetString(GuestMemory.ReadSpan(U(s), (int)U(s + 4)));
    string WStrRead(uint s)
    {
        uint p = U(s), n = U(s + 4);
        if (p == 0) return "";
        var c = new char[n];
        for (uint i = 0; i < n; i++) c[i] = (char)H(p + i * 2);
        return new string(c);
    }

    void StrRelease(uint s)
    {
        uint p = U(s);
        if (p != 0 && p != _emptyString && p != _emptyWString) Free(p);
    }

    /// <summary>Replaces the string's text (its old buffer is freed).</summary>
    void StrAssign(uint s, ReadOnlySpan<byte> bytes)
    {
        StrRelease(s);
        if (bytes.Length == 0) { StrInit(s); return; }
        uint p = Alloc((uint)bytes.Length + 1);
        bytes.CopyTo(GuestMemory.Span(p, bytes.Length));
        B(p + (uint)bytes.Length, 0);
        U(s, p); U(s + 4, (uint)bytes.Length);
    }

    void StrAssign(uint s, string text) => StrAssign(s, Encoding.UTF8.GetBytes(text));

    void WStrAssign(uint s, string text)
    {
        StrRelease(s);
        if (text.Length == 0) { StrInit(s, true); return; }
        uint p = Alloc((uint)(text.Length + 1) * 2);
        for (int i = 0; i < text.Length; i++) H(p + (uint)i * 2, text[i]);
        H(p + (uint)text.Length * 2, 0);
        U(s, p); U(s + 4, (uint)text.Length);
    }

    /// <summary>A NUL-terminated 16-bit string.</summary>
    static string WRead(uint p, int max = int.MaxValue)
    {
        if (p == 0) return "";
        var sb = new StringBuilder();
        for (uint i = 0; sb.Length < max; i++) { ushort c = H(p + i * 2); if (c == 0) break; sb.Append((char)c); }
        return sb.ToString();
    }

    static string CRead(uint p) => p == 0 ? "" : Encoding.UTF8.GetString(GuestMemory.ReadSpan(p, (int)StrLen(p)));

    static uint StrLen(uint p) { uint n = 0; while (B(p + n) != 0) n++; return n; }
}
