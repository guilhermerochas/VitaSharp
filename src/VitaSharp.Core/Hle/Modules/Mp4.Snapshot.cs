using VitaSharp.Core.Media;
using VitaSharp.Core.SaveState;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>Save states: open movies are reopened on the host and continue right after the last unit the game read
/// (<see cref="SeekPast"/>).</summary>
[GenerateSnapshot, SnapshotIgnoreFields("_files")]
public sealed partial class Mp4
{
    sealed partial class File
    {
        public string? HostPath;
    }

    void SaveExtra(SnapshotWriter w)
    {
        lock (_files)
        {
            w.Write(_files.Count);
            foreach (var (h, f) in _files)
            {
                w.Write(h); w.Write(f.HostPath); w.Write(f.TempPath); w.Write(f.Buffer); w.Write(f.BufferSize);
                w.Write(f.Enabled.Count); foreach (var s in f.Enabled) w.Write(s);
                w.Write(f.Last != null);
                if (f.Last is { } u) { w.Write(u.Stream); w.Write(u.Data); w.Write(u.Pts90k); w.Write(u.Dts90k); w.Write(u.Key); }
            }
        }
    }

    /// <summary>
    /// Seeks the reopened demuxer to the key frame before <paramref name="last"/> and reads on up to that unit, so the
    /// game's next GetNextUnit returns the unit that follows it, as it would have without the save. Restarting at the
    /// key frame sent the game units it already had (older pts than its movie clock): the picture stood still for
    /// seconds while its player threw them away (Ninja Gaiden Sigma 2 Plus's intro). The video decoder keeps its
    /// reference pictures across the load (Videodec.Snapshot.cs). False (back at the key frame) when the unit is not met.
    /// </summary>
    static bool SeekPast(IMp4Demuxer demux, DemuxedUnit last, HashSet<int> enabled)
    {
        demux.Seek(last.Pts90k);
        for (int i = 0; i < 20000; i++)
        {
            var u = demux.Next(enabled.Contains);
            if (u == null) break;
            if (u.Stream == last.Stream && u.Dts90k == last.Dts90k && u.Pts90k == last.Pts90k && u.Data.Length == last.Data.Length) return true;
        }
        demux.Seek(last.Pts90k);
        return false;
    }

    void LoadExtra(SnapshotReader r)
    {
        lock (_files)
        {
            foreach (var f in _files.Values) f.Demux.Dispose();
            _files.Clear();
            int n = r.ReadInt32();
            for (int i = 0; i < n; i++)
            {
                int h = r.ReadInt32();
                string? host = r.ReadString();
                string temp = r.ReadStringNotNull();
                uint buf = r.ReadUInt32(), size = r.ReadUInt32();
                var enabled = new HashSet<int>();
                int c = r.ReadInt32();
                for (int k = 0; k < c; k++) enabled.Add(r.ReadInt32());
                DemuxedUnit? last = r.ReadBool() ? new DemuxedUnit(r.ReadInt32(), r.ReadBytes() ?? [], r.ReadInt64(), r.ReadInt64(), r.ReadBool()) : null;
                var demux = host != null && System.IO.File.Exists(host) ? Mp4Demuxers.Open(host) : null;
                if (demux == null) throw new SnapshotException($"the movie {host} of the saved session cannot be opened");
                bool resumed = last != null && SeekPast(demux, last, enabled);
                var f = new File { Demux = demux, TempPath = temp, HostPath = host, Buffer = buf, BufferSize = size, Last = last };
                foreach (var s in enabled) f.Enabled.Add(s);
                _files[h] = f;
                Log.Info($"Load state: movie {Path.GetFileName(host)} reopened at {(last?.Pts90k ?? 0) / 90000.0:F2} s{(last == null ? "" : resumed ? ", right after the last unit read" : ", at the key frame before it")}");
            }
        }
    }
}
