namespace VitaSharp.Core.Host;

/// <summary>
/// A raw image file as the backing of an emulated block device (the internal eMMC under SceSdif): 512-byte sectors,
/// as many as the file holds. Reads and writes go straight to the file (the image is the device's contents, kept
/// between runs). Out-of-range accesses are refused here; the caller answers them as the card would.
/// </summary>
public sealed class BlockImage : IDisposable
{
    public const int SectorSize = 0x200;

    private readonly FileStream _file;
    private readonly object _lock = new();

    private BlockImage(FileStream file)
    {
        _file = file;
    }

    /// <summary>The path the image was opened from (logs).</summary>
    public string Path => _file.Name;

    /// <summary>The device's size in sectors (the file's length, whole sectors only).</summary>
    public long SectorCount => _file.Length / SectorSize;

    /// <summary>Opens <paramref name="path"/> for reading and writing (it must exist).</summary>
    public static BlockImage Open(string path) =>
        new(new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read));

    /// <summary>The image <see cref="Util.EnvVars.EmmcImage"/> names, or null (unset, or the file cannot be opened:
    /// logged).</summary>
    public static BlockImage? FromEnvironment(string variable)
    {
        string? path = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Open(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Util.Log.Warn($"[sdif] {variable}: the image cannot be opened ({e.Message}); the device stays without one");
            return null;
        }
    }

    /// <summary>True when sectors [<paramref name="sector"/>, + <paramref name="count"/>) are inside the device.</summary>
    public bool Contains(ulong sector, ulong count) => count <= (ulong)SectorCount && sector <= (ulong)SectorCount - count;

    /// <summary>Reads whole sectors from <paramref name="sector"/> into <paramref name="data"/>.</summary>
    public void Read(ulong sector, Span<byte> data)
    {
        lock (_lock)
        {
            _file.Position = (long)sector * SectorSize;
            _file.ReadExactly(data);
        }
    }

    /// <summary>Writes whole sectors from <paramref name="sector"/>.</summary>
    public void Write(ulong sector, ReadOnlySpan<byte> data)
    {
        lock (_lock)
        {
            _file.Position = (long)sector * SectorSize;
            _file.Write(data);
            _file.Flush();
        }
    }

    public void Dispose() => _file.Dispose();
}
