using System.IO.Compression;
using System.Text.RegularExpressions;
using VitaSharp.Core.Capture;

namespace VitaSharp.Tests;

/// <summary>
/// A "Gravar tudo" capture is shared (a GitHub issue): none of its files may name the person who recorded it. A
/// synthetic capture is recorded with a data folder inside a home folder, a user, a computer and an online id of a
/// made-up person in the log, the run options and the session notes, and every file of the folder and of its zip is
/// scanned for them, for MAC and e-mail addresses: only the placeholders may be left.
/// </summary>
[Collection("guest")]
public sealed class SessionCapturePrivacyTests(GuestMemoryFixture fixture) : IDisposable
{
    private const string UserName = "zelinda", Computer = "ZELINDA-DESKTOP", OnlineId = "ZelOnline77", Mac = "a4:5e:60:12:34:56", Email = "zelinda.q@example.org";

    private readonly HleHarness _harness = new(fixture, "capprivacy");

    public void Dispose() => _harness.Dispose();

    private static readonly Regex s_personal = new($"{UserName}|{Computer}|{OnlineId}|{Regex.Escape(Mac)}|{Regex.Escape(Email)}", RegexOptions.IgnoreCase);

    [Fact]
    public void RedactPutsPlaceholdersForPathsNamesAccountsMacsAndEmails()
    {
        // the folders are spelt in pieces: no user folder is written whole in the repository (NoPersonalPathsTests)
        const string Users = "Users", UsersLower = "users";
        var privacy = new CapturePrivacy($@"C:\{Users}\zelinda\AppData\Roaming\VitaSharp", $@"C:\{Users}\zelinda", UserName, Computer, [OnlineId]);
        string text = privacy.Redact($@"Loading from C:\{Users}\zelinda\AppData\Roaming\VitaSharp\fs\ux0\app\PCSE00001 and c:/{UsersLower}/Zelinda/Desktop/x.vpk on {Computer} as {OnlineId}, mac {Mac}, mail {Email}");
        Assert.Equal(@"Loading from <data>\fs\ux0\app\PCSE00001 and <user>/Desktop/x.vpk on <pc> as <psn-id>, mac <mac>, mail <email>", text);
        Assert.Equal(["--psn-id=<psn-id>", "--rpcn-user=<psn-id>", "--shader-cache=<data>/cache", "--cpu=native"],
            privacy.RedactOptions(["--psn-id=Whoever", "--rpcn-user=Someone", $"--shader-cache=C:/{Users}/zelinda/AppData/Roaming/VitaSharp/cache", "--cpu=native"]));
        // a version or a driver number is no MAC address; the default online id names nobody
        Assert.Equal("driver 546.17.0.0, build 1.0.0+dcd27d99", privacy.Redact("driver 546.17.0.0, build 1.0.0+dcd27d99"));
        Assert.DoesNotContain("<psn-id>", CapturePrivacy.ForThisComputer(null, Core.Hle.PsnAccount.Default).Redact("--psn VitaSharp"));
    }

    [Fact]
    public void NoFileOfACaptureNamesThePersonWhoRecordedIt()
    {
        string home = Path.Combine(_harness.Root, "Users", UserName);
        string data = Path.Combine(home, "AppData", "VitaSharp");
        string root = Path.Combine(data, "captures");
        string log = Path.Combine(_harness.Root, "session.log");
        File.WriteAllText(log, $"Loading PCSE00001 from {Path.Combine(data, "fs", "ux0", "app", "PCSE00001")}\nshader cache {data.Replace('\\', '/')}/shadercache\n"
            + $"host {Computer}, signed in as {OnlineId}, adapter {Mac}, contact {Email}\nplugins: {Path.Combine(home, "Downloads", "plugin.skprx")}\n");
        IPresentTap? tap = null;
        var cap = new SessionCapture(_harness.Emu, new SessionCaptureOptions
        {
            Root = root,
            SetPresentTap = t => tap = t,
            StartState = false,
            GpuDump = false,
            Zip = true,
            LogFile = () => log,
            Threads = () => $"thread 'main' waiting in {Path.Combine(home, "x")} on {Computer}",
            Info = () => $"host: user {UserName} on {Computer}\nrenderer: Shader cache: {Path.Combine(data, "shadercache")}",
            RunOptions = ["--psn-id=" + OnlineId, "--shader-cache=" + Path.Combine(data, "shadercache"), "--cpu=interp"],
            Privacy = new CapturePrivacy(data, home, UserName, Computer, [OnlineId]),
        });
        Assert.True(cap.Start("test"));
        HleHarness.Until(() => cap.IsRecording && tap != null, "the recording to start");
        byte[] px = new byte[32 * 16 * 4];
        for (int f = 0; f < 3; f++)
        {
            tap!.OnPresent(new PresentInfo { Present = f + 1, Flip = f, VBlank = f * 2, PresentNs = CaptureClock.NowNs, IntervalMs = 16.7f, Hitch = "" }, px, 32, 16);
        }

        Assert.True(cap.Stop("test end"));
        Assert.True(cap.Idle.Wait(120_000), "the recording was not saved");
        string dir = cap.Dir!;
        cap.Dispose();

        var leaks = new List<string>();
        foreach (string file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            string text = File.ReadAllText(file);
            if (s_personal.Match(text) is { Success: true } m)
            {
                leaks.Add($"{Path.GetRelativePath(dir, file)}: '{m.Value}'");
            }
        }

        using (ZipArchive zip = ZipFile.OpenRead(cap.LastZip!))
        {
            foreach (ZipArchiveEntry entry in zip.Entries)
            {
                using var reader = new StreamReader(entry.Open());
                if (s_personal.Match(reader.ReadToEnd() + entry.FullName) is { Success: true } m)
                {
                    leaks.Add($"zip {entry.FullName}: '{m.Value}'");
                }
            }
        }

        Assert.True(leaks.Count == 0, "personal items left in the capture: " + string.Join("; ", leaks));
        string session = File.ReadAllText(Path.Combine(dir, "session.txt"));
        Assert.Contains("--psn-id=<psn-id>", session, StringComparison.Ordinal);
        Assert.Contains("<data>", File.ReadAllText(Path.Combine(dir, "log.txt")), StringComparison.Ordinal);
        Assert.Contains("app-version:", session, StringComparison.Ordinal);
        Assert.Contains("firmware:", session, StringComparison.Ordinal);
    }
}
