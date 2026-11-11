using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace VitaSharp.Core.Update;

/// <summary>One downloadable file of a release.</summary>
public sealed record UpdateAsset(string Name, string Url, long Size);

/// <summary>One release of the GitHub releases API: <c>build-&lt;hash&gt;</c> (automatic build of main, prerelease) or
/// <c>v&lt;version&gt;</c>.</summary>
public sealed record UpdateRelease(string Tag, string Name, string Body, DateTimeOffset Published, bool Prerelease,
                                   string Commit, IReadOnlyList<UpdateAsset> Assets)
{
    public bool IsBuild => Tag.StartsWith("build-", StringComparison.Ordinal);
    public bool IsVersion => Tag.StartsWith('v') && Tag.Length > 1 && char.IsAsciiDigit(Tag[1]);

    /// <summary>The short commit of an automatic build (<c>build-abc1234</c>), else "".</summary>
    public string BuildHash => IsBuild ? Tag["build-".Length..] : "";

    /// <summary>True when this release was made from <paramref name="commit"/> (a full or short hash).</summary>
    public bool IsCommit(string commit)
    {
        if (commit.Length < 7)
        {
            return false;
        }

        if (BuildHash.Length >= 7 && commit.StartsWith(BuildHash, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return Commit.Length >= 7 && (Commit.StartsWith(commit, StringComparison.OrdinalIgnoreCase) || commit.StartsWith(Commit, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>The build that is running: its version, commit and build time (assembly metadata, set by the build).</summary>
public sealed record BuildIdentity(string Version, string Commit, DateTimeOffset Built)
{
    /// <summary>From an InformationalVersion such as <c>0.1.0+0123abcd...</c> (the SDK appends the commit).</summary>
    public static BuildIdentity Parse(string? informationalVersion, DateTimeOffset built)
    {
        string info = informationalVersion ?? "";
        int plus = info.IndexOf('+');
        string version = plus >= 0 ? info[..plus] : info;
        string commit = plus >= 0 ? info[(plus + 1)..] : "";
        if (commit.Length > 0 && !commit.All(char.IsAsciiHexDigit))
        {
            commit = "";
        }

        return new BuildIdentity(version.Length > 0 ? version : "0.0.0", commit, built);
    }

    /// <summary>"0.1.0+abc1234", the way the launcher shows it.</summary>
    public string Display => Commit.Length >= 7 ? $"{Version}+{Commit[..7]}" : Version;
}

/// <summary>
/// The update check: reads the GitHub releases API (or a local feed with the same JSON: a <c>file://</c> folder with
/// <c>releases.json</c>, or any http URL) and picks the release and package to install.
/// </summary>
public static class UpdateFeed
{
    public const string ChannelBuilds = "builds", ChannelReleases = "releases";
    public const string SumsFile = "SHA256SUMS.txt";

    /// <summary>The releases API of <paramref name="repository"/> ("owner/name").</summary>
    public static Uri GitHubFeed(string repository) => new($"https://api.github.com/repos/{repository}/releases?per_page=30");

    public static IReadOnlyList<UpdateRelease> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var list = new List<UpdateRelease>();
        foreach (var r in doc.RootElement.EnumerateArray())
        {
            if (r.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True)
            {
                continue;
            }

            static string Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            var assets = new List<UpdateAsset>();
            if (r.TryGetProperty("assets", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in arr.EnumerateArray())
                {
                    assets.Add(new UpdateAsset(Str(a, "name"), Str(a, "browser_download_url"),
                        a.TryGetProperty("size", out var size) && size.TryGetInt64(out long n) ? n : 0));
                }
            }

            string published = Str(r, "published_at");
            if (published.Length == 0)
            {
                published = Str(r, "created_at");
            }

            list.Add(new UpdateRelease(Str(r, "tag_name"), Str(r, "name"), Str(r, "body"),
                DateTimeOffset.TryParse(published, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var when) ? when : DateTimeOffset.MinValue,
                r.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True,
                Str(r, "target_commitish"), assets));
        }
        return list;
    }

    /// <summary>
    /// The newest release of <paramref name="channel"/> that is newer than <paramref name="current"/>, or null. "builds"
    /// follows every automatic build and version; "releases" only the <c>v*</c> versions. A release is newer when it
    /// was published after the release of the running commit (or, for a build that is not on the list, after the build
    /// time).
    /// </summary>
    public static UpdateRelease? FindUpdate(IReadOnlyList<UpdateRelease> releases, BuildIdentity current, string channel)
    {
        var candidates = releases.Where(r => channel == ChannelReleases ? r.IsVersion : r.IsBuild || r.IsVersion)
                                 .OrderByDescending(r => r.Published).ToList();
        var latest = candidates.FirstOrDefault();
        if (latest == null || (current.Commit.Length > 0 && latest.IsCommit(current.Commit)))
        {
            return null;
        }

        var mine = current.Commit.Length > 0 ? releases.FirstOrDefault(r => r.IsCommit(current.Commit)) : null;
        // a minute of slack: the packages are built before their release is published
        var since = mine?.Published ?? current.Built.AddMinutes(1);
        return latest.Published > since ? latest : null;
    }

    /// <summary>The runtime identifier of the packages (win-x64, linux-x64, osx-arm64...).</summary>
    public static string CurrentRid()
    {
        string os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
        string arch = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture switch
        {
            System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
            System.Runtime.InteropServices.Architecture.X86 => "x86",
            _ => "x64",
        };
        return $"{os}-{arch}";
    }

    /// <summary>The package of <paramref name="release"/> for <paramref name="rid"/> in <paramref name="kind"/>
    /// (".zip", "-setup.exe", ".tar.gz", ".AppImage", ".app.zip"), or null.</summary>
    public static UpdateAsset? PickPackage(UpdateRelease release, string rid, string kind) =>
        release.Assets.FirstOrDefault(a => a.Name.StartsWith("VitaSharp-", StringComparison.Ordinal)
                                           && a.Name.EndsWith($"-{rid}{kind}", StringComparison.Ordinal));

    /// <summary>The default package kind of a platform.</summary>
    public static string DefaultKind(string rid) => rid.StartsWith("win-") ? ".zip" : rid.StartsWith("osx-") ? ".app.zip" : ".tar.gz";

    /// <summary>The SHA-256 of <paramref name="file"/> in a <c>sha256sum</c> listing ("hash  name" or "hash *name").</summary>
    public static string? ExpectedHash(string sums, string file)
    {
        foreach (var raw in sums.Split('\n'))
        {
            var line = raw.Trim();
            int sp = line.IndexOf(' ');
            if (sp != 64)
            {
                continue;
            }

            string name = line[(sp + 1)..].TrimStart(' ', '*');
            if (name == file)
            {
                return line[..64].ToLowerInvariant();
            }
        }
        return null;
    }

    public static string Sha256(string file)
    {
        using var s = File.OpenRead(file);
        return Convert.ToHexStringLower(SHA256.HashData(s));
    }

    // ------------------------------------------------------------------ transport

    /// <summary>Reads a feed or file: http(s) through <paramref name="http"/>, <c>file://</c> from disk.</summary>
    public static async Task<Stream> OpenAsync(HttpClient http, Uri uri, CancellationToken ct)
    {
        if (uri.IsFile)
        {
            return File.OpenRead(uri.LocalPath);
        }

        var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
    }

    /// <summary>The releases of a feed: an API URL, or a folder (<c>file://</c>) holding <c>releases.json</c>.</summary>
    public static async Task<IReadOnlyList<UpdateRelease>> FetchAsync(HttpClient http, Uri feed, CancellationToken ct = default)
    {
        if (feed.IsFile && Directory.Exists(feed.LocalPath))
        {
            feed = new Uri(Path.Combine(feed.LocalPath, "releases.json"));
        }

        using var s = await OpenAsync(http, feed, ct).ConfigureAwait(false);
        using var reader = new StreamReader(s);
        return Parse(await reader.ReadToEndAsync(ct).ConfigureAwait(false));
    }

    /// <summary>An HttpClient for the GitHub API (it rejects requests without a User-Agent).</summary>
    public static HttpClient CreateClient(string version)
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"VitaSharp/{version}");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    /// <summary>Downloads <paramref name="asset"/> to <paramref name="dest"/>; <paramref name="progress"/> gets 0..1.</summary>
    public static async Task DownloadAsync(HttpClient http, UpdateAsset asset, string dest, Action<double>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        string part = dest + ".part";
        await using (var src = await OpenAsync(http, new Uri(asset.Url), ct).ConfigureAwait(false))
        await using (var dst = File.Create(part))
        {
            var buf = new byte[1 << 16];
            long done = 0;
            int n;
            while ((n = await src.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                done += n;
                if (asset.Size > 0)
                {
                    progress?.Invoke(Math.Min(1, (double)done / asset.Size));
                }
            }
        }
        File.Move(part, dest, true);
        progress?.Invoke(1);
    }

    /// <summary>Downloads the release's <see cref="SumsFile"/> and checks <paramref name="file"/> (named
    /// <paramref name="name"/> there) against it; throws <see cref="InvalidDataException"/> when it is missing or differs.</summary>
    public static async Task VerifyAsync(HttpClient http, UpdateRelease release, string file, string name, CancellationToken ct)
    {
        var sumsAsset = release.Assets.FirstOrDefault(a => a.Name == SumsFile)
            ?? throw new InvalidDataException($"The release {release.Tag} has no {SumsFile}");
        string sums;
        using (var s = await OpenAsync(http, new Uri(sumsAsset.Url), ct).ConfigureAwait(false))
        using (var reader = new StreamReader(s))
        {
            sums = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
        }

        string expected = ExpectedHash(sums, name) ?? throw new InvalidDataException($"{name} is not in {SumsFile}");
        string actual = Sha256(file);
        if (actual != expected)
        {
            throw new InvalidDataException($"{name}: SHA-256 {actual} does not match {SumsFile} ({expected})");
        }
    }
}
