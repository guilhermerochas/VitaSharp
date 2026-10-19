using System.Buffers.Binary;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// SceNet socket, epoll, resolver, dump and emulation objects in the offline profile: objects are real (ids, options,
/// registrations), nothing ever arrives from the network, and blocking calls wait for their timeout or until they are
/// aborted/closed instead of spinning. Every failure also writes errno (code &amp; 0xFF) to the sceNetErrnoLoc slot.
/// </summary>
public sealed partial class Net
{
    internal const int EINTR = unchecked((int)0x80410104), EBADF = unchecked((int)0x80410109), EAGAIN = unchecked((int)0x80410123),
        EINVAL = unchecked((int)0x80410116), EBUSY = unchecked((int)0x80410110), EEXIST = unchecked((int)0x80410111),
        ENOENT = unchecked((int)0x80410102), ENOTINIT = unchecked((int)0x804101C8), RESOLVER_ENODNS = unchecked((int)0x804101E1);

    const int SOL_SOCKET = 0xFFFF, SO_SNDTIMEO = 0x1005, SO_RCVTIMEO = 0x1006, SO_TYPE = 0x1008, SO_NBIO = 0x1100;
    const int SOCK_STREAM = 1, SOCK_STREAM_P2P = 10, MSG_DONTWAIT = 0x80;

    sealed class NetSocket
    {
        public int Id, Domain, Type, Protocol;
        public string Name = "";
        public bool Nbio, Closed;
        public uint RcvTimeoUs, SndTimeoUs;
        public byte[]? Local;
        public int AbortGen;
        public bool AbortRecvSticky, AbortSendSticky;
        public readonly Dictionary<(int Level, int Opt), byte[]> Options = new();
    }

    sealed class NetEpoll
    {
        public int Id;
        public string Name = "";
        public readonly Dictionary<int, (uint Events, ulong Data)> Entries = new();
        public int AbortGen;
        public bool AbortSticky, Destroyed;
    }

    sealed class NetDump
    {
        public int Id, AbortGen;
        public bool AbortSticky, Destroyed;
    }

    readonly object _netLock = new();
    bool _netInited;
    uint _errnoLoc;
    int _nextSock = 1, _nextEpoll = 1, _nextResolver = 1, _nextDump = 1;
    readonly Dictionary<int, NetSocket> _sockets = new();
    readonly Dictionary<int, NetEpoll> _epolls = new();
    readonly HashSet<int> _resolvers = new();
    readonly Dictionary<int, NetDump> _dumps = new();
    readonly byte[][] _emulation = [new byte[0x200], new byte[0x200]];
    uint _dns0, _dns1;

    /// <summary>Records errno for a failed call and returns the code (Vita3K ret_net_errno behaviour).</summary>
    int NetErr(int code)
    {
        if (_errnoLoc != 0) GuestMemory.W32(_errnoLoc, (uint)(code & 0xFF));
        return code;
    }

    [Hle] uint sceNetErrnoLoc() { if (_errnoLoc == 0) _errnoLoc = Emu.Memory.Alloc(16, "net errno", MemoryRegion.User, 16); return _errnoLoc; }

    /// <summary>SceNetInitParam (0xC): +0 memory, +4 size, +8 flags.</summary>
    [Hle]
    int sceNetInit(uint param)
    {
        lock (_netLock)
        {
            if (_netInited) { Log.Warn("sceNetInit: already initialised"); return NetErr(EBUSY); }
            if (param == 0 || GuestMemory.R32(param) == 0 || (int)GuestMemory.R32(param + 4) < 0x4000 || GuestMemory.R32(param + 8) != 0)
                return NetErr(EINVAL);
            _netInited = true;
            return 0;
        }
    }

    [Hle]
    int sceNetTerm()
    {
        lock (_netLock)
        {
            if (!_netInited) return NetErr(ENOTINIT);
            _netInited = false;
            foreach (var s in _sockets.Values) s.Closed = true;
            foreach (var e in _epolls.Values) e.Destroyed = true;
            foreach (var d in _dumps.Values) d.Destroyed = true;
            _sockets.Clear(); _epolls.Clear(); _dumps.Clear(); _resolvers.Clear();
            Monitor.PulseAll(_netLock);
            return 0;
        }
    }

    // ------------------------------------------------------------------ sockets

    NetSocket? Sock(int s) => _sockets.GetValueOrDefault(s);

    [Hle]
    int sceNetSocket(string name, int domain, int type, int protocol)
    {
        lock (_netLock)
        {
            var s = new NetSocket { Id = _nextSock++, Name = name, Domain = domain, Type = type, Protocol = protocol };
            _sockets[s.Id] = s;
            return s.Id;
        }
    }

    [Hle]
    int sceNetSocketClose(int s)
    {
        lock (_netLock)
        {
            if (!_sockets.Remove(s, out var so)) return NetErr(EBADF);
            so.Closed = true;
            foreach (var e in _epolls.Values) e.Entries.Remove(s);
            Monitor.PulseAll(_netLock);
            return 0;
        }
    }

    [Hle]
    int sceNetSocketAbort(int s, int flags)
    {
        lock (_netLock)
        {
            if (s < 0 || flags is < 0 or > 3) return NetErr(EINVAL);
            var so = Sock(s);
            if (so == null) return NetErr(EBADF);
            so.AbortGen++;
            if ((flags & 1) != 0) so.AbortRecvSticky = true;
            if ((flags & 2) != 0) so.AbortSendSticky = true;
            Monitor.PulseAll(_netLock);
            return 0;
        }
    }

    int CheckSock(int s) { lock (_netLock) return Sock(s) == null ? NetErr(EBADF) : 0; }

    [Hle] int sceNetShutdown(int s, int how) => CheckSock(s);
    [Hle] int sceNetListen(int s, int backlog) => CheckSock(s);

    [Hle]
    int sceNetBind(int s, uint addr, uint len)
    {
        lock (_netLock)
        {
            var so = Sock(s);
            if (so == null) return NetErr(EBADF);
            if (addr == 0) return NetErr(EINVAL);
            so.Local = GuestMemory.Span(addr, (int)Math.Min(Math.Max(len, 16u), 16u)).ToArray();
            return 0;
        }
    }

    [Hle] int sceNetAccept(int s, uint addr, uint len) { int r = CheckSock(s); return r < 0 ? r : NetErr(ERR_NET_ENOTCONN); }
    [Hle] int sceNetConnect(int s, uint addr, uint len) { int r = CheckSock(s); return r < 0 ? r : NetErr(ERR_NET_ENETUNREACH); }
    [Hle] int sceNetSend(int s, uint buf, uint len, int flags) { int r = CheckSock(s); return r < 0 ? r : NetErr(ERR_NET_ENOTCONN); }
    [Hle] int sceNetSendto(int s, uint buf, uint len, int flags, uint to, uint tolen) { int r = CheckSock(s); return r < 0 ? r : NetErr(ERR_NET_ENETUNREACH); }

    /// <summary>SceNetMsghdr (0x1C): +0 name, +4 namelen, +8 iov, +C iovlen, +10 control, +14 controllen, +18 flags.</summary>
    [Hle]
    int sceNetSendmsg(int s, uint msg, int flags)
    {
        if (msg == 0 || (int)GuestMemory.R32(msg + 0xC) < 0) return NetErr(EINVAL);
        return sceNetSendto(s, 0, 0, flags, GuestMemory.R32(msg), GuestMemory.R32(msg + 4));
    }

    /// <summary>Offline nothing ever arrives: non-blocking → EAGAIN; blocking → waits for SO_RCVTIMEO (EAGAIN), an abort
    /// (EINTR) or close (EBADF); a stream socket is never connected (ENOTCONN).</summary>
    int Receive(int s, int flags)
    {
        lock (_netLock)
        {
            var so = Sock(s);
            if (so == null) return NetErr(EBADF);
            if (so.Type is SOCK_STREAM or SOCK_STREAM_P2P) return NetErr(ERR_NET_ENOTCONN);
            if (so.AbortRecvSticky) return NetErr(EINTR);
            if (so.Nbio || (flags & MSG_DONTWAIT) != 0) return NetErr(EAGAIN);
            int gen = so.AbortGen;
            int w = Kernel.Wait.Until(_netLock, () => so.Closed || so.AbortGen != gen, so.RcvTimeoUs == 0 ? null : so.RcvTimeoUs,
                what: "net recv");
            if (so.Closed) return NetErr(EBADF);
            if (so.AbortGen != gen) return NetErr(EINTR);
            return NetErr(w == SceError.WAIT_TIMEOUT ? EAGAIN : EINTR);
        }
    }

    [Hle] int sceNetRecv(int s, uint buf, uint len, int flags) => Receive(s, flags);
    [Hle] int sceNetRecvfrom(int s, uint buf, uint len, int flags, uint from, uint fromlen) => Receive(s, flags);

    [Hle]
    int sceNetRecvmsg(int s, uint msg, int flags)
    {
        if (msg == 0) return NetErr(EINVAL);
        int r = Receive(s, flags);
        GuestMemory.W32(msg + 0x18, 0);
        return r;
    }

    [Hle]
    int sceNetSetsockopt(int s, int level, int optname, uint optval, uint optlen)
    {
        lock (_netLock)
        {
            var so = Sock(s);
            if (so == null) return NetErr(EBADF);
            if (optval == 0 || optlen < 4) return NetErr(EINVAL);
            var bytes = GuestMemory.Span(optval, (int)Math.Min(optlen, 256u)).ToArray();
            if (level == SOL_SOCKET)
            {
                uint v = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
                switch (optname)
                {
                    case SO_NBIO: so.Nbio = v != 0; break;
                    case SO_RCVTIMEO: so.RcvTimeoUs = v; break;
                    case SO_SNDTIMEO: so.SndTimeoUs = v; break;
                }
            }
            so.Options[(level, optname)] = bytes;
            return 0;
        }
    }

    [Hle]
    int sceNetGetsockopt(int s, int level, int optname, uint optval, uint optlen)
    {
        lock (_netLock)
        {
            var so = Sock(s);
            if (so == null) return NetErr(EBADF);
            if (optval == 0 || optlen == 0) return NetErr(EINVAL);
            uint cap = GuestMemory.R32(optlen);
            byte[] v;
            if (level == SOL_SOCKET && optname == SO_TYPE) v = BitConverter.GetBytes(so.Type);
            else if (level == SOL_SOCKET && optname == SO_NBIO) v = BitConverter.GetBytes(so.Nbio ? 1 : 0);
            else if (!so.Options.TryGetValue((level, optname), out v!)) v = new byte[4];
            int n = (int)Math.Min(cap, (uint)v.Length);
            v.AsSpan(0, n).CopyTo(GuestMemory.Span(optval, n));
            GuestMemory.W32(optlen, (uint)n);
            return 0;
        }
    }

    [Hle]
    int sceNetGetsockname(int s, uint addr, uint len)
    {
        lock (_netLock)
        {
            var so = Sock(s);
            if (so == null) return NetErr(EBADF);
            if (addr == 0) return NetErr(EINVAL);
            GuestMemory.Fill(addr, 0, 16);
            if (so.Local != null) so.Local.CopyTo(GuestMemory.Span(addr, so.Local.Length));
            else { GuestMemory.Write<byte>(addr, 16); GuestMemory.Write<byte>(addr + 1, 2); } // AF_INET, any address
            if (len != 0) GuestMemory.W32(len, 16);
            return 0;
        }
    }

    [Hle]
    int sceNetGetpeername(int s, uint name, uint namelen)
    {
        lock (_netLock)
        {
            if (Sock(s) == null) return NetErr(EBADF);
            if (name == 0 || namelen == 0) return NetErr(EINVAL);
            return NetErr(ERR_NET_ENOTCONN);
        }
    }

    [Hle] int sceNetGetSockInfo(int s, uint info, int n, int flags) => 0;

    /// <summary>SceNetFdSet (0x80): bit id set for every open socket id &lt; 1024. Returns the number of sockets.</summary>
    [Hle]
    int sceNetGetSockIdInfo(uint fds, int sockinfoflags, int flags)
    {
        if (fds == 0) return NetErr(EINVAL);
        lock (_netLock)
        {
            GuestMemory.Fill(fds, 0, 0x80);
            int n = 0;
            foreach (int id in _sockets.Keys)
            {
                if (id >= 1024) continue;
                uint w = fds + (uint)(id >> 5) * 4;
                GuestMemory.W32(w, GuestMemory.R32(w) | 1u << (id & 31));
                n++;
            }
            return n;
        }
    }

    // ------------------------------------------------------------------ epoll

    [Hle]
    int sceNetEpollCreate(string name, int flags)
    {
        lock (_netLock)
        {
            var e = new NetEpoll { Id = _nextEpoll++, Name = name };
            _epolls[e.Id] = e;
            return e.Id;
        }
    }

    [Hle]
    int sceNetEpollDestroy(int eid)
    {
        lock (_netLock)
        {
            if (!_epolls.Remove(eid, out var e)) return NetErr(EBADF);
            e.Destroyed = true;
            Monitor.PulseAll(_netLock);
            return 0;
        }
    }

    /// <summary>SceNetEpollEvent (0x20): +0 events, +4 reserved, +8 system[4], +0x18 data (8 bytes).</summary>
    [Hle]
    int sceNetEpollControl(int eid, int op, int id, uint ev)
    {
        lock (_netLock)
        {
            if (!_epolls.TryGetValue(eid, out var e)) return NetErr(EBADF);
            switch (op)
            {
                case 1: // ADD
                    if (ev == 0) return NetErr(EINVAL);
                    if (!_sockets.ContainsKey(id) && !_resolvers.Contains(id)) return NetErr(EBADF);
                    if (e.Entries.ContainsKey(id)) return NetErr(EEXIST);
                    e.Entries[id] = (GuestMemory.R32(ev), GuestMemory.Read<ulong>(ev + 0x18));
                    return 0;
                case 2: // MOD
                    if (ev == 0) return NetErr(EINVAL);
                    if (!e.Entries.ContainsKey(id)) return NetErr(ENOENT);
                    e.Entries[id] = (GuestMemory.R32(ev), GuestMemory.Read<ulong>(ev + 0x18));
                    return 0;
                case 3: // DEL
                    return e.Entries.Remove(id) ? 0 : NetErr(ENOENT);
                default:
                    return NetErr(EINVAL);
            }
        }
    }

    /// <summary>Timeout in microseconds; negative = forever, 0 = poll. Offline no registered socket ever becomes ready,
    /// so the wait ends on timeout (0 events) or on abort/destroy (EINTR).</summary>
    [Hle("sceNetEpollWait")]
    [Hle("sceNetEpollWaitCB")]
    int sceNetEpollWait(int eid, uint events, int maxevents, int timeout)
    {
        lock (_netLock)
        {
            if (!_epolls.TryGetValue(eid, out var e)) return NetErr(EBADF);
            if (maxevents <= 0 || events == 0) return NetErr(EINVAL);
            if (e.AbortSticky) return NetErr(EINTR);
            if (timeout == 0) return 0;
            int gen = e.AbortGen;
            int w = Kernel.Wait.Until(_netLock, () => e.Destroyed || e.AbortGen != gen, timeout < 0 ? null : timeout, what: "net epoll");
            if (e.Destroyed || e.AbortGen != gen) return NetErr(EINTR);
            return w == SceError.WAIT_TIMEOUT ? 0 : NetErr(EINTR);
        }
    }

    [Hle]
    int sceNetEpollAbort(int eid, int flags)
    {
        lock (_netLock)
        {
            if (!_epolls.TryGetValue(eid, out var e)) return NetErr(EBADF);
            if (flags is < 0 or > 1) return NetErr(EINVAL);
            e.AbortGen++;
            if (flags == 1) e.AbortSticky = true;
            Monitor.PulseAll(_netLock);
            return 0;
        }
    }

    // ------------------------------------------------------------------ resolver (no DNS offline)

    [Hle] int sceNetResolverCreate(string name, uint param, int flags) { lock (_netLock) { int id = _nextResolver++ | 0x10000; _resolvers.Add(id); return id; } }
    [Hle] int sceNetResolverDestroy(int rid) { lock (_netLock) return _resolvers.Remove(rid) ? 0 : NetErr(EBADF); }
    int CheckResolver(int rid) { lock (_netLock) return _resolvers.Contains(rid) ? 0 : NetErr(EBADF); }

    [Hle]
    int sceNetResolverStartNtoa(int rid, string host, uint addr, int timeout, int retry, int flags)
    {
        int r = CheckResolver(rid);
        return r < 0 ? r : NetErr(RESOLVER_ENODNS);
    }

    [Hle]
    int sceNetResolverStartAton(int rid, uint addr, uint host, int len, int timeout, int retry, int flags)
    {
        int r = CheckResolver(rid);
        return r < 0 ? r : NetErr(RESOLVER_ENODNS);
    }

    [Hle] int sceNetResolverGetError(int rid, Ptr<int> err) { err.TrySet(RESOLVER_ENODNS); return 0; }

    [Hle]
    int sceNetResolverAbort(int rid, int flags)
    {
        if (flags is < 0 or > 3) return NetErr(EINVAL);
        return CheckResolver(rid); // resolutions fail at once offline: nothing in flight to abort
    }

    // ------------------------------------------------------------------ DNS / misc

    /// <summary>SceNetDnsInfo (8): dns_addr[2]. Stored; offline the resolver fails anyway.</summary>
    [Hle]
    int sceNetSetDnsInfo(uint info, int flags)
    {
        if (info == 0 || flags != 0) return NetErr(EINVAL);
        _dns0 = GuestMemory.R32(info); _dns1 = GuestMemory.R32(info + 4);
        return 0;
    }

    [Hle] int sceNetClearDnsCache(int flags) => flags != 0 ? NetErr(EINVAL) : 0;
    [Hle] ulong sceNetHtonll(ulong v) => BinaryPrimitives.ReverseEndianness(v);
    [Hle] ulong sceNetNtohll(ulong v) => BinaryPrimitives.ReverseEndianness(v);

    [Hle]
    int sceNetShowIfconfig(uint p, int b)
    {
        Log.Info("net: wlan0 inet 192.168.0.10 netmask 255.255.255.0 mtu 1500 (offline)");
        return 0;
    }

    [Hle] int sceNetShowRoute() { Log.Info("net: default via 192.168.0.1 dev wlan0 (offline)"); return 0; }

    /// <summary>"xx:xx:xx:xx:xx:xx" (lower-case hex) + NUL.</summary>
    [Hle]
    int sceNetEtherNtostr(uint n, uint str, uint len)
    {
        if (!_netInited) return NetErr(ENOTINIT);
        if (n == 0 || str == 0 || len < 18) return NetErr(EINVAL);
        var b = GuestMemory.Span(n, 6);
        GuestMemory.WriteCString(str, $"{b[0]:x2}:{b[1]:x2}:{b[2]:x2}:{b[3]:x2}:{b[4]:x2}:{b[5]:x2}", 18);
        return 0;
    }

    [Hle]
    int sceNetEtherStrton(uint str, uint n)
    {
        if (!_netInited) return NetErr(ENOTINIT);
        if (str == 0 || n == 0) return NetErr(EINVAL);
        var parts = GuestMemory.ReadCString(str, 64).Split(':');
        if (parts.Length != 6) return NetErr(EINVAL);
        Span<byte> mac = stackalloc byte[6];
        for (int i = 0; i < 6; i++)
            if (parts[i].Length is < 1 or > 2 || !byte.TryParse(parts[i], System.Globalization.NumberStyles.AllowHexSpecifier, null, out mac[i]))
                return NetErr(EINVAL);
        mac.CopyTo(GuestMemory.Span(n, 6));
        return 0;
    }

    // ------------------------------------------------------------------ packet dump (no traffic offline)

    const int DUMP_DONTWAIT = 0x20;

    [Hle]
    int sceNetDumpCreate(string name, int len, int flags)
    {
        if (len <= 0) return NetErr(EINVAL);
        lock (_netLock)
        {
            var d = new NetDump { Id = _nextDump++ };
            _dumps[d.Id] = d;
            return d.Id;
        }
    }

    [Hle]
    int sceNetDumpRead(int id, uint buf, int len, uint pflags)
    {
        lock (_netLock)
        {
            if (!_dumps.TryGetValue(id, out var d)) return NetErr(EBADF);
            if (buf == 0 || len <= 0) return NetErr(EINVAL);
            int f = pflags != 0 ? (int)GuestMemory.R32(pflags) : 0;
            if (d.AbortSticky) return NetErr(EINTR);
            if ((f & DUMP_DONTWAIT) != 0) return NetErr(EAGAIN);
            int gen = d.AbortGen;
            Kernel.Wait.Until(_netLock, () => d.Destroyed || d.AbortGen != gen, (long?)null, what: "net dump");
            return NetErr(d.Destroyed ? EBADF : EINTR);
        }
    }

    [Hle]
    int sceNetDumpAbort(int id, int flags)
    {
        lock (_netLock)
        {
            if (!_dumps.TryGetValue(id, out var d)) return NetErr(EBADF);
            d.AbortGen++;
            if ((flags & 1) != 0) d.AbortSticky = true;
            Monitor.PulseAll(_netLock);
            return 0;
        }
    }

    [Hle]
    int sceNetDumpDestroy(int id)
    {
        lock (_netLock)
        {
            if (!_dumps.Remove(id, out var d)) return NetErr(EBADF);
            d.Destroyed = true;
            Monitor.PulseAll(_netLock);
            return 0;
        }
    }

    // ------------------------------------------------------------------ network emulation (stored per interface)

    /// <summary>SceNetEmulationParam is 0x200 bytes; flags 1 = eth0, 2 = wlan0.</summary>
    [Hle]
    int sceNetEmulationSet(uint param, int flags)
    {
        if (param == 0 || flags is not (1 or 2)) return NetErr(EINVAL);
        GuestMemory.Span(param, 0x200).CopyTo(_emulation[flags - 1]);
        return 0;
    }

    [Hle]
    int sceNetEmulationGet(uint param, int flags)
    {
        if (param == 0 || flags is not (1 or 2)) return NetErr(EINVAL);
        _emulation[flags - 1].CopyTo(GuestMemory.Span(param, 0x200));
        return 0;
    }

    // ------------------------------------------------------------------ SceNetCtl additions

    const int NETCTL_NOT_INITIALIZED = unchecked((int)0x80412101), NETCTL_NOT_TERMINATED = unchecked((int)0x80412102),
        NETCTL_INVALID_SIZE = unchecked((int)0x80412111), NETCTL_UNKNOWN_DEVICE = unchecked((int)0x80412188);
    bool _netctlInited;
    readonly long _netctlResetTick = DateTime.UtcNow.Ticks / 10 + 62135596800000000L; // SceRtcTick (µs since 0001-01-01)

    [Hle]
    int sceNetCtlInit()
    {
        if (_netctlInited) { Log.Warn("sceNetCtlInit: already initialised"); return NETCTL_NOT_TERMINATED; }
        _netctlInited = true;
        return 0;
    }

    [Hle]
    void sceNetCtlTerm()
    {
        _netctlInited = false;
        lock (_inetCallbacks) { _inetCallbacks.Clear(); _adhocCallbacks.Clear(); }
    }

    [Hle]
    int sceNetCtlAdhocGetResult(int eventType, Ptr<int> errorCode)
    {
        if (!_netctlInited) return NETCTL_NOT_INITIALIZED;
        if (errorCode.IsNull) return ERR_NETCTL_INVALID_ADDR;
        errorCode.Value = 0; // disconnected without a specific error
        return 0;
    }

    /// <summary>Offline no ad hoc peer is ever visible: *buflen = 0.</summary>
    [Hle]
    int sceNetCtlAdhocGetPeerList(uint buflen, uint buf)
    {
        if (!_netctlInited) return NETCTL_NOT_INITIALIZED;
        if (buflen == 0) return ERR_NETCTL_INVALID_ADDR;
        GuestMemory.W32(buflen, 0);
        return 0;
    }

    /// <summary>SceNetCtlIfStat (0x40): +0 size (caller), +4 totalSec, +8 txBytes, +10 rxBytes, +18 resetTick, +20 reserved.</summary>
    [Hle]
    int sceNetCtlGetIfStat(int device, uint ifstat)
    {
        if (!_netctlInited) return NETCTL_NOT_INITIALIZED;
        if (ifstat == 0) return ERR_NETCTL_INVALID_ADDR;
        if (GuestMemory.R32(ifstat) != 0x40) return NETCTL_INVALID_SIZE;
        if (device is < 0 or > 1) return NETCTL_UNKNOWN_DEVICE;
        GuestMemory.Fill(ifstat + 4, 0, 0x3C);
        GuestMemory.Write(ifstat + 0x18, _netctlResetTick);
        return 0;
    }

    /// <summary>Wi-Fi console: no cellular download limit.</summary>
    [Hle]
    int sceNetCtlGetPhoneMaxDownloadableSize(uint maxSize)
    {
        if (!_netctlInited) return NETCTL_NOT_INITIALIZED;
        if (maxSize == 0) return ERR_NETCTL_INVALID_ADDR;
        GuestMemory.Write(maxSize, long.MaxValue);
        return 0;
    }
}
