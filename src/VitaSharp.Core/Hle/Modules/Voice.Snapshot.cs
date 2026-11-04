using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>Save states: voice ports with their buffered audio; a port's host output stream is opened again on demand.</summary>
[GenerateSnapshot, SnapshotIgnoreFields("Clock", "MemBlockExists")]
public sealed partial class Voice
{
    [GenerateSnapshot, SnapshotIgnoreFields("Stream", "StreamTried")]
    sealed partial class Port;

    void BeforeSnapshotLoad()
    {
        foreach (var p in _ports) { p?.Stream?.Dispose(); if (p != null) p.Stream = null; }
    }
}
