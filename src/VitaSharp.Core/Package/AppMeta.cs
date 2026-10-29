using VitaSharp.Core.Util;

namespace VitaSharp.Core.Package;

/// <summary>
/// ux0:appmeta/&lt;TITLEID&gt;: the copy of an installed application's metadata (param.sfo, icon0.png, pic0.png and the
/// LiveArea folder) that the Vita's package installer (the promoter) writes next to ux0:app/&lt;TITLEID&gt;. The shell does
/// not scan ux0:app: its database rebuild (vs0:vsh/common/dbrecovery_plugin.suprx) lists ux0:appmeta, mounts each entry
/// (sceAppMgrAppDataMountById 110) and registers the application from it, so a title appears on the home screen only
/// once its appmeta exists. VitaSharp's installers are the emulated package installer: they write appmeta for what they
/// install, and <see cref="Sync"/> does the same for titles installed before (or copied in by hand) when the system
/// software starts.
/// </summary>
public static class AppMeta
{
    /// <summary>The files of sce_sys the promoter keeps in appmeta (the LiveArea folder is copied as a whole).</summary>
    static readonly string[] Files = ["param.sfo", "icon0.png", "pic0.png"];

    /// <summary>
    /// Brings ux0:appmeta in line with ux0:app: writes the metadata of every installed application that has none (or
    /// whose param.sfo changed, e.g. a patch was installed) and drops the entries of applications that are gone. When
    /// anything changed, ux0:id.dat is removed: the installed content changed outside this system's software, and the
    /// shell, finding a memory card without its id file, rebuilds the card's part of its database (what a real Vita does
    /// for a card prepared elsewhere). Returns the number of entries written or removed.
    /// </summary>
    public static int Sync(string ux0Root, Action<string>? log = null)
    {
        string apps = Path.Combine(ux0Root, "app"), metas = Path.Combine(ux0Root, "appmeta");
        int changed = 0;
        if (Directory.Exists(apps))
        {
            foreach (var dir in Directory.EnumerateDirectories(apps))
            {
                string id = Path.GetFileName(dir);
                try
                {
                    if (Write(ux0Root, id, force: false)) { changed++; log?.Invoke($"appmeta: {id} registered"); }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    log?.Invoke($"appmeta: {id} skipped ({e.Message})");
                }
            }
        }
        if (Directory.Exists(metas))
        {
            foreach (var dir in Directory.EnumerateDirectories(metas))
            {
                string id = Path.GetFileName(dir);
                if (Directory.Exists(Path.Combine(apps, id)) || !IsApplication(Path.Combine(dir, "param.sfo"))) continue;
                try
                {
                    Directory.Delete(dir, true);
                    changed++;
                    log?.Invoke($"appmeta: {id} removed (no longer installed)");
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { log?.Invoke($"appmeta: {id} not removed ({e.Message})"); }
            }
        }
        if (changed > 0) InvalidateCardId(ux0Root, log);
        return changed;
    }

    /// <summary>After an installer put <paramref name="titleId"/> into ux0:app (or its patch into ux0:patch): writes its
    /// appmeta and makes the shell rebuild the card's database at its next start.</summary>
    public static void Installed(string ux0Root, string titleId, Action<string>? log = null)
    {
        try
        {
            if (Write(ux0Root, titleId, force: true)) InvalidateCardId(ux0Root, log);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            log?.Invoke($"appmeta: {titleId} not written ({e.Message})");
        }
    }

    /// <summary>Writes ux0:appmeta/<paramref name="titleId"/> from the installed application (an installed patch's
    /// sce_sys files take precedence, as the promoter refreshes appmeta when a patch is installed). Without
    /// <paramref name="force"/> an entry whose param.sfo already matches is left alone. True when something was written.</summary>
    public static bool Write(string ux0Root, string titleId, bool force)
    {
        string app = Path.Combine(ux0Root, "app", titleId);
        string appSys = Io.Vfs.ResolveCaseInsensitive(app, "sce_sys");
        string patchSys = Io.Vfs.ResolveCaseInsensitive(Path.Combine(ux0Root, "patch", titleId), "sce_sys");
        string? Source(string name)
        {
            foreach (var sys in new[] { patchSys, appSys })
            {
                var p = Io.Vfs.ResolveCaseInsensitive(sys, name);
                if (File.Exists(p) || Directory.Exists(p)) return p;
            }
            return null;
        }
        var sfo = Source("param.sfo");
        if (sfo == null || !File.Exists(Io.Vfs.ResolveCaseInsensitive(app, "eboot.bin"))) return false;

        string meta = Path.Combine(ux0Root, "appmeta", titleId);
        var dstSfo = Path.Combine(meta, "param.sfo");
        if (!force && File.Exists(dstSfo) && SameFile(sfo, dstSfo)) return false;

        Directory.CreateDirectory(meta);
        foreach (var f in Files)
        {
            var src = Source(f);
            var dst = Path.Combine(meta, f);
            if (src != null && File.Exists(src)) File.Copy(src, dst, true);
            else if (File.Exists(dst)) File.Delete(dst);
        }
        var liveDst = Path.Combine(meta, "livearea");
        if (Directory.Exists(liveDst)) Directory.Delete(liveDst, true);
        if (Source("livearea") is { } live && Directory.Exists(live)) CopyTree(live, liveDst);
        return true;
    }

    /// <summary>Removes ux0:id.dat, the memory card's binding to this system (MID/DIG/DID, written by the shell): the
    /// shell then treats ux0 as a card it has not seen and rebuilds that part of its database from ux0:appmeta.</summary>
    static void InvalidateCardId(string ux0Root, Action<string>? log)
    {
        var id = Path.Combine(ux0Root, "id.dat");
        if (!File.Exists(id)) return;
        try
        {
            File.Delete(id);
            log?.Invoke("appmeta: installed applications changed; ux0:id.dat reset so the shell rebuilds its database");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { log?.Invoke($"appmeta: ux0:id.dat not reset ({e.Message})"); }
    }

    static bool SameFile(string a, string b)
    {
        var fa = new FileInfo(a);
        var fb = new FileInfo(b);
        return fa.Length == fb.Length && fa.LastWriteTimeUtc == fb.LastWriteTimeUtc;
    }

    /// <summary>A game/application entry (param.sfo CATEGORY gd, gdc, gp...), not some other kind of appmeta.</summary>
    static bool IsApplication(string sfoPath)
    {
        try { return File.Exists(sfoPath) && Sfo.Load(sfoPath).Category.StartsWith('g'); }
        catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException or IndexOutOfRangeException) { return false; }
    }

    static void CopyTree(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.EnumerateFiles(src)) File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), true);
        foreach (var d in Directory.EnumerateDirectories(src)) CopyTree(d, Path.Combine(dst, Path.GetFileName(d)));
    }
}
