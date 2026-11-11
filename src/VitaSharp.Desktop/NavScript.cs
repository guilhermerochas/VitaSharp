using System.Globalization;
using VitaSharp.Core.Host;
using VitaSharp.Core.Util;
using VitaSharp.Renderer.Vulkan;

namespace VitaSharp.Desktop;

/// <summary>
/// Screen-driven navigation script (<c>--nav=&lt;file&gt;</c>): one command per line, run in order, each one starting
/// when the previous one finished, so a script waits for the screen instead of counting frames (system apps load at
/// different speeds from run to run). Commands:
///   tap X Y [frames]                touch the front panel at normalised X, Y (default 8 frames)
///   btap X Y [frames]               touch the rear touch pad at normalised X, Y (default 8 frames)
///   swipe X0 Y0 X1 Y1 [frames]      drag from X0, Y0 to X1, Y1 (default 30 frames)
///   press BUTTON [frames]           cross, circle, square, triangle, up, down, left, right, start, select, l1, r1,
///                                   ltrigger, rtrigger, psbutton (default 8 frames)
///   lstick X Y [frames]             hold the left stick at X, Y in -1..1 (Y -1 = up; rstick: the right stick;
///                                   default 30 frames)
///   hold BUTTONS                    keep buttons held (comma separated, e.g. rtrigger,cross) under the following
///                                   commands until "hold none": accelerate while lstick steers
///   wait FRAMES                     presented frames
///   sleep SECONDS                   wall-clock seconds
///   stable [SECONDS] [TIMEOUT]      until the picture has not changed for SECONDS (default 1.5; give up after TIMEOUT,
///                                   default 60, with a NAV warning)
///   shot NAME                       the next presented frame, full size, as &lt;out&gt;/NAME.png
///   log TEXT                        a NAV line in the log (marks a step of the sweep)
///   quit                            end the run
/// With <c>--nav-live</c> the end of the file is not the end of the script: the file is polled for appended lines
/// (an interactive session: append commands, read the shots). Every executed command is logged as "NAV ...".
/// </summary>
sealed class NavScript : IInputBackend
{
    readonly string _file;
    readonly bool _live;
    readonly string _outDir;
    readonly VulkanRendererOptions _ro;
    readonly object _lock = new();
    // the action being performed: buttons held, or a touch (from, to, progress over the presented frames)
    VitaButtons _buttons, _held;
    byte _lx = 128, _ly = 128, _rx = 128, _ry = 128; // sticks held by lstick/rstick (0..255, 128 = centre)
    (float X0, float Y0, float X1, float Y1, long Start, long Frames)? _touch;
    bool _touchBack; // the touch above is on the rear pad (btap)
    long _actionEnd = -1;

    // last frame picture (sink) and when it last changed
    byte[]? _lastPicture;
    DateTime _lastChange = DateTime.UtcNow;
    long _sinkFrames;

    public volatile bool Quit;

    public NavScript(string file, bool live, string outDir, VulkanRendererOptions ro)
    {
        _file = file;
        _live = live;
        _outDir = outDir;
        _ro = ro;
        var previous = ro.FrameSink;
        ro.FrameSink = (n, px, w, h) => { previous?.Invoke(n, px, w, h); OnFrame(px); };
        new Thread(Run) { IsBackground = true, Name = "NavScript" }.Start();
    }

    static long Frames => VitaSharp.Core.Hle.Modules.Display.FramesPresented;

    void OnFrame(byte[] px)
    {
        lock (_lock)
        {
            _sinkFrames++;
            if (_lastPicture != null && Similar(_lastPicture, px)) return;
            _lastPicture = (byte[])px.Clone();
            _lastChange = DateTime.UtcNow;
        }
    }

    /// <summary>Same picture, allowing a little noise (a blinking cursor or a spinner of a few pixels still counts as a
    /// change; video noise does not exist in system apps).</summary>
    static bool Similar(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        int diff = 0;
        for (int i = 0; i < a.Length; i += 4)
            if (Math.Abs(a[i] - b[i]) + Math.Abs(a[i + 1] - b[i + 1]) + Math.Abs(a[i + 2] - b[i + 2]) > 24) diff++;
        return diff <= 2;
    }

    IEnumerable<string> Lines()
    {
        long pos = 0;
        var pending = "";
        while (true)
        {
            string chunk = "";
            try
            {
                using var fs = new FileStream(_file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (fs.Length > pos)
                {
                    fs.Seek(pos, SeekOrigin.Begin);
                    using var r = new StreamReader(fs);
                    chunk = r.ReadToEnd();
                    pos = fs.Length;
                }
            }
            catch (IOException) { }
            pending += chunk;
            int nl;
            while ((nl = pending.IndexOf('\n')) >= 0)
            {
                yield return pending[..nl].Trim();
                pending = pending[(nl + 1)..];
            }
            if (!_live)
            {
                if (pending.Trim().Length > 0) yield return pending.Trim();
                yield break;
            }
            Thread.Sleep(200);
        }
    }

    void Run()
    {
        try
        {
            foreach (var line in Lines())
            {
                if (Quit) return;
                if (line.Length == 0 || line.StartsWith('#')) continue;
                Log.Info($"NAV f{Frames}: {line}");
                Execute(line);
            }
            if (!_live) Log.Info($"NAV f{Frames}: end of script");
        }
        catch (Exception e) { Log.Error($"NAV script failed: {e.Message}"); }
        finally { if (!_live) Quit = true; }
    }

    void Execute(string line)
    {
        var inv = CultureInfo.InvariantCulture;
        var p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        float F(int i) => float.Parse(p[i], inv);
        long Len(int i, long def) => p.Length > i ? long.Parse(p[i], inv) : def;
        switch (p[0].ToLowerInvariant())
        {
            case "tap" or "btap":
                Act(VitaButtons.None, (F(1), F(2), F(1), F(2)), Len(3, 8), back: p[0].Equals("btap", StringComparison.OrdinalIgnoreCase));
                break;
            case "swipe":
                Act(VitaButtons.None, (F(1), F(2), F(3), F(4)), Len(5, 30));
                break;
            case "press":
                Act(Enum.Parse<VitaButtons>(p[1], true), null, Len(2, 8));
                break;
            case "hold":
                lock (_lock) _held = p.Length < 2 || p[1].Equals("none", StringComparison.OrdinalIgnoreCase) ? VitaButtons.None : Enum.Parse<VitaButtons>(p[1], true);
                break;
            case "lstick" or "rstick":
            {
                byte x = Axis(F(1)), y = Axis(F(2));
                bool left = p[0].Equals("lstick", StringComparison.OrdinalIgnoreCase);
                // lstick x y [frames [rampFrames]]: x/y in -1..1; the optional ramp moves the stick there over that
                // many frames, as a thumb does, instead of jumping from the centre
                long ramp = Len(4, 0);
                for (long i = 1; i < ramp; i++)
                {
                    byte rx = Axis(F(1) * i / ramp), ry = Axis(F(2) * i / ramp);
                    lock (_lock) { if (left) (_lx, _ly) = (rx, ry); else (_rx, _ry) = (rx, ry); }
                    WaitFrames(Frames + 1);
                }
                lock (_lock) { if (left) (_lx, _ly) = (x, y); else (_rx, _ry) = (x, y); }
                Act(VitaButtons.None, null, Len(3, 30));
                lock (_lock) { _lx = _ly = _rx = _ry = 128; }
                break;
            }
            case "wait":
                WaitFrames(Frames + Len(1, 30));
                break;
            case "sleep":
                Thread.Sleep(TimeSpan.FromSeconds(double.Parse(p[1], inv)));
                break;
            case "stable":
                Stable(p.Length > 1 ? double.Parse(p[1], inv) : 1.5, p.Length > 2 ? double.Parse(p[2], inv) : 60);
                break;
            case "shot":
                Shot(p.Length > 1 ? p[1] : $"f{Frames}");
                break;
            case "log":
                break; // the NAV line itself is the log entry
            case "quit":
                Quit = true;
                break;
            default:
                Log.Warn($"NAV unknown command '{line}'");
                break;
        }
    }

    void Act(VitaButtons buttons, (float, float, float, float)? touch, long frames, bool back = false)
    {
        long start = Frames;
        lock (_lock)
        {
            _buttons = buttons;
            _touchBack = back;
            _touch = touch is var (x0, y0, x1, y1) ? (x0, y0, x1, y1, start, Math.Max(1, frames)) : null;
            _actionEnd = start + Math.Max(1, frames);
        }
        WaitFrames(_actionEnd);
        lock (_lock) { _buttons = VitaButtons.None; _touch = null; }
        WaitFrames(Frames + 2); // a release the guest can see before the next press
    }

    static byte Axis(float v) => (byte)Math.Clamp((int)MathF.Round((v + 1) * 127.5f), 0, 255);

    void WaitFrames(long target)
    {
        var deadline = DateTime.UtcNow.AddSeconds(120); // a guest that stopped presenting must not stall the script
        while (Frames < target && !Quit && DateTime.UtcNow < deadline) Thread.Sleep(5);
        if (Frames < target && !Quit) Log.Warn($"NAV no frames presented for 120 s (waiting for frame {target}, at {Frames})");
    }

    void Stable(double seconds, double timeout)
    {
        var start = DateTime.UtcNow;
        Thread.Sleep(TimeSpan.FromSeconds(Math.Min(seconds, 0.5)));
        while (!Quit)
        {
            DateTime changed;
            lock (_lock) changed = _lastChange;
            var now = DateTime.UtcNow;
            if ((now - changed).TotalSeconds >= seconds && (now - start).TotalSeconds >= seconds) return;
            if ((now - start).TotalSeconds > timeout) { Log.Warn($"NAV f{Frames}: screen still changing after {timeout} s"); return; }
            Thread.Sleep(50);
        }
    }

    void Shot(string name)
    {
        string path = Path.Combine(_outDir, name + ".png");
        try { File.Delete(path); } catch (IOException) { }
        _ro.ShotPath = path;
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!File.Exists(path) && DateTime.UtcNow < deadline && !Quit) Thread.Sleep(20);
        Thread.Sleep(50); // the PNG is written in one go; let it close
        if (!File.Exists(path)) Log.Warn($"NAV shot {name}: no frame presented");
    }

    public ControllerState GetController(int port)
    {
        var st = ControllerState.Neutral;
        lock (_lock) { st.Buttons = _buttons | _held; st.Lx = _lx; st.Ly = _ly; st.Rx = _rx; st.Ry = _ry; }
        return st;
    }

    public IReadOnlyList<TouchPoint> GetTouches(bool back)
    {
        Span<TouchPoint> one = stackalloc TouchPoint[1];
        return GetTouches(back, one) == 1 ? [one[0]] : [];
    }

    public int GetTouches(bool back, Span<TouchPoint> dest)
    {
        if (dest.Length == 0) return 0;
        lock (_lock)
        {
            if (_touch is not { } t || back != _touchBack) return 0;
            float k = Math.Clamp((Frames - t.Start) / (float)t.Frames, 0, 1);
            dest[0] = new TouchPoint(1, t.X0 + (t.X1 - t.X0) * k, t.Y0 + (t.Y1 - t.Y0) * k);
            return 1;
        }
    }
}
