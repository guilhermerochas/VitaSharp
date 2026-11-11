using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

public sealed partial class Ssl
{
    /// <summary>sceSslInternalWrite(SSL *ssl, const void *buf, int num): libssl.suprx 3.74's SSL_write. NULL = -1; a
    /// connection that already sent its close notify (shutdown +0x2C bit 0) sets rwstate (+0x18) to SSL_NOTHING and
    /// fails (-1); otherwise the call goes to the connection's method table (+8), its write slot (+0x20).</summary>
    [Hle("sceSslInternalWrite")]
    int sceSslInternalWrite(uint ssl, uint buf, int num)
    {
        if (ssl == 0) return -1;
        if ((GuestMemory.R32(ssl + 0x2C) & 1) != 0)
        {
            GuestMemory.W32(ssl + 0x18, 1);
            return -1;
        }
        uint method = GuestMemory.R32(ssl + 8);
        uint write = method != 0 ? GuestMemory.R32(method + 0x20) : 0;
        if (write == 0 || GuestThread.Current is not { } t) return -1;
        return (int)t.CallGuest(write, ssl, buf, (uint)num);
    }
}
