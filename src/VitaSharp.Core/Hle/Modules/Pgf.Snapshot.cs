using VitaSharp.Core.Hle.Font;
using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>Save states: font libraries and the system fonts open in them (parsed again from the firmware on load).
/// A font the game opened from its own memory or file is refused for now.</summary>
[GenerateSnapshot, SnapshotIgnoreFields("GuestCallOverride", "SystemFontOverride", "_fonts", "_libs")]
public sealed partial class Pgf : ISnapshotState
{
    string? ISnapshotState.CheckSnapshot()
    {
        lock (_lock) return _fonts.Values.Any(f => f.SystemIndex < 0) ? "host resource PGF font opened from game data not supported yet" : null;
    }

    void SaveExtra(SnapshotWriter w)
    {
        lock (_lock)
        {
            w.Write(_libs.Count);
            foreach (var l in _libs.Values)
            {
                w.Write(l.Handle); w.Write(l.UserData); w.Write(l.NumFonts); w.Write(l.Alloc); w.Write(l.Free); w.Write(l.OpenFunc); w.Write(l.CloseFunc);
                w.Write(l.ReadFunc); w.Write(l.SeekFunc); w.Write(l.FontTable); w.Write(l.InternalTable); w.Write(l.HRes); w.Write(l.VRes); w.Write(l.AltChar);
                w.Write(l.Slots.Length);
                foreach (var f in l.Slots) w.Write(f?.SystemIndex ?? -1);
            }
        }
    }

    void LoadExtra(SnapshotReader r)
    {
        lock (_lock)
        {
            _libs.Clear(); _fonts.Clear();
            int n = r.ReadInt32();
            for (int i = 0; i < n; i++)
            {
                var l = new FontLib { Handle = r.ReadUInt32(), UserData = r.ReadUInt32(), NumFonts = r.ReadUInt32(), Alloc = r.ReadUInt32(), Free = r.ReadUInt32(), OpenFunc = r.ReadUInt32(), CloseFunc = r.ReadUInt32() };
                l.ReadFunc = r.ReadUInt32(); l.SeekFunc = r.ReadUInt32(); l.FontTable = r.ReadUInt32(); l.InternalTable = r.ReadUInt32();
                l.HRes = r.ReadSingle(); l.VRes = r.ReadSingle(); l.AltChar = r.ReadUInt16();
                l.Slots = new OpenFont?[r.ReadInt32()];
                for (int s = 0; s < l.Slots.Length; s++)
                {
                    int idx = r.ReadInt32();
                    if (idx < 0) continue;
                    string key = SystemFontDir + SystemFonts[idx].File;
                    var font = s_files.TryGetValue(key, out var c) && c != null ? c : ReadVfs(key) is { } data ? PgfFont.Parse(data) : null;
                    if (font == null) throw new SnapshotException($"system font {key} cannot be read");
                    s_files[key] = font;
                    var f = new OpenFont { Lib = l, Font = font, Slot = s, SystemIndex = idx, Handle = l.FontTable + (uint)s * FontEntrySize };
                    l.Slots[s] = f;
                    _fonts[f.Handle] = f;
                }
                _libs[l.Handle] = l;
            }
        }
    }
}
