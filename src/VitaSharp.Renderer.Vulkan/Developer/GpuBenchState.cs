#if VITASHARP_DEVELOPER
using Silk.NET.Vulkan;

namespace VitaSharp.Renderer.Vulkan.Developer;

/// <summary>
/// Switches of the GPU bench (developer builds only), set by <see cref="GpuBench"/> before it creates the renderer.
/// All off by default: a developer build playing a game takes none of the bench paths.
/// </summary>
public static class GpuBenchState
{
    /// <summary>Timestamp pair around every scene (<see cref="VulkanRenderer.DevReadSceneTimes"/>).</summary>
    public static bool Timing;
    /// <summary>Folder of replacement shaders (HASH_HINT.vert|frag[.spv]); null = translated shaders only.</summary>
    public static string? ShaderDir;
    /// <summary>Depth load op of scene renderings that do not load depth (null: as chosen by the renderer).</summary>
    public static AttachmentLoadOp? DepthLoad;
    /// <summary>Depth store op of scene renderings (null: as chosen by the renderer).</summary>
    public static AttachmentStoreOp? DepthStore;
    /// <summary>A full memory barrier after every tiled rendering.</summary>
    public static bool FullBarriers;

    public static void Reset()
    {
        Timing = false;
        ShaderDir = null;
        DepthLoad = null;
        DepthStore = null;
        FullBarriers = false;
    }
}
#endif
