using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

[GenerateSnapshot, SnapshotIgnoreFields("_recorders")]
public sealed partial class Mp4Recorder : ISnapshotState
{
    string? ISnapshotState.CheckSnapshot()
    {
        if (_recorders.Count > 0) return "host resource MP4 recorder not supported yet";
        return null;
    }

    /// <summary>The saved state had none (the save is refused otherwise).</summary>
    void LoadExtra(SnapshotReader r)
    {
        _recorders.Clear();
    }
}
