using MauiReactor;
using VitaSharp.Android.Ui;

namespace VitaSharp.Android.Pages;

public enum RootTab { Library, Controls, Performance, Settings }

public sealed class RootState
{
    public RootTab Tab { get; set; } = RootTab.Library;
}

/// <summary>
/// The app's root: a navigation page whose first page holds the four tabs of the bottom bar (Biblioteca, Controles,
/// Desempenho, Ajustes); detail screens (Detalhes, Troféus, Firmware, ...) are pushed on top of it.
/// </summary>
public sealed class RootPage : Screen<RootState>
{
    static event Action<RootTab>? TabRequested;

    /// <summary>Switches the bottom bar to <paramref name="tab"/> (the gear button of the Biblioteca).</summary>
    public static void Show(RootTab tab) => TabRequested?.Invoke(tab);

    protected override void OnMounted()
    {
        TabRequested += OnTabRequested;
        base.OnMounted();
    }

    protected override void OnWillUnmount()
    {
        TabRequested -= OnTabRequested;
        base.OnWillUnmount();
    }

    void OnTabRequested(RootTab tab) => SetState(s => s.Tab = tab);

    public override VisualNode Render() =>
        NavigationPage(
            ContentPage(
                Grid("*,Auto", "*",
                    State.Tab switch
                    {
                        RootTab.Controls => new ControlsView(),
                        RootTab.Performance => new PerformanceView(),
                        RootTab.Settings => new SettingsView(),
                        _ => new LibraryView(),
                    },
                    BottomBar().GridRow(1))
            ).HasNavigationBar(false).BackgroundColor(Palette.Background)
        ).BarBackgroundColor(Palette.Background).BarTextColor(Palette.Text);

    VisualNode BottomBar() =>
        Grid("Auto", "*,*,*,*",
            TabButton(RootTab.Library, "▦", "Biblioteca", 0),
            TabButton(RootTab.Controls, "◎", "Controles", 1),
            TabButton(RootTab.Performance, "∿", "Desempenho", 2),
            TabButton(RootTab.Settings, "≡", "Ajustes", 3)
        ).BackgroundColor(Palette.Background).Padding(0, 8, 0, 12);

    VisualNode TabButton(RootTab tab, string glyph, string label, int column)
    {
        var color = State.Tab == tab ? Palette.Accent : Palette.Muted;
        return VStack(
                Label(glyph).FontSize(22).TextColor(color).HCenter(),
                Label(label).FontFamily(Fonts.InterMedium).FontSize(12).TextColor(color).HCenter())
            .Spacing(2).GridColumn(column)
            .OnTapped(() => SetState(s => s.Tab = tab));
    }
}
