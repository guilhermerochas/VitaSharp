using VitaSharp.Core.Util;

namespace VitaSharp.Tests;

/// <summary>
/// <see cref="GpuSubmitterThread"/>: the host thread recording GXM scenes, which the Android app moves to the fast cores
/// (VITASHARP_ANDROID_GXM_THREAD=big). The contract: one notification per change of recorder (never per scene), the
/// previous recorder named, nothing without a listener. In the "guest" collection: the hook is process-wide and the
/// tests that run guest threads may begin scenes.
/// </summary>
[Collection("guest")]
public sealed class GpuSubmitterThreadTests : IDisposable
{
    private readonly List<(int Tid, int Previous)> _changes = [];

    public GpuSubmitterThreadTests()
    {
        GpuSubmitterThread.Reset();
    }

    public void Dispose()
    {
        GpuSubmitterThread.Changed = null;
        GpuSubmitterThread.Reset();
    }

    private void Listen()
    {
        GpuSubmitterThread.Changed = (tid, previous) =>
        {
            lock (_changes)
            {
                _changes.Add((tid, previous));
            }
        };
    }

    [Fact]
    public void OneNotificationPerChangeOfRecorder()
    {
        Listen();
        // a job system: frame 1 on job thread 101 (three scenes), frame 2 on 102, frame 3 on 102 again, frame 4 on 101
        int[] scenes = [101, 101, 101, 102, 102, 102, 102, 101];
        foreach (int tid in scenes)
        {
            GpuSubmitterThread.SceneBegan(tid);
        }

        Assert.Equal([(101, 0), (102, 101), (101, 102)], _changes);
        Assert.Equal(101, GpuSubmitterThread.Current);
    }

    [Fact]
    public void NothingWithoutAListenerOrAHostThread()
    {
        // no listener: not even the recorder is tracked (one field read per scene)
        GpuSubmitterThread.SceneBegan(101);
        Assert.Equal(0, GpuSubmitterThread.Current);

        // a scene begun outside a guest thread (host id 0: HLE tests, replays) is not a recorder
        Listen();
        GpuSubmitterThread.SceneBegan(0);
        Assert.Empty(_changes);
        Assert.Equal(0, GpuSubmitterThread.Current);
    }

    [Fact]
    public void ScenesFromTwoThreadsAtOnceReportEveryChange()
    {
        // two threads alternating scenes: every hand-over is reported with the right previous recorder
        Listen();
        using var go = new Barrier(2);
        int rounds = 200;
        void Record(int tid)
        {
            for (int i = 0; i < rounds; i++)
            {
                go.SignalAndWait();
                if ((i & 1) == (tid & 1))
                {
                    GpuSubmitterThread.SceneBegan(tid);
                }
                go.SignalAndWait();
            }
        }
        var a = new Thread(() => Record(200));
        var b = new Thread(() => Record(201));
        a.Start();
        b.Start();
        a.Join();
        b.Join();

        Assert.Equal(rounds, _changes.Count);
        for (int i = 1; i < _changes.Count; i++)
        {
            Assert.Equal(_changes[i - 1].Tid, _changes[i].Previous);
            Assert.NotEqual(_changes[i].Tid, _changes[i].Previous);
        }
    }
}
