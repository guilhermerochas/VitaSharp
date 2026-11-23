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
        if (line.Length == 0 || line[0] == '#')
        {
            return null;
        }

        string[] p = line.Split('\t', 5);
        if (p.Length < 5 || !long.TryParse(p[1], out long ns) || !long.TryParse(p[2], out long us) || !long.TryParse(p[3], out long flip))
        {
            return null;
        }

        return new InputEvent(p[0], ns, us, flip, p[4]);
    }

    public static InputEvent Now(string kind, string data) =>
        new(kind, CaptureClock.NowNs, GuestClock.Microseconds, Hle.Modules.Display.FramesPresented, data);
}

/// <summary>The "mark" events the capture writes for pauses of the whole emulation (<see cref="EmulationPause"/>).</summary>
public static class CaptureMarks
{
    /// <summary>The player paused (pause key, pause menu).</summary>
    public const string Paused = "emulation paused";
    /// <summary>The emulator paused for itself (a save state taken or loaded).</summary>
    public const string PausedInternal = "emulation paused (save state)";
    public const string Resumed = "emulation resumed";

    public static bool IsPause(string data) => data is Paused or PausedInternal;
}

/// <summary>
/// Wraps the front-end's input and motion sources while a session capture is armed or recording: every change the
/// guest reads (buttons, sticks, touches) and every motion sample it receives goes to the capture. The values pass
/// through unchanged; the check is a compare per poll.
/// </summary>
public sealed class InputRecorder(IInputBackend? inner, IMotionSource? innerMotion, Action<InputEvent> sink) : IInputBackend, IMotionSource, IGuestReadInput
{
    /// <summary>The token a pad or touch event carries when the guest's read saw the change: "read=N", the N-th read of
    /// that port or panel by the process (<see cref="IGuestReadInput"/>).</summary>
    public const string ReadToken = "read=";

    private readonly ControllerState[] _guestLast = new ControllerState[8];
    private readonly bool[] _guestSeen = new bool[8];
    private readonly TouchPoint[][] _guestLastTouch = [new TouchPoint[8], new TouchPoint[8]];
    private readonly int[] _guestLastTouchCount = new int[2];
    private readonly bool[] _guestTouchSeen = new bool[2];

    public IInputBackend? Inner { get; } = inner;
    public IMotionSource? InnerMotion { get; } = innerMotion;
    private readonly ControllerState[] _last = new ControllerState[8];
    private readonly bool[] _seen = new bool[8];
    private readonly TouchPoint[][] _lastTouch = [new TouchPoint[8], new TouchPoint[8]];
    private readonly int[] _lastTouchCount = new int[2];

    public ControllerState GetController(int port)
    {
        ControllerState controllerState = Inner?.GetController(port) ?? ControllerState.Neutral;
        if ((uint)port < _last.Length)
        {
            lock (_last)
            {
                // the guest reads this port through its controller driver (the read-numbered events below): the other
                // readers (the system's power and PS button polls) are not the guest's input
                if (_guestSeen[port])
                {
                    return controllerState;
                }

                ref ControllerState l = ref _last[port];
                if (!_seen[port] || l.Buttons != controllerState.Buttons || l.Lx != controllerState.Lx || l.Ly != controllerState.Ly || l.Rx != controllerState.Rx || l.Ry != controllerState.Ry)
                {
                    _seen[port] = true;
                    l = controllerState;
                    sink(InputEvent.Now("pad", FormatPad(port, controllerState)));
                }
            }
        }

        return controllerState;
    }

    /// <summary>The guest's <paramref name="guestRead"/>-th read of <paramref name="port"/>: a change is logged with the
    /// number of the read that first saw it, which the re-run hands it to.</summary>
    public ControllerState GetController(int port, long guestRead)
    {
        // a re-run's own recording wraps the playback: the read number goes on to it
        ControllerState controllerState = Inner is IGuestReadInput ordered ? ordered.GetController(port, guestRead) : Inner?.GetController(port) ?? ControllerState.Neutral;
        if ((uint)port < _guestLast.Length)
        {
            lock (_last)
            {
                ref ControllerState l = ref _guestLast[port];
                if (!_guestSeen[port] || l.Buttons != controllerState.Buttons || l.Lx != controllerState.Lx || l.Ly != controllerState.Ly || l.Rx != controllerState.Rx || l.Ry != controllerState.Ry)
                {
                    _guestSeen[port] = true;
                    l = controllerState;
                    sink(InputEvent.Now("pad", FormatPad(port, controllerState) + " " + ReadToken + guestRead.ToString(CultureInfo.InvariantCulture)));
                }
            }
        }

        return controllerState;
    }

    public int GetTouches(bool back, Span<TouchPoint> dest, long guestRead)
    {
        int n = Inner is IGuestReadInput ordered ? ordered.GetTouches(back, dest, guestRead) : Inner?.GetTouches(back, dest) ?? 0;
        int side = back ? 1 : 0;
        lock (_lastTouch)
        {
            if (!_guestTouchSeen[side] || !SameTouches(_guestLastTouch[side], _guestLastTouchCount[side], dest[..n]))
            {
                _guestTouchSeen[side] = true;
                int m = Math.Min(n, _guestLastTouch[side].Length);
                dest[..m].CopyTo(_guestLastTouch[side]);
                _guestLastTouchCount[side] = m;
                sink(InputEvent.Now("touch", FormatTouches(back, dest[..m]) + " " + ReadToken + guestRead.ToString(CultureInfo.InvariantCulture)));
            }
        }

        return n;
    }

    private static bool SameTouches(TouchPoint[] last, int lastCount, ReadOnlySpan<TouchPoint> now)
    {
        if (now.Length != lastCount)
        {
            return false;
        }

        for (int i = 0; i < now.Length && i < last.Length; i++)
        {
            if (last[i] != now[i])
            {
                return false;
            }
        }

        return true;
    }

    private static string FormatTouches(bool back, ReadOnlySpan<TouchPoint> touches)
    {
        var sb = new StringBuilder(back ? "back" : "front");
        foreach (TouchPoint touch in touches)
        {
            sb.Append(CultureInfo.InvariantCulture, $" {touch.Id}:{touch.X:F4}:{touch.Y:F4}");
        }

        return sb.ToString();
    }

    public static string FormatPad(int port, ControllerState controllerState) => $"{port} {(uint)controllerState.Buttons:X8} {controllerState.Lx} {controllerState.Ly} {controllerState.Rx} {controllerState.Ry}";

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
            if (_guestTouchSeen[side])
            {
                return n;
            }

            TouchPoint[] last = _lastTouch[side];
            bool same = n == _lastTouchCount[side];
            for (int i = 0; same && i < n && i < last.Length; i++)
            {
                same = last[i] == dest[i];
            }

            if (!same)
            {
                int m = Math.Min(n, last.Length);
                dest[..m].CopyTo(last);
                _lastTouchCount[side] = m;
                var sb = new StringBuilder(back ? "back" : "front");
                for (int i = 0; i < m; i++)
                {
                    sb.Append(CultureInfo.InvariantCulture, $" {dest[i].Id}:{dest[i].X:F4}:{dest[i].Y:F4}");
                }

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
        if (!RecordMotion)
        {
            return n;
        }

        for (int i = 0; i < n; i++)
        {
            MotionSample motionSample = dest[i];
            sink(InputEvent.Now("motion", string.Create(CultureInfo.InvariantCulture,
                $"{motionSample.TimestampUs} {motionSample.Acceleration.X:F5} {motionSample.Acceleration.Y:F5} {motionSample.Acceleration.Z:F5} {motionSample.AngularVelocity.X:F5} {motionSample.AngularVelocity.Y:F5} {motionSample.AngularVelocity.Z:F5}")));
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
public sealed class InputPlayback : IInputBackend, IMotionSource, IRandomHook, IGuestReadInput
{
    private readonly List<(long Us, ControllerState S)>[] _pad = [new(), new(), new(), new(), new()];
    private readonly List<(long Us, TouchPoint[] T)>[] _touch = [new(), new()];
    // the changes as the guest's reads saw them (captures that number the reads): by read, not by guest clock
    private readonly List<(long Read, ControllerState S)>[] _padByRead = [new(), new(), new(), new(), new()];
    private readonly List<(long Read, TouchPoint[] T)>[] _touchByRead = [new(), new()];
    private readonly List<(long Us, MotionSample M)> _motion = new();
    private readonly Queue<byte[]> _random = new();
    private readonly List<(long Us, byte[] B)> _randomAll = new();
    private long _motionReadUs = long.MinValue;
    private int _motionIndex;

    public bool Active { get; private set; }
    /// <summary>Guest time of the last recorded event (the re-run's natural end).</summary>
    public long LastUs { get; private set; }
    public int Events { get; private set; }

    public InputPlayback(IEnumerable<InputEvent> events)
    {
        foreach (InputEvent inputEvent in events.OrderBy(e => e.GuestUs))
        {
            string[] p = inputEvent.Data.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            try
            {
                switch (inputEvent.Kind)
                {
                    case "pad" when p.Length >= 6 && int.TryParse(p[0], out int port) && (uint)port < _pad.Length:
                    {
                        var state = new ControllerState
                        {
                            Buttons = (VitaButtons)uint.Parse(p[1], NumberStyles.HexNumber),
                            Lx = byte.Parse(p[2]),
                            Ly = byte.Parse(p[3]),
                            Rx = byte.Parse(p[4]),
                            Ry = byte.Parse(p[5]),
                        };
                        if (GuestRead(p) is { } read)
                        {
                            _padByRead[port].Add((read, state));
                        }
                        else
                        {
                            _pad[port].Add((inputEvent.GuestUs, state));
                        }

                        break;
                    }
                    case "touch" when p.Length >= 1:
                    {
                        TouchPoint[] touches = p.Skip(1).Where(t => !t.StartsWith(InputRecorder.ReadToken, StringComparison.Ordinal)).Select(t =>
                        {
                            string[] q = t.Split(':');
                            return new TouchPoint(int.Parse(q[0]), float.Parse(q[1], CultureInfo.InvariantCulture), float.Parse(q[2], CultureInfo.InvariantCulture));
                        }).ToArray();
                        int side = p[0] == "back" ? 1 : 0;
                        if (GuestRead(p) is { } read)
                        {
                            _touchByRead[side].Add((read, touches));
                        }
                        else
                        {
                            _touch[side].Add((inputEvent.GuestUs, touches));
                        }

                        break;
                    }
                    case "motion" when p.Length >= 7:
                        float F(int i) => float.Parse(p[i], CultureInfo.InvariantCulture);
                        _motion.Add((inputEvent.GuestUs, new MotionSample(ulong.Parse(p[0]), new Vector3(F(1), F(2), F(3)), new Vector3(F(4), F(5), F(6)))));
                        break;
                    case "rng" when p.Length >= 1:
                        _randomAll.Add((inputEvent.GuestUs, Convert.FromHexString(p[0])));
                        break;
                    default:
                        continue;
                }
                Events++;
                LastUs = Math.Max(LastUs, inputEvent.GuestUs);
            }
            catch (FormatException) { }
        }

        // several guest threads may read one port: the read numbers are what orders the changes
        foreach (List<(long Read, ControllerState S)> list in _padByRead)
        {
            list.Sort((x, y) => x.Read.CompareTo(y.Read));
        }

        foreach (List<(long Read, TouchPoint[] T)> list in _touchByRead)
        {
            list.Sort((x, y) => x.Read.CompareTo(y.Read));
        }
    }

    /// <summary>The read number of an event the guest's read saw ("read=N"), or null.</summary>
    private static long? GuestRead(string[] fields)
    {
        foreach (string field in fields)
        {
            if (field.StartsWith(InputRecorder.ReadToken, StringComparison.Ordinal)
                && long.TryParse(field.AsSpan(InputRecorder.ReadToken.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out long read))
            {
                return read;
            }
        }

        return null;
    }

    public static InputPlayback Load(string inputTsv) =>
        new(File.ReadLines(inputTsv).Select(InputEvent.Parse).Where(e => e != null).Select(e => e!.Value));

    /// <summary>The capture's state was loaded: play the events from guest time <paramref name="fromUs"/> on.</summary>
    public void Activate(long fromUs)
    {
        lock (_random)
        {
            _random.Clear();
            foreach ((long us, byte[]? b) in _randomAll)
            {
                if (us >= fromUs)
                {
                    _random.Enqueue(b);
                }
            }
        }
        _motionReadUs = fromUs;
        _motionIndex = 0;
        Active = true;
        Log.Info($"[capture] re-run: input playback from guest time {fromUs / 1e6:F3} s ({Events} events, last at {LastUs / 1e6:F3} s, {_random.Count} random results)");
    }

    private static int Find<T>(List<(long Us, T V)> list, long us)
    {
        int lo = 0, hi = list.Count - 1, r = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (list[mid].Us <= us)
            {
                r = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }
        return r;
    }

    public ControllerState GetController(int port)
    {
        if (!Active || (uint)port >= _pad.Length)
        {
            return ControllerState.Neutral;
        }

        int i = Find(_pad[port], GuestClock.Microseconds);
        return i >= 0 ? _pad[port][i].S : ControllerState.Neutral;
    }

    /// <summary>The <paramref name="guestRead"/>-th read of the port: the change the recording saw at that read or the
    /// last before it (the read count is in the save state, so the reads from the state on are the recording's).
    /// A capture without read numbers is played by guest clock.</summary>
    public ControllerState GetController(int port, long guestRead)
    {
        if (!Active || (uint)port >= _pad.Length)
        {
            return ControllerState.Neutral;
        }

        List<(long Read, ControllerState S)> list = _padByRead[port];
        if (list.Count == 0)
        {
            return GetController(port);
        }

        int i = Find(list, guestRead);
        return i >= 0 ? list[i].S : ControllerState.Neutral;
    }

    public int GetTouches(bool back, Span<TouchPoint> dest, long guestRead)
    {
        if (!Active)
        {
            return 0;
        }

        List<(long Read, TouchPoint[] T)> list = _touchByRead[back ? 1 : 0];
        IReadOnlyList<TouchPoint> touches;
        if (list.Count == 0)
        {
            touches = GetTouches(back);
        }
        else
        {
            int i = Find(list, guestRead);
            touches = i >= 0 ? list[i].T : [];
        }

        int n = Math.Min(touches.Count, dest.Length);
        for (int k = 0; k < n; k++)
        {
            dest[k] = touches[k];
        }

        return n;
    }

    public IReadOnlyList<TouchPoint> GetTouches(bool back)
    {
        if (!Active)
        {
            return [];
        }

        List<(long Us, TouchPoint[] T)> list = _touch[back ? 1 : 0];
        int i = Find(list, GuestClock.Microseconds);
        return i >= 0 ? list[i].T : [];
    }

    public bool HasSensors => _motion.Count > 0;

    public int ReadSamples(Span<MotionSample> dest)
    {
        if (!Active)
        {
            return 0;
        }

        long now = GuestClock.Microseconds;
        int n = 0;
        lock (_motion)
        {
            while (_motionIndex < _motion.Count && _motion[_motionIndex].Us <= _motionReadUs)
            {
                _motionIndex++;
            }

            while (_motionIndex < _motion.Count && _motion[_motionIndex].Us <= now && n < dest.Length)
            {
                dest[n++] = _motion[_motionIndex++].M;
            }

            _motionReadUs = now;
        }
        return n;
    }

    public bool Replay(Span<byte> dst)
    {
        if (!Active)
        {
            return false;
        }

        lock (_random)
        {
            if (!_random.TryDequeue(out byte[]? b) || b.Length != dst.Length)
            {
                return false;
            }

            b.CopyTo(dst);
            return true;
        }
    }

    public void Recorded(ReadOnlySpan<byte> bytes)
    {
    }
}
