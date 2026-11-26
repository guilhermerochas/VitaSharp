#if VITASHARP_DEVELOPER
using Android.App;
using Android.OS;
using Android.Views;
using AView = Android.Views.View;
using VitaSharp.Core.Util;

namespace VitaSharp.Android.Developer;

/// <summary>
/// Developer builds: VITASHARP_ANDROID_UI_INPUT names a file polled every 250 ms on the UI thread of an activity (the
/// game screen polls "&lt;path&gt;.game", the app's screens "&lt;path&gt;", since they run in two processes), so it also
/// works while the game is paused and the scripted pad is not polled. Each line is injected into that activity through
/// the activity's own dispatch (see <see cref="Inject"/>): "key:dpad_down" / "key:button_a" (any
/// KEYCODE_ name, sent as a gamepad key; "key:back" is the system back), "touch:x:y" (a tap at x, y in 0..1 of the
/// window, held 80 ms so the touch driver samples it at a vblank) or "drag:x1:y1:x2:y2:ms" (a finger moved from the
/// first point to the second in that many milliseconds, a move every 16 ms: page scrolls, the lock screen's peel). The
/// file is deleted once read. Device checks of the pause menu and of the screens use it on phones that
/// refuse <c>adb shell input</c>.
/// </summary>
public static class UiScript
{
    public static void Start(Activity activity, string suffix = "")
    {
        if (System.Environment.GetEnvironmentVariable(EnvVars.AndroidUiInput) is not { Length: > 0 } basePath) return;
        string path = basePath + suffix;
        Log.Info($"{EnvVars.AndroidUiInput}: polling {path}");
        var handler = new Handler(Looper.MainLooper!);
        void Poll()
        {
            if (activity.IsDestroyed) return;
            try
            {
                if (File.Exists(path))
                {
                    var lines = File.ReadAllLines(path);
                    File.Delete(path);
                    int w = activity.Window?.DecorView.Width ?? 0, h = activity.Window?.DecorView.Height ?? 0;
                    Task.Run(() => Inject(activity, lines, w, h));
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            handler.PostDelayed(Poll, 250);
        }
        handler.PostDelayed(Poll, 250);
    }

    /// <summary>Off the UI thread: each line is dispatched on the UI thread, 200 ms apart. HyperOS refuses
    /// Instrumentation's injection even into the app's own focused window (INJECT_EVENTS), so the events go straight
    /// into the activity: <see cref="Activity.DispatchKeyEvent"/> / <see cref="Activity.DispatchTouchEvent"/>, a D-pad key
    /// nobody took moves the focus as the view root would (FocusSearch), and "key:back" is the activity's back
    /// (OnBackPressed, which the system's back-invoked callback also reaches).</summary>
    static void Inject(Activity activity, string[] lines, int width, int height)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        foreach (var raw in lines)
        {
            var p = raw.Trim().Split(':');
            if (p[0].Length == 0) continue;
            if (p[0] is "touch" or "drag")
            {
                string? gestureError = Gesture(activity, p, width, height, inv);
                if (gestureError != null)
                {
                    Log.Warn($"{EnvVars.AndroidUiInput}: '{raw}': {gestureError}");
                }
                else
                {
                    Log.Info($"{EnvVars.AndroidUiInput}: {raw}");
                }

                Thread.Sleep(200);
                continue;
            }
            using var done = new ManualResetEventSlim();
            string? error = null;
            activity.RunOnUiThread(() =>
            {
                try
                {
                    error = Dispatch(activity, p, width, height, inv);
                }
                catch (Exception e)
                {
                    error = e.Message;
                }
                finally
                {
                    done.Set();
                }
            });
            done.Wait(2000);
            if (error != null) Log.Warn($"{EnvVars.AndroidUiInput}: '{raw}': {error}");
            else Log.Info($"{EnvVars.AndroidUiInput}: {raw}");
            Thread.Sleep(200);
        }
    }

    static string? Dispatch(Activity activity, string[] p, int width, int height, IFormatProvider inv)
    {
        if (p[0] == "key" && p.Length > 1 && Enum.TryParse<Keycode>(p[1].Replace("_", ""), true, out var code))
        {
            if (code == Keycode.Back)
            {
#pragma warning disable CA1422, CS0618 // the back of the activity itself (GameActivity opens the pause menu there)
                activity.OnBackPressed();
#pragma warning restore CA1422, CS0618
                return null;
            }
            FocusSearchDirection? dir = code switch
            {
                Keycode.DpadUp => FocusSearchDirection.Up, Keycode.DpadDown => FocusSearchDirection.Down,
                Keycode.DpadLeft => FocusSearchDirection.Left, Keycode.DpadRight => FocusSearchDirection.Right, _ => null,
            };
            // a real key leaves touch mode first and focuses the window's first focusable view when none has the focus
            // (ViewRootImpl.ensureTouchMode); a D-pad press that only did that is consumed, as on a device
            if (activity.Window?.DecorView is { IsInTouchMode: true } decor)
            {
                // GetFocusables in touch mode lists only the views focusable in touch mode (none in the pause menu):
                // the views a key can focus are all the focusable ones, and leaving touch mode restores the default
                // focus (View.FocusedByDefault) before the first one (ViewRootImpl.leaveTouchMode)
                var all = new List<AView>();
                decor.AddFocusables(all, FocusSearchDirection.Forward, FocusablesFlags.All);
                var target = activity.CurrentFocus ?? all.FirstOrDefault(v => v.FocusedByDefault) ?? all.FirstOrDefault();
                target?.RequestFocusFromTouch();
                if (dir != null && target != null && activity.CurrentFocus == target) return null;
            }
            long now = SystemClock.UptimeMillis();
            bool taken = activity.DispatchKeyEvent(new KeyEvent(now, now, KeyEventActions.Down, code, 0, 0, -1, 0, 0, InputSourceType.Gamepad));
            activity.DispatchKeyEvent(new KeyEvent(now, SystemClock.UptimeMillis(), KeyEventActions.Up, code, 0, 0, -1, 0, 0, InputSourceType.Gamepad));
            if (!taken && dir is { } d && activity.CurrentFocus is { } focus) focus.FocusSearch(d)?.RequestFocus(d);
            return null;
        }
        return "unknown line";
    }

    /// <summary>Off the UI thread: a tap ("touch:x:y", held 80 ms) or a drag ("drag:x1:y1:x2:y2:ms", a move every
    /// 16 ms), each event dispatched on the UI thread at its time, so the touch driver sees the finger down over
    /// several vblanks as with a real finger. Null when done, else the reason.</summary>
    static string? Gesture(Activity activity, string[] p, int width, int height, IFormatProvider inv)
    {
        bool drag = p[0] == "drag";
        if ((drag && p.Length < 6) || (!drag && p.Length < 3))
        {
            return "missing coordinates";
        }

        float x1 = float.Parse(p[1], inv) * width;
        float y1 = float.Parse(p[2], inv) * height;
        float x2 = drag ? float.Parse(p[3], inv) * width : x1;
        float y2 = drag ? float.Parse(p[4], inv) * height : y1;
        int duration = drag ? Math.Max(16, int.Parse(p[5], inv)) : 80;
        long down = SystemClock.UptimeMillis();
        Send(activity, down, MotionEventActions.Down, x1, y1);
        int steps = Math.Max(1, duration / 16);
        for (int i = 1; i <= steps; i++)
        {
            Thread.Sleep(duration / steps);
            if (drag)
            {
                float t = (float)i / steps;
                Send(activity, down, MotionEventActions.Move, x1 + ((x2 - x1) * t), y1 + ((y2 - y1) * t));
            }
        }

        Send(activity, down, MotionEventActions.Up, x2, y2);
        return null;
    }

    /// <summary>One touch event, dispatched on the UI thread and waited for.</summary>
    static void Send(Activity activity, long downTime, MotionEventActions action, float x, float y)
    {
        using var done = new ManualResetEventSlim();
        activity.RunOnUiThread(() =>
        {
            try
            {
                using MotionEvent motion = MotionEvent.Obtain(downTime, SystemClock.UptimeMillis(), action, x, y, 0)!;
                activity.DispatchTouchEvent(motion);
            }
            finally
            {
                done.Set();
            }
        });
        done.Wait(2000);
    }
}
#endif
