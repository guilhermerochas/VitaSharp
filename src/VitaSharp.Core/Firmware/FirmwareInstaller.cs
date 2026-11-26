using System.Buffers.Binary;
using System.Text.Json;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Firmware;

/// <summary>Progress of a firmware installation: <see cref="Fraction"/> 0..1 over the whole job.</summary>
public readonly record struct FirmwareProgress(string Stage, double Fraction, string? Detail = null);

/// <summary>What is installed in a file-system root (&lt;DataRoot&gt;/fs), from its firmware.json or detected.</summary>
/// <para><see cref="FontPackage"/>: the system fonts of the firmware font package are in sa0:data/font
/// (<see cref="HasSystemFonts"/>), read from the disk, not from how the firmware was installed. On the console sa0 is
/// written at the factory; no system update (PSVUPDAT.PUP) carries it, so a firmware installed from the update alone
/// has no fonts until the font package PUP (or another installation's sa0) is added.</para>
public sealed record FirmwareInfo(string? Version, uint Build, string Source, DateTime? InstalledUtc, bool FontPackage, IReadOnlyList<string> Partitions)
{
    public const string MetadataFile = "firmware.json";

    /// <summary>Firmware partitions VitaSharp installs (the same folders Vita3K fills).</summary>
    public static readonly string[] PartitionNames = ["os0", "vs0", "sa0", "pd0"];

    /// <summary>Text for the launcher: "3.74", "3.74 + fontes" or "instalado (versao desconhecida)".</summary>
    public string Display => (Version ?? Localization.Loc.Get("UnknownVersion")) + (FontPackage ? Localization.Loc.Get("FwFontPackage") : "");

    /// <summary>
    /// The installed firmware, or null when vs0:sys/external holds no modules. Without firmware.json (a vs0 copied by
    /// hand or from another emulator) the version is read from the system-software version of a firmware module.
    /// </summary>
    public static FirmwareInfo? Read(string fsRoot)
    {
        string meta = Path.Combine(fsRoot, MetadataFile);
        bool hasModules = HasModules(fsRoot);
        if (File.Exists(meta))
        {
            try
            {
                SortedDictionary<string, string> d = JsonSerializer.Deserialize(File.ReadAllText(meta), CoreJson.Indented.SortedDictionaryStringString) ?? new();
                string[] parts = d.GetValueOrDefault("partitions", "").Split(',', StringSplitOptions.RemoveEmptyEntries);
                if (!hasModules && parts.Contains("vs0"))
                {
                    return null; // metadata left behind after vs0 was removed
                }

                return new FirmwareInfo(d.GetValueOrDefault("version"), uint.TryParse(d.GetValueOrDefault("build"), out uint b) ? b : 0,
                    d.GetValueOrDefault("source", ""),
                    DateTime.TryParse(d.GetValueOrDefault("installed"), null, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime t) ? t : null,
                    HasSystemFonts(fsRoot), parts);
            }
            catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
            {
                Log.Warn($"firmware: cannot read {meta}: {e.Message}");
            }
        }
        if (!hasModules)
        {
            return null;
        }

        return new FirmwareInfo(DetectVersion(fsRoot), 0, "", null, HasSystemFonts(fsRoot),
            PartitionNames.Where(p => Directory.Exists(Path.Combine(fsRoot, p)) && Directory.EnumerateFileSystemEntries(Path.Combine(fsRoot, p)).Any()).ToArray());
    }

    internal void Write(string fsRoot)
    {
        var d = new SortedDictionary<string, string>
        {
            ["version"] = Version ?? "",
            ["build"] = Build.ToString(),
            ["source"] = Source,
            ["installed"] = (InstalledUtc ?? DateTime.UtcNow).ToString("o"),
            ["font_package"] = FontPackage ? "true" : "false",
            ["partitions"] = string.Join(',', Partitions),
        };
        File.WriteAllText(Path.Combine(fsRoot, MetadataFile), JsonSerializer.Serialize(d, CoreJson.Indented.SortedDictionaryStringString));
    }

    /// <summary>The font folders of the firmware font package: sa0:data/font/pgf (libpgf, PSP-style fonts) and
    /// sa0:data/font/pvf (libpvf: the Vita system's own text through libpaf, and the games that use ScePvf).</summary>
    public static readonly string[] SystemFontKinds = ["pgf", "pvf"];

    /// <summary>True when sa0:data/font holds both the PGF and the PVF system fonts (installed with the font package,
    /// copied from Vita3K, or put there by hand), whatever else is installed. Without the PVF fonts the Vita system's
    /// text is not drawn at all (libpaf gets no font from libpvf); without the PGF fonts the text of libpgf games.</summary>
    public static bool HasSystemFonts(string fsRoot) => MissingSystemFonts(fsRoot).Count == 0;

    /// <summary>The Vita system's executable, relative to the file-system root.</summary>
    public const string ShellPath = "vs0/vsh/shell/shell.self";

    /// <summary>The Vita system boots with its text: its shell is installed and sa0 holds the system fonts. A console
    /// always has its factory sa0; without it the shell, its first-time setup included, draws no text, so the
    /// front-ends warn before booting it (install the font package, or open it anyway; the emulator blocks nothing).</summary>
    public static bool SystemReady(string fsRoot) =>
        File.Exists(Path.Combine(fsRoot, ShellPath.Replace('/', Path.DirectorySeparatorChar))) && HasSystemFonts(fsRoot);

    /// <summary>The <see cref="SystemFontKinds"/> with no font file in sa0:data/font/&lt;kind&gt;.</summary>
    public static IReadOnlyList<string> MissingSystemFonts(string fsRoot)
    {
        var missing = new List<string>();
        foreach (string kind in SystemFontKinds)
        {
            string dir = Path.Combine(fsRoot, "sa0", "data", "font", kind);
            bool present = false;
            try
            {
                present = Directory.Exists(dir) && Directory.EnumerateFiles(dir, "*." + kind).Any();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            if (!present)
            {
                missing.Add(kind);
            }
        }
        return missing;
    }

    private static bool HasModules(string fsRoot)
    {
        string ext = Path.Combine(fsRoot, "vs0", "sys", "external");
        try
        {
            return Directory.Exists(ext) && Directory.EnumerateFiles(ext, "*.suprx").Any();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>"3.74" from the app-info system-software version (0x00000374_00000000 for 3.74) of a firmware module.</summary>
    internal static string? DetectVersion(string fsRoot)
    {
        foreach (string? rel in new[] { "vs0/sys/external/libc.suprx", "vs0/sys/external/libfios2.suprx", "os0/psp2bootconfig.skprx" })
        {
            string path = Path.Combine(fsRoot, rel.Replace('/', Path.DirectorySeparatorChar));
            try
            {
                if (!File.Exists(path))
                {
                    continue;
                }

                byte[] head = new byte[0x400];
                using (FileStream stream = File.OpenRead(path))
                {
                    stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
                }

                if (BinaryPrimitives.ReadUInt32LittleEndian(head) != 0x00454353)
                {
                    continue;
                }

                long appinfo = (long)BinaryPrimitives.ReadUInt64LittleEndian(head.AsSpan(32 + 24));
                if (appinfo <= 0 || appinfo + 24 > head.Length)
                {
                    continue;
                }

                ulong sysver = BinaryPrimitives.ReadUInt64LittleEndian(head.AsSpan((int)appinfo + 16));
                uint v = (uint)(sysver >> 32);
                if (v == 0)
                {
                    continue;
                }

                return $"{(v >> 8) & 0xFF:X}.{v & 0xFF:X2}";
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return null;
    }
}

/// <summary>
/// Installs the official firmware into a file-system root, the way Vita3K does (packages/src/pup.cpp): every SPKG of a
/// PUP that belongs to os0, vs0, sa0 or pd0 is decrypted, the parts of each partition are joined into an image, and
/// the image (FAT for os0/vs0/sa0, exFAT for pd0) is extracted into &lt;fsRoot&gt;/&lt;partition&gt;. The vs0 tar
/// patches the updater applies per console model are applied for the handheld PS Vita (Vita3K skips them).
/// <para>
/// The work happens in a staging folder inside <c>fsRoot</c>; only when everything was extracted are the installed
/// partitions swapped in (a cancelled or failed install leaves the previous firmware untouched). A full firmware
/// replaces os0, vs0 and pd0; sa0 is merged, so a font package installed before (or after) is kept. A PUP without vs0
/// (the separate font package) only merges what it carries and keeps the installed version.
/// </para>
/// </summary>
public static class FirmwareInstaller
{
    private const string StagingName = ".firmware-install";

    private static string? PartitionOf(SpkgType spkgType) => spkgType switch
    {
        SpkgType.Os0 => "os0",
        SpkgType.Vs0 => "vs0",
        SpkgType.Sa0 => "sa0",
        SpkgType.Pd0 => "pd0",
        _ => null,
    };

    /// <summary>The files an installation writes next to the partitions: its metadata and the module index.</summary>
    public static readonly string[] IndexFiles = [FirmwareInfo.MetadataFile, FirmwareLibraries.CacheFileName];

    /// <summary>
    /// Removes what a firmware installation created in <paramref name="fsRoot"/>: the partitions
    /// (<see cref="FirmwareInfo.PartitionNames"/>), a staging folder left behind and the <see cref="IndexFiles"/>. The
    /// memory card and the user's partitions (ux0, ur0, uma0, imc0, grw0, vd0 with the registry...) are never touched,
    /// and a junction or symbolic link (a partition linked to another emulator's firmware, or one inside it) is removed
    /// as a link, never what it points to. Afterwards the data folder is as before the first install, and installing
    /// again works the same. Returns the names removed.
    /// </summary>
    public static IReadOnlyList<string> Uninstall(string fsRoot)
    {
        var removed = new List<string>();
        if (!Directory.Exists(fsRoot))
        {
            return removed;
        }

        foreach (string name in FirmwareInfo.PartitionNames.Append(StagingName))
        {
            var dir = new DirectoryInfo(Path.Combine(fsRoot, name));
            if (dir.LinkTarget != null)
            {
                dir.Delete(recursive: false); // the link itself (also a broken one), never its target
                removed.Add(name);
            }
            else if (dir.Exists)
            {
                DeleteTree(dir.FullName);
                removed.Add(name);
            }
        }

        foreach (string name in IndexFiles)
        {
            string path = Path.Combine(fsRoot, name);
            if (File.Exists(path))
            {
                File.Delete(path);
                removed.Add(name);
            }
        }

        FirmwareLibraries.Forget(fsRoot);
        Log.Info($"firmware: uninstalled from {fsRoot} ({(removed.Count == 0 ? "nothing installed" : string.Join(", ", removed))})");
        return removed;
    }

    public static FirmwareInfo Install(string pupPath, string fsRoot, IProgress<FirmwareProgress>? progress = null, CancellationToken ct = default)
    {
        using var pup = Pup.Open(pupPath);
        return Install(pup, Path.GetFileName(pupPath), fsRoot, progress, ct);
    }

    /// <summary>After a system update install (which has no pd0: factory data, the home screen's BGM): copies Vita3K's
    /// pd0 when <paramref name="fsRoot"/> has none and Vita3K has one. Returns the new info, or null when nothing was
    /// copied.</summary>
    public static FirmwareInfo? ImportFactoryDataFromVita3KIfMissing(string fsRoot, CancellationToken cancellationToken = default)
    {
        if (HasFactoryData(fsRoot) || FindVita3KFirmware() is not { } v3k || !HasFactoryData(v3k))
        {
            return null;
        }

        try
        {
            return ImportFactoryData(v3k, fsRoot, cancellationToken);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Log.Warn($"firmware: pd0 from {v3k}: {e.Message}");
            return null;
        }
    }

    /// <summary>After a system update install (which has no sa0: the system fonts): copies the system fonts of Vita3K's
    /// sa0 when <paramref name="fsRoot"/> lacks some and Vita3K has them all. Returns the new info, or null when nothing
    /// was copied.</summary>
    public static FirmwareInfo? ImportSystemFontsFromVita3KIfMissing(string fsRoot, CancellationToken cancellationToken = default)
    {
        if (FirmwareInfo.HasSystemFonts(fsRoot) || FindVita3KFirmware() is not { } v3k || !FirmwareInfo.HasSystemFonts(v3k))
        {
            return null;
        }

        try
        {
            return ImportSystemFonts(v3k, fsRoot, cancellationToken);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Log.Warn($"firmware: sa0 fonts from {v3k}: {e.Message}");
            return null;
        }
    }

    /// <summary>
    /// Copies the system fonts (sa0:data/font, what the firmware font package installs) of an installed firmware
    /// (another emulator's, e.g. Vita3K's) into <paramref name="fsRoot"/>, merged into its sa0 as the font package is.
    /// </summary>
    public static FirmwareInfo ImportSystemFonts(string sourceFsRoot, string fsRoot, CancellationToken cancellationToken = default)
    {
        if (!FirmwareInfo.HasSystemFonts(sourceFsRoot))
        {
            throw new InvalidDataException($"{sourceFsRoot} has no system fonts (sa0/data/font/pgf and pvf)");
        }

        string src = Path.Combine(sourceFsRoot, "sa0", "data", "font");
        string staging = Path.Combine(fsRoot, StagingName);
        DeleteTree(staging);
        Directory.CreateDirectory(staging);
        try
        {
            foreach (string file in EnumerateFilesNoLinks(src))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string dst = Path.Combine(staging, "sa0", "data", "font", Path.GetRelativePath(src, file));
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                File.Copy(file, dst, overwrite: true);
            }
            Commit(Path.Combine(staging, "sa0"), Path.Combine(fsRoot, "sa0"), merge: true, staging);
            var previous = FirmwareInfo.Read(fsRoot);
            FirmwareInfo info = previous is null ? new FirmwareInfo(FirmwareInfo.DetectVersion(fsRoot), 0, "sa0", DateTime.UtcNow, true, ["sa0"])
                : previous with
                {
                    FontPackage = FirmwareInfo.HasSystemFonts(fsRoot),
                    Partitions = previous.Partitions.Union(["sa0"]).OrderBy(p => Array.IndexOf(FirmwareInfo.PartitionNames, p)).ToArray()
                };
            info.Write(fsRoot);
            Log.Info($"firmware: copied the system fonts (sa0:data/font) from {sourceFsRoot} into {fsRoot}");
            return info;
        }
        finally
        {
            DeleteTree(staging);
        }
    }

    /// <summary>pd0 (factory data) is installed in <paramref name="fsRoot"/>: pd0/data holds the system BGM
    /// (data/systembgm/home.at9, the shell's home screen music).</summary>
    public static bool HasFactoryData(string fsRoot) => Directory.Exists(Path.Combine(fsRoot, "pd0", "data"));

    /// <summary>
    /// Copies only pd0 (the factory data partition) from an installed firmware (another emulator's, e.g. Vita3K's) into
    /// <paramref name="fsRoot"/>, replacing the one there. No system update carries pd0, so a firmware installed from
    /// PSVUPDAT.PUP has none and the shell's home screen stays silent (pd0:data/systembgm/home.at9).
    /// </summary>
    public static FirmwareInfo ImportFactoryData(string sourceFsRoot, string fsRoot, CancellationToken cancellationToken = default)
    {
        string src = Path.Combine(sourceFsRoot, "pd0");
        if (!HasFactoryData(sourceFsRoot))
        {
            throw new InvalidDataException($"{sourceFsRoot} has no pd0 (pd0/data is missing)");
        }

        string staging = Path.Combine(fsRoot, StagingName);
        DeleteTree(staging);
        Directory.CreateDirectory(staging);
        try
        {
            foreach (string file in EnumerateFilesNoLinks(src))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string dst = Path.Combine(staging, "pd0", Path.GetRelativePath(src, file));
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                File.Copy(file, dst, overwrite: true);
            }
            Commit(Path.Combine(staging, "pd0"), Path.Combine(fsRoot, "pd0"), merge: false, staging);
            var previous = FirmwareInfo.Read(fsRoot);
            FirmwareInfo info = previous is null ? new FirmwareInfo(FirmwareInfo.DetectVersion(fsRoot), 0, "pd0", DateTime.UtcNow, FirmwareInfo.HasSystemFonts(fsRoot), ["pd0"])
                : previous with
                {
                    Partitions = previous.Partitions.Union(["pd0"]).OrderBy(p => Array.IndexOf(FirmwareInfo.PartitionNames, p)).ToArray()
                };
            info.Write(fsRoot);
            Log.Info($"firmware: copied pd0 from {sourceFsRoot} into {fsRoot}");
            return info;
        }
        finally
        {
            DeleteTree(staging);
        }
    }

    public static FirmwareInfo Install(Pup pup, string sourceName, string fsRoot, IProgress<FirmwareProgress>? progress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(fsRoot);
        string staging = Path.Combine(fsRoot, StagingName);
        DeleteTree(staging);
        Directory.CreateDirectory(staging);
        try
        {
            string version = pup.ReadVersionText() ?? pup.FirmwareVersionString;
            Log.Info($"firmware: installing {sourceName}: firmware {version} (header 0x{pup.FirmwareVersion:X8}), build {pup.BuildNumber}, {pup.Entries.Count} files");

            // 1. decrypt the partition parts, joined in package order (Vita3K numbers them in PUP order and joins sorted)
            var parts = new List<(PupEntry Entry, string Partition)>();
            var tarPatches = new List<PupEntry>();
            foreach (PupEntry e in pup.Entries)
            {
                if (e.KnownName != null || e.Length < 0x500)
                {
                    continue;
                }

                byte[] head = pup.Read(e with
                {
                    Length = Math.Min(e.Length, 0x1000)
                });
                if (!Spkg.TryReadHeader(head, out Spkg.Header h))
                {
                    continue;
                }

                string? part = PartitionOf(h.Type);
                if (part != null)
                {
                    parts.Add((e, part));
                }
                // files the updater adds to vs0 per console model: take the ones for every model and for the handheld
                // (target 2 = PS TV: its own system apps and location provider)
                else if (h.Type == SpkgType.Vs0TarPatch && h.Target is 0 or 1)
                {
                    tarPatches.Add(e);
                }
            }
            if (parts.Count == 0)
            {
                throw new InvalidDataException("The PUP holds no os0/vs0/sa0/pd0 partition (is it a Vita system update?)");
            }

            long totalPkg = parts.Sum(p => p.Entry.Length), donePkg = 0;
            var images = new Dictionary<string, FileStream>();
            try
            {
                foreach ((PupEntry? entry, string? part) in parts)
                {
                    ct.ThrowIfCancellationRequested();
                    progress?.Report(new FirmwareProgress("Descriptografando", 0.5 * donePkg / totalPkg, part));
                    byte[] data = Spkg.Decrypt(pup.Read(entry));
                    if (!images.TryGetValue(part, out FileStream? img))
                    {
                        images[part] = img = new FileStream(Path.Combine(staging, part + ".img"), FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 16);
                    }

                    img.Write(data);
                    donePkg += entry.Length;
                }

                // 2. extract each image
                long totalBytes = 0, doneBytes = 0;
                var listings = new Dictionary<string, (FsImage Image, List<FsImageEntry> Entries)>();
                foreach ((string? part, FileStream? img) in images)
                {
                    img.Flush();
                    var fs = FsImage.Open(img);
                    var list = fs.Enumerate().ToList();
                    listings[part] = (fs, list);
                    totalBytes += list.Where(x => !x.IsDirectory).Sum(x => x.Size);
                }
                int files = 0;
                foreach (string part in FirmwareInfo.PartitionNames)
                {
                    if (!listings.TryGetValue(part, out (FsImage Image, List<FsImageEntry> Entries) l))
                    {
                        continue;
                    }

                    string dest = Path.Combine(staging, part);
                    Directory.CreateDirectory(dest);
                    foreach (FsImageEntry fsImageEntry in l.Entries)
                    {
                        ct.ThrowIfCancellationRequested();
                        string host = Path.Combine(dest, fsImageEntry.Path.Replace('/', Path.DirectorySeparatorChar));
                        if (fsImageEntry.IsDirectory)
                        {
                            Directory.CreateDirectory(host);
                            continue;
                        }
                        Directory.CreateDirectory(Path.GetDirectoryName(host)!);
                        using (var o = new FileStream(host, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
                        {
                            l.Image.CopyTo(fsImageEntry, o, ct);
                        }

                        doneBytes += fsImageEntry.Size;
                        files++;
                        if ((files & 31) == 0 || fsImageEntry.Size > 1 << 20)
                        {
                            progress?.Report(new FirmwareProgress("Extraindo", 0.5 + 0.45 * doneBytes / Math.Max(1, totalBytes), part + ":" + fsImageEntry.Path));
                        }
                    }
                    Log.Info($"firmware: {part}: {l.Entries.Count(x => !x.IsDirectory)} files, {l.Entries.Where(x => !x.IsDirectory).Sum(x => x.Size) / 1024} KiB");
                }
                if (listings.ContainsKey("vs0"))
                {
                    foreach (PupEntry tp in tarPatches)
                    {
                        ct.ThrowIfCancellationRequested();
                        int n = ApplyTar(Spkg.Decrypt(pup.Read(tp)), Path.Combine(staging, "vs0"));
                        Log.Info($"firmware: vs0 tar patch 0x{tp.Type:X}: {n} files");
                    }
                }
            }
            finally
            {
                foreach (FileStream img in images.Values)
                {
                    img.Dispose();
                }

                foreach (string part in images.Keys)
                {
                    TryDeleteFile(Path.Combine(staging, part + ".img"));
                }
            }

            // 3. swap the partitions in
            ct.ThrowIfCancellationRequested();
            progress?.Report(new FirmwareProgress("Instalando", 0.96));
            var installed = parts.Select(p => p.Partition).Distinct().ToList();
            bool full = installed.Contains("vs0");
            var previous = FirmwareInfo.Read(fsRoot);
            foreach (string? part in installed)
            {
                bool merge = part == "sa0" || !full;
                Commit(Path.Combine(staging, part), Path.Combine(fsRoot, part), merge, staging);
            }

            FirmwareInfo info = full
                ? new FirmwareInfo(version, pup.BuildNumber, sourceName, DateTime.UtcNow, FirmwareInfo.HasSystemFonts(fsRoot),
                    installed.Union(previous?.Partitions ?? []).Where(FirmwareInfo.PartitionNames.Contains).OrderBy(p => Array.IndexOf(FirmwareInfo.PartitionNames, p)).ToArray())
                : new FirmwareInfo(previous?.Version, previous?.Build ?? 0, previous?.Source is { Length: > 0 } s ? s : sourceName, DateTime.UtcNow, FirmwareInfo.HasSystemFonts(fsRoot),
                    installed.Union(previous?.Partitions ?? []).OrderBy(p => Array.IndexOf(FirmwareInfo.PartitionNames, p)).ToArray());
            info.Write(fsRoot);
            progress?.Report(new FirmwareProgress(Localization.Loc.Get("FwDone"), 1.0));
            Log.Info($"firmware: installed {info.Display} into {fsRoot} ({string.Join(", ", installed)})");
            return info;
        }
        finally
        {
            DeleteTree(staging);
        }
    }

    /// <summary>
    /// Copies an already installed firmware (another emulator's os0/vs0/sa0/pd0, e.g. Vita3K's) into
    /// <paramref name="fsRoot"/>. Copying rather than linking keeps the source untouched by anything VitaSharp writes
    /// and lets either side be removed or upgraded on its own.
    /// </summary>
    public static FirmwareInfo ImportFrom(string sourceFsRoot, string fsRoot, string sourceName, IProgress<FirmwareProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (Path.GetFullPath(sourceFsRoot).TrimEnd(Path.DirectorySeparatorChar).Equals(Path.GetFullPath(fsRoot).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Source and destination are the same folder");
        }

        var parts = FirmwareInfo.PartitionNames.Where(p => Directory.Exists(Path.Combine(sourceFsRoot, p))).ToList();
        if (!parts.Contains("vs0") || !Directory.Exists(Path.Combine(sourceFsRoot, "vs0", "sys", "external")))
        {
            throw new InvalidDataException($"{sourceFsRoot} has no installed firmware (vs0/sys/external is missing)");
        }

        Directory.CreateDirectory(fsRoot);
        string staging = Path.Combine(fsRoot, StagingName);
        DeleteTree(staging);
        Directory.CreateDirectory(staging);
        try
        {
            var files = parts.SelectMany(p => EnumerateFilesNoLinks(Path.Combine(sourceFsRoot, p)).Select(f => (Part: p, File: f))).ToList();
            long total = files.Sum(f => new FileInfo(f.File).Length), done = 0;
            int n = 0;
            foreach ((string? part, string? file) in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string rel = Path.GetRelativePath(Path.Combine(sourceFsRoot, part), file);
                string dst = Path.Combine(staging, part, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                File.Copy(file, dst, overwrite: true);
                done += new FileInfo(dst).Length;
                if ((++n & 31) == 0)
                {
                    progress?.Report(new FirmwareProgress("Copiando", 0.95 * done / Math.Max(1, total), part + ":" + rel.Replace('\\', '/')));
                }
            }
            progress?.Report(new FirmwareProgress("Instalando", 0.96));
            foreach (string? part in parts)
            {
                Commit(Path.Combine(staging, part), Path.Combine(fsRoot, part), merge: part == "sa0", staging);
            }

            var info = new FirmwareInfo(FirmwareInfo.DetectVersion(fsRoot), 0, sourceName, DateTime.UtcNow, FirmwareInfo.HasSystemFonts(fsRoot), parts);
            info.Write(fsRoot);
            progress?.Report(new FirmwareProgress(Localization.Loc.Get("FwDone"), 1.0));
            Log.Info($"firmware: copied {info.Display} from {sourceFsRoot} into {fsRoot}");
            return info;
        }
        finally
        {
            DeleteTree(staging);
        }
    }

    /// <summary>The folder of a Vita3K installation that holds an installed firmware, or null. Vita3K keeps its
    /// emulated file system in its pref path (Windows %APPDATA%\Vita3K\Vita3K, Linux ~/.local/share/Vita3K/Vita3K,
    /// macOS ~/Library/Application Support/Vita3K/Vita3K).</summary>
    public static string? FindVita3KFirmware()
    {
        foreach (string c in Vita3KRoots())
        {
            try
            {
                string ext = Path.Combine(c, "vs0", "sys", "external");
                if (Directory.Exists(ext) && Directory.EnumerateFiles(ext, "*.suprx").Any())
                {
                    return c;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return null;
    }

    /// <summary>Where Vita3K keeps its data folder (ux0, vs0, ...) on this platform.</summary>
    public static IEnumerable<string> Vita3KRoots()
    {
        var candidates = new List<string>();
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsWindows())
        {
            candidates.Add(Path.Combine(appData, "Vita3K", "Vita3K"));
        }
        else if (OperatingSystem.IsMacOS())
        {
            candidates.Add(Path.Combine(home, "Library", "Application Support", "Vita3K", "Vita3K"));
        }
        else
        {
            string? xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            candidates.Add(Path.Combine(!string.IsNullOrEmpty(xdg) && Path.IsPathRooted(xdg) ? xdg : Path.Combine(home, ".local", "share"), "Vita3K", "Vita3K"));
        }
        return candidates;
    }

    /// <summary>Moves a staged partition into place. Replace: the old folder (or a link standing for it) goes away
    /// first. Merge: staged files overwrite same-named ones, others stay.</summary>
    private static void Commit(string staged, string target, bool merge, string staging)
    {
        if (!Directory.Exists(staged))
        {
            return;
        }

        bool targetIsLink = Directory.Exists(target) && new DirectoryInfo(target).Attributes.HasFlag(FileAttributes.ReparsePoint);
        if (targetIsLink)
        {
            // a junction/symlink to someone else's firmware: drop the link itself, never what it points to
            Directory.Delete(target, recursive: false);
        }
        if (merge && Directory.Exists(target))
        {
            foreach (string file in Directory.EnumerateFiles(staged, "*", SearchOption.AllDirectories))
            {
                string dst = Path.Combine(target, Path.GetRelativePath(staged, file));
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                File.Move(file, dst, overwrite: true);
            }
            return;
        }
        if (Directory.Exists(target))
        {
            string old = Path.Combine(staging, "old-" + Path.GetFileName(target));
            DeleteTree(old);
            Directory.Move(target, old);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        Directory.Move(staged, target);
    }

    /// <summary>Extracts the regular files of a ustar/v7 tar archive under <paramref name="dest"/> (overwriting);
    /// returns how many were written. Names that would leave <paramref name="dest"/> are skipped.</summary>
    internal static int ApplyTar(byte[] tar, string dest)
    {
        int count = 0;
        string root = Path.GetFullPath(dest) + Path.DirectorySeparatorChar;
        for (long o = 0; o + 512 <= tar.Length;)
        {
            Span<byte> h = tar.AsSpan((int)o, 512);
            if (h[0] == 0)
            {
                break; // end-of-archive blocks
            }

            string name = System.Text.Encoding.UTF8.GetString(h[..100]).TrimEnd('\0');
            if (h.Slice(257, 5).SequenceEqual("ustar"u8))
            {
                string prefix = System.Text.Encoding.UTF8.GetString(h.Slice(345, 155)).TrimEnd('\0');
                if (prefix.Length > 0)
                {
                    name = prefix + "/" + name;
                }
            }
            string sizeText = System.Text.Encoding.ASCII.GetString(h.Slice(124, 12)).Trim('\0', ' ');
            long size = sizeText.Length == 0 ? 0 : Convert.ToInt64(sizeText, 8);
            byte type = h[156];
            long data = o + 512;
            if (size < 0 || data + size > tar.Length)
            {
                throw new InvalidDataException($"tar entry {name} is truncated");
            }

            string host = Path.GetFullPath(Path.Combine(dest, name.TrimStart('/').Replace('/', Path.DirectorySeparatorChar)));
            bool inside = host.StartsWith(root, StringComparison.OrdinalIgnoreCase);
            if (inside && type == (byte)'5')
            {
                Directory.CreateDirectory(host);
            }
            else if (inside && type is (byte)'0' or 0 && !name.EndsWith('/'))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(host)!);
                File.WriteAllBytes(host, tar.AsSpan((int)data, (int)size).ToArray());
                count++;
            }
            o = data + ((size + 511) & ~511L);
        }
        return count;
    }

    private static IEnumerable<string> EnumerateFilesNoLinks(string dir)
    {
        var stack = new Stack<string>();
        stack.Push(dir);
        while (stack.Count > 0)
        {
            string d = stack.Pop();
            foreach (string f in Directory.EnumerateFiles(d))
            {
                yield return f;
            }

            foreach (string sub in Directory.EnumerateDirectories(d))
            {
                if (!new DirectoryInfo(sub).Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    stack.Push(sub);
                }
            }
        }
    }

    /// <summary>Deletes a folder tree without following junctions or symbolic links (they are removed as links).</summary>
    internal static void DeleteTree(string dir)
    {
        if (!Directory.Exists(dir))
        {
            return;
        }

        var info = new DirectoryInfo(dir);
        if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            info.Delete(recursive: false);
            return;
        }
        foreach (DirectoryInfo sub in info.EnumerateDirectories())
        {
            DeleteTree(sub.FullName);
        }

        foreach (FileInfo f in info.EnumerateFiles())
        {
            if (f.Attributes.HasFlag(FileAttributes.ReadOnly))
            {
                f.Attributes &= ~FileAttributes.ReadOnly;
            }

            f.Delete();
        }
        info.Delete(recursive: false);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
