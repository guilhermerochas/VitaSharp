using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

[GenerateSnapshot, SnapshotIgnoreFields("Clock", "MemBlockExists", "_ports")]
public sealed partial class Voice : ISnapshotState
{
    string? ISnapshotState.CheckSnapshot()
    {
        if (_ports.Any(p => p != null)) return "host resource voice chat port not supported yet";
        return null;
    }

    /// <summary>The saved state had none (the save is refused otherwise).</summary>
    void LoadExtra(SnapshotReader r)
    {
        Array.Clear(_ports);
    }
}
