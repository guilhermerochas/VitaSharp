using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>Save states: a trophy context is its communication id; the set (and what is unlocked, which is the
/// user's, not the machine's) is read again from the game and the trophy data.</summary>
[GenerateSnapshot, SnapshotIgnoreFields("_contexts", "_bridge")]
public sealed partial class NpTrophy
{
    void SaveExtra(SnapshotWriter w)
    {
        lock (_contexts)
        {
            w.Write(_contexts.Count);
            foreach (var (id, set) in _contexts) { w.Write(id); w.Write(set.CommId); }
        }
    }

    void LoadExtra(SnapshotReader r)
    {
        lock (_contexts)
        {
            _contexts.Clear();
            int n = r.ReadInt32();
            for (int i = 0; i < n; i++)
            {
                int id = r.ReadInt32();
                string comm = r.ReadStringNotNull();
                if (Load(comm) is { } set) _contexts[id] = set;
                else throw new SnapshotException($"trophy set {comm} cannot be read");
            }
        }
    }
}
