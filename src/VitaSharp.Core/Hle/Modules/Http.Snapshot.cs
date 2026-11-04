using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>Save states: templates, connections and requests are kept; an exchange in flight on the real network, or
/// an epoll, cannot be (the save is refused while one exists).</summary>
[GenerateSnapshot, SnapshotIgnoreFields("ConfigOverride", "_client", "_epolls")]
public sealed partial class Http : ISnapshotState
{
    [GenerateSnapshot, SnapshotIgnoreFields("Ex", "Epoll")]
    sealed partial class Obj;

    [GenerateSnapshot]
    sealed partial class Settings;

    string? ISnapshotState.CheckSnapshot()
    {
        lock (_lock)
        {
            if (_epolls.Count > 0) return "host resource HTTP epoll not supported yet";
            if (_objs.Values.Any(o => o.Ex != null || o.Epoll != null)) return "host resource HTTP exchange in progress not supported yet";
        }
        return null;
    }
}

[GenerateSnapshot]
public sealed partial class Ssl;
