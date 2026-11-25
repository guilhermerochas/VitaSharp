using VitaSharp.Core.Util;

namespace VitaSharp.Tests;

/// <summary>
/// The start-up line every session logs (<see cref="StartupMeter"/>): the time to the first frame and the slow frames
/// of the first minute, with the JIT compilation around them, the numbers the ARM code precompilation is measured by
/// on the desktop and on Android. Timestamps and the JIT total are microsecond ticks (frequency 1,000,000).
/// </summary>
public sealed class StartupMeterTests
{
    private const double Frame = 1000.0 / 30;

    /// <summary>A meter whose session started at 0 and showed its first frame at <paramref name="firstFrameMs"/>.</summary>
    private sealed class Session
    {
        public readonly StartupMeter Meter = new(1_000_000);
        public readonly List<string> Lines = [];
        private double _nowMs;
        private double _jitMs;

        public Session(double firstFrameMs, double beginMs = 0)
        {
            Meter.Begin(Ticks(beginMs));
            _nowMs = firstFrameMs;
            Assert.Null(Meter.Present(Ticks(_nowMs), 0));
        }

        public static long Ticks(double ms) => (long)Math.Round(ms * 1000);

        /// <summary>The next frame, <paramref name="intervalMs"/> after the last, with <paramref name="jitMs"/> of JIT
        /// compilation in between.</summary>
        public void Present(double intervalMs, double jitMs = 0)
        {
            _nowMs += intervalMs;
            _jitMs += jitMs;
            if (Meter.Present(Ticks(_nowMs), Ticks(_jitMs)) is { } line)
            {
                Lines.Add(line);
            }
        }
    }

    [Fact]
    public void ReportsTheFirstMinuteOnceWithTheSlowFramesAndTheirJit()
    {
        var session = new Session(firstFrameMs: 13_340, beginMs: 1_000); // first frame 12.34 s after the start
        for (int i = 0; i < 1500; i++)
        {
            session.Present(Frame);
        }

        session.Present(120, jitMs: 8); // slow, with 8 ms of JIT
        session.Present(60); // slow: the JIT of the interval before counts
        session.Present(Frame);
        session.Present(200, jitMs: 3); // slow, 3 ms of JIT around it: not a JIT frame
        while (session.Lines.Count == 0)
        {
            session.Present(Frame);
        }

        session.Present(500, jitMs: 50); // after the minute: not counted, nothing more reported

        string line = Assert.Single(session.Lines);
        Assert.StartsWith($"Startup: first frame {12.3:F1} s after the emulator started; first 60 s: ", line, StringComparison.Ordinal);
        Assert.EndsWith($"3 over 50 ms (2 over 100 ms, {120 + 60 + 200 - 3 * Frame:F0} ms above 33.3 ms), 2 of them with JIT compilation "
            + "over 5 ms, JIT compiling 11 ms", line, StringComparison.Ordinal);
        Assert.Null(session.Meter.Finish());
    }

    [Fact]
    public void TheMinuteEndsWithTheFrameThatReachesIt()
    {
        var session = new Session(firstFrameMs: 500);
        for (int i = 0; i < 1799; i++)
        {
            session.Present(Frame);
        }

        Assert.Empty(session.Lines); // 59.97 s
        session.Present(40);

        Assert.Contains(": 1800 frames, 0 over 50 ms", Assert.Single(session.Lines), StringComparison.Ordinal);
    }

    [Fact]
    public void ASessionShorterThanAMinuteIsReportedWhenItEnds()
    {
        var session = new Session(firstFrameMs: 4_000);
        session.Present(300);
        session.Present(20);

        string? line = session.Meter.Finish();

        Assert.Equal($"Startup: first frame {4.0:F1} s after the emulator started; first {0.3:F1} s (the session ended): 2 frames, "
            + $"1 over 50 ms (1 over 100 ms, {300 - Frame:F0} ms above 33.3 ms), 0 of them with JIT compilation over 5 ms, JIT compiling 0 ms", line);
        Assert.Null(session.Meter.Finish());
    }

    [Fact]
    public void ASessionWithoutFramesSaysSo()
    {
        var meter = new StartupMeter(1000);
        meter.Begin(0);

        Assert.Equal("Startup: no frame presented", meter.Finish());
    }

    [Fact]
    public void NothingIsMeasuredBeforeASessionStarts()
    {
        var meter = new StartupMeter(1000);

        Assert.Null(meter.Present(100, 0));
        Assert.Null(meter.Finish());
    }

    [Fact]
    public void ANewSessionStartsOver()
    {
        var session = new Session(firstFrameMs: 1_000);
        session.Present(500, jitMs: 400);
        session.Meter.Begin(Session.Ticks(10_000));
        Assert.Null(session.Meter.Present(Session.Ticks(12_000), Session.Ticks(400))); // its first frame, 2 s after it started
        Assert.Null(session.Meter.Present(Session.Ticks(12_033), Session.Ticks(400)));

        string? line = session.Meter.Finish();

        Assert.NotNull(line);
        Assert.StartsWith($"Startup: first frame {2.0:F1} s after the emulator started", line, StringComparison.Ordinal);
        Assert.Contains(": 1 frames, 0 over 50 ms", line, StringComparison.Ordinal);
        Assert.EndsWith("JIT compiling 0 ms", line, StringComparison.Ordinal);
    }
}
