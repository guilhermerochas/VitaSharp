using VitaSharp.Core.Package.Pfs;

namespace VitaSharp.Core.Package;

public sealed record InstalledTitle(string TitleId, string Title, string Path, PkgContentType Type);

/// <summary>Installs a retail .pkg + NoNpDrm license (work.bin / .rif) into the virtual ux0.</summary>
public static class PkgInstaller
{
    public static byte[] ReadKlicensee(string licensePath)
    {
        var rif = File.ReadAllBytes(licensePath);
        if (rif.Length < 0x60) throw new InvalidDataException("License file is too small");
        return rif.AsSpan(0x50, 16).ToArray();
    }

    public static InstalledTitle Install(string pkgPath, string licensePath, string ux0Root,
                                         Action<string>? log = null, Action<double>? progress = null)
    {
        using var pkg = new PkgReader(pkgPath);
        var sfo = new Sfo(pkg.ParamSfo);
        var type = pkg.ContentType;
        if (type == PkgContentType.App && sfo.Category == "gp") type = PkgContentType.Patch;

        string titleId = sfo.TitleId.Length > 0 ? sfo.TitleId : pkg.TitleId;
        string dest = type switch
        {
            PkgContentType.App => Path.Combine(ux0Root, "app", titleId),
            PkgContentType.Patch => Path.Combine(ux0Root, "patch", titleId),
            PkgContentType.Dlc => Path.Combine(ux0Root, "addcont", titleId, pkg.ContentId[20..]),
            _ => Path.Combine(ux0Root, "theme", pkg.ContentId),
        };
        if (Directory.Exists(dest)) Directory.Delete(dest, true);
        Directory.CreateDirectory(dest);

        long total = pkg.Entries.Sum(e => (long)e.DataSize), done = 0;
        foreach (var e in pkg.Entries)
        {
            var target = Path.Combine(dest, e.Name.Replace('/', Path.DirectorySeparatorChar));
            if (e.IsDirectory) { Directory.CreateDirectory(target); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var outFs = File.Create(target);
            pkg.Extract(e, outFs, n => { done += n; progress?.Invoke(0.5 * done / Math.Max(1, total)); });
        }
        log?.Invoke($"Extracted {pkg.Entries.Count} entries to {dest}");

        byte[] klic = ReadKlicensee(licensePath);
        if (PfsDecryptor.HasPfs(dest))
        {
            var pfs = new PfsDecryptor(dest, klic) { Log = log };
            pfs.DecryptInPlace((d, t) => progress?.Invoke(0.5 + 0.5 * d / Math.Max(1, t)));
        }

        var licDir = Path.Combine(ux0Root, "license", titleId);
        Directory.CreateDirectory(licDir);
        File.Copy(licensePath, Path.Combine(licDir, pkg.ContentId + ".rif"), true);
        Directory.CreateDirectory(Path.Combine(dest, "sce_sys", "package"));
        File.Copy(licensePath, Path.Combine(dest, "sce_sys", "package", "work.bin"), true);
        progress?.Invoke(1.0);
        return new InstalledTitle(titleId, sfo.Title, dest, type);
    }
}
