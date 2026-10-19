using VitaSharp.Core.Util;

namespace VitaSharp.Tests;

public class BlobPackTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "vitasharp-blobpack-" + Guid.NewGuid().ToString("N"));
    string PackPath => Path.Combine(_dir, "test.pack");

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void RoundTripsAndKeepsInsertionOrder()
    {
        using (var p = BlobPack.Open(PackPath, 7)!)
        {
            p.Add(30, [1, 2, 3]);
            p.Add(10, [4]);
            p.Add(30, [9, 9]); // duplicate key: ignored
        }
        using var q = BlobPack.Open(PackPath, 7)!;
        Assert.Equal(2, q.Count);
        Assert.Equal([30ul, 10ul], q.Keys());
        Assert.Equal([1, 2, 3], q.TryRead(30));
        Assert.Equal([4], q.TryRead(10));
        Assert.Null(q.TryRead(99));
    }

    [Fact]
    public void OtherVersionStartsOver()
    {
        using (var p = BlobPack.Open(PackPath, 1)!) p.Add(1, [1]);
        using var q = BlobPack.Open(PackPath, 2)!;
        Assert.Equal(0, q.Count);
        Assert.Null(q.TryRead(1));
    }

    [Fact]
    public void TornTailIsDroppedAndAppendsStayReadable()
    {
        using (var p = BlobPack.Open(PackPath, 1)!)
        {
            p.Add(1, new byte[100]);
            p.Add(2, new byte[200]);
        }
        long full = new FileInfo(PackPath).Length;
        using (var fs = new FileStream(PackPath, FileMode.Open)) fs.SetLength(full - 50); // crash in the middle of record 2
        using (var p = BlobPack.Open(PackPath, 1)!)
        {
            Assert.Equal(1, p.Count);
            Assert.NotNull(p.TryRead(1));
            p.Add(3, [5, 6]);
        }
        using var q = BlobPack.Open(PackPath, 1)!;
        Assert.Equal([1ul, 3ul], q.Keys());
        Assert.Equal([5, 6], q.TryRead(3));
    }

    [Fact]
    public void CorruptedPayloadReadsAsMissing()
    {
        using (var p = BlobPack.Open(PackPath, 1)!) p.Add(1, [1, 2, 3, 4]);
        var bytes = File.ReadAllBytes(PackPath);
        bytes[^1] ^= 0xFF;
        File.WriteAllBytes(PackPath, bytes);
        using var q = BlobPack.Open(PackPath, 1)!;
        Assert.Null(q.TryRead(1));
    }
}
