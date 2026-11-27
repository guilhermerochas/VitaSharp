using VitaSharp.Core.Util;
using VitaSharp.Desktop;
using VitaSharp.Renderer.Vulkan;

namespace VitaSharp.Tests;

/// <summary>
/// The desktop's phone profile (<see cref="PhoneProfile"/>, --phone-profile / VITASHARP_PHONE_PROFILE=1,
/// build/phone-profile): it turns on what the Android build does (waits without spinning, the Mali GPU profile, the
/// 333 MHz guest clock, the 4 MB GC gen0 budget, two host cores), keeps what the environment already says, starts the
/// process again when the GC setting is missing (the runtime reads it only at start), reports the runtime and the data
/// folder's case sensitivity, and an F8 capture records that it was on.
/// </summary>
[Collection("guest")]
public sealed class PhoneProfileTests(GuestMemoryFixture fixture) : IDisposable
{
    private readonly HleHarness _harness = new(fixture, "phoneprofile");

    public void Dispose()
    {
        PhoneProfile.ResetForTests();
        _harness.Dispose();
    }

    [Fact]
    public void TheProfileIsAskedForByOnValues()
    {
        Assert.True(PhoneProfile.IsRequested("1"));
        Assert.True(PhoneProfile.IsRequested(" On "));
        Assert.True(PhoneProfile.IsRequested("TRUE"));
        Assert.True(PhoneProfile.IsRequested("yes"));
        Assert.False(PhoneProfile.IsRequested(null));
        Assert.False(PhoneProfile.IsRequested(""));
        Assert.False(PhoneProfile.IsRequested("0"));
        Assert.False(PhoneProfile.IsRequested("off"));
    }

    /// <summary>Every option of the profile is on afterwards; a value already set in the environment wins.</summary>
    [Fact]
    public void ApplyTurnsEveryOptionOnAndKeepsExplicitValues()
    {
        PhoneProfile.ResetForTests();
        var env = new Dictionary<string, string> { [EnvVars.GpuProfile] = "adreno" };

        PhoneProfile.Apply(name => env.GetValueOrDefault(name), (name, value) => env[name] = value);

        Assert.True(PhoneProfile.Active);
        Assert.True(HostTuning.AvoidBusyWaits);
        Assert.Equal("adreno", env[EnvVars.GpuProfile]);
        Assert.Equal("333", env[EnvVars.CpuMhz]);
        Assert.Equal("0x400000", env[PhoneProfile.Gen0Variable]);

        env.Remove(EnvVars.GpuProfile);
        env[EnvVars.CpuMhz] = "444";
        PhoneProfile.Apply(name => env.GetValueOrDefault(name), (name, value) => env[name] = value);
        Assert.Equal("mali", env[EnvVars.GpuProfile]);
        Assert.Equal("444", env[EnvVars.CpuMhz]);
    }

    /// <summary>The gen0 budget can only come from the environment the runtime started with.</summary>
    [Fact]
    public void AMissingGcBudgetNeedsAFreshProcess()
    {
        Assert.True(PhoneProfile.NeedsRestartForGc(_ => null));
        Assert.True(PhoneProfile.NeedsRestartForGc(_ => ""));
        Assert.False(PhoneProfile.NeedsRestartForGc(name => name == PhoneProfile.Gen0Variable ? "0x400000" : null));
    }

    /// <summary>Two cores by default (upper half, CPUs n-4 and n-2), or the mask asked for, kept to existing CPUs.</summary>
    [Fact]
    public void TheCoresAreTwoByDefaultOrTheMaskAskedFor()
    {
        Assert.Equal(0x50UL, PhoneProfile.DefaultAffinity(8));
        Assert.Equal(0x5000UL, PhoneProfile.DefaultAffinity(16));
        Assert.Equal(0x5UL, PhoneProfile.DefaultAffinity(4));
        Assert.Equal(0x3UL, PhoneProfile.DefaultAffinity(2));
        Assert.Equal(0x1UL, PhoneProfile.DefaultAffinity(1));
        if (Environment.ProcessorCount >= 2)
        {
            Assert.Equal(2UL, ulong.PopCount(PhoneProfile.DefaultAffinity(Environment.ProcessorCount)));
        }

        Assert.Equal(0x50UL, PhoneProfile.ParseAffinity(null, 8));
        Assert.Equal(0x30UL, PhoneProfile.ParseAffinity("0x30", 8));
        Assert.Equal(0xF0UL, PhoneProfile.ParseAffinity("240", 8));
        Assert.Equal(0x0UL, PhoneProfile.ParseAffinity("0", 8));
        Assert.Equal(0xFFUL, PhoneProfile.ParseAffinity("0xFFFF", 8));
        Assert.Equal(0x50UL, PhoneProfile.ParseAffinity("soon", 8));
    }

    /// <summary>The probe tells a folder that tells names apart by case from one that does not (the usual temporary
    /// folder: case-insensitive on Windows and macOS, case-sensitive on Linux).</summary>
    [Fact]
    public void TheDataFolderCaseSensitivityIsChecked()
    {
        string folder = Path.Combine(_harness.Root, "casecheck");
        bool? sensitive = PhoneProfile.CheckCaseSensitive(folder);
        Assert.Equal(OperatingSystem.IsLinux(), sensitive);
        Assert.Empty(Directory.EnumerateFileSystemEntries(folder));
    }

    /// <summary>The log line and the F8 capture's notes say the profile was on and with what; off without it.</summary>
    [Fact]
    public void TheCaptureRecordsThatTheProfileWasOn()
    {
        PhoneProfile.ResetForTests();
        var env = new Dictionary<string, string>();
        Assert.Equal("off", PhoneProfile.Describe(name => env.GetValueOrDefault(name)));
        using (var off = new SessionCaptureControl(_harness.Emu, new VulkanRendererOptions(), () => null, [], null, null, Options()))
        {
            Assert.Contains("phone-profile: off", off.Capture.Options.Info!());
        }

        PhoneProfile.Apply(name => env.GetValueOrDefault(name), (name, value) => env[name] = value);
        PhoneProfile.ApplyAffinity(0);
        PhoneProfile.CheckDataFolder(Path.Combine(_harness.Root, "data"));
        string line = PhoneProfile.Describe(name => env.GetValueOrDefault(name));
        Assert.StartsWith("on: waits without spinning", line);
        Assert.Contains("GPU profile mali", line);
        Assert.Contains("guest clock 333 MHz", line);
        Assert.Contains("not pinned (VITASHARP_PHONE_AFFINITY=0)", line);
        Assert.Contains("GC gen0 budget 0x400000", line);
        Assert.Contains("runtime", line);
        Assert.Contains("data folder", line);

        using var on = new SessionCaptureControl(_harness.Emu, new VulkanRendererOptions(), () => null, [], null, null, Options());
        Assert.Contains("phone-profile: on: waits without spinning", on.Capture.Options.Info!());
    }

    private Core.Capture.SessionCaptureOptions Options() => new()
    {
        Root = Path.Combine(_harness.Root, "captures"),
        SetPresentTap = _ => { },
        StartState = false,
        GpuDump = false,
        Zip = false,
    };
}
