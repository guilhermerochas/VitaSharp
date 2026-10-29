using System.Buffers.Binary;
using System.Text.Json;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Firmware;

/// <summary>Progress of a firmware installation: <see cref="Fraction"/> 0..1 over the whole job.</summary>
public readonly record struct FirmwareProgress(string Stage, double Fraction, string? Detail = null);

/// <summary>What is installed in a file-system root (&lt;DataRoot&gt;/fs), from its firmware.json or detected.</summary>
public sealed record FirmwareInfo(string? Version, uint Build, string Source, DateTime? InstalledUtc, bool FontPackage, IReadOnlyList<string> Partitions)
{
    public const string MetadataFile = "firmware.json";

    /// <summary>Firmware partitions VitaSharp installs (the same folders Vita3K fills).</summary>
    public static readonly string[] PartitionNames = ["os0", "vs0", "sa0", "pd0"];

    /// <summary>Text for the launcher: "3.74", "3.74 + fontes" or "instalado (versão desconhecida)".</summary>
    public string Display => (Version ?? "versão desconhecida") + (FontPackage ? " + pacote de fontes" : "");

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
                var d = JsonSerializer.Deserialize(File.ReadAllText(meta), CoreJson.Indented.SortedDictionaryStringString) ?? new();
                var parts = d.GetValueOrDefault("partitions", "").Split(',', StringSplitOptions.RemoveEmptyEntries);
                if (!hasModules && parts.Contains("vs0")) return null; // metadata left behind after vs0 was removed
                return new FirmwareInfo(d.GetValueOrDefault("version"), uint.TryParse(d.GetValueOrDefault("build"), out var b) ? b : 0,
                    d.GetValueOrDefault("source", ""),
                    DateTime.TryParse(d.GetValueOrDefault("installed"), null, System.Globalization.DateTimeStyles.RoundtripKind, out var t) ? t : null,
                    d.GetValueOrDefault("font_package") == "true", parts);
            }
            catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
            {
                Log.Warn($"firmware: cannot read {meta}: {e.Message}");
            }
        }
        if (!hasModules) return null;
        return new FirmwareInfo(DetectVersion(fsRoot), 0, "", null, false,
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

    static bool HasModules(string fsRoot)
    {
        var ext = Path.Combine(fsRoot, "vs0", "sys", "external");
        try { return Directory.Exists(ext) && Directory.EnumerateFiles(ext, "*.suprx").Any(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>"3.74" from the app-info system-software version (0x00000374_00000000 for 3.74) of a firmware module.</summary>
    internal static string? DetectVersion(string fsRoot)
    {
        foreach (var rel in new[] { "vs0/sys/external/libc.suprx", "vs0/sys/external/libfios2.suprx", "os0/psp2bootconfig.skprx" })
        {
            var path = Path.Combine(fsRoot, rel.Replace('/', Path.DirectorySeparatorChar));
            try
            {
                if (!File.Exists(path)) continue;
                var head = new byte[0x400];
                using (var f = File.OpenRead(path)) f.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
                if (BinaryPrimitives.ReadUInt32LittleEndian(head) != 0x00454353) continue;
                long appinfo = (long)BinaryPrimitives.ReadUInt64LittleEndian(head.AsSpan(32 + 24));
                if (appinfo <= 0 || appinfo + 24 > head.Length) continue;
                ulong sysver = BinaryPrimitives.ReadUInt64LittleEndian(head.AsSpan((int)appinfo + 16));
                uint v = (uint)(sysver >> 32);
                if (v == 0) continue;
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
    const string StagingName = ".firmware-install";

    static string? PartitionOf(SpkgType t) => t switch
    {
        SpkgType.Os0 => "os0", SpkgType.Vs0 => "vs0", SpkgType.Sa0 => "sa0", SpkgType.Pd0 => "pd0", _ => null,
    };

    public static FirmwareInfo Install(string pupPath, string fsRoot, IProgress<FirmwareProgress>? progress = null, CancellationToken ct = default)
    {
        using var pup = Pup.Open(pupPath);
        return Install(pup, Path.GetFileName(pupPath), fsRoot, progress, ct);
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
            foreach (var e in pup.Entries)
            {
                if (e.KnownName != null || e.Length < 0x500) continue;
                var head = pup.Read(e with { Length = Math.Min(e.Length, 0x1000) });
                if (!Spkg.TryReadHeader(head, out var h)) continue;
                var part = PartitionOf(h.Type);
                if (part != null) parts.Add((e, part));
                // files the updater adds to vs0 per console model: take the ones for every model and for the handheld
                // (target 2 = PS TV: its own system apps and location provider)
                else if (h.Type == SpkgType.Vs0TarPatch && h.Target is 0 or 1) tarPatches.Add(e);
            }
            if (parts.Count == 0) throw new InvalidDataException("The PUP holds no os0/vs0/sa0/pd0 partition (is it a Vita system update?)");

            long totalPkg = parts.Sum(p => p.Entry.Length), donePkg = 0;
            var images = new Dictionary<string, FileStream>();
            try
            {
                foreach (var (entry, part) in parts)
                {
                    ct.ThrowIfCancellationRequested();
                    progress?.Report(new FirmwareProgress("Descriptografando", 0.5 * donePkg / totalPkg, part));
                    var data = Spkg.Decrypt(pup.Read(entry));
                    if (!images.TryGetValue(part, out var img))
                        images[part] = img = new FileStream(Path.Combine(staging, part + ".img"), FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 16);
                    img.Write(data);
                    donePkg += entry.Length;
                }

                // 2. extract each image
                long totalBytes = 0, doneBytes = 0;
                var listings = new Dictionary<string, (FsImage Image, List<FsImageEntry> Entries)>();
                foreach (var (part, img) in images)
                {
                    img.Flush();
                    var fs = FsImage.Open(img);
                    var list = fs.Enumerate().ToList();
                    listings[part] = (fs, list);
                    totalBytes += list.Where(x => !x.IsDirectory).Sum(x => x.Size);
                }
                int files = 0;
                foreach (var part in FirmwareInfo.PartitionNames)
                {
                    if (!listings.TryGetValue(part, out var l)) continue;
                    string dest = Path.Combine(staging, part);
                    Directory.CreateDirectory(dest);
                    foreach (var e in l.Entries)
                    {
                        ct.ThrowIfCancellationRequested();
                        string host = Path.Combine(dest, e.Path.Replace('/', Path.DirectorySeparatorChar));
                        if (e.IsDirectory) { Directory.CreateDirectory(host); continue; }
                        Directory.CreateDirectory(Path.GetDirectoryName(host)!);
                        using (var o = new FileStream(host, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
                            l.Image.CopyTo(e, o, ct);
                        doneBytes += e.Size;
                        files++;
                        if ((files & 31) == 0 || e.Size > 1 << 20)
                            progress?.Report(new FirmwareProgress("Extraindo", 0.5 + 0.45 * doneBytes / Math.Max(1, totalBytes), part + ":" + e.Path));
                    }
                    Log.Info($"firmware: {part}: {l.Entries.Count(x => !x.IsDirectory)} files, {l.Entries.Where(x => !x.IsDirectory).Sum(x => x.Size) / 1024} KiB");
                }
                if (listings.ContainsKey("vs0"))
                {
                    foreach (var tp in tarPatches)
                    {
                        ct.ThrowIfCancellationRequested();
                        int n = ApplyTar(Spkg.Decrypt(pup.Read(tp)), Path.Combine(staging, "vs0"));
                        Log.Info($"firmware: vs0 tar patch 0x{tp.Type:X}: {n} files");
                    }
                }
            }
            finally
            {
                foreach (var img in images.Values) img.Dispose();
                foreach (var part in images.Keys) TryDeleteFile(Path.Combine(staging, part + ".img"));
            }

            // 3. swap the partitions in
            ct.ThrowIfCancellationRequested();
            progress?.Report(new FirmwareProgress("Instalando", 0.96));
            var installed = parts.Select(p => p.Partition).Distinct().ToList();
            bool full = installed.Contains("vs0");
            var previous = FirmwareInfo.Read(fsRoot);
            foreach (var part in installed)
            {
                bool merge = part == "sa0" || !full;
                Commit(Path.Combine(staging, part), Path.Combine(fsRoot, part), merge, staging);
            }

            var info = full
                ? new FirmwareInfo(version, pup.BuildNumber, sourceName, DateTime.UtcNow, previous?.FontPackage ?? false,
                    installed.Union(previous?.Partitions ?? []).Where(FirmwareInfo.PartitionNames.Contains).OrderBy(p => Array.IndexOf(FirmwareInfo.PartitionNames, p)).ToArray())
                : new FirmwareInfo(previous?.Version, previous?.Build ?? 0, previous?.Source is { Length: > 0 } s ? s : sourceName, DateTime.UtcNow, true,
                    installed.Union(previous?.Partitions ?? []).OrderBy(p => Array.IndexOf(FirmwareInfo.PartitionNames, p)).ToArray());
            info.Write(fsRoot);
            progress?.Report(new FirmwareProgress("Concluído", 1.0));
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
    public static FirmwareInfo ImportFrom(string sourceFsRoot, string fsRoot, string sourceName, IProgress<FirmwareProgress>? progress = null, CancellationToken ct = default)
    {
        if (Path.GetFullPath(sourceFsRoot).TrimEnd(Path.DirectorySeparatorChar).Equals(Path.GetFullPath(fsRoot).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Source and destination are the same folder");
        var parts = FirmwareInfo.PartitionNames.Where(p => Directory.Exists(Path.Combine(sourceFsRoot, p))).ToList();
        if (!parts.Contains("vs0") || !Directory.Exists(Path.Combine(sourceFsRoot, "vs0", "sys", "external")))
            throw new InvalidDataException($"{sourceFsRoot} has no installed firmware (vs0/sys/external is missing)");
        Directory.CreateDirectory(fsRoot);
        string staging = Path.Combine(fsRoot, StagingName);
        DeleteTree(staging);
        Directory.CreateDirectory(staging);
        try
        {
            var files = parts.SelectMany(p => EnumerateFilesNoLinks(Path.Combine(sourceFsRoot, p)).Select(f => (Part: p, File: f))).ToList();
            long total = files.Sum(f => new FileInfo(f.File).Length), done = 0;
            int n = 0;
            foreach (var (part, file) in files)
            {
                ct.ThrowIfCancellationRequested();
                string rel = Path.GetRelativePath(Path.Combine(sourceFsRoot, part), file);
                string dst = Path.Combine(staging, part, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                File.Copy(file, dst, overwrite: true);
                done += new FileInfo(dst).Length;
                if ((++n & 31) == 0) progress?.Report(new FirmwareProgress("Copiando", 0.95 * done / Math.Max(1, total), part + ":" + rel.Replace('\\', '/')));
            }
            progress?.Report(new FirmwareProgress("Instalando", 0.96));
            foreach (var part in parts) Commit(Path.Combine(staging, part), Path.Combine(fsRoot, part), merge: part == "sa0", staging);
            var info = new FirmwareInfo(FirmwareInfo.DetectVersion(fsRoot), 0, sourceName, DateTime.UtcNow, false, parts);
            info.Write(fsRoot);
            progress?.Report(new FirmwareProgress("Concluído", 1.0));
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
        var candidates = new List<string>();
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsWindows()) candidates.Add(Path.Combine(appData, "Vita3K", "Vita3K"));
        else if (OperatingSystem.IsMacOS()) candidates.Add(Path.Combine(home, "Library", "Application Support", "Vita3K", "Vita3K"));
        else
        {
            var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            candidates.Add(Path.Combine(!string.IsNullOrEmpty(xdg) && Path.IsPathRooted(xdg) ? xdg : Path.Combine(home, ".local", "share"), "Vita3K", "Vita3K"));
        }
        foreach (var c in candidates)
        {
            try
            {
                var ext = Path.Combine(c, "vs0", "sys", "external");
                if (Directory.Exists(ext) && Directory.EnumerateFiles(ext, "*.suprx").Any()) return c;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return null;
    }

    /// <summary>Moves a staged partition into place. Replace: the old folder (or a link standing for it) goes away
    /// first. Merge: staged files overwrite same-named ones, others stay.</summary>
    static void Commit(string staged, string target, bool merge, string staging)
    {
        if (!Directory.Exists(staged)) return;
        bool targetIsLink = Directory.Exists(target) && new DirectoryInfo(target).Attributes.HasFlag(FileAttributes.ReparsePoint);
        if (targetIsLink)
        {
            // a junction/symlink to someone else's firmware: drop the link itself, never what it points to
            Directory.Delete(target, recursive: false);
        }
        if (merge && Directory.Exists(target))
        {
            foreach (var file in Directory.EnumerateFiles(staged, "*", SearchOption.AllDirectories))
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
            var h = tar.AsSpan((int)o, 512);
            if (h[0] == 0) break; // end-of-archive blocks
            string name = System.Text.Encoding.UTF8.GetString(h[..100]).TrimEnd('\0');
            if (h.Slice(257, 5).SequenceEqual("ustar"u8))
            {
                string prefix = System.Text.Encoding.UTF8.GetString(h.Slice(345, 155)).TrimEnd('\0');
                if (prefix.Length > 0) name = prefix + "/" + name;
            }
            string sizeText = System.Text.Encoding.ASCII.GetString(h.Slice(124, 12)).Trim('\0', ' ');
            long size = sizeText.Length == 0 ? 0 : Convert.ToInt64(sizeText, 8);
            byte type = h[156];
            long data = o + 512;
            if (size < 0 || data + size > tar.Length) throw new InvalidDataException($"tar entry {name} is truncated");
            string host = Path.GetFullPath(Path.Combine(dest, name.TrimStart('/').Replace('/', Path.DirectorySeparatorChar)));
            bool inside = host.StartsWith(root, StringComparison.OrdinalIgnoreCase);
            if (inside && type == (byte)'5') Directory.CreateDirectory(host);
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

    static IEnumerable<string> EnumerateFilesNoLinks(string dir)
    {
        var stack = new Stack<string>();
        stack.Push(dir);
        while (stack.Count > 0)
        {
            var d = stack.Pop();
            foreach (var f in Directory.EnumerateFiles(d)) yield return f;
            foreach (var sub in Directory.EnumerateDirectories(d))
                if (!new DirectoryInfo(sub).Attributes.HasFlag(FileAttributes.ReparsePoint)) stack.Push(sub);
        }
    }

    /// <summary>Deletes a folder tree without following junctions or symbolic links (they are removed as links).</summary>
    internal static void DeleteTree(string dir)
    {
        if (!Directory.Exists(dir)) return;
        var info = new DirectoryInfo(dir);
        if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)) { info.Delete(recursive: false); return; }
        foreach (var sub in info.EnumerateDirectories()) DeleteTree(sub.FullName);
        foreach (var f in info.EnumerateFiles())
        {
            if (f.Attributes.HasFlag(FileAttributes.ReadOnly)) f.Attributes &= ~FileAttributes.ReadOnly;
            f.Delete();
        }
        info.Delete(recursive: false);
    }

    static void TryDeleteFile(string path)
    {
        try { File.Delete(path); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
