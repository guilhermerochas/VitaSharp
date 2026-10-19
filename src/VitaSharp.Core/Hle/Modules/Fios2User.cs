using System.Runtime.CompilerServices;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>SceFios2User: process path overlays used by the (LLE) libfios2 and applied to every sceIo path.</summary>
public sealed class Fios2User : HleModule
{
    sealed record Overlay(int Id, byte Type, byte Order, string Dst, string Src);

    /// <summary>Unknown overlay id / bad parameter (FIOS facility; exact firmware value not documented).</summary>
    const int ERR_FIOS = unchecked((int)0x80820001);
    /// <summary>SCE_KERNEL_ERROR_UNSUP: FW 3.60 rejects overlay directory handles ("unsupported operation", psp2/fios2kernel.h).</summary>
    const int ERR_UNSUP = unchecked((int)0x80020004);
    /// <summary>First privileged overlay order: 0x00-0x7F are application overlays.</summary>
    const uint PrivilegedOrder = 0x80;

    readonly List<Overlay> _overlays = new();
    int _nextId = 1;

    // per guest thread "application overlays disabled" flag (a GuestThread may move between host threads)
    readonly ConditionalWeakTable<GuestThread, StrongBox<bool>> _threadDisabled = new();
    bool _noThreadDisabled; // callers outside a guest thread (tests, host code)

    static string Standardize(string p)
    {
        p = p.Replace('\\', '/');
        while (p.Contains("//")) p = p.Replace("//", "/");
        int colon = p.IndexOf(':');
        if (colon > 0 && colon + 1 < p.Length && p[colon + 1] == '/') p = p.Remove(colon + 1, 1); // "app0:/x" == "app0:x"
        return p.Length > 1 && p.EndsWith('/') ? p[..^1] : p;
    }

    /// <summary>Applies the overlay table (lowest order first) to a guest path. With application overlays disabled for
    /// the calling thread, only privileged overlays (order &gt;= 0x80) apply.</summary>
    public string Resolve(string path, uint minOrder = 0, uint maxOrder = uint.MaxValue)
    {
        lock (_overlays)
        {
            if (_overlays.Count == 0) return path;
        }
        if (ThreadOverlaysDisabled) minOrder = Math.Max(minOrder, PrivilegedOrder);
        lock (_overlays)
        {
            string cur = Standardize(path);
            foreach (var o in _overlays)
            {
                if (o.Order < minOrder) continue;
                if (o.Order > maxOrder) break;
                if (!cur.StartsWith(o.Dst, StringComparison.OrdinalIgnoreCase)) continue;
                if (cur.Length == o.Dst.Length || cur[o.Dst.Length] == '/') cur = o.Src + cur[o.Dst.Length..];
                else if (o.Dst.EndsWith(':')) // device root ("app0:") covers every path on the device
                    cur = o.Src + (o.Src.EndsWith(':') ? "" : "/") + cur[o.Dst.Length..];
            }
            return cur == Standardize(path) ? path : cur;
        }
    }

    internal bool ThreadOverlaysDisabled
    {
        get
        {
            var t = GuestThread.Current;
            if (t == null) return _noThreadDisabled;
            return _threadDisabled.TryGetValue(t, out var b) && b.Value;
        }
        set
        {
            var t = GuestThread.Current;
            if (t == null) { _noThreadDisabled = value; return; }
            _threadDisabled.GetOrCreateValue(t).Value = value;
        }
    }

    static Overlay ReadOverlay(uint overlay, int id) => new(id, GuestMemory.Read<byte>(overlay), GuestMemory.Read<byte>(overlay + 1),
        Standardize(GuestMemory.ReadCString(overlay + 0x10, 292)), Standardize(GuestMemory.ReadCString(overlay + 0x134, 292)));

    /// <summary>Inserts keeping the list sorted by order (insertion order within the same order). Caller holds the lock.</summary>
    void InsertSorted(Overlay o)
    {
        int i = 0;
        while (i < _overlays.Count && _overlays[i].Order <= o.Order) i++;
        _overlays.Insert(i, o);
    }

    [Hle]
    int sceFiosOverlayAddForProcess02(int pid, uint overlay, Ptr<int> outId)
    {
        if (overlay == 0) return SceError.ILLEGAL_ADDR;
        Overlay o;
        lock (_overlays)
        {
            o = ReadOverlay(overlay, _nextId++);
            InsertSorted(o);
            outId.TrySet(o.Id);
        }
        Log.Info($"FIOS overlay {o.Order}: {o.Dst} -> {o.Src} (type {o.Type})");
        return 0;
    }

    [Hle]
    int sceFiosOverlayGetList02(int pid, uint minOrder, uint maxOrder, uint outIds, uint maxIds, Ptr<uint> actual)
    {
        lock (_overlays)
        {
            var ids = _overlays.Where(o => o.Order >= (minOrder & 0xFF) && o.Order <= (maxOrder & 0xFF)).Select(o => o.Id).ToArray();
            actual.TrySet((uint)ids.Length);
            if (outIds != 0)
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

    /// <summary>SceFiosOverlay (0x258): +0 type, +1 order, +2 dst_len, +4 src_len, +6 unk2, +8 pid, +0xC id,
    /// +0x10 dst[292], +0x134 src[292].</summary>
    [Hle]
    int sceFiosOverlayGetInfoForProcess02(int pid, int id, uint outOverlay)
    {
        if (outOverlay == 0) return SceError.ILLEGAL_ADDR;
        lock (_overlays)
        {
            var o = _overlays.FirstOrDefault(x => x.Id == id);
            if (o == null) return ERR_FIOS;
            GuestMemory.Span(outOverlay, 0x258).Clear();
            GuestMemory.Write(outOverlay, o.Type);
            GuestMemory.Write(outOverlay + 1, o.Order);
            GuestMemory.Write(outOverlay + 2, (ushort)System.Text.Encoding.UTF8.GetByteCount(o.Dst));
            GuestMemory.Write(outOverlay + 4, (ushort)System.Text.Encoding.UTF8.GetByteCount(o.Src));
            GuestMemory.W32(outOverlay + 8, (uint)pid);
            GuestMemory.W32(outOverlay + 0xC, (uint)o.Id);
            GuestMemory.WriteCString(outOverlay + 0x10, o.Dst, 292);
            GuestMemory.WriteCString(outOverlay + 0x134, o.Src, 292);
        }
        return 0;
    }

    /// <summary>Replaces type/order/dst/src of an overlay, keeping its id; a changed order moves it in the list.</summary>
    [Hle]
    int sceFiosOverlayModifyForProcess02(int pid, int id, uint overlay)
    {
        if (overlay == 0) return SceError.ILLEGAL_ADDR;
        Overlay n;
        lock (_overlays)
        {
            int i = _overlays.FindIndex(x => x.Id == id);
            if (i < 0) return ERR_FIOS;
            n = ReadOverlay(overlay, id);
            _overlays.RemoveAt(i);
            InsertSorted(n);
        }
        Log.Info($"FIOS overlay {id} modified {n.Order}: {n.Dst} -> {n.Src} (type {n.Type})");
        return 0;
    }

    [Hle]
    int sceFiosOverlayThreadSetDisabled02(int disabled) { ThreadOverlaysDisabled = disabled != 0; return 0; }

    [Hle]
    int sceFiosOverlayThreadIsDisabled02() => ThreadOverlaysDisabled ? 1 : 0;

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

    [Hle("_sceFiosKernelOverlayGetInfo")]
    int _sceFiosKernelOverlayGetInfo(int id, uint outOverlay) => sceFiosOverlayGetInfoForProcess02(0, id, outOverlay);

    [Hle("_sceFiosKernelOverlayGetInfoForProcess")]
    int _sceFiosKernelOverlayGetInfoForProcess(int pid, int id, uint outOverlay) => sceFiosOverlayGetInfoForProcess02(pid, id, outOverlay);

    /// <summary>SceFiosGetListSyscallArgs (0x18, layout inferred from sceFiosKernelOverlayGetList02_opt):
    /// +0 out_ids, +4 max_ids, +8 SceSize *actual_ids, +0xC out_ids buffer size in bytes, +0x10 reserved[2].</summary>
    [Hle("_sceFiosKernelOverlayGetList")]
    int _sceFiosKernelOverlayGetList(int pid, uint minOrder, uint maxOrder, uint args)
    {
        if (args == 0) return ERR_FIOS;
        uint outIds = GuestMemory.R32(args), maxIds = GuestMemory.R32(args + 4), actual = GuestMemory.R32(args + 8),
            bufSize = GuestMemory.R32(args + 0xC);
        if (bufSize != 0) maxIds = Math.Min(maxIds, bufSize / 4);
        return sceFiosOverlayGetList02(pid, minOrder, maxOrder, outIds, maxIds, new Ptr<uint>(actual));
    }

    [Hle("_sceFiosKernelOverlayModify")]
    int _sceFiosKernelOverlayModify(int id, uint newValue) => sceFiosOverlayModifyForProcess02(0, id, newValue);

    [Hle("_sceFiosKernelOverlayModifyForProcess")]
    int _sceFiosKernelOverlayModifyForProcess(int pid, int id, uint newValue) => sceFiosOverlayModifyForProcess02(pid, id, newValue);

    [Hle("_sceFiosKernelOverlayGetRecommendedScheduler")]
    int _sceFiosKernelOverlayGetRecommendedScheduler(int param, string path) => sceFiosOverlayGetRecommendedScheduler02(param, path);

    [Hle("_sceFiosKernelOverlayResolveWithRangeSync")]
    int _sceFiosKernelOverlayResolveWithRangeSync(int pid, int mode, string inPath, uint opt)
    {
        if (opt == 0) return ERR_FIOS;
        uint outPath = GuestMemory.R32(opt), maxPath = GuestMemory.R32(opt + 4);
        byte lo = GuestMemory.Read<byte>(opt + 8), hi = GuestMemory.Read<byte>(opt + 9);
        GuestMemory.WriteCString(outPath, Resolve(inPath, lo, hi == 0 ? uint.MaxValue : hi), (int)maxPath);
        return 0;
    }

    [Hle("_sceFiosKernelOverlayResolveSync")]
    int _sceFiosKernelOverlayResolveSync(int pid, int mode, string inPath, uint opt)
    {
        if (opt == 0) return ERR_FIOS;
        GuestMemory.WriteCString(GuestMemory.R32(opt), Resolve(inPath), (int)GuestMemory.R32(opt + 4));
        return 0;
    }

    [Hle("_sceFiosKernelOverlayThreadSetDisabled")]
    int _sceFiosKernelOverlayThreadSetDisabled(int disabled) => sceFiosOverlayThreadSetDisabled02(disabled);

    [Hle("_sceFiosKernelOverlayThreadIsDisabled")]
    int _sceFiosKernelOverlayThreadIsDisabled() => sceFiosOverlayThreadIsDisabled02();

    // Merged overlay directory handles: psp2/fios2kernel.h notes that on FW 3.60 DHOpenSync "returns an
    // unsupported-operation error" before reading its arguments, so libfios2 falls back to plain sceIoDopen.
    // No handle is ever created; the other DH calls reject any handle the same way.

    [Hle("_sceFiosKernelOverlayDHOpenSync")]
    int _sceFiosKernelOverlayDHOpenSync(uint outDh, string path, int fromOrder, uint args)
    {
        Log.WarnOnce("fios-dhopen", $"_sceFiosKernelOverlayDHOpenSync({path}, from {fromOrder & 0xFF}): unsupported on FW 3.60");
        return ERR_UNSUP;
    }

    [Hle("_sceFiosKernelOverlayDHReadSync")]
    int _sceFiosKernelOverlayDHReadSync(int dh, uint outEntry) => ERR_UNSUP;

    [Hle("_sceFiosKernelOverlayDHStatSync")]
    int _sceFiosKernelOverlayDHStatSync(int dh, uint outStat) => ERR_UNSUP;

    [Hle("_sceFiosKernelOverlayDHChstatSync")]
    int _sceFiosKernelOverlayDHChstatSync(int dh, uint newStat, uint cbit) => ERR_UNSUP;

    [Hle("_sceFiosKernelOverlayDHSyncSync")]
    int _sceFiosKernelOverlayDHSyncSync(int dh, int flag) => ERR_UNSUP;

    [Hle("_sceFiosKernelOverlayDHCloseSync")]
    int _sceFiosKernelOverlayDHCloseSync(int dh) => ERR_UNSUP;
}
