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
