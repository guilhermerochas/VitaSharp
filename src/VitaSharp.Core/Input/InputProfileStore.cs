using System.Text.Json;

namespace VitaSharp.Core.Input;

/// <summary>
/// Loads and saves input.json and picks the profile for a device: the saved profile with the same GUID, else one
/// with the same name (the GUID of a controller can change between USB and Bluetooth or between platforms), else the
/// built-in default for its family. Devices without a saved profile keep the historic default mapping.
/// </summary>
public sealed class InputProfileStore
{
    public InputConfig Config { get; private set; } = new();
    public string? Path { get; }

    public const string FileName = "input.json";

    public InputProfileStore(string? path = null)
    {
        Path = path;
    }

    public static InputProfileStore Load(string path)
    {
        var s = new InputProfileStore(path);
        try
        {
            if (File.Exists(path)) s.Config = Deserialize(File.ReadAllText(path)) ?? new();
        }
        catch (JsonException) { }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return s;
    }

    public void Save()
    {
        if (Path == null) return;
        if (System.IO.Path.GetDirectoryName(Path) is { Length: > 0 } dir) Directory.CreateDirectory(dir);
        var tmp = Path + ".tmp";
        File.WriteAllText(tmp, Serialize(Config));
        File.Move(tmp, Path, true);
    }

    public static string Serialize(InputConfig c) => JsonSerializer.Serialize(c, InputJson.Default.InputConfig);
    public static InputConfig? Deserialize(string json) => JsonSerializer.Deserialize(json, InputJson.Default.InputConfig);

    public ControllerProfile KeyboardProfile() => Config.Keyboard?.Clone() ?? DefaultMappings.Keyboard();

    /// <summary>The saved profile for a controller, or null.</summary>
    public ControllerProfile? FindSaved(string guid, string name)
        => Config.Controllers.FirstOrDefault(p => p.Id.Equals(guid, StringComparison.OrdinalIgnoreCase))
           ?? Config.Controllers.FirstOrDefault(p => p.Name.Length > 0 && p.Name.Equals(name, StringComparison.Ordinal));

    /// <summary>The profile to use for a controller (a copy: editing it does not change the store).</summary>
    public ControllerProfile ProfileFor(string guid, string name, ControllerKind kind, bool isGamepad)
        => FindSaved(guid, name)?.Clone() ?? DefaultMappings.For(guid, name, kind, isGamepad);

    /// <summary>Stores (or replaces) a profile; the keyboard profile has Id "keyboard".</summary>
    public void Put(ControllerProfile p)
    {
        var copy = p.Clone();
        if (p.Id == ControllerProfile.KeyboardId) { Config.Keyboard = copy; return; }
        int i = Config.Controllers.FindIndex(x => x.Id.Equals(p.Id, StringComparison.OrdinalIgnoreCase));
        if (i >= 0) Config.Controllers[i] = copy; else Config.Controllers.Add(copy);
    }

    /// <summary>Forgets a device's profile, so it goes back to the default mapping.</summary>
    public void Remove(string id)
    {
        if (id == ControllerProfile.KeyboardId) Config.Keyboard = null;
        else Config.Controllers.RemoveAll(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
    }
}
