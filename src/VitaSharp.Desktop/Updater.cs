using System.Diagnostics;
using System.Reflection;
using VitaSharp.Core.Update;
using VitaSharp.Core.Util;

namespace VitaSharp.Desktop;

/// <summary>
/// Emulator updates from the GitHub releases (PCSX2-style): the check, the download with its SHA256SUMS check, and the
/// swap. The running executable cannot overwrite itself on Windows, so the new package's own executable does the copy
/// (<c>apply-update</c>) after this process exits, then starts the updated emulator again. Only release packages built
/// by the CI know their repository (<c>VitaSharpUpdateRepository</c>) and can update themselves; a build from source
/// is updated with git. <see cref="EnvVars.UpdateFeed"/> points the check at another feed (tests).
/// </summary>
public static class Updater
{
    static string? Metadata(string key) =>
        typeof(Updater).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == key)?.Value is { Length: > 0 } v ? v : null;

    /// <summary>The running build (version and commit from InformationalVersion, time of the executable).</summary>
    public static BuildIdentity Current { get; } = BuildIdentity.Parse(
        (Assembly.GetEntryAssembly() ?? typeof(Updater).Assembly).GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
        BuildTime());

    static DateTimeOffset BuildTime()
    {
        try { return File.GetLastWriteTimeUtc(Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "VitaSharp.Core.dll")); }
        catch (Exception) { return DateTimeOffset.UtcNow; }
    }

    /// <summary>"owner/name" of the GitHub repository the packages come from (set by the CI build).</summary>
    public static string? Repository => Metadata("VitaSharpUpdateRepository");

    /// <summary>The releases feed: <see cref="EnvVars.UpdateFeed"/> (a URL or a folder with releases.json), else the
    /// GitHub API of <see cref="Repository"/>; null for a build without one.</summary>
    public static Uri? Feed(string? overrideFeed = null)
    {
        string? f = overrideFeed ?? Environment.GetEnvironmentVariable(EnvVars.UpdateFeed);
        if (!string.IsNullOrEmpty(f)) return Uri.TryCreate(f, UriKind.Absolute, out var u) ? u : new Uri(Path.GetFullPath(f));
        return Repository is { } repo ? UpdateFeed.GitHubFeed(repo) : null;
    }

    /// <summary>The folder the emulator is installed in (the .app bundle on macOS).</summary>
    public static string InstallDir()
    {
        string dir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        int app = dir.IndexOf(".app" + Path.DirectorySeparatorChar + "Contents", StringComparison.Ordinal);
        return app > 0 ? dir[..(app + 4)] : dir;
    }

    /// <summary>The package kind this installation is updated with: the installer when Inno Setup installed it, the
    /// AppImage when running as one, else the platform's archive.</summary>
    public static string PackageKind()
    {
        if (OperatingSystem.IsWindows() && File.Exists(Path.Combine(InstallDir(), "unins000.exe"))) return "-setup.exe";
        if (OperatingSystem.IsLinux() && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("APPIMAGE"))) return ".AppImage";
        return UpdateFeed.DefaultKind(UpdateFeed.CurrentRid());
    }

    public static async Task<UpdateRelease?> CheckAsync(string channel, Uri feed, CancellationToken ct = default)
    {
        using var http = UpdateFeed.CreateClient(Current.Version);
        var releases = await UpdateFeed.FetchAsync(http, feed, ct).ConfigureAwait(false);
        return UpdateFeed.FindUpdate(releases, Current, channel);
    }

    /// <summary>
    /// Downloads and checks the package of <paramref name="release"/> and starts what replaces this installation (the
    /// new package's <c>apply-update</c>, the installer, or the AppImage swap). The caller exits right after, so the
    /// files are free; the emulator starts again with <paramref name="restartArgs"/>.
    /// </summary>
    public static async Task InstallAsync(UpdateRelease release, Action<double>? progress, Action<string>? log,
                                          string[] restartArgs, CancellationToken ct = default)
    {
        string rid = UpdateFeed.CurrentRid(), kind = PackageKind();
        var asset = UpdateFeed.PickPackage(release, rid, kind)
            ?? throw new InvalidDataException($"The release {release.Tag} has no {rid}{kind} package");
        string work = Path.Combine(Path.GetTempPath(), "VitaSharp-update", release.Tag);
        string file = Path.Combine(work, asset.Name);
        using var http = UpdateFeed.CreateClient(Current.Version);
        log?.Invoke($"Update: downloading {asset.Url}");
        await UpdateFeed.DownloadAsync(http, asset, file, progress, ct).ConfigureAwait(false);
        await UpdateFeed.VerifyAsync(http, release, file, asset.Name, ct).ConfigureAwait(false);
        log?.Invoke($"Update: {asset.Name} matches {UpdateFeed.SumsFile}");

        string self = Environment.ProcessPath ?? throw new InvalidOperationException("No executable path");
        switch (kind)
        {
            case "-setup.exe":
                // the installer replaces the files (it asks for elevation when installed for all users)
                Process.Start(new ProcessStartInfo(file, "/SILENT /SUPPRESSMSGBOXES /NORESTART") { UseShellExecute = true });
                return;
            case ".AppImage":
            {
                string appImage = Environment.GetEnvironmentVariable("APPIMAGE")!;
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(file, File.GetUnixFileMode(appImage) | UnixFileMode.UserExecute);
                File.Move(file, appImage, overwrite: true); // a rename: the running image keeps its old inode
                Start(appImage, restartArgs, Path.GetDirectoryName(appImage)!);
                return;
            }
        }

        string root = await Task.Run(() => UpdateInstaller.Extract(file, Path.Combine(work, "package")), ct).ConfigureAwait(false);
        string target = InstallDir();
        // the executable at the same place inside the package (VitaSharp.exe, VitaSharp.app/Contents/MacOS/VitaSharp)
        string updater = Path.Combine(root, Path.GetRelativePath(target, self));
        if (!File.Exists(updater)) throw new InvalidDataException($"The package has no {Path.GetRelativePath(target, self)}");
        var args = new List<string> { "apply-update", $"--from={root}", $"--to={target}", $"--pid={Environment.ProcessId}", $"--restart={self}" };
        args.AddRange(restartArgs.Select(a => "--restart-arg=" + a));
        log?.Invoke($"Update: {updater} applies {release.Tag} to {target} after this process exits");
        var psi = new ProcessStartInfo(updater) { UseShellExecute = false, WorkingDirectory = work };
        foreach (var a in args) psi.ArgumentList.Add(a);
        Process.Start(psi);
    }

    static void Start(string exe, string[] args, string workingDir)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = workingDir };
        foreach (var a in args) psi.ArgumentList.Add(a);
        Process.Start(psi);
    }

    /// <summary><c>apply-update --from=&lt;package&gt; --to=&lt;install&gt; --pid=N --restart=&lt;exe&gt; [--restart-arg=A]...</c>:
    /// run from the extracted package; waits for the old emulator to exit, copies the package over it and starts it.</summary>
    public static int ApplyCli(string[] args)
    {
        string? Opt(string name) => args.FirstOrDefault(a => a.StartsWith(name + "="))?[(name.Length + 1)..];
        string from = Opt("--from") ?? AppContext.BaseDirectory, to = Opt("--to") ?? "";
        string log = Path.Combine(Path.GetTempPath(), "VitaSharp-update", "apply-update.log");
        void Log(string s)
        {
            Console.WriteLine(s);
            try { File.AppendAllText(log, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {s}{Environment.NewLine}"); } catch (IOException) { }
        }
        try
        {
            if (to.Length == 0) throw new ArgumentException("--to=<installation> is missing");
            if (int.TryParse(Opt("--pid"), out int pid))
            {
                try
                {
                    using var old = Process.GetProcessById(pid);
                    if (!old.WaitForExit(TimeSpan.FromSeconds(60))) Log($"apply-update: process {pid} still running after 60 s, copying anyway");
                }
                catch (ArgumentException) { } // already gone
            }
            UpdateInstaller.Apply(from, to, Program.DataRoot, TimeSpan.FromSeconds(30), Log);
            if (Opt("--restart") is { } exe && File.Exists(exe))
            {
                var restartArgs = args.Where(a => a.StartsWith("--restart-arg=")).Select(a => a["--restart-arg=".Length..]).ToArray();
                Start(exe, restartArgs, to);
                Log($"apply-update: started {exe} {string.Join(' ', restartArgs)}");
            }
            return 0;
        }
        catch (Exception e)
        {
            Log($"apply-update failed: {e.Message}");
            return 1;
        }
    }

    /// <summary><c>update [--channel=builds|releases] [--feed=URL|folder] [--apply] [--restart-arg=A]...</c>: the
    /// launcher's check without a window; --apply installs the update and exits (the emulator restarts with the
    /// --restart-arg values, none: the launcher).</summary>
    public static int Cli(string[] args)
    {
        string? Opt(string name) => args.FirstOrDefault(a => a.StartsWith(name + "="))?[(name.Length + 1)..];
        Console.WriteLine($"VitaSharp {Current.Display} ({UpdateFeed.CurrentRid()}, {PackageKind()} package, installed in {InstallDir()})");
        var feed = Feed(Opt("--feed"));
        if (feed == null) { Console.WriteLine("This build has no update feed (built from source): update it with git, or pass --feed="); return 2; }
        string channel = Opt("--channel") ?? UpdateFeed.ChannelBuilds;
        try
        {
            var release = CheckAsync(channel, feed).GetAwaiter().GetResult();
            if (release == null) { Console.WriteLine($"Up to date ({channel}, {feed})"); return 0; }
            Console.WriteLine($"Update: {Current.Display} -> {release.Name} ({release.Tag}, {release.Published:yyyy-MM-dd HH:mm})");
            foreach (var line in release.Body.Split('\n').Take(12)) Console.WriteLine("  " + line.TrimEnd());
            if (!args.Contains("--apply")) return 0;
            int lastPct = -1;
            InstallAsync(release, p => { int pct = (int)(p * 100); if (pct / 10 != lastPct / 10) { lastPct = pct; Console.WriteLine($"  {pct}%"); } },
                Console.WriteLine, args.Where(a => a.StartsWith("--restart-arg=")).Select(a => a["--restart-arg=".Length..]).ToArray()).GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception e) when (e is HttpRequestException or IOException or InvalidDataException or System.Text.Json.JsonException or InvalidOperationException or TaskCanceledException)
        {
            Console.WriteLine($"Update failed: {e.Message}");
            return 1;
        }
    }

    /// <summary>Deletes the downloads of earlier updates (best effort, at start).</summary>
    public static void CleanDownloads()
    {
        string dir = Path.Combine(Path.GetTempPath(), "VitaSharp-update");
        if (!Directory.Exists(dir)) return;
        foreach (var d in Directory.GetDirectories(dir))
            try { if (Directory.GetLastWriteTimeUtc(d) < DateTime.UtcNow.AddMinutes(-10)) Directory.Delete(d, true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
