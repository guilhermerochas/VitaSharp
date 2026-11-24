using VitaSharp.Core.Compat.Ge;
using VitaSharp.Core.Util;

namespace VitaSharp.Renderer.Vulkan;

/// <summary>
/// The GE's rasterizer for the running emulator: a <see cref="GeVulkanRasterizer"/> on a Vulkan device of its own
/// (no presentation surface), so the GE's batches, submitted from the compat core's thread, never share a queue
/// with the GXM renderer's threads. The renderer offers it to every GE (<see cref="GeEngine.DefaultRasterizerFactory"/>)
/// when it starts; a GE makes it only at its first batch.
/// </summary>
public sealed class GeVulkanRasterizerDevice : IGeRasterizer, IDisposable
{
    private readonly VkCore _core;
    private readonly ShaderCompiler _compiler;
    private readonly GeVulkanRasterizer _rasterizer;

    private GeVulkanRasterizerDevice(VkCore core, ShaderCompiler compiler, GeVulkanRasterizer rasterizer)
    {
        _core = core;
        _compiler = compiler;
        _rasterizer = rasterizer;
    }

    /// <summary>The device and its rasterizer; null (logged) when Vulkan or the shader compiler is not available.</summary>
    public static IGeRasterizer? Create()
    {
        VkCore? core = null;
        ShaderCompiler? compiler = null;
        try
        {
            core = new VkCore(null, false);
            compiler = new ShaderCompiler(null);
            GeVulkanRasterizer rasterizer = new(core, compiler);
            Log.Info($"GE: rasterizer on {core.DeviceName}");
            return new GeVulkanRasterizerDevice(core, compiler, rasterizer);
        }
        catch (Exception failure) when (failure is InvalidOperationException or DllNotFoundException or EntryPointNotFoundException)
        {
            Log.Error($"GE: no Vulkan rasterizer ({failure.Message}): the GE's draws are dropped");
            compiler?.Dispose();
            core?.Dispose();
            return null;
        }
    }

    /// <summary>The batches drawn.</summary>
    public long Draws => _rasterizer.Draws;

    /// <summary>The rasterizer itself (tests read its counters).</summary>
    public GeVulkanRasterizer Rasterizer => _rasterizer;

    public void Draw(GeTopology topology, ReadOnlySpan<GeVertex> vertices, GeRenderState state, GeSurface color, GeSurface? depth, IGeMemory memory)
    {
        _rasterizer.Draw(topology, vertices, state, color, depth, memory);
    }

    public void Flush(IGeMemory memory)
    {
        _rasterizer.Flush(memory);
    }

    public bool Holds(uint address, int length)
    {
        return _rasterizer.Holds(address, length);
    }

    public void Invalidate(uint address, int length)
    {
        _rasterizer.Invalidate(address, length);
    }

    public void Dispose()
    {
        _rasterizer.Dispose();
        _compiler.Dispose();
        _core.Dispose();
    }
}
