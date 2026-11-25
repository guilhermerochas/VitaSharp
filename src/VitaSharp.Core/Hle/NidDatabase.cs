namespace VitaSharp.Core.Hle;

public sealed record NidInfo(uint Nid, string Module, string Library, string Name);

/// <summary>
/// NID ↔ symbol name database (Vita3K's nids.inc as Hle/Data/nids.txt). The table is made at compile time
/// (VitaSharp.Core.Generators.NidTableGenerator: the entries in the file's order, sorted indices by NID and by name):
/// lookups are binary searches over data of the assembly, and an entry's <see cref="NidInfo"/> is made the first time
/// it is asked for. A NID listed twice answers with its first line; a name answers with every line, in the file's order.
/// </summary>
public static partial class NidDatabase
{
    private static NidInfo?[]? s_infos;

    public static NidInfo? Lookup(uint nid)
    {
        int entry = FirstEntry(nid);
        return entry < 0 ? null : Info(entry);
    }

    public static string NameOf(uint nid)
    {
        int entry = FirstEntry(nid);
        return entry < 0 ? $"NID_{nid:X8}" : Info(entry).Name;
    }

    public static IReadOnlyList<NidInfo> LookupName(string name)
    {
        ReadOnlySpan<ushort> byName = ByName;
        int low = 0;
        int high = byName.Length;
        while (low < high)
        {
            int middle = (low + high) >>> 1;
            if (NameSpan(byName[middle]).SequenceCompareTo(name) < 0)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        int end = low;
        while (end < byName.Length && NameSpan(byName[end]).SequenceEqual(name))
        {
            end++;
        }

        if (end == low)
        {
            return [];
        }

        NidInfo[] found = new NidInfo[end - low];
        for (int i = low; i < end; i++)
        {
            found[i - low] = Info(byName[i]);
        }

        return found;
    }

    /// <summary>The NIDs the database knows (each once).</summary>
    public static int Count => UniqueCount;

    /// <summary>Every NID once (its first line), in the file's order.</summary>
    public static IEnumerable<NidInfo> All
    {
        get
        {
            for (int entry = 0; entry < EntryCount; entry++)
            {
                if (FirstEntry(EntryNid[entry]) == entry)
                {
                    yield return Info(entry);
                }
            }
        }
    }

    /// <summary>NID computation for symbols not present in the database: SHA1(name + suffix) first word, little endian.</summary>
    public static uint ComputeNid(string name, ReadOnlySpan<byte> suffix)
    {
        byte[] bytes = System.Text.Encoding.ASCII.GetBytes(name).Concat(suffix.ToArray()).ToArray();
        byte[] hash = System.Security.Cryptography.SHA1.HashData(bytes);
        return BitConverter.ToUInt32(hash, 0);
    }

    /// <summary>The first line of <paramref name="nid"/> (the index in the file's order), or -1.</summary>
    private static int FirstEntry(uint nid)
    {
        ReadOnlySpan<ushort> byNid = ByNid;
        ReadOnlySpan<uint> nids = EntryNid;
        int low = 0;
        int high = byNid.Length;
        while (low < high)
        {
            int middle = (low + high) >>> 1;
            if (nids[byNid[middle]] < nid)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low < byNid.Length && nids[byNid[low]] == nid ? byNid[low] : -1;
    }

    private static ReadOnlySpan<char> NameSpan(int entry)
    {
        int start = EntryNameStart[entry];
        return NameText.AsSpan(start, EntryNameStart[entry + 1] - start);
    }

    private static NidInfo Info(int entry)
    {
        NidInfo?[]? infos = Volatile.Read(ref s_infos);
        if (infos == null)
        {
            Interlocked.CompareExchange(ref s_infos, new NidInfo?[EntryCount], null);
            infos = s_infos!;
        }

        NidInfo? info = Volatile.Read(ref infos[entry]);
        if (info == null)
        {
            info = new NidInfo(EntryNid[entry], StringAt(EntryModule[entry]), StringAt(EntryLibrary[entry]), NameSpan(entry).ToString());
            Volatile.Write(ref infos[entry], info);
        }

        return info;
    }
}
