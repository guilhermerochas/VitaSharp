using System.Text;
using VitaSharp.Core.Host;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// SceIme: the in-game keyboard (no modal dialog). The game draws its own text box; the system keyboard delivers
/// events (OPEN, UPDATE_TEXT, UPDATE_CARET, PRESS_ENTER, PRESS_CLOSE) to the game's handler from inside sceImeUpdate,
/// on the calling thread. If --ime-text is set (<see cref="CommonDialog.ImeDefaultText"/>, the same source as
/// sceImeDialog) the text is "typed" and Enter pressed right after OPEN; otherwise text comes from
/// <see cref="HostServices.TextInput"/> when the front-end provides a keyboard. Without either the IME stays open and
/// only OPEN is delivered.
/// There is no pre-edit (kana/hangul composition): preeditLength is always 0.
/// </summary>
public sealed class Ime : HleModule
{
    internal const int ERR_ALREADY_OPENED = unchecked((int)0x80100700), ERR_NOT_OPENED = unchecked((int)0x80100701),
        ERR_INVALID_POINTER = unchecked((int)0x80100702), ERR_INVALID_TEXT = unchecked((int)0x80100706),
        ERR_INVALID_SUPPORTED_LANGUAGES = unchecked((int)0x80100713), ERR_INVALID_TYPE = unchecked((int)0x80100714),
        ERR_INVALID_OPTION = unchecked((int)0x80100715), ERR_INVALID_WORK = unchecked((int)0x80100716),
        ERR_INVALID_ARG = unchecked((int)0x80100717), ERR_INVALID_HANDLER = unchecked((int)0x80100718),
        ERR_INVALID_MAX_TEXT_LENGTH = unchecked((int)0x80100719), ERR_INVALID_INPUT_TEXT_BUFFER = unchecked((int)0x8010071A),
        ERR_INVALID_RESERVED = unchecked((int)0x8010071B), ERR_INVALID_ENTER_LABEL = unchecked((int)0x8010071C);

    internal const uint EVENT_OPEN = 0, EVENT_UPDATE_TEXT = 1, EVENT_UPDATE_CARET = 2, EVENT_CHANGE_SIZE = 3,
        EVENT_PRESS_CLOSE = 4, EVENT_PRESS_ENTER = 5;
    const uint MaxTextLength = 2048, OptionMultiline = 1, LanguageMask = 0xFFFFF;
    const uint EventSize = 0x2C;
    /// <summary>Scratch layout: event at 0, filter outTextLength at 0x30, srcText at 0x100, outText after it.</summary>
    const uint ScratchSize = 0x100 + 4 * (MaxTextLength + 1);
    /// <summary>Keyboard rectangle reported by OPEN (screen 960x544). Real height to confirm on hardware.</summary>
    internal const uint KeyboardHeight = 272;

    readonly record struct Pending(uint Id, string? Text, int Caret);

    readonly object _lock = new();
    readonly Queue<Pending> _events = new();
    bool _open;
    int _session;
    uint _handler, _arg, _filter, _buffer, _maxLen, _option;
    string _text = "";
    int _caret;
    uint _scratch;
    ITextInputBackend? _backend;

    /// <summary>Test hook: replaces the guest call made for the handler/filter (null = the calling guest thread).</summary>
    internal Func<uint, uint[], uint>? GuestCallOverride;

    internal bool IsOpen { get { lock (_lock) return _open; } }
    internal string Text { get { lock (_lock) return _text; } }
    internal int Caret { get { lock (_lock) return _caret; } }

    uint CallGuest(uint fn, params uint[] args) =>
        GuestCallOverride != null ? GuestCallOverride(fn, args) : GuestThread.Current?.CallGuest(fn, args) ?? 0;

    uint Scratch() => _scratch != 0 ? _scratch : _scratch = (Emu?.Memory ?? GuestMemory.Instance)!.Alloc(ScratchSize, "SceIme scratch", MemoryRegion.User, 16);

    /// <summary>SceImeParam (0x40): +0 sdkVersion, +4 inputMethod, +8 supportedLanguages (u64), +0x10 languagesForced,
    /// +0x14 type, +0x18 option, +0x1C work, +0x20 arg, +0x24 handler, +0x28 filter, +0x2C initialText,
    /// +0x30 maxTextLength, +0x34 inputTextBuffer, +0x38 enterLabel (u8), +0x39 reserved[7].</summary>
    [Hle]
    int sceImeOpen(uint param)
    {
        if (param == 0) return ERR_INVALID_POINTER;
        ulong languages = GuestMemory.Read<ulong>(param + 8);
        uint type = GuestMemory.R32(param + 0x14), option = GuestMemory.R32(param + 0x18), work = GuestMemory.R32(param + 0x1C);
        uint arg = GuestMemory.R32(param + 0x20), handler = GuestMemory.R32(param + 0x24), filter = GuestMemory.R32(param + 0x28);
        uint initialText = GuestMemory.R32(param + 0x2C), maxLen = GuestMemory.R32(param + 0x30), buffer = GuestMemory.R32(param + 0x34);
        byte enterLabel = GuestMemory.Read<byte>(param + 0x38);
        lock (_lock)
            if (_open) return ERR_ALREADY_OPENED;
        if (handler == 0) return ERR_INVALID_HANDLER;
        if (work == 0) return ERR_INVALID_WORK;
        if (buffer == 0) return ERR_INVALID_INPUT_TEXT_BUFFER;
        if (maxLen == 0 || maxLen > MaxTextLength) return ERR_INVALID_MAX_TEXT_LENGTH;
        if (type > 5) return ERR_INVALID_TYPE;
        if ((option & ~7u) != 0) return ERR_INVALID_OPTION;
        if (enterLabel > 3) return ERR_INVALID_ENTER_LABEL;
        for (uint i = 0; i < 7; i++)
            if (GuestMemory.Read<byte>(param + 0x39 + i) != 0) return ERR_INVALID_RESERVED;
        if ((languages & ~(ulong)LanguageMask) != 0) return ERR_INVALID_SUPPORTED_LANGUAGES;
        string initial = CommonDialog.ReadUtf16(initialText, (int)maxLen + 1);
        if (initial.Length > maxLen) return ERR_INVALID_TEXT;

        ITextInputBackend? backend;
        int session;
        lock (_lock)
        {
            _open = true;
            _session++;
            _handler = handler; _arg = arg; _filter = filter; _buffer = buffer; _maxLen = maxLen; _option = option;
            _text = initial;
            _caret = initial.Length;
            _events.Clear();
            _events.Enqueue(new Pending(EVENT_OPEN, null, 0));
            CommonDialog.WriteUtf16(buffer, initial, (int)maxLen);
            backend = _backend = Emu?.Host.TextInput;
            session = _session;
        }
        Log.WarnOnce("ime-rect", $"sceIme: keyboard rectangle reported as 960x{KeyboardHeight} (height to confirm)");
        if (CommonDialog.ImeDefaultText is { } scripted)
        {
            // automated runs: the user "types" --ime-text and presses Enter (same source as sceImeDialog), even when a
            // host keyboard exists
            lock (_lock) _backend = null;
            string t = scripted.Replace("\r\n", "\n");
            Post(session, new Pending(EVENT_UPDATE_TEXT, t, t.Length));
            Post(session, new Pending(EVENT_PRESS_ENTER, null, 0));
        }
        else if (backend != null)
        {
            backend.Begin(initial, (int)maxLen, (int)type, (option & OptionMultiline) != 0,
                (t, c) => Post(session, new Pending(EVENT_UPDATE_TEXT, t, c)),
                () => Post(session, new Pending(EVENT_PRESS_ENTER, null, 0)),
                () => Post(session, new Pending(EVENT_PRESS_CLOSE, null, 0)));
        }
        else Log.WarnOnce("ime-nokeyboard", "sceIme: no host keyboard and no --ime-text: the IME stays open without input");
        return 0;
    }

    /// <summary>Queues an event from the host keyboard (any thread); dropped when it belongs to a closed session.</summary>
    void Post(int session, Pending e)
    {
        lock (_lock)
            if (_open && session == _session) _events.Enqueue(e);
    }

    /// <summary>Front-end/test entry points equivalent to the host keyboard callbacks.</summary>
    internal void HostTextChanged(string text, int caret) { lock (_lock) Post(_session, new Pending(EVENT_UPDATE_TEXT, text, caret)); }
    internal void HostEnter() { lock (_lock) Post(_session, new Pending(EVENT_PRESS_ENTER, null, 0)); }
    internal void HostClose() { lock (_lock) Post(_session, new Pending(EVENT_PRESS_CLOSE, null, 0)); }

    /// <summary>Dispatches pending events to handler(arg, &amp;SceImeEventData) in order. The handler may close the IME.</summary>
    [Hle]
    int sceImeUpdate()
    {
        Pending[] batch;
        int session;
        lock (_lock)
        {
            if (!_open) return ERR_NOT_OPENED;
            batch = _events.ToArray();
            _events.Clear();
            session = _session;
        }
        uint ev = Scratch();
        foreach (var p in batch)
        {
            lock (_lock)
                if (!_open || _session != session) break; // closed (or reopened) by the handler
            GuestMemory.Fill(ev, 0, EventSize);
            GuestMemory.W32(ev, p.Id);
            switch (p.Id)
            {
                case EVENT_OPEN:
                case EVENT_CHANGE_SIZE:
                    GuestMemory.W32(ev + 4, 0);
                    GuestMemory.W32(ev + 8, 544 - KeyboardHeight);
                    GuestMemory.W32(ev + 12, 960);
                    GuestMemory.W32(ev + 16, KeyboardHeight);
                    break;
                case EVENT_UPDATE_TEXT:
                    if (!ApplyText(ev, p.Text ?? "", p.Caret)) continue;
                    break;
                case EVENT_UPDATE_CARET:
                    GuestMemory.W32(ev + 4, (uint)p.Caret);
                    break;
            }
            CallGuest(_handler, _arg, ev);
        }
        return 0;
    }

    /// <summary>Runs the filter, stores the new text in inputTextBuffer and fills SceImeEditText at ev+4.
    /// Returns false when the filter rejected the edit.</summary>
    bool ApplyText(uint ev, string text, int caret)
    {
        uint max, buffer, filter, option;
        string old;
        lock (_lock) { max = _maxLen; buffer = _buffer; filter = _filter; option = _option; old = _text; }
        if ((option & OptionMultiline) == 0) text = text.Replace("\n", "");
        if (text.Length > max) text = text[..(int)max];
        if (filter != 0)
        {
            uint scratch = Scratch(), outLen = scratch + 0x30, src = scratch + 0x100, dst = src + 2 * (MaxTextLength + 1);
            CommonDialog.WriteUtf16(src, text, (int)max);
            GuestMemory.W32(outLen, 0);
            GuestMemory.Write<ushort>(dst, 0);
            int r = (int)CallGuest(filter, dst, outLen, src, (uint)text.Length);
            if (r < 0) return false;
            uint n = Math.Min(GuestMemory.R32(outLen), max);
            text = Encoding.Unicode.GetString(GuestMemory.Span(dst, (int)n * 2));
        }
        int prefix = 0;
        while (prefix < old.Length && prefix < text.Length && old[prefix] == text[prefix]) prefix++;
        caret = Math.Clamp(caret, 0, text.Length);
        lock (_lock)
        {
            _text = text;
            _caret = caret;
        }
        CommonDialog.WriteUtf16(buffer, text, (int)max);
        // SceImeEditText: preeditIndex, preeditLength, caretIndex, str, editIndex, editLengthChange
        GuestMemory.W32(ev + 4, (uint)caret);
        GuestMemory.W32(ev + 8, 0);
        GuestMemory.W32(ev + 12, (uint)caret);
        GuestMemory.W32(ev + 16, buffer);
        GuestMemory.W32(ev + 20, (uint)prefix);
        GuestMemory.W32(ev + 24, (uint)(text.Length - old.Length));
        return true;
    }

    /// <summary>The game replaces the text: buffer and state are updated, the caret goes to the end. No UPDATE_TEXT is
    /// echoed back (the change came from the game itself).</summary>
    [Hle]
    int sceImeSetText(uint text, uint length)
    {
        ITextInputBackend? backend;
        string s;
        lock (_lock)
        {
            if (!_open) return ERR_NOT_OPENED;
            if (text == 0 && length > 0) return ERR_INVALID_POINTER;
            if (length > _maxLen) return ERR_INVALID_TEXT;
            s = length == 0 ? "" : Encoding.Unicode.GetString(GuestMemory.Span(text, (int)length * 2));
            int nul = s.IndexOf('\0');
            if (nul >= 0) s = s[..nul];
            _text = s;
            _caret = s.Length;
            CommonDialog.WriteUtf16(_buffer, s, (int)_maxLen);
            backend = _backend;
        }
        backend?.SetText(s, s.Length);
        return 0;
    }

    /// <summary>SceImeCaret (0x10): x, y, height, index. Moves the logical caret; an UPDATE_CARET is queued when it moved.</summary>
    [Hle]
    int sceImeSetCaret(uint caret)
    {
        lock (_lock)
        {
            if (!_open) return ERR_NOT_OPENED;
            if (caret == 0) return ERR_INVALID_POINTER;
            uint index = GuestMemory.R32(caret + 12);
            if (index > _text.Length) return ERR_INVALID_ARG;
            if (index != _caret)
            {
                _caret = (int)index;
                _events.Enqueue(new Pending(EVENT_UPDATE_CARET, null, (int)index));
            }
        }
        return 0;
    }

    /// <summary>SceImePreeditGeometry (0xC): where the game draws pre-edit text. No pre-edit in HLE: accepted and ignored.</summary>
    [Hle]
    int sceImeSetPreeditGeometry(uint preedit)
    {
        lock (_lock)
        {
            if (!_open) return ERR_NOT_OPENED;
            return preedit == 0 ? ERR_INVALID_POINTER : 0;
        }
    }

    /// <summary>Hides the keyboard; pending events are discarded. Safe from inside the handler.</summary>
    [Hle]
    int sceImeClose()
    {
        ITextInputBackend? backend;
        lock (_lock)
        {
            if (!_open) return ERR_NOT_OPENED;
            _open = false;
            _session++;
            _events.Clear();
            backend = _backend;
            _backend = null;
        }
        backend?.End();
        return 0;
    }
}
