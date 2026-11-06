using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

[GenerateSnapshot, SnapshotIgnoreFields("_safeMem", "_safeMemPath", "_safeMemLock")]
public sealed partial class AppUtil
{
}
