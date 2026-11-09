using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

// np_manager.suprx's own libipmi connection to the shell's NP service ("SceNpManager"), FW 3.74, NIDs without public
// names, read in the module. The library opens it at initialisation (sceNpInit / NID_C89E0B94) with libshellsvc's
// IPMI::Client and makes its auth/ticket requests through it as async requests: a request id from the client's
// request table (types 1, 2, 4), then for each call: take the id (its event handle), send method M with {ptr, size}
// inputs (vtable +0x1C), wait on the handle, receive method M + 1 with {ptr, size} outputs (vtable +0x24), give the id
// back. The HLE library makes the same calls on the same client code (the firmware's libshellsvc) and the shell
// serves them; it opens the connection the first time a request needs it, so a process that never makes one does not
// connect. Without libshellsvc (an application without the system software) there is no connection: the requests
// fail like the library's "not initialised" (0x80550502). The connection's shell event (0x33, the service restarting)
// is not registered: the HLE's NID_351A74C2 keeps no handlers to tell.

public sealed partial class NpManager
{
    const uint NidIpmiEstimate = 0x296D44D4, NidIpmiCreate = 0xBC3A3031, NidIpmiConnect = 0x2C6DB642,
        NidIpmiDisconnect = 0x3554D4B0, NidIpmiDestroy = 0xA20BB95B, NidIpmiInner = 0x1372C7B7,
        NidIpmiNewRequest = 0x367CEF82, NidIpmiTake = 0x7C002FEC, NidIpmiWait = 0x7E079E19, NidIpmiAbort = 0x48E072C8,
        NidIpmiDelete = 0x5ECD40E9, NidIpmiGive = 0x8A0BAC4E;
    const int IPMI_ERR_BUSY = unchecked((int)0x80101720), IPMI_ERR_NO_REQUEST = unchecked((int)0x80101721),
        IPMI_ERR_DEAD = unchecked((int)0x80101722), IPMI_ERR_CANCELED = unchecked((int)0x80101760);
    const int MGR_IPMI_ERROR = unchecked((int)0x80550581), MGR_IPMI_BUSY = unchecked((int)0x80550582),
        MGR_IPMI_NO_REQUEST = unchecked((int)0x80550583), MGR_IPMI_ABORTED = unchecked((int)0x80550585),
        MGR_REQUEST_DEAD = unchecked((int)0x80550507), MGR_NO_MEMORY = unchecked((int)0x80550504);

    readonly object _ipmiLock = new();
    uint _ipmi, _ipmiMem, _ipmiCfg;
    bool _ipmiTried;

    /// <summary>The library's IPMI::Client, opened on first use (0: none).</summary>
    uint IpmiClient()
    {
        lock (_ipmiLock)
        {
            if (_ipmi != 0 || _ipmiTried) return _ipmi;
            lock (_lock) if (!_npInited && !_sysInited) return 0;
            _ipmiTried = true;
        }
        if (GuestThread.Current is not { } t) return 0;
        foreach (uint n in new[] { NidIpmiEstimate, NidIpmiCreate, NidIpmiConnect, NidIpmiInner, NidIpmiNewRequest, NidIpmiTake,
            NidIpmiWait, NidIpmiAbort, NidIpmiDelete, NidIpmiGive })
            if (!Emu.Linker.TryGetFunctionExport(n, out _)) return 0;
        var mem = (Emu.Memory ?? GuestMemory.Instance)!;
        // IPMI::Client::Config as np_manager fills it (0x154 bytes): name, then the client's buffer and request sizes
        uint cfg = _ipmiCfg != 0 ? _ipmiCfg : _ipmiCfg = mem.Alloc(0x200, "SceNpManagerIpc config");
        GuestMemory.Fill(cfg, 0, 0x154);
        GuestMemory.WriteCString(cfg, "SceNpManager");
        uint[] head = [0x10, 1, 0xF00, 0xF00, 1, 0x400, 0x400, 4, 0x400, 0x3000, 0, 1];
        for (int i = 0; i < head.Length; i++) GuestMemory.W32(cfg + 0x1C + (uint)i * 4, head[i]);
        for (uint k = 0; k < 32; k++) { GuestMemory.W32(cfg + 0x4C + k * 8, 0x100); GuestMemory.W32(cfg + 0x50 + k * 8, 8); }
        GuestMemory.W32(cfg + 0x150, 2);
        uint size = Guest(t, NidIpmiEstimate, cfg);
        if ((int)size <= 0) return 0;
        uint work = mem.Alloc(size, "SceNpManagerIpc client");
        uint cell = Scratch(t, 8);
        GuestMemory.W32(cell, 0);
        int r = (int)Guest(t, NidIpmiCreate, cell, cfg, work);
        uint client = GuestMemory.R32(cell);
        if (r != 0 || client == 0) { mem.Free(work); return 0; }
        r = (int)Guest(t, NidIpmiConnect, client);
        if (r != 0)
        {
            Guest(t, NidIpmiDestroy, client);
            mem.Free(work);
            return 0;
        }
        lock (_ipmiLock) { _ipmi = client; _ipmiMem = work; }
        return client;
    }

    /// <summary>Closes the connection (sceNpTerm / the system-side term): disconnect, destroy, free.</summary>
    void IpmiClose()
    {
        uint client, work;
        lock (_ipmiLock) { client = _ipmi; work = _ipmiMem; _ipmi = _ipmiMem = 0; _ipmiTried = false; }
        if (client == 0 || GuestThread.Current is not { } t) return;
        Guest(t, NidIpmiDisconnect, client);
        Guest(t, NidIpmiDestroy, client);
        (Emu.Memory ?? GuestMemory.Instance)!.Free(work);
    }

    uint Guest(GuestThread t, uint nid, params ReadOnlySpan<uint> args)
    {
        Emu.Linker.TryGetFunctionExport(nid, out uint fn);
        return t.CallGuestOnStack(fn, _scratchTop != 0 ? _scratchTop : 0, args);
    }

    [ThreadStatic] static uint _scratchTop;

    /// <summary>A block on the calling thread's stack below its frame (valid during this HLE call); later guest calls
    /// run below it.</summary>
    static uint Scratch(GuestThread t, uint size)
    {
        uint top = _scratchTop != 0 ? _scratchTop : StackPointer(t) - 0x40;
        uint p = (top - size) & ~7u;
        _scratchTop = p;
        return p;
    }

    static unsafe uint StackPointer(GuestThread t) => t.Core.State->R[13];

    /// <summary>One request buffer: guest memory or words copied to the stack.</summary>
    readonly record struct Buf(uint Ptr, uint Size, uint[]? Words = null)
    {
        public static Buf Of(params uint[] words) => new(0, (uint)words.Length * 4, words);
    }

    uint Descs(GuestThread t, Buf[] bufs)
    {
        if (bufs.Length == 0) return 0;
        uint d = Scratch(t, (uint)bufs.Length * 8);
        for (int i = 0; i < bufs.Length; i++)
        {
            uint p = bufs[i].Ptr;
            if (bufs[i].Words is { } w)
            {
                p = Scratch(t, (uint)w.Length * 4 + 4);
                for (int j = 0; j < w.Length; j++) GuestMemory.W32(p + (uint)j * 4, w[j]);
            }
            GuestMemory.W32(d + (uint)i * 8, p);
            GuestMemory.W32(d + (uint)i * 8 + 4, bufs[i].Size);
        }
        return d;
    }

    static int MapTake(int r) => r switch
    {
        0 => 0,
        IPMI_ERR_BUSY => MGR_IPMI_BUSY,
        IPMI_ERR_NO_REQUEST => MGR_IPMI_NO_REQUEST,
        IPMI_ERR_DEAD => MGR_REQUEST_DEAD,
        _ => MGR_IPMI_ERROR,
    };

    /// <summary>Runs <paramref name="body"/> with a fresh stack scratch area (restored afterwards: HLE calls nest).</summary>
    T WithScratch<T>(Func<GuestThread, T> body, T none)
    {
        if (GuestThread.Current is not { } t) return none;
        uint saved = _scratchTop;
        _scratchTop = 0;
        try { return body(t); }
        finally { _scratchTop = saved; }
    }

    /// <summary>One async request on request <paramref name="reqId"/>: send <paramref name="method"/> with the inputs,
    /// wait, receive method + 1 into the outputs (built after the wait, from the values <paramref name="outs"/> then
    /// sees), give the id back. Returns the library's result: its own errors, the server's answer, or the give-back's
    /// error (unless <paramref name="ignoreGive"/>).</summary>
    int Request(uint reqId, uint method, Buf[] ins, Func<GuestThread, Buf[]> outs, bool ignoreGive = false,
        Action<GuestThread>? afterGive = null) => WithScratch(t =>
    {
        uint c = IpmiClient();
        if (c == 0) return MGR_NOT_INITIALIZED;
        uint handle = Scratch(t, 8), asyncId = Scratch(t, 4), result = Scratch(t, 4);
        GuestMemory.W32(asyncId, 0);
        int r = MapTake((int)Guest(t, NidIpmiTake, c, reqId, handle));
        if (r < 0) return r;
        uint inner = Guest(t, NidIpmiInner, c);
        uint inDesc = Descs(t, ins);
        r = (int)t.CallGuestOnStack(GuestMemory.R32(GuestMemory.R32(inner) + 0x1C), _scratchTop, inner, method, inDesc, (uint)ins.Length, asyncId, handle);
        if (r >= 0)
        {
            int w = (int)Guest(t, NidIpmiWait, c, handle, 0);
            if (w == IPMI_ERR_CANCELED) { Guest(t, NidIpmiAbort, c, reqId); r = MGR_IPMI_ABORTED; }
            else r = w < 0 ? w : 0;
            if (r >= 0)
            {
                var o = outs(t);
                uint outDesc = Descs(t, o);
                GuestMemory.W32(result, 0);
                int e = (int)t.CallGuestOnStack(GuestMemory.R32(GuestMemory.R32(inner) + 0x24), _scratchTop, inner, method + 1,
                    GuestMemory.R32(asyncId), result, outDesc, (uint)o.Length);
                r = e != 0 ? e : (int)GuestMemory.R32(result);
            }
        }
        int g = (int)Guest(t, NidIpmiGive, c, reqId);
        if (g < 0 && !ignoreGive) return g;
        afterGive?.Invoke(t);
        return r;
    }, MGR_NOT_INITIALIZED);

    /// <summary>A synchronous call on the connection (vtable +0x14), no inputs.</summary>
    int Sync(uint method, Buf[] outs) => WithScratch(t =>
    {
        uint c = IpmiClient();
        if (c == 0) return MGR_NOT_INITIALIZED;
        uint inner = Guest(t, NidIpmiInner, c), result = Scratch(t, 4);
        GuestMemory.W32(result, 0);
        uint outDesc = Descs(t, outs);
        int e = (int)t.CallGuestOnStack(GuestMemory.R32(GuestMemory.R32(inner) + 0x14), _scratchTop, inner, method, 0, 0, result, outDesc, (uint)outs.Length);
        return e != 0 ? e : (int)GuestMemory.R32(result);
    }, MGR_NOT_INITIALIZED);

    int NewRequest(uint type, uint outp, bool returnId) => WithScratch(t =>
    {
        uint c = IpmiClient();
        if (c == 0) return MGR_NOT_INITIALIZED;
        uint id = Scratch(t, 4), arg = Scratch(t, 4);
        GuestMemory.W32(arg, type);
        int r = (int)Guest(t, NidIpmiNewRequest, c, id, arg, 4);
        if (r < 0) return r;
        if (outp != 0) GuestMemory.W32(outp, GuestMemory.R32(id));
        return returnId ? (int)GuestMemory.R32(id) : r;
    }, MGR_NOT_INITIALIZED);

    int DeleteRequest(uint reqId) => WithScratch(t =>
    {
        uint c = IpmiClient();
        if (c == 0) return MGR_NOT_INITIALIZED;
        int r = (int)Guest(t, NidIpmiDelete, c, reqId);
        if (r >= 0) return r;
        return r == IPMI_ERR_BUSY ? MGR_IPMI_BUSY : r == IPMI_ERR_NO_REQUEST ? MGR_IPMI_NO_REQUEST : MGR_IPMI_ERROR;
    }, MGR_NOT_INITIALIZED);

    int AbortRequest(uint reqId) => WithScratch(t =>
    {
        uint c = IpmiClient();
        if (c == 0) return MGR_NOT_INITIALIZED;
        int r = (int)Guest(t, NidIpmiAbort, c, reqId);
        if (r >= 0) return r;
        return r == IPMI_ERR_BUSY ? MGR_IPMI_BUSY : MGR_IPMI_ERROR;
    }, MGR_NOT_INITIALIZED);

    // ---- request ids

    /// <summary>(void): a new request of type 1 (the auth ticket request); its id or the error.</summary>
    [Hle("sceNpManager_8D7981D4", Nid = 0x8D7981D4, Library = "SceNpManager")] int NpIpmiNewRequest1() => NewRequest(1, 0, true);
    /// <summary>(void): a new request of type 2.</summary>
    [Hle("sceNpManager_21F1D18E", Nid = 0x21F1D18E, Library = "SceNpManager")] int NpIpmiNewRequest2() => NewRequest(2, 0, true);
    /// <summary>(int *id): a new request of type 4, its id written out.</summary>
    [Hle("sceNpManager_4110B787", Nid = 0x4110B787, Library = "SceNpManager")] int NpIpmiNewRequest4(uint id) => NewRequest(4, id, false);
    [Hle("sceNpManager_3907F96A", Nid = 0x3907F96A, Library = "SceNpManager")] int NpIpmiDeleteRequestA(uint id) => DeleteRequest(id);
    [Hle("sceNpManager_A3E472B8", Nid = 0xA3E472B8, Library = "SceNpManager")] int NpIpmiDeleteRequestB(uint id) => DeleteRequest(id);
    [Hle("sceNpManager_737B2820", Nid = 0x737B2820, Library = "SceNpManager")] int NpIpmiAbortRequestA(uint id) => AbortRequest(id);
    [Hle("sceNpManager_6EA27D2A", Nid = 0x6EA27D2A, Library = "SceNpManager")] int NpIpmiAbortRequestB(uint id) => AbortRequest(id);

    // ---- requests

    /// <summary>(id, SceNpId *out): 0xB0000/1, the request's NpId (0x24).</summary>
    [Hle("sceNpManager_82DA7CFD", Nid = 0x82DA7CFD, Library = "SceNpManager")]
    int NpIpmiB0000(uint id, uint npId) => Request(id, 0xB0000, [Buf.Of(id)], _ => [new(npId, 0x24)]);

    /// <summary>(id, SceNpId *out): 0xB0002/3.</summary>
    [Hle("sceNpManager_2841C20F", Nid = 0x2841C20F, Library = "SceNpManager")]
    int NpIpmiB0002(uint id, uint npId) => Request(id, 0xB0002, [Buf.Of(id)], _ => [new(npId, 0x24)]);

    /// <summary>(id, void *ticket 0x600, uint *size): a ticket request; the size in is *size when a buffer is given,
    /// out come the size, a second word and the ticket.</summary>
    int TicketRequest(uint method, uint id, uint ticket, uint size) => WithScratch(t =>
    {
        uint extra = Scratch(t, 4);
        GuestMemory.W32(extra, ticket != 0 ? GuestMemory.R32(size) : 0);
        return Request(id, method, [Buf.Of(id, GuestMemory.R32(extra))], _ => [new(size, 4), new(extra, 4), new(ticket, 0x600)]);
    }, MGR_NOT_INITIALIZED);

    [Hle("sceNpManager_7CE94530", Nid = 0x7CE94530, Library = "SceNpManager")] int NpIpmiB0004(uint id, uint ticket, uint size) => TicketRequest(0xB0004, id, ticket, size);
    [Hle("sceNpManager_E2FAC2CA", Nid = 0xE2FAC2CA, Library = "SceNpManager")] int NpIpmiB0006(uint id, uint ticket, uint size) => TicketRequest(0xB0006, id, ticket, size);
    [Hle("sceNpManager_5EB63590", Nid = 0x5EB63590, Library = "SceNpManager")] int NpIpmiB0008(uint id, uint ticket, uint size) => TicketRequest(0xB0008, id, ticket, size);

    /// <summary>(id, void *a 0x1D0, void *b 0x10): 0xB000A/B with which outputs are wanted; both arrive in local
    /// buffers and are copied out once the request id is given back.</summary>
    [Hle("sceNpManager_2FE3625C", Nid = 0x2FE3625C, Library = "SceNpManager")]
    int NpIpmiB000A(uint id, uint a, uint b) => WithScratch(t =>
    {
        uint la = Scratch(t, 0x1D0), lb = Scratch(t, 0x10);
        return Request(id, 0xB000A, [Buf.Of(id, a != 0 ? 1u : 0, b != 0 ? 1u : 0)], _ => [new(la, 0x1D0), new(lb, 0x10)], afterGive: _ =>
        {
            if (a != 0) GuestMemory.Copy(a, la, 0x1D0);
            if (b != 0) GuestMemory.Copy(b, lb, 0x10);
        });
    }, MGR_NOT_INITIALIZED);

    /// <summary>(id, const void *a 0x44, const void *b 0x24): 0xB000C/D, nothing back but the result.</summary>
    [Hle("sceNpManager_63EF4853", Nid = 0x63EF4853, Library = "SceNpManager")]
    int NpIpmiB000C(uint id, uint a, uint b) => Request(id, 0xB000C, [Buf.Of(id), new(a, 0x44), new(b, 0x24)], _ => []);

    /// <summary>(id, void *out 0x108): 0xB0042/3 (the give-back's error is not reported).</summary>
    [Hle("sceNpManager_FDE78C74", Nid = 0xFDE78C74, Library = "SceNpManager")]
    int NpIpmiB0042(uint id, uint outp) => Request(id, 0xB0042, [Buf.Of(id)], _ => [new(outp, 0x108)], ignoreGive: true);

    /// <summary>(id, const SceNpId *npId, int b, void *out 0x1C8): 0xB002C/D.</summary>
    [Hle("sceNpManager_FF0D6501", Nid = 0xFF0D6501, Library = "SceNpManager")]
    int NpIpmiB002C(uint id, uint npId, uint b, uint outp) => outp == 0 || npId == 0 ? MGR_INVALID_ARGUMENT
        : Request(id, 0xB002C, [Buf.Of(id, b), new(npId, 0x24)], _ => [new(outp, 0x1C8)]);

    /// <summary>(id, a, b, void *items, int count, int *got): a list request with 0x19C-byte items (0xB0030, 0xB0034,
    /// 0xB0038): in {id, a, b, count}; out the count, the items, the number written.</summary>
    int ItemList(uint method, uint id, uint a, uint b, uint items, uint count, uint got) => items == 0 || got == 0 ? MGR_INVALID_ARGUMENT
        : WithScratch(t =>
        {
            uint n = Scratch(t, 4);
            GuestMemory.W32(n, count);
            return Request(id, method, [Buf.Of(id, a, b, count)], _ => [new(n, 4), new(items, GuestMemory.R32(n) * 0x19C), new(got, 4)]);
        }, MGR_NOT_INITIALIZED);

    [Hle("sceNpManager_A5F0BD9C", Nid = 0xA5F0BD9C, Library = "SceNpManager")] int NpIpmiB0030(uint id, uint a, uint b, uint items, uint count, uint got) => ItemList(0xB0030, id, a, b, items, count, got);
    [Hle("sceNpManager_89E693FB", Nid = 0x89E693FB, Library = "SceNpManager")] int NpIpmiB0034(uint id, uint a, uint b, uint items, uint count, uint got) => ItemList(0xB0034, id, a, b, items, count, got);
    [Hle("sceNpManager_6D13F55F", Nid = 0x6D13F55F, Library = "SceNpManager")] int NpIpmiB0038(uint id, uint a, uint b, uint items, uint count, uint got) => ItemList(0xB0038, id, a, b, items, count, got);

    /// <summary>(id, const int *key, a, b, int *ids, char (*names)[0x80], int count, int *got): 0xB0032/3: in {id, a, b,
    /// count} and the key word; out the count, count ids (4 bytes), count names (0x80), the number written.</summary>
    [Hle("sceNpManager_D6C54CC2", Nid = 0xD6C54CC2, Library = "SceNpManager")]
    int NpIpmiB0032(uint id, uint key, uint a, uint b, uint ids, uint names, uint count, uint got) =>
        key == 0 || ids == 0 || names == 0 || got == 0 ? MGR_INVALID_ARGUMENT : WithScratch(t =>
        {
            uint n = Scratch(t, 4);
            GuestMemory.W32(n, count);
            return Request(id, 0xB0032, [Buf.Of(id, a, b, count), new(key, 4)], _ =>
            {
                uint c = GuestMemory.R32(n);
                return [new(n, 4), new(ids, c << 2), new(names, c << 7), new(got, 4)];
            });
        }, MGR_NOT_INITIALIZED);

    /// <summary>(id, const int *key, a, b, int *x, int *y, char (*names)[0x80], int count, int *got): 0xB0036/7, as
    /// NID_D6C54CC2 with two word arrays.</summary>
    [Hle("sceNpManager_28DED5D1", Nid = 0x28DED5D1, Library = "SceNpManager")]
    int NpIpmiB0036(uint id, uint key, uint a, uint b, uint x, uint y, uint names, uint count, uint got) =>
        key == 0 || x == 0 || y == 0 || names == 0 || got == 0 ? MGR_INVALID_ARGUMENT : WithScratch(t =>
        {
            uint n = Scratch(t, 4);
            GuestMemory.W32(n, count);
            return Request(id, 0xB0036, [Buf.Of(id, a, b, count), new(key, 4)], _ =>
            {
                uint c = GuestMemory.R32(n);
                return [new(n, 4), new(x, c << 2), new(y, c << 2), new(names, c << 7), new(got, 4)];
            });
        }, MGR_NOT_INITIALIZED);

    /// <summary>(id, a, b, const int *key, c, d, int *ids, char (*names)[0x80], int *more, int count, int *got1,
    /// int *got2): 0xB003E/F: in {id, a, b, c, d, count, more == NULL} and the key; out the count, ids, names, the
    /// second count (count when more is given, else 0), more (4 bytes each), both numbers written.</summary>
    [Hle("sceNpManager_557D5CC1", Nid = 0x557D5CC1, Library = "SceNpManager")]
    int NpIpmiB003E(uint id, uint a, uint b, uint key, uint c, uint d, uint ids, uint names, uint more, uint count, uint got1, uint got2) =>
        key == 0 || ids == 0 || names == 0 || got1 == 0 || got2 == 0 ? MGR_INVALID_ARGUMENT : WithScratch(t =>
        {
            uint n = Scratch(t, 4), m = Scratch(t, 4);
            GuestMemory.W32(n, count);
            return Request(id, 0xB003E, [Buf.Of(id, a, b, c, d, count, more == 0 ? 1u : 0), new(key, 4)], _ =>
            {
                GuestMemory.W32(m, more != 0 ? count : 0);
                uint k = GuestMemory.R32(n);
                return [new(n, 4), new(ids, k << 2), new(names, k << 7), new(m, 4), new(more, GuestMemory.R32(m) << 2), new(got1, 4), new(got2, 4)];
            });
        }, MGR_NOT_INITIALIZED);

    /// <summary>(id, a, b, c, d, char out[0x80]): 0xB0040/1.</summary>
    [Hle("sceNpManager_D891AB9A", Nid = 0xD891AB9A, Library = "SceNpManager")]
    int NpIpmiB0040(uint id, uint a, uint b, uint c, uint d, uint outp) => outp == 0 ? MGR_INVALID_ARGUMENT
        : Request(id, 0xB0040, [Buf.Of(id, a, b, c, d)], _ => [new(outp, 0x80)]);

    /// <summary>(id, const void *a 0x34, const void *b 0x10, const void *c 0x40, const uint *d, const void *e,
    /// int f, const uint *g, const void *h, int i, void *out 0x1C8, int *o1, int *o2, void *o3 8, const void *data,
    /// uint dataSize): 0xB002E/F. The optional blocks are copied (zero when NULL) and sent with flags saying which were
    /// given; the four outputs come back.</summary>
    [Hle("sceNpManager_2BB7D844", Nid = 0x2BB7D844, Library = "SceNpManager")]
    int NpIpmiB002E(uint id, uint a, uint b, uint c, uint d, uint e, uint f, uint g, uint h, uint i, uint outp, uint o1,
        uint o2, uint o3, uint data, uint dataSize) =>
        outp == 0 || o1 == 0 || o2 == 0 || o3 == 0 || data == 0 ? MGR_INVALID_ARGUMENT : WithScratch(t =>
        {
            uint la = Scratch(t, 0x34), lb = Scratch(t, 0x10), lc = Scratch(t, 0x40), ld = Scratch(t, 4), lg = Scratch(t, 4),
                le = Scratch(t, 8), lh = Scratch(t, 0x10);
            GuestMemory.Fill(la, 0, 0x34); GuestMemory.Fill(lb, 0, 0x10); GuestMemory.Fill(lc, 0, 0x40);
            GuestMemory.Fill(le, 0, 8); GuestMemory.Fill(lh, 0, 0x10);
            if (a != 0) GuestMemory.Copy(la, a, 0x34);
            if (b != 0) GuestMemory.Copy(lb, b, 0x10);
            if (c != 0) GuestMemory.Copy(lc, c, 0x40);
            GuestMemory.W32(ld, d != 0 ? GuestMemory.R32(d) : 0);
            GuestMemory.W32(lg, g != 0 ? GuestMemory.R32(g) : 0);
            static uint Z(uint p) => p == 0 ? 1u : 0;
            return Request(id, 0xB002E,
                [Buf.Of(id, Z(a), Z(b), Z(c), Z(d), Z(e), f, Z(g), Z(h), i, dataSize), new(la, 0x34), new(lb, 0x10), new(lc, 0x40),
                    new(ld, 4), new(e != 0 ? e : le, 8), new(lg, 4), new(h != 0 ? h : lh, 0xD), new(data, dataSize)],
                _ => [new(outp, 0x1C8), new(o1, 4), new(o2, 4), new(o3, 8)]);
        }, MGR_NOT_INITIALIZED);

    /// <summary>(void): synchronous 0xB0048 on the connection.</summary>
    [Hle("sceNpManager_F66A7AAE", Nid = 0xF66A7AAE, Library = "SceNpManager")] int NpIpmiB0048() => Sync(0xB0048, []);

    /// <summary>(void *a 0x108, uint8_t *b): synchronous 0xB0049, both filled.</summary>
    [Hle("sceNpManager_1F0C6314", Nid = 0x1F0C6314, Library = "SceNpManager")] int NpIpmiB0049(uint a, uint b) => Sync(0xB0049, [new(a, 0x108), new(b, 1)]);
}
