using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

public sealed partial class Http
{
    /// <summary>sceHttpGetCookieStats(stats*) (libhttp.suprx 3.74): before sceHttpInit 0x80431001, NULL 0x804311FE;
    /// otherwise, under the cookie lock, five words: bytes held, cookies held, peak bytes, peak cookies, cookies dropped
    /// to make room.</summary>
    [Hle]
    int sceHttpGetCookieStats(uint stats)
    {
        lock (_lock)
        {
            if (!_inited) return ERR_BEFORE_INIT;
            if (stats == 0) return ERR_INVALID_VALUE;
            GuestMemory.W32(stats, (uint)_cookies.Bytes);
            GuestMemory.W32(stats + 4, (uint)_cookies.Count);
            GuestMemory.W32(stats + 8, (uint)_cookies.PeakBytes);
            GuestMemory.W32(stats + 12, (uint)_cookies.PeakCount);
            GuestMemory.W32(stats + 16, (uint)_cookies.Evicted);
            return 0;
        }
    }
}
