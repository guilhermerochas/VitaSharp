using System.Runtime.CompilerServices;
using VitaSharp.Core.Hle;
using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// SceSysconForDriver over the emulated syscon chip (FW 3.74 syscon.skprx): the frame the module builds (command,
/// length, data, inverted checksum) and the reply it reads (status byte, length, result, data), the scratchpads, the
/// flags the module keeps from the chip's status word and status byte, the raw packet path other modules use
/// (ksceSysconCmdExec/Sync), the jig port's state and the chip in a save state. The module and the chip are reached by
/// name so the proof without them still builds.
/// </summary>
[Collection("guest")]
public sealed class KernelSysconChipTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int Invalid = unchecked((int)0x80250001), Null = unchecked((int)0x80250002);
    private const int NotQueued = unchecked((int)0x80250003), Secure = unchecked((int)0x80020004);

    private readonly HleHarness _harness = new(fixture, "kchip");

    public void Dispose() => _harness.Dispose();

    private HleModule Syscon => _harness.Emu.Hle.Modules.Single(module => module.GetType().Name == "SysconKernel");

    private object Chip => _harness.Emu.Vita.GetType().GetProperty("Syscon")!.GetValue(_harness.Emu.Vita)!;

    private object? ChipMember(string name)
    {
        object chip = Chip;
        return chip.GetType().GetField(name)?.GetValue(chip) ?? chip.GetType().GetProperty(name)?.GetValue(chip);
    }

    private void SetChip(string name, object value)
    {
        object chip = Chip;
        if (chip.GetType().GetField(name) is { } field)
        {
            field.SetValue(chip, value);
        }
        else
        {
            chip.GetType().GetProperty(name)!.SetValue(chip, value);
        }
    }

    private static string RepoRoot([CallerFilePath] string source = "")
    {
        for (DirectoryInfo? directory = new(Path.GetDirectoryName(source)!); directory != null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "VitaSharp.sln")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("the repository");
    }

    [Fact]
    public void EveryMissingSysconExportOfTheCensusIsBound()
    {
        string tsv = Path.Combine(RepoRoot(), "docs", "hle", "data", "kernel-exports.tsv");
        var bound = HleBindings.Modules.SelectMany(module => module.Bindings())
            .Where(binding => binding.Library == "SceSysconForDriver").Select(binding => binding.Nid).ToHashSet();
        var census = File.ReadLines(tsv).Select(line => line.Split('\t'))
            .Where(parts => parts.Length > 3 && parts[0] == "SceSysconForDriver")
            .Select(parts => Convert.ToUInt32(parts[1], 16)).ToList();
        Assert.Equal(311, census.Count);
        Assert.All(census, nid => Assert.Contains(nid, bound));
    }

    [Fact]
    public void TheScratchpadKeepsWhatIsWrittenInsideItsSize()
    {
        uint source = _harness.Alloc(0x20), target = _harness.Alloc(0x20);
        for (uint i = 0; i < 0x18; i++)
        {
            GuestMemory.Write(source + i, (byte)(0xA0 + i));
        }

        Assert.Equal(0, Call(Syscon, "ksceSysconSendCommand", 0xE8u, source, 0x18u));
        Assert.Equal(0, Call(Syscon, "ksceSysconReadCommand", 0xF0u, target, 0x10u));
        Assert.Equal(0xA8, GuestMemory.Read<byte>(target));
        Assert.Equal(0xB7, GuestMemory.Read<byte>(target + 0xF));

        // 1..0x18 bytes, inside 0x100 (the second scratchpad: 0x80)
        Assert.Equal(Invalid, Call(Syscon, "ksceSysconReadCommand", 0xF0u, target, 0x11u));
        Assert.Equal(Invalid, Call(Syscon, "ksceSysconReadCommand", 0u, target, 0x19u));
        Assert.Equal(Invalid, Call(Syscon, "ksceSysconReadCommand", 0u, target, 0u));
        Assert.Equal(Invalid, Call(Syscon, "NID_4E0EB57B", 0x70u, source, 0x11u));
        Assert.Equal(0, Call(Syscon, "NID_4E0EB57B", 0x70u, source, 0x10u));
        Assert.Equal(0, Call(Syscon, "NID_BF5CDB61", 0x78u, target, 8u));
        Assert.Equal(0xA8, GuestMemory.Read<byte>(target));

        // the two areas are apart
        Assert.Equal(0, Call(Syscon, "ksceSysconReadCommand", 0x70u, target, 4u));
        Assert.Equal(0u, GuestMemory.R32(target));
    }

    [Fact]
    public void ARawPacketGoesThroughTheChipWithItsChecksum()
    {
        uint packet = _harness.Alloc(0x80), source = _harness.Alloc(4);
        GuestMemory.W32(source, 0x44332211);
        Assert.Equal(0, Call(Syscon, "ksceSysconSendCommand", 0x10u, source, 4u));

        // the scratchpad read {0x10, 0, 4}, the checksum left to the module
        ReadOnlySpan<byte> frame = [0x90, 0x00, 0x04, 0x10, 0x00, 0x04];
        frame.CopyTo(GuestMemory.Span(packet + 0x10, frame.Length));
        Assert.Equal(0, Call(Syscon, "ksceSysconCmdExec", packet, 0u));

        byte expectedChecksum = unchecked((byte)~(0x90 + 0x04 + 0x10 + 0x04));
        Assert.Equal(expectedChecksum, GuestMemory.Read<byte>(packet + 0x16));
        Assert.Equal(6, GuestMemory.Read<byte>(packet + 0x32)); // 4 data bytes + result + checksum
        Assert.Equal(0, GuestMemory.Read<byte>(packet + 0x33));
        Assert.Equal(0x44332211u, GuestMemory.R32(packet + 0x34));
        Assert.Equal(0, Call(Syscon, "ksceSysconCmdSync", packet, 1));
        Assert.Equal(NotQueued, Call(Syscon, "NID_C9DB5BFF", packet));

        // a frame longer than the packet's 0x1C data bytes
        GuestMemory.Write(packet + 0x12, (byte)0x1E);
        Assert.Equal(unchecked((int)0x80250006), Call(Syscon, "ksceSysconCmdExec", packet, 0u));
    }

    [Fact]
    public void TheFlagsFollowTheChipsStatusWordAndByte()
    {
        Assert.Equal(0, Call(Syscon, "NID_27758A64")); // low battery
        Assert.Equal(0, Call(Syscon, "NID_9DA2A5AB")); // AC adapter: an unplugged unit
        Assert.Equal(0, Call(Syscon, "ksceSysconIsLowBatteryInhibitUpdateDownload"));

        SetChip("StatusWord", (1u << 17) | (1u << 29) | (1u << 0));
        SetChip("AcAdapter", true);
        Assert.Equal(1, Call(Syscon, "NID_27758A64"));
        Assert.Equal(1, Call(Syscon, "ksceSysconIsLowBatteryInhibitUpdateDownload"));
        Assert.Equal(0, Call(Syscon, "ksceSysconIsLowBatteryInhibitUpdateReboot"));
        Assert.Equal(1, Call(Syscon, "NID_C0F215B7"));
        Assert.Equal(1, Call(Syscon, "NID_9DA2A5AB"));
        Assert.Equal(0x08, Call(Syscon, "NID_EBE3262C"));
        Assert.Equal((1u << 17) | (1u << 29) | 1u, Convert.ToUInt32(Invoke(Syscon, "NID_B7BCC638")));
        Assert.Equal(0, Call(Syscon, "ksceSysconIsDownLoaderMode"));
    }

    [Fact]
    public void ASetCommandIsKeptOnTheChipAndItsReadPairAnswersIt()
    {
        Assert.Equal(0, Call(Syscon, "ksceSysconCtrlLED", 2u, 1));
        var registers = (Dictionary<ushort, uint>)ChipMember("Registers")!;
        Assert.Equal(0x82u, registers[0x891]);
        Assert.Equal(0, Call(Syscon, "ksceSysconCtrlLED", 2u, 0));
        Assert.Equal(0x02u, registers[0x891]);

        uint value = _harness.Alloc(4);
        registers[0x480] = 0x1234;
        Assert.Equal(0, Call(Syscon, "NID_D01E64FC", value));
        Assert.Equal(0x1234u, GuestMemory.R32(value));
        Assert.Equal(Null, Call(Syscon, "NID_D01E64FC", 0u));
    }

    [Fact]
    public void TheMasksAndTheMultiConnectorAreTheModulesOwn()
    {
        Assert.Equal(3u, Convert.ToUInt32(Invoke(Syscon, "NID_2E4A2198")));
        Assert.Equal(7u, Convert.ToUInt32(Invoke(Syscon, "NID_1E08A40F")));
        Assert.Equal(0, Call(Syscon, "NID_3FDD29D6", 4u, 1));
        Assert.Equal(7u, Convert.ToUInt32(Invoke(Syscon, "NID_2E4A2198")));
        Assert.Equal(0, Call(Syscon, "NID_79E6DD8B", 2u, 0));
        Assert.Equal(5u, Convert.ToUInt32(Invoke(Syscon, "NID_1E08A40F")));

        uint info = _harness.Alloc(4);
        Assert.Equal(0, Call(Syscon, "ksceSysconGetMultiCnInfo", info));
        Assert.Equal(0x4FF00u, GuestMemory.R32(info));
        Assert.Equal(0, Call(Syscon, "ksceSysconGetMicroUsbInfo", info));
        Assert.Equal(0u, GuestMemory.R32(info));
    }

    [Fact]
    public void TheJigPortOpensOnceAndClosesOnce()
    {
        uint buffer = _harness.Alloc(0x40);
        Assert.Equal(Invalid, Call(Syscon, "NID_3C80B529", 0u, 4u, buffer));
        Assert.Equal(Invalid, Call(Syscon, "ksceSysconJigClosePort"));
        Assert.Equal(0, Call(Syscon, "ksceSysconJigOpenPort"));
        Assert.Equal(Invalid, Call(Syscon, "ksceSysconJigOpenPort"));
        Assert.Equal(0, Call(Syscon, "NID_3C80B529", 0u, 0x20u, buffer));
        Assert.Equal(Null, Call(Syscon, "NID_3C80B529", 0u, 4u, 0u));
        Assert.Equal(0, Call(Syscon, "ksceSysconJigClosePort"));
        Assert.Equal(Invalid, Call(Syscon, "ksceSysconJigSetConfig", 1u, 2u));
    }

    [Fact]
    public void TheModulesOwnAnswersAndChecks()
    {
        uint sum = _harness.Alloc(4), data = _harness.Alloc(4);
        GuestMemory.W32(data, 0x04030201);
        Assert.Equal(0, Call(Syscon, "ksceSysconUpdaterCalcChecksum", data, 4u, sum));
        Assert.Equal(~10u, GuestMemory.R32(sum));
        Assert.Equal(Invalid, Call(Syscon, "ksceSysconUpdaterCalcChecksum", data, 0u, sum));

        Assert.Equal(Invalid, Call(Syscon, "ksceSysconUpdaterSetRunMode", 0x1234u));
        Assert.Equal(0, Call(Syscon, "ksceSysconUpdaterSetRunMode", 0x72BAu));
        Assert.Equal(Invalid, Call(Syscon, "ksceSysconCtrlDolceLED", 3u));
        Assert.Equal(Invalid, Call(Syscon, "NID_6F586D1A", 0x41u, 1u));

        // the syscon authentication needs the secure side's keys: refused after the argument checks
        Assert.Equal(Invalid, Call(Syscon, "NID_4D03754A", data, 0x10u, data, 0x28u));
        Assert.Equal(Secure, Call(Syscon, "NID_4D03754A", data, 0x28u, data, 0x28u));

        // the versions: the newest syscon family the 3.74 modules test, the boot parameters' hardware info
        Assert.True(Convert.ToUInt32(Invoke(Syscon, "ksceSysconGetBaryonVersion")) >= 0x1040105u);
        Assert.Equal(NotQueued, Call(Syscon, "ksceSysconGetHardwareInfo2", data));
    }

    [Fact]
    public void TheChipIsInTheSaveState()
    {
        uint source = _harness.Alloc(4);
        GuestMemory.W32(source, 0xCAFEBABE);
        Assert.Equal(0, Call(Syscon, "ksceSysconSendCommand", 0x40u, source, 4u));
        SetChip("StatusWord", 1u << 23);
        var writer = new SnapshotWriter();
        writer.Write((ISnapshotState)Chip);

        SetChip("StatusWord", 0u);
        ((byte[])ChipMember("Scratchpad")!).AsSpan().Clear();
        Assert.True(new SnapshotReader(writer.Data.ToArray()).ReadInto((ISnapshotState)Chip));
        Assert.Equal(1u << 23, (uint)ChipMember("StatusWord")!);
        Assert.Equal(0xBE, ((byte[])ChipMember("Scratchpad")!)[0x40]);
        Assert.Equal(1, Call(Syscon, "NID_50CAE242"));
    }
}
