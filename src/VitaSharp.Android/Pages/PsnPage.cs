using VitaSharp.Core.Localization;
using MauiReactor;
using VitaSharp.Android.Services;
using VitaSharp.Android.Ui;
using VitaSharp.Core.Hle;
using VitaSharp.Mobile.Core.Settings;

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
        return Page(Loc.Get("MPsnAndNetwork"),
            VStack(
                SectionTitle(Loc.Get("MAccount")),
                Card(
                    ToggleRow(Loc.Get("MPsnSignedIn"), n.PsnSignedIn, v => Change(x => x.PsnSignedIn = v),
                        detail: Loc.Get("MPsnSignedInDetail")),
                    Divider(),
                    Row(Loc.Get("MOnlineId"), n.PsnOnlineId, () => Edit(Loc.Get("MOnlineId"), Loc.Get("MOnlineIdHint"), n.PsnOnlineId, v =>
                    {
                        if (PsnAccount.IsValidOnlineId(v)) Change(x => x.PsnOnlineId = v);
                        else _ = Alert(Loc.Get("MOnlineId"), Loc.Get("MOnlineIdInvalid"));
                    })),
                    Divider(),
                    ToggleRow(Loc.Get("MPsnLocal"), n.PsnLocalServices, v => Change(x => x.PsnLocalServices = v),
                        detail: Loc.Get("MPsnLocalDetail"))
                ),
                SectionTitle(Loc.Get("MNetwork")),
                Card(
                    ToggleRow(Loc.Get("MRealInternet"), n.RealInternet, v => Change(x => x.RealInternet = v),
                        detail: Loc.Get("MRealInternetDetail"))
                ),
                Note(NetworkSettings.PrivacyNote),
                SectionTitle(Loc.Get("MRpcnServerOptional")),
                Card(
                    Row(Loc.Get("MServer"), n.RpcnServer.Length > 0 ? n.RpcnServer : Loc.Get("MNoneLower"), rpcn ? () => Edit(Loc.Get("MRpcnServer"), Loc.Get("MHostPort"), n.RpcnServer, v => Change(x => x.RpcnServer = v.Trim())) : null),
                    Divider(),
                    Row(Loc.Get("MUser"), n.RpcnUser.Length > 0 ? n.RpcnUser : "\u2014", rpcn ? () => Edit(Loc.Get("MRpcnUser"), "", n.RpcnUser, v => Change(x => x.RpcnUser = v.Trim())) : null),
                    Divider(),
                    Row(Loc.Get("MPassword"), State.HasPassword ? "\u2022\u2022\u2022\u2022\u2022\u2022\u2022\u2022" : "\u2014", rpcn ? EditPassword : null, detail: Loc.Get("MKeystoreDetail")),
                    Divider(),
                    Row("Token", n.RpcnToken.Length > 0 ? Loc.Get("MSet") : "\u2014", rpcn ? () => Edit(Loc.Get("MRpcnToken"), Loc.Get("MRpcnTokenHint"), n.RpcnToken, v => Change(x => x.RpcnToken = v.Trim())) : null)
                ).Opacity(rpcn ? 1 : 0.5),
                Note(rpcn ? Loc.Get("MRpcnNote")
                          : Loc.Get("MRpcnNeedsInternet"))
            ), kicker: Loc.Get("MSettings"));
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
        var v = await Prompt(Loc.Get("MRpcnPassword"), Loc.Get("MRpcnPasswordDetail"), "", password: true);
        if (v == null) return;
        await AppServices.SetRpcnPasswordAsync(v);
        SetState(s => s.HasPassword = v.Length > 0);
    }
}
