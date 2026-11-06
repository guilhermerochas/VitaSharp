using System.IO.Compression;
using System.Text;

namespace VitaSharp.Core.SaveState;

/// <summary>What a save state file says about itself (read without decompressing the body: slot lists, thumbnails).</summary>
public sealed class SaveStateHeader
{
    public int FormatVersion;
    /// <summary>Informational version of the emulator that wrote it (shown to the user).</summary>
    public string EmulatorVersion = "";
    /// <summary>Hash of every snapshot layout (<see cref="SnapshotSchema.Hash"/>): a state loads only into a build whose
    /// state layout is the same, whatever its version string.</summary>
    public string Schema = "";
    public string TitleId = "", Title = "", AppVersion = "";
    public DateTime CreatedUtc;
    public double GuestSeconds;
    /// <summary>PNG picture of the screen when the state was saved (may be empty).</summary>
    public byte[] Thumbnail = [];
}

/// <summary>
/// The save state container:
/// <code>
/// "VSSTATE\0" | u32 format version | u32 header length | header | Brotli( { string section name, i32 length, bytes } ... )
/// </code>
/// The header is uncompressed so front-ends list slots cheaply. Every section belongs to one part of the state
/// (memory, kernel objects, one HLE module...); a section a reader does not know is an error (the schema hash makes
/// that impossible for states it accepted).
/// </summary>
public static class SaveStateFile
{
    public const int FormatVersion = 1;
    static ReadOnlySpan<byte> Magic => "VSSTATE\0"u8;

    /// <summary>Emulator version string stored in new states (front-ends set it from their assembly).</summary>
    public static string EmulatorVersion { get; set; } = typeof(SaveStateFile).Assembly
        .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
        .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "unknown";

    /// <summary>Writes the magic, the version and the header; the sections follow through <see cref="SaveStateWriter"/>.</summary>
    public static void WriteHeader(Stream output, SaveStateHeader header)
    {
        var h = new SnapshotWriter();
        h.Write(header.EmulatorVersion);
        h.Write(header.Schema);
        h.Write(header.TitleId);
        h.Write(header.Title);
        h.Write(header.AppVersion);
        h.Write(header.CreatedUtc.Ticks);
        h.Write(header.GuestSeconds);
        h.Write(header.Thumbnail);
        output.Write(Magic);
        Span<byte> word = stackalloc byte[4];
        BitConverter.TryWriteBytes(word, FormatVersion); output.Write(word);
        BitConverter.TryWriteBytes(word, h.Length); output.Write(word);
        output.Write(h.Data);
    }

    /// <summary>Reads only the header. Throws <see cref="SnapshotException"/> for a file that is no save state, or one
    /// of another format version (with the version that wrote it).</summary>
    public static SaveStateHeader ReadHeader(Stream input)
    {
        Span<byte> head = stackalloc byte[16];
        if (input.ReadAtLeast(head, 16, throwOnEndOfStream: false) < 16 || !head[..8].SequenceEqual(Magic))
            throw new SnapshotException("this file is not a VitaSharp save state");
        int version = BitConverter.ToInt32(head[8..]);
        int len = BitConverter.ToInt32(head[12..]);
        if (len < 0 || len > 64 << 20) throw new SnapshotException("the save state header is corrupt");
        var buf = new byte[len];
        input.ReadExactly(buf);
        var r = new SnapshotReader(buf);
        var h = new SaveStateHeader { FormatVersion = version };
        h.EmulatorVersion = r.ReadStringNotNull();
        h.Schema = r.ReadStringNotNull();
        h.TitleId = r.ReadStringNotNull();
        h.Title = r.ReadStringNotNull();
        h.AppVersion = r.ReadStringNotNull();
        h.CreatedUtc = new DateTime(r.ReadInt64(), DateTimeKind.Utc);
        h.GuestSeconds = r.ReadDouble();
        h.Thumbnail = r.ReadBytes() ?? [];
        return h;
    }

    public static SaveStateHeader ReadHeader(string path)
    {
        using var f = File.OpenRead(path);
        return ReadHeader(f);
    }

    /// <summary>Why a state with this header cannot be loaded into the running build and title, or null. Shown to the
    /// user as is (title bar, launcher tooltip, Android notice), so in Portuguese like the rest of the UI.</summary>
    public static string? Incompatibility(SaveStateHeader h, string titleId)
    {
        if (h.FormatVersion != FormatVersion)
            return $"Este estado foi salvo por outra versão do VitaSharp ({ShortVersion(h.EmulatorVersion)}) e não pode ser carregado (formato {h.FormatVersion}; esta versão lê o formato {FormatVersion})";
        if (h.Schema != SnapshotSchema.Hash)
            return $"Este estado foi salvo por outra versão do VitaSharp ({ShortVersion(h.EmulatorVersion)}) e não pode ser carregado";
        if (!string.Equals(h.TitleId, titleId, StringComparison.OrdinalIgnoreCase))
            return $"Este estado é de outro jogo ({h.TitleId} {h.Title}) e não pode ser carregado em {titleId}";
        return null;
    }

    /// <summary>"1.0.0+60d6517ede5e..." as "1.0.0+60d6517": the build's commit, short enough for a title bar.</summary>
    static string ShortVersion(string? v)
    {
        if (string.IsNullOrEmpty(v)) return "versão desconhecida";
        int plus = v.IndexOf('+');
        return plus >= 0 && v.Length > plus + 8 ? v[..(plus + 8)] : v;
    }
}

/// <summary>Writes the compressed sections of a save state after its header.</summary>
public sealed class SaveStateWriter : IDisposable
{
    readonly BrotliStream _z;

    public SaveStateWriter(Stream output, SaveStateHeader header)
    {
        SaveStateFile.WriteHeader(output, header);
        _z = new BrotliStream(output, CompressionLevel.Fastest, leaveOpen: true);
    }

    /// <summary>Starts a section of <paramref name="length"/> bytes; the caller writes exactly that many to the result.</summary>
    public Stream Begin(string name, long length)
    {
        var nb = Encoding.UTF8.GetBytes(name);
        Span<byte> b = stackalloc byte[12];
        BitConverter.TryWriteBytes(b, nb.Length);
        _z.Write(b[..4]);
        _z.Write(nb);
        BitConverter.TryWriteBytes(b, length);
        _z.Write(b[..8]);
        return _z;
    }

    public void Section(string name, ReadOnlySpan<byte> data) => Begin(name, data.Length).Write(data);
    public void Section(string name, SnapshotWriter w) => Section(name, w.Data);

    public void Dispose() => _z.Dispose();
}

/// <summary>Reads the sections of a save state in file order (<see cref="Next"/>, then the section's bytes).</summary>
public sealed class SaveStateReader : IDisposable
{
    readonly BrotliStream _z;
    long _left;

    public SaveStateHeader Header { get; }

    public SaveStateReader(Stream input)
    {
        Header = SaveStateFile.ReadHeader(input);
        if (Header.FormatVersion != SaveStateFile.FormatVersion)
            throw new SaveStateIncompatibleException(SaveStateFile.Incompatibility(Header, Header.TitleId)!);
        _z = new BrotliStream(input, CompressionMode.Decompress, leaveOpen: true);
    }

    /// <summary>Moves to the next section (skipping what is left of the current one); false at the end.</summary>
    public bool Next(out string name, out long length)
    {
        if (_left > 0) Skip(_left);
        Span<byte> b = stackalloc byte[8];
        int got = _z.ReadAtLeast(b[..4], 4, throwOnEndOfStream: false);
        name = "";
        length = 0;
        if (got == 0) return false;
        if (got < 4) throw new SnapshotException("the save state is truncated");
        int nl = BitConverter.ToInt32(b);
        if (nl is < 0 or > 4096) throw new SnapshotException("the save state is corrupt");
        var nb = new byte[nl];
        Fill(nb);
        Fill(b);
        length = BitConverter.ToInt64(b);
        if (length < 0) throw new SnapshotException("the save state is corrupt");
        name = Encoding.UTF8.GetString(nb);
        _left = length;
        return true;
    }

    void Fill(Span<byte> dst)
    {
        try { _z.ReadExactly(dst); }
        catch (EndOfStreamException) { throw new SnapshotException("the save state is truncated"); }
        catch (InvalidDataException) { throw new SnapshotException("the save state is corrupt"); }
    }

    /// <summary>Reads the next <paramref name="dst"/>.Length bytes of the current section.</summary>
    public void Read(Span<byte> dst)
    {
        if (dst.Length > _left) throw new SnapshotException("the save state is corrupt (section overrun)");
        Fill(dst);
        _left -= dst.Length;
    }

    /// <summary>The rest of the current section.</summary>
    public byte[] ReadAll()
    {
        if (_left > int.MaxValue) throw new SnapshotException("the save state is corrupt (section too large)");
        var data = new byte[_left];
        Read(data);
        return data;
    }

    void Skip(long n)
    {
        var tmp = new byte[(int)Math.Min(n, 1 << 16)];
        while (n > 0)
        {
            int c = (int)Math.Min(n, tmp.Length);
            Read(tmp.AsSpan(0, c));
            n -= c;
        }
    }

    public void Dispose() => _z.Dispose();
}
