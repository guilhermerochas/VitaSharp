using Silk.NET.Vulkan;
using VitaSharp.Core.Gpu;
using VitaSharp.Core.Util;

namespace VitaSharp.Renderer.Vulkan;

/// <summary>Renderer side of <see cref="AstcTranscoder"/>: when it is used and where its disk cache lives.</summary>
public sealed unsafe partial class VulkanRenderer
{
    /// <summary>VITASHARP_ASTC=0: BC/PVRTC fall back to RGBA8 even when the GPU samples ASTC.</summary>
    private static readonly bool s_astcAllowed = Environment.GetEnvironmentVariable(EnvVars.Astc)?.Trim() != "0";
    /// <summary>VITASHARP_ASTC_CACHE_MB: size limit of a title's ASTC disk cache (default 512 MB).</summary>
    private static readonly long s_astcCacheBytes = (long.TryParse(Environment.GetEnvironmentVariable(EnvVars.AstcCacheMb), out long mb) && mb > 0 ? mb : 512) << 20;

    private bool? _astcUsable;

    /// <summary>textureCompressionASTC_LDR is enabled and the 4x4 formats can be sampled with linear filtering.</summary>
    private bool AstcUsable => _astcUsable ??= s_astcAllowed && _core.Caps.Has(GpuFeature.TextureCompressionAstc)
        && _core.SupportsSampled(Format.Astc4x4UnormBlock) && _core.SupportsSampled(Format.Astc4x4SrgbBlock);

    private static bool IsAstc(in TexFormatInfo info) => info.Convert is TexConvert.AstcFromBc or TexConvert.AstcFromPvrtc;

    /// <summary>VITASHARP_ASTC_PRELOAD_MB: how much of the title's ASTC disk cache is read into memory at the title start,
    /// most recently used first (default TranscodeDiskCache.DefaultPreloadMb; 0 off).</summary>
    private static readonly long s_astcPreloadBytes = (long.TryParse(Environment.GetEnvironmentVariable(Core.Util.EnvVars.AstcPreloadMb), out long mb) ? mb : TranscodeDiskCache.DefaultPreloadMb) << 20;

    /// <summary>Title start: the ASTC disk cache goes into the title's shader cache folder.</summary>
    private void OpenAstcCache(string titleDir)
    {
        // a GPU that samples BCn natively never transcodes (desktop GPUs, the Android emulator's host GPU)
        if (!AstcUsable || _core.TextureCompressionBC)
        {
            return;
        }

        string dir = Path.Combine(titleDir, "astc");
        // the same title started again (a state load): keep the cache and what its read-ahead already has in memory
        if (AstcTranscoder.Disk?.Directory == dir)
        {
            return;
        }

        AstcTranscoder.Disk?.ReleasePreloaded();
        AstcTranscoder.Disk = new TranscodeDiskCache(dir, s_astcCacheBytes);
        AstcTranscoder.Disk.StartPreload(s_astcPreloadBytes);
        Log.Info($"[texcache] BC/PVRTC textures are transcoded to ASTC 4x4; disk cache {AstcTranscoder.Disk.Directory} (limit {s_astcCacheBytes >> 20} MB, {AstcTranscoder.Disk.Bytes >> 20} MB used)");
    }
}
