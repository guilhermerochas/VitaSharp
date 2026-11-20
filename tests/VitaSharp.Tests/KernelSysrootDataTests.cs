using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using Xunit;

namespace VitaSharp.Tests;

/// <summary>
/// The FW 3.74 sysroot's data words and the rest of its slots (SceSysrootForKernel/ForDriver): the KBL parameters
/// built from the unit's identity (pscode, OpenPSID, hardware info, QA flags, debug switches, boot flags), the boot
/// mode queries read from them (safe, update, manufacturing, external boot, BSOD reboot, USB wakeup), the sysroot's
/// guest copy and its words, the status word that only rises, the init callbacks by level, the thread manager's time
/// counter and handler structure, the kernel heap of ksceKernelAllocHeapMemory2, the kernel's self auth info, the
/// boot image table and the sysmem counter.
/// </summary>
[Collection("guest")]
public sealed class KernelSysrootDataTests(GuestMemoryFixture fixture) : IDisposable
{
    private const uint InvalidArgument = 0x80020005, IllegalAddress = 0x80020006;

    private readonly HleHarness _harness = new(fixture, "ksysrootdata");

    public void Dispose() => _harness.Dispose();

    private uint Code(params ushort[] halfwords)
    {
        uint code = _harness.Alloc((uint)halfwords.Length * 2 + 4);
        for (int i = 0; i < halfwords.Length; i++)
        {
            GuestMemory.Write(code + (uint)i * 2, halfwords[i]);
        }

        return code | 1;
    }

    private unsafe (uint R0, uint R1) Run(GuestThread thread, uint nid, params uint[] args)
    {
        Action<HleContext> implementation = _harness.Emu.Hle.ImplOf(nid) ?? throw new InvalidOperationException($"0x{nid:X8} not implemented");
        for (int i = 0; i < 4; i++)
        {
            thread.Core.State->R[i] = i < args.Length ? args[i] : 0xA5A5A5A5;
        }

        implementation(thread.Hle);
        return (thread.Core.State->R[0], thread.Core.State->R[1]);
    }

    private uint R0(GuestThread thread, uint nid, params uint[] args) => Run(thread, nid, args).R0;

    [Fact]
    public void TheKblParametersCarryTheUnitsIdentityAndANormalBoot()
    {
        _harness.OnGuest(t =>
        {
            uint kbl = R0(t, 0x9DB56D1F); // ksceKernelSysrootGetKblParam
            Assert.NotEqual(0u, kbl);
            Assert.Equal(0x100, GuestMemory.Read<ushort>(kbl + 2));
            Assert.Equal(0x03740011u, GuestMemory.R32(kbl + 4));
            Assert.Equal(new byte[] { 0x00, 0x01, 0x01, 0x04, 0x00, 0x10, 0x00, 0x00 }, GuestMemory.Span(kbl + 0xA0, 8).ToArray());
            Assert.Equal(ConsoleIdentity.OpenPsId.ToArray(), GuestMemory.Span(kbl + 0x70, 16).ToArray());
            Assert.All(GuestMemory.Span(kbl + 0x30, 16).ToArray(), b => Assert.Equal(0xFF, b));
            Assert.All(GuestMemory.Span(kbl + 0x20, 16).ToArray(), b => Assert.Equal(0, b)); // retail: no QA flags
            Assert.Equal(ConsoleIdentity.HardwareInfo, R0(t, 0x930B1342)); // ksceSysrootGetHardwareInfo

            // the sysroot points at the same parameters and has the firmware's size
            uint sysroot = R0(t, 0x3E455842);
            Assert.Equal(0x41Cu, GuestMemory.R32(sysroot + 8));
            Assert.Equal(kbl, GuestMemory.R32(sysroot + 0x6C));
            Assert.Equal(0x41Cu, R0(t, 0xEEB867C0));

            // a retail unit booted normally: no special mode, a cold boot
            foreach (uint mode in new uint[] { 0x834439A7, 0xB0E1FC67, 0xAE7A8F1D, 0x55392965, 0x89D19090, 0x7B7F8171, 0x4373AC96, 0x79C9AE10, 0xE4EA1960, 0x7918D44E })
            {
                Assert.Equal(0u, R0(t, mode));
            }

            Assert.Equal(0u, R0(t, 0x50FE3B4D)); // the boot byte 0x35 at 0xFF: no internal storage mode (PCH-1000)
            Assert.Equal(1u, R0(t, 0xD7198963)); // ksceKernelIsColdBoot
        });
    }

    [Fact]
    public void TheBootModeQueriesFollowTheKblBytes()
    {
        _harness.OnGuest(t =>
        {
            uint kbl = R0(t, 0x9DB56D1F);
            GuestMemory.Write(kbl + 0x30, (byte)0);
            Assert.Equal(1u, R0(t, 0xB0E1FC67)); // update mode
            GuestMemory.Write(kbl + 0x30, (byte)0xFF);

            GuestMemory.Write(kbl + 0x33, (byte)1);
            Assert.Equal(1u, R0(t, 0x834439A7)); // safe mode byte
            GuestMemory.Write(kbl + 0x33, (byte)0xFF);

            GuestMemory.W32(kbl + 0xC4, 0x118); // wakeup kind 0x18 (bits above 7 ignored)
            Assert.Equal(1u, R0(t, 0x834439A7));
            GuestMemory.W32(kbl + 0xC4, 0xB);
            Assert.Equal(0u, R0(t, 0x834439A7)); // kind 0xB needs the boot loader's bits
            GuestMemory.W32(kbl + 0xCC, ~0x5400u);
            Assert.Equal(1u, R0(t, 0x834439A7));
            GuestMemory.W32(kbl + 0xC4, 4);
            Assert.Equal(0u, R0(t, 0x834439A7)); // kind 4 also needs +0xD0
            GuestMemory.W32(kbl + 0xD0, 1);
            Assert.Equal(1u, R0(t, 0x834439A7));

            GuestMemory.W32(kbl + 0xC4, 0x17);
            Assert.Equal(1u, R0(t, 0x4373AC96)); // BSOD reboot
            GuestMemory.W32(kbl + 0xC4, 0xF);
            Assert.Equal(1u, R0(t, 0x79C9AE10)); // USB enumeration wakeup
            GuestMemory.W32(kbl + 0xC4, 0);
            GuestMemory.W32(kbl + 0xC0, 0x10000);
            Assert.Equal(1u, R0(t, 0x79C9AE10));
            Assert.Equal(0x10000u, R0(t, 0xAB3CC7D0));

            GuestMemory.W32(kbl + 0x6C, 1 | 4 | (1 << 16) | (1 << 19));
            Assert.Equal(1u, R0(t, 0x89D19090));
            Assert.Equal(1u, R0(t, 0x55392965));
            Assert.Equal(1u, R0(t, 0x7918D44E));
            Assert.Equal(1u, R0(t, 0x7B7F8171));
        });
    }

    [Fact]
    public void TheDebugSwitchesAreRefreshedIntoTheKblParameters()
    {
        _harness.OnGuest(t =>
        {
            SystemServicesVsh services = _harness.Module<SystemServicesVsh>();
            uint kbl = R0(t, 0x9DB56D1F);
            Assert.Equal(0u, GuestMemory.R32(kbl + 0x44));
            HleHarness.Call(services, "sceKernelSetDipsw", 0x21u);
            R0(t, 0x9DB56D1F);
            Assert.Equal(2u, GuestMemory.R32(kbl + 0x44));
            HleHarness.Call(services, "sceKernelClearDipsw", 0x21u);
        });
    }

    [Fact]
    public void WordsWrittenBeforeAndAfterTheGuestCopyAreInIt()
    {
        _harness.OnGuest(t =>
        {
            R0(t, 0x7A7E7C0C, 3, 0x1234); // module private word 3
            Assert.Equal(0x1234u, R0(t, 0x37EC12BB, 3));
            uint sysroot = R0(t, 0x3E455842);
            Assert.Equal(0x1234u, GuestMemory.R32(sysroot + 0x34 + 12));
            R0(t, 0x611F17A4, 0x5678);
            Assert.Equal(0x5678u, GuestMemory.R32(sysroot + 0x314));
            Assert.Equal(0x5678u, R0(t, 0x118657C6));
            R0(t, 0x0F2F2B4E, 0x9ABC);
            Assert.Equal(0x9ABCu, R0(t, 0xCC85905B));
            Assert.Equal(0u, R0(t, 0xDD7821AA, 7));
            Assert.Equal(7u, R0(t, 0x340575CB));
            Assert.Equal(0x03740011u, R0(t, 0xD3872270));
        });
    }

    [Fact]
    public void TheStatusWordOnlyRises()
    {
        _harness.OnGuest(t =>
        {
            Assert.Equal(0x100u, R0(t, 0x5C426B19)); // the first process was created
            R0(t, 0xA84676E3, 0x50);
            Assert.Equal(0x100u, R0(t, 0x5C426B19));
            R0(t, 0xA84676E3, 0x200);
            Assert.Equal(0x200u, R0(t, 0x5C426B19));
        });
    }

    [Fact]
    public void InitCallbacksRunNowForAPassedLevelAndOtherwiseWhenTheLevelIsInvoked()
    {
        // adds r0, #1; str r0, [r1]; bx lr: the callback writes flag + 1 into the word its argument names
        uint callback = Code(0x3001, 0x6008, 0x4770);
        _harness.OnGuest(t =>
        {
            uint now = _harness.Word(0);
            uint result = _harness.Word(0);
            Assert.Equal(0u, R0(t, 0x778D0966, callback, 3, now, result)); // level 3 < 8: runs now with flag 0
            Assert.Equal(1u, GuestMemory.R32(now));
            Assert.Equal(1u, GuestMemory.R32(result));
            Assert.Equal(InvalidArgument, R0(t, 0x778D0966, 0, 3, now, 0));
            Assert.Equal(InvalidArgument, R0(t, 0x778D0966, callback, 9, now, 0));

            // back to level 2: levels 3..8 wait for the invocation
            uint sysroot = R0(t, 0x3E455842);
            GuestMemory.W32(sysroot + 0x90, 2);
            uint level4 = _harness.Word(0);
            uint level5 = _harness.Word(0);
            Assert.Equal(0u, R0(t, 0x778D0966, callback, 4, level4, 0));
            Assert.Equal(0u, R0(t, 0x778D0966, callback, 5, level5, 0));
            Assert.Equal(0u, GuestMemory.R32(level4));
            R0(t, 0x93CD44CD, 4); // ksceKernelInvokeInitCallback: levels 3 and 4, flag 0
            Assert.Equal(1u, GuestMemory.R32(level4));
            Assert.Equal(0u, GuestMemory.R32(level5));
            Assert.Equal(4u, GuestMemory.R32(sysroot + 0x90));
            R0(t, 0x7C2C10E2, 5, 2); // a flag above 1: nothing
            Assert.Equal(0u, GuestMemory.R32(level5));
            R0(t, 0x7C2C10E2, 5, 1);
            Assert.Equal(2u, GuestMemory.R32(level5)); // flag 1
            R0(t, 0x93CD44CD, 4); // a level already reached runs nothing again
            Assert.Equal(1u, GuestMemory.R32(level4));

            // a level holds 8 callbacks
            for (int slot = 0; slot < 8; slot++)
            {
                Assert.Equal(0u, R0(t, 0x778D0966, callback, 7, _harness.Word(0), 0));
            }

            Assert.Equal(0x8002000Fu, R0(t, 0x778D0966, callback, 7, _harness.Word(0), 0));
            GuestMemory.W32(sysroot + 0x90, 8);
        });
    }

    [Fact]
    public void TheTimeCounterIsTheThreadManagersOnceRegistered()
    {
        _harness.OnGuest(t =>
        {
            Assert.Equal(IllegalAddress, R0(t, 0x1D8DB3A5, 0));
            uint counter = _harness.Alloc(8);
            GuestMemory.Write(counter, 0x0000_0001_2345_6789UL);
            R0(t, 0x5B5EBFB1, counter);
            Assert.Equal((0x23456789u, 1u), Run(t, 0xE20F6FC8));
            Assert.Equal(0x23456789u, R0(t, 0x41636522));
            uint time = _harness.Alloc(8);
            Assert.Equal(0u, R0(t, 0x1D8DB3A5, time));
            Assert.Equal(0x0000_0001_2345_6789UL, GuestMemory.Read<ulong>(time));
        });
    }

    [Fact]
    public void TheThreadManagersHandlersAreCopiedFromItsStructure()
    {
        uint seven = Code(0x2007, 0x4770); // movs r0, #7; bx lr
        _harness.OnGuest(t =>
        {
            uint handlers = _harness.Alloc(0x30);
            GuestMemory.W32(handlers, 0x2C);
            GuestMemory.W32(handlers + 4, seven); // -> +0x330
            GuestMemory.W32(handlers + 0x14, seven); // -> +0x338
            Assert.Equal(InvalidArgument, R0(t, 0x26458702, handlers));
            Assert.Equal(InvalidArgument, R0(t, 0x26458702, 0));
            GuestMemory.W32(handlers, 0x30);
            Assert.Equal(0u, R0(t, 0x26458702, handlers));
            Assert.Equal(7u, R0(t, 0xD441DC34));
            Assert.Equal(7u, R0(t, 0x7385CADE));
            Assert.Equal(seven, GuestMemory.R32(R0(t, 0x3E455842) + 0x338));
        });
    }

    [Fact]
    public void KernelPidsAllocateFromTheKernelHeap()
    {
        _harness.OnGuest(t =>
        {
            uint block = R0(t, 0xD351EBC8, (uint)KernelSpace.KernelPid, 0x40, 0);
            Assert.NotEqual(0u, block);
            GuestMemory.W32(block + 0x3C, 1);
            Assert.Equal(0u, R0(t, 0x7968F091, (uint)KernelSpace.KernelPid, block));
            uint user = R0(t, 0xD351EBC8, (uint)t.Pid, 0x20, 0); // the process manager's default
            Assert.NotEqual(0u, user);
            Assert.Equal(0u, R0(t, 0x7968F091, (uint)t.Pid, user));
        });
    }

    [Fact]
    public void WithoutAProcessTableTheFirmwaresFallbacksAnswer()
    {
        _harness.OnGuest(t =>
        {
            uint info = _harness.Alloc(0x90);
            Assert.Equal(0u, R0(t, 0x4F0A4066, (uint)t.Pid, info)); // ksceSysrootGetSelfAuthInfo
            Assert.Equal(0x2808000000000001UL, GuestMemory.Read<ulong>(info));
            Assert.Equal(0x10007u, R0(t, 0x7334F1E8));
            uint heap = _harness.Word(0xFFFF);
            R0(t, 0x59B76D23, 0x77);
            Assert.Equal(0u, R0(t, 0x88DE85EF, (uint)t.Pid, heap));
            Assert.Equal(0x77u, GuestMemory.R32(heap));

            // a registered table answers instead
            uint five = Code(0x2005, 0x4770);
            uint table = _harness.Alloc(0x20);
            GuestMemory.W32(table, 0x20);
            GuestMemory.W32(table + 8, five);
            Assert.Equal(InvalidArgument, R0(t, 0x733C243E, info));
            Assert.Equal(0u, R0(t, 0x733C243E, table));
            Assert.Equal(5u, R0(t, 0x7334F1E8));
        });
    }

    [Fact]
    public void TheBootImageTableIsCheckedBeforeItIsRead()
    {
        _harness.OnGuest(t =>
        {
            uint info = _harness.Alloc(0xC);
            Assert.Equal(InvalidArgument, R0(t, 0xF10AB792, 3, info));
            Assert.Equal(IllegalAddress, R0(t, 0xF10AB792, 0, 0));
            Assert.Equal(0x80020009u, R0(t, 0xF10AB792, 0, info));
            GuestMemory.W32(info, 0xC);
            Assert.Equal(0x8001000Cu, R0(t, 0xF10AB792, 0, info)); // no table at +0x3A0
        });
    }

    [Fact]
    public void TheSysmemCounterMovesBySixteen()
    {
        _harness.OnGuest(t =>
        {
            Assert.Equal(4u, R0(t, 0xEE934615));
            Assert.Equal(0x14u, R0(t, 0xEE934615));
            Assert.Equal(0x24u, R0(t, 0xEEF091A7));
            Assert.Equal(0x14u, R0(t, 0xEEF091A7));
        });
    }

    [Fact]
    public void TheDrmAndSecuritySlotsTakeTheirProvidersHandlers()
    {
        uint add = Code(0x1840, 0x4770); // adds r0, r0, r1; bx lr
        _harness.OnGuest(t =>
        {
            uint table = _harness.Alloc(0x40);
            GuestMemory.W32(table, 0x40);
            GuestMemory.W32(table + 0x3C, add);
            Assert.Equal(0u, R0(t, 0x579CC758, table));
            Assert.Equal(5u, R0(t, 0xD04C7723, 2, 3));
            Assert.Equal(0u, R0(t, 0x9A486846)); // an empty entry: 0

            R0(t, 0x35875119, add);
            Assert.Equal(9u, R0(t, 0xD75D4F37, 4, 5));
            R0(t, 0x35875119, 0);
            Assert.Equal(0x80010002u, R0(t, 0xD75D4F37, 4, 5));

            R0(t, 0x71DB83A2, 0); // the licence manager's slot emptied: -1
            Assert.Equal(0xFFFFFFFFu, R0(t, 0x377895EB));
        });
    }
}
