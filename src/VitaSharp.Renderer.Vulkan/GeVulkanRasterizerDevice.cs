using VitaSharp.Core.Compat.Ge;
using VitaSharp.Core.Util;

namespace VitaSharp.Renderer.Vulkan;

/// <summary>
/// The GE's rasterizer for the running emulator: a <see cref="GeVulkanRasterizer"/> whose batches, submitted from the
/// compat core's thread, never share a queue with the GXM renderer's threads. The renderer offers its device when it
/// starts (<see cref="Offer"/>) and every GE gets <see cref="Create"/> (<see cref="GeEngine.DefaultRasterizerFactory"/>),
/// made only at its first batch:
/// <list type="bullet">
/// <item>the renderer's device has a second queue in its graphics family (<see cref="VkCore.SecondaryQueue"/>: Adreno,
/// Mali, NVIDIA, Intel): the GE shares the device and submits on that queue, with its own command pool, fence and
/// memory (counted in the renderer's budget, allocated without the renderer's reclaim, which belongs to its threads).
/// No second instance or device: mobile drivers limit both, and a device costs its creation time and its own
/// memory;</item>
/// <item>no second queue (one graphics queue: AMD desktop, the software drivers), no renderer, or
/// <c>VITASHARP_GE_DEVICE=own</c>: a device of its own, as before (the GE's work never waits behind the GXM queue).</item>
/// </list>
/// The shared device stays alive until the GE is done with it (<see cref="VkCore.AddUser"/>), also when the renderer
/// ended first; a renderer that loses its device makes a new one, which the next GE device takes.
/// </summary>
public sealed class GeVulkanRasterizerDevice : IGeRasterizer, IDisposable
{
    private static readonly object s_offerLock = new();
    private static VkCore? s_offered;

    private readonly VkCore _core;
    private readonly ShaderCompiler _compiler;
    private readonly GeVulkanRasterizer _rasterizer;

    private GeVulkanRasterizerDevice(VkCore core, ShaderCompiler compiler, GeVulkanRasterizer rasterizer, bool shared)
    {
        _core = core;
        _compiler = compiler;
        _rasterizer = rasterizer;
        Shared = shared;
    }

    /// <summary>The device is the renderer's (on its second queue), not one of its own.</summary>
    public bool Shared { get; }

    /// <summary>The renderer's device, offered to the GE devices made from now on (the renderer's start).</summary>
    public static void Offer(VkCore core)
    {
        lock (s_offerLock)
        {
            s_offered = core;
        }
    }

    /// <summary>The renderer ends with <paramref name="core"/>: the next GE devices do not take it (one that has it
    /// keeps it until it is done).</summary>
    public static void Withdraw(VkCore core)
    {
        lock (s_offerLock)
        {
            if (ReferenceEquals(s_offered, core))
            {
                s_offered = null;
            }
        }
    }

    /// <summary>The device and its rasterizer: on the renderer's second queue when there is one, else on a device of
    /// its own; null (logged) when Vulkan or the shader compiler is not available.</summary>
    public static IGeRasterizer? Create()
    {
        string? choice = Environment.GetEnvironmentVariable(EnvVars.GeDevice)?.Trim();
        if (!string.Equals(choice, "own", StringComparison.OrdinalIgnoreCase))
        {
            VkCore? offered;
            lock (s_offerLock)
            {
                offered = s_offered?.HasSecondaryQueue == true ? s_offered.AddUser() : null;
            }

            if (offered != null)
            {
                return CreateOn(offered, offered.SecondaryQueue, offered.SecondaryQueueLock);
            }
        }

        VkCore? core = null;
        try
        {
            core = new VkCore(null, false);
        }
        catch (Exception failure) when (failure is InvalidOperationException or DllNotFoundException or EntryPointNotFoundException)
        {
            Log.Error($"GE: no Vulkan rasterizer ({failure.Message}): the GE's draws are dropped");
            core?.Dispose();
            return null;
        }

        return CreateOn(core, core.Queue, core.SecondaryQueueLock, shared: false);
    }

    /// <summary>A rasterizer on <paramref name="core"/> (one user of it, released by <see cref="Dispose"/>) submitting
    /// on <paramref name="queue"/> under <paramref name="queueLock"/>. Tests: the shared path on a software device,
    /// which has one queue (nothing else submits there in the test).</summary>
    internal static GeVulkanRasterizerDevice? CreateOn(VkCore core, Silk.NET.Vulkan.Queue queue, object queueLock, bool shared = true)
    {
        ShaderCompiler? compiler = null;
        try
        {
            compiler = new ShaderCompiler(null);
            GeVulkanRasterizer rasterizer = new(core, compiler, queue, queueLock, reclaim: false);
            Log.Info($"GE: rasterizer on {core.DeviceName} ({(shared ? "the renderer's device, its second queue" : "a device of its own")})");
            return new GeVulkanRasterizerDevice(core, compiler, rasterizer, shared);
        }
        catch (Exception failure) when (failure is InvalidOperationException or DllNotFoundException or EntryPointNotFoundException)
        {
            Log.Error($"GE: no Vulkan rasterizer ({failure.Message}): the GE's draws are dropped");
            compiler?.Dispose();
            core.Dispose();
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
