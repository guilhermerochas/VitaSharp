using Silk.NET.Input;
using SkiaSharp;
using VitaSharp.Core.Host;
using VitaSharp.Core.Localization;
using VitaSharp.Core.SaveState;
using VitaSharp.Core.Util;

namespace VitaSharp.Desktop.Host;

/// <summary>
/// The in-game save state picker (like PCSX2's): F6 opens it over the paused game with the quick slot and slots 1-9,
/// each with the picture of the screen, the date it was saved, the play time and the VitaSharp version. Two tabs, Save
/// and Load (Tab, L / R, or a click on them); the arrows / d-pad pick a slot, Enter / Cross saves or loads it (saving
/// over a used slot shows the old and the current picture and asks first; loading shows the slot's picture large and
/// asks first), Delete / Triangle deletes a slot (asks first), F5 saves and F7 loads the selected slot, F6 moves to the
/// next one, Esc / Circle closes. The game sees a neutral controller while it is open (the front-end also pauses it,
/// <see cref="Opened"/> / <see cref="Closed"/>); the chosen slot stays selected for Shift+F5 / Shift+F7.
/// </summary>
public sealed class SaveStateOverlay : ITextCapture
{
    public const int Slots = 10; // 0 = quick, 1..9

    /// <summary>What the open question asks about the selected slot.</summary>
    public enum Question
    {
        None, Overwrite, Load, Delete
    }

    readonly WindowHost _window;
    readonly Action<byte[]?, int, int> _show;
    readonly Func<ControllerState> _pad;
    readonly Func<string, string?> _titleCheck; // slot path -> why it cannot be loaded (null: it can)
    readonly Func<int, string> _pathOf;
    readonly object _lock = new();
    bool _open, _saveTab = true, _padArmed, _dirty;
    int _selected = 1;
    VitaButtons _prevPad;
    /// <summary>Slot the question is about (-1: none), the question and its selected answer (0 yes, 1 no).</summary>
    int _askSlot = -1, _askChoice = 1;
    Question _ask;
    SlotInfo[] _slots = [];
    /// <summary>The game's screen when the picker opened (shown next to the old picture when overwriting).</summary>
    SKBitmap? _current;
    Panel? _panel;

    /// <summary>Raised (window thread) when the user saves / loads a slot from the picker; the slot becomes the selected one.</summary>
    public event Action<int>? SaveRequested, LoadRequested;
    /// <summary>Raised (window thread) after a slot's file was deleted from the picker (the slot number).</summary>
    public event Action<int>? Deleted;
    /// <summary>Raised (window thread) when the picker opens / closes: the front-end pauses the game meanwhile.</summary>
    public event Action? Opened, Closed;

    sealed record SlotInfo(bool Exists, DateTime Saved, SKBitmap? Thumb, string? Problem, double PlaySeconds, string Version);

    public SaveStateOverlay(WindowHost window, Action<byte[]?, int, int> show, Func<int, string> pathOf, Func<string, string?> loadProblem, Func<ControllerState>? pad = null)
    {
        _window = window;
        _show = show;
        _pathOf = pathOf;
        _titleCheck = loadProblem;
        _pad = pad ?? window.RawController;
        _window.Tick += PollGamepad;
    }

    public bool IsOpen
    {
        get
        {
            lock (_lock)
            {
                return _open;
            }
        }
    }

    /// <summary>Opens the picker on <paramref name="slot"/> (window thread); false when another dialog is open.</summary>
    public bool Open(int slot)
    {
        if (_window.TextCapture != null && _window.TextCapture != this)
        {
            return false;
        }

        var slots = ReadSlots();
        bool wasOpen;
        lock (_lock)
        {
            wasOpen = _open;
            DisposeThumbs();
            _slots = slots;
            _selected = Math.Clamp(slot, 0, Slots - 1);
            _askSlot = -1;
            _ask = Question.None;
            _open = true;
            _padArmed = false;
        }
        Log.Info($"Save state picker: open on slot {slot} ({slots.Count(s => s.Exists)} saved)");
        _window.TextCapture = this;
        Redraw();
        if (!wasOpen)
        {
            Opened?.Invoke();
        }

        return true;
    }

    /// <summary>The game's screen right now (PNG; null: none), drawn next to the old picture of a slot being overwritten.</summary>
    public void SetCurrentPicture(byte[]? png)
    {
        SKBitmap? bmp = null;
        try
        {
            bmp = png is { Length: > 0 } ? SKBitmap.Decode(png) : null;
        }
        catch (Exception e)
        {
            Log.WarnOnce("state-overlay-pic", $"Save state picker: the current picture could not be decoded: {e.Message}");
        }
        lock (_lock)
        {
            if (!_open)
            {
                bmp?.Dispose();
                return;
            }
            _current?.Dispose();
            _current = bmp;
            _dirty = true; // drawn on the window thread (PollGamepad)
        }
    }

    SlotInfo[] ReadSlots()
    {
        var list = new SlotInfo[Slots];
        for (int i = 0; i < Slots; i++)
        {
            string path = _pathOf(i);
            if (!File.Exists(path))
            {
                list[i] = new SlotInfo(false, default, null, null, 0, "");
                continue;
            }
            try
            {
                var h = SaveStateFile.ReadHeader(path);
                SKBitmap? thumb = h.Thumbnail.Length > 0 ? SKBitmap.Decode(h.Thumbnail) : null;
                list[i] = new SlotInfo(true, h.CreatedUtc.ToLocalTime(), thumb, _titleCheck(path), h.GuestSeconds, SaveStateFile.ShortVersion(h.EmulatorVersion));
            }
            catch (Exception e) when (e is IOException or SnapshotException or UnauthorizedAccessException)
            {
                list[i] = new SlotInfo(true, File.GetLastWriteTime(path), null, e.Message, 0, "");
            }
        }
        return list;
    }

    public void Close()
    {
        lock (_lock)
        {
            if (!_open)
            {
                return;
            }

            _open = false;
            _askSlot = -1;
            _ask = Question.None;
            DisposeThumbs();
            _current?.Dispose();
            _current = null;
            _panel = null;
        }
        if (_window.TextCapture == this)
        {
            _window.TextCapture = null;
        }

        _show(null, 0, 0);
        Closed?.Invoke();
    }

    void DisposeThumbs()
    {
        foreach (var s in _slots)
        {
            s.Thumb?.Dispose();
        }

        _slots = [];
    }

    // ---------------------------------------------------------------- input (window thread)

    public void OnChar(char c)
    {
    }

    public void OnKey(Key key)
    {
        bool asking;
        lock (_lock)
        {
            asking = _askSlot >= 0;
        }

        if (asking)
        {
            switch (key)
            {
                case Key.Left or Key.Right or Key.Up or Key.Down or Key.Tab:
                    lock (_lock)
                    {
                        _askChoice ^= 1;
                    }

                    Redraw();
                    break;
                case Key.Enter or Key.KeypadEnter or Key.Space:
                    Answer(null);
                    break;
                case Key.Escape:
                    Answer(false);
                    break;
            }
            return;
        }
        switch (key)
        {
            case Key.Left:
                Move(-1);
                break;
            case Key.Right:
                Move(1);
                break;
            case Key.Up:
                Move(-5);
                break;
            case Key.Down:
                Move(5);
                break;
            case Key.Tab:
                SwitchTab();
                break;
            case Key.Enter or Key.KeypadEnter or Key.Space:
                Act(null);
                break;
            case Key.F5:
                Act(true);
                break;
            case Key.F7:
                Act(false);
                break;
            case Key.F6:
                Move(1, wrap: true);
                break;
            case Key.Delete or Key.Backspace:
                AskDelete();
                break;
            case Key.Escape:
                Close();
                break;
        }
    }

    public void OnClick(int x, int y)
    {
        var (fbW, fbH) = _window.FramebufferSize;
        Panel? p;
        bool asking;
        lock (_lock)
        {
            p = _panel;
            asking = _askSlot >= 0;
        }
        if (p == null)
        {
            return;
        }

        var (ox, oy) = VitaSharp.Renderer.Vulkan.VulkanRenderer.CenterOverlayOrigin(p.Width, fbW, fbH);
        float px = x - ox, py = y - oy;
        if (asking)
        {
            for (int i = 0; i < p.ConfirmButtons.Length; i++)
            {
                if (p.ConfirmButtons[i].Contains(px, py))
                {
                    Answer(i == 0);
                    return;
                }
            }

            return;
        }
        if (p.SaveTab.Contains(px, py))
        {
            SetTab(true);
            return;
        }
        if (p.LoadTab.Contains(px, py))
        {
            SetTab(false);
            return;
        }
        for (int i = 0; i < p.Tiles.Length; i++)
        {
            if (p.Tiles[i].Contains(px, py))
            {
                lock (_lock)
                {
                    _selected = i;
                }

                Act(null);
                return;
            }
        }
    }

    void PollGamepad()
    {
        if (_window.TextCapture != this)
        {
            return;
        }

        bool dirty;
        lock (_lock)
        {
            dirty = _dirty;
            _dirty = false;
        }
        if (dirty)
        {
            Redraw();
        }

        var st = _pad();
        const VitaButtons Watched = VitaButtons.Cross | VitaButtons.Circle | VitaButtons.Triangle | VitaButtons.Left | VitaButtons.Right
                                    | VitaButtons.Up | VitaButtons.Down | VitaButtons.LTrigger | VitaButtons.RTrigger;
        var b = st.Buttons & Watched;
        VitaButtons pressed;
        lock (_lock)
        {
            if (!_padArmed)
            {
                _padArmed = b == VitaButtons.None;
                _prevPad = b;
                return;
            }
            pressed = b & ~_prevPad;
            _prevPad = b;
        }
        if (pressed == VitaButtons.None)
        {
            return;
        }

        bool asking;
        lock (_lock)
        {
            asking = _askSlot >= 0;
        }

        if (asking)
        {
            if ((pressed & (VitaButtons.Left | VitaButtons.Right)) != 0)
            {
                lock (_lock)
                {
                    _askChoice ^= 1;
                }

                Redraw();
            }
            if ((pressed & VitaButtons.Cross) != 0)
            {
                Answer(null);
            }
            else if ((pressed & VitaButtons.Circle) != 0)
            {
                Answer(false);
            }

            return;
        }
        if ((pressed & VitaButtons.Left) != 0)
        {
            Move(-1);
        }

        if ((pressed & VitaButtons.Right) != 0)
        {
            Move(1);
        }

        if ((pressed & VitaButtons.Up) != 0)
        {
            Move(-5);
        }

        if ((pressed & VitaButtons.Down) != 0)
        {
            Move(5);
        }

        if ((pressed & VitaButtons.LTrigger) != 0)
        {
            SetTab(true);
        }

        if ((pressed & VitaButtons.RTrigger) != 0)
        {
            SetTab(false);
        }

        if ((pressed & VitaButtons.Triangle) != 0)
        {
            AskDelete();
        }
        else if ((pressed & VitaButtons.Cross) != 0)
        {
            Act(null);
        }
        else if ((pressed & VitaButtons.Circle) != 0)
        {
            Close();
        }
    }

    void Move(int delta, bool wrap = false)
    {
        lock (_lock)
        {
            int n = _selected + delta;
            _selected = wrap ? (n % Slots + Slots) % Slots : Math.Clamp(n, 0, Slots - 1);
        }
        Redraw();
    }

    void SwitchTab()
    {
        bool save;
        lock (_lock)
        {
            save = !_saveTab;
        }

        SetTab(save);
    }

    void SetTab(bool save)
    {
        lock (_lock)
        {
            _saveTab = save;
        }

        Redraw();
    }

    /// <summary>Saves (true), loads (false) or does the current tab's action (null) on the selected slot: overwriting
    /// and loading ask first.</summary>
    void Act(bool? save)
    {
        int slot;
        bool direct;
        lock (_lock)
        {
            if (!_open || _slots.Length == 0)
            {
                return;
            }

            slot = _selected;
            var info = _slots[slot];
            bool doSave = save ?? _saveTab;
            if (save is { } s)
            {
                _saveTab = s;
            }

            direct = doSave && !info.Exists; // an empty slot is saved at once
            if (doSave && info.Exists)
            {
                Ask(Question.Overwrite, slot);
            }
            else if (!doSave && info.Exists && info.Problem == null)
            {
                Ask(Question.Load, slot);
            }
            // nothing to load: the tile says why
        }
        if (!direct)
        {
            Redraw();
            return;
        }
        Close();
        SaveRequested?.Invoke(slot);
    }

    void Ask(Question q, int slot)
    {
        _ask = q;
        _askSlot = slot;
        _askChoice = 1; // No selected: a stray Enter changes nothing
    }

    void AskDelete()
    {
        lock (_lock)
        {
            if (!_open || _slots.Length == 0 || !_slots[_selected].Exists)
            {
                return;
            }

            Ask(Question.Delete, _selected);
        }
        Redraw();
    }

    /// <summary>The open question: yes (true), no (false) or the selected answer (null).</summary>
    void Answer(bool? yes)
    {
        int slot;
        bool ok;
        Question q;
        lock (_lock)
        {
            if (_askSlot < 0)
            {
                return;
            }

            slot = _askSlot;
            q = _ask;
            ok = yes ?? _askChoice == 0;
            _askSlot = -1;
            _ask = Question.None;
        }
        if (!ok)
        {
            Redraw();
            return;
        }
        switch (q)
        {
            case Question.Overwrite:
                Close();
                SaveRequested?.Invoke(slot);
                break;
            case Question.Load:
                Close();
                LoadRequested?.Invoke(slot);
                break;
            case Question.Delete:
                Delete(slot);
                break;
        }
    }

    void Delete(int slot)
    {
        string path = _pathOf(slot);
        try
        {
            File.Delete(path);
            Log.Info($"Save state picker: deleted {path}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Save state picker: {path} could not be deleted: {e.Message}");
        }
        var slots = ReadSlots();
        lock (_lock)
        {
            if (!_open)
            {
                foreach (var s in slots)
                {
                    s.Thumb?.Dispose();
                }

                return;
            }
            DisposeThumbs();
            _slots = slots;
        }
        Redraw();
        if (!File.Exists(path))
        {
            Deleted?.Invoke(slot);
        }
    }

    void Redraw()
    {
        SlotInfo[] slots;
        int selected, ask, choice;
        bool save;
        Question q;
        SKBitmap? current;
        lock (_lock)
        {
            if (!_open)
            {
                return;
            }

            slots = _slots;
            selected = _selected;
            ask = _askSlot;
            choice = _askChoice;
            save = _saveTab;
            q = _ask;
            current = _current;
            try
            {
                float scale = Math.Clamp(_window.FramebufferSize.Height / 544f, 1f, 3f);
                var view = slots.Select((s, i) => new SlotView(SlotName(i),
                    s.Problem != null ? Loc.Get("SsIncompatible") : s.Exists ? s.Saved.ToString("g", Loc.Culture) : Loc.Get("SsEmpty"),
                    s.Exists && s.Problem == null ? PlayTime(s.PlaySeconds) + " \u00B7 " + s.Version : "", s.Thumb, s.Exists, s.Problem != null)).ToArray();
                QuestionView? question = null;
                if (ask >= 0)
                {
                    var s = slots[ask];
                    string key = q switch
                    {
                        Question.Load => "SsLoadAsk",
                        Question.Delete => "SsDeleteAsk",
                        _ => "SsOverwrite"
                    };
                    question = new QuestionView(Loc.Format(key, SlotName(ask), s.Saved.ToString("g", Loc.Culture)), s.Problem == null ? Extra(s) : "",
                        s.Thumb, q == Question.Overwrite ? current : null, q, choice);
                }
                var p = Render(view, selected, save, question, scale);
                _panel = p;
                _show(p.Bgra, p.Width, p.Height);
            }
            catch (Exception ex) // a missing native Skia must never take the game down
            {
                Log.WarnOnce("state-overlay", $"Save state picker could not be drawn: {ex.Message}");
            }
        }
    }

    /// <summary>"Play time 1:02:03 - 1.0.0+abcdef1" (with a middle dot).</summary>
    static string Extra(SlotInfo s) => Loc.Format("SsPlayTime", PlayTime(s.PlaySeconds)) + " \u00B7 " + s.Version;

    /// <summary>Guest seconds as h:mm:ss (m:ss under an hour).</summary>
    public static string PlayTime(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}" : $"{t.Minutes}:{t.Seconds:D2}";
    }

    public static string SlotName(int slot) => slot == 0 ? Loc.Get("SsQuick") : Loc.Format("SsSlot", slot);

    // ---------------------------------------------------------------- drawing

    public sealed record SlotView(string Name, string Detail, string Extra, SKBitmap? Thumb, bool Exists, bool Unloadable);

    /// <summary>The open question: its text, a detail line, the slot's picture, the current one (overwrite) and the
    /// selected answer (0 yes, 1 no).</summary>
    public sealed record QuestionView(string Text, string Detail, SKBitmap? Picture, SKBitmap? Current, Question Kind, int Choice);

    public sealed record Panel(byte[] Bgra, int Width, int Height, SKRect[] Tiles, SKRect SaveTab, SKRect LoadTab, SKRect[] ConfirmButtons);

    /// <summary>The picker as opaque BGRA bytes with the clickable areas (panel pixels).</summary>
    public static Panel Render(SlotView[] slots, int selected, bool saveTab, QuestionView? question, float scale)
    {
        const int Cols = 5;
        int rows = (slots.Length + Cols - 1) / Cols;
        float pad = 16 * scale, gap = 10 * scale;
        float tileW = 150 * scale, thumbH = tileW * 544f / 960f, tileH = thumbH + 58 * scale;
        int w = (int)(pad * 2 + Cols * tileW + (Cols - 1) * gap);
        float tabsH = 34 * scale, footH = 22 * scale;
        int h = (int)(pad + tabsH + 10 * scale + rows * tileH + (rows - 1) * gap + 10 * scale + footH + pad / 2);

        // the question box: the pictures (one large, or the old and the current side by side), two text lines, the buttons
        float qw = w - 2 * pad, picW = 0, picH = 0, qh = 0;
        bool twoPics = question is { Kind: Question.Overwrite };
        if (question != null)
        {
            picW = twoPics ? (qw - 3 * pad) / 2 : Math.Min(qw - 2 * pad, 480 * scale);
            picH = picW * 544f / 960f;
            qh = pad + (twoPics ? 20 * scale : 0) + picH + 14 * scale + 20 * scale + 18 * scale + 14 * scale + 34 * scale + 14 * scale;
            h = Math.Max(h, (int)(qh + 2 * pad));
        }

        var tiles = new SKRect[slots.Length];
        SKRect saveRect, loadRect;
        var confirmRects = Array.Empty<SKRect>();
        using var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var c = new SKCanvas(bmp))
        {
            c.Clear(new SKColor(0x1B, 0x1F, 0x27));
            using var accent = new SKPaint { Color = new SKColor(0x3D, 0x8B, 0xFD), IsAntialias = true };
            using var white = new SKPaint { Color = SKColors.White, IsAntialias = true };
            using var muted = new SKPaint { Color = new SKColor(0x9A, 0xA3, 0xB2), IsAntialias = true };
            using var warn = new SKPaint { Color = new SKColor(0xFF, 0xB5, 0x47), IsAntialias = true };
            using var danger = new SKPaint { Color = new SKColor(0xD9, 0x4A, 0x4A), IsAntialias = true };
            using var box = new SKPaint { Color = new SKColor(0x2A, 0x30, 0x3B), IsAntialias = true };
            using var empty = new SKPaint { Color = new SKColor(0x22, 0x27, 0x30) };
            using var ring = new SKPaint { Color = new SKColor(0x8C, 0xBC, 0xFF), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3 * scale };
            using var dim = new SKPaint { Color = new SKColor(0, 0, 0, 0xA0) };
            using var sampling = new SKPaint { FilterQuality = SKFilterQuality.Medium, IsAntialias = true };
            using var bold = new SKFont(SKTypeface.FromFamilyName(null, SKFontStyle.Bold), 15 * scale);
            using var font = new SKFont(SKTypeface.Default, 13 * scale);
            using var small = new SKFont(SKTypeface.Default, 12 * scale);
            c.DrawRect(0, 0, w, 4 * scale, accent);

            void Picture(SKBitmap? pic, SKRect r)
            {
                if (pic != null)
                {
                    c.DrawBitmap(pic, r, sampling);
                    return;
                }
                c.DrawRect(r, empty);
                c.DrawText("?", r.MidX - Measure(bold, "?") / 2, r.MidY + 5 * scale, bold, muted);
            }

            // tabs
            float tabW = 140 * scale, ty = pad;
            saveRect = new SKRect(pad, ty, pad + tabW, ty + tabsH);
            loadRect = new SKRect(saveRect.Right + gap, ty, saveRect.Right + gap + tabW, ty + tabsH);
            foreach (var (r, label, on) in new[] { (saveRect, Loc.Get("SsSave"), saveTab), (loadRect, Loc.Get("SsLoad"), !saveTab) })
            {
                c.DrawRoundRect(r, 6 * scale, 6 * scale, on ? accent : box);
                c.DrawText(label, r.MidX - Measure(bold, label) / 2, r.MidY + 5 * scale, bold, white);
            }
            string title = Loc.Get("SsTitle");
            c.DrawText(title, w - pad - Measure(bold, title), ty + tabsH / 2 + 5 * scale, bold, muted);

            // slots
            float top = pad + tabsH + 10 * scale;
            for (int i = 0; i < slots.Length; i++)
            {
                float x = pad + i % Cols * (tileW + gap), y = top + i / Cols * (tileH + gap);
                var r = new SKRect(x, y, x + tileW, y + tileH);
                tiles[i] = r;
                c.DrawRoundRect(r, 6 * scale, 6 * scale, box);
                var thumb = new SKRect(x + 4 * scale, y + 4 * scale, r.Right - 4 * scale, y + 4 * scale + thumbH - 8 * scale);
                if (slots[i].Thumb is { } t)
                {
                    c.DrawBitmap(t, thumb, sampling);
                }
                else
                {
                    c.DrawRect(thumb, empty);
                    string e = slots[i].Exists ? "?" : "\u2014";
                    c.DrawText(e, thumb.MidX - Measure(bold, e) / 2, thumb.MidY + 5 * scale, bold, muted);
                }
                c.DrawText(Fit(slots[i].Name, bold, tileW - 8 * scale), x + 6 * scale, thumb.Bottom + 18 * scale, bold, white);
                c.DrawText(Fit(slots[i].Detail, small, tileW - 8 * scale), x + 6 * scale, thumb.Bottom + 34 * scale, small,
                    slots[i].Unloadable ? warn : muted);
                if (slots[i].Extra.Length > 0)
                {
                    c.DrawText(Fit(slots[i].Extra, small, tileW - 8 * scale), x + 6 * scale, thumb.Bottom + 49 * scale, small, muted);
                }

                if (i == selected)
                {
                    c.DrawRoundRect(r, 6 * scale, 6 * scale, ring);
                }
            }

            string help = Loc.Get("SsHelp");
            c.DrawText(Fit(help, small, w - 2 * pad), pad, h - pad / 2 - 6 * scale, small, muted);

            // the question over the slots
            if (question != null)
            {
                c.DrawRect(0, 0, w, h, dim);
                var q = new SKRect((w - qw) / 2, (h - qh) / 2, (w + qw) / 2, (h + qh) / 2);
                using var qbg = new SKPaint { Color = new SKColor(0x1B, 0x1F, 0x27), IsAntialias = true };
                c.DrawRoundRect(q, 8 * scale, 8 * scale, qbg);
                c.DrawRoundRect(q, 8 * scale, 8 * scale, ring);
                float py = q.Top + pad;
                if (twoPics)
                {
                    var left = new SKRect(q.Left + pad, py + 20 * scale, q.Left + pad + picW, py + 20 * scale + picH);
                    var right = new SKRect(left.Right + pad, left.Top, left.Right + pad + picW, left.Bottom);
                    string was = Loc.Get("SsPictureSaved"), now = Loc.Get("SsPictureNow");
                    c.DrawText(was, left.MidX - Measure(small, was) / 2, py + 13 * scale, small, muted);
                    c.DrawText(now, right.MidX - Measure(small, now) / 2, py + 13 * scale, small, muted);
                    Picture(question.Picture, left);
                    Picture(question.Current, right);
                    py = left.Bottom;
                }
                else
                {
                    var pic = new SKRect(q.MidX - picW / 2, py, q.MidX + picW / 2, py + picH);
                    Picture(question.Picture, pic);
                    py = pic.Bottom;
                }
                string text = Fit(question.Text, bold, qw - 2 * pad);
                c.DrawText(text, q.MidX - Measure(bold, text) / 2, py + 14 * scale + 16 * scale, bold, white);
                if (question.Detail.Length > 0)
                {
                    string d = Fit(question.Detail, small, qw - 2 * pad);
                    c.DrawText(d, q.MidX - Measure(small, d) / 2, py + 14 * scale + 20 * scale + 16 * scale, small, muted);
                }
                float bw = 120 * scale, bh = 34 * scale, by = q.Bottom - bh - 14 * scale;
                confirmRects = [new SKRect(q.MidX - bw - 8 * scale, by, q.MidX - 8 * scale, by + bh), new SKRect(q.MidX + 8 * scale, by, q.MidX + 8 * scale + bw, by + bh)];
                string[] labels = [Loc.Get("DlgYes"), Loc.Get("DlgNo")];
                for (int i = 0; i < 2; i++)
                {
                    var on = i == question.Choice ? (i == 0 && question.Kind == Question.Delete ? danger : accent) : box;
                    c.DrawRoundRect(confirmRects[i], 6 * scale, 6 * scale, on);
                    c.DrawText(labels[i], confirmRects[i].MidX - Measure(bold, labels[i]) / 2, confirmRects[i].MidY + 5 * scale, bold, white);
                }
            }
        }
        return new Panel(bmp.Bytes, w, h, tiles, saveRect, loadRect, confirmRects);
    }

    static string Fit(string s, SKFont font, float width)
    {
        if (Measure(font, s) <= width)
        {
            return s;
        }

        while (s.Length > 1 && Measure(font, s + "\u2026") > width)
        {
            s = s[..^1];
        }

        return s + "\u2026";
    }

    static float Measure(SKFont font, string s)
    {
        using var p = new SKPaint { Typeface = font.Typeface, TextSize = font.Size };
        return p.MeasureText(s);
    }
}
