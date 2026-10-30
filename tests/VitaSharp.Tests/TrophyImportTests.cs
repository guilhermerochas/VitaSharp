using System.Buffers.Binary;
using VitaSharp.Core.Hle.Np;
using Xunit;

namespace VitaSharp.Tests;

/// <summary>Importing trophies earned elsewhere: Vita3K's TROPUSR.DAT and a PS Vita's ur0:user/00/trophy folder.</summary>
public class TrophyImportTests
{
    static byte[] Vita3KFile(params (int Id, ulong Unix)[] unlocked)
    {
        var d = new byte[0x670];
        BinaryPrimitives.WriteUInt32LittleEndian(d, TrophyImport.Vita3KMagic);
        foreach (var (id, unix) in unlocked)
        {
            int word = 4 + id / 32 * 4;
            BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(word), BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(word)) | 1u << (id % 32));
            BinaryPrimitives.WriteUInt64LittleEndian(d.AsSpan(0x70 + id * 8), unix);
        }
        return d;
    }

    [Fact]
    public void ReadsVita3KUnlocksAndTimes()
    {
        // Vita3K's file of a title with trophies 1, 2, 3 and 40 unlocked (2023-04-27 and later, Unix seconds)
        var u = TrophyImport.ReadVita3K(Vita3KFile((1, 0x6449EABB), (2, 0x6449ED78), (3, 0x6449F1BC), (40, 0)));
        Assert.Equal([1, 2, 3, 40], u.Keys.Order().ToArray());
        Assert.Equal(new DateTime(2023, 4, 27, 3, 23, 39, DateTimeKind.Utc), TrophyUnlocks.TickToUtc(u[1]));
        Assert.Equal(0ul, u[40]); // no time kept
        Assert.Throws<InvalidDataException>(() => TrophyImport.ReadVita3K(new byte[0x670]));
    }

    [Fact]
    public void MergeAddsNewTrophiesAndKeepsTheEarlierTime()
    {
        var store = new Dictionary<int, ulong> { [1] = 500, [2] = 0 };
        int added = TrophyImport.Merge(store, new Dictionary<int, ulong> { [1] = 400, [2] = 700, [3] = 900 });
        Assert.Equal(1, added);
        Assert.Equal(400ul, store[1]);
        Assert.Equal(700ul, store[2]);
        Assert.Equal(900ul, store[3]);
        Assert.Equal(0, TrophyImport.Merge(store, new Dictionary<int, ulong> { [1] = 450 }));
        Assert.Equal(400ul, store[1]);
    }

    [Fact]
    public void DetectsTheKindOfFolder()
    {
        string root = Path.Combine(Path.GetTempPath(), "vitasharp-trophyimport-" + Guid.NewGuid().ToString("N"));
        try
        {
            // a Vita3K data folder (ux0/user/00/trophy/data/<id>/TROPUSR.DAT), imported into a fresh fs root
            string v3k = Path.Combine(root, "v3k", "ux0", "user", "00", "trophy", "data", "NPWR03852_00");
            Directory.CreateDirectory(v3k);
            File.WriteAllBytes(Path.Combine(v3k, "TROPUSR.DAT"), Vita3KFile((1, 0x6449EABB)));
            Assert.Equal(TrophyImport.SourceKind.Vita3K, TrophyImport.Detect(TrophyImport.Locate(Path.Combine(root, "v3k"))));
            string fs = Path.Combine(root, "fs");
            var r = TrophyImport.Import(Path.Combine(root, "v3k"), fs);
            Assert.Equal(1, r.Titles.Single().Imported);
            Assert.True(TrophyUnlocks.Load(TrophyUnlocks.PathFor(fs, "NPWR03852_00")).ContainsKey(1));
            Assert.Equal(0, TrophyImport.Import(Path.Combine(root, "v3k"), fs).Titles.Single().Imported); // again: nothing new

            // a console's folder as stored on its memory: protected (sce_pfs) and encrypted
            string vita = Path.Combine(root, "vita", "ur0", "user", "00", "trophy", "data");
            Directory.CreateDirectory(Path.Combine(vita, "sce_trop"));
            Directory.CreateDirectory(Path.Combine(vita, "NPWR01931_00", "sce_pfs"));
            File.WriteAllBytes(Path.Combine(vita, "sce_trop", "TRPUSER.DAT"), [0x12, 0x34, 0x56, 0x78, 0, 0]);
            File.WriteAllBytes(Path.Combine(vita, "NPWR01931_00", "TRPTITLE.DAT"), [1, 2, 3, 4]);
            Assert.Equal(TrophyImport.SourceKind.VitaEncrypted, TrophyImport.Import(Path.Combine(root, "vita"), fs).Kind);
            Assert.False(Directory.Exists(Path.Combine(fs, "ur0")));

            // decrypted (the server's own plain files): it becomes the system's trophy data, the old one is kept
            Directory.Delete(Path.Combine(vita, "NPWR01931_00", "sce_pfs"));
            File.WriteAllBytes(Path.Combine(vita, "sce_trop", "TRPUSER.DAT"), [0x7F, 0x15, 0x99, 0x36, 0, 1, 0, 0]);
            string sys = Path.Combine(fs, "ur0", "user", "00", "trophy");
            Directory.CreateDirectory(Path.Combine(sys, "db"));
            File.WriteAllText(Path.Combine(sys, "db", "old.txt"), "old");
            r = TrophyImport.Import(Path.Combine(root, "vita"), fs);
            Assert.Equal(TrophyImport.SourceKind.VitaDecrypted, r.Kind);
            Assert.Equal("NPWR01931_00", r.Titles.Single().CommId);
            Assert.True(File.Exists(Path.Combine(sys, "data", "NPWR01931_00", "TRPTITLE.DAT")));
            Assert.False(File.Exists(Path.Combine(sys, "db", "old.txt")));
            Assert.True(File.Exists(Path.Combine(r.Backup!, "db", "old.txt")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("NPWR01931_00", true)]
    [InlineData("sce_trop", false)]
    [InlineData("NPWR0193_000", false)]
    public void CommunicationIds(string name, bool valid) => Assert.Equal(valid, TrophyImport.IsCommId(name));
}
