using VitaSharp.Core;
using VitaSharp.Core.Gpu;
using VitaSharp.Core.SaveState;
using VitaSharp.Core.Util;
using VitaSharp.Renderer.Vulkan;

namespace VitaSharp.Desktop;

/// <summary>
/// Save states on the desktop (docs/notes/save-states.md): the window's hotkeys (F5 quick save, F7 quick load, F6
/// picks a slot 1-9, Shift+F5 / Shift+F7 save / load that slot), --load-state=PATH at start, and the scheduled
/// test steps --save-state=fN[,fN...] and --load-state-at=fN[,fN...] (loads the last state this run saved).
/// States live in &lt;data&gt;/savestates/&lt;TITLEID&gt;/ with a small picture of the screen.
/// </summary>
sealed class SaveStateControl(Emulator emu, VulkanRendererOptions ro, Func<IGpuBackend?> backendFactory, Action<string>? status)
{
    int _slot = 1;
    string? _lastSaved;
    int _busy;

    public int Slot => _slot;

    string PathOf(int slot) => SaveStates.SlotPath(Program.DataRoot, emu.TitleId, slot);

    public void NextSlot()
    {
        _slot = _slot % 9 + 1;
        string p = PathOf(_slot);
        status?.Invoke(File.Exists(p) ? $"Estado {_slot}: salvo em {File.GetLastWriteTime(p):g}" : $"Estado {_slot}: vazio");
    }

    /// <summary>Saves to <paramref name="slot"/> (0 = quick save) off the calling thread.</summary>
    public void SaveAsync(int slot) => Run(() => Save(PathOf(slot), slot == 0 ? "rápido" : slot.ToString()));

    public void LoadAsync(int slot) => Run(() => Load(PathOf(slot), slot == 0 ? "rápido" : slot.ToString()));

    void Run(Action a)
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        Task.Run(() => { try { a(); } finally { Volatile.Write(ref _busy, 0); } });
    }

    public bool Save(string path, string label = "")
    {
        try
        {
            byte[]? thumb = EmulationPause.IsPaused ? null : GrabThumbnail(ro, 300);
            SaveStates.Save(emu, path, thumb);
            _lastSaved = path;
            status?.Invoke($"Estado {label} salvo");
            return true;
        }
        catch (SnapshotException e)
        {
            Log.Warn($"Save state: {e.Message}");
            status?.Invoke($"Não foi possível salvar o estado: {e.Message}");
            return false;
        }
    }

    public bool Load(string path, string label = "")
    {
        if (!File.Exists(path)) { status?.Invoke($"Estado {label} vazio"); return false; }
        try
        {
            SaveStates.Load(emu, path, emu.Gpu.Backend != null ? backendFactory : null);
            status?.Invoke($"Estado {label} carregado");
            return true;
        }
        catch (Exception e) when (e is SnapshotException or IOException)
        {
            Log.Warn($"Load state: {e.Message}");
            status?.Invoke($"Não foi possível carregar o estado: {e.Message}");
            return false;
        }
    }

    /// <summary>A picture of the next presented frame as PNG bytes (the renderer's small frame sink), or null.</summary>
    public static byte[]? GrabThumbnail(VulkanRendererOptions ro, int timeoutMs)
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
