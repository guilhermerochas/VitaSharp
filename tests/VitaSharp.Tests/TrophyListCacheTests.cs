using VitaSharp.Core.Hle.Np;
using VitaSharp.Mobile.Core.Library;
using VitaSharp.Mobile.Core.Storage;

namespace VitaSharp.Tests;

/// <summary>
/// The Android Trofeus screen's disk cache (TrophyListCache): the first opening reads the TRP once and extracts the
/// pictures; opening again (same process or after a restart) reads no TRP and extracts nothing; a changed TRP or a
/// changed progress file is picked up (the progress without reading the TRP again).
/// </summary>
public sealed class TrophyListCacheTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vitasharp-trophycache-" + Guid.NewGuid().ToString("N"));
    private static readonly byte[] s_icon1 = [0x89, (byte)'P', (byte)'N', (byte)'G', 1, 1];
    private static readonly byte[] s_icon2 = [0x89, (byte)'P', (byte)'N', (byte)'G', 2, 2];

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private (GameEntry Game, DataLayout Data, string Trp) InstallGame(string firstName = "First")
    {
        var data = new DataLayout(_root);
        data.EnsureCreated();
        string appDir = Path.Combine(data.Apps, "PCSA00001");
        string trophyDir = Path.Combine(appDir, "sce_sys", "trophy", "NPWR00001_00");
        Directory.CreateDirectory(trophyDir);
        string trp = Path.Combine(trophyDir, "TROPHY.TRP");
        WriteTrp(trp, firstName);
        return (new GameEntry("PCSA00001", "Game", "01.00", "gd", appDir, null, 0), data, trp);
    }

    private static void WriteTrp(string trp, string firstName) =>
        File.WriteAllBytes(trp, NpLocalTests.BuildTrp(("TROPCONF.SFM", NpLocalTests.Sfm("Conf", "x")), ("TROP.SFM", NpLocalTests.Sfm("Game", firstName)),
            ("TROP001.PNG", s_icon1), ("TROP002.PNG", s_icon2)));

    [Fact]
    public void TheSecondOpeningReadsNoTrpAndExtractsNothing()
    {
        (GameEntry game, DataLayout data, _) = InstallGame();
        var cache = new TrophyListCache(data.TrophyCache);
        TrophyList first = cache.Load(game, data);
        Assert.Equal(1, cache.TrpReads);
        Assert.Equal(1, cache.IconExtractions);
        Assert.Equal(3, first.Rows.Count);

        TrophyList second = cache.Load(game, data);
        Assert.Equal(1, cache.TrpReads);
        Assert.Equal(1, cache.IconExtractions);
        Assert.Equal(1, cache.UnlockReads);
        Assert.Equal(first.Rows.Select(r => (r.Id, r.Name, r.Grade)), second.Rows.Select(r => (r.Id, r.Name, r.Grade)));
    }

    /// <summary>The app restarted: a new cache over the same folder takes the set from disk.</summary>
    [Fact]
    public void AfterARestartTheListComesFromDisk()
    {
        (GameEntry game, DataLayout data, _) = InstallGame();
        new TrophyListCache(data.TrophyCache).Load(game, data);

        var restarted = new TrophyListCache(data.TrophyCache);
        TrophyList list = restarted.Load(game, data);
        Assert.Equal(0, restarted.TrpReads);
        Assert.Equal(0, restarted.IconExtractions);
        Assert.Equal(1, restarted.DiskHits);
        Assert.Equal("Game", list.Title);
        TrophyRow withIcon = list.Rows.Single(r => r.Id == 2);
        Assert.Null(withIcon.Set);
        Assert.NotNull(withIcon.IconFile);
        Assert.StartsWith(data.TrophyCache, withIcon.IconFile);
        Assert.Equal(s_icon2, withIcon.LoadIcon());
        Assert.False(list.Rows.Single(r => r.Id == 0).HasIcon);
    }

    /// <summary>The user unlocked a trophy: the list shows it, read from the progress file only.</summary>
    [Fact]
    public void AChangedProgressIsShownWithoutReadingTheTrp()
    {
        (GameEntry game, DataLayout data, _) = InstallGame();
        var cache = new TrophyListCache(data.TrophyCache);
        Assert.Equal(0, cache.Load(game, data).Progress.Earned);

        string progress = TrophyUnlocks.PathFor(data.Fs, "NPWR00001_00");
        TrophyUnlocks.Save(progress, new Dictionary<int, ulong> { [2] = 63_000_000_000_000_000 });
        TrophyList list = cache.Load(game, data);
        Assert.Equal(1, list.Progress.Earned);
        Assert.True(list.Rows.Single(r => r.Id == 2).Unlocked);
        Assert.Equal(1, cache.TrpReads);
        Assert.Equal(2, cache.UnlockReads);

        // the same progress again: not read
        cache.Load(game, data);
        Assert.Equal(2, cache.UnlockReads);

        // also after a restart
        var restarted = new TrophyListCache(data.TrophyCache);
        Assert.Equal(1, restarted.Load(game, data).Progress.Earned);
        Assert.Equal(0, restarted.TrpReads);
    }

    /// <summary>A game update replaced the TRP: texts and pictures are built again.</summary>
    [Fact]
    public void AChangedTrpIsReadAgain()
    {
        (GameEntry game, DataLayout data, string trp) = InstallGame();
        var cache = new TrophyListCache(data.TrophyCache);
        cache.Load(game, data);

        WriteTrp(trp, "Renamed");
        File.SetLastWriteTimeUtc(trp, File.GetLastWriteTimeUtc(trp).AddMinutes(1));
        TrophyList list = cache.Load(game, data);
        Assert.Equal(2, cache.TrpReads);
        Assert.Equal(2, cache.IconExtractions);
        Assert.Equal("Renamed", list.Rows.Single(r => r.Id == 1).Name);

        var restarted = new TrophyListCache(data.TrophyCache);
        Assert.Equal("Renamed", restarted.Load(game, data).Rows.Single(r => r.Id == 1).Name);
        Assert.Equal(0, restarted.TrpReads);
    }

    /// <summary>Pictures deleted from the cache folder (the system clears app caches): extracted again.</summary>
    [Fact]
    public void AMissingPictureIsExtractedAgain()
    {
        (GameEntry game, DataLayout data, _) = InstallGame();
        TrophyList first = new TrophyListCache(data.TrophyCache).Load(game, data);
        File.Delete(first.Rows.Single(r => r.Id == 1).IconFile!);

        var restarted = new TrophyListCache(data.TrophyCache);
        TrophyList list = restarted.Load(game, data);
        Assert.Equal(1, restarted.IconExtractions);
        Assert.Equal(s_icon1, list.Rows.Single(r => r.Id == 1).LoadIcon());
    }

    /// <summary>Each language has its own texts file; the pictures are shared.</summary>
    [Fact]
    public void LanguagesAreCachedApartAndShareThePictures()
    {
        (GameEntry game, DataLayout data, _) = InstallGame();
        var cache = new TrophyListCache(data.TrophyCache);
        cache.Load(game, data, -1);
        cache.Load(game, data, 17);
        Assert.Equal(2, cache.TrpReads);
        Assert.Equal(1, cache.IconExtractions);
    }

    /// <summary>One open of the TRP reads every picture of a set.</summary>
    [Fact]
    public void ReadEntriesReadsTheIconsLeftOnDisk()
    {
        (_, _, string trp) = InstallGame();
        TrophyPackage set = TrophyPackage.LoadTexts(trp, "NPWR00001_00")!;
        List<(string Name, byte[] Data)> icons = set.ReadEntries(name => name.EndsWith(".PNG", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(["TROP001.PNG", "TROP002.PNG"], icons.Select(e => e.Name).Order());
        Assert.Equal(s_icon1, icons.Single(e => e.Name == "TROP001.PNG").Data);
    }
}
