using System.Text.Json;

namespace VitaSharp.Desktop.Launcher;

/// <summary>Launcher preferences, stored as JSON in the data folder.</summary>
public sealed class LauncherSettings
{
    public bool Fullscreen { get; set; }
    public int Scale { get; set; } = 1;
    public bool VSync { get; set; } = true;
    public bool Validation { get; set; }
    public bool ShowLog { get; set; }

    static string FilePath => Path.Combine(Program.DataRoot, "launcher.json");

    public static LauncherSettings Load()
    {
        try
        {
            if (File.Exists(FilePath)) return JsonSerializer.Deserialize<LauncherSettings>(File.ReadAllText(FilePath)) ?? new();
        }
        catch (JsonException) { }
        catch (IOException) { }
        return new();
    }

    public void Save()
    {
        Directory.CreateDirectory(Program.DataRoot);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>Command-line options for "run".</summary>
    public IEnumerable<string> RunOptions()
    {
        if (Fullscreen) yield return "--fullscreen";
        if (Scale > 1) yield return $"--scale={Scale}";
        if (!VSync) yield return "--no-vsync";
        if (Validation) yield return "--validation";
    }
}
