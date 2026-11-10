using VitaSharp.Core.Memory;
using static VitaSharp.Core.Hle.ShellSvcIpc;

namespace VitaSharp.Core.Hle.Modules;

// np_manager.suprx's requests to the shell's NP service over libshellsvc (FW 3.74, NIDs without public names; methods
// 0xA00xx / 0xB00xx), read in the module. The library passes the arguments to the shell's service object unchanged
// (ShellSvcIpc) and returns the server's answer; the shell itself has a handler table (NID_77DE17D4) for two of them.
// The requests np_manager makes over its own libipmi connection to the NP service (async requests with a request id)
// are not here: the HLE library does not open that connection.

public sealed partial class NpManager
{
    int Svc(uint method, ReadOnlySpan<Arg> inArgs, ReadOnlySpan<Arg> outArgs) => Invoke(Emu, method, inArgs, outArgs, MGR_NOT_INITIALIZED);
    int Svc(uint method, params Arg[] inArgs) => Invoke(Emu, method, inArgs, [], MGR_NOT_INITIALIZED);

    static uint[] Words(uint p, int count)
    {
        var w = new uint[count];
        if (p != 0) for (int i = 0; i < count; i++) w[i] = GuestMemory.R32(p + (uint)i * 4);
        return w;
    }

    [Hle("sceNpManager_0E26EA86", Nid = 0x0E26EA86, Library = "SceNpManager")] int NpSvcA0004(uint a) => Svc(0xA0004, Val(a));
    [Hle("sceNpManager_BD57A0B8", Nid = 0xBD57A0B8, Library = "SceNpManager")] int NpSvcA0001(uint a, uint b) => Svc(0xA0001, Val(a, b));
    /// <summary>(int *out).</summary>
    [Hle("sceNpManager_4C5D2A19", Nid = 0x4C5D2A19, Library = "SceNpManager")] int NpSvcA0003(uint p) => Svc(0xA0003, [], [Buf(p, 4)]);
    [Hle("sceNpManager_1796CD2E", Nid = 0x1796CD2E, Library = "SceNpManager")] int NpSvcB0011() => Svc(0xB0011, Val(0));
    [Hle("sceNpManager_BB384A4D", Nid = 0xBB384A4D, Library = "SceNpManager")] int NpSvcB0022() => Svc(0xB0022, Val(0));
    [Hle("sceNpManager_4E94274C", Nid = 0x4E94274C, Library = "SceNpManager")] int NpSvcB001D() => Svc(0xB001D);
    [Hle("sceNpManager_D4180D28", Nid = 0xD4180D28, Library = "SceNpManager")] int NpSvcB0016() => Svc(0xB0016);

    /// <summary>(void *out 0x40): method 0xB0025 fills 64 bytes (the IPC the game-side library makes; a NULL buffer is
    /// the library's invalid argument).</summary>
    [Hle("sceNpManager_FFBFC540", Nid = 0xFFBFC540, Library = "SceNpManager")]
    int NpSvcB0025(uint p) => p == 0 ? MGR_INVALID_ARGUMENT : Svc(0xB0025, [], [Buf(p, 0x40)]);

    /// <summary>(uint32_t *a, uint8_t b[6]): method 0xB001F fills both.</summary>
    [Hle("sceNpManager_97291707", Nid = 0x97291707, Library = "SceNpManager")]
    int NpSvcB001F(uint a, uint b) => a == 0 || b == 0 ? MGR_INVALID_ARGUMENT : Svc(0xB001F, [], [Buf(a, 4), Buf(b, 6)]);

    /// <summary>(int *out, int in): the shell's handler +8 (with the caller's process id), else method 0xB0026.</summary>
    [Hle("sceNpManager_375A8BC0", Nid = 0x375A8BC0, Library = "SceNpManager")]
    int NpSvcB0026(uint outp, uint v) => HandlerOrSvc(8, 0xB0026, outp, v);

    /// <summary>As NID_375A8BC0: handler +0xC, method 0xB0027.</summary>
    [Hle("sceNpManager_F35CC917", Nid = 0xF35CC917, Library = "SceNpManager")]
    int NpSvcB0027(uint outp, uint v) => HandlerOrSvc(0xC, 0xB0027, outp, v);

    int HandlerOrSvc(uint slot, uint method, uint outp, uint v)
    {
        uint h = _sysHandlers == 0 ? 0 : GuestMemory.R32(_sysHandlers + slot);
        if (h != 0) return CallHandler(h, outp, v, (uint)Emu.Pid);
        return Svc(method, [Val(v)], [Buf(outp, 4)]);
    }

    /// <summary>(int a, int b, const void *p 0x44, const void *q 0x24): method 0xB0013 with (a, b, p == NULL, q == NULL)
    /// and copies of both blocks (unset when NULL).</summary>
    [Hle("sceNpManager_455E05C1", Nid = 0x455E05C1, Library = "SceNpManager")]
    int NpSvcB0013(uint a, uint b, uint p, uint q) =>
        Svc(0xB0013, Val(a, b, p == 0 ? 1u : 0, q == 0 ? 1u : 0), Val(Words(p, 0x11)), Val(Words(q, 9)));

    /// <summary>(void *a 0x44, void *b 0x24, void *c 8): method 0xB001A with which of them are given and the three
    /// blocks (a local buffer for each NULL one).</summary>
    [Hle("sceNpManager_6CDB2AEC", Nid = 0x6CDB2AEC, Library = "SceNpManager")]
    int NpSvcB001A(uint a, uint b, uint c) =>
        Svc(0xB001A, Val(a != 0 ? 1u : 0, b != 0 ? 1u : 0, c != 0 ? 1u : 0),
            a != 0 ? Buf(a, 0x44) : Val(new uint[0x11]), b != 0 ? Buf(b, 0x24) : Val(new uint[9]), c != 0 ? Buf(c, 8) : Val(0, 0));

    /// <summary>(const void *data 0x400, const void *id 0x20): method 0xA0008; nothing (0) without data, a zero id when
    /// NULL.</summary>
    [Hle("sceNpManager_72936716", Nid = 0x72936716, Library = "SceNpManager")]
    int NpSvcA0008(uint data, uint id) => data == 0 ? 0 : Svc(0xA0008, Buf(data, 0x400), id != 0 ? Buf(id, 0x20) : Val(new uint[8]));

    /// <summary>(const char a[0x80], const char b[0x80], const void *id 0x20): method 0xA0007 (a zero id when NULL).</summary>
    [Hle("sceNpManager_9554802B", Nid = 0x9554802B, Library = "SceNpManager")]
    int NpSvcA0007(uint a, uint b, uint id) => Svc(0xA0007, Buf(a, 0x80), Buf(b, 0x80), id != 0 ? Buf(id, 0x20) : Val(new uint[8]));

    // ---- the library's own state

    /// <summary>(void): whether sceNpInit ran.</summary>
    [Hle("sceNpManager_A6964218", Nid = 0xA6964218, Library = "SceNpManager")]
    int NpIsInitialized() => NpInited ? 1 : 0;

    /// <summary>(SceNpCommunicationConfig *out): the configuration sceNpInit kept (pointers to the library's copies of
    /// the id 0xC, passphrase 0x80 and signature 0xA0; NULL for those not given).</summary>
    [Hle("sceNpManager_96DF1B76", Nid = 0x96DF1B76, Library = "SceNpManager")]
    int NpGetCommunicationConfig(uint outp)
    {
        lock (_lock)
        {
            if (!_npInited) return ERR_NOT_INITIALIZED;
            GuestMemory.W32(outp, _cfgId);
            GuestMemory.W32(outp + 4, _cfgPass);
            GuestMemory.W32(outp + 8, _cfgSign);
            return 0;
        }
    }

    uint _cfgBlock, _cfgId, _cfgPass, _cfgSign;

    /// <summary>sceNpInit keeps copies of the configuration (np_manager: 0xC id, 0x80 passphrase, 0xA0 signature).</summary>
    void KeepConfig(uint config)
    {
        _cfgId = _cfgPass = _cfgSign = 0;
        if (config == 0) return;
        if (_cfgBlock == 0) _cfgBlock = (Emu?.Memory ?? GuestMemory.Instance)!.Alloc(0x200, "SceNpManager config");
        uint id = GuestMemory.R32(config), pass = GuestMemory.R32(config + 4), sign = GuestMemory.R32(config + 8);
        if (id != 0) { GuestMemory.Copy(_cfgBlock, id, 0xC); _cfgId = _cfgBlock; }
        if (pass != 0) { GuestMemory.Copy(_cfgBlock + 0x10, pass, 0x80); _cfgPass = _cfgBlock + 0x10; }
        if (sign != 0) { GuestMemory.Copy(_cfgBlock + 0x90, sign, 0xA0); _cfgSign = _cfgBlock + 0x90; }
    }

    /// <summary>(void): ends the system-side initialisation (NID_C89E0B94).</summary>
    [Hle("sceNpManager_C253AE5B", Nid = 0xC253AE5B, Library = "SceNpManager")]
    int sceNpManagerTermForSystem()
    {
        lock (_lock)
        {
            if (!_sysInited) return ERR_NOT_INITIALIZED;
            _sysInited = false;
            if (_npInited) return 0;
        }
        IpmiClose();
        return 0;
    }
}
