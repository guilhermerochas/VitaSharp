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
    readonly CheckBox _swap, _triggers, _touchpad, _invertL, _invertR;
    readonly Slider _dzL, _dzR, _sensL, _sensR;
    readonly TextBlock _dzLText, _dzRText, _sensLText, _sensRText;
    readonly Border _gamepadOptions;
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
        var reset = new Button { Content = "Reset to default", VerticalAlignment = VerticalAlignment.Center };
        reset.Click += (_, _) => ResetProfile();
        _deviceInfo = new TextBlock { Foreground = LauncherTheme.Muted, TextWrapping = TextWrapping.Wrap, FontSize = 12 };

        var deviceRow = new DockPanel { LastChildFill = true };
        var deviceLabel = new TextBlock { Text = "Device", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
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
            Text = "Click a control (or a row on the right), then press a button, move a stick or press a key. Esc cancels. " +
                   "Clicking a stick binds its four directions in turn. Right-click the screen or the rear pad to place the tap point of the touch controls.",
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
        _swap = new CheckBox { Content = "Swap Cross and Circle (Circle confirms)" };
        _swap.IsCheckedChanged += (_, _) => Edit(p => p.SwapConfirm = _swap.IsChecked == true);
        _triggers = new CheckBox { Content = "Analog triggers also press L / R" };
        _triggers.IsCheckedChanged += (_, _) => Edit(p => DefaultMappings.SetTriggersAsShoulders(p, _triggers.IsChecked == true));
        _touchpad = new CheckBox { Content = "Touch pad = front touch (hold L2: rear, R2: both)" };
        _touchpad.IsCheckedChanged += (_, _) => Edit(p => p.TouchpadEnabled = _touchpad.IsChecked == true);
        var gp = new StackPanel { Spacing = 6 };
        gp.Children.Add(_triggers);
        gp.Children.Add(_touchpad);
        _gamepadOptions = new Border { Child = gp };

        (_dzL, _dzLText) = SliderRow(0, 0.5, v => Edit(p => p.LeftStick.Deadzone = (float)v), v => $"{v:P0}");
        (_sensL, _sensLText) = SliderRow(0.5, 2, v => Edit(p => p.LeftStick.Sensitivity = (float)v), v => $"{v:0.00}x");
        (_dzR, _dzRText) = SliderRow(0, 0.5, v => Edit(p => p.RightStick.Deadzone = (float)v), v => $"{v:P0}");
        (_sensR, _sensRText) = SliderRow(0.5, 2, v => Edit(p => p.RightStick.Sensitivity = (float)v), v => $"{v:0.00}x");
        _invertL = new CheckBox { Content = "Invert Y" };
        _invertL.IsCheckedChanged += (_, _) => Edit(p => p.LeftStick.InvertY = _invertL.IsChecked == true);
        _invertR = new CheckBox { Content = "Invert Y" };
        _invertR.IsCheckedChanged += (_, _) => Edit(p => p.RightStick.InvertY = _invertR.IsChecked == true);

        var options = new StackPanel { Spacing = 8 };
        options.Children.Add(Header("Options"));
        options.Children.Add(_swap);
        options.Children.Add(_gamepadOptions);
        options.Children.Add(Header("Left stick"));
        options.Children.Add(Row("Deadzone", _dzL, _dzLText));
        options.Children.Add(Row("Sensitivity", _sensL, _sensLText));
        options.Children.Add(_invertL);
        options.Children.Add(Header("Right stick"));
        options.Children.Add(Row("Deadzone", _dzR, _dzRText));
        options.Children.Add(Row("Sensitivity", _sensR, _sensRText));
        options.Children.Add(_invertR);
        options.Children.Add(Header("Bindings"));
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
        if (_pads == null) _status.Text = "Controller support is unavailable (SDL could not be loaded): only the keyboard can be configured.";
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
        var items = new List<DeviceItem> { new(ControllerProfile.KeyboardId, "Keyboard", null, ControllerKind.Keyboard, false) };
        var pads = _pads?.Devices ?? [];
        PadInfo? fresh = null;
        foreach (var p in pads)
        {
            items.Add(new DeviceItem(p.Guid, $"{p.Name} — {ControllerClassifier.KindLabel(p.Kind)}", p, p.Kind, p.IsGamepad));
            if (_knownPads.Add(p.InstanceId) && !_firstSync) fresh = p;
        }
        _knownPads.IntersectWith(pads.Select(p => p.InstanceId));
        // saved profiles of controllers that are not connected stay editable
        foreach (var saved in _store.Config.Controllers)
            if (!items.Any(i => i.Id.Equals(saved.Id, StringComparison.OrdinalIgnoreCase)))
                items.Add(new DeviceItem(saved.Id, $"{(saved.Name.Length > 0 ? saved.Name : saved.Id)} (not connected)", null, saved.Kind, saved.Bindings.Values.SelectMany(v => v).Any(b => b.StartsWith("pad:"))));

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
        if (fresh != null) _status.Text = $"Connected: {fresh.Name}. {MappingSource(fresh)}";
    }

    string MappingSource(PadInfo p)
    {
        bool saved = _store.FindSaved(p.Guid, p.Name) != null;
        string db = p.IsGamepad ? "Recognised by the SDL controller database (standard layout)" : "Not in the controller database: raw joystick layout";
        return $"{db}; {(saved ? "using your saved profile" : "using the default mapping")}.";
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
        _deviceInfo.Text = d.IsKeyboard
            ? "Keyboard of the game window. Mouse: left button = front touch screen, right button = rear touch pad."
            : d.Pad is { } info
                ? $"{ControllerClassifier.KindLabel(info.Kind)} · {info.Model} · {(info.IsGamepad ? "SDL database mapping" : "raw joystick")}{(info.HasTouchpad ? " · touch pad" : "")} · GUID {info.Guid}\n{MappingSource(info)}"
                : $"Saved profile · GUID {d.Id} · connect the controller to test it.";
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
        _status.Text = "Default mapping restored (saved when you press Save).";
        Changed?.Invoke();
    }

    void RefreshOptions()
    {
        _updatingUi = true;
        _swap.IsChecked = _profile.SwapConfirm;
        bool isPad = _current is { IsKeyboard: false };
        _gamepadOptions.IsVisible = isPad;
        _triggers.IsChecked = DefaultMappings.TriggersAsShoulders(_profile);
        _triggers.IsEnabled = _current?.IsGamepad == true;
        _touchpad.IsChecked = _profile.TouchpadEnabled;
        _touchpad.IsEnabled = _current?.Pad?.HasTouchpad != false; // unknown when disconnected
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
        var list = _profile.Get(c).Select(b => b.Kind == BindingKind.Key ? KeyNames.Pretty(b.Key ?? "") : b.Describe(kind)).ToList();
        return list.Count == 0 ? "—" : string.Join(", ", list);
    }

    void RefreshBindings()
    {
        _bindingRows.Children.Clear();
        foreach (var c in VitaControls.All)
        {
            bool listening = _listen == c;
            var name = new TextBlock { Text = VitaControls.Label(c), Width = 130, VerticalAlignment = VerticalAlignment.Center, FontSize = 13 };
            var value = new TextBlock
            {
                Text = listening ? (_listenMode == ListenMode.Add ? "add: press…" : "press…") : Describe(c),
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
            ToolTip.SetTip(row, "Click to rebind");
            row.Click += (_, _) => BeginListen(c, ListenMode.Replace);
            var add = new Button { Content = "+", Padding = new Thickness(8, 3), VerticalAlignment = VerticalAlignment.Center };
            ToolTip.SetTip(add, "Add another binding");
            add.Click += (_, _) => BeginListen(c, ListenMode.Add);
            var clear = new Button { Content = "×", Padding = new Thickness(8, 3), VerticalAlignment = VerticalAlignment.Center };
            ToolTip.SetTip(clear, "Remove all bindings");
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
            _status.Text = $"{VitaControls.Label(c)}: tap point set to {normalised.X:P0}, {normalised.Y:P0}.";
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
        _status.Text = $"{VitaControls.Label(c)}: {(_current?.IsKeyboard == true ? "press a key" : "press a button or move a stick / trigger")} (Esc cancels)…";
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
        _status.Text = $"{VitaControls.Label(c)} = {(b.Kind == BindingKind.Key ? KeyNames.Pretty(b.Key ?? "") : b.Describe(kind))}";
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
            if (e.Key == Key.Escape) { CancelListen(); _status.Text = "Cancelled."; e.Handled = true; return; }
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

    /// <summary>Runs one update immediately (tests render right after changing the state).</summary>
    public void Pump() => Tick();

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
            if (DateTime.UtcNow > _listenUntil) { CancelListen(); _status.Text = "No input detected."; }
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
        _layout.SetState(m, rawL, rawR, _profile.LeftStick.Deadzone, _profile.RightStick.Deadzone);

        if (_listen == null && _ticks % 6 == 0 && (m.State.Buttons != 0 || m.FrontCount + m.BackCount > 0))
            _status.Text = $"Pressed: {Pressed(m.State.Buttons)}{(m.FrontCount > 0 ? $"  · front touch {m.FrontCount}" : "")}{(m.BackCount > 0 ? $"  · rear touch {m.BackCount}" : "")}";
    }

    static string Pressed(VitaButtons b)
    {
        var names = new List<string>();
        foreach (var c in VitaControls.All)
        {
            var bit = VitaControls.Button(c);
            if (bit != 0 && (b & bit) != 0) names.Add(VitaControls.Label(c));
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
    }

    public bool HasChanges => _dirty.Count > 0 || _reset.Count > 0;
}
