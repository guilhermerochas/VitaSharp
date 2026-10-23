using System.Buffers.Binary;
using System.IO.Hashing;
using Microsoft.Win32.SafeHandles;

namespace VitaSharp.Core.Util;

/// <summary>
/// Append-only key → blob store in a single file (one file per cache instead of thousands of small ones, which is
/// much cheaper on mobile storage). Only the index (key → offset) lives in memory; blobs are read on demand.
/// Every record carries its own checksum, so a write torn by a crash or a killed process only loses that record:
/// the truncated tail is cut off on the next open and later appends stay aligned.
/// Thread-safe: reads are positional and may run concurrently with an append.
/// <para>Concurrent sessions: the first process holds the file for writing; another process opening it gets a
/// read-only view (<see cref="IsReadOnly"/>): it reads what was complete at open time, never truncates or resets the
/// file (a version mismatch just reads as empty), and its <see cref="Add"/> calls are no-ops.</para>
/// </summary>
public sealed class BlobPack : IDisposable
{
    const uint FileMagic = 0x50535356; // "VSSP"
    const uint RecordMagic = 0x31434552; // "REC1"
    const int FileHeaderSize = 16, RecordHeaderSize = 24;
    const int MaxBlob = 16 << 20;

    readonly FileStream _fs;
    readonly SafeFileHandle _h;
    readonly Dictionary<ulong, (long Offset, int Length)> _index = new();
    readonly List<ulong> _order = new();
    readonly object _lock = new();
    long _end;
    bool _disposed;

    public string Path { get; }
    public int Count { get { lock (_lock) return _index.Count; } }
    /// <summary>Another process holds the file for writing: this view only reads.</summary>
    public bool IsReadOnly { get; }

    BlobPack(string path, FileStream fs, bool readOnly)
    {
        Path = path;
        _fs = fs;
        _h = fs.SafeFileHandle;
        IsReadOnly = readOnly;
    }

    /// <summary>Opens (or creates) a pack. A pack written with another <paramref name="version"/> is discarded.
    /// Falls back to a read-only view when another process holds the file; returns null when it cannot be opened at
    /// all (read-only storage).</summary>
    public static BlobPack? Open(string path, uint version)
    {
        FileStream fs;
        bool readOnly = false;
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, 1, FileOptions.RandomAccess);
        }
        catch (IOException e) when (File.Exists(path))
        {
            // sharing violation: another session writes this pack
            try { fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1, FileOptions.RandomAccess); readOnly = true; }
            catch (Exception e2) { Log.Warn($"[shadercache] cannot open {path}: {e.Message} / {e2.Message}"); return null; }
            Log.Info($"[shadercache] {System.IO.Path.GetFileName(path)} is in use by another session: read-only");
        }
        catch (Exception e) { Log.Warn($"[shadercache] cannot open {path}: {e.Message}"); return null; }
        var pack = new BlobPack(path, fs, readOnly);
        try { pack.Load(version); }
        catch (Exception e)
        {
            Log.Warn($"[shadercache] {path} unreadable ({e.Message}), starting over");
            if (readOnly) { pack._index.Clear(); pack._order.Clear(); return pack; }
            try { pack.Reset(version); } catch { pack.Dispose(); return null; }
        }
        return pack;
    }

    /// <summary>The version a pack file was written with, or null (missing, unreadable, not a pack). Never modifies it.</summary>
    public static uint? PeekVersion(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1);
            Span<byte> hdr = stackalloc byte[FileHeaderSize];
            if (fs.Read(hdr) != FileHeaderSize || BinaryPrimitives.ReadUInt32LittleEndian(hdr) != FileMagic) return null;
            return BinaryPrimitives.ReadUInt32LittleEndian(hdr[4..]);
        }
        catch (Exception) { return null; }
    }

    void Load(uint version)
    {
        long len = RandomAccess.GetLength(_h);
        Span<byte> hdr = stackalloc byte[RecordHeaderSize];
        if (len < FileHeaderSize) { if (!IsReadOnly) Reset(version); return; }
        RandomAccess.Read(_h, hdr[..FileHeaderSize], 0);
        if (BinaryPrimitives.ReadUInt32LittleEndian(hdr) != FileMagic || BinaryPrimitives.ReadUInt32LittleEndian(hdr[4..]) != version)
        {
            if (!IsReadOnly) Reset(version);
            return;
        }
        long pos = FileHeaderSize;
        while (pos + RecordHeaderSize <= len)
        {
            if (RandomAccess.Read(_h, hdr, pos) != RecordHeaderSize) break;
            uint magic = BinaryPrimitives.ReadUInt32LittleEndian(hdr);
            int size = BinaryPrimitives.ReadInt32LittleEndian(hdr[4..]);
            ulong key = BinaryPrimitives.ReadUInt64LittleEndian(hdr[8..]);
            if (magic != RecordMagic || size < 0 || size > MaxBlob || pos + RecordHeaderSize + size > len) break;
            if (_index.TryAdd(key, (pos + RecordHeaderSize, size))) _order.Add(key);
            else _index[key] = (pos + RecordHeaderSize, size);
            pos += RecordHeaderSize + size;
        }
        if (pos != len && !IsReadOnly) // (read-only: the writer may be appending that tail right now)
        {
            Log.Warn($"[shadercache] {System.IO.Path.GetFileName(Path)}: dropped {len - pos} bytes of a torn record");
            _fs.SetLength(pos);
        }
        _end = pos;
    }

    void Reset(uint version)
    {
        _index.Clear();
        _order.Clear();
        _fs.SetLength(0);
        Span<byte> hdr = stackalloc byte[FileHeaderSize];
        hdr.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(hdr, FileMagic);
        BinaryPrimitives.WriteUInt32LittleEndian(hdr[4..], version);
        RandomAccess.Write(_h, hdr, 0);
        _end = FileHeaderSize;
    }

    public bool Contains(ulong key) { lock (_lock) return _index.ContainsKey(key); }

    /// <summary>Keys in insertion order (first-seen first).</summary>
    public ulong[] Keys() { lock (_lock) return _order.ToArray(); }

    /// <summary>Reads and verifies a blob; a corrupted record counts as missing.</summary>
    public byte[]? TryRead(ulong key)
    {
        long offset; int length;
        lock (_lock)
        {
            if (_disposed || !_index.TryGetValue(key, out var e)) return null;
            (offset, length) = e;
        }
        try
        {
            Span<byte> hdr = stackalloc byte[RecordHeaderSize];
            if (RandomAccess.Read(_h, hdr, offset - RecordHeaderSize) != RecordHeaderSize) return null;
            var data = new byte[length];
            if (RandomAccess.Read(_h, data, offset) != length) return null;
            if (XxHash64.HashToUInt64(data) != BinaryPrimitives.ReadUInt64LittleEndian(hdr[16..])) return null;
            return data;
        }
        catch (ObjectDisposedException) { return null; }
        catch (IOException) { return null; }
    }

    /// <summary>Appends a blob (no-op when the key is already stored).</summary>
    public void Add(ulong key, ReadOnlySpan<byte> data)
    {
        if (data.Length > MaxBlob || IsReadOnly) return;
        lock (_lock)
        {
            if (_disposed || _index.ContainsKey(key)) return;
            Span<byte> hdr = stackalloc byte[RecordHeaderSize];
            BinaryPrimitives.WriteUInt32LittleEndian(hdr, RecordMagic);
            BinaryPrimitives.WriteInt32LittleEndian(hdr[4..], data.Length);
            BinaryPrimitives.WriteUInt64LittleEndian(hdr[8..], key);
            BinaryPrimitives.WriteUInt64LittleEndian(hdr[16..], XxHash64.HashToUInt64(data));
            try
            {
                // payload first, header last: a crash in between leaves a tail that fails the magic check
                RandomAccess.Write(_h, data, _end + RecordHeaderSize);
                RandomAccess.Write(_h, hdr, _end);
            }
            catch (Exception e)
            {
                Log.WarnOnce("blobpack-write", $"[shadercache] write to {System.IO.Path.GetFileName(Path)} failed: {e.Message}");
                return;
            }
            _index[key] = (_end + RecordHeaderSize, data.Length);
            _order.Add(key);
            _end += RecordHeaderSize + data.Length;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _fs.Dispose();
        }
    }
}
