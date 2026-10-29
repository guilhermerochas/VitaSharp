using VitaSharp.Core.Util;

namespace VitaSharp.Core.Host;

/// <summary>One running host capture device, owned by a <see cref="SharedCapture"/>.</summary>
public interface IHostCapture
{
    /// <summary>Latest frame of the device (grey/empty until it delivers one).</summary>
    CameraFrameBuffer Frames { get; }

    /// <summary>A guest started a camera again: a capture that failed to start and waits to retry does so now.</summary>
    void RetryNow();

    /// <summary>Stops the device and releases everything it holds (graph, threads, windows). Never throws.</summary>
    Task StopAsync();
}

/// <summary>
/// Keeps one host capture for both Vita cameras. Games switch sensors by stopping one camera and starting the other
/// (Tearaway does it on every pause and menu); each switch used to close the webcam and reopen it, which takes over a
/// second on DirectShow and briefly needs two graphs. Here a started Vita camera leases the running capture; when the
/// last lease ends the capture keeps running for <see cref="Linger"/> so the next start reuses it, then stops. A capture
/// in another mode replaces the idle one only after the old one has fully stopped (devices cannot be opened twice).
/// </summary>
public sealed class SharedCapture : IDisposable
{
    readonly object _gate = new();
    readonly TimeSpan _linger;
    IHostCapture? _capture;
    object? _mode;
    int _leases;
    long _generation;
    Task _lastStop = Task.CompletedTask;
    bool _disposed;

    public SharedCapture(TimeSpan linger) => _linger = linger;

    /// <summary>How long an unused capture keeps running before it is stopped.</summary>
    public TimeSpan Linger => _linger;

    /// <summary>Number of captures created so far (tests, logs).</summary>
    public int Created { get; private set; }

    /// <summary>
    /// A stream on the capture for <paramref name="mode"/> (compared with Equals): the running one when it has the same
    /// mode or is still used by another stream, else a new one from <paramref name="create"/>, which receives the stop
    /// of the previous capture and must not open the device before that task completes. Null after Dispose.
    /// </summary>
    public ICameraStream? Acquire(object mode, Func<Task, IHostCapture> create, out bool reused)
    {
        reused = false;
        ICameraStream lease;
        lock (_gate)
        {
            if (_disposed) return null;
            if (_capture != null && _leases == 0 && !Equals(_mode, mode)) StopLocked();
            if (_capture != null)
            {
                reused = true;
                _capture.RetryNow();
            }
            else
            {
                _capture = create(_lastStop);
                _mode = mode;
                Created++;
            }
            _leases++;
            _generation++; // cancels a pending linger stop
            lease = new Lease(this, _capture);
        }
        return lease;
    }

    /// <summary>Starts stopping the current capture; the caller holds <see cref="_gate"/>.</summary>
    void StopLocked()
    {
        var c = _capture!;
        _capture = null;
        _mode = null;
        _leases = 0;
        _generation++;
        var prev = _lastStop;
        // stops are chained: a device is never opened while an earlier capture of it is still closing
        _lastStop = Task.Run(async () =>
        {
            try { await prev; } catch { }
            try { await c.StopAsync(); } catch (Exception e) { Log.Warn($"Camera: stopping the host capture failed: {e.Message}"); }
        });
    }

    void Release(IHostCapture c)
    {
        long gen;
        lock (_gate)
        {
            if (_capture != c || _leases == 0) return; // already replaced or stopped
            if (--_leases > 0) return;
            gen = ++_generation;
        }
        if (_linger <= TimeSpan.Zero) { StopIfIdle(c, gen); return; }
        _ = Task.Delay(_linger).ContinueWith(_ => StopIfIdle(c, gen), TaskScheduler.Default);
    }

    void StopIfIdle(IHostCapture c, long gen)
    {
        lock (_gate)
        {
            if (_capture == c && _leases == 0 && _generation == gen) StopLocked();
        }
    }

    /// <summary>Waits (bounded) until every capture stopped so far has released its device.</summary>
    public bool WaitStopped(TimeSpan timeout)
    {
        Task t;
        lock (_gate) t = _lastStop;
        try { return t.Wait(timeout); } catch { return true; }
    }

    /// <summary>Stops the capture now (even with live streams) and waits a bounded time for the device to close.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            if (_capture != null) StopLocked();
        }
        WaitStopped(TimeSpan.FromSeconds(3));
    }

    sealed class Lease(SharedCapture owner, IHostCapture capture) : ICameraStream
    {
        int _disposed;
        public CameraFrameBuffer Frames => capture.Frames;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) owner.Release(capture);
        }
    }
}
