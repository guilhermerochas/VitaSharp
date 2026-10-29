using System.Collections.Concurrent;
using System.Net;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle;

/// <summary>Network profile of the emulated console (<see cref="NetConfig.Mode"/>).</summary>
public enum NetMode
{
    /// <summary>Default: a LAN without internet. Sockets, DNS and HTTP fail like an unreachable server.</summary>
    Off,
    /// <summary>Opt-in: SceNet sockets, the resolver and SceHttp go through the host's network stack.</summary>
    Real,
}

/// <summary>
/// Real-internet settings (<c>--net=real|off</c>, <c>--net-allow=</c>, launcher "Internet real"). Off by default.
/// With <see cref="NetMode.Real"/> every destination still goes through <see cref="Permits(string, out string)"/>:
/// <list type="bullet">
/// <item>Sony's PSN/PlayStation domains are always blocked (most are shut down, and a game must never talk to them on the
/// user's behalf); the request fails like an unreachable server and the attempt is logged.</item>
/// <item>With an allow list only the listed hosts (or IP addresses) are reachable; "localhost" also allows the loopback
/// addresses. Tests always run with an allow list of localhost.</item>
/// </list>
/// Process-wide like <see cref="PsnAccount"/>; set from <see cref="EmulatorOptions.Net"/> when the emulator starts. The HLE
/// modules take an override for tests.
/// </summary>
public sealed class NetConfig
{
    public NetMode Mode { get; }
    /// <summary>Hosts (names or IP addresses) that may be reached; null = any host except the blocked ones.</summary>
    public IReadOnlyList<string>? Allow { get; }

    readonly ConcurrentDictionary<IPAddress, string> _resolved = new();
    readonly ConcurrentDictionary<string, byte> _logged = new(StringComparer.OrdinalIgnoreCase);

    public NetConfig(NetMode mode, IEnumerable<string>? allow = null)
    {
        Mode = mode;
        var list = allow?.Select(a => a.Trim()).Where(a => a.Length > 0).ToList();
        Allow = list is { Count: > 0 } ? list : null;
    }

    public static readonly NetConfig Off = new(NetMode.Off);

    static volatile NetConfig _current = Off;

    /// <summary>The configuration the HLE modules use.</summary>
    public static NetConfig Current { get => _current; set => _current = value ?? Off; }

    public bool Real => Mode == NetMode.Real;

    /// <summary>Domains that are never contacted: Sony's PSN, PlayStation and SIE servers (all subdomains).</summary>
    public static readonly string[] BlockedDomains =
    [
        "playstation.net", "playstation.com", "playstation.org", "sonyentertainmentnetwork.com", "sony.com", "sony.net",
        "scea.com", "scee.com", "scei.co.jp", "sie.com", "siedev.net", "station.sony.com",
    ];

    static bool DomainMatch(string host, string domain) =>
        host.Equals(domain, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);

    /// <summary>Parses <c>off</c>/<c>real</c> (also on/1/true); null when not recognised.</summary>
    public static NetMode? ParseMode(string? s) => s?.Trim().ToLowerInvariant() switch
    {
        "off" or "0" or "false" or "no" => NetMode.Off,
        "real" or "on" or "1" or "true" or "yes" => NetMode.Real,
        _ => null,
    };

    /// <summary>Whether <paramref name="host"/> (a name or an address literal) may be contacted; <paramref name="why"/>
    /// explains a refusal.</summary>
    public bool Permits(string host, out string why)
    {
        why = "";
        host = host.Trim().TrimEnd('.').Trim('[', ']');
        if (!Real) { why = "real internet is off (--net=off)"; return false; }
        if (IPAddress.TryParse(host, out var ip)) return PermitsAddress(ip, out why);
        foreach (var d in BlockedDomains)
            if (DomainMatch(host, d)) { why = $"{host} is a Sony/PSN server (always blocked)"; return false; }
        if (Allow == null) return true;
        foreach (var a in Allow)
            if (DomainMatch(host, a)) return true;
        why = $"{host} is not in the allow list (--net-allow={string.Join(",", Allow)})";
        return false;
    }

    /// <summary>Whether a socket may talk to <paramref name="ip"/>: the address must be allowed itself or have been
    /// resolved from an allowed host name (see <see cref="NoteResolved"/>).</summary>
    public bool PermitsAddress(IPAddress ip, out string why)
    {
        why = "";
        if (!Real) { why = "real internet is off (--net=off)"; return false; }
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (_resolved.TryGetValue(ip, out var name) && !Permits(name, out why)) return false;
        if (Allow == null) return true;
        if (IPAddress.IsLoopback(ip) && Allow.Any(a => a.Equals("localhost", StringComparison.OrdinalIgnoreCase))) return true;
        if (ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.Broadcast)) return true; // bind / LAN broadcast
        foreach (var a in Allow)
            if (IPAddress.TryParse(a, out var allowed) && allowed.Equals(ip)) return true;
        if (name != null) return true; // resolved from an allowed name (checked above)
        why = $"{ip} is not in the allow list (--net-allow={string.Join(",", Allow)})";
        return false;
    }

    /// <summary>Remembers which host name the resolver turned into these addresses, so connections to them are checked
    /// against the name.</summary>
    public void NoteResolved(string host, IEnumerable<IPAddress> addresses)
    {
        foreach (var a in addresses) _resolved[a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a] = host;
    }

    /// <summary>The host name an address was resolved from, if any.</summary>
    public string? NameOf(IPAddress ip) => _resolved.GetValueOrDefault(ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip);

    /// <summary>Logs a refused destination once per destination.</summary>
    public void LogBlocked(string what, string why)
    {
        if (_logged.TryAdd(what + "|" + why, 0)) Log.Warn($"net: blocked {what}: {why}");
    }

    /// <summary>Logs the first contact with a destination (the user can see where game traffic goes).</summary>
    public void LogContact(string what)
    {
        if (_logged.TryAdd(what, 0)) Log.Info($"net: {what}");
    }

    public override string ToString() => Real ? Allow == null ? "real internet" : $"real internet, only {string.Join(", ", Allow)}" : "no internet";
}
