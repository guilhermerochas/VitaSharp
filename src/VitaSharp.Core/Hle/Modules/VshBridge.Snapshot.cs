using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

[GenerateSnapshot, SnapshotIgnoreFields("_caf")] // crash-report crypto contexts live for one upload
public sealed partial class VshBridge
{
}
