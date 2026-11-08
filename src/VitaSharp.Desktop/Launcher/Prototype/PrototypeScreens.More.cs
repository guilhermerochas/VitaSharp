using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using VitaSharp.Core.Hle.Np;

namespace VitaSharp.Desktop.Launcher.Prototype;

/// <summary>A game's trophy set with the unlocks the emulator recorded (read from the data folder).</summary>
sealed record ProtoTrophySet(ProtoGame Game, TrophyPackage Package, Dictionary<int, ulong> Unlocked)
{
    public int Got => Package.Trophies.Count(t => Unlocked.ContainsKey(t.Id));
    public int Total => Package.Trophies.Count;

    /// <summary>Unlock time: microseconds since 0001-01-01 UTC (SceRtcTick).</summary>
    public DateTime? When(int id) => Unlocked.TryGetValue(id, out var us) ? new DateTime((long)us * 10, DateTimeKind.Utc).ToLocalTime() : null;
}

/// <summary>A file of the Capturas page: an F8 "Gravar tudo" recording or a GPU dump.</summary>
sealed record ProtoCapture(string Kind, string TitleId, string Name, long Size, DateTime When, string? Thumb);

/// <summary>What the extra screens read from the data folder: firmware state, trophies, captures.</summary>
sealed class ProtoData
{
    public bool FirmwareInstalled { get; private set; }
    public string FirmwareVersion { get; private set; } = "";
    public string FirmwareBuild { get; private set; } = "";
    public string FirmwareSource { get; private set; } = "";
    public DateTime? FirmwareDate { get; private set; }
    public bool FontsInstalled { get; private set; }
    public bool Pd0 { get; private set; }
    public List<ProtoTrophySet> TrophySets { get; } = new();
    public List<ProtoCapture> Captures { get; } = new();

    public string FirmwareDetail => FirmwareInstalled
        ? $"{FirmwareVersion} · build {FirmwareBuild} · {FirmwareSource}" + (FontsInstalled ? " · fontes instaladas" : " · sem o pacote de fontes")
        : "Não instalado";

    static readonly CultureInfo Pt = new("pt-BR");

    public static string Size(long bytes) => bytes switch
    {
        >= 1L << 30 => (bytes / (double)(1L << 30)).ToString("0.0", Pt) + " GB",
        >= 1L << 20 => (bytes / (double)(1L << 20)).ToString("0.0", Pt) + " MB",
        _ => (bytes / 1024.0).ToString("0", Pt) + " KB",
    };

    public static ProtoData Load(string dataRoot, List<ProtoGame> games)
    {
        var d = new ProtoData();
        string fs = System.IO.Path.Combine(dataRoot, "fs");
        try
        {
            var fw = System.IO.Path.Combine(fs, "firmware.json");
            if (File.Exists(fw))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(fw));
                string Str(string name) => doc.RootElement.TryGetProperty(name, out var v) ? v.GetString() ?? "" : "";
                d.FirmwareInstalled = true;
                d.FirmwareVersion = Str("version");
                d.FirmwareBuild = Str("build");
                d.FirmwareSource = Str("source");
                d.FontsInstalled = Str("font_package") == "true";
                if (DateTime.TryParse(Str("installed"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var when)) d.FirmwareDate = when.ToLocalTime();
            }
        }
        catch (Exception e) when (e is IOException or JsonException) { }
        d.Pd0 = Directory.Exists(System.IO.Path.Combine(fs, "pd0", "data"));

        int language = Core.Firmware.SystemLanguage.FromCulture(Pt);
        foreach (var g in games)
        {
            try
            {
                if (TrophyPackage.FindSets(g.Path).FirstOrDefault() is { TrpPath: not null } set && TrophyPackage.Load(set.TrpPath, set.CommId, language) is { } pkg)
                {
                    var ts = new ProtoTrophySet(g, pkg, TrophyUnlocks.Load(TrophyUnlocks.PathFor(fs, set.CommId)));
                    d.TrophySets.Add(ts);
                    g.TrophyTotal = ts.Total;
                    g.RealGot = ts.Got;
                }
            }
            catch (Exception e) when (e is IOException or InvalidDataException) { }
        }

        string? Pic(string id) => games.FirstOrDefault(g => g.TitleId == id)?.Pic0;
        var captures = System.IO.Path.Combine(dataRoot, "captures");
        if (Directory.Exists(captures))
            foreach (var zip in Directory.GetFiles(captures, "*.zip"))
            {
                var name = System.IO.Path.GetFileNameWithoutExtension(zip);
                var id = name.Split('_')[0];
                d.Captures.Add(new ProtoCapture("Gravação F8", id, System.IO.Path.GetFileName(zip), new FileInfo(zip).Length, File.GetLastWriteTime(zip), Pic(id)));
            }
        var dumps = System.IO.Path.Combine(dataRoot, "gpudumps");
        if (Directory.Exists(dumps))
            foreach (var f in Directory.GetFiles(dumps, "*.vsgd"))
            {
                var id = System.IO.Path.GetFileName(f).Split('_')[0];
                d.Captures.Add(new ProtoCapture("Dump da GPU", id, System.IO.Path.GetFileName(f), new FileInfo(f).Length, File.GetLastWriteTime(f), Pic(id)));
            }
        d.Captures.Sort((a, b) => b.When.CompareTo(a.When));
        return d;
    }
}

sealed partial class PrototypeScreens
{
    public const string LibraryList = "library-list", LibraryHome = "library-home", SettingsGeneral = "settings-general",
        VitaSystem = "system", TrophyList = "trophies", Captures = "captures", Install = "install", Update = "update", Empty = "empty";

    /// <summary>The Vita system shortcut: no F-key (F8, F11 and the others are taken in the game window).</summary>
    const string LaunchKey = "Ctrl H";

    static readonly CultureInfo Pt = new("pt-BR");

    // ------------------------------------------------------------------ shared pieces

    Border Kbd(string text, bool onAccent = false) => new()
    {
        Padding = new Thickness(7, 2), CornerRadius = new CornerRadius(6), VerticalAlignment = VerticalAlignment.Center,
        BorderBrush = ProtoKit.B(onAccent ? P.OnAccent : P.BorderStrong, onAccent ? 0.35 : 1), BorderThickness = new Thickness(1),
        Child = k.Text(text, 11, FontWeight.SemiBold, onAccent ? P.OnAccent : P.Muted),
    };

    /// <summary>The sidebar's "Abrir Sistema do Vita": the shell in one click from any screen.</summary>
    Border LaunchButton(bool enabled)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        var fg = enabled ? P.OnAccent : P.Faint;
        g.Children.Add(new Border
        {
            Width = 30, Height = 30, CornerRadius = new CornerRadius(15), Margin = new Thickness(0, 0, 10, 0),
            Background = ProtoKit.B(enabled ? P.OnAccent : P.Border, enabled ? 0.16 : 1),
            Child = new Border { Margin = new Thickness(2, 0, 0, 0), Child = k.Icon("play", 13, fg, fill: true) },
        });
        var texts = new StackPanel { Spacing = 0, VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(k.Text("Abrir Sistema do Vita", 14, FontWeight.SemiBold, fg));
        texts.Children.Add(enabled ? k.Text(LaunchKey.Replace(' ', '+') + " · em qualquer tela", 11, FontWeight.Medium, P.OnAccent).Also(x => x.Opacity = 0.75)
            : k.Text("Instale o firmware antes", 11, color: P.Faint));
        g.Children.Add(ProtoKit.At(texts, col: 1));
        return new Border
        {
            Height = 54, Padding = new Thickness(12, 0), CornerRadius = new CornerRadius(16), Child = g,
            Background = enabled
                ? new LinearGradientBrush
                {
                    StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                    GradientStops = { new GradientStop(P.Accent, 0), new GradientStop(P.Dark ? Color.Parse("#8C7CFF") : Color.Parse("#5B4FD6"), 1) },
                }
                : ProtoKit.B(P.Surface),
            BorderBrush = ProtoKit.B(enabled ? Colors.Transparent : P.Border), BorderThickness = new Thickness(1),
            BoxShadow = enabled ? BoxShadows.Parse(P.Dark ? "0 8 24 0 #406E9BFF" : "0 8 20 0 #403767DB") : default,
        };
    }

    /// <summary>The launch button's tooltip, drawn where it appears on hover.</summary>
    Control LaunchTooltip()
    {
        var row = new StackPanel { Spacing = 4 };
        var top = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        var fg = P.Dark ? P.Text : Colors.White;
        top.Children.Add(k.Text("Abrir Sistema do Vita", 13, FontWeight.SemiBold, fg));
        top.Children.Add(new Border
        {
            Padding = new Thickness(7, 2), CornerRadius = new CornerRadius(6), BorderBrush = ProtoKit.B(fg, 0.35), BorderThickness = new Thickness(1),
            Child = k.Text("Ctrl+H", 11, FontWeight.SemiBold, fg),
        });
        row.Children.Add(top);
        row.Children.Add(k.Text("O shell.self do firmware em tela cheia: tela inicial, LiveArea,", 12, color: P.Dark ? P.Muted : Color.Parse("#C9D1DC")));
        row.Children.Add(k.Text("apps do sistema. Funciona em qualquer tela do launcher.", 12, color: P.Dark ? P.Muted : Color.Parse("#C9D1DC")));
        var tip = new Border
        {
            Padding = new Thickness(14, 10), CornerRadius = new CornerRadius(12), Child = row,
            Background = ProtoKit.B(P.Dark ? Color.Parse("#232C3E") : Color.Parse("#1B2130")),
            BorderBrush = ProtoKit.B(P.Dark ? P.HighlightBorder : Colors.Transparent), BorderThickness = new Thickness(1),
            BoxShadow = BoxShadows.Parse("0 10 30 0 #55000000"),
        };
        Canvas.SetLeft(tip, 246);
        Canvas.SetTop(tip, 80);
        return new Canvas { Children = { tip }, IsHitTestVisible = false };
    }

    Control PageHeader(string title, string subtitle, Control? right = null)
    {
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 0, 0, 22) };
        var titles = new StackPanel { Spacing = 2 };
        titles.Children.Add(k.Heading(title, 30));
        titles.Children.Add(k.Text(subtitle, 13.5, color: P.Muted));
        header.Children.Add(titles);
        if (right != null)
        {
            right.VerticalAlignment = VerticalAlignment.Center;
            header.Children.Add(ProtoKit.At(right, col: 1));
        }
        return header;
    }

    StackPanel Buttons(params Control[] items)
    {
        var s = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        foreach (var c in items) s.Children.Add(c);
        return s;
    }

    Grid Dashed(Control child, double height, double radius = 22)
    {
        var g = new Grid { Height = height };
        g.Children.Add(new Border { CornerRadius = new CornerRadius(radius), Background = ProtoKit.B(P.Accent, P.Dark ? 0.05 : 0.04) });
        g.Children.Add(new Rectangle
        {
            RadiusX = radius, RadiusY = radius, Stroke = ProtoKit.B(P.Accent, 0.55), StrokeThickness = 1.5,
            StrokeDashArray = new AvaloniaList<double> { 5, 4 },
        });
        child.HorizontalAlignment = HorizontalAlignment.Center;
        child.VerticalAlignment = VerticalAlignment.Center;
        g.Children.Add(child);
        return g;
    }

    Control GradeDot(TrophyGrade g, double size = 9) => k.Dot(GradeColor(g), size);

    Color GradeColor(TrophyGrade g) => g switch
    {
        TrophyGrade.Platinum => P.Platinum, TrophyGrade.Gold => P.Gold, TrophyGrade.Silver => P.Silver, _ => P.Bronze,
    };

    static string GradeName(TrophyGrade g) => g switch
    {
        TrophyGrade.Platinum => "Platina", TrophyGrade.Gold => "Ouro", TrophyGrade.Silver => "Prata", _ => "Bronze",
    };

    // ------------------------------------------------------------------ library: list and Vita home

    Control GameList()
    {
        var s = new StackPanel();
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("64,*,100,90,150,100,240"), Margin = new Thickness(18, 0, 18, 8) };
        string[] cols = ["", "Jogo", "Região", "Versão", "Troféus", "Tamanho", ""];
        for (int i = 0; i < cols.Length; i++) head.Children.Add(ProtoKit.At(k.Kicker(cols[i]), col: i));
        s.Children.Add(head);
        var rows = new StackPanel();
        int n = 0;
        foreach (var g in games.Take(11))
        {
            bool hover = n == 2;
            var r = new Grid { ColumnDefinitions = new ColumnDefinitions("64,*,100,90,150,100,240"), Height = 62, Margin = new Thickness(18, 0) };
            r.Children.Add(k.Picture(g.Icon, 46, 46, 12, g.Initials).Also(p => p.HorizontalAlignment = HorizontalAlignment.Left));
            var t = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            t.Children.Add(k.Text(g.Title, 14, FontWeight.SemiBold));
            t.Children.Add(k.Text(g.TitleId, 12, color: P.Muted));
            r.Children.Add(ProtoKit.At(t, col: 1));
            r.Children.Add(ProtoKit.At(k.Text(g.Region, 13, color: P.TextSoft), col: 2));
            r.Children.Add(ProtoKit.At(k.Text("v" + g.Version, 13, color: P.TextSoft), col: 3));
            var tro = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
            tro.Children.Add(k.Text($"{g.TrophyGot}/{g.Total}", 13, color: P.TextSoft).Also(x => x.Width = 44));
            tro.Children.Add(new Border { Width = 80, Child = k.Bar(g.TrophyGot / (double)g.Total, 4) });
            r.Children.Add(ProtoKit.At(tro, col: 4));
            r.Children.Add(ProtoKit.At(k.Text(ProtoData.Size(FolderSize(g.Path)), 13, color: P.TextSoft), col: 5));
            if (hover)
            {
                var acts = Buttons(k.PrimaryButton("Jogar", "play", 36, 0), k.SecondaryButton("LiveArea", "vita", 36));
                acts.HorizontalAlignment = HorizontalAlignment.Right;
                acts.VerticalAlignment = VerticalAlignment.Center;
                r.Children.Add(ProtoKit.At(acts, col: 6));
            }
            rows.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(14), Child = r,
                Background = hover ? ProtoKit.B(P.Highlight) : n % 2 == 0 ? ProtoKit.B(P.Surface) : Brushes.Transparent,
                BorderBrush = ProtoKit.B(hover ? P.HighlightBorder : Colors.Transparent), BorderThickness = new Thickness(1),
            });
            n++;
        }
        s.Children.Add(rows);
        return s;
    }

    static long FolderSize(string dir)
    {
        try { return new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return 0; }
    }

    /// <summary>"Tela inicial": the launcher's existing Vita home view (firmware bubbles, wallpaper, pages) in the frame.</summary>
    Control HomeView()
    {
        Control inner;
        try
        {
            var shell = new Shell.ShellView(new LauncherSettings { View = LauncherSettings.ViewHome, HomeBgm = false, SystemSounds = false }, Program.FsRoot);
            shell.SetApps(games.Select(g => new GameEntry(g.TitleId, g.Title, g.Version, g.Category, g.Path, g.Icon, 0)));
            inner = shell;
        }
        catch (Exception e)
        {
            inner = k.Text("Tela inicial indisponível: " + e.Message, 13, color: P.Muted);
        }
        var s = new StackPanel { Spacing = 12 };
        s.Children.Add(new Border
        {
            Height = 620, CornerRadius = new CornerRadius(22), ClipToBounds = true, Child = inner, Background = Brushes.Black,
            BorderBrush = ProtoKit.B(P.Border), BorderThickness = new Thickness(1),
        });
        var hint = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        hint.Children.Add(k.Icon("vita", 16, P.Muted));
        hint.Children.Add(k.Text("Bolhas e papel de parede do firmware instalado. Um clique abre a LiveArea do jogo; Jogar começa direto.", 12.5, color: P.Muted));
        s.Children.Add(hint);
        return s;
    }

    // ------------------------------------------------------------------ settings: general (start-up, updates)

    Control SettingsFrame(string selected, params Control[] body)
    {
        var page = new Grid { ColumnDefinitions = new ColumnDefinitions("220,*"), Margin = new Thickness(36, 28, 36, 0) };
        var nav = new StackPanel { Spacing = 2 };
        nav.Children.Add(new Border { Margin = new Thickness(0, 0, 0, 22), Child = k.Heading("Configurações", 26) });
        foreach (var (icon, text) in new[]
                 {
                     ("settings", "Geral"), ("vita", "Sistema do Vita"), ("monitor", "Gráficos"), ("volume", "Áudio"), ("gamepad", "Controles"),
                     ("user", "Rede e PSN"), ("trophy", "Troféus"), ("hdd", "Armazenamento"), ("terminal", "Avançado"),
                 })
        {
            bool sel = text == selected;
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
        var s = new StackPanel { Spacing = 10, Margin = new Thickness(36, 0, 0, 0), MaxWidth = 860, HorizontalAlignment = HorizontalAlignment.Left };
        var search = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        search.Children.Add(new Border { Margin = new Thickness(0, 0, 10, 0), Child = k.Icon("search", 17, P.Muted) });
        search.Children.Add(ProtoKit.At(k.Text("Buscar um ajuste", 14, color: P.Faint), col: 1));
        s.Children.Add(new Border
        {
            Height = 44, Padding = new Thickness(14, 0), CornerRadius = new CornerRadius(14), Margin = new Thickness(0, 0, 0, 12),
            Background = ProtoKit.B(P.Surface), BorderBrush = ProtoKit.B(P.BorderStrong), BorderThickness = new Thickness(1), Child = search,
        });
        foreach (var c in body) s.Children.Add(c);
        page.Children.Add(ProtoKit.At(s, col: 1));
        return page;
    }

    Control SettingsGeneralPage()
    {
        var current = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        current.Children.Add(k.Badge("Nova: build-176f5d6", P.Accent));
        current.Children.Add(k.SecondaryButton("Procurar agora", null, 36));
        return SettingsFrame("Geral",
            Group("Início"),
            k.Group(
                k.Row("vita", "Ao abrir o emulador", "Sistema do Vita entra direto no shell, como ligar o console",
                    k.Segmented(("Biblioteca", null, true), ("Sistema do Vita", null, false))),
                k.Row("play", "Ao escolher um jogo", "O clique na capa; o outro fica no botão ao lado",
                    k.Segmented(("Jogar direto", null, true), ("Abrir a LiveArea", null, false))),
                k.Row("terminal", "Atalho do Sistema do Vita", "Em qualquer tela do launcher", Kbd("Ctrl + H"))),
            Group("Atualizações"),
            k.Group(
                k.Row("download", "Procurar atualizações ao iniciar", "Releases do GitHub do VitaSharp", k.Switch(true)),
                k.Row("layers", "Canal", "Builds automáticas saem a cada mudança; versões, só nas marcadas",
                    k.Segmented(("Builds automáticas", null, true), ("Só versões", null, false))),
                k.Row("clock", "Versão em uso", "build-99396f5 · 06/11/2026 · verificado hoje, 21:40", current)),
            Group("Aparência"),
            k.Group(
                k.Row(P.Dark ? "moon" : "sun", "Tema", "Segue o Windows por padrão", k.Segmented(("Sistema", null, false), ("Escuro", "moon", P.Dark), ("Claro", "sun", !P.Dark))),
                k.Row("globe", "Idioma do launcher", null, k.Dropdown("Português (Brasil)", 200))));
    }

    // ------------------------------------------------------------------ Sistema do Vita (extras: firmware, pd0, options)

    Control SystemPage()
    {
        var page = new StackPanel { Margin = new Thickness(36, 28, 36, 0) };
        page.Children.Add(PageHeader("Sistema do Vita", "O firmware de verdade (shell.self e libpaf) rodando no emulador. Abrir: botão na barra lateral ou Ctrl+H.",
            k.SecondaryButton("Instalar ou atualizar firmware", "download", 44)));

        var cols = new Grid { ColumnDefinitions = new ColumnDefinitions("*,24,*") };
        var left = new StackPanel { Spacing = 10 };
        left.Children.Add(Group("Firmware"));
        left.Children.Add(k.Group(
            k.Row("cpu", $"Firmware {data.FirmwareVersion}", $"build {data.FirmwareBuild} · {data.FirmwareSource}" +
                (data.FirmwareDate is { } d ? $" · instalado em {d:dd/MM/yyyy}" : ""), k.Badge("Instalado", P.Ok)),
            k.Row("box", "Partições os0 e vs0", "Módulos do sistema, shell e apps", k.Badge("OK", P.Ok)),
            k.Row("volume", "pd0", "Música da tela inicial, temas e dados de fábrica", data.Pd0 ? k.Badge("Instalado", P.Ok) : k.Badge("Ausente", P.Warn)),
            k.Row("layers", "Fontes do sistema", data.FontsInstalled ? "Pacote de fontes instalado" : "Sem o pacote de fontes: textos do sistema usam a fonte substituta",
                data.FontsInstalled ? k.Badge("Instalado", P.Ok) : k.SecondaryButton("Instalar fontes", null, 36)),
            k.Row("check", "Verificar arquivos", "Compara com as somas do PSVUPDAT.PUP", k.SecondaryButton("Verificar", null, 36))));
        left.Children.Add(Group("Importar"));
        left.Children.Add(k.Group(
            k.Row("folder", "Importar do Vita3K", "Copia os0, vs0, sa0 e pd0 (nunca liga às pastas dele)", chevron: true),
            k.Row("trophy", "Importar troféus", "De um Vita ou do Vita3K", chevron: true)));
        cols.Children.Add(left);

        var right = new StackPanel { Spacing = 10 };
        right.Children.Add(Group("Opções do sistema"));
        right.Children.Add(k.Group(
            k.Row("globe", "Idioma do sistema", null, k.Dropdown("Português (Brasil)", 190)),
            k.Row("gamepad", "Botão de confirmar", null, k.Segmented(("✕ Cruz", null, true), ("○ Círculo", null, false))),
            k.Row("clock", "Data e hora", "Do computador", chevron: true),
            k.Row("image", "Tema da tela inicial", null, k.Dropdown("Padrão do firmware", 190)),
            k.Row("volume", "Música do sistema", null, k.Switch(true)),
            k.Row("volume", "Efeitos sonoros do sistema", null, k.Switch(true))));
        right.Children.Add(Group("Conta"));
        right.Children.Add(k.Group(
            k.Row("user", "Usuário do sistema", "ID online e avatar usados pelo shell e pelos jogos", chevron: true)));
        cols.Children.Add(ProtoKit.At(right, col: 2));
        page.Children.Add(cols);
        return page;
    }

    // ------------------------------------------------------------------ trophies

    Control TrophiesPage()
    {
        var sets = data.TrophySets.OrderByDescending(s => s.Got).ThenBy(s => s.Game.Title).ToList();
        int got = sets.Sum(s => s.Got), total = sets.Sum(s => s.Total);
        var page = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), Margin = new Thickness(36, 28, 36, 0) };
        var counts = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 18 };
        foreach (var grade in new[] { TrophyGrade.Platinum, TrophyGrade.Gold, TrophyGrade.Silver, TrophyGrade.Bronze })
        {
            var c = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
            c.Children.Add(GradeDot(grade, 10));
            c.Children.Add(k.Text(sets.Sum(s => s.Package.Trophies.Count(t => t.Grade == grade && s.Unlocked.ContainsKey(t.Id))).ToString(Pt), 15, FontWeight.SemiBold));
            counts.Children.Add(c);
        }
        page.Children.Add(PageHeader("Troféus", $"{got} de {total} troféus em {sets.Count} jogos", counts));

        var cols = new Grid { ColumnDefinitions = new ColumnDefinitions("390,24,*") };
        ProtoKit.At(cols, row: 1);
        var list = new StackPanel { Spacing = 6 };
        var search = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        search.Children.Add(new Border { Margin = new Thickness(0, 0, 10, 0), Child = k.Icon("search", 16, P.Muted) });
        search.Children.Add(ProtoKit.At(k.Text("Buscar jogo", 13.5, color: P.Faint), col: 1));
        list.Children.Add(new Border
        {
            Height = 40, Padding = new Thickness(12, 0), CornerRadius = new CornerRadius(12), Margin = new Thickness(0, 0, 0, 6),
            Background = ProtoKit.B(P.Surface), BorderBrush = ProtoKit.B(P.BorderStrong), BorderThickness = new Thickness(1), Child = search,
        });
        var selected = sets.FirstOrDefault();
        foreach (var s in sets.Take(9))
        {
            bool sel = s == selected;
            var r = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
            r.Children.Add(k.Picture(s.Game.Icon, 44, 44, 11, s.Game.Initials));
            var t = new StackPanel { Spacing = 5, Margin = new Thickness(12, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
            t.Children.Add(k.Text(s.Game.Title, 13.5, FontWeight.SemiBold));
            t.Children.Add(k.Bar(s.Total == 0 ? 0 : s.Got / (double)s.Total, 4));
            r.Children.Add(ProtoKit.At(t, col: 1));
            r.Children.Add(ProtoKit.At(k.Text($"{s.Got}/{s.Total}", 12.5, FontWeight.Medium, P.Muted), col: 2));
            list.Children.Add(new Border
            {
                Padding = new Thickness(10, 8), CornerRadius = new CornerRadius(14), Child = r,
                Background = ProtoKit.B(sel ? P.Highlight : P.Surface), BorderBrush = ProtoKit.B(sel ? P.HighlightBorder : P.Border), BorderThickness = new Thickness(1),
            });
        }
        cols.Children.Add(list);
        if (selected != null) cols.Children.Add(ProtoKit.At(TrophyDetail(selected), col: 2));
        page.Children.Add(cols);
        return page;
    }

    Control TrophyDetail(ProtoTrophySet s)
    {
        var body = new StackPanel();
        var banner = new Grid { Height = 120 };
        banner.Children.Add(new Border { CornerRadius = new CornerRadius(18, 18, 0, 0), Background = ProtoKit.Image(s.Game.Pic0) });
        banner.Children.Add(new Border { CornerRadius = new CornerRadius(18, 18, 0, 0), Background = ProtoKit.Fade(P.Surface, new RelativePoint(0, 0, RelativeUnit.Relative), new RelativePoint(0, 1, RelativeUnit.Relative), 0.25, 1) });
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(20, 0, 20, 4) };
        head.Children.Add(k.Picture(s.Game.Icon, 64, 64, 16, s.Game.Initials));
        var ht = new StackPanel { Spacing = 4, Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        ht.Children.Add(k.Heading(s.Game.Title, 20));
        var dots = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 };
        foreach (var grade in new[] { TrophyGrade.Platinum, TrophyGrade.Gold, TrophyGrade.Silver, TrophyGrade.Bronze })
        {
            var d = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            d.Children.Add(GradeDot(grade));
            d.Children.Add(k.Text($"{s.Package.Trophies.Count(t => t.Grade == grade && s.Unlocked.ContainsKey(t.Id))}/{s.Package.Trophies.Count(t => t.Grade == grade)}", 12.5, FontWeight.Medium, P.TextSoft));
            dots.Children.Add(d);
        }
        ht.Children.Add(dots);
        head.Children.Add(ProtoKit.At(ht, col: 1));
        head.Children.Add(ProtoKit.At(k.Heading(s.Total == 0 ? "0%" : $"{s.Got * 100 / s.Total}%", 24).Also(x => x.Foreground = ProtoKit.B(P.Accent)), col: 2));
        banner.Children.Add(head);
        body.Children.Add(banner);

        var filters = Buttons(k.Chip("Todos", true), k.Chip($"Obtidos · {s.Got}", false), k.Chip($"Faltam · {s.Total - s.Got}", false));
        filters.Margin = new Thickness(20, 14, 20, 8);
        body.Children.Add(filters);

        var ordered = s.Package.Trophies.OrderByDescending(t => s.When(t.Id) ?? DateTime.MinValue).ThenBy(t => t.Id).Take(8).ToList();
        for (int i = 0; i < ordered.Count; i++)
        {
            var t = ordered[i];
            var when = s.When(t.Id);
            bool hidden = t.Hidden && when == null;
            var r = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(20, 9) };
            var pic = new Border { Width = 48, Height = 48, CornerRadius = new CornerRadius(10), Background = ProtoKit.B(P.SurfaceHigh), Opacity = when == null ? 0.45 : 1 };
            if (!hidden && s.Package.TrophyIcon(t.Id) is { } png) pic.Background = new ImageBrush(new Bitmap(new MemoryStream(png))) { Stretch = Stretch.UniformToFill };
            else if (hidden) pic.Child = k.Icon("x", 18, P.Faint);
            r.Children.Add(pic);
            var tx = new StackPanel { Spacing = 3, Margin = new Thickness(14, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center };
            tx.Children.Add(k.Text(hidden ? "Troféu oculto" : t.Name, 14, FontWeight.SemiBold, when == null ? P.TextSoft : P.Text));
            tx.Children.Add(k.Text(hidden ? "Os detalhes aparecem quando ele for obtido." : t.Detail, 12.5, color: P.Muted));
            r.Children.Add(ProtoKit.At(tx, col: 1));
            var meta = new StackPanel { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            var g = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right };
            g.Children.Add(GradeDot(t.Grade, 8));
            g.Children.Add(k.Text(GradeName(t.Grade), 12.5, FontWeight.Medium, P.TextSoft));
            meta.Children.Add(g);
            meta.Children.Add(k.Text(when is { } w ? w.ToString("dd/MM/yyyy HH:mm", Pt) : "Bloqueado", 12, color: when == null ? P.Faint : P.Muted).Also(x => x.HorizontalAlignment = HorizontalAlignment.Right));
            r.Children.Add(ProtoKit.At(meta, col: 2));
            if (i > 0) body.Children.Add(k.Divider(20));
            body.Children.Add(r);
        }
        return k.Card(body, 18, new Thickness(0, 0, 0, 8));
    }

    // ------------------------------------------------------------------ captures

    Control CapturesPage()
    {
        var caps = data.Captures;
        int rec = caps.Count(c => c.Kind.StartsWith("Grava")), dumps = caps.Count - rec;
        var page = new StackPanel { Margin = new Thickness(36, 28, 36, 0) };
        page.Children.Add(PageHeader("Capturas", $"{caps.Count} arquivos · {ProtoData.Size(caps.Sum(c => c.Size))} na pasta de dados",
            Buttons(k.SecondaryButton("Apagar as de mais de 30 dias", "x", 44), k.SecondaryButton("Abrir pasta", "folder", 44))));
        var chips = Buttons(k.Chip($"Tudo · {caps.Count}", true), k.Chip($"Gravações F8 · {rec}", false, "camera"),
            k.Chip("Capturas de tela · 0", false, "image"), k.Chip($"Dumps da GPU · {dumps}", false, "layers"),
            k.Chip("Jogo: todos", false, dropdown: true));
        chips.Spacing = 8;
        chips.Margin = new Thickness(0, 0, 0, 18);
        page.Children.Add(chips);

        var rows = new StackPanel();
        int i = 0;
        foreach (var c in caps.Take(9))
        {
            var title = c.TitleId == "shell" ? "Sistema do Vita" : games.FirstOrDefault(g => g.TitleId == c.TitleId)?.Title ?? c.TitleId;
            var r = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,150,110,Auto"), Margin = new Thickness(14, 10) };
            var thumb = c.Thumb != null ? k.Picture(c.Thumb, 112, 63, 10) : new Border
            {
                Width = 112, Height = 63, CornerRadius = new CornerRadius(10), Background = ProtoKit.B(P.SurfaceHigh), Child = k.Icon("vita", 26, P.AccentSoft),
            };
            r.Children.Add(thumb);
            var tx = new StackPanel { Spacing = 4, Margin = new Thickness(16, 0), VerticalAlignment = VerticalAlignment.Center };
            var top = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            top.Children.Add(k.Text(title, 14, FontWeight.SemiBold));
            top.Children.Add(k.Badge(c.Kind, c.Kind.StartsWith("Grava") ? P.Accent : P.Warn));
            tx.Children.Add(top);
            tx.Children.Add(k.Text(c.Name, 12, color: P.Muted));
            r.Children.Add(ProtoKit.At(tx, col: 1));
            r.Children.Add(ProtoKit.At(k.Text(c.When.ToString("dd/MM/yyyy HH:mm", Pt), 13, color: P.TextSoft), col: 2));
            r.Children.Add(ProtoKit.At(k.Text(ProtoData.Size(c.Size), 13, FontWeight.Medium, P.TextSoft), col: 3));
            var acts = Buttons(k.SecondaryButton("", "folder", 36), k.SecondaryButton("", "x", 36, P.Danger));
            acts.Spacing = 6;
            r.Children.Add(ProtoKit.At(acts, col: 4));
            if (i++ > 0) rows.Children.Add(k.Divider(14));
            rows.Children.Add(r);
        }
        page.Children.Add(k.Card(rows, 18, new Thickness(0, 4)));
        return page;
    }

    // ------------------------------------------------------------------ install queue

    Control InstallPage()
    {
        var page = new StackPanel { Margin = new Thickness(36, 28, 36, 0) };
        page.Children.Add(PageHeader("Instalar pkg / vpk", "Jogos, atualizações, DLCs e o firmware. Os arquivos ficam em ux0:app; o original não é apagado."));
        var drop = new StackPanel { Spacing = 10, HorizontalAlignment = HorizontalAlignment.Center };
        drop.Children.Add(new Border { Child = k.IconChip("download", 52) });
        drop.Children.Add(k.Text("Arraste arquivos .pkg, .vpk ou .zip para cá (ou para qualquer tela)", 15, FontWeight.SemiBold).Also(t => t.HorizontalAlignment = HorizontalAlignment.Center));
        drop.Children.Add(k.Text("pkg precisa da licença: zRIF, work.bin ou o .rif do Vita3K", 12.5, color: P.Muted).Also(t => t.HorizontalAlignment = HorizontalAlignment.Center));
        var b = Buttons(k.PrimaryButton("Escolher arquivos", "folder", 42, 0), k.SecondaryButton("Pasta de jogo descriptografado", null, 42));
        b.HorizontalAlignment = HorizontalAlignment.Center;
        b.Margin = new Thickness(0, 6, 0, 0);
        drop.Children.Add(b);
        page.Children.Add(Dashed(drop, 230));

        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 26, 0, 12) };
        head.Children.Add(k.Text("Fila", 17, FontWeight.SemiBold));
        head.Children.Add(ProtoKit.At(k.Text("Limpar concluídos", 13, FontWeight.SemiBold, P.Accent), col: 1));
        page.Children.Add(head);

        var done = games.Skip(5).FirstOrDefault() ?? hero;
        var done2 = games.Skip(12).FirstOrDefault() ?? hero;
        var rows = new StackPanel();
        rows.Children.Add(QueueRow("download", P.Accent, "PCSB00245.pkg", "Instalando · 1,9 de 3,1 GB · 40 s restantes", 0.62, k.SecondaryButton("Cancelar", null, 36)));
        rows.Children.Add(k.Divider(16));
        rows.Children.Add(QueueRow("clock", P.Muted, "PCSE00120 atualização 1.03.pkg", "Na fila · 412,0 MB · aplica sobre o jogo instalado", null, k.SecondaryButton("", "x", 36)));
        rows.Children.Add(k.Divider(16));
        rows.Children.Add(QueueRow("check", P.Ok, done.Title, $"Instalado · {done.TitleId} · há 5 min", null, k.PrimaryButton("Jogar", "play", 36, 0), done.Icon));
        rows.Children.Add(k.Divider(16));
        rows.Children.Add(QueueRow("check", P.Ok, done2.Title, $"Instalado · {done2.TitleId} · há 12 min", null, k.SecondaryButton("Abrir página", null, 36), done2.Icon));
        rows.Children.Add(k.Divider(16));
        rows.Children.Add(QueueRow("x", P.Danger, "WRC 4.pkg", "Falhou: falta a licença (zRIF, work.bin ou .rif)", null,
            Buttons(k.SecondaryButton("Informar licença", null, 36), k.SecondaryButton("", "x", 36))));
        page.Children.Add(k.Card(rows, 18, new Thickness(0, 4)));
        return page;
    }

    Control QueueRow(string icon, Color color, string title, string detail, double? progress, Control action, string? picture = null)
    {
        var r = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(16, 12) };
        r.Children.Add(picture != null ? k.Picture(picture, 44, 44, 11) : k.IconChip(icon, 44, Color.FromArgb(40, color.R, color.G, color.B), color));
        var t = new StackPanel { Spacing = 5, Margin = new Thickness(14, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center };
        var top = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        top.Children.Add(k.Text(title, 14, FontWeight.SemiBold));
        if (picture != null) top.Children.Add(k.Icon("check", 15, P.Ok, 2.4));
        t.Children.Add(top);
        t.Children.Add(k.Text(detail, 12.5, color: color == P.Danger ? P.Danger : P.Muted));
        if (progress is { } p) t.Children.Add(new Border { MaxWidth = 520, HorizontalAlignment = HorizontalAlignment.Left, Width = 520, Child = k.Bar(p, 5) });
        r.Children.Add(ProtoKit.At(t, col: 1));
        action.VerticalAlignment = VerticalAlignment.Center;
        r.Children.Add(ProtoKit.At(action, col: 2));
        return r;
    }

    // ------------------------------------------------------------------ update dialog (PCSX2-style)

    static readonly string[] Notes =
    [
        "Firmware: install pd0 so the shell's home screen music plays",
        "Trophies: migrate every stored title silently, with signatures read from the games and the original dates",
        "Internal resolution above native: 2x/3x/4x per game (PCSX2-style upscaling)",
        "Audio dump: a port that changes its rate starts a new WAV",
        "Roadmap: close the done items (automatic releases, Android accents, Android pause menu)",
        "Roadmap: findings on the IME circle button and the AvMediaService dispatch",
    ];

    Control UpdateDialog()
    {
        var root = new Grid { Background = ProtoKit.B(Colors.Black, P.Dark ? 0.6 : 0.4) };
        var s = new StackPanel { Spacing = 0 };
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(24, 22, 20, 0) };
        using (var logo = typeof(PrototypeScreens).Assembly.GetManifestResourceStream("icon-64.png"))
            if (logo != null) head.Children.Add(new Image { Source = new Bitmap(logo), Width = 48, Height = 48 });
        var ht = new StackPanel { Spacing = 3, Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        ht.Children.Add(k.Heading("Nova versão do VitaSharp", 21));
        ht.Children.Add(k.Text("build-176f5d6 · 07/11/2026 · 132 mudanças desde a sua", 13, color: P.Muted));
        head.Children.Add(ProtoKit.At(ht, col: 1));
        head.Children.Add(ProtoKit.At(k.SecondaryButton("", "x", 36), col: 2));
        s.Children.Add(head);

        var versions = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,*"), Margin = new Thickness(24, 20, 24, 0) };
        versions.Children.Add(VersionBox("Em uso", "build-99396f5", "06/11/2026", false));
        versions.Children.Add(ProtoKit.At(new Border { Margin = new Thickness(12, 0), Child = k.Icon("chevron-right", 20, P.Muted) }, col: 1));
        versions.Children.Add(ProtoKit.At(VersionBox("Nova", "build-176f5d6", "07/11/2026", true), col: 2));
        s.Children.Add(versions);

        var notes = new StackPanel { Spacing = 9 };
        notes.Children.Add(k.Kicker("O que mudou"));
        foreach (var n in Notes)
        {
            var r = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
            r.Children.Add(new Border { Margin = new Thickness(0, 6, 10, 0), VerticalAlignment = VerticalAlignment.Top, Child = k.Dot(P.Accent, 6) });
            r.Children.Add(ProtoKit.At(new TextBlock { Text = n, FontFamily = ProtoKit.Inter, FontSize = 13, Foreground = ProtoKit.B(P.TextSoft), TextWrapping = TextWrapping.Wrap }, col: 1));
            notes.Children.Add(r);
        }
        notes.Children.Add(k.Text("e mais 126 · ver tudo no GitHub", 12.5, FontWeight.SemiBold, P.Accent));
        s.Children.Add(new Border
        {
            Margin = new Thickness(24, 16, 24, 0), Padding = new Thickness(16, 14), CornerRadius = new CornerRadius(14),
            Background = ProtoKit.B(P.Background), BorderBrush = ProtoKit.B(P.Border), BorderThickness = new Thickness(1), Child = notes,
        });

        var info = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(26, 14, 24, 0) };
        info.Children.Add(k.Icon("check", 15, P.Ok, 2.2));
        info.Children.Add(new TextBlock { Text = "Baixa o pacote win-x64, confere o SHA256SUMS e reabre. Saves, firmware e jogos não são tocados.", FontFamily = ProtoKit.Inter, FontSize = 12.5, Foreground = ProtoKit.B(P.Muted), TextWrapping = TextWrapping.Wrap, MaxWidth = 560 });
        s.Children.Add(info);

        var foot = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(24, 20, 24, 22) };
        foot.Children.Add(k.SecondaryButton("Pular esta versão", null, 44));
        var right = Buttons(k.SecondaryButton("Lembrar depois", null, 44), k.PrimaryButton("Atualizar agora", "download", 44, 0));
        foot.Children.Add(ProtoKit.At(right, col: 2));
        s.Children.Add(foot);

        root.Children.Add(new Border
        {
            Width = 640, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            CornerRadius = new CornerRadius(24), Background = ProtoKit.B(P.Surface), BorderBrush = ProtoKit.B(P.BorderStrong), BorderThickness = new Thickness(1),
            BoxShadow = BoxShadows.Parse("0 24 60 0 #66000000"), Child = s,
        });
        return root;
    }

    Control VersionBox(string label, string version, string date, bool isNew)
    {
        var s = new StackPanel { Spacing = 3 };
        s.Children.Add(k.Kicker(label, isNew ? P.Accent : P.Muted));
        s.Children.Add(k.Text(version, 15, FontWeight.SemiBold));
        s.Children.Add(k.Text(date, 12.5, color: P.Muted));
        return new Border
        {
            Padding = new Thickness(16, 12), CornerRadius = new CornerRadius(14), Child = s,
            Background = ProtoKit.B(isNew ? P.Highlight : P.Background), BorderBrush = ProtoKit.B(isNew ? P.HighlightBorder : P.Border), BorderThickness = new Thickness(1),
        };
    }

    // ------------------------------------------------------------------ first run

    Control EmptyPage()
    {
        var page = new StackPanel { Margin = new Thickness(36, 28, 36, 0) };
        page.Children.Add(PageHeader("Bem-vindo ao VitaSharp", "Dois passos e a biblioteca está pronta. Nada sai do seu computador."));
        var steps = new UniformGrid { Columns = 3, Margin = new Thickness(0, 0, -16, 0) };
        steps.Children.Add(Step(1, "cpu", "Instale o firmware", "O PSVUPDAT.PUP 3.74 oficial: liga o Sistema do Vita e as bibliotecas que muitos jogos usam.",
            k.PrimaryButton("Escolher PSVUPDAT.PUP", "folder", 42, 0), "Importar do Vita3K", true));
        steps.Children.Add(Step(2, "download", "Adicione jogos", "Arraste um pkg, vpk ou zip para a janela, ou escolha os arquivos. pkg pede a licença (zRIF).",
            k.SecondaryButton("Instalar jogo", "plus", 42), "Usar pasta já descriptografada", false));
        steps.Children.Add(Step(3, "gamepad", "Confira os controles", "Teclado já configurado. Controles de PlayStation, Xbox e genéricos são reconhecidos sozinhos.",
            k.SecondaryButton("Configurar controles", null, 42), null, false));
        page.Children.Add(steps);

        var empty = new StackPanel { Spacing = 8 };
        empty.Children.Add(new Border { Child = k.IconChip("library", 56) });
        empty.Children.Add(k.Text("Sua biblioteca está vazia", 16, FontWeight.SemiBold).Also(t => t.HorizontalAlignment = HorizontalAlignment.Center));
        empty.Children.Add(k.Text("Os jogos instalados aparecem aqui com capa, troféus e estados salvos.", 13, color: P.Muted).Also(t => t.HorizontalAlignment = HorizontalAlignment.Center));
        var dz = Dashed(empty, 300);
        dz.Margin = new Thickness(0, 24, 0, 0);
        page.Children.Add(dz);
        return page;
    }

    Control Step(int n, string icon, string title, string text, Control action, string? link, bool current)
    {
        var s = new StackPanel { Spacing = 10 };
        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        top.Children.Add(k.IconChip(icon, 44));
        top.Children.Add(ProtoKit.At(new Border
        {
            Width = 26, Height = 26, CornerRadius = new CornerRadius(13), Background = ProtoKit.B(current ? P.Accent : P.Track),
            Child = k.Text(n.ToString(Pt), 12.5, FontWeight.Bold, current ? P.OnAccent : P.TextSoft).Also(t => t.HorizontalAlignment = HorizontalAlignment.Center),
        }, col: 2));
        s.Children.Add(top);
        s.Children.Add(k.Text(title, 16, FontWeight.SemiBold).Also(t => t.Margin = new Thickness(0, 6, 0, 0)));
        s.Children.Add(new TextBlock { Text = text, FontFamily = ProtoKit.Inter, FontSize = 13, Foreground = ProtoKit.B(P.Muted), TextWrapping = TextWrapping.Wrap, Height = 54 });
        action.HorizontalAlignment = HorizontalAlignment.Left;
        s.Children.Add(action);
        if (link != null) s.Children.Add(k.Text(link, 13, FontWeight.SemiBold, P.Accent));
        var card = k.Card(s, 20, new Thickness(20, 18));
        card.Margin = new Thickness(0, 0, 16, 0);
        if (current) card.BorderBrush = ProtoKit.B(P.HighlightBorder);
        return card;
    }
}

static class ProtoExtensions
{
    public static T Also<T>(this T value, Action<T> action)
    {
        action(value);
        return value;
    }
}
