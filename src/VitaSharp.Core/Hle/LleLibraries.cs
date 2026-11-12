namespace VitaSharp.Core.Hle;

/// <summary>
/// The functions the emulator provides by running a real module (LLE) instead of an HLE, for the coverage report: the
/// libraries an installed module really exports, found by scanning (no fixed list). The modules the loader runs for a
/// program count: the modules a game ships in app0:sce_module (decrypted with its work.bin or licence), the user
/// modules programs load by path from the ur0:/ux0: data (<see cref="DataModules"/>), and the firmware modules the
/// loader gives every program that imports their library (<see cref="HleModule.FirmwareForEveryImporter"/>). Their functions are emulated, so the coverage counts them as
/// covered, shown apart as "LLE" (like "default"). The loader decides by its own rules; this only measures.
/// </summary>
public static class LleLibraries
{
    /// <summary>An installed module that exports libraries, with the key that decrypts it (null when none is needed).</summary>
    public sealed record InstalledModule(string HostPath, string VitaPath, byte[]? Klicensee);

    /// <summary>Every app0:sce_module/*.suprx of the applications installed under <paramref name="fsRoot"/>.</summary>
    public static IEnumerable<InstalledModule> GameModules(string fsRoot)
    {
        string apps = Io.FirmwareLayout.Applications.Host(fsRoot);
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
            string? keyFile = File.Exists(work) ? work : Io.FirmwareLayout.ApplicationLicenseFiles(fsRoot, Path.GetFileName(app)).FirstOrDefault();
            byte[]? klicensee = keyFile != null ? Package.PkgInstaller.ReadKlicensee(keyFile) : null;
            foreach (var file in Directory.GetFiles(dir, "*.suprx").Order(StringComparer.OrdinalIgnoreCase))
            {
                yield return new InstalledModule(file, "app0:sce_module/" + Path.GetFileName(file), klicensee);
            }
        }
    }

    /// <summary>The user modules a program can load by path at run time (sceKernelLoadStartModule) from the memory card
    /// and internal storage data: every *.suprx under ur0: and ux0: outside ux0:app (installed applications, see
    /// <see cref="GameModules"/>). They are plain or fake-signed modules, so no key is needed.</summary>
    public static IEnumerable<InstalledModule> DataModules(string fsRoot)
    {
        foreach (var device in new[] { "ur0", "ux0" })
        {
            var root = Path.Combine(fsRoot, device);
            if (!Directory.Exists(root))
            {
                continue;
            }

            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MatchCasing = MatchCasing.CaseInsensitive };
            string apps = Io.FirmwareLayout.Applications.OnDevice(root) + Path.DirectorySeparatorChar;
            foreach (var file in Directory.EnumerateFiles(root, "*.suprx", options).Order(StringComparer.OrdinalIgnoreCase))
            {
                if (device == "ux0" && file.StartsWith(apps, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                yield return new InstalledModule(file, device + ":" + Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/'), null);
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
    /// library): every library of the games' own modules and of the data folders' modules, and the libraries of
    /// firmware modules the loader serves to every importer. The caller leaves out the NIDs the HLE implements (those
    /// count as HLE).
    /// </summary>
    public static HashSet<uint> InstalledNids(string fsRoot, Func<string, bool> firmwareForEveryImporter)
    {
        var set = new HashSet<uint>();
        var exports = Exports(GameModules(fsRoot).Concat(DataModules(fsRoot)), (_, _) => true)
            .Concat(Exports(FirmwareModules(fsRoot, firmwareForEveryImporter), (_, library) => firmwareForEveryImporter(library)));
        foreach (var (_, _, nids) in exports)
        {
            set.UnionWith(nids);
        }

        return set;
    }
}
