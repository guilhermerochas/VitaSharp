namespace VitaSharp.Core.Hle;

/// <summary>
/// The functions the emulator provides by running a real module (LLE) instead of an HLE, for the coverage report: the
/// libraries an installed module really exports, found by scanning (no fixed list). Two kinds of modules count, the two
/// the loader runs for a program: the modules a game ships in app0:sce_module (decrypted with its work.bin or licence),
/// and the firmware modules the loader gives every program that imports their library
/// (<see cref="HleModule.FirmwareForEveryImporter"/>). Their functions are emulated, so the coverage counts them as
/// covered, shown apart as "LLE" (like "default"). The loader decides by its own rules; this only measures.
/// </summary>
public static class LleLibraries
{
    /// <summary>An installed module that exports libraries, with the key that decrypts it (null when none is needed).</summary>
    public sealed record InstalledModule(string HostPath, string VitaPath, byte[]? Klicensee);

    /// <summary>Every app0:sce_module/*.suprx of the applications installed under <paramref name="fsRoot"/>.</summary>
    public static IEnumerable<InstalledModule> GameModules(string fsRoot)
    {
        var apps = Path.Combine(fsRoot, "ux0", "app");
        if (!Directory.Exists(apps))
        {
            yield break;
        }

        foreach (var app in Directory.GetDirectories(apps).Order(StringComparer.Ordinal))
        {
            var dir = Path.Combine(app, "sce_module");
            if (!Directory.Exists(dir))
            {
                continue;
            }

            var work = Path.Combine(app, "sce_sys", "package", "work.bin");
            var licence = Path.Combine(fsRoot, "ux0", "license", Path.GetFileName(app));
            var keyFile = File.Exists(work) ? work : Directory.Exists(licence) ? Directory.GetFiles(licence, "*.rif").FirstOrDefault() : null;
            byte[]? klicensee = keyFile != null ? Package.PkgInstaller.ReadKlicensee(keyFile) : null;
            foreach (var file in Directory.GetFiles(dir, "*.suprx").Order(StringComparer.OrdinalIgnoreCase))
            {
                yield return new InstalledModule(file, "app0:sce_module/" + Path.GetFileName(file), klicensee);
            }
        }
    }

    /// <summary>The firmware modules under <paramref name="fsRoot"/> that export a library the loader serves to every
    /// importer (<paramref name="firmwareForEveryImporter"/>).</summary>
    public static IEnumerable<InstalledModule> FirmwareModules(string fsRoot, Func<string, bool> firmwareForEveryImporter)
    {
        foreach (var m in Firmware.FirmwareLibraries.For(fsRoot).Modules)
        {
            if (!m.Libraries.Any(l => firmwareForEveryImporter(l.Library)))
            {
                continue;
            }

            var parts = m.VitaPath.Split(':', 2);
            var host = Path.Combine(fsRoot, parts[0], parts[1].Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(host))
            {
                yield return new InstalledModule(host, m.VitaPath, null);
            }
        }
    }

    /// <summary>The exports (library, function and variable NIDs) of each module of <paramref name="modules"/> that can
    /// be read; <paramref name="include"/> picks the libraries that count.</summary>
    public static IEnumerable<(InstalledModule Module, string Library, IReadOnlyList<uint> Nids)> Exports(IEnumerable<InstalledModule> modules,
        Func<InstalledModule, string, bool> include)
    {
        foreach (var m in modules)
        {
            Loader.ModuleImage img;
            try
            {
                img = Loader.ModuleImage.Read(Loader.Self.SelfDecryptor.ToElf(File.ReadAllBytes(m.HostPath), m.Klicensee));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Util.Log.Warn($"LLE scan: {m.HostPath} not readable ({ex.Message})");
                continue;
            }

            foreach (var export in img.Exports.Where(x => include(m, x.Library)))
            {
                yield return (m, export.Library, export.FuncNids.Concat(export.VarNids).ToList());
            }
        }
    }

    /// <summary>
    /// The NIDs installed modules really export (the union over the copies: games ship different versions of the same
    /// library): every library of the games' own modules, and the libraries of firmware modules the loader serves to every
    /// importer. The caller leaves out the NIDs the HLE implements (those count as HLE).
    /// </summary>
    public static HashSet<uint> InstalledNids(string fsRoot, Func<string, bool> firmwareForEveryImporter)
    {
        var set = new HashSet<uint>();
        var exports = Exports(GameModules(fsRoot), (_, _) => true)
            .Concat(Exports(FirmwareModules(fsRoot, firmwareForEveryImporter), (_, library) => firmwareForEveryImporter(library)));
        foreach (var (_, _, nids) in exports)
        {
            set.UnionWith(nids);
        }

        return set;
    }
}
