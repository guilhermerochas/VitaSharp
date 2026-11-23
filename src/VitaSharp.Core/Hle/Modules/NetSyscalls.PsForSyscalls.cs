using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

// The rest of SceNetPsForSyscalls as libnet.suprx 3.74 calls it: calls with up to four arguments pass them in
// registers unchanged; Recvfrom and Sysctl pack six words into a block, Setsockopt/Getsockopt five words, EpollWait
// five (the fifth 0). Each answers like the kernel (-errno on failure) over the HLE socket table of SceNet.
public sealed partial class Net
{
    static uint W(uint p, int i) => GuestMemory.R32(p + (uint)i * 4);

    [Hle] int sceNetSyscallBind(int s, uint addr, uint len) => Kerr(sceNetBindThroughEntry(s, addr, len));
    [Hle] int sceNetSyscallListen(int s, int backlog) => Kerr(sceNetListenThroughEntry(s, backlog));
    [Hle] int sceNetSyscallAccept(int s, uint addr, uint len) => Kerr(sceNetAcceptThroughEntry(s, addr, len));
    [Hle] int sceNetSyscallConnect(int s, uint addr, uint len) => Kerr(sceNetConnectThroughEntry(s, addr, len));
    [Hle] int sceNetSyscallShutdown(int s, int how) => Kerr(sceNetShutdownThroughEntry(s, how));
    [Hle] int sceNetSyscallGetsockname(int s, uint addr, uint len) => Kerr(sceNetGetsocknameThroughEntry(s, addr, len));
    [Hle] int sceNetSyscallGetpeername(int s, uint addr, uint len) => Kerr(sceNetGetpeernameThroughEntry(s, addr, len));
    [Hle] int sceNetSyscallSendmsg(int s, uint msg, int flags) => Kerr(sceNetSendmsgThroughEntry(s, msg, flags));
    [Hle] int sceNetSyscallRecvmsg(int s, uint msg, int flags) => Kerr(sceNetRecvmsgThroughEntry(s, msg, flags));
    [Hle] int sceNetSyscallSocketAbort(int s, int flags) => Kerr(sceNetSocketAbortThroughEntry(s, flags));
    [Hle] int sceNetSyscallGetSockinfo(int s, uint info, int n, int flags) => Kerr(sceNetGetSockInfoThroughEntry(s, info, n, flags));

    /// <summary>(const struct { int s; void *buf; uint len; int flags; SceNetSockaddr *from; uint *fromlen; } *p).</summary>
    [Hle]
    int sceNetSyscallRecvfrom(uint p)
    {
        if (p == 0)
        {
            return -14; // EFAULT
        }

        return Kerr(sceNetRecvfromThroughEntry((int)W(p, 0), W(p, 1), W(p, 2), (int)W(p, 3), W(p, 4), W(p, 5)));
    }

    /// <summary>(const struct { int s; int level; int name; const void *val; uint len; } *p).</summary>
    [Hle]
    int sceNetSyscallSetsockopt(uint p)
    {
        if (p == 0)
        {
            return -14;
        }

        return Kerr(sceNetSetsockoptThroughEntry((int)W(p, 0), (int)W(p, 1), (int)W(p, 2), W(p, 3), W(p, 4)));
    }

    /// <summary>(const struct { int s; int level; int name; void *val; uint *len; } *p).</summary>
    [Hle]
    int sceNetSyscallGetsockopt(uint p)
    {
        if (p == 0)
        {
            return -14;
        }

        return Kerr(sceNetGetsockoptThroughEntry((int)W(p, 0), (int)W(p, 1), (int)W(p, 2), W(p, 3), W(p, 4)));
    }

    [Hle] int sceNetSyscallEpollCreate(string name, int flags) => Kerr(sceNetEpollCreate(name, flags));
    [Hle] int sceNetSyscallEpollCtl(int eid, int op, int id, uint ev) => Kerr(sceNetEpollControlThroughEntry(eid, op, id, ev));
    [Hle] int sceNetSyscallEpollClose(int eid) => Kerr(sceNetEpollDestroyThroughEntry(eid));
    [Hle] int sceNetSyscallEpollAbort(int eid, int flags) => Kerr(sceNetEpollAbortThroughEntry(eid, flags));

    /// <summary>(const struct { int eid; SceNetEpollEvent *events; int max; int timeout; int 0; } *p).</summary>
    [Hle]
    int sceNetSyscallEpollWait(uint p)
    {
        if (p == 0)
        {
            return -14;
        }

        return Kerr(sceNetEpollWaitThroughEntry((int)W(p, 0), W(p, 1), (int)W(p, 2), (int)W(p, 3)));
    }

    [Hle] int sceNetSyscallDumpCreate(string name, int len, int flags) => Kerr(sceNetDumpCreate(name, len, flags));
    [Hle] int sceNetSyscallDumpRead(int id, uint buf, int len, uint pflags) => Kerr(sceNetDumpReadThroughEntry(id, buf, len, pflags));
    [Hle] int sceNetSyscallDumpClose(int id) => Kerr(sceNetDumpDestroyThroughEntry(id));
    [Hle] int sceNetSyscallDumpAbort(int id, int flags) => Kerr(sceNetDumpAbortThroughEntry(id, flags));

    // ---- generic descriptors: SceNetInternal's handles on kernel network objects (statistics, interfaces)
    readonly HashSet<int> _descriptors = new();
    int _nextDescriptor = 0x100;

    /// <summary>(const char *name, int flags): a new network descriptor (the kernel's id; ids share the socket
    /// number space, so these start apart from the sockets).</summary>
    [Hle]
    int sceNetSyscallDescriptorCreate(string name, int flags)
    {
        lock (_netLock)
        {
            int id = _nextDescriptor++;
            _descriptors.Add(id);
            return id;
        }
    }

    [Hle]
    int sceNetSyscallDescriptorClose(int id)
    {
        lock (_netLock)
        {
            return _descriptors.Remove(id) ? 0 : -9; // EBADF
        }
    }

    /// <summary>(int id, int op, void *arg, int len): controls on a descriptor; the emulated kernel keeps none of
    /// the objects behind them, so a known descriptor accepts the request, an unknown one is EBADF.</summary>
    [Hle]
    int sceNetSyscallDescriptorCtl(int id, int op, uint arg, int len)
    {
        lock (_netLock)
        {
            return _descriptors.Contains(id) ? 0 : -9;
        }
    }

    /// <summary>(const struct { const int *name; uint namelen; void *old; uint *oldlen; const void *new; uint newlen; } *p):
    /// the BSD sysctl of the network stack. The emulated kernel has no MIB tree: every name is ENOENT, a bad block
    /// or a name longer than CTL_MAXNAME (24) EINVAL, like the kernel's sysctl_lookup without the node.</summary>
    [Hle]
    int sceNetSyscallSysctl(uint p)
    {
        if (p == 0)
        {
            return -14;
        }

        uint name = W(p, 0), namelen = W(p, 1);
        if (name == 0 || namelen is 0 or > 24)
        {
            return -22; // EINVAL
        }

        return -2; // ENOENT
    }
}
