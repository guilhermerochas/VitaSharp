using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

[GenerateSnapshot]
public sealed partial class ThreadmgrKernel
{
    /// <summary>The alarms of a loaded state are still due: the thread manager's timer line is armed again for the
    /// earliest one (the line's schedule is not part of the state).</summary>
    void AfterSnapshotLoad() => ArmAlarmTimer();
}
