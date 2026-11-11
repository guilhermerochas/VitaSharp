using VitaSharp.Core.Localization;
using VitaSharp.Core;
using VitaSharp.Core.SaveState;
using VitaSharp.Core.Util;
using VitaSharp.Renderer.Vulkan;

namespace VitaSharp.Android.Game;

/// <summary>Save states on Android ("Salvar estado" / "Carregar estado" in the pause menu): slots 1-6 in
/// &lt;data&gt;/savestates/&lt;TITLEID&gt;/, each with a picture of the screen taken when the menu opened.</summary>
public sealed partial class EmulationSession
{
    public const int StateSlots = 6;
    byte[]? _menuThumbnail;

    public string StatePath(int slot) => SaveStates.SlotPath(Services.AppServices.Data.Root, _game.TitleId, slot);

    /// <summary>Header of the state in <paramref name="slot"/>, or null when the slot is empty or unreadable.</summary>
    public SaveStateHeader? StateInfo(int slot)
    {
        try
        {
            return File.Exists(StatePath(slot)) ? SaveStateFile.ReadHeader(StatePath(slot)) : null;
        }
        catch (Exception e) when (e is SnapshotException or IOException)
        {
            return null;
        }
    }

    /// <summary>Reads back the next presented frame (at most 150 ms) as the picture of a state saved from the menu.</summary>
    public void CaptureMenuThumbnail()
    {
        _menuThumbnail = null;
        if (Paused || !_emuStarted)
            return;
        byte[]? px = null;
        int w = 0, h = 0;
        using var got = new ManualResetEventSlim();
        var previous = _rendererOptions.FrameSink;
        _rendererOptions.FrameSink = (n, p, pw, ph) =>
        {
            previous?.Invoke(n, p, pw, ph);
            if (!got.IsSet)
            {
                px = p;
                w = pw;
                h = ph;
                got.Set();
            }
        };
        try
        {
            got.Wait(150);
        }
        finally
        {
            _rendererOptions.FrameSink = previous;
        }
        if (px == null)
            return;
        string tmp = Path.Combine(Path.GetTempPath(), "vitasharp-thumb.png");
        try
        {
            Png.Write(tmp, px, w, h);
            _menuThumbnail = File.ReadAllBytes(tmp);
        }
        catch (IOException) { }
    }

    /// <summary>Saves to <paramref name="slot"/>; returns the message to show.</summary>
    public string SaveState(int slot)
    {
        try
        {
            SaveStates.Save(_emu, StatePath(slot), _menuThumbnail);
            return Loc.Format("MStateSaved", slot);
        }
        catch (SnapshotException e)
        {
            Log.Warn($"Save state: {e.Message}");
            return Loc.Format("MStateSaveFailed", e.Message);
        }
    }

    public string LoadState(int slot)
    {
        Performance.SetLoading(true); // until the next presented frame (GamePerformance.OnFrame)
        try
        {
            // a state of another executable of the game (saved after a LoadExec: Jak 1 of the Jak and Daxter
            // Collection, God of War of its collection) while the game's menu runs: that one is started first (as on
            // the desktop). The LoadExec and its first frames need the emulation running, the pause menu paused it.
            if (SaveStates.ProgramOf(StatePath(slot)) is { } program && _emu.MainModule is { } main
                && !string.Equals(program, main.Path, StringComparison.OrdinalIgnoreCase))
            {
                bool paused = EmulationPause.IsPaused;
                if (paused)
                    EmulationPause.Resume("load state: LoadExec");
                try
                {
                    SaveStates.SwitchProgramFor(_emu, StatePath(slot), s => CaptureStatus?.Invoke(s));
                }
                finally
                {
                    if (paused)
                        EmulationPause.Pause("load state: LoadExec");
                }
            }
            SaveStates.Load(_emu, StatePath(slot), () => { lock (_surfaceLock) return _renderer = new VulkanRenderer(_rendererOptions); });
            return Loc.Format("MStateLoaded", slot);
        }
        catch (Exception e) when (e is SnapshotException or IOException)
        {
            Log.Warn($"Load state: {e.Message}");
            return e is SaveStateIncompatibleException ? e.Message : Loc.Format("MStateLoadFailed", e.Message);
        }
    }
}
