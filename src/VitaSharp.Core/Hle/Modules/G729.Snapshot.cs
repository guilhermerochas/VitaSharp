using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

[GenerateSnapshot, SnapshotIgnoreFields("_dec", "_enc")]
public sealed unsafe partial class G729 : ISnapshotState
{
    string? ISnapshotState.CheckSnapshot()
    {
        if (_dec.Count > 0) return "host resource G.729 decoder not supported yet";
        if (_enc.Count > 0) return "host resource G.729 encoder not supported yet";
        return null;
    }

    /// <summary>The saved state had none (the save is refused otherwise).</summary>
    void LoadExtra(SnapshotReader r)
    {
        _dec.Clear();
        _enc.Clear();
    }
}
