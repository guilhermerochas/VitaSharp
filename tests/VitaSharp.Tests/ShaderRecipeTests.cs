using VitaSharp.Core.Gxm;
using VitaSharp.Core.Gxm.Shader;
using VitaSharp.Core.Util;

namespace VitaSharp.Tests;

/// <summary>Version-independent shader/pipeline records (programs.pack, pipelines.list v4).</summary>
public class ShaderRecipeTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "vitasharp-recipes-" + Guid.NewGuid().ToString("N"));
    string StorePath => Path.Combine(_dir, "programs.pack");

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    /// <summary>A minimal GXP (all-zero tables) whose flags select vertex or fragment; <paramref name="seed"/> varies its hash.</summary>
    static GxpProgram Program(bool fragment, byte seed)
    {
        var data = new byte[0xC0];
        data[0x14] = fragment ? (byte)1 : (byte)0;
        data[0xBF] = seed;
        return new GxpProgram(data, 0x81000000);
    }

    static ShaderHints VertexHints() => new([0x0C000000, 0x80000000, 0x0C000000], [(0u, 4, 1), (3u, 2, 7)], 0);
    static ShaderHints FragmentHints(bool fetch = false) => new([0x86000000, 0x0C000000], [], 0x80001000, fetch);

    [Fact]
    public void ShaderRecipeRoundTrips()
    {
        foreach (var h in new[] { VertexHints(), FragmentHints(), FragmentHints(fetch: true), FragmentHints() with { OutputRegisterFormat = 1 },
                     FragmentHints(fetch: true) with { OutputRegisterFormat = 5 } })
        {
            var r = new ShaderRecipe(0x1122334455667788, h.Key(), h.Attributes.Length > 0, h);
            var back = ShaderRecipe.Decode(r.Encode());
            Assert.NotNull(back);
            Assert.Equal(r.ProgramHash, back!.ProgramHash);
            Assert.Equal(r.HintKey, back.HintKey);
            Assert.Equal(r.Vertex, back.Vertex);
            Assert.Equal(h.TextureFormats, back.Hints.TextureFormats);
            Assert.Equal(h.Attributes, back.Hints.Attributes);
            Assert.Equal(h.ColorFormat, back.Hints.ColorFormat);
            Assert.Equal(h.FetchInputAttachment, back.Hints.FetchInputAttachment);
            Assert.Equal(h.OutputRegisterFormat, back.Hints.OutputRegisterFormat);
            Assert.Equal(h.Key(), back.Hints.Key());
        }
    }

    [Fact]
    public void RecipesWithoutOutputFormatKeepTheirLayout()
    {
        var h = FragmentHints();
        var plain = new ShaderRecipe(1, h.Key(), false, h).Encode();
        Assert.Equal(2, plain[0]); // the tag and size older builds read
        var withFormat = new ShaderRecipe(1, (h with { OutputRegisterFormat = 1 }).Key(), false, h with { OutputRegisterFormat = 1 }).Encode();
        Assert.Equal(3, withFormat[0]);
        Assert.Equal(plain.Length + 4, withFormat.Length);
        Assert.Null(ShaderRecipe.Decode(withFormat.AsSpan(0, withFormat.Length - 1)));
    }

    [Fact]
    public void MalformedShaderRecipesAreRejected()
    {
        var h = VertexHints();
        var good = new ShaderRecipe(1, h.Key(), true, h).Encode();
        Assert.Null(ShaderRecipe.Decode(good.AsSpan(0, good.Length - 1))); // truncated
        Assert.Null(ShaderRecipe.Decode([.. good, 0])); // trailing byte
        Assert.Null(ShaderRecipe.Decode(new byte[3]));
        var badTag = (byte[])good.Clone(); badTag[0] = 9;
        Assert.Null(ShaderRecipe.Decode(badTag));
        var badKey = new ShaderRecipe(1, h.Key() ^ 1, true, h).Encode(); // hints no longer produce the key
        Assert.Null(ShaderRecipe.Decode(badKey));
        var badCount = (byte[])good.Clone(); badCount[18] = 200; // texture count beyond the record
        Assert.Null(ShaderRecipe.Decode(badCount));
    }

    static PipelineRecipe SamplePipeline() => new(1, 2, 3, 4, 97, true, true, 1, 2, 3, 4, 5, 6, 0xF, 3, 0, 4, false, 16,
        [new GxmVertexAttribute(0, 0, 3, 3, 0), new GxmVertexAttribute(1, 12, 7, 2, 5)], [new GxmVertexStream(24, 0), new GxmVertexStream(8, 2)]);

    [Fact]
    public void PipelineRecipeRoundTrips()
    {
        var p = SamplePipeline();
        var enc = p.Encode();
        var back = PipelineRecipe.Decode(enc);
        Assert.NotNull(back);
        Assert.Equal(p.Attributes, back!.Attributes);
        Assert.Equal(p.Streams, back.Streams);
        Assert.Equal(p with { Attributes = back.Attributes, Streams = back.Streams }, back);
        Assert.Equal(PipelineRecipe.KeyOf(enc), PipelineRecipe.KeyOf(back.Encode()));
        Assert.Null(PipelineRecipe.Decode(enc.AsSpan(0, enc.Length - 2)));
        Assert.Null(PipelineRecipe.Decode(enc.AsSpan(0, 10)));
        var bad = (byte[])enc.Clone(); bad[36] = 7;
        Assert.Null(PipelineRecipe.Decode(bad));
    }

    [Fact]
    public void StoreKeepsFirstUseOrderAndProgramBytes()
    {
        var vp = Program(false, 1);
        var fp = Program(true, 2);
        Assert.True(vp.IsVertex);
        Assert.True(fp.IsFragment);
        using (var s = ShaderRecipeStore.Open(StorePath)!)
        {
            s.Add(vp, VertexHints().Key(), VertexHints());
            s.Add(fp, FragmentHints().Key(), FragmentHints());
            s.Add(fp, FragmentHints(true).Key(), FragmentHints(true)); // same program, second variant: bytes stored once
            s.Add(vp, VertexHints().Key(), VertexHints()); // duplicate: ignored
            Assert.True(s.Has(fp.Hash, FragmentHints(true).Key()));
        }
        using var q = ShaderRecipeStore.Open(StorePath)!;
        var list = q.Recipes(out int bad);
        Assert.Equal(0, bad);
        Assert.Equal([(vp.Hash, VertexHints().Key()), (fp.Hash, FragmentHints().Key()), (fp.Hash, FragmentHints(true).Key())],
            list.Select(r => (r.ProgramHash, r.HintKey)));
        Assert.True(list[0].Vertex);
        Assert.False(list[1].Vertex);
        Assert.Equal(vp.Data, q.ProgramBytes(vp.Hash));
        Assert.Equal(fp.Data, q.ProgramBytes(fp.Hash));
        Assert.Null(q.ProgramBytes(12345));
        // the program parses back to the same identity
        Assert.Equal(fp.Hash, new GxpProgram(q.ProgramBytes(fp.Hash)!, 0).Hash);
    }

    [Fact]
    public void CorruptedRecordsAreSkipped()
    {
        var vp = Program(false, 3);
        using (var s = ShaderRecipeStore.Open(StorePath)!) s.Add(vp, VertexHints().Key(), VertexHints());
        // flip the last byte: the recipe (last record) fails its checksum
        var bytes = File.ReadAllBytes(StorePath);
        bytes[^1] ^= 0xFF;
        File.WriteAllBytes(StorePath, bytes);
        using (var q = ShaderRecipeStore.Open(StorePath)!)
        {
            Assert.Empty(q.Recipes(out int bad));
            Assert.Equal(1, bad);
            Assert.Equal(vp.Data, q.ProgramBytes(vp.Hash)); // the program record is intact
        }
        // a corrupted program record reads as missing
        var fresh = Path.Combine(_dir, "p2.pack");
        using (var s = ShaderRecipeStore.Open(fresh)!) s.Add(vp, VertexHints().Key(), VertexHints());
        bytes = File.ReadAllBytes(fresh);
        bytes[16 + 24 + 10] ^= 0x55; // inside the first payload (file header 16, record header 24, tag + GXP)
        File.WriteAllBytes(fresh, bytes);
        using var r = ShaderRecipeStore.Open(fresh)!;
        Assert.Null(r.ProgramBytes(vp.Hash));
        Assert.Single(r.Recipes(out _));
    }

    [Fact]
    public void TornTailKeepsEarlierRecipes()
    {
        var a = Program(false, 4);
        var b = Program(true, 5);
        using (var s = ShaderRecipeStore.Open(StorePath)!)
        {
            s.Add(a, VertexHints().Key(), VertexHints());
            s.Add(b, FragmentHints().Key(), FragmentHints());
        }
        long len = new FileInfo(StorePath).Length;
        using (var fs = new FileStream(StorePath, FileMode.Open)) fs.SetLength(len - 5); // killed while writing b's recipe
        using var q = ShaderRecipeStore.Open(StorePath)!;
        var list = q.Recipes(out _);
        Assert.Single(list);
        Assert.Equal(a.Hash, list[0].ProgramHash);
    }

    [Fact]
    public void OtherFormatVersionStartsOver()
    {
        // a pack written with another version (e.g. a shaders.pack, or a future programs.pack) is not read as recipes
        using (var p = BlobPack.Open(StorePath, 0x00030008)!) p.Add(1, [2, 1, 2, 3]);
        using var q = ShaderRecipeStore.Open(StorePath)!;
        Assert.Empty(q.Recipes(out int bad));
        Assert.Equal(0, bad);
    }

    [Fact]
    public void SecondSessionGetsReadOnlyView()
    {
        var a = Program(false, 6);
        using var first = ShaderRecipeStore.Open(StorePath)!;
        first.Add(a, VertexHints().Key(), VertexHints());
        Assert.False(first.IsReadOnly);
        using var second = ShaderRecipeStore.Open(StorePath)!;
        Assert.True(second.IsReadOnly);
        Assert.Single(second.Recipes(out _));
        second.Add(Program(true, 7), FragmentHints().Key(), FragmentHints()); // no-op, must not corrupt the writer's file
        first.Add(Program(true, 8), FragmentHints().Key(), FragmentHints());
        first.Dispose();
        second.Dispose();
        using var third = ShaderRecipeStore.Open(StorePath)!;
        Assert.Equal(2, third.Recipes(out int bad).Count);
        Assert.Equal(0, bad);
    }

    [Fact]
    public void PeekVersionDoesNotModify()
    {
        using (var p = BlobPack.Open(StorePath, 0x00030008)!) p.Add(1, [1]);
        long len = new FileInfo(StorePath).Length;
        Assert.Equal(0x00030008u, BlobPack.PeekVersion(StorePath));
        Assert.Equal(len, new FileInfo(StorePath).Length);
        Assert.Null(BlobPack.PeekVersion(Path.Combine(_dir, "missing.pack")));
    }

    [Fact]
    public void ReadOnlyViewOfOtherVersionIsEmptyAndUntouched()
    {
        using var writer = BlobPack.Open(StorePath, 5)!;
        writer.Add(1, [1, 2]);
        using var reader = BlobPack.Open(StorePath, 6)!; // another build with another format
        Assert.True(reader.IsReadOnly);
        Assert.Equal(0, reader.Count);
        writer.Add(2, [3]);
        Assert.Equal([3], writer.TryRead(2));
    }
}
