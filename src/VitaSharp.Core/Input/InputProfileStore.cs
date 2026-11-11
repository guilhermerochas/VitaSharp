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

    /// <summary>Bumped by every change of <see cref="Config"/> (Put, Remove, a reload from disk), so the input of a running
    /// game recompiles its profiles at once (<see cref="CheckChanged"/>).</summary>
    public int Revision => Volatile.Read(ref _revision);

    int _revision;
    DateTime _fileTime;
    long _checkedAt;
    readonly object _diskLock = new();

    /// <summary>How often <see cref="CheckChanged"/> looks at the file's time stamp.</summary>
    public int DiskCheckIntervalMs { get; set; } = 500;

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
            if (File.Exists(path))
            {
                s._fileTime = File.GetLastWriteTimeUtc(path);
                s.Config = Deserialize(File.ReadAllText(path)) ?? new();
            }
        }
        catch (JsonException) { }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return s;
    }

    public void Save()
    {
        if (Path == null)
        {
            return;
        }

        if (System.IO.Path.GetDirectoryName(Path) is { Length: > 0 } dir)
        {
            Directory.CreateDirectory(dir);
        }

        var tmp = Path + ".tmp";
        File.WriteAllText(tmp, Serialize(Config));
        File.Move(tmp, Path, true);
        lock (_diskLock)
        {
            _fileTime = File.GetLastWriteTimeUtc(Path);
        }
    }

    /// <summary>
    /// True when the profiles changed since the caller last saw revision <paramref name="seen"/> (which is updated): a Put or
    /// Remove on this store, or input.json rewritten by another store (the launcher's Controles screen, another process),
    /// which is reloaded here. The file is looked at no more than every <see cref="DiskCheckIntervalMs"/>, so the
    /// game's input can call this on every poll.
    /// </summary>
    public bool CheckChanged(ref int seen)
    {
        RefreshFromDisk();
        int r = Revision;
        if (r == seen)
        {
            return false;
        }

        seen = r;
        return true;
    }

    /// <summary>Reloads input.json when its time stamp changed since it was last loaded or saved here.</summary>
    public void RefreshFromDisk()
    {
        if (Path == null)
        {
            return;
        }

        long now = Environment.TickCount64;
        if (now - Volatile.Read(ref _checkedAt) < DiskCheckIntervalMs)
        {
            return;
        }

        lock (_diskLock)
        {
            if (now - _checkedAt < DiskCheckIntervalMs)
            {
                return;
            }

            _checkedAt = now;
            try
            {
                var time = File.Exists(Path) ? File.GetLastWriteTimeUtc(Path) : default;
                if (time == _fileTime)
                {
                    return;
                }

                var config = time == default ? new InputConfig() : Deserialize(File.ReadAllText(Path));
                if (config == null)
                {
                    return;
                }

                _fileTime = time;
                Config = config;
                Interlocked.Increment(ref _revision);
            }
            // a half-written file (the writer replaces it atomically, but another editor may not): retried next time
            catch (JsonException) { }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public static string Serialize(InputConfig c) => JsonSerializer.Serialize(c, InputJson.Default.InputConfig);
    public static InputConfig? Deserialize(string json) => JsonSerializer.Deserialize(json, InputJson.Default.InputConfig);

    /// <summary>The keyboard profile (a copy). A profile saved before the virtual motion existed (version 1) gets the
    /// default tilt keys that do not clash with its own bindings; one saved before the pause hotkey (version 2) gets
    /// its default key the same way.</summary>
    public ControllerProfile KeyboardProfile()
    {
        if (Config.Keyboard == null)
        {
            return DefaultMappings.Keyboard();
        }

        var p = Config.Keyboard.Clone();
        if (Config.Version < 2)
        {
            DefaultMappings.AddMotionKeys(p);
        }

        if (Config.Version < 3)
        {
            DefaultMappings.AddHotkeys(p);
        }

        return p;
    }

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
        if (p.Id == ControllerProfile.KeyboardId)
        {
            Config.Keyboard = copy;
            Config.Version = InputConfig.CurrentVersion; // stored as the player sees it, motion keys included
            Interlocked.Increment(ref _revision);
            return;
        }
        int i = Config.Controllers.FindIndex(x => x.Id.Equals(p.Id, StringComparison.OrdinalIgnoreCase));
        if (i >= 0)
        {
            Config.Controllers[i] = copy;
        }
        else
        {
            Config.Controllers.Add(copy);
        }

        Interlocked.Increment(ref _revision);
    }

    /// <summary>Forgets a device's profile, so it goes back to the default mapping.</summary>
    public void Remove(string id)
    {
        if (id == ControllerProfile.KeyboardId)
        {
            Config.Keyboard = null;
        }
        else
        {
            Config.Controllers.RemoveAll(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        }

        Interlocked.Increment(ref _revision);
    }
}
