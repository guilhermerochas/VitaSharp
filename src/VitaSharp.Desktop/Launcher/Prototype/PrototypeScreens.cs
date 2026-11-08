using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using VitaSharp.Core.Hle.Np;
using VitaSharp.Core.Package;

namespace VitaSharp.Desktop.Launcher.Prototype;

/// <summary>A library entry of the prototype: what the scan reads (title, ID, version, icon0, pic0) plus sample stats.</summary>
sealed record ProtoGame(string TitleId, string Title, string Version, string Path, string? Icon, string? Pic0, string Category)
{
    /// <summary>Region from the title ID (PCSA/PCSE US, PCSB/PCSF EU, PCSC/PCSG JP, PCSD/PCSH Asia).</summary>
    public string Region => TitleId.Length >= 4 && TitleId.StartsWith("PCS") ? TitleId[3] switch
    {
        'A' or 'E' => "EUA", 'B' or 'F' => "Europa", 'C' or 'G' => "Japão", 'D' or 'H' => "Ásia", _ => "Outra",
    } : "Homebrew";

    public bool Playable => Category == "gd";

    // sample numbers until the real play-time and trophy data are wired in (stage 2)
    int Seed => Math.Abs(TitleId.GetHashCode(StringComparison.Ordinal)) % 1000;
    public int TrophyTotal { get; set; } = 0;
    public int Total => TrophyTotal > 0 ? TrophyTotal : 20 + Seed % 40;
    public int TrophyGot => 1 + Seed * 7 % Total * 3 / 4;

    public string Initials => string.Concat(Title.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(w => char.ToUpperInvariant(w[0])));

    public static List<ProtoGame> Scan(string ux0)
    {
        var list = new List<ProtoGame>();
        var appDir = System.IO.Path.Combine(ux0, "app");
        if (!Directory.Exists(appDir)) return list;
        foreach (var dir in Directory.GetDirectories(appDir))
        {
            string id = System.IO.Path.GetFileName(dir), title = id, version = "", category = "gd";
            var sfoPath = Core.Io.Vfs.ResolveCaseInsensitive(dir, "sce_sys/param.sfo");
            try
            {
                if (File.Exists(sfoPath))
                {
                    var sfo = Sfo.Load(sfoPath);
                    if (sfo.Title.Length > 0) title = sfo.Title.Replace('\n', ' ');
                    if (sfo.TitleId.Length > 0) id = sfo.TitleId;
                    version = sfo.AppVersion.TrimStart('0');
                    if (version.StartsWith('.')) version = "0" + version;
                    category = sfo.Category;
                }
            }
            catch (InvalidDataException) { }
            string? File_(string rel) => Core.Io.Vfs.ResolveCaseInsensitive(dir, rel) is var p && File.Exists(p) ? p : null;
            list.Add(new ProtoGame(id, title, version, dir, File_("sce_sys/icon0.png"), File_("sce_sys/pic0.png"), category));
        }
        return list.OrderBy(g => g.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
    }
}

/// <summary>
/// The proposed launcher screens (stage 1 of "Sugestão de layout novo para o launcher do desktop"): sidebar navigation,
/// library with large covers, game page, settings by section, light and dark. Static mock-ups built from the real
/// controls; nothing here is interactive yet.
/// </summary>
sealed class PrototypeScreens(ProtoKit k, List<ProtoGame> games, ProtoGame hero)
{
    ProtoPalette P => k.P;

    public const string Library = "library", Game = "game", Settings = "settings";

    public Control Build(string screen)
    {
        // the hero's real trophy count (its package), sample progress
        if (TrophySet() is { } set) hero.TrophyTotal = set.Trophies.Count;
        var root = new Grid { ColumnDefinitions = new ColumnDefinitions("252,*"), Background = ProtoKit.B(P.Background) };
        root.Children.Add(Sidebar(screen));
        var content = screen switch { Game => GamePage(), Settings => SettingsPage(), _ => LibraryPage() };
        root.Children.Add(ProtoKit.At(new Border { ClipToBounds = true, Child = content }, col: 1));
        return root;
    }

    // ------------------------------------------------------------------ sidebar

    Control Sidebar(string screen)
    {
        var s = new DockPanel { Margin = new Thickness(16, 22, 16, 18) };

        var brand = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 11, Margin = new Thickness(8, 0, 0, 26) };
        using (var logo = typeof(PrototypeScreens).Assembly.GetManifestResourceStream("icon-64.png"))
            if (logo != null) brand.Children.Add(new Image { Source = new Bitmap(logo), Width = 34, Height = 34 });
        brand.Children.Add(k.Heading("VitaSharp", 20));
        DockPanel.SetDock(brand, Dock.Top);
        s.Children.Add(brand);

        var bottom = new StackPanel { Spacing = 12 };
        // pkg/vpk installation in progress (also the drop target for files dragged onto the window)
        var install = new StackPanel { Spacing = 8 };
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        head.Children.Add(new Border { Margin = new Thickness(0, 0, 8, 0), Child = k.Icon("download", 16, P.Accent) });
        head.Children.Add(ProtoKit.At(k.Text("Instalando", 12.5, FontWeight.SemiBold, P.Accent), col: 1));
        head.Children.Add(ProtoKit.At(k.Text("62%", 12.5, FontWeight.SemiBold, P.TextSoft), col: 2));
        install.Children.Add(head);
        install.Children.Add(k.Text("PCSB00245.pkg", 13.5, FontWeight.Medium));
        install.Children.Add(k.Bar(0.62, 5));
        install.Children.Add(k.Text("1,9 de 3,1 GB · 40 s restantes", 11.5, color: P.Muted));
        bottom.Children.Add(new Border
        {
            Background = ProtoKit.B(P.Surface), BorderBrush = ProtoKit.B(P.Border), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16), Padding = new Thickness(14, 12), Child = install,
        });
        var fw = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(6, 2, 0, 0) };
        fw.Children.Add(k.Dot(P.Ok));
        fw.Children.Add(k.Text("Firmware 3.74 instalado", 12.5, color: P.Muted));
        bottom.Children.Add(fw);
        bottom.Children.Add(k.Segmented(("Escuro", "moon", P.Dark), ("Claro", "sun", !P.Dark)));
        DockPanel.SetDock(bottom, Dock.Bottom);
        s.Children.Add(bottom);

        var nav = new StackPanel { Spacing = 4 };
        nav.Children.Add(NavItem("library", "Biblioteca", screen is Library or Game, $"{games.Count}"));
        nav.Children.Add(NavItem("vita", "Sistema do Vita", false));
        nav.Children.Add(NavItem("trophy", "Troféus", false));
        nav.Children.Add(NavItem("image", "Capturas", false));
        nav.Children.Add(NavItem("download", "Instalar pkg / vpk", false));
        nav.Children.Add(new Border { Height = 1, Background = ProtoKit.B(P.Border), Margin = new Thickness(8, 12) });
        nav.Children.Add(NavItem("settings", "Configurações", screen == Settings));
        s.Children.Add(nav);

        return new Border
        {
            Background = ProtoKit.B(P.Sidebar), BorderBrush = ProtoKit.B(P.Border), BorderThickness = new Thickness(0, 0, 1, 0), Child = s,
        };
    }

    Control NavItem(string icon, string text, bool selected, string? count = null)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        g.Children.Add(new Border { Margin = new Thickness(0, 0, 12, 0), Child = k.Icon(icon, 19, selected ? P.Accent : P.Muted) });
        g.Children.Add(ProtoKit.At(k.Text(text, 14, selected ? FontWeight.SemiBold : FontWeight.Medium, selected ? P.Text : P.TextSoft), col: 1));
        if (count != null) g.Children.Add(ProtoKit.At(k.Text(count, 12, FontWeight.Medium, P.Muted), col: 2));
        return new Border
        {
            Height = 42, Padding = new Thickness(12, 0), CornerRadius = new CornerRadius(12), Child = g,
            Background = selected ? ProtoKit.B(P.Highlight) : Brushes.Transparent,
            BorderBrush = selected ? ProtoKit.B(P.HighlightBorder) : Brushes.Transparent, BorderThickness = new Thickness(1),
        };
    }

    // ------------------------------------------------------------------ library

    Control LibraryPage()
    {
        var page = new StackPanel { Margin = new Thickness(36, 28, 36, 0), Spacing = 0 };

        // title + search + install
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), Margin = new Thickness(0, 0, 0, 20) };
        var titles = new StackPanel { Spacing = 2 };
        titles.Children.Add(k.Heading("Biblioteca", 30));
        int playable = games.Count(g => g.Playable);
        titles.Children.Add(k.Text($"{games.Count} jogos instalados · {playable} jogáveis", 13.5, color: P.Muted));
        header.Children.Add(titles);
        var search = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        search.Children.Add(new Border { Margin = new Thickness(0, 0, 10, 0), Child = k.Icon("search", 18, P.Muted) });
        search.Children.Add(ProtoKit.At(k.Text("Buscar por nome ou ID", 14, color: P.Faint), col: 1));
        search.Children.Add(ProtoKit.At(new Border
        {
            Padding = new Thickness(7, 2), CornerRadius = new CornerRadius(6), BorderBrush = ProtoKit.B(P.BorderStrong), BorderThickness = new Thickness(1),
            Child = k.Text("Ctrl F", 11, FontWeight.Medium, P.Faint),
        }, col: 2));
        header.Children.Add(ProtoKit.At(new Border
        {
            Width = 340, Height = 46, Padding = new Thickness(14, 0, 10, 0), CornerRadius = new CornerRadius(14), Margin = new Thickness(0, 0, 12, 0),
            Background = ProtoKit.B(P.Surface), BorderBrush = ProtoKit.B(P.BorderStrong), BorderThickness = new Thickness(1), Child = search,
            VerticalAlignment = VerticalAlignment.Center,
        }, col: 1));
        var add = k.PrimaryButton("Instalar", "plus", 46, 0);
        add.VerticalAlignment = VerticalAlignment.Center;
        header.Children.Add(ProtoKit.At(add, col: 2));
        page.Children.Add(header);

        // filters
        var filters = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 0, 0, 22) };
        var chips = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        chips.Children.Add(k.Chip($"Todos · {games.Count}", true));
        chips.Children.Add(k.Chip($"Jogáveis · {playable}", false));
        chips.Children.Add(k.Chip("Recentes", false));
        chips.Children.Add(k.Chip("Com troféus", false));
        chips.Children.Add(new Border { Width = 1, Height = 22, Background = ProtoKit.B(P.BorderStrong), Margin = new Thickness(6, 0), VerticalAlignment = VerticalAlignment.Center });
        chips.Children.Add(k.Chip("Região: todas", false, "globe", dropdown: true));
        chips.Children.Add(k.Chip("Ordenar: A–Z", false, dropdown: true));
        filters.Children.Add(chips);
        filters.Children.Add(ProtoKit.At(k.Segmented(("", "grid", true), ("", "list", false), ("", "vita", false)), col: 1));
        page.Children.Add(filters);

        page.Children.Add(HeroCard());

        var section = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 28, 0, 14) };
        section.Children.Add(k.Text("Todos os jogos", 17, FontWeight.SemiBold));
        section.Children.Add(ProtoKit.At(k.Text("A–Z", 13, FontWeight.Medium, P.Muted), col: 1));
        page.Children.Add(section);

        // as many columns as fit (cards at least 240 px wide), the gap is each card's right/bottom margin
        var grid = new UniformGrid { Columns = 4, Margin = new Thickness(0, 0, -18, 0) };
        foreach (var g in games.Where(g => g != hero).Take(8)) grid.Children.Add(GameCard(g));
        page.Children.Add(grid);
        return page;
    }

    Control HeroCard()
    {
        var card = new Grid { Height = 232 };
        card.Children.Add(new Border { CornerRadius = new CornerRadius(24), Background = ProtoKit.Image(hero.Pic0) });
        card.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(24),
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(250, P.Scrim.R, P.Scrim.G, P.Scrim.B), 0),
                    new GradientStop(Color.FromArgb(225, P.Scrim.R, P.Scrim.G, P.Scrim.B), 0.42),
                    new GradientStop(Color.FromArgb(40, P.Scrim.R, P.Scrim.G, P.Scrim.B), 1),
                },
            },
        });
        card.Children.Add(new Border { CornerRadius = new CornerRadius(24), BorderBrush = ProtoKit.B(P.Border), BorderThickness = new Thickness(1) });

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(28, 0) };
        var icon = k.Picture(hero.Icon, 132, 132, 30, hero.Initials);
        icon.VerticalAlignment = VerticalAlignment.Center;
        icon.BoxShadow = BoxShadows.Parse("0 10 30 0 #55000000");
        row.Children.Add(icon);
        var info = new StackPanel { Spacing = 7, Margin = new Thickness(26, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, MaxWidth = 520, HorizontalAlignment = HorizontalAlignment.Left };
        info.Children.Add(k.Kicker("Continuar jogando", P.Accent));
        info.Children.Add(k.Heading(hero.Title, 26));
        var meta = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 };
        meta.Children.Add(IconText("clock", "12h 40m · ontem"));
        meta.Children.Add(IconText("trophy", $"{hero.TrophyGot}/{hero.Total}", P.Gold));
        meta.Children.Add(new Border { Width = 120, Child = k.Bar(hero.TrophyGot / (double)hero.Total) });
        info.Children.Add(meta);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(0, 10, 0, 0) };
        buttons.Children.Add(k.PrimaryButton("Jogar", "play", 46, 140));
        buttons.Children.Add(k.SecondaryButton("Carregar estado de ontem", "save", 46));
        info.Children.Add(buttons);
        row.Children.Add(ProtoKit.At(info, col: 1));
        card.Children.Add(row);
        return card;
    }

    Control IconText(string icon, string text, Color? iconColor = null, double size = 13)
    {
        var s = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        s.Children.Add(k.Icon(icon, size + 2, iconColor ?? P.Muted));
        s.Children.Add(k.Text(text, size, color: P.TextSoft));
        return s;
    }

    Control GameCard(ProtoGame g)
    {
        const double ArtH = 150;
        var art = new Grid { Height = ArtH };
        art.Children.Add(new Border { Background = ProtoKit.Image(g.Pic0), CornerRadius = new CornerRadius(18, 18, 0, 0) });
        art.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(18, 18, 0, 0),
            Background = ProtoKit.Fade(P.Surface, new RelativePoint(0, 0.35, RelativeUnit.Relative), new RelativePoint(0, 1, RelativeUnit.Relative)),
        });
        var icon = k.Picture(g.Icon, 64, 64, 16, g.Initials);
        icon.HorizontalAlignment = HorizontalAlignment.Left;
        icon.VerticalAlignment = VerticalAlignment.Bottom;
        icon.Margin = new Thickness(14, 0, 0, -6);
        icon.BorderBrush = ProtoKit.B(P.Surface);
        icon.BorderThickness = new Thickness(2);
        art.Children.Add(icon);
        art.Children.Add(new Border
        {
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(10),
            Padding = new Thickness(8, 3), CornerRadius = new CornerRadius(8), Background = ProtoKit.B(Color.Parse("#0B0E13"), 0.62),
            Child = k.Text(g.Region, 11, FontWeight.SemiBold, Color.Parse("#E8ECF2")),
        });

        var text = new StackPanel { Spacing = 4, Margin = new Thickness(14, 14, 14, 14) };
        text.Children.Add(k.Text(g.Title, 14.5, FontWeight.SemiBold));
        text.Children.Add(k.Text($"{g.TitleId} · v{g.Version}", 12, color: P.Muted));
        var foot = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(0, 6, 0, 0) };
        foot.Children.Add(g.Playable ? k.Badge("Jogável", P.Accent) : k.Badge("Atualização", P.Warn));
        var tro = IconText("trophy", $"{g.TrophyGot}/{g.Total}", P.Gold, 12);
        foot.Children.Add(ProtoKit.At(tro, col: 2));
        text.Children.Add(foot);

        var stack = new StackPanel();
        stack.Children.Add(art);
        stack.Children.Add(text);
        return new Border
        {
            Margin = new Thickness(0, 0, 18, 18), CornerRadius = new CornerRadius(18), Background = ProtoKit.B(P.Surface), ClipToBounds = true,
            BorderBrush = ProtoKit.B(P.Border), BorderThickness = new Thickness(1), Child = stack,
            BoxShadow = P.Dark ? default : BoxShadows.Parse("0 2 8 0 #14000000"),
        };
    }

    // ------------------------------------------------------------------ game page

    Control GamePage()
    {
        var root = new Grid();
        // LiveArea/pic0 art behind the top of the page, fading into the background
        root.Children.Add(new Border { Height = 380, VerticalAlignment = VerticalAlignment.Top, Background = ProtoKit.Image(hero.Pic0, ay: AlignmentY.Top) });
        root.Children.Add(new Border
        {
            Height = 380, VerticalAlignment = VerticalAlignment.Top,
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(70, P.Scrim.R, P.Scrim.G, P.Scrim.B), 0),
                    new GradientStop(Color.FromArgb(150, P.Scrim.R, P.Scrim.G, P.Scrim.B), 0.45),
                    new GradientStop(P.Background, 1),
                },
            },
        });

        var page = new StackPanel { Margin = new Thickness(36, 24, 36, 0) };
        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto") };
        top.Children.Add(GlassButton("chevron-left", "Biblioteca"));
        top.Children.Add(ProtoKit.At(GlassButton("folder", null), col: 2));
        var more = GlassButton("more", null);
        more.Margin = new Thickness(8, 0, 0, 0);
        top.Children.Add(ProtoKit.At(more, col: 3));
        page.Children.Add(top);

        // identity block
        var id = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(0, 110, 0, 0) };
        var icon = k.Picture(hero.Icon, 148, 148, 34, hero.Initials);
        icon.BoxShadow = BoxShadows.Parse("0 12 36 0 #66000000");
        id.Children.Add(icon);
        var info = new StackPanel { Spacing = 8, Margin = new Thickness(28, 0, 0, 0), VerticalAlignment = VerticalAlignment.Bottom };
        var badges = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        badges.Children.Add(k.Badge("Jogável", P.Accent));
        badges.Children.Add(k.Badge(hero.Region, P.TextSoft));
        info.Children.Add(badges);
        info.Children.Add(k.Heading(hero.Title, 34));
        info.Children.Add(k.Text($"{hero.TitleId}  ·  v{hero.Version}  ·  3,2 GB  ·  instalado em 02/11/2026", 13.5, color: P.Muted));
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(0, 10, 0, 0) };
        buttons.Children.Add(k.PrimaryButton("Jogar", "play", 50, 170));
        buttons.Children.Add(k.SecondaryButton("Abrir na LiveArea", "vita", 50));
        buttons.Children.Add(k.SecondaryButton("Carregar estado", "save", 50));
        info.Children.Add(buttons);
        id.Children.Add(ProtoKit.At(info, col: 1));
        page.Children.Add(id);

        // stats
        var stats = new UniformGrid { Columns = 4, Margin = new Thickness(0, 26, 0, 0) };
        stats.Children.Add(Stat("clock", "Tempo jogado", "12h 40m"));
        stats.Children.Add(Stat("zap", "Última vez", "Ontem, 21:14"));
        stats.Children.Add(Stat("gauge", "FPS médio", "30"));
        stats.Children.Add(Stat("trophy", "Troféus", $"{hero.TrophyGot} / {hero.Total}", P.Gold));
        page.Children.Add(stats);

        // tabs
        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 26, Margin = new Thickness(0, 26, 0, 0) };
        foreach (var (t, sel) in new[] { ("Visão geral", true), ("Estados salvos · 4", false), ("Troféus", false), ("Capturas · 12", false), ("Ajustes do jogo", false) })
        {
            var tab = new StackPanel { Spacing = 9 };
            tab.Children.Add(k.Text(t, 14, sel ? FontWeight.SemiBold : FontWeight.Medium, sel ? P.Text : P.Muted));
            tab.Children.Add(new Border { Height = 3, CornerRadius = new CornerRadius(2), Background = sel ? ProtoKit.B(P.Accent) : Brushes.Transparent });
            tabs.Children.Add(tab);
        }
        page.Children.Add(tabs);
        page.Children.Add(new Border { Height = 1, Background = ProtoKit.B(P.Border), Margin = new Thickness(0, -1, 0, 22) });

        var cols = new Grid { ColumnDefinitions = new ColumnDefinitions("*,24,400") };
        var left = new StackPanel { Spacing = 22 };
        left.Children.Add(SaveStates());
        left.Children.Add(Trophies());
        cols.Children.Add(left);
        cols.Children.Add(ProtoKit.At(GameSettings(), col: 2));
        page.Children.Add(cols);

        root.Children.Add(page);
        return root;
    }

    Border GlassButton(string icon, string? text)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center };
        row.Children.Add(k.Icon(icon, 18, Color.Parse("#E8ECF2")));
        if (text != null) row.Children.Add(k.Text(text, 13.5, FontWeight.Medium, Color.Parse("#E8ECF2")));
        return new Border
        {
            Height = 40, MinWidth = 40, Padding = new Thickness(text != null ? 12 : 0, 0, text != null ? 16 : 0, 0), CornerRadius = new CornerRadius(13),
            Background = ProtoKit.B(Color.Parse("#0B0E13"), 0.55), BorderBrush = ProtoKit.B(Colors.White, 0.12), BorderThickness = new Thickness(1), Child = row,
        };
    }

    Control Stat(string icon, string label, string value, Color? iconColor = null)
    {
        var s = new StackPanel { Spacing = 6 };
        var l = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
        l.Children.Add(k.Icon(icon, 15, iconColor ?? P.Muted));
        l.Children.Add(k.Text(label, 12.5, color: P.Muted));
        s.Children.Add(l);
        s.Children.Add(k.Heading(value, 20));
        var card = k.Card(s, 18, new Thickness(18, 14));
        card.Margin = new Thickness(0, 0, 14, 0);
        return card;
    }

    Control SectionHeader(string title, string? link)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 0, 0, 12) };
        g.Children.Add(k.Text(title, 16, FontWeight.SemiBold));
        if (link != null) g.Children.Add(ProtoKit.At(k.Text(link, 13, FontWeight.SemiBold, P.Accent), col: 1));
        return g;
    }

    Control SaveStates()
    {
        var s = new StackPanel();
        s.Children.Add(SectionHeader("Estados salvos", "Ver todos"));
        var row = new UniformGrid { Columns = 4 };
        var slots = new[] { ("Rápido", "ontem, 21:14", AlignmentX.Left), ("Slot 1", "ontem, 20:02", AlignmentX.Center), ("Slot 2", "05/11, 22:47", AlignmentX.Right) };
        foreach (var (name, when, ax) in slots)
        {
            var c = new StackPanel { Spacing = 8, Margin = new Thickness(0, 0, 14, 0) };
            var thumb = new Grid { Height = 92 };
            // stand-in for the state's screenshot: crops of the cover art
            var crop = ProtoKit.Image(hero.Pic0, ax: ax);
            if (crop is ImageBrush ib) ib.Transform = new ScaleTransform(1.9, 1.9);
            if (crop is ImageBrush ib2) ib2.TransformOrigin = new RelativePoint(ax == AlignmentX.Left ? 0.15 : ax == AlignmentX.Right ? 0.85 : 0.5, ax == AlignmentX.Center ? 0.85 : 0.3, RelativeUnit.Relative);
            thumb.Children.Add(new Border { CornerRadius = new CornerRadius(14), Background = crop });
            thumb.Children.Add(new Border { CornerRadius = new CornerRadius(14), BorderBrush = ProtoKit.B(P.Border), BorderThickness = new Thickness(1) });
            c.Children.Add(thumb);
            c.Children.Add(k.Text(name, 13.5, FontWeight.SemiBold));
            c.Children.Add(k.Text(when, 12, color: P.Muted));
            row.Children.Add(c);
        }
        var add = new StackPanel { Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        add.Children.Add(k.Icon("plus", 22, P.Muted));
        add.Children.Add(k.Text("Salvar no jogo", 12, color: P.Muted));
        row.Children.Add(new Border
        {
            Height = 92, VerticalAlignment = VerticalAlignment.Top, CornerRadius = new CornerRadius(14), Child = add,
            BorderBrush = ProtoKit.B(P.BorderStrong), BorderThickness = new Thickness(1.5), Background = ProtoKit.B(P.Surface),
        });
        s.Children.Add(row);
        return s;
    }

    Control Trophies()
    {
        var s = new StackPanel();
        s.Children.Add(SectionHeader("Troféus", "Ver lista completa"));
        var body = new StackPanel { Spacing = 14 };
        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var dots = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
        var pkg = TrophySet();
        int Count(TrophyGrade g) => pkg?.Trophies.Count(t => t.Grade == g) ?? 0;
        foreach (var (grade, color) in new[] { (TrophyGrade.Platinum, P.Platinum), (TrophyGrade.Gold, P.Gold), (TrophyGrade.Silver, P.Silver), (TrophyGrade.Bronze, P.Bronze) })
        {
            int total = Count(grade);
            var d = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            d.Children.Add(k.Dot(color, 9));
            d.Children.Add(k.Text($"{(total * hero.TrophyGot + hero.Total / 2) / hero.Total}/{total}", 13, FontWeight.Medium, P.TextSoft));
            dots.Children.Add(d);
        }
        top.Children.Add(dots);
        top.Children.Add(ProtoKit.At(k.Text($"{hero.TrophyGot * 100 / hero.Total}%", 14, FontWeight.SemiBold, P.Accent), col: 1));
        body.Children.Add(top);
        body.Children.Add(k.Bar(hero.TrophyGot / (double)hero.Total, 6));
        var recent = new UniformGrid { Columns = 3, Margin = new Thickness(0, 4, 0, 0) };
        foreach (var t in pkg?.Trophies.Where(t => !t.Hidden).Skip(1).Take(3) ?? [])
        {
            var r = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(0, 0, 12, 0) };
            var png = pkg!.TrophyIcon(t.Id);
            var pic = new Border { Width = 44, Height = 44, CornerRadius = new CornerRadius(10), Background = ProtoKit.B(P.SurfaceHigh) };
            if (png != null) pic.Background = new ImageBrush(new Bitmap(new MemoryStream(png))) { Stretch = Stretch.UniformToFill };
            r.Children.Add(pic);
            var tx = new StackPanel { Spacing = 2, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            tx.Children.Add(k.Text(t.Name, 13, FontWeight.SemiBold));
            tx.Children.Add(k.Text(t.Grade switch { TrophyGrade.Gold => "Ouro", TrophyGrade.Silver => "Prata", TrophyGrade.Platinum => "Platina", _ => "Bronze" } + " · ontem", 11.5, color: P.Muted));
            r.Children.Add(ProtoKit.At(tx, col: 1));
            recent.Children.Add(r);
        }
        body.Children.Add(recent);
        s.Children.Add(k.Card(body, 18, new Thickness(20, 18)));
        return s;
    }

    TrophyPackage? _trophies;
    bool _trophiesRead;

    TrophyPackage? TrophySet()
    {
        if (_trophiesRead) return _trophies;
        _trophiesRead = true;
        try
        {
            if (TrophyPackage.FindSets(hero.Path).FirstOrDefault() is { TrpPath: not null } set) _trophies = TrophyPackage.Load(set.TrpPath, set.CommId);
        }
        catch (Exception e) when (e is IOException or InvalidDataException) { }
        return _trophies;
    }

    /// <summary>"Ajustes do jogo": the per-game options (the internal resolution and frame-rate controls of the
    /// performance branch are placed here as they are, not redesigned).</summary>
    Control GameSettings()
    {
        var s = new StackPanel();
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 0, 0, 12) };
        head.Children.Add(k.Text("Ajustes deste jogo", 16, FontWeight.SemiBold));
        head.Children.Add(ProtoKit.At(k.Text("Usar os globais", 13, FontWeight.SemiBold, P.Accent), col: 1));
        s.Children.Add(head);
        s.Children.Add(k.Group(
            k.Row("monitor", "Resolução interna", "Mais nítido, custa GPU", k.Dropdown("2x (1920×1088)", 160)),
            k.Row("gauge", "Taxa de quadros", "Limite deste jogo", k.Dropdown("Do jogo (30)", 160)),
            k.Row("gamepad", "Controles", "DualSense · perfil próprio", chevron: true),
            k.Row("cpu", "CPU", "Recompilador (padrão)", chevron: true),
            k.Row("zap", "Mostrar FPS", null, k.Switch(true))));
        var note = k.Text("Vale na próxima vez que o jogo abrir.", 12, color: P.Muted);
        note.Margin = new Thickness(6, 10, 0, 0);
        s.Children.Add(note);
        return s;
    }

    // ------------------------------------------------------------------ settings

    Control SettingsPage()
    {
        var page = new Grid { ColumnDefinitions = new ColumnDefinitions("220,*"), Margin = new Thickness(36, 28, 36, 0) };

        var nav = new StackPanel { Spacing = 2 };
        nav.Children.Add(new Border { Margin = new Thickness(0, 0, 0, 22), Child = k.Heading("Configurações", 26) });
        foreach (var (icon, text, sel) in new[]
                 {
                     ("settings", "Geral", false), ("vita", "Sistema do Vita", true), ("monitor", "Gráficos", false), ("volume", "Áudio", false),
                     ("gamepad", "Controles", false), ("user", "Rede e PSN", false), ("trophy", "Troféus", false), ("hdd", "Armazenamento", false),
                     ("terminal", "Avançado", false),
                 })
        {
            var g = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            g.Children.Add(k.Icon(icon, 18, sel ? P.Accent : P.Muted));
            g.Children.Add(k.Text(text, 14, sel ? FontWeight.SemiBold : FontWeight.Medium, sel ? P.Text : P.TextSoft));
            nav.Children.Add(new Border
            {
                Height = 40, Padding = new Thickness(12, 0), CornerRadius = new CornerRadius(11), Child = g,
                Background = sel ? ProtoKit.B(P.Highlight) : Brushes.Transparent,
            });
        }
        page.Children.Add(nav);

        var body = new StackPanel { Spacing = 10, Margin = new Thickness(36, 0, 0, 0), MaxWidth = 860, HorizontalAlignment = HorizontalAlignment.Left };
        var search = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        search.Children.Add(new Border { Margin = new Thickness(0, 0, 10, 0), Child = k.Icon("search", 17, P.Muted) });
        search.Children.Add(ProtoKit.At(k.Text("Buscar um ajuste", 14, color: P.Faint), col: 1));
        body.Children.Add(new Border
        {
            Height = 44, Padding = new Thickness(14, 0), CornerRadius = new CornerRadius(14), Margin = new Thickness(0, 0, 0, 12),
            Background = ProtoKit.B(P.Surface), BorderBrush = ProtoKit.B(P.BorderStrong), BorderThickness = new Thickness(1), Child = search,
        });

        body.Children.Add(Group("Sistema do Vita"));
        body.Children.Add(k.Group(
            k.Row("cpu", "Firmware", "3.74 · PSVUPDAT.PUP verificado · fontes instaladas", k.Badge("Instalado", P.Ok), chevron: true),
            k.Row("vita", "Abrir o Sistema do Vita", "shell.self do firmware: tela inicial, LiveArea e apps do sistema", k.SecondaryButton("Abrir", "play", 36)),
            k.Row("globe", "Idioma do sistema", null, k.Dropdown("Português (Brasil)", 200)),
            k.Row("gamepad", "Botão de confirmar", "Usado pelos jogos e pelo sistema", k.Segmented(("✕ Cruz", null, true), ("○ Círculo", null, false)))));

        body.Children.Add(Group("Aparência"));
        body.Children.Add(k.Group(
            k.Row(P.Dark ? "moon" : "sun", "Tema", "Segue o Windows por padrão", k.Segmented(("Sistema", null, false), ("Escuro", "moon", P.Dark), ("Claro", "sun", !P.Dark))),
            k.Row("library", "Ao abrir o VitaSharp", null, k.Segmented(("Biblioteca", null, true), ("Sistema do Vita", null, false))),
            k.Row("volume", "Música do sistema na tela inicial", null, k.Switch(true))));

        body.Children.Add(Group("Gráficos"));
        body.Children.Add(k.Group(
            k.Row("monitor", "Iniciar em tela cheia", "F11 alterna durante o jogo", k.Switch(false)),
            k.Row("layers", "Compilar shaders em segundo plano", "Menos engasgos na primeira vez", k.Switch(true))));
        page.Children.Add(ProtoKit.At(body, col: 1));
        return page;
    }

    Control Group(string title)
    {
        var t = k.Kicker(title);
        t.Margin = new Thickness(6, 14, 0, 4);
        return t;
    }
}
