using System.Text.Json;

namespace VitaSharp.Android.Storage;

/// <summary>The game process that is running (or was, until the marker is found stale).</summary>
/// <param name="GpuConfig">The GPU driver configuration the process loaded (<see cref="Gpu.GpuDriverScreen.ConfigOf"/>):
/// a process loads one Vulkan driver, so a different configuration needs a restart of the game.</param>
public sealed record GameProcessInfo(int Pid, string TitleId, string Title, string GpuConfig, DateTime StartedUtc);

/// <summary>
/// &lt;internal&gt;/run/game.json: written by the game process (":game") when a game starts and removed when it ends, so
/// the screens' process knows whether a game is running and with which GPU driver ("Driver da GPU" offers to restart
/// it after a change). A marker whose process is gone (killed, crashed) is ignored and deleted.
/// </summary>
public static class GameProcessMarker
{
    public static void Write(string path, GameProcessInfo info)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string tmp = path + ".tmp";
        using (var f = File.Create(tmp))
        using (var w = new Utf8JsonWriter(f, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteNumber("pid", info.Pid);
            w.WriteString("titleId", info.TitleId);
            w.WriteString("title", info.Title);
            w.WriteString("gpuConfig", info.GpuConfig);
            w.WriteString("started", info.StartedUtc);
            w.WriteEndObject();
        }
        File.Move(tmp, path, true);
    }

    /// <summary>The running game, or null (no marker, unreadable, or its process is gone: then the marker is deleted).</summary>
    public static GameProcessInfo? Read(string path, Func<int, bool> isAlive)
    {
        GameProcessInfo? info;
        try
        {
            if (!File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var r = doc.RootElement;
            string S(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            int pid = r.TryGetProperty("pid", out var p) && p.TryGetInt32(out int n) ? n : 0;
            DateTime started = r.TryGetProperty("started", out var s) && s.TryGetDateTime(out var d) ? d : default;
            info = new GameProcessInfo(pid, S("titleId"), S("title"), S("gpuConfig"), started);
        }
        catch (Exception e) when (e is JsonException or IOException or InvalidOperationException) { info = null; }
        if (info != null && info.Pid > 0 && isAlive(info.Pid)) return info;
        try { File.Delete(path); } catch (IOException) { }
        return null;
    }

    /// <summary>Removes the marker if it is this process's (a newer game process may have replaced it).</summary>
    public static void Clear(string path, int pid)
    {
        var info = Read(path, _ => true);
        if (info == null || info.Pid != pid) return;
        try { File.Delete(path); } catch (IOException) { }
    }

    /// <summary>True when <paramref name="pid"/> is a live game process of this app (Linux /proc: its command line is
    /// "&lt;package&gt;:game"). Processes of the same app share a user id, so /proc shows them.</summary>
    public static bool IsGameProcess(int pid)
    {
        try { return File.ReadAllText($"/proc/{pid}/cmdline").TrimEnd('\0').EndsWith(":game", StringComparison.Ordinal); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }
}
