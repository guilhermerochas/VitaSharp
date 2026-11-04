using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

[GenerateSnapshot, SnapshotIgnoreFields("GuestCallOverride", "_backend", "_events")]
public sealed partial class Ime : ISnapshotState
{
    string? ISnapshotState.CheckSnapshot()
    {
        if (_events.Count > 0) return "host resource IME session events not supported yet";
        return null;
    }

    /// <summary>The saved state had none (the save is refused otherwise).</summary>
    void LoadExtra(SnapshotReader r)
    {
        _events.Clear();
    }
}
