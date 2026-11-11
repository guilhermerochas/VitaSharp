namespace VitaSharp.Core.Hle;

/// <summary>
/// The coverage report's list of libraries the emulator provides by running the real module (LLE) instead of an HLE:
/// the program brings the module (a game's app0:sce_module) or the loader gives every importer the firmware module
/// (<see cref="HleModule.FirmwareForEveryImporter"/>), and links its exports to the program's imports. Their functions
/// are emulated, so the coverage counts them as covered, shown apart as "LLE" (like "default"). The list only feeds the
/// report (hle-coverage, the HLE status page): the loader decides by its own generic rules, never by this list. Each
/// entry is checked by LleLibrariesTests: the module loads under VitaSharp, exports the library and one export runs.
/// </summary>
public static class LleLibraries
{
    /// <summary>A library and the module that provides it (where the program loads it from).</summary>
    public sealed record Entry(string Library, string Module, string Source);

    public static readonly IReadOnlyList<Entry> All =
    [
        new("SceSmart", "app0:sce_module/libsmart.suprx", "shipped by the games that use it (loaded with sceSysmoduleLoadModule(SCE_SYSMODULE_SMART))"),
        new("SceFace", "app0:sce_module/libface.suprx", "shipped by the games that use it (loaded with sceSysmoduleLoadModule(SCE_SYSMODULE_FACE))"),
        new("SceLsdb", "vs0:vsh/shell/livespace_db.suprx", "the firmware's LiveArea database module: the emulator loads it for every program that imports the library (the HLE keeps only a fallback for its getters)"),
    ];

    /// <summary>The installed copies of an entry's module under <paramref name="fsRoot"/> with the key that decrypts
    /// each (a game's work.bin or licence for app0: modules; none for the others).</summary>
    public static IEnumerable<(string Path, byte[]? Klicensee)> InstalledModules(string fsRoot, Entry e)
    {
        if (e.Module.StartsWith("app0:", StringComparison.Ordinal))
        {
            var apps = System.IO.Path.Combine(fsRoot, "ux0", "app");
            if (!Directory.Exists(apps)) yield break;
            foreach (var app in Directory.GetDirectories(apps).Order(StringComparer.Ordinal))
            {
                var file = System.IO.Path.Combine(app, e.Module["app0:".Length..].Replace('/', System.IO.Path.DirectorySeparatorChar));
                if (!File.Exists(file)) continue;
                var work = System.IO.Path.Combine(app, "sce_sys", "package", "work.bin");
                var lic = System.IO.Path.Combine(fsRoot, "ux0", "license", System.IO.Path.GetFileName(app));
                var keyFile = File.Exists(work) ? work : Directory.Exists(lic) ? Directory.GetFiles(lic, "*.rif").FirstOrDefault() : null;
                if (keyFile != null) yield return (file, Package.PkgInstaller.ReadKlicensee(keyFile));
            }
            yield break;
        }
        var dev = e.Module.Split(':', 2);
        var path = System.IO.Path.Combine(fsRoot, dev[0], dev[1].Replace('/', System.IO.Path.DirectorySeparatorChar));
        if (File.Exists(path)) yield return (path, null);
    }

    /// <summary>The NIDs of the LLE-provided libraries that an installed copy of their module really exports (the
    /// union over the copies: games ship different versions of libsmart/libface). Only these count as covered.</summary>
    public static HashSet<uint> InstalledNids(string fsRoot)
    {
        var set = new HashSet<uint>();
        foreach (var e in All)
            foreach (var (path, klic) in InstalledModules(fsRoot, e))
            {
                try
                {
                    var img = Loader.ModuleImage.Read(Loader.Self.SelfDecryptor.ToElf(File.ReadAllBytes(path), klic));
                    foreach (var x in img.Exports.Where(x => x.Library == e.Library))
                        foreach (uint n in x.FuncNids.Concat(x.VarNids)) set.Add(n);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException) { Util.Log.Warn($"LLE library {e.Library}: {path} not readable ({ex.Message})"); }
            }
        return set;
    }
}
