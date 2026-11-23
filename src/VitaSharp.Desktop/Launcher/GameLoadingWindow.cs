using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using VitaSharp.Core.Boot;
using VitaSharp.Desktop.Launcher.Ui;

namespace VitaSharp.Desktop.Launcher;

/// <summary>
/// The game's window from the click on: the launcher opens it at once, the size of the game window (960x544 times
/// --scale), before the game process does any of its heavy start-up (firmware, controllers, audio, Vulkan device,
/// shader cache, ARM code), and shows the <see cref="LoadingScreen"/> on it: the game's start-up picture
/// (sce_sys/pic0.png, what the Vita shows while a process loads), its icon and name, the current step with its bar
/// and, during the two waits that have one, "Cancelar". The game's own window waits hidden at the same place
/// (--loading-screen=launcher --window-pos) and appears when the game is ready; this one closes then. Closing it
/// (X) stops the game being started. A start that fails (the title cannot be loaded, its license is missing, the
/// process ends with an error) leaves the reason on this window, with where the log is, and "Fechar".
/// </summary>
public sealed class GameLoadingWindow : Window
{
    readonly Kit _k = new();
    readonly TextBlock _step;
    readonly ProgressBar _bar;
    readonly Button _cancel;
    readonly TextBlock _log;
    readonly Button _close;
    bool _closingByCode;

    public GameLoadingWindow(LoadingScreen screen, double width, double height)
    {
        Screen = screen;
        Title = Ui.Strings.LoadingWindowTitle(screen.Title);
        Width = width;
        Height = height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Kit.B(static p => p.Background);
        if (screen.IconPath is { } icon && File.Exists(icon))
        {
            try
            {
                Icon = new WindowIcon(icon);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
            {
                // an unreadable icon: the window keeps the launcher's
            }
        }

        _step = _k.Text(screen.StepText, 14, FontWeight.Medium, static p => p.Text);
        _step.HorizontalAlignment = HorizontalAlignment.Center;
        _step.Name = "LoadingStep";
        _bar = new ProgressBar { Minimum = 0, Maximum = 1, Width = 320, Height = 6, HorizontalAlignment = HorizontalAlignment.Center, Name = "LoadingBar" };
        _cancel = _k.Secondary(LoadingScreen.CancelText, null, () => CancelRequested?.Invoke(), 36);
        _cancel.HorizontalAlignment = HorizontalAlignment.Center;
        _cancel.Name = "LoadingCancel";
        // a failed start: the reason stays on the window with where the log is, and "Fechar"
        _step.TextWrapping = TextWrapping.Wrap;
        _step.TextAlignment = TextAlignment.Center;
        _step.MaxWidth = Math.Max(200, width - 160);
        _log = _k.Text("", 12, FontWeight.Normal, static p => p.Muted);
        _log.HorizontalAlignment = HorizontalAlignment.Center;
        _log.TextWrapping = TextWrapping.Wrap;
        _log.TextAlignment = TextAlignment.Center;
        _log.MaxWidth = _step.MaxWidth;
        _log.Name = "LoadingLog";
        _close = _k.Secondary(LoadingScreen.CloseText, null, Close, 36);
        _close.HorizontalAlignment = HorizontalAlignment.Center;
        _close.Name = "LoadingClose";

        var name = _k.Text(screen.Title, 22, FontWeight.SemiBold, static p => p.Text, heading: true);
        name.HorizontalAlignment = HorizontalAlignment.Center;
        name.MaxWidth = width - 80;
        name.Name = "LoadingTitle";
        var card = new StackPanel
        {
            Spacing = 14,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var picture = _k.Picture(screen.IconPath, 112, 112, 24, Initials(screen.Title), 224);
        picture.HorizontalAlignment = HorizontalAlignment.Center;
        card.Children.Add(picture);
        card.Children.Add(name);
        card.Children.Add(_step);
        card.Children.Add(_bar);
        card.Children.Add(_cancel);
        card.Children.Add(_log);
        card.Children.Add(_close);

        var layers = new Grid();
        if (Kit.LoadImage(screen.BackgroundPath, 960) is { } background)
        {
            // the start-up picture under a shade, so the texts read on any picture
            layers.Children.Add(new Border { Background = new ImageBrush(background) { Stretch = Stretch.UniformToFill } });
            layers.Children.Add(new Border { Background = Kit.Fixed(Colors.Black, 0.62) });
        }

        layers.Children.Add(new Border
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(36, 28),
            CornerRadius = new CornerRadius(22),
            Background = Kit.B(static p => p.Surface, 0.92),
            Child = card,
        });
        Content = layers;

        screen.Changed += Refresh;
        Refresh();
    }

    /// <summary>The model this window shows.</summary>
    public LoadingScreen Screen { get; }

    /// <summary>"Cancelar" was clicked (the launcher stops the wait that runs).</summary>
    public event Action? CancelRequested;

    /// <summary>The user closed the window (X, Alt+F4): the launcher stops the game being started.</summary>
    public event Action? ClosedByUser;

    /// <summary>What the window shows now (the headless tests).</summary>
    internal string StepTextShown => _step.Text ?? "";

    internal bool CancelShown => _cancel.IsVisible;

    internal double? BarShown => _bar.IsIndeterminate ? null : _bar.Value;

    internal bool BarVisible => _bar.IsVisible;

    internal string? LogTextShown => _log.IsVisible ? _log.Text : null;

    internal bool CloseShown => _close.IsVisible;

    /// <summary>A click on "Cancelar" (the headless tests).</summary>
    internal void ClickCancelForTests() => _cancel.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

    /// <summary>A click on "Fechar" of a failed start (the headless tests).</summary>
    internal void ClickCloseForTests() => _close.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

    /// <summary>Times the window was brought to the front (a second click on the game).</summary>
    internal int BroughtToFrontCount { get; private set; }

    /// <summary>A second click on the game while it loads: this window comes back to the front, restored if minimized.</summary>
    public void BringToFront()
    {
        BroughtToFrontCount++;
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
    }

    /// <summary>The game is ready or its process ended: the window goes without stopping anything.</summary>
    public void CloseByLauncher()
    {
        _closingByCode = true;
        Close();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        Screen.Changed -= Refresh;
        if (!_closingByCode)
        {
            ClosedByUser?.Invoke();
        }
    }

    void Refresh()
    {
        _step.Text = Screen.StepText;
        if (Screen.Fraction is { } fraction)
        {
            _bar.IsIndeterminate = false;
            _bar.Value = fraction;
        }
        else
        {
            _bar.IsIndeterminate = true;
        }

        _cancel.IsVisible = Screen.CanCancel;
        _bar.IsVisible = !Screen.IsFailed;
        _log.Text = Screen.FailureLogText ?? "";
        _log.IsVisible = Screen.FailureLogText != null;
        _close.IsVisible = Screen.IsFailed;
        if (Screen.IsFailed)
        {
            _step.Foreground = Kit.B(static p => p.Danger);
        }
    }

    static string Initials(string title)
    {
        var words = title.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length switch
        {
            0 => "",
            1 => words[0][..Math.Min(2, words[0].Length)].ToUpperInvariant(),
            _ => string.Concat(char.ToUpperInvariant(words[0][0]), char.ToUpperInvariant(words[1][0])),
        };
    }
}
