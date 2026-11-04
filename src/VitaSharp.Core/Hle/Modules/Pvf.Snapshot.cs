using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>Save states: font libraries and the system fonts open in them with their size and style; a font the game
/// opened from its own memory or file is refused for now.</summary>
[GenerateSnapshot, SnapshotIgnoreFields("GuestCallOverride", "SystemFontOverride", "_fonts", "_libs")]
public sealed partial class Pvf : ISnapshotState
{
    string? ISnapshotState.CheckSnapshot()
    {
        lock (_lock) return _fonts.Values.Any(f => f.SystemIndex < 0) ? "host resource PVF font opened from game data not supported yet" : null;
    }

    void SaveExtra(SnapshotWriter w)
    {
        lock (_lock)
        {
            w.Write(_libs.Count);
            foreach (var l in _libs.Values)
            {
                w.Write(l.Handle); w.Write(l.UserData); w.Write(l.MaxFonts); w.Write(l.Alloc); w.Write(l.Realloc); w.Write(l.Free);
                w.Write(l.FontTable); w.Write(l.InternalTable); w.Write(l.HRes); w.Write(l.VRes); w.Write(l.Em); w.Write(l.AltChar);
                w.Write(l.Slots.Length);
                foreach (var f in l.Slots)
                {
                    w.Write(f?.SystemIndex ?? -1);
                    if (f == null) continue;
                    w.Write(f.CharH); w.Write(f.CharV); w.Write(f.Embolden); w.Write(f.SkewX); w.Write(f.SkewY);
                }
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
                var l = new PvfLib { Handle = r.ReadUInt32(), UserData = r.ReadUInt32(), MaxFonts = r.ReadUInt32(), Alloc = r.ReadUInt32(), Realloc = r.ReadUInt32(), Free = r.ReadUInt32() };
                l.FontTable = r.ReadUInt32(); l.InternalTable = r.ReadUInt32(); l.HRes = r.ReadSingle(); l.VRes = r.ReadSingle(); l.Em = r.ReadSingle(); l.AltChar = r.ReadUInt16();
                l.Slots = new OpenFont?[r.ReadInt32()];
                for (int s = 0; s < l.Slots.Length; s++)
                {
                    int idx = r.ReadInt32();
                    if (idx < 0) continue;
                    var face = SystemFace(idx, out _) ?? throw new SnapshotException($"system font {SystemFonts[idx].File} cannot be read");
                    var f = new OpenFont { Lib = l, Face = face, OwnsFace = false, Slot = s, SystemIndex = idx, Handle = l.FontTable + (uint)s * FontEntrySize };
                    f.CharH = r.ReadSingle(); f.CharV = r.ReadSingle(); f.Embolden = r.ReadSingle(); f.SkewX = r.ReadSingle(); f.SkewY = r.ReadSingle();
                    l.Slots[s] = f;
                    _fonts[f.Handle] = f;
                }
                _libs[l.Handle] = l;
            }
        }
    }
}
