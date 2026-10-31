using System.Numerics;
using Android.App;
using Android.Hardware;
using Android.Hardware.Display;
using Android.OS;
using Android.Runtime;
using Android.Views;
using VitaSharp.Android.Input;
using VitaSharp.Android.Services;
using VitaSharp.Core.Util;

namespace VitaSharp.Android.Game;

/// <summary>
/// Display.getRotation() of the game activity, kept current by a DisplayManager listener: the activity is
/// sensorLandscape, so turning the phone over flips the screen between ROTATION_90 and ROTATION_270 without a
/// configuration change, and the motion axes and the camera image follow it.
/// </summary>
public sealed class DisplayRotationTracker : Java.Lang.Object, DisplayManager.IDisplayListener
{
    readonly Activity _activity;
    readonly DisplayManager? _manager;
    volatile int _rotation;

    public DisplayRotationTracker(Activity activity, Handler handler)
    {
        _activity = activity;
        _rotation = Read();
        _manager = (DisplayManager?)activity.GetSystemService(global::Android.Content.Context.DisplayService);
        _manager?.RegisterDisplayListener(this, handler);
    }

    /// <summary>Surface.ROTATION_0..270 as 0..3.</summary>
    public int Rotation => _rotation;

    public event Action<int>? Changed;

    int Read()
    {
        try
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(30)) return (int)(_activity.Display?.Rotation ?? SurfaceOrientation.Rotation0) & 3;
#pragma warning disable CA1422 // the pre-API 30 way, only taken below API 30
            return (int)(_activity.WindowManager?.DefaultDisplay?.Rotation ?? SurfaceOrientation.Rotation0) & 3;
#pragma warning restore CA1422
        }
        catch (Exception) { return _rotation; }
    }

    public void OnDisplayAdded(int displayId) { }
    public void OnDisplayRemoved(int displayId) { }

    public void OnDisplayChanged(int displayId)
    {
        int r = Read();
        if (r == _rotation) return;
        _rotation = r;
        Log.Info($"Display rotation {r * 90} degrees");
        Changed?.Invoke(r);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) try { _manager?.UnregisterDisplayListener(this); } catch (Exception) { }
        base.Dispose(disposing);
    }
}

/// <summary>
/// The SensorManager side of SceMotion: TYPE_ACCELEROMETER + TYPE_GYROSCOPE at SENSOR_DELAY_GAME, of the phone or of a
/// controller (InputDevice.getSensorManager, Android 12+), feeding a <see cref="SensorMotionSource"/> on a "sensors"
/// thread; registered while the activity is in the foreground (<see cref="Resume"/> / <see cref="Pause"/>). The
/// fused orientation the game reads comes from the core's MotionFusion, as on the desktop, so
/// TYPE_GAME_ROTATION_VECTOR is not needed (the Vita reports raw sensors plus its own fusion).
/// </summary>
public sealed class AndroidMotionSensors : IDisposable
{
    readonly Activity _activity;
    readonly HandlerThread _thread;
    readonly Handler _handler;
    readonly DisplayRotationTracker _display;
    readonly AndroidMotion _motion;
    Listener? _listener;
    int _controllerId = -1;
    bool _resumed, _disposed;

    public AndroidMotionSensors(Activity activity, string? setting)
    {
        _activity = activity;
        _thread = new HandlerThread("sensors");
        _thread.Start();
        _handler = new Handler(_thread.Looper!);
        _display = new DisplayRotationTracker(activity, _handler);
        var phone = PhoneSensors();
        _motion = new AndroidMotion(AndroidMotionModes.Resolve(setting, phone != null));
        Log.Info($"Motion: {AndroidMotionModes.Label(_motion.Mode)} (setting '{setting}', phone sensors {(phone != null ? "yes" : "no")})");
        _display.Changed += r => { if (_motion.Sensors is { } s) s.DisplayRotation = r; };
        if (_motion.Mode == AndroidMotionMode.Controller) AppServices.Gamepads.DevicesChanged += OnControllersChanged;
    }

    /// <summary>The session's SceMotion source.</summary>
    public AndroidMotion Motion => _motion;

    /// <summary>The display rotation the camera also follows.</summary>
    public DisplayRotationTracker Display => _display;

    SensorManager? PhoneSensors()
    {
        var sm = (SensorManager?)_activity.GetSystemService(global::Android.Content.Context.SensorService);
        return sm?.GetDefaultSensor(SensorType.Accelerometer) != null && sm.GetDefaultSensor(SensorType.Gyroscope) != null ? sm : null;
    }

    /// <summary>The first connected controller that has an accelerometer and a gyroscope (Android 12+).</summary>
    (SensorManager Sm, int Id, string Name)? ControllerSensors()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(31)) return null;
        foreach (var (id, name, _) in AppServices.Gamepads.Devices)
        {
            try
            {
                var sm = InputDevice.GetDevice(id)?.SensorManager;
                if (sm?.GetDefaultSensor(SensorType.Accelerometer) != null && sm.GetDefaultSensor(SensorType.Gyroscope) != null) return (sm, id, name);
            }
            catch (Exception e) { Log.Warn($"Motion: sensors of controller {name}: {e.Message}"); }
        }
        return null;
    }

    /// <summary>OnResume: registers the listeners.</summary>
    public void Resume()
    {
        if (_disposed) return;
        _resumed = true;
        Register();
    }

    /// <summary>OnPause: unregisters them (no sensor work while the game is in the background).</summary>
    public void Pause()
    {
        _resumed = false;
        Unregister();
    }

    /// <summary>The re-centre action: the pose the phone (or controller) is held in now is the Vita lying flat.</summary>
    public void Recenter() => _motion.Recenter();

    void OnControllersChanged()
    {
        if (!_resumed || _disposed) return;
        var c = ControllerSensors();
        if ((c?.Id ?? -1) == _controllerId) return;
        Unregister();
        Register();
    }

    void Register()
    {
        if (_listener != null || _motion.Mode == AndroidMotionMode.Off) return;
        SensorManager? sm;
        SensorMotionSource source;
        if (_motion.Mode == AndroidMotionMode.Phone)
        {
            sm = PhoneSensors();
            if (sm == null) return;
            source = _motion.Sensors ?? new SensorMotionSource(SensorFrame.Phone, "phone sensors");
        }
        else
        {
            var c = ControllerSensors();
            if (c == null) { _controllerId = -1; _motion.Sensors = null; return; }
            sm = c.Value.Sm;
            // another controller: a fresh history (its own re-centre)
            source = _controllerId == c.Value.Id && _motion.Sensors != null ? _motion.Sensors : new SensorMotionSource(SensorFrame.Controller, $"controller '{c.Value.Name}'");
            _controllerId = c.Value.Id;
        }
        source.DisplayRotation = _display.Rotation;
        source.Clear();
        _motion.Sensors = source;
        var accel = sm.GetDefaultSensor(SensorType.Accelerometer)!;
        var gyro = sm.GetDefaultSensor(SensorType.Gyroscope)!;
        var listener = new Listener(source);
        bool ok = sm.RegisterListener(listener, accel, SensorDelay.Game, _handler)
                  & sm.RegisterListener(listener, gyro, SensorDelay.Game, _handler);
        _listener = listener;
        listener.Manager = sm;
        Log.Info($"Motion: {source.Name} registered ({accel.Name}, {gyro.Name}, rotation {_display.Rotation * 90}){(ok ? "" : ", a listener was refused")}");
    }

    void Unregister()
    {
        var l = _listener;
        _listener = null;
        if (l == null) return;
        try { l.Manager?.UnregisterListener(l); } catch (Exception e) { Log.Warn($"Motion: unregister: {e.Message}"); }
        l.Dispose();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        AppServices.Gamepads.DevicesChanged -= OnControllersChanged;
        Unregister();
        _display.Dispose();
        _thread.QuitSafely();
    }

    sealed class Listener(SensorMotionSource source) : Java.Lang.Object, ISensorEventListener
    {
        public SensorManager? Manager;

        public void OnAccuracyChanged(Sensor? sensor, [GeneratedEnum] SensorStatus accuracy) { }

        public void OnSensorChanged(SensorEvent? e)
        {
            if (e?.Values is not { Count: >= 3 } v || e.Sensor == null) return;
            var vec = new Vector3(v[0], v[1], v[2]);
            switch (e.Sensor.Type)
            {
                case SensorType.Accelerometer: source.OnAccelerometer(vec); break;
                case SensorType.Gyroscope: source.OnGyroscope(e.Timestamp, vec); break;
            }
        }
    }
}
