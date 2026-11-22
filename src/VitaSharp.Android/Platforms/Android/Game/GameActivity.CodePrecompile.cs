using Android.Views;
using Android.Widget;
using VitaSharp.Core.Cpu;
using VitaSharp.Mobile.Core.Cpu;
using ProgressBar = Android.Widget.ProgressBar;

namespace VitaSharp.Android.Game;

/// <summary>"Precompilar codigo ARM" (Ajustes and the game's own settings): while the boot step translates the game's
/// ARM code before it starts, a card over the game shows the progress and "Cancelar" (the game starts right away; the
/// rest goes on in the background), as the desktop launcher's status bar does.</summary>
public sealed partial class GameActivity
{
    readonly CodePrecompileBanner _codePrecompile = new();
    LinearLayout? _codePrecompileCard;
    TextView? _codePrecompileText;
    ProgressBar? _codePrecompileBar;

    /// <summary>Builds the (hidden) card and follows <see cref="CodePrecompileLog.Reported"/>; called from OnCreate.</summary>
    void AddCodePrecompileBanner(FrameLayout root)
    {
        _codePrecompileCard = AddProgressCard(root, CodePrecompileBanner.CancelText, () =>
        {
            _codePrecompile.Cancel();
            UpdateCodePrecompileBanner();
        }, out _codePrecompileText, out _codePrecompileBar);
        CodePrecompileLog.Reported += OnCodePrecompileReported;
    }

    void RemoveCodePrecompileBanner()
    {
        CodePrecompileLog.Reported -= OnCodePrecompileReported;
    }

    /// <summary>On the thread that starts the game: shown on the UI thread.</summary>
    void OnCodePrecompileReported(CodePrecompileProgress progress)
    {
        RunOnUiThread(() =>
        {
            _codePrecompile.Show(progress);
            UpdateCodePrecompileBanner();
        });
    }

    void UpdateCodePrecompileBanner()
    {
        if (_codePrecompileCard == null || _codePrecompileText == null || _codePrecompileBar == null)
        {
            return;
        }

        _codePrecompileCard.Visibility = _codePrecompile.Visible ? ViewStates.Visible : ViewStates.Gone;
        _codePrecompileText.Text = _codePrecompile.Text ?? "";
        _codePrecompileBar.Progress = (int)(_codePrecompile.Fraction * 1000);
    }
}
