using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Compat.Allegrex;

/// <summary>
/// Save states (docs/notes/save-states.md): the whole architectural state of the core, as it stands between two
/// instructions: the general registers, HI/LO, the PC with its pending branch, COP0 and its control registers, the
/// driven interrupt lines, the interrupt control register, the Count offset and its timer interrupt, the LL bit, halt
/// and stop, the instruction count, the FPU (registers and FCR31) and the VFPU (registers and control registers: the S,
/// T and D prefixes, the condition codes, the random state). The lanes and matrices the VFPU instructions work in are
/// scratch of one instruction; the bus and the trap handler are the session's.
/// </summary>
[GenerateSnapshot, SnapshotIgnoreFields("TrapHandler", "_laneS", "_laneT", "_laneD", "_matrixS", "_matrixT", "_matrixD")]
public sealed partial class AllegrexCore
{
}
