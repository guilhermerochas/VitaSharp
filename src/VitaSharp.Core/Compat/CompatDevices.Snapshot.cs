using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Compat;

/// <summary>Save states: the four bank registers (the unknown-access log and the hardware link are the session's).</summary>
[GenerateSnapshot, SnapshotIgnoreFields("_unknownLogged", "_unknownAccesses", "Hardware")]
public sealed partial class PspBus
{
}

/// <summary>Save states: the pending lines of both sides and the ring register (the callbacks are the session's).</summary>
[GenerateSnapshot, SnapshotIgnoreFields("ArmInterrupt", "PspInterruptLevel")]
public sealed partial class CompatMailbox
{
}

/// <summary>Save states: the shared SRAM's words.</summary>
[GenerateSnapshot]
public sealed partial class CompatSharedSram
{
}

/// <summary>Save states: the destination, the size, whether it runs and the frames copied (the callbacks are the
/// session's).</summary>
[GenerateSnapshot, SnapshotIgnoreFields("FrameDone", "BeforeRead")]
public sealed partial class CompatLcdDma
{
}
