namespace VitaSharp.Core.Hle.Modules;

public sealed partial class Http
{
    sealed partial class Settings
    {
        /// <summary>sceHttpSetProxy: the two words of option 0x11 (mode, flags) and the host/port of option 0x12.</summary>
        public int ProxyMode, ProxyFlags;
        public string? ProxyHost;
        public ushort ProxyPort;
    }

    /// <summary>(int id, int mode, int flags, const char *host, SceUShort16 port): the proxy of a template, connection or
    /// request (Settings sets the one the user typed), libhttp's option 0x11 (mode and flags) then 0x12 (host and port).
    /// The library has to be initialised and the host given. The offline model keeps it; real-internet requests go
    /// direct.</summary>
    [Hle("sceHttpSetProxy", Nid = 0x53B722F2, Library = "SceHttp")]
    int sceHttpSetProxy(int id, int mode, int flags, uint host, ushort port)
    {
        lock (_lock)
        {
            if (!_inited) return ERR_BEFORE_INIT;
            if (host == 0) return ERR_INVALID_VALUE;
            int r = Find(id, out var o);
            if (r < 0) return r;
            o.S.ProxyMode = mode;
            o.S.ProxyFlags = flags;
            o.S.ProxyHost = ReadL1(host);
            o.S.ProxyPort = port;
            return 0;
        }
    }
}
