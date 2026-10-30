using Android;
using Android.App;
using Android.Content.PM;
using Android.Graphics;
using Android.Hardware.Camera2;
using Android.Media;
using Android.OS;
using Android.Runtime;
using Android.Views;
using VitaSharp.Android.Media;
using VitaSharp.Core.Host;
using VitaSharp.Core.Util;
using OperationCanceledException = System.OperationCanceledException;

namespace VitaSharp.Android.Game;

/// <summary>
/// The phone's cameras for SceCamera through Camera2: the front camera for the Vita's front camera (0), the back one
/// for its back camera (1); YUV_420_888 from an ImageReader at the smallest size covering the request, converted
/// upright (sensor orientation and display rotation) to RGBA into the core's frame buffer, which crops/scales and
/// converts it to the format the game asked for. The AE frame-rate range follows the game's rate. The CAMERA
/// permission is asked the first time a game starts a camera; denied, the cameras deliver the grey "no camera" frames
/// (as on a desktop without a webcam). One capture is shared by both Vita cameras and lingers 2 s after a stop
/// (<see cref="SharedCapture"/>, the desktop's scheme: games switch sensors by stop + start). In the background the
/// device is closed and reopened on resume (Android takes cameras away from background apps).
/// </summary>
public sealed class AndroidCamera : ICameraSource
{
    /// <summary>The requestCode of the CAMERA permission request (GameActivity.OnRequestPermissionsResult).</summary>
    public const int PermissionRequestCode = 0x5CA0;

    readonly Activity _activity;
    readonly Func<int> _displayRotation;
    readonly CameraManager? _manager;
    readonly SharedCapture _shared = new(TimeSpan.FromSeconds(2));
    readonly object _lock = new();
    readonly List<Capture> _captures = new();
    TaskCompletionSource<bool>? _permission;
    bool _denied, _paused;
    List<Info>? _cameras;

    sealed record Info(string Id, int Facing, int SensorOrientation, (int W, int H)[] Sizes, (int Lo, int Hi)[] FpsRanges);

    public AndroidCamera(Activity activity, Func<int> displayRotation)
    {
        _activity = activity;
        _displayRotation = displayRotation;
        _manager = (CameraManager?)activity.GetSystemService(global::Android.Content.Context.CameraService);
    }

    List<Info> Cameras()
    {
        if (_cameras != null) return _cameras;
        var list = new List<Info>();
        try
        {
            foreach (var id in _manager?.GetCameraIdList() ?? [])
            {
                try
                {
                    var ch = _manager!.GetCameraCharacteristics(id);
                    int facing = Int(ch.Get(CameraCharacteristics.LensFacing)) ?? CameraSupport.LensExternal;
                    int orient = Int(ch.Get(CameraCharacteristics.SensorOrientation)) ?? 0;
                    var map = ch.Get(CameraCharacteristics.ScalerStreamConfigurationMap).JavaCast<global::Android.Hardware.Camera2.Params.StreamConfigurationMap>();
                    var sizes = map?.GetOutputSizes((int)ImageFormatType.Yuv420888)?.Select(s => (s.Width, s.Height)).ToArray() ?? [];
                    var fps = ch.Get(CameraCharacteristics.ControlAeAvailableTargetFpsRanges) is { } r
                        ? JNIEnv.GetArray<global::Android.Util.Range>(r.Handle)?.Select(x => (Int(x.Lower) ?? 0, Int(x.Upper) ?? 0)).ToArray() ?? []
                        : [];
                    if (sizes.Length > 0) list.Add(new Info(id, facing, orient, sizes, fps));
                }
                catch (Exception e) { Log.Warn($"Camera: characteristics of camera {id}: {e.Message}"); }
            }
        }
        catch (Exception e) { Log.Warn($"Camera: camera list: {e.Message}"); }
        return _cameras = list;
    }

    static int? Int(Java.Lang.Object? o) => o == null ? null : o.JavaCast<Java.Lang.Number>()?.IntValue();

    static string FacingName(int f) => f switch { CameraSupport.LensFront => "front", CameraSupport.LensBack => "back", _ => "external" };

    public IReadOnlyList<string> DeviceNames => Cameras().Select(c => $"camera {c.Id} ({FacingName(c.Facing)})").ToList();

    public ICameraStream? Open(CameraFacing facing, int width, int height, int fps)
    {
        lock (_lock)
            if (_denied)
            {
                Log.Info("Camera: permission denied, grey frames");
                return null;
            }
        var cams = Cameras();
        var id = CameraSupport.PickCamera(cams.Select(c => (c.Id, c.Facing)).ToList(), (int)facing);
        if (id == null) { Log.Info("Camera: the device has no camera"); return null; }
        var info = cams.First(c => c.Id == id);
        bool front = info.Facing == CameraSupport.LensFront;
        int rot = CameraSupport.UprightRotation(info.SensorOrientation, front, _displayRotation());
        var size = CameraSupport.ChooseSize(info.Sizes, width, height, rotated: rot is 90 or 270)!.Value;
        var range = CameraSupport.ChooseFpsRange(info.FpsRanges, fps);
        var mode = (id, size.W, size.H, range);
        var stream = _shared.Acquire(mode, after => NewCapture(info, size, range, after), out bool reused);
        Log.Info($"Camera: {facing} camera -> {FacingName(info.Facing)} camera {id} {size.W}x{size.H} YUV_420_888, fps {range?.Lo}-{range?.Hi} " +
                 $"(asked {width}x{height} at {fps}), sensor {info.SensorOrientation} deg{(reused ? " (capture already running, reused)" : "")}");
        return stream;
    }

    Capture NewCapture(Info info, (int W, int H) size, (int Lo, int Hi)? range, Task after)
    {
        var c = new Capture(this, info, size, range, after);
        lock (_lock) _captures.Add(c);
        return c;
    }

    // ------------------------------------------------------------------ permission

    /// <summary>True once the CAMERA permission is granted; asks for it (once per game process) otherwise.</summary>
    Task<bool> EnsurePermissionAsync()
    {
        if (_activity.CheckSelfPermission(Manifest.Permission.Camera) == Permission.Granted) return Task.FromResult(true);
        lock (_lock)
        {
            if (_denied) return Task.FromResult(false);
            if (_permission != null) return _permission.Task;
            _permission = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        Log.Info("Camera: asking for the CAMERA permission");
        _activity.RunOnUiThread(() =>
        {
            try { _activity.RequestPermissions([Manifest.Permission.Camera], PermissionRequestCode); }
            catch (Exception e) { Log.Warn($"Camera: permission request failed: {e.Message}"); OnPermissionResult(false); }
        });
        return _permission.Task;
    }

    /// <summary>GameActivity.OnRequestPermissionsResult; false when the request was not this one.</summary>
    public bool OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
    {
        if (requestCode != PermissionRequestCode) return false;
        OnPermissionResult(grantResults.Length > 0 && grantResults[0] == Permission.Granted);
        return true;
    }

    void OnPermissionResult(bool granted)
    {
        TaskCompletionSource<bool>? tcs;
        lock (_lock)
        {
            tcs = _permission;
            _permission = null;
            if (!granted) _denied = true;
        }
        Log.Info(granted ? "Camera: permission granted" : "Camera: permission denied, the Vita cameras deliver grey frames");
        tcs?.TrySetResult(granted);
    }

    // ------------------------------------------------------------------ lifecycle

    /// <summary>OnPause: closes the open cameras (the captures stay, grey until resume).</summary>
    public void Pause()
    {
        List<Capture> list;
        lock (_lock) { _paused = true; list = _captures.ToList(); }
        foreach (var c in list) c.Suspend();
    }

    /// <summary>OnResume: reopens them.</summary>
    public void Resume()
    {
        List<Capture> list;
        lock (_lock) { _paused = false; list = _captures.ToList(); }
        foreach (var c in list) c.RetryNow();
    }

    bool Paused { get { lock (_lock) return _paused; } }

    public void Dispose() => _shared.Dispose();

    /// <summary>
    /// One opened camera: a loop that waits for the permission and the previous capture's close, opens the device,
    /// configures an ImageReader session with a repeating request, and on a disconnect or error (another app took
    /// the camera, the activity went to the background) closes everything and tries again with a growing delay, at once
    /// on resume or when a guest starts a camera again.
    /// </summary>
    sealed class Capture : IHostCapture
    {
        readonly AndroidCamera _owner;
        readonly Info _info;
        readonly (int W, int H) _size;
        readonly (int Lo, int Hi)? _range;
        readonly CameraFrameBuffer _frames = new();
        readonly CancellationTokenSource _cts = new();
        readonly SemaphoreSlim _wake = new(0, 1);
        readonly HandlerThread _thread;
        readonly Handler _handler;
        readonly Task _run;
        readonly object _lock = new();
        CameraDevice? _device;
        CameraCaptureSession? _session;
        ImageReader? _reader;
        TaskCompletionSource<string>? _lost;
        bool _loggedError, _loggedFirst;

        public CameraFrameBuffer Frames => _frames;

        public Capture(AndroidCamera owner, Info info, (int W, int H) size, (int Lo, int Hi)? range, Task after)
        {
            _owner = owner;
            _info = info;
            _size = size;
            _range = range;
            _thread = new HandlerThread("camera");
            _thread.Start();
            _handler = new Handler(_thread.Looper!);
            _run = Task.Run(() => RunAsync(after));
        }

        async Task RunAsync(Task after)
        {
            var ct = _cts.Token;
            try { await after.WaitAsync(TimeSpan.FromSeconds(5), ct); } // the previous capture closes its camera first
            catch (OperationCanceledException) { return; }
            catch { }
            bool granted;
            try { granted = await _owner.EnsurePermissionAsync().WaitAsync(ct); }
            catch (OperationCanceledException) { return; }
            if (!granted) return; // grey frames
            for (int attempt = 1; ; )
            {
                if (_owner.Paused)
                {
                    try { await _wake.WaitAsync(ct); } catch (OperationCanceledException) { return; }
                    continue;
                }
                string reason;
                try
                {
                    var lost = await OpenAsync(ct);
                    if (attempt > 1) Log.Info($"Camera: camera {_info.Id} started on attempt {attempt}");
                    attempt = 1;
                    reason = await lost.WaitAsync(ct);
                    Log.Info($"Camera: camera {_info.Id} {reason}");
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (Exception e)
                {
                    reason = e.Message;
                    if (attempt == 1) Log.Warn($"Camera: could not start camera {_info.Id}: {e.Message} (grey frames, retrying)");
                    else if (attempt % 10 == 0) Log.Warn($"Camera: camera {_info.Id} still failing after {attempt} attempts: {e.Message}");
                    attempt++;
                }
                Close();
                if (ct.IsCancellationRequested) return;
                if (_owner.Paused) continue;
                try { await _wake.WaitAsync(TimeSpan.FromSeconds(Math.Min(10, 2 * attempt)), ct); }
                catch (OperationCanceledException) { return; }
            }
        }

        /// <summary>Opens the device and starts the repeating request; the returned task completes (with a reason)
        /// when the camera is lost.</summary>
        async Task<Task<string>> OpenAsync(CancellationToken ct)
        {
            var lost = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_lock) _lost = lost;
            var opened = new TaskCompletionSource<CameraDevice>(TaskCreationOptions.RunContinuationsAsynchronously);
            _owner._manager!.OpenCamera(_info.Id, new DeviceCallback(opened, lost), _handler);
            CameraDevice device;
            try { device = await opened.Task.WaitAsync(TimeSpan.FromSeconds(10), ct); }
            catch
            {
                // stopped or timed out while opening: a device that opens later is closed at once
                _ = opened.Task.ContinueWith(t => { if (t.IsCompletedSuccessfully) try { t.Result.Close(); } catch (Exception) { } }, TaskScheduler.Default);
                throw;
            }
            lock (_lock) _device = device;

            var reader = ImageReader.NewInstance(_size.W, _size.H, ImageFormatType.Yuv420888, 2);
            reader.SetOnImageAvailableListener(new ImageListener(this), _handler);
            lock (_lock) _reader = reader;
            var configured = new TaskCompletionSource<CameraCaptureSession>(TaskCreationOptions.RunContinuationsAsynchronously);
#pragma warning disable CA1422 // createCaptureSession(List, ...) is deprecated from API 30 but available from 21; minSdk is 26
            device.CreateCaptureSession([reader.Surface!], new SessionCallback(configured, lost), _handler);
#pragma warning restore CA1422
            var session = await configured.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
            lock (_lock) _session = session;

            var request = device.CreateCaptureRequest(CameraTemplate.Preview);
            request.AddTarget(reader.Surface!);
            if (_range is { } r)
                request.Set(CaptureRequest.ControlAeTargetFpsRange!, new global::Android.Util.Range(Java.Lang.Integer.ValueOf(r.Lo), Java.Lang.Integer.ValueOf(r.Hi)));
            session.SetRepeatingRequest(request.Build(), null, _handler);
            return lost.Task;
        }

        void OnImage(ImageReader reader)
        {
            global::Android.Media.Image? img = null;
            try
            {
                img = reader.AcquireLatestImage();
                if (img == null) return;
                var planes = img.GetPlanes();
                if (planes is not { Length: 3 }) return;
                int w = img.Width, h = img.Height;
                int rot = CameraSupport.UprightRotation(_info.SensorOrientation, _info.Facing == CameraSupport.LensFront, _owner._displayRotation());
                var (ow, oh) = CameraSupport.RotatedSize(w, h, rot);
                _frames.Publish(ow, oh, dst => Convert(planes, w, h, rot, dst));
                if (!_loggedFirst) { _loggedFirst = true; Log.Info($"Camera: first frame {w}x{h}, rotation {rot}, pixel stride {planes[1].PixelStride}"); }
            }
            catch (Exception e)
            {
                if (!_loggedError) { _loggedError = true; Log.Warn($"Camera: frame conversion failed: {e.Message}"); }
            }
            finally { img?.Close(); }
        }

        static void Convert(global::Android.Media.Image.Plane[] p, int w, int h, int rot, Span<byte> dst) =>
            CameraSupport.ToRgba(Plane(p[0]), Plane(p[1]), Plane(p[2]), w, h, rot, dst);

        static unsafe YuvImageCopy.Plane Plane(global::Android.Media.Image.Plane p)
        {
            var b = p.Buffer!;
            var span = new ReadOnlySpan<byte>((byte*)b.GetDirectBufferAddress() + b.Position(), b.Remaining());
            return new YuvImageCopy.Plane(span, p.RowStride, p.PixelStride);
        }

        void Close()
        {
            CameraCaptureSession? s;
            CameraDevice? d;
            ImageReader? r;
            lock (_lock)
            {
                s = _session; d = _device; r = _reader;
                _session = null; _device = null; _reader = null; _lost = null;
            }
            try { s?.Close(); } catch (Exception) { }
            try { d?.Close(); } catch (Exception) { }
            try { r?.Close(); } catch (Exception) { }
        }

        /// <summary>The activity went to the background: close the camera now.</summary>
        public void Suspend()
        {
            TaskCompletionSource<string>? lost;
            lock (_lock) lost = _lost;
            lost?.TrySetResult("closed (in the background)");
        }

        public void RetryNow()
        {
            try { _wake.Release(); } catch (SemaphoreFullException) { }
        }

        public async Task StopAsync()
        {
            _cts.Cancel();
            try { await _run; } catch { }
            Close();
            lock (_owner._lock) _owner._captures.Remove(this);
            _thread.QuitSafely();
            _cts.Dispose();
        }

        sealed class ImageListener(Capture owner) : Java.Lang.Object, ImageReader.IOnImageAvailableListener
        {
            public void OnImageAvailable(ImageReader? reader) { if (reader != null) owner.OnImage(reader); }
        }
    }

    sealed class DeviceCallback(TaskCompletionSource<CameraDevice> opened, TaskCompletionSource<string> lost) : CameraDevice.StateCallback
    {
        public override void OnOpened(CameraDevice camera) => opened.TrySetResult(camera);

        public override void OnDisconnected(CameraDevice camera)
        {
            if (!opened.TrySetException(new IOException("camera disconnected"))) lost.TrySetResult("disconnected");
            try { camera.Close(); } catch (Exception) { }
        }

        public override void OnError(CameraDevice camera, [GeneratedEnum] CameraError error)
        {
            if (!opened.TrySetException(new IOException($"camera error {error}"))) lost.TrySetResult($"error {error}");
            try { camera.Close(); } catch (Exception) { }
        }
    }

    sealed class SessionCallback(TaskCompletionSource<CameraCaptureSession> configured, TaskCompletionSource<string> lost) : CameraCaptureSession.StateCallback
    {
        public override void OnConfigured(CameraCaptureSession session) => configured.TrySetResult(session);

        public override void OnConfigureFailed(CameraCaptureSession session)
        {
            if (!configured.TrySetException(new IOException("session configuration failed"))) lost.TrySetResult("session failed");
        }
    }
}
