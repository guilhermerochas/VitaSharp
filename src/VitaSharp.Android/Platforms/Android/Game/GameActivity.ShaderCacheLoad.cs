using Android.Graphics;
using Android.Views;
using Android.Widget;
using VitaSharp.Core.Gpu;
using VitaSharp.Mobile.Core.Gpu;
using Button = Android.Widget.Button;
using Color = Android.Graphics.Color;
using ProgressBar = Android.Widget.ProgressBar;

namespace VitaSharp.Android.Game;

/// <summary>"Carregar o cache de shaders antes de jogar" (Ajustes and the game's own settings): while the game's recorded
/// shaders and pipelines are built before it starts, a card over the game shows the progress and "Cancelar" (the game
/// starts right away; the rest keeps building in the background), as the desktop launcher's status bar does.</summary>
public sealed partial class GameActivity
{
    readonly ShaderCacheLoadBanner _shaderCacheLoad = new();
    LinearLayout? _shaderCacheLoadCard;
    TextView? _shaderCacheLoadText;
    ProgressBar? _shaderCacheLoadBar;

    /// <summary>Builds the (hidden) card and follows <see cref="ShaderCacheLoad.Reported"/>; called from OnCreate.</summary>
    void AddShaderCacheLoadBanner(FrameLayout root)
    {
        _shaderCacheLoadCard = AddProgressCard(root, ShaderCacheLoadBanner.CancelText, () =>
        {
            _shaderCacheLoad.Cancel();
            UpdateShaderCacheLoadBanner();
        }, out _shaderCacheLoadText, out _shaderCacheLoadBar);
        ShaderCacheLoad.Reported += OnShaderCacheLoadReported;
    }

    /// <summary>A hidden card centred over the game with a text, a bar (0..1000) and a "Cancelar" button: the shader cache
    /// load's and the ARM code precompilation's (GameActivity.CodePrecompile.cs).</summary>
    LinearLayout AddProgressCard(FrameLayout root, string cancelText, Action cancelled, out TextView text, out ProgressBar bar)
    {
        var card = new LinearLayout(this) { Orientation = Orientation.Vertical, Visibility = ViewStates.Gone };
        card.SetPadding(Dp(18), Dp(14), Dp(18), Dp(10));
        var background = new global::Android.Graphics.Drawables.GradientDrawable();
        background.SetColor(Color.ParseColor("#E6171B22"));
        background.SetCornerRadius(Dp(18));
        card.Background = background;

        text = new TextView(this) { TextSize = 14 };
        text.SetTextColor(Color.ParseColor("#E8ECF2"));
        card.AddView(text);

        bar = new ProgressBar(this, null, global::Android.Resource.Attribute.ProgressBarStyleHorizontal) { Max = 1000 };
        card.AddView(bar, new LinearLayout.LayoutParams(Dp(260), ViewGroup.LayoutParams.WrapContent) { TopMargin = Dp(8) });

        var cancel = new Button(this) { Text = cancelText };
        cancel.Click += (_, _) => cancelled();
        card.AddView(cancel, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { Gravity = GravityFlags.End });

        root.AddView(card, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent, GravityFlags.Center));
        return card;
    }

    void RemoveShaderCacheLoadBanner()
    {
        ShaderCacheLoad.Reported -= OnShaderCacheLoadReported;
    }

    /// <summary>On the thread that starts the game: shown on the UI thread.</summary>
    void OnShaderCacheLoadReported(ShaderCacheLoadProgress progress)
    {
        RunOnUiThread(() =>
        {
            _shaderCacheLoad.Show(progress);
            UpdateShaderCacheLoadBanner();
        });
    }

    void UpdateShaderCacheLoadBanner()
    {
        if (_shaderCacheLoadCard == null || _shaderCacheLoadText == null || _shaderCacheLoadBar == null)
        {
            return;
        }

        _shaderCacheLoadCard.Visibility = _shaderCacheLoad.Visible ? ViewStates.Visible : ViewStates.Gone;
        _shaderCacheLoadText.Text = _shaderCacheLoad.Text ?? "";
        _shaderCacheLoadBar.Progress = (int)(_shaderCacheLoad.Fraction * 1000);
    }
}
