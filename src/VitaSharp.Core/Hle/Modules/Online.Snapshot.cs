using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>Save states: net callbacks and settings are kept; sockets, epolls, resolvers and dumps are host network
/// state (the save is refused while one is open).</summary>
[GenerateSnapshot, SnapshotIgnoreFields("ConfigOverride", "_sockets", "_epolls", "_resolvers", "_dumps", "_poller")]
public sealed partial class Net : ISnapshotState
{
    [GenerateSnapshot]
    sealed partial class NetCallback;

    string? ISnapshotState.CheckSnapshot()
    {
        lock (_sockets) if (_sockets.Count > 0) return "host resource network socket not supported yet";
        lock (_epolls) if (_epolls.Count > 0) return "host resource network epoll not supported yet";
        lock (_resolvers) if (_resolvers.Count > 0) return "host resource DNS resolver not supported yet";
        lock (_dumps) if (_dumps.Count > 0) return "host resource network dump not supported yet";
        return null;
    }
}
