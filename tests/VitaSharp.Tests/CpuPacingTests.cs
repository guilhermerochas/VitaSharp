using System.Diagnostics;
using VitaSharp.Core.Cpu;
using VitaSharp.Core.Cpu.Backends;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Tests;

/// <summary>
/// CPU pacing of host-side work charged to the emulated CPU (<see cref="CpuCore.ChargeInstructions"/>): a guest loop
/// that does little but call a costly HLE function is paced by the charge as it goes, at the pacing window's
/// granularity, not only once enough guest cycles of its own ran. Wipeout 2048's display callback spins on
/// sceDisplayGetVcount (12,000 cycles charged a call, ~25 cycles of its own code): the pacing check came ~100 ms of
/// emulated time apart and the thread then slept it all off at once, missing the vblanks it was polling for.
/// </summary>
[Collection("guest")]
public sealed unsafe class CpuPacingTests(GuestMemoryFixture fixture)
{
    private const uint PollSvc = 1;
    private const uint DoneSvc = 2;
    private const long ChargePerCall = 12_000;

    /// <summary>Answers SVC #1 like a cheap HLE getter with a large charge (r0 = 1 once enough calls were made) and
    /// leaves <see cref="CpuCore.Run"/> on SVC #2.</summary>
    private sealed class ChargingHandler(CpuCore core, int calls) : CpuCore.ISvcHandler
    {
        public int Count;

        public bool TryHandleSvc(CpuState* s)
        {
            if (s->SvcImm != PollSvc)
            {
                return false;
            }

            Count++;
            core.ChargeInstructions(ChargePerCall);
            s->R[0] = Count >= calls ? 1u : 0u;
            return true;
        }

        public bool TryHandleSvcNative(CpuState* s) => TryHandleSvc(s);
    }

    private static IEnumerable<string> Backends() => NativeX64Compiler.Instance.IsSupported ? ["interp", "native"] : ["interp"];

    [Fact]
    public void ChargedHleTimeIsPacedWhileTheLoopRuns()
    {
        const int Calls = 400;
        uint code = fixture.Memory.Alloc(0x20, "pacing loop");
        GuestMemory.W32(code + 0, 0xEF000000 | PollSvc); // loop: svc #1
        GuestMemory.W32(code + 4, 0xE3500000);           //       cmp r0, #0
        GuestMemory.W32(code + 8, 0x0AFFFFFC);           //       beq loop
        GuestMemory.W32(code + 12, 0xEF000000 | DoneSvc); //      svc #2
        long oldRate = CpuCore.InstructionsPerSecond, oldHot = NativeX64Compiler.Instance.HotInstructions;
        string oldMode = Jit.Mode;
        bool oldTurbo = GuestClock.Turbo;
        CpuCore.InstructionsPerSecond = CpuCore.DefaultClockHz;
        NativeX64Compiler.Instance.HotInstructions = 1;
        GuestClock.Turbo = false;
        using JitTestState.Scope jitOn = JitTestState.Enable();
        try
        {
            foreach (string backend in Backends())
            {
                Assert.True(Jit.Select(backend));
                // a first pass lets the native backend compile the loop; the second one is measured
                for (int pass = 0; pass < 2; pass++)
                {
                    using var core = new CpuCore(new BlockCache());
                    var handler = new ChargingHandler(core, Calls);
                    core.SvcHandler = handler;
                    CpuState* s = core.State;
                    s->R[0] = 0;
                    s->R[15] = code;
                    s->Thumb = 0;
                    long t0 = Stopwatch.GetTimestamp();
                    Assert.Equal(ExitReason.Svc, core.Run());
                    double elapsedMs = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                    Assert.Equal(DoneSvc, s->SvcImm);
                    Assert.Equal(Calls, handler.Count);
                    if (pass == 1)
                    {
                        // 400 calls x 12,000 cycles at 333 MHz = 14.4 ms of emulated time; the pacing lets a thread run
                        // up to 1 ms ahead and aims its sleeps early by the host's average wake-up lateness (at most
                        // 2 ms). Without the charge in the window the loop ran unpaced: well under 1 ms.
                        double emulatedMs = Calls * ChargePerCall * 1000.0 / CpuCore.DefaultClockHz;
                        Assert.True(elapsedMs >= emulatedMs - 4.0, $"{backend}: {Calls} charged calls took {elapsedMs:F2} ms, expected about {emulatedMs:F1} ms of pacing");
                    }

                    if (backend == "native")
                    {
                        Assert.True(Jit.WaitIdle(TimeSpan.FromSeconds(60)));
                    }
                }
            }
        }
        finally
        {
            CpuCore.InstructionsPerSecond = oldRate;
            NativeX64Compiler.Instance.HotInstructions = oldHot;
            GuestClock.Turbo = oldTurbo;
            Jit.Select(oldMode);
        }
    }
}
