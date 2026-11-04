using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

[GenerateSnapshot, SnapshotIgnoreFields("_engine")]
public sealed partial class Sas : ISnapshotState
{
    string? ISnapshotState.CheckSnapshot()
    {
        if (_engine != null) return "host resource SAS audio engine not supported yet";
        return null;
    }

    /// <summary>The saved state had none (the save is refused otherwise).</summary>
    void LoadExtra(SnapshotReader r)
    {
        _engine = null;
    }
}
