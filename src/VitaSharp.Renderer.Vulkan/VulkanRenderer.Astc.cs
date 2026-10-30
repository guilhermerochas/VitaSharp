using Silk.NET.Vulkan;
using VitaSharp.Core.Gpu;
using VitaSharp.Core.Util;

namespace VitaSharp.Renderer.Vulkan;

/// <summary>Renderer side of <see cref="AstcTranscoder"/>: when it is used and where its disk cache lives.</summary>
public sealed unsafe partial class VulkanRenderer
{
    /// <summary>VITASHARP_ASTC=0: BC/PVRTC fall back to RGBA8 even when the GPU samples ASTC.</summary>
    static readonly bool AstcAllowed = Environment.GetEnvironmentVariable(EnvVars.Astc)?.Trim() != "0";
    /// <summary>VITASHARP_ASTC_CACHE_MB: size limit of a title's ASTC disk cache (default 512 MB).</summary>
    static readonly long AstcCacheBytes = (long.TryParse(Environment.GetEnvironmentVariable(EnvVars.AstcCacheMb), out var mb) && mb > 0 ? mb : 512) << 20;

    bool? _astcUsable;

    /// <summary>textureCompressionASTC_LDR is enabled and the 4x4 formats can be sampled with linear filtering.</summary>
    bool AstcUsable => _astcUsable ??= AstcAllowed && _core.Caps.Has(GpuFeature.TextureCompressionAstc)
        && _core.SupportsSampled(Format.Astc4x4UnormBlock) && _core.SupportsSampled(Format.Astc4x4SrgbBlock);

    static bool IsAstc(in TexFormatInfo info) => info.Convert is TexConvert.AstcFromBc or TexConvert.AstcFromPvrtc;

    /// <summary>Title start: the ASTC disk cache goes into the title's shader cache folder.</summary>
    void OpenAstcCache(string titleDir)
    {
        if (!AstcUsable) return;
        AstcTranscoder.Disk = new TranscodeDiskCache(Path.Combine(titleDir, "astc"), AstcCacheBytes);
        Log.Info($"[texcache] BC/PVRTC textures are transcoded to ASTC 4x4; disk cache {AstcTranscoder.Disk.Directory} (limit {AstcCacheBytes >> 20} MB, {AstcTranscoder.Disk.Bytes >> 20} MB used)");
    }
}
