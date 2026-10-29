using System.Security.Cryptography;
using VitaSharp.Core.Firmware;
using VitaSharp.Core.Loader.Self;
using VitaSharp.Core.Util;

namespace VitaSharp.Desktop;

/// <summary>
/// install-firmware &lt;PSVUPDAT.PUP | folder | --vita3k&gt;: installs the official firmware into &lt;DataRoot&gt;/fs (os0, vs0,
/// sa0, pd0). A folder (or --vita3k) is an already installed firmware to copy, e.g. Vita3K's. Ctrl+C cancels and
/// leaves the previous firmware in place.
/// firmware-info: prints what is installed. firmware-verify [other fs root]: checks that the key modules decrypt and
/// optionally compares the installed files with another installation (hashes).
/// </summary>
public static class FirmwareCli
{
    public static int Install(string source, string[] options)
    {
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch (IOException) { } // stage names are Portuguese (launcher)
        using var cts = new CancellationTokenSource();
        ConsoleCancelEventHandler onCancel = (_, e) => { e.Cancel = true; cts.Cancel(); Console.WriteLine("\nCancelling…"); };
        Console.CancelKeyPress += onCancel;
        int lastPct = -1;
        string lastStage = "";
        var progress = new SyncProgress(p =>
        {
            int pct = (int)(p.Fraction * 100);
            if (pct == lastPct && p.Stage == lastStage) return;
            lastPct = pct;
            lastStage = p.Stage;
            Console.WriteLine($"  {pct,3}%  {p.Stage}{(p.Detail != null ? "  " + p.Detail : "")}");
        });
        try
        {
            FirmwareInfo info;
            if (source == "--vita3k")
            {
                var v3k = FirmwareInstaller.FindVita3KFirmware();
                if (v3k == null) { Console.Error.WriteLine("No Vita3K installation with firmware found."); return 1; }
                Console.WriteLine($"Copying the firmware installed in Vita3K ({v3k}) into {Program.FsRoot}");
                info = FirmwareInstaller.ImportFrom(v3k, Program.FsRoot, "Vita3K", progress, cts.Token);
            }
            else if (Directory.Exists(source))
            {
                Console.WriteLine($"Copying the firmware in {source} into {Program.FsRoot}");
                info = FirmwareInstaller.ImportFrom(source, Program.FsRoot, Path.GetFileName(Path.TrimEndingDirectorySeparator(source)), progress, cts.Token);
            }
            else
            {
                Console.WriteLine($"Installing {source} into {Program.FsRoot}");
                info = FirmwareInstaller.Install(source, Program.FsRoot, progress, cts.Token);
            }
            Console.WriteLine($"Installed firmware {info.Display} ({string.Join(", ", info.Partitions)})");
            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Cancelled; the previous firmware (if any) was left as it was.");
            return 2;
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Console.Error.WriteLine("Firmware install failed: " + e.Message);
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
        }
    }

    public static int Info()
    {
        var info = FirmwareInfo.Read(Program.FsRoot);
        Console.WriteLine(info == null
            ? $"No firmware installed in {Program.FsRoot}"
            : $"Firmware {info.Display}, partitions {string.Join(", ", info.Partitions)}{(info.Source.Length > 0 ? ", from " + info.Source : "")}{(info.InstalledUtc is { } t ? $", installed {t.ToLocalTime():g}" : "")}");
        return info == null ? 1 : 0;
    }

    /// <summary>Modules and data stage 2 relies on.</summary>
    static readonly string[] KeyFiles =
    [
        "vs0/sys/external/libSceJson.suprx", "vs0/sys/external/libSceXml.suprx", "vs0/sys/external/libSceSqlite.suprx",
        "vs0/sys/external/libult.suprx", "vs0/sys/external/libfiber.suprx", "vs0/sys/external/libc.suprx",
    ];

    public static int Verify(string[] args)
    {
        string root = Program.FsRoot;
        int bad = 0;
        foreach (var rel in KeyFiles)
        {
            var path = Path.Combine(root, rel);
            if (!File.Exists(path)) { Console.WriteLine($"MISSING  {rel}"); bad++; continue; }
            try
            {
                var elf = SelfDecryptor.ToElf(File.ReadAllBytes(path), null);
                Console.WriteLine($"OK       {rel} -> ELF {elf.Length} bytes");
            }
            catch (Exception e) { Console.WriteLine($"FAIL     {rel}: {e.Message}"); bad++; }
        }
        // every module in vs0:sys/external must decrypt
        int mods = 0, modFail = 0;
        foreach (var f in Directory.EnumerateFiles(Path.Combine(root, "vs0", "sys", "external"), "*.suprx"))
        {
            mods++;
            try { SelfDecryptor.ToElf(File.ReadAllBytes(f), null); }
            catch (Exception e) { modFail++; Console.WriteLine($"FAIL     vs0/sys/external/{Path.GetFileName(f)}: {e.Message}"); }
        }
        Console.WriteLine($"vs0:sys/external: {mods - modFail}/{mods} modules decrypt");
        bad += modFail;
        foreach (var dir in new[] { "sa0/data/font/pgf", "sa0/data/font/pvf" })
        {
            var d = Path.Combine(root, dir);
            int n = Directory.Exists(d) ? Directory.EnumerateFiles(d).Count() : 0;
            Console.WriteLine($"{(n > 0 ? "OK      " : "MISSING ")} {dir}: {n} files");
            if (n == 0) bad++;
        }
        if (args.Length > 0) bad += Compare(root, args[0]);
        return bad == 0 ? 0 : 1;
    }

    /// <summary>File list + SHA-256 comparison of os0/vs0/sa0/pd0 with another installation.</summary>
    static int Compare(string mine, string other)
    {
        int diffs = 0;
        foreach (var part in FirmwareInfo.PartitionNames)
        {
            string a = Path.Combine(mine, part), b = Path.Combine(other, part);
            var fa = List(a);
            var fb = List(b);
            int same = 0, differ = 0;
            var onlyA = fa.Keys.Except(fb.Keys, StringComparer.OrdinalIgnoreCase).Order().ToList();
            var onlyB = fb.Keys.Except(fa.Keys, StringComparer.OrdinalIgnoreCase).Order().ToList();
            foreach (var rel in fa.Keys.Intersect(fb.Keys, StringComparer.OrdinalIgnoreCase))
            {
                if (Hash(fa[rel]) == Hash(fb[rel])) same++;
                else { differ++; if (differ <= 20) Console.WriteLine($"  DIFF   {part}/{rel}"); }
            }
            Console.WriteLine($"{part}: {same} identical, {differ} different, {onlyA.Count} only here, {onlyB.Count} only in {other}");
            foreach (var r in onlyA.Take(20)) Console.WriteLine($"  +      {part}/{r}");
            foreach (var r in onlyB.Take(20)) Console.WriteLine($"  -      {part}/{r}");
            diffs += differ;
        }
        return diffs;
    }

    static Dictionary<string, string> List(string dir)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(dir)) return d;
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            d[Path.GetRelativePath(dir, f).Replace('\\', '/')] = f;
        return d;
    }

    static string Hash(string path)
    {
        using var f = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(f));
    }

    /// <summary>IProgress that reports on the caller's thread (Progress&lt;T&gt; posts to the thread pool).</summary>
    sealed class SyncProgress(Action<FirmwareProgress> report) : IProgress<FirmwareProgress>
    {
        public void Report(FirmwareProgress value) => report(value);
    }
}
