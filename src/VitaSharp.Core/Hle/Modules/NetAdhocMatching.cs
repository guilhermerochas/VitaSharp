using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// SceNetAdhocMatching in the offline profile: the console is not in an ad hoc network and never sees a peer. The
/// library and its contexts initialise and start normally, no hello is ever received (so the handler gets no event),
/// and every operation aimed at a peer fails with UNKNOWN_TARGET — to the game, "searching and nobody showed up".
/// </summary>
public sealed class NetAdhocMatching : HleModule
{
    internal const int ERR_INVALID_MODE = unchecked((int)0x80413101), ERR_INVALID_PORT = unchecked((int)0x80413102),
        ERR_INVALID_MAXNUM = unchecked((int)0x80413103), ERR_RXBUF_TOO_SHORT = unchecked((int)0x80413104),
        ERR_INVALID_OPTLEN = unchecked((int)0x80413105), ERR_INVALID_ARG = unchecked((int)0x80413106),
        ERR_INVALID_ID = unchecked((int)0x80413107), ERR_IS_RUNNING = unchecked((int)0x8041310A),
        ERR_NOT_RUNNING = unchecked((int)0x8041310B), ERR_UNKNOWN_TARGET = unchecked((int)0x8041310C),
        ERR_ALREADY_INITIALIZED = unchecked((int)0x80413112), ERR_NOT_INITIALIZED = unchecked((int)0x80413113),
        ERR_PORT_IN_USE = unchecked((int)0x80413114), ERR_INVALID_DATALEN = unchecked((int)0x80413116);

    const int ModeParent = 1, ModeChild = 2, ModeP2P = 3;

    sealed class Ctx
    {
        public int Id, Mode, MaxNum, RxBufLen, RetryCount;
        public ushort Port;
        public uint HelloIntUs, KeepAliveIntUs, RexmtIntUs, Handler;
        public bool Running;
        public byte[] HelloOpt = [];
    }

    readonly object _lock = new();
    bool _inited;
    uint _poolSize, _poolPtr;
    readonly Dictionary<int, Ctx> _ctx = new();
    int _nextId = 1;

    [Hle]
    int sceNetAdhocMatchingInit(uint poolSize, uint poolPtr)
    {
        lock (_lock)
        {
            if (_inited) return ERR_ALREADY_INITIALIZED;
            if (poolSize == 0 || poolPtr == 0) return ERR_INVALID_ARG;
            _inited = true;
            _poolSize = poolSize;
            _poolPtr = poolPtr;
            return 0;
        }
    }

    [Hle]
    int sceNetAdhocMatchingTerm()
    {
        lock (_lock)
        {
            _ctx.Clear();
            _inited = false;
            return 0;
        }
    }

    [Hle]
    int sceNetAdhocMatchingCreate(int mode, int maxMembers, ushort port, int rxBufferLen, uint helloInterval, uint keepAliveInterval,
        int retryCount, uint rexmtInterval, uint handler)
    {
        lock (_lock)
        {
            if (!_inited) return ERR_NOT_INITIALIZED;
            if (mode is not (ModeParent or ModeChild or ModeP2P)) return ERR_INVALID_MODE;
            if (maxMembers is < 2 or > 16) return ERR_INVALID_MAXNUM;
            if (port == 0) return ERR_INVALID_PORT;
            if (rxBufferLen <= 0) return ERR_RXBUF_TOO_SHORT;
            if (_ctx.Values.Any(c => c.Port == port)) return ERR_PORT_IN_USE;
            var x = new Ctx
            {
                Id = _nextId++, Mode = mode, MaxNum = maxMembers, Port = port, RxBufLen = rxBufferLen, HelloIntUs = helloInterval,
                KeepAliveIntUs = keepAliveInterval, RetryCount = retryCount, RexmtIntUs = rexmtInterval, Handler = handler,
            };
            _ctx[x.Id] = x;
            Log.Debug($"sceNetAdhocMatchingCreate(mode {mode}, max {maxMembers}, port {port}) = {x.Id}");
            return x.Id;
        }
    }

    int Get(int id, out Ctx c)
    {
        c = null!;
        if (!_inited) return ERR_NOT_INITIALIZED;
        if (!_ctx.TryGetValue(id, out var f)) return ERR_INVALID_ID;
        c = f;
        return 0;
    }

    [Hle]
    int sceNetAdhocMatchingDelete(int id)
    {
        lock (_lock)
        {
            int r = Get(id, out _);
            if (r < 0) return r;
            _ctx.Remove(id); // a running context is stopped and deleted (PSP behaviour)
            return 0;
        }
    }

    static int ReadOpt(int optLen, uint opt, out byte[] data)
    {
        data = [];
        if (optLen < 0 || optLen > 0x4000) return ERR_INVALID_OPTLEN;
        if (optLen > 0 && opt == 0) return ERR_INVALID_ARG;
        if (optLen > 0) data = GuestMemory.Span(opt, optLen).ToArray();
        return 0;
    }

    [Hle]
    int sceNetAdhocMatchingStart(int id, int threadPriority, int threadStackSize, int threadCpuAffinityMask, int helloOptLen, uint helloOpt)
    {
        lock (_lock)
        {
            int r = Get(id, out var c);
            if (r < 0) return r;
            if (c.Running) return ERR_IS_RUNNING;
            if (c.Mode != ModeChild)
            {
                r = ReadOpt(helloOptLen, helloOpt, out var data);
                if (r < 0) return r;
                c.HelloOpt = data;
            }
            c.Running = true; // no event thread: nothing is ever received offline
            return 0;
        }
    }

    [Hle]
    int sceNetAdhocMatchingStop(int id)
    {
        lock (_lock)
        {
            int r = Get(id, out var c);
            if (r < 0) return r;
            c.Running = false;
            return 0;
        }
    }

    int TargetOp(int id, uint target, int optLen, uint opt)
    {
        lock (_lock)
        {
            int r = Get(id, out var c);
            if (r < 0) return r;
            if (!c.Running) return ERR_NOT_RUNNING;
            if (target == 0) return ERR_INVALID_ARG;
            r = ReadOpt(optLen, opt, out _);
            if (r < 0) return r;
            return ERR_UNKNOWN_TARGET;
        }
    }

    [Hle] int sceNetAdhocMatchingSelectTarget(int id, uint target, int optLen, uint opt) => TargetOp(id, target, optLen, opt);
    [Hle] int sceNetAdhocMatchingCancelTarget(int id, uint target) => TargetOp(id, target, 0, 0);
    [Hle] int sceNetAdhocMatchingCancelTargetWithOpt(int id, uint target, int optLen, uint opt) => TargetOp(id, target, optLen, opt);

    [Hle]
    int sceNetAdhocMatchingSendData(int id, uint addr, int dataLen, uint data)
    {
        lock (_lock)
        {
            int r = Get(id, out var c);
            if (r < 0) return r;
            if (!c.Running) return ERR_NOT_RUNNING;
            if (addr == 0 || data == 0) return ERR_INVALID_ARG;
            if (dataLen <= 0 || dataLen > 0x4000) return ERR_INVALID_DATALEN;
            return ERR_UNKNOWN_TARGET;
        }
    }

    [Hle] int sceNetAdhocMatchingAbortSendData(int id, uint addr) => TargetOp(id, addr, 0, 0);

    [Hle]
    int sceNetAdhocMatchingSetHelloOpt(int id, int optLen, uint opt)
    {
        lock (_lock)
        {
            int r = Get(id, out var c);
            if (r < 0) return r;
            if (c.Mode == ModeChild) return ERR_INVALID_MODE;
            if (!c.Running) return ERR_NOT_RUNNING;
            r = ReadOpt(optLen, opt, out var d);
            if (r < 0) return r;
            c.HelloOpt = d;
            return 0;
        }
    }

    /// <summary>*optLen: in = capacity of opt, out = size of the current hello data; opt == NULL only queries.</summary>
    [Hle]
    int sceNetAdhocMatchingGetHelloOpt(int id, uint optLen, uint opt)
    {
        lock (_lock)
        {
            int r = Get(id, out var c);
            if (r < 0) return r;
            if (optLen == 0) return ERR_INVALID_ARG;
            if (c.Mode == ModeChild) return ERR_INVALID_MODE;
            uint cap = GuestMemory.R32(optLen);
            if (opt != 0)
            {
                int n = (int)Math.Min(cap, (uint)c.HelloOpt.Length);
                c.HelloOpt.AsSpan(0, n).CopyTo(GuestMemory.Span(opt, n));
            }
            GuestMemory.W32(optLen, (uint)c.HelloOpt.Length);
            return 0;
        }
    }

    /// <summary>The console has no ad hoc address offline, so the member list is empty.</summary>
    [Hle]
    int sceNetAdhocMatchingGetMembers(int id, uint membersCount, uint members)
    {
        lock (_lock)
        {
            int r = Get(id, out var c);
            if (r < 0) return r;
            if (!c.Running) return ERR_NOT_RUNNING;
            if (membersCount == 0) return ERR_INVALID_ARG;
            GuestMemory.W32(membersCount, 0);
            return 0;
        }
    }
}
