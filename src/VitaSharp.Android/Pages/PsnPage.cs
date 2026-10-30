using MauiReactor;
using VitaSharp.Android.Services;
using VitaSharp.Android.Settings;
using VitaSharp.Android.Ui;
using VitaSharp.Core.Hle;

namespace VitaSharp.Android.Pages;

public sealed class PsnState
{
    public bool HasPassword { get; set; }
    public int Revision { get; set; }
}

/// <summary>
/// Conta PSN e rede (psn.html): the emulated PSN account (signed in, online ID), the local PSN services, the opt-in
/// "Internet real" with its privacy note always visible, and the optional RPCN server, user, password (Android
/// Keystore) and token, editable only with real internet on. Same meaning and defaults as the desktop launcher.
/// </summary>
public sealed class PsnPage : Screen<PsnState>
{
    protected override async void OnMounted()
    {
        base.OnMounted();
        var pw = await AppServices.RpcnPasswordAsync();
        SetState(s => s.HasPassword = pw.Length > 0);
    }

    public override VisualNode Render()
    {
        var n = AppServices.Prefs.Network;
        bool rpcn = n.RpcnEditable;
        return Page("Conta PSN e rede",
            VStack(
                SectionTitle("Conta"),
                Card(
                    ToggleRow("Conta PSN conectada", n.PsnSignedIn, v => Change(x => x.PsnSignedIn = v),
                        detail: "Simulada: as checagens de login do jogo passam"),
                    Divider(),
                    Row("ID online", n.PsnOnlineId, () => Edit("ID online", "3 a 16 letras, números, - ou _", n.PsnOnlineId, v =>
                    {
                        if (PsnAccount.IsValidOnlineId(v)) Change(x => x.PsnOnlineId = v);
                        else _ = Alert("ID online", "ID inválido: 3 a 16 caracteres, começando com letra (letras, números, - ou _).");
                    })),
                    Divider(),
                    ToggleRow("Serviços PSN locais", n.PsnLocalServices, v => Change(x => x.PsnLocalServices = v),
                        detail: "Rankings, TUS e salvamento na nuvem guardados no aparelho")
                ),
                SectionTitle("Rede"),
                Card(
                    ToggleRow("Internet real", n.RealInternet, v => Change(x => x.RealInternet = v),
                        detail: "Os jogos acessam a internet deste aparelho (servidores da Sony sempre bloqueados)")
                ),
                Note(NetworkSettings.PrivacyNote),
                SectionTitle("Servidor RPCN (opcional)"),
                Card(
                    Row("Servidor", n.RpcnServer.Length > 0 ? n.RpcnServer : "nenhum", rpcn ? () => Edit("Servidor RPCN", "host ou host:porta", n.RpcnServer, v => Change(x => x.RpcnServer = v.Trim())) : null),
                    Divider(),
                    Row("Usuário", n.RpcnUser.Length > 0 ? n.RpcnUser : "—", rpcn ? () => Edit("Usuário RPCN", "", n.RpcnUser, v => Change(x => x.RpcnUser = v.Trim())) : null),
                    Divider(),
                    Row("Senha", State.HasPassword ? "••••••••" : "—", rpcn ? EditPassword : null, detail: "Guardada no Android Keystore"),
                    Divider(),
                    Row("Token", n.RpcnToken.Length > 0 ? "definido" : "—", rpcn ? () => Edit("Token RPCN", "só no primeiro login de uma conta nova", n.RpcnToken, v => Change(x => x.RpcnToken = v.Trim())) : null)
                ).Opacity(rpcn ? 1 : 0.5),
                Note(rpcn ? "Com um servidor RPCN, rankings e TUS vão para ele e o ID online passa a ser o usuário RPCN."
                          : "Ligue a internet real (e a conta PSN) para configurar um servidor RPCN.")
            ), kicker: "Ajustes");
    }

    void Change(Action<NetworkSettings> change)
    {
        change(AppServices.Prefs.Network);
        AppServices.SavePrefs();
        SetState(s => s.Revision++);
    }

    async void Edit(string title, string message, string current, Action<string> apply)
    {
        var v = await Prompt(title, message, current);
        if (v != null) apply(v);
    }

    async void EditPassword()
    {
        var v = await Prompt("Senha RPCN", "Fica no Android Keystore deste aparelho.", "", password: true);
        if (v == null) return;
        await AppServices.SetRpcnPasswordAsync(v);
        SetState(s => s.HasPassword = v.Length > 0);
    }
}
