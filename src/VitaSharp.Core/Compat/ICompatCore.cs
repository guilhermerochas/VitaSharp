using VitaSharp.Core.Compat.Allegrex;

namespace VitaSharp.Core.Compat;

/// <summary>
/// The compat core as SceCompat's side drives it (<see cref="CompatCoreRunner"/>): reset to the boot address, run in
/// slices, the interrupt controller line held high or low, halted until an interrupt. <see cref="AllegrexCompatCore"/>
/// is the Allegrex interpreter; tests give their own.
/// </summary>
public interface ICompatCore
{
    /// <summary>Resets the core and starts it at <paramref name="pc"/> (a PSP virtual address).</summary>
    void Reset(uint pc);

    /// <summary>Runs up to <paramref name="cycles"/> cycles; returns the cycles run (fewer when the core halts).</summary>
    int Run(int cycles);

    /// <summary>Drives the core's interrupt line <paramref name="line"/> high.</summary>
    void RaiseInterrupt(int line);

    /// <summary>Drives the core's interrupt line <paramref name="line"/> low.</summary>
    void ClearInterrupt(int line);

    /// <summary>The core waits for an interrupt (halt/wait): no cycles run until a line changes.</summary>
    bool Halted { get; }

    /// <summary>Stops the core (the reset line held: nothing runs until the next <see cref="Reset"/>).</summary>
    void Stop();
}

/// <summary>The Allegrex interpreter as the compat core.</summary>
public sealed class AllegrexCompatCore(AllegrexCore core) : ICompatCore
{
    /// <summary>The interpreter.</summary>
    public AllegrexCore Core => core;

    public bool Halted => core.Halted;

    public void Reset(uint pc)
    {
        core.Reset(pc);
    }

    public int Run(int cycles)
    {
        return core.Run(cycles);
    }

    public void RaiseInterrupt(int line)
    {
        core.RaiseInterrupt(line);
    }

    public void ClearInterrupt(int line)
    {
        core.ClearInterrupt(line);
    }

    public void Stop()
    {
        core.Stop();
    }
}
