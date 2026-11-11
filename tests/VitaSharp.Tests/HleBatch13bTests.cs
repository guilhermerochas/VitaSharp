using System.Reflection;
using System.Text;
using VitaSharp.Core;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using Xunit;

namespace VitaSharp.Tests;

/// <summary>HLE batch 13b: libpaf's type checks and math, SceShellUtil/SceShellSvc, small np_common_ps4/dbutil exports.</summary>
[Collection("guest")]
public sealed class HleBatch13bTests(GuestMemoryFixture fx) : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "vitasharp-hle13b-" + Guid.NewGuid().ToString("N"));
    Emulator? _emu;

    Emulator Emu()
    {
        if (_emu != null)
        {
            return _emu;
        }

        Directory.CreateDirectory(_root);
        return _emu = new Emulator(new EmulatorOptions { FsRoot = _root, Memory = fx.Memory, EnableJit = false, CpuBackend = "interp" });
    }

    T Module<T>() => Emu().Hle.Modules.OfType<T>().Single();

    static object? Invoke(object module, string name, params object?[] args) =>
        module.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!.Invoke(module, args);

    uint Alloc(uint size)
    {
        uint p = fx.Memory.Alloc(size, "hle13b");
        GuestMemory.Fill(p, 0, size);
        return p;
    }

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
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException) { }
    }

    uint TypeName(string s) => (uint)Invoke(Module<Libc>(), "ConstString", s)!;

    [Fact]
    public void IsInheritWalksTheBaseClassesByPointer()
    {
        var paf = Module<Paf>();
        bool Is(string method, uint name) => (bool)Invoke(paf, method, 0u, name)!;
        // dialog -> box -> plane -> widget_base
        Assert.True(Is("IsInheritUiDialog", TypeName("dialog")));
        Assert.True(Is("IsInheritUiDialog", TypeName("box")));
        Assert.True(Is("IsInheritUiDialog", TypeName("plane")));
        Assert.True(Is("IsInheritUiDialog", TypeName("widget_base")));
        Assert.False(Is("IsInheritUiDialog", TypeName("button")));
        Assert.False(Is("IsInheritUiPlane", TypeName("box")));
        // the same text somewhere else is another type (a pointer comparison, as in the firmware)
        Assert.False(Is("IsInheritUiDialog", CStr("dialog")));
        // check_box -> state_button_base -> button_base -> widget_base; the scene's type is "page"
        Assert.True(Is("IsInheritUiCheckBox", TypeName("button_base")));
        Assert.True(Is("IsInheritUiScene", TypeName("page")));
        // draw objects and property elements
        Assert.True(Is("IsInheritGraphPlaneObj", TypeName("DrawObj")));
        Assert.False(Is("IsInheritGraphDrawObj", TypeName("PlaneObj")));
        Assert.True(Is("IsInheritCommonPropertylistStringElement", TypeName("property_element")));
        Assert.False(Is("IsInheritUiFilelistVisualizer", TypeName("widget_base")));
    }

    [Fact]
    public void ShellUtilClientQueuesRequestsAndKeepsTheLocks()
    {
        var s = Module<ShellUtil>();
        Assert.Equal(unchecked((int)0x80101801), Invoke(s, "Lock", 1u));
        Assert.Equal(0, Invoke(s, "InitEvents", 1u)); // no client thread: the program polls
        Assert.Equal(unchecked((int)0x80101802), Invoke(s, "InitEvents", 1u));
        Assert.Equal(0, Invoke(s, "RegisterEventHandler", 0u, 0x1234u));
        Assert.Equal(0, Invoke(s, "Lock", 0x801u));
        Assert.Equal(0, Invoke(s, "Lock", 0x4u));
        Assert.Equal(0x805u, s.LockedBits);
        Assert.Equal(0, Invoke(s, "Unlock", 0x1u));
        Assert.Equal(0, Invoke(s, "Request0B"));
        // four slots in flight
        Assert.Equal(unchecked((int)0x80101803), Invoke(s, "Request05", 0u, 0u));
        for (int i = 0; i < 4; i++)
        {
            Assert.Equal(0, Invoke(s, "Poll"));
        }

        Assert.Equal(0, Invoke(s, "Request0C"));
        Assert.Equal(0x804u, s.LockedBits);
        Invoke(s, "TermEvents");
        Assert.Equal(unchecked((int)0x80101801), Invoke(s, "Poll"));
        Assert.Equal(unchecked((int)0x80101804), Invoke(s, "RequestLaunchApp", 0u));
    }

    [Fact]
    public void ShellUtilClipboardAndEventFlags()
    {
        var s = Module<ShellUtil>();
        uint text = Alloc(0x40);
        foreach (var (c, i) in "hello".Select((c, i) => (c, (uint)i)))
        {
            GuestMemory.Write(text + i * 2, (ushort)c);
        }

        Assert.Equal(0, Invoke(s, "SetClipboard", text, 5u));
        Assert.Equal(5u, Invoke(s, "ClipboardLength"));
        uint buf = Alloc(0x40), len = Alloc(4);
        Assert.Equal(0, Invoke(s, "GetClipboard", buf, 3u, len));
        Assert.Equal(3u, GuestMemory.R32(len));
        Assert.Equal('l', (char)GuestMemory.Read<ushort>(buf + 4));
        Assert.Equal(0, GuestMemory.Read<ushort>(buf + 6));
        Assert.Equal(unchecked((int)0x80101810), Invoke(s, "SetClipboard", text, 0x1FFEu));
        Assert.Equal("hello", s.Clipboard);

        Assert.Equal(0, Invoke(s, "RegisterEvent", 0x35, 0x8000u, 0u));
        Assert.Equal(unchecked((int)0x80100901), Invoke(s, "RegisterEvent", 0x35, 0x8000u, 0u));
        Assert.Equal(unchecked((int)0x80100910), Invoke(s, "RegisterEvent", 0x80, 0x8000u, 0u));
        uint raised = Alloc(4);
        Assert.Equal(0, Invoke(s, "PollState", 0x21, raised));
        Assert.Equal(0, GuestMemory.Read<byte>(raised));
        Assert.Equal(unchecked((int)0x80100904), Invoke(s, "WaitState", 0x21, 0u));
        Invoke(s, "RaiseEvent", 0x21, true);
        Assert.Equal(0, Invoke(s, "WaitState", 0x21, 0u));
        Assert.Equal(0, Invoke(s, "PollState", 0x21, raised));
        Assert.Equal(1, GuestMemory.Read<byte>(raised));
        Assert.Equal(0, Invoke(s, "ClearState", 0x21));
        Assert.Equal(unchecked((int)0x80100970), Invoke(s, "ClearState", 0x80));
    }

    [Fact]
    public void NpCommonPs4AndDbutilMembers()
    {
        var n = Module<NpCommonPs4>();
        uint o = Alloc(0x120);
        GuestMemory.W32(o + 0x10, 5);
        GuestMemory.W32(o + 0x14, 5);
        Assert.Equal(true, Invoke(n, "Ps4_E67CA80C", o));
        GuestMemory.W32(o + 0x14, 6);
        Assert.Equal(false, Invoke(n, "Ps4_E67CA80C", o));
        Assert.Equal(5u, Invoke(n, "Ps4_B85224E9", o));
        Assert.Equal(o + 0x10 - 4, Invoke(n, "Ps4_A486D855", o + 0x10));
        Assert.Equal(0u, Invoke(n, "Ps4_C6A17ECD", o, 0x7Fu));
        Assert.Equal(0x7Fu, Invoke(n, "Ps4_C6BD1713", o));
        GuestMemory.Fill(o + 8, 0xAA, 0x108);
        Invoke(n, "Ps4_EF60709C", o);
        Assert.Equal(0u, GuestMemory.R32(o + 8));
        Assert.Equal(0u, GuestMemory.R32(o + 0x10C));
        var d = Module<Dbutil>();
        GuestMemory.W32(o + 4, 0x55);
        Assert.Equal(0x55u, Invoke(d, "Dbu_7C8652D9", o));
    }

    [Fact]
    public void Psp2CompatKeysAndEnvironment()
    {
        var p = Module<Psp2Compat>();
        uint key = Alloc(4);
        Assert.Equal(0, Invoke(p, "KeyCreate", key, 0u));
        uint k = GuestMemory.R32(key);
        Assert.Equal(0, Invoke(p, "KeyDelete", k));
        Assert.Equal(2, Invoke(p, "KeyDelete", k));
        Assert.Equal(0x16, Invoke(p, "KeyDelete", 0x40u));
        for (int i = 0; i < 0x40; i++)
        {
            Assert.Equal(0, Invoke(p, "KeyCreate", key, 0u));
        }

        Assert.Equal(0xB, Invoke(p, "KeyCreate", key, 0u));

        Assert.Equal(0u, Invoke(p, "Getenv", CStr("HOME")));
        Assert.Equal(0, Invoke(p, "Setenv", CStr("HOME"), CStr("/app0"), 0));
        Assert.Equal("/app0", GuestMemory.ReadCString((uint)Invoke(p, "Getenv", CStr("HOME"))!));
        Assert.Equal(0, Invoke(p, "Setenv", CStr("HOME"), CStr("/ux0"), 1));
        Assert.Equal("/ux0", GuestMemory.ReadCString((uint)Invoke(p, "Getenv", CStr("HOME"))!));
        Assert.Equal(-1, Invoke(p, "Setenv", CStr(new string('n', 0x41)), CStr("v"), 1));
        Assert.Equal(true, Invoke(p, "EndsWithPad", CStr("ux0:/a/b.pad")));
        Assert.Equal(false, Invoke(p, "EndsWithPad", CStr("ux0:/a/b.txt")));
    }

    [Fact]
    public void TextLayoutLayoutAttributes()
    {
        var paf = Module<Paf>();
        uint l = Alloc(0x100), o = Alloc(8);
        Assert.Equal(0, Invoke(paf, "TextLayoutGetBool", l, 9, o));
        Assert.Equal((byte)0, GuestMemory.Read<byte>(o));
        Assert.Equal(unchecked((int)0x80AF2302), Invoke(paf, "TextLayoutGetBool", l, 8, o));
        Assert.Equal(unchecked((int)0x80AF2302), Invoke(paf, "TextLayoutGetFloat", l, 3, o));
        Assert.Equal(unchecked((int)0x80AF2302), Invoke(paf, "TextLayoutGetInt", l, 4, o));
        GuestMemory.Write<byte>(l + 0xD, 0xFE);
        Assert.Equal(0, Invoke(paf, "TextLayoutGetInt", l, 1, o));
        Assert.Equal(unchecked((uint)-2), GuestMemory.R32(o));
        GuestMemory.Write(l + 0x18, 12.5f);
        Assert.Equal(0, Invoke(paf, "TextLayoutGetFloat", l, 0x13, o));
        Assert.Equal(12.5f, GuestMemory.Read<float>(o));
    }
}
