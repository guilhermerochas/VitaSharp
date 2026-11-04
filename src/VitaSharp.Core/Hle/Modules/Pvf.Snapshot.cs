using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

[GenerateSnapshot, SnapshotIgnoreFields("GuestCallOverride", "SystemFontOverride", "_fonts", "_libs")]
public sealed partial class Pvf : ISnapshotState
{
    string? ISnapshotState.CheckSnapshot()
    {
        if (_fonts.Count > 0) return "host resource open PVF font not supported yet";
        if (_libs.Count > 0) return "host resource PVF font library not supported yet";
        return null;
    }

    /// <summary>The saved state had none (the save is refused otherwise).</summary>
    void LoadExtra(SnapshotReader r)
    {
        _fonts.Clear();
        _libs.Clear();
    }
}
