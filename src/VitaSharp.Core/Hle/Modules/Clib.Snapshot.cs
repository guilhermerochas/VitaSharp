using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>Save states: the mspace heaps; the allocation-to-heap index refers to them by heap base.</summary>
[GenerateSnapshot, SnapshotIgnoreFields("_owner")]
public sealed partial class Clib
{
    [GenerateSnapshot]
    sealed partial class Heap;

    void SaveExtra(SnapshotWriter w)
    {
        lock (_heaps)
        {
            var key = new Dictionary<Heap, uint>(ReferenceEqualityComparer.Instance);
            foreach (var (k, v) in _heaps) key[v] = k;
            var owned = _owner.Where(kv => key.ContainsKey(kv.Value)).ToList();
            w.Write(owned.Count);
            foreach (var (p, h) in owned) { w.Write(p); w.Write(key[h]); }
        }
    }

    void LoadExtra(SnapshotReader r)
    {
        lock (_heaps)
        {
            _owner.Clear();
            int n = r.ReadInt32();
            for (int i = 0; i < n; i++)
            {
                uint p = r.ReadUInt32(), h = r.ReadUInt32();
                if (_heaps.TryGetValue(h, out var heap)) _owner[p] = heap;
            }
        }
    }
}
