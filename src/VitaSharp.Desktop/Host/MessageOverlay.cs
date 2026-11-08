using Silk.NET.Input;
using SkiaSharp;
using VitaSharp.Core.Host;
using VitaSharp.Core.Localization;
using VitaSharp.Core.Util;

namespace VitaSharp.Desktop.Host;

/// <summary>
/// Desktop UI of the system message dialogs (<see cref="IMessageDialogBackend"/>: sceMsgDialog, the save data dialog's
/// messages and confirmations): a panel with the message, an optional progress bar and the buttons, drawn with SkiaSharp
/// into the renderer's centre overlay like the IME entry. The arrows (or Tab) move the selection, Enter / Space press
/// it, Esc presses the cancel button (NO, Cancel); on a controller the d-pad / left stick move, Cross presses and
/// Circle cancels (the other way round on a console whose enter button is Circle); a click presses a button. While it is open the game sees a neutral controller and no clicks.
/// </summary>
public sealed class MessageOverlay : IMessageDialogBackend, ITextCapture
{
    readonly WindowHost _window;
    readonly Action<byte[]?, int, int> _show;
    readonly Func<ControllerState> _pad;
    readonly object _lock = new();
    MessageDialogRequest? _request;
    Action<int>? _done;
    int _selected;
    int? _progress;
    string _message = "";
    VitaButtons _prevPad;
    bool _padArmed;
    // the buttons of the last drawn panel, in panel pixels, and the panel's size
    SKRect[] _buttonRects = [];
    int _panelW;

    /// <param name="pad">The controller the dialog reads (the mapped host controller, or the --input script).</param>
    public MessageOverlay(WindowHost window, Action<byte[]?, int, int> show, Func<ControllerState>? pad = null)
    {
        _window = window;
        _show = show;
        _pad = pad ?? window.RawController;
        _window.Tick += PollGamepad;
    }

    /// <summary>Whether a dialog is shown (the game's scripted input is held neutral meanwhile).</summary>
    public bool IsOpen { get { lock (_lock) return _request != null; } }

    // ---------------------------------------------------------------- IMessageDialogBackend (any thread)

    public void Open(MessageDialogRequest request, Action<int> done)
    {
        lock (_lock)
        {
            _request = request;
            _done = done;
            _message = request.Message;
            _progress = request.Progress;
            _selected = Math.Clamp(request.DefaultButton, 0, Math.Max(0, request.Buttons.Count - 1));
            _padArmed = false;
        }
        Log.Info($"Message overlay: \"{request.Message.Replace('\n', ' ')}\" [{string.Join(" | ", request.Buttons.Select(b => b.Label))}]");
        _window.TextCapture = this;
        Redraw();
    }

    public void SetProgress(int percent, string? message)
    {
        lock (_lock)
        {
            if (_request == null) return;
            if (_progress != null) _progress = Math.Clamp(percent, 0, 100);
            if (message != null) _message = message;
        }
        Redraw();
    }

    public void Close() => Hide();

    // ---------------------------------------------------------------- input (window thread)

    public void OnChar(char c) { }

    public void OnKey(Key key)
    {
        switch (key)
        {
            case Key.Left or Key.Up: Move(-1); break;
            case Key.Right or Key.Down or Key.Tab: Move(IsShift() && key == Key.Tab ? -1 : 1); break;
            case Key.Enter or Key.KeypadEnter or Key.Space: Press(null); break;
            case Key.Escape: Cancel(); break;
        }
    }

    bool IsShift() => _window.IsShiftDown;

    public void OnClick(int x, int y)
    {
        var (fbW, fbH) = _window.FramebufferSize;
        int index = -1;
        lock (_lock)
        {
            if (_request == null) return;
            var (ox, oy) = VitaSharp.Renderer.Vulkan.VulkanRenderer.CenterOverlayOrigin(_panelW, fbW, fbH);
            for (int i = 0; i < _buttonRects.Length; i++)
                if (_buttonRects[i].Contains(x - ox, y - oy)) index = i;
        }
        if (index >= 0) Press(index);
    }

    /// <summary>Controller: the d-pad / left stick move the selection, Cross presses, Circle cancels (edges only,
    /// after every button was released once, so the press that opened the dialog does not answer it).</summary>
    void PollGamepad()
    {
        if (_window.TextCapture != this) return;
        var st = _pad();
        var b = st.Buttons & (VitaButtons.Cross | VitaButtons.Circle | VitaButtons.Left | VitaButtons.Right | VitaButtons.Up | VitaButtons.Down);
        if (st.Lx < 48) b |= VitaButtons.Left; else if (st.Lx > 208) b |= VitaButtons.Right;
        VitaButtons pressed;
        lock (_lock)
        {
            if (!_padArmed) { _padArmed = b == VitaButtons.None; _prevPad = b; return; }
            pressed = b & ~_prevPad;
            _prevPad = b;
        }
        if ((pressed & (VitaButtons.Left | VitaButtons.Up)) != 0) Move(-1);
        if ((pressed & (VitaButtons.Right | VitaButtons.Down)) != 0) Move(1);
        bool circle;
        lock (_lock) circle = _request?.CircleConfirms == true;
        var enter = circle ? VitaButtons.Circle : VitaButtons.Cross;
        if ((pressed & enter) != 0) Press(null);
        else if ((pressed & (VitaButtons.Cross | VitaButtons.Circle) & ~enter) != 0) Cancel();
    }

    void Move(int delta)
    {
        lock (_lock)
        {
            if (_request is not { Buttons.Count: > 1 } r) return;
            _selected = (_selected + delta + r.Buttons.Count) % r.Buttons.Count;
        }
        Redraw();
    }

    /// <summary>Presses button <paramref name="index"/> (null: the selected one).</summary>
    void Press(int? index)
    {
        Action<int>? done; int i;
        lock (_lock)
        {
            if (_request is not { Buttons.Count: > 0 } r || _done == null) return;
            i = index ?? _selected;
            if (i < 0 || i >= r.Buttons.Count) return;
            done = _done;
        }
        Hide();
        done(i);
    }

    void Cancel()
    {
        int index = -1;
        lock (_lock)
        {
            if (_request is not { } r) return;
            for (int i = 0; i < r.Buttons.Count; i++) if (r.Buttons[i].IsCancel) index = i;
            if (index < 0 && r.Buttons.Count == 1) index = 0; // a lone OK: either button closes it
        }
        if (index >= 0) Press(index);
    }

    void Hide()
    {
        bool was;
        lock (_lock)
        {
            was = _request != null;
            _request = null;
            _done = null;
            _buttonRects = [];
        }
        if (_window.TextCapture == this) _window.TextCapture = null;
        if (was) _show(null, 0, 0);
    }

    void Redraw()
    {
        MessageDialogRequest? r; string message; int? progress; int selected;
        lock (_lock) { r = _request; message = _message; progress = _progress; selected = _selected; }
        if (r == null) return;
        try
        {
            float scale = Math.Clamp(_window.FramebufferSize.Height / 544f, 1f, 3f);
            var p = Render(message, r.Buttons.Select(b => b.Label).ToArray(), selected, progress, scale);
            lock (_lock)
            {
                if (_request != r) return; // answered meanwhile
                _buttonRects = p.Buttons;
                _panelW = p.Width;
            }
            _show(p.Bgra, p.Width, p.Height);
        }
        catch (Exception ex) // a missing native Skia must never take the game down
        {
            Log.WarnOnce("msg-overlay", $"Message overlay could not be drawn: {ex.Message}");
        }
    }

    // ---------------------------------------------------------------- drawing

    public sealed record Panel(byte[] Bgra, int Width, int Height, SKRect[] Buttons);

    /// <summary>The panel as opaque BGRA bytes, with the buttons' rectangles (panel pixels).</summary>
    public static Panel Render(string message, string[] buttons, int selected, int? progress, float scale)
    {
        int w = (int)(600 * scale), pad = (int)(18 * scale), lineH = (int)(24 * scale);
        string all = message + string.Concat(buttons);
        using var textFont = new SKFont(TypefaceFor(all, SKFontStyle.Normal), 17 * scale);
        using var buttonFont = new SKFont(TypefaceFor(all, SKFontStyle.Bold), 16 * scale);
        using var small = new SKFont(SKTypeface.Default, 12 * scale);
        var lines = Wrap(message, textFont, w - 2 * pad);
        if (lines.Count > 12) { lines = lines.Take(12).ToList(); lines[^1] += "\u2026"; }
        int textH = Math.Max(1, lines.Count) * lineH;
        int barH = progress != null ? (int)(26 * scale) : 0;
        int buttonH = (int)(36 * scale), buttonsH = buttons.Length > 0 ? buttonH + (int)(16 * scale) : 0;
        int footH = (int)(20 * scale);
        int h = pad + textH + barH + buttonsH + (int)(10 * scale) + footH + pad / 2;
        var rects = new SKRect[buttons.Length];
        var info = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var bmp = new SKBitmap(info);
        using (var c = new SKCanvas(bmp))
        {
            c.Clear(new SKColor(0x1B, 0x1F, 0x27));
            using var accent = new SKPaint { Color = new SKColor(0x3D, 0x8B, 0xFD), IsAntialias = true };
            c.DrawRect(0, 0, w, 4 * scale, accent);
            using var white = new SKPaint { Color = SKColors.White, IsAntialias = true };
            using var muted = new SKPaint { Color = new SKColor(0x9A, 0xA3, 0xB2), IsAntialias = true };
            using var boxPaint = new SKPaint { Color = new SKColor(0x2A, 0x30, 0x3B), IsAntialias = true };
            using var ring = new SKPaint { Color = new SKColor(0x8C, 0xBC, 0xFF), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2 * scale };

            float y = pad;
            for (int i = 0; i < lines.Count; i++)
            {
                float lw = Measure(textFont, lines[i]);
                c.DrawText(lines[i], (w - lw) / 2, y + (i + 1) * lineH - 6 * scale, textFont, white);
            }
            y += textH;
            if (progress is { } pct)
            {
                var track = new SKRect(pad, y + 8 * scale, w - pad, y + 18 * scale);
                c.DrawRoundRect(track, 5 * scale, 5 * scale, boxPaint);
                var fill = new SKRect(track.Left, track.Top, track.Left + track.Width * Math.Clamp(pct, 0, 100) / 100f, track.Bottom);
                if (fill.Width > 0) c.DrawRoundRect(fill, 5 * scale, 5 * scale, accent);
                y += barH;
            }
            if (buttons.Length > 0)
            {
                y += 12 * scale;
                float gap = 14 * scale;
                float bw = Math.Min(180 * scale, (w - 2 * pad - gap * (buttons.Length - 1)) / buttons.Length);
                float total = bw * buttons.Length + gap * (buttons.Length - 1);
                float x = (w - total) / 2;
                for (int i = 0; i < buttons.Length; i++)
                {
                    var r = new SKRect(x, y, x + bw, y + buttonH);
                    rects[i] = r;
                    bool sel = i == selected;
                    c.DrawRoundRect(r, 6 * scale, 6 * scale, sel ? accent : boxPaint);
                    if (sel) c.DrawRoundRect(r, 6 * scale, 6 * scale, ring);
                    string label = Fit(buttons[i], buttonFont, bw - 12 * scale);
                    c.DrawText(label, r.MidX - Measure(buttonFont, label) / 2, r.MidY + 6 * scale, buttonFont, white);
                    x += bw + gap;
                }
                y += buttonH;
            }
            string help = Loc.Get(buttons.Length > 0 ? "DlgHelp" : "DlgHelpNoButtons");
            c.DrawText(help, (w - Measure(small, help)) / 2, h - pad / 2 - 6 * scale, small, muted);
        }
        return new Panel(bmp.Bytes, w, h, rects);
    }

    /// <summary>The default typeface, or a system font that has the text's glyphs when the default lacks some (the
    /// Japanese / Chinese / Korean messages of Asian games).</summary>
    static SKTypeface TypefaceFor(string text, SKFontStyle style)
    {
        var normal = SKTypeface.FromFamilyName(null, style) ?? SKTypeface.Default;
        foreach (var rune in text.EnumerateRunes())
        {
            if (rune.Value < 0x80 || normal.ContainsGlyph(rune.Value)) continue;
            if (SKFontManager.Default.MatchCharacter(null, style, null, rune.Value) is { } other) return other;
            break;
        }
        return normal;
    }

    /// <summary>Word wrap to <paramref name="width"/> (the game's line breaks kept).</summary>
    static List<string> Wrap(string text, SKFont font, float width)
    {
        var lines = new List<string>();
        foreach (var para in text.Replace("\r\n", "\n").Split('\n'))
        {
            string line = "";
            foreach (var word in para.Split(' '))
            {
                string candidate = line.Length == 0 ? word : line + " " + word;
                if (Measure(font, candidate) <= width) { line = candidate; continue; }
                if (line.Length > 0) lines.Add(line);
                // a word wider than the panel (text without spaces: Japanese, Chinese) breaks between characters
                line = "";
                foreach (var ch in word.EnumerateRunes())
                {
                    string next = line + ch;
                    if (line.Length > 0 && Measure(font, next) > width) { lines.Add(line); next = ch.ToString(); }
                    line = next;
                }
            }
            lines.Add(line);
        }
        return lines;
    }

    static string Fit(string s, SKFont font, float width)
    {
        if (Measure(font, s) <= width) return s;
        while (s.Length > 1 && Measure(font, s + "\u2026") > width) s = s[..^1];
        return s + "\u2026";
    }

    static float Measure(SKFont font, string s)
    {
        using var p = new SKPaint { Typeface = font.Typeface, TextSize = font.Size };
        return p.MeasureText(s);
    }
}
