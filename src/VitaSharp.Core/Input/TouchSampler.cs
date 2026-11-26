using System.Diagnostics;
using VitaSharp.Core.Host;

namespace VitaSharp.Core.Input;

/// <summary>
/// The recent history of one touch panel's contacts on the host, so the touch driver can sample it at the panel's
/// rate as the Vita does.
/// <para>
/// The Vita's touch driver samples each panel at every vblank into a ring of reports with timestamps; sceTouchPeek and
/// sceTouchRead copy the newest buffers of it, and libpaf (FW 3.74) peeks 16 of them per frame and processes every
/// sample newer than the last one it saw (press, move, release by report id). The front-end records here every
/// change of its fingers with the host time it happened at (Android: each MotionEvent and its historical points), and
/// the touch HLE asks for the contacts at the time of each vblank since its previous read: a tap shorter than one of
/// the guest's (slow) frames still reaches it as a press and a release, and a drag keeps the points between frames.
/// </para>
/// <para>
/// Each new contact gets an id of its own, kept while the finger stays down (the host's pointer ids are reused:
/// Android gives every first finger id 0), so two quick taps stay two contacts instead of one finger jumping from the
/// first place to the second. The ids are the ones <see cref="Current"/> and <see cref="At"/> report.
/// </para>
/// </summary>
public sealed class TouchSampler
{
    /// <summary>Contacts kept per change (SCE_TOUCH_MAX_REPORT).</summary>
    public const int MaxContacts = 8;

    /// <summary>Changes kept: about two seconds of a finger moving at the 240 Hz touch rate of a phone.</summary>
    public const int Capacity = 512;

    private readonly object _lock = new();
    private readonly Func<long> _clockUs;
    private readonly long[] _timeUs = new long[Capacity];
    private readonly int[] _count = new int[Capacity];
    private readonly TouchPoint[] _contacts = new TouchPoint[Capacity * MaxContacts];
    // the change most recently recorded (its slot) and how many slots hold one
    private int _newest = -1;
    private int _filled;
    // host pointer id -> contact id, for the fingers down now
    private readonly int[] _hostIds = new int[MaxContacts];
    private readonly int[] _ids = new int[MaxContacts];
    private int _liveCount;
    private int _nextId = 1;

    /// <summary>A sampler on the host's monotonic clock (<see cref="StopwatchMicroseconds"/>), or on
    /// <paramref name="clockUs"/> (tests).</summary>
    public TouchSampler(Func<long>? clockUs = null)
    {
        _clockUs = clockUs ?? StopwatchMicroseconds;
    }

    /// <summary>The host's monotonic clock in microseconds (Stopwatch).</summary>
    public static long StopwatchMicroseconds() => (long)(Stopwatch.GetTimestamp() * (1_000_000.0 / Stopwatch.Frequency));

    /// <summary>Now on this sampler's clock (µs): the time base of <see cref="Record"/>.</summary>
    public long NowUs => _clockUs();

    /// <summary>Changes recorded so far (the touch diagnostics).</summary>
    public long Changes { get; private set; }

    /// <summary>The host's fingers (its pointer ids, positions 0..1) as of <paramref name="timeUs"/> on this sampler's
    /// clock (null: now). A time before the previous change is taken as that change's time (the history stays in
    /// order).</summary>
    public void Record(ReadOnlySpan<TouchPoint> fingers, long? timeUs = null)
    {
        lock (_lock)
        {
            long time = timeUs ?? _clockUs();
            if (_newest >= 0 && time < _timeUs[_newest])
            {
                time = _timeUs[_newest];
            }

            Span<int> hostIds = stackalloc int[MaxContacts];
            Span<int> ids = stackalloc int[MaxContacts];
            int n = Math.Min(fingers.Length, MaxContacts);
            int slot = (_newest + 1) % Capacity;
            for (int i = 0; i < n; i++)
            {
                int hostId = fingers[i].Id;
                int id = 0;
                for (int k = 0; k < _liveCount; k++)
                {
                    if (_hostIds[k] == hostId)
                    {
                        id = _ids[k];
                        break;
                    }
                }

                if (id == 0)
                {
                    id = _nextId;
                    _nextId = _nextId == int.MaxValue ? 1 : _nextId + 1;
                }

                hostIds[i] = hostId;
                ids[i] = id;
                _contacts[slot * MaxContacts + i] = new TouchPoint(id, fingers[i].X, fingers[i].Y);
            }

            hostIds[..n].CopyTo(_hostIds);
            ids[..n].CopyTo(_ids);
            _liveCount = n;
            _timeUs[slot] = time;
            _count[slot] = n;
            _newest = slot;
            _filled = Math.Min(_filled + 1, Capacity);
            Changes++;
        }
    }

    /// <summary>Releases every finger now (the screen lost focus).</summary>
    public void Clear() => Record([]);

    /// <summary>The contacts down now.</summary>
    public int Current(Span<TouchPoint> dest)
    {
        lock (_lock)
        {
            return _newest < 0 ? 0 : Copy(_newest, dest);
        }
    }

    /// <summary>The contacts down <paramref name="ageUs"/> microseconds ago; -1 when that is older than the history
    /// kept (the caller then has no sample for that time).</summary>
    public int At(long ageUs, Span<TouchPoint> dest)
    {
        lock (_lock)
        {
            if (_newest < 0)
            {
                return 0; // never touched: nothing was down
            }

            long time = _clockUs() - ageUs;
            for (int back = 0; back < _filled; back++)
            {
                int slot = (_newest - back + Capacity) % Capacity;
                if (_timeUs[slot] <= time)
                {
                    return Copy(slot, dest);
                }
            }

            // before every change kept: nothing was down if the history still holds its first change
            return _filled < Capacity ? 0 : -1;
        }
    }

    private int Copy(int slot, Span<TouchPoint> dest)
    {
        int n = Math.Min(_count[slot], dest.Length);
        _contacts.AsSpan(slot * MaxContacts, n).CopyTo(dest);
        return n;
    }
}
