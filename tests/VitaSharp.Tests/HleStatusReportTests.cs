using System.Globalization;
using System.Runtime.CompilerServices;
using VitaSharp.Core.Firmware;

namespace VitaSharp.Tests;

/// <summary>
/// The README status (badges, progress map, docs/hle/progress.md) is generated from docs/hle/data/hle-status.tsv by
/// build/hle-status.ps1. Rendering the committed summary again must give the committed files, so a hand edit or a
/// summary refreshed without its pictures fails here (run build/hle-status.ps1 again).
/// </summary>
public class HleStatusReportTests
{
    static string Root([CallerFilePath] string source = "")
    {
        for (var d = new DirectoryInfo(Path.GetDirectoryName(source)!); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "VitaSharp.sln"))) return d.FullName;
        throw new DirectoryNotFoundException("repository root");
    }

    static string Read(string relative) => File.ReadAllText(Path.Combine(Root(), relative)).Replace("\r\n", "\n");

    [Fact]
    public void CommittedFilesMatchTheSummary()
    {
        var status = HleStatusReport.Parse(Read(HleStatusReport.TsvPath));
        Assert.NotEmpty(status.Libraries);
        var stale = HleStatusReport.Render(status).Where(f => !File.Exists(Path.Combine(Root(), f.Key)) || Read(f.Key) != f.Value)
            .Select(f => f.Key).ToList();
        Assert.Empty(stale);
    }

    [Fact]
    public void BadgesShowTheSummaryCounts()
    {
        var status = HleStatusReport.Parse(Read(HleStatusReport.TsvPath));
        Assert.Equal(status.Libraries.Sum(l => l.Total), status.Total);
        foreach (var (id, _, impl, total) in HleStatusReport.Badges(status))
        {
            string svg = Read($"{HleStatusReport.AssetDir}/badge-{id}.svg");
            Assert.Contains($"({impl.ToString("N0", CultureInfo.InvariantCulture)}/{total.ToString("N0", CultureInfo.InvariantCulture)})", svg);
        }
    }

    [Fact]
    public void RoundTripsThroughTheTsv()
    {
        var s = HleStatusReport.Build("3.74", 2, 10, 4,
        [
            ("SceA", "vs0:a.suprx", "games", true), ("SceA", "vs0:a.suprx", "system", false), ("SceB", "os0:b.skprx", "general", true),
        ]);
        var back = HleStatusReport.Parse(HleStatusReport.ToTsv(s));
        Assert.Equal(s.Firmware, back.Firmware);
        Assert.Equal((s.VitaSdk, s.VitaSdkImpl, s.GameTitles), (back.VitaSdk, back.VitaSdkImpl, back.GameTitles));
        Assert.Equal(s.Libraries, back.Libraries);
        Assert.Equal((3, 2, 1, 1, 1, 0), (back.Total, back.Impl, back.Games, back.GamesImpl, back.System, back.SystemImpl));
    }
}
