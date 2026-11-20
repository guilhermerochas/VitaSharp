using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// SceExcpmgrForKernel beside the handler registration, as FW 3.74 excpmgr has it (decompiled, study only): the data
/// block and its words, unregistering a handler (its checks, the chain relinked), and a default handler put in front
/// of the current one (every chain ends at it, a kind without handlers enters it).
/// </summary>
[Collection("guest")]
public sealed class KernelExcpmgrTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int IllegalAddr = unchecked((int)0x80020006), IllegalExcpCode = unchecked((int)0x80027001),
        IllegalExcpHandler = unchecked((int)0x80027002), NotRegistered = unchecked((int)0x80027003);

    private const uint DataAbort = 4, Undefined = 1;

    private readonly HleHarness _harness = new(fixture, "kexcp");

    public void Dispose() => _harness.Dispose();

    private ProcessmgrKernel Processmgr => _harness.Module<ProcessmgrKernel>();

    private uint Handler() => _harness.Alloc(0x10);

    private List<uint> Chain(uint kind) => (List<uint>)Invoke(Processmgr, "ExceptionHandlers", kind)!;

    [Fact]
    public void TheDataBlockKeepsTheWordsAtTheirOffsets()
    {
        uint data = (uint)Invoke(Processmgr, "ksceExcpmgrGetData")!;
        Assert.NotEqual(0u, data);
        Assert.Equal(data, (uint)Invoke(Processmgr, "ksceExcpmgrGetData")!);
        (string Setter, uint Offset)[] words = [("NID_293DFA04", 0x44), ("NID_E7487AFD", 0x48), ("NID_1FBF5654", 0x80), ("NID_58F7212B", 0x84), ("NID_9EE59C6E", 0x88)];
        foreach ((string setter, uint offset) in words)
        {
            Invoke(Processmgr, setter, 0x81000000u + offset);
            Assert.Equal(0x81000000u + offset, GuestMemory.R32(data + offset));
            Assert.Equal(0x81000000u + offset, (uint)Invoke(Processmgr, "NID_5420ED8F", (int)(offset / 4))!);
        }

        Assert.Equal(0, Call(Processmgr, "NID_44CE04B8", 0x11u, 0x22u));
        Assert.Equal(0x11u, GuestMemory.R32(data + 0x41DC));
        Assert.Equal(0x22u, GuestMemory.R32(data + 0x41D4));
        GuestMemory.W32(data, 3);
        Assert.Equal(3u, (uint)Invoke(Processmgr, "NID_B615A7DA")!); // no current thread: the core's word
        _harness.OnGuest(_ => Assert.Equal(0u, (uint)Invoke(Processmgr, "NID_B615A7DA")!));
    }

    [Fact]
    public void UnregisteringCheckTheHandlerAndRelinksTheChain()
    {
        uint first = Handler();
        uint second = Handler();
        uint third = Handler();
        Assert.Equal(0, Call(Processmgr, "sceKernelRegisterExceptionHandler", DataAbort, 0u, first));
        Assert.Equal(0, Call(Processmgr, "sceKernelRegisterExceptionHandler", DataAbort, 1u, second));
        Assert.Equal(0, Call(Processmgr, "sceKernelRegisterExceptionHandler", DataAbort, 2u, third));
        Assert.Equal(new[] { first, second, third }, Chain(DataAbort).Take(3));

        Assert.Equal(IllegalAddr, Call(Processmgr, "NID_DA7BB671", DataAbort, 0u));
        Assert.Equal(IllegalExcpHandler, Call(Processmgr, "NID_DA7BB671", DataAbort, Handler()));
        Assert.Equal(IllegalExcpCode, Call(Processmgr, "NID_DA7BB671", 8u, second));
        Assert.Equal(NotRegistered, Call(Processmgr, "NID_DA7BB671", Undefined, second));

        Assert.Equal(0, Call(Processmgr, "NID_DA7BB671", DataAbort, second));
        Assert.Equal(third, GuestMemory.R32(first));
        Assert.Equal(new[] { first, third }, Chain(DataAbort).Take(2));
        Assert.Equal(NotRegistered, Call(Processmgr, "NID_DA7BB671", DataAbort, second));
        // the first one: the chain starts at its next
        Assert.Equal(0, Call(Processmgr, "NID_DA7BB671", DataAbort, first));
        Assert.Equal((third & ~3u) + 8, (uint)Invoke(Processmgr, "ExceptionVector", DataAbort)!);
        Assert.Equal(0, Call(Processmgr, "NID_DA7BB671", DataAbort, third));
        Assert.Equal(0u, (uint)Invoke(Processmgr, "ExceptionVector", DataAbort)!);
    }

    [Fact]
    public void ADefaultHandlerEndsEveryChainAndEntersTheKindsWithoutHandlers()
    {
        uint handler = Handler();
        Assert.Equal(0, Call(Processmgr, "sceKernelRegisterExceptionHandler", DataAbort, 0u, handler));
        uint earlier = (uint)Invoke(Processmgr, "NID_4337DD78")!;
        Assert.Equal(earlier, GuestMemory.R32(handler));
        Assert.Equal(0u, (uint)Invoke(Processmgr, "ExceptionVector", Undefined)!);

        uint fallback = Handler();
        Assert.Equal(0, Call(Processmgr, "NID_416C0E20", fallback));
        Assert.Equal(fallback, (uint)Invoke(Processmgr, "NID_4337DD78")!);
        Assert.Equal(earlier, GuestMemory.R32(fallback));
        Assert.Equal(fallback, GuestMemory.R32(handler));
        Assert.Equal(fallback + 8, (uint)Invoke(Processmgr, "ExceptionVector", Undefined)!);
        // a used record is refused, and a handler registered later ends at the new default too
        Assert.Equal(IllegalExcpHandler, Call(Processmgr, "NID_416C0E20", fallback));
        uint later = Handler();
        Assert.Equal(0, Call(Processmgr, "sceKernelRegisterExceptionHandler", Undefined, 0u, later));
        Assert.Equal(fallback, GuestMemory.R32(later));
    }
}
