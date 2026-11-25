using Android.App;
using Android.Content.PM;
using VitaSharp.Android.Services;
using VitaSharp.Core.Util;

namespace VitaSharp.Android.Game;

/// <summary>
/// The phone's sensors and cameras in the game (roadmap: "Sensores do celular alimentando o SceMotion e camera do
/// celular no SceCamera"): <see cref="AndroidMotionSensors"/> as the SceMotion source (the "Movimento" setting) and
/// <see cref="AndroidCamera"/> as the SceCamera host camera, <see cref="AndroidMicrophone"/> as the SceAudioIn host
/// microphone and <see cref="AndroidLocation"/> as the SceLibLocation position (when "Localizacao" says so). The activity creates it, hands it to the session and
/// forwards its lifecycle (listeners and cameras only while in the foreground) and the permission result.
/// </summary>
public sealed class GameSensors : IDisposable
{
    readonly AndroidMotionSensors _motion;
    readonly AndroidCamera _camera;
    readonly AndroidMicrophone _microphone;
    readonly AndroidLocation _location;

    public GameSensors(Activity activity)
    {
        _motion = new AndroidMotionSensors(activity, AppServices.Prefs.Motion);
        _camera = new AndroidCamera(activity, () => _motion.Display.Rotation);
        _microphone = new AndroidMicrophone(activity);
        _location = new AndroidLocation(activity);
    }

    /// <summary>Connects them to the session (the keyboard's tilt keys set before stay as the motion fallback).</summary>
    public void Attach(EmulationSession session) => session.SetSensors(_motion.Motion, _camera, _microphone, _location);

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
        _camera.OnRequestPermissionsResult(requestCode, permissions, grantResults)
        || _microphone.OnRequestPermissionsResult(requestCode, permissions, grantResults)
        || _location.OnRequestPermissionsResult(requestCode, permissions, grantResults);

    /// <summary>The camera itself is disposed by the emulator's host services with the session.</summary>
    public void Dispose()
    {
        try
        {
            _motion.Dispose();
        }
        catch (Exception e)
        {
            Log.Warn($"Motion: {e.Message}");
        }
    }
}
