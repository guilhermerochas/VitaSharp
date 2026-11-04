using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

[GenerateSnapshot, SnapshotIgnoreFields("_ctx")]
public sealed partial class NetAdhocMatching : ISnapshotState
{
    string? ISnapshotState.CheckSnapshot()
    {
        if (_ctx.Count > 0) return "host resource ad hoc matching context (network) not supported yet";
        return null;
    }

    /// <summary>The saved state had none (the save is refused otherwise).</summary>
    void LoadExtra(SnapshotReader r)
    {
        _ctx.Clear();
    }
}
