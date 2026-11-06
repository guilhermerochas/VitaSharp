using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Views;
using Android.Widget;
using VitaSharp.Android.Services;
using VitaSharp.Core.Util;
using AView = Android.Views.View;
using Color = Android.Graphics.Color;
using Paint = Android.Graphics.Paint;
using Path = Android.Graphics.Path;
using ScrollView = Android.Widget.ScrollView;

namespace VitaSharp.Android.Game;

/// <summary>
/// The pause menu (docs/android/mockups/pausa.html, laid out for landscape): a card over the paused game in the app's
/// palette, fonts, corners and icons (Ui/Theme.cs), with continue, save/load state (slot pages with the pictures),
/// recentre, the "Gravar tudo" capture, the in-game settings, the developer recordings and leave. Touch and physical
/// controllers both work: every button takes the focus (D-pad / left stick move it, A or Enter press, B or Back go back,
/// Start or PS close), and the focused one gets the accent outline.
/// </summary>
public sealed partial class GameActivity
{
    // Ui/Theme.cs Palette (the MAUI pages), as Android colours
    static readonly Color CBackdrop = Color.ParseColor("#A6060810"), CPanel = Color.ParseColor("#12161C"), CPanelBorder = Color.ParseColor("#262E3A"),
        CSurface = Color.ParseColor("#171B22"), CBorder = Color.ParseColor("#232A35"), CBorderStrong = Color.ParseColor("#2A313D"),
        CText = Color.ParseColor("#E8ECF2"), CMuted = Color.ParseColor("#9AA4B2"), CFaint = Color.ParseColor("#6B7584"),
        CAccent = Color.ParseColor("#6E9BFF"), CAccentSoft = Color.ParseColor("#9DB4E8"), COnAccent = Color.ParseColor("#0B1220"),
        CDanger = Color.ParseColor("#F0A8A8"), CDangerBorder = Color.ParseColor("#3A2A2E"), CCoverText = Color.ParseColor("#DCE5F7"),
        CRec = Color.ParseColor("#FF6B6B");

    // the icons of pausa.html (24x24 SVG paths, stroked)
    const string IconSave = "M5 4h11l3 3v13H5zM8 4v5h7V4M8 20v-6h8v6", IconLoad = "M4 12a8 8 0 1 0 2.3-5.7M4 4v4h4",
        IconSettings = "M4 7h10M18 7h2M4 17h4M12 17h8M16 5v4M10 15v4", IconRecenter = "M12 3v4M12 17v4M3 12h4M17 12h4M12 9a3 3 0 1 0 0 6 3 3 0 0 0 0-6",
        IconRecord = "M12 5a7 7 0 1 0 0 14 7 7 0 0 0 0-14", IconStop = "M7 7h10v10H7z", IconDev = "M8 8l-4 4 4 4M16 8l4 4-4 4M13.5 6l-3 12",
        IconPlay = "M8 5.5v13l11-6.5z", IconBack = "M15 5l-7 7 7 7";

    FrameLayout? _root, _menu;
    LinearLayout? _menuCard;
    Action? _menuBack;
    readonly System.Diagnostics.Stopwatch _sessionClock = System.Diagnostics.Stopwatch.StartNew();
    Typeface? _inter, _interMedium, _interSemiBold, _sora;

    bool MenuVisible => _menu != null;

    Typeface Font(ref Typeface? cache, string asset)
    {
        if (cache != null) return cache;
        try { cache = Typeface.CreateFromAsset(Assets, asset); }
        catch (Exception e) { Log.Warn($"Pause menu: font {asset}: {e.Message}"); cache = Typeface.Default; }
        return cache!;
    }

    Typeface Inter => Font(ref _inter, "Inter-Regular.ttf");
    Typeface InterMedium => Font(ref _interMedium, "Inter-Medium.ttf");
    Typeface InterSemiBold => Font(ref _interSemiBold, "Inter-SemiBold.ttf");
    Typeface Sora => Font(ref _sora, "Sora-SemiBold.ttf");

    void OnPsPressed() => RunOnUiThread(() => { if (MenuVisible) CloseMenu(); else OpenMenu(); });

    /// <summary>Opens the pause menu (Back, the PS button, the pause chip, the keyboard's pause key); Back while it is
    /// open goes one page back.</summary>
    void OpenMenu()
    {
        if (MenuVisible) { _menuBack?.Invoke(); return; }
        if (_menuOpen || _root == null) return; // a fatal dialog
        _menuOpen = true;
        _session?.CaptureMenuThumbnail(); // the picture of a state saved from this menu (taken before the pause)
        _session?.Pause();
        _pad.Reset();
        _keyboard?.ReleaseAll();
        _menu = new FrameLayout(this) { Clickable = true }; // eats the touches meant for the game below
        _menu.SetBackgroundColor(CBackdrop);
        _menu.Click += (_, _) => CloseMenu(); // a tap outside the card continues the game
        _menuCard = new LinearLayout(this) { Orientation = Orientation.Vertical, Clickable = true };
        _menuCard.SetPadding(Dp(20), Dp(16), Dp(20), Dp(18));
        _menuCard.Background = Rounded(CPanel, 28, CPanelBorder);
        var scroll = new ScrollView(this) { FillViewport = false };
        scroll.AddView(_menuCard);
        int width = Math.Min(Dp(660), (int)((Resources?.DisplayMetrics?.WidthPixels ?? Dp(700)) * 0.92));
        _menu.AddView(scroll, new FrameLayout.LayoutParams(width, ViewGroup.LayoutParams.WrapContent, GravityFlags.Center));
        _root.AddView(_menu, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
        ShowMainPage();
    }

    void CloseMenu() => CloseMenu(resume: true);

    void CloseMenu(bool resume)
    {
        if (_menu == null) return;
        _root?.RemoveView(_menu);
        _menu = null;
        _menuCard = null;
        _menuBack = null;
        _menuOpen = false;
        _controls?.Invalidate();
        if (resume && !IsFinishing) _session?.Resume();
        EnterImmersive();
    }

    // ------------------------------------------------------------------ pages

    void ShowMainPage()
    {
        if (_menuCard is not { } card) return;
        card.RemoveAllViews();
        _menuBack = CloseMenu;
        var session = _session;

        // header: the cover (icon0.png or the initials tile), title and session line
        var header = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        header.SetGravity(GravityFlags.CenterVertical);
        header.AddView(Cover(), new LinearLayout.LayoutParams(Dp(48), Dp(48)));
        var titles = new LinearLayout(this) { Orientation = Orientation.Vertical };
        titles.AddView(Text(session?.Game.Title ?? "VitaSharp", 16, Sora, CText, single: true));
        long minutes = (long)_sessionClock.Elapsed.TotalMinutes;
        string rec = session?.Capture.IsRecording == true ? " · gravando" : "";
        titles.AddView(Text($"Pausado · sessão de {(minutes < 1 ? "menos de 1 min" : $"{minutes} min")}{rec}", 12, Inter, CMuted, single: true));
        header.AddView(titles, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1) { LeftMargin = Dp(12) });
        card.AddView(header);

        // two columns: continue / leave on the left, the actions grid on the right
        var columns = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        var left = new LinearLayout(this) { Orientation = Orientation.Vertical };
        var resume = PrimaryButton("Continuar", IconPlay, CloseMenu);
        left.AddView(resume, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, Dp(84)));
        left.AddView(DangerButton("Sair do jogo", () => { CloseMenu(resume: false); Leave(); }), new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, Dp(50)) { TopMargin = Dp(10) });
        columns.AddView(left, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1));

        var tiles = new List<AView>
        {
            Tile("Salvar estado", IconSave, () => ShowSlotsPage(save: true)),
            Tile("Carregar estado", IconLoad, () => ShowSlotsPage(save: false)),
            Tile("Ajustes", IconSettings, ShowSettingsPage),
        };
        if (_sensors?.CanRecenter == true)
            tiles.Add(Tile("Recentralizar", IconRecenter, () => { _sensors?.Recenter(); Toast.MakeText(this, "Movimento recentralizado", ToastLength.Short)?.Show(); }));
        // "Gravar tudo" (every build): the session capture, docs/notes/session-capture.md
        if (session?.Capture.IsRecording == true)
            tiles.Add(Tile("Parar gravação", IconStop, () => { session.Capture.Stop("pause menu"); ShowMainPage(); }, CRec));
        else
            tiles.Add(Tile("Gravar tudo", IconRecord, () => { session?.Capture.Start("pause menu"); CloseMenu(); }, CRec));
#if VITASHARP_DEVELOPER
        tiles.Add(Tile("Desenvolvedor", IconDev, ShowDeveloperPage));
#endif
        columns.AddView(Grid(tiles, 3), new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 2) { LeftMargin = Dp(10) });
        card.AddView(columns, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { TopMargin = Dp(16) });
        resume.RequestFocus();
    }

    /// <summary>The save state slots: a picture and the date of each slot; saving or loading runs off the UI thread and
    /// ends with a toast, and the game resumes.</summary>
    void ShowSlotsPage(bool save)
    {
        if (_menuCard is not { } card || _session is not { } session) return;
        card.RemoveAllViews();
        _menuBack = ShowMainPage;
        card.AddView(PageHeader(save ? "Salvar estado" : "Carregar estado", save ? "Escolha o slot (o estado anterior do slot é substituído)" : "Escolha o estado salvo"));
        var slots = new List<AView>();
        AView? first = null;
        for (int slot = 1; slot <= EmulationSession.StateSlots; slot++)
        {
            int s = slot;
            var info = session.StateInfo(s);
            bool enabled = save || info != null;
            var v = SlotCard(s, info, enabled, () =>
            {
                CloseMenu(resume: false); // the game resumes once the save or load below is done
                Toast.MakeText(this, save ? "Salvando estado…" : "Carregando estado…", ToastLength.Short)?.Show();
                Task.Run(() =>
                {
                    string msg = save ? session.SaveState(s) : session.LoadState(s);
                    RunOnUiThread(() =>
                    {
                        Toast.MakeText(this, msg, ToastLength.Long)?.Show();
                        if (!IsFinishing && !MenuVisible) session.Resume();
                    });
                });
            });
            if (enabled && first == null) first = v;
            slots.Add(v);
        }
        card.AddView(Grid(slots, 3), new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { TopMargin = Dp(12) });
        (first ?? card.GetChildAt(0))?.RequestFocus();
    }

    /// <summary>The in-game settings: what can change while the game runs (the rest is in the game's page of the app).</summary>
    void ShowSettingsPage()
    {
        if (_menuCard is not { } card) return;
        card.RemoveAllViews();
        _menuBack = ShowMainPage;
        card.AddView(PageHeader("Ajustes do jogo", "CPU, GPU e resolução ficam na página do jogo no app"));
        var list = new LinearLayout(this) { Orientation = Orientation.Vertical };
        list.Background = Rounded(CSurface, 20, CBorder);
        AView? first = null;
        void Add(AView row)
        {
            if (list.ChildCount > 0) list.AddView(new AView(this) { Background = new ColorDrawable(CBorder) }, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 1) { LeftMargin = Dp(16), RightMargin = Dp(16) });
            list.AddView(row);
            first ??= row;
        }
        Add(ToggleRow("Toque traseiro", "Metade de baixo da tela vira o touchpad traseiro", () => _pad.RearTouch, v => _pad.RearTouch = v));
        Add(ToggleRow("Controles na tela", null, () => _pad.Visible, v => _pad.Visible = v));
        if (_session is { } session)
            Add(ToggleRow("Pré-gravação", "\"Gravar tudo\" inclui os últimos segundos antes de você apertar", () => session.Capture.Armed,
                v => session.Capture.SetPreRoll(v ? EmulationSession.PreRollSeconds : 0)));
        if (_sensors?.CanRecenter == true)
            Add(ActionRow("Recentralizar movimento", "A posição atual do celular vira a posição de repouso", () => { _sensors?.Recenter(); Toast.MakeText(this, "Movimento recentralizado", ToastLength.Short)?.Show(); }));
        card.AddView(list, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { TopMargin = Dp(12) });
        first?.RequestFocus();
    }

#if VITASHARP_DEVELOPER
    void ShowDeveloperPage()
    {
        if (_menuCard is not { } card || _session is not { } session) return;
        card.RemoveAllViews();
        _menuBack = ShowMainPage;
        card.AddView(PageHeader("Gravações de desenvolvedor", "Pasta de captura do build de desenvolvedor"));
        var list = new LinearLayout(this) { Orientation = Orientation.Vertical };
        list.Background = Rounded(CSurface, 20, CBorder);
        AView first;
        if (session.CaptureDir != null)
            list.AddView(first = ActionRow("Parar gravação (desenvolvedor)", session.CaptureDir, () => { session.StopCapture(); CloseMenu(); }));
        else
        {
            list.AddView(first = ActionRow("Gravar tempo", "Perfil de tempo (CPU, GPU, quadros)", () => { session.StartCapture("tempo"); CloseMenu(); }));
            list.AddView(new AView(this) { Background = new ColorDrawable(CBorder) }, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 1) { LeftMargin = Dp(16), RightMargin = Dp(16) });
            list.AddView(ActionRow("Gravar conteúdo", "Quadros, dump de GPU e áudio", () => { session.StartCapture("conteudo"); CloseMenu(); }));
        }
        card.AddView(list, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { TopMargin = Dp(12) });
        first.RequestFocus();
    }
#endif

    /// <summary>"Salvando a gravação" over the game while leaving waits for a "Gravar tudo" save: a card in the menu's
    /// style that eats every touch (the caller removes it).</summary>
    AView SavingNotice()
    {
        var overlay = new FrameLayout(this) { Clickable = true };
        overlay.SetBackgroundColor(CBackdrop);
        var card = new LinearLayout(this) { Orientation = Orientation.Vertical };
        card.SetPadding(Dp(24), Dp(18), Dp(24), Dp(20));
        card.Background = Rounded(CPanel, 28, CPanelBorder);
        var title = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        title.SetGravity(GravityFlags.CenterVertical);
        title.AddView(new IconView(this, IconRecord, CRec), new LinearLayout.LayoutParams(Dp(20), Dp(20)));
        title.AddView(Text("Salvando a gravação…", 16, Sora, CText), new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { LeftMargin = Dp(10) });
        card.AddView(title);
        card.AddView(Text("Não feche o app: o jogo fecha sozinho quando terminar", 13, Inter, CMuted), new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { TopMargin = Dp(6) });
        overlay.AddView(card, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent, GravityFlags.Center));
        _root?.AddView(overlay, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
        return overlay;
    }

    // ------------------------------------------------------------------ controller keys while the menu is open

    /// <summary>A key of a physical controller while the menu is open: A presses the focused button, B goes back, Start
    /// and PS close the menu; the D-pad moves the focus (Android's own focus search, so it returns false). Key-ups still
    /// reach the controller state, so a button held when the menu opened is not left pressed.</summary>
    bool MenuControllerKey(KeyEvent e)
    {
        if (e.Action == KeyEventActions.Up) { AppServices.Gamepads.OnKey(e.DeviceId, (int)e.KeyCode, false); return e.KeyCode is not (Keycode.DpadUp or Keycode.DpadDown or Keycode.DpadLeft or Keycode.DpadRight or Keycode.DpadCenter); }
        if (e.RepeatCount > 0 && e.KeyCode is not (Keycode.DpadUp or Keycode.DpadDown or Keycode.DpadLeft or Keycode.DpadRight)) return true;
        switch (e.KeyCode)
        {
            case Keycode.ButtonA: case Keycode.DpadCenter: CurrentFocus?.PerformClick(); return true;
            case Keycode.ButtonB: _menuBack?.Invoke(); return true;
            case Keycode.ButtonStart: case Keycode.ButtonMode: CloseMenu(); return true;
            case Keycode.DpadUp: case Keycode.DpadDown: case Keycode.DpadLeft: case Keycode.DpadRight: return false;
            default: return true; // nothing else reaches the paused game
        }
    }

    // ------------------------------------------------------------------ building blocks (Ui/Screen.cs, in Android views)

    GradientDrawable Rounded(Color fill, float radiusDp, Color? stroke = null, int strokeDp = 1)
    {
        var d = new GradientDrawable();
        d.SetColor(fill);
        d.SetCornerRadius(Dp(radiusDp));
        if (stroke is { } s) d.SetStroke(Math.Max(1, Dp(strokeDp)), s);
        return d;
    }

    /// <summary>The normal look, and the accent outline when the controller's focus is on it (or it is pressed).</summary>
    StateListDrawable Focusable(Color fill, float radiusDp, Color stroke)
    {
        var states = new StateListDrawable();
        states.AddState([global::Android.Resource.Attribute.StateFocused], Rounded(fill, radiusDp, CAccent, 2));
        states.AddState([global::Android.Resource.Attribute.StatePressed], Rounded(fill, radiusDp, CAccentSoft, 2));
        states.AddState([], Rounded(fill, radiusDp, stroke));
        return states;
    }

    TextView Text(string text, float sp, Typeface font, Color color, bool single = false)
    {
        var t = new TextView(this) { Text = text, TextSize = sp };
        t.SetTypeface(font, TypefaceStyle.Normal);
        t.SetTextColor(color);
        if (single) { t.SetSingleLine(true); t.Ellipsize = global::Android.Text.TextUtils.TruncateAt.End; }
        return t;
    }

    void MakeButton(AView v, Action onClick)
    {
        v.Focusable = true;
        v.Clickable = true;
        v.Click += (_, _) => onClick();
    }

    AView Cover()
    {
        var game = _session?.Game;
        if (game?.IconPath is { } icon && File.Exists(icon) && BitmapFactory.DecodeFile(icon) is { } bmp)
        {
            var image = new ImageView(this);
            var rounded = AndroidX.Core.Graphics.Drawable.RoundedBitmapDrawableFactory.Create(Resources!, bmp)!;
            rounded.CornerRadius = Dp(14);
            image.SetImageDrawable(rounded);
            image.SetScaleType(ImageView.ScaleType.CenterCrop!);
            return image;
        }
        var tile = Text(game?.Initials ?? "VS", 15, Font(ref _soraBold, "Sora-Bold.ttf"), CCoverText);
        tile.Gravity = GravityFlags.Center;
        tile.Background = Rounded(Color.ParseColor(Library.GameLibrary.CoverColor(game?.TitleId ?? "")), 14);
        return tile;
    }

    Typeface? _soraBold;

    AView PrimaryButton(string text, string icon, Action onClick)
    {
        var b = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        b.SetGravity(GravityFlags.Center);
        var states = new StateListDrawable();
        states.AddState([global::Android.Resource.Attribute.StateFocused], Rounded(CAccent, 18, CText, 2));
        states.AddState([], Rounded(CAccent, 18));
        b.Background = states;
        b.AddView(new IconView(this, icon, COnAccent, filled: true), new LinearLayout.LayoutParams(Dp(20), Dp(20)) { RightMargin = Dp(8) });
        b.AddView(Text(text, 16, InterSemiBold, COnAccent));
        MakeButton(b, onClick);
        return b;
    }

    AView DangerButton(string text, Action onClick)
    {
        var t = Text(text, 14, InterMedium, CDanger);
        t.Gravity = GravityFlags.Center;
        t.Background = Focusable(Color.Transparent, 16, CDangerBorder);
        MakeButton(t, onClick);
        return t;
    }

    AView Tile(string text, string icon, Action onClick, Color? iconColor = null)
    {
        var b = new LinearLayout(this) { Orientation = Orientation.Vertical };
        b.SetGravity(GravityFlags.Center);
        b.Background = Focusable(CSurface, 18, CBorder);
        b.AddView(new IconView(this, icon, iconColor ?? CAccentSoft), new LinearLayout.LayoutParams(Dp(22), Dp(22)) { Gravity = GravityFlags.CenterHorizontal });
        var label = Text(text, 12, InterMedium, CText, single: true);
        label.Gravity = GravityFlags.Center;
        b.AddView(label, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { TopMargin = Dp(8), Gravity = GravityFlags.CenterHorizontal });
        b.SetMinimumHeight(Dp(84));
        MakeButton(b, onClick);
        return b;
    }

    /// <summary>Rows of <paramref name="columns"/> equal cells, 10 dp apart (the CSS grid of the mockup).</summary>
    LinearLayout Grid(IReadOnlyList<AView> cells, int columns)
    {
        var grid = new LinearLayout(this) { Orientation = Orientation.Vertical };
        for (int i = 0; i < cells.Count; i += columns)
        {
            var row = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            for (int c = 0; c < columns; c++)
            {
                var lp = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1) { LeftMargin = c > 0 ? Dp(10) : 0 };
                row.AddView(i + c < cells.Count ? cells[i + c] : new AView(this), lp);
            }
            grid.AddView(row, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { TopMargin = i > 0 ? Dp(10) : 0 });
        }
        return grid;
    }

    AView PageHeader(string title, string? detail)
    {
        var header = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        header.SetGravity(GravityFlags.CenterVertical);
        var back = new FrameLayout(this);
        back.Background = Focusable(CSurface, 14, CBorderStrong);
        back.AddView(new IconView(this, IconBack, CText), new FrameLayout.LayoutParams(Dp(20), Dp(20), GravityFlags.Center));
        MakeButton(back, () => _menuBack?.Invoke());
        header.AddView(back, new LinearLayout.LayoutParams(Dp(44), Dp(44)));
        var titles = new LinearLayout(this) { Orientation = Orientation.Vertical };
        titles.AddView(Text(title, 18, Sora, CText, single: true));
        if (detail != null) titles.AddView(Text(detail, 12, Inter, CMuted, single: true));
        header.AddView(titles, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1) { LeftMargin = Dp(14) });
        return header;
    }

    AView SlotCard(int slot, Core.SaveState.SaveStateHeader? info, bool enabled, Action onClick)
    {
        var b = new LinearLayout(this) { Orientation = Orientation.Vertical };
        b.SetPadding(Dp(8), Dp(8), Dp(8), Dp(10));
        b.Background = Focusable(CSurface, 18, CBorder);
        var image = new ImageView(this);
        image.SetScaleType(ImageView.ScaleType.CenterCrop!);
        // the corners are the view's outline: a RoundedBitmapDrawable rounds in the small bitmap's pixels (a pill shape)
        image.Background = Rounded(Color.ParseColor("#0E1116"), 12, CBorder);
        image.OutlineProvider = ViewOutlineProvider.Background;
        image.ClipToOutline = true;
        if (info?.Thumbnail is { Length: > 0 } png && BitmapFactory.DecodeByteArray(png, 0, png.Length) is { } bmp)
            image.SetImageBitmap(bmp);
        // a wide picture over the card width, low enough for both rows of slots on a landscape phone (~380 dp tall)
        image.LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, Dp(60));
        b.AddView(image);
        b.AddView(Text($"Slot {slot}", 13, InterSemiBold, enabled ? CText : CFaint), new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { TopMargin = Dp(8) });
        b.AddView(Text(info != null ? info.CreatedUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm") : "vazio", 12, Inter, CMuted, single: true));
        if (enabled) MakeButton(b, onClick);
        else { b.Focusable = false; b.Alpha = 0.6f; }
        return b;
    }

    AView ToggleRow(string label, string? detail, Func<bool> get, Action<bool> set)
    {
        var row = Row(label, detail);
        // drawn here: the activity's platform theme (Theme.Black) gives android.widget.Switch the old "LIG/DESL" text look
        var sw = new ToggleView(this, CAccent, CBorderStrong, CText) { Checked = get() };
        row.AddView(sw, new LinearLayout.LayoutParams(Dp(44), Dp(26)) { Gravity = GravityFlags.CenterVertical, LeftMargin = Dp(12) });
        MakeButton(row, () => { set(!get()); sw.Checked = get(); _controls?.Invalidate(); });
        return row;
    }

    AView ActionRow(string label, string? detail, Action onClick)
    {
        var row = Row(label, detail);
        row.AddView(Text("›", 20, Inter, CFaint), new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { Gravity = GravityFlags.CenterVertical });
        MakeButton(row, onClick);
        return row;
    }

    LinearLayout Row(string label, string? detail)
    {
        var row = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        row.SetGravity(GravityFlags.CenterVertical);
        row.SetPadding(Dp(16), Dp(10), Dp(16), Dp(10));
        row.SetMinimumHeight(Dp(56));
        row.Background = Focusable(Color.Transparent, 20, Color.Transparent);
        var texts = new LinearLayout(this) { Orientation = Orientation.Vertical };
        texts.AddView(Text(label, 15, InterMedium, CText));
        if (detail != null) texts.AddView(Text(detail, 12, Inter, CMuted, single: true));
        row.AddView(texts, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1));
        return row;
    }

    /// <summary>The app's switch (Ui/Theme.cs): a rounded track, accent when on, with the thumb at its end.</summary>
    sealed class ToggleView(global::Android.Content.Context context, Color on, Color off, Color thumb) : AView(context)
    {
        readonly Paint _paint = new(PaintFlags.AntiAlias);
        bool _checked;

        public bool Checked { get => _checked; set { _checked = value; Invalidate(); } }

        protected override void OnDraw(Canvas canvas)
        {
            base.OnDraw(canvas);
            float h = Height, r = h / 2;
            _paint.Color = _checked ? on : off;
            canvas.DrawRoundRect(0, 0, Width, h, r, r, _paint);
            _paint.Color = thumb;
            canvas.DrawCircle(_checked ? Width - r : r, r, r - h * 0.14f, _paint);
        }
    }

    /// <summary>A 24x24 SVG path icon (the mockups' Lucide-style strokes), stroked 1.9 units wide or filled.</summary>
    sealed class IconView : AView
    {
        readonly Path _path;
        readonly Paint _paint;

        public IconView(global::Android.Content.Context context, string svg, Color color, bool filled = false) : base(context)
        {
            _path = AndroidX.Core.Graphics.PathParser.CreatePathFromPathData(svg) ?? new Path();
            _paint = new Paint(PaintFlags.AntiAlias) { Color = color, StrokeWidth = 1.9f, StrokeCap = Paint.Cap.Round!, StrokeJoin = Paint.Join.Round! };
            _paint.SetStyle(filled ? Paint.Style.Fill! : Paint.Style.Stroke!);
            Focusable = false;
        }

        protected override void OnDraw(Canvas canvas)
        {
            base.OnDraw(canvas);
            float s = Math.Min(Width, Height) / 24f;
            canvas.Save();
            canvas.Translate((Width - 24 * s) / 2, (Height - 24 * s) / 2);
            canvas.Scale(s, s);
            canvas.DrawPath(_path, _paint);
            canvas.Restore();
        }
    }
}
