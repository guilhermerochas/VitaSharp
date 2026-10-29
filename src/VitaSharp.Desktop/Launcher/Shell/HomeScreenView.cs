using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using VitaSharp.Core.Shell;

namespace VitaSharp.Desktop.Launcher.Shell;

/// <summary>
/// The Vita home screen: pages of up to ten round bubbles (rows of 3, 4 and 3, like the console) over the theme
/// wallpaper, the status bar, and the page dots at the right. Pages scroll vertically: mouse wheel, dragging,
/// Page Up/Down, L/R; arrows / D-pad move the selection; Enter / Cross (or a click) opens the app's LiveArea.
/// </summary>
public sealed class HomeScreenView : ShellCanvas
{
    public const int PerPage = 10;
    const double Radius = 62;
    static readonly int[] RowStart = [0, 3, 7], RowCount = [3, 4, 3];
    static readonly double[] RowY = [138, 288, 438];

    IReadOnlyList<ShellApp> _apps = [];
    int _selected;
    double _scroll;     // current page position (fractional while animating)
    int _page;
    Point? _dragStart;
    double _dragScroll;
    bool _dragged;
    int _hover = -1;

    /// <summary>The selection ring is shown once the keyboard or a controller moved it (the mouse hides it).</summary>
    public bool NavigationMode { get; set; }

    /// <summary>A bubble was activated (click, Enter, Cross).</summary>
    public event Action<ShellApp>? Opened;
    /// <summary>The selection moved (sound effects).</summary>
    public event Action? SelectionMoved;

    public HomeScreenView(ShellVisuals visuals) : base(visuals) { }

    public IReadOnlyList<ShellApp> Apps
    {
        get => _apps;
        set
        {
            string? keep = _selected < _apps.Count ? _apps[_selected].TitleId : null;
            _apps = value;
            int idx = keep == null ? -1 : _apps.ToList().FindIndex(a => a.TitleId == keep);
            _selected = Math.Clamp(idx >= 0 ? idx : _selected, 0, Math.Max(0, _apps.Count - 1));
            GoToPage(_selected / PerPage, animate: false);
        }
    }

    public int Pages => Math.Max(1, (_apps.Count + PerPage - 1) / PerPage);
    public int Page => _page;
    public ShellApp? Selected => _selected < _apps.Count ? _apps[_selected] : null;

    public void Select(string titleId)
    {
        int i = _apps.ToList().FindIndex(a => a.TitleId == titleId);
        if (i < 0) return;
        _selected = i;
        GoToPage(i / PerPage, animate: false);
    }

    public void GoToPage(int page, bool animate = true)
    {
        page = Math.Clamp(page, 0, Pages - 1);
        _page = page;
        if (!animate) { _scroll = page; InvalidateVisual(); return; }
        Animate(() =>
        {
            double d = _page - _scroll;
            if (Math.Abs(d) < 0.002) { _scroll = _page; return false; }
            _scroll += d * 0.22;
            return true;
        });
    }

    /// <summary>Centre of slot <paramref name="slot"/> (0..9) on a page.</summary>
    static Point SlotCenter(int slot)
    {
        int row = slot < 3 ? 0 : slot < 7 ? 1 : 2;
        int col = slot - RowStart[row];
        // rows of 4 span 190..730; rows of 3 sit in their gaps
        double x = RowCount[row] == 4 ? 190 + col * 180 : 280 + col * 180;
        return new Point(x, RowY[row]);
    }

    static int RowOf(int slot) => slot < 3 ? 0 : slot < 7 ? 1 : 2;

    protected override void RenderScreen(DrawingContext ctx)
    {
        IBrush fontBrush = Visuals.Theme?.FontColor is { } fc ? new SolidColorBrush(ShellVisuals.ToColor(fc)) : Brushes.White;
        int first = Math.Max(0, (int)Math.Floor(_scroll)), last = Math.Min(Pages - 1, (int)Math.Ceiling(_scroll));
        for (int p = first; p <= last; p++)
        {
            double dy = (p - _scroll) * H;
            for (int slot = 0; slot < PerPage; slot++)
            {
                int i = p * PerPage + slot;
                if (i >= _apps.Count) break;
                var c = SlotCenter(slot) + new Point(0, dy);
                DrawBubble(ctx, _apps[i], c, (NavigationMode && i == _selected) || i == _hover, i == _selected, fontBrush);
            }
        }
        if (_apps.Count == 0)
        {
            var t = Text("Nenhum jogo instalado. Use \"Add game ▾\" para instalar um VPK, PKG ou pasta.", 18, Brushes.White, null, 700, TextAlignment.Center, 2);
            DrawShadowed(ctx, t, new Point((W - 700) / 2, H / 2 - 20));
        }
        DrawPageDots(ctx);
        DrawStatusBar(ctx, null);
    }

    void DrawBubble(DrawingContext ctx, ShellApp app, Point c, bool highlight, bool selected, IBrush fontBrush)
    {
        double r = highlight ? Radius * 1.06 : Radius;
        // drop shadow
        ctx.DrawEllipse(new SolidColorBrush(Color.FromArgb(70, 0, 0, 0)), null, c + new Point(0, 4), r + 2, r + 2);
        var rect = new Rect(c.X - r, c.Y - r, 2 * r, 2 * r);
        using (ctx.PushGeometryClip(new EllipseGeometry(rect)))
        {
            if (Visuals.File(app.IconPath) is { } icon) DrawImageFit(ctx, icon, rect, fill: true);
            else
            {
                ctx.FillRectangle(new SolidColorBrush(Color.Parse("#3A4A63")), rect);
                var q = Text(app.Title.Length > 0 ? app.Title[..1] : "?", 48, Brushes.White, UiBold);
                ctx.DrawText(q, new Point(c.X - q.Width / 2, c.Y - q.Height / 2));
            }
            // glossy top light, like the bubbles' lacquer
            ctx.DrawEllipse(new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0.5, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0.5, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Color.FromArgb(70, 255, 255, 255), 0), new GradientStop(Color.FromArgb(0, 255, 255, 255), 0.55) },
            }, null, c, r, r);
        }
        ctx.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb(150, 255, 255, 255)), 2), c, r, r);
        if (highlight)
            ctx.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb(230, 120, 200, 255)), 4), c, r + 4, r + 4);
        if (app.IsSystem)
        {
            // system apps run from vs0 once the shell can run them (stage 4): marked with a small badge
            ctx.DrawEllipse(new SolidColorBrush(Color.FromArgb(200, 20, 30, 45)), new Pen(Brushes.White, 1.2), c + new Point(r * 0.7, r * 0.7), 9, 9);
            var g = Text("⚙", 11, Brushes.White);
            ctx.DrawText(g, c + new Point(r * 0.7 - g.Width / 2, r * 0.7 - g.Height / 2));
        }
        var t = Text(app.Title, selected ? 13.5 : 12.5, fontBrush, selected ? UiBold : Ui, 172, TextAlignment.Center);
        DrawShadowed(ctx, t, new Point(c.X - 86, c.Y + Radius + 4));
    }

    void DrawPageDots(DrawingContext ctx)
    {
        int n = Pages;
        if (n <= 1) return;
        double spacing = 22, top = (H + StatusBarHeight) / 2 - (n - 1) * spacing / 2;
        var dot = Visuals.Texture(ShellAssets.TopMenuRco, ShellAssets.Textures.PageDot);
        for (int i = 0; i < n; i++)
        {
            var c = new Point(928, top + i * spacing);
            double a = i == _page ? 1 : 0.4;
            using (ctx.PushOpacity(a))
            {
                if (dot != null) ctx.DrawImage(dot, new Rect(c.X - 8, c.Y - 8, 16, 16));
                else ctx.DrawEllipse(Brushes.White, null, c, i == _page ? 6 : 4.5, i == _page ? 6 : 4.5);
            }
        }
    }

    // ------------------------------------------------------------------ input

    int HitTest(Point screen)
    {
        for (int slot = 0; slot < PerPage; slot++)
        {
            int i = _page * PerPage + slot;
            if (i >= _apps.Count) break;
            var c = SlotCenter(slot) + new Point(0, (_page - _scroll) * H);
            var d = screen - c;
            if (d.X * d.X + d.Y * d.Y <= Radius * Radius) return i;
        }
        return -1;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _dragStart = ToScreen(e.GetPosition(this));
        _dragScroll = _scroll;
        _dragged = false;
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = ToScreen(e.GetPosition(this));
        if (_dragStart is { } s)
        {
            double dy = p.Y - s.Y;
            if (Math.Abs(dy) > 8) _dragged = true;
            if (_dragged)
            {
                _scroll = Math.Clamp(_dragScroll - dy / H, -0.3, Pages - 0.7);
                InvalidateVisual();
            }
            return;
        }
        NavigationMode = false;
        int h = HitTest(p);
        if (h != _hover) { _hover = h; InvalidateVisual(); }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_hover >= 0) { _hover = -1; InvalidateVisual(); }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_dragStart is not { } s) return;
        _dragStart = null;
        e.Pointer.Capture(null);
        var p = ToScreen(e.GetPosition(this));
        if (_dragged)
        {
            // a swipe of a sixth of the screen turns the page
            double dy = p.Y - s.Y;
            int target = Math.Abs(dy) > H / 6 ? _page + (dy < 0 ? 1 : -1) : _page;
            ChangePage(target);
            return;
        }
        int hit = HitTest(p);
        if (hit >= 0)
        {
            _selected = hit;
            Opened?.Invoke(_apps[hit]);
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (Math.Abs(e.Delta.Y) < 0.01) return;
        ChangePage(_page + (e.Delta.Y < 0 ? 1 : -1));
        e.Handled = true;
    }

    void ChangePage(int target)
    {
        target = Math.Clamp(target, 0, Pages - 1);
        if (target != _page)
        {
            // keep the selection on the visible page
            int slot = _selected % PerPage;
            _selected = Math.Min(target * PerPage + slot, _apps.Count - 1);
            SelectionMoved?.Invoke();
        }
        GoToPage(target);
    }

    /// <summary>Directional navigation (keyboard arrows, D-pad, stick).</summary>
    public void Move(int dx, int dy)
    {
        if (_apps.Count == 0) return;
        NavigationMode = true;
        int old = _selected;
        int page = _selected / PerPage, slot = _selected % PerPage;
        if (dx != 0)
        {
            _selected = Math.Clamp(_selected + dx, 0, _apps.Count - 1);
        }
        else if (dy != 0)
        {
            int row = RowOf(slot) + dy;
            int targetPage = page;
            if (row < 0) { if (page == 0) return; targetPage--; row = 2; }
            else if (row > 2) { if (page >= Pages - 1) return; targetPage++; row = 0; }
            double x = SlotCenter(slot).X;
            int best = -1;
            double bestD = double.MaxValue;
            for (int k = 0; k < RowCount[row]; k++)
            {
                int s = RowStart[row] + k, i = targetPage * PerPage + s;
                if (i >= _apps.Count) break;
                double d = Math.Abs(SlotCenter(s).X - x);
                if (d < bestD) { bestD = d; best = i; }
            }
            if (best < 0)
            {
                // the row below is empty: next page's first bubble, else the last bubble
                if (dy > 0 && targetPage == page && page < Pages - 1) best = (page + 1) * PerPage;
                else if (dy > 0) best = _apps.Count - 1;
                else return;
            }
            _selected = Math.Min(best, _apps.Count - 1);
        }
        if (_selected != old) SelectionMoved?.Invoke();
        if (_selected / PerPage != _page) GoToPage(_selected / PerPage);
        InvalidateVisual();
    }

    public void PageBy(int delta) => ChangePage(_page + delta);

    public void Activate()
    {
        if (Selected is { } a) Opened?.Invoke(a);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.Key)
        {
            case Key.Left: Move(-1, 0); break;
            case Key.Right: Move(1, 0); break;
            case Key.Up: Move(0, -1); break;
            case Key.Down: Move(0, 1); break;
            case Key.PageUp: PageBy(-1); break;
            case Key.PageDown: PageBy(1); break;
            case Key.Enter or Key.Space: Activate(); break;
            default: return;
        }
        e.Handled = true;
    }

    protected override void OnGotFocus(GotFocusEventArgs e) { base.OnGotFocus(e); InvalidateVisual(); }
    protected override void OnLostFocus(Avalonia.Interactivity.RoutedEventArgs e) { base.OnLostFocus(e); InvalidateVisual(); }
}
