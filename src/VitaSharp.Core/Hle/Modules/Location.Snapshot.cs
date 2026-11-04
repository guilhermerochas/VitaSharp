using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

[GenerateSnapshot]
public sealed partial class Gps
{
}

[GenerateSnapshot, SnapshotIgnoreFields("_handles")]
public sealed partial class Location : ISnapshotState
{
    string? ISnapshotState.CheckSnapshot()
    {
        if (_handles.Count > 0) return "host resource location handle not supported yet";
        return null;
    }

    /// <summary>The saved state had none (the save is refused otherwise).</summary>
    void LoadExtra(SnapshotReader r)
    {
        _handles.Clear();
    }
}
