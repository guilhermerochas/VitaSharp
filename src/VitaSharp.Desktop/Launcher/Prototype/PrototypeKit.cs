using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace VitaSharp.Desktop.Launcher.Prototype;

/// <summary>
/// Colour tokens of the proposed launcher (docs/notes/desktop-launcher-design.md). The dark set is the Android app's
/// palette (src/VitaSharp.Android/Ui/Theme.cs); the light set keeps the same roles with the accent darkened for contrast.
/// </summary>
sealed class ProtoPalette
{
    public required bool Dark { get; init; }
    public required Color Background, Sidebar, Surface, SurfaceHigh, Highlight, HighlightBorder, Border, BorderStrong;
    public required Color Text, TextSoft, Muted, Faint, Accent, AccentSoft, OnAccent, Track;
    public required Color Gold, Silver, Bronze, Platinum, Danger, Ok, Warn, Scrim;

    public static readonly ProtoPalette DarkSet = new()
    {
        Dark = true,
        Background = C("#0E1116"), Sidebar = C("#0B0E13"), Surface = C("#171B22"), SurfaceHigh = C("#1E2A40"),
        Highlight = C("#1A2233"), HighlightBorder = C("#2B3A55"), Border = C("#232A35"), BorderStrong = C("#2A313D"),
        Text = C("#E8ECF2"), TextSoft = C("#C9D1DC"), Muted = C("#9AA4B2"), Faint = C("#6B7584"),
        Accent = C("#6E9BFF"), AccentSoft = C("#9DB4E8"), OnAccent = C("#0B1220"), Track = C("#2A3550"),
        Gold = C("#E2B13C"), Silver = C("#B8C2D0"), Bronze = C("#C98A5A"), Platinum = C("#A9C7F0"),
        Danger = C("#FF8A8A"), Ok = C("#7FD6A4"), Warn = C("#F2B45A"), Scrim = C("#0E1116"),
    };

    public static readonly ProtoPalette LightSet = new()
    {
        Dark = false,
        Background = C("#F3F5F9"), Sidebar = C("#EAEEF4"), Surface = C("#FFFFFF"), SurfaceHigh = C("#E8EEFA"),
        Highlight = C("#E3EBFD"), HighlightBorder = C("#C3D3F7"), Border = C("#E1E6EE"), BorderStrong = C("#D2D9E4"),
        Text = C("#121722"), TextSoft = C("#2E3746"), Muted = C("#5D6878"), Faint = C("#8A94A3"),
        Accent = C("#3767DB"), AccentSoft = C("#5B7FD1"), OnAccent = C("#FFFFFF"), Track = C("#DCE4F3"),
        Gold = C("#B98712"), Silver = C("#8592A5"), Bronze = C("#A86A3C"), Platinum = C("#5E86C2"),
        Danger = C("#C93C3C"), Ok = C("#1E8E55"), Warn = C("#B87412"), Scrim = C("#F3F5F9"),
    };

    static Color C(string hex) => Color.Parse(hex);
}

/// <summary>Building blocks of the prototype screens: text, icons, cards, chips, rows, buttons, bars.</summary>
sealed class ProtoKit(ProtoPalette p)
{
    public ProtoPalette P { get; } = p;

    public static readonly FontFamily Inter = new("avares://VitaSharp.Desktop/Assets/Fonts#Inter");
    public static readonly FontFamily Sora = new("avares://VitaSharp.Desktop/Assets/Fonts#Sora");

    public static IBrush B(Color c) => new SolidColorBrush(c);
    public static IBrush B(Color c, double alpha) => new SolidColorBrush(c, alpha);

    // ------------------------------------------------------------------ text

    public TextBlock Text(string text, double size = 14, FontWeight weight = FontWeight.Normal, Color? color = null, bool heading = false) => new()
    {
        Text = text, FontSize = size, FontWeight = weight, FontFamily = heading ? Sora : Inter,
        Foreground = B(color ?? P.Text), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
    };

    public TextBlock Heading(string text, double size) => Text(text, size, FontWeight.Bold, heading: true);

    public TextBlock Kicker(string text, Color? color = null)
    {
        var t = Text(text.ToUpperInvariant(), 11.5, FontWeight.SemiBold, color ?? P.Muted);
        t.LetterSpacing = 1.2;
        return t;
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
    };

    public Control Icon(string name, double size = 20, Color? color = null, double stroke = 1.8, bool fill = false)
    {
        var path = new Avalonia.Controls.Shapes.Path
        {
            Data = Geometry.Parse(IconPaths[name]),
            Stroke = B(color ?? P.Text), StrokeThickness = name == "more" ? 3.2 : stroke,
            StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round,
            Fill = fill ? B(color ?? P.Text) : null,
        };
        return new Viewbox
        {
            Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
            Child = new Canvas { Width = 24, Height = 24, Children = { path } },
        };
    }

    /// <summary>The square tile with an icon used by setting rows and the sidebar (Android "icon chip").</summary>
    public Border IconChip(string icon, double size = 40, Color? bg = null, Color? fg = null) => new()
    {
        Width = size, Height = size, CornerRadius = new CornerRadius(size * 0.3),
        Background = B(bg ?? P.Highlight), Child = Icon(icon, size * 0.5, fg ?? P.AccentSoft),
    };

    // ------------------------------------------------------------------ surfaces

    public Border Card(Control child, double radius = 20, Thickness? padding = null) => new()
    {
        Background = B(P.Surface), BorderBrush = B(P.Border), BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(radius), Padding = padding ?? new Thickness(20), Child = child,
    };

    public Border Divider(double inset = 16) => new() { Height = 1, Background = B(P.Border), Margin = new Thickness(inset, 0) };

    public static IBrush Image(string? path, Stretch stretch = Stretch.UniformToFill, AlignmentX ax = AlignmentX.Center, AlignmentY ay = AlignmentY.Center)
    {
        if (path == null || !File.Exists(path)) return Brushes.Transparent;
        return new ImageBrush(new Bitmap(path)) { Stretch = stretch, AlignmentX = ax, AlignmentY = ay };
    }

    /// <summary>A picture with rounded corners (cover art, thumbnails), or a tinted placeholder with initials.</summary>
    public Border Picture(string? path, double width, double height, double radius, string fallback = "", Color? tint = null)
    {
        var b = new Border { Width = width, Height = height, CornerRadius = new CornerRadius(radius), ClipToBounds = true };
        if (path != null && File.Exists(path)) b.Background = Image(path);
        else
        {
            b.Background = B(tint ?? P.SurfaceHigh);
            b.Child = new TextBlock
            {
                Text = fallback, FontFamily = Sora, FontWeight = FontWeight.Bold, FontSize = Math.Min(width, height) * 0.28,
                Foreground = B(P.Text, 0.85), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };
        }
        return b;
    }

    /// <summary>A gradient from transparent to <paramref name="to"/> (fades cover art into the page).</summary>
    public static LinearGradientBrush Fade(Color to, RelativePoint start, RelativePoint end, double from = 0, double toAlpha = 1) => new()
    {
        StartPoint = start, EndPoint = end,
        GradientStops = { new GradientStop(Color.FromArgb((byte)(from * 255), to.R, to.G, to.B), 0), new GradientStop(Color.FromArgb((byte)(toAlpha * 255), to.R, to.G, to.B), 1) },
    };

    // ------------------------------------------------------------------ controls (drawn as the styled Avalonia controls will look)

    public Border Chip(string text, bool selected, string? icon = null, bool dropdown = false)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        var fg = selected ? P.OnAccent : P.TextSoft;
        if (icon != null) row.Children.Add(Icon(icon, 15, fg));
        row.Children.Add(Text(text, 13, selected ? FontWeight.SemiBold : FontWeight.Medium, fg));
        if (dropdown) row.Children.Add(Icon("chevron-down", 14, P.Muted));
        return new Border
        {
            Height = 34, Padding = new Thickness(14, 0), CornerRadius = new CornerRadius(17), Child = row,
            Background = B(selected ? P.Accent : Colors.Transparent), BorderBrush = B(selected ? P.Accent : P.BorderStrong), BorderThickness = new Thickness(1),
        };
    }

    public Border PrimaryButton(string text, string? icon = "play", double height = 48, double minWidth = 150)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Center };
        if (icon != null) row.Children.Add(Icon(icon, icon == "play" ? 16 : 18, P.OnAccent, fill: icon == "play"));
        row.Children.Add(Text(text, 15, FontWeight.SemiBold, P.OnAccent));
        return new Border
        {
            Height = height, MinWidth = minWidth, Padding = new Thickness(22, 0), CornerRadius = new CornerRadius(16),
            Background = B(P.Accent), Child = row, BoxShadow = P.Dark ? default : BoxShadows.Parse("0 6 16 0 #333767DB"),
        };
    }

    public Border SecondaryButton(string text, string? icon = null, double height = 48, Color? fg = null)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 9, HorizontalAlignment = HorizontalAlignment.Center };
        if (icon != null) row.Children.Add(Icon(icon, 18, fg ?? P.Text));
        if (text.Length > 0) row.Children.Add(Text(text, 14, FontWeight.SemiBold, fg ?? P.Text));
        return new Border
        {
            Height = height, MinWidth = height, Padding = new Thickness(text.Length > 0 ? 18 : 0, 0), CornerRadius = new CornerRadius(16),
            Background = B(P.Surface), BorderBrush = B(P.BorderStrong), BorderThickness = new Thickness(1), Child = row,
        };
    }

    /// <summary>Round play button of the cards.</summary>
    public Border PlayDisc(double size) => new()
    {
        Width = size, Height = size, CornerRadius = new CornerRadius(size / 2), Background = B(P.Accent),
        Child = new Border { Margin = new Thickness(size * 0.08, 0, 0, 0), Child = Icon("play", size * 0.38, P.OnAccent, fill: true) },
    };

    public Border Switch(bool on) => new()
    {
        Width = 44, Height = 24, CornerRadius = new CornerRadius(12), Background = B(on ? P.Accent : P.Track),
        Child = new Border
        {
            Width = 18, Height = 18, CornerRadius = new CornerRadius(9), Margin = new Thickness(3, 0),
            HorizontalAlignment = on ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            Background = B(on ? P.OnAccent : (P.Dark ? P.Muted : Colors.White)),
        },
    };

    /// <summary>A closed drop-down (styled ComboBox) showing <paramref name="value"/>.</summary>
    public Border Dropdown(string value, double width = 210)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        g.Children.Add(Text(value, 13.5, FontWeight.Medium, P.Text));
        var chev = Icon("chevron-down", 16, P.Muted);
        Grid.SetColumn(chev, 1);
        g.Children.Add(chev);
        return new Border
        {
            Width = width, Height = 36, Padding = new Thickness(12, 0, 10, 0), CornerRadius = new CornerRadius(12),
            Background = B(P.Dark ? P.Background : P.Background), BorderBrush = B(P.BorderStrong), BorderThickness = new Thickness(1), Child = g,
        };
    }

    /// <summary>A segmented control (theme picker, view mode).</summary>
    public Border Segmented(params (string Text, string? Icon, bool Selected)[] items)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        foreach (var (text, icon, sel) in items)
        {
            var inner = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center };
            if (icon != null) inner.Children.Add(Icon(icon, 15, sel ? P.Text : P.Muted));
            if (text.Length > 0) inner.Children.Add(Text(text, 13, sel ? FontWeight.SemiBold : FontWeight.Medium, sel ? P.Text : P.Muted));
            row.Children.Add(new Border
            {
                Height = 30, MinWidth = 34, Padding = new Thickness(text.Length > 0 ? 12 : 8, 0), CornerRadius = new CornerRadius(9), Child = inner,
                Background = sel ? B(P.Dark ? P.SurfaceHigh : P.Surface) : Brushes.Transparent,
                BoxShadow = sel && !P.Dark ? BoxShadows.Parse("0 1 3 0 #22000000") : default,
            });
        }
        return new Border
        {
            Padding = new Thickness(3), CornerRadius = new CornerRadius(12), Child = row,
            Background = B(P.Dark ? P.Background : P.Sidebar), BorderBrush = B(P.Border), BorderThickness = new Thickness(1),
        };
    }

    public Grid Bar(double fraction, double height = 5, Color? fill = null)
    {
        fraction = Math.Clamp(fraction, 0, 1);
        var g = new Grid { Height = height, VerticalAlignment = VerticalAlignment.Center, ColumnDefinitions = new ColumnDefinitions($"{fraction * 1000:F0}*,{(1 - fraction) * 1000:F0}*") };
        g.Children.Add(new Border { Background = B(P.Track), CornerRadius = new CornerRadius(height / 2), [Grid.ColumnSpanProperty] = 2 });
        g.Children.Add(new Border { Background = B(fill ?? P.Accent), CornerRadius = new CornerRadius(height / 2) });
        return g;
    }

    public Border Badge(string text, Color fg, Color? bg = null) => new()
    {
        Padding = new Thickness(9, 3), CornerRadius = new CornerRadius(8), Background = B(bg ?? fg, bg == null ? 0.16 : 1),
        Child = Text(text, 11.5, FontWeight.SemiBold, fg), VerticalAlignment = VerticalAlignment.Center,
    };

    public Border Dot(Color c, double size = 8) => new() { Width = size, Height = size, CornerRadius = new CornerRadius(size / 2), Background = B(c), VerticalAlignment = VerticalAlignment.Center };

    /// <summary>A setting row: icon chip, title, detail, then a value or a control on the right.</summary>
    public Grid Row(string? icon, string title, string? detail = null, Control? right = null, bool chevron = false)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"), MinHeight = 56, Margin = new Thickness(16, 7) };
        if (icon != null) g.Children.Add(new Border { Margin = new Thickness(0, 0, 14, 0), Child = IconChip(icon, 38) });
        var texts = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(Text(title, 14.5, FontWeight.Medium));
        if (detail != null) texts.Children.Add(Text(detail, 12.5, color: P.Muted));
        Grid.SetColumn(texts, 1);
        g.Children.Add(texts);
        if (right != null)
        {
            right.VerticalAlignment = VerticalAlignment.Center;
            right.Margin = new Thickness(16, 0, 0, 0);
            Grid.SetColumn(right, 2);
            g.Children.Add(right);
        }
        if (chevron)
        {
            var c = Icon("chevron-right", 18, P.Faint);
            c.Margin = new Thickness(10, 0, 0, 0);
            Grid.SetColumn(c, 3);
            g.Children.Add(c);
        }
        return g;
    }

    /// <summary>A card of rows separated by dividers (the Android grouped list).</summary>
    public Border Group(params Control[] rows)
    {
        var s = new StackPanel();
        for (int i = 0; i < rows.Length; i++)
        {
            if (i > 0) s.Children.Add(Divider());
            s.Children.Add(rows[i]);
        }
        return Card(s, 18, new Thickness(0, 2));
    }

    public static T At<T>(T c, int row = 0, int col = 0, int colSpan = 1, int rowSpan = 1) where T : Control
    {
        Grid.SetRow(c, row);
        Grid.SetColumn(c, col);
        if (colSpan > 1) Grid.SetColumnSpan(c, colSpan);
        if (rowSpan > 1) Grid.SetRowSpan(c, rowSpan);
        return c;
    }
}
