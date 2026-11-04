using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

[GenerateSnapshot, SnapshotIgnoreFields("_encoders")]
public sealed partial class Audioenc : ISnapshotState
{
    string? ISnapshotState.CheckSnapshot()
    {
        if (_encoders.Count > 0) return "host resource audio encoder not supported yet";
        return null;
    }

    /// <summary>The saved state had none (the save is refused otherwise).</summary>
    void LoadExtra(SnapshotReader r)
    {
        _encoders.Clear();
    }
}
