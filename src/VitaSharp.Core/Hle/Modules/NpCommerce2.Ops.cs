using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

// np_commerce2.suprx's context and request operations without public names (FW 3.74; the PS Store application and the
// store checkout plugin use them), read in the module. A context holds up to eight requests; a request id is the
// context id in the high half and a rolling 16-bit number in the low half, and each request has a type (the PS
// Store's request kinds). Every export first checks that the library is initialised (0x80550F01) and its pointer
// arguments (0x80550F03), then looks up the context (0x80550F0C) and the request (0x80550F0E). The operations that then
// talk to the store (an HTTP exchange with the PS Store servers, or a request to the shell's store service) end like
// the HLE's other store requests: the servers cannot be reached.

public sealed partial class NpCommerce2
{
    const int ERR_CTX_NOT_FOUND = unchecked((int)0x80550F0C), ERR_REQ_NOT_FOUND = unchecked((int)0x80550F0E),
        ERR_ID_EXHAUSTED = unchecked((int)0x80550F0F), ERR_BUFFER_TOO_SMALL = unchecked((int)0x80550F18),
        ERR_TOO_MANY_REQS = unchecked((int)0x80550F19), ERR_NO_SESSION = unchecked((int)0x80550F91),
        ERR_BAD_VALUE = unchecked((int)0x80550F94), ERR_NO_TICKET = unchecked((int)0x8055102A),
        ERR_LIB_NOT_INITIALIZED = unchecked((int)0x80550F01);

    readonly Dictionary<uint, ushort> _reqTypes = new();
    ushort _lastReqNumber;

    /// <summary>A request of <paramref name="type"/> in context <paramref name="ctxId"/> (*reqId = ctx &lt;&lt; 16 | n,
    /// n rolling over 1..0xFFFF and skipping the ids in use; at most eight per context).</summary>
    int CreateTypedReq(uint ctxId, uint reqIdOut, ushort type)
    {
        lock (_lock)
        {
            if (!_inited) return ERR_LIB_NOT_INITIALIZED;
            if (reqIdOut == 0) return ERR_SYS_INVALID;
            if (!_ctx.Contains(ctxId)) return ERR_CTX_NOT_FOUND;
            if (_reqs.Count(kv => kv.Value == ctxId) > 7) return ERR_TOO_MANY_REQS;
            ushort start = _lastReqNumber, n = start;
            do
            {
                n = n == 0xFFFF ? (ushort)1 : (ushort)(n + 1);
                uint id = ctxId << 16 | n;
                if (!_reqs.ContainsKey(id))
                {
                    _lastReqNumber = n;
                    _reqs[id] = ctxId;
                    _reqTypes[id] = type;
                    GuestMemory.W32(reqIdOut, id);
                    return 0;
                }
            } while (n != start);
            return ERR_ID_EXHAUSTED;
        }
    }

    /// <summary>The library's checks before a request operation: initialised, the request's context, the request.</summary>
    int FindReq(uint reqId, out ushort type)
    {
        lock (_lock)
        {
            type = 0;
            if (!_inited) return ERR_LIB_NOT_INITIALIZED;
            if (_reqs.ContainsKey(reqId)) { type = _reqTypes.GetValueOrDefault(reqId); return 0; }
            return _ctx.Contains(reqId >> 16) ? ERR_REQ_NOT_FOUND : ERR_CTX_NOT_FOUND;
        }
    }

    /// <summary>A request operation that needs the store: the request's checks, then the unreachable server.</summary>
    int StoreRequest(uint reqId) => FindReq(reqId, out _) is var r and < 0 ? r : PsnAccount.Current.ServerError;

    /// <summary>Initialised first, then the export's own argument check, then the request.</summary>
    int InitOr(bool argsOk, uint reqId)
    {
        lock (_lock) if (!_inited) return ERR_LIB_NOT_INITIALIZED;
        return argsOk ? StoreRequest(reqId) : ERR_SYS_INVALID;
    }

    // ---- request creation, one export per request type: (ctxId, uint *reqId)

    [Hle("sceNpCommerce2_07C16199", Nid = 0x07C16199, Library = "SceNpCommerce2")] int Create07C16199(uint ctx, uint req) => CreateTypedReq(ctx, req, 0x41);
    [Hle("sceNpCommerce2_0985546A", Nid = 0x0985546A, Library = "SceNpCommerce2")] int Create0985546A(uint ctx, uint req) => CreateTypedReq(ctx, req, 0x10);
    [Hle("sceNpCommerce2_0BF3C1C4", Nid = 0x0BF3C1C4, Library = "SceNpCommerce2")] int Create0BF3C1C4(uint ctx, uint req) => CreateTypedReq(ctx, req, 0x39);
    [Hle("sceNpCommerce2_0C33B954", Nid = 0x0C33B954, Library = "SceNpCommerce2")] int Create0C33B954(uint ctx, uint req) => CreateTypedReq(ctx, req, 0x46);
    [Hle("sceNpCommerce2_142EAC1B", Nid = 0x142EAC1B, Library = "SceNpCommerce2")] int Create142EAC1B(uint ctx, uint req) => CreateTypedReq(ctx, req, 0xc);
    [Hle("sceNpCommerce2_1729CBC6", Nid = 0x1729CBC6, Library = "SceNpCommerce2")] int Create1729CBC6(uint ctx, uint req) => CreateTypedReq(ctx, req, 0x29);
    [Hle("sceNpCommerce2_1AF7FC73", Nid = 0x1AF7FC73, Library = "SceNpCommerce2")] int Create1AF7FC73(uint ctx, uint req) => CreateTypedReq(ctx, req, 8);
    [Hle("sceNpCommerce2_24257016", Nid = 0x24257016, Library = "SceNpCommerce2")] int Create24257016(uint ctx, uint req) => CreateTypedReq(ctx, req, 0x58);
    [Hle("sceNpCommerce2_2511F962", Nid = 0x2511F962, Library = "SceNpCommerce2")] int Create2511F962(uint ctx, uint req) => CreateTypedReq(ctx, req, 0x57);
    [Hle("sceNpCommerce2_27E08EFA", Nid = 0x27E08EFA, Library = "SceNpCommerce2")] int Create27E08EFA(uint ctx, uint req) => CreateTypedReq(ctx, req, 0x24);
    [Hle("sceNpCommerce2_2AF9CC74", Nid = 0x2AF9CC74, Library = "SceNpCommerce2")] int Create2AF9CC74(uint ctx, uint req) => CreateTypedReq(ctx, req, 0x32);
    [Hle("sceNpCommerce2_2CCC62DD", Nid = 0x2CCC62DD, Library = "SceNpCommerce2")] int Create2CCC62DD(uint ctx, uint req) => CreateTypedReq(ctx, req, 0x4c);
    [Hle("sceNpCommerce2_2EBD25FF", Nid = 0x2EBD25FF, Library = "SceNpCommerce2")] int Create2EBD25FF(uint ctx, uint req) => CreateTypedReq(ctx, req, 0x2e);
    [Hle("sceNpCommerce2_2FEC7ABC", Nid = 0x2FEC7ABC, Library = "SceNpCommerce2")] int Create2FEC7ABC(uint ctx, uint req) => CreateTypedReq(ctx, req, 10);
    [Hle("sceNpCommerce2_42DB888A", Nid = 0x42DB888A, Library = "SceNpCommerce2")] int Create42DB888A(uint ctx, uint req) => CreateTypedReq(ctx, req, 0x36);
    [Hle("sceNpCommerce2_4615377F", Nid = 0x4615377F, Library = "SceNpCommerce2")] int Create4615377F(uint ctx, uint req) => CreateTypedReq(ctx, req, 0x3f);
    [Hle("sceNpCommerce2_48171860", Nid = 0x48171860, Library = "SceNpCommerce2")] int Create48171860(uint ctx, uint req) => CreateTypedReq(ctx, req, 0x2f);
    [Hle("sceNpCommerce2_4C30AF39", Nid = 0x4C30AF39, Library = "SceNpCommerce2")] int Create4C30AF39(uint ctx, uint req) => CreateTypedReq(ctx, req, 0x13);
    [Hle("sceNpCommerce2_4C5EA07A", Nid = 0x4C5EA07A, Library = "SceNpCommerce2")] int Create4C5EA07A(uint ctx, uint req) => CreateTypedReq(ctx, req, 0x40);
    [Hle("sceNpCommerce2_4D7B5ACE", Nid = 0x4D7B5ACE, Library = "SceNpCommerce2")] int Create4D7B5ACE(uint ctx, uint req) => CreateTypedReq(ctx, req, 0x45);
    [Hle("sceNpCommerce2_4E4A2898", Nid = 0x4E4A2898, Library = "SceNpCommerce2")] int Create4E4A2898(uint ctx, uint req) => CreateTypedReq(ctx, req, 0x26);
    [Hle("sceNpCommerce2_5402711A", Nid = 0x5402711A, Library = "SceNpCommerce2")] int Create5402711A(uint ctx, uint req) => CreateTypedReq(ctx, req, 0x2c);
    [Hle("sceNpCommerce2_577F8BF3", Nid = 0x577F8BF3, Library = "SceNpCommerce2")] int Create577F8BF3(uint ctx, uint req) => CreateTypedReq(ctx, req, 0x44);
    [Hle("sceNpCommerce2_5A1ACD90", Nid = 0x5A1ACD90, Library = "SceNpCommerce2")] int Create5A1ACD90(uint ctx, uint req) => CreateTypedReq(ctx, req, 0x1d);
    [Hle("sceNpCommerce2_65743DF2", Nid = 0x65743DF2, Library = "SceNpCommerce2")] int Create65743DF2(uint ctx, uint req) => CreateTypedReq(ctx, req, 0x27);
    [Hle("sceNpCommerce2_693EAE89", Nid = 0x693EAE89, Library = "SceNpCommerce2")] int Create693EAE89(uint ctx, uint req) => CreateTypedReq(ctx, req, 0x25);
    [Hle("sceNpCommerce2_6E0F1699", Nid = 0x6E0F1699, Library = "SceNpCommerce2")] int Create6E0F1699(uint ctx, uint req) => CreateTypedReq(ctx, req, 0x23);
    [Hle("sceNpCommerce2_6F3A7FEF", Nid = 0x6F3A7FEF, Library = "SceNpCommerce2")] int Create6F3A7FEF(uint ctx, uint req) => CreateTypedReq(ctx, req, 0x37);
    [Hle("sceNpCommerce2_76DA0D80", Nid = 0x76DA0D80, Library = "SceNpCommerce2")] int Create76DA0D80(uint ctx, uint req) => CreateTypedReq(ctx, req, 0x3c);
    [Hle("sceNpCommerce2_8AA18E16", Nid = 0x8AA18E16, Library = "SceNpCommerce2")] int Create8AA18E16(uint ctx, uint req) => CreateTypedReq(ctx, req, 0x22);
    [Hle("sceNpCommerce2_907BC19E", Nid = 0x907BC19E, Library = "SceNpCommerce2")] int Create907BC19E(uint ctx, uint req) => CreateTypedReq(ctx, req, 0x3a);
    [Hle("sceNpCommerce2_95C185B5", Nid = 0x95C185B5, Library = "SceNpCommerce2")] int Create95C185B5(uint ctx, uint req) => CreateTypedReq(ctx, req, 0x1e);
    [Hle("sceNpCommerce2_95F62CC5", Nid = 0x95F62CC5, Library = "SceNpCommerce2")] int Create95F62CC5(uint ctx, uint req) => CreateTypedReq(ctx, req, 6);
    [Hle("sceNpCommerce2_984036CB", Nid = 0x984036CB, Library = "SceNpCommerce2")] int Create984036CB(uint ctx, uint req) => CreateTypedReq(ctx, req, 0x18);
    [Hle("sceNpCommerce2_9A24EB17", Nid = 0x9A24EB17, Library = "SceNpCommerce2")] int Create9A24EB17(uint ctx, uint req) => CreateTypedReq(ctx, req, 0x2d);
    [Hle("sceNpCommerce2_9B4811DF", Nid = 0x9B4811DF, Library = "SceNpCommerce2")] int Create9B4811DF(uint ctx, uint req) => CreateTypedReq(ctx, req, 0x1a);
    [Hle("sceNpCommerce2_9C1C168D", Nid = 0x9C1C168D, Library = "SceNpCommerce2")] int Create9C1C168D(uint ctx, uint req) => CreateTypedReq(ctx, req, 0x71);
    [Hle("sceNpCommerce2_9FD5705B", Nid = 0x9FD5705B, Library = "SceNpCommerce2")] int Create9FD5705B(uint ctx, uint req) => CreateTypedReq(ctx, req, 100);
    [Hle("sceNpCommerce2_A9DFEF68", Nid = 0xA9DFEF68, Library = "SceNpCommerce2")] int CreateA9DFEF68(uint ctx, uint req) => CreateTypedReq(ctx, req, 0x33);
    [Hle("sceNpCommerce2_AC9457D4", Nid = 0xAC9457D4, Library = "SceNpCommerce2")] int CreateAC9457D4(uint ctx, uint req) => CreateTypedReq(ctx, req, 0x28);
    [Hle("sceNpCommerce2_ACABE7E1", Nid = 0xACABE7E1, Library = "SceNpCommerce2")] int CreateACABE7E1(uint ctx, uint req) => CreateTypedReq(ctx, req, 0x11);
    [Hle("sceNpCommerce2_B75D54D3", Nid = 0xB75D54D3, Library = "SceNpCommerce2")] int CreateB75D54D3(uint ctx, uint req) => CreateTypedReq(ctx, req, 0x31);
    [Hle("sceNpCommerce2_B7D151D1", Nid = 0xB7D151D1, Library = "SceNpCommerce2")] int CreateB7D151D1(uint ctx, uint req) => CreateTypedReq(ctx, req, 0x1f);
    [Hle("sceNpCommerce2_B98EE069", Nid = 0xB98EE069, Library = "SceNpCommerce2")] int CreateB98EE069(uint ctx, uint req) => CreateTypedReq(ctx, req, 5);
    [Hle("sceNpCommerce2_BDA95C6D", Nid = 0xBDA95C6D, Library = "SceNpCommerce2")] int CreateBDA95C6D(uint ctx, uint req) => CreateTypedReq(ctx, req, 0xd);
    [Hle("sceNpCommerce2_C0ECF440", Nid = 0xC0ECF440, Library = "SceNpCommerce2")] int CreateC0ECF440(uint ctx, uint req) => CreateTypedReq(ctx, req, 0xb);
    [Hle("sceNpCommerce2_C8844157", Nid = 0xC8844157, Library = "SceNpCommerce2")] int CreateC8844157(uint ctx, uint req) => CreateTypedReq(ctx, req, 9);
    [Hle("sceNpCommerce2_CAC11D21", Nid = 0xCAC11D21, Library = "SceNpCommerce2")] int CreateCAC11D21(uint ctx, uint req) => CreateTypedReq(ctx, req, 0x19);
    [Hle("sceNpCommerce2_DACD1180", Nid = 0xDACD1180, Library = "SceNpCommerce2")] int CreateDACD1180(uint ctx, uint req) => CreateTypedReq(ctx, req, 0x2a);
    [Hle("sceNpCommerce2_DF504EEF", Nid = 0xDF504EEF, Library = "SceNpCommerce2")] int CreateDF504EEF(uint ctx, uint req) => CreateTypedReq(ctx, req, 0x12);
    [Hle("sceNpCommerce2_E0B222F3", Nid = 0xE0B222F3, Library = "SceNpCommerce2")] int CreateE0B222F3(uint ctx, uint req) => CreateTypedReq(ctx, req, 0x30);
    [Hle("sceNpCommerce2_E1C27906", Nid = 0xE1C27906, Library = "SceNpCommerce2")] int CreateE1C27906(uint ctx, uint req) => CreateTypedReq(ctx, req, 0x3d);
    [Hle("sceNpCommerce2_E324B61B", Nid = 0xE324B61B, Library = "SceNpCommerce2")] int CreateE324B61B(uint ctx, uint req) => CreateTypedReq(ctx, req, 0x54);
    [Hle("sceNpCommerce2_E4DEA05E", Nid = 0xE4DEA05E, Library = "SceNpCommerce2")] int CreateE4DEA05E(uint ctx, uint req) => CreateTypedReq(ctx, req, 0x17);
    [Hle("sceNpCommerce2_EA4A4CAE", Nid = 0xEA4A4CAE, Library = "SceNpCommerce2")] int CreateEA4A4CAE(uint ctx, uint req) => CreateTypedReq(ctx, req, 0x2b);
    [Hle("sceNpCommerce2_ED1A7FD1", Nid = 0xED1A7FD1, Library = "SceNpCommerce2")] int CreateED1A7FD1(uint ctx, uint req) => CreateTypedReq(ctx, req, 0x35);
    [Hle("sceNpCommerce2_F1E443C9", Nid = 0xF1E443C9, Library = "SceNpCommerce2")] int CreateF1E443C9(uint ctx, uint req) => CreateTypedReq(ctx, req, 0x34);
    [Hle("sceNpCommerce2_F4DD3FD8", Nid = 0xF4DD3FD8, Library = "SceNpCommerce2")] int CreateF4DD3FD8(uint ctx, uint req) => CreateTypedReq(ctx, req, 0x21);
    [Hle("sceNpCommerce2_F764DBA5", Nid = 0xF764DBA5, Library = "SceNpCommerce2")] int CreateF764DBA5(uint ctx, uint req) => CreateTypedReq(ctx, req, 0x20);
    [Hle("sceNpCommerce2_F88D6E68", Nid = 0xF88D6E68, Library = "SceNpCommerce2")] int CreateF88D6E68(uint ctx, uint req) => CreateTypedReq(ctx, req, 0x4d);
    [Hle("sceNpCommerce2_F8D3F362", Nid = 0xF8D3F362, Library = "SceNpCommerce2")] int CreateF8D3F362(uint ctx, uint req) => CreateTypedReq(ctx, req, 0x14);
    [Hle("sceNpCommerce2_FB35C43D", Nid = 0xFB35C43D, Library = "SceNpCommerce2")] int CreateFB35C43D(uint ctx, uint req) => CreateTypedReq(ctx, req, 0x3b);
    [Hle("sceNpCommerce2_FDB807AC", Nid = 0xFDB807AC, Library = "SceNpCommerce2")] int CreateFDB807AC(uint ctx, uint req) => CreateTypedReq(ctx, req, 0x50);
    /// <summary>(ctxId, uint *reqId): request type 1 in its two variants (the library's kinds 2 and 11).</summary>
    [Hle("sceNpCommerce2_7DE103BB", Nid = 0x7DE103BB, Library = "SceNpCommerce2")] int Create7DE103BB(uint ctx, uint req) => CreateTypedReq(ctx, req, 1);
    [Hle("sceNpCommerce2_DA5B6779", Nid = 0xDA5B6779, Library = "SceNpCommerce2")] int CreateDA5B6779(uint ctx, uint req) => CreateTypedReq(ctx, req, 1);

    // ---- send a request and read the store's reply into the caller's buffer: (reqId, void *buf, uint size,
    // uint *filled); the replies of types 3 and 4 need a buffer of 256 KiB (0x80550F18). One routine, 63 exports.

    [Hle("sceNpCommerce2_03A27E16", Nid = 0x03A27E16, Library = "SceNpCommerce2")]
    [Hle("sceNpCommerce2_05D6DBDB", Nid = 0x05D6DBDB, Library = "SceNpCommerce2")]
    [Hle("sceNpCommerce2_07E38F93", Nid = 0x07E38F93, Library = "SceNpCommerce2")]
    [Hle("sceNpCommerce2_0CA0B23F", Nid = 0x0CA0B23F, Library = "SceNpCommerce2")]
    [Hle("sceNpCommerce2_12467D66", Nid = 0x12467D66, Library = "SceNpCommerce2")]
    [Hle("sceNpCommerce2_1DE3D522", Nid = 0x1DE3D522, Library = "SceNpCommerce2")]
    [Hle("sceNpCommerce2_22640144", Nid = 0x22640144, Library = "SceNpCommerce2")]
    [Hle("sceNpCommerce2_2AA6C91E", Nid = 0x2AA6C91E, Library = "SceNpCommerce2")]
    [Hle("sceNpCommerce2_2AD3766D", Nid = 0x2AD3766D, Library = "SceNpCommerce2")]
    [Hle("sceNpCommerce2_2D3073AF", Nid = 0x2D3073AF, Library = "SceNpCommerce2")]
    [Hle("sceNpCommerce2_2E9BE5D6", Nid = 0x2E9BE5D6, Library = "SceNpCommerce2")]
    [Hle("sceNpCommerce2_2FC09E2B", Nid = 0x2FC09E2B, Library = "SceNpCommerce2")]
    [Hle("sceNpCommerce2_39E7EB72", Nid = 0x39E7EB72, Library = "SceNpCommerce2")]
    [Hle("sceNpCommerce2_3EFF3363", Nid = 0x3EFF3363, Library = "SceNpCommerce2")]
    [Hle("sceNpCommerce2_3FEEFAB4", Nid = 0x3FEEFAB4, Library = "SceNpCommerce2")]
    [Hle("sceNpCommerce2_4322E924", Nid = 0x4322E924, Library = "SceNpCommerce2")]
    [Hle("sceNpCommerce2_4719EB55", Nid = 0x4719EB55, Library = "SceNpCommerce2")]
    [Hle("sceNpCommerce2_4ABA502B", Nid = 0x4ABA502B, Library = "SceNpCommerce2")]
    [Hle("sceNpCommerce2_4C1B2B7B", Nid = 0x4C1B2B7B, Library = "SceNpCommerce2")]
    [Hle("sceNpCommerce2_4C69865E", Nid = 0x4C69865E, Library = "SceNpCommerce2")]
    [Hle("sceNpCommerce2_5786F157", Nid = 0x5786F157, Library = "SceNpCommerce2")]
    [Hle("sceNpCommerce2_6632CE20", Nid = 0x6632CE20, Library = "SceNpCommerce2")]
    [Hle("sceNpCommerce2_6A717339", Nid = 0x6A717339, Library = "SceNpCommerce2")]
    [Hle("sceNpCommerce2_6C0B2508", Nid = 0x6C0B2508, Library = "SceNpCommerce2")]
    [Hle("sceNpCommerce2_771C582F", Nid = 0x771C582F, Library = "SceNpCommerce2")]
    [Hle("sceNpCommerce2_777ADB4A", Nid = 0x777ADB4A, Library = "SceNpCommerce2")]
    [Hle("sceNpCommerce2_797BC431", Nid = 0x797BC431, Library = "SceNpCommerce2")]
    [Hle("sceNpCommerce2_7B93B7A6", Nid = 0x7B93B7A6, Library = "SceNpCommerce2")]
    [Hle("sceNpCommerce2_81DE35CA", Nid = 0x81DE35CA, Library = "SceNpCommerce2")]
    [Hle("sceNpCommerce2_838772EE", Nid = 0x838772EE, Library = "SceNpCommerce2")]
    [Hle("sceNpCommerce2_84B9CB66", Nid = 0x84B9CB66, Library = "SceNpCommerce2")]
    [Hle("sceNpCommerce2_85587466", Nid = 0x85587466, Library = "SceNpCommerce2")]
    [Hle("sceNpCommerce2_86E0D693", Nid = 0x86E0D693, Library = "SceNpCommerce2")]
    [Hle("sceNpCommerce2_89171F84", Nid = 0x89171F84, Library = "SceNpCommerce2")]
    [Hle("sceNpCommerce2_8B4BEA99", Nid = 0x8B4BEA99, Library = "SceNpCommerce2")]
    [Hle("sceNpCommerce2_8D8509B1", Nid = 0x8D8509B1, Library = "SceNpCommerce2")]
    [Hle("sceNpCommerce2_8E4CB649", Nid = 0x8E4CB649, Library = "SceNpCommerce2")]
    [Hle("sceNpCommerce2_92DF40CC", Nid = 0x92DF40CC, Library = "SceNpCommerce2")]
    [Hle("sceNpCommerce2_94AA1BFB", Nid = 0x94AA1BFB, Library = "SceNpCommerce2")]
    [Hle("sceNpCommerce2_94B068D1", Nid = 0x94B068D1, Library = "SceNpCommerce2")]
    [Hle("sceNpCommerce2_97BE12D0", Nid = 0x97BE12D0, Library = "SceNpCommerce2")]
    [Hle("sceNpCommerce2_9833E1F1", Nid = 0x9833E1F1, Library = "SceNpCommerce2")]
    [Hle("sceNpCommerce2_9E1F6D58", Nid = 0x9E1F6D58, Library = "SceNpCommerce2")]
    [Hle("sceNpCommerce2_9FA3DB36", Nid = 0x9FA3DB36, Library = "SceNpCommerce2")]
    [Hle("sceNpCommerce2_A222BBDC", Nid = 0xA222BBDC, Library = "SceNpCommerce2")]
    [Hle("sceNpCommerce2_A609C808", Nid = 0xA609C808, Library = "SceNpCommerce2")]
    [Hle("sceNpCommerce2_AD4F85E1", Nid = 0xAD4F85E1, Library = "SceNpCommerce2")]
    [Hle("sceNpCommerce2_B15F1702", Nid = 0xB15F1702, Library = "SceNpCommerce2")]
    [Hle("sceNpCommerce2_B7387C96", Nid = 0xB7387C96, Library = "SceNpCommerce2")]
    [Hle("sceNpCommerce2_BC05DE41", Nid = 0xBC05DE41, Library = "SceNpCommerce2")]
    [Hle("sceNpCommerce2_C07A3248", Nid = 0xC07A3248, Library = "SceNpCommerce2")]
    [Hle("sceNpCommerce2_C3765C32", Nid = 0xC3765C32, Library = "SceNpCommerce2")]
    [Hle("sceNpCommerce2_C81DF030", Nid = 0xC81DF030, Library = "SceNpCommerce2")]
    [Hle("sceNpCommerce2_CD123380", Nid = 0xCD123380, Library = "SceNpCommerce2")]
    [Hle("sceNpCommerce2_D039082A", Nid = 0xD039082A, Library = "SceNpCommerce2")]
    [Hle("sceNpCommerce2_DDB75C18", Nid = 0xDDB75C18, Library = "SceNpCommerce2")]
    [Hle("sceNpCommerce2_DDB91745", Nid = 0xDDB91745, Library = "SceNpCommerce2")]
    [Hle("sceNpCommerce2_E16A3E4F", Nid = 0xE16A3E4F, Library = "SceNpCommerce2")]
    [Hle("sceNpCommerce2_E6BB6285", Nid = 0xE6BB6285, Library = "SceNpCommerce2")]
    [Hle("sceNpCommerce2_E89CF937", Nid = 0xE89CF937, Library = "SceNpCommerce2")]
    [Hle("sceNpCommerce2_F40D2B1E", Nid = 0xF40D2B1E, Library = "SceNpCommerce2")]
    [Hle("sceNpCommerce2_F9C7F5F5", Nid = 0xF9C7F5F5, Library = "SceNpCommerce2")]
    [Hle("sceNpCommerce2_F9DE9564", Nid = 0xF9DE9564, Library = "SceNpCommerce2")]
    int StoreExchange(uint reqId, uint buf, uint size, uint filled)
    {
        lock (_lock) if (!_inited) return ERR_LIB_NOT_INITIALIZED;
        if (buf == 0 || size == 0 || filled == 0) return ERR_SYS_INVALID;
        if (FindReq(reqId, out ushort type) is var r and < 0) return r;
        if (type is 3 or 4 && size < 0x40000) return ERR_BUFFER_TOO_SMALL;
        return PsnAccount.Current.ServerError;
    }

    // ---- requests whose arguments the library passes on unchecked: (reqId, ...)

    [Hle("sceNpCommerce2_0D56D112", Nid = 0x0D56D112, Library = "SceNpCommerce2")]
    [Hle("sceNpCommerce2_1BA1F399", Nid = 0x1BA1F399, Library = "SceNpCommerce2")]
    [Hle("sceNpCommerce2_25A32361", Nid = 0x25A32361, Library = "SceNpCommerce2")]
    [Hle("sceNpCommerce2_2DE33C94", Nid = 0x2DE33C94, Library = "SceNpCommerce2")]
    [Hle("sceNpCommerce2_3A050F80", Nid = 0x3A050F80, Library = "SceNpCommerce2")]
    [Hle("sceNpCommerce2_4BA3CA8C", Nid = 0x4BA3CA8C, Library = "SceNpCommerce2")]
    [Hle("sceNpCommerce2_527C3D90", Nid = 0x527C3D90, Library = "SceNpCommerce2")]
    [Hle("sceNpCommerce2_536DA1DA", Nid = 0x536DA1DA, Library = "SceNpCommerce2")]
    [Hle("sceNpCommerce2_68CF4B6B", Nid = 0x68CF4B6B, Library = "SceNpCommerce2")]
    [Hle("sceNpCommerce2_71CCD6E9", Nid = 0x71CCD6E9, Library = "SceNpCommerce2")]
    [Hle("sceNpCommerce2_7A8CA2C5", Nid = 0x7A8CA2C5, Library = "SceNpCommerce2")]
    [Hle("sceNpCommerce2_7B4888F0", Nid = 0x7B4888F0, Library = "SceNpCommerce2")]
    [Hle("sceNpCommerce2_7D2A014E", Nid = 0x7D2A014E, Library = "SceNpCommerce2")]
    [Hle("sceNpCommerce2_81C509DA", Nid = 0x81C509DA, Library = "SceNpCommerce2")]
    [Hle("sceNpCommerce2_8BB68957", Nid = 0x8BB68957, Library = "SceNpCommerce2")]
    [Hle("sceNpCommerce2_8EDA59C2", Nid = 0x8EDA59C2, Library = "SceNpCommerce2")]
    [Hle("sceNpCommerce2_917646A1", Nid = 0x917646A1, Library = "SceNpCommerce2")]
    [Hle("sceNpCommerce2_96C6955D", Nid = 0x96C6955D, Library = "SceNpCommerce2")]
    [Hle("sceNpCommerce2_974B42AE", Nid = 0x974B42AE, Library = "SceNpCommerce2")]
    [Hle("sceNpCommerce2_B38AB217", Nid = 0xB38AB217, Library = "SceNpCommerce2")]
    [Hle("sceNpCommerce2_B9BC6885", Nid = 0xB9BC6885, Library = "SceNpCommerce2")]
    [Hle("sceNpCommerce2_C9C5EBF7", Nid = 0xC9C5EBF7, Library = "SceNpCommerce2")]
    [Hle("sceNpCommerce2_CD382E48", Nid = 0xCD382E48, Library = "SceNpCommerce2")]
    [Hle("sceNpCommerce2_D3509F1B", Nid = 0xD3509F1B, Library = "SceNpCommerce2")]
    [Hle("sceNpCommerce2_D35F548B", Nid = 0xD35F548B, Library = "SceNpCommerce2")]
    [Hle("sceNpCommerce2_D7B43E47", Nid = 0xD7B43E47, Library = "SceNpCommerce2")]
    [Hle("sceNpCommerce2_ECCF1C47", Nid = 0xECCF1C47, Library = "SceNpCommerce2")]
    [Hle("sceNpCommerce2_1CC9B661", Nid = 0x1CC9B661, Library = "SceNpCommerce2")]
    [Hle("sceNpCommerce2_D160F700", Nid = 0xD160F700, Library = "SceNpCommerce2")]
    [Hle("sceNpCommerce2_00F16110", Nid = 0x00F16110, Library = "SceNpCommerce2")]
    [Hle("sceNpCommerce2_6B743513", Nid = 0x6B743513, Library = "SceNpCommerce2")]
    int StoreOp(uint reqId) => StoreRequest(reqId);

    // ---- (reqId, const void *param): the parameter is required

    [Hle("sceNpCommerce2_0E7B2BCD", Nid = 0x0E7B2BCD, Library = "SceNpCommerce2")]
    [Hle("sceNpCommerce2_10B76A6F", Nid = 0x10B76A6F, Library = "SceNpCommerce2")]
    [Hle("sceNpCommerce2_1A9E8047", Nid = 0x1A9E8047, Library = "SceNpCommerce2")]
    [Hle("sceNpCommerce2_513940B4", Nid = 0x513940B4, Library = "SceNpCommerce2")]
    [Hle("sceNpCommerce2_518197E0", Nid = 0x518197E0, Library = "SceNpCommerce2")]
    [Hle("sceNpCommerce2_528BF506", Nid = 0x528BF506, Library = "SceNpCommerce2")]
    [Hle("sceNpCommerce2_5DEC2B36", Nid = 0x5DEC2B36, Library = "SceNpCommerce2")]
    [Hle("sceNpCommerce2_7DB5D84A", Nid = 0x7DB5D84A, Library = "SceNpCommerce2")]
    [Hle("sceNpCommerce2_7FC86589", Nid = 0x7FC86589, Library = "SceNpCommerce2")]
    [Hle("sceNpCommerce2_9F34247B", Nid = 0x9F34247B, Library = "SceNpCommerce2")]
    [Hle("sceNpCommerce2_A4C52416", Nid = 0xA4C52416, Library = "SceNpCommerce2")]
    [Hle("sceNpCommerce2_AFD1BEE5", Nid = 0xAFD1BEE5, Library = "SceNpCommerce2")]
    [Hle("sceNpCommerce2_B7434181", Nid = 0xB7434181, Library = "SceNpCommerce2")]
    [Hle("sceNpCommerce2_B7552026", Nid = 0xB7552026, Library = "SceNpCommerce2")]
    [Hle("sceNpCommerce2_BAB8A533", Nid = 0xBAB8A533, Library = "SceNpCommerce2")]
    [Hle("sceNpCommerce2_D9A80442", Nid = 0xD9A80442, Library = "SceNpCommerce2")]
    [Hle("sceNpCommerce2_F9730784", Nid = 0xF9730784, Library = "SceNpCommerce2")]
    int StoreOpParam(uint reqId, uint param) => InitOr(param != 0, reqId);

    // ---- the others, each with its own argument checks

    /// <summary>(reqId, a, const void *param).</summary>
    [Hle("sceNpCommerce2_00DEFA26", Nid = 0x00DEFA26, Library = "SceNpCommerce2")] int StoreOp00DEFA26(uint reqId, uint a, uint p) => InitOr(p != 0, reqId);
    /// <summary>(reqId, a, b): at least one of the two.</summary>
    [Hle("sceNpCommerce2_397F839E", Nid = 0x397F839E, Library = "SceNpCommerce2")] int StoreOp397F839E(uint reqId, uint a, uint b) => InitOr(a != 0 || b != 0, reqId);
    [Hle("sceNpCommerce2_4FB61A81", Nid = 0x4FB61A81, Library = "SceNpCommerce2")]
    [Hle("sceNpCommerce2_B27F7BA1", Nid = 0xB27F7BA1, Library = "SceNpCommerce2")]
    [Hle("sceNpCommerce2_E13EBC17", Nid = 0xE13EBC17, Library = "SceNpCommerce2")]
    [Hle("sceNpCommerce2_EB8775F5", Nid = 0xEB8775F5, Library = "SceNpCommerce2")]
    int StoreOp2(uint reqId, uint a, uint b) => InitOr(a != 0 && b != 0, reqId);
    [Hle("sceNpCommerce2_A73CBF33", Nid = 0xA73CBF33, Library = "SceNpCommerce2")]
    [Hle("sceNpCommerce2_E2D8070E", Nid = 0xE2D8070E, Library = "SceNpCommerce2")]
    int StoreOp3(uint reqId, uint a, uint b, uint c) => InitOr(a != 0 && b != 0 && c != 0, reqId);
    [Hle("sceNpCommerce2_1B229032", Nid = 0x1B229032, Library = "SceNpCommerce2")]
    [Hle("sceNpCommerce2_BD0555F8", Nid = 0xBD0555F8, Library = "SceNpCommerce2")]
    int StoreOp4(uint reqId, uint a, uint b, uint c, uint d) => InitOr(a != 0 && b != 0 && c != 0 && d != 0, reqId);
    /// <summary>(reqId, a, b, c, d, e): all five.</summary>
    [Hle("sceNpCommerce2_E8F39860", Nid = 0xE8F39860, Library = "SceNpCommerce2")] int StoreOpE8F39860(uint reqId, uint a, uint b, uint c, uint d, uint e) => InitOr(a != 0 && b != 0 && c != 0 && d != 0 && e != 0, reqId);
    /// <summary>(reqId, a, b, c, d, e): the two stack arguments are required.</summary>
    [Hle("sceNpCommerce2_92DCC788", Nid = 0x92DCC788, Library = "SceNpCommerce2")] int StoreOp92DCC788(uint reqId, uint a, uint b, uint c, uint d, uint e) => InitOr(d != 0 && e != 0, reqId);
    /// <summary>(reqId, a, b, c, uint size): a and b required; size 0 or equal to b.</summary>
    [Hle("sceNpCommerce2_868D6E88", Nid = 0x868D6E88, Library = "SceNpCommerce2")] int StoreOp868D6E88(uint reqId, uint a, uint b, uint c, uint d) => InitOr(a != 0 && b != 0 && (d == 0 || d == b), reqId);
    /// <summary>(reqId, int kind, a, b): kind 1 needs a and b.</summary>
    [Hle("sceNpCommerce2_FE0EB375", Nid = 0xFE0EB375, Library = "SceNpCommerce2")] int StoreOpFE0EB375(uint reqId, uint kind, uint a, uint b) => InitOr(!(kind == 1 && (a == 0 || b == 0)), reqId);
    /// <summary>(reqId, int kind, a, b, c, d, e, f, g): kind 1 needs a; b..g required.</summary>
    [Hle("sceNpCommerce2_C3F5383C", Nid = 0xC3F5383C, Library = "SceNpCommerce2")]
    int StoreOpC3F5383C(uint reqId, uint kind, uint a, uint b, uint c, uint d, uint e, uint f, uint g) =>
        InitOr(!(kind == 1 && a == 0) && b != 0 && c != 0 && d != 0 && e != 0 && f != 0 && g != 0, reqId);
    /// <summary>(reqId, a, b, c, uint len &lt;= 60, uint mode 1..2): mode 1 needs a and b.</summary>
    [Hle("sceNpCommerce2_4075B119", Nid = 0x4075B119, Library = "SceNpCommerce2")]
    int StoreOp4075B119(uint reqId, uint a, uint b, uint c, uint len, uint mode)
    {
        lock (_lock) if (!_inited) return ERR_LIB_NOT_INITIALIZED;
        if (mode == 0 || mode > 2 || len > 0x3C || (mode == 1 && (a == 0 || b == 0))) return ERR_SYS_INVALID;
        return StoreRequest(reqId);
    }
    /// <summary>(reqId, uint count 1..8, a, b, const entry *list {kind &lt;= 6, ptr, ptr}, uint listCount 1, uint m &lt;= 7).</summary>
    [Hle("sceNpCommerce2_98345A30", Nid = 0x98345A30, Library = "SceNpCommerce2")]
    int StoreOp98345A30(uint reqId, uint count, uint a, uint b, uint list, uint listCount, uint m)
    {
        lock (_lock) if (!_inited) return ERR_LIB_NOT_INITIALIZED;
        if (count == 0 || count > 8 || listCount != 1 || m > 7) return ERR_SYS_INVALID;
        for (uint i = 0; i < listCount; i++)
            if (GuestMemory.R32(list + i * 12) > 6 || GuestMemory.R32(list + i * 12 + 4) == 0 || GuestMemory.R32(list + i * 12 + 8) == 0)
                return ERR_SYS_INVALID;
        return StoreRequest(reqId);
    }
    /// <summary>(reqId, const ticket *t): the ticket's +0x38 must be set (0x8055102A).</summary>
    [Hle("sceNpCommerce2_8B99ED11", Nid = 0x8B99ED11, Library = "SceNpCommerce2")]
    [Hle("sceNpCommerce2_D4D3D6D2", Nid = 0xD4D3D6D2, Library = "SceNpCommerce2")]
    [Hle("sceNpCommerce2_FA3E492B", Nid = 0xFA3E492B, Library = "SceNpCommerce2")]
    int StoreOpTicket(uint reqId, uint t)
    {
        lock (_lock) if (!_inited) return ERR_LIB_NOT_INITIALIZED;
        if (t == 0) return ERR_SYS_INVALID;
        if (GuestMemory.R32(t + 0x38) == 0) return ERR_NO_TICKET;
        return StoreRequest(reqId);
    }
    /// <summary>(reqId, const void *param): two operations, with and without the parameter.</summary>
    [Hle("sceNpCommerce2_F50D7BE4", Nid = 0xF50D7BE4, Library = "SceNpCommerce2")] int StoreOpF50D7BE4(uint reqId, uint p) => StoreRequest(reqId);

    /// <summary>(reqId, a, b, void *out): the store service's request of one kind (7, 4, 5, 12) with a, b and out.</summary>
    [Hle("sceNpCommerce2_2A22BEAF", Nid = 0x2A22BEAF, Library = "SceNpCommerce2")]
    [Hle("sceNpCommerce2_4A36892A", Nid = 0x4A36892A, Library = "SceNpCommerce2")]
    [Hle("sceNpCommerce2_A6CC5E4A", Nid = 0xA6CC5E4A, Library = "SceNpCommerce2")]
    [Hle("sceNpCommerce2_FFFC0101", Nid = 0xFFFC0101, Library = "SceNpCommerce2")]
    int StoreServiceRequest(uint reqId, uint a, uint b, uint outp) => InitOr(a != 0 && b != 0 && outp != 0, reqId);

    /// <summary>(reqId, a, b, void *out, int mode 0..2): as NID_2A22BEAF with the kind from the mode (1, 2, 11).</summary>
    [Hle("sceNpCommerce2_3CA01B22", Nid = 0x3CA01B22, Library = "SceNpCommerce2")]
    [Hle("sceNpCommerce2_6218343E", Nid = 0x6218343E, Library = "SceNpCommerce2")]
    int StoreServiceRequestMode(uint reqId, uint a, uint b, uint outp, int mode)
    {
        lock (_lock) if (!_inited) return ERR_LIB_NOT_INITIALIZED;
        if (mode is < 0 or > 2) return ERR_BAD_VALUE;
        return StoreServiceRequest(reqId, a, b, outp);
    }

    // ---- context operations

    /// <summary>(ctxId): marks the context deleted.</summary>
    [Hle("sceNpCommerce2_1EC16D19", Nid = 0x1EC16D19, Library = "SceNpCommerce2")]
    int DeleteCtx(uint ctxId)
    {
        lock (_lock)
        {
            if (!_inited) return ERR_LIB_NOT_INITIALIZED;
            return _ctx.Remove(ctxId) ? 0 : ERR_CTX_NOT_FOUND;
        }
    }

    /// <summary>(ctxId, void *out 0x20): the account data the context's requests use (np_manager's account data, which
    /// the HLE reports as zeros).</summary>
    [Hle("sceNpCommerce2_2C6028A0", Nid = 0x2C6028A0, Library = "SceNpCommerce2")]
    int GetCtxAccountData(uint ctxId, uint outp)
    {
        lock (_lock)
        {
            if (!_inited) return ERR_LIB_NOT_INITIALIZED;
            if (outp == 0) return ERR_SYS_INVALID;
            if (!_ctx.Contains(ctxId)) return ERR_CTX_NOT_FOUND;
        }
        GuestMemory.Fill(outp, 0, 0x20);
        return 0;
    }

    /// <summary>(srcCtx, dstCtx, void *buf, uint size, uint *len): copies a context's store session to another; no
    /// session is ever created (it needs the store): 0x80550F91.</summary>
    [Hle("sceNpCommerce2_2F0645A6", Nid = 0x2F0645A6, Library = "SceNpCommerce2")]
    int CopyCtxSession(uint src, uint dst, uint buf, uint size)
    {
        lock (_lock)
        {
            if (!_inited) return ERR_LIB_NOT_INITIALIZED;
            if (buf == 0 || size == 0) return ERR_SYS_INVALID;
            return _ctx.Contains(src) ? ERR_NO_SESSION : ERR_CTX_NOT_FOUND;
        }
    }
}
