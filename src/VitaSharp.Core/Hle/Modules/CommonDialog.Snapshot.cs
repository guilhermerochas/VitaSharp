using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

[GenerateSnapshot, SnapshotIgnoreFields("ImeBackendOverride", "_imeBackend")]
public sealed partial class CommonDialog
{
}
