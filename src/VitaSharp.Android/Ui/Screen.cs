using MauiReactor;
using MauiReactor.Shapes;
using VitaSharp.Android.Settings;
using VitaSharp.Core.Util;

namespace VitaSharp.Android.Ui;

/// <summary>Props of a screen about one game.</summary>
public sealed class GameProps
{
    public string TitleId { get; set; } = "";
}

/// <summary>No props.</summary>
public sealed class NoProps;

/// <summary>
/// Base of every screen: the building blocks of the mockups (header with back button, cards, setting rows, chips,
/// progress bars, buttons) and the pickers, so the pages read as a description of the screen.
/// </summary>
public abstract class Screen<TState, TProps> : Component<TState, TProps>
    where TState : class, new()
    where TProps : class, new()
{
    // ------------------------------------------------------------------ page frame

    /// <summary>A full page: optional header (title, back arrow) and a scrolling body; <paramref name="overlay"/> (already
    /// placed in grid row 1) floats over the body.</summary>
    protected VisualNode Page(string title, VisualNode body, bool back = true, string? kicker = null, VisualNode? overlay = null) =>
        ContentPage(PageBody(title, body, back, kicker, overlay)).HasNavigationBar(false).BackgroundColor(Palette.Background);

    /// <summary>The content of <see cref="Page"/> without the page (the tabs of the root page).</summary>
    protected VisualNode PageBody(string title, VisualNode body, bool back = false, string? kicker = null, VisualNode? overlay = null) =>
        Grid("Auto,*", "*",
            Header(title, back, kicker),
            VScrollView(VStack(body, BoxView().HeightRequest(24).Color(Colors.Transparent))).GridRow(1),
            overlay);

    protected MauiReactor.HStack Header(string title, bool back, string? kicker = null) =>
        HStack(
            back ? IconButton("‹", () => Navigation?.PopAsync()) : null,
            VStack(
                kicker != null ? Label(kicker.ToUpperInvariant()).FontSize(12).TextColor(Palette.Muted).CharacterSpacing(1) : null,
                Label(title).FontFamily(Fonts.SoraBold).FontSize(back ? 22 : 28).LineBreakMode(LineBreakMode.TailTruncation)
            ).Spacing(2).VCenter()
        ).Spacing(14).Padding(20, 20, 20, 8);

    protected MauiReactor.Border IconButton(string glyph, Action onTap, double size = 44) =>
        Border(Label(glyph).FontSize(22).TextColor(Palette.Text).Center())
            .StrokeShape(new RoundRectangle().CornerRadius(14)).Stroke(Palette.BorderStrong).StrokeThickness(1)
            .BackgroundColor(Palette.Surface).WidthRequest(size).HeightRequest(size)
            .OnTapped(onTap);

    // ------------------------------------------------------------------ blocks

    protected MauiReactor.Grid SectionTitle(string text, string? right = null) =>
        Grid("Auto", "*,Auto",
            Label(text).FontFamily(Fonts.SoraSemiBold).FontSize(16),
            right != null ? Label(right).FontSize(12).TextColor(Palette.Muted).GridColumn(1).VEnd() : null
        ).Margin(20, 18, 20, 8);

    protected MauiReactor.Border Card(params VisualNode?[] children) =>
        Border(VStack(children).Spacing(0))
            .StrokeShape(new RoundRectangle().CornerRadius(20)).Stroke(Palette.Border).StrokeThickness(1)
            .BackgroundColor(Palette.Surface).Margin(20, 0, 20, 12);

    protected MauiReactor.BoxView Divider() => BoxView().HeightRequest(1).Color(Palette.Border).Margin(16, 0);

    /// <summary>A setting row: label (and detail), value and a chevron when it opens something.</summary>
    protected MauiReactor.Grid Row(string label, string? value = null, Action? onTap = null, string? detail = null, Color? valueColor = null)
    {
        var row = Grid("Auto", "*,Auto,Auto",
            VStack(
                Label(label).FontFamily(Fonts.InterMedium).FontSize(15),
                detail != null ? Label(detail).FontSize(12).TextColor(Palette.Muted) : null
            ).Spacing(2).VCenter(),
            value != null ? Label(value).FontSize(14).TextColor(valueColor ?? Palette.Muted).VCenter().HEnd()
                .LineBreakMode(LineBreakMode.TailTruncation).MaximumWidthRequest(200).GridColumn(1).Margin(8, 0, 0, 0) : null,
            onTap != null ? Label("›").FontSize(20).TextColor(Palette.Faint).VCenter().GridColumn(2).Margin(10, 0, 0, 0) : null
        ).Padding(16, 14).MinimumHeightRequest(56);
        return onTap != null ? row.OnTapped(onTap) : row;
    }

    protected MauiReactor.Grid ToggleRow(string label, bool value, Action<bool> onChange, string? detail = null, bool enabled = true) =>
        Grid("Auto", "*,Auto",
            VStack(
                Label(label).FontFamily(Fonts.InterMedium).FontSize(15).TextColor(enabled ? Palette.Text : Palette.Faint),
                detail != null ? Label(detail).FontSize(12).TextColor(Palette.Muted) : null
            ).Spacing(2).VCenter(),
            Switch().IsToggled(value).IsEnabled(enabled).OnToggled((s, e) => onChange(e.Value)).GridColumn(1).VCenter()
        ).Padding(16, 10).MinimumHeightRequest(56);

    protected MauiReactor.Border Chip(string text, bool selected, Action onTap) =>
        Border(Label(text).FontFamily(selected ? Fonts.InterSemiBold : Fonts.InterMedium).FontSize(13)
                .TextColor(selected ? Palette.OnAccent : Palette.TextSoft).Center())
            .StrokeShape(new RoundRectangle().CornerRadius(999)).Stroke(selected ? Palette.Accent : Palette.BorderStrong).StrokeThickness(1)
            .BackgroundColor(selected ? Palette.Accent : Colors.Transparent).Padding(14, 0).HeightRequest(34)
            .OnTapped(onTap);

    /// <summary>A thin progress bar (trophy progress).</summary>
    protected MauiReactor.Grid Bar(double fraction, Color? track = null) =>
        Grid("4", $"{Math.Clamp(fraction, 0, 1) * 1000:F0}*,{(1 - Math.Clamp(fraction, 0, 1)) * 1000:F0}*",
            BoxView().Color(Palette.Accent).CornerRadius(2),
            BoxView().Color(track ?? Palette.Track).CornerRadius(2).GridColumn(1)
        ).ColumnSpacing(0).VCenter();

    protected MauiReactor.Button PrimaryButton(string text, Action onTap, bool enabled = true) =>
        Button(text).OnClicked(onTap).IsEnabled(enabled).HeightRequest(54)
            .BackgroundColor(enabled ? Palette.Accent : Palette.BorderStrong);

    protected MauiReactor.Border SecondaryButton(string text, Action onTap, bool enabled = true) =>
        Border(Label(text).FontFamily(Fonts.InterSemiBold).FontSize(15).TextColor(enabled ? Palette.Text : Palette.Faint).Center())
            .StrokeShape(new RoundRectangle().CornerRadius(16)).Stroke(Palette.BorderStrong).StrokeThickness(1)
            .BackgroundColor(Palette.Surface).HeightRequest(48)
            .OnTapped(() => { if (enabled) onTap(); });

    protected MauiReactor.Label Note(string text) =>
        Label(text).FontSize(12).TextColor(Palette.Muted).Margin(24, 0, 24, 12);

    /// <summary>The coloured tile with the game's initials, or its icon0.png.</summary>
    protected MauiReactor.Border Cover(string titleId, string initials, string? iconPath, double size, double corner, double fontSize) =>
        Border(
            iconPath != null
                ? Image().Source(iconPath).Aspect(Aspect.AspectFill)
                : Label(initials).FontFamily(Fonts.SoraBold).FontSize(fontSize).TextColor(Color.FromArgb("#DCE5F7")).Center())
            .StrokeShape(new RoundRectangle().CornerRadius(corner)).StrokeThickness(0)
            .BackgroundColor(Color.FromArgb(Library.GameLibrary.CoverColor(titleId)))
            .WidthRequest(size).HeightRequest(size);

    // ------------------------------------------------------------------ dialogs

    /// <summary>A choice from a list (action sheet); null when cancelled.</summary>
    protected async Task<string?> Choose(string title, IReadOnlyList<Choice> choices)
    {
        if (ContainerPage is not { } page) return null;
        var labels = choices.Select(c => c.Label).ToArray();
        var picked = await page.DisplayActionSheetAsync(title, "Cancelar", null, labels);
        return choices.FirstOrDefault(c => c.Label == picked)?.Value;
    }

    protected async Task<bool> Confirm(string title, string message, string accept = "Sim", string cancel = "Cancelar") =>
        ContainerPage is { } page && await page.DisplayAlertAsync(title, message, accept, cancel);

    protected async Task Alert(string title, string message)
    {
        if (ContainerPage is { } page) await page.DisplayAlertAsync(title, message, "OK");
    }

    protected async Task<string?> Prompt(string title, string message, string initial = "", bool password = false)
    {
        if (ContainerPage is not { } page) return null;
        return await page.DisplayPromptAsync(title, message, "OK", "Cancelar", initialValue: initial,
            keyboard: password ? Keyboard.Default : Keyboard.Text);
    }

    /// <summary>Runs work off the UI thread and reports failures in a dialog (and the log).</summary>
    protected async Task Run(string what, Func<Task> work)
    {
        try { await work(); }
        catch (Exception e)
        {
            Log.Warn($"{what}: {e}");
            await Alert(what, e.Message);
        }
    }
}

public abstract class Screen<TState> : Screen<TState, NoProps> where TState : class, new();
