using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>Save states: net callbacks and settings are kept; open sockets are dropped (see LoadExtra).</summary>
[GenerateSnapshot, SnapshotIgnoreFields("ConfigOverride", "_sockets", "_epolls", "_resolvers", "_dumps", "_poller")]
public sealed partial class Net
{
    [GenerateSnapshot]
    sealed partial class NetCallback;

    /// <summary>Sockets, epolls and resolvers are host network state: they are not saved, and a load closes the
    /// running session's ones, so the game sees its connections gone (as after losing the network).</summary>
    void LoadExtra(SnapshotReader r)
    {
        lock (_sockets)
        {
            foreach (var s in _sockets.Values) { s.Closed = true; CloseHost(s); }
            _sockets.Clear();
        }
        lock (_epolls) _epolls.Clear();
        lock (_resolvers) _resolvers.Clear();
        lock (_dumps) _dumps.Clear();
    }
}
