using VitaSharp.Core.Localization;
using VitaSharp.Core;
using VitaSharp.Core.Gpu;
using VitaSharp.Core.SaveState;
using VitaSharp.Core.Util;
using VitaSharp.Renderer.Vulkan;

namespace VitaSharp.Desktop;

/// <summary>
/// Save states on the desktop (docs/notes/save-states.md): the window's hotkeys (F5 quick save, F7 quick load, F6
/// picks a slot 1-9 in the save state picker (Host.SaveStateOverlay), Shift+F5 / Shift+F7 save / load that slot), --load-state=PATH at start, and the scheduled
/// test steps --save-state=fN[,fN...] and --load-state-at=fN[,fN...] (loads the last state this run saved).
/// States live in &lt;data&gt;/savestates/&lt;TITLEID&gt;/ with a small picture of the screen.
/// </summary>
sealed class SaveStateControl(Emulator emu, VulkanRendererOptions ro, Func<IGpuBackend?> backendFactory, Action<string>? status, Action<string>? error = null)
{
    int _slot = 1;
    string? _lastSaved;
    int _busy;

    public int Slot => _slot;

    public string PathOf(int slot) => SaveStates.SlotPath(Program.DataRoot, emu.TitleId, slot);

    /// <summary>Why the state at <paramref name="path"/> cannot be loaded into this game and build, or null.</summary>
    public string? LoadProblem(string path) => SaveStates.CheckFile(emu, path);

    /// <summary>The slot picked in the save state picker becomes the one Shift+F5 / Shift+F7 use (the quick slot leaves it).</summary>
    public void Select(int slot) { if (slot is >= 1 and <= 9) _slot = slot; }

    public void NextSlot()
    {
        _slot = _slot % 9 + 1;
        string p = PathOf(_slot);
        status?.Invoke(File.Exists(p) ? Loc.Format("StateSlotSaved", _slot, File.GetLastWriteTime(p)) : Loc.Format("StateSlotEmpty", _slot));
    }

    /// <summary>Saves to <paramref name="slot"/> (0 = quick save) off the calling thread. <paramref name="thumbnail"/>:
    /// the picture to store (the save state picker's, taken when it opened), else the next presented frame.</summary>
    public void SaveAsync(int slot, byte[]? thumbnail = null) => Run(() => Save(PathOf(slot), slot == 0 ? Loc.Get("StateQuickLabel") : slot.ToString(), thumbnail));

    public void LoadAsync(int slot) => Run(() => Load(PathOf(slot), slot == 0 ? Loc.Get("StateQuickLabel") : slot.ToString()));

    void Run(Action a)
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        Task.Run(() => { try { a(); } finally { Volatile.Write(ref _busy, 0); } });
    }

    public bool Save(string path, string label = "", byte[]? thumbnail = null)
    {
        try
        {
            byte[]? thumb = thumbnail ?? (EmulationPause.IsPaused ? null : GrabThumbnail(ro, 300));
            SaveStates.Save(emu, path, thumb);
            _lastSaved = path;
            status?.Invoke(Loc.Format("StateSaved", label));
            return true;
        }
        catch (SnapshotException e)
        {
            Log.Warn($"Save state: {e.Message}");
            (error ?? status)?.Invoke(Loc.Format("StateSaveFailed", e.Message));
            return false;
        }
    }

    public bool Load(string path, string label = "")
    {
        if (!File.Exists(path)) { status?.Invoke(Loc.Format("StateEmpty", label)); return false; }
        try
        {
            // a state of another executable of the game (saved after a LoadExec): that one is started first
            SaveStates.SwitchProgramFor(emu, path, status);
            SaveStates.Load(emu, path, emu.Gpu.Backend != null ? backendFactory : null);
            status?.Invoke(Loc.Format("StateLoaded", label));
            return true;
        }
        catch (Exception e) when (e is SnapshotException or IOException)
        {
            Log.Warn($"Load state: {e.Message}");
            (error ?? status)?.Invoke(e is SaveStateIncompatibleException ? e.Message : Loc.Format("StateLoadFailed", e.Message));
            return false;
        }
    }

    /// <summary>Size of the pictures stored in save states: large enough for the picker's big preview.</summary>
    public const int ThumbWidth = 480, ThumbHeight = 272;

    /// <summary>A picture of the next presented frame as PNG bytes (<see cref="ThumbWidth"/> x <see cref="ThumbHeight"/>,
    /// from the renderer's one-shot screenshot; the small frame sink when that does not come in time), or null.</summary>
    public static byte[]? GrabThumbnail(VulkanRendererOptions ro, int timeoutMs)
    {
        string shot = Path.Combine(Path.GetTempPath(), $"vitasharp-shot-{Environment.ProcessId}-{Environment.TickCount64}.png");
        try
        {
            ro.ShotPath = shot;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs + 700) // the frame retires a few frames later, then the PNG is written
            {
                if (File.Exists(shot) && ScaleShot(shot) is { } png) return png;
                Thread.Sleep(15);
            }
            Log.Info("Save state: no screenshot in time, using the small frame picture");
        }
        finally
        {
            Interlocked.CompareExchange(ref ro.ShotPath, null, shot);
            try { File.Delete(shot); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        return GrabSinkThumbnail(ro, timeoutMs);
    }

    /// <summary>The screenshot at <paramref name="path"/> scaled to the thumbnail size as PNG; null while it is still
    /// being written.</summary>
    static byte[]? ScaleShot(string path)
    {
        try
        {
            byte[] bytes = File.ReadAllBytes(path); // the writer holds the file open exclusively until it is complete
            if (bytes.Length < 12 || !bytes.AsSpan(bytes.Length - 8, 4).SequenceEqual("IEND"u8)) return null;
            using var full = SkiaSharp.SKBitmap.Decode(bytes);
            if (full == null) return null;
            using var small = full.Resize(new SkiaSharp.SKImageInfo(ThumbWidth, ThumbHeight), SkiaSharp.SKFilterQuality.Medium);
            if (small == null) return null;
            using var data = small.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
            return data?.ToArray();
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    static byte[]? GrabSinkThumbnail(VulkanRendererOptions ro, int timeoutMs)
    {
        byte[]? px = null;
        int w = 0, h = 0;
        using var got = new ManualResetEventSlim();
        var previous = ro.FrameSink;
        ro.FrameSink = (n, p, pw, ph) =>
        {
            previous?.Invoke(n, p, pw, ph);
            if (got.IsSet) return;
            px = p; w = pw; h = ph;
            got.Set();
        };
        try { got.Wait(timeoutMs); }
        finally { ro.FrameSink = previous; }
        if (px == null) return null;
        string tmp = Path.Combine(Path.GetTempPath(), $"vitasharp-thumb-{Environment.ProcessId}.png");
        try
        {
            Png.Write(tmp, px, w, h);
            return File.ReadAllBytes(tmp);
        }
        catch (IOException) { return null; }
        finally { try { File.Delete(tmp); } catch (IOException) { } }
    }

    /// <summary>--save-state=fN[,fN...] (to &lt;data&gt;/savestates/&lt;TITLEID&gt;/test_fN.vss), --load-state-at=fN[,...]
    /// (the last state saved by this run, or --load-state's file) and --load-state=PATH[@fN] (default: after 120
    /// presented frames, once the game's modules and service threads exist).</summary>
    public void StartScheduled(string[] options)
    {
        var steps = new SortedList<long, Action>();
        void Add(long frame, Action a) { while (steps.ContainsKey(frame)) frame++; steps.Add(frame, a); }
        string? loadPath = null;
        if (Runner.Opt(options, "--load-state") is { } ls)
        {
            int at = ls.LastIndexOf('@');
            long frame = 120;
            if (at > 1 && long.TryParse(ls[(at + 1)..].TrimStart('f'), out var lf)) { frame = lf; ls = ls[..at]; }
            loadPath = ls;
            Add(frame, () => Load(ls, Path.GetFileName(ls)));
        }
        if (Runner.Opt(options, "--save-state") is { } ss)
            foreach (var f in ss.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(s => long.Parse(s.TrimStart('f'))))
                Add(f, () => Save(Path.Combine(SaveStates.Folder(Program.DataRoot, emu.TitleId), $"test_f{f}.vss"), $"f{f}"));
        if (Runner.Opt(options, "--load-state-at") is { } la)
            foreach (var f in la.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(s => long.Parse(s.TrimStart('f'))))
                Add(f, () => { if ((_lastSaved ?? loadPath) is { } p) Load(p, Path.GetFileName(p)); else Log.Warn("--load-state-at: no state saved yet"); });
        if (steps.Count == 0) return;
        new Thread(() =>
        {
            while (steps.Count > 0)
            {
                if (VitaSharp.Core.Hle.Modules.Display.FramesPresented >= steps.Keys[0])
                {
                    var a = steps.Values[0];
                    steps.RemoveAt(0);
                    Log.Info($"Save state step at guest frame {VitaSharp.Core.Hle.Modules.Display.FramesPresented}");
                    a();
                }
                Thread.Sleep(5);
            }
        }) { IsBackground = true, Name = "save-state steps" }.Start();
    }
}
