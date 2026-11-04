using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle;

/// <summary>
/// Save states: the SVC numbers in the guest's import stubs index this registry's function table, which is filled in
/// link order. The table and the HLE variables (guest addresses handed out on first import) are saved and restored
/// as they were, so the restored stubs call the same functions in any session.
/// </summary>
public sealed partial class HleRegistry
{
    readonly Dictionary<HleModule, List<uint>> _nidsByModule = new();

    void NoteModuleNid(HleModule module, uint nid)
    {
        if (!_nidsByModule.TryGetValue(module, out var l)) _nidsByModule[module] = l = new();
        l.Add(nid);
    }

    /// <summary>True when the guest called any function of <paramref name="module"/>.</summary>
    internal bool WasUsed(HleModule module)
    {
        if (!_nidsByModule.TryGetValue(module, out var nids)) return false;
        lock (_functions)
            foreach (var nid in nids)
                if (_indexByNid.TryGetValue(nid, out var i) && _functions[i].CallCount > 0) return true;
        return false;
    }

    internal void SaveState(SnapshotWriter w)
    {
        lock (_functions)
        {
            w.Write(_functions.Count);
            for (int i = 1; i < _functions.Count; i++)
            {
                var f = _functions[i];
                w.Write(f.Nid); w.Write(f.Name); w.Write(f.Library);
            }
        }
        lock (_vars)
        {
            w.Write(_vars.Count);
            foreach (var (nid, a) in _vars) { w.Write(nid); w.Write(a); }
        }
    }

    internal void LoadState(SnapshotReader r)
    {
        lock (_functions)
        {
            int n = r.ReadInt32();
            var hostByName = _functions.Where(f => f.Nid == 0 && f.Library == "host").ToDictionary(f => f.Name);
            var list = new List<HleFunction>(n) { _functions[0] };
            var index = new Dictionary<uint, int>();
            for (int i = 1; i < n; i++)
            {
                uint nid = r.ReadUInt32();
                string name = r.ReadStringNotNull(), lib = r.ReadStringNotNull();
                if (nid == 0 && lib == "host")
                {
                    list.Add(hostByName.GetValueOrDefault(name) ?? throw new SnapshotException($"host function {name} of the saved session does not exist here"));
                    continue;
                }
                // the existing record when there is one (call counts and warnings carry on)
                var f = _indexByNid.TryGetValue(nid, out var cur) ? _functions[cur] : new HleFunction { Nid = nid, Name = name, Library = lib, Invoke = _impls.GetValueOrDefault(nid) };
                list.Add(f);
                index[nid] = list.Count - 1;
            }
            _functions.Clear();
            _functions.AddRange(list);
            _indexByNid.Clear();
            foreach (var (k, v) in index) _indexByNid[k] = v;
        }
        lock (_vars)
        {
            _vars.Clear();
            int n = r.ReadInt32();
            for (int i = 0; i < n; i++) _vars[r.ReadUInt32()] = r.ReadUInt32();
        }
    }
}
