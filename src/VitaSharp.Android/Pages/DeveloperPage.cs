#if VITASHARP_DEVELOPER
using VitaSharp.Core.Localization;
using MauiReactor;
using VitaSharp.Android.Developer;
using VitaSharp.Android.Services;
using VitaSharp.Android.Ui;
using VitaSharp.Renderer.Vulkan.Developer;

namespace VitaSharp.Android.Pages;

public sealed class DeveloperState
{
    public int Revision { get; set; }
    public string Status { get; set; } = "";
}

/// <summary>"Opcoes de desenvolvedor" (developer APK only): the GPU bench's fixtures and variants in files/bench, a run of
/// one fixture against every variant (A/B interleaved, one process per run) and the results table.</summary>
public sealed class DeveloperPage : Screen<DeveloperState>
{
    static IEnumerable<string> Names(string dir, bool files) =>
        !Directory.Exists(dir) ? [] : (files ? Directory.EnumerateFiles(dir, "*.vsgd").Select(System.IO.Path.GetFileNameWithoutExtension) : Directory.EnumerateDirectories(dir).Select(System.IO.Path.GetFileName))
            .OfType<string>().Where(n => !n.StartsWith('_')).Order();

    public override VisualNode Render()
    {
        string root = BenchActivity.Root;
        var fixtures = Names(System.IO.Path.Combine(root, "fixtures"), true).ToList();
        var variants = Names(System.IO.Path.Combine(root, "variants"), false).Prepend("base").Distinct().ToList();
        string results = System.IO.Path.Combine(root, "results", "ui.tsv");
        string table = File.Exists(results) ? GpuBenchTable.Format(File.ReadAllLines(results), "base", 3) : "";
        return Page(Loc.Get("MDeveloperOptions"),
            VStack(
                SectionTitle(Loc.Get("MGpuBench")),
                Card(fixtures.Select(f => (VisualNode?)Row(f, Loc.Get("MRun"), () => RunFixture(f, variants))).DefaultIfEmpty(Row(Loc.Get("MNoFixture"), null, detail: $"{root}/fixtures")).ToArray()),
                Note(Loc.Format("MBenchVariants", string.Join(", ", variants))),
                Label(State.Status).TextColor(Palette.Faint).Margin(20, 4),
                SectionTitle(Loc.Get("MBenchResults")),
                Card(Label(table.Length > 0 ? table : "\u2014").FontFamily("monospace").FontSize(10).Margin(12)),
                VStack(SecondaryButton(Loc.Get("MClearResults"), () => { File.Delete(results); SetState(s => s.Revision++); })).Padding(20, 8)
            ), kicker: Loc.Get("MSettings"));
    }

    async void RunFixture(string fixture, List<string> variants)
    {
        var ctx = AppServices.AppContext;
        for (int round = 0; round < 3; round++)
            foreach (var v in round % 2 == 0 ? variants : Enumerable.Reverse(variants))
            {
                SetState(s => s.Status = Loc.Format("MBenchRound", fixture, v, round + 1));
                BenchActivity.Launch(ctx, $"fixture={fixture};variant={v};reps=3;run=ui");
                await Task.Delay(1500);
                // the run's process ends with it
                while (IsBenchRunning()) await Task.Delay(500);
            }
        SetState(s => { s.Status = Loc.Get("MDone"); s.Revision++; });
    }

    static bool IsBenchRunning()
    {
        var am = (global::Android.App.ActivityManager?)AppServices.AppContext.GetSystemService(global::Android.Content.Context.ActivityService);
        return am?.RunningAppProcesses?.Any(p => p.ProcessName?.EndsWith(":bench") == true) == true;
    }
}
#endif
