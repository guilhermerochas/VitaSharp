using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using VitaSharp.Desktop.Input;

namespace VitaSharp.Desktop.Launcher;

/// <summary>Settings window: "Geral" (window, graphics, camera, CPU, Desempenho = shader compilation, Rede e PSN = emulated
/// PSN account) and "Controles" (keyboard / controller mapping).</summary>
public sealed class SettingsDialog : Window
{
    public const int GeneralTab = 0, ControlsTab = 1;

    readonly TabControl _tabs;

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
        // backends this build/host cannot run (the IL JIT in Native AOT builds, the x64 JIT on ARM64) are not offered;
        // a saved choice that became unavailable shows as Automatic
        var cpuChoices = new (string Value, string Label)[]
        {
            ("", "Automático (o melhor para este PC)"),
            ("native", "JIT nativo (código de máquina x64)"),
            ("il", "JIT IL (.NET DynamicMethod)"),
            ("interp", "Interpretador (mais lento, depuração)"),
        }.Where(c => c.Value is "" or "interp" || VitaSharp.Core.Cpu.Jit.All.Any(b => b.Name == c.Value && b.IsSupported)).ToArray();
        string[] cpuValues = cpuChoices.Select(c => c.Value).ToArray();
        var cpu = new ComboBox
        {
            ItemsSource = cpuChoices.Select(c => c.Label).ToArray(),
            Width = 320, SelectedIndex = Math.Max(0, Array.FindIndex(cpuValues, v => v.Equals(settings.CpuBackend, StringComparison.OrdinalIgnoreCase))),
        };
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
                              "localmente: a verificação de login passa, e recursos online (placares, salvamento na nuvem, " +
                              "multijogador) falham como se o servidor estivesse fora do ar. Desligado, o console fica sem rede e sem conta.";
        var psnSignedIn = new CheckBox { Content = "Simular conta PSN conectada (offline)", IsChecked = settings.PsnSignedIn };
        ToolTip.SetTip(psnSignedIn, psnTip);
        var psnId = new TextBox { Text = settings.PsnOnlineId, Width = 200, MaxLength = 16, Watermark = VitaSharp.Core.Hle.PsnAccount.DefaultOnlineId };
        var psnIdError = new TextBlock
        {
            Text = "ID online inválido: use de 3 a 16 letras, números, '-' ou '_', começando com uma letra.",
            Foreground = Brushes.IndianRed, TextWrapping = TextWrapping.Wrap, FontSize = 12, IsVisible = false,
        };
        void SyncPsn() => psnId.IsEnabled = psnSignedIn.IsChecked == true;
        psnSignedIn.IsCheckedChanged += (_, _) => SyncPsn();
        SyncPsn();
        var general = new StackPanel { Spacing = 12, Margin = new Thickness(4, 12), MaxWidth = 560, HorizontalAlignment = HorizontalAlignment.Left };
        general.Children.Add(fullscreen);
        general.Children.Add(Row("Tamanho", scale));
        general.Children.Add(vsync);
        general.Children.Add(validation);
        general.Children.Add(Row("Câmera", camera));
        general.Children.Add(Row("CPU", cpu));
        general.Children.Add(new TextBlock { Text = "Desempenho", FontSize = 15, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 8, 0, 0) });
        general.Children.Add(WithHint(fastShaders, fastShadersTip));
        general.Children.Add(WithHint(asyncShaders, asyncShadersTip));
        general.Children.Add(new TextBlock { Text = "Rede e PSN", FontSize = 15, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 8, 0, 0) });
        general.Children.Add(WithHint(psnSignedIn, psnTip));
        general.Children.Add(Row("ID online", psnId));
        general.Children.Add(psnIdError);
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
            settings.Fullscreen = fullscreen.IsChecked == true;
            settings.VSync = vsync.IsChecked == true;
            settings.Validation = validation.IsChecked == true;
            settings.Scale = scale.SelectedIndex + 1;
            settings.Camera = camera.SelectedIndex >= 0 ? cameraValues[camera.SelectedIndex] : "";
            settings.CpuBackend = cpu.SelectedIndex >= 0 ? cpuValues[cpu.SelectedIndex] : "";
            settings.FastShaderCompile = fastShaders.IsChecked == true;
            settings.BackgroundShaderCompile = asyncShaders.IsChecked == true;
            settings.DebugMode = debugMode.IsChecked == true;
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
