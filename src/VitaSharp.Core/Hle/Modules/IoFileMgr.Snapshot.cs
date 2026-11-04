using VitaSharp.Core.SaveState;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// Save states: open files are reopened by path with their access and offset, open directories with their listing
/// and position. File contents are the host's: what the game wrote after the save stays written (as on the console,
/// saved data is not part of the machine state).
/// </summary>
[GenerateSnapshot, SnapshotIgnoreFields("_files", "_dirs", "_fios")]
public sealed partial class IoFileMgr
{
    void SaveExtra(SnapshotWriter w)
    {
        var files = _files.ToArray();
        w.Write(files.Length);
        foreach (var (fd, f) in files)
        {
            w.Write(fd); w.Write(f.VitaPath); w.Write(f.Tty); w.Write(f.Priority);
            w.Write(f.Stream != null);
            if (f.Stream == null) continue;
            w.Write(f.Stream.CanRead); w.Write(f.Stream.CanWrite); w.Write(f.Stream.Position);
        }
        var dirs = _dirs.ToArray();
        w.Write(dirs.Length);
        foreach (var (fd, d) in dirs)
        {
            w.Write(fd); w.Write(d.Entries.Length);
            foreach (var e in d.Entries) w.Write(e);
            w.Write(d.Index);
        }
    }

    void LoadExtra(SnapshotReader r)
    {
        foreach (var f in _files.Values) f.Stream?.Dispose();
        _files.Clear();
        _dirs.Clear();
        int n = r.ReadInt32();
        for (int i = 0; i < n; i++)
        {
            int fd = r.ReadInt32();
            var f = new OpenFile { VitaPath = r.ReadStringNotNull(), Tty = r.ReadInt32(), Priority = r.ReadInt32() };
            if (r.ReadBool())
            {
                bool read = r.ReadBool(), write = r.ReadBool();
                long pos = r.ReadInt64();
                var host = HostPath(f.VitaPath, write);
                var access = read && write ? FileAccess.ReadWrite : write ? FileAccess.Write : FileAccess.Read;
                try
                {
                    if (host == null) throw new FileNotFoundException(f.VitaPath);
                    f.Stream = new FileStream(host, write ? FileMode.OpenOrCreate : FileMode.Open, access, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.RandomAccess);
                    f.Stream.Position = pos;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // the game sees read errors on that handle (its file went away meanwhile)
                    Log.Warn($"Load state: cannot reopen {f.VitaPath}: {e.Message}");
                }
            }
            _files[fd] = f;
        }
        n = r.ReadInt32();
        for (int i = 0; i < n; i++)
        {
            int fd = r.ReadInt32();
            var entries = new string[r.ReadInt32()];
            for (int j = 0; j < entries.Length; j++) entries[j] = r.ReadStringNotNull();
            _dirs[fd] = new OpenDir { Entries = entries, Index = r.ReadInt32() };
        }
    }
}
