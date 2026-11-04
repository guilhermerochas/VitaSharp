using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>Save states: the LiveArea frame cache is read again from the game's files on demand.</summary>
[GenerateSnapshot, SnapshotIgnoreFields("VfsOverride", "_frames")]
public sealed partial class LiveArea
{
}
