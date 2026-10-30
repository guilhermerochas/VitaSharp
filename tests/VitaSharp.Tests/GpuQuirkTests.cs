using System.IO.Compression;
using VitaSharp.Core.Gpu;

namespace VitaSharp.Tests;

/// <summary>
/// The mobile GPU quirk layer (Core/Gpu/GpuQuirks.cs): which workarounds a vendor/driver/version tuple selects, what
/// the emulated --gpu-profile classes remove, the VITASHARP_GPU_QUIRKS overrides, and the custom driver package store.
/// </summary>
public sealed class GpuQuirkTests
{
    const GpuFeature Desktop = GpuFeature.TextureCompressionBC | GpuFeature.ShaderStorageImageWriteWithoutFormat | GpuFeature.SampleRateShading
        | GpuFeature.DepthClamp | GpuFeature.FillModeNonSolid | GpuFeature.GeometryShader | GpuFeature.LogicOp | GpuFeature.Storage16Bit
        | GpuFeature.WideLines | GpuFeature.OcclusionQueryPrecise | GpuFeature.MemoryBudget | GpuFeature.Timestamps | GpuFeature.PushDescriptor
        | GpuFeature.IndependentBlend | GpuFeature.DepthBiasClamp | GpuFeature.SamplerMirrorClampToEdge | GpuFeature.SamplerAnisotropy
        | GpuFeature.DynamicRendering | GpuFeature.ExtendedDynamicState | GpuFeature.ShaderClipDistance;

    static readonly GpuLimits DesktopLimits = new()
    {
        MaxPushConstantsSize = 256, MaxBoundDescriptorSets = 32, MaxPerStageDescriptorSamplers = 1048576, MaxPerStageDescriptorSampledImages = 1048576,
        MaxPerStageDescriptorStorageBuffers = 1048576, MaxPerStageDescriptorInputAttachments = 1048576, MaxPerStageResources = 4294967295,
        MaxDescriptorSetSamplers = 1048576, FramebufferSampleCounts = 1 | 2 | 4 | 8, MaxImageDimension2D = 32768,
    };

    static GpuDeviceInfo Device(uint vendor, uint driverId, uint version, GpuFeature? features = null, GpuLimits? limits = null, bool unified = false) => new()
    {
        VendorId = vendor, DeviceId = 0x1234, DriverId = driverId, DriverVersion = version, ApiVersion = GpuQuirkTable.MakeVersion(1, 3, 250),
        DeviceName = "test GPU", Features = features ?? Desktop, Limits = limits ?? DesktopLimits, UnifiedMemory = unified,
    };

    // GTX 1650 on 5xx drivers (NVIDIA encodes 10.8.8.6 bits)
    static readonly GpuDeviceInfo Nvidia = Device(GpuQuirkTable.VendorNvidia, GpuQuirkTable.DriverNvidiaProprietary, (566u << 22) | (36u << 14));
    static readonly GpuDeviceInfo AdrenoBlob = Device(GpuQuirkTable.VendorQualcomm, GpuQuirkTable.DriverQualcommProprietary, GpuQuirkTable.MakeVersion(512, 744),
        Desktop & ~(GpuFeature.TextureCompressionBC | GpuFeature.MemoryBudget), DesktopLimits with { FramebufferSampleCounts = 1 | 2 | 4 }, unified: true);

    [Fact]
    public void DesktopNvidiaHasNoQuirks()
    {
        var c = GpuQuirkTable.Select(Nvidia);
        Assert.Equal(GpuVendor.Nvidia, c.Vendor);
        Assert.Equal(GpuDriver.NvidiaProprietary, c.Driver);
        Assert.Equal(GpuQuirk.None, c.Quirks);
        Assert.Equal(Desktop, c.Features);
        Assert.Equal(GpuFeature.None, c.Masked);
        Assert.Equal(2, c.MsaaSamples(2));
        Assert.Equal(4, c.MsaaSamples(4));
        Assert.Equal(0.8, c.FallbackBudgetFraction);
    }

    [Fact]
    public void IntelIntegratedOnWindowsHasNoQuirks()
    {
        // Iris Xe: unified memory, but VK_EXT_memory_budget present and an immediate-mode renderer
        var c = GpuQuirkTable.Select(Device(GpuQuirkTable.VendorIntel, GpuQuirkTable.DriverIntelWindows, (101u << 14) | 5333, unified: true));
        Assert.Equal(GpuDriver.IntelWindows, c.Driver);
        Assert.Equal(GpuQuirk.None, c.Quirks);
    }

    [Fact]
    public void AdrenoProprietarySelectsTheBlobWorkarounds()
    {
        var c = GpuQuirkTable.Select(AdrenoBlob);
        Assert.Equal(GpuVendor.Qualcomm, c.Vendor);
        Assert.Equal(GpuDriver.QualcommProprietary, c.Driver);
        foreach (var q in new[] { GpuQuirk.TileBased, GpuQuirk.DynamicIndexLowering, GpuQuirk.StaticExtendedDynamicState, GpuQuirk.StaticDepthBiasEnable,
                     GpuQuirk.AvoidPushDescriptors, GpuQuirk.AvoidClearAttachments, GpuQuirk.PipelineCachePerDriverBuild, GpuQuirk.CustomDriverCapable,
                     GpuQuirk.DynamicRenderingSuspect, GpuQuirk.ConservativeMemoryBudget })
            Assert.True(c.Has(q), q.ToString());
        Assert.False(c.Has(GpuQuirk.TurnipDriver));
        Assert.False(c.Has(GpuQuirk.Msaa2xAs4x));
        Assert.Equal("512.744", GpuCapabilities.FormatDriverVersion(c.Vendor, c.Driver, c.DriverVersion));
        Assert.Contains(c.QuirkReasons, r => r.StartsWith("StaticExtendedDynamicState:") && r.Contains("yuzu"));
    }

    [Theory]
    [InlineData(GpuQuirkTable.DriverTurnip, 24u)]  // driverID MESA_TURNIP
    [InlineData(0u, 24u)]                           // no driver properties: Mesa-style major version (< 100)
    public void TurnipGetsNoProprietaryWorkarounds(uint driverId, uint major)
    {
        var c = GpuQuirkTable.Select(Device(GpuQuirkTable.VendorQualcomm, driverId, GpuQuirkTable.MakeVersion(major, 1, 0)));
        Assert.Equal(GpuDriver.Turnip, c.Driver);
        Assert.True(c.Has(GpuQuirk.TurnipDriver));
        Assert.True(c.Has(GpuQuirk.TileBased));
        Assert.True(c.Has(GpuQuirk.CustomDriverCapable));
        Assert.True(c.Has(GpuQuirk.PipelineCachePerDriverBuild));
        Assert.False(c.Has(GpuQuirk.DynamicIndexLowering));
        Assert.False(c.Has(GpuQuirk.StaticExtendedDynamicState));
        Assert.False(c.Has(GpuQuirk.AvoidPushDescriptors));
    }

    [Fact]
    public void QualcommWithoutDriverPropertiesAndBlobVersionIsProprietary()
    {
        Assert.Equal(GpuDriver.QualcommProprietary, GpuQuirkTable.DriverOf(GpuQuirkTable.VendorQualcomm, 0, GpuQuirkTable.MakeVersion(512, 615)));
        Assert.Equal(GpuDriver.Turnip, GpuQuirkTable.DriverOf(GpuQuirkTable.VendorQualcomm, 0, GpuQuirkTable.MakeVersion(99, 0)));
    }

    [Fact]
    public void MaliWithoutTwoSamplesRendersTwoXAsFour()
    {
        var c = GpuQuirkTable.Select(Device(GpuQuirkTable.VendorArm, GpuQuirkTable.DriverArmProprietary, GpuQuirkTable.MakeVersion(38, 1),
            Desktop & ~GpuFeature.TextureCompressionBC, DesktopLimits with { FramebufferSampleCounts = 1 | 4 }));
        Assert.Equal(GpuDriver.ArmProprietary, c.Driver);
        Assert.True(c.Has(GpuQuirk.TileBased));
        Assert.True(c.Has(GpuQuirk.Msaa2xAs4x));
        Assert.True(c.Has(GpuQuirk.AvoidPushDescriptors));
        Assert.True(c.Has(GpuQuirk.DynamicRenderingSuspect));
        Assert.False(c.Has(GpuQuirk.DynamicIndexLowering));
        Assert.False(c.Has(GpuQuirk.StaticExtendedDynamicState));
        Assert.False(c.Has(GpuQuirk.CustomDriverCapable));
        Assert.Equal(4, c.MsaaSamples(2));
        Assert.Equal(4, c.MsaaSamples(4));
    }

    [Fact]
    public void NoMultisamplingAtAllFallsBack()
    {
        var c = GpuQuirkTable.Select(Device(GpuQuirkTable.VendorArm, GpuQuirkTable.DriverArmProprietary, 1, limits: DesktopLimits with { FramebufferSampleCounts = 1 }));
        Assert.False(c.Has(GpuQuirk.Msaa2xAs4x));
        Assert.Equal(0, c.MsaaSamples(2));
        Assert.Equal(0, c.MsaaSamples(4));
    }

    [Fact]
    public void PowerVrIsTileBasedWithMobileBlobQuirks()
    {
        var c = GpuQuirkTable.Select(Device(GpuQuirkTable.VendorImagination, GpuQuirkTable.DriverImaginationProprietary, GpuQuirkTable.MakeVersion(1, 426, 234)));
        Assert.Equal(GpuVendor.Imagination, c.Vendor);
        Assert.True(c.Has(GpuQuirk.TileBased));
        Assert.True(c.Has(GpuQuirk.AvoidPushDescriptors));
        Assert.False(c.Has(GpuQuirk.AvoidClearAttachments));
    }

    [Theory]
    [InlineData(GpuQuirkTable.VendorSamsung)]
    [InlineData(GpuQuirkTable.VendorAmd)]
    public void SamsungXclipseIsImmediateMode(uint vendor)
    {
        var c = GpuQuirkTable.Select(Device(vendor, GpuQuirkTable.DriverSamsungProprietary, GpuQuirkTable.MakeVersion(2, 0)));
        Assert.Equal(GpuDriver.SamsungProprietary, c.Driver);
        Assert.False(c.Has(GpuQuirk.TileBased));
        Assert.Equal(GpuQuirk.None, c.Quirks);
    }

    [Fact]
    public void AdrenoProfileOnDesktopRemovesFeaturesAndActsAsTheBlob()
    {
        var c = GpuQuirkTable.Select(Nvidia, GpuProfile.Adreno);
        Assert.Equal(GpuVendor.Qualcomm, c.Vendor);
        Assert.Equal(GpuDriver.QualcommProprietary, c.Driver);
        Assert.False(c.Has(GpuFeature.TextureCompressionBC));
        Assert.False(c.Has(GpuFeature.SampleRateShading));
        Assert.False(c.Has(GpuFeature.MemoryBudget));
        Assert.False(c.Has(GpuFeature.ShaderStorageImageWriteWithoutFormat));
        Assert.True(c.Has(GpuFeature.DepthClamp));
        Assert.True(c.Has(GpuFeature.Timestamps));
        Assert.True((c.Masked & GpuFeature.TextureCompressionBC) != 0);
        Assert.Equal(128u, c.Limits.MaxPushConstantsSize);
        Assert.Equal(1u | 2 | 4, c.Limits.FramebufferSampleCounts);
        Assert.True(c.Has(GpuQuirk.StaticExtendedDynamicState));
        Assert.True(c.Has(GpuQuirk.DynamicIndexLowering));
        Assert.True(c.Has(GpuQuirk.TileBased));
        Assert.True(c.Has(GpuQuirk.ConservativeMemoryBudget));
        Assert.Contains("emulating Qualcomm", c.Describe());
    }

    [Fact]
    public void MaliProfileOnDesktopShrinksLimits()
    {
        var c = GpuQuirkTable.Select(Nvidia, GpuProfile.Mali);
        Assert.Equal(GpuVendor.Arm, c.Vendor);
        // as the Mali-G57 MC2 (r54) reports: depth clamp but no clip distance, so the far plane is a fragment discard
        Assert.True(c.Has(GpuFeature.DepthClamp));
        Assert.False(c.Has(GpuFeature.ShaderClipDistance));
        Assert.True(c.Has(GpuQuirk.FarPlaneDiscard));
        Assert.False(c.Has(GpuFeature.TextureCompressionBC));
        Assert.False(c.Has(GpuFeature.FillModeNonSolid));
        Assert.Equal(16u, c.Limits.MaxPerStageDescriptorSamplers);
        Assert.Equal(4u, c.Limits.MaxBoundDescriptorSets);
        Assert.Equal(1u | 4, c.Limits.FramebufferSampleCounts);
        Assert.True(c.Has(GpuQuirk.Msaa2xAs4x));
        Assert.False(c.Has(GpuQuirk.StaticExtendedDynamicState));
    }

    [Fact]
    public void FarPlaneWithoutClipDistance()
    {
        var mali = GpuFeature.DepthClamp | GpuFeature.DynamicRendering | GpuFeature.ExtendedDynamicState;
        // depth clamp, no clip distance: fragment discard
        var c = GpuQuirkTable.Select(Device(GpuQuirkTable.VendorArm, GpuQuirkTable.DriverArmProprietary, GpuQuirkTable.MakeVersion(54, 1), mali));
        Assert.True(c.Has(GpuQuirk.FarPlaneDiscard));
        Assert.True(c.Has(GpuFeature.DepthClamp));
        Assert.Contains("FarPlaneDiscard", c.Describe());
        // forced off: no depth clamp, the hardware clips both planes
        var off = GpuQuirkTable.Select(Device(GpuQuirkTable.VendorArm, GpuQuirkTable.DriverArmProprietary, 1, mali), overrides: "-FarPlaneDiscard");
        Assert.False(off.Has(GpuQuirk.FarPlaneDiscard));
        Assert.False(off.Has(GpuFeature.DepthClamp));
        Assert.True((off.Masked & GpuFeature.DepthClamp) != 0);
        // neither: the hardware clips the far plane, nothing to emulate
        var none = GpuQuirkTable.Select(Device(GpuQuirkTable.VendorArm, GpuQuirkTable.DriverArmProprietary, 1, GpuFeature.DynamicRendering));
        Assert.False(none.Has(GpuQuirk.FarPlaneDiscard));
        // a desktop GPU has both: clip distance, no quirk
        Assert.False(GpuQuirkTable.Select(Nvidia).Has(GpuQuirk.FarPlaneDiscard));
        // forced on a desktop GPU (testing the discard path)
        Assert.True(GpuQuirkTable.Select(Nvidia, overrides: "+FarPlaneDiscard").Has(GpuQuirk.FarPlaneDiscard));
    }

    [Fact]
    public void ProfilesNeverAddWhatTheDeviceLacks()
    {
        var weak = Device(GpuQuirkTable.VendorNvidia, GpuQuirkTable.DriverNvidiaProprietary, 1, GpuFeature.Timestamps, DesktopLimits with { FramebufferSampleCounts = 1 | 4, MaxPerStageDescriptorSamplers = 8 });
        var c = GpuQuirkTable.Select(weak, GpuProfile.Adreno);
        Assert.Equal(GpuFeature.Timestamps, c.Features);
        Assert.Equal(1u | 4, c.Limits.FramebufferSampleCounts);
        Assert.Equal(8u, GpuQuirkTable.Select(weak, GpuProfile.Mali).Limits.MaxPerStageDescriptorSamplers);
    }

    [Fact]
    public void DesktopProfileOnAPhoneDisablesVendorQuirks()
    {
        var c = GpuQuirkTable.Select(AdrenoBlob, GpuProfile.Desktop);
        Assert.Equal(GpuDriver.QualcommProprietary, c.Driver);
        Assert.False(c.Has(GpuQuirk.StaticExtendedDynamicState));
        Assert.False(c.Has(GpuQuirk.TileBased));
        Assert.False(c.Has(GpuFeature.TextureCompressionBC)); // the device still lacks it
    }

    [Fact]
    public void OverridesForceQuirksAndRemoveFeatures()
    {
        var c = GpuQuirkTable.Select(AdrenoBlob, GpuProfile.Native, "-StaticExtendedDynamicState, +Msaa2xAs4x ,-timestamps,bogus,+SampleRateShading");
        Assert.False(c.Has(GpuQuirk.StaticExtendedDynamicState));
        Assert.True(c.Has(GpuQuirk.Msaa2xAs4x));
        Assert.True(c.Has(GpuQuirk.DynamicIndexLowering));
        Assert.False(c.Has(GpuFeature.Timestamps));
        Assert.True((c.Masked & GpuFeature.Timestamps) != 0);
        // features can only be removed: "+SampleRateShading" names no quirk
        Assert.Equal(["bogus", "+SampleRateShading"], c.UnknownOverrides);
        Assert.Contains(c.QuirkReasons, r => r.StartsWith("StaticExtendedDynamicState: forced off"));
        Assert.DoesNotContain(c.QuirkReasons, r => r.StartsWith("StaticExtendedDynamicState: Qualcomm"));
        Assert.Contains("'bogus' is not a quirk", c.Describe());

        var on = GpuQuirkTable.Select(Nvidia, GpuProfile.Native, "TileBased");
        Assert.Equal(GpuQuirk.TileBased, on.Quirks);
    }

    [Fact]
    public void Vulkan11DeviceUsesRenderPassesAndStaticState()
    {
        var old = Device(GpuQuirkTable.VendorQualcomm, GpuQuirkTable.DriverQualcommProprietary, GpuQuirkTable.MakeVersion(512, 454),
            Desktop & ~(GpuFeature.DynamicRendering | GpuFeature.ExtendedDynamicState | GpuFeature.TextureCompressionBC)) with { ApiVersion = GpuQuirkTable.MakeVersion(1, 1, 128) };
        var c = GpuQuirkTable.Select(old);
        Assert.True(c.Has(GpuQuirk.RenderPasses));
        Assert.True(c.Has(GpuQuirk.StaticExtendedDynamicState));
        Assert.True(c.Has(GpuQuirk.StaticDepthBiasEnable));
        Assert.Contains(c.QuirkReasons, r => r.StartsWith("RenderPasses:"));
        // what the device cannot do is not overridable; Turnip on the same phone (no blob quirks) still needs them
        Assert.True(GpuQuirkTable.Select(old, GpuProfile.Native, "-RenderPasses,-StaticExtendedDynamicState").Has(GpuQuirk.RenderPasses | GpuQuirk.StaticExtendedDynamicState));
        var turnip = GpuQuirkTable.Select(old with { DriverId = GpuQuirkTable.DriverTurnip, DriverVersion = GpuQuirkTable.MakeVersion(24, 1) });
        Assert.True(turnip.Has(GpuQuirk.RenderPasses | GpuQuirk.StaticExtendedDynamicState));
    }

    [Fact]
    public void RenderPassesFollowSuspectDriversAndOverrides()
    {
        Assert.False(GpuQuirkTable.Select(Nvidia).Has(GpuQuirk.RenderPasses));
        Assert.Equal(GpuQuirk.RenderPasses, GpuQuirkTable.Select(Nvidia, GpuProfile.Native, "+RenderPasses").Quirks);
        // proprietary mobile blobs with suspect dynamic rendering get render passes (DuckStation); a phone whose
        // driver has working dynamic rendering can turn them off
        Assert.True(GpuQuirkTable.Select(AdrenoBlob).Has(GpuQuirk.RenderPasses));
        Assert.False(GpuQuirkTable.Select(AdrenoBlob, GpuProfile.Native, "-RenderPasses").Has(GpuQuirk.RenderPasses));
        Assert.False(GpuQuirkTable.Select(Device(GpuQuirkTable.VendorQualcomm, GpuQuirkTable.DriverTurnip, GpuQuirkTable.MakeVersion(24, 1))).Has(GpuQuirk.RenderPasses));
    }

    [Fact]
    public void AdrenoVulkan11ProfileRemovesDynamicRendering()
    {
        var c = GpuQuirkTable.Select(Nvidia, GpuProfile.AdrenoVulkan11);
        Assert.Equal(GpuVendor.Qualcomm, c.Vendor);
        Assert.False(c.Has(GpuFeature.DynamicRendering));
        Assert.False(c.Has(GpuFeature.ExtendedDynamicState));
        Assert.False(c.Has(GpuFeature.TextureCompressionBC));
        Assert.True(c.Has(GpuQuirk.RenderPasses | GpuQuirk.StaticExtendedDynamicState | GpuQuirk.StaticDepthBiasEnable | GpuQuirk.TileBased));
        Assert.Contains("emulating Qualcomm", c.Describe());
    }

    [Theory]
    [InlineData("adreno-vk11", GpuProfile.AdrenoVulkan11)]
    [InlineData("adreno", GpuProfile.Adreno)]
    [InlineData(" Mali ", GpuProfile.Mali)]
    [InlineData("desktop", GpuProfile.Desktop)]
    [InlineData("", GpuProfile.Native)]
    [InlineData(null, GpuProfile.Native)]
    [InlineData("powervr", GpuProfile.Native)]
    public void ParsesProfiles(string? s, GpuProfile p) => Assert.Equal(p, GpuQuirkTable.ParseProfile(s));

    [Fact]
    public void DescribeListsFeaturesLimitsAndQuirks()
    {
        var d = GpuQuirkTable.Select(Nvidia, GpuProfile.Adreno).Describe();
        Assert.Contains("GPU profile: adreno", d);
        Assert.Contains("TextureCompressionBC(profile)", d);
        Assert.Contains("GPU limits: pushConstants 128", d);
        Assert.Contains("GPU quirks: TileBased DynamicIndexLowering", d);
    }

    // ------------------------------------------------------------------ custom drivers

    static string MakeZip(string dir, string meta, params string[] files)
    {
        string zip = Path.Combine(dir, $"pkg{Guid.NewGuid():N}.zip");
        using var z = ZipFile.Open(zip, ZipArchiveMode.Create);
        using (var w = new StreamWriter(z.CreateEntry("meta.json").Open())) w.Write(meta);
        foreach (var f in files) using (var w = new StreamWriter(z.CreateEntry(f).Open())) w.Write("ELF");
        return zip;
    }

    const string TurnipMeta = """{"schemaVersion":1,"name":"Turnip v24.1","description":"Mesa Turnip","author":"K11MCH1","packageVersion":"1","vendor":"Mesa","driverVersion":"Vulkan 1.3.274","minApi":27,"libraryName":"vulkan.ad07XX.so"}""";

    [Fact]
    public void DriverStoreInstallsListsAndRemoves()
    {
        string root = Path.Combine(Path.GetTempPath(), $"vs-drivers-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var store = new CustomDriverStore(Path.Combine(root, "gpu_drivers"));
            var d = store.Install(MakeZip(root, TurnipMeta, "vulkan.ad07XX.so", "../evil.so", "sub/x.so"), deviceApiLevel: 33);
            Assert.Equal("Turnip_v24.1", d.Id);
            Assert.True(File.Exists(d.LibraryPath));
            Assert.EndsWith(Path.DirectorySeparatorChar.ToString(), d.Directory);
            Assert.False(File.Exists(Path.Combine(store.Root, "evil.so")));
            Assert.False(Directory.Exists(Path.Combine(d.Directory, "sub")));
            var list = store.List();
            Assert.Single(list);
            Assert.Equal("Vulkan 1.3.274", list[0].Meta.DriverVersion);
            Assert.NotNull(store.Find("Turnip_v24.1"));
            Assert.Null(store.Find(""));
            // reinstall replaces
            store.Install(MakeZip(root, TurnipMeta, "vulkan.ad07XX.so"));
            Assert.Single(store.List());
            Assert.True(store.Remove("Turnip_v24.1"));
            Assert.False(store.Remove("../.."));
            Assert.Empty(store.List());
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("""{"schemaVersion":2,"name":"x","libraryName":"a.so"}""", "schemaVersion")]
    [InlineData("""{"schemaVersion":1,"name":"","libraryName":"a.so"}""", "no name")]
    [InlineData("""{"schemaVersion":1,"name":"x","libraryName":"../a.so"}""", "libraryName")]
    [InlineData("""{"schemaVersion":1,"name":"x","libraryName":"a.dll"}""", "libraryName")]
    [InlineData("""not json""", "JSON")]
    public void BadMetaIsRejected(string json, string why)
    {
        Assert.Null(CustomDriverMeta.Parse(json, out var err));
        Assert.Contains(why, err);
    }

    [Fact]
    public void DriverStoreRejectsUnusablePackages()
    {
        string root = Path.Combine(Path.GetTempPath(), $"vs-drivers-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var store = new CustomDriverStore(Path.Combine(root, "d"));
            Assert.Contains("not in the package", Assert.Throws<InvalidDataException>(() => store.Install(MakeZip(root, TurnipMeta))).Message);
            Assert.Contains("API 27", Assert.Throws<InvalidDataException>(() => store.Install(MakeZip(root, TurnipMeta, "vulkan.ad07XX.so"), deviceApiLevel: 26)).Message);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void DriverSettingsRoundTrip()
    {
        string path = Path.Combine(Path.GetTempPath(), $"vs-gpu-{Guid.NewGuid():N}.json");
        try
        {
            Assert.Equal("", GpuDriverSettings.Load(path).Driver);
            new GpuDriverSettings { Driver = "Turnip_v24.1", Turbo = true, PatchBcn = false }.Save(path);
            var s = GpuDriverSettings.Load(path);
            Assert.Equal("Turnip_v24.1", s.Driver);
            Assert.True(s.Turbo);
            Assert.False(s.PatchBcn);
            Assert.False(s.FileRedirect);
        }
        finally { File.Delete(path); }
    }
}
