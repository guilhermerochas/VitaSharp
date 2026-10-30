using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using VitaSharp.Core.Host;
using VitaSharp.Core.Input;
using VitaSharp.Desktop.Input;

namespace VitaSharp.Desktop.Launcher;

/// <summary>
/// Settings > Controls: picks a device (keyboard or a detected controller), shows the live Vita layout and lets the
/// player rebind any control by clicking it and pressing a button / moving an axis / pressing a key. Profiles are
/// kept in memory until <see cref="Save"/> writes input.json.
/// </summary>
public sealed class ControllerSettingsPanel : UserControl
{
    sealed record DeviceItem(string Id, string Label, PadInfo? Pad, ControllerKind Kind, bool IsGamepad)
    {
        public bool IsKeyboard => Id == ControllerProfile.KeyboardId;
        public override string ToString() => Label;
    }

    enum ListenMode { Replace, Add }

    readonly InputProfileStore _store;
    readonly Dictionary<string, ControllerProfile> _working = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _dirty = new(StringComparer.OrdinalIgnoreCase), _reset = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _keys = new();
    readonly SdlGamepads? _pads;
    readonly bool _ownsPads;
    volatile bool _devicesChanged = true;
    readonly HashSet<int> _knownPads = new();
    bool _firstSync = true;

    readonly ComboBox _device;
    readonly TextBlock _deviceInfo, _status, _hint;
    readonly VitaLayoutView _layout;
    readonly StackPanel _bindingRows;
    readonly CheckBox _swap, _triggers, _touchpad, _invertL, _invertR, _motion;
    readonly ComboBox _motionOrientation;
    readonly TextBlock _motionLive;
    readonly Slider _dzL, _dzR, _sensL, _sensR;
    readonly TextBlock _dzLText, _dzRText, _sensLText, _sensRText;
    readonly Border _gamepadOptions;
    // keyboard: virtual motion (tilt keys + mouse look)
    readonly ComboBox _motionSource;
    readonly Slider _tiltAngle, _tiltSpeed, _mouseSens;
    readonly TextBlock _tiltAngleText, _tiltSpeedText, _mouseSensText, _tiltLive;
    readonly CheckBox _mouseLook, _mouseInvert;
    readonly Border _keyboardMotionOptions;
    readonly VirtualMotion _tiltPreview = new();
    readonly MotionSample[] _tiltScratch = new MotionSample[64];
    TextBlock? _motionRowsHeader;
    readonly System.Diagnostics.Stopwatch _tiltClock = System.Diagnostics.Stopwatch.StartNew();
    readonly DispatcherTimer _timer;
    int _ticks;
    bool _updatingUi;

    DeviceItem? _current;
    ControllerProfile _profile;
    CompiledProfile _compiled;
    readonly PadSnapshot _snap = new(), _baseline = new();
    VitaControl? _listen;
    ListenMode _listenMode;
    readonly Queue<VitaControl> _sequence = new();
    DateTime _listenUntil;

    /// <summary>Raised whenever a profile changes (the dialog's Save button saves them).</summary>
    public event Action? Changed;

    /// <param name="pads">Controller backend to use (tests / previews); null = start one for this panel.</param>
    public ControllerSettingsPanel(string dataRoot, SdlGamepads? pads = null)
    {
        _store = InputProfileStore.Load(HostInput.ConfigPath(dataRoot));
        if (pads == null)
        {
            pads = new SdlGamepads(dataRoot);
            _ownsPads = true;
        }
        _pads = pads.Available ? pads : null;
        if (_pads != null) _pads.DevicesChanged += () => _devicesChanged = true;
        _profile = _store.KeyboardProfile();
        _compiled = new CompiledProfile(_profile);

        _device = new ComboBox { MinWidth = 340, VerticalAlignment = VerticalAlignment.Center };
        _device.SelectionChanged += (_, _) => { if (!_updatingUi && _device.SelectedItem is DeviceItem d) Select(d); };
        var reset = new Button { Content = "Restaurar padrão", VerticalAlignment = VerticalAlignment.Center };
        reset.Click += (_, _) => ResetProfile();
        _deviceInfo = new TextBlock { Foreground = LauncherTheme.Muted, TextWrapping = TextWrapping.Wrap, FontSize = 12 };

        var deviceRow = new DockPanel { LastChildFill = true };
        var deviceLabel = new TextBlock { Text = "Dispositivo", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        DockPanel.SetDock(deviceLabel, Dock.Left);
        DockPanel.SetDock(_device, Dock.Left);
        DockPanel.SetDock(reset, Dock.Right);
        deviceRow.Children.Add(deviceLabel);
        deviceRow.Children.Add(_device);
        deviceRow.Children.Add(reset);
        deviceRow.Children.Add(new Border());

        _layout = new VitaLayoutView { MinHeight = 300 };
        _layout.ControlClicked += OnLayoutClicked;
        _status = new TextBlock { TextWrapping = TextWrapping.Wrap, MinHeight = 20 };
        _hint = new TextBlock
        {
            Foreground = LauncherTheme.Muted, TextWrapping = TextWrapping.Wrap, FontSize = 12,
            Text = "Clique num controle (ou numa linha à direita) e aperte um botão, mova um analógico ou aperte uma tecla. Esc cancela. " +
                   "Clicar num analógico mapeia as quatro direções em sequência. Botão direito na tela ou no touchpad traseiro posiciona o ponto de toque.",
        };

        var left = new DockPanel();
        var leftBottom = new StackPanel { Spacing = 6, Margin = new Thickness(0, 8, 0, 0) };
        leftBottom.Children.Add(_status);
        leftBottom.Children.Add(_hint);
        DockPanel.SetDock(leftBottom, Dock.Bottom);
        left.Children.Add(leftBottom);
        left.Children.Add(new Border { Background = LauncherTheme.Panel, CornerRadius = new CornerRadius(10), Padding = new Thickness(8), Child = _layout });

        // right side: bindings + options
        _bindingRows = new StackPanel { Spacing = 2 };
        _swap = new CheckBox { Content = "Trocar Cruz e Círculo (Círculo confirma)" };
        _swap.IsCheckedChanged += (_, _) => Edit(p => p.SwapConfirm = _swap.IsChecked == true);
        _triggers = new CheckBox { Content = "Gatilhos analógicos também acionam L / R" };
        _triggers.IsCheckedChanged += (_, _) => Edit(p => DefaultMappings.SetTriggersAsShoulders(p, _triggers.IsChecked == true));
        _touchpad = new CheckBox { Content = "Touchpad = toque frontal (L2: traseiro, R2: ambos)" };
        _touchpad.IsCheckedChanged += (_, _) => Edit(p => p.TouchpadEnabled = _touchpad.IsChecked == true);
        _motion = new CheckBox { Content = "Usar giroscópio do controle" };
        ToolTip.SetTip(_motion, "O giroscópio e o acelerômetro do controle (DualShock 4, DualSense, Switch Pro, Joy-Con) movem os sensores de movimento do Vita.");
        _motion.IsCheckedChanged += (_, _) => Edit(p => p.MotionEnabled = _motion.IsChecked == true);
        _motionOrientation = new ComboBox
        {
            ItemsSource = new[] { "Face do controle = tela do Vita", "Controle deitado = Vita em pé (realidade aumentada)" },
            HorizontalAlignment = HorizontalAlignment.Stretch, FontSize = 12,
        };
        ToolTip.SetTip(_motionOrientation, "Natural: segure o controle como seguraria o Vita. Vita em pé: com o controle na horizontal o Vita fica de pé à sua frente, câmera traseira apontando para a frente.");
        _motionOrientation.SelectionChanged += (_, _) =>
            Edit(p => p.MotionOrientation = _motionOrientation.SelectedIndex == 1 ? MotionOrientation.FlatIsUpright : MotionOrientation.Natural);
        _motionLive = new TextBlock { Foreground = LauncherTheme.Muted, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        var gp = new StackPanel { Spacing = 6 };
        gp.Children.Add(_triggers);
        gp.Children.Add(_touchpad);
        gp.Children.Add(_motion);
        gp.Children.Add(_motionOrientation);
        gp.Children.Add(_motionLive);
        _gamepadOptions = new Border { Child = gp };

        _motionSource = new ComboBox
        {
            ItemsSource = new[] { "Automático", "Controle", "Teclado e mouse", "Desligado" },
            Width = 170, VerticalAlignment = VerticalAlignment.Center, FontSize = 12,
        };
        ToolTip.SetTip(_motionSource, "Quem move os sensores de movimento do Vita (giroscópio e acelerômetro), para o emulador todo. " +
                                      "Automático: um controle com giroscópio quando conectado, senão as teclas de inclinação e o mouse.");
        _motionSource.SelectionChanged += (_, _) => Edit(p => p.MotionSource = (MotionSourceMode)Math.Max(0, _motionSource.SelectedIndex));
        (_tiltAngle, _tiltAngleText) = SliderRow(5, 85, v => Edit(p => p.Tilt.MaxAngle = (float)Math.Round(v)), v => $"{v:0}°");
        (_tiltSpeed, _tiltSpeedText) = SliderRow(0.25, 4, v => Edit(p => p.Tilt.Speed = (float)v), v => $"{v:0.00}x");
        (_mouseSens, _mouseSensText) = SliderRow(0.1, 4, v => Edit(p => p.Tilt.MouseSensitivity = (float)v), v => $"{v:0.00}x");
        _mouseLook = new CheckBox { Content = "Mouse move o Vita com o botão do meio pressionado" };
        ToolTip.SetTip(_mouseLook, "Segure o botão do meio (roda) e mova o mouse: na horizontal o Vita gira, na vertical inclina para frente / trás. Recentralizar volta ao neutro.");
        _mouseLook.IsCheckedChanged += (_, _) => Edit(p => p.Tilt.MouseLook = _mouseLook.IsChecked == true);
        _mouseInvert = new CheckBox { Content = "Inverter eixo Y do mouse" };
        _mouseInvert.IsCheckedChanged += (_, _) => Edit(p => p.Tilt.MouseInvertY = _mouseInvert.IsChecked == true);
        _tiltLive = new TextBlock { Foreground = LauncherTheme.Muted, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        var km = new StackPanel { Spacing = 6 };
        km.Children.Add(Header("Movimento (giroscópio)"));
        km.Children.Add(Row("Movimento", _motionSource, new TextBlock()));
        km.Children.Add(Row("Ângulo máx.", _tiltAngle, _tiltAngleText));
        km.Children.Add(Row("Velocidade", _tiltSpeed, _tiltSpeedText));
        km.Children.Add(_mouseLook);
        km.Children.Add(Row("Mouse", _mouseSens, _mouseSensText));
        km.Children.Add(_mouseInvert);
        km.Children.Add(_tiltLive);
        _keyboardMotionOptions = new Border { Child = km };

        (_dzL, _dzLText) = SliderRow(0, 0.5, v => Edit(p => p.LeftStick.Deadzone = (float)v), v => $"{v:P0}");
        (_sensL, _sensLText) = SliderRow(0.5, 2, v => Edit(p => p.LeftStick.Sensitivity = (float)v), v => $"{v:0.00}x");
        (_dzR, _dzRText) = SliderRow(0, 0.5, v => Edit(p => p.RightStick.Deadzone = (float)v), v => $"{v:P0}");
        (_sensR, _sensRText) = SliderRow(0.5, 2, v => Edit(p => p.RightStick.Sensitivity = (float)v), v => $"{v:0.00}x");
        _invertL = new CheckBox { Content = "Inverter eixo Y" };
        _invertL.IsCheckedChanged += (_, _) => Edit(p => p.LeftStick.InvertY = _invertL.IsChecked == true);
        _invertR = new CheckBox { Content = "Inverter eixo Y" };
        _invertR.IsCheckedChanged += (_, _) => Edit(p => p.RightStick.InvertY = _invertR.IsChecked == true);

        var options = new StackPanel { Spacing = 8 };
        options.Children.Add(Header("Opções"));
        options.Children.Add(_swap);
        options.Children.Add(_gamepadOptions);
        options.Children.Add(_keyboardMotionOptions);
        options.Children.Add(Header("Analógico esquerdo"));
        options.Children.Add(Row("Zona morta", _dzL, _dzLText));
        options.Children.Add(Row("Sensibilidade", _sensL, _sensLText));
        options.Children.Add(_invertL);
        options.Children.Add(Header("Analógico direito"));
        options.Children.Add(Row("Zona morta", _dzR, _dzRText));
        options.Children.Add(Row("Sensibilidade", _sensR, _sensRText));
        options.Children.Add(_invertR);
        options.Children.Add(Header("Mapeamento"));
        options.Children.Add(_bindingRows);

        var right = new Border
        {
            Background = LauncherTheme.Panel, CornerRadius = new CornerRadius(10), Padding = new Thickness(12), Width = 360, Margin = new Thickness(12, 0, 0, 0),
            Child = new ScrollViewer { Content = options, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled },
        };

        var body = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        DockPanel.SetDock(right, Dock.Right);
        body.Children.Add(right);
        body.Children.Add(left);

        var root = new DockPanel();
        var top = new StackPanel { Spacing = 6 };
        top.Children.Add(deviceRow);
        top.Children.Add(_deviceInfo);
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);
        root.Children.Add(body);
        Content = root;
        Focusable = true;

        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Input, (_, _) => Tick());
        SyncDevices();
        if (_pads == null) _status.Text = "Suporte a controles indisponível (não foi possível carregar o SDL2): só o teclado pode ser configurado.";
    }

    static TextBlock Header(string text) => new() { Text = text, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 8, 0, 0) };

    (Slider, TextBlock) SliderRow(double min, double max, Action<double> set, Func<double, string> fmt)
    {
        var s = new Slider { Minimum = min, Maximum = max, Width = 170, VerticalAlignment = VerticalAlignment.Center };
        var t = new TextBlock { Width = 48, VerticalAlignment = VerticalAlignment.Center, Foreground = LauncherTheme.Muted, TextAlignment = TextAlignment.Right };
        s.PropertyChanged += (_, e) =>
        {
            if (e.Property != RangeBase.ValueProperty) return;
            t.Text = fmt(s.Value);
            if (!_updatingUi) set(Math.Round(s.Value, 2));
        };
        return (s, t);
    }

    static Control Row(string label, Control slider, Control value)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        p.Children.Add(new TextBlock { Text = label, Width = 80, VerticalAlignment = VerticalAlignment.Center });
        p.Children.Add(slider);
        p.Children.Add(value);
        return p;
    }

    // ------------------------------------------------------------------ lifecycle

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (TopLevel.GetTopLevel(this) is { } top)
        {
            top.AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
            top.AddHandler(KeyUpEvent, OnKeyUp, RoutingStrategies.Tunnel);
            if (top is Window w) w.Deactivated += OnDeactivated;
        }
        _timer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _timer.Stop();
        if (TopLevel.GetTopLevel(this) is { } top)
        {
            top.RemoveHandler(KeyDownEvent, OnKeyDown);
            top.RemoveHandler(KeyUpEvent, OnKeyUp);
            if (top is Window w) w.Deactivated -= OnDeactivated;
        }
        if (_ownsPads) _pads?.Dispose();
        base.OnDetachedFromVisualTree(e);
    }

    void OnDeactivated(object? sender, EventArgs e) => _keys.Clear();

    // ------------------------------------------------------------------ devices

    void SyncDevices()
    {
        _devicesChanged = false;
        var items = new List<DeviceItem> { new(ControllerProfile.KeyboardId, "Teclado", null, ControllerKind.Keyboard, false) };
        var pads = _pads?.Devices ?? [];
        PadInfo? fresh = null;
        foreach (var p in pads)
        {
            items.Add(new DeviceItem(p.Guid, $"{p.Name} — {ControlNames.Kind(p.Kind)}", p, p.Kind, p.IsGamepad));
            if (_knownPads.Add(p.InstanceId) && !_firstSync) fresh = p;
        }
        _knownPads.IntersectWith(pads.Select(p => p.InstanceId));
        // saved profiles of controllers that are not connected stay editable
        foreach (var saved in _store.Config.Controllers)
            if (!items.Any(i => i.Id.Equals(saved.Id, StringComparison.OrdinalIgnoreCase)))
                items.Add(new DeviceItem(saved.Id, $"{(saved.Name.Length > 0 ? saved.Name : saved.Id)} (desconectado)", null, saved.Kind, saved.Bindings.Values.SelectMany(v => v).Any(b => b.StartsWith("pad:"))));

        // a controller that was just plugged in is selected (that is the automatic recognition the player sees);
        // on opening, the first connected controller is preferred over the keyboard
        string? want = fresh?.Guid ?? (_firstSync && pads.Count > 0 ? pads[0].Guid : _current?.Id);
        if (_current != null && fresh == null && !items.Any(i => i.Id == _current.Id)) want = ControllerProfile.KeyboardId;
        _firstSync = false;
        _updatingUi = true;
        _device.ItemsSource = items;
        var sel = items.FirstOrDefault(i => i.Id == want) ?? items[0];
        _device.SelectedItem = sel;
        _updatingUi = false;
        if (sel.Id != _current?.Id || sel.Pad?.InstanceId != _current?.Pad?.InstanceId) Select(sel);
        if (fresh != null) _status.Text = $"Conectado: {fresh.Name}. {MappingSource(fresh)}";
    }

    string MappingSource(PadInfo p)
    {
        string db = p.IsGamepad ? "Reconhecido pela base de controles do SDL (layout padrão)" : "Fora da base de controles: layout de joystick bruto";
        return $"{db}; {ProfileState(p.Guid, _store.FindSaved(p.Guid, p.Name) != null)}.";
    }

    string ProfileState(string id, bool saved)
        => _dirty.Contains(id) ? "perfil editado (não salvo)"
            : _reset.Contains(id) ? "mapeamento padrão (não salvo)"
            : saved ? "usando o seu perfil salvo" : "usando o mapeamento padrão";

    void RefreshDeviceInfo()
    {
        if (_current is not { } d) return;
        _deviceInfo.Text = d.IsKeyboard
            ? $"Teclado da janela do jogo ({ProfileState(d.Id, _store.Config.Keyboard != null)}). Mouse: botão esquerdo = tela frontal, botão direito = touchpad traseiro, botão do meio = mover o Vita (giroscópio)."
            : d.Pad is { } info
                ? $"{ControlNames.Kind(info.Kind)} · {ControlNames.Model(info.Model)} · {(info.IsGamepad ? "mapeamento da base SDL" : "joystick bruto")}{(info.HasTouchpad ? " · touchpad" : "")} · GUID {info.Guid}\n{MappingSource(info)}"
                : $"Perfil salvo · GUID {d.Id} · conecte o controle para testá-lo.";
    }

    void Select(DeviceItem d)
    {
        CancelListen();
        _current = d;
        if (!_working.TryGetValue(d.Id, out var p))
        {
            p = d.IsKeyboard ? _store.KeyboardProfile()
                : d.Pad is { } pad ? _store.ProfileFor(pad.Guid, pad.Name, pad.Kind, pad.IsGamepad)
                : _store.FindSaved(d.Id, "")?.Clone() ?? DefaultMappings.For(d.Id, d.Label, d.Kind, d.IsGamepad);
            // a profile found by name is stored under this controller's GUID from now on
            if (d.Pad is { } pp) { p.Id = pp.Guid; p.Name = pp.Name; p.Kind = pp.Kind; }
            _working[d.Id] = p;
        }
        _profile = p;
        _compiled = new CompiledProfile(p);
        _status.Text = "";
        RefreshDeviceInfo();
        RefreshOptions();
        RefreshBindings();
    }

    // ------------------------------------------------------------------ editing

    void Edit(Action<ControllerProfile> change)
    {
        if (_updatingUi) return;
        change(_profile);
        _compiled = new CompiledProfile(_profile);
        _dirty.Add(_profile.Id);
        _reset.Remove(_profile.Id);
        RefreshBindings();
        RefreshDeviceInfo();
        Changed?.Invoke();
    }

    void ResetProfile()
    {
        if (_current == null) return;
        CancelListen();
        var d = _current;
        var p = d.IsKeyboard ? DefaultMappings.Keyboard()
            : DefaultMappings.For(_profile.Id, _profile.Name, d.Pad?.Kind ?? d.Kind, d.Pad?.IsGamepad ?? d.IsGamepad);
        _working[d.Id] = _profile = p;
        _compiled = new CompiledProfile(p);
        _reset.Add(p.Id);
        _dirty.Remove(p.Id);
        RefreshOptions();
        RefreshBindings();
        RefreshDeviceInfo();
        _status.Text = "Mapeamento padrão restaurado (gravado ao clicar em Salvar).";
        Changed?.Invoke();
    }

    void RefreshOptions()
    {
        _updatingUi = true;
        _swap.IsChecked = _profile.SwapConfirm;
        bool isPad = _current is { IsKeyboard: false };
        _gamepadOptions.IsVisible = isPad;
        _keyboardMotionOptions.IsVisible = !isPad;
        _motionSource.SelectedIndex = (int)_profile.MotionSource;
        _tiltAngle.Value = _profile.Tilt.MaxAngle; _tiltSpeed.Value = _profile.Tilt.Speed; _mouseSens.Value = _profile.Tilt.MouseSensitivity;
        _tiltAngleText.Text = $"{_tiltAngle.Value:0}°"; _tiltSpeedText.Text = $"{_tiltSpeed.Value:0.00}x"; _mouseSensText.Text = $"{_mouseSens.Value:0.00}x";
        _mouseLook.IsChecked = _profile.Tilt.MouseLook;
        _mouseInvert.IsChecked = _profile.Tilt.MouseInvertY;
        _triggers.IsChecked = DefaultMappings.TriggersAsShoulders(_profile);
        _triggers.IsEnabled = _current?.IsGamepad == true;
        _touchpad.IsChecked = _profile.TouchpadEnabled;
        _touchpad.IsEnabled = _current?.Pad?.HasTouchpad != false; // unknown when disconnected
        _motion.IsChecked = _profile.MotionEnabled;
        _motion.IsEnabled = _motionOrientation.IsEnabled = _current?.Pad?.HasMotion != false;
        _motionOrientation.SelectedIndex = _profile.MotionOrientation == MotionOrientation.FlatIsUpright ? 1 : 0;
        _motionLive.Text = _current?.Pad is { HasMotion: false } ? "Este controle não tem giroscópio: o Vita fica parado (deitado, tela para cima)." : "";
        _dzL.Value = _profile.LeftStick.Deadzone; _dzR.Value = _profile.RightStick.Deadzone;
        _sensL.Value = _profile.LeftStick.Sensitivity; _sensR.Value = _profile.RightStick.Sensitivity;
        _invertL.IsChecked = _profile.LeftStick.InvertY; _invertR.IsChecked = _profile.RightStick.InvertY;
        _dzLText.Text = $"{_dzL.Value:P0}"; _dzRText.Text = $"{_dzR.Value:P0}";
        _sensLText.Text = $"{_sensL.Value:0.00}x"; _sensRText.Text = $"{_sensR.Value:0.00}x";
        _updatingUi = false;
    }

    string Describe(VitaControl c)
    {
        var kind = _current?.Kind ?? ControllerKind.Generic;
        var list = _profile.Get(c).Select(b => ControlNames.Describe(b, kind)).ToList();
        return list.Count == 0 ? "—" : string.Join(", ", list);
    }

    void RefreshBindings()
    {
        _bindingRows.Children.Clear();
        foreach (var c in VitaControls.All)
        {
            if (c == VitaControl.TiltForward)
            {
                var h = Header("Movimento (inclinar o Vita)");
                h.FontSize = 13;
                _motionRowsHeader = h;
                _bindingRows.Children.Add(h);
            }
            bool listening = _listen == c;
            var name = new TextBlock { Text = ControlNames.Label(c), Width = 140, VerticalAlignment = VerticalAlignment.Center, FontSize = 13 };
            var value = new TextBlock
            {
                Text = listening ? (_listenMode == ListenMode.Add ? "adicionar: aperte…" : "aperte…") : Describe(c),
                Foreground = listening ? new SolidColorBrush(Color.Parse("#FFB547")) : LauncherTheme.Muted,
                VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 13,
            };
            var rowContent = new DockPanel();
            DockPanel.SetDock(name, Dock.Left);
            rowContent.Children.Add(name);
            rowContent.Children.Add(value);
            var row = new Button
            {
                Content = rowContent, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(8, 3), Background = _layout.SelectedControl == c || listening ? new SolidColorBrush(Color.Parse("#2A3550")) : Brushes.Transparent,
            };
            ToolTip.SetTip(row, "Clique para remapear");
            row.Click += (_, _) => BeginListen(c, ListenMode.Replace);
            var add = new Button { Content = "+", Padding = new Thickness(8, 3), VerticalAlignment = VerticalAlignment.Center };
            ToolTip.SetTip(add, "Adicionar outro mapeamento");
            add.Click += (_, _) => BeginListen(c, ListenMode.Add);
            var clear = new Button { Content = "×", Padding = new Thickness(8, 3), VerticalAlignment = VerticalAlignment.Center };
            ToolTip.SetTip(clear, "Remover todos os mapeamentos");
            clear.Click += (_, _) => { CancelListen(); Edit(p => p.Set(c, null)); };
            var line = new DockPanel();
            DockPanel.SetDock(clear, Dock.Right);
            DockPanel.SetDock(add, Dock.Right);
            line.Children.Add(clear);
            line.Children.Add(add);
            line.Children.Add(row);
            _bindingRows.Children.Add(line);
        }
    }

    // ------------------------------------------------------------------ listening for a binding

    void OnLayoutClicked(VitaControl c, bool right, Point normalised)
    {
        if (right && c is VitaControl.TouchFront or VitaControl.TouchBack)
        {
            Edit(p =>
            {
                if (c == VitaControl.TouchFront) { p.TouchFrontX = (float)Math.Round(normalised.X, 3); p.TouchFrontY = (float)Math.Round(normalised.Y, 3); }
                else { p.TouchBackX = (float)Math.Round(normalised.X, 3); p.TouchBackY = (float)Math.Round(normalised.Y, 3); }
            });
            _status.Text = $"{ControlNames.Label(c)}: ponto de toque em {normalised.X:P0}, {normalised.Y:P0}.";
            return;
        }
        _sequence.Clear();
        if (c is VitaControl.LStickUp or VitaControl.RStickUp)
        {
            // a stick: bind up, down, left, right in turn
            for (var d = c + 1; d <= c + 3; d++) _sequence.Enqueue(d);
        }
        BeginListen(c, ListenMode.Replace, keepSequence: true);
    }

    /// <summary>Starts waiting for the input to bind to <paramref name="c"/>.</summary>
    public void BeginListen(VitaControl c, ListenModeArg mode = ListenModeArg.Replace) => BeginListen(c, mode == ListenModeArg.Add ? ListenMode.Add : ListenMode.Replace);

    public enum ListenModeArg { Replace, Add }

    void BeginListen(VitaControl c, ListenMode mode, bool keepSequence = false)
    {
        if (!keepSequence) _sequence.Clear();
        _listen = c;
        _listenMode = mode;
        _listenUntil = DateTime.UtcNow.AddSeconds(8);
        _layout.ListeningControl = c;
        _layout.SelectedControl = c;
        if (_current?.Pad is { } pad && _pads != null) _pads.TryGetState(pad.InstanceId, _baseline);
        else _baseline.Clear();
        _status.Text = $"{ControlNames.Label(c)}: {(_current?.IsKeyboard == true ? "aperte uma tecla" : "aperte um botão ou mova um analógico / gatilho")} (Esc cancela)…";
        RefreshBindings();
        Focus();
    }

    void CancelListen()
    {
        if (_listen == null) return;
        _listen = null;
        _sequence.Clear();
        _layout.ListeningControl = null;
        RefreshBindings();
    }

    void Bound(InputBinding b)
    {
        var c = _listen!.Value;
        var mode = _listenMode;
        _listen = null;
        _layout.ListeningControl = null;
        Edit(p => { if (mode == ListenMode.Add) p.Add(c, b); else p.Set(c, b); });
        var kind = _current?.Kind ?? ControllerKind.Generic;
        var shared = VitaControls.All.Where(o => o != c && _profile.Has(o, b)).Select(ControlNames.Label).ToList();
        _status.Text = $"{ControlNames.Label(c)} = {ControlNames.Describe(b, kind)}"
                       + (shared.Count > 0 ? $"  (também aciona: {string.Join(", ", shared)})" : "");
        if (_sequence.Count > 0)
        {
            // next direction of the stick: wait until the stick is back near the centre first
            var next = _sequence.Dequeue();
            DispatcherTimer.RunOnce(() => BeginListen(next, ListenMode.Replace, keepSequence: true), TimeSpan.FromMilliseconds(350));
        }
    }

    void OnKeyDown(object? sender, KeyEventArgs e)
    {
        var name = KeyNames.FromAvalonia(e.PhysicalKey, e.Key);
        if (_listen != null)
        {
            if (e.Key == Key.Escape) { CancelListen(); _status.Text = "Cancelado."; e.Handled = true; return; }
            if (_current?.IsKeyboard == true && name != null)
            {
                Bound(InputBinding.OfKey(name));
                e.Handled = true;
                return;
            }
        }
        if (name != null && _current?.IsKeyboard == true)
        {
            _keys.Add(name);
            // keep the arrow keys / space from moving the focus while the keyboard is being tested
            if (e.Source is not TextBox && e.Source is not Slider && e.Source is not ComboBox) e.Handled = true;
        }
    }

    void OnKeyUp(object? sender, KeyEventArgs e)
    {
        if (KeyNames.FromAvalonia(e.PhysicalKey, e.Key) is { } name) _keys.Remove(name);
    }

    /// <summary>Test hook: simulates a key of the game keyboard.</summary>
    public void SimulateKey(string silkKeyName, bool down) { if (down) _keys.Add(silkKeyName); else _keys.Remove(silkKeyName); }

    /// <summary>Test hook: selects the device with this id ("keyboard" or a GUID).</summary>
    public bool SelectDevice(string id)
    {
        if (_devicesChanged) SyncDevices();
        if (_device.ItemsSource is not IEnumerable<DeviceItem> items || items.FirstOrDefault(i => i.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) is not { } d) return false;
        _device.SelectedItem = d;
        return true;
    }

    /// <summary>Test hook: changes the current profile as the option controls would.</summary>
    public void EditProfile(Action<ControllerProfile> change)
    {
        Edit(change);
        RefreshOptions();
    }

    /// <summary>Runs one update immediately (tests render right after changing the state).</summary>
    public void Pump() => Tick();

    /// <summary>Test hook: scrolls the option list to the motion settings (<paramref name="rows"/> = to the tilt
    /// binding rows).</summary>
    public void ShowMotion(bool rows)
    {
        if (rows) { _bindingRows.Children[^1].BringIntoView(); _motionRowsHeader?.BringIntoView(); }
        else _keyboardMotionOptions.BringIntoView();
    }

    // ------------------------------------------------------------------ live update

    void Tick()
    {
        if (_devicesChanged) SyncDevices();
        if (++_ticks % 15 == 0) _layout.Blink();
        PadSnapshot? pad = null;
        if (_current?.Pad is { } info && _pads != null && _pads.TryGetState(info.InstanceId, _snap)) pad = _snap;
        var keys = _current?.IsKeyboard == true ? _keys : null;

        if (_listen != null)
        {
            if (DateTime.UtcNow > _listenUntil) { CancelListen(); _status.Text = "Nenhuma entrada detectada."; }
            else if (pad != null && InputCapture.Detect(_baseline, pad) is { } b)
            {
                // a pad binding of the other view (raw vs gamepad) would never fire: InputCapture follows the device
                Bound(b);
            }
        }

        var m = InputMapper.Map(_compiled, pad, keys);
        float V(VitaControl c) => InputMapper.Value(_compiled, c, pad, keys);
        var rawL = (V(VitaControl.LStickRight) - V(VitaControl.LStickLeft), V(VitaControl.LStickDown) - V(VitaControl.LStickUp));
        var rawR = (V(VitaControl.RStickRight) - V(VitaControl.RStickLeft), V(VitaControl.RStickDown) - V(VitaControl.RStickUp));
        if (_profile.LeftStick.InvertY) rawL.Item2 = -rawL.Item2;
        if (_profile.RightStick.InvertY) rawR.Item2 = -rawR.Item2;
        _layout.TapPointFront = _compiled[VitaControl.TouchFront].Length > 0 ? new Point(_profile.TouchFrontX, _profile.TouchFrontY) : null;
        _layout.TapPointBack = _compiled[VitaControl.TouchBack].Length > 0 ? new Point(_profile.TouchBackX, _profile.TouchBackY) : null;
        _layout.SetState(m, rawL, rawR, _profile.LeftStick.Deadzone, _profile.RightStick.Deadzone);

        if (_current?.IsKeyboard == true)
        {
            // the virtual motion as the game would get it from these keys
            _tiltPreview.Settings = _profile.Tilt;
            _tiltPreview.SetControls(_compiled, null, keys);
            long now = (long)(_tiltClock.Elapsed.TotalMilliseconds * 1000);
            while (_tiltPreview.Generate(now, _tiltScratch) == _tiltScratch.Length) { }
            if (_ticks % 6 == 0)
            {
                var (tp, tr, ty) = _tiltPreview.Angles;
                var g = VirtualMotion.Gravity(_tiltPreview.Pose);
                _tiltLive.Text = _profile.MotionSource is MotionSourceMode.Off or MotionSourceMode.Controller
                    ? "Teclado e mouse não movem o Vita com esta escolha."
                    : $"Vita: inclinação {tp:+0;-0;0}° · rolagem {tr:+0;-0;0}° · giro {ty:+0;-0;0}°\nacelerômetro {g.X:+0.00;-0.00} {g.Y:+0.00;-0.00} {g.Z:+0.00;-0.00} G";
            }
        }

        if (_ticks % 6 == 0 && pad is { HasMotion: true })
        {
            // live motion in the Vita's axes, as the game will see it
            var s = MotionAxes.FromGamepad(0, pad.Accel, pad.Gyro, _profile.MotionOrientation);
            var a = s.Acceleration;
            float pitch = MathF.Atan2(-a.Y, -a.Z) * 180 / MathF.PI, roll = MathF.Atan2(a.X, MathF.Sqrt(a.Y * a.Y + a.Z * a.Z)) * 180 / MathF.PI;
            _motionLive.Text = _profile.MotionEnabled
                ? $"Vita: inclinação {pitch:+0;-0;0}° · rolagem {roll:+0;-0;0}° · rotação {s.AngularVelocity.Length() * 180 / MathF.PI:0}°/s\n" +
                  $"acelerômetro {a.X:+0.00;-0.00} {a.Y:+0.00;-0.00} {a.Z:+0.00;-0.00} G"
                : "Giroscópio desligado: o Vita fica parado.";
        }

        if (_listen == null && _ticks % 6 == 0 && (m.State.Buttons != 0 || m.FrontCount + m.BackCount > 0))
            _status.Text = $"Pressionado: {Pressed(m.State.Buttons)}{(m.FrontCount > 0 ? $"  · toque frontal {m.FrontCount}" : "")}{(m.BackCount > 0 ? $"  · toque traseiro {m.BackCount}" : "")}";
    }

    static string Pressed(VitaButtons b)
    {
        var names = new List<string>();
        foreach (var c in VitaControls.All)
        {
            var bit = VitaControls.Button(c);
            if (bit != 0 && (b & bit) != 0) names.Add(ControlNames.Label(c));
        }
        return names.Count == 0 ? "—" : string.Join(", ", names);
    }

    // ------------------------------------------------------------------ saving

    /// <summary>Writes the edited profiles to input.json (devices reset to default lose their saved profile).</summary>
    public void Save()
    {
        CancelListen();
        if (_dirty.Count == 0 && _reset.Count == 0) return;
        foreach (var id in _reset) _store.Remove(id);
        foreach (var p in _working.Values)
            if (_dirty.Contains(p.Id)) _store.Put(p);
        _store.Save();
        _dirty.Clear();
        _reset.Clear();
        RefreshDeviceInfo();
    }

    public bool HasChanges => _dirty.Count > 0 || _reset.Count > 0;
}
