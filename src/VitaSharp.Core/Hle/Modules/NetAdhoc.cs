namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// ScePspnetAdhoc (vs0:sys/external/pspnet_adhoc.suprx), the PSP-compatible ad hoc stack, in the offline profile.
/// On the Vita sceNetAdhocInit only sets up the library (its worker threads and a local control socket) and does
/// not need Wi-Fi or a peer: it fails only before sceNetInit (the socket cannot be created) or when the library is
/// already initialised. Joining a group (sceNetAdhocctl*) is where the absence of a network shows.
/// </summary>
public sealed partial class NetAdhoc : HleModule
{
    internal const int ERR_ALREADY_INITIALIZED = unchecked((int)0x80410713), ERR_BUSY = unchecked((int)0x80410714);

    readonly object _lock = new();
    bool _inited;

    Net Net => Emu.Hle.Modules.OfType<Net>().First();

    [Hle]
    int sceNetAdhocInit()
    {
        lock (_lock)
        {
            if (_inited) return ERR_ALREADY_INITIALIZED;
            // the library's sceNetSocket fails with ENOTINIT before sceNetInit, and Init returns that error
            if (!Net.Initialized) return Net.ENOTINIT;
            _inited = true;
            return 0;
        }
    }

    /// <summary>A library that was never initialised terminates fine (pspnet_adhoc returns 0 then too).</summary>
    [Hle]
    int sceNetAdhocTerm()
    {
        lock (_lock)
        {
            _inited = false;
            return 0;
        }
    }
}
