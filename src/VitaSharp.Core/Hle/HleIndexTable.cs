namespace VitaSharp.Core.Hle;

/// <summary>
/// The SVC numbers of the HLE functions, one table for every process of an emulated Vita. An import stub is
/// "svc #index; bx lr; nid", and kernel memory (the kernel modules' import stubs, the HLE kernel exports, the trampolines
/// a plugin's hook copies from them) is shared by every process and runs on threads of any of them: an index must name
/// the same function in each process's <see cref="HleRegistry"/>, whatever order each one binds its imports in.
/// Index 0 is no function; a host function (<see cref="HleRegistry.RegisterHostFunction"/>) gets an index of its own,
/// with NID 0.
/// </summary>
public sealed class HleIndexTable
{
    private readonly object _lock = new();

    /// <summary>The NID of each index (0: none, or a host function).</summary>
    private readonly List<uint> _nids = [0];
    private readonly Dictionary<uint, int> _indexByNid = new();

    /// <summary>The index of <paramref name="nid"/>, given out on its first use in any process.</summary>
    public int IndexOf(uint nid)
    {
        lock (_lock)
        {
            if (_indexByNid.TryGetValue(nid, out int index))
            {
                return index;
            }

            _nids.Add(nid);
            _indexByNid[nid] = _nids.Count - 1;
            return _nids.Count - 1;
        }
    }

    /// <summary>A new index for a host function.</summary>
    public int NewHostIndex()
    {
        lock (_lock)
        {
            _nids.Add(0);
            return _nids.Count - 1;
        }
    }

    /// <summary>The host functions every process of the system serves at one index (code in shared kernel memory that
    /// enters C#, such as the plugin hooks' counting stubs), by index.</summary>
    private readonly Dictionary<int, (string Name, Action<HleContext> Body)> _sharedHost = new();

    /// <summary>A new index for a host function that every process's registry serves (each binds it on its first
    /// call, <see cref="SharedHostFunction"/>).</summary>
    public int NewSharedHostIndex(string name, Action<HleContext> body)
    {
        lock (_lock)
        {
            _nids.Add(0);
            int index = _nids.Count - 1;
            _sharedHost[index] = (name, body);
            return index;
        }
    }

    /// <summary>The shared host function at <paramref name="index"/> (<see cref="NewSharedHostIndex"/>), if any.</summary>
    public bool SharedHostFunction(uint index, out string name, out Action<HleContext> body)
    {
        lock (_lock)
        {
            if (_sharedHost.TryGetValue((int)index, out (string Name, Action<HleContext> Body) entry))
            {
                name = entry.Name;
                body = entry.Body;
                return true;
            }

            name = "";
            body = null!;
            return false;
        }
    }

    /// <summary>The NID index <paramref name="index"/> was given to (0: none or a host function).</summary>
    public uint NidAt(uint index)
    {
        lock (_lock)
        {
            return index < (uint)_nids.Count ? _nids[(int)index] : 0;
        }
    }

    /// <summary>
    /// After a save state load: takes the saved table (<paramref name="nids"/>, index to NID, 0 for host functions and
    /// empty slots) when it agrees with every index this session already gave out, so the restored stubs and the
    /// functions bound later share one table. Returns false (nothing changed) when it does not agree: a state saved
    /// by a build whose processes each had their own table.
    /// </summary>
    public bool Adopt(IReadOnlyList<uint> nids)
    {
        lock (_lock)
        {
            int common = Math.Min(nids.Count, _nids.Count);
            for (int i = 1; i < common; i++)
            {
                if (_nids[i] != nids[i])
                {
                    return false;
                }
            }

            for (int i = 1; i < nids.Count; i++)
            {
                if (nids[i] != 0 && _indexByNid.TryGetValue(nids[i], out int given) && given != i)
                {
                    return false;
                }
            }

            for (int i = _nids.Count; i < nids.Count; i++)
            {
                _nids.Add(nids[i]);
                if (nids[i] != 0)
                {
                    _indexByNid[nids[i]] = i;
                }
            }

            return true;
        }
    }
}
