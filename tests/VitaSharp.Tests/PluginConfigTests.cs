using VitaSharp.Core.Loader;

namespace VitaSharp.Tests;

/// <summary>
/// The plugin configuration read with taiHEN's rules (taihen-parser lexer.c/parser.c, taiHEN plugin.c/hen.c): exact
/// section names, ALL for every process but not the kernel, the '!' halt, files taiHEN refuses as a whole, and
/// ux0:tai before ur0:tai.
/// </summary>
[Collection("guest")]
public sealed class PluginConfigTests(GuestMemoryFixture fixture)
{
    [Fact]
    public void SectionsMatchExactlyAndAllIsNotForTheKernel()
    {
        const string Config = "# comment\n*KERNEL\nur0:tai/k.skprx\n*ALL\nux0:tai/all.suprx\n*PCSA00001\n  ux0:tai/my plugin.suprx  \n*pcsa00001\nux0:tai/lower.suprx\n*main\nur0:tai/shell.suprx\n";
        Assert.Equal(["ur0:tai/k.skprx"], PluginConfig.ModulesFor(Config, "KERNEL"));
        Assert.Equal(["ux0:tai/all.suprx", "ux0:tai/my plugin.suprx"], PluginConfig.ModulesFor(Config, "PCSA00001"));
        Assert.Equal(["ux0:tai/all.suprx", "ur0:tai/shell.suprx"], PluginConfig.ModulesFor(Config, "main"));
    }

    [Fact]
    public void HaltSectionEndsTheReadingAtTheNextSection()
    {
        const string Config = "*ALL\na.suprx\n* ! PCSA00001\nb.suprx\n*ALL\nc.suprx\n*KERNEL\nk.skprx\n";
        Assert.Equal(["a.suprx", "b.suprx"], PluginConfig.ModulesFor(Config, "PCSA00001"));
        // a halt section that does not match does not stop anything
        Assert.Equal(["a.suprx", "c.suprx"], PluginConfig.ModulesFor(Config, "PCSB00002"));
        Assert.Equal(["k.skprx"], PluginConfig.ModulesFor(Config, "KERNEL"));
    }

    [Fact]
    public void CrlfAndWhitespaceAreLikeTaihenReadsThem()
    {
        const string Config = "\r\n\t*KERNEL \r\n\tur0:tai/a.skprx\t\r\n#*ALL\r\n";
        Assert.True(PluginConfig.IsValid(Config));
        Assert.Equal(["ur0:tai/a.skprx"], PluginConfig.ModulesFor(Config, "KERNEL"));
    }

    [Theory]
    [InlineData("ux0:tai/early.suprx\n*ALL\nux0:tai/a.suprx\n")] // a path before the first section
    [InlineData("*\nux0:tai/a.suprx\n")]                         // a section without a name
    [InlineData("*!\nux0:tai/a.suprx\n")]
    public void InvalidFilesLoadNothing(string config)
    {
        Assert.False(PluginConfig.IsValid(config));
        Assert.Empty(PluginConfig.ModulesFor(config, "PCSA00001"));
    }

    [Fact]
    public void ALineOfTheMaximumLengthMakesTheFileInvalid()
    {
        string ok = "*ALL\n" + new string('a', PluginConfig.MaxLineLength - 1) + "\n";
        string tooLong = "*ALL\n" + new string('a', PluginConfig.MaxLineLength) + "\n";
        Assert.True(PluginConfig.IsValid(ok));
        Assert.False(PluginConfig.IsValid(tooLong));
    }

    [Fact]
    public void Ux0ConfigComesBeforeUr0()
    {
        Assert.Equal(["ux0:tai/config.txt", "ur0:tai/config.txt"], PluginConfig.Paths);
    }

    /// <summary>The emulator loads the plugins of the file with these rules: the '!' section and the exact title ID
    /// (the old reading took "!KERNEL" for a name and compared title IDs ignoring case).</summary>
    [Fact]
    public void EmulatorLoadsThePluginsTaihenWouldLoad()
    {
        using var harness = new HleHarness(fixture, "taicfg");
        string ux0 = Path.Combine(harness.Root, "ux0", "tai");
        string ur0 = Path.Combine(harness.Root, "ur0", "tai");
        Directory.CreateDirectory(ux0);
        Directory.CreateDirectory(ur0);
        File.WriteAllText(Path.Combine(ux0, "config.txt"), "*!KERNEL\nur0:tai/first.skprx\n*KERNEL\nur0:tai/second.skprx\n*pcsa00001\nux0:tai/lower.suprx\n");
        File.WriteAllText(Path.Combine(ur0, "config.txt"), "*KERNEL\nur0:tai/recovery.skprx\n");
        List<string> kernel = harness.Emu.LoadKernelPlugins();
        Assert.Single(kernel);
        Assert.StartsWith("ur0:tai/first.skprx: ", kernel[0]);
        Assert.Empty(harness.Emu.LoadUserPlugins("PCSA00001"));
    }
}
