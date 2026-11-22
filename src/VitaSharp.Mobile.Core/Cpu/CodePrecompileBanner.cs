using VitaSharp.Core.Cpu;
using VitaSharp.Core.Localization;

namespace VitaSharp.Mobile.Core.Cpu;

/// <summary>
/// "Precompilar codigo ARM" on the Android game screen: while the boot step translates the game's ARM code before it
/// starts (<see cref="CodePrecompileLog.Reported"/>), a banner over the game shows the progress and a "Cancelar" that
/// lets the game start right away (<see cref="CodePrecompileLog.Cancel"/>; the rest goes on in the background), the same
/// text and button as the desktop launcher's status bar. Separate from the shader cache load's banner: the two waits are
/// different work. Platform-neutral, so it is unit-tested on the desktop.
/// </summary>
public sealed class CodePrecompileBanner
{
    /// <summary>The banner's text, null when it is hidden (no boot step running, or it ended or was cancelled).</summary>
    public string? Text { get; private set; }

    /// <summary>The bar's fraction, 0..1.</summary>
    public double Fraction { get; private set; }

    public bool Visible => Text != null;

    /// <summary>The "Cancelar" button's text.</summary>
    public static string CancelText => Loc.Get("PrecompileCodeCancel");

    /// <summary>A <see cref="CodePrecompileLog.Reported"/> progress: shown, or hidden when it is the end.</summary>
    public void Show(CodePrecompileProgress progress)
    {
        if (progress.Finished)
        {
            Hide();
            return;
        }

        Text = Loc.Format("PrecompileCodeStatus", progress.Done, progress.Total);
        Fraction = progress.Total > 0 ? Math.Clamp((double)progress.Done / progress.Total, 0, 1) : 0;
    }

    /// <summary>"Cancelar": the game starts at once (what is left goes on in the background) and the banner goes.</summary>
    public void Cancel()
    {
        CodePrecompileLog.Cancel();
        Hide();
    }

    public void Hide()
    {
        Text = null;
        Fraction = 0;
    }
}
