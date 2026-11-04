using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

[GenerateSnapshot, SnapshotIgnoreFields("_events")]
public sealed partial class TriggerUtil : ISnapshotState
{
    string? ISnapshotState.CheckSnapshot()
    {
        if (_events?.Count > 0) return "host resource scheduled trigger event not supported yet";
        return null;
    }

    /// <summary>The saved state had none (the save is refused otherwise).</summary>
    void LoadExtra(SnapshotReader r)
    {
        _events?.Clear();
    }
}
