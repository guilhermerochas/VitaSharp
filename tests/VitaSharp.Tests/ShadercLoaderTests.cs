using Silk.NET.Shaderc;
using System.Runtime.InteropServices;
using VitaSharp.Renderer.Vulkan;

namespace VitaSharp.Tests;

/// <summary>
/// <see cref="ShadercLoader"/>: shaderc loads even when Silk.NET's own search misses it. On the Ubuntu CI runner that
/// search looked for the bundled copy under runtimes/ubuntu.26.04-x64 (no fallback to linux-x64 for Ubuntu) and for
/// libshaderc_shared.so (the distribution names it libshaderc.so.1), so every renderer test was skipped there.
/// </summary>
public sealed class ShadercLoaderTests
{
    private static Shaderc DefaultSearchMisses() =>
        throw new FileNotFoundException("Could not load from any of the possible library names!");

    [Fact]
    public void BundledCopyIsTheFirstCandidateUnderThePortableRid()
    {
        string first = ShadercLoader.Candidates().First();
        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "runtimes", RuntimeInformation.RuntimeIdentifier, "native", ShadercLoader.BundledFileName), first);
        Assert.True(File.Exists(first), $"{first} is missing: restore the Silk.NET.Shaderc.Native package");
    }

    [Fact]
    public void LoadsWhenTheDefaultSearchMissesTheLibrary()
    {
        Shaderc api = ShadercLoader.Load(DefaultSearchMisses);
        unsafe
        {
            // the loaded library answers: a compiler is made (kept: glslang counts its users, see ShaderCompiler)
            Compiler* compiler = api.CompilerInitialize();
            Assert.True(compiler != null);
        }
    }

    [Fact]
    public void LinuxAlsoTriesTheDistributionsLibraryName()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        Assert.Contains("libshaderc.so.1", ShadercLoader.Candidates());
    }

    /// <summary>The CI runners install or bundle shaderc: a skip there means the renderer is not tested on that system.</summary>
    [Fact]
    public void ShadercIsAvailableOnTheCiRunners()
    {
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true")
        {
            return;
        }

        Assert.Null(ShadercAvailability.SkipReason);
    }
}
