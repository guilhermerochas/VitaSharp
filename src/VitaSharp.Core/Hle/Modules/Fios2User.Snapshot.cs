using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>Save states: the overlay table (per-thread "overlays disabled" flags are not kept).</summary>
[GenerateSnapshot, SnapshotIgnoreFields("_overlays", "_threadDisabled")]
public sealed partial class Fios2User
{
    void SaveExtra(SnapshotWriter w)
    {
        lock (_overlays)
        {
            w.Write(_overlays.Count);
            foreach (var o in _overlays) { w.Write(o.Id); w.Write(o.Type); w.Write(o.Order); w.Write(o.Dst); w.Write(o.Src); }
        }
    }

    void LoadExtra(SnapshotReader r)
    {
        lock (_overlays)
        {
            _overlays.Clear();
            int n = r.ReadInt32();
            for (int i = 0; i < n; i++) _overlays.Add(new Overlay(r.ReadInt32(), r.ReadByte(), r.ReadByte(), r.ReadStringNotNull(), r.ReadStringNotNull()));
        }
    }
}
