using System.Globalization;

namespace VitaSharp.Core.Hle;

public sealed record NidInfo(uint Nid, string Module, string Library, string Name);

/// <summary>NID ↔ symbol name database (generated from Vita3K's nids.inc, embedded as nids.txt).</summary>
public static class NidDatabase
{
    static readonly Dictionary<uint, NidInfo> ByNid = new();
    static readonly Dictionary<string, List<NidInfo>> ByName = new();

    static NidDatabase()
    {
        using var s = typeof(NidDatabase).Assembly.GetManifestResourceStream("nids.txt")
                      ?? throw new InvalidOperationException("nids.txt resource missing");
        using var r = new StreamReader(s);
        while (r.ReadLine() is { } line)
        {
            var p = line.Split(' ');
            if (p.Length < 4) continue;
            uint nid = uint.Parse(p[0].AsSpan(2), NumberStyles.HexNumber);
            var info = new NidInfo(nid, p[1], p[2], p[3]);
            ByNid.TryAdd(nid, info);
            if (!ByName.TryGetValue(p[3], out var list)) ByName[p[3]] = list = new();
            list.Add(info);
        }
    }

    public static NidInfo? Lookup(uint nid) => ByNid.GetValueOrDefault(nid);
    public static string NameOf(uint nid) => ByNid.TryGetValue(nid, out var i) ? i.Name : $"NID_{nid:X8}";
    public static IReadOnlyList<NidInfo> LookupName(string name) => ByName.TryGetValue(name, out var l) ? l : [];
    public static int Count => ByNid.Count;

    /// <summary>NID computation for symbols not present in the database: SHA1(name + suffix) first word, little endian.</summary>
    public static uint ComputeNid(string name, ReadOnlySpan<byte> suffix)
    {
        var bytes = System.Text.Encoding.ASCII.GetBytes(name).Concat(suffix.ToArray()).ToArray();
        var hash = System.Security.Cryptography.SHA1.HashData(bytes);
        return BitConverter.ToUInt32(hash, 0);
    }
}
