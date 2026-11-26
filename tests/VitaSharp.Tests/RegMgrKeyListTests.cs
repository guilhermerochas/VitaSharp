using VitaSharp.Core.Firmware;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// sceRegMgrSetKeys and sceRegMgrGetKeys (SceRegMgr, FW 3.74 SceRegistryMgr): a list of 0x10-byte entries
/// { +0 0x10, +4 name, +8 u16 type, +0xA u16 size, +0xC the integer, or a pointer to the bytes of a string or binary
/// key }. The shell's libmtp keeps the hosts it knows this way (/CONFIG/MTP/NET/00: host_id, host_name,
/// last_connected_time). Every value used to be taken as the integer at +0xC: those keys were saved with the guest
/// address of their bytes (a registry with "host_name = 40 85 27 83 00..."), and reading them back overwrote the
/// caller's pointer with 0.
/// </summary>
[Collection("guest")]
public sealed class RegMgrKeyListTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int Invalid = unchecked((int)0x800D0004);
    private const int BadEntrySize = unchecked((int)0x800D001C);
    private const string Category = "/CONFIG/MTP/NET/00/";

    private readonly HleHarness _harness = new(fixture, "regmgr-keys");

    public void Dispose() => _harness.Dispose();

    private Registry DefineHostKeys()
    {
        Registry registry = Registry.For(_harness.Root);
        registry.Define(Category + "host_id", Registry.KeyType.Bin, 16);
        registry.Define(Category + "host_name", Registry.KeyType.Str, 0x81);
        registry.Define(Category + "host_type", Registry.KeyType.Int, 4);
        return registry;
    }

    private void Entry(uint list, int index, string name, Registry.KeyType type, int size, uint value)
    {
        uint entry = list + (uint)(index * 0x10);
        GuestMemory.W32(entry, 0x10);
        GuestMemory.W32(entry + 4, _harness.Str(name));
        GuestMemory.Write(entry + 8, (ushort)type);
        GuestMemory.Write(entry + 0xA, (ushort)size);
        GuestMemory.W32(entry + 0xC, value);
    }

    [Fact]
    public void SetKeysStoresTheBytesAStringOrBinaryEntryPointsAt()
    {
        Registry registry = DefineHostKeys();
        RegMgrSystem regmgr = _harness.Module<RegMgrSystem>();
        uint list = _harness.Alloc(3 * 0x10);
        uint name = _harness.Str("MY-PC");
        uint id = _harness.Alloc(16);
        for (uint i = 0; i < 16; i++)
        {
            GuestMemory.Write(id + i, (byte)(0xA0 + i));
        }

        Entry(list, 0, "host_id", Registry.KeyType.Bin, 16, id);
        Entry(list, 1, "host_name", Registry.KeyType.Str, 6, name);
        Entry(list, 2, "host_type", Registry.KeyType.Int, 4, 2);

        Assert.Equal(0, Call(regmgr, "sceRegMgrSetKeys", _harness.Str(Category), list, 3));

        Assert.Equal(GuestMemory.Span(id, 16).ToArray(), registry.Get(Category, "host_id"));
        Assert.Equal("MY-PC", System.Text.Encoding.ASCII.GetString(registry.Get(Category, "host_name")!).TrimEnd('\0'));
        Assert.True(registry.TryGetInt(Category, "host_type", out int type));
        Assert.Equal(2, type);
    }

    [Fact]
    public void GetKeysFillsTheCallersBuffersAndKeepsItsPointers()
    {
        Registry registry = DefineHostKeys();
        Assert.True(registry.Set(Category, "host_name", "MY-PC\0"u8));
        Assert.True(registry.SetInt(Category, "host_type", 3));
        RegMgrSystem regmgr = _harness.Module<RegMgrSystem>();
        uint list = _harness.Alloc(2 * 0x10);
        uint buffer = _harness.Alloc(0x81);
        Entry(list, 0, "host_name", Registry.KeyType.Str, 0x81, buffer);
        Entry(list, 1, "host_type", Registry.KeyType.Int, 4, 0xFFFFFFFF);

        Assert.Equal(0, Call(regmgr, "sceRegMgrGetKeys", _harness.Str(Category), list, 2));

        Assert.Equal("MY-PC", GuestMemory.ReadCString(buffer, 0x81));
        Assert.Equal(buffer, GuestMemory.R32(list + 0xC));
        Assert.Equal(3u, GuestMemory.R32(list + 0x10 + 0xC));
    }

    [Fact]
    public void AListIsCheckedWholeBeforeAnythingIsWritten()
    {
        Registry registry = DefineHostKeys();
        RegMgrSystem regmgr = _harness.Module<RegMgrSystem>();
        uint category = _harness.Str(Category);
        uint list = _harness.Alloc(2 * 0x10);
        Entry(list, 0, "host_type", Registry.KeyType.Int, 4, 7);
        Entry(list, 1, "host_name", Registry.KeyType.Str, 6, 0);

        // a string entry without its bytes: nothing is written, not even the integer before it
        Assert.Equal(Invalid, Call(regmgr, "sceRegMgrSetKeys", category, list, 2));
        Assert.False(registry.TryGetWrittenInt(Category + "host_type", out _));

        // an entry that does not state its size, an unknown type, no name, and counts outside 1..32
        GuestMemory.W32(list + 0x10, 4);
        Assert.Equal(BadEntrySize, Call(regmgr, "sceRegMgrSetKeys", category, list, 2));
        Entry(list, 1, "host_name", (Registry.KeyType)3, 4, 0);
        Assert.Equal(Invalid, Call(regmgr, "sceRegMgrSetKeys", category, list, 2));
        GuestMemory.W32(list + 4, 0);
        Assert.Equal(Invalid, Call(regmgr, "sceRegMgrGetKeys", category, list, 1));
        Assert.Equal(Invalid, Call(regmgr, "sceRegMgrSetKeys", category, list, 0));
        Assert.Equal(Invalid, Call(regmgr, "sceRegMgrSetKeys", category, list, 33));
        Assert.Equal(Invalid, Call(regmgr, "sceRegMgrSetKeys", 0u, list, 1));
    }
}
