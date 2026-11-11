using System.Diagnostics;

namespace VitaSharp.Core.Package;

/// <summary>Progress of a .pkg or VPK install: bytes written so far, the total, and the time since it started.</summary>
public readonly record struct InstallProgress(long BytesDone, long BytesTotal, string? CurrentEntry = null, TimeSpan Elapsed = default)
{
    public double Fraction => BytesTotal <= 0 ? 0 : Math.Clamp((double)BytesDone / BytesTotal, 0, 1);

    /// <summary>Average speed since the start (0 for the first half second, while it is still meaningless).</summary>
    public double BytesPerSecond => Elapsed.TotalSeconds >= 0.5 ? BytesDone / Elapsed.TotalSeconds : 0;

    public TimeSpan? Remaining => BytesPerSecond > 0 && BytesTotal >= BytesDone
        ? TimeSpan.FromSeconds((BytesTotal - BytesDone) / BytesPerSecond) : null;

    /// <summary>"42% · 380 MB/s · 0:12 left" (just the percentage until the speed is known).</summary>
    public string Describe(string left = "left")
    {
        string text = $"{Fraction:P0}";
        if (BytesPerSecond <= 0)
        {
            return text;
        }

        text += $" · {BytesPerSecond / (1024 * 1024):N0} MB/s";
        if (Remaining is { } r)
        {
            text += r.TotalHours >= 1 ? $" · {(int)r.TotalHours}:{r.Minutes:00}:{r.Seconds:00} {left}" : $" · {r.Minutes}:{r.Seconds:00} {left}";
        }

        return text;
    }
}

/// <summary>Reports <see cref="InstallProgress"/> at most every <see cref="s_interval"/> (the UIs redraw on each report).</summary>
internal sealed class InstallProgressReporter(Action<InstallProgress>? sink, long total)
{
    private static readonly TimeSpan s_interval = TimeSpan.FromMilliseconds(100);
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private TimeSpan? _last;
    private long _done;

    public long Done => Interlocked.Read(ref _done);
    public TimeSpan Elapsed => _clock.Elapsed;

    public void Add(long bytes, string? entry = null)
    {
        long done = Interlocked.Add(ref _done, bytes);
        if (sink == null)
        {
            return;
        }

        TimeSpan now = _clock.Elapsed;
        if (_last is { } last && now - last < s_interval)
        {
            return;
        }

        _last = now;
        sink(new InstallProgress(done, total, entry, now));
    }

    /// <summary>Reports now, whatever the interval (start and end of the install).</summary>
    public void Flush(string? entry = null) => sink?.Invoke(new InstallProgress(Done, total, entry, _clock.Elapsed));

    /// <summary>Reports the install as complete (the bytes written can be fewer than the total: trimmed padding).</summary>
    public void Finish()
    {
        Interlocked.Exchange(ref _done, total);
        Flush();
    }
}
