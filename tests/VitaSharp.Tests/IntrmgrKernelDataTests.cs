using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// SceIntrmgrForKernel's data words and queries as FW 3.74 intrmgr has them (decompiled, study only): the interrupt
/// stack tops of each core, the words at their firmware addresses, the trace hooks, the one-time syscall table and the
/// handler info of a line.
/// </summary>
[Collection("guest")]
public sealed class IntrmgrKernelDataTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int IntrCode = unchecked((int)0x80027102), IntrOption = unchecked((int)0x80027103);

    private readonly HleHarness _harness = new(fixture, "kintrdata");

    public void Dispose() => _harness.Dispose();

    private SystemServicesVsh Services => _harness.Module<SystemServicesVsh>();

    [Fact]
    public void TheStackTopsAreSixteenPerCoreBelowTheTop()
    {
        uint top = (uint)Invoke(Services, "NID_01911612", 0u, 1u)!;
        Assert.NotEqual(0u, top);
        Assert.Equal(top - 0x2000, (uint)Invoke(Services, "NID_01911612", 0u, 2u)!);
        Assert.Equal(top - 0x20000 - 15 * 0x2000, (uint)Invoke(Services, "NID_01911612", 1u, 16u)!);
        Assert.Equal(0u, (uint)Invoke(Services, "NID_01911612", 4u, 1u)!);
        Assert.Equal(0u, (uint)Invoke(Services, "NID_01911612", 0u, 0u)!);
        Assert.Equal(0u, (uint)Invoke(Services, "NID_01911612", 0u, 17u)!);
        // the word at the top and the ones after it
        Invoke(Services, "NID_9CB39268", 0xAAu);
        Invoke(Services, "NID_46F04D82", 0xBBu);
        Invoke(Services, "NID_764F140A", 0xCCu);
        Assert.Equal(0xAAu, GuestMemory.R32(top));
        Assert.Equal(0xBBu, GuestMemory.R32(top + 4));
        Assert.Equal(0xCCu, GuestMemory.R32(top + 8));
        GuestMemory.W32(top + 0x50, 0x1234);
        Assert.Equal(0x1234u, (uint)Invoke(Services, "NID_807B2004")!);
        // below the stacks: two words and a record
        uint data = top - 0x80040;
        Invoke(Services, "NID_ABEB5A69", 0x11u);
        Invoke(Services, "NID_7E3D4560", 0x22u);
        uint record = _harness.Alloc(0x10);
        GuestMemory.W32(record + 12, 0x44);
        Invoke(Services, "NID_04E635AA", record);
        Assert.Equal(0x11u, GuestMemory.R32(data));
        Assert.Equal(0x22u, GuestMemory.R32(data + 4));
        Assert.Equal(0x44u, GuestMemory.R32(data + 8 + 12));
        Invoke(Services, "NID_8D6E1289", 0x81000001u, 0x81000003u);
        Invoke(Services, "NID_217AC350", 0x81000005u, 0x81000007u);
        Assert.Equal(0x81000001u, GuestMemory.R32(top + 0x64));
        Assert.Equal(0x81000003u, GuestMemory.R32(top + 0x44));
        Assert.Equal(0x81000005u, GuestMemory.R32(top + 0x48));
        Assert.Equal(0x81000007u, GuestMemory.R32(top + 0x4C));
    }

    [Fact]
    public void TheSyscallTableIsAllocatedOnce()
    {
        uint baseOut = _harness.Alloc(4);
        Assert.Equal(-1, Call(Services, "NID_B5A665C7", 0u, baseOut));
        Assert.Equal(0, Call(Services, "NID_B5A665C7", 0x400u, baseOut));
        uint table = GuestMemory.R32(baseOut);
        Assert.NotEqual(0u, table);
        Assert.Equal(0u, GuestMemory.R32(table + 0xFFC));
        Assert.Equal(-1, Call(Services, "NID_B5A665C7", 0x400u, baseOut));
    }

    [Fact]
    public void TheHandlerInfoOfALineComesFromTheLineTable()
    {
        uint handler = _harness.Alloc(0x20);
        Assert.Equal(0, Call(Services, "ksceKernelRegisterIntrHandler", 0x40u, 0u, 0u, handler, 0x55u, 0x80u, 0x10000u, 0u));
        uint info = _harness.Alloc(0x70);
        GuestMemory.W32(info, 0x6C);
        Assert.Equal(IntrOption, Call(Services, "ksceKernelQueryIntrHandlerInfo", 0x40u, -1, info));
        GuestMemory.W32(info, 0x70);
        Assert.Equal(IntrCode, Call(Services, "ksceKernelQueryIntrHandlerInfo", 0x100u, -1, info));
        Assert.Equal(0, Call(Services, "ksceKernelQueryIntrHandlerInfo", 0x40u, -1, info));
        Assert.Equal(handler, GuestMemory.R32(info + 4));
        Assert.Equal(0x55u, GuestMemory.R32(info + 8));
        Assert.Equal((ushort)0x40, GuestMemory.Read<ushort>(info + 0xC));
        Assert.Equal((ushort)0x80, GuestMemory.Read<ushort>(info + 0x12));
        Assert.Equal((ushort)0, GuestMemory.Read<ushort>(info + 0x16)); // not enabled
        Assert.Equal(0, Call(Services, "ksceKernelEnableIntr", 0x40u));
        Assert.Equal(0, Call(Services, "ksceKernelQueryIntrHandlerInfo", 0x40u, -1, info));
        Assert.Equal((ushort)1, GuestMemory.Read<ushort>(info + 0x16));
    }
}
