using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

// The last two system-side exports of libnetctl.suprx (FW 3.74, NIDs without public names), read in the module: the
// ad hoc authentication server and the UPnP port mapping of the authentication client context. Both start with the
// library's own checks; past them the real library needs a network the emulated connection does not have (an ad hoc
// network, a UPnP gateway on the LAN), and ends with the error it gives there.

public sealed partial class Net
{
    const int NETCTL_ADHOC_NO_COMM_ID = unchecked((int)0x80412119), NETCTL_NOT_CONNECTED = unchecked((int)0x80412108),
        NETAUTH_NO_RESPONSE = unchecked((int)0x80414187);

    /// <summary>(ctx *c, int a, uint16 port1, uint16 port2, void **info, uint *tried, uint *done): maps the
    /// authentication ports on the LAN's UPnP gateway. The two step masks start at 0; the context must exist and be
    /// initialised. With an IP address the library searches the LAN for a gateway (SSDP): the step is marked in *tried,
    /// and in *done only when a gateway answered. None answers in the emulated network: the library's no-response
    /// error.</summary>
    [Hle("sceNetCtl_9CB6FEE1", Nid = 0x9CB6FEE1, Library = "SceNetCtl")]
    int NetAuthCtxMapPorts(uint ctx, uint a, uint port1, uint port2, uint info, uint tried, uint done)
    {
        if (tried != 0) GuestMemory.W32(tried, 0);
        if (done != 0) GuestMemory.W32(done, 0);
        if (ctx == 0) return NETAUTH_INVALID_ADDR;
        if (GuestMemory.Read<byte>(ctx) == 0) return NETAUTH_NOT_INITIALIZED;
        GuestMemory.W32(ctx + 0x12A4, 0);
        // the raw infrastructure state (its read fails before sceNetCtlInit) must be 4, IP obtained, as it always is here
        if (!_netctlInited) return NETCTL_NOT_INITIALIZED;
        if (tried != 0) GuestMemory.W32(tried, GuestMemory.R32(tried) | 1);
        return NETAUTH_NO_RESPONSE;
    }

    /// <summary>(const char *name?, int priority, int stackSize, const uint cfg[5], int peers, int flags,
    /// const SceNpCommunicationId *commId): starts the ad hoc authentication server (its sockets on the ad hoc
    /// network, a receive/send pair and the "SceNetAdhocAuth" thread). The library must be initialised and a
    /// communication id given; the emulated console is never in an ad hoc network, so it is not connected.</summary>
    [Hle("sceNetCtl_1172C5FC", Nid = 0x1172C5FC, Library = "SceNetCtl")]
    int NetCtlAdhocAuthStart(uint name, int priority, uint stackSize, uint cfg, int peers, uint flags, uint commId)
    {
        if (!_netctlInited) return NETCTL_NOT_INITIALIZED;
        if (commId == 0) return NETCTL_ADHOC_NO_COMM_ID;
        return NETCTL_NOT_CONNECTED;
    }
}
