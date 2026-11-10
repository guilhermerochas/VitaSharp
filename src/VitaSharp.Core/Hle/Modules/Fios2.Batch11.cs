using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

public sealed partial class Fios2
{
    /// <summary>(int index, SceFiosIOFilterCallback *callback, void **context): the filter in a slot (NULL when empty).</summary>
    [Hle]
    int sceFiosIOFilterGetInfo(int index, uint callback, uint context)
    {
        if (index is < 0 or >= 8) return ERR_BAD_INDEX;
        lock (_lock)
        {
            if (callback != 0) GuestMemory.W32(callback, _filters[index]);
            if (context != 0) GuestMemory.W32(context, _filterContexts[index]);
        }
        return 0;
    }

    /// <summary>(const SceFiosOpAttr *attr, const char *path, const SceFiosStat *stat, int changeBits): the path must
    /// exist; the HLE's files keep their host times and modes, so the change itself is accepted.</summary>
    [Hle] int sceFiosChangeStatSync(uint attr, string path, uint stat, int bits) => stat == 0 ? ERR_BAD_PTR : Exists(path, 0) ? 0 : ERR_BAD_PATH;
    [Hle] int sceFiosChangeStat(uint attr, string path, uint stat, int bits) => Complete(attr, sceFiosChangeStatSync(attr, path, stat, bits), path: path);

    /// <summary>(SceFiosStatistics *stats): the library's debug I/O statistics (a debug build's counters; the release
    /// library keeps none).</summary>
    [Hle(Default = true)] void sceFiosStatisticsGet(uint stats) { }
}
