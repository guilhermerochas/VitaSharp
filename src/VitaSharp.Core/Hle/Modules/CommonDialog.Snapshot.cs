using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>Save states: the dialog slot (a finished dialog's result is kept); no state is saved while a dialog is on
/// screen, its host UI cannot be recreated.</summary>
[GenerateSnapshot, SnapshotIgnoreFields("ImeBackendOverride", "_imeBackend", "MessageBackendOverride", "_ui")]
public sealed partial class CommonDialog : ISnapshotState
{
    string? ISnapshotState.CheckSnapshot() => State.IsRunning ? $"a system dialog ({State.Type}) is open; close it, then save" : null;

    void SaveExtra(SnapshotWriter w) { lock (State.Lock) { w.Write((int)State.Type); w.Write(State.Status); w.Write(State.Result); } }

    void LoadExtra(SnapshotReader r) => State.Restore((DialogType)r.ReadInt32(), r.ReadInt32(), r.ReadInt32());
}

public sealed partial class CommonDialogState
{
    internal void Restore(DialogType type, int status, int result)
    {
        lock (Lock) { Type = type; Status = status; Result = result; }
    }
}
