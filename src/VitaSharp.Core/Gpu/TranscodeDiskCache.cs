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
    private const uint Magic = 0x54415356; // "VSAT"
    private const int HeaderSize = 24;

    private readonly string _dir;
    private readonly long _limit;
    private readonly object _lock = new();
    private long _bytes = -1;
    public long Hits, Misses, Writes, Evictions;
    /// <summary>The keys on disk (null until the folder was listed once, on a background thread).</summary>
    private HashSet<ulong>? _index;
    private int _indexing;
    /// <summary>
    /// Entries read ahead by <see cref="StartPreload"/>: the checked payload in native memory (outside the GC heap: a
    /// whole title's cache, 110 MB for Killzone, held as managed arrays made a collection during the first aim cost
    /// 810 ms), with its tag. They stay for the session, since the same level is asked for again whenever the in-memory
    /// transcode cache in front dropped it, and are freed by <see cref="ReleasePreloaded"/> when the title's cache is
    /// replaced. Guarded by <see cref="_nativeLock"/>.
    /// </summary>
    private readonly Dictionary<ulong, NativeEntry> _preloaded = new();
    private readonly object _nativeLock = new();
    private bool _released;

    private struct NativeEntry
    {
        public nint Ptr;
        public int Length;
        public ulong Tag;
        public bool Touched;
    }

    private long _preloadedBytes;
    public long Preloaded, PreloadHits;
    /// <summary>Default read-ahead budget: large enough for a whole title's cache seen so far (Killzone: 110 MB) so the
    /// first weapon change finds its levels in memory, not only the most recent ones.</summary>
    public const int DefaultPreloadMb = 160;
    /// <summary>Set when the read-ahead thread ended (tests).</summary>
    public readonly ManualResetEventSlim PreloadDone = new(false);

    public string Directory => _dir;
    public long Limit => _limit;

    public TranscodeDiskCache(string dir, long limitBytes)
    {
        _dir = dir;
        _limit = Math.Max(limitBytes, 1 << 20);
    }

    private string PathOf(ulong key) => Path.Combine(_dir, $"{key:x16}.astc");

    /// <summary>
    /// Whether an entry for <paramref name="key"/> is on disk, from an index of the folder listed once (no file
    /// system call per lookup: on Android's shared storage every call goes through FUSE). Null while the index is not
    /// built yet; the first call starts building it on a thread-pool thread.
    /// </summary>
    public bool? Contains(ulong key)
    {
        lock (_lock)
        {
            if (_index != null)
            {
                return _index.Contains(key);
            }
        }

        if (Interlocked.Exchange(ref _indexing, 1) == 0)
        {
            ThreadPool.UnsafeQueueUserWorkItem(_ => BuildIndex(), null);
        }

        return null;
    }

    /// <summary>Lists the folder into the index (tests call it directly).</summary>
    public void BuildIndex()
    {
        var keys = new HashSet<ulong>();
        try
        {
            if (System.IO.Directory.Exists(_dir))
            {
                foreach (string file in System.IO.Directory.EnumerateFiles(_dir, "*.astc"))
                {
                    if (ulong.TryParse(Path.GetFileNameWithoutExtension(file), System.Globalization.NumberStyles.HexNumber, null, out ulong key))
                    {
                        keys.Add(key);
                    }
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.WarnOnce("astc-cache-index", $"[texcache] cannot list {_dir}: {e.Message}");
        }
        lock (_lock)
        {
            // entries written while the folder was listed are in both
            if (_index != null)
            {
                keys.UnionWith(_index);
            }

            _index = keys;
        }
    }

    /// <summary>
    /// Title start: reads the most recently used entries into memory on a background thread, up to
    /// <paramref name="maxBytes"/>, so the first use of a texture the title has transcoded before costs no file system
    /// call on the GPU thread (on Android's shared storage each one goes through FUSE: a weapon change that brought in
    /// ~100 levels stalled the frame 0.2-0.5 s in texture uploads). Entries stay in memory for the session (up to the
    /// budget) and are dropped with the cache. The content and its checks are those of a file read.
    /// </summary>
    public void StartPreload(long maxBytes)
    {
        if (maxBytes <= 0)
        {
            PreloadDone.Set();
            return;
        }

        var thread = new Thread(() => Preload(maxBytes)) { Name = "ASTC cache read-ahead", IsBackground = true };
        thread.Start();
    }

    private void Preload(long maxBytes)
    {
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        int onDisk = 0;
        try
        {
            if (!System.IO.Directory.Exists(_dir))
            {
                return;
            }

            var files = new List<(DateTime Used, string Path, ulong Key)>();
            foreach (string file in System.IO.Directory.EnumerateFiles(_dir, "*.astc"))
            {
                if (ulong.TryParse(Path.GetFileNameWithoutExtension(file), System.Globalization.NumberStyles.HexNumber, null, out ulong key))
                {
                    files.Add((File.GetLastWriteTimeUtc(file), file, key));
                }
            }

            // most recently used first: what the last sessions played is what this one is most likely to ask for
            files.Sort((a, b) => b.Used.CompareTo(a.Used));
            onDisk = files.Count;
            foreach ((DateTime _, string path, ulong key) in files)
            {
                if (Interlocked.Read(ref _preloadedBytes) >= maxBytes)
                {
                    break;
                }

                ReadAhead(path, key);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.WarnOnce("astc-cache-preload", $"[texcache] cannot read {_dir} ahead: {e.Message}");
        }
        finally
        {
            PreloadDone.Set();
            Log.Info($"[texcache] read ahead {Interlocked.Read(ref Preloaded)} of {onDisk} entries ({Interlocked.Read(ref _preloadedBytes) / 1048576.0:F1} MB, budget {maxBytes >> 20} MB) in {System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds:F0} ms; {Interlocked.Read(ref PreloadHits)} already used while it ran");
        }
    }

    /// <summary>One file into native memory, straight from the file (no managed buffer); a torn or foreign file is
    /// left to the file path of <see cref="TryGet"/>, which deletes it.</summary>
    private unsafe void ReadAhead(string path, ulong key)
    {
        using Microsoft.Win32.SafeHandles.SafeFileHandle handle = File.OpenHandle(path);
        long fileLength = RandomAccess.GetLength(handle);
        if (fileLength < HeaderSize || fileLength > int.MaxValue)
        {
            return;
        }

        Span<byte> header = stackalloc byte[HeaderSize];
        if (RandomAccess.Read(handle, header, 0) != HeaderSize || BinaryPrimitives.ReadUInt32LittleEndian(header) != Magic
            || BinaryPrimitives.ReadUInt32LittleEndian(header[4..]) != fileLength - HeaderSize || BinaryPrimitives.ReadUInt64LittleEndian(header[8..]) != key)
        {
            return;
        }

        int length = (int)(fileLength - HeaderSize);
        nint ptr = (nint)System.Runtime.InteropServices.NativeMemory.Alloc((nuint)Math.Max(length, 1));
        var payload = new Span<byte>((void*)ptr, length);
        int read = 0;
        while (read < length)
        {
            int n = RandomAccess.Read(handle, payload[read..], HeaderSize + read);
            if (n <= 0)
            {
                break;
            }

            read += n;
        }

        lock (_nativeLock)
        {
            if (read == length && !_released && !_preloaded.ContainsKey(key))
            {
                _preloaded[key] = new NativeEntry { Ptr = ptr, Length = length, Tag = BinaryPrimitives.ReadUInt64LittleEndian(header[16..]) };
                _preloadedBytes += fileLength;
                Preloaded++;
                return;
            }
        }

        System.Runtime.InteropServices.NativeMemory.Free((void*)ptr);
    }

    /// <summary>Whether <paramref name="key"/> was read ahead (no file system call).</summary>
    public bool IsPreloaded(ulong key)
    {
        lock (_nativeLock)
        {
            return _preloaded.ContainsKey(key);
        }
    }

    /// <summary>
    /// Copies the read-ahead payload of <paramref name="key"/> into <paramref name="destination"/> when it has the
    /// expected <paramref name="tag"/> and exactly the destination's length: the GPU thread's lookup, with no managed
    /// allocation and no file system call. The first use refreshes the entry's use time (the LRU order) off-thread.
    /// </summary>
    public unsafe bool TryCopyPreloaded(ulong key, ulong tag, Span<byte> destination)
    {
        string? touch = null;
        lock (_nativeLock)
        {
            if (!_preloaded.TryGetValue(key, out NativeEntry e) || e.Tag != tag || e.Length != destination.Length)
            {
                return false;
            }

            new ReadOnlySpan<byte>((void*)e.Ptr, e.Length).CopyTo(destination);
            if (!e.Touched)
            {
                e.Touched = true;
                _preloaded[key] = e;
                touch = PathOf(key);
            }
        }

        Interlocked.Increment(ref Hits);
        Interlocked.Increment(ref PreloadHits);
        Interlocked.Increment(ref FrameLedger.AstcDiskReadAheadHits);
        if (touch != null)
        {
            ThreadPool.UnsafeQueueUserWorkItem(static path =>
            {
                try
                {
                    File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }, touch, false);
        }

        return true;
    }

    /// <summary>Frees the read-ahead entries (the title's cache is being replaced); later reads ahead are dropped.</summary>
    public unsafe void ReleasePreloaded()
    {
        lock (_nativeLock)
        {
            _released = true;
            foreach (NativeEntry e in _preloaded.Values)
            {
                System.Runtime.InteropServices.NativeMemory.Free((void*)e.Ptr);
            }

            _preloaded.Clear();
            _preloadedBytes = 0;
        }
    }

    /// <summary>The cached payload of <paramref name="key"/> when it exists, has the expected <paramref name="tag"/>
    /// (format and size of what was transcoded) and length; null otherwise. A read-ahead entry is copied out of native
    /// memory (the GPU thread uses <see cref="TryCopyPreloaded"/> instead, without the array).</summary>
    public byte[]? TryGet(ulong key, ulong tag, int expectedLength)
    {
        if (IsPreloaded(key))
        {
            byte[] ahead = new byte[expectedLength];
            if (TryCopyPreloaded(key, tag, ahead))
            {
                return ahead;
            }
        }

        string p = PathOf(key);
        try
        {
            byte[] data;
            {
                lock (_lock)
                {
                    if (!File.Exists(p))
                    {
                        Misses++;
                        return null;
                    }
                    data = File.ReadAllBytes(p);
                    try
                    {
                        File.SetLastWriteTimeUtc(p, DateTime.UtcNow);
                    }
                    catch (IOException) { }
                }

                Interlocked.Increment(ref FrameLedger.AstcDiskFileReads);
            }
            if (data.Length < HeaderSize || BinaryPrimitives.ReadUInt32LittleEndian(data) != Magic
                || BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4)) != data.Length - HeaderSize || BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(8)) != key)
            {
                // torn or foreign file: drop it so the next Put can write the entry again
                lock (_lock)
                {
                    try
                    {
                        File.Delete(p);
                        _bytes = -1;
                        _index?.Remove(key);
                    }
                    catch (IOException) { }
                }
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
        byte[] data = new byte[HeaderSize + payload.Length];
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
                if (_bytes < 0)
                {
                    _bytes = Measure();
                }

                if (File.Exists(p))
                {
                    return;
                }
                // written in place (a rename right after the write fails while a virus scanner holds the new file);
                // a torn file fails the length/magic check on read and is deleted there
                File.WriteAllBytes(p, data);
                _bytes += data.Length;
                Writes++;
                _index?.Add(key);
                if (_bytes > _limit)
                {
                    Trim();
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.WarnOnce("astc-cache-write", $"[texcache] cannot write {p}: {e.Message}");
        }
    }

    private long Measure()
    {
        long n = 0;
        foreach (FileInfo f in new DirectoryInfo(_dir).EnumerateFiles("*.astc"))
        {
            n += f.Length;
        }

        return n;
    }

    /// <summary>Deletes the least recently used entries down to 90% of the limit.</summary>
    private void Trim()
    {
        var files = new DirectoryInfo(_dir).GetFiles("*.astc").OrderBy(f => f.LastWriteTimeUtc).ToList();
        long total = files.Sum(f => f.Length);
        long target = _limit * 9 / 10;
        foreach (FileInfo? f in files)
        {
            if (total <= target)
            {
                break;
            }

            long length = f.Length; // (read first: a deleted FileInfo no longer knows it)
            try
            {
                f.Delete();
                total -= length;
                Evictions++;
                if (ulong.TryParse(Path.GetFileNameWithoutExtension(f.Name), System.Globalization.NumberStyles.HexNumber, null, out ulong evicted))
                {
                    _index?.Remove(evicted);
                }
            }
            catch (IOException) { }
        }
        _bytes = total;
    }

    public long Bytes
    {
        get
        {
            lock (_lock)
            {
                return _bytes < 0 ? (_bytes = System.IO.Directory.Exists(_dir) ? Measure() : 0) : _bytes;
            }
        }
    }

    public string Summary() => $"disk cache {Hits} hits ({PreloadHits} read ahead at the title start: {Preloaded} entries read, {Interlocked.Read(ref _preloadedBytes) / 1048576.0:F1} MB held), {Misses} misses, {Writes} written, {Evictions} evicted, {Bytes / 1048576.0:F1} of {_limit / 1048576} MB";
}
