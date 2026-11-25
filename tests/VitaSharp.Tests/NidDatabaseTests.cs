using System.Globalization;
using System.Runtime.CompilerServices;
using VitaSharp.Core.Hle;
using Xunit;

namespace VitaSharp.Tests;

/// <summary>
/// The NID database is a table made at compile time (NidTableGenerator) from Hle/Data/nids.txt: the core carries no
/// text to parse when it starts (the parse took ~45 ms and 10 MB at every start, the most costly static constructor of
/// the core), and the table answers exactly as the text does: a NID with its first line, a name with every line in the
/// file's order, unknown NIDs and names with nothing.
/// </summary>
public sealed class NidDatabaseTests
{
    private static string NidsText([CallerFilePath] string source = "")
    {
        for (DirectoryInfo? directory = new(Path.GetDirectoryName(source)!); directory != null; directory = directory.Parent)
        {
            string path = Path.Combine(directory.FullName, "src", "VitaSharp.Core", "Hle", "Data", "nids.txt");
            if (File.Exists(path))
            {
                return path;
            }
        }

        throw new FileNotFoundException("src/VitaSharp.Core/Hle/Data/nids.txt");
    }

    [Fact]
    public void NothingIsParsedWhenTheCoreStarts()
    {
        Assert.DoesNotContain("nids.txt", typeof(NidDatabase).Assembly.GetManifestResourceNames());
        Assert.Null(typeof(NidDatabase).TypeInitializer);
    }

    [Fact]
    public void TheTableAnswersAsTheText()
    {
        Dictionary<uint, NidInfo> byNid = [];
        Dictionary<string, List<NidInfo>> byName = new(StringComparer.Ordinal);
        foreach (string line in File.ReadLines(NidsText()))
        {
            string[] parts = line.Split(' ');
            if (parts.Length < 4)
            {
                continue;
            }

            NidInfo info = new(uint.Parse(parts[0].AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture), parts[1], parts[2], parts[3]);
            byNid.TryAdd(info.Nid, info);
            if (!byName.TryGetValue(info.Name, out List<NidInfo>? list))
            {
                list = [];
                byName[info.Name] = list;
            }

            list.Add(info);
        }

        Assert.Equal(byNid.Count, NidDatabase.Count);
        Assert.Equal(byNid.Values, NidDatabase.All);
        foreach ((uint nid, NidInfo info) in byNid)
        {
            Assert.Equal(info, NidDatabase.Lookup(nid));
            Assert.Equal(info.Name, NidDatabase.NameOf(nid));
        }

        foreach ((string name, List<NidInfo> infos) in byName)
        {
            Assert.Equal(infos, NidDatabase.LookupName(name));
        }

        uint unknown = 0;
        while (byNid.ContainsKey(unknown))
        {
            unknown++;
        }

        Assert.Null(NidDatabase.Lookup(unknown));
        Assert.Equal($"NID_{unknown:X8}", NidDatabase.NameOf(unknown));
        Assert.Empty(NidDatabase.LookupName("NoSuchFunctionInTheDatabase"));
        Assert.Empty(NidDatabase.LookupName(""));
    }
}
