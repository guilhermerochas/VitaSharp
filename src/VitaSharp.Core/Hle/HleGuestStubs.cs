using VitaSharp.Core.Loader;

namespace VitaSharp.Core.Hle;

/// <summary>
/// Guest-callable addresses for HLE objects whose firmware class has a virtual table in the module's own data: each
/// slot is an HLE stub of one of the class's exported methods (guest code calling through the object lands in the
/// same C# method) or of host code for a slot the module keeps internal. One instance per HLE module.
/// </summary>
public sealed class HleGuestStubs(HleModule owner, string prefix)
{
    readonly object _lock = new();
    uint _block, _used;
    readonly Dictionary<string, uint> _vtables = new();
    readonly Dictionary<string, uint> _host = new();
    readonly Dictionary<uint, uint> _exports = new();

    uint NewStub(uint svc, uint nid)
    {
        lock (_lock)
        {
            if (_block == 0 || _used + 16 > 0x1000) { _block = owner.Emu.Memory.Alloc(0x1000, prefix + " stubs", align: 16); _used = 0; }
            uint a = _block + _used;
            _used += 16;
            Linker.WriteHleStub(a, svc, nid);
            owner.Emu.Code.Invalidate(a, 16);
            return a;
        }
    }

    /// <summary>A guest address that calls the HLE version of export <paramref name="nid"/>.</summary>
    public uint Export(uint nid, string library)
    {
        lock (_lock)
        {
            if (_exports.TryGetValue(nid, out uint a)) return a;
            return _exports[nid] = NewStub(owner.Emu.Hle.GetHleFunctionIndex(nid, 0, library), nid);
        }
    }

    /// <summary>A guest address that runs <paramref name="body"/> (one per name).</summary>
    public uint Host(string name, Action<HleContext> body)
    {
        lock (_lock)
        {
            if (_host.TryGetValue(name, out uint a)) return a;
            return _host[name] = NewStub(owner.Emu.Hle.RegisterHostFunction(prefix + ":" + name, body), 0);
        }
    }

    /// <summary>The virtual table <paramref name="name"/> (built once): two words (offset to top, type info) then the
    /// slot addresses; returns the address objects point to (the first slot).</summary>
    public uint VTable(string name, Func<uint[]> slots)
    {
        lock (_lock)
        {
            if (_vtables.TryGetValue(name, out uint vt)) return vt;
            uint[] s = slots();
            uint p = owner.Emu.Memory.Alloc((uint)(s.Length + 2) * 4, prefix + " vtable " + name, align: 8);
            Memory.GuestMemory.W32(p, 0);
            Memory.GuestMemory.W32(p + 4, 0);
            for (int i = 0; i < s.Length; i++) Memory.GuestMemory.W32(p + 8 + (uint)i * 4, s[i]);
            return _vtables[name] = p + 8;
        }
    }
}
