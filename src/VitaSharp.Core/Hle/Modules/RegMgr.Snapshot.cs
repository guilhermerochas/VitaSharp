using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>Save states: the registry values the game wrote are the user's settings, kept on disk (not rolled back).</summary>
[GenerateSnapshot, SnapshotIgnoreFields("_written", "FsRootOverride")]
public sealed partial class RegMgr
{
}
