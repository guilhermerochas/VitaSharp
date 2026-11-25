using System.Text.Json;
using System.Text.Json.Serialization;

namespace VitaSharp.Core.Util;

/// <summary>
/// "It worked before" (roadmap "O log e a captura F8 dizem a causa de uma trava de cara"): per title, the last emulator
/// version that ran it past <see cref="Frames"/> presented frames without a stall, in &lt;data&gt;/good-runs.json (next to
/// the launcher's playtime.json). A session that stalls says in its STALL line which version last ran the title
/// without one: the start of a bisection, without asking the user. The front end's watchdog records and asks.
/// </summary>
public static class GoodRuns
{
    /// <summary>Frames a session presents without a stall before it counts as a good run (about 30 s at 60 fps).</summary>
    public const long Frames = 1800;

    /// <summary>The file in <paramref name="dataRoot"/>.</summary>
    public static string FilePath(string dataRoot) => Path.Combine(dataRoot, "good-runs.json");

    /// <summary>The good runs by title ID (empty when the file is missing or unreadable).</summary>
    public static Dictionary<string, GoodRun> Load(string dataRoot)
    {
        try
        {
            string path = FilePath(dataRoot);
            if (File.Exists(path))
            {
                return JsonSerializer.Deserialize(File.ReadAllText(path), GoodRunsJson.Default.DictionaryStringGoodRun) ?? [];
            }
        }
        catch (JsonException)
        {
        }
        catch (IOException)
        {
        }

        return [];
    }

    /// <summary>Records <paramref name="version"/> as the last one that ran <paramref name="titleId"/> without a stall.</summary>
    public static void Record(string dataRoot, string titleId, string version, DateTime utc)
    {
        Dictionary<string, GoodRun> all = Load(dataRoot);
        all[titleId] = new GoodRun { Version = version, Utc = utc };
        try
        {
            Directory.CreateDirectory(dataRoot);
            string path = FilePath(dataRoot);
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(all, GoodRunsJson.Default.DictionaryStringGoodRun));
            File.Move(path + ".tmp", path, true);
        }
        catch (IOException failure)
        {
            Log.Warn($"good runs: {failure.Message}");
        }
    }

    /// <summary>The line a stalled session logs: the version that last ran the title without a stall, or that none
    /// is known.</summary>
    public static string Describe(string dataRoot, string titleId, string version)
    {
        if (!Load(dataRoot).TryGetValue(titleId, out GoodRun? run))
        {
            return $"{titleId}: no version is known to have run it without a stall";
        }

        string same = run.Version == version ? " (this version)" : $" (this one is {version})";
        return $"{titleId} ran without a stall on version {run.Version}, {run.Utc:yyyy-MM-dd HH:mm} UTC{same}";
    }
}

/// <summary>
/// One session's side of <see cref="GoodRuns"/>, for every front end: a session that presented
/// <see cref="GoodRuns.Frames"/> frames and has not stalled is recorded as a good run (once), and a stall gets the line
/// that names the version that last ran the title without one. The desktop's watchdog detects its stalls itself and
/// tells the tracker (<see cref="NoteFrames"/>, <see cref="NoteStall"/>); a front end without one (Android) calls
/// <see cref="Tick"/> about once a second, which detects a stall as the watchdog does (no frame presented for
/// <see cref="StallSeconds"/> once the game presents, a pause not counted) and returns the line to log.
/// </summary>
public sealed class GoodRunTracker(string dataRoot, Func<string?> titleId, string version)
{
    /// <summary>Seconds without a presented frame that make a stall (the desktop watchdog's first report).</summary>
    public const double StallSeconds = 5;

    private long _lastFrames = -1;
    private DateTime _frameAt;
    private bool _stalled;
    private bool _recorded;
    private bool _reported;

    /// <summary>The session stalled (it is no good run any more).</summary>
    public bool Stalled => _stalled;

    /// <summary>The frames presented so far: past <see cref="GoodRuns.Frames"/> without a stall, the good run is recorded.</summary>
    public void NoteFrames(long frames)
    {
        if (_stalled || _recorded || frames < GoodRuns.Frames || titleId() is not { Length: > 0 } title)
        {
            return;
        }

        _recorded = true;
        GoodRuns.Record(dataRoot, title, version, DateTime.UtcNow);
    }

    /// <summary>The session stalled: no good run from it, and the line for its report.</summary>
    public string NoteStall()
    {
        _stalled = true;
        return titleId() is { Length: > 0 } title ? "good run: " + GoodRuns.Describe(dataRoot, title, version) : "good run: no title";
    }

    /// <summary>For a front end without a watchdog: called about once a second with the frames presented; the STALL
    /// line to log when a stall starts (once per stall), else null.</summary>
    public string? Tick(long frames, DateTime now, bool paused)
    {
        if (paused || frames != _lastFrames)
        {
            _lastFrames = frames;
            _frameAt = now;
            _reported = false;
            NoteFrames(frames);
            return null;
        }

        double seconds = (now - _frameAt).TotalSeconds;
        if (frames <= 0 || _reported || seconds < StallSeconds)
        {
            return null;
        }

        _reported = true;
        return $"STALL: no frame presented for {seconds:F0} s (frame {frames})\n{NoteStall()}";
    }
}

/// <summary>One title's good run: the version and when it was recorded.</summary>
public sealed class GoodRun
{
    public string Version { get; set; } = "";

    public DateTime Utc { get; set; }
}

[JsonSerializable(typeof(Dictionary<string, GoodRun>))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal sealed partial class GoodRunsJson : JsonSerializerContext
{
}
