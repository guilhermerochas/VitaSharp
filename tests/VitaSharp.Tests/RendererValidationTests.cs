using Microsoft.Win32;
using VitaSharp.Core.Gpu;
using VitaSharp.Core.Util;
using VitaSharp.Renderer.Vulkan;

namespace VitaSharp.Tests;

/// <summary>
/// The renderer workloads of <see cref="RendererTextureTests"/> and <see cref="RendererPathsTests"/> under the Khronos
/// validation layer with synchronization validation (VITASHARP_VK_VALIDATION=sync), on the software device through the
/// system loader: a validation error fails the test with the messages. Opt-in (Category Slow: the layer makes the
/// software device several times slower); skipped without the layer (the LunarG Vulkan SDK) or the loader.
/// </summary>
[Collection("guest")]
public sealed class RendererValidationTests(GuestMemoryFixture fx)
{
    private sealed class Collect : ILogSink
    {
        public readonly List<string> Lines = [];
        public void Write(LogLevel level, string thread, string message)
        {
            if (message.StartsWith("[Vulkan]") || message.Contains("WITHOUT validation"))
            {
                lock (Lines)
                {
                    Lines.Add(message);
                }
            }
        }
    }

    private static bool LayerInstalled()
    {
        const string Json = "VkLayer_khronos_validation.json";
        foreach (string v in new[] { "VK_LAYER_PATH", "VK_ADD_LAYER_PATH" })
        {
            if (Environment.GetEnvironmentVariable(v) is { Length: > 0 } dirs && dirs.Split(Path.PathSeparator).Any(d => File.Exists(Path.Combine(d, Json))))
            {
                return true;
            }
        }

        if (OperatingSystem.IsWindows())
        {
            using RegistryKey? key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Khronos\Vulkan\ExplicitLayers");
            return key?.GetValueNames().Any(n => n.EndsWith(Json, StringComparison.OrdinalIgnoreCase) && File.Exists(n)) == true;
        }
        return new[] { "/usr/share/vulkan/explicit_layer.d", "/etc/vulkan/explicit_layer.d", "/usr/local/share/vulkan/explicit_layer.d" }
            .Any(d => File.Exists(Path.Combine(d, Json)));
    }

    public sealed class ValidationFactAttribute : FactAttribute
    {
        public ValidationFactAttribute()
        {
            if (SoftwareVulkan.IcdManifest == null)
            {
                Skip = "no software Vulkan driver behind a system Vulkan loader";
            }
            else if (!LayerInstalled())
            {
                Skip = "the Khronos validation layer is not installed (LunarG Vulkan SDK)";
            }
        }
    }

    [ValidationFact]
    [Trait("Category", "Slow")]
    public void TextureFormatsMsaaAndDebugModeRunWithoutValidationErrors()
    {
        using IDisposable driver = SoftwareVulkan.UseThroughLoader();
        string? savedMode = Environment.GetEnvironmentVariable(EnvVars.VkValidation);
        Environment.SetEnvironmentVariable(EnvVars.VkValidation, "sync");
        var sink = new Collect();
        ILogSink? savedSink = Log.Sink;
        Log.Sink = sink;
        bool wasDebug = DebugMode.Enabled;
        try
        {
            foreach ((string msaa, ushort samples, bool debug) in new[] { ("native", (ushort)0, false), ("native", (ushort)2, true), ("ss", (ushort)2, false) })
            {
                if (debug)
                {
                    DebugMode.Enable();
                }
                else
                {
                    DebugMode.Disable();
                }

                var renderer = new VulkanRenderer(new VulkanRendererOptions { VSync = false, AsyncShaders = false, AsyncPipelines = false, Validation = true, Msaa = msaa });
                using var h = new HleHarness(fx, "vkvalid");
                try
                {
                    h.Emu.Gpu.Backend = renderer;
                    var w = new RendererTextureTests.Workload(h, samples, scaleMode: samples != 0 ? 1u : 0u);
                    w.Run(16);
                    w.Terminate();
                }
                finally
                {
                    h.Emu.Gpu.WaitIdle();
                    h.Emu.Gpu.Backend = null;
                    renderer.Dispose();
                }
            }
        }
        finally
        {
            Log.Sink = savedSink;
            Environment.SetEnvironmentVariable(EnvVars.VkValidation, savedMode);
            if (wasDebug)
            {
                DebugMode.Enable();
            }
            else
            {
                DebugMode.Disable();
            }
        }
        // the layer was loaded (the renderer warns when validation was asked for and the layer is missing), on the
        // software driver through the loader
        lock (sink.Lines)
        {
            Assert.DoesNotContain(sink.Lines, l => l.Contains("WITHOUT validation"));
        }

        Assert.False(VulkanLoader.CustomDriverLoaded);
        string[] errors;
        lock (sink.Lines)
        {
            errors = sink.Lines.Where(l => l.Contains("Validation Error", StringComparison.OrdinalIgnoreCase) || l.Contains("SYNC-HAZARD")).ToArray();
        }

        Assert.True(errors.Length == 0, $"{errors.Length} validation errors:\n" + string.Join("\n", errors.Take(30)));
    }
}
