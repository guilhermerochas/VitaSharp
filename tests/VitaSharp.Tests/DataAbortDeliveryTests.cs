using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// Data aborts reaching the SceExcpmgr chain (<see cref="Core.Cpu.DataAborts"/>): a guest access to a page no block
/// maps, once a kernel module registered a data abort handler, enters the handler in abort mode with LR = the
/// instruction + 8, SPSR = the interrupted CPSR (its IT state included), DFAR = the address and DFSR = a translation
/// fault (WnR on a write), with every register as before the instruction; the handler edits the context and returns.
/// An abort no handler takes is let through (the page reads zeros, as without handlers), also inside an IT block.
/// Without a data abort handler nothing changes. Handlers are synthetic, in the firmware's record shape.
/// </summary>
[Collection("guest")]
public sealed class DataAbortDeliveryTests(GuestMemoryFixture fixture) : IDisposable
{
    /// <summary>Pages no block maps (a different one per scenario: a page let through stays readable).</summary>
    private const uint Unmapped = 0xFFE0_0000, UnmappedThrough = 0xFFE1_0000, UnmappedIt = 0xFFE2_0000;

    private readonly HleHarness _harness = new(fixture, "dabt");

    public unsafe void Dispose()
    {
        _harness.Dispose();
        foreach (uint page in new[] { Unmapped, UnmappedThrough, UnmappedIt })
        {
            GuestMemory.MappedTable(GuestMemory.Base)[page >> 12] = 0; // the next run sees the pages unmapped again
        }
    }

    private ProcessmgrKernel Procmgr => _harness.Module<ProcessmgrKernel>();

    private uint Code(params uint[] words)
    {
        uint at = _harness.Alloc((uint)(4 * words.Length + 16));
        for (int i = 0; i < words.Length; i++)
        {
            GuestMemory.W32(at + (uint)(4 * i), words[i]);
        }

        return at;
    }

    /// <summary>
    /// An abort-mode handler that records DFAR, DFSR, LR, SPSR and the interrupted r4 at <paramref name="record"/>, sets
    /// r0 = 0x55 and returns past the faulting ARM instruction (SUBS PC, LR, #4: LR is the instruction + 8).
    /// </summary>
    private uint RecordingHandler(uint record) => Code(0, 0,
        0xEE161F10,  // mrc p15, 0, r1, c6, c0, 0   (DFAR)
        0xEE152F10,  // mrc p15, 0, r2, c5, c0, 0   (DFSR)
        0xE14F3000,  // mrs r3, spsr
        0xE59FC018,  // ldr r12, [pc, #0x18]        (record)
        0xE58C1000,  // str r1, [r12]
        0xE58C2004,  // str r2, [r12, #4]
        0xE58CE008,  // str lr, [r12, #8]
        0xE58C300C,  // str r3, [r12, #12]
        0xE58C4010,  // str r4, [r12, #16]
        0xE3A00055,  // mov r0, #0x55
        0xE25EF004,  // subs pc, lr, #4
        record);

    /// <summary>A handler that passes the exception on to its next handler (+8), the way kubridge chains.</summary>
    private uint PassingHandler() => Code(0, 0,
        0xE92D0003,  // push {r0, r1}
        0xE51F0014,  // ldr r0, [pc, #-0x14]   (the record's next word)
        0xE2800008,  // add r0, r0, #8
        0xE52D0004,  // str r0, [sp, #-4]!
        0xE59D0004,  // ldr r0, [sp, #4]
        0xE59D1008,  // ldr r1, [sp, #8]
        0xE49DF00C); // ldr pc, [sp], #12

    /// <summary>User code: r4 = <paramref name="address"/>, r0 = 5, the access, return r0.</summary>
    private uint UserCode(uint access, uint address) => Code(
        0xE59F4008,  // ldr r4, [pc, #8]
        0xE3A00005,  // mov r0, #5
        access,      // at +8
        0xE12FFF1E,  // bx lr
        address);

    private const uint LoadWriteback = 0xE5B40004; // ldr r0, [r4, #4]!
    private const uint Store = 0xE5840000;         // str r0, [r4]

    [Fact]
    public void WithoutADataAbortHandlerAnUnmappedLoadReadsZerosAsBefore()
    {
        uint user = UserCode(LoadWriteback, Unmapped);
        _harness.OnGuest(thread => Assert.Equal(0u, thread.CallGuest(user)));
        Assert.False(_harness.Emu.Code.DataAbortGuards);
    }

    [Fact]
    public void ARegisteredHandlerGetsTheLoadsFaultAndResumesTheEditedContext()
    {
        uint record = _harness.Alloc(32);
        Assert.Equal(0, Call(Procmgr, "sceKernelRegisterExceptionHandler", GuestThread.ExceptionDataAbort, 0u, RecordingHandler(record)));
        Assert.True(_harness.Emu.Code.DataAbortGuards);
        uint user = UserCode(LoadWriteback, Unmapped);
        _harness.OnGuest(thread =>
        {
            Assert.Equal(0x55u, thread.CallGuest(user));
            Assert.Equal(Core.Cpu.ExceptionModes.User, thread.CpuMode);
        });
        Assert.Equal(Unmapped + 4, GuestMemory.R32(record));            // DFAR: the address accessed
        Assert.Equal(GuestThread.FaultTranslationPage, GuestMemory.R32(record + 4)); // DFSR: translation fault, read
        Assert.Equal(user + 8 + 8, GuestMemory.R32(record + 8));        // LR: the instruction + 8
        Assert.Equal(0x10u, GuestMemory.R32(record + 12) & 0x3F);       // SPSR: user mode, ARM
        Assert.Equal(Unmapped, GuestMemory.R32(record + 16));           // the base register was not written back
    }

    [Fact]
    public void AStoreFaultHasTheWriteBitSet()
    {
        uint record = _harness.Alloc(32);
        Assert.Equal(0, Call(Procmgr, "sceKernelRegisterExceptionHandler", GuestThread.ExceptionDataAbort, 0u, RecordingHandler(record)));
        uint user = UserCode(Store, Unmapped);
        _harness.OnGuest(thread => Assert.Equal(0x55u, thread.CallGuest(user)));
        Assert.Equal(Unmapped, GuestMemory.R32(record));
        Assert.Equal(GuestThread.FaultTranslationPage | GuestThread.FaultWrite, GuestMemory.R32(record + 4));
    }

    [Fact]
    public void AHandlerOfAnotherKindDoesNotArmDataAborts()
    {
        Assert.Equal(0, Call(Procmgr, "sceKernelRegisterExceptionHandler", GuestThread.ExceptionUndefined, 0u, PassingHandler()));
        Assert.False(_harness.Emu.Code.DataAbortGuards);
        uint user = UserCode(LoadWriteback, Unmapped);
        _harness.OnGuest(thread => Assert.Equal(0u, thread.CallGuest(user)));
    }

    /// <summary>The handlers pass the abort on to the default handler (a kubridge with no user handler): the access is
    /// let through and reads zeros, as without handlers.</summary>
    [Fact]
    public void ADataAbortNoHandlerTakesIsLetThroughAndReadsZeros()
    {
        Assert.Equal(0, Call(Procmgr, "sceKernelRegisterExceptionHandler", GuestThread.ExceptionDataAbort, 0u, PassingHandler()));
        uint user = UserCode(LoadWriteback, UnmappedThrough);
        _harness.OnGuest(thread =>
        {
            Assert.Equal(0u, thread.CallGuest(user));
            Assert.Equal(Core.Cpu.ExceptionModes.User, thread.CpuMode);
            Assert.Equal(0u, thread.CallGuest(user)); // the page stays let through
        });
    }

    /// <summary>
    /// A let-through abort inside an IT block resumes the rest of the block with its IT state: after "ite eq" the
    /// faulting LDREQ runs again and the MOVNE that follows stays skipped.
    /// </summary>
    [Fact]
    public void ALetThroughAbortInsideAnItBlockKeepsTheItState()
    {
        Assert.Equal(0, Call(Procmgr, "sceKernelRegisterExceptionHandler", GuestThread.ExceptionDataAbort, 0u, PassingHandler()));
        uint user = Code(
            0x20074903,  // ldr r1, [pc, #12]; movs r0, #7
            0xBF0C4280,  // cmp r0, r0; ite eq
            0x2009680A,  // ldreq r2, [r1]; movne r0, #9
            0xBF004770,  // bx lr; nop
            UnmappedIt);
        _harness.OnGuest(thread => Assert.Equal(7u, thread.CallGuest(user | 1)));
    }
}
