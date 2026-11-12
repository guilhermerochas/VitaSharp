#if VITASHARP_DEVELOPER
using Android.App;
using Android.OS;
using VitaSharp.Android.Media;
using VitaSharp.Core.Media;
using VitaSharp.Core.Util;

namespace VitaSharp.Android.Developer;

/// <summary>
/// Developer APK only: the media parity run (Core/Media/MediaContract.cs) with the Android backends the games use
/// (MediaCodec H.264, AAC and MP3), started by build/android-media-parity.sh (.ps1):
/// <c>adb shell am start -n io.github.vitasharp/io.github.vitasharp.MediaParityActivity [--es sync 0]</c>. It reads the
/// audio clips the script pushed to <c>files/media-parity/</c>, writes <c>files/media-parity/trace.txt</c> (then
/// <c>done.txt</c>) and closes. <c>--es sync 0</c> runs with VITASHARP_MEDIACODEC_SYNC=0 (the old output timing), for
/// the before/after comparison; <c>--es env "NAME=VALUE;NAME=VALUE"</c> sets other variables the same way. Its own
/// process, so the settings apply from the first decoder on.
/// </summary>
[Activity(Name = "io.github.vitasharp.MediaParityActivity", Label = "VitaSharp Media Parity", Process = ":parity", Exported = true,
    Theme = "@android:style/Theme.Black.NoTitleBar")]
public sealed class MediaParityActivity : Activity
{
    /// <summary>Time between decode calls: a 30 fps movie, so MediaCodec has the time it has in a game.</summary>
    const int VideoPacingUs = 33_333;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        SetContentView(new global::Android.Widget.TextView(this) { Text = "media parity run", TextSize = 16 });
        string? sync = Intent?.GetStringExtra("sync");
        if (!string.IsNullOrEmpty(sync))
        {
            System.Environment.SetEnvironmentVariable(EnvVars.MediaCodecSync, sync);
        }

        foreach (string setting in (Intent?.GetStringExtra("env") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            int equals = setting.IndexOf('=');
            if (equals > 0)
            {
                System.Environment.SetEnvironmentVariable(setting[..equals], setting[(equals + 1)..]);
            }
        }

        string root = GetExternalFilesDir(null)?.AbsolutePath ?? FilesDir!.AbsolutePath;
        string folder = Path.Combine(root, "media-parity");
        var worker = new Thread(() => RunAndClose(folder)) { IsBackground = true, Name = "Media parity" };
        worker.Start();
    }

    void RunAndClose(string folder)
    {
        string donePath = Path.Combine(folder, "done.txt");
        try
        {
            Directory.CreateDirectory(folder);
            File.Delete(donePath);
            Log.OpenFile(Path.Combine(folder, "parity.log"));
            VideoDecoders.Backend = new MediaCodecVideoBackend();
            AudioFrameDecoders.AacBackend = MediaCodecAacDecoder.Create;
            AudioFrameDecoders.Mp3Backend = MediaCodecAacDecoder.CreateMp3;
            byte[] aacClip = File.ReadAllBytes(Path.Combine(folder, MediaContract.AacClipFile));
            byte[] mp3Clip = File.ReadAllBytes(Path.Combine(folder, MediaContract.Mp3ClipFile));
            List<string> trace = MediaContract.RunInstalledBackends(aacClip, mp3Clip, VideoPacingUs);
            File.WriteAllText(Path.Combine(folder, "trace.txt"), MediaContract.Format(trace));
            Log.Flush();
            File.WriteAllText(donePath, "ok\n");
        }
        catch (Exception e)
        {
            Log.Warn($"Media parity run failed: {e}");
            File.WriteAllText(donePath, "failed: " + e.Message + "\n");
        }
        // the decoders read their settings once per process: the next run starts a new one
        RunOnUiThread(() =>
        {
            FinishAndRemoveTask();
            global::Android.OS.Process.KillProcess(global::Android.OS.Process.MyPid());
        });
    }
}
#endif
