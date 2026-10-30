using System.Text;

namespace VitaSharp.Core.Host;

/// <summary>
/// The text being typed in a host text entry (the desktop IME overlay): text, caret, the dialog's limits (maximum
/// length in UTF-16 units, input type) and the editing keys. Pure state, no UI (unit-tested).
/// </summary>
public sealed class TextEntry(string title, string initial, int maxLength, int type, bool multiline, bool password)
{
    /// <summary>SceImeType: 0 default, 1 basic Latin, 2 URL, 3 mail, 4 number, 5 extended number.</summary>
    public const int TypeBasicLatin = 1, TypeUrl = 2, TypeMail = 3, TypeNumber = 4, TypeExtendedNumber = 5;

    readonly StringBuilder _text = new(initial.Length > maxLength ? initial[..maxLength] : initial);
    public string Title { get; } = title;
    public int MaxLength { get; } = Math.Max(1, maxLength);
    public int Type { get; } = type;
    public bool Multiline { get; } = multiline;
    public bool Password { get; } = password;
    public int Caret { get; private set; } = Math.Min(initial.Length, Math.Max(1, maxLength));
    public string Text => _text.ToString();

    /// <summary>Whether <paramref name="c"/> may be typed in a dialog of this type.</summary>
    public bool Accepts(char c)
    {
        if (c == '\n') return Multiline;
        if (char.IsControl(c)) return false;
        return Type switch
        {
            TypeNumber => c is >= '0' and <= '9',
            TypeExtendedNumber => c is >= '0' and <= '9' or '.' or ',' or '-' or '+',
            TypeBasicLatin or TypeUrl or TypeMail => c < 0x80 && (Type == TypeBasicLatin || c != ' '),
            _ => true,
        };
    }

    /// <summary>Inserts at the caret what fits and is accepted; returns whether anything changed.</summary>
    public bool Insert(string s)
    {
        var sb = new StringBuilder();
        foreach (char c in s.Replace("\r\n", "\n").Replace('\r', '\n'))
            if (Accepts(c)) sb.Append(c);
        int room = MaxLength - _text.Length;
        if (room <= 0 || sb.Length == 0) return false;
        string add = sb.Length > room ? sb.ToString(0, room) : sb.ToString();
        if (add.Length > 0 && char.IsHighSurrogate(add[^1])) add = add[..^1]; // never half a pair
        if (add.Length == 0) return false;
        _text.Insert(Caret, add);
        Caret += add.Length;
        return true;
    }

    public bool Backspace()
    {
        if (Caret == 0) return false;
        int n = Caret >= 2 && char.IsLowSurrogate(_text[Caret - 1]) && char.IsHighSurrogate(_text[Caret - 2]) ? 2 : 1;
        _text.Remove(Caret - n, n);
        Caret -= n;
        return true;
    }

    public bool Delete()
    {
        if (Caret >= _text.Length) return false;
        int n = Caret + 1 < _text.Length && char.IsHighSurrogate(_text[Caret]) && char.IsLowSurrogate(_text[Caret + 1]) ? 2 : 1;
        _text.Remove(Caret, n);
        return true;
    }

    public bool Move(int delta)
    {
        int c = Math.Clamp(Caret + delta, 0, _text.Length);
        if (c > 0 && c < _text.Length && char.IsLowSurrogate(_text[c]) && char.IsHighSurrogate(_text[c - 1])) c += delta > 0 ? 1 : -1;
        if (c == Caret) return false;
        Caret = c;
        return true;
    }

    public bool MoveTo(int index)
    {
        index = Math.Clamp(index, 0, _text.Length);
        if (index == Caret) return false;
        Caret = index;
        return true;
    }

    /// <summary>The game replaced the text (sceImeSetText).</summary>
    public void Set(string text, int caret)
    {
        _text.Clear().Append(text.Length > MaxLength ? text[..MaxLength] : text);
        Caret = Math.Clamp(caret, 0, _text.Length);
    }
}
