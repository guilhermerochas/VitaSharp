using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// CPU exceptions reaching the handlers kernel modules register with SceExcpmgr (sceKernelRegisterExceptionHandler):
/// the exception enters the first handler's code (+8) in undefined/abort mode with LR and SPSR as the ARM ARM sets
/// them; a handler that takes it returns to user code, one that does not jumps to its next handler (+8), and the chain
/// ends at the default handler, where VitaSharp reports the fault as it does with no handler. Handlers are synthetic,
/// in the shape of the firmware's records ({next, reserved, code}).
/// </summary>
[Collection("guest")]
public sealed class ExceptionDeliveryTests(GuestMemoryFixture fixture) : IDisposable
{
    private readonly HleHarness _harness = new(fixture, "excp");

    public void Dispose() => _harness.Dispose();

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

    /// <summary>A record whose code sets r0 to <paramref name="value"/> and returns past the undefined instruction
    /// (MOVS PC, LR: LR is the next instruction for an undefined instruction).</summary>
    private uint TakingHandler(uint value) => Code(0, 0, 0xE3A00000 | value, 0xE1B0F00E);

    /// <summary>A record whose code passes the exception on: it jumps to its next handler's code (+8) with every
    /// register as it found them (the way kubridge chains).</summary>
    private uint PassingHandler() => Code(0, 0,
        0xE92D0003,  // push {r0, r1}
        0xE51F0014,  // ldr r0, [pc, #-0x14]   (the record's next word)
        0xE2800008,  // add r0, r0, #8
        0xE52D0004,  // str r0, [sp, #-4]!
        0xE59D0004,  // ldr r0, [sp, #4]
        0xE59D1008,  // ldr r1, [sp, #8]
        0xE49DF00C); // ldr pc, [sp], #12

    /// <summary>User code: r0 = 5, an undefined instruction, return r0.</summary>
    private uint UserCodeWithUndefined() => Code(0xE3A00005, 0xE7F000F0, 0xE12FFF1E);

    [Fact]
    public void WithoutAHandlerAnUndefinedInstructionIsReportedAsBefore()
    {
        uint user = UserCodeWithUndefined();
        _harness.OnGuest(thread =>
        {
            GuestFaultException e = Assert.Throws<GuestFaultException>(() => thread.CallGuest(user));
            Assert.StartsWith("Undefined instruction 0xE7F000F0", e.Message);
        });
    }

    [Fact]
    public void ARegisteredHandlerTakesTheUndefinedInstructionAndReturnsToUserCode()
    {
        uint handler = TakingHandler(0x77);
        Assert.Equal(0, Call(Procmgr, "sceKernelRegisterExceptionHandler", GuestThread.ExceptionUndefined, 0u, handler));
        uint user = UserCodeWithUndefined();
        _harness.OnGuest(thread =>
        {
            Assert.Equal(0x77u, thread.CallGuest(user));
            // back in user mode, on the user stack
            Assert.Equal(Core.Cpu.ExceptionModes.User, thread.CpuMode);
        });
    }

    /// <summary>Entering the chain puts the core inside an exception (its excpmgr nesting word is 1), what the
    /// exception return (NID_3E55B5C3) checks and clears.</summary>
    [Fact]
    public void EnteringTheChainMarksTheCoreInsideAnException()
    {
        uint data = (uint)Invoke(Procmgr, "ksceExcpmgrGetData")!;
        GuestMemory.W32(data, 0);
        Assert.Equal(0, Call(Procmgr, "sceKernelRegisterExceptionHandler", GuestThread.ExceptionUndefined, 0u, TakingHandler(0x33)));
        uint user = UserCodeWithUndefined();
        _harness.OnGuest(thread => Assert.Equal(0x33u, thread.CallGuest(user)));
        Assert.Equal(1u, GuestMemory.R32(data));
    }

    [Fact]
    public void HandlersArePassedOnInPriorityOrder()
    {
        uint taking = TakingHandler(0x42);
        uint passing = PassingHandler();
        Assert.Equal(0, Call(Procmgr, "sceKernelRegisterExceptionHandler", GuestThread.ExceptionUndefined, 1u, taking));
        Assert.Equal(0, Call(Procmgr, "sceKernelRegisterExceptionHandler", GuestThread.ExceptionUndefined, 0u, passing));
        uint user = UserCodeWithUndefined();
        _harness.OnGuest(thread => Assert.Equal(0x42u, thread.CallGuest(user)));
    }

    [Fact]
    public void AnExceptionNoHandlerTakesReachesTheDefaultHandlerAndIsReported()
    {
        Assert.Equal(0, Call(Procmgr, "sceKernelRegisterExceptionHandler", GuestThread.ExceptionUndefined, 0u, PassingHandler()));
        uint user = UserCodeWithUndefined();
        _harness.OnGuest(thread =>
        {
            GuestFaultException e = Assert.Throws<GuestFaultException>(() => thread.CallGuest(user));
            Assert.Contains($"Undefined instruction 0xE7F000F0 at 0x{user + 4:X8}", e.Message);
            Assert.Contains("no exception handler took it", e.Message);
        });
    }

    [Fact]
    public void AHandlerOfAnotherKindDoesNotSeeTheException()
    {
        Assert.Equal(0, Call(Procmgr, "sceKernelRegisterExceptionHandler", GuestThread.ExceptionDataAbort, 0u, TakingHandler(1)));
        uint user = UserCodeWithUndefined();
        _harness.OnGuest(thread => Assert.Throws<GuestFaultException>(() => thread.CallGuest(user)));
    }

    /// <summary>A jump to an unmapped address is a prefetch abort: abort mode, LR = the address + 4, IFAR = the
    /// address; the handler reads the user LR through STM ^ and returns there with MOVS PC, LR.</summary>
    [Fact]
    public void APrefetchAbortEntersAbortModeWithIfarSet()
    {
        const uint Unmapped = 0xFFF0_0000;
        uint record = _harness.Alloc(8);
        uint handler = Code(0, 0,
            0xEE161F50,  // mrc p15, 0, r1, c6, c0, 2   (IFAR)
            0xE59F2014,  // ldr r2, [pc, #0x14]        (record)
            0xE5821000,  // str r1, [r2]
            0xE2823004,  // add r3, r2, #4
            0xE8C34000,  // stm r3, {lr}^               (the user lr)
            0xE593E000,  // ldr lr, [r3]
            0xE3A00099,  // mov r0, #0x99
            0xE1B0F00E,  // movs pc, lr
            record);
        Assert.Equal(0, Call(Procmgr, "sceKernelRegisterExceptionHandler", GuestThread.ExceptionPrefetchAbort, 0u, handler));
        uint user = Code(
            0xE92D4000,  // push {lr}
            0xE59F3004,  // ldr r3, [pc, #4]
            0xE12FFF33,  // blx r3   (lr = user + 12)
            0xE8BD8000,  // pop {pc}
            Unmapped);
        Assert.False(GuestMemory.IsMapped(Unmapped));
        _harness.OnGuest(thread =>
        {
            Assert.Equal(0x99u, thread.CallGuest(user));
            Assert.Equal(Unmapped, GuestMemory.R32(record));
            Assert.Equal(user + 12, GuestMemory.R32(record + 4));
        });
    }

    /// <summary>
    /// The protocol of a kernel module that hands exceptions to a user-mode handler (kubridge's
    /// kuKernelRegisterExceptionHandler): the abort handler saves the interrupted context (r0-r12, user SP/LR through
    /// STM ^, the faulting pc, the SPSR) on the user stack, moves the user SP below it (LDM ^) and enters the user
    /// handler in user mode with RFE; the user handler edits the context and returns through an undefined
    /// instruction, whose kernel handler loads the context back (LDM ^ for SP/LR) and resumes it with RFE.
    /// </summary>
    [Fact]
    public void AUserModeHandlerRunsAndTheContextItEditedIsResumed()
    {
        const uint Unmapped = 0xFFF0_0000;
        uint userHandler = Code(
            0xE3A01099,  // mov r1, #0x99
            0xE5801000,  // str r1, [r0]            ctx.r0 = 0x99
            0xE5901038,  // ldr r1, [r0, #0x38]
            0xE580103C,  // str r1, [r0, #0x3C]     ctx.pc = ctx.lr (back to the caller of the bad jump)
            0xE7F000F0); // udf #0                  return to the kernel (r0 = ctx)
        uint abortHandler = Code(0, 0,
            0xE52D0004,  // str r0, [sp, #-4]!
            0xE24DD004,  // sub sp, sp, #4
            0xE8CD2000,  // stm sp, {sp}^           the user sp
            0xE49D0004,  // ldr r0, [sp], #4
            0xE2400044,  // sub r0, r0, #0x44       ctx on the user stack
            0xE9801FFE,  // stmib r0, {r1-r12}
            0xE49D1004,  // ldr r1, [sp], #4
            0xE5801000,  // str r1, [r0]            ctx.r0
            0xE2801034,  // add r1, r0, #0x34
            0xE8C16000,  // stm r1, {sp, lr}^       ctx.sp, ctx.lr (user)
            0xE580E03C,  // str lr, [r0, #0x3C]     ctx.pc
            0xE14F1000,  // mrs r1, spsr
            0xE5801040,  // str r1, [r0, #0x40]     ctx.cpsr
            0xE52D0004,  // str r0, [sp, #-4]!
            0xE8DD2000,  // ldm sp, {sp}^           user sp = ctx
            0xE28DD004,  // add sp, sp, #4
            0xE59F1008,  // ldr r1, [pc, #8]        the user handler
            0xE3A02010,  // mov r2, #0x10           user mode, ARM
            0xE92D0006,  // push {r1, r2}
            0xF8BD0A00,  // rfeia sp!
            userHandler);
        uint undefinedHandler = Code(0, 0,
            0xE2801034,  // add r1, r0, #0x34
            0xE8D16000,  // ldm r1, {sp, lr}^       user sp, lr
            0xE590103C,  // ldr r1, [r0, #0x3C]
            0xE5902040,  // ldr r2, [r0, #0x40]
            0xE92D0006,  // push {r1, r2}
            0xE9901FFE,  // ldmib r0, {r1-r12}
            0xE5900000,  // ldr r0, [r0]
            0xF8BD0A00); // rfeia sp!
        Assert.Equal(0, Call(Procmgr, "sceKernelRegisterExceptionHandler", GuestThread.ExceptionPrefetchAbort, 0u, abortHandler));
        Assert.Equal(0, Call(Procmgr, "sceKernelRegisterExceptionHandler", GuestThread.ExceptionUndefined, 0u, undefinedHandler));
        uint user = Code(
            0xE92D4FF0,  // push {r4-r11, lr}
            0xE59F3014,  // ldr r3, [pc, #0x14]
            0xE3A04044,  // mov r4, #0x44           values the context must bring back
            0xE3A0B0BB,  // mov r11, #0xBB
            0xE12FFF33,  // blx r3                  lr = here + 4
            0xE0800004,  // add r0, r0, r4          the handler's 0x99 + 0x44
            0xE080000B,  // add r0, r0, r11         + 0xBB
            0xE8BD8FF0,  // pop {r4-r11, pc}
            Unmapped);
        _harness.OnGuest(thread =>
        {
            // r0 edited by the user handler, r4 and r11 brought back from the saved context
            Assert.Equal(0x99u + 0x44 + 0xBB, thread.CallGuest(user));
            Assert.Equal(Core.Cpu.ExceptionModes.User, thread.CpuMode);
        });
    }
}
