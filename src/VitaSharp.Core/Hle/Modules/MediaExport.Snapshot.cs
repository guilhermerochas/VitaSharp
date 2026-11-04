using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

[GenerateSnapshot, SnapshotIgnoreFields("NowOverride", "VfsOverride")]
public sealed partial class MediaExport
{
}
