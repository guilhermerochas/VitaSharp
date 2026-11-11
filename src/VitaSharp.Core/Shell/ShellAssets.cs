using SQLitePCL;
using System.Xml;
using System.Xml.Linq;
using VitaSharp.Core.Package;

namespace VitaSharp.Core.Shell;

/// <summary>A home-screen theme: the firmware's default, or a custom theme installed in ux0:theme.</summary>
public sealed class ShellTheme
{
    public const string DefaultId = "default";
    public required string Id { get; init; }
    public required string Title { get; init; }
    /// <summary>Home screen wallpapers (the first is used for every page here); empty for the default theme, whose
    /// wave background the shell draws itself.</summary>
    public List<string> HomeBackgrounds { get; } = new();
    public string? StartScreenImage { get; set; }
    public string? BgmPath { get; set; }
    public string? PreviewImage { get; set; }
    /// <summary>Status bar colour and the text colour on the wallpapers (0xAARRGGBB), when the theme sets them.</summary>
    public uint? BarColor { get; set; }
    public uint? FontColor { get; set; }
}

/// <summary>A system application in vs0:app with a home-screen bubble (Settings, Trophies, Browser, Content manager...).</summary>
public sealed record ShellSystemApp(string TitleId, string Title, string Path, string? IconPath);

/// <summary>Which shell sound effect to play. <see cref="Notice"/> is the system's notice chime (see
/// <see cref="TrophySound"/>).</summary>
public enum ShellSoundEffect
{
    Confirm, Cancel, Alternate, Notice
}

/// <summary>
/// What the installed firmware offers the launcher's Vita-style home screen: themes (vs0:data/internal/theme and
/// ux0:theme/*/theme.xml), the system BGM (pd0:data/systembgm/home.at9 or the theme's), the system apps' bubbles
/// (vs0:app, in the order of the shell's database), textures from the shell RCOs (status-bar icons, page dots) and the system sound effects (the
/// decide/cancel sounds of common_resource.rco). Every member degrades to null/empty without firmware.
/// </summary>
public sealed class ShellAssets
{
    public string FsRoot { get; }
    private readonly Dictionary<string, Rco?> _rcos = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, RgbaImage?> _textures = new();
    private readonly Dictionary<ShellSoundEffect, PcmSound?> _sounds = new();
    private readonly object _lock = new();

    public ShellAssets(string fsRoot)
    {
        FsRoot = fsRoot;
    }

    private string Vs0(params string[] parts) => Path.Combine([FsRoot, "vs0", .. parts]);

    /// <summary>True when vs0 holds the shell (vsh/shell), i.e. firmware is installed.</summary>
    public bool HasFirmware => Directory.Exists(Vs0("vsh", "shell"));

    /// <summary>Shell RCOs the home screen takes textures from.</summary>
    public const string TopMenuRco = "vsh/shell/topmenu_plugin.rco", IndicatorRco = "vsh/shell/indicator_plugin.rco",
        LiveAreaRco = "vsh/shell/livearea_plugin.rco", CommonRco = "data/external/common/common_resource.rco";

    /// <summary>Texture ids (hashes, as firmware RCOs keep them) the home screen uses.</summary>
    public static class Textures
    {
        public const string PageDot = "0x754463D1";      // topmenu: 16x16 page indicator dot
        public const string BubbleShape = "0xD33EC202";  // topmenu: 128x128 white disc (bubble shadow/mask)
        public const string Battery = "0x56E72F16";      // indicator: battery, full
        public const string Wifi = "0xA13376BC";         // indicator: Wi-Fi signal, full
    }

    // ------------------------------------------------------------------ themes

    /// <summary>The default theme (when the firmware has its wallpaper) followed by the custom themes in ux0:theme.</summary>
    public List<ShellTheme> Themes(string language = "en")
    {
        var list = new List<ShellTheme>();
        string home = Vs0("data", "internal", "theme", "defaultTheme_homeScreen.png");
        if (File.Exists(home))
        {
            // the default theme's wallpaper is not an image: the shell renders its blue wave (vs0:data/internal/effects/
            // wave); the PNGs in data/internal/theme are the theme-settings previews
            var t = new ShellTheme { Id = ShellTheme.DefaultId, Title = "Padrão", PreviewImage = home };
            string start = Vs0("data", "internal", "theme", "defaultTheme_startScreen.png");
            if (File.Exists(start))
            {
                t.StartScreenImage = start;
            }

            t.BgmPath = SystemBgm;
            list.Add(t);
        }
        string themesDir = Path.Combine(FsRoot, "ux0", "theme");
        if (Directory.Exists(themesDir))
        {
            foreach (string? dir in Directory.GetDirectories(themesDir).Order(StringComparer.OrdinalIgnoreCase))
            {
                if (LoadTheme(dir, language) is { } t)
                {
                    list.Add(t);
                }
            }
        }
        return list;
    }

    /// <summary>pd0:data/systembgm/home.at9 when installed.</summary>
    public string? SystemBgm
    {
        get
        {
            string p = Path.Combine(FsRoot, "pd0", "data", "systembgm", "home.at9");
            return File.Exists(p) ? p : null;
        }
    }

    /// <summary>A custom theme folder (theme.xml as written by the theme packages, the same fields Vita3K reads).</summary>
    public static ShellTheme? LoadTheme(string dir, string language = "en")
    {
        string xmlPath = Io.Vfs.ResolveCaseInsensitive(dir, "theme.xml");
        if (!File.Exists(xmlPath))
        {
            return null;
        }

        XElement root;
        try
        {
            root = XDocument.Load(xmlPath).Root ?? throw new InvalidDataException("empty");
        }
        catch (Exception e) when (e is XmlException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Util.Log.Warn($"theme: {xmlPath}: {e.Message}");
            return null;
        }
        if (root.Name.LocalName != "theme")
        {
            return null;
        }

        XElement? info = root.Element("InfomationProperty");
        XElement? bar = root.Element("InfomationBarProperty");
        XElement? homeProp = root.Element("HomeProperty");
        XElement? startProp = root.Element("StartScreenProperty");
        string? Asset(string? rel)
        {
            if (string.IsNullOrWhiteSpace(rel))
            {
                return null;
            }

            string p = Io.Vfs.ResolveCaseInsensitive(dir, rel.Trim().Replace('\\', '/'));
            return File.Exists(p) ? p : null;
        }
        var t = new ShellTheme
        {
            Id = (info?.Element("m_contentId")?.Value.Trim()) is { Length: > 0 } cid ? cid : Path.GetFileName(dir),
            Title = Localized(info?.Element("m_title"), language) ?? Path.GetFileName(dir),
            BgmPath = Asset(homeProp?.Element("m_bgmFilePath")?.Value),
            PreviewImage = Asset(info?.Element("m_packageImageFilePath")?.Value) ?? Asset(info?.Element("m_previewFilePath")?.Value),
            StartScreenImage = Asset(startProp?.Element("m_filePath")?.Value),
            BarColor = Color(bar?.Element("m_barColor")?.Value),
        };
        foreach (XElement? bg in homeProp?.Element("m_bgParam")?.Descendants().Where(e => e.Element("m_imageFilePath") != null) ?? [])
        {
            if (Asset(bg.Element("m_imageFilePath")?.Value) is { } img)
            {
                t.HomeBackgrounds.Add(img);
                t.FontColor ??= Color(bg.Element("m_fontColor")?.Value);
            }
        }
        return t.HomeBackgrounds.Count > 0 ? t : null;
    }

    private static string? Localized(XElement? e, string language)
    {
        if (e == null)
        {
            return null;
        }

        XElement? param = e.Element("m_param");
        if (param != null)
        {
            string key = "m_" + language.Replace('-', '_');
            XElement? match = param.Elements().FirstOrDefault(p => p.Name.LocalName.Equals(key, StringComparison.OrdinalIgnoreCase))
                ?? param.Elements().FirstOrDefault(p => p.Name.LocalName.Equals("m_" + language.Split('-')[0], StringComparison.OrdinalIgnoreCase));
            if (match != null && match.Value.Trim().Length > 0)
            {
                return match.Value.Trim();
            }
        }
        string? def = e.Element("m_default")?.Value.Trim();
        if (!string.IsNullOrEmpty(def))
        {
            return def;
        }

        string own = string.Concat(e.Nodes().OfType<XText>().Select(n => n.Value)).Trim();
        return own.Length > 0 ? own : null;
    }

    /// <summary>Theme colours: "ff336699" (AARRGGBB) or "#336699".</summary>
    private static uint? Color(string? s)
    {
        if (string.IsNullOrWhiteSpace(s))
        {
            return null;
        }

        s = s.Trim().TrimStart('#');
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            s = s[2..];
        }

        if (!uint.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out uint v))
        {
            return null;
        }

        return s.Length <= 6 ? 0xFF000000 | v : v;
    }

    // ------------------------------------------------------------------ system apps

    /// <summary>
    /// The system apps (vs0:app) the home screen shows as bubbles, in the shell's order: the bubbles of the shell's own
    /// database (ur0:shell/db/app.db, table tbl_appinfo_icon by page and position, which the real shell writes) whose
    /// title is a vs0:app folder; without that database (the shell never ran) every vs0:app title, in folder order.
    /// Only titles whose sce_sys has an icon are listed.
    /// </summary>
    public List<ShellSystemApp> SystemApps()
    {
        var list = new List<ShellSystemApp>();
        string apps = Vs0("app");
        if (!Directory.Exists(apps))
        {
            return list;
        }

        var installed = Directory.GetDirectories(apps).Select(d => Path.GetFileName(d)).Order(StringComparer.Ordinal).ToList();
        List<string> ids = ShellDatabaseBubbles() is { } bubbles
            ? bubbles.Where(b => installed.Contains(b, StringComparer.OrdinalIgnoreCase)).ToList()
            : installed;
        foreach (string? id in ids)
        {
            string dir = Vs0("app", id);
            string icon = Io.Vfs.ResolveCaseInsensitive(dir, "sce_sys/icon0.png");
            if (!File.Exists(icon))
            {
                continue;
            }

            string title = id;
            string sfo = Io.Vfs.ResolveCaseInsensitive(dir, "sce_sys/param.sfo");
            try
            {
                if (File.Exists(sfo))
                {
                    var s = Sfo.Load(sfo);
                    if (s.Title.Length > 0)
                    {
                        title = s.Title.Replace('\n', ' ');
                    }
                }
            }
            catch (InvalidDataException) { }
            list.Add(new ShellSystemApp(id, title, dir, icon));
        }
        return list;
    }

    /// <summary>The title IDs of the shell database's bubbles in page and position order, or null when the database
    /// is missing or unreadable.</summary>
    private List<string>? ShellDatabaseBubbles()
    {
        string db = Path.Combine(FsRoot, "ur0", "shell", "db", "app.db");
        if (!File.Exists(db))
        {
            return null;
        }

        SQLitePCL.Batteries_V2.Init();
        // read only and immutable: the file is the shell's (a running shell may hold it open)
        if (SQLitePCL.raw.sqlite3_open_v2(new Uri(Path.GetFullPath(db)).AbsoluteUri + "?immutable=1",
                out sqlite3? h, SQLitePCL.raw.SQLITE_OPEN_READONLY | SQLitePCL.raw.SQLITE_OPEN_URI, null) != SQLitePCL.raw.SQLITE_OK)
        {
            h.Dispose();
            return null;
        }
        using (h)
        {
            if (SQLitePCL.raw.sqlite3_prepare_v2(h, "SELECT titleId FROM tbl_appinfo_icon WHERE titleId IS NOT NULL ORDER BY pageId, pos", out sqlite3_stmt? st) != SQLitePCL.raw.SQLITE_OK)
            {
                st.Dispose();
                return null;
            }
            using (st)
            {
                var ids = new List<string>();
                int rc;
                while ((rc = SQLitePCL.raw.sqlite3_step(st)) == SQLitePCL.raw.SQLITE_ROW)
                {
                    ids.Add(SQLitePCL.raw.sqlite3_column_text(st, 0).utf8_to_string());
                }

                return rc == SQLitePCL.raw.SQLITE_DONE ? ids : null;
            }
        }
    }

    // ------------------------------------------------------------------ RCO resources

    private Rco? RcoOf(string rel)
    {
        lock (_lock)
        {
            if (_rcos.TryGetValue(rel, out Rco? r))
            {
                return r;
            }

            string path = Vs0(rel.Split('/'));
            try
            {
                r = File.Exists(path) ? Rco.Load(path) : null;
            }
            catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                Util.Log.Warn($"shell: {path}: {e.Message}");
                r = null;
            }
            _rcos[rel] = r;
            return r;
        }
    }

    /// <summary>A texture of a shell RCO (GIM or PNG decoded to RGBA), or null when missing.</summary>
    public RgbaImage? Texture(string rco, string id)
    {
        string key = rco + "|" + id;
        lock (_lock)
        {
            if (_textures.TryGetValue(key, out RgbaImage? cached))
            {
                return cached;
            }
        }

        RgbaImage? img = null;
        Rco? r = RcoOf(rco);
        if (r?.Find("texturetable", id) is { } e)
        {
            try
            {
                byte[] data = r.ReadFile(e);
                if (Gim.IsGim(data))
                {
                    img = Gim.Decode(data);
                }
            }
            catch (InvalidDataException ex)
            {
                Util.Log.Warn($"shell: texture {id} of {rco}: {ex.Message}");
            }
        }
        lock (_lock)
        {
            _textures[key] = img;
        }

        return img;
    }

    /// <summary>The raw bytes of an RCO texture (PNG textures are returned as is), or null.</summary>
    public byte[]? TextureFile(string rco, string id)
    {
        Rco? r = RcoOf(rco);
        if (r?.Find("texturetable", id) is not { } e)
        {
            return null;
        }

        try
        {
            return r.ReadFile(e);
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>
    /// A system sound effect from common_resource.rco. The RCO's soundindextable names which sounddata each control
    /// plays: a check box's "check" sound is the decide (confirm) sound, its "uncheck" sound the cancel/back sound, and
    /// the third button sound set's "decide" an alternate one.
    /// </summary>
    public PcmSound? Sound(ShellSoundEffect effect)
    {
        lock (_lock)
        {
            if (_sounds.TryGetValue(effect, out PcmSound? cached))
            {
                return cached;
            }
        }

        PcmSound? pcm = null;
        Rco? r = RcoOf(CommonRco);
        RcoElement? index = r?.Root.Descendants().FirstOrDefault(e => e.Name == "soundindextable");
        if (r != null && (index != null || effect == ShellSoundEffect.Notice))
        {
            RcoElement? check = index?.Children.FirstOrDefault(c => c.Name == "sound_check_box");
            List<RcoElement> buttons = index?.Children.Where(c => c.Name == "sound_button").ToList() ?? [];
            uint? id = effect switch
            {
                ShellSoundEffect.Confirm => check?.Attribute("check")?.Hash ?? buttons.FirstOrDefault()?.Attribute("decide")?.Hash,
                ShellSoundEffect.Cancel => check?.Attribute("uncheck")?.Hash,
                // not a control sound: the system code plays it by id (TrophySound.FirmwareNoticeSound)
                ShellSoundEffect.Notice => TrophySound.FirmwareNoticeSound,
                _ => buttons.Select(b => b.Attribute("decide")?.Hash).Where(h => h != null)
                    .FirstOrDefault(h => h != check?.Attribute("check")?.Hash && h != check?.Attribute("uncheck")?.Hash),
            };
            if (id != null && r.Find("sounddatatable", $"0x{id:X8}") is { } sd)
            {
                try
                {
                    if (sd.Attribute("src") != null)
                    {
                        pcm = Vag.Decode(r.ReadFile(sd));
                    }
                    else if (sd.Attribute("left") != null)
                    {
                        pcm = Vag.Decode(r.ReadFile(sd, "left"), sd.Attribute("right") != null ? r.ReadFile(sd, "right") : null);
                    }
                }
                catch (InvalidDataException e)
                {
                    Util.Log.Warn($"shell: sound {effect}: {e.Message}");
                }
            }
        }
        lock (_lock)
        {
            _sounds[effect] = pcm;
        }

        return pcm;
    }
}
