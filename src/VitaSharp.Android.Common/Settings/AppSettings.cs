using VitaSharp.Core.Cpu;
using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Np.Rpcn;

namespace VitaSharp.Android.Settings;

/// <summary>The choices of the per-game / global CPU setting ("CPU" in detalhes.html and ajustes.html).</summary>
public static class CpuChoices
{
    /// <summary>
    /// The CPU backends this device can run, best first. On a phone (ARM64) the automatic choice is the native ARM64
    /// JIT (docs/notes/native-jit.md); the IL JIT only exists when the runtime can compile IL (not under Native AOT);
    /// the interpreter is always there.
    /// </summary>
    public static IReadOnlyList<Choice> Available()
    {
        var list = new List<Choice> { new("", $"Automático · {Label(Jit.All.FirstOrDefault(b => b.IsSupported)?.Name ?? "interp")}") };
        foreach (var b in Jit.All.Where(b => b.IsSupported)) list.Add(new(b.Name, Label(b.Name)));
        list.Add(new("interp", Label("interp")));
        return list;
    }

    public static string Label(string backend) => backend switch
    {
        "native" => System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64
            ? "Nativo (ARM64)" : "Nativo (x64)",
        "il" => "JIT IL (.NET)",
        "interp" => "Interpretador (mais lento)",
        _ => backend,
    };
}

/// <summary>
/// "Conta PSN e rede" (docs/android/mockups/psn.html): the emulated PSN account, the local PSN services, the opt-in
/// real internet and the optional RPCN server, with the same defaults and meaning as the desktop launcher
/// (docs/hle/E2-np.md §0.000-0.0). Nothing ever goes to Sony's servers; with <see cref="RealInternet"/> off (the default)
/// nothing leaves the phone. The RPCN password is not part of this object: the front-end keeps it in the Android
/// Keystore (EncryptedSharedPreferences) and passes it to <see cref="ToOptions"/>.
/// </summary>
public sealed class NetworkSettings
{
    public bool PsnSignedIn { get; set; } = true;
    public string PsnOnlineId { get; set; } = PsnAccount.DefaultOnlineId;
    public bool PsnLocalServices { get; set; } = true;
    public bool RealInternet { get; set; }
    public string RpcnServer { get; set; } = "";
    public string RpcnUser { get; set; } = "";
    public string RpcnToken { get; set; } = "";

    /// <summary>The privacy note shown under "Internet real" whatever its state.</summary>
    public const string PrivacyNote = "Privacidade: com a internet real ligada, o jogo envia dados (seu IP, o ID online, pontuações e " +
        "salvamentos na nuvem) para os servidores dele e para o servidor RPCN abaixo. Desligada (padrão), nada sai do aparelho.";

    /// <summary>The RPCN fields are editable only with real internet and a signed-in account (as on the desktop).</summary>
    public bool RpcnEditable => RealInternet && PsnSignedIn;

    /// <summary>The values for EmulatorOptions.Psn / Net / Rpcn. An RPCN server needs a user and a password;
    /// without them Score/TUS stay on the local services.</summary>
    public (PsnAccount Psn, NetConfig Net, RpcnConfig? Rpcn) ToOptions(string rpcnPassword)
    {
        RpcnConfig? rpcn = null;
        if (RpcnEditable && RpcnConfig.ParseServer(RpcnServer) is { } server && RpcnUser.Length > 0 && rpcnPassword.Length > 0)
            rpcn = new RpcnConfig(server.Host, server.Port, RpcnUser, rpcnPassword, RpcnToken);
        // with an RPCN server the online ID is the RPCN user (as on the desktop)
        string? id = rpcn != null && PsnAccount.IsValidOnlineId(rpcn.User) ? rpcn.User
            : PsnAccount.IsValidOnlineId(PsnOnlineId) ? PsnOnlineId : null;
        return (PsnAccount.Create(PsnSignedIn, id, PsnLocalServices), new NetConfig(RealInternet ? NetMode.Real : NetMode.Off), rpcn);
    }
}
