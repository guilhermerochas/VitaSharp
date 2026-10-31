using Android.App;
using Android.Content;
using Android.OS;
using Android.Views;
using VitaSharp.Core.Util;

namespace VitaSharp.Android.Game;

/// <summary>
/// The game activity's display side of <see cref="GamePerformance"/>: the surface's frame rate follows the game, and
/// the optional sustained performance mode. Created once the session exists; the activity calls
/// <see cref="SurfaceCreated"/> for every new surface. Everything runs on the UI thread.
/// </summary>
public sealed class GameDisplayTuning
{
    readonly Activity _activity;
    readonly SurfaceView _view;
    readonly GamePerformance _performance;
    float _frameRateSet;

    GameDisplayTuning(Activity activity, SurfaceView view, GamePerformance performance)
    {
        _activity = activity;
        _view = view;
        _performance = performance;
    }

    /// <summary>Applies the current rate and the sustained mode, and follows the game's rate changes.</summary>
    public static GameDisplayTuning Start(Activity activity, SurfaceView view, GamePerformance performance)
    {
        var t = new GameDisplayTuning(activity, view, performance);
        performance.FrameRateChanged += fps => activity.RunOnUiThread(() => t.ApplyFrameRate(fps));
        t.ApplyFrameRate(performance.RefreshRate);
        t.ApplySustainedPerformance();
        return t;
    }

    /// <summary>A new Surface starts without a frame rate.</summary>
    public void SurfaceCreated()
    {
        _frameRateSet = 0;
        ApplyFrameRate(_performance.RefreshRate);
    }

    /// <summary>
    /// The game's frame rate as the surface's frame rate (API 30+), so a 90/120 Hz panel runs at a rate the game's
    /// 60 or 30 fps divides evenly (no judder; the system may pick 60 or 120 for 60, 30/60/120 for 30). The first rate
    /// of a surface may switch the display mode even when that is not seamless (a full-screen game, at its start);
    /// later changes only when seamless.
    /// </summary>
    void ApplyFrameRate(float fps)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(30) || _view.Holder?.Surface is not { IsValid: true } s || fps == _frameRateSet) return;
        try
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(31))
                s.SetFrameRate(fps, (int)SurfaceFrameRateCompatibility.Default,
                    _frameRateSet == 0 ? (int)SurfaceChangeFrameRate.Always : (int)SurfaceChangeFrameRate.OnlyIfSeamless);
            else s.SetFrameRate(fps, (int)SurfaceFrameRateCompatibility.Default);
            Log.Info($"Surface frame rate: {fps:F0} Hz{(_activity.Display?.RefreshRate is float hz ? $" (display at {hz:F0} Hz now)" : "")}");
            _frameRateSet = fps;
        }
        catch (Exception e) { Log.Warn($"Surface.setFrameRate({fps}): {e.Message}"); }
    }

    /// <summary>
    /// <c>VITASHARP_ANDROID_SUSTAINED=1</c>: Window.setSustainedPerformanceMode where the device supports it. Off by
    /// default: it holds the clocks at a level the phone can keep for hours, which is below its peak, and the ADPF
    /// session already asks for what each frame needs. Worth trying for long sessions on a phone that throttles hard.
    /// </summary>
    void ApplySustainedPerformance()
    {
        bool want = System.Environment.GetEnvironmentVariable(EnvVars.AndroidSustained)?.Trim() is "1" or "on" or "true" or "yes";
        bool supported = _activity.GetSystemService(Context.PowerService) is PowerManager pm && pm.IsSustainedPerformanceModeSupported;
        if (want && supported) _activity.Window?.SetSustainedPerformanceMode(true);
        Log.Info($"Sustained performance mode: {(supported ? "supported" : "not supported")}, {(want && supported ? "on" : "off")}"
                 + (want ? "" : $" ({EnvVars.AndroidSustained}=1 to try it)"));
    }
}
