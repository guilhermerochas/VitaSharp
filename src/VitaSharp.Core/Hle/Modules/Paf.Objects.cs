using VitaSharp.Core.Loader;
using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// Guest-side plumbing for the libpaf objects the HLE creates: virtual tables whose slots are HLE stubs of the
/// class's exported methods (so guest code calling through a File* lands in the same C# methods), host stubs for the
/// slots libpaf does not export, and common::SharedPtr (an 8-byte { T *ptr; RefCounter *rc } pair whose counter block
/// is { ptr, count, weak count, deleter }).
/// </summary>
public sealed partial class Paf
{
    readonly object _stubLock = new();
    uint _stubBlock, _stubUsed;
    readonly Dictionary<string, uint> _vtables = new();
    readonly Dictionary<string, uint> _hostStubs = new();

    uint NewStub(uint svc, uint nid)
    {
        lock (_stubLock)
        {
            if (_stubBlock == 0 || _stubUsed + 16 > 0x1000) { _stubBlock = C.PafStatic(0x1000, 16); _stubUsed = 0; }
            uint a = _stubBlock + _stubUsed;
            _stubUsed += 16;
            Linker.WriteHleStub(a, svc, nid);
            Emu.Code.Invalidate(a, 16);
            return a;
        }
    }

    /// <summary>A guest-callable address of one of libpaf's exports the HLE implements.</summary>
    uint ExportStub(uint nid) => NewStub(Emu.Hle.GetHleFunctionIndex(nid, 0, NidDatabase.Lookup(nid)?.Library ?? "ScePaf"), nid);

    /// <summary>A guest-callable address of host code (a slot libpaf keeps internal).</summary>
    uint HostStub(string name, Action<HleContext> body)
    {
        lock (_stubLock)
        {
            if (_hostStubs.TryGetValue(name, out uint a)) return a;
            return _hostStubs[name] = NewStub(Emu.Hle.RegisterHostFunction("ScePaf:" + name, body), 0);
        }
    }

    /// <summary>The virtual table <paramref name="name"/> (built once): offset-to-top and type words, then one slot per
    /// entry, an export NID or a host stub address (given as a negative-free marker through <see cref="Slot"/>).</summary>
    uint VTable(string name, params Slot[] slots)
    {
        lock (_stubLock)
        {
            if (_vtables.TryGetValue(name, out uint vt)) return vt;
            uint p = C.PafStatic((uint)(slots.Length + 2) * 4, 8);
            U(p, 0); U(p + 4, 0);
            for (int i = 0; i < slots.Length; i++) U(p + 8 + (uint)i * 4, slots[i].Resolve(this));
            return _vtables[name] = p + 8;
        }
    }

    readonly record struct Slot(uint Nid, string? HostName, Action<HleContext>? Host)
    {
        public static implicit operator Slot(uint nid) => new(nid, null, null);
        public uint Resolve(Paf p) => Host != null ? p.HostStub(HostName!, Host) : p.ExportStub(Nid);
    }

    static Slot HostSlot(string name, Action<HleContext> body) => new(0, name, body);

    // ---- common::SharedPtr
    /// <summary>Fills a SharedPtr at <paramref name="sp"/> owning <paramref name="obj"/> (count 1); the deleter runs
    /// the object's deleting destructor (virtual slot <paramref name="d0Slot"/>) when the last reference goes.</summary>
    void SharedPtrNew(uint sp, uint obj, int d0Slot)
    {
        if (obj == 0) { U(sp, 0); U(sp + 4, 0); return; }
        uint rc = Alloc(16);
        U(rc, obj); U(rc + 4, 1); U(rc + 8, 1);
        U(rc + 0xC, HostStub("SharedPtr.delete." + d0Slot, c =>
        {
            uint o = c.Gpr(0);
            if (o != 0) c.Thread.CallGuest(U(U(o) + (uint)d0Slot * 4), o);
        }));
        U(sp, obj); U(sp + 4, rc);
    }

    /// <summary>Drops one reference of a SharedPtr (the deleter and the counter block go with the last one).</summary>
    void SharedPtrRelease(uint sp)
    {
        uint obj = U(sp), rc = U(sp + 4);
        U(sp, 0); U(sp + 4, 0);
        if (obj == 0 || rc == 0) return;
        uint n = U(rc + 4) - 1;
        U(rc + 4, n);
        if (n != 0) return;
        if (Kernel.GuestThread.Current is { } t) t.CallGuest(U(rc + 0xC), U(rc));
        uint w = U(rc + 8) - 1;
        U(rc + 8, w);
        if (w == 0) Free(rc);
    }
}
