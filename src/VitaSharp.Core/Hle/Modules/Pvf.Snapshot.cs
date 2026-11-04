using VitaSharp.Core.Hle.Font;
using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>Save states: font libraries and their open fonts with size and style: system fonts by index, fonts the
/// game opened from its memory or files with their file contents.</summary>
[GenerateSnapshot, SnapshotIgnoreFields("GuestCallOverride", "SystemFontOverride", "_fonts", "_libs")]
public sealed partial class Pvf
{
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
                    w.Write(f == null ? -2 : f.SystemIndex);
                    if (f == null) continue;
                    if (f.SystemIndex < 0) { w.Write(f.Face.FileBytes.ToArray()); w.Write(f.Face.SubIndex); }
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
                    if (idx == -2) continue;
                    var face = idx >= 0
                        ? SystemFace(idx, out _) ?? throw new SnapshotException($"system font {SystemFonts[idx].File} cannot be read")
                        : PvfFace.Open(r.ReadBytes() ?? [], r.ReadInt32()) ?? throw new SnapshotException("a game font cannot be read again");
                    var f = new OpenFont { Lib = l, Face = face, OwnsFace = idx < 0, Slot = s, SystemIndex = idx, Handle = l.FontTable + (uint)s * FontEntrySize };
                    f.CharH = r.ReadSingle(); f.CharV = r.ReadSingle(); f.Embolden = r.ReadSingle(); f.SkewX = r.ReadSingle(); f.SkewY = r.ReadSingle();
                    l.Slots[s] = f;
                    _fonts[f.Handle] = f;
                }
                _libs[l.Handle] = l;
            }
        }
    }
}
