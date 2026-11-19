using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// SceIntrmgrForDriver (intrmgr.skprx 3.74): the line table (register with its argument checks, release, the hook and
/// its release), enable/disable/suspend/resume on the device lines, priority and target, the sub-interrupts through the
/// parent driver's operations table, TriggerSubIntr and TriggerSGI calling the handler at once in interrupt context.
/// </summary>
[Collection("guest")]
public sealed class IntrmgrKernelTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int InInterrupt = unchecked((int)0x80027101), Code = unchecked((int)0x80027102);
    private const int Option = unchecked((int)0x80027103), Priority = unchecked((int)0x80027104);
    private const int Target = unchecked((int)0x80027105), SgiMode = unchecked((int)0x80027106);
    private const int Flags = unchecked((int)0x80027107), Handler = unchecked((int)0x80027108);
    private const int Registered = unchecked((int)0x80027109), NotRegistered = unchecked((int)0x8002710A);
    private const int Invalid = unchecked((int)0x80020005), NoSubOps = unchecked((int)0x80020001);

    private readonly HleHarness _harness = new(fixture, "intrmgr");

    public void Dispose() => _harness.Dispose();

    private SystemServicesVsh Services => _harness.Module<SystemServicesVsh>();

    /// <summary>ARM code that stores r0..r2 at <paramref name="target"/> and returns <paramref name="result"/>.</summary>
    private uint Recorder(uint target, uint result = 0)
    {
        uint code = _harness.Alloc(32);
        GuestMemory.W32(code, 0xE59FC00C); // ldr r12, [pc, #12]  (the word at +20)
        GuestMemory.W32(code + 4, 0xE88C0007); // stmia r12, {r0-r2}
        GuestMemory.W32(code + 8, 0xE59F0008); // ldr r0, [pc, #8]  (the word at +24)
        GuestMemory.W32(code + 12, 0xE12FFF1E); // bx lr
        GuestMemory.W32(code + 16, 0xE12FFF1E);
        GuestMemory.W32(code + 20, target);
        GuestMemory.W32(code + 24, result);
        return code;
    }

    private uint Options(uint subCount, uint operations)
    {
        uint option = _harness.Alloc(0x14);
        GuestMemory.W32(option, 0x14);
        GuestMemory.W32(option + 4, subCount);
        GuestMemory.W32(option + 8, operations);
        return option;
    }

    private int Register(uint line, uint handler, uint option = 0, uint priority = 0x80, uint target = 0x10000)
    {
        return Call(Services, "ksceKernelRegisterIntrHandler", line, 0u, 0u, handler, 0x55u, priority, target, option);
    }

    [Fact]
    public void RegistrationChecksItsArguments()
    {
        uint handler = Recorder(_harness.Alloc(16));
        Assert.Equal(Code, Register(0x100, handler));
        Assert.Equal(Flags, Call(Services, "ksceKernelRegisterIntrHandler", 0x40u, 0u, 2u, handler, 0u, 0x80u, 1u, 0u));
        Assert.Equal(Handler, Register(0x40, 0));
        Assert.Equal(Priority, Register(0x40, handler, priority: 0x81));
        Assert.Equal(Target, Register(0x40, handler, target: 0));
        Assert.Equal(Target, Register(0x40, handler, target: 0x10001));
        uint badOption = Options(0, 0);
        GuestMemory.W32(badOption, 0x10);
        Assert.Equal(Option, Register(0x40, handler, badOption));
        Assert.Equal(0, Register(0x40, handler, target: 3));
        Assert.Equal(Registered, Register(0x40, handler));
        Assert.Equal((handler, false, 0x80u, 3u), Services.IntrState(0x40));
        Assert.Equal(0, Call(Services, "ksceKernelReleaseIntrHandler", 0x40u));
        Assert.Equal(NotRegistered, Call(Services, "ksceKernelReleaseIntrHandler", 0x40u));
        Assert.Equal(0, Call(Services, "ksceKernelIsIntrContext"));
    }

    [Fact]
    public void TheLineStateIsKept()
    {
        uint handler = Recorder(_harness.Alloc(16));
        uint word = _harness.Word(0xAA);
        Assert.Equal(NotRegistered, Call(Services, "ksceKernelEnableIntr", 0x50u));
        Assert.Equal(Code, Call(Services, "ksceKernelEnableIntr", 0x5u));
        Assert.Equal(0, Register(0x50, handler));
        Assert.Equal(0, Call(Services, "ksceKernelEnableIntr", 0x50u));
        Assert.True(Services.IntrState(0x50).Enabled);
        Assert.Equal(Invalid, Call(Services, "ksceKernelSuspendIntr", 0x50u, 0u));
        Assert.Equal(0, Call(Services, "ksceKernelSuspendIntr", 0x50u, word));
        Assert.Equal(1u, GuestMemory.R32(word));
        Assert.False(Services.IntrState(0x50).Enabled);
        Assert.Equal(Invalid, Call(Services, "ksceKernelResumeIntr", 0x50u, 2u));
        Assert.Equal(0, Call(Services, "ksceKernelResumeIntr", 0x50u, 1u));
        Assert.True(Services.IntrState(0x50).Enabled);
        Assert.Equal(0, Call(Services, "ksceKernelDisableIntr", 0x50u));
        Assert.False(Services.IntrState(0x50).Enabled);

        Assert.Equal(Priority, Call(Services, "ksceKernelSetIntrPriority", 0x50u, 0x8u));
        Assert.Equal(0, Call(Services, "ksceKernelSetIntrPriority", 0x50u, 0x30u));
        Assert.Equal(0, Call(Services, "ksceKernelGetIntrPriority", 0x50u, word));
        Assert.Equal(0x30u, GuestMemory.R32(word));
        Assert.Equal(Target, Call(Services, "ksceKernelSetIntrTarget", 0x50u, 0x80000001u));
        Assert.Equal(0, Call(Services, "ksceKernelSetIntrTarget", 0x50u, 0x20000u));
        Assert.Equal(0, Call(Services, "ksceKernelGetIntrTarget", 0x50u, word));
        Assert.Equal(2u, GuestMemory.R32(word));
        Assert.Equal(NotRegistered, Call(Services, "ksceKernelGetIntrTarget", 0x51u, word));

        uint hook = Recorder(_harness.Alloc(16));
        Assert.Equal(Invalid, Call(Services, "ksceKernelRegisterIntrHookHandler", 0x50u, hook, 0u));
        Assert.Equal(0, Call(Services, "ksceKernelRegisterIntrHookHandler", 0x50u, hook, word));
        Assert.Equal(handler, GuestMemory.R32(word));
        Assert.Equal(hook, Services.IntrState(0x50).Handler);
        Assert.Equal(Registered, Call(Services, "ksceKernelRegisterIntrHookHandler", 0x50u, hook, word));
        Assert.Equal(0, Call(Services, "ksceKernelReleaseIntrHookHandler", 0x50u));
        Assert.Equal(handler, Services.IntrState(0x50).Handler);
        Assert.Equal(NotRegistered, Call(Services, "ksceKernelReleaseIntrHookHandler", 0x50u));
    }

    [Fact]
    public void SubInterruptsGoThroughTheParentsOperations()
    {
        uint seen = _harness.Alloc(0x100);
        uint operations = _harness.Alloc(0x28);
        uint beforeRegister = Recorder(seen + 0x00);
        uint enable = Recorder(seen + 0x10, 7);
        uint disable = Recorder(seen + 0x20, 8);
        GuestMemory.W32(operations + 4, beforeRegister);
        GuestMemory.W32(operations + 0x14, enable);
        GuestMemory.W32(operations + 0x18, disable);
        uint handler = Recorder(seen + 0x30, 0x1234);
        uint parent = Recorder(_harness.Alloc(16));
        Assert.Equal(0, Register(0x60, parent, priority: 0xA0));
        Assert.Equal(NoSubOps, Call(Services, "ksceKernelRegisterSubIntrHandler", 0x60u, 0u, 0u, handler, 0x77u));
        Assert.Equal(0, Call(Services, "ksceKernelReleaseIntrHandler", 0x60u));
        Assert.Equal(0, Register(0x60, parent, Options(4, operations), priority: 0xA0));
        Assert.Equal(Code, Call(Services, "ksceKernelRegisterSubIntrHandler", 0x60u, 4u, 0u, handler, 0x77u));
        Assert.Equal(NotRegistered, Call(Services, "ksceKernelTriggerSubIntr", 0x60u, 2u, 0u));
        _harness.OnGuest(_ =>
        {
            Assert.Equal(0, Call(Services, "ksceKernelRegisterSubIntrHandler", 0x60u, 2u, 0u, handler, 0x77u));
            Assert.Equal(Registered, Call(Services, "ksceKernelRegisterSubIntrHandler", 0x60u, 2u, 0u, handler, 0x77u));
            Assert.Equal(7, Call(Services, "ksceKernelEnableSubIntr", 0x60u, 2u));
            Assert.Equal(8, Call(Services, "ksceKernelDisableSubIntr", 0x60u, 2u));
            Assert.Equal(NotRegistered, Call(Services, "ksceKernelResumeSubIntr", 0x60u, 2u, 0u, 0u));
            Assert.Equal(NoSubOps, Call(Services, "ksceKernelSuspendSubIntr", 0x60u, 2u, 0u, 0u));
            Assert.Equal(0x1234, Call(Services, "ksceKernelTriggerSubIntr", 0x60u, 2u, 0x99u));
        });

        // the parent's operation saw (line, sub, handler); the handler (argument, its argument, priority)
        Assert.Equal(new uint[] { 0x60, 2, handler }, Words(seen, 3));
        Assert.Equal(new uint[] { 0x60, 2 }, Words(seen + 0x10, 2));
        Assert.Equal(new uint[] { 0x99, 0x77, 0xA0 }, Words(seen + 0x30, 3));
        Assert.Equal(0, Call(Services, "ksceKernelReleaseSubIntrHandler", 0x60u, 2u));
        Assert.Equal(NotRegistered, Call(Services, "ksceKernelReleaseSubIntrHandler", 0x60u, 2u));
        Assert.Equal(NotRegistered, Call(Services, "ksceKernelTriggerSubIntr", 0x60u, 2u, 0u));
    }

    [Fact]
    public void ASoftwareInterruptRunsItsHandler()
    {
        uint seen = _harness.Alloc(0x10);
        uint handler = Recorder(seen);
        Assert.Equal(Code, Call(Services, "ksceKernelTriggerSGI", 16u, 0u, 1u));
        Assert.Equal(SgiMode, Call(Services, "ksceKernelTriggerSGI", 3u, 3u, 1u));
        Assert.Equal(Target, Call(Services, "ksceKernelTriggerSGI", 3u, 0u, 0u));
        Assert.Equal(0, Call(Services, "ksceKernelTriggerSGI", 3u, 2u, 0u));
        Assert.Equal(0, Register(3, handler));
        _harness.OnGuest(_ => Assert.Equal(0, Call(Services, "ksceKernelTriggerSGI", 3u, 1u, 0u)));
        Assert.Equal(new uint[] { 3, 0x55 }, Words(seen, 2));
    }

    [Fact]
    public void RegistrationIsRefusedInsideAHandler()
    {
        // a handler that calls ksceKernelRegisterIntrHandler is in interrupt context: the HLE answers for the state
        Assert.Equal(0, Call(Services, "ksceKernelIsIntrContext"));
        Assert.Equal(InInterrupt, InHandler(() => Register(0x70, 0x1000)));
        Assert.Equal(InInterrupt, InHandler(() => Call(Services, "ksceKernelReleaseIntrHandler", 0x70u)));
    }

    private int InHandler(Func<int> body)
    {
        var field = typeof(SystemServicesVsh).GetField("_intrDepth", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        field.SetValue(Services, 1);
        try
        {
            Assert.Equal(1, Call(Services, "ksceKernelIsIntrContext"));
            return body();
        }
        finally
        {
            field.SetValue(Services, 0);
        }
    }

    private static uint[] Words(uint address, int count)
    {
        return Enumerable.Range(0, count).Select(i => GuestMemory.R32(address + (uint)(i * 4))).ToArray();
    }
}
