using Android.Graphics;
using Android.Views;
using Android.Widget;
using VitaSharp.Android.Services;
using VitaSharp.Core.Boot;
using VitaSharp.Core.Cpu;
using VitaSharp.Core.Gpu;
using VitaSharp.Mobile.Core.Library;
using Button = Android.Widget.Button;
using Color = Android.Graphics.Color;
using ProgressBar = Android.Widget.ProgressBar;

namespace VitaSharp.Android.Game;

/// <summary>
/// The loading screen over the game from the tap on (the desktop launcher's loading window, the same
/// <see cref="LoadingScreen"/> model): the game's start-up picture (sce_sys/pic0.png, what the Vita shows while a
/// process loads), its icon and name, the current step with its bar and, during "Carregando o cache de shaders" and
/// "Pre-compilando codigo ARM", "Cancelar" (the game starts right away; the rest goes on in the background). It goes
/// when the game is ready (<see cref="BootStep.Ready"/>) or its start failed.
/// </summary>
public sealed partial class GameActivity
{
    LoadingScreen? _loadingScreen;
    FrameLayout? _loadingView;
    TextView? _loadingStep;
    ProgressBar? _loadingBar;
    Button? _loadingCancel;

    /// <summary>Builds the screen (shown at once) and follows the boot steps and the two waits; called from OnCreate.</summary>
    void AddLoadingScreen(FrameLayout root, GameEntry game)
    {
        string picture = VitaSharp.Core.Io.Vfs.ResolveCaseInsensitive(game.AppDir, "sce_sys/pic0.png");
        // the resolution the game starts with: its own choice, else the global one (Ajustes)
        var effective = VitaSharp.Mobile.Core.Settings.EffectiveSettings.Resolve(AppServices.Prefs, AppServices.GameSettingsFor(game.TitleId));
        string detail = LoadingScreen.ResolutionDetail(effective.ResScaleValue);
        var screen = _loadingScreen = new LoadingScreen(game.Title, game.IconPath, File.Exists(picture) ? picture : null, detail);
        var view = _loadingView = new FrameLayout(this) { Clickable = true };
        view.SetBackgroundColor(Color.Black);
        if (screen.BackgroundPath is { } path && BitmapFactory.DecodeFile(path) is { } bitmap)
        {
            var background = new ImageView(this);
            background.SetImageBitmap(bitmap);
            background.SetScaleType(ImageView.ScaleType.CenterCrop!);
            background.Alpha = 0.38f;
            view.AddView(background, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
        }

        var card = new LinearLayout(this) { Orientation = Orientation.Vertical };
        card.SetGravity(GravityFlags.CenterHorizontal);
        card.SetPadding(Dp(28), Dp(22), Dp(28), Dp(14));
        var cardBackground = new global::Android.Graphics.Drawables.GradientDrawable();
        cardBackground.SetColor(Color.ParseColor("#E6171B22"));
        cardBackground.SetCornerRadius(Dp(22));
        card.Background = cardBackground;

        if (screen.IconPath is { } icon && File.Exists(icon) && BitmapFactory.DecodeFile(icon) is { } iconBitmap)
        {
            var image = new ImageView(this);
            var rounded = AndroidX.Core.Graphics.Drawable.RoundedBitmapDrawableFactory.Create(Resources!, iconBitmap)!;
            rounded.CornerRadius = Dp(18);
            image.SetImageDrawable(rounded);
            image.SetScaleType(ImageView.ScaleType.CenterCrop!);
            card.AddView(image, new LinearLayout.LayoutParams(Dp(84), Dp(84)) { Gravity = GravityFlags.CenterHorizontal, BottomMargin = Dp(12) });
        }

        var title = new TextView(this) { Text = screen.Title, TextSize = 18, Gravity = GravityFlags.Center };
        title.SetTextColor(Color.ParseColor("#E8ECF2"));
        title.SetMaxLines(2);
        card.AddView(title, new LinearLayout.LayoutParams(Dp(300), ViewGroup.LayoutParams.WrapContent));

        var detailView = new TextView(this) { Text = screen.Detail, TextSize = 12, Gravity = GravityFlags.Center };
        detailView.SetTextColor(Color.ParseColor("#9AA4B2"));
        card.AddView(detailView, new LinearLayout.LayoutParams(Dp(300), ViewGroup.LayoutParams.WrapContent) { TopMargin = Dp(4) });

        _loadingStep = new TextView(this) { TextSize = 14, Gravity = GravityFlags.Center };
        _loadingStep.SetTextColor(Color.ParseColor("#C9D1DC"));
        card.AddView(_loadingStep, new LinearLayout.LayoutParams(Dp(300), ViewGroup.LayoutParams.WrapContent) { TopMargin = Dp(10) });

        _loadingBar = new ProgressBar(this, null, global::Android.Resource.Attribute.ProgressBarStyleHorizontal) { Max = 1000 };
        card.AddView(_loadingBar, new LinearLayout.LayoutParams(Dp(280), ViewGroup.LayoutParams.WrapContent) { TopMargin = Dp(8) });

        _loadingCancel = new Button(this) { Text = LoadingScreen.CancelText };
        _loadingCancel.Click += (_, _) => CancelLoadingWait();
        card.AddView(_loadingCancel, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { Gravity = GravityFlags.CenterHorizontal });

        view.AddView(card, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent, GravityFlags.Center));
        root.AddView(view, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));

        screen.Changed += UpdateLoadingScreen;
        BootProgress.Reported += OnBootStepReported;
        ShaderCacheLoad.Reported += OnShaderCacheLoadReported;
        CodePrecompileLog.Reported += OnCodePrecompileReported;
        UpdateLoadingScreen();
    }

    void RemoveLoadingScreen()
    {
        BootProgress.Reported -= OnBootStepReported;
        ShaderCacheLoad.Reported -= OnShaderCacheLoadReported;
        CodePrecompileLog.Reported -= OnCodePrecompileReported;
    }

    /// <summary>The start failed: the screen keeps the game's picture and shows the reason with the log's place (the
    /// error dialog over it says the same and goes back to the screens), as the desktop's loading window does.</summary>
    void FailLoadingScreen(string message) => _loadingScreen?.Fail(message, System.IO.Path.Combine(AppServices.Data.Logs, "last.log"));

    // the events come on the thread that runs the step: the screen changes on the UI thread
    void OnBootStepReported(BootStep step) => RunOnUiThread(() => _loadingScreen?.OnStep(step));

    void OnShaderCacheLoadReported(ShaderCacheLoadProgress progress) => RunOnUiThread(() => _loadingScreen?.OnShaderCache(progress));

    void OnCodePrecompileReported(CodePrecompileProgress progress) => RunOnUiThread(() => _loadingScreen?.OnCodePrecompile(progress));

    /// <summary>"Cancelar": the wait that runs stops; the game starts right away.</summary>
    void CancelLoadingWait()
    {
        switch (_loadingScreen?.Cancel())
        {
            case BootStep.ShaderCache:
                ShaderCacheLoad.Cancel();
                break;
            case BootStep.CodePrecompile:
                CodePrecompileLog.Cancel();
                break;
            default:
                break;
        }
    }

    void UpdateLoadingScreen()
    {
        if (_loadingScreen is not { } screen || _loadingView == null || _loadingStep == null || _loadingBar == null || _loadingCancel == null)
        {
            return;
        }

        _loadingView.Visibility = screen.IsDone ? ViewStates.Gone : ViewStates.Visible;
        _loadingStep.Text = screen.FailureLogText is { } log ? screen.StepText + "\n" + log : screen.StepText;
        _loadingBar.Visibility = screen.IsFailed ? ViewStates.Gone : ViewStates.Visible;
        _loadingBar.Indeterminate = screen.Fraction == null;
        _loadingBar.Progress = (int)((screen.Fraction ?? 0) * 1000);
        _loadingCancel.Visibility = screen.CanCancel ? ViewStates.Visible : ViewStates.Gone;
    }
}
