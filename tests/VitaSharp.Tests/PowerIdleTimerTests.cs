using VitaSharp.Core.Cpu;
using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// ScePower's idle timer callbacks (power.skprx 3.74, decompiled, study only: the vblank check 0x81005b70): a slot
/// whose time has passed since its start calls its callback once (slot, elapsed low, argument, elapsed high) and stays
/// fired; a tick then tells it the user is back (slot, 0, argument, the old state 1) and the slot counts again from the
/// tick; NID_660D5AB4 makes the next check fire it whatever its time; nothing is armed without a callback. The check
/// is an HLE source of the interrupt scheduler, fired here by hand at the guest time the test names.
/// </summary>
[Collection("guest")]
public sealed unsafe class PowerIdleTimerTests(GuestMemoryFixture fixture) : IDisposable
{
    private const uint Argument = 0x1234;
    private const long TimeoutUs = 5_000_000;

    private readonly HleHarness _harness = new(fixture, "powidle");

    public void Dispose() => _harness.Dispose();

    private SystemServicesVsh Services => _harness.Module<SystemServicesVsh>();

    private PowerKernel Power => _harness.Module<PowerKernel>();

    private ulong? Armed => Services.IntrSchedule(SystemServicesVsh.HleInterruptPowerVblank).Deadline;

    /// <summary>ARM code that stores r0..r3 after the word at <paramref name="target"/>, then adds 1 to it; returns 0.</summary>
    private uint Recorder(uint target)
    {
        uint code = _harness.Alloc(48);
        uint[] words =
        [
            0xE59FC020, // ldr r12, [pc, #32]  (the word at +40)
            0xE58C0004, // str r0, [r12, #4]
            0xE58C1008, // str r1, [r12, #8]
            0xE58C200C, // str r2, [r12, #12]
            0xE58C3010, // str r3, [r12, #16]
            0xE59C1000, // ldr r1, [r12]
            0xE2811001, // add r1, r1, #1
            0xE58C1000, // str r1, [r12]
            0xE3A00000, // mov r0, #0
            0xE12FFF1E, // bx lr
            target,
        ];
        for (int i = 0; i < words.Length; i++)
        {
            GuestMemory.W32(code + (uint)i * 4, words[i]);
        }

        return code;
    }

    private void SetIdleCallback(uint slot, long timeUs, uint callback)
    {
        CpuState* cpu = CpuState.Allocate();
        try
        {
            var context = new HleContext { Cpu = cpu, Emu = _harness.Emu };
            uint stack = _harness.Alloc(0x10);
            cpu->R[0] = slot;
            cpu->R[1] = 0;
            cpu->R[2] = (uint)timeUs;
            cpu->R[3] = (uint)(timeUs >> 32);
            cpu->R[13] = stack;
            GuestMemory.W32(stack, callback);
            GuestMemory.W32(stack + 4, Argument);
            Invoke(Power, "kscePowerSetIdleCallback", context);
            Assert.Equal(0u, cpu->R[0]);
        }
        finally
        {
            CpuState.Free(cpu);
        }
    }

    private void CheckAt(ulong nowUs) => _harness.OnGuest(_ => Services.IntrDispatchDue(nowUs));

    private (uint Count, uint Slot, uint Second, uint Argument, uint Fourth) Seen(uint seen)
        => (GuestMemory.R32(seen), GuestMemory.R32(seen + 4), GuestMemory.R32(seen + 8), GuestMemory.R32(seen + 12), GuestMemory.R32(seen + 16));

    [Fact]
    public void AnIdleTimerCallsItsCallbackAtItsTimeAndAgainOnTheNextTick()
    {
        using var clock = ControlledGuestClock.Stop();
        Services.IntrDispatchByHand();
        Assert.Null(Armed);
        uint seen = _harness.Alloc(24);
        ulong start = Services.IntrNowUs;
        SetIdleCallback(1, TimeoutUs, Recorder(seen));
        Assert.Equal(start + (ulong)TimeoutUs, Armed);

        CheckAt(start + (ulong)TimeoutUs - 1);
        Assert.Equal(0u, Seen(seen).Count);
        clock.Advance(TimeoutUs);
        CheckAt(Services.IntrNowUs);
        Assert.Equal((1u, 1u, (uint)TimeoutUs, Argument, 0u), Seen(seen));
        Assert.Null(Armed);

        // fired: nothing more until a tick, which the next check tells the callback (elapsed 0, old state 1)
        clock.Advance(TimeoutUs);
        Assert.Equal(0, Call(Power, "kscePowerTick", 2u));
        CheckAt(Services.IntrNowUs);
        Assert.Equal((2u, 1u, 0u, Argument, 1u), Seen(seen));
        ulong counting = Services.IntrNowUs + (ulong)TimeoutUs;
        Assert.Equal(counting, Armed);

        // a second tick before the time keeps the slot counting from its new start, quietly
        clock.Advance(1_000_000);
        Assert.Equal(0, Call(Power, "kscePowerTick", 0u));
        CheckAt(Services.IntrNowUs);
        Assert.Equal(2u, Seen(seen).Count);
        Assert.Equal(Services.IntrNowUs + (ulong)TimeoutUs, Armed);

        // told to fire: the next check calls it whatever its time
        Assert.Equal(0, Call(Power, "NID_660D5AB4", 1u));
        CheckAt(Services.IntrNowUs);
        Assert.Equal((3u, 1u, 0u, Argument, 0u), Seen(seen));
    }
}
