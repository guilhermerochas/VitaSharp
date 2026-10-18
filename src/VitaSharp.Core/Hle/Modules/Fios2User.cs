using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>SceFios2User: process path overlays used by the (LLE) libfios2 and applied to every sceIo path.</summary>
public sealed class Fios2User : HleModule
{
    sealed record Overlay(int Id, byte Type, byte Order, string Dst, string Src);

    readonly List<Overlay> _overlays = new();
    int _nextId = 1;

    static string Standardize(string p)
    {
        p = p.Replace('\\', '/');
        while (p.Contains("//")) p = p.Replace("//", "/");
        int colon = p.IndexOf(':');
        if (colon > 0 && colon + 1 < p.Length && p[colon + 1] == '/') p = p.Remove(colon + 1, 1); // "app0:/x" == "app0:x"
        return p.Length > 1 && p.EndsWith('/') ? p[..^1] : p;
    }

    /// <summary>Applies the overlay table (lowest order first) to a guest path.</summary>
    public string Resolve(string path, uint minOrder = 0, uint maxOrder = uint.MaxValue)
    {
        lock (_overlays)
        {
            string cur = Standardize(path);
            foreach (var o in _overlays)
            {
                if (o.Order < minOrder) continue;
                if (o.Order > maxOrder) break;
                if (cur.StartsWith(o.Dst, StringComparison.OrdinalIgnoreCase) && (cur.Length == o.Dst.Length || cur[o.Dst.Length] == '/'))
                    cur = o.Src + cur[o.Dst.Length..];
            }
            return cur == Standardize(path) ? path : cur;
        }
    }

    [Hle]
    int sceFiosOverlayAddForProcess02(int pid, uint overlay, Ptr<int> outId)
    {
        byte type = GuestMemory.Read<byte>(overlay);
        byte order = GuestMemory.Read<byte>(overlay + 1);
        string dst = Standardize(GuestMemory.ReadCString(overlay + 16, 292));
        string src = Standardize(GuestMemory.ReadCString(overlay + 308, 292));
        lock (_overlays)
        {
            var o = new Overlay(_nextId++, type, order, dst, src);
            int i = 0;
            while (i < _overlays.Count && _overlays[i].Order <= order) i++;
            _overlays.Insert(i, o);
            outId.TrySet(o.Id);
        }
        Log.Info($"FIOS overlay {order}: {dst} -> {src} (type {type})");
        return 0;
    }

    [Hle]
    int sceFiosOverlayGetList02(int pid, uint minOrder, uint maxOrder, uint outIds, uint maxIds, Ptr<uint> actual)
    {
        lock (_overlays)
        {
            var ids = _overlays.Where(o => o.Order >= minOrder && o.Order <= maxOrder).Select(o => o.Id).ToArray();
            actual.TrySet((uint)ids.Length);
            for (int i = 0; i < Math.Min(ids.Length, (int)maxIds); i++) GuestMemory.W32(outIds + (uint)i * 4, (uint)ids[i]);
        }
        return 0;
    }

    [Hle]
    int sceFiosOverlayGetRecommendedScheduler02(int param, string path)
    {
        if (param <= 1 || path.Length < 6) return 0;
        return path.StartsWith("host") && path[4] <= '9' && path[5] == ':' ? 1 : 0;
    }

    [Hle]
    int sceFiosOverlayResolveWithRangeSync02(int pid, int mode, string inPath, uint outPath, uint maxPath, uint minOrder, uint maxOrder)
    {
        GuestMemory.WriteCString(outPath, Resolve(inPath, minOrder, maxOrder), (int)maxPath);
        return 0;
    }

    [Hle]
    int sceFiosOverlayResolveSync02(int pid, int mode, string inPath, uint outPath, uint maxPath)
        => sceFiosOverlayResolveWithRangeSync02(pid, mode, inPath, outPath, maxPath, 0, uint.MaxValue);

    [Hle]
    int sceFiosOverlayRemoveForProcess02(int pid, int id)
    {
        lock (_overlays) _overlays.RemoveAll(o => o.Id == id);
        return 0;
    }

    [Hle]
    int sceFiosOverlayGetInfoForProcess02(int pid, int id, uint outOverlay)
    {
        lock (_overlays)
        {
            var o = _overlays.FirstOrDefault(x => x.Id == id);
            if (o == null) return unchecked((int)0x80820001);
            GuestMemory.Span(outOverlay, 600).Clear();
            GuestMemory.Write(outOverlay, o.Type);
            GuestMemory.Write(outOverlay + 1, o.Order);
            GuestMemory.WriteCString(outOverlay + 16, o.Dst, 292);
            GuestMemory.WriteCString(outOverlay + 308, o.Src, 292);
        }
        return 0;
    }

    [Hle] int sceFiosOverlayModifyForProcess02(int pid, int id, uint overlay) => 0;
    [Hle] int sceFiosOverlayThreadSetDisabled02(int disabled) => 0;
    [Hle] int sceFiosOverlayThreadIsDisabled02() => 0;

    // ------------------------------------------------------------------ SceFios2Kernel (libfios2 from SDKs before 1.80)
    // Same overlay table; extra arguments travel in an option struct:
    // { char *pOutPath; SceSize maxPath; char loOrderFilter; char hiOrderFilter; char reserved[2]; int reserved[4]; }

    [Hle("_sceFiosKernelOverlayAdd")]
    int _sceFiosKernelOverlayAdd(uint overlay, Ptr<int> outId) => sceFiosOverlayAddForProcess02(0, overlay, outId);

    [Hle("_sceFiosKernelOverlayAddForProcess")]
    int _sceFiosKernelOverlayAddForProcess(int pid, uint overlay, Ptr<int> outId) => sceFiosOverlayAddForProcess02(pid, overlay, outId);

    [Hle("_sceFiosKernelOverlayRemove")]
    int _sceFiosKernelOverlayRemove(int id) => sceFiosOverlayRemoveForProcess02(0, id);

    [Hle("_sceFiosKernelOverlayRemoveForProcess")]
    int _sceFiosKernelOverlayRemoveForProcess(int pid, int id) => sceFiosOverlayRemoveForProcess02(pid, id);

    [Hle("_sceFiosKernelOverlayGetInfoForProcess")]
    int _sceFiosKernelOverlayGetInfoForProcess(int pid, int id, uint outOverlay) => sceFiosOverlayGetInfoForProcess02(pid, id, outOverlay);

    [Hle("_sceFiosKernelOverlayGetRecommendedScheduler")]
    int _sceFiosKernelOverlayGetRecommendedScheduler(int param, string path) => sceFiosOverlayGetRecommendedScheduler02(param, path);

    [Hle("_sceFiosKernelOverlayResolveWithRangeSync")]
    int _sceFiosKernelOverlayResolveWithRangeSync(int pid, int mode, string inPath, uint opt)
    {
        if (opt == 0) return unchecked((int)0x80820001);
        uint outPath = GuestMemory.R32(opt), maxPath = GuestMemory.R32(opt + 4);
        byte lo = GuestMemory.Read<byte>(opt + 8), hi = GuestMemory.Read<byte>(opt + 9);
        GuestMemory.WriteCString(outPath, Resolve(inPath, lo, hi == 0 ? uint.MaxValue : hi), (int)maxPath);
        return 0;
    }

    [Hle("_sceFiosKernelOverlayResolveSync")]
    int _sceFiosKernelOverlayResolveSync(int pid, int mode, string inPath, uint opt)
    {
        if (opt == 0) return unchecked((int)0x80820001);
        GuestMemory.WriteCString(GuestMemory.R32(opt), Resolve(inPath), (int)GuestMemory.R32(opt + 4));
        return 0;
    }

    [Hle("_sceFiosKernelOverlayThreadSetDisabled")]
    int _sceFiosKernelOverlayThreadSetDisabled(int disabled) => 0;

    [Hle("_sceFiosKernelOverlayThreadIsDisabled")]
    int _sceFiosKernelOverlayThreadIsDisabled() => 0;
}
