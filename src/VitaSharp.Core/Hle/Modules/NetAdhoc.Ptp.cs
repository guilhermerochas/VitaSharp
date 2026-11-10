using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// ScePspnetAdhoc PTP (stream) sockets, socket polling, the socket statistics and the control library's peer queries
/// (pspnet_adhoc.suprx FW 3.74). As with the PDP sockets, the emulated unit is in no ad hoc group: sockets are created,
/// listened on and closed locally; a connect or an accept never finds the other side (it times out, or WOULD_BLOCK
/// when non-blocking), a socket that never connected cannot send or receive (NOT_CONNECTED), and the peer list is empty.
/// </summary>
public sealed partial class NetAdhoc
{
    const int ERR_NOT_ENOUGH_SPACE = unchecked((int)0x80410706), ERR_NOT_CONNECTED = unchecked((int)0x8041070B),
        ERR_NOT_OPENED = unchecked((int)0x8041070D), ERR_NOT_LISTENED = unchecked((int)0x8041070E),
        ERR_NO_ENTRY = unchecked((int)0x80410716), ERR_MISALIGNED = unchecked((int)0x8041071D);
    const int PtpStateClosed = 0, PtpStateListen = 1, PtpStateSynSent = 2;

    [GenerateSnapshot]
    sealed partial class PtpSocket
    {
        public ushort Port, PeerPort;
        public byte[] Peer = new byte[6];
        public uint BufferSize;
        public int State;
        public uint AlertFlags;
        public bool Deleted;
    }

    readonly Dictionary<int, PtpSocket> _ptp = new();

    int NewSocketId()
    {
        int id = 1;
        while (_pdp.ContainsKey(id) || _ptp.ContainsKey(id)) id++;
        return id;
    }

    ushort FreePort(ushort port)
    {
        if (port != 0) return port;
        port = 0x8000;
        while (_pdp.Values.Any(s => s.Port == port) || _ptp.Values.Any(s => s.Port == port)) port++;
        return port;
    }

    /// <summary>(const SceNetEtherAddr *saddr, SceUShort16 sport, const SceNetEtherAddr *daddr, SceUShort16 dport,
    /// unsigned int bufsize, unsigned int rexmt_int, int rexmt_cnt, int flag): a socket that will connect to the peer.</summary>
    [Hle]
    int sceNetAdhocPtpOpen(uint saddr, uint sport, uint daddr, uint dport, uint bufsize, uint rexmtInt, int rexmtCnt, int flag)
    {
        if (saddr == 0 || flag != 0) return ERR_INVALID_ARG;
        lock (_lock)
        {
            if (!_inited) return ERR_NOT_INITIALIZED;
            if (daddr == 0) return ERR_INVALID_ADDR;
            if ((ushort)dport == 0) return ERR_INVALID_PORT;
            if (bufsize == 0) return ERR_NOT_ENOUGH_SPACE;
            ushort port = FreePort((ushort)sport);
            if (_ptp.Values.Any(s => s.Port == port && s.State == PtpStateListen)) return ERR_PORT_IN_USE;
            int id = NewSocketId();
            if (id > MaxSockets) return ERR_SOCKET_ID_NOT_AVAIL;
            var s = new PtpSocket { Port = port, PeerPort = (ushort)dport, BufferSize = bufsize, State = PtpStateClosed };
            GuestMemory.ReadSpan(daddr, 6).CopyTo(s.Peer);
            _ptp[id] = s;
            return id;
        }
    }

    /// <summary>(const SceNetEtherAddr *saddr, SceUShort16 sport, unsigned int bufsize, unsigned int rexmt_int, int
    /// rexmt_cnt, int backlog, int flag): a listening socket.</summary>
    [Hle]
    int sceNetAdhocPtpListen(uint saddr, uint sport, uint bufsize, uint rexmtInt, int rexmtCnt, int backlog, int flag)
    {
        if (saddr == 0 || flag != 0) return ERR_INVALID_ARG;
        lock (_lock)
        {
            if (!_inited) return ERR_NOT_INITIALIZED;
            if (bufsize == 0) return ERR_NOT_ENOUGH_SPACE;
            ushort port = FreePort((ushort)sport);
            if (_ptp.Values.Any(s => s.Port == port) || _pdp.Values.Any(s => s.Port == port)) return ERR_PORT_IN_USE;
            int id = NewSocketId();
            if (id > MaxSockets) return ERR_SOCKET_ID_NOT_AVAIL;
            _ptp[id] = new PtpSocket { Port = port, BufferSize = bufsize, State = PtpStateListen };
            return id;
        }
    }

    /// <summary>Waits on a socket for something no peer will ever do: WOULD_BLOCK non-blocking, else TIMEOUT after the
    /// timeout (0: until the socket is closed, SOCKET_DELETED, or the operation alerted, SOCKET_ALERTED).</summary>
    int WaitNothing(PtpSocket s, uint alert, uint timeout, int flag, string what)
    {
        if ((s.AlertFlags & alert) != 0) return ERR_SOCKET_ALERTED;
        if ((flag & FlagNonBlock) != 0) return ERR_WOULD_BLOCK;
        int r = Kernel.Wait.Until(_lock, () => s.Deleted || (s.AlertFlags & alert) != 0, timeout == 0 ? null : (long)timeout, null, what);
        if (r == SceError.WAIT_TIMEOUT) return ERR_TIMEOUT;
        return s.Deleted ? ERR_SOCKET_DELETED : ERR_SOCKET_ALERTED;
    }

    /// <summary>(int id, unsigned int timeout, int flag): connects to the peer: no peer answers.</summary>
    [Hle]
    int sceNetAdhocPtpConnect(int id, uint timeout, int flag)
    {
        lock (_lock)
        {
            if (!_inited) return ERR_NOT_INITIALIZED;
            if (!_ptp.TryGetValue(id, out var s)) return ERR_INVALID_SOCKET_ID;
            if (s.State == PtpStateListen) return ERR_NOT_OPENED;
            s.State = PtpStateSynSent;
            int r = WaitNothing(s, AlertConnect, timeout, flag, "adhoc ptp connect");
            if (r != ERR_WOULD_BLOCK) s.State = PtpStateClosed;
            return r;
        }
    }

    /// <summary>(int id, SceNetEtherAddr *addr, SceUShort16 *port, unsigned int timeout, int flag): no peer connects.</summary>
    [Hle]
    int sceNetAdhocPtpAccept(int id, uint addr, uint port, uint timeout, int flag)
    {
        lock (_lock)
        {
            if (!_inited) return ERR_NOT_INITIALIZED;
            if (!_ptp.TryGetValue(id, out var s)) return ERR_INVALID_SOCKET_ID;
            if (s.State != PtpStateListen) return ERR_NOT_LISTENED;
            return WaitNothing(s, AlertAccept, timeout, flag, "adhoc ptp accept");
        }
    }

    PtpSocket? Connected(int id, out int error)
    {
        error = 0;
        if (!_inited) { error = ERR_NOT_INITIALIZED; return null; }
        if (!_ptp.TryGetValue(id, out var s)) { error = ERR_INVALID_SOCKET_ID; return null; }
        error = ERR_NOT_CONNECTED; // never: no connect or accept completes
        return s;
    }

    /// <summary>(int id, const void *data, int *len, unsigned int timeout, int flag).</summary>
    [Hle]
    int sceNetAdhocPtpSend(int id, uint data, Ptr<int> len, uint timeout, int flag)
    {
        if (len.IsNull || flag != 0 && (flag & ~FlagNonBlock) != 0) return ERR_INVALID_ARG;
        lock (_lock) { Connected(id, out int e); return e; }
    }

    /// <summary>(int id, void *buf, int *len, unsigned int timeout, int flag).</summary>
    [Hle]
    int sceNetAdhocPtpRecv(int id, uint buf, Ptr<int> len, uint timeout, int flag)
    {
        if (buf == 0 || len.IsNull) return ERR_INVALID_ARG;
        lock (_lock) { Connected(id, out int e); return e; }
    }

    /// <summary>(int id, unsigned int timeout, int flag): waits until what was sent is acknowledged.</summary>
    [Hle]
    int sceNetAdhocPtpFlush(int id, uint timeout, int flag)
    {
        lock (_lock) { Connected(id, out int e); return e; }
    }

    /// <summary>(int id, int flag): closes the socket; waiters wake with SOCKET_DELETED.</summary>
    [Hle]
    int sceNetAdhocPtpClose(int id, int flag)
    {
        if (flag != 0) return ERR_INVALID_ARG;
        lock (_lock)
        {
            if (!_inited) return ERR_NOT_INITIALIZED;
            if (!_ptp.Remove(id, out var s)) return ERR_INVALID_SOCKET_ID;
            s.Deleted = true;
            Monitor.PulseAll(_lock);
            return 0;
        }
    }

    /// <summary>(SceNetAdhocPollSd *sds {int id, int events, int revents}, int nsds, unsigned int timeout, int flag): no
    /// event ever arrives on an ad hoc socket here; the count of ready sockets is 0 after the timeout (WOULD_BLOCK
    /// non-blocking, as the library does for an empty poll).</summary>
    [Hle]
    int sceNetAdhocPollSocket(uint sds, int nsds, uint timeout, int flag)
    {
        if (sds == 0 || nsds <= 0) return ERR_INVALID_ARG;
        lock (_lock)
        {
            if (!_inited) return ERR_NOT_INITIALIZED;
            for (int i = 0; i < nsds; i++)
            {
                uint e = sds + (uint)i * 12;
                int id = (int)GuestMemory.R32(e);
                if (!_pdp.ContainsKey(id) && !_ptp.ContainsKey(id)) return ERR_INVALID_SOCKET_ID;
                GuestMemory.W32(e + 8, 0);
            }
            if ((flag & FlagNonBlock) != 0 || timeout == 0) return ERR_WOULD_BLOCK;
        }
        Wait.Sleep(timeout);
        return ERR_TIMEOUT;
    }

    /// <summary>(int *size, SceNetAdhocPdpStat *stat): one 0x14-byte record per PDP socket {next, id, laddr[6], lport,
    /// rcv_sb_cc}; NULL stat gives the size needed.</summary>
    [Hle]
    int sceNetAdhocGetPdpStat(Ptr<int> size, uint stat)
    {
        if (size.IsNull) return ERR_INVALID_ARG;
        if ((stat & 3) != 0) return ERR_MISALIGNED;
        lock (_lock)
        {
            if (!_inited) return ERR_NOT_INITIALIZED;
            int used = 0;
            uint prev = 0;
            foreach (var (id, s) in _pdp.OrderBy(e => e.Key))
            {
                if (stat != 0)
                {
                    if (size.Value < used + 0x14) break;
                    uint r = stat + (uint)used;
                    GuestMemory.Fill(r, 0, 0x14);
                    GuestMemory.W32(r + 4, (uint)id);
                    Net.MacAddress.CopyTo(GuestMemory.Span(r + 8, 6));
                    GuestMemory.Write(r + 0xE, s.Port);
                    if (prev != 0) GuestMemory.W32(prev, r);
                    prev = r;
                }
                used += 0x14;
            }
            size.Value = used;
            return 0;
        }
    }

    /// <summary>(int *size, SceNetAdhocPtpStat *stat): one 0x24-byte record per PTP socket {next, id, laddr[6],
    /// paddr[6], lport, pport, snd_sb_cc, rcv_sb_cc, state}.</summary>
    [Hle]
    int sceNetAdhocGetPtpStat(Ptr<int> size, uint stat)
    {
        if (size.IsNull) return ERR_INVALID_ARG;
        if ((stat & 3) != 0) return ERR_MISALIGNED;
        lock (_lock)
        {
            if (!_inited) return ERR_NOT_INITIALIZED;
            int used = 0;
            uint prev = 0;
            foreach (var (id, s) in _ptp.OrderBy(e => e.Key))
            {
                if (stat != 0)
                {
                    if (size.Value < used + 0x24) break;
                    uint r = stat + (uint)used;
                    GuestMemory.Fill(r, 0, 0x24);
                    GuestMemory.W32(r + 4, (uint)id);
                    Net.MacAddress.CopyTo(GuestMemory.Span(r + 8, 6));
                    s.Peer.CopyTo(GuestMemory.Span(r + 0xE, 6));
                    GuestMemory.Write(r + 0x14, s.Port);
                    GuestMemory.Write(r + 0x16, s.PeerPort);
                    GuestMemory.W32(r + 0x20, (uint)s.State);
                    if (prev != 0) GuestMemory.W32(prev, r);
                    prev = r;
                }
                used += 0x24;
            }
            size.Value = used;
            return 0;
        }
    }

    // ---- control library queries (no group: no peers)

    /// <summary>(SceNetAdhocctlAdhocId *id): the id sceNetAdhocctlInit kept.</summary>
    [Hle]
    int sceNetAdhocctlGetAdhocId(uint id)
    {
        lock (_lock)
        {
            if (!_ctlInited) return CTL_ERR_NOT_INITIALIZED;
            if (id == 0) return CTL_ERR_INVALID_ARG;
            _adhocId.CopyTo(GuestMemory.Span(id, 16));
            return 0;
        }
    }

    /// <summary>(int *buflen, SceNetAdhocctlPeerInfo *buf): the peers seen (0x98 bytes each): none.</summary>
    [Hle]
    int sceNetAdhocctlGetPeerList(Ptr<int> buflen, uint buf)
    {
        if (buflen.IsNull) return CTL_ERR_INVALID_ARG;
        lock (_lock) { if (!_ctlInited) return CTL_ERR_NOT_INITIALIZED; }
        buflen.Value = 0;
        return 0;
    }

    [Hle]
    int sceNetAdhocctlGetPeerInfo(uint addr, int size, uint info)
    {
        if (addr == 0 || info == 0) return CTL_ERR_INVALID_ARG;
        lock (_lock) return _ctlInited ? ERR_NO_ENTRY : CTL_ERR_NOT_INITIALIZED;
    }

    [Hle]
    int sceNetAdhocctlGetAddrByName(uint nickname, Ptr<int> buflen, uint buf)
    {
        if (nickname == 0 || buflen.IsNull) return CTL_ERR_INVALID_ARG;
        lock (_lock) return _ctlInited ? ERR_NO_ENTRY : CTL_ERR_NOT_INITIALIZED;
    }

    [Hle]
    int sceNetAdhocctlGetNameByAddr(uint addr, uint nickname)
    {
        if (addr == 0 || nickname == 0) return CTL_ERR_INVALID_ARG;
        lock (_lock) return _ctlInited ? ERR_NO_ENTRY : CTL_ERR_NOT_INITIALIZED;
    }

    /// <summary>(SceNetAdhocctlParameter *param 0x94): {channel, group name[8], nickname[128], bssid[6]}: the Wi-Fi
    /// channel 1, no group, the unit's nickname, the unit's MAC.</summary>
    [Hle]
    int sceNetAdhocctlGetParameter(uint param)
    {
        lock (_lock) { if (!_ctlInited) return CTL_ERR_NOT_INITIALIZED; }
        if (param == 0) return CTL_ERR_INVALID_ARG;
        GuestMemory.Fill(param, 0, 0x94);
        GuestMemory.W32(param, 1);
        byte[] nick = System.Text.Encoding.UTF8.GetBytes(AppUtil.UserName);
        nick.AsSpan(0, Math.Min(nick.Length, 127)).CopyTo(GuestMemory.Span(param + 0xC, 127));
        Net.MacAddress.CopyTo(GuestMemory.Span(param + 0x8C, 6));
        return 0;
    }
}
