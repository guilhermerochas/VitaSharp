using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using VitaSharp.Desktop.Input;

namespace VitaSharp.Desktop.Launcher;

/// <summary>Settings window: "Geral" (window, graphics, camera, CPU, Desempenho = shader compilation, Rede e PSN = emulated
/// PSN account, Firmware = installed system software) and "Controles" (keyboard / controller mapping).</summary>
public sealed class SettingsDialog : Window
{
    public const int GeneralTab = 0, ControlsTab = 1;

    readonly TabControl _tabs;

    /// <summary>Geral > Firmware (installed version, install from a .PUP or from Vita3K).</summary>
    public FirmwarePanel Firmware { get; }

    /// <summary>The controller page (created when the tab is first shown, so SDL only starts when needed).</summary>
    public ControllerSettingsPanel? Controls { get; private set; }

    /// <param name="pads">Controller backend for the Controls tab (tests / previews); null = its own.</param>
    public SettingsDialog(LauncherSettings settings, int initialTab = GeneralTab, SdlGamepads? pads = null)
    {
        Title = "Configurações";
        Width = 1120;
        Height = 760;
        MinWidth = 900;
        MinHeight = 600;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = LauncherTheme.WindowBackground;

        var fullscreen = new CheckBox { Content = "Iniciar jogos em tela cheia (F11 alterna no jogo)", IsChecked = settings.Fullscreen };
        var vsync = new CheckBox { Content = "VSync", IsChecked = settings.VSync };
        var validation = new CheckBox { Content = "Camadas de validação do Vulkan (depuração, lento)", IsChecked = settings.Validation };
        var scale = new ComboBox { ItemsSource = new[] { "1x (960×544)", "2x (1920×1088)", "3x (2880×1632)" }, SelectedIndex = Math.Clamp(settings.Scale - 1, 0, 2), Width = 200 };
        // camera: "" = first device, "none" = disabled, otherwise a device name
        var cameraValues = new List<string> { "", "none" };
        var cameraLabels = new List<string> { "Padrão (primeira câmera)", "Nenhuma (imagem cinza)" };
        foreach (var d in VitaSharp.Desktop.Host.FlashCapCamera.ListDevices()) { cameraValues.Add(d.Name); cameraLabels.Add(d.Name); }
        if (!cameraValues.Contains(settings.Camera, StringComparer.OrdinalIgnoreCase)) { cameraValues.Add(settings.Camera); cameraLabels.Add(settings.Camera + " (desconectada)"); }
        var camera = new ComboBox
        {
            ItemsSource = cameraLabels, Width = 320,
            SelectedIndex = Math.Max(0, cameraValues.FindIndex(v => v.Equals(settings.Camera, StringComparison.OrdinalIgnoreCase))),
        };
        // CPU backend: "" = best supported on this PC, else --cpu=native|il|interp
        // backends this build/host cannot run (the IL JIT in Native AOT builds, the native JIT on hosts that are neither x64 nor ARM64) are not offered;
        // a saved choice that became unavailable shows as Automatic
        var cpuChoices = new (string Value, string Label)[]
        {
            ("", "Automático (o melhor para este PC)"),
            ("native", System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "JIT nativo (código de máquina ARM64)" : "JIT nativo (código de máquina x64)"),
            ("il", "JIT IL (.NET DynamicMethod)"),
            ("interp", "Interpretador (mais lento, depuração)"),
        }.Where(c => c.Value is "" or "interp" || VitaSharp.Core.Cpu.Jit.All.Any(b => b.Name == c.Value && b.IsSupported)).ToArray();
        string[] cpuValues = cpuChoices.Select(c => c.Value).ToArray();
        var cpu = new ComboBox
        {
            ItemsSource = cpuChoices.Select(c => c.Label).ToArray(),
            Width = 320, SelectedIndex = Math.Max(0, Array.FindIndex(cpuValues, v => v.Equals(settings.CpuBackend, StringComparison.OrdinalIgnoreCase))),
        };
        // emulated ARM clock (--cpu-mhz=): retail 333 MHz by default; 444 MHz is what a game gets when it asks for it
        var clockValues = new[] { 0, 444 };
        var clock = new ComboBox
        {
            ItemsSource = new[] { "333 MHz (como o Vita, recomendado)", "444 MHz (carregamentos mais rápidos)" },
            Width = 320, SelectedIndex = settings.CpuMhz == 444 ? 1 : 0,
        };
        ToolTip.SetTip(clock, "Velocidade da CPU emulada. O Vita roda os jogos a 333 MHz; 444 MHz carrega mais rápido, " +
                              "mas jogos que dependem do tempo do console (por exemplo Silent Hill: Book of Memories) podem falhar.");
        // performance: shader compilation stutter (--shaderc-opt=, --async-shaders=)
        const string fastShadersTip = "Reduz travamentos quando shaders novos aparecem; desligue se notar perda de desempenho.";
        const string asyncShadersTip = "Prepara shaders e pipelines novos em outras threads enquanto o jogo ainda envia o quadro, " +
                                       "reduzindo travamentos ao entrar em áreas novas.";
        var fastShaders = new CheckBox { Content = "Compilação rápida de shaders", IsChecked = settings.FastShaderCompile };
        ToolTip.SetTip(fastShaders, fastShadersTip);
        var asyncShaders = new CheckBox { Content = "Compilar shaders e pipelines em segundo plano", IsChecked = settings.BackgroundShaderCompile };
        ToolTip.SetTip(asyncShaders, asyncShadersTip);

        // network and PSN (--psn=, --psn-id=): an emulated signed-in account; nothing is ever sent to Sony's servers
        const string psnTip = "Muitos jogos só passam da tela inicial com uma conta PSN conectada. O VitaSharp simula a conta " +
                              "localmente: a verificação de login passa e nada é enviado aos servidores da Sony. " +
                              "Desligado, o console fica sem rede e sem conta.";
        const string psnLocalTip = "Serviços da PSN que funcionam para um jogador só, guardados neste PC (pasta de dados\\np): " +
                                   "salvamento na nuvem (TUS / Cross-Save), placares com as suas pontuações, tickets de login " +
                                   "e DLC instalado como comprado. O multijogador abre o serviço mas não encontra salas. " +
                                   "Desligado, todo pedido a um servidor da PSN falha como se ele estivesse fora do ar.";
        var psnLocal = new CheckBox { Content = "Serviços PSN locais (nuvem, placares, DLC)", IsChecked = settings.PsnLocalServices };
        ToolTip.SetTip(psnLocal, psnLocalTip);
        var psnSignedIn = new CheckBox { Content = "Simular conta PSN conectada (offline)", IsChecked = settings.PsnSignedIn };
        ToolTip.SetTip(psnSignedIn, psnTip);
        var psnId = new TextBox { Text = settings.PsnOnlineId, Width = 200, MaxLength = 16, Watermark = VitaSharp.Core.Hle.PsnAccount.DefaultOnlineId };
        var psnIdError = new TextBlock
        {
            Text = "ID online inválido: use de 3 a 16 letras, números, '-' ou '_', começando com uma letra.",
            Foreground = Brushes.IndianRed, TextWrapping = TextWrapping.Wrap, FontSize = 12, IsVisible = false,
        };
        // real internet (--net=real) and an optional RPCN server for Score/TUS (--rpcn=, --rpcn-user=, password in the
        // game process environment): off by default, with the privacy note always visible
        const string realNetTip = "Os sockets, o DNS e o HTTP dos jogos passam a usar a rede deste PC. Os servidores da Sony " +
                                  "continuam bloqueados (a maioria já foi desligada); o resto do tráfego do jogo vai para os " +
                                  "servidores que o próprio jogo escolher.";
        var realNet = new CheckBox { Content = "Internet real", IsChecked = settings.RealInternet };
        ToolTip.SetTip(realNet, realNetTip);
        var realNetNote = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Brushes.Goldenrod,
            Text = "Privacidade: com a internet real ligada, o jogo envia dados (seu IP, o ID online, pontuações e salvamentos " +
                   "na nuvem) para os servidores dele e para o servidor RPCN configurado abaixo. Desligada (padrão), nada sai do PC.",
        };
        var rpcnServer = new TextBox { Text = settings.RpcnServer, Width = 260, Watermark = "ex.: np.rpcs3.net" };
        var rpcnUser = new TextBox { Text = settings.RpcnUser, Width = 200, MaxLength = 16 };
        var rpcnPass = new TextBox { Text = settings.RpcnPassword, Width = 200, PasswordChar = '•' };
        var rpcnToken = new TextBox { Text = settings.RpcnToken, Width = 200, Watermark = "só no primeiro login" };
        const string rpcnTip = "Servidor RPCN (o servidor aberto do RPCS3) para placares e salvamento na nuvem (TUS) com " +
                               "outros jogadores. Vazio: os serviços PSN locais. A senha fica gravada neste PC (launcher.json).";
        ToolTip.SetTip(rpcnServer, rpcnTip);
        void SyncPsn()
        {
            psnId.IsEnabled = psnLocal.IsEnabled = psnSignedIn.IsChecked == true;
            rpcnServer.IsEnabled = rpcnUser.IsEnabled = rpcnPass.IsEnabled = rpcnToken.IsEnabled = realNet.IsChecked == true && psnSignedIn.IsChecked == true;
        }
        psnSignedIn.IsCheckedChanged += (_, _) => SyncPsn();
        realNet.IsCheckedChanged += (_, _) => SyncPsn();
        SyncPsn();
        var general = new StackPanel { Spacing = 12, Margin = new Thickness(4, 12), MaxWidth = 560, HorizontalAlignment = HorizontalAlignment.Left };
        general.Children.Add(fullscreen);
        general.Children.Add(Row("Tamanho", scale));
        general.Children.Add(vsync);
        general.Children.Add(validation);
        general.Children.Add(Row("Câmera", camera));
        general.Children.Add(Row("CPU", cpu));
        general.Children.Add(Row("Clock da CPU", clock));
        // library view and the home screen ("Tela inicial": bubbles, LiveArea, theme, BGM and sounds of the firmware)
        general.Children.Add(new TextBlock { Text = "Tela inicial", FontSize = 15, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 8, 0, 0) });
        var view = new ComboBox { ItemsSource = new[] { "Lista", "Tela inicial recriada (provisória)" }, Width = 320, SelectedIndex = settings.View == LauncherSettings.ViewHome ? 1 : 0 };
        general.Children.Add(Row("Exibição", view));
        var shellAssets = new VitaSharp.Core.Shell.ShellAssets(Program.FsRoot);
        var themes = shellAssets.Themes(VitaSharp.Core.Shell.LiveAreaTemplate.VitaLanguage(System.Globalization.CultureInfo.CurrentUICulture));
        var theme = new ComboBox
        {
            ItemsSource = themes.Select(t => t.Title).ToArray(), Width = 320,
            SelectedIndex = Math.Max(0, themes.FindIndex(t => t.Id == settings.Theme)),
        };
        // a choice only when there is more than one theme (the firmware's default plus themes installed in ux0:theme)
        if (themes.Count > 1) general.Children.Add(Row("Tema", theme));
        var homeBgm = new CheckBox { Content = "Música do sistema na tela inicial", IsChecked = settings.HomeBgm, IsEnabled = themes.Any(t => t.BgmPath != null) || shellAssets.SystemBgm != null };
        general.Children.Add(WithHint(homeBgm, homeBgm.IsEnabled ? "Toca a música do tema (pd0:data/systembgm/home.at9 no tema padrão)."
                                                                   : "Requer um firmware com a partição pd0 (por exemplo, importado do Vita3K) ou um tema com música."));
        var systemSounds = new CheckBox { Content = "Efeitos sonoros do sistema", IsChecked = settings.SystemSounds, IsEnabled = shellAssets.HasFirmware };
        general.Children.Add(WithHint(systemSounds, "Sons de confirmar e voltar do Vita (common_resource.rco do firmware)."));
        if (!shellAssets.HasFirmware)
            general.Children.Add(new TextBlock
            {
                Foreground = LauncherTheme.Muted, TextWrapping = TextWrapping.Wrap, FontSize = 12,
                Text = "Sem firmware instalado a tela inicial usa um visual padrão. Instale o firmware (abaixo) para o papel de parede, ícones e sons do Vita.",
            });
        general.Children.Add(new TextBlock { Text = "Desempenho", FontSize = 15, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 8, 0, 0) });
        general.Children.Add(WithHint(fastShaders, fastShadersTip));
        general.Children.Add(WithHint(asyncShaders, asyncShadersTip));
        general.Children.Add(new TextBlock { Text = "Rede e PSN", FontSize = 15, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 8, 0, 0) });
        general.Children.Add(WithHint(psnSignedIn, psnTip));
        general.Children.Add(Row("ID online", psnId));
        general.Children.Add(psnIdError);
        general.Children.Add(WithHint(psnLocal, psnLocalTip));
        general.Children.Add(WithHint(realNet, realNetTip));
        general.Children.Add(realNetNote);
        general.Children.Add(Row("Servidor RPCN", rpcnServer));
        general.Children.Add(Row("Usuário RPCN", rpcnUser));
        general.Children.Add(Row("Senha RPCN", rpcnPass));
        general.Children.Add(Row("Token RPCN", rpcnToken));
        // firmware: install the official PUP (or copy Vita3K's), and the libraries to keep on HLE (--hle-modules=)
        general.Children.Add(new TextBlock { Text = "Firmware", FontSize = 15, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 8, 0, 0) });
        var firmware = Firmware = new FirmwarePanel();
        general.Children.Add(firmware);
        var hleModules = new TextBox { Text = settings.HleModules, Width = 320, Watermark = "ex.: libSceJson, libult" };
        general.Children.Add(Row("Manter em HLE", hleModules));
        general.Children.Add(new TextBlock
        {
            Foreground = LauncherTheme.Muted, TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(100, 0, 0, 0),
            Text = "Bibliotecas que continuam na implementação do VitaSharp mesmo quando o jogo ou o firmware traz o módulo " +
                   "(para contornar um problema com a versão original). Vazio: usa os módulos disponíveis.",
        });
        Closing += (_, e) =>
        {
            if (!firmware.Busy) return;
            // cancel the installation and close once it has cleaned up its staging folder
            e.Cancel = true;
            firmware.Finished += Close;
            firmware.CancelRunning();
        };
        general.Children.Add(new TextBlock
        {
            Foreground = LauncherTheme.Muted, TextWrapping = TextWrapping.Wrap,
            Text = "Teclado, controles e toque são configurados na aba Controles. F11 alterna a tela cheia no jogo. " +
                   "F10 captura um diagnóstico (quadro, texturas, captura de tela e log) em um .zip na pasta de diagnósticos. " +
                   "F9 liga ou desliga o modo de depuração no meio do jogo. " +
                   "Mouse: botão esquerdo = tela frontal, botão direito = touchpad traseiro.",
        });
        var debugMode = new CheckBox { Content = "Modo de depuração (gera logs detalhados para diagnóstico)", IsChecked = settings.DebugMode };
        general.Children.Add(WithHint(debugMode, "Registra no log o primeiro uso de shaders, alvos e formatos, erros de HLE e travamentos, " +
                                                 "e salva um diagnóstico automático quando a imagem parece corrompida. Pode custar alguns por cento de desempenho."));
        var openDiag = new Button { Content = "Abrir pasta de diagnósticos", HorizontalAlignment = HorizontalAlignment.Left };
        openDiag.Click += (_, _) =>
        {
            try
            {
                Directory.CreateDirectory(Diagnostics.Root);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Diagnostics.Root) { UseShellExecute = true });
            }
            catch (Exception) { }
        };
        general.Children.Add(openDiag);
        general.Children.Add(new TextBlock { Text = "Pasta de dados: " + Program.DataRoot, Foreground = LauncherTheme.Muted, TextWrapping = TextWrapping.Wrap });

        var controlsHost = new ContentControl();
        _tabs = new TabControl
        {
            Items =
            {
                new TabItem { Header = "Geral", Content = new ScrollViewer { Content = general } },
                new TabItem { Header = "Controles", Content = controlsHost },
            },
        };
        _tabs.SelectionChanged += (_, _) =>
        {
            if (_tabs.SelectedIndex == ControlsTab && Controls == null)
                controlsHost.Content = Controls = new ControllerSettingsPanel(Program.DataRoot, pads) { Margin = new Thickness(4, 12, 4, 0) };
        };

        var ok = new Button { Content = "Salvar", Background = LauncherTheme.Accent, Foreground = Brushes.White, IsDefault = true };
        var cancel = new Button { Content = "Cancelar" };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);

        var root = new DockPanel { Margin = new Thickness(20, 16, 20, 20) };
        var title = new TextBlock { Text = "Configurações", FontSize = 18, FontWeight = FontWeight.SemiBold };
        DockPanel.SetDock(title, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(title);
        root.Children.Add(buttons);
        root.Children.Add(_tabs);
        Content = root;

        ok.Click += (_, _) =>
        {
            string id = (psnId.Text ?? "").Trim();
            if (id.Length == 0) id = VitaSharp.Core.Hle.PsnAccount.DefaultOnlineId;
            psnIdError.IsVisible = !VitaSharp.Core.Hle.PsnAccount.IsValidOnlineId(id);
            if (psnIdError.IsVisible) { _tabs.SelectedIndex = GeneralTab; return; }
            settings.PsnSignedIn = psnSignedIn.IsChecked == true;
            settings.PsnOnlineId = id;
            settings.PsnLocalServices = psnLocal.IsChecked == true;
            settings.RealInternet = realNet.IsChecked == true;
            settings.RpcnServer = (rpcnServer.Text ?? "").Trim();
            settings.RpcnUser = (rpcnUser.Text ?? "").Trim();
            settings.RpcnPassword = rpcnPass.Text ?? "";
            settings.RpcnToken = (rpcnToken.Text ?? "").Trim();
            settings.Fullscreen = fullscreen.IsChecked == true;
            settings.VSync = vsync.IsChecked == true;
            settings.Validation = validation.IsChecked == true;
            settings.Scale = scale.SelectedIndex + 1;
            settings.Camera = camera.SelectedIndex >= 0 ? cameraValues[camera.SelectedIndex] : "";
            settings.CpuBackend = cpu.SelectedIndex >= 0 ? cpuValues[cpu.SelectedIndex] : "";
            settings.CpuMhz = clock.SelectedIndex >= 0 ? clockValues[clock.SelectedIndex] : 0;
            settings.FastShaderCompile = fastShaders.IsChecked == true;
            settings.BackgroundShaderCompile = asyncShaders.IsChecked == true;
            settings.DebugMode = debugMode.IsChecked == true;
            settings.HleModules = (hleModules.Text ?? "").Trim();
            settings.View = view.SelectedIndex == 1 ? LauncherSettings.ViewHome : LauncherSettings.ViewList;
            if (themes.Count > 0 && theme.SelectedIndex >= 0) settings.Theme = themes[theme.SelectedIndex].Id == VitaSharp.Core.Shell.ShellTheme.DefaultId ? "" : themes[theme.SelectedIndex].Id;
            settings.HomeBgm = homeBgm.IsChecked == true;
            settings.SystemSounds = systemSounds.IsChecked == true;
            settings.Save();
            Controls?.Save();
            Close();
        };
        cancel.Click += (_, _) => Close();
        _tabs.SelectedIndex = initialTab;
    }

    public void ShowTab(int index) => _tabs.SelectedIndex = index;

    /// <summary>A check box with a short muted description under it.</summary>
    static Control WithHint(Control c, string hint)
    {
        var p = new StackPanel { Spacing = 2 };
        p.Children.Add(c);
        p.Children.Add(new TextBlock { Text = hint, Foreground = LauncherTheme.Muted, TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(28, 0, 0, 0) });
        return p;
    }

    static Control Row(string label, Control c)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        p.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Width = 90 });
        p.Children.Add(c);
        return p;
    }
}
