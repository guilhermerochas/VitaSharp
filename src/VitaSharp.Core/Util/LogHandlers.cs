using System.Runtime.CompilerServices;

namespace VitaSharp.Core.Util;

/// <summary>
/// Interpolated-string handler for <see cref="Log.Debug(ref DebugLogHandler)"/> and friends: when the level is disabled
/// the holes are neither formatted nor boxed, so disabled logging on hot paths allocates nothing.
/// </summary>
[InterpolatedStringHandler]
public ref struct DebugLogHandler
{
    LevelLogHandler _h;
    public DebugLogHandler(int literalLength, int formattedCount, out bool enabled) => _h = new(literalLength, formattedCount, LogLevel.Debug, out enabled);
    public void AppendLiteral(string s) => _h.AppendLiteral(s);
    public void AppendFormatted<T>(T value) => _h.AppendFormatted(value);
    public void AppendFormatted<T>(T value, string? format) => _h.AppendFormatted(value, format);
    public void AppendFormatted<T>(T value, int alignment) => _h.AppendFormatted(value, alignment);
    public void AppendFormatted<T>(T value, int alignment, string? format) => _h.AppendFormatted(value, alignment, format);
    public void AppendFormatted(ReadOnlySpan<char> value) => _h.AppendFormatted(value);
    public void AppendFormatted(string? value) => _h.AppendFormatted(value);
    internal string ToStringAndClear() => _h.ToStringAndClear();
}

/// <summary>Trace-level counterpart of <see cref="DebugLogHandler"/>.</summary>
[InterpolatedStringHandler]
public ref struct TraceLogHandler
{
    LevelLogHandler _h;
    public TraceLogHandler(int literalLength, int formattedCount, out bool enabled) => _h = new(literalLength, formattedCount, LogLevel.Trace, out enabled);
    public void AppendLiteral(string s) => _h.AppendLiteral(s);
    public void AppendFormatted<T>(T value) => _h.AppendFormatted(value);
    public void AppendFormatted<T>(T value, string? format) => _h.AppendFormatted(value, format);
    public void AppendFormatted<T>(T value, int alignment) => _h.AppendFormatted(value, alignment);
    public void AppendFormatted<T>(T value, int alignment, string? format) => _h.AppendFormatted(value, alignment, format);
    public void AppendFormatted(ReadOnlySpan<char> value) => _h.AppendFormatted(value);
    public void AppendFormatted(string? value) => _h.AppendFormatted(value);
    internal string ToStringAndClear() => _h.ToStringAndClear();
}

/// <summary>Shared implementation: formats into a <see cref="DefaultInterpolatedStringHandler"/> only when enabled.</summary>
ref struct LevelLogHandler
{
    DefaultInterpolatedStringHandler _inner;
    readonly bool _enabled;

    public LevelLogHandler(int literalLength, int formattedCount, LogLevel level, out bool enabled)
    {
        _enabled = enabled = Log.Level <= level;
        _inner = enabled ? new DefaultInterpolatedStringHandler(literalLength, formattedCount) : default;
    }

    public void AppendLiteral(string s) { if (_enabled) _inner.AppendLiteral(s); }
    public void AppendFormatted<T>(T value) { if (_enabled) _inner.AppendFormatted(value); }
    public void AppendFormatted<T>(T value, string? format) { if (_enabled) _inner.AppendFormatted(value, format); }
    public void AppendFormatted<T>(T value, int alignment) { if (_enabled) _inner.AppendFormatted(value, alignment); }
    public void AppendFormatted<T>(T value, int alignment, string? format) { if (_enabled) _inner.AppendFormatted(value, alignment, format); }
    public void AppendFormatted(ReadOnlySpan<char> value) { if (_enabled) _inner.AppendFormatted(value); }
    public void AppendFormatted(string? value) { if (_enabled) _inner.AppendFormatted(value); }
    public string ToStringAndClear() => _enabled ? _inner.ToStringAndClear() : "";
}

/// <summary>
/// Key of <see cref="Log.WarnOnce(ref OnceKeyHandler, ref OnceMessageHandler)"/>: formatted into a per-thread buffer, so
/// a key that was already seen costs no allocation (keys are built on every call, messages only the first time).
/// </summary>
[InterpolatedStringHandler]
public ref struct OnceKeyHandler
{
    [ThreadStatic] static char[]? _scratch;
    char[] _buf;
    int _pos;

    public OnceKeyHandler(int literalLength, int formattedCount)
    {
        _buf = _scratch ??= new char[256];
        _pos = 0;
    }

    internal readonly ReadOnlySpan<char> Text => _buf.AsSpan(0, _pos);

    void Grow(int need)
    {
        var n = new char[Math.Max(_buf.Length * 2, _pos + need)];
        _buf.AsSpan(0, _pos).CopyTo(n);
        _buf = _scratch = n;
    }

    public void AppendLiteral(string s) => AppendFormatted(s.AsSpan());

    public void AppendFormatted(ReadOnlySpan<char> s)
    {
        if (_pos + s.Length > _buf.Length) Grow(s.Length);
        s.CopyTo(_buf.AsSpan(_pos));
        _pos += s.Length;
    }

    public void AppendFormatted(string? s) => AppendFormatted(s.AsSpan());

    public void AppendFormatted<T>(T value) => AppendFormatted(value, null);

    // fully optimised from the first call: the interface checks below must fold to constrained calls (no boxing)
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void AppendFormatted<T>(T value, string? format)
    {
        if (value is ISpanFormattable)
        {
            int written;
            while (!((ISpanFormattable)value).TryFormat(_buf.AsSpan(_pos), out written, format, null)) Grow(64);
            _pos += written;
        }
        else AppendFormatted(value is IFormattable f ? f.ToString(format, null) : value?.ToString());
    }
}

/// <summary>Message of <see cref="Log.WarnOnce(ref OnceKeyHandler, ref OnceMessageHandler)"/>: only formatted when the key is new.</summary>
[InterpolatedStringHandler]
public ref struct OnceMessageHandler
{
    DefaultInterpolatedStringHandler _inner;
    internal readonly bool Enabled;

    public OnceMessageHandler(int literalLength, int formattedCount, scoped ref OnceKeyHandler key, out bool enabled)
    {
        enabled = Enabled = Log.TryAddOnce(key.Text);
        _inner = enabled ? new DefaultInterpolatedStringHandler(literalLength, formattedCount) : default;
    }

    public void AppendLiteral(string s) => _inner.AppendLiteral(s);
    public void AppendFormatted<T>(T value) => _inner.AppendFormatted(value);
    public void AppendFormatted<T>(T value, string? format) => _inner.AppendFormatted(value, format);
    public void AppendFormatted<T>(T value, int alignment) => _inner.AppendFormatted(value, alignment);
    public void AppendFormatted<T>(T value, int alignment, string? format) => _inner.AppendFormatted(value, alignment, format);
    public void AppendFormatted(ReadOnlySpan<char> value) => _inner.AppendFormatted(value);
    public void AppendFormatted(string? value) => _inner.AppendFormatted(value);
    internal string ToStringAndClear() => _inner.ToStringAndClear();
}
