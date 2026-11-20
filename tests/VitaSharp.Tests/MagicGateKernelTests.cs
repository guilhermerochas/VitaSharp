using System.Buffers.Binary;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// SceMagicGateForDriver as magicgate.skprx 3.74 answers on a unit with no MagicGate medium: the lock and the start
/// and stop bookkeeping succeed, no session starts and no slot holds a key, the ciphers and authentication steps keep
/// their size checks and then answer "not supported", and the module's constant data comes only from the installed
/// firmware.
/// </summary>
[Collection("guest")]
public sealed class MagicGateKernelTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int Unsupported = unchecked((int)0x80020004), Size = unchecked((int)0x80501002);
    private const int Slot = unchecked((int)0x80501006), NotStarted = unchecked((int)0x80505005);

    private readonly HleHarness _harness = new(fixture, "mgk");

    public void Dispose() => _harness.Dispose();

    private SystemServicesVsh Services => _harness.Module<SystemServicesVsh>();

    [Theory]
    [InlineData(0xA49B9D70u)] [InlineData(0x43518F19u)] [InlineData(0x492EE950u)] [InlineData(0x38AE3ACAu)]
    [InlineData(0x46A28FAAu)] [InlineData(0x6D3B42E6u)] [InlineData(0xFEF11D84u)] [InlineData(0x144BFA76u)]
    [InlineData(0x12CE769Du)] [InlineData(0xE0888D78u)] [InlineData(0x4F4B2DC9u)] [InlineData(0xB0827B4Fu)]
    [InlineData(0x593016E1u)] [InlineData(0x6A240950u)] [InlineData(0x9CEF4965u)] [InlineData(0xA495FA95u)]
    [InlineData(0x0770CBEFu)] [InlineData(0xCFD943E0u)] [InlineData(0x42A9D74Eu)] [InlineData(0x7A233CBCu)]
    [InlineData(0x465A7B76u)] [InlineData(0xB20497C7u)] [InlineData(0x2D5069DEu)] [InlineData(0xCA74A13Du)]
    [InlineData(0x0B01FE3Fu)] [InlineData(0x01FD0260u)] [InlineData(0x07BDCE2Fu)] [InlineData(0x4E8B4EB5u)]
    [InlineData(0xC9658A76u)] [InlineData(0x05912BF5u)] [InlineData(0xACA0F529u)] [InlineData(0x2B026FFFu)]
    [InlineData(0xF2502A3Bu)] [InlineData(0x3270BEE6u)] [InlineData(0xA855C045u)] [InlineData(0x1BCE34ADu)]
    [InlineData(0x75B445DAu)] [InlineData(0x3EDD73EBu)] [InlineData(0x8E87BA88u)] [InlineData(0x7B052A10u)]
    [InlineData(0x30DF2E82u)] [InlineData(0xE62B8A67u)] [InlineData(0x8E2E577Eu)] [InlineData(0xD819D5C6u)]
    [InlineData(0x6A4D4F91u)] [InlineData(0x0201E54Bu)] [InlineData(0x758D7158u)] [InlineData(0x0EBA5958u)]
    public void Exports_are_served(uint nid)
    {
        Assert.NotNull(_harness.Emu.Hle.ImplOf(nid));
    }

    [Fact]
    public void No_session_ever_starts()
    {
        Assert.Equal(0, Call(Services, "MagicGateLock"));
        Assert.Equal(0, Call(Services, "MagicGateBookkeeping"));
        Assert.Equal(0, Call(Services, "MagicGateResetReader", 0u, 0xFFFFFFFFu));
        Assert.Equal(unchecked((int)0x80501000), Call(Services, "MagicGateResetReader", 0u, 5u));
        Assert.Equal(Unsupported, Call(Services, "MagicGateStart"));
        Assert.Equal(NotStarted, Call(Services, "MagicGateNotStarted"));
        Assert.Equal(Slot, Call(Services, "MagicGateSlotReady", 3));
    }

    [Fact]
    public void Ciphers_and_messages_check_their_sizes_then_refuse()
    {
        uint buffer = _harness.Alloc(0x40);
        Assert.Equal(Size, Call(Services, "MagicGateCipher", buffer, buffer, 0u, 0u));
        Assert.Equal(Size, Call(Services, "MagicGateCipher", buffer, buffer, 0u, 0x11u));
        Assert.Equal(Unsupported, Call(Services, "MagicGateCipher", buffer, buffer, 0u, 0x10u));
        Assert.Equal(Size, Call(Services, "MagicGateMessage", 0u, buffer, 0x2Bu));
        Assert.Equal(Unsupported, Call(Services, "MagicGateMessage", 0u, buffer, 0x2Cu));
        Assert.Equal(Unsupported, Call(Services, "MagicGateSecure"));
    }

    [Fact]
    public void Constant_data_comes_only_from_the_installed_firmware()
    {
        // no firmware in the test data: the identifier and the tables are not known; a size query still answers
        Assert.Equal(Unsupported, Call(Services, "MagicGateIdentifier", _harness.Alloc(0x10)));
        Assert.Equal(0x8C, Call(Services, "MagicGateTableA", 0u, 0u));
        Assert.Equal(Unsupported, Call(Services, "MagicGateTableA", _harness.Alloc(0x8C), 0x8Cu));
        Assert.Equal(0x104, Call(Services, "MagicGateTableB", 0u, 0u));
        byte[] elf = new byte[0x200 + 0x7000];
        BinaryPrimitives.WriteUInt32LittleEndian(elf.AsSpan(0x1C), 0x40);
        BinaryPrimitives.WriteUInt32LittleEndian(elf.AsSpan(0x40 + 4), 0x200);
        BinaryPrimitives.WriteUInt32LittleEndian(elf.AsSpan(0x40 + 0x10), 0x7000);
        elf[0x200 + 0x69D0] = 0x5A;
        Assert.Equal(0x5A, SystemServicesVsh.FirmwareSegmentBytes(elf, 0xD140CEAC, 0xD140CEAC, 0x69D0, 0x10)![0]);
        Assert.Null(SystemServicesVsh.FirmwareSegmentBytes(elf, 0x1, 0xD140CEAC, 0x69D0, 0x10));
        Assert.Null(SystemServicesVsh.FirmwareSegmentBytes(elf, 0xD140CEAC, 0xD140CEAC, 0x6FF8, 0x10));
    }
}
