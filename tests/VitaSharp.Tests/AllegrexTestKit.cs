using VitaSharp.Core.Compat.Allegrex;

namespace VitaSharp.Tests;

/// <summary>A flat RAM bus for the Allegrex tests: 1 MB at PSP physical 0x08000000 and 4 KB at 0x1FC00000.</summary>
public sealed class AllegrexTestBus : IAllegrexBus
{
    public const uint RamBase = 0x08000000;
    public const uint SramBase = 0x1FC00000;

    private readonly byte[] _ram = new byte[0x100000];
    private readonly byte[] _sram = new byte[0x1000];

    public int Writes { get; private set; }

    private Span<byte> At(uint address, int size)
    {
        address &= AllegrexCore.PhysicalMask;
        if (address >= RamBase && address + size <= RamBase + _ram.Length)
        {
            return _ram.AsSpan((int)(address - RamBase), size);
        }

        if (address >= SramBase && address + size <= SramBase + _sram.Length)
        {
            return _sram.AsSpan((int)(address - SramBase), size);
        }

        throw new InvalidOperationException($"test bus: no memory at 0x{address:X8}");
    }

    public byte Read8(uint address)
    {
        return At(address, 1)[0];
    }

    public ushort Read16(uint address)
    {
        Assert.Equal(0u, address & 1);
        return BitConverter.ToUInt16(At(address, 2));
    }

    public uint Read32(uint address)
    {
        Assert.Equal(0u, address & 3);
        return BitConverter.ToUInt32(At(address, 4));
    }

    public void Write8(uint address, byte value)
    {
        Writes++;
        At(address, 1)[0] = value;
    }

    public void Write16(uint address, ushort value)
    {
        Assert.Equal(0u, address & 1);
        Writes++;
        BitConverter.TryWriteBytes(At(address, 2), value);
    }

    public void Write32(uint address, uint value)
    {
        Assert.Equal(0u, address & 3);
        Writes++;
        BitConverter.TryWriteBytes(At(address, 4), value);
    }

    public void Load(uint address, ReadOnlySpan<uint> words)
    {
        for (int i = 0; i < words.Length; i++)
        {
            BitConverter.TryWriteBytes(At(address + (uint)(i * 4), 4), words[i]);
        }
    }
}

/// <summary>Encoders for the Allegrex instructions the tests use (our own encoding of the MIPS formats).</summary>
public static class AllegrexAsm
{
    public const int Zero = 0;
    public const int At = 1;
    public const int V0 = 2;
    public const int V1 = 3;
    public const int A0 = 4;
    public const int A1 = 5;
    public const int A2 = 6;
    public const int A3 = 7;
    public const int T0 = 8;
    public const int T1 = 9;
    public const int T2 = 10;
    public const int K0 = 26;
    public const int K1 = 27;
    public const int Sp = 29;
    public const int Ra = 31;

    public const uint Nop = 0;

    public static uint RType(int funct, int rd, int rs, int rt, int sa = 0)
    {
        return (uint)((rs << 21) | (rt << 16) | (rd << 11) | (sa << 6) | funct);
    }

    public static uint IType(int opcode, int rt, int rs, int immediate)
    {
        return (uint)((opcode << 26) | (rs << 21) | (rt << 16) | (immediate & 0xFFFF));
    }

    public static uint Special2(int funct, int rd, int rs, int rt, int sa = 0)
    {
        return (0x1Cu << 26) | RType(funct, rd, rs, rt, sa);
    }

    public static uint Special3(int funct, int rd, int rs, int rt, int sa)
    {
        return (0x1Fu << 26) | RType(funct, rd, rs, rt, sa);
    }

    public static uint Addiu(int rt, int rs, int immediate)
    {
        return IType(0x09, rt, rs, immediate);
    }

    public static uint Ori(int rt, int rs, int immediate)
    {
        return IType(0x0D, rt, rs, immediate);
    }

    public static uint Lui(int rt, int immediate)
    {
        return IType(0x0F, rt, 0, immediate);
    }

    public static uint Addu(int rd, int rs, int rt)
    {
        return RType(0x21, rd, rs, rt);
    }

    public static uint Break(int code = 0)
    {
        return (uint)((code << 6) | 0x0D);
    }

    public static uint Syscall(int code = 0)
    {
        return (uint)((code << 6) | 0x0C);
    }

    public static uint Beq(int rs, int rt, int offsetWords)
    {
        return IType(0x04, rt, rs, offsetWords);
    }

    public static uint Bne(int rs, int rt, int offsetWords)
    {
        return IType(0x05, rt, rs, offsetWords);
    }

    public static uint Lw(int rt, int baseRegister, int offset)
    {
        return IType(0x23, rt, baseRegister, offset);
    }

    public static uint Sw(int rt, int baseRegister, int offset)
    {
        return IType(0x2B, rt, baseRegister, offset);
    }

    public static uint Mtc0(int rt, int register)
    {
        return (0x10u << 26) | (4u << 21) | (uint)(rt << 16) | (uint)(register << 11);
    }

    public static uint Mfc0(int rt, int register)
    {
        return (0x10u << 26) | (uint)(rt << 16) | (uint)(register << 11);
    }

    public const uint Eret = 0x42000018;
    public const uint Halt = 0x70000000;
}

/// <summary>A core on a test bus with code at PSP 0x88000000 and EBase at 0x88001000.</summary>
public sealed class AllegrexRig
{
    public const uint CodeBase = 0x88000000;
    public const uint ExceptionBase = 0x88001000;

    public AllegrexRig(params uint[] code)
    {
        Bus = new AllegrexTestBus();
        Bus.Load(CodeBase, code);
        Core = new AllegrexCore(Bus);
        Core.Reset(CodeBase);
        Core.Status = 0;
        Core.EBase = ExceptionBase;
    }

    public AllegrexTestBus Bus { get; }

    public AllegrexCore Core { get; }

    public uint this[int register]
    {
        get => Core.Gpr[register];
        set => Core.Gpr[register] = value;
    }

    /// <summary>Runs <paramref name="instructions"/> instructions and checks they all ran.</summary>
    public void Step(int instructions = 1)
    {
        Assert.Equal(instructions, Core.Run(instructions));
    }

    /// <summary>Runs one instruction with rs/rt preset and returns rd's value (and that no exception was taken).</summary>
    public static uint Evaluate(uint instruction, uint rsValue, uint rtValue, uint rdBefore = 0xDEADBEEF)
    {
        AllegrexRig rig = new(instruction);
        int rs = (int)((instruction >> 21) & 31);
        int rt = (int)((instruction >> 16) & 31);
        int rd = (int)((instruction >> 11) & 31);
        rig[rd] = rdBefore;
        rig[rs] = rsValue;
        rig[rt] = rtValue;
        rig.Step();
        Assert.Equal(CodeBase + 4, rig.Core.Pc);
        return rig[rd];
    }
}
