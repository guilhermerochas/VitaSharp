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

    /// <summary>Host functions of tables a module builds on first use and again after every load (the host vnodes'
    /// op table: <see cref="VitaSharp.Core.Hle.Modules.IoFileMgr.HostVnodeStubPrefix"/>): a session booted from a state has not built
    /// them yet when the function table loads, and nothing calls the saved ones.</summary>
    internal static bool RebuiltAfterLoad(string name) => name.StartsWith(VitaSharp.Core.Hle.Modules.IoFileMgr.HostVnodeStubPrefix + ":", StringComparison.Ordinal);

    internal void LoadState(SnapshotReader r)
    {
        lock (_functions)
        {
            int n = r.ReadInt32();
            // by name (a shared host function can sit at more than one index: a state loaded over one that had it)
            var hostByName = _functions.Where(f => f.Nid == 0 && f.Library == "host").GroupBy(f => f.Name).ToDictionary(g => g.Key, g => g.First());
            var list = new List<HleFunction>(n) { _functions[0] };
            var index = new Dictionary<uint, int>();
            var nids = new List<uint>(n) { 0 };
            var shared = new List<(int Index, string Name)>();
            for (int i = 1; i < n; i++)
            {
                uint nid = r.ReadUInt32();
                string name = r.ReadStringNotNull(), lib = r.ReadStringNotNull();
                nids.Add(nid);
                if (nid == 0 && lib == "host")
                {
                    if (hostByName.GetValueOrDefault(name) is { } host)
                    {
                        list.Add(host);
                        if (Indexes.SharedHostByName(name, out _))
                        {
                            // every process serves it at the saved index too
                            shared.Add((list.Count - 1, name));
                        }
                    }
                    else if (Indexes.SharedHostByName(name, out Action<HleContext> body))
                    {
                        // a host function every process serves, declared but not yet given an index in this session
                        list.Add(SharedHostRecord(name, body));
                        shared.Add((list.Count - 1, name));
                    }
                    else if (RebuiltAfterLoad(name))
                    {
                        // a table its module builds on first use and builds again after a load, pointing the guest
                        // objects at the new one: the saved index is never called (a session booted from the state)
                        list.Add(s_unbound);
                    }
                    else
                    {
                        throw new SnapshotException($"host function {name} of the saved session does not exist here");
                    }

                    continue;
                }

                if (nid == 0)
                {
                    list.Add(s_unbound); // a slot another process of the saved session used
                    continue;
                }
                // the existing record when there is one (call counts and warnings carry on)
                // a new record has its declaration (or its library's class) as GetHleFunctionIndex gives it
                var f = _indexByNid.TryGetValue(nid, out var cur) ? _functions[cur] : NewFunction(nid, name, lib);
                list.Add(f);
                index[nid] = list.Count - 1;
            }
            _functions.Clear();
            _functions.AddRange(list);
            _indexByNid.Clear();
            foreach (var (k, v) in index) _indexByNid[k] = v;
            // the saved session's processes shared one table: it is this session's from now on (a state of a build
            // whose processes each had their own table keeps this one apart, as that build ran it)
            if (Indexes.Adopt(nids))
            {
                foreach ((int at, string name) in shared)
                {
                    Indexes.AliasSharedHost(at, name);
                }
            }
        }
        lock (_vars)
        {
            _vars.Clear();
            int n = r.ReadInt32();
            for (int i = 0; i < n; i++) _vars[r.ReadUInt32()] = r.ReadUInt32();
        }
    }
}
