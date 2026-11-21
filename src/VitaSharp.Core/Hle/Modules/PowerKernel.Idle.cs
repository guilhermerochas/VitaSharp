using VitaSharp.Core.Kernel;
using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// ScePower's idle timers as power.skprx 3.74 checks them (decompiled, study only: the vblank sub-interrupt handler
/// "ScePowerVblank", 0x81005b70). At each check, for every slot with a callback: a slot told to fire
/// (NID_660D5AB4) fires whatever its time; otherwise a tick since the last check (kscePowerTick) clears and, when the
/// slot had fired or was never checked, calls the callback (slot, 0, argument, the slot's old state) as the slot counts
/// again; without a tick a slot that fired waits for one, and one counting fires once its time has passed since its
/// start: the callback gets (slot, the elapsed time's low word, argument, its high word) and the slot is fired. The
/// firmware looks at every vblank; here the check is one of the interrupt scheduler's HLE sources, armed for the next
/// moment something can happen (a pending tick or forced slot: now; else the earliest counting slot's time), so an idle
/// system runs nothing per frame. Not modelled: the processes' own idle times (kscePowerSetProcessIdleCallback's list)
/// and the slots whose flag 0x400 makes a tick depend on the power module's other state.
/// </summary>
public sealed partial class PowerKernel : ISnapshotTimers
{
    /// <summary>A slot's check states (+0x1F): not checked yet, counting, fired, to fire at the next check.</summary>
    const byte IdleNotChecked = 0xFF, IdleCounting = 0, IdleFired = 1, IdleForced = 2;

    /// <summary>A slot kscePowerTick(0) leaves alone.</summary>
    const uint IdleFlagNotAll = 0x200;

    SystemServicesVsh Intrmgr => Emu.Hle.Modules.OfType<SystemServicesVsh>().First();

    /// <summary>Arms the check for the next moment a slot can change (or disarms it).</summary>
    void ArmIdleCheck()
    {
        long now = NowUs;
        long next = long.MaxValue;
        lock (State)
        {
            for (int slot = 0; slot < ScePowerState.IdleTimerCount; slot++)
            {
                if (State.IdleCallback[slot] == 0)
                {
                    continue;
                }

                byte state = State.IdleState[slot];
                if (state == IdleForced || State.IdleTickPending[slot])
                {
                    next = now;
                }
                else if (state != IdleFired && State.IdleTimeoutUs[slot] != 0)
                {
                    next = Math.Min(next, State.IdleStartUs[slot] + State.IdleTimeoutUs[slot]);
                }
            }
        }

        SystemServicesVsh intrmgr = Intrmgr;
        if (next == long.MaxValue)
        {
            intrmgr.DisarmHleInterrupt(SystemServicesVsh.HleInterruptPowerVblank);
            return;
        }

        ulong at = intrmgr.IntrNowUs + (ulong)Math.Max(0, next - now);
        intrmgr.ArmHleInterrupt(SystemServicesVsh.HleInterruptPowerVblank, at, IdleCheck);
    }

    /// <summary>The check (interrupt context, on <paramref name="thread"/>): the callbacks are called outside the
    /// state's lock, in slot order.</summary>
    void IdleCheck(GuestThread thread)
    {
        long now = NowUs;
        for (int slot = 0; slot < ScePowerState.IdleTimerCount; slot++)
        {
            if (TakeIdleEvent(slot, now, out uint callback, out uint argument, out uint second, out uint fourth))
            {
                thread.CallGuest(callback, (uint)slot, second, argument, fourth);
            }
        }

        ArmIdleCheck();
    }

    /// <summary>One slot's step of the check: true when its callback is to be called with (slot,
    /// <paramref name="second"/>, <paramref name="argument"/>, <paramref name="fourth"/>).</summary>
    bool TakeIdleEvent(int slot, long now, out uint callback, out uint argument, out uint second, out uint fourth)
    {
        second = 0;
        fourth = 0;
        lock (State)
        {
            callback = State.IdleCallback[slot];
            argument = State.IdleArgument[slot];
            byte state = State.IdleState[slot];
            if (callback == 0)
            {
                return false;
            }

            if (state == IdleForced)
            {
                State.IdleTickPending[slot] = false;
            }
            else if (State.IdleTickPending[slot])
            {
                // a tick: the slot counts again, and a slot that had fired (or was never checked) is told so
                State.IdleTickPending[slot] = false;
                if (state == IdleCounting)
                {
                    return false;
                }

                State.IdleState[slot] = IdleCounting;
                fourth = (uint)(sbyte)state;
                return true;
            }
            else if (state == IdleFired)
            {
                return false;
            }

            if (State.IdleTimeoutUs[slot] == 0)
            {
                return false;
            }

            long elapsed = now - State.IdleStartUs[slot];
            if (state != IdleForced && elapsed < State.IdleTimeoutUs[slot])
            {
                return false;
            }

            State.IdleState[slot] = IdleFired;
            second = (uint)elapsed;
            fourth = (uint)((ulong)elapsed >> 32);
            return true;
        }
    }
    /// <summary>A state load: the check is armed for the loaded slots.</summary>
    void ISnapshotTimers.RearmTimers()
    {
        ArmIdleCheck();
    }
}
