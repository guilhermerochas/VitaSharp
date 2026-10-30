using Android.App;
using Android.Content.PM;
using VitaSharp.Android.Services;
using VitaSharp.Core.Util;

namespace VitaSharp.Android.Game;

/// <summary>
/// The phone's sensors and cameras in the game (roadmap: "Sensores do celular alimentando o SceMotion e câmera do
/// celular no SceCamera"): <see cref="AndroidMotionSensors"/> as the SceMotion source (the "Movimento" setting) and
/// <see cref="AndroidCamera"/> as the SceCamera host camera. The activity creates it, hands it to the session and
/// forwards its lifecycle (listeners and cameras only while in the foreground) and the permission result.
/// </summary>
public sealed class GameSensors : IDisposable
{
    readonly AndroidMotionSensors _motion;
    readonly AndroidCamera _camera;

    public GameSensors(Activity activity)
    {
        _motion = new AndroidMotionSensors(activity, AppServices.Prefs.Motion);
        _camera = new AndroidCamera(activity, () => _motion.Display.Rotation);
    }

    /// <summary>Connects them to the session (the keyboard's tilt keys set before stay as the motion fallback).</summary>
    public void Attach(EmulationSession session) => session.SetSensors(_motion.Motion, _camera);

    public void Resume()
    {
        _motion.Resume();
        _camera.Resume();
    }

    public void Pause()
    {
        _motion.Pause();
        _camera.Pause();
    }

    /// <summary>The pause menu's "Recentralizar movimento".</summary>
    public void Recenter() => _motion.Recenter();

    /// <summary>Whether the motion follows a sensor device (the pause menu offers re-centring then).</summary>
    public bool CanRecenter => _motion.Motion.Sensors != null;

    public bool OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults) =>
        _camera.OnRequestPermissionsResult(requestCode, permissions, grantResults);

    /// <summary>The camera itself is disposed by the emulator's host services with the session.</summary>
    public void Dispose()
    {
        try { _motion.Dispose(); } catch (Exception e) { Log.Warn($"Motion: {e.Message}"); }
    }
}
