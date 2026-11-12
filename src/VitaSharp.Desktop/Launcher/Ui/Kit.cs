using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using System.Runtime.CompilerServices;

namespace VitaSharp.Desktop.Launcher.Ui;

/// <summary>
/// Colour tokens of the launcher (docs/notes/desktop-launcher-design.md). The dark set is the Android app's palette
/// (src/VitaSharp.Android/Ui/Theme.cs); the light set keeps the same roles with the accent darkened for contrast.
/// </summary>
public sealed class Palette
{
    public required bool Dark { get; init; }
    public required Color Background, Sidebar, Surface, SurfaceHigh, Highlight, HighlightBorder, Border, BorderStrong;
    public required Color Text, TextSoft, Muted, Faint, Accent, AccentSoft, OnAccent, Track;
    public required Color Gold, Silver, Bronze, Platinum, Danger, Ok, Warn, Scrim, Accent2;

    public static readonly Palette DarkSet = new()
    {
        Dark = true,
        Background = C("#0E1116"),
        Sidebar = C("#0B0E13"),
        Surface = C("#171B22"),
        SurfaceHigh = C("#1E2A40"),
        Highlight = C("#1A2233"),
        HighlightBorder = C("#2B3A55"),
        Border = C("#232A35"),
        BorderStrong = C("#2A313D"),
        Text = C("#E8ECF2"),
        TextSoft = C("#C9D1DC"),
        Muted = C("#9AA4B2"),
        Faint = C("#6B7584"),
        Accent = C("#6E9BFF"),
        AccentSoft = C("#9DB4E8"),
        OnAccent = C("#0B1220"),
        Track = C("#2A3550"),
        Gold = C("#E2B13C"),
        Silver = C("#B8C2D0"),
        Bronze = C("#C98A5A"),
        Platinum = C("#A9C7F0"),
        Danger = C("#FF8A8A"),
        Ok = C("#7FD6A4"),
        Warn = C("#F2B45A"),
        Scrim = C("#0E1116"),
        Accent2 = C("#8C7CFF"),
    };

    public static readonly Palette LightSet = new()
    {
        Dark = false,
        Background = C("#F3F5F9"),
        Sidebar = C("#EAEEF4"),
        Surface = C("#FFFFFF"),
        SurfaceHigh = C("#E8EEFA"),
        Highlight = C("#E3EBFD"),
        HighlightBorder = C("#C3D3F7"),
        Border = C("#E1E6EE"),
        BorderStrong = C("#D2D9E4"),
        Text = C("#121722"),
        TextSoft = C("#2E3746"),
        Muted = C("#5D6878"),
        Faint = C("#8A94A3"),
        Accent = C("#3767DB"),
        AccentSoft = C("#5B7FD1"),
        OnAccent = C("#FFFFFF"),
        Track = C("#DCE4F3"),
        Gold = C("#B98712"),
        Silver = C("#8592A5"),
        Bronze = C("#A86A3C"),
        Platinum = C("#5E86C2"),
        Danger = C("#C93C3C"),
        Ok = C("#1E8E55"),
        Warn = C("#B87412"),
        Scrim = C("#F3F5F9"),
        Accent2 = C("#5B4FD6"),
    };

    /// <summary>The palette of the theme the launcher shows now (LauncherApp sets it from the chosen theme).</summary>
    public static Palette Current { get; set; } = DarkSet;

    static Color C(string hex) => Color.Parse(hex);
}

/// <summary>
/// The theme-dependent parts of the launcher (brushes, gradient stops, shadows, the theme row's icon), each bound to how
/// its value is chosen from the palette. A theme switch re-evaluates every live one (<see cref="Apply"/>) instead of
/// building the window again. Held weakly (<see cref="ConditionalWeakTable{TKey,TValue}"/>): a page that is dropped
/// lets its brushes go, and a binding never keeps its target alive.
/// </summary>
public static class ThemeBindings
{
    sealed class Bound
    {
        public readonly List<Action<Palette>> Updates = new();
    }

    static readonly ConditionalWeakTable<object, Bound> s_bound = new();

    /// <summary>Runs <paramref name="apply"/> on <paramref name="target"/> now with the current palette, and again on
    /// every palette change while the target is alive.</summary>
    public static T Bind<T>(T target, Action<T, Palette> apply) where T : class
    {
        apply(target, Palette.Current);
        Bound bound = s_bound.GetOrCreateValue(target);
        lock (bound)
        {
            bound.Updates.Add(palette => apply(target, palette));
        }

        return target;
    }

    /// <summary>Re-evaluates every live binding with <paramref name="palette"/> (called by the theme switch).</summary>
    public static void Apply(Palette palette)
    {
        // a snapshot: an update may create brushes (a hover handler), which must not change the table being walked
        List<Bound> live = new();
        foreach (KeyValuePair<object, Bound> entry in s_bound)
        {
            live.Add(entry.Value);
        }

        foreach (Bound bound in live)
        {
            Action<Palette>[] updates;
            lock (bound)
            {
                updates = bound.Updates.ToArray();
            }

            foreach (Action<Palette> update in updates)
            {
                update(palette);
            }
        }
    }

    /// <summary>How many objects carry a binding now (tests).</summary>
    internal static int LiveCount
    {
        get
        {
            int count = 0;
            foreach (KeyValuePair<object, Bound> entry in s_bound)
            {
                count++;
            }

            return count;
        }
    }
}

/// <summary>Building blocks of the launcher pages: text, icons, cards, chips, rows, buttons, bars. Every colour is
/// given as how it is picked from the palette (<c>p => p.Muted</c>), so the theme switch only re-colours
/// (<see cref="ThemeBindings"/>); colours that never follow the theme (text over cover art) use <see cref="Fixed"/>.</summary>
public sealed class Kit
{
    public static readonly FontFamily Inter = new("avares://VitaSharp.Desktop/Assets/Fonts#Inter");
    public static readonly FontFamily Sora = new("avares://VitaSharp.Desktop/Assets/Fonts#Sora");

    /// <summary>A brush coloured by <paramref name="pick"/>, re-coloured on every theme switch.</summary>
    public static IBrush B(Func<Palette, Color> pick)
    {
        return ThemeBindings.Bind(new SolidColorBrush(), (brush, palette) => brush.Color = pick(palette));
    }

    /// <summary>A brush coloured by <paramref name="pick"/> at <paramref name="opacity"/>, re-coloured on every theme switch.</summary>
    public static IBrush B(Func<Palette, Color> pick, double opacity)
    {
        SolidColorBrush brush = new() { Opacity = opacity };
        return ThemeBindings.Bind(brush, (target, palette) => target.Color = pick(palette));
    }

    /// <summary>A brush whose colour and opacity both depend on the theme.</summary>
    public static IBrush B(Func<Palette, Color> pick, Func<Palette, double> opacity)
    {
        return ThemeBindings.Bind(new SolidColorBrush(), (brush, palette) =>
        {
            brush.Color = pick(palette);
            brush.Opacity = opacity(palette);
        });
    }

    /// <summary>A brush that keeps its colour in every theme (overlays on cover art, the dialog dimmer).</summary>
    public static IBrush Fixed(Color color, double opacity = 1)
    {
        return new SolidColorBrush(color, opacity);
    }

    /// <summary>A gradient stop coloured by <paramref name="pick"/> with the alpha <paramref name="alpha"/>.</summary>
    public static GradientStop Stop(Func<Palette, Color> pick, byte alpha, double offset)
    {
        return ThemeBindings.Bind(new GradientStop { Offset = offset }, (stop, palette) =>
        {
            Color color = pick(palette);
            stop.Color = Color.FromArgb(alpha, color.R, color.G, color.B);
        });
    }

    /// <summary><paramref name="color"/> with the alpha <paramref name="alpha"/>.</summary>
    public static Color WithAlpha(Color color, byte alpha)
    {
        return Color.FromArgb(alpha, color.R, color.G, color.B);
    }

    /// <summary>The light theme's drop shadow on <paramref name="border"/> (the dark theme draws none).</summary>
    public static T LightShadow<T>(T border, string shadow) where T : Border
    {
        BoxShadows shadows = BoxShadows.Parse(shadow);
        return ThemeBindings.Bind(border, (target, palette) => target.BoxShadow = palette.Dark ? default : shadows);
    }

    // ------------------------------------------------------------------ text

    public TextBlock Text(string text, double size = 14, FontWeight weight = FontWeight.Normal, Func<Palette, Color>? color = null, bool heading = false) => new()
    {
        Text = text,
        FontSize = size,
        FontWeight = weight,
        FontFamily = heading ? Sora : Inter,
        Foreground = B(color ?? (static p => p.Text)),
        TextTrimming = TextTrimming.CharacterEllipsis,
        VerticalAlignment = VerticalAlignment.Center,
    };

    public TextBlock Wrap(string text, double size = 13, Func<Palette, Color>? color = null, double maxWidth = double.PositiveInfinity) => new()
    {
        Text = text,
        FontSize = size,
        FontFamily = Inter,
        Foreground = B(color ?? (static p => p.Muted)),
        TextWrapping = TextWrapping.Wrap,
        MaxWidth = maxWidth,
    };

    public TextBlock Heading(string text, double size) => Text(text, size, FontWeight.Bold, heading: true);

    public TextBlock Kicker(string text, Func<Palette, Color>? color = null)
    {
        TextBlock kicker = Text(text.ToUpperInvariant(), 11.5, FontWeight.SemiBold, color ?? (static p => p.Muted));
        kicker.LetterSpacing = 1.2;
        return kicker;
    }

    // ------------------------------------------------------------------ icons (Lucide-style 24x24 strokes)

    static readonly Dictionary<string, string> IconPaths = new()
    {
        ["library"] = "M4 3h6a1 1 0 0 1 1 1v6a1 1 0 0 1-1 1H4a1 1 0 0 1-1-1V4a1 1 0 0 1 1-1z M14 3h6a1 1 0 0 1 1 1v6a1 1 0 0 1-1 1h-6a1 1 0 0 1-1-1V4a1 1 0 0 1 1-1z M14 13h6a1 1 0 0 1 1 1v6a1 1 0 0 1-1 1h-6a1 1 0 0 1-1-1v-6a1 1 0 0 1 1-1z M4 13h6a1 1 0 0 1 1 1v6a1 1 0 0 1-1 1H4a1 1 0 0 1-1-1v-6a1 1 0 0 1 1-1z",
        ["vita"] = "M2 9a4 4 0 0 1 4-4h12a4 4 0 0 1 4 4v6a4 4 0 0 1-4 4H6a4 4 0 0 1-4-4z M8 9h8v6H8z",
        ["trophy"] = "M6 9H4.5a2.5 2.5 0 0 1 0-5H6 M18 9h1.5a2.5 2.5 0 0 0 0-5H18 M4 22h16 M10 14.66V17c0 .55-.47.98-.97 1.21C7.85 18.75 7 20.24 7 22 M14 14.66V17c0 .55.47.98.97 1.21C16.15 18.75 17 20.24 17 22 M18 2H6v7a6 6 0 0 0 12 0V2z",
        ["download"] = "M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4 M7 10l5 5l5-5 M12 15V3",
        ["settings"] = "M4 6h9 M17 6h3 M15 4v4 M4 12h3 M11 12h9 M9 10v4 M4 18h11 M19 18h1 M17 16v4",
        ["search"] = "M4 11a7 7 0 1 0 14 0a7 7 0 1 0-14 0z M20 20l-3.6-3.6",
        ["play"] = "M7 4.5v15l12.5-7.5z",
        ["stop"] = "M6 6h12v12H6z",
        ["gamepad"] = "M6 11h4 M8 9v4 M15 12h.01 M18 10h.01 M17.32 5H6.68a4 4 0 0 0-3.98 3.59l-.9 7.28A3 3 0 0 0 4.79 19c1 0 1.9-.5 2.4-1.3l1.04-1.56A2 2 0 0 1 9.9 15.3h4.2a2 2 0 0 1 1.66.89l1.04 1.56A2.9 2.9 0 0 0 19.2 19a3 3 0 0 0 2.99-3.13l-.9-7.28A4 4 0 0 0 17.32 5z",
        ["image"] = "M5 3h14a2 2 0 0 1 2 2v14a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2z M9 10.5a1.5 1.5 0 1 0 0-3a1.5 1.5 0 0 0 0 3z M21 15l-5-5L5 21",
        ["save"] = "M19 21H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h11l5 5v11a2 2 0 0 1-2 2z M17 21v-8H7v8 M7 3v5h8",
        ["chevron-right"] = "M9 18l6-6l-6-6",
        ["chevron-left"] = "M15 18l-6-6l6-6",
        ["chevron-down"] = "M6 9l6 6l6-6",
        ["sun"] = "M8 12a4 4 0 1 0 8 0a4 4 0 1 0-8 0z M12 2v2 M12 20v2 M4.93 4.93l1.41 1.41 M17.66 17.66l1.41 1.41 M2 12h2 M20 12h2 M6.34 17.66l-1.41 1.41 M19.07 4.93l-1.41 1.41",
        ["moon"] = "M12 3a6 6 0 0 0 9 9a9 9 0 1 1-9-9z",
        ["cpu"] = "M6 6h12v12H6z M9.5 9.5h5v5h-5z M9 2v4 M15 2v4 M9 18v4 M15 18v4 M2 9h4 M2 15h4 M18 9h4 M18 15h4",
        ["globe"] = "M2 12a10 10 0 1 0 20 0a10 10 0 1 0-20 0z M2 12h20 M12 2a15.3 15.3 0 0 1 4 10a15.3 15.3 0 0 1-4 10a15.3 15.3 0 0 1-4-10a15.3 15.3 0 0 1 4-10z",
        ["volume"] = "M11 5L6 9H2v6h4l5 4z M15.54 8.46a5 5 0 0 1 0 7.07 M19.07 4.93a10 10 0 0 1 0 14.14",
        ["monitor"] = "M4 3h16a2 2 0 0 1 2 2v10a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2z M8 21h8 M12 17v4",
        ["user"] = "M19 21v-2a4 4 0 0 0-4-4H9a4 4 0 0 0-4 4v2 M8 7a4 4 0 1 0 8 0a4 4 0 1 0-8 0z",
        ["terminal"] = "M4 17l6-6l-6-6 M12 19h8",
        ["more"] = "M5 12h.01 M12 12h.01 M19 12h.01",
        ["clock"] = "M2 12a10 10 0 1 0 20 0a10 10 0 1 0-20 0z M12 6v6l4 2",
        ["folder"] = "M4 20h16a2 2 0 0 0 2-2V8a2 2 0 0 0-2-2h-7.93a2 2 0 0 1-1.66-.9l-.82-1.2A2 2 0 0 0 7.93 3H4a2 2 0 0 0-2 2v13a2 2 0 0 0 2 2z",
        ["plus"] = "M12 5v14 M5 12h14",
        ["grid"] = "M3 3h7v7H3z M14 3h7v7h-7z M14 14h7v7h-7z M3 14h7v7H3z",
        ["list"] = "M8 6h13 M8 12h13 M8 18h13 M3 6h.01 M3 12h.01 M3 18h.01",
        ["zap"] = "M13 2L3 14h9l-1 8l10-12h-9z",
        ["gauge"] = "M12 14l4-4 M3.34 19a10 10 0 1 1 17.32 0",
        ["camera"] = "M14.5 4h-5L7 7H4a2 2 0 0 0-2 2v9a2 2 0 0 0 2 2h16a2 2 0 0 0 2-2V9a2 2 0 0 0-2-2h-3z M9 13a3 3 0 1 0 6 0a3 3 0 1 0-6 0z",
        ["layers"] = "M12 2l10 5l-10 5L2 7z M2 17l10 5l10-5 M2 12l10 5l10-5",
        ["check"] = "M20 6L9 17l-5-5",
        ["box"] = "M21 8l-9-5l-9 5v8l9 5l9-5z M3.3 7.6L12 12.5l8.7-4.9 M12 22V12.5",
        ["hdd"] = "M22 12H2 M5.45 5.11L2 12v6a2 2 0 0 0 2 2h16a2 2 0 0 0 2-2v-6l-3.45-6.89A2 2 0 0 0 16.76 4H7.24a2 2 0 0 0-1.79 1.11z M6 16h.01 M10 16h.01",
        ["x"] = "M18 6L6 18 M6 6l12 12",
        ["trash"] = "M3 6h18 M19 6v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6 M8 6V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2",
        ["refresh"] = "M21 12a9 9 0 1 1-2.64-6.36L21 8 M21 3v5h-5",
        ["file"] = "M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z M14 2v6h6",
        ["key"] = "M15 7a4 4 0 1 1-3.87 5H3v3h3v3h3v-3h2.13A4 4 0 0 1 15 7z",
        ["alert"] = "M12 9v4 M12 17h.01 M10.29 3.86L1.82 18a2 2 0 0 0 1.71 3h16.94a2 2 0 0 0 1.71-3L13.71 3.86a2 2 0 0 0-3.42 0z",
    };

    public Control Icon(string name, double size = 20, Func<Palette, Color>? color = null, double stroke = 1.8, bool fill = false)
    {
        Func<Palette, Color> pick = color ?? (static p => p.Text);
        var path = new Avalonia.Controls.Shapes.Path
        {
            Data = Geometry.Parse(IconPaths[name]),
            Stroke = B(pick),
            StrokeThickness = name == "more" ? 3.2 : stroke,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round,
            Fill = fill ? B(pick) : null,
        };
        return new Viewbox
        {
            Width = size,
            Height = size,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = new Canvas { Width = 24, Height = 24, Children = { path } },
        };
    }

    /// <summary>The square tile with an icon used by setting rows (the Android "icon chip").</summary>
    public Border IconChip(string icon, double size = 40, Func<Palette, Color>? bg = null, Func<Palette, Color>? fg = null)
    {
        return ChipAround(Icon(icon, size * 0.5, fg ?? (static p => p.AccentSoft)), size, bg);
    }

    /// <summary>An icon chip whose icon depends on the theme (the sun or the moon on the theme row).</summary>
    public Border IconChip(Func<Palette, string> icon, double size = 40)
    {
        return ChipAround(Icon(icon, size * 0.5, static p => p.AccentSoft), size, null);
    }

    /// <summary>An icon whose shape depends on the theme: its geometry is swapped on every theme switch.</summary>
    public Control Icon(Func<Palette, string> name, double size = 20, Func<Palette, Color>? color = null)
    {
        Control icon = Icon(name(Palette.Current), size, color);
        Avalonia.Controls.Shapes.Path path = (Avalonia.Controls.Shapes.Path)((Canvas)((Viewbox)icon).Child!).Children[0];
        ThemeBindings.Bind(path, (target, palette) => target.Data = Geometry.Parse(IconPaths[name(palette)]));
        return icon;
    }

    static Border ChipAround(Control icon, double size, Func<Palette, Color>? bg) => new()
    {
        Width = size,
        Height = size,
        CornerRadius = new CornerRadius(size * 0.3),
        Background = B(bg ?? (static p => p.Highlight)),
        Child = icon,
    };

    // ------------------------------------------------------------------ surfaces

    public Border Card(Control child, double radius = 20, Thickness? padding = null)
    {
        return LightShadow(new Border
        {
            Background = B(static p => p.Surface),
            BorderBrush = B(static p => p.Border),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(radius),
            Padding = padding ?? new Thickness(20),
            Child = child,
        }, "0 2 8 0 #10000000");
    }

    public Border Divider(double inset = 16) => new() { Height = 1, Background = B(static p => p.Border), Margin = new Thickness(inset, 0) };

    // decoded images by path, scaled down to the size they are shown at (icon0 128 px, pic0 960x544)
    static readonly Dictionary<(string, int), Bitmap?> Images = new();

    public static Bitmap? LoadImage(string? path, int decodeWidth = 0)
    {
        if (path == null)
        {
            return null;
        }

        lock (Images)
        {
            if (Images.TryGetValue((path, decodeWidth), out var cached))
            {
                return cached;
            }

            Bitmap? b = null;
            try
            {
                if (File.Exists(path))
                {
                    using var s = File.OpenRead(path);
                    b = decodeWidth > 0 ? Bitmap.DecodeToWidth(s, decodeWidth) : new Bitmap(s);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException) { }
            Images[(path, decodeWidth)] = b;
            return b;
        }
    }

    public static void ForgetImages(string folder)
    {
        lock (Images)
        {
            foreach (var key in Images.Keys.Where(k => k.Item1.StartsWith(folder, StringComparison.OrdinalIgnoreCase)).ToList())
            {
                Images.Remove(key);
            }
        }
    }

    public static IBrush Image(string? path, int decodeWidth = 0, Stretch stretch = Stretch.UniformToFill, AlignmentX ax = AlignmentX.Center, AlignmentY ay = AlignmentY.Center)
        => LoadImage(path, decodeWidth) is { } bmp ? new ImageBrush(bmp) { Stretch = stretch, AlignmentX = ax, AlignmentY = ay } : Brushes.Transparent;

    public static IBrush Image(Bitmap? bmp) => bmp != null ? new ImageBrush(bmp) { Stretch = Stretch.UniformToFill } : Brushes.Transparent;

    /// <summary>A picture with rounded corners (icon0, thumbnails), or a tinted placeholder with initials.</summary>
    public Border Picture(string? path, double width, double height, double radius, string fallback = "", int decodeWidth = 256)
    {
        var b = new Border { Width = width, Height = height, CornerRadius = new CornerRadius(radius), ClipToBounds = true };
        if (LoadImage(path, decodeWidth) is { } bmp)
        {
            b.Background = new ImageBrush(bmp) { Stretch = Stretch.UniformToFill };
        }
        else
        {
            b.Background = B(static p => p.SurfaceHigh);
            b.Child = new TextBlock
            {
                Text = fallback,
                FontFamily = Sora,
                FontWeight = FontWeight.Bold,
                FontSize = Math.Max(10, Math.Min(width, height) * 0.28),
                Foreground = B(static p => p.Text, 0.85),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
        }
        return b;
    }

    /// <summary>A gradient from transparent to the colour <paramref name="to"/> picks (fades cover art into the page).</summary>
    public static LinearGradientBrush Fade(Func<Palette, Color> to, RelativePoint start, RelativePoint end, double from = 0, double toAlpha = 1) => new()
    {
        StartPoint = start,
        EndPoint = end,
        GradientStops = { Stop(to, (byte)(from * 255), 0), Stop(to, (byte)(toAlpha * 255), 1) },
    };

    public static RelativePoint Rel(double x, double y) => new(x, y, RelativeUnit.Relative);

    // ------------------------------------------------------------------ interaction

    /// <summary>Makes <paramref name="visual"/> a button (style class "flat": no Fluent chrome, a light hover).</summary>
    public static Button Click(Control visual, Action onClick, string? tip = null, bool enabled = true)
    {
        var b = new Button
        {
            Content = visual,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            HorizontalAlignment = visual.HorizontalAlignment,
            VerticalAlignment = visual.VerticalAlignment,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
            Cursor = new Cursor(StandardCursorType.Hand),
            IsEnabled = enabled,
            Margin = visual.Margin,
        };
        visual.Margin = default;
        b.Classes.Add("flat");
        b.Click += (_, e) => { e.Handled = true; onClick(); };
        if (tip != null)
        {
            ToolTip.SetTip(b, tip);
        }

        return b;
    }

    // ------------------------------------------------------------------ controls

    public Button Chip(string text, bool selected, Action onClick, string? icon = null, bool dropdown = false)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        Func<Palette, Color> fg = selected ? static p => p.OnAccent : static p => p.TextSoft;
        if (icon != null)
        {
            row.Children.Add(Icon(icon, 15, fg));
        }

        row.Children.Add(Text(text, 13, selected ? FontWeight.SemiBold : FontWeight.Medium, fg));
        if (dropdown)
        {
            row.Children.Add(Icon("chevron-down", 14, selected ? fg : static p => p.Muted));
        }

        return Click(new Border
        {
            Height = 34,
            Padding = new Thickness(14, 0),
            CornerRadius = new CornerRadius(17),
            Child = row,
            Background = selected ? B(static p => p.Accent) : Fixed(Colors.Transparent),
            BorderBrush = B(p => selected ? p.Accent : p.BorderStrong),
            BorderThickness = new Thickness(1),
        }, onClick);
    }

    /// <summary>A chip that opens a menu of choices (region, sort order).</summary>
    public Button ChipMenu(string text, string? icon, IReadOnlyList<(string Label, bool Selected, Action Pick)> items)
    {
        var flyout = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
        foreach (var (label, sel, pick) in items)
        {
            var mi = new MenuItem { Header = label, Icon = sel ? Icon("check", 14, static p => p.Accent, 2.2) : null };
            mi.Click += (_, _) => pick();
            flyout.Items.Add(mi);
        }
        var b = Chip(text, false, () => { }, icon, dropdown: true);
        b.Flyout = flyout;
        return b;
    }

    public Border PrimaryVisual(string text, string? icon = "play", double height = 48, double minWidth = 150)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        if (icon != null)
        {
            row.Children.Add(Icon(icon, icon is "play" or "stop" ? 16 : 18, static p => p.OnAccent, fill: icon is "play" or "stop"));
        }

        if (text.Length > 0)
        {
            row.Children.Add(Text(text, 15, FontWeight.SemiBold, static p => p.OnAccent));
        }

        return LightShadow(new Border
        {
            Height = height,
            MinWidth = minWidth,
            Padding = new Thickness(text.Length > 0 ? 22 : 0, 0),
            CornerRadius = new CornerRadius(16),
            Background = B(static p => p.Accent),
            Child = row,
        }, "0 6 16 0 #333767DB");
    }

    public Button Primary(string text, string? icon, Action onClick, double height = 48, double minWidth = 150, bool enabled = true, string? tip = null)
    {
        var v = PrimaryVisual(text, icon, height, minWidth);
        if (!enabled)
        {
            v.Opacity = 0.45;
        }

        return Click(v, onClick, tip, enabled);
    }

    public Border SecondaryVisual(string text, string? icon = null, double height = 48, Func<Palette, Color>? fg = null)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 9, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        if (icon != null)
        {
            row.Children.Add(Icon(icon, 18, fg ?? (static p => p.Text), fill: icon is "play" or "stop"));
        }

        if (text.Length > 0)
        {
            row.Children.Add(Text(text, 14, FontWeight.SemiBold, fg ?? (static p => p.Text)));
        }

        return new Border
        {
            Height = height,
            MinWidth = height,
            Padding = new Thickness(text.Length > 0 ? 18 : 0, 0),
            CornerRadius = new CornerRadius(Math.Min(16, height / 2.6)),
            Background = B(static p => p.Surface),
            BorderBrush = B(static p => p.BorderStrong),
            BorderThickness = new Thickness(1),
            Child = row,
        };
    }

    public Button Secondary(string text, string? icon, Action onClick, double height = 48, Func<Palette, Color>? fg = null, bool enabled = true, string? tip = null)
    {
        var v = SecondaryVisual(text, icon, height, fg);
        if (!enabled)
        {
            v.Opacity = 0.45;
        }

        return Click(v, onClick, tip, enabled);
    }

    /// <summary>A secondary button with a drop-down menu.</summary>
    public Button MenuButton(string text, string? icon, double height, params (string Label, Action Pick, bool Enabled)[] items)
    {
        var flyout = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight };
        foreach (var (label, pick, enabled) in items)
        {
            var mi = new MenuItem { Header = label, IsEnabled = enabled };
            mi.Click += (_, _) => pick();
            flyout.Items.Add(mi);
        }
        var b = Secondary(text, icon, () => { }, height);
        b.Flyout = flyout;
        return b;
    }

    public Button PrimaryMenu(string text, string? icon, double height, params (string Label, Action Pick)[] items)
    {
        var flyout = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight };
        foreach (var (label, pick) in items)
        {
            var mi = new MenuItem { Header = label };
            mi.Click += (_, _) => pick();
            flyout.Items.Add(mi);
        }
        var b = Primary(text, icon, () => { }, height, 0);
        b.Flyout = flyout;
        return b;
    }

    /// <summary>Round play button.</summary>
    public Border PlayDisc(double size) => new()
    {
        Width = size,
        Height = size,
        CornerRadius = new CornerRadius(size / 2),
        Background = B(static p => p.Accent),
        Child = new Border { Margin = new Thickness(size * 0.08, 0, 0, 0), Child = Icon("play", size * 0.38, static p => p.OnAccent, fill: true) },
    };

    public ToggleSwitch Switch(bool on, Action<bool> changed, bool enabled = true)
    {
        var t = new ToggleSwitch { IsChecked = on, OnContent = null, OffContent = null, IsEnabled = enabled, MinWidth = 0 };
        t.IsCheckedChanged += (_, _) => changed(t.IsChecked == true);
        return t;
    }

    public ComboBox Dropdown(IReadOnlyList<string> items, int selected, Action<int> changed, double width = 220, bool enabled = true)
    {
        var c = new ComboBox
        {
            ItemsSource = items,
            SelectedIndex = selected,
            Width = width,
            MinHeight = 36,
            CornerRadius = new CornerRadius(12),
            Background = B(static p => p.Background),
            BorderBrush = B(static p => p.BorderStrong),
            FontFamily = Inter,
            IsEnabled = enabled,
        };
        c.SelectionChanged += (_, _) =>
        {
            if (c.SelectedIndex >= 0)
            {
                changed(c.SelectedIndex);
            }
        };
        return c;
    }

    public TextBox Field(string text, Action<string> changed, double width = 240, string? watermark = null, char password = '\0')
    {
        var t = new TextBox
        {
            Text = text,
            Width = width,
            Watermark = watermark,
            MinHeight = 36,
            CornerRadius = new CornerRadius(12),
            FontFamily = Inter,
            Background = B(static p => p.Background),
            BorderBrush = B(static p => p.BorderStrong),
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        if (password != '\0')
        {
            t.PasswordChar = password;
        }

        t.TextChanged += (_, _) => changed(t.Text ?? "");
        return t;
    }

    /// <summary>A segmented control (theme picker, view mode, start-up choice); <paramref name="picked"/> gets the index.</summary>
    public Border Segmented(int selected, Action<int> picked, params (string Text, string? Icon, string? Tip)[] items)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        var host = new Border
        {
            Padding = new Thickness(3),
            CornerRadius = new CornerRadius(12),
            Child = row,
            VerticalAlignment = VerticalAlignment.Center,
            Background = B(static p => p.Dark ? p.Background : p.Sidebar),
            BorderBrush = B(static p => p.Border),
            BorderThickness = new Thickness(1),
        };
        void Build()
        {
            row.Children.Clear();
            for (int i = 0; i < items.Length; i++)
            {
                var (text, icon, tip) = items[i];
                bool sel = i == selected;
                var inner = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                if (icon != null)
                {
                    inner.Children.Add(Icon(icon, 15, p => sel ? p.Text : p.Muted));
                }

                if (text.Length > 0)
                {
                    inner.Children.Add(Text(text, 13, sel ? FontWeight.SemiBold : FontWeight.Medium, p => sel ? p.Text : p.Muted));
                }

                int index = i;
                Border segment = new()
                {
                    Height = 30,
                    MinWidth = 34,
                    Padding = new Thickness(text.Length > 0 ? 12 : 8, 0),
                    CornerRadius = new CornerRadius(9),
                    Child = inner,
                    Background = sel ? B(static p => p.Dark ? p.SurfaceHigh : p.Surface) : Brushes.Transparent,
                };
                if (sel)
                {
                    LightShadow(segment, "0 1 3 0 #22000000");
                }

                row.Children.Add(Click(segment, () =>
                {
                    if (selected == index)
                    {
                        return;
                    }

                    selected = index;
                    Build();
                    picked(index);
                }, tip));
            }
        }
        Build();
        return host;
    }

    public Grid Bar(double fraction, double height = 5, Func<Palette, Color>? fill = null)
    {
        fraction = double.IsFinite(fraction) ? Math.Clamp(fraction, 0, 1) : 0;
        var g = new Grid { Height = height, VerticalAlignment = VerticalAlignment.Center, ColumnDefinitions = new ColumnDefinitions($"{fraction * 1000:F0}*,{(1 - fraction) * 1000:F0}*") };
        g.Children.Add(new Border { Background = B(static p => p.Track), CornerRadius = new CornerRadius(height / 2), [Grid.ColumnSpanProperty] = 2 });
        g.Children.Add(new Border { Background = B(fill ?? (static p => p.Accent)), CornerRadius = new CornerRadius(height / 2) });
        return g;
    }

    public Border Badge(string text, Func<Palette, Color> fg, Func<Palette, Color>? bg = null) => new()
    {
        Padding = new Thickness(9, 3),
        CornerRadius = new CornerRadius(8),
        Background = B(bg ?? fg, bg == null ? 0.16 : 1),
        Child = Text(text, 11.5, FontWeight.SemiBold, fg),
        VerticalAlignment = VerticalAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Left,
    };

    public Border Kbd(string text, bool onAccent = false) => new()
    {
        Padding = new Thickness(7, 2),
        CornerRadius = new CornerRadius(6),
        VerticalAlignment = VerticalAlignment.Center,
        BorderBrush = B(p => onAccent ? p.OnAccent : p.BorderStrong, onAccent ? 0.35 : 1),
        BorderThickness = new Thickness(1),
        Child = Text(text, 11, FontWeight.SemiBold, p => onAccent ? p.OnAccent : p.Muted),
    };

    public Border Dot(Func<Palette, Color> c, double size = 8) => new() { Width = size, Height = size, CornerRadius = new CornerRadius(size / 2), Background = B(c), VerticalAlignment = VerticalAlignment.Center };

    /// <summary>A setting row: icon chip, title, detail, then a control on the right (or a chevron when it opens something).</summary>
    public Control Row(string? icon, string title, string? detail = null, Control? right = null, Action? open = null, string? tip = null)
    {
        return RowWith(icon == null ? null : IconChip(icon, 38), title, detail, right, open, tip);
    }

    /// <summary>A setting row whose icon depends on the theme (the theme row: the moon in the dark, the sun in the light).</summary>
    public Control Row(Func<Palette, string> icon, string title, string? detail = null, Control? right = null, Action? open = null, string? tip = null)
    {
        return RowWith(IconChip(icon, 38), title, detail, right, open, tip);
    }

    Control RowWith(Border? chip, string title, string? detail, Control? right, Action? open, string? tip)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"), MinHeight = 56, Margin = new Thickness(16, 7), Background = Brushes.Transparent };
        if (chip != null)
        {
            g.Children.Add(new Border { Margin = new Thickness(0, 0, 14, 0), Child = chip, VerticalAlignment = VerticalAlignment.Center });
        }

        var texts = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(Text(title, 14.5, FontWeight.Medium));
        if (detail != null)
        {
            texts.Children.Add(Wrap(detail, 12.5));
        }

        Grid.SetColumn(texts, 1);
        g.Children.Add(texts);
        if (right != null)
        {
            right.VerticalAlignment = VerticalAlignment.Center;
            right.Margin = new Thickness(16, 0, 0, 0);
            Grid.SetColumn(right, 2);
            g.Children.Add(right);
        }
        if (open != null)
        {
            var c = Icon("chevron-right", 18, static p => p.Faint);
            c.Margin = new Thickness(10, 0, 0, 0);
            Grid.SetColumn(c, 3);
            g.Children.Add(c);
            return Click(g, open, tip);
        }
        if (tip != null)
        {
            ToolTip.SetTip(g, tip);
        }

        return g;
    }

    /// <summary>A card of rows separated by dividers (the Android grouped list).</summary>
    public Border Group(params Control?[] rows)
    {
        var s = new StackPanel();
        foreach (var r in rows)
        {
            if (r == null)
            {
                continue;
            }

            if (s.Children.Count > 0)
            {
                s.Children.Add(Divider());
            }

            s.Children.Add(r);
        }
        return Card(s, 18, new Thickness(0, 2));
    }

    public TextBlock GroupTitle(string title)
    {
        var t = Kicker(title);
        t.Margin = new Thickness(6, 14, 0, 4);
        return t;
    }

    /// <summary>A dashed drop area.</summary>
    public Grid Dashed(Control child, double height, double radius = 22)
    {
        var g = new Grid { Height = height };
        g.Children.Add(new Border { CornerRadius = new CornerRadius(radius), Background = B(static p => p.Accent, static p => p.Dark ? 0.05 : 0.04) });
        g.Children.Add(new Rectangle
        {
            RadiusX = radius,
            RadiusY = radius,
            Stroke = B(static p => p.Accent, 0.55),
            StrokeThickness = 1.5,
            StrokeDashArray = new AvaloniaList<double> { 5, 4 },
        });
        child.HorizontalAlignment = HorizontalAlignment.Center;
        child.VerticalAlignment = VerticalAlignment.Center;
        g.Children.Add(child);
        return g;
    }

    public StackPanel HStack(double spacing, params Control?[] items)
    {
        var s = new StackPanel { Orientation = Orientation.Horizontal, Spacing = spacing };
        foreach (var c in items)
        {
            if (c != null)
            {
                s.Children.Add(c);
            }
        }

        return s;
    }

    public StackPanel VStack(double spacing, params Control?[] items)
    {
        var s = new StackPanel { Spacing = spacing };
        foreach (var c in items)
        {
            if (c != null)
            {
                s.Children.Add(c);
            }
        }

        return s;
    }

    public static T At<T>(T c, int row = 0, int col = 0, int colSpan = 1, int rowSpan = 1) where T : Control
    {
        Grid.SetRow(c, row);
        Grid.SetColumn(c, col);
        if (colSpan > 1)
        {
            Grid.SetColumnSpan(c, colSpan);
        }

        if (rowSpan > 1)
        {
            Grid.SetRowSpan(c, rowSpan);
        }

        return c;
    }
}

static class UiExtensions
{
    public static T Also<T>(this T value, Action<T> action)
    {
        action(value);
        return value;
    }
}
