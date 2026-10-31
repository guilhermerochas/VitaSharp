using System.Text;
using Silk.NET.Vulkan;
using VitaSharp.Core.Gpu;
using VitaSharp.Core.Gxm.Shader;
using VitaSharp.Core.Util;

namespace VitaSharp.Renderer.Vulkan;

/// <summary>
/// Fallbacks for devices with fewer features or smaller limits than desktop GPUs (phones; desktop GPUs under
/// --gpu-profile=adreno|mali). Each path checks <see cref="VkCore.Caps"/>; see docs/notes/mobile-gpu.md.
/// </summary>
public sealed unsafe partial class VulkanRenderer
{
    /// <summary>Texture units bound per stage (GXM has 16). Devices with fewer per-stage samplers / sampled images
    /// (Mali Bifrost: 16, of which the fragment stage also needs 2 for framebuffer fetch) bind fewer.</summary>
    int _texUnitsV = 16, _texUnitsF = 16;
    long _statUnitSkips;

    /// <summary>Picks the per-stage texture unit counts from the descriptor limits.</summary>
    void ChooseTextureUnits()
    {
        var l = _core.Caps.Limits;
        int perStage = (int)Math.Min(l.MaxPerStageDescriptorSamplers, l.MaxPerStageDescriptorSampledImages);
        // fragment stage: framebuffer-fetch set 3 adds two combined image samplers
        _texUnitsF = Math.Clamp(perStage - 2, 1, 16);
        _texUnitsV = Math.Clamp(perStage, 1, 16);
        // all sets of the pipeline layout together (maxDescriptorSetSamplers)
        int total = (int)l.MaxDescriptorSetSamplers;
        if (_texUnitsV + _texUnitsF + 2 > total) _texUnitsV = Math.Max(1, total - _texUnitsF - 2);
        if (_texUnitsV < 16 || _texUnitsF < 16)
            Log.Warn($"[gpu] {_core.DeviceName}: {l.MaxPerStageDescriptorSamplers} samplers / {l.MaxPerStageDescriptorSampledImages} sampled images per stage: binding {_texUnitsV} vertex and {_texUnitsF} fragment texture units (draws using higher units are skipped)");
    }

    /// <summary>A program samples a texture unit the layout does not have (devices with small descriptor limits).</summary>
    bool UsesMissingTextureUnit(uint used, int units, ulong programHash)
    {
        if (units >= 16 || (used >> units) == 0) return false;
        _statUnitSkips++;
        Log.WarnOnce($"texunit-{programHash:X16}", $"[gpu] program {programHash:X16} samples texture unit {31 - System.Numerics.BitOperations.LeadingZeroCount(used)} but this device binds only {units}: draw skipped");
        return true;
    }

    static string BcCacheSummary() => BcSoftware.Summary();

    /// <summary>"PERF mobile" line when a quirk or a profile is active (nothing on desktop GPUs: their report is unchanged).</summary>
    string MobileReport()
    {
        var c = _core.Caps;
        if (c.Quirks == GpuQuirk.None && c.Profile == GpuProfile.Native && c.Has(GpuFeature.TextureCompressionBC) && AstcTranscoder.Encoded == 0) return "";
        return $"\nPERF mobile ({c.Profile.ToString().ToLowerInvariant()} profile): {MobilePathsSummary()}; {_statPipelinesStatic} static-state pipelines";
    }

    /// <summary>The fallbacks in use (info.txt "Mobile paths" line).</summary>
    string MobilePathsSummary()
    {
        var c = _core.Caps;
        var sb = new StringBuilder();
        void Add(string s) { if (sb.Length > 0) sb.Append("; "); sb.Append(s); }
        Add(c.Has(GpuFeature.TextureCompressionBC) ? "BC sampled compressed" : AstcUsable ? $"BC transcoded to ASTC (signed BC4/BC5 decoded on the CPU: {BcCacheSummary()})" : $"BC decoded on the CPU ({BcCacheSummary()})");
        Add(AstcUsable ? AstcTranscoder.Summary() : "PVRTC decoded on the CPU");
        Add($"texture units {_texUnitsV} vertex / {_texUnitsF} fragment{(_statUnitSkips > 0 ? $" ({_statUnitSkips} draws skipped)" : "")}");
        Add($"MSAA 2X scenes {(c.MsaaSamples(2) is var s2 && s2 == 0 ? "not multisampled" : $"with {s2} samples")}, 4X {(c.MsaaSamples(4) == 0 ? "not multisampled" : "with 4 samples")}");
        Add($"MSAA framebuffer fetch {(_core.RasterizationOrderColor ? "rasterization-order input attachment (no barriers)" : "input attachment + by-region barrier")}, {(_core.SampleRateShading ? "per sample" : "sample 0 (no sampleRateShading)")}");
        Add(c.Has(GpuQuirk.StaticExtendedDynamicState) ? "extended dynamic state baked into pipelines" : "extended dynamic state");
        Add(c.Has(GpuQuirk.StaticDepthBiasEnable) ? "depth bias always enabled (zero when off)" : "dynamic depth bias enable");
        Add(ShaderGenerator.LowerDynamicIndexing ? "dynamic register indexing lowered to switches" : "dynamic register indexing");
        Add(c.Has(GpuQuirk.TileBased) ? "tile-based load/store ops" : "immediate-mode load/store ops");
        Add(RenderPassReport());
        Add(_core.DepthClamp ? "depth clamp" : "no depth clamp (near plane clips)");
        Add(ShaderGenerator.FarPlane switch
        {
            ShaderGenerator.FarPlaneClip.Discard => "far plane clipped by a fragment discard (no shaderClipDistance)",
            ShaderGenerator.FarPlaneClip.Hardware => "far plane clipped by the hardware (no shaderClipDistance, no depth clamp)",
            _ => "far plane clip distance",
        });
        Add(_core.FillModeNonSolid ? "line/point fill" : "line/point fill drawn filled");
        Add(_core.MemoryBudgetSupported ? "memory budget" : $"no memory budget (eviction at {c.FallbackBudgetFraction:P0} of the device heap)");
        Add(_core.Timestamps ? "timestamps" : "no timestamps");
        Add($"driver pipeline cache {Path.GetFileName(_vkCachePath ?? "(none)")}");
        return sb.ToString();
    }
}
