using System.Buffers.Binary;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Gpu;

/// <summary>
/// Content-addressed disk cache of transcoded texture levels (ASTC blocks made from BC/PVRTC data), one folder per
/// title inside the shader cache (&lt;ShaderCacheDir&gt;/titles/&lt;TITLEID&gt;/astc). An entry is named by the
/// 64-bit key of its source (hash of the compressed data, format, size, encoder version); it holds a small header and
/// the payload. Least recently used entries are deleted once the folder grows past its size limit (the use time is
/// the file's last-write time, refreshed on every hit).
/// </summary>
public sealed class TranscodeDiskCache
{
    const uint Magic = 0x54415356; // "VSAT"
    const int HeaderSize = 24;

    readonly string _dir;
    readonly long _limit;
    readonly object _lock = new();
    long _bytes = -1;
    public long Hits, Misses, Writes, Evictions;

    public string Directory => _dir;
    public long Limit => _limit;

    public TranscodeDiskCache(string dir, long limitBytes)
    {
        _dir = dir;
        _limit = Math.Max(limitBytes, 1 << 20);
    }

    string PathOf(ulong key) => Path.Combine(_dir, $"{key:x16}.astc");

    /// <summary>The cached payload of <paramref name="key"/> when it exists, has the expected <paramref name="tag"/>
    /// (format and size of what was transcoded) and length; null otherwise.</summary>
    public byte[]? TryGet(ulong key, ulong tag, int expectedLength)
    {
        string p = PathOf(key);
        try
        {
            byte[] data;
            lock (_lock)
            {
                if (!File.Exists(p)) { Misses++; return null; }
                data = File.ReadAllBytes(p);
                try { File.SetLastWriteTimeUtc(p, DateTime.UtcNow); } catch (IOException) { }
            }
            if (data.Length < HeaderSize || BinaryPrimitives.ReadUInt32LittleEndian(data) != Magic
                || BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4)) != data.Length - HeaderSize || BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(8)) != key)
            {
                // torn or foreign file: drop it so the next Put can write the entry again
                lock (_lock) { try { File.Delete(p); _bytes = -1; } catch (IOException) { } }
                Misses++;
                return null;
            }
            if (data.Length != HeaderSize + expectedLength || BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(16)) != tag)
            {
                Misses++;
                return null;
            }
            Hits++;
            return data[HeaderSize..];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Misses++;
            return null;
        }
    }

    public void Put(ulong key, ulong tag, ReadOnlySpan<byte> payload)
    {
        var data = new byte[HeaderSize + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(data, Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), (uint)payload.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(8), key);
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(16), tag);
        payload.CopyTo(data.AsSpan(HeaderSize));
        string p = PathOf(key);
        try
        {
            lock (_lock)
            {
                System.IO.Directory.CreateDirectory(_dir);
                if (_bytes < 0) _bytes = Measure();
                if (File.Exists(p)) return;
                // written in place (a rename right after the write fails while a virus scanner holds the new file);
                // a torn file fails the length/magic check on read and is deleted there
                File.WriteAllBytes(p, data);
                _bytes += data.Length;
                Writes++;
                if (_bytes > _limit) Trim();
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.WarnOnce("astc-cache-write", $"[texcache] cannot write {p}: {e.Message}");
        }
    }

    long Measure()
    {
        long n = 0;
        foreach (var f in new DirectoryInfo(_dir).EnumerateFiles("*.astc")) n += f.Length;
        return n;
    }

    /// <summary>Deletes the least recently used entries down to 90% of the limit.</summary>
    void Trim()
    {
        var files = new DirectoryInfo(_dir).GetFiles("*.astc").OrderBy(f => f.LastWriteTimeUtc).ToList();
        long total = files.Sum(f => f.Length);
        long target = _limit * 9 / 10;
        foreach (var f in files)
        {
            if (total <= target) break;
            long length = f.Length; // (read first: a deleted FileInfo no longer knows it)
            try { f.Delete(); total -= length; Evictions++; }
            catch (IOException) { }
        }
        _bytes = total;
    }

    public long Bytes { get { lock (_lock) return _bytes < 0 ? (_bytes = System.IO.Directory.Exists(_dir) ? Measure() : 0) : _bytes; } }

    public string Summary() => $"disk cache {Hits} hits, {Misses} misses, {Writes} written, {Evictions} evicted, {Bytes / 1048576.0:F1} of {_limit / 1048576} MB";
}
