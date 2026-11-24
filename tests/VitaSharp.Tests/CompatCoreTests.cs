using System.Buffers.Binary;
using VitaSharp.Core.Compat;
using VitaSharp.Core.Compat.Allegrex;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// SceCompat's side of the PSP compat core (FW 3.74 SceCompat, docs/notes/psp-compat.md): the core's bus over the
/// window (four 16 MB banks, the hole a mirror of bank 1, the segments), the shared SRAM at 0xBFC00000, the mailbox
/// lines in both directions, InitEx loading the boot code and pcff through SceSblFwLoader, Start releasing the core at
/// 0x88600000 on its own thread, the boot handshake 1-2-3-0, a request on channel 3 taken by WaitAndGetRequest and
/// answered by ReturnValueEx, the core's answer interrupt reaching WaitIntr, WriteSharedCtrl's words read by the core,
/// a virtual interrupt (sceCompatInterrupt) reaching it, and Stop ending the core's thread. The core is a scripted
/// test core that does what a synthetic boot code does, through the same bus (no firmware code).
/// </summary>
[Collection("guest")]
public sealed class CompatCoreTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int NotStarted = unchecked((int)0x80027205), NoCore = unchecked((int)0x80010086);
    private const int Unavailable = unchecked((int)0x80020004), BadLicense = unchecked((int)0x8001000D);
    private const int Timeout = unchecked((int)0x80028005), Empty = unchecked((int)0x80010016);
    private const uint PcbcMagic = 0x43425350, PcffMagic = 0x46465350; // "PSBC", "PSFF"
    private const string ContentId = "UP0000-NPUZ00000_00-0000000000000000";

    private readonly HleHarness _harness = new(fixture, "compatcore");

    public void Dispose()
    {
        _harness.Dispose();
    }

    private Compat Compat => _harness.Module<Compat>();

    private string HostPath(string guest)
    {
        string host = _harness.Emu.Vfs.Resolve(guest, forWrite: true)!;
        Directory.CreateDirectory(Path.GetDirectoryName(host)!);
        return host;
    }

    /// <summary>The user's decrypted boot code (by default a magic word; or the test's MIPS boot) and a plain pcff
    /// image (our own bytes).</summary>
    private void PlaceBootImages(byte[]? bootCode = null)
    {
        byte[] pcbc = bootCode ?? new byte[0x200];
        if (bootCode == null)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(pcbc, PcbcMagic);
        }

        File.WriteAllBytes(HostPath("os0:kd/pcbc.bin"), pcbc);
        byte[] pcff = new byte[0x100];
        BinaryPrimitives.WriteUInt32LittleEndian(pcff, PcffMagic);
        File.WriteAllBytes(HostPath("vs0:app/NPXS10028/pcff.bin"), pcff);
    }

    /// <summary>Activation data of the current account and a free PSP license for <see cref="ContentId"/>.</summary>
    private void PlaceLicense()
    {
        byte[] activation = new byte[0x1038];
        BinaryPrimitives.WriteUInt16BigEndian(activation, 1);
        File.WriteAllBytes(HostPath("tm0:npdrm/act.dat"), activation);
        byte[] rif = new byte[0x98];
        BinaryPrimitives.WriteUInt16BigEndian(rif, 1);
        BinaryPrimitives.WriteUInt16BigEndian(rif.AsSpan(2), 1);
        BinaryPrimitives.WriteUInt16BigEndian(rif.AsSpan(6), 0x000D); // a free license
        System.Text.Encoding.ASCII.GetBytes(ContentId).CopyTo(rif, 0x10);
        File.WriteAllBytes(HostPath($"ux0:pspemu/PSP/LICENSE/{ContentId}.rif"), rif);
    }

    private ScriptedBootCore UseScriptedCore()
    {
        ScriptedBootCore core = new();
        Compat.CoreFactory = bus =>
        {
            core.Bus = bus;
            return core;
        };
        return core;
    }

    /// <summary>The window taken, the images and the license placed, InitEx and SetRif done.</summary>
    private uint Init(byte[]? bootCode = null)
    {
        _harness.Emu.IsSystemProgram = true;
        PlaceBootImages(bootCode);
        PlaceLicense();
        uint bases = _harness.Alloc(8);
        Assert.Equal(0, Call(Compat, "sceCompatAllocCdramWithHole", bases));
        Assert.Equal(0, Call(Compat, "sceCompatSetRif", _harness.Str(ContentId)));
        Assert.Equal(0, Call(Compat, "sceCompatInitEx", 1));
        return GuestMemory.R32(bases);
    }

    [Fact]
    public void TheBankMapReachesTheWindowFromEverySegmentAndTheHoleMirrorsBankOne()
    {
        uint window = _harness.Alloc(0x100);
        CompatSharedSram sram = new();
        PspBus bus = new(sram, new CompatMailbox());
        bus.SetBank(0, window | 1);
        bus.SetBank(1, (window + 0x40) | 1);
        bus.SetBank(2, (window + 0x40) | 1);
        bus.SetBank(3, window + 0x80); // bit 0 clear: unmapped

        bus.Write32(0x88000010, 0x11111111);
        Assert.Equal(0x11111111u, GuestMemory.R32(window + 0x10));
        Assert.Equal(0x11111111u, bus.Read32(0xA8000010));
        Assert.Equal(0x11111111u, bus.Read32(0x08000010));
        Assert.Equal(0x11111111u, bus.Read32(0x48000010));
        bus.Write16(0x89000004, 0x2222);
        Assert.Equal((ushort)0x2222, GuestMemory.Read<ushort>(window + 0x44));
        Assert.Equal((ushort)0x2222, bus.Read16(0x8A000004)); // the hole is bank 1 again
        bus.Write8(0x8A000006, 0x33);
        Assert.Equal((byte)0x33, GuestMemory.Read<byte>(window + 0x46));

        Assert.Equal(0u, bus.Read32(0x8B000000));
        Assert.Equal(1, bus.UnknownAccesses);
        bus.Write32(0xBFC00120, 0xCAFEBABE);
        Assert.Equal(0xCAFEBABEu, sram.Read32(0x120));
        bus.Write8(0xBFC00121, 0x00);
        Assert.Equal(0xCAFE00BEu, sram.Read32(0x120));
        Assert.Equal(1, bus.UnknownAccesses);
    }

    [Fact]
    public void TheMailboxRingsEachSideOnceUntilAcknowledged()
    {
        CompatMailbox mailbox = new();
        List<int> armInterrupts = [];
        List<bool> levels = [];
        mailbox.ArmInterrupt = armInterrupts.Add;
        mailbox.PspInterruptLevel = levels.Add;

        mailbox.PspRing(4);
        mailbox.PspRing(4);
        Assert.Equal([4], armInterrupts);
        mailbox.ArmAcknowledge(4);
        mailbox.PspRing(4);
        Assert.Equal([4, 4], armInterrupts);

        mailbox.ArmRing(7);
        mailbox.ArmRing(0x19);
        Assert.Equal([true], levels);
        Assert.Equal((1u << 7) | (1u << 0x19), mailbox.PspRead32(CompatMailbox.PspPendingRegister));
        mailbox.PspWrite32(CompatMailbox.PspAcknowledgeRegister, 1u << 7);
        Assert.Equal([true], levels);
        mailbox.PspWrite32(CompatMailbox.PspAcknowledgeRegister, 1u << 0x19);
        Assert.Equal([true, false], levels);

        // the core rings by pulsing a bit of 0xBC300050: only a bit going from 0 to 1 rings its line
        mailbox.PspWrite32(CompatMailbox.RingRegister, 1u << 8);
        Assert.Equal([4, 4, 8], armInterrupts);
        mailbox.ArmAcknowledge(8);
        mailbox.PspWrite32(CompatMailbox.RingRegister, 1u << 8);
        Assert.Equal([4, 4, 8], armInterrupts);
        mailbox.PspWrite32(CompatMailbox.RingRegister, 0);
        mailbox.PspWrite32(CompatMailbox.RingRegister, (1u << 8) | (1u << 5));
        Assert.Equal([4, 4, 8, 5, 8], armInterrupts);
        Assert.Equal((1u << 8) | (1u << 5) | (1u << 4), mailbox.ArmPending); // line 4 still waits for the ARM
    }

    /// <summary>The mailbox sits in the PSP interrupt controller's block at 0xBC300040..0x5F: the core's ring there
    /// reaches the ARM; the controller's own registers and the PSP systimers (0xBC500000) are not the mailbox.</summary>
    [Fact]
    public void TheCoreReachesTheMailboxAtTheInterruptControllersBlock()
    {
        CompatMailbox mailbox = new();
        List<int> armInterrupts = [];
        mailbox.ArmInterrupt = armInterrupts.Add;
        PspBus bus = new(new CompatSharedSram(), mailbox);
        bus.Write32(0xBC500010, 1u << 4);
        bus.Write32(0xBC300000, 1u << 4);
        Assert.Empty(armInterrupts);
        bus.Write32(0xBC300050, 1u << 4);
        Assert.Equal([4], armInterrupts);
        mailbox.ArmRing(9);
        Assert.Equal(1u << 9, bus.Read32(0xBC300040));
        bus.Write32(0xBC300044, 1u << 9);
        Assert.Equal(0u, bus.Read32(0xBC300040));
        Assert.Equal(1u << 4, bus.Read32(0xBC300048));
    }

    /// <summary>Without the user's decrypted boot code InitEx gets the loader's "not supported" (the secure processor's
    /// layer), and nothing needing a core works; without the window it is 0x80027205.</summary>
    [Fact]
    public void InitExNeedsTheWindowAndTheDecryptedBootCode()
    {
        _harness.OnGuest(_ =>
        {
            _harness.Emu.IsSystemProgram = true;
            Assert.Equal(unchecked((int)0x8002F001), Call(Compat, "sceCompatInitEx", 1));
            uint bases = _harness.Alloc(8);
            Assert.Equal(0, Call(Compat, "sceCompatAllocCdramWithHole", bases));
            byte[] pcff = new byte[0x10];
            File.WriteAllBytes(HostPath("vs0:app/NPXS10028/pcff.bin"), pcff);
            Assert.Equal(Unavailable, Call(Compat, "sceCompatInitEx", 1));
            Assert.Equal(NotStarted, Call(Compat, "sceCompatWriteSharedCtrl", _harness.Alloc(12)));
            Assert.Equal(NoCore, Call(Compat, "sceCompatInterrupt", 9));
            Assert.Equal(NotStarted, Call(Compat, "sceCompatReturnValueEx", 0u, _harness.Alloc(8), 0UL));
        });
    }

    /// <summary>InitEx loads pcff and the boot code into the window at their offsets and the seed at + 0x3FFF00; the
    /// core's banks point at the window; no thread runs before Start.</summary>
    [Fact]
    public void InitExLoadsTheImagesAndMapsTheBanks()
    {
        UseScriptedCore();
        _harness.OnGuest(_ =>
        {
            uint window = Init();
            Assert.Equal(PcbcMagic, GuestMemory.R32(window + 0x600000));
            Assert.Equal(PcffMagic, GuestMemory.R32(window + 0x3000000));
            Assert.Equal(window, Compat.Bus.Bank(0));
            Assert.Equal(window + 0x1000000, Compat.Bus.Bank(1));
            Assert.Equal(window + 0x1000000, Compat.Bus.Bank(2));
            Assert.Equal(window + 0x3000000, Compat.Bus.Bank(3));
            Assert.Equal(PcbcMagic, Compat.Bus.Read32(0x88600000));
            Assert.Equal(GuestMemory.R32(window + 0x3FFF00), Compat.Bus.Read32(0x883FFF00));
            Assert.Null(Compat.Runner);
            Assert.Equal(0, Call(Compat, "sceCompatGetStatus"));
        });
    }

    /// <summary>sceCompatStart's license step is SceNpDrm's PSP key query, as the module calls it: a license that
    /// passes SceNpDrm's checks still gets "not supported" (the title key is the secure processor's), so a retail unit
    /// does not start the core (0x8001000D), and neither without the license file; the stop path runs each time
    /// (state 1, no thread).</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void StartTakesTheLicenseQuerysAnswer(bool licensePresent)
    {
        UseScriptedCore();
        _harness.OnGuest(_ =>
        {
            Init();
            if (!licensePresent)
            {
                File.Delete(HostPath($"ux0:pspemu/PSP/LICENSE/{ContentId}.rif"));
            }

            Assert.Equal(BadLicense, Call(Compat, "sceCompatStart"));
            Assert.Equal(1, Call(Compat, "sceCompatGetStatus"));
            Assert.Null(Compat.Runner);
        });
    }

    /// <summary>The whole phase-1 protocol with the scripted boot (see <see cref="ScriptedBootCore"/>).</summary>
    [Fact]
    public void TheBootHandshakeARequestTheControllerAndAVirtualInterruptReachTheCore()
    {
        ScriptedBootCore core = UseScriptedCore();
        _harness.OnGuest(_ =>
        {
            uint window = Init();
            Assert.Equal(0, Compat.ReleaseCoreForTests()); // past the start path's license step (see StartTakesTheLicenseQuerysAnswer)
            Assert.Equal(2, Call(Compat, "sceCompatGetStatus"));
            Assert.True(Compat.Runner!.Running);
            Assert.Equal(0x88600000u, core.ResetPc);

            Assert.Equal(0, Call(Compat, "sceCompatWaitSpecialRequest", 1));
            Assert.Equal(PcbcMagic, core.BootWord);
            Assert.True(core.SawAcknowledged);
            Assert.Equal(0u, GuestMemory.R32(window + 0x3D78000)); // back to idle after state 3

            int request = Call(Compat, "sceCompatWaitAndGetRequest", 3u, 0u);
            Assert.Equal((int)(ScriptedBootCore.RequestAddress >> 1), request);
            uint located = window + (((uint)request << 1) & 0x3FFFFFF);
            Assert.Equal(ScriptedBootCore.RequestCommand, GuestMemory.R32(located));

            Assert.Equal(0, Call(Compat, "sceCompatWaitIntr", 0u)); // the semaphore starts at 1
            uint info = _harness.Alloc(8);
            GuestMemory.W32(info, 5);
            GuestMemory.W32(info + 4, ScriptedBootCore.RequestAddress + 0x100);
            Assert.Equal(0, Call(Compat, "sceCompatReturnValueEx", 0u, info, 0x1122334455667788UL));
            Assert.Equal(0, Call(Compat, "sceCompatWaitIntr", 0u)); // the core's answer interrupt
            Assert.Equal([0x55667788u, 0x11223344u, 5u, 0u, ScriptedBootCore.RequestAddress + 0x100, 0u], core.Answer);

            uint controller = _harness.Alloc(12);
            GuestMemory.W32(controller, 0x0000_8000);
            GuestMemory.W32(controller + 4, 0x7F80_7F80);
            GuestMemory.W32(controller + 8, 0x0000_0001);
            Assert.Equal(0, Call(Compat, "sceCompatWriteSharedCtrl", controller));
            Assert.Equal(0, Call(Compat, "sceCompatInterrupt", 9));
            Until(() => core.Done, "the core to take the virtual interrupt");
            Assert.Equal([0x0000_8000u, 0x7F80_7F80u, 0x0000_0001u], core.Controller);
            Assert.Equal(9, core.VirtualInterrupt);
            Assert.Equal(0u, Compat.SharedSram.Read32(0x8C0 + 9 * 8));

            Assert.Equal(0, Call(Compat, "sceCompatStop"));
            Assert.Equal(1, Call(Compat, "sceCompatGetStatus"));
            Assert.Null(Compat.Runner);
            Assert.True(core.StoppedByHost);
            Assert.Equal(NoCore, Call(Compat, "sceCompatInterrupt", 9));
        });
    }

    /// <summary>A core that never reaches state 1 fails the handshake after 5 s of its own time (0x80028005), however
    /// long that takes on the host.</summary>
    [Fact]
    public void TheBootHandshakeTimesOutOnTheCoresClock()
    {
        ScriptedBootCore core = UseScriptedCore();
        core.NeverReady = true;
        _harness.OnGuest(_ =>
        {
            Init();
            Assert.Equal(0, Compat.ReleaseCoreForTests()); // past the start path's license step (see StartTakesTheLicenseQuerysAnswer)
            Assert.Equal(Timeout, Call(Compat, "sceCompatWaitSpecialRequest", 1));
            Assert.True(Compat.Runner!.CoreTimeUs >= 5_000_000);
            Assert.Equal(NotStarted, Call(Compat, "sceCompatWriteSharedCtrl", _harness.Alloc(12)));
            Assert.Equal(0, Call(Compat, "sceCompatStop"));
        });
    }

    private const int S0 = 16, S1 = 17, S2 = 18, S3 = 19, S4 = 20;
    private const uint MipsResults = 0x200000; // window offset (PSP 0x88200000) where the boot writes what it saw

    private static uint Andi(int rt, int rs, int immediate)
    {
        return AllegrexAsm.IType(0x0C, rt, rs, immediate);
    }

    private static uint And(int rd, int rs, int rt)
    {
        return AllegrexAsm.RType(0x24, rd, rs, rt);
    }

    /// <summary>
    /// A synthetic boot code (our own MIPS, assembled here), at PSP 0x88600000 with its interrupt handler at
    /// 0x88601000: s0 = the handshake word 0x8BD78000, s1 = the shared SRAM 0xBFC00000, s2 = the mailbox 0xBC300040,
    /// s3 = its request 0x88100000, s4 = its results 0x88200000. It sets EBase and Status (IE, IM2: the reset Status
    /// has ERL and BEV), writes handshake state 1, spins until 2, writes 3, posts command 0x1234 on channel 3 in slot 0
    /// and rings line 4 (a pulse of bit 4 at 0xBC300050), then halts in a loop. The handler reads the pending lines: line 4 (the slot taken) is
    /// acknowledged; line 7 (the answer) is acknowledged, its six words copied to the results and line 7 rung back to
    /// the ARM; line 0x19 (virtual interrupt 9) is acknowledged, its slot's word saved and cleared, the controller
    /// words (0x140, 0x120..0x128) saved, and 9 written as the "done" mark; then eret.
    /// </summary>
    private static byte[] MipsBoot()
    {
        uint[] main =
        [
            AllegrexAsm.Lui(S0, 0x8BD7),
            AllegrexAsm.Ori(S0, S0, 0x8000),
            AllegrexAsm.Lui(S1, 0xBFC0),
            AllegrexAsm.Lui(S2, 0xBC30),
            AllegrexAsm.Ori(S2, S2, 0x0040),
            AllegrexAsm.Lui(S3, 0x8810),
            AllegrexAsm.Lui(S4, 0x8820),
            AllegrexAsm.Lui(AllegrexAsm.T0, 0x8860),
            AllegrexAsm.Ori(AllegrexAsm.T0, AllegrexAsm.T0, 0x1000),
            AllegrexAsm.Mtc0(AllegrexAsm.T0, AllegrexCore.Cop0EBase),
            AllegrexAsm.Ori(AllegrexAsm.T0, AllegrexAsm.Zero, 0x0401), // IM2 | IE
            AllegrexAsm.Mtc0(AllegrexAsm.T0, AllegrexCore.Cop0Status),
            AllegrexAsm.Addiu(AllegrexAsm.T0, AllegrexAsm.Zero, 1),
            AllegrexAsm.Sw(AllegrexAsm.T0, S0, 0), // state 1
            AllegrexAsm.Addiu(AllegrexAsm.T2, AllegrexAsm.Zero, 2),
            AllegrexAsm.Lw(AllegrexAsm.T1, S0, 0), // 15: wait for 2
            AllegrexAsm.Bne(AllegrexAsm.T1, AllegrexAsm.T2, -2),
            AllegrexAsm.Nop,
            AllegrexAsm.Addiu(AllegrexAsm.T0, AllegrexAsm.Zero, 3),
            AllegrexAsm.Sw(AllegrexAsm.T0, S0, 0), // state 3
            AllegrexAsm.Ori(AllegrexAsm.T0, AllegrexAsm.Zero, 0x1234),
            AllegrexAsm.Sw(AllegrexAsm.T0, S3, 0), // the request's command
            AllegrexAsm.Lui(AllegrexAsm.T0, 3),
            AllegrexAsm.Sw(AllegrexAsm.T0, S1, 0x800), // slot 0: channel 3
            AllegrexAsm.Sw(S3, S1, 0x804), // slot 0: the request's address
            AllegrexAsm.Sw(AllegrexAsm.Zero, S2, 0x10), // ring line 4: bit 4 of 0xBC300050 cleared, then set
            AllegrexAsm.Addiu(AllegrexAsm.T0, AllegrexAsm.Zero, 1 << 4),
            AllegrexAsm.Sw(AllegrexAsm.T0, S2, 0x10),
            AllegrexAsm.Halt, // 28
            AllegrexAsm.Beq(AllegrexAsm.Zero, AllegrexAsm.Zero, -2),
            AllegrexAsm.Nop,
        ];
        uint[] handler =
        [
            AllegrexAsm.Lw(AllegrexAsm.K0, S2, 0), // the lines pending for the core
            Andi(AllegrexAsm.K1, AllegrexAsm.K0, 1 << 4),
            AllegrexAsm.Beq(AllegrexAsm.K1, AllegrexAsm.Zero, 3),
            AllegrexAsm.Nop,
            AllegrexAsm.Addiu(AllegrexAsm.T0, AllegrexAsm.Zero, 1 << 4),
            AllegrexAsm.Sw(AllegrexAsm.T0, S2, 4), // acknowledge line 4
            Andi(AllegrexAsm.K1, AllegrexAsm.K0, 1 << 7), // 6
            AllegrexAsm.Beq(AllegrexAsm.K1, AllegrexAsm.Zero, 18),
            AllegrexAsm.Nop,
            AllegrexAsm.Addiu(AllegrexAsm.T0, AllegrexAsm.Zero, 1 << 7),
            AllegrexAsm.Sw(AllegrexAsm.T0, S2, 4), // acknowledge line 7
            AllegrexAsm.Lw(AllegrexAsm.T1, S1, 0x840),
            AllegrexAsm.Sw(AllegrexAsm.T1, S4, 0),
            AllegrexAsm.Lw(AllegrexAsm.T1, S1, 0x844),
            AllegrexAsm.Sw(AllegrexAsm.T1, S4, 4),
            AllegrexAsm.Lw(AllegrexAsm.T1, S1, 0x848),
            AllegrexAsm.Sw(AllegrexAsm.T1, S4, 8),
            AllegrexAsm.Lw(AllegrexAsm.T1, S1, 0x84C),
            AllegrexAsm.Sw(AllegrexAsm.T1, S4, 0xC),
            AllegrexAsm.Lw(AllegrexAsm.T1, S1, 0x850),
            AllegrexAsm.Sw(AllegrexAsm.T1, S4, 0x10),
            AllegrexAsm.Lw(AllegrexAsm.T1, S1, 0x854),
            AllegrexAsm.Sw(AllegrexAsm.T1, S4, 0x14),
            AllegrexAsm.Sw(AllegrexAsm.Zero, S2, 0x10), // ring line 7 to the ARM: the answer interrupt
            AllegrexAsm.Addiu(AllegrexAsm.T0, AllegrexAsm.Zero, 1 << 7),
            AllegrexAsm.Sw(AllegrexAsm.T0, S2, 0x10),
            AllegrexAsm.Lui(AllegrexAsm.K1, 0x0200), // 26
            And(AllegrexAsm.K1, AllegrexAsm.K0, AllegrexAsm.K1),
            AllegrexAsm.Beq(AllegrexAsm.K1, AllegrexAsm.Zero, 15),
            AllegrexAsm.Nop,
            AllegrexAsm.Sw(AllegrexAsm.K1, S2, 4), // acknowledge line 0x19
            AllegrexAsm.Lw(AllegrexAsm.T1, S1, 0x8C0 + 9 * 8),
            AllegrexAsm.Sw(AllegrexAsm.T1, S4, 0x18), // virtual interrupt 9's slot
            AllegrexAsm.Sw(AllegrexAsm.Zero, S1, 0x8C0 + 9 * 8),
            AllegrexAsm.Lw(AllegrexAsm.T1, S1, 0x140),
            AllegrexAsm.Sw(AllegrexAsm.T1, S4, 0x1C),
            AllegrexAsm.Lw(AllegrexAsm.T1, S1, 0x120),
            AllegrexAsm.Sw(AllegrexAsm.T1, S4, 0x20),
            AllegrexAsm.Lw(AllegrexAsm.T1, S1, 0x124),
            AllegrexAsm.Sw(AllegrexAsm.T1, S4, 0x24),
            AllegrexAsm.Lw(AllegrexAsm.T1, S1, 0x128),
            AllegrexAsm.Sw(AllegrexAsm.T1, S4, 0x28),
            AllegrexAsm.Addiu(AllegrexAsm.T1, AllegrexAsm.Zero, 9),
            AllegrexAsm.Sw(AllegrexAsm.T1, S4, 0x2C), // done
            AllegrexAsm.Eret, // 44
        ];
        byte[] image = new byte[0x2000];
        for (int i = 0; i < main.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(i * 4), main[i]);
        }

        for (int i = 0; i < handler.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x1000 + i * 4), handler[i]);
        }

        return image;
    }

    /// <summary>The same protocol with the real Allegrex interpreter running <see cref="MipsBoot"/>: the boot
    /// handshake, channel 3's request and its answer (and the core's answer interrupt reaching WaitIntr), the
    /// controller words and virtual interrupt 9 through the interrupt controller line, then Stop.</summary>
    [Fact]
    public void TheAllegrexRunsASyntheticBootThroughTheWholeProtocol()
    {
        _harness.OnGuest(_ =>
        {
            uint window = Init(MipsBoot());
            Assert.Equal(0, Compat.ReleaseCoreForTests()); // past the start path's license step
            Assert.Equal(0, Call(Compat, "sceCompatWaitSpecialRequest", 1));
            Assert.Equal(0u, GuestMemory.R32(window + 0x3D78000));

            int request = Call(Compat, "sceCompatWaitAndGetRequest", 3u, 0u);
            Assert.Equal((int)(0x88100000u >> 1), request);
            Assert.Equal(0x1234u, GuestMemory.R32(window + (((uint)request << 1) & 0x3FFFFFF)));

            Assert.Equal(0, Call(Compat, "sceCompatWaitIntr", 0u)); // the semaphore starts at 1
            uint info = _harness.Alloc(8);
            GuestMemory.W32(info, 7);
            GuestMemory.W32(info + 4, 0x88100100);
            Assert.Equal(0, Call(Compat, "sceCompatReturnValueEx", 0u, info, 0xA1B2C3D4E5F60718UL));
            Assert.Equal(0, Call(Compat, "sceCompatWaitIntr", 0u)); // rung back by the handler
            uint results = window + MipsResults;
            Assert.Equal(0xE5F60718u, GuestMemory.R32(results));
            Assert.Equal(0xA1B2C3D4u, GuestMemory.R32(results + 4));
            Assert.Equal(7u, GuestMemory.R32(results + 8));
            Assert.Equal(0u, GuestMemory.R32(results + 0xC));
            Assert.Equal(0x88100100u, GuestMemory.R32(results + 0x10));
            Assert.Equal(0u, GuestMemory.R32(results + 0x14));

            uint controller = _harness.Alloc(12);
            GuestMemory.W32(controller, 0x0000_4000);
            GuestMemory.W32(controller + 4, 0x8080_8080);
            GuestMemory.W32(controller + 8, 0x0000_0002);
            Assert.Equal(0, Call(Compat, "sceCompatWriteSharedCtrl", controller));
            Assert.Equal(0, Call(Compat, "sceCompatInterrupt", 9));
            Until(() => GuestMemory.R32(results + 0x2C) == 9, "the boot's handler to take virtual interrupt 9");
            Assert.Equal(1u, GuestMemory.R32(results + 0x18));
            Assert.Equal(0u, Compat.SharedSram.Read32(0x8C0 + 9 * 8));
            Assert.Equal(0x0000_4000u, GuestMemory.R32(results + 0x1C));
            Assert.Equal(0x0000_4000u, GuestMemory.R32(results + 0x20));
            Assert.Equal(0x8080_8080u, GuestMemory.R32(results + 0x24));
            Assert.Equal(0x0000_0002u, GuestMemory.R32(results + 0x28));
            Assert.Null(Compat.Runner!.Failure);

            Assert.Equal(0, Call(Compat, "sceCompatStop"));
            Assert.Null(Compat.Runner);
            Assert.Equal(1, Call(Compat, "sceCompatGetStatus"));
        });
    }

    /// <summary>A channel's ring holds 16 requests: the 17th stays in the core's slot (not acknowledged) with the
    /// overflow flag, and goes in when a request is taken (then acknowledged), as the 3.74 module does it: without
    /// adding it to the ring's count, so the wake it had finds the count at 0 (0x80010016, an empty ring's answer). A
    /// command naming a channel past 15 is ignored and stays unacknowledged.</summary>
    [Fact]
    public void AFullRingKeepsTheCoresEntryUntilARequestIsTaken()
    {
        _harness.OnGuest(_ =>
        {
            _harness.Emu.IsSystemProgram = true;
            Assert.Equal(0, Call(Compat, "sceCompatAllocCdramWithHole", _harness.Alloc(8)));
            CompatSharedSram sram = Compat.SharedSram;
            CompatMailbox mailbox = Compat.Mailbox;
            for (uint i = 0; i < 17; i++)
            {
                sram.Write32(0x800, 5u << 16);
                sram.Write32(0x804, 0x88000000 + i * 0x80);
                mailbox.PspRing(4);
                bool acknowledged = (mailbox.ArmPending & (1u << 4)) == 0;
                Assert.Equal(i < 16, acknowledged);
                mailbox.PspAcknowledge(4);
            }

            for (uint i = 0; i < 16; i++)
            {
                int request = Call(Compat, "sceCompatWaitAndGetRequest", 5u, 0u);
                Assert.Equal((int)((0x88000000 + i * 0x80) >> 1), request);
                if (i == 0)
                {
                    Assert.Equal(0u, mailbox.ArmPending & (1u << 4)); // the kept entry went in
                    Assert.NotEqual(0u, mailbox.PspPending & (1u << 4));
                }
            }

            // the 3.74 module puts the kept entry in the ring without counting it: the 17th wake finds the count at 0
            Assert.Equal(Empty, Call(Compat, "sceCompatWaitAndGetRequest", 5u, 0u));

            sram.Write32(0x810, 0x10u << 16); // slot 2 names channel 16: ignored, not acknowledged
            mailbox.PspRing(6);
            Assert.NotEqual(0u, mailbox.ArmPending & (1u << 6));

            // channel 0's semaphore ("SceCompatVirtualHandler") starts with one count: its empty ring answers at once
            Assert.Equal(Empty, Call(Compat, "sceCompatWaitAndGetRequest", 0u, 0u));
        });
    }

    /// <summary>
    /// The synthetic boot, as a script over the bus (the core's own accesses): checks the boot code's first word at
    /// 0x88600000, writes handshake state 1 at 0x8BD78000, waits for 2, writes 3; posts a request (command
    /// <see cref="RequestCommand"/> at <see cref="RequestAddress"/>) on channel 3 in slot 0 and rings line 4; on its
    /// interrupt line it reads the pending mailbox lines, acknowledges them, takes the answer of line 7 from 0x840 and
    /// rings line 7 back (the answer interrupt sceCompatWaitIntr waits for); it waits for the controller mark at 0x140
    /// and reads the three words; then for line 0x19 (virtual interrupt 9): its slot 0x8C0 + 9 * 8 holds 1, which it
    /// clears; then it halts.
    /// </summary>
    private sealed class ScriptedBootCore : ICompatCore
    {
        public const uint RequestAddress = 0x88100000, RequestCommand = 0x1234;
        private const uint Handshake = 0x8BD78000, Mailbox = 0xBC300040, Sram = 0xBFC00000;

        private int _state;
        private bool _lineHigh;

        public IAllegrexBus Bus = null!;
        public uint ResetPc;
        public uint BootWord;
        public bool SawAcknowledged;
        public bool NeverReady;
        public uint[] Answer = [];
        public uint[] Controller = [];
        public int VirtualInterrupt;
        public volatile bool Done;
        public volatile bool StoppedByHost;

        public bool Halted => Done;

        public void Reset(uint pc)
        {
            ResetPc = pc;
            _state = 0;
        }

        public void RaiseInterrupt(int line)
        {
            Assert.Equal(AllegrexCore.InterruptControllerLine, line);
            _lineHigh = true;
        }

        public void ClearInterrupt(int line)
        {
            _lineHigh = false;
        }

        public void Stop()
        {
            StoppedByHost = true;
        }

        public int Run(int cycles)
        {
            if (Done)
            {
                return 0; // halted
            }

            switch (_state)
            {
                case 0:
                    BootWord = Bus.Read32(0x88600000);
                    if (NeverReady)
                    {
                        return cycles;
                    }

                    Bus.Write32(Handshake, 1);
                    _state = 1;
                    return cycles;
                case 1:
                    if (Bus.Read32(Handshake) != 2)
                    {
                        return 100;
                    }

                    SawAcknowledged = true;
                    Bus.Write32(Handshake, 3);
                    _state = 2;
                    return 100;
                case 2:
                    Bus.Write32(RequestAddress, RequestCommand);
                    Bus.Write32(Sram + 0x800, 3u << 16);
                    Bus.Write32(Sram + 0x804, RequestAddress);
                    Bus.Write32(Mailbox + 0x10, 0);
                    Bus.Write32(Mailbox + 0x10, 1u << 4);
                    _state = 3;
                    return 100;
                default:
                    return Serve();
            }
        }

        private int Serve()
        {
            uint pending = _lineHigh ? Bus.Read32(Mailbox) : 0;
            for (int line = 0; line < 32; line++)
            {
                if ((pending & (1u << line)) != 0)
                {
                    Bus.Write32(Mailbox + 4, 1u << line);
                    Take(line);
                }
            }

            if (_state == 4 && Bus.Read32(Sram + 0x140) != 0)
            {
                Controller = [Bus.Read32(Sram + 0x120), Bus.Read32(Sram + 0x124), Bus.Read32(Sram + 0x128)];
                _state = 5;
            }

            return 50;
        }

        private void Take(int line)
        {
            if (line == 7 && _state == 3)
            {
                uint[] answer = new uint[6];
                for (int i = 0; i < answer.Length; i++)
                {
                    answer[i] = Bus.Read32(Sram + 0x840 + (uint)i * 4);
                }

                Answer = answer;
                Bus.Write32(Mailbox + 0x10, 0);
                Bus.Write32(Mailbox + 0x10, 1u << 7);
                _state = 4;
            }
            else if (line == 0x19 && _state == 5 && Bus.Read32(Sram + 0x8C0 + 9 * 8) == 1)
            {
                Bus.Write32(Sram + 0x8C0 + 9 * 8, 0);
                VirtualInterrupt = 9;
                Done = true;
            }
        }
    }
}
