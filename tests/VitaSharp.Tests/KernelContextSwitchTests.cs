using VitaSharp.Core.Cpu;
using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// The context switch primitives of SceIntrmgrForKernel (save NID_71C5F42F, restore NID_08FDB0CE, copy NID_71E08D71)
/// and the exception return of SceExcpmgrForKernel (NID_3E55B5C3), as FW 3.74 has them (decompiled, study only),
/// driven on a register file the way the HLE dispatcher hands it over.
/// </summary>
[Collection("guest")]
public sealed unsafe class KernelContextSwitchTests(GuestMemoryFixture fixture) : IDisposable
{
    private readonly HleHarness _harness = new(fixture, "kctx");

    private readonly CpuState* _state = CpuState.Allocate();

    public void Dispose()
    {
        CpuState.Free(_state);
        _harness.Dispose();
    }

    private SystemServicesVsh Services => _harness.Module<SystemServicesVsh>();

    private ProcessmgrKernel Processmgr => _harness.Module<ProcessmgrKernel>();

    private HleContext Context => new() { Cpu = _state, Thread = null!, Emu = _harness.Emu };

    private void Registers(uint seed)
    {
        for (int i = 0; i < 15; i++)
        {
            _state->R[i] = seed + (uint)i;
        }

        ulong* d = (ulong*)_state->D;
        for (int i = 0; i < 32; i++)
        {
            d[i] = seed * 0x1_0000_0001UL + (ulong)i;
        }

        _state->Fpscr = seed;
        _state->TpidrUrw = seed + 0x100;
        _state->TpidrUro = seed + 0x200;
        _state->N = 1;
        _state->Z = 0;
        _state->C = 1;
        _state->V = 0;
        _state->Q = 0;
    }

    [Fact]
    public void ASavedContextReturnsASecondTimeWithOne()
    {
        uint context = _harness.Alloc(0x200);
        uint vfp = _harness.Alloc(0x110);
        Registers(0x1000);
        _state->R[14] = 0x8100_1235;
        Assert.Equal(0, Call(Services, "NID_71C5F42F", Context, context, vfp));
        Assert.Equal(0u, GuestMemory.R32(context));
        for (uint i = 0; i < 8; i++)
        {
            Assert.Equal(0x1004u + i, GuestMemory.R32(context + 0x14 + i * 4));
        }

        Assert.Equal(0x100Du, GuestMemory.R32(context + 0x38));
        Assert.Equal(0x8100_1235u, GuestMemory.R32(context + 0x3C));
        Assert.Equal((0b10100u << 27) | 0x20, GuestMemory.R32(context + 0x40));
        Assert.Equal(0x1100u, GuestMemory.R32(context + 0x50));
        Assert.Equal(0x1200u, GuestMemory.R32(context + 0x54));
        // the whole VFP form: d8-d15 at +0x40, FPSCR at +0x100
        Assert.Equal(0x1000UL * 0x1_0000_0001UL + 8, GuestMemory.Read<ulong>(vfp + 0x40));
        Assert.Equal(0x1000u, GuestMemory.R32(vfp + 0x100));

        // other registers, then the restore: the callee-saved ones come back and the save returns 1
        Registers(0x5000);
        _state->R[14] = 0x8200_0001;
        Invoke(Services, "NID_08FDB0CE", Context, context, 0u, vfp);
        Assert.Equal(1u, _state->R[0]);
        for (int i = 4; i < 12; i++)
        {
            Assert.Equal(0x1000u + (uint)i, _state->R[i]);
        }

        Assert.Equal(0x100Du, _state->R[13]);
        Assert.Equal(0x8100_1235u, _state->R[14]);
        Assert.Equal(0x1000UL * 0x1_0000_0001UL + 9, ((ulong*)_state->D)[9]);
        Assert.Equal(0x5000UL * 0x1_0000_0001UL + 3, ((ulong*)_state->D)[3]); // not callee-saved: unchanged
        Assert.Equal(0x1000u, _state->Fpscr);
        Assert.Equal(0x1100u, _state->TpidrUrw);
        Assert.Equal(1u, _state->N);
        Assert.Equal(1u, _state->C);
    }

    [Fact]
    public void TheCompactVfpFormKeepsTheCalleeSavedRegistersAtTheStart()
    {
        uint context = _harness.Alloc(0x200);
        uint vfp = _harness.Alloc(0x50);
        Registers(0x2000);
        Assert.Equal(0, Call(Services, "NID_71C5F42F", Context, context, vfp | 1));
        Assert.Equal(0x2000UL * 0x1_0000_0001UL + 8, GuestMemory.Read<ulong>(vfp));
        Assert.Equal(0x2000UL * 0x1_0000_0001UL + 15, GuestMemory.Read<ulong>(vfp + 0x38));
        Assert.Equal(0x2000u, GuestMemory.R32(vfp + 0x40));
        Registers(0x3000);
        Invoke(Services, "NID_08FDB0CE", Context, context, 0u, vfp | 1);
        Assert.Equal(0x2000UL * 0x1_0000_0001UL + 15, ((ulong*)_state->D)[15]);
        Assert.Equal(0x2000u, _state->Fpscr);
    }

    [Fact]
    public void AWholeContextResumesTheInterruptedState()
    {
        uint context = _harness.Alloc(0x200);
        GuestMemory.W32(context, 1);
        for (uint i = 0; i < 13; i++)
        {
            GuestMemory.W32(context + 4 + i * 4, 0x700 + i);
        }

        GuestMemory.W32(context + 0x38, 0x8300_0000);
        GuestMemory.W32(context + 0x3C, 0x8300_1001);
        GuestMemory.W32(context + 0xC0, 0x8300_2003);
        GuestMemory.W32(context + 0xC4, 0x6000_0030);
        Registers(0x4000);
        Invoke(Services, "NID_08FDB0CE", Context, context, 0u, 0u);
        for (int i = 0; i < 13; i++)
        {
            Assert.Equal(0x700u + (uint)i, _state->R[i]);
        }

        Assert.Equal(0x8300_0000u, _state->R[13]);
        Assert.Equal(0x8300_1001u, _state->R[14]);
        Assert.Equal(0x8300_2002u, _state->R[15]);
        Assert.Equal(1u, _state->Thumb);
        Assert.Equal(0u, _state->N);
        Assert.Equal(1u, _state->Z);
        Assert.Equal(1u, _state->C);
    }

    [Fact]
    public void ACopyMovesTheWholeContextAndAnswersBothEnds()
    {
        uint source = _harness.Alloc(0x200);
        uint destination = _harness.Alloc(0x200);
        for (uint i = 0; i < 0x80; i++)
        {
            GuestMemory.W32(source + i * 4, i * 3);
        }

        ulong ends = (ulong)Invoke(Services, "NID_71E08D71", destination, source)!;
        Assert.Equal(destination + 0x200, (uint)ends);
        Assert.Equal(source + 0x200, (uint)(ends >> 32));
        Assert.Equal(0x7Fu * 3, GuestMemory.R32(destination + 0x1FC));
    }

    [Fact]
    public void AnExceptionReturnRestoresTheFrameAndLeavesTheException()
    {
        uint data = (uint)Invoke(Processmgr, "ksceExcpmgrGetData")!;
        uint frame = _harness.Alloc(0x400);
        for (uint i = 0; i < 13; i++)
        {
            GuestMemory.W32(frame + i * 4, 0x900 + i);
        }

        GuestMemory.W32(frame + 0x34, 0x8400_0000);
        GuestMemory.W32(frame + 0x38, 0x8400_1001);
        GuestMemory.W32(frame + 0x3C, 0x8400_2000);
        GuestMemory.W32(frame + 0x40, 1);
        GuestMemory.W32(frame + 0x44, 0x8000_0010); // N, user mode, ARM
        GuestMemory.W32(frame + 0x5C, 0x8400_3000);
        GuestMemory.Write(frame + 0x200 + 5 * 8, 0x1234_5678_9ABC_DEF0UL);
        GuestMemory.W32(frame + 0x300, 0x0300_0000);
        Registers(0x6000);
        GuestMemory.W32(data, 1);
        Invoke(Processmgr, "NID_3E55B5C3", Context, frame);
        Assert.Equal(0u, GuestMemory.R32(data));
        for (int i = 0; i < 13; i++)
        {
            Assert.Equal(0x900u + (uint)i, _state->R[i]);
        }

        Assert.Equal(0x8400_0000u, _state->R[13]);
        Assert.Equal(0x8400_1001u, _state->R[14]);
        Assert.Equal(0x8400_2000u, _state->R[15]);
        Assert.Equal(ExceptionModes.User, _state->Mode);
        Assert.Equal(0u, _state->Thumb);
        Assert.Equal(1u, _state->N);
        Assert.Equal(0x1234_5678_9ABC_DEF0UL, ((ulong*)_state->D)[5]);
        Assert.Equal(0x0300_0000u, _state->Fpscr);
        Assert.Equal(0x8400_3000u, _state->TpidrUro);
    }

    [Fact]
    public void AnExceptionReturnOutsideAnExceptionIsAKernelPanic()
    {
        uint data = (uint)Invoke(Processmgr, "ksceExcpmgrGetData")!;
        GuestMemory.W32(data, 0);
        uint frame = _harness.Alloc(0x400);
        Assert.Throws<InvalidOperationException>(() => Invoke(Processmgr, "NID_3E55B5C3", Context, frame));
    }
}
