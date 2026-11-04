using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

[GenerateSnapshot, SnapshotIgnoreFields("_recognizers", "_primitives", "_live", "_mine")]
public sealed partial class SystemGesture : ISnapshotState
{
    string? ISnapshotState.CheckSnapshot()
    {
        if (_recognizers.Count > 0) return "host resource touch gesture recognizer not supported yet";
        if (_primitives.Count > 0 || _live.Count > 0 || _mine.Count > 0) return "host resource touch gesture state not supported yet";
        return null;
    }

    /// <summary>The saved state had none (the save is refused otherwise).</summary>
    void LoadExtra(SnapshotReader r)
    {
        _recognizers.Clear();
        _primitives.Clear(); _live.Clear(); _mine.Clear();
    }
}
