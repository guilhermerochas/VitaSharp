using Silk.NET.Input;
using SkiaSharp;
using VitaSharp.Core.Host;
using VitaSharp.Core.Util;

namespace VitaSharp.Desktop.Host;

/// <summary>
/// Desktop text entry over the game for the system IME dialog (<see cref="IImeDialogBackend"/>) and the in-game
/// keyboard SceIme (<see cref="ITextInputBackend"/>): a panel with the dialog's title and the text being typed, drawn
/// with SkiaSharp into the renderer's centre overlay, fed by the physical keyboard (characters, Backspace/Delete,
/// arrows, Home/End, Ctrl+V; Shift+Enter is a line break in multiline dialogs). Enter confirms and Esc cancels; on a
/// gamepad Cross (or Start) confirms and Circle cancels. While it is open the game sees a neutral controller and the
/// keyboard does not drive the Vita buttons.
/// </summary>
public sealed class ImeOverlay : IImeDialogBackend, ITextInputBackend, ITextCapture
{
    readonly WindowHost _window;
    readonly Action<byte[]?, int, int> _show;
    readonly object _lock = new();
    TextEntry? _entry;
    // dialog session: the answer callback; SceIme session: the event callbacks
    Action<string?>? _done;
    Action<string, int>? _changed;
    Action? _enter, _close;
    VitaButtons _prevPad;
    bool _padArmed;

    public ImeOverlay(WindowHost window, Action<byte[]?, int, int> show)
    {
        _window = window;
        _show = show;
        _window.Tick += PollGamepad;
    }

    // ---------------------------------------------------------------- IImeDialogBackend

    public void Open(ImeDialogRequest request, Action<string?> done)
    {
        lock (_lock)
        {
            _entry = new TextEntry(request.Title, request.InitialText, request.MaxLength, request.Type, request.Multiline, request.Password);
            _done = done;
            _changed = null; _enter = _close = null;
            _padArmed = false;
        }
        Log.Info($"IME overlay: \"{request.Title}\" (max {request.MaxLength}, type {request.Type}{(request.Multiline ? ", multiline" : "")}{(request.Password ? ", password" : "")})");
        _window.TextCapture = this;
        Redraw();
    }

    public void Close() => Hide();

    // ---------------------------------------------------------------- ITextInputBackend (SceIme)

    public void Begin(string initial, int maxLength, int type, bool multiline, Action<string, int> onTextChanged, Action onEnter, Action onClose)
    {
        lock (_lock)
        {
            _entry = new TextEntry("", initial, maxLength, type, multiline, false);
            _done = null;
            _changed = onTextChanged; _enter = onEnter; _close = onClose;
            _padArmed = false;
        }
        _window.TextCapture = this;
        Redraw();
    }

    public void SetText(string text, int caret)
    {
        lock (_lock) _entry?.Set(text, caret);
        Redraw();
    }

    public void End() => Hide();

    // ---------------------------------------------------------------- input (window thread)

    public void OnChar(char c)
    {
        if (_window.IsControlDown) return; // Ctrl+letter shortcuts are keys, not text
        Edit(e => e.Insert(c.ToString()));
    }

    public void OnKey(Key key)
    {
        switch (key)
        {
            case Key.Enter or Key.KeypadEnter:
                bool newline;
                lock (_lock) newline = _entry?.Multiline == true && _window.IsShiftDown;
                if (newline) Edit(e => e.Insert("\n"));
                else Confirm();
                break;
            case Key.Escape: Cancel(); break;
            case Key.Backspace: Edit(e => e.Backspace()); break;
            case Key.Delete: Edit(e => e.Delete()); break;
            case Key.Left: Edit(e => e.Move(-1), caretOnly: true); break;
            case Key.Right: Edit(e => e.Move(1), caretOnly: true); break;
            case Key.Home: Edit(e => e.MoveTo(0), caretOnly: true); break;
            case Key.End: Edit(e => e.MoveTo(e.Text.Length), caretOnly: true); break;
            case Key.V when _window.IsControlDown:
                string clip = _window.ClipboardText;
                if (clip.Length > 0) Edit(e => e.Insert(clip));
                break;
        }
    }

    /// <summary>Gamepad: Cross / Start confirm, Circle cancels (edges only, after every button was released once, so the
    /// press that opened the dialog does not answer it).</summary>
    void PollGamepad()
    {
        if (_window.TextCapture != this) return;
        var b = _window.RawController().Buttons & (VitaButtons.Cross | VitaButtons.Circle | VitaButtons.Start);
        VitaButtons pressed;
        lock (_lock)
        {
            if (!_padArmed) { _padArmed = b == VitaButtons.None; _prevPad = b; return; }
            pressed = b & ~_prevPad;
            _prevPad = b;
        }
        if ((pressed & (VitaButtons.Cross | VitaButtons.Start)) != 0) Confirm();
        else if ((pressed & VitaButtons.Circle) != 0) Cancel();
    }

    void Edit(Func<TextEntry, bool> edit, bool caretOnly = false)
    {
        Action<string, int>? changed;
        string text; int caret;
        lock (_lock)
        {
            if (_entry is not { } e || !edit(e)) return;
            changed = _changed;
            text = e.Text; caret = e.Caret;
        }
        // SceIme reports text edits; a caret move alone is not echoed (UPDATE_CARET comes from the game's own SetCaret)
        if (!caretOnly) changed?.Invoke(text, caret);
        Redraw();
    }

    void Confirm()
    {
        Action<string?>? done; Action? enter; string text;
        lock (_lock)
        {
            if (_entry == null) return;
            done = _done; enter = _enter; text = _entry.Text;
        }
        if (done != null) { Hide(); done(text); }
        else enter?.Invoke(); // SceIme: the game decides when to close (sceImeClose)
    }

    void Cancel()
    {
        Action<string?>? done; Action? close;
        lock (_lock)
        {
            if (_entry == null) return;
            done = _done; close = _close;
        }
        if (done != null) { Hide(); done(null); }
        else close?.Invoke();
    }

    void Hide()
    {
        lock (_lock)
        {
            _entry = null;
            _done = null; _changed = null; _enter = _close = null;
        }
        if (_window.TextCapture == this) _window.TextCapture = null;
        _show(null, 0, 0);
    }

    void Redraw()
    {
        TextEntry? e;
        lock (_lock) e = _entry;
        if (e == null) return;
        try
        {
            float scale = Math.Clamp(_window.FramebufferSize.Height / 544f, 1f, 3f);
            var (bgra, w, h) = Render(e, scale);
            _show(bgra, w, h);
        }
        catch (Exception ex) // a missing native Skia must never take the game down
        {
            Log.WarnOnce("ime-overlay", $"IME overlay could not be drawn: {ex.Message}");
        }
    }

    // ---------------------------------------------------------------- drawing

    /// <summary>The panel as opaque BGRA bytes.</summary>
    public static (byte[] Bgra, int Width, int Height) Render(TextEntry e, float scale)
    {
        string shown = e.Password ? new string('•', e.Text.Length) : e.Text;
        string[] lines = shown.Split('\n');
        // the caret's line and column
        int caretLine = 0, caretCol = e.Caret;
        for (int i = 0, start = 0; i < lines.Length; start += lines[i].Length + 1, i++)
            if (e.Caret <= start + lines[i].Length) { caretLine = i; caretCol = e.Caret - start; break; }
        const int MaxLines = 5;
        int first = Math.Clamp(caretLine - MaxLines + 1, 0, Math.Max(0, lines.Length - 1));
        int visible = Math.Min(MaxLines, lines.Length - first);

        int w = (int)(600 * scale), pad = (int)(14 * scale), lineH = (int)(24 * scale);
        int titleH = (int)(26 * scale), footH = (int)(22 * scale);
        int boxH = visible * lineH + (int)(12 * scale);
        int h = pad + titleH + boxH + (int)(8 * scale) + footH + pad;
        var info = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var bmp = new SKBitmap(info);
        using (var c = new SKCanvas(bmp))
        {
            c.Clear(new SKColor(0x1B, 0x1F, 0x27));
            using (var accent = new SKPaint { Color = new SKColor(0x3D, 0x8B, 0xFD) }) c.DrawRect(0, 0, w, 4 * scale, accent);
            using var titleFont = new SKFont(SKTypeface.FromFamilyName(null, SKFontStyle.Bold), 16 * scale);
            using var textFont = new SKFont(SKTypeface.Default, 17 * scale);
            using var small = new SKFont(SKTypeface.Default, 12 * scale);
            using var white = new SKPaint { Color = SKColors.White, IsAntialias = true };
            using var muted = new SKPaint { Color = new SKColor(0x9A, 0xA3, 0xB2), IsAntialias = true };
            using var boxPaint = new SKPaint { Color = new SKColor(0x2A, 0x30, 0x3B), IsAntialias = true };
            using var caretPaint = new SKPaint { Color = new SKColor(0x3D, 0x8B, 0xFD), IsAntialias = true };

            c.DrawText(e.Title.Length > 0 ? Fit(e.Title.Replace('\n', ' '), titleFont, w - 2 * pad) : "Digite o texto", pad, pad + 17 * scale, titleFont, white);
            float boxTop = pad + titleH, boxLeft = pad, boxRight = w - pad;
            c.DrawRoundRect(new SKRect(boxLeft, boxTop, boxRight, boxTop + boxH), 6 * scale, 6 * scale, boxPaint);
            float textLeft = boxLeft + 10 * scale, textWidth = boxRight - 10 * scale - textLeft;
            for (int i = 0; i < visible; i++)
            {
                string line = lines[first + i];
                float baseline = boxTop + 6 * scale + (i + 1) * lineH - 6 * scale;
                // keep the caret in view: the line is scrolled so the caret column stays inside the box
                int col = first + i == caretLine ? caretCol : 0;
                float caretX = Measure(textFont, line[..Math.Min(col, line.Length)]);
                float shift = Math.Max(0, caretX - textWidth + 4 * scale);
                c.Save();
                c.ClipRect(new SKRect(textLeft, boxTop, textLeft + textWidth, boxTop + boxH));
                c.DrawText(line, textLeft - shift, baseline, textFont, white);
                if (first + i == caretLine)
                    c.DrawRect(textLeft - shift + caretX, baseline - 16 * scale, 2 * scale, 20 * scale, caretPaint);
                c.Restore();
            }
            float footY = boxTop + boxH + 8 * scale + 14 * scale;
            c.DrawText(e.Multiline ? "Enter confirmar · Shift+Enter nova linha · Esc cancelar · controle: Cruz / Círculo"
                                   : "Enter confirmar · Esc cancelar · controle: Cruz confirma, Círculo cancela", pad, footY, small, muted);
            string count = $"{e.Text.Length}/{e.MaxLength}";
            c.DrawText(count, w - pad - Measure(small, count), footY, small, muted);
        }
        return (bmp.Bytes, w, h);
    }

    static string Fit(string s, SKFont font, float width)
    {
        if (Measure(font, s) <= width) return s;
        while (s.Length > 1 && Measure(font, s + "…") > width) s = s[..^1];
        return s + "…";
    }

    static float Measure(SKFont font, string s)
    {
        using var p = new SKPaint { Typeface = font.Typeface, TextSize = font.Size };
        return p.MeasureText(s);
    }
}
