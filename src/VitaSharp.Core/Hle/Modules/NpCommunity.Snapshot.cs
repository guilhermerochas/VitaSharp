using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

[GenerateSnapshot, SnapshotIgnoreFields("StoreOverride")]
public sealed partial class NpScore
{
}

[GenerateSnapshot, SnapshotIgnoreFields("StoreOverride")]
public sealed partial class NpTus
{
}

[GenerateSnapshot]
public sealed partial class NpUtility
{
}
