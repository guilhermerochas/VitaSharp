using System.Globalization;
using System.Numerics;
using System.Text;
using VitaSharp.Core.Host;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Capture;

/// <summary>
/// One line of a capture's <c>input.tsv</c>: what the guest read when it changed, with the host time (ns, capture
/// clock), the guest clock (µs) and the guest flip number at that moment. Kinds: <c>pad</c> (port, buttons, sticks),
/// <c>touch</c> (front/back, the contacts), <c>motion</c> (one sensor sample), <c>rng</c> (a sceKernelGetRandomNumber
/// result), <c>key</c> (an emulator hotkey) and <c>mark</c> (capture events).
/// </summary>
public readonly record struct InputEvent(string Kind, long Ns, long GuestUs, long Flip, string Data)
{
    public const string Header = "# kind\thostNs\tguestUs\tflip\tdata";

    public override string ToString() => $"{Kind}\t{Ns}\t{GuestUs}\t{Flip}\t{Data}";

    public static InputEvent? Parse(string line)
    {
        if (line.Length == 0 || line[0] == '#') return null;
        var p = line.Split('\t', 5);
        if (p.Length < 5 || !long.TryParse(p[1], out long ns) || !long.TryParse(p[2], out long us) || !long.TryParse(p[3], out long flip)) return null;
        return new InputEvent(p[0], ns, us, flip, p[4]);
    }

    public static InputEvent Now(string kind, string data) =>
        new(kind, CaptureClock.NowNs, GuestClock.Microseconds, Hle.Modules.Display.FramesPresented, data);
}

/// <summary>
/// Wraps the front-end's input and motion sources while a session capture is armed or recording: every change the
/// guest reads (buttons, sticks, touches) and every motion sample it receives goes to the capture. The values pass
/// through unchanged; the check is a compare per poll.
/// </summary>
public sealed class InputRecorder(IInputBackend? inner, IMotionSource? innerMotion, Action<InputEvent> sink) : IInputBackend, IMotionSource
{
    public IInputBackend? Inner { get; } = inner;
    public IMotionSource? InnerMotion { get; } = innerMotion;
    readonly ControllerState[] _last = new ControllerState[8];
    readonly bool[] _seen = new bool[8];
    readonly TouchPoint[][] _lastTouch = [new TouchPoint[8], new TouchPoint[8]];
    readonly int[] _lastTouchCount = new int[2];

    public ControllerState GetController(int port)
    {
        var s = Inner?.GetController(port) ?? ControllerState.Neutral;
        if ((uint)port < _last.Length)
            lock (_last)
            {
                ref var l = ref _last[port];
                if (!_seen[port] || l.Buttons != s.Buttons || l.Lx != s.Lx || l.Ly != s.Ly || l.Rx != s.Rx || l.Ry != s.Ry)
                {
                    _seen[port] = true;
                    l = s;
                    sink(InputEvent.Now("pad", FormatPad(port, s)));
                }
            }
        return s;
    }

    public static string FormatPad(int port, ControllerState s) => $"{port} {(uint)s.Buttons:X8} {s.Lx} {s.Ly} {s.Rx} {s.Ry}";

    public IReadOnlyList<TouchPoint> GetTouches(bool back)
    {
        Span<TouchPoint> t = stackalloc TouchPoint[8];
        int n = GetTouches(back, t);
        return t[..n].ToArray();
    }

    public int GetTouches(bool back, Span<TouchPoint> dest)
    {
        int n = Inner?.GetTouches(back, dest) ?? 0;
        int side = back ? 1 : 0;
        lock (_lastTouch)
        {
            var last = _lastTouch[side];
            bool same = n == _lastTouchCount[side];
            for (int i = 0; same && i < n && i < last.Length; i++) same = last[i] == dest[i];
            if (!same)
            {
                int m = Math.Min(n, last.Length);
                dest[..m].CopyTo(last);
                _lastTouchCount[side] = m;
                var sb = new StringBuilder(back ? "back" : "front");
                for (int i = 0; i < m; i++) sb.Append(CultureInfo.InvariantCulture, $" {dest[i].Id}:{dest[i].X:F4}:{dest[i].Y:F4}");
                sink(InputEvent.Now("touch", sb.ToString()));
            }
        }
        return n;
    }

    public bool HasSensors => InnerMotion?.HasSensors ?? false;

    /// <summary>Motion samples go to the sink only while this is set (armed or recording): with the capture off the
    /// recorder only keeps the last button and touch changes (the input before a recording).</summary>
    public volatile bool RecordMotion = true;

    public int ReadSamples(Span<MotionSample> dest)
    {
        int n = InnerMotion?.ReadSamples(dest) ?? 0;
        if (!RecordMotion) return n;
        for (int i = 0; i < n; i++)
        {
            var m = dest[i];
            sink(InputEvent.Now("motion", string.Create(CultureInfo.InvariantCulture,
                $"{m.TimestampUs} {m.Acceleration.X:F5} {m.Acceleration.Y:F5} {m.Acceleration.Z:F5} {m.AngularVelocity.X:F5} {m.AngularVelocity.Y:F5} {m.AngularVelocity.Z:F5}")));
        }
        return n;
    }
}

/// <summary>
/// Re-run of a capture: the recorded input, motion samples and random numbers handed back to the guest by guest
/// clock. A save state restores the guest clock exactly, so after <see cref="Activate"/> (once the capture's state is
/// loaded) the guest sees each change at the same guest time it saw it while recording. Before that, the pad is idle.
/// The emulation itself is not deterministic (host thread timing), so a re-run is close, not identical.
/// </summary>
public sealed class InputPlayback : IInputBackend, IMotionSource, IRandomHook
{
    readonly List<(long Us, ControllerState S)>[] _pad = [new(), new(), new(), new(), new()];
    readonly List<(long Us, TouchPoint[] T)>[] _touch = [new(), new()];
    readonly List<(long Us, MotionSample M)> _motion = new();
    readonly Queue<byte[]> _random = new();
    readonly List<(long Us, byte[] B)> _randomAll = new();
    long _motionReadUs = long.MinValue;
    int _motionIndex;

    public bool Active { get; private set; }
    /// <summary>Guest time of the last recorded event (the re-run's natural end).</summary>
    public long LastUs { get; private set; }
    public int Events { get; private set; }

    public InputPlayback(IEnumerable<InputEvent> events)
    {
        foreach (var e in events.OrderBy(e => e.GuestUs))
        {
            var p = e.Data.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            try
            {
                switch (e.Kind)
                {
                    case "pad" when p.Length >= 6 && int.TryParse(p[0], out int port) && (uint)port < _pad.Length:
                        _pad[port].Add((e.GuestUs, new ControllerState
                        {
                            Buttons = (VitaButtons)uint.Parse(p[1], NumberStyles.HexNumber),
                            Lx = byte.Parse(p[2]), Ly = byte.Parse(p[3]), Rx = byte.Parse(p[4]), Ry = byte.Parse(p[5]),
                        }));
                        break;
                    case "touch" when p.Length >= 1:
                        _touch[p[0] == "back" ? 1 : 0].Add((e.GuestUs, p.Skip(1).Select(t =>
                        {
                            var q = t.Split(':');
                            return new TouchPoint(int.Parse(q[0]), float.Parse(q[1], CultureInfo.InvariantCulture), float.Parse(q[2], CultureInfo.InvariantCulture));
                        }).ToArray()));
                        break;
                    case "motion" when p.Length >= 7:
                        float F(int i) => float.Parse(p[i], CultureInfo.InvariantCulture);
                        _motion.Add((e.GuestUs, new MotionSample(ulong.Parse(p[0]), new Vector3(F(1), F(2), F(3)), new Vector3(F(4), F(5), F(6)))));
                        break;
                    case "rng" when p.Length >= 1:
                        _randomAll.Add((e.GuestUs, Convert.FromHexString(p[0])));
                        break;
                    default: continue;
                }
                Events++;
                LastUs = Math.Max(LastUs, e.GuestUs);
            }
            catch (FormatException) { }
        }
    }

    public static InputPlayback Load(string inputTsv) =>
        new(File.ReadLines(inputTsv).Select(InputEvent.Parse).Where(e => e != null).Select(e => e!.Value));

    /// <summary>The capture's state was loaded: play the events from guest time <paramref name="fromUs"/> on.</summary>
    public void Activate(long fromUs)
    {
        lock (_random)
        {
            _random.Clear();
            foreach (var (us, b) in _randomAll) if (us >= fromUs) _random.Enqueue(b);
        }
        _motionReadUs = fromUs;
        _motionIndex = 0;
        Active = true;
        Log.Info($"[capture] re-run: input playback from guest time {fromUs / 1e6:F3} s ({Events} events, last at {LastUs / 1e6:F3} s, {_random.Count} random results)");
    }

    static int Find<T>(List<(long Us, T V)> list, long us)
    {
        int lo = 0, hi = list.Count - 1, r = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (list[mid].Us <= us) { r = mid; lo = mid + 1; } else hi = mid - 1;
        }
        return r;
    }

    public ControllerState GetController(int port)
    {
        if (!Active || (uint)port >= _pad.Length) return ControllerState.Neutral;
        int i = Find(_pad[port], GuestClock.Microseconds);
        return i >= 0 ? _pad[port][i].S : ControllerState.Neutral;
    }

    public IReadOnlyList<TouchPoint> GetTouches(bool back)
    {
        if (!Active) return [];
        var list = _touch[back ? 1 : 0];
        int i = Find(list, GuestClock.Microseconds);
        return i >= 0 ? list[i].T : [];
    }

    public bool HasSensors => _motion.Count > 0;

    public int ReadSamples(Span<MotionSample> dest)
    {
        if (!Active) return 0;
        long now = GuestClock.Microseconds;
        int n = 0;
        lock (_motion)
        {
            while (_motionIndex < _motion.Count && _motion[_motionIndex].Us <= _motionReadUs) _motionIndex++;
            while (_motionIndex < _motion.Count && _motion[_motionIndex].Us <= now && n < dest.Length) dest[n++] = _motion[_motionIndex++].M;
            _motionReadUs = now;
        }
        return n;
    }

    public bool Replay(Span<byte> dst)
    {
        if (!Active) return false;
        lock (_random)
        {
            if (!_random.TryDequeue(out var b) || b.Length != dst.Length) return false;
            b.CopyTo(dst);
            return true;
        }
    }

    public void Recorded(ReadOnlySpan<byte> bytes) { }
}
