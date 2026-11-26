using VitaSharp.Core;
using VitaSharp.Core.Gpu;
using VitaSharp.Core.Util;

namespace VitaSharp.Tests;

/// <summary>
/// The end of a session (<see cref="Emulator.Dispose"/>) never disposes the GPU backend while the GPU worker is still
/// inside it. The worker can still be running a command well after the guest stopped: a present waits for the previous
/// frame's fence, which on a loaded machine with a software GPU took longer than the 2 s the end waited for the worker,
/// and the renderer's Dispose then destroyed the command pools under that present (0xC0000005 in vkResetCommandPool on
/// the GPU worker; the re-run of a capture ended with a non-zero code after "Run finished", 25/11/2026). The worker is
/// held inside the backend (a command's Execute, or Idle on an empty queue) until the end has either disposed the
/// backend (the old end) or logged that it waits for the worker (the end that waits).
/// </summary>
[Collection("guest")]
public sealed class GpuSessionEndTests(GuestMemoryFixture fixture)
{
    private const string WaitsForTheWorker = "GPU worker still running a command";

    /// <summary>The first Execute or Idle (as chosen) blocks until released; Dispose records whether it ran while the
    /// worker was inside the backend.</summary>
    private sealed class HoldingBackend(bool holdInExecute) : IGpuBackend
    {
        public readonly ManualResetEventSlim Entered = new();
        public readonly ManualResetEventSlim Release = new();
        private int _holds;
        private volatile bool _inside;
        public volatile bool DisposedWhileInside;
        public int DisposeCount;

        public long PresentCount => 0;

        public byte[]? ExportDumpState() => null;

        public void Execute(GpuCommand command)
        {
            if (holdInExecute)
            {
                Hold();
            }
        }

        public void Idle()
        {
            if (!holdInExecute)
            {
                Hold();
            }
        }

        private void Hold()
        {
            if (Interlocked.Increment(ref _holds) != 1)
            {
                return;
            }

            _inside = true;
            Entered.Set();
            Release.Wait();
            _inside = false;
        }

        public void Dispose()
        {
            if (_inside)
            {
                DisposedWhileInside = true;
            }

            Interlocked.Increment(ref DisposeCount);
        }
    }

    private sealed class CollectingSink : ILogSink
    {
        private readonly List<string> _messages = [];

        public void Write(LogLevel level, string thread, string message)
        {
            lock (_messages)
            {
                _messages.Add(message);
            }
        }

        public bool Contains(string text)
        {
            lock (_messages)
            {
                return _messages.Exists(m => m.Contains(text, StringComparison.Ordinal));
            }
        }
    }

    /// <summary>A present still running (Execute) or the worker's Idle on an empty queue when the session ends.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheSessionEndDisposesTheBackendOnlyAfterTheWorkerLeftIt(bool holdInExecute)
    {
        string root = Path.Combine(Path.GetTempPath(), "vitasharp-gpuend-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        bool jitEnabled = VitaSharp.Core.Cpu.Jit.Enabled;
        string jitMode = VitaSharp.Core.Cpu.Jit.Mode;
        var sink = new CollectingSink();
        ILogSink? savedSink = Log.Sink;
        var backend = new HoldingBackend(holdInExecute);
        Thread? end = null;
        try
        {
            var emu = new Emulator(new EmulatorOptions { FsRoot = root, Memory = fixture.Memory, EnableJit = false, CpuBackend = "interp" });
            emu.Gpu.Backend = backend;
            if (holdInExecute)
            {
                emu.Gpu.Submit(new CallbackCommand { Action = static () => { } });
            }

            Assert.True(backend.Entered.Wait(120_000), "the GPU worker never entered the backend");
            Log.Sink = sink;
            end = new Thread(emu.Dispose)
            {
                IsBackground = true,
                Name = "session end",
            };
            end.Start();
            // the worker stays inside the backend until the end disposed it (the old end) or says it waits for it
            Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref backend.DisposeCount) > 0 || sink.Contains(WaitsForTheWorker), 120_000),
                "the session end neither disposed the backend nor waited for the worker");
            Assert.False(backend.DisposedWhileInside, "the GPU backend was disposed while the GPU worker was still inside it");
            backend.Release.Set();
            Assert.True(end.Join(120_000), "the session end never returned after the worker left the backend");
            Assert.False(backend.DisposedWhileInside, "the GPU backend was disposed while the GPU worker was still inside it");
            Assert.Equal(1, backend.DisposeCount);
            Assert.Null(emu.Gpu.Backend);
        }
        finally
        {
            backend.Release.Set();
            end?.Join(120_000);
            Log.Sink = savedSink;
            VitaSharp.Core.Cpu.Jit.Enabled = jitEnabled;
            VitaSharp.Core.Cpu.Jit.Select(jitMode);
            try
            {
                Directory.Delete(root, true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>A worker that never leaves the backend (stuck in the driver): the end gives up disposing it and says so
    /// in the log, instead of destroying it under the worker or hanging.</summary>
    [Fact]
    public void AWorkerThatNeverLeavesTheBackendLeavesItUndisposedAndALogLine()
    {
        int stopWait = GpuQueue.WorkerStopWaitMs;
        int leaveWait = GpuQueue.BackendLeaveWaitMs;
        var sink = new CollectingSink();
        ILogSink? savedSink = Log.Sink;
        var backend = new HoldingBackend(holdInExecute: false);
        var queue = new GpuQueue();
        try
        {
            queue.Backend = backend;
            Assert.True(backend.Entered.Wait(120_000), "the GPU worker never entered the backend");
            GpuQueue.WorkerStopWaitMs = 0;
            GpuQueue.BackendLeaveWaitMs = 0;
            Log.Sink = sink;
            queue.Shutdown();
            Assert.Equal(0, backend.DisposeCount);
            Assert.True(sink.Contains(WaitsForTheWorker));
            Assert.True(sink.Contains("did not leave the GPU backend"));
        }
        finally
        {
            Log.Sink = savedSink;
            GpuQueue.WorkerStopWaitMs = stopWait;
            GpuQueue.BackendLeaveWaitMs = leaveWait;
            backend.Release.Set();
            queue.Dispose();
        }
    }
}
