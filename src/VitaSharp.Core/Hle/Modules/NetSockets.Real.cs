using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// SceNet with real internet (<see cref="NetConfig"/>, <c>--net=real</c>): every guest socket owns a host socket, always in
/// host non-blocking mode. Guest blocking calls (recv, accept, connect, epoll waits) wait with
/// <see cref="Kernel.Wait.Until(object, Func{bool}, long?, Func{int}?, string)"/> on the net lock, so the waiting guest
/// thread gives its emulated core away; a host poller thread (<see cref="PollLoop"/>) selects on the sockets that have
/// waiters and wakes them when one becomes ready. Abort, close and term wake the waits as in the offline profile.
/// Structures and codes: vita-headers psp2/net/net.h (SceNetSockaddrIn 16 bytes, SCE_NET_ERROR_X = 0x80410100 | BSD errno),
/// behaviour as Vita3K's SceNet (P2P socket types map to TCP/UDP, the virtual port is ignored).
/// </summary>
public sealed partial class Net
{
    // BSD errno values (the low byte of SCE_NET_ERROR_*)
    const int E_INTR = 4, E_BADF = 9, E_ACCES = 13, E_FAULT = 14, E_INVAL = 22, E_MFILE = 24, E_PIPE = 32, E_AGAIN = 35,
        E_INPROGRESS = 36, E_ALREADY = 37, E_NOTSOCK = 38, E_DESTADDRREQ = 39, E_MSGSIZE = 40, E_PROTOTYPE = 41,
        E_NOPROTOOPT = 42, E_PROTONOSUPPORT = 43, E_SOCKTNOSUPPORT = 44, E_OPNOTSUPP = 45, E_PFNOSUPPORT = 46,
        E_AFNOSUPPORT = 47, E_ADDRINUSE = 48, E_ADDRNOTAVAIL = 49, E_NETDOWN = 50, E_NETUNREACH = 51, E_NETRESET = 52,
        E_CONNABORTED = 53, E_CONNRESET = 54, E_NOBUFS = 55, E_ISCONN = 56, E_NOTCONN = 57, E_SHUTDOWN = 58,
        E_TIMEDOUT = 60, E_CONNREFUSED = 61, E_HOSTDOWN = 64, E_HOSTUNREACH = 65;

    internal const int RESOLVER_ETIMEDOUT = unchecked((int)0x804101E2), RESOLVER_ENOHOST = unchecked((int)0x804101E6),
        RESOLVER_ENORECORD = unchecked((int)0x804101E9);

    static int Code(int errno) => unchecked((int)0x80410100) | errno;

    const int SOCK_DGRAM = 2, SOCK_RAW = 3, SOCK_DGRAM_P2P = 6, AF_INET = 2;
    const int SO_REUSEADDR = 0x4, SO_KEEPALIVE = 0x8, SO_BROADCAST = 0x20, SO_LINGER = 0x80, SO_OOBINLINE = 0x100,
        SO_REUSEPORT = 0x200, SO_SNDBUF = 0x1001, SO_RCVBUF = 0x1002, SO_ERROR = 0x1007;
    const int IPPROTO_IP = 0, IPPROTO_TCP = 6, IP_TOS = 3, IP_TTL = 4, IP_MULTICAST_IF = 9, IP_MULTICAST_TTL = 10,
        IP_MULTICAST_LOOP = 11, IP_ADD_MEMBERSHIP = 12, IP_DROP_MEMBERSHIP = 13, TCP_NODELAY = 1;
    const int MSG_OOB = 0x1, MSG_PEEK = 0x2, MSG_DONTROUTE = 0x4, MSG_WAITALL = 0x40;
    internal const uint EPOLLIN = 0x1, EPOLLOUT = 0x2, EPOLLERR = 0x8, EPOLLHUP = 0x10;

    /// <summary>The network profile this instance follows (tests set it; otherwise <see cref="NetConfig.Current"/>).</summary>
    internal NetConfig? ConfigOverride;
    NetConfig Cfg => ConfigOverride ?? NetConfig.Current;

    /// <summary>BSD errno of a host socket error.</summary>
    internal static int Errno(SocketError e) => e switch
    {
        SocketError.WouldBlock or SocketError.IOPending or SocketError.TryAgain => E_AGAIN,
        SocketError.InProgress => E_INPROGRESS,
        SocketError.AlreadyInProgress => E_ALREADY,
        SocketError.NotSocket => E_NOTSOCK,
        SocketError.DestinationAddressRequired => E_DESTADDRREQ,
        SocketError.MessageSize => E_MSGSIZE,
        SocketError.ProtocolType => E_PROTOTYPE,
        SocketError.ProtocolOption => E_NOPROTOOPT,
        SocketError.ProtocolNotSupported => E_PROTONOSUPPORT,
        SocketError.SocketNotSupported => E_SOCKTNOSUPPORT,
        SocketError.OperationNotSupported => E_OPNOTSUPP,
        SocketError.ProtocolFamilyNotSupported => E_PFNOSUPPORT,
        SocketError.AddressFamilyNotSupported => E_AFNOSUPPORT,
        SocketError.AddressAlreadyInUse => E_ADDRINUSE,
        SocketError.AddressNotAvailable => E_ADDRNOTAVAIL,
        SocketError.NetworkDown => E_NETDOWN,
        SocketError.NetworkUnreachable => E_NETUNREACH,
        SocketError.NetworkReset => E_NETRESET,
        SocketError.ConnectionAborted or SocketError.OperationAborted => E_CONNABORTED,
        SocketError.ConnectionReset => E_CONNRESET,
        SocketError.NoBufferSpaceAvailable => E_NOBUFS,
        SocketError.IsConnected => E_ISCONN,
        SocketError.NotConnected => E_NOTCONN,
        SocketError.Shutdown => E_PIPE,
        SocketError.TimedOut => E_TIMEDOUT,
        SocketError.ConnectionRefused => E_CONNREFUSED,
        SocketError.HostDown => E_HOSTDOWN,
        SocketError.HostUnreachable or SocketError.HostNotFound => E_HOSTUNREACH,
        SocketError.Interrupted => E_INTR,
        SocketError.AccessDenied => E_ACCES,
        SocketError.Fault => E_FAULT,
        SocketError.TooManyOpenSockets => E_MFILE,
        _ => E_INVAL,
    };

    int SockErr(SocketError e) => NetErr(Code(Errno(e)));

    // ------------------------------------------------------------------ sockaddr

    /// <summary>SceNetSockaddrIn (16): +0 sin_len, +1 sin_family, +2 sin_port (BE), +4 sin_addr (BE), +8 sin_vport, +A zero[6].</summary>
    static IPEndPoint? ReadSockaddr(uint addr, uint len)
    {
        if (addr == 0 || len < 8) return null;
        if (GuestMemory.Read<byte>(addr + 1) != AF_INET) return null;
        ushort port = BinaryPrimitives.ReverseEndianness(GuestMemory.Read<ushort>(addr + 2));
        return new IPEndPoint(new IPAddress(GuestMemory.Span(addr + 4, 4)), port);
    }

    static void WriteSockaddr(uint addr, uint lenPtr, EndPoint? ep)
    {
        if (addr == 0) return;
        uint cap = lenPtr != 0 ? GuestMemory.R32(lenPtr) : 16;
        Span<byte> b = stackalloc byte[16];
        b.Clear();
        b[0] = 16;
        b[1] = AF_INET;
        if (ep is IPEndPoint ip)
        {
            var a = ip.Address.IsIPv4MappedToIPv6 ? ip.Address.MapToIPv4() : ip.Address;
            BinaryPrimitives.WriteUInt16BigEndian(b[2..], (ushort)ip.Port);
            if (a.AddressFamily == AddressFamily.InterNetwork) a.TryWriteBytes(b[4..8], out _);
        }
        int n = (int)Math.Min(cap, 16u);
        b[..n].CopyTo(GuestMemory.Span(addr, n));
        if (lenPtr != 0) GuestMemory.W32(lenPtr, 16);
    }

    static string Describe(EndPoint? ep, NetConfig cfg) =>
        ep is IPEndPoint ip && cfg.NameOf(ip.Address) is { } name ? $"{ip} ({name})" : ep?.ToString() ?? "?";

    // ------------------------------------------------------------------ socket creation

    /// <summary>Creates the host socket of a new guest socket; returns 0 or the SCE error.</summary>
    int RealCreate(NetSocket s)
    {
        if (s.Domain != AF_INET) return NetErr(Code(E_AFNOSUPPORT));
        (SocketType st, ProtocolType pt) = s.Type switch
        {
            SOCK_STREAM or SOCK_STREAM_P2P => (SocketType.Stream, ProtocolType.Tcp),
            SOCK_DGRAM or SOCK_DGRAM_P2P => (SocketType.Dgram, ProtocolType.Udp),
            SOCK_RAW => (SocketType.Raw, s.Protocol == 0 ? ProtocolType.Icmp : (ProtocolType)s.Protocol),
            _ => (SocketType.Unknown, ProtocolType.Unknown),
        };
        if (st == SocketType.Unknown) return NetErr(Code(E_SOCKTNOSUPPORT));
        try
        {
            var h = new Socket(AddressFamily.InterNetwork, st, pt) { Blocking = false };
            // Windows reports an ICMP "port unreachable" of an earlier datagram as a reset of the next recvfrom (SIO_UDP_CONNRESET);
            // BSD sockets, the Vita's included, do not
            if (st == SocketType.Dgram && OperatingSystem.IsWindows())
                try { h.IOControl(unchecked((int)0x9800000C), [0, 0, 0, 0], null); } catch (SocketException) { }
            s.Host = h;
            EnsurePoller();
            return 0;
        }
        catch (SocketException e)
        {
            Log.Warn($"sceNetSocket({s.Name}, type {s.Type}): host socket failed: {e.SocketErrorCode}");
            return SockErr(e.SocketErrorCode);
        }
    }

    static void CloseHost(NetSocket s)
    {
        try { s.Host?.Dispose(); } catch (ObjectDisposedException) { }
    }

    // ------------------------------------------------------------------ readiness and the poller

    Thread? _poller;

    void EnsurePoller()
    {
        if (_poller != null) return;
        _poller = new Thread(PollLoop) { IsBackground = true, Name = "SceNet poller" };
        _poller.Start();
    }

    /// <summary>Host thread: selects on the sockets that guest threads wait on and wakes them (PulseAll on the net lock)
    /// as soon as one is ready. Waiters check readiness themselves before they wait, so a socket added during a select is
    /// picked up at the next round (at most <see cref="PollSliceUs"/> later).</summary>
    void PollLoop()
    {
        var read = new List<Socket>();
        var write = new List<Socket>();
        var err = new List<Socket>();
        while (true)
        {
            read.Clear(); write.Clear(); err.Clear();
            lock (_netLock)
            {
                foreach (var s in _sockets.Values)
                {
                    if (s.Host == null || s.Closed) continue;
                    if (s.ReadWaiters > 0) read.Add(s.Host);
                    if (s.WriteWaiters > 0) { write.Add(s.Host); err.Add(s.Host); }
                }
                if (read.Count == 0 && write.Count == 0)
                {
                    Monitor.Wait(_netLock, 200); // a new waiter pulses the lock
                    continue;
                }
            }
            bool any;
            try
            {
                Socket.Select(read.Count > 0 ? read : null, write.Count > 0 ? write : null, err.Count > 0 ? err : null, PollSliceUs);
                any = read.Count + write.Count + err.Count > 0;
            }
            catch (Exception e) when (e is ObjectDisposedException or SocketException or ArgumentException)
            {
                any = true; // a socket was closed during the select: let the waiters re-check
            }
            if (any)
            {
                lock (_netLock) Monitor.PulseAll(_netLock);
                Thread.Sleep(1); // give the woken waiters time to consume before selecting again
            }
        }
    }

    const int PollSliceUs = 5_000;

    static bool Ready(Socket h, SelectMode mode)
    {
        try { return h.Poll(0, mode); }
        catch (Exception e) when (e is ObjectDisposedException or SocketException) { return true; }
    }

    /// <summary>Blocks (cooperatively) until <paramref name="ready"/>, a close, an abort of the given direction or the
    /// timeout. Returns 0 when ready, else the SCE error.</summary>
    int WaitSocket(NetSocket so, bool forRead, bool forWrite, uint timeoutUs, Func<bool> ready, bool send, string what)
    {
        int gen = so.AbortGen;
        if (forRead) so.ReadWaiters++;
        if (forWrite) so.WriteWaiters++;
        Monitor.PulseAll(_netLock); // wake the poller
        try
        {
            int w = Block(() => so.Closed || so.AbortGen != gen || (send ? so.AbortSendSticky : so.AbortRecvSticky) || ready(),
                timeoutUs == 0 ? null : timeoutUs, what);
            if (so.Closed) return NetErr(EBADF);
            if (so.AbortGen != gen || (send ? so.AbortSendSticky : so.AbortRecvSticky)) return NetErr(EINTR);
            if (w == SceError.WAIT_TIMEOUT) return NetErr(EAGAIN);
            return w == SceError.OK ? 0 : NetErr(EINTR);
        }
        finally
        {
            if (forRead) so.ReadWaiters--;
            if (forWrite) so.WriteWaiters--;
        }
    }

    // ------------------------------------------------------------------ bind / listen / accept / connect

    int RealBind(NetSocket so, uint addr, uint len)
    {
        var ep = ReadSockaddr(addr, len);
        if (ep == null) return NetErr(Code(E_AFNOSUPPORT));
        try
        {
            so.Host!.Bind(ep);
            so.Local = null;
            return 0;
        }
        catch (SocketException e)
        {
            Log.Debug($"sceNetBind({so.Name}, {ep}): {e.SocketErrorCode}");
            return SockErr(e.SocketErrorCode);
        }
    }

    int RealListen(NetSocket so, int backlog)
    {
        try
        {
            so.Host!.Listen(Math.Max(backlog, 1));
            return 0;
        }
        catch (SocketException e) { return SockErr(e.SocketErrorCode); }
    }

    int RealAccept(NetSocket so, uint addr, uint len)
    {
        var h = so.Host!;
        while (true)
        {
            if (so.AbortRecvSticky) return NetErr(EINTR);
            if (Ready(h, SelectMode.SelectRead))
            {
                Socket c;
                try { c = h.Accept(); }
                catch (SocketException e) when (e.SocketErrorCode == SocketError.WouldBlock) { continue; }
                catch (SocketException e) { return SockErr(e.SocketErrorCode); }
                catch (InvalidOperationException) { return NetErr(Code(E_INVAL)); } // not listening
                c.Blocking = false;
                var ns = new NetSocket { Id = _nextSock++, Name = so.Name, Domain = so.Domain, Type = so.Type, Protocol = so.Protocol, Host = c, Nbio = so.Nbio, StreamConnected = true };
                _sockets[ns.Id] = ns;
                WriteSockaddr(addr, len, c.RemoteEndPoint);
                return ns.Id;
            }
            if (so.Nbio) return NetErr(EAGAIN);
            int r = WaitSocket(so, true, false, so.RcvTimeoUs, () => Ready(h, SelectMode.SelectRead), false, "net accept");
            if (r < 0) return r;
        }
    }

    int RealConnect(NetSocket so, uint addr, uint len)
    {
        var ep = ReadSockaddr(addr, len);
        if (ep == null) return NetErr(Code(E_AFNOSUPPORT));
        var cfg = Cfg;
        if (!cfg.PermitsAddress(ep.Address, out string why))
        {
            cfg.LogBlocked($"connect to {Describe(ep, cfg)}", why);
            return NetErr(Code(E_NETUNREACH));
        }
        var h = so.Host!;
        if (so.StreamConnected && h.SocketType == SocketType.Stream) return NetErr(Code(E_ISCONN));
        if (so.Connecting)
        {
            if (Ready(h, SelectMode.SelectError)) { so.Connecting = false; return SockErr(PendingError(h)); }
            if (!Ready(h, SelectMode.SelectWrite))
            {
                if (so.Nbio) return NetErr(Code(E_ALREADY));
            }
            else { so.Connecting = false; so.StreamConnected = true; return NetErr(Code(E_ISCONN)); }
        }
        else
        {
            cfg.LogContact($"{(h.SocketType == SocketType.Stream ? "TCP" : "UDP")} connect to {Describe(ep, cfg)}");
            try
            {
                h.Connect(ep);
                so.StreamConnected = true;
                return 0;
            }
            catch (SocketException e) when (e.SocketErrorCode is SocketError.WouldBlock or SocketError.InProgress)
            {
                so.Connecting = true;
                if (so.Nbio) return NetErr(Code(E_INPROGRESS));
            }
            catch (SocketException e) { return SockErr(e.SocketErrorCode); }
        }
        // blocking connect: wait until the handshake completes or fails
        int r = WaitSocket(so, false, true, so.SndTimeoUs, () => Ready(h, SelectMode.SelectWrite) || Ready(h, SelectMode.SelectError), true, "net connect");
        if (r < 0)
        {
            if (r == EAGAIN) return NetErr(Code(E_TIMEDOUT));
            return r;
        }
        so.Connecting = false;
        var pe = PendingError(h);
        if (pe != SocketError.Success) return SockErr(pe);
        so.StreamConnected = true;
        return 0;
    }

    /// <summary>Completes a non-blocking connect the guest only waited for (epoll OUT) without calling connect again.</summary>
    static void UpdateConnect(NetSocket so)
    {
        if (!so.Connecting || so.Host == null) return;
        if (Ready(so.Host, SelectMode.SelectError)) so.Connecting = false;
        else if (Ready(so.Host, SelectMode.SelectWrite)) { so.Connecting = false; so.StreamConnected = true; }
    }

    static SocketError PendingError(Socket h)
    {
        try { return (SocketError)(int)h.GetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Error)!; }
        catch (SocketException e) { return e.SocketErrorCode; }
        catch (ObjectDisposedException) { return SocketError.NotSocket; }
    }

    // ------------------------------------------------------------------ send / receive

    static SocketFlags HostFlags(int flags)
    {
        var f = SocketFlags.None;
        if ((flags & MSG_OOB) != 0) f |= SocketFlags.OutOfBand;
        if ((flags & MSG_PEEK) != 0) f |= SocketFlags.Peek;
        if ((flags & MSG_DONTROUTE) != 0) f |= SocketFlags.DontRoute;
        return f;
    }

    int RealSend(NetSocket so, ReadOnlySpan<byte> data, int flags, IPEndPoint? to)
    {
        var h = so.Host!;
        var cfg = Cfg;
        if (to != null && h.SocketType != SocketType.Stream)
        {
            if (!cfg.PermitsAddress(to.Address, out string why))
            {
                cfg.LogBlocked($"datagram to {Describe(to, cfg)}", why);
                return NetErr(Code(E_NETUNREACH));
            }
            cfg.LogContact($"UDP traffic to {Describe(to, cfg)}");
        }
        bool nb = so.Nbio || (flags & MSG_DONTWAIT) != 0;
        var hf = HostFlags(flags);
        int sent = 0;
        // the guest buffer is copied once: a waiting send must not keep a span over guest memory across the wait
        byte[] buf = data.ToArray();
        while (true)
        {
            if (so.AbortSendSticky) return NetErr(EINTR);
            int n;
            SocketError err;
            if (to != null && h.SocketType != SocketType.Stream)
            {
                try { n = h.SendTo(buf, hf, to); err = SocketError.Success; }
                catch (SocketException e) { n = 0; err = e.SocketErrorCode; }
            }
            else n = h.Send(buf, sent, buf.Length - sent, hf, out err);
            if (err == SocketError.Success)
            {
                sent += n;
                if (sent >= buf.Length || h.SocketType != SocketType.Stream || nb) return sent;
                continue; // a blocking stream send returns when everything was sent
            }
            if (err is not (SocketError.WouldBlock or SocketError.IOPending or SocketError.NoBufferSpaceAvailable))
                return sent > 0 ? sent : err == SocketError.NotConnected && h.SocketType == SocketType.Stream ? NetErr(ERR_NET_ENOTCONN) : SockErr(err);
            if (nb) return sent > 0 ? sent : NetErr(EAGAIN);
            int r = WaitSocket(so, false, true, so.SndTimeoUs, () => Ready(h, SelectMode.SelectWrite), true, "net send");
            if (r < 0) return sent > 0 ? sent : r;
        }
    }

    /// <summary>Receives into guest memory. <paramref name="from"/>/<paramref name="fromlen"/>: the sender's
    /// SceNetSockaddrIn.</summary>
    int RealReceive(NetSocket so, uint buf, uint len, int flags, uint from, uint fromlen)
    {
        var tmp = new byte[Math.Min(len, 1u << 20)];
        int n = RealReceiveCore(so, tmp, flags, from, fromlen);
        if (n > 0) tmp.AsSpan(0, n).CopyTo(GuestMemory.Span(buf, n));
        return n;
    }

    /// <summary>Receives into <paramref name="tmp"/>: the byte count (0 = the peer closed the stream) or the SCE error.</summary>
    int RealReceiveCore(NetSocket so, byte[] tmp, int flags, uint from, uint fromlen)
    {
        var h = so.Host!;
        bool stream = h.SocketType == SocketType.Stream;
        bool nb = so.Nbio || (flags & MSG_DONTWAIT) != 0;
        bool waitAll = (flags & MSG_WAITALL) != 0 && stream && !nb;
        var hf = HostFlags(flags);
        int got = 0;
        while (true)
        {
            if (so.AbortRecvSticky) return got > 0 ? got : NetErr(EINTR);
            UpdateConnect(so);
            if (stream && !so.StreamConnected && !so.Connecting) return got > 0 ? got : NetErr(ERR_NET_ENOTCONN);
            if (Ready(h, SelectMode.SelectRead))
            {
                int n;
                SocketError err = SocketError.Success;
                if (stream) n = h.Receive(tmp, got, tmp.Length - got, hf, out err);
                else
                {
                    EndPoint ep = new IPEndPoint(IPAddress.Any, 0);
                    try { n = h.ReceiveFrom(tmp, 0, tmp.Length, hf, ref ep); }
                    catch (SocketException e) when (e.SocketErrorCode == SocketError.MessageSize) { n = tmp.Length; } // BSD truncates
                    catch (SocketException e) { n = 0; err = e.SocketErrorCode; }
                    if (err == SocketError.Success) { WriteSockaddr(from, fromlen, ep); return n; }
                }
                if (err == SocketError.Success)
                {
                    got += n;
                    if (n == 0 || !waitAll || got >= tmp.Length)
                    {
                        if (from != 0) WriteSockaddr(from, fromlen, RemoteOf(h));
                        return got;
                    }
                    continue;
                }
                if (err is not (SocketError.WouldBlock or SocketError.IOPending)) return got > 0 ? got : SockErr(err);
            }
            if (nb) return got > 0 ? got : NetErr(EAGAIN);
            int r = WaitSocket(so, true, false, so.RcvTimeoUs, () => Ready(h, SelectMode.SelectRead), false, "net recv");
            if (r < 0) return got > 0 ? got : r;
        }
    }

    static EndPoint? RemoteOf(Socket h)
    {
        try { return h.RemoteEndPoint; }
        catch (Exception e) when (e is SocketException or ObjectDisposedException) { return null; }
    }

    static EndPoint? LocalOf(Socket h)
    {
        try { return h.LocalEndPoint; }
        catch (Exception e) when (e is SocketException or ObjectDisposedException) { return null; }
    }

    // ------------------------------------------------------------------ msghdr (iovec gather/scatter)

    /// <summary>SceNetIovec (8): +0 base, +4 len.</summary>
    static List<(uint Base, uint Len)> ReadIov(uint msg)
    {
        uint iov = GuestMemory.R32(msg + 8);
        int n = (int)GuestMemory.R32(msg + 0xC);
        var list = new List<(uint, uint)>();
        for (int i = 0; i < n && iov != 0 && i < 1024; i++)
            list.Add((GuestMemory.R32(iov + (uint)i * 8), GuestMemory.R32(iov + (uint)i * 8 + 4)));
        return list;
    }

    int RealSendmsg(NetSocket so, uint msg, int flags)
    {
        using var ms = new MemoryStream();
        foreach (var (b, l) in ReadIov(msg))
            if (b != 0 && l > 0) ms.Write(GuestMemory.Span(b, (int)Math.Min(l, 1u << 20)));
        var to = ReadSockaddr(GuestMemory.R32(msg), GuestMemory.R32(msg + 4));
        return RealSend(so, ms.ToArray(), flags, to);
    }

    int RealRecvmsg(NetSocket so, uint msg, int flags)
    {
        var iov = ReadIov(msg);
        long total = 0;
        foreach (var (_, l) in iov) total += l;
        var tmp = new byte[Math.Min(total, 1L << 20)];
        uint name = GuestMemory.R32(msg);
        int r = RealReceiveCore(so, tmp, flags, name, name != 0 ? msg + 4 : 0);
        GuestMemory.W32(msg + 0x18, 0);
        if (r <= 0) return r;
        int off = 0;
        foreach (var (b, l) in iov)
        {
            if (off >= r) break;
            int n = (int)Math.Min(l, (uint)(r - off));
            if (b != 0 && n > 0) tmp.AsSpan(off, n).CopyTo(GuestMemory.Span(b, n));
            off += n;
        }
        return r;
    }

    // ------------------------------------------------------------------ options

    void RealSetsockopt(NetSocket so, int level, int optname, byte[] v)
    {
        var h = so.Host!;
        int iv = v.Length >= 4 ? BinaryPrimitives.ReadInt32LittleEndian(v) : 0;
        try
        {
            switch (level, optname)
            {
                case (SOL_SOCKET, SO_REUSEADDR) or (SOL_SOCKET, SO_REUSEPORT): h.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, iv != 0); break;
                case (SOL_SOCKET, SO_KEEPALIVE): h.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, iv != 0); break;
                case (SOL_SOCKET, SO_BROADCAST): h.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, iv != 0); break;
                case (SOL_SOCKET, SO_OOBINLINE): h.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.OutOfBandInline, iv != 0); break;
                case (SOL_SOCKET, SO_SNDBUF) when iv > 0: h.SendBufferSize = iv; break;
                case (SOL_SOCKET, SO_RCVBUF) when iv > 0: h.ReceiveBufferSize = iv; break;
                case (SOL_SOCKET, SO_LINGER) when v.Length >= 8:
                    h.LingerState = new LingerOption(iv != 0, BinaryPrimitives.ReadInt32LittleEndian(v.AsSpan(4))); break;
                case (IPPROTO_TCP, TCP_NODELAY): h.NoDelay = iv != 0; break;
                case (IPPROTO_IP, IP_TTL) when iv > 0: h.Ttl = (short)Math.Min(iv, 255); break;
                case (IPPROTO_IP, IP_TOS): h.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.TypeOfService, iv); break;
                case (IPPROTO_IP, IP_MULTICAST_TTL): h.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, v.Length >= 4 ? iv : v[0]); break;
                case (IPPROTO_IP, IP_MULTICAST_LOOP): h.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, (v.Length >= 4 ? iv : v[0]) != 0); break;
                case (IPPROTO_IP, IP_MULTICAST_IF) when v.Length >= 4: h.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, v[..4]); break;
                case (IPPROTO_IP, IP_ADD_MEMBERSHIP) or (IPPROTO_IP, IP_DROP_MEMBERSHIP) when v.Length >= 8:
                    var m = new MulticastOption(new IPAddress(v.AsSpan(0, 4)), new IPAddress(v.AsSpan(4, 4)));
                    h.SetSocketOption(SocketOptionLevel.IP, optname == IP_ADD_MEMBERSHIP ? SocketOptionName.AddMembership : SocketOptionName.DropMembership, m);
                    break;
            }
        }
        catch (SocketException e)
        {
            // options the host refuses (e.g. multicast without a route) are kept as stored values, like the offline profile
            Log.DebugOnce($"net-sockopt-{level}-{optname}", $"sceNetSetsockopt({so.Name}, {level:X}, {optname:X}): host refused ({e.SocketErrorCode}), value stored");
        }
    }

    /// <summary>The live value of an option of a real socket, or null to use the stored one.</summary>
    static byte[]? RealGetsockopt(NetSocket so, int level, int optname)
    {
        var h = so.Host!;
        try
        {
            return (level, optname) switch
            {
                (SOL_SOCKET, SO_ERROR) => BitConverter.GetBytes(PendingError(h) is var e && e == SocketError.Success ? 0 : Errno(e)),
                (SOL_SOCKET, SO_SNDBUF) => BitConverter.GetBytes(h.SendBufferSize),
                (SOL_SOCKET, SO_RCVBUF) => BitConverter.GetBytes(h.ReceiveBufferSize),
                (IPPROTO_TCP, TCP_NODELAY) when h.ProtocolType == ProtocolType.Tcp => BitConverter.GetBytes(h.NoDelay ? 1 : 0),
                _ => null,
            };
        }
        catch (SocketException) { return null; }
    }

    // ------------------------------------------------------------------ epoll

    /// <summary>Current SCE_NET_EPOLL* events of a registered socket (requested ones, plus ERR/HUP).</summary>
    static uint SocketEvents(NetSocket s, uint wanted)
    {
        var h = s.Host;
        if (h == null || s.Closed) return EPOLLHUP & (wanted | EPOLLHUP);
        uint ev = 0;
        bool err = s.Connecting && Ready(h, SelectMode.SelectError);
        if (err) ev |= EPOLLERR | EPOLLOUT; // a failed connect: BSD reports the socket writable with SO_ERROR set
        if ((wanted & EPOLLIN) != 0 && Ready(h, SelectMode.SelectRead))
        {
            ev |= EPOLLIN;
            if (h.SocketType == SocketType.Stream && (s.StreamConnected || s.Connecting) && h.Available == 0)
            {
                // readable without data on a connected stream: the peer closed it
                Span<byte> one = stackalloc byte[1];
                if (h.Receive(one, SocketFlags.Peek, out var e) == 0 && e == SocketError.Success) ev |= EPOLLHUP;
            }
        }
        if ((wanted & EPOLLOUT) != 0 && Ready(h, SelectMode.SelectWrite)) ev |= EPOLLOUT;
        return ev & (wanted | EPOLLERR | EPOLLHUP);
    }

    int CollectEpoll(NetEpoll e, uint events, int maxevents)
    {
        int n = 0;
        foreach (var (id, (wanted, data)) in e.Entries)
        {
            if (n >= maxevents) break;
            if (!_sockets.TryGetValue(id, out var s) || s.Host == null) continue;
            uint ev = SocketEvents(s, wanted);
            if (ev == 0) continue;
            uint p = events + (uint)n * 0x20;
            GuestMemory.Fill(p, 0, 0x20);
            GuestMemory.W32(p, ev);
            GuestMemory.Write(p + 0x18, data);
            n++;
        }
        return n;
    }

    bool AnyReady(NetEpoll e)
    {
        foreach (var (id, (wanted, _)) in e.Entries)
            if (_sockets.TryGetValue(id, out var s) && s.Host != null && SocketEvents(s, wanted) != 0) return true;
        return false;
    }

    int RealEpollWait(NetEpoll e, uint events, int maxevents, int timeout)
    {
        int n = CollectEpoll(e, events, maxevents);
        if (n > 0 || timeout == 0) return n;
        var watched = new List<(NetSocket S, bool R, bool W)>();
        foreach (var (id, (wanted, _)) in e.Entries)
            if (_sockets.TryGetValue(id, out var s) && s.Host != null)
            {
                bool r = (wanted & EPOLLIN) != 0, w = (wanted & EPOLLOUT) != 0;
                if (r) s.ReadWaiters++;
                if (w) s.WriteWaiters++;
                watched.Add((s, r, w));
            }
        Monitor.PulseAll(_netLock);
        int gen = e.AbortGen;
        try
        {
            int w = Block(() => e.Destroyed || e.AbortGen != gen || AnyReady(e), timeout < 0 ? null : timeout, "net epoll");
            if (e.Destroyed || e.AbortGen != gen) return NetErr(EINTR);
            if (w == SceError.WAIT_TIMEOUT) return 0;
            if (w != SceError.OK) return NetErr(EINTR);
            return CollectEpoll(e, events, maxevents);
        }
        finally
        {
            foreach (var (s, r, wr) in watched)
            {
                if (r) s.ReadWaiters--;
                if (wr) s.WriteWaiters--;
            }
        }
    }

    // ------------------------------------------------------------------ resolver

    sealed class Resolver
    {
        public int Id, AbortGen, LastError;
    }

    /// <summary>Waits (cooperatively) for a host DNS task; returns null when it completed, else the SCE error.</summary>
    int? WaitResolve(Resolver r, Task task, int timeoutUs, int retry)
    {
        long limit = timeoutUs <= 0 ? 10_000_000L : Math.Clamp((long)timeoutUs, 1_000_000L, 30_000_000L) * Math.Clamp(retry + 1, 1, 3);
        limit = Math.Min(limit, 30_000_000L);
        task.ContinueWith(_ => { lock (_netLock) Monitor.PulseAll(_netLock); }, TaskScheduler.Default);
        int gen = r.AbortGen;
        int w = Block(() => task.IsCompleted || r.AbortGen != gen || !_resolvers.ContainsKey(r.Id), limit, "net resolver");
        if (!_resolvers.ContainsKey(r.Id)) return NetErr(EBADF);
        if (r.AbortGen != gen) return NetErr(EINTR);
        if (w == SceError.WAIT_TIMEOUT && !task.IsCompleted) return NetErr(RESOLVER_ETIMEDOUT);
        return null;
    }

    int RealResolveName(Resolver r, string host, uint addr, int timeout, int retry)
    {
        var cfg = Cfg;
        if (!cfg.Permits(host, out string why))
        {
            cfg.LogBlocked($"DNS lookup of {host}", why);
            r.LastError = RESOLVER_ENOHOST;
            return NetErr(RESOLVER_ENOHOST);
        }
        var task = Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork);
        if (WaitResolve(r, task, timeout, retry) is int err) { r.LastError = err; return err; }
        if (task.IsFaulted || task.Result.Length == 0)
        {
            Log.Debug($"sceNetResolverStartNtoa({host}): {task.Exception?.InnerException?.Message ?? "no address"}");
            r.LastError = RESOLVER_ENOHOST;
            return NetErr(RESOLVER_ENOHOST);
        }
        var ip = task.Result[0];
        cfg.NoteResolved(host, task.Result);
        cfg.LogContact($"resolved {host} -> {ip}");
        if (addr != 0) ip.TryWriteBytes(GuestMemory.Span(addr, 4), out _);
        r.LastError = 0;
        return 0;
    }

    int RealResolveAddr(Resolver r, uint addr, uint hostBuf, int len, int timeout, int retry)
    {
        if (addr == 0 || hostBuf == 0 || len <= 0) return NetErr(EINVAL);
        var ip = new IPAddress(GuestMemory.Span(addr, 4));
        var cfg = Cfg;
        if (!cfg.PermitsAddress(ip, out string why))
        {
            cfg.LogBlocked($"reverse DNS lookup of {ip}", why);
            r.LastError = RESOLVER_ENOHOST;
            return NetErr(RESOLVER_ENOHOST);
        }
        var task = Dns.GetHostEntryAsync(ip);
        if (WaitResolve(r, task, timeout, retry) is int err) { r.LastError = err; return err; }
        if (task.IsFaulted || string.IsNullOrEmpty(task.Result.HostName))
        {
            r.LastError = RESOLVER_ENORECORD;
            return NetErr(RESOLVER_ENORECORD);
        }
        GuestMemory.WriteCString(hostBuf, task.Result.HostName, len);
        r.LastError = 0;
        return 0;
    }
}
