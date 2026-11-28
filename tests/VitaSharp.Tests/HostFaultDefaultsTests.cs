using System.Runtime.InteropServices;
using VitaSharp.Core.Cpu.Native;
using Xunit;

namespace VitaSharp.Tests;

/// <summary>
/// Which hosts find data aborts through host faults instead of a guard per access
/// (<see cref="HostFaultAborts.Arm64SignalHandlerUsed"/>): on Linux and Android ARM64 with 4 KB host pages it is the
/// default, as fastmem is in Dynarmic and Box64; VITASHARP_DATA_ABORT_FAULTS=0 and 16 KB host pages keep the guards.
/// </summary>
public sealed class HostFaultDefaultsTests
{
    [Fact]
    public void AnArm64DeviceWith4KbPagesUsesHostFaultsByDefault()
    {
        Assert.True(HostFaultAborts.Arm64SignalHandlerUsed(true, Architecture.Arm64, 4096, null));
        Assert.True(HostFaultAborts.Arm64SignalHandlerUsed(true, Architecture.Arm64, 4096, ""));
        Assert.True(HostFaultAborts.Arm64SignalHandlerUsed(true, Architecture.Arm64, 4096, "1"));
    }

    [Fact]
    public void ZeroKeepsTheGuards()
    {
        Assert.False(HostFaultAborts.Arm64SignalHandlerUsed(true, Architecture.Arm64, 4096, "0"));
    }

    [Theory]
    [InlineData(16384)]
    [InlineData(65536)]
    public void LargerHostPagesKeepTheGuards(int hostPageSize)
    {
        Assert.False(HostFaultAborts.Arm64SignalHandlerUsed(true, Architecture.Arm64, hostPageSize, null));
        Assert.False(HostFaultAborts.Arm64SignalHandlerUsed(true, Architecture.Arm64, hostPageSize, "1"));
    }

    [Fact]
    public void OtherHostsAreNotTheArm64SignalHandlersPath()
    {
        Assert.False(HostFaultAborts.Arm64SignalHandlerUsed(false, Architecture.Arm64, 4096, null));
        Assert.False(HostFaultAborts.Arm64SignalHandlerUsed(true, Architecture.X64, 4096, null));
        Assert.False(HostFaultAborts.Arm64SignalHandlerUsed(true, Architecture.X64, 4096, "1"));
    }

    [Fact]
    public void TheCaptureLineSaysWhichWayNativeCodeFindsDataAborts()
    {
        Assert.Equal("data aborts: a handler is registered; native code finds them through host faults (no check per access)",
            HostFaultAborts.ModeLine(true, true, "X", null));
        Assert.Equal("data aborts: a handler is registered; native code checks every guest access (a guard per load/store), X=0",
            HostFaultAborts.ModeLine(true, false, "X", "0"));
        Assert.StartsWith("data aborts: no handler registered (no checks); with one, ", HostFaultAborts.ModeLine(false, true, "X", null));
    }

    [Fact]
    public void ThisHostsBackendFollowsTheDecision()
    {
        bool expected = HostFaultAborts.Arm64SignalHandlerUsed(OperatingSystem.IsLinux() || OperatingSystem.IsAndroid(),
            RuntimeInformation.ProcessArchitecture, Environment.SystemPageSize,
            Environment.GetEnvironmentVariable(VitaSharp.Core.Util.EnvVars.DataAbortFaults));
        if (expected)
        {
            Assert.True(HostFaultAborts.Supported);
        }
    }
}
