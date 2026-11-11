using System.IO.Compression;
using System.Net;
using System.Text;
using VitaSharp.Core.Update;

namespace VitaSharp.Tests;

public class UpdateFeedTests
{
    const string Old = "1111111aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", New = "2222222bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    static string Json(params (string Tag, string Commit, string When, bool Pre, string[] Assets)[] releases)
    {
        var sb = new StringBuilder("[");
        foreach (var r in releases)
        {
            if (sb.Length > 1) sb.Append(',');
            string assets = string.Join(",", r.Assets.Select(a => $"{{\"name\":\"{a}\",\"browser_download_url\":\"https://example.invalid/{a}\",\"size\":10}}"));
            sb.Append($"{{\"tag_name\":\"{r.Tag}\",\"name\":\"VitaSharp {r.Tag}\",\"body\":\"## Changes\\n- a commit (abc)\",\"draft\":false," +
                      $"\"prerelease\":{(r.Pre ? "true" : "false")},\"target_commitish\":\"{r.Commit}\",\"published_at\":\"{r.When}\",\"assets\":[{assets}]}}");
        }
        return sb.Append(']').ToString();
    }

    static readonly IReadOnlyList<UpdateRelease> Feed = UpdateFeed.Parse(Json(
        ("build-2222222", New, "2026-11-07T12:00:00Z", true, ["VitaSharp-0.1.0-build.g2222222-win-x64.zip", "VitaSharp-0.1.0-build.g2222222-win-x64-setup.exe", "SHA256SUMS.txt"]),
        ("v0.1.0", "3333333ccccccccccccccccccccccccccccccccc", "2026-11-05T12:00:00Z", false, ["VitaSharp-0.1.0-linux-x64.tar.gz", "SHA256SUMS.txt"]),
        ("build-1111111", Old, "2026-11-01T12:00:00Z", true, ["SHA256SUMS.txt"])));

    [Fact]
    public void BuildsChannelOffersTheNewestBuild()
    {
        var current = BuildIdentity.Parse("1.0.0+" + Old, DateTimeOffset.Parse("2026-11-01T11:50:00Z"));
        Assert.Equal("1111111", current.Display[^7..]);
        Assert.Equal("build-2222222", UpdateFeed.FindUpdate(Feed, current, UpdateFeed.ChannelBuilds)?.Tag);
        Assert.Equal("v0.1.0", UpdateFeed.FindUpdate(Feed, current, UpdateFeed.ChannelReleases)?.Tag);
    }

    [Fact]
    public void NoUpdateForTheNewestBuildOrNewerLocalBuild()
    {
        Assert.Null(UpdateFeed.FindUpdate(Feed, BuildIdentity.Parse("1.0.0+" + New, DateTimeOffset.UtcNow), UpdateFeed.ChannelBuilds));
        // a commit that is not on the list (a local build) is compared by its build time
        var local = BuildIdentity.Parse("1.0.0+4444444ddddddddddddddddddddddddddddddddd", DateTimeOffset.Parse("2026-11-08T00:00:00Z"));
        Assert.Null(UpdateFeed.FindUpdate(Feed, local, UpdateFeed.ChannelBuilds));
        var older = local with { Built = DateTimeOffset.Parse("2026-11-06T00:00:00Z") };
        Assert.Equal("build-2222222", UpdateFeed.FindUpdate(Feed, older, UpdateFeed.ChannelBuilds)?.Tag);
        // the releases channel ignores builds: v0.1.0 is older than the running build
        Assert.Null(UpdateFeed.FindUpdate(Feed, BuildIdentity.Parse("1.0.0+" + New, DateTimeOffset.UtcNow), UpdateFeed.ChannelReleases));
    }

    [Fact]
    public void PicksThePlatformPackageAndReadsChecksums()
    {
        var r = Feed[0];
        Assert.Equal("VitaSharp-0.1.0-build.g2222222-win-x64.zip", UpdateFeed.PickPackage(r, "win-x64", ".zip")?.Name);
        Assert.Equal("VitaSharp-0.1.0-build.g2222222-win-x64-setup.exe", UpdateFeed.PickPackage(r, "win-x64", "-setup.exe")?.Name);
        Assert.Null(UpdateFeed.PickPackage(r, "linux-x64", ".tar.gz"));
        string hash = new('a', 64);
        Assert.Equal(hash, UpdateFeed.ExpectedHash($"{new string('b', 64)}  other.zip\n{hash}  VitaSharp-x.zip\n", "VitaSharp-x.zip"));
        Assert.Equal(hash, UpdateFeed.ExpectedHash($"{hash} *VitaSharp-x.zip", "VitaSharp-x.zip"));
        Assert.Null(UpdateFeed.ExpectedHash($"{hash}  VitaSharp-x.zip", "VitaSharp-y.zip"));
    }

    /// <summary>The whole flow against a local feed served over HTTP: check, download, SHA256SUMS, extract, apply.</summary>
    [Fact]
    public async Task DownloadsVerifiesAndAppliesFromLocalServer()
    {
        var dir = Directory.CreateTempSubdirectory("vitasharp-update-");
        try
        {
            // the package: VitaSharp/ with the new executable and a new file
            var stage = Directory.CreateDirectory(Path.Combine(dir.FullName, "stage", "VitaSharp"));
            File.WriteAllText(Path.Combine(stage.FullName, "VitaSharp.exe"), "new");
            Directory.CreateDirectory(Path.Combine(stage.FullName, "ffmpeg"));
            File.WriteAllText(Path.Combine(stage.FullName, "ffmpeg", "lib.dll"), "lib");
            var served = Directory.CreateDirectory(Path.Combine(dir.FullName, "served"));
            string zipName = "VitaSharp-0.1.0-build.g2222222-win-x64.zip";
            ZipFile.CreateFromDirectory(stage.FullName, Path.Combine(served.FullName, zipName), CompressionLevel.Fastest, includeBaseDirectory: true);
            File.WriteAllText(Path.Combine(served.FullName, "SHA256SUMS.txt"), $"{UpdateFeed.Sha256(Path.Combine(served.FullName, zipName))}  {zipName}\n");

            using var listener = new HttpListener();
            int port = 0;
            for (int p = 45100; p < 45200 && port == 0; p++)
            {
                try { listener.Prefixes.Clear(); listener.Prefixes.Add($"http://localhost:{p}/"); listener.Start(); port = p; }
                catch (HttpListenerException) { }
            }
            Assert.NotEqual(0, port);
            string baseUrl = $"http://localhost:{port}/";
            string json = Json(("build-2222222", New, "2026-11-07T12:00:00Z", true, [zipName, "SHA256SUMS.txt"])).Replace("https://example.invalid/", baseUrl);
            var serve = Task.Run(async () =>
            {
                while (listener.IsListening)
                {
                    HttpListenerContext ctx;
                    try { ctx = await listener.GetContextAsync(); } catch (Exception) { return; }
                    string name = ctx.Request.Url!.AbsolutePath.Trim('/');
                    byte[] body = name.StartsWith("releases") ? Encoding.UTF8.GetBytes(json)
                        : File.Exists(Path.Combine(served.FullName, name)) ? File.ReadAllBytes(Path.Combine(served.FullName, name)) : [];
                    ctx.Response.StatusCode = body.Length > 0 ? 200 : 404;
                    await ctx.Response.OutputStream.WriteAsync(body);
                    ctx.Response.Close();
                }
            });

            using var http = UpdateFeed.CreateClient("test");
            var releases = await UpdateFeed.FetchAsync(http, new Uri(baseUrl + "releases"));
            var release = UpdateFeed.FindUpdate(releases, BuildIdentity.Parse("1.0.0+" + Old, DateTimeOffset.Parse("2026-11-01T00:00:00Z")), UpdateFeed.ChannelBuilds);
            Assert.NotNull(release);
            var asset = UpdateFeed.PickPackage(release, "win-x64", ".zip")!;
            string file = Path.Combine(dir.FullName, "download", asset.Name);
            double last = 0;
            await UpdateFeed.DownloadAsync(http, asset, file, p => last = p, default);
            Assert.Equal(1, last);
            await UpdateFeed.VerifyAsync(http, release, file, asset.Name, default);

            // a tampered package fails the check
            string bad = file + ".bad";
            File.Copy(file, bad);
            File.AppendAllText(bad, "x");
            await Assert.ThrowsAsync<InvalidDataException>(() => UpdateFeed.VerifyAsync(http, release, bad, asset.Name, default));
            listener.Stop();

            // the installation: an old executable, a file the package does not have, and a data folder elsewhere
            var install = Directory.CreateDirectory(Path.Combine(dir.FullName, "install"));
            File.WriteAllText(Path.Combine(install.FullName, "VitaSharp.exe"), "old");
            File.WriteAllText(Path.Combine(install.FullName, "keep.txt"), "mine");
            string root = UpdateInstaller.Extract(file, Path.Combine(dir.FullName, "package"));
            Assert.Equal("VitaSharp", Path.GetFileName(root));
            Assert.Equal(2, UpdateInstaller.Apply(root, install.FullName, Path.Combine(dir.FullName, "data"), TimeSpan.FromSeconds(1)));
            Assert.Equal("new", File.ReadAllText(Path.Combine(install.FullName, "VitaSharp.exe")));
            Assert.Equal("lib", File.ReadAllText(Path.Combine(install.FullName, "ffmpeg", "lib.dll")));
            Assert.Equal("mine", File.ReadAllText(Path.Combine(install.FullName, "keep.txt")));

            // a data folder inside the installation is never overwritten
            Assert.Throws<InvalidOperationException>(() => UpdateInstaller.Apply(root, install.FullName, Path.Combine(install.FullName, "data"), TimeSpan.Zero));
            await serve;
        }
        finally { dir.Delete(true); }
    }
}
