using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>Save states: matching contexts and their callbacks (the online session itself is offline/mock).</summary>
[GenerateSnapshot, SnapshotIgnoreFields("MemoryOverride")]
public sealed partial class NpMatching2
{
    [GenerateSnapshot]
    sealed partial class Context;
}

[GenerateSnapshot, SnapshotIgnoreFields("_contexts", "_polls")]
public sealed partial class Rudp : ISnapshotState
{
    string? ISnapshotState.CheckSnapshot()
    {
        if (_contexts.Count > 0) return "host resource RUDP context (network) not supported yet";
        if (_polls.Count > 0) return "host resource RUDP poll (network) not supported yet";
        return null;
    }

    /// <summary>The saved state had none (the save is refused otherwise).</summary>
    void LoadExtra(SnapshotReader r)
    {
        _contexts.Clear();
        _polls.Clear();
    }
}
