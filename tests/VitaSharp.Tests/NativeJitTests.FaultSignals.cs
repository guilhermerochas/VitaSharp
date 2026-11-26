using Iced.Intel;
using System.Runtime.InteropServices;
using VitaSharp.Core.Cpu;
using VitaSharp.Core.Cpu.Native;
using VitaSharp.Core.Memory;
using Xunit;
using static Iced.Intel.AssemblerRegisters;

namespace VitaSharp.Tests;

/// <summary>
/// The SIGSEGV handlers of host fault data aborts (<see cref="HostFaultSignals"/>: Linux/Android ARM64, Linux x86-64),
/// run on hand-made signal frames (siginfo, ucontext with the registers and, on ARM64, the ESR record): the ARM64 one
/// in the sandbox, the x86-64 one called through a System V thunk. A fault at a registered fault site on an unmapped
/// guest page stores the fault in the state and resumes at the site's exit; one outside every guest window goes to the
/// previous handler with the signal's arguments. The paths that call the kernel (mprotect, the default action) only run
/// on a Linux host or a device.
/// </summary>
public sealed unsafe partial class NativeJitTests
{
    private const ulong FakeExit = 0x1234_5678;

    private sealed class SignalFrame : IDisposable
    {
        public readonly byte* Info = (byte*)NativeMemory.AllocZeroed(128);
        public readonly byte* Context = (byte*)NativeMemory.AllocZeroed(8192);
        public readonly CpuState* State = CpuState.Allocate();

        public void Dispose()
        {
            NativeMemory.Free(Info);
            NativeMemory.Free(Context);
            CpuState.Free(State);
        }
    }

    /// <summary>A fault site around <paramref name="pc"/> (in the code arena) whose exit is <see cref="FakeExit"/>.</summary>
    private static ulong* PublishFakeSite(ulong pc)
    {
        ulong* sites = (ulong*)NativeMemory.Alloc(3, sizeof(ulong));
        sites[0] = pc - 4;
        sites[1] = pc + 16;
        sites[2] = FakeExit;
        HostFaultSignals.PublishSites(sites, 1);
        return sites;
    }

    [Arm64SandboxFact]
    public void TheArm64SignalHandlerEntersTheExitOfAFaultSite()
    {
        if (Arm64Runner.Native)
        {
            return; // a device: the real handler runs in the device tests
        }

        EnsureGuestPages();
        Assert.Equal(0, GuestMemory.MappedTable(GuestMemory.Base)[AbortTestAddress >> 12]);
        byte* block = HostFaultSignals.PrepareBlockForTests();
        HostFaultSignals.AddWindow(GuestMemory.Base);
        ExecutableMemory code = NativeRuntime.Instance.Code;
        byte* handler = code.Place(_ => HostFaultSignals.EmitHandler((ulong)block, (uint)StateLayout.Of(nameof(CpuState.AbortAddr)),
            (uint)StateLayout.Of(nameof(CpuState.AbortStatus)), GuestMemory.MappedTableOffset));
        ulong pc = (ulong)code.ExecBase + 0x40;
        ulong* sites = PublishFakeSite(pc);
        using var sandbox = new Arm64Sandbox();
        using var frame = new SignalFrame();
        try
        {
            foreach (bool write in new[] { true, false })
            {
                *(ulong*)(frame.Info + 16) = (ulong)GuestMemory.Base + AbortTestAddress + 4;
                *(ulong*)(frame.Context + 176 + 264) = pc;
                *(ulong*)(frame.Context + 176 + 8 + 8 * 19) = (ulong)frame.State;
                // the extension records: FPSIMD (528 bytes), then ESR (WnR = bit 6), then the terminator
                byte* records = frame.Context + 176 + 288;
                *(uint*)records = 0x46508001;
                *(uint*)(records + 4) = 528;
                *(uint*)(records + 528) = 0x45535201;
                *(uint*)(records + 532) = 16;
                *(ulong*)(records + 536) = 0x9200_0007UL | (write ? 1UL << 6 : 0);
                *(uint*)(records + 544) = 0;
                sandbox.Call((ulong)handler, 11, (ulong)frame.Info, (ulong)frame.Context);
                Assert.Equal(FakeExit, *(ulong*)(frame.Context + 176 + 264));
                Assert.Equal(AbortTestAddress + 4, frame.State->AbortAddr);
                Assert.Equal(DataAborts.TranslationFault | (write ? DataAborts.WriteNotRead : 0), frame.State->AbortStatus);
            }

            // outside every guest window: the previous handler gets the signal's arguments, the pc stays
            ulong[] got = new ulong[3];
            sandbox.FakeFunction = (x0, x1, x2, _) =>
            {
                got[0] = x0;
                got[1] = x1;
                got[2] = x2;
                return 0;
            };
            HostFaultSignals.SetPreviousHandlerForTests(Arm64Sandbox.FakeStub);
            *(ulong*)(frame.Info + 16) = (ulong)frame.State;
            *(ulong*)(frame.Context + 176 + 264) = pc;
            sandbox.Call((ulong)handler, 11, (ulong)frame.Info, (ulong)frame.Context);
            Assert.Equal([11UL, (ulong)frame.Info, (ulong)frame.Context], got);
            Assert.Equal(pc, *(ulong*)(frame.Context + 176 + 264));
        }
        finally
        {
            HostFaultSignals.SetPreviousHandlerForTests(0);
            HostFaultSignals.PublishSites(null, 0);
            NativeMemory.Free(sites);
        }
    }

    [NativeFact]
    public void TheX64SignalHandlerEntersTheExitOfAFaultSite()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // the thunk is a Windows x64 caller; a Linux host runs the real handler
        }

        EnsureGuestPages();
        byte* block = HostFaultSignals.PrepareBlockForTests();
        HostFaultSignals.AddWindow(GuestMemory.Base);
        ExecutableMemory code = NativeRuntime.Instance.Code;
        byte* handler = code.Place(rip => HostFaultSignals.EmitHandlerX64(rip, (ulong)block, (uint)StateLayout.Of(nameof(CpuState.AbortAddr)),
            (uint)StateLayout.Of(nameof(CpuState.AbortStatus)), GuestMemory.MappedTableOffset));
        ulong* cell = (ulong*)NativeMemory.AllocZeroed(16);
        byte* previous = code.Place(rip => Assemble(rip, a =>
        {
            a.mov(rax, (ulong)cell);
            a.mov(__qword_ptr[rax], rdi);
            a.mov(__qword_ptr[rax + 8], rdx);
            a.ret();
        }));
        byte* thunk = code.Place(rip => Assemble(rip, a =>
        {
            // Windows x64 caller -> System V callee (rdi and rsi are callee-saved on Windows)
            a.push(rdi);
            a.push(rsi);
            a.sub(rsp, 8);
            a.mov(rdi, rcx);
            a.mov(rsi, rdx);
            a.mov(rdx, r8);
            a.mov(rax, (ulong)handler);
            a.call(rax);
            a.add(rsp, 8);
            a.pop(rsi);
            a.pop(rdi);
            a.ret();
        }));
        var call = (delegate* unmanaged<long, byte*, byte*, void>)thunk;
        ulong pc = (ulong)code.ExecBase + 0x40;
        ulong* sites = PublishFakeSite(pc);
        using var frame = new SignalFrame();
        try
        {
            foreach (bool write in new[] { true, false })
            {
                *(ulong*)(frame.Info + 16) = (ulong)GuestMemory.Base + AbortTestAddress + 8;
                *(ulong*)(frame.Context + 40 + 8 * 16) = pc;
                *(ulong*)(frame.Context + 40 + 8 * 11) = (ulong)frame.State;
                *(ulong*)(frame.Context + 40 + 8 * 19) = write ? 6UL : 4UL; // page fault error code: user, W
                call(11, frame.Info, frame.Context);
                Assert.Equal(FakeExit, *(ulong*)(frame.Context + 40 + 8 * 16));
                Assert.Equal(AbortTestAddress + 8, frame.State->AbortAddr);
                Assert.Equal(DataAborts.TranslationFault | (write ? DataAborts.WriteNotRead : 0), frame.State->AbortStatus);
            }

            HostFaultSignals.SetPreviousHandlerForTests((ulong)previous);
            *(ulong*)(frame.Info + 16) = (ulong)frame.State;
            *(ulong*)(frame.Context + 40 + 8 * 16) = pc;
            call(11, frame.Info, frame.Context);
            Assert.Equal(11UL, cell[0]);
            Assert.Equal((ulong)frame.Context, cell[1]);
            Assert.Equal(pc, *(ulong*)(frame.Context + 40 + 8 * 16));
        }
        finally
        {
            HostFaultSignals.SetPreviousHandlerForTests(0);
            HostFaultSignals.PublishSites(null, 0);
            NativeMemory.Free(sites);
            NativeMemory.Free(cell);
        }
    }

    private static byte[] Assemble(ulong rip, Action<Assembler> body)
    {
        var a = new Assembler(64);
        body(a);
        using var ms = new MemoryStream();
        a.Assemble(new StreamCodeWriter(ms), rip);
        return ms.ToArray();
    }
}
