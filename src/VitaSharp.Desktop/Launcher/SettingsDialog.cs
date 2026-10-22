using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using VitaSharp.Desktop.Input;

namespace VitaSharp.Desktop.Launcher;

/// <summary>Settings window: "General" (window, graphics, camera, CPU) and "Controls" (controller mapping).</summary>
public sealed class SettingsDialog : Window
{
    public const int GeneralTab = 0, ControlsTab = 1;

    readonly TabControl _tabs;

    /// <summary>The controller page (created when the tab is first shown, so SDL only starts when needed).</summary>
    public ControllerSettingsPanel? Controls { get; private set; }

    /// <param name="pads">Controller backend for the Controls tab (tests / previews); null = its own.</param>
    public SettingsDialog(LauncherSettings settings, int initialTab = GeneralTab, SdlGamepads? pads = null)
    {
        Title = "Settings";
        Width = 1120;
        Height = 760;
        MinWidth = 900;
        MinHeight = 600;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = LauncherTheme.WindowBackground;

        var fullscreen = new CheckBox { Content = "Start games in fullscreen (F11 toggles in game)", IsChecked = settings.Fullscreen };
        var vsync = new CheckBox { Content = "VSync", IsChecked = settings.VSync };
        var validation = new CheckBox { Content = "Vulkan validation layers (debugging, slow)", IsChecked = settings.Validation };
        var scale = new ComboBox { ItemsSource = new[] { "1x (960×544)", "2x (1920×1088)", "3x (2880×1632)" }, SelectedIndex = Math.Clamp(settings.Scale - 1, 0, 2), Width = 200 };
        // camera: "" = first device, "none" = disabled, otherwise a device name
        var cameraValues = new List<string> { "", "none" };
        var cameraLabels = new List<string> { "Default (first camera)", "None (grey image)" };
        foreach (var d in VitaSharp.Desktop.Host.FlashCapCamera.ListDevices()) { cameraValues.Add(d.Name); cameraLabels.Add(d.Name); }
        if (!cameraValues.Contains(settings.Camera, StringComparer.OrdinalIgnoreCase)) { cameraValues.Add(settings.Camera); cameraLabels.Add(settings.Camera + " (not connected)"); }
        var camera = new ComboBox
        {
            ItemsSource = cameraLabels, Width = 260,
            SelectedIndex = Math.Max(0, cameraValues.FindIndex(v => v.Equals(settings.Camera, StringComparison.OrdinalIgnoreCase))),
        };
        // CPU backend: "" = best supported on this PC, else --cpu=native|il|interp
        string[] cpuValues = ["", "native", "il", "interp"];
        var cpu = new ComboBox
        {
            ItemsSource = new[] { "Auto (best for this PC)", "Native JIT (x64 machine code)", "IL JIT (.NET DynamicMethod)", "Interpreter (slowest, debugging)" },
            Width = 260, SelectedIndex = Math.Max(0, Array.FindIndex(cpuValues, v => v.Equals(settings.CpuBackend, StringComparison.OrdinalIgnoreCase))),
        };
        var general = new StackPanel { Spacing = 12, Margin = new Thickness(4, 12), MaxWidth = 560, HorizontalAlignment = HorizontalAlignment.Left };
        general.Children.Add(fullscreen);
        general.Children.Add(Row("Window size", scale));
        general.Children.Add(vsync);
        general.Children.Add(validation);
        general.Children.Add(Row("Camera", camera));
        general.Children.Add(Row("CPU", cpu));
        general.Children.Add(new TextBlock
        {
            Foreground = LauncherTheme.Muted, TextWrapping = TextWrapping.Wrap,
            Text = "Keyboard, controllers and touch are configured in the Controls tab. F11 toggles fullscreen in game. " +
                   "Mouse: left button = front touch screen, right button = rear touch pad.",
        });
        general.Children.Add(new TextBlock { Text = "Data folder: " + Program.DataRoot, Foreground = LauncherTheme.Muted, TextWrapping = TextWrapping.Wrap });

        var controlsHost = new ContentControl();
        _tabs = new TabControl
        {
            Items =
            {
                new TabItem { Header = "General", Content = new ScrollViewer { Content = general } },
                new TabItem { Header = "Controls", Content = controlsHost },
            },
        };
        _tabs.SelectionChanged += (_, _) =>
        {
            if (_tabs.SelectedIndex == ControlsTab && Controls == null)
                controlsHost.Content = Controls = new ControllerSettingsPanel(Program.DataRoot, pads) { Margin = new Thickness(4, 12, 4, 0) };
        };

        var ok = new Button { Content = "Save", Background = LauncherTheme.Accent, Foreground = Brushes.White, IsDefault = true };
        var cancel = new Button { Content = "Cancel" };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);

        var root = new DockPanel { Margin = new Thickness(20, 16, 20, 20) };
        var title = new TextBlock { Text = "Settings", FontSize = 18, FontWeight = FontWeight.SemiBold };
        DockPanel.SetDock(title, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(title);
        root.Children.Add(buttons);
        root.Children.Add(_tabs);
        Content = root;

        ok.Click += (_, _) =>
        {
            settings.Fullscreen = fullscreen.IsChecked == true;
            settings.VSync = vsync.IsChecked == true;
            settings.Validation = validation.IsChecked == true;
            settings.Scale = scale.SelectedIndex + 1;
            settings.Camera = camera.SelectedIndex >= 0 ? cameraValues[camera.SelectedIndex] : "";
            settings.CpuBackend = cpu.SelectedIndex >= 0 ? cpuValues[cpu.SelectedIndex] : "";
            settings.Save();
            Controls?.Save();
            Close();
        };
        cancel.Click += (_, _) => Close();
        _tabs.SelectedIndex = initialTab;
    }

    static Control Row(string label, Control c)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        p.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Width = 90 });
        p.Children.Add(c);
        return p;
    }
}
