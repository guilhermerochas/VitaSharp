using VitaSharp.Core.Package;

namespace VitaSharp.Desktop.Launcher;

/// <summary>An installed title found under ux0:app.</summary>
public sealed record GameEntry(string TitleId, string Title, string Version, string Category, string Path, string? IconPath, long SizeBytes)
{
    public string SizeText => SizeBytes switch
    {
        >= 1L << 30 => $"{SizeBytes / (double)(1L << 30):F2} GB",
        >= 1L << 20 => $"{SizeBytes / (double)(1L << 20):F0} MB",
        _ => $"{SizeBytes / 1024.0:F0} KB",
    };

    public string CategoryText => Category switch
    {
        "gd" => "Game",
        "gp" => "Game patch",
        "ac" => "Add-on",
        _ => Category,
    };
}

/// <summary>Scans the virtual ux0:app folder (titles installed with "install" or copied in as decrypted folders).</summary>
public static class GameLibrary
{
    public static List<GameEntry> Scan(string ux0)
    {
        var list = new List<GameEntry>();
        var appDir = System.IO.Path.Combine(ux0, "app");
        if (!Directory.Exists(appDir)) return list;
        foreach (var dir in Directory.GetDirectories(appDir))
        {
            var entry = Read(dir);
            if (entry != null) list.Add(entry);
        }
        return list.OrderBy(g => g.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public static GameEntry? Read(string dir)
    {
        if (!File.Exists(VitaSharp.Core.Io.Vfs.ResolveCaseInsensitive(dir, "eboot.bin"))) return null;
        string id = System.IO.Path.GetFileName(dir), title = id, version = "", category = "gd";
        var sfoPath = VitaSharp.Core.Io.Vfs.ResolveCaseInsensitive(dir, "sce_sys/param.sfo");
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
        var icon = VitaSharp.Core.Io.Vfs.ResolveCaseInsensitive(dir, "sce_sys/icon0.png");
        return new GameEntry(id, title, version, category, dir, File.Exists(icon) ? icon : null, DirectorySize(dir));
    }

    static long DirectorySize(string dir)
    {
        try { return new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length); }
        catch (IOException) { return 0; }
        catch (UnauthorizedAccessException) { return 0; }
    }
}
