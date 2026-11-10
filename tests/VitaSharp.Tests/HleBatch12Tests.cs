using System.Reflection;
using System.Text;
using VitaSharp.Core;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using Xunit;

namespace VitaSharp.Tests;

/// <summary>HLE batch 12: SceLsdb's AppInfo getters and helpers (and the other small libraries of the batch).</summary>
[Collection("guest")]
public sealed class HleBatch12Tests(GuestMemoryFixture fx) : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "vitasharp-hle12-" + Guid.NewGuid().ToString("N"));
    Emulator? _emu;

    Emulator Emu()
    {
        if (_emu != null) return _emu;
        Directory.CreateDirectory(_root);
        return _emu = new Emulator(new EmulatorOptions { FsRoot = _root, Memory = fx.Memory, EnableJit = false, CpuBackend = "interp" });
    }

    T Module<T>() => Emu().Hle.Modules.OfType<T>().Single();

    static object? Invoke(object module, string name, params object?[] args) =>
        module.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!.Invoke(module, args);

    uint Alloc(uint size) { uint p = fx.Memory.Alloc(size, "hle12"); GuestMemory.Fill(p, 0, size); return p; }

    uint CStr(string s)
    {
        byte[] b = Encoding.UTF8.GetBytes(s + "\0");
        uint p = Alloc((uint)b.Length);
        b.CopyTo(GuestMemory.Span(p, b.Length));
        return p;
    }

    public void Dispose()
    {
        _emu?.Dispose();
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    /// <summary>An AppInfo as the module builds it: { -> { header } }, a binary search tree of nodes { +4 larger,
    /// +8 smaller, +0x10 key, +0x18 type, +0x20 value, +0x29 nil }, the header being the nil end.</summary>
    uint AppInfo(params (uint Key, object Value)[] entries)
    {
        uint head = Alloc(0x30);
        GuestMemory.Write<byte>(head + 0x29, 1);
        uint root = head;
        foreach (var (key, value) in entries.OrderBy(e => e.Key))
        {
            uint n = Alloc(0x30);
            GuestMemory.W32(n + 4, head); GuestMemory.W32(n + 8, head);
            GuestMemory.W32(n + 0x10, key);
            if (value is string s)
            {
                uint holder = Alloc(4);
                GuestMemory.W32(holder, CStr(s));
                GuestMemory.W32(n + 0x18, 1); GuestMemory.W32(n + 0x20, holder);
            }
            else { GuestMemory.W32(n + 0x18, 0); GuestMemory.Write(n + 0x20, Convert.ToUInt64(value)); }
            if (root == head) { root = n; continue; }
            // insert into the tree (larger keys at +4)
            for (uint p = root; ;)
            {
                uint off = GuestMemory.R32(p + 0x10) > key ? 4u : 8u;
                uint next = GuestMemory.R32(p + off);
                if (next == head) { GuestMemory.W32(p + off, n); break; }
                p = next;
            }
        }
        GuestMemory.W32(head, root);
        uint holderOfHead = Alloc(4), info = Alloc(4);
        GuestMemory.W32(holderOfHead, head);
        GuestMemory.W32(info, holderOfHead);
        return info;
    }

    [Fact]
    public void LsdbGettersReadTheAppInfoMap()
    {
        var l = Module<Lsdb>();
        uint info = AppInfo((0xA1541070, "Gravity Rush"), (0x21CA76B1, "gd"), (0x19FAFFCF, (ulong)((1 << 13) | (1 << 16)) | (3ul << 34)),
            (0x924BC88D, (ulong)5), (0xBCD7161E, "01.02"), (0x222641E9, "Default"), (0x87_9D_39_0D, 7ul));
        string S(string fn) => Invoke(l, fn, info) is uint p and not 0 ? GuestMemory.ReadCString(p) : "<null>";
        Assert.Equal("Gravity Rush", S("sceLsdbGetName"));
        Assert.Equal("gd", S("sceLsdbGetCategory"));
        Assert.Equal("01.02", S("sceLsdbGetAppVer"));
        Assert.Equal("<null>", S("sceLsdbGetContentId"));             // missing key
        Assert.Equal("", S("sceLsdbGetGameDataId"));                   // key with an integer, not a string
        Assert.Equal("Default", S("sceLsdbGetTitle"));                 // no title in the system language
        Assert.Equal(7ul, Invoke(l, "sceLsdbGetParentalLevel", info));
        Assert.Equal(0ul, Invoke(l, "sceLsdbGetBootable", info));
        Assert.Equal(1, Invoke(l, "sceLsdbGetLiveAreaSystemVersion", info));
        Assert.Equal(0, Invoke(l, "sceLsdbGetLiveAreaActivityDisabled", info));
        Assert.Equal(1, Invoke(l, "sceLsdbGetLiveAreaPreviewDisabled", info));
        Assert.Equal(3ul, Invoke(l, "sceLsdbGetExtendedMemoryMode", info));
        Assert.Equal(true, Invoke(l, "sceLsdbIsGameOrEgCategory", info));
        Assert.Equal(false, Invoke(l, "sceLsdbIsCategoryGda", info));
        Assert.Equal(true, Invoke(l, "sceLsdbIsGameCategoryOrLiveAreaSystemVersion1Or2", info));
        Assert.Equal(0x11F082E9u, Invoke(l, "sceLsdbGetStitleKeyForLanguage", 0));
        Assert.Equal(0xEED70EC4u, Invoke(l, "sceLsdbGetStitleKeyForLanguage", 25));
    }

    [Fact]
    public void LsdbHelpers()
    {
        var l = Module<Lsdb>();
        uint str = Alloc(8);
        GuestMemory.W32(str, CStr("03.65")); GuestMemory.W32(str + 4, 5);
        Assert.Equal(365, Invoke(l, "ParseVersion", str));
        GuestMemory.W32(str, CStr("3.650"));
        Assert.Equal(-1, Invoke(l, "ParseVersion", str));
        uint range = Alloc(16);
        GuestMemory.Write(range, 0ul); GuestMemory.Write(range + 8, ulong.MaxValue);
        Assert.Equal(true, Invoke(l, "sceLsdbIsCurrentTimeInRange", range, false, false));
        GuestMemory.Write(range, 1ul); GuestMemory.Write(range + 8, 2ul);
        Assert.Equal(false, Invoke(l, "sceLsdbIsCurrentTimeInRange", range, false, false));
        Assert.Equal(true, Invoke(l, "sceLsdbNormalizeBool", 5));
        uint frame = Alloc(0x40), items = Alloc(8);
        GuestMemory.W32(items, 0x1111); GuestMemory.W32(items + 4, 0x2222);
        GuestMemory.W32(frame + 0x38, items); GuestMemory.W32(frame + 0x3C, 2);
        Assert.Equal(0x2222u, Invoke(l, "sceLsdbLiveAreaFrameGetItem", frame, 1));
        Assert.Equal(0u, Invoke(l, "sceLsdbLiveAreaFrameGetItem", frame, 2));
    }

    [Fact]
    public void LibcRandomHelpers()
    {
        // _XLgamma is the Lanczos log-gamma: Γ(5) = 24, Γ(0.5) = √π
        Assert.Equal(Math.Log(24), Libc.XLgamma(5), 9);
        Assert.Equal(Math.Log(Math.Sqrt(Math.PI)), Libc.XLgamma(0.5), 9);
        var c = Module<Libc>();
        uint mp = Alloc(5 * 8);
        Invoke(c, "_ZSt7_MP_MulPyyy", mp, 0xFFFFFFFFFFFFFFFFul, 0x10ul);            // 2^68 - 16
        Assert.Equal(0xFFFFFFF0ul, GuestMemory.Read<ulong>(mp));
        Assert.Equal(0xFFFFFFFFul, GuestMemory.Read<ulong>(mp + 8));
        Assert.Equal(0xFul, GuestMemory.Read<ulong>(mp + 16));
        Invoke(c, "_ZSt7_MP_AddPyy", mp, 0x10ul);                                 // 2^68
        Assert.Equal(0x10ul, GuestMemory.Read<ulong>(mp + 16));
        Invoke(c, "_ZSt7_MP_RemPyy", mp, 1000000007ul);
        Assert.Equal((ulong)(System.Numerics.BigInteger.Pow(2, 68) % 1000000007), Invoke(c, "_ZSt7_MP_GetPy", mp));
        Assert.Equal("broken promise", GuestMemory.ReadCString((uint)Invoke(c, "_ZSt17_Future_error_mapi", 1)!));
        Assert.Equal(0u, Invoke(c, "_ZSt22_Get_future_error_whati", 9));
        Assert.Equal(0, Invoke(c, "_ZSt22_Random_device_entropyv"));
    }

    [Fact]
    public void PromoterWithoutTheShellIsUnavailable()
    {
        var f = Module<SystemFallback>();
        Assert.Equal(unchecked((int)0x80AF008F), Invoke(f, "PromoterDeletePkg", 0u));
        Assert.Equal(unchecked((int)0x80AF008F), Invoke(f, "PromoterGetSize", CStr("PCSA00001"), 0u));
        Assert.Equal(unchecked((int)0x80AF008F), Invoke(f, "PromoterUpdateLiveArea", 0u));
    }

    [Fact]
    public void PamgrTimebaseAndNoTraceHardware()
    {
        var d = Module<DebugPerf>();
        Assert.Equal(1_000_000u, Invoke(d, "sceKernelPaGetTimebaseFrequency"));
        Assert.NotEqual(0u, Invoke(d, "sceKernelPaGetIoBaseAddress"));
        Assert.Equal(unchecked((int)0x80010002), Invoke(d, "sceDTraceClientOpen", CStr("/dev/dtrace/dtrace"), 0));
        Assert.Equal(1u, Invoke(d, "sceKernelGetPMUSERENR"));
    }

    [Fact]
    public void EmailEngineAccessorsReadAndWriteTheirMember()
    {
        var e = Module<EmailEngine>();
        uint o = Alloc(0x100);
        for (uint off = 0; off < 0x100; off += 4) GuestMemory.W32(o + off, 0x1000 + off);
        Assert.Equal(0x100Cu, Invoke(e, "Get0C", o));
        Assert.Equal(0x1080u, Invoke(e, "Get80", o));
        Invoke(e, "Set48", o, 0xABCDu);
        Assert.Equal(0xABCDu, Invoke(e, "Get48", o));
        Assert.Null(Invoke(e, "Release", 0u)); // NULL: nothing to delete
    }

    [Fact]
    public void Psp2CompatAccessorsAndConstants()
    {
        var p = Module<Psp2Compat>();
        uint o = Alloc(0x400);
        for (uint off = 0; off < 0x400; off += 4) GuestMemory.W32(o + off, 0x2000 + off);
        Assert.Equal(0x2004u, Invoke(p, "Get04", o));
        Assert.Equal(0x2320u, Invoke(p, "Get320", o));
        Assert.Equal(0x0000202400002020ul, Invoke(p, "Get20Wide", o));
        Invoke(p, "Flag2C", o);
        Assert.Equal((byte)1, GuestMemory.Read<byte>(o + 0x2C));
        Assert.Equal(0x44700000u, Invoke(p, "Width"));
        Assert.Equal(true, Invoke(p, "Equal", 5u, 5u));
        uint name = Alloc(0x20);
        Invoke(p, "PoolName", 0x1Fu, name);
        Assert.Equal("SceManxMemoryPool1f", GuestMemory.ReadCString(name));
    }
}
