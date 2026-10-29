namespace VitaSharp.Core.Hle;

/// <summary>
/// The PSN account of the emulated console. VitaSharp never talks to Sony's servers; this only decides what the
/// system reports locally:
/// <list type="bullet">
/// <item><b>Signed in</b> (default, like Vita3K's "PSN status: signed in"): NetCtl reports a connected access point with an
/// IP address, NP reports <see cref="OnlineId"/> as the account (service state ONLINE with the local services, else
/// SIGNED_IN until a PSN_ONLINE NetCheckDialog connects) and the NetCheckDialog PSN modes finish OK.
/// With <see cref="LocalServices"/> (default) the PSN services that can work for a single player are served locally
/// (<see cref="Np.LocalPsn"/>: TUS / Cross-Save storage, rankings with the user's own scores, auth tickets, matching
/// contexts with no other players, an empty store); the rest (WebAPI, messages, signaling to peers) fails like an
/// unreachable server with <see cref="ServerUnreachable"/>. Without local services every server request fails that way.
/// Many titles refuse to leave their start screen without a signed-in account (e.g. a Cross-Save sign-in prompt that only
/// offers "retry").</item>
/// <item><b>Signed out</b>: no account and no network; NP reports SIGNED_OUT, account queries fail with
/// SCE_NP_ERROR_SIGNED_OUT, NetCtl callbacks are told DISCONNECTED and the NetCheckDialog is canceled.</item>
/// </list>
/// Process-wide (one emulated console per process); set from <see cref="EmulatorOptions.Psn"/> when the emulator starts.
/// </summary>
public sealed record PsnAccount(bool SignedIn, string OnlineId, bool LocalServices = true)
{
    /// <summary>"host:port" of the RPCN server Score and TUS go to (real internet, see <see cref="Np.Rpcn.RpcnClient"/>);
    /// null = the local services. With a server the other services stay local.</summary>
    public string? Server { get; init; }

    public const string DefaultOnlineId = "VitaSharp";

    /// <summary>SCE_HTTP_ERROR_NETWORK (psp2/net/http.h): what a signed-in console gets for a PSN server request that
    /// cannot reach the server (the emulated LAN has no route to the internet).</summary>
    public const int ServerUnreachable = unchecked((int)0x80431063);

    /// <summary>SCE_NP_ERROR_SIGNED_OUT.</summary>
    public const int SignedOutError = unchecked((int)0x80550006);

    public static readonly PsnAccount Default = new(true, DefaultOnlineId);
    public static readonly PsnAccount Offline = new(false, DefaultOnlineId);

    static volatile PsnAccount _current = Default;

    /// <summary>The account the HLE modules report.</summary>
    public static PsnAccount Current { get => _current; set => _current = value ?? Default; }

    /// <summary>The error of a request that needs a PSN server: unreachable when signed in, SIGNED_OUT otherwise.</summary>
    public int ServerError => SignedIn ? ServerUnreachable : SignedOutError;

    /// <summary>Signed in with the local PSN services: TUS, rankings, tickets and matching work offline.</summary>
    public bool UsesLocalServices => SignedIn && (LocalServices || Server != null);

    /// <summary>
    /// PSN online IDs: 3 to 16 characters, ASCII letters, digits, '-' and '_', starting with a letter.
    /// </summary>
    public static bool IsValidOnlineId(string? id)
    {
        if (id is null || id.Length is < 3 or > 16 || !char.IsAsciiLetter(id[0])) return false;
        foreach (char c in id)
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')) return false;
        return true;
    }

    /// <summary>A signed-in or signed-out account; an invalid online id falls back to <see cref="DefaultOnlineId"/>.</summary>
    public static PsnAccount Create(bool signedIn, string? onlineId, bool localServices = true) =>
        new(signedIn, IsValidOnlineId(onlineId) ? onlineId! : DefaultOnlineId, localServices);
}
