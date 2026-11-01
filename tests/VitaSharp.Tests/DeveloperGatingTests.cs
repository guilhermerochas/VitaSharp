using System.Reflection;

namespace VitaSharp.Tests;

/// <summary>
/// The developer tooling (namespaces *.Developer, the renderer's Dev* bench hooks) is compiled only with
/// -p:VitaSharpDeveloper=true (Directory.Build.props): a normal build has none of it. build/check-dev-gating.* checks
/// a packaged APK or desktop folder the same way.
/// </summary>
public class DeveloperGatingTests
{
    static readonly Assembly[] Assemblies = [typeof(Core.Gpu.GpuDumpReader).Assembly, typeof(Renderer.Vulkan.VulkanRenderer).Assembly];

    [Fact]
    public void NormalBuildHasNoDeveloperTooling()
    {
        var types = Assemblies.SelectMany(a => a.GetTypes()).Where(t => t.Namespace?.EndsWith(".Developer", StringComparison.Ordinal) == true).Select(t => t.FullName).ToList();
        var hooks = typeof(Renderer.Vulkan.VulkanRenderer).GetMembers(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(m => m.Name.StartsWith("Dev", StringComparison.Ordinal) && !m.Name.StartsWith("Device", StringComparison.Ordinal)).Select(m => m.Name).ToList();
#if VITASHARP_DEVELOPER
        Assert.NotEmpty(types);
        Assert.NotEmpty(hooks);
#else
        Assert.Empty(types);
        Assert.Empty(hooks);
#endif
    }
}
