using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

[GenerateSnapshot, SnapshotIgnoreFields("DecoderFactory", "_handles")]
public sealed partial class Atrac : ISnapshotState
{
    string? ISnapshotState.CheckSnapshot()
    {
        if (_handles.Count > 0) return "host resource ATRAC decoder stream not supported yet";
        return null;
    }

    /// <summary>The saved state had none (the save is refused otherwise).</summary>
    void LoadExtra(SnapshotReader r)
    {
        _handles.Clear();
    }
}
