using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// ScePspnetAdhoc control and PDP sockets with no ad hoc network around (pspnet_adhoc.suprx FW 3.74; the error codes
/// are the PSP's 0x804107xx / 0x80410Bxx). sceNetAdhocctlInit only sets the library up (its buffers come from libnet's
/// pool, so it needs sceNetInit): already initialised 0x80410B07, NULL id 0x80410B04, an id type other than 0..2
/// 0x80412105. A PDP socket is created and bound locally (NULL address or a non-zero flag 0x80410711, before
/// sceNetAdhocInit 0x80410712); with no group to join nothing ever arrives and what is sent reaches no one.
/// </summary>
public sealed partial class NetAdhoc
{
    const int ERR_INVALID_SOCKET_ID = unchecked((int)0x80410701), ERR_INVALID_ADDR = unchecked((int)0x80410702),
        ERR_INVALID_PORT = unchecked((int)0x80410703), ERR_INVALID_BUFLEN = unchecked((int)0x80410704),
        ERR_INVALID_DATALEN = unchecked((int)0x80410705), ERR_SOCKET_DELETED = unchecked((int)0x80410707),
        ERR_SOCKET_ALERTED = unchecked((int)0x80410708), ERR_WOULD_BLOCK = unchecked((int)0x80410709),
        ERR_PORT_IN_USE = unchecked((int)0x8041070A), ERR_SOCKET_ID_NOT_AVAIL = unchecked((int)0x8041070F),
        ERR_INVALID_ARG = unchecked((int)0x80410711), ERR_NOT_INITIALIZED = unchecked((int)0x80410712),
        ERR_TIMEOUT = unchecked((int)0x80410715);
    const int CTL_ERR_INVALID_ARG = unchecked((int)0x80410B04), CTL_ERR_ALREADY_INITIALIZED = unchecked((int)0x80410B07),
        CTL_ERR_NOT_INITIALIZED = unchecked((int)0x80410B08), CTL_ERR_INVALID_ID_TYPE = unchecked((int)0x80412105),
        CTL_ERR_NO_MEMORY = unchecked((int)0x8041211D);
    const int FlagNonBlock = 1, MaxSockets = 255;
    const uint AlertSend = 0x0001, AlertRecv = 0x0002, AlertPoll = 0x0004, AlertConnect = 0x0008, AlertAccept = 0x0010;

    bool _ctlInited;
    readonly byte[] _adhocId = new byte[16];

    [GenerateSnapshot]
    sealed partial class PdpSocket
    {
        public ushort Port;
        public uint BufferSize;
        public uint AlertFlags;
        public bool Deleted;
    }

    readonly Dictionary<int, PdpSocket> _pdp = new();

    /// <summary>(const SceNetAdhocctlAdhocId *adhocId): {int type; char data[9]; padding}, kept by the library.</summary>
    [Hle]
    int sceNetAdhocctlInit(uint adhocId)
    {
        lock (_lock)
        {
            if (_ctlInited) return CTL_ERR_ALREADY_INITIALIZED;
            if (adhocId == 0) return CTL_ERR_INVALID_ARG;
            if (GuestMemory.R32(adhocId) > 2) return CTL_ERR_INVALID_ID_TYPE;
            if (!Net.Initialized) return CTL_ERR_NO_MEMORY;
            GuestMemory.ReadSpan(adhocId, 16).CopyTo(_adhocId);
            _ctlInited = true;
            return 0;
        }
    }

    /// <summary>Stops the library's threads; 0 also when it was not initialised.</summary>
    [Hle]
    int sceNetAdhocctlTerm()
    {
        lock (_lock) _ctlInited = false;
        return 0;
    }

    /// <summary>(SceNetEtherAddr *addr): the unit's own Wi-Fi MAC.</summary>
    [Hle]
    int sceNetAdhocctlGetEtherAddr(uint addr)
    {
        lock (_lock)
        {
            if (!_ctlInited) return CTL_ERR_NOT_INITIALIZED;
            if (addr == 0) return CTL_ERR_INVALID_ARG;
        }
        VitaSharp.Core.Hle.Modules.Net.MacAddress.CopyTo(GuestMemory.Span(addr, 6));
        return 0;
    }

    /// <summary>(const SceNetEtherAddr *saddr, SceUShort16 sport, unsigned int bufsize, int flag): returns the socket
    /// id. Port 0 picks a free one; a port another PDP socket holds is PORT_IN_USE.</summary>
    [Hle]
    int sceNetAdhocPdpCreate(uint saddr, uint sport, uint bufsize, int flag)
    {
        if (saddr == 0 || flag != 0) return ERR_INVALID_ARG;
        lock (_lock)
        {
            if (!_inited) return ERR_NOT_INITIALIZED;
            if (bufsize == 0) return ERR_INVALID_BUFLEN;
            ushort port = (ushort)sport;
            if (port == 0)
            {
                port = 0x8000;
                while (_pdp.Values.Any(s => s.Port == port)) port++;
            }
            else if (_pdp.Values.Any(s => s.Port == port)) return ERR_PORT_IN_USE;
            int id = 1;
            while (_pdp.ContainsKey(id)) id++;
            if (id > MaxSockets) return ERR_SOCKET_ID_NOT_AVAIL;
            _pdp[id] = new PdpSocket { Port = port, BufferSize = bufsize };
            return id;
        }
    }

    /// <summary>(int id, int flag): a receiver blocked on the socket wakes with SOCKET_DELETED.</summary>
    [Hle]
    int sceNetAdhocPdpDelete(int id, int flag)
    {
        lock (_lock)
        {
            if (!_inited) return ERR_NOT_INITIALIZED;
            if (!_pdp.Remove(id, out var s)) return ERR_INVALID_SOCKET_ID;
            s.Deleted = true;
            Monitor.PulseAll(_lock);
            return 0;
        }
    }

    /// <summary>(int id, const SceNetEtherAddr *daddr, SceUShort16 dport, const void *data, int len, unsigned int
    /// timeout, int flag): no peer can be reached, so the datagram goes nowhere; it is accepted whole.</summary>
    [Hle]
    int sceNetAdhocPdpSend(int id, uint daddr, uint dport, uint data, int len, uint timeout, int flag)
    {
        lock (_lock)
        {
            if (!_inited) return ERR_NOT_INITIALIZED;
            if (!_pdp.TryGetValue(id, out var s)) return ERR_INVALID_SOCKET_ID;
            if ((s.AlertFlags & AlertSend) != 0) return ERR_SOCKET_ALERTED;
        }
        if (daddr == 0) return ERR_INVALID_ADDR;
        if ((ushort)dport == 0) return ERR_INVALID_PORT;
        if (len < 0 || (len > 0 && data == 0)) return ERR_INVALID_DATALEN;
        return len;
    }

    /// <summary>(int id, SceNetEtherAddr *saddr, SceUShort16 *sport, void *buf, int *len, unsigned int timeout, int
    /// flag): nothing ever arrives. Non-blocking: WOULD_BLOCK; blocking: TIMEOUT after the timeout (0 waits until the
    /// socket is deleted or its receive is alerted).</summary>
    [Hle]
    int sceNetAdhocPdpRecv(int id, uint saddr, uint sport, uint buf, Ptr<int> len, uint timeout, int flag)
    {
        lock (_lock)
        {
            if (!_inited) return ERR_NOT_INITIALIZED;
            if (!_pdp.TryGetValue(id, out var s)) return ERR_INVALID_SOCKET_ID;
            if (saddr == 0 || sport == 0 || buf == 0 || len.IsNull) return ERR_INVALID_ARG;
            if (len.Value <= 0) return ERR_INVALID_BUFLEN;
            if ((s.AlertFlags & AlertRecv) != 0) return ERR_SOCKET_ALERTED;
            if ((flag & FlagNonBlock) != 0) return ERR_WOULD_BLOCK;
            int r = Kernel.Wait.Until(_lock, () => s.Deleted || (s.AlertFlags & AlertRecv) != 0, timeout == 0 ? null : (long)timeout,
                null, "adhoc pdp recv");
            if (r == SceError.WAIT_TIMEOUT) return ERR_TIMEOUT;
            return s.Deleted ? ERR_SOCKET_DELETED : ERR_SOCKET_ALERTED;
        }
    }

    /// <summary>(int id, int flag): the socket's alert flags (send 1, recv 2, poll 4, connect 8, accept 0x10); an alerted
    /// operation fails with SOCKET_ALERTED and a blocked receiver wakes.</summary>
    [Hle]
    int sceNetAdhocSetSocketAlert(int id, int flag)
    {
        if ((flag & ~(int)(AlertSend | AlertRecv | AlertPoll | AlertConnect | AlertAccept)) != 0) return ERR_INVALID_ARG;
        lock (_lock)
        {
            if (!_inited) return ERR_NOT_INITIALIZED;
            if (!_pdp.TryGetValue(id, out var s)) return ERR_INVALID_SOCKET_ID;
            s.AlertFlags = (uint)flag;
            Monitor.PulseAll(_lock);
            return 0;
        }
    }

    /// <summary>(int id, int *flag).</summary>
    [Hle]
    int sceNetAdhocGetSocketAlert(int id, Ptr<int> flag)
    {
        lock (_lock)
        {
            if (!_inited) return ERR_NOT_INITIALIZED;
            if (!_pdp.TryGetValue(id, out var s)) return ERR_INVALID_SOCKET_ID;
            if (flag.IsNull) return ERR_INVALID_ARG;
            flag.Value = (int)s.AlertFlags;
            return 0;
        }
    }
}
