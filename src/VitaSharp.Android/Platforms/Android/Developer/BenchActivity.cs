#if VITASHARP_DEVELOPER
using Android.App;
using Android.Content;
using Android.OS;
using VitaSharp.Android.Services;
using VitaSharp.Core.Util;
using VitaSharp.Renderer.Vulkan.Developer;

namespace VitaSharp.Android.Developer;

/// <summary>
/// Developer APK only: one GPU bench run (<see cref="GpuBench"/>) in a process of its own (the renderer's environment
/// switches are read once per process), then the process ends. Started by build/android-bench.* or the
/// "Opções de desenvolvedor" screen:
/// <c>adb shell am start -n io.github.vitasharp/io.github.vitasharp.BenchActivity --es bench "fixture=uc_title;variant=v1;reps=3;run=R"</c>.
/// Fixtures, variants and results are in files/bench (tools/dev/README.md); the BENCH lines also go to logcat.
/// </summary>
[Activity(Name = "io.github.vitasharp.BenchActivity", Label = "VitaSharp Bench", Process = ":bench", Exported = true,
    Theme = "@android:style/Theme.Black.NoTitleBar", LaunchMode = global::Android.Content.PM.LaunchMode.SingleTask)]
public sealed class BenchActivity : Activity
{
    public const string ExtraBench = "bench";

    public static string Root => Path.Combine(AppServices.Data.Root, "bench");

    public static void Launch(Context context, string spec)
    {
        var intent = new Intent(context, typeof(BenchActivity));
        intent.PutExtra(ExtraBench, spec);
        intent.AddFlags(ActivityFlags.NewTask);
        context.StartActivity(intent);
    }

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        AppServices.Initialize(this, gameProcess: true);
        Window?.AddFlags(global::Android.Views.WindowManagerFlags.KeepScreenOn);
        string text = Intent?.GetStringExtra(ExtraBench) ?? "";
        var t = new Thread(() =>
        {
            try
            {
                Directory.CreateDirectory(Path.Combine(Root, "results"));
                Log.OpenFile(Path.Combine(AppServices.Data.Logs, "bench.log"));
                Log.Info($"VitaSharp Dev bench: {text}");
                GpuBench.Run(GpuBenchSpec.Parse(text, Root), Log.Info);
            }
            catch (Exception e)
            {
                Log.Error($"[bench] {text}: {e}");
                try { File.AppendAllText(Path.Combine(Root, "results", "errors.txt"), $"{DateTime.Now:s} {text}: {e.Message}\n"); } catch (IOException) { }
            }
            Log.Flush();
            RunOnUiThread(() => { FinishAndRemoveTask(); global::Android.OS.Process.KillProcess(global::Android.OS.Process.MyPid()); });
        }) { Name = "GPU bench", IsBackground = true };
        t.Start();
    }
}
#endif
