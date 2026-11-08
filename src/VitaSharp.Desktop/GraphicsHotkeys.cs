using VitaSharp.Core;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.SaveState;
using VitaSharp.Core.Util;
using VitaSharp.Renderer.Vulkan;

namespace VitaSharp.Desktop;

/// <summary>
/// Graphics options changed while the game runs (the window's F1 / F2, and key@fN:F1 steps of an --input script):
/// F1 the next internal resolution (1x, 2x, 3x, 4x, then back to 1x), Shift+F1 the previous one; F2 the next frame
/// rate mode (native, fixed 30, fixed 60, unlimited), Shift+F2 the previous one; F3 the next MSAA mode (native
/// multisampling, 2x supersampling, off), Shift+F3 the previous one. Each change shows a notice with the
/// new value. They last for the session: the launcher's per-game settings are not changed.
/// <para>
/// A new resolution needs a new renderer (render targets, depth images and pipelines are sized by it): the guest
/// threads are held between two GPU scenes and the backend is replaced, as for a state load, and the new one builds
/// its caches again from guest memory (<see cref="SaveStates.ReplaceBackendBetweenScenes"/>). The frame rate mode is
/// read at every display wait, so it changes at once.
/// </para>
/// </summary>
sealed class GraphicsHotkeys(Emulator emu, VulkanRendererOptions ro, Func<VulkanRenderer?> current, Func<VulkanRenderer> factory, Action<string> notice)
{
    static readonly float[] Scales = [1f, 2f, 3f, 4f];
    static readonly FrameRateMode[] Modes = [FrameRateMode.Native, FrameRateMode.Fixed30, FrameRateMode.Fixed60, FrameRateMode.Unlimited];
    int _busy;

    /// <summary>Host key names of the hotkeys (window keys and --input steps).</summary>
    public static bool IsHotkey(string key) => key is "F1" or "Shift+F1" or "F2" or "Shift+F2" or "F3" or "Shift+F3";

    /// <summary>Runs the hotkey <paramref name="key"/> ("F1", "Shift+F1", "F2", "Shift+F2", "F3", "Shift+F3").</summary>
    public void Press(string key)
    {
        bool back = key.StartsWith("Shift+", StringComparison.Ordinal);
        if (key.EndsWith("F1", StringComparison.Ordinal)) CycleResolution(back);
        else if (key.EndsWith("F2", StringComparison.Ordinal)) CycleFrameRate(back);
        else if (key.EndsWith("F3", StringComparison.Ordinal)) CycleMsaa(back);
    }

    static readonly string[] MsaaModes = ["native", "ss", "off"];

    /// <summary>The next (or previous) MSAA mode (real multisampling, 2x supersampling, off): like a new resolution, a
    /// new renderer replaces the current one between two GPU scenes.</summary>
    public void CycleMsaa(bool back)
    {
        if (current() is not { } r) return;
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        string from = r.MsaaMode;
        string to = MsaaModes[Next(Array.IndexOf(MsaaModes, from), MsaaModes.Length, back)];
        Task.Run(() =>
        {
            try
            {
                string? saved = ro.Msaa;
                ro.Msaa = to;
                if (SaveStates.ReplaceBackendBetweenScenes(emu, factory) is { } why)
                {
                    ro.Msaa = saved;
                    Log.Warn($"MSAA {from} -> {to} not applied: {why}");
                    notice($"MSAA: não foi possível trocar agora ({why})");
                    return;
                }
                Log.Info($"MSAA {from} -> {to} (hotkey) at guest frame {Display.FramesPresented}");
                notice($"MSAA: {MsaaLabel(to)}");
            }
            catch (Exception e)
            {
                Log.Error($"MSAA change failed: {e}");
                notice("MSAA: falhou (veja o log)");
            }
            finally { Volatile.Write(ref _busy, 0); }
        });
    }

    static string MsaaLabel(string mode) => mode switch
    {
        "ss" => "superamostragem 2x",
        "off" => "desligado",
        _ => "nativo (multiamostragem real)",
    };

    static int Next(int i, int count, bool back) => back ? (i <= 0 ? count - 1 : i - 1) : (i < 0 ? 0 : (i + 1) % count);

    /// <summary>The next (or previous) internal resolution; a factor below native (0.5, 0.75) goes to 1x first.</summary>
    public void CycleResolution(bool back)
    {
        if (current() is not { } r) return; // no renderer (a headless run without frames)
        if (Interlocked.Exchange(ref _busy, 1) == 1) return; // a change is still being applied
        float from = r.InternalScale;
        float to = Scales[Next(Array.IndexOf(Scales, from), Scales.Length, back)];
        Task.Run(() =>
        {
            try
            {
                float saved = ro.ResScale;
                ro.ResScale = to;
                if (SaveStates.ReplaceBackendBetweenScenes(emu, factory) is { } why)
                {
                    ro.ResScale = saved;
                    Log.Warn($"Internal resolution {from}x -> {to}x not applied: {why}");
                    notice($"Resolução interna: não foi possível trocar agora ({why})");
                    return;
                }
                Log.Info($"Internal resolution {from}x -> {to}x (hotkey) at guest frame {Display.FramesPresented}");
                notice($"Resolução interna: {Label(to)}");
            }
            catch (Exception e)
            {
                Log.Error($"Internal resolution change failed: {e}");
                notice("Resolução interna: falhou (veja o log)");
            }
            finally { Volatile.Write(ref _busy, 0); }
        });
    }

    /// <summary>The next (or previous) frame rate mode; display waits read it at once.</summary>
    public void CycleFrameRate(bool back)
    {
        var to = Modes[Next(Array.IndexOf(Modes, FrameRate.Mode), Modes.Length, back)];
        FrameRate.Mode = to;
        Log.Info($"Frame rate: {FrameRate.Name(to)} (hotkey) at guest frame {Display.FramesPresented}");
        notice($"Taxa de quadros: {Label(to)}");
    }

    static string Label(float scale) => scale == 1f ? "1x (nativa, 960x544)" : $"{scale:0}x ({960 * scale:0}x{544 * scale:0})";

    static string Label(FrameRateMode m) => m switch
    {
        FrameRateMode.Fixed30 => "fixa em 30",
        FrameRateMode.Fixed60 => "fixa em 60",
        FrameRateMode.Unlimited => "sem limite",
        _ => "nativa (a do jogo)",
    };
}
