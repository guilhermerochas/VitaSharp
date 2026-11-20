using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// ksceKernelGetProcessInfo, the process list and ksceKernelGetProcessLocalStorageAddr of FW 3.74 processmgr
/// (decompiled, study only): the structure filled from the process object, its size and pointer checks, the list's
/// modes (information without or with the kernel, UIDs) and the current process's local storage.
/// </summary>
[Collection("guest")]
public sealed class KernelProcessInfoTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int InvalidArgument = unchecked((int)0x80020005), InvalidPid = unchecked((int)0x80029001);

    private const int KernelPid = 0x10005;

    private readonly HleHarness _harness = new(fixture, "kprocinfo");

    public void Dispose() => _harness.Dispose();

    private ProcessmgrKernel Processmgr => _harness.Module<ProcessmgrKernel>();

    [Fact]
    public void TheProcessInfoComesFromTheProcessObject()
    {
        int pid = _harness.Emu.Pid;
        uint block = (uint)Invoke(Processmgr, "ksceKernelGetProcessKernelBuf", pid)!;
        GuestMemory.W32(block + 0x5C, 0x5C);
        GuestMemory.W32(block + 0x3C, 0x000F0002);
        GuestMemory.W32(block + 0x208, 0x12345678);
        uint info = _harness.Alloc(0xE8);
        Assert.Equal(InvalidArgument, Call(Processmgr, "ksceKernelGetProcessInfo", pid, 0u));
        GuestMemory.W32(info, 0xE4);
        Assert.Equal(unchecked((int)0x80020009), Call(Processmgr, "ksceKernelGetProcessInfo", pid, info));
        GuestMemory.W32(info, 0xE8);
        Assert.Equal(InvalidPid, Call(Processmgr, "ksceKernelGetProcessInfo", 0x7FFF0001, info));
        Assert.Equal(0, Call(Processmgr, "ksceKernelGetProcessInfo", pid, info));
        Assert.Equal((uint)pid, GuestMemory.R32(info + 4));
        Assert.Equal(0x5Cu, GuestMemory.R32(info + 8));
        Assert.Equal(2u, GuestMemory.R32(info + 0x10)); // bits 16-19 cleared
        Assert.Equal(0x12345678u, GuestMemory.R32(info + 0x68));
        Assert.NotEqual(0, GuestMemory.Read<byte>(info + 0x30));
        Assert.Equal(0, Call(Processmgr, "ksceKernelGetProcessInfo", KernelPid, info));
        Assert.Equal((uint)KernelPid, GuestMemory.R32(info + 4));
        Assert.Equal(0xFFFFFFFFu, GuestMemory.R32(info + 0x24));
    }

    [Fact]
    public void TheProcessListHasThreeModes()
    {
        uint buffer = _harness.Alloc(0xE8 * 8);
        uint count = _harness.Word(0xCC);
        Assert.Equal(InvalidArgument, Call(Processmgr, "NID_BA0A06A4", 0, buffer, 0xE8u * 8, 0u, 0));
        Assert.Equal(InvalidArgument, Call(Processmgr, "NID_BA0A06A4", 0, buffer, 0xE8u * 8, count, 3));
        int processes = _harness.Emu.Vita.Processes.Count;
        Assert.Equal(processes + 1, Call(Processmgr, "NID_E8005AFC", 0, buffer, 0x20u, count, 2));
        uint written = GuestMemory.R32(count);
        Assert.Equal((uint)Math.Min(processes + 1, 8), written);
        Assert.Equal((uint)KernelPid, GuestMemory.R32(buffer));
        Assert.Equal(processes + 1, Call(Processmgr, "NID_EFFF2975", 0, buffer, 0xE8u * 8, count, 1));
        Assert.Equal((uint)KernelPid, GuestMemory.R32(buffer + 4));
        Assert.Equal(processes + 1, Call(Processmgr, "NID_BA0A06A4", 0, buffer, 0xE8u * 8, count, 0));
        Assert.Equal((uint)processes, GuestMemory.R32(count));
        if (processes > 0)
        {
            Assert.NotEqual((uint)KernelPid, GuestMemory.R32(buffer + 4));
        }
    }

    [Fact]
    public void TheCurrentProcessLocalStorageIsMadeOnFirstUse()
    {
        int key = Call(Processmgr, "ksceKernelCreateProcessLocalStorage", _harness.Str("kpls"), 0x40u);
        Assert.True(key >= 0);
        Assert.Equal(0u, (uint)Invoke(Processmgr, "ksceKernelGetProcessLocalStorageAddr", key)!); // no current thread
        _harness.OnGuest(_ =>
        {
            uint address = (uint)Invoke(Processmgr, "ksceKernelGetProcessLocalStorageAddr", key)!;
            Assert.NotEqual(0u, address);
            Assert.Equal(address, (uint)Invoke(Processmgr, "ksceKernelGetProcessLocalStorageAddr", key)!);
            Assert.Equal(0u, (uint)Invoke(Processmgr, "ksceKernelGetProcessLocalStorageAddr", 32)!);
        });
    }

    [Fact]
    public void SuspendAndResumeMoveTheProcessBetweenRunningAndSuspended()
    {
        int pid = _harness.Emu.Pid;
        uint status = _harness.Alloc(4);
        Assert.Equal(InvalidArgument, Call(Processmgr, "ksceKernelSuspendProcess", pid, 0x01u));
        Assert.Equal(unchecked((int)0x80029002), Call(Processmgr, "ksceKernelSuspendProcess", KernelPid, 0x11u));
        Assert.Equal(InvalidPid, Call(Processmgr, "ksceKernelSuspendProcess", 0x7FFF0001, 0x11u));
        Assert.Equal(unchecked((int)0x80029004), Call(Processmgr, "ksceKernelResumeProcess", pid));
        Assert.Equal(0, Call(Processmgr, "ksceKernelSuspendProcess", pid, 0x11u));
        Assert.True(_harness.Emu.Suspended);
        Assert.Equal(0, Call(Processmgr, "ksceKernelGetProcessStatus", pid, status));
        Assert.Equal(0x11u, GuestMemory.R32(status));
        Assert.Equal(unchecked((int)0x80029004), Call(Processmgr, "ksceKernelSuspendProcess", pid, 0x11u));
        Assert.Equal(0, Call(Processmgr, "ksceKernelResumeProcess", pid));
        Assert.False(_harness.Emu.Suspended);
        Assert.Equal(0, Call(Processmgr, "ksceKernelGetProcessStatus", pid, status));
        Assert.Equal(2u, GuestMemory.R32(status));
        Assert.Equal(unchecked((int)0x80029004), Call(Processmgr, "ksceKernelResumeProcess", pid));
    }
}
