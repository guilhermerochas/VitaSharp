using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

// The rest of SceSulpha (libsulpha.suprx, FW 3.74): the NGS audio profiler's agent, which streams the session's nodes,
// events and bookmarks to the Sulpha tool on a PC over the network or a host file. No tool is ever connected, so the
// library refuses what needs the connection or a node/event of this session (0x804D0002 not connected, 0x804D0005
// unknown handle) after its argument checks (0x804D0004), as libsulpha does when the PC side is absent.
public sealed partial class SystemFallback
{
    const int SULPHA_NOT_CONNECTED = unchecked((int)0x804D0002);

    /// <summary>(const config *c, uint *size): the work memory for the config: the config's buffer size.</summary>
    [Hle("sceSulphaGetNeededMemory", Library = "SceSulpha")]
    int SulphaNeededMemory(uint config, uint size)
    {
        if (config == 0 || size == 0) return SULPHA_INVALID;
        GuestMemory.W32(size, GuestMemory.R32(config + 4) + GuestMemory.R32(config + 8));
        return 0;
    }

    [Hle("sceSulphaNetworkInit", Library = "SceSulpha", Default = true)] int SulphaNetworkInit() => SULPHA_NOT_CONNECTED;
    [Hle("sceSulphaNetworkShutdown", Library = "SceSulpha", Default = true)] int SulphaNetworkShutdown() => SULPHA_NOT_CONNECTED;
    [Hle("sceSulphaFileConnect", Library = "SceSulpha", Default = true)] int SulphaFileConnect(uint path) => path == 0 ? SULPHA_INVALID : SULPHA_NOT_CONNECTED;
    [Hle("sceSulphaFileDisconnect", Library = "SceSulpha", Default = true)] int SulphaFileDisconnect() => SULPHA_NOT_CONNECTED;
    [Hle("sceSulphaAgentsGetNeededMemory", Library = "SceSulpha", Default = true)]
    int SulphaAgentsNeededMemory(uint count, uint size) => size == 0 ? SULPHA_INVALID : SULPHA_NOT_CONNECTED;
    [Hle("sceSulphaAgentsRegister", Library = "SceSulpha", Default = true)]
    int SulphaAgentsRegister(uint config, uint handle, uint buf, uint size) => config == 0 || handle == 0 ? SULPHA_INVALID : SULPHA_NOT_CONNECTED;
    [Hle("sceSulphaAgentsUnregister", Library = "SceSulpha", Default = true)] int SulphaAgentsUnregister(uint handle, uint count) => handle == 0 ? SULPHA_INVALID : SULPHA_NOT_CONNECTED;
    [Hle("sceSulphaGetAgent", Library = "SceSulpha", Default = true)] int SulphaGetAgent(uint a, uint b, uint c, uint agent) => agent == 0 ? SULPHA_INVALID : SULPHA_NOT_CONNECTED;
    [Hle("sceSulphaNodeNew", Library = "SceSulpha", Default = true)] int SulphaNodeNew(uint parent, uint name, uint node) => node == 0 ? SULPHA_INVALID : SULPHA_NOT_CONNECTED;
    [Hle("sceSulphaEventNew", Library = "SceSulpha", Default = true)] int SulphaEventNew(uint node, uint name, uint type, uint ev) => ev == 0 ? SULPHA_INVALID : SULPHA_NOT_CONNECTED;
    [Hle("sceSulphaSetBookmark", Library = "SceSulpha", Default = true)] int SulphaSetBookmark(uint name, uint a) => SULPHA_NOT_CONNECTED;
    [Hle("sceSulphaGetTimestamp", Library = "SceSulpha", Default = true)] int SulphaGetTimestamp(uint ts, uint a) => ts == 0 ? SULPHA_INVALID : SULPHA_NOT_CONNECTED;
    /// <summary>(int level, const char *file, int line, const char *fmt, ...): the agent's log hook: dropped below the
    /// level set (nothing to send it to).</summary>
    [Hle("sceSulphaLogHandler", Library = "SceSulpha", Default = true)] void SulphaLogHandler(int level, uint file, int line, uint fmt, uint a) { }
}

// SceNgsInternal's Sulpha queries (bootimage SceNgs): the module list the profiler shows, module descriptions and the
// synth update hook; the HLE's NGS has no debug block for them (0x804A0001 for a missing argument).
public sealed partial class Ngs
{
    const int NGS_DBG_INVALID = unchecked((int)0x804A0001);
    uint _sulphaSynthCallback, _sulphaSynthArg;

    [Hle(Default = true)]
    int sceNgsSulphaGetModuleListInternal(uint ids, uint max, uint count)
    {
        if (ids == 0 || count == 0) return NGS_DBG_INVALID;
        GuestMemory.W32(count, 0);
        return 0;
    }
    [Hle(Default = true)] int sceNgsSulphaQueryModuleInternal(uint id, uint info) => info == 0 ? NGS_DBG_INVALID : NGS_DBG_INVALID;
    [Hle(Default = true)] int sceNgsSulphaGetInfoInternal(uint info) { if (info == 0) return NGS_DBG_INVALID; GuestMemory.Fill(info, 0, 0x10); return 0; }
    [Hle(Default = true)] int sceNgsSulphaSetSynthUpdateCallbackInternal(uint cb, uint arg) { _sulphaSynthCallback = cb; _sulphaSynthArg = arg; return 0; }
    [Hle(Default = true)]
    int sceNgsSulphaGetSynthUpdateCallbackInternal(uint cb, uint arg)
    {
        if (cb == 0 || arg == 0) return NGS_DBG_INVALID;
        GuestMemory.W32(cb, _sulphaSynthCallback);
        GuestMemory.W32(arg, _sulphaSynthArg);
        return 0;
    }
}
