using BenchmarkDotNet.Attributes;
using VitaSharp.Core.Gxm;
using VitaSharp.Renderer.Vulkan;

namespace VitaSharp.Benchmarks;

/// <summary>Texture layout conversion (swizzled and tiled to linear; the *PerTexel methods are the conversions before the
/// table and run copies, VITASHARP_TEX_LAYOUT=legacy) and the CPU PVRTC decoder the renderer runs on
/// every upload of such a texture (Killzone streams many; TexMemUpload of the worker profile).</summary>
public class TextureBenchmarks
{
    [Params(256, 1024)] public uint Size;
    [Params(4)] public int BytesPerPixel;

    private byte[] _src = [], _dst = [], _pvrtc = [];

    [GlobalSetup]
    public void Setup()
    {
        var rng = new Random(1);
        _src = new byte[Size * Size * (uint)BytesPerPixel];
        _dst = new byte[_src.Length];
        rng.NextBytes(_src);
        _pvrtc = new byte[Size * Size / 2]; // 4 bpp
        rng.NextBytes(_pvrtc);
    }

    [Benchmark] public void SwizzledToLinear() => TextureLayout.SwizzledToLinear(_dst, _src, Size, Size, BytesPerPixel);

    [Benchmark] public void SwizzledToLinearPerTexel() => TextureLayout.SwizzledToLinearPerTexel(_dst, _src, Size, Size, BytesPerPixel);

    [Benchmark] public void TiledToLinearPerTexel() => TextureLayout.TiledToLinearPerTexel(_dst, _src, Size, Size, BytesPerPixel);

    [Benchmark] public void TiledToLinear() => TextureLayout.TiledToLinear(_dst, _src, Size, Size, BytesPerPixel);

    [Benchmark] public void LinearCopy() => _src.AsSpan().CopyTo(_dst);

    [Benchmark]
    public int Pvrtc4Decode()
    {
        using Texels t = Pvrtc.Decode(_pvrtc, Size, Size, twoBpp: false, ii: false);
        return t.Length;
    }
}
