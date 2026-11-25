using System.Buffers.Binary;
using BenchmarkDotNet.Attributes;
using VitaSharp.Core.Compat.Allegrex;

namespace VitaSharp.Benchmarks;

/// <summary>
/// The Allegrex interpreter's speed (the PSP core runs at 333 MHz): endless loops of integer, FPU, VFPU and
/// call/memory code, <see cref="Instructions"/> instructions per invocation, so the mean time per operation is the time
/// per instruction (MIPS = 1000 / ns). Synthetic code on a flat RAM bus.
/// </summary>
[MemoryDiagnoser]
public class AllegrexBenchmarks
{
    public const int Instructions = 1_000_000;

    private const uint Code = 0x88000000;

    private AllegrexCore _integer = null!;
    private AllegrexCore _fpu = null!;
    private AllegrexCore _vfpu = null!;
    private AllegrexCore _calls = null!;

    private sealed class FlatBus : IAllegrexBus
    {
        private readonly byte[] _ram = new byte[0x100000];

        private Span<byte> At(uint address, int size)
        {
            return _ram.AsSpan((int)((address & 0x1FFFFFFF) - 0x08000000), size);
        }

        public byte Read8(uint address)
        {
            return At(address, 1)[0];
        }

        public ushort Read16(uint address)
        {
            return BinaryPrimitives.ReadUInt16LittleEndian(At(address, 2));
        }

        public uint Read32(uint address)
        {
            return BinaryPrimitives.ReadUInt32LittleEndian(At(address, 4));
        }

        public void Write8(uint address, byte value)
        {
            At(address, 1)[0] = value;
        }

        public void Write16(uint address, ushort value)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(At(address, 2), value);
        }

        public void Write32(uint address, uint value)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(At(address, 4), value);
        }
    }

    private static uint R(int funct, int rd, int rs, int rt, int sa = 0)
    {
        return (uint)((rs << 21) | (rt << 16) | (rd << 11) | (sa << 6) | funct);
    }

    private static uint I(int opcode, int rt, int rs, int immediate)
    {
        return (uint)((opcode << 26) | (rs << 21) | (rt << 16) | (immediate & 0xFFFF));
    }

    private static AllegrexCore Make(uint[] program)
    {
        FlatBus bus = new();
        for (int i = 0; i < program.Length; i++)
        {
            bus.Write32(Code + (uint)(i * 4), program[i]);
        }

        AllegrexCore core = new(bus);
        core.Reset(Code);
        core.Status = 0;
        core.Gpr[29] = 0x88080000; // sp
        core.Gpr[4] = 0x88040000;  // a0: data
        return core;
    }

    [GlobalSetup]
    public void Setup()
    {
        // t0 += t1; t2 ^= t0; t3 = t2 << 3; t1 += 1; t4 = t1 < t3; bne zero, zero... (always) back with a slot
        _integer = Make(
        [
            R(0x21, 8, 8, 9),        // addu t0, t0, t1
            R(0x26, 10, 10, 8),      // xor t2, t2, t0
            R(0x00, 11, 0, 10, 3),   // sll t3, t2, 3
            I(0x09, 9, 9, 1),        // addiu t1, t1, 1
            R(0x2A, 12, 9, 11),      // slt t4, t1, t3
            R(0x24, 13, 12, 11),     // and t5, t4, t3
            I(0x04, 0, 0, -7),       // beq zero, zero, start
            R(0x25, 14, 13, 8),      // or t6, t5, t0 (slot)
        ]);

        // f2 = f0 + f1; f3 = f2 * f1; f0 = f3 - f2 ... (f1 = 1: the values stay finite and normal)
        uint addS = 0x46000000u | (1u << 16) | (0u << 11) | (2u << 6);
        uint mulS = 0x46000002u | (1u << 16) | (2u << 11) | (3u << 6);
        uint subS = 0x46000001u | (2u << 16) | (3u << 11) | (0u << 6);
        _fpu = Make([addS, mulS, subS, addS, I(0x04, 0, 0, -5), mulS]);
        _fpu.Fpr[0] = BitConverter.SingleToUInt32Bits(1.5f);
        _fpu.Fpr[1] = BitConverter.SingleToUInt32Bits(1.0f);

        // vadd.q C000, C010, C020; vmul.q C010, C000, C020; vdot.q S030, C000, C010
        uint vadd = 0x60000000u | (2u << 16) | (1u << 8) | 0u | 0x8080u;
        uint vmul = 0x64000000u | (2u << 16) | (0u << 8) | 1u | 0x8080u;
        uint vdot = 0x64800000u | (1u << 16) | (0u << 8) | 3u | 0x8080u;
        _vfpu = Make([vadd, vmul, vdot, vadd, I(0x04, 0, 0, -5), vmul]);
        for (int i = 0; i < 4; i++)
        {
            _vfpu.WriteVfpuRegister(0x02 | (i << 5), BitConverter.SingleToUInt32Bits(0.5f));
        }

        // a loop calling a function that loads, adds and stores, then returns
        _calls = Make(
        [
            (0x03u << 26) | (((Code + 0x40) >> 2) & 0x03FFFFFF), // jal function
            R(0x21, 8, 8, 9),                                    // slot: addu t0, t0, t1
            I(0x05, 0, 8, -3),                                   // bne t0, zero, start
            I(0x09, 9, 9, 1),                                    // slot: addiu t1, t1, 1
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            I(0x23, 10, 4, 0),                                   // 0x40: lw t2, 0(a0)
            R(0x21, 10, 10, 8),                                  // addu t2, t2, t0
            I(0x2B, 10, 4, 0),                                   // sw t2, 0(a0)
            R(0x08, 0, 31, 0),                                   // jr ra
            I(0x09, 4, 4, 0),                                    // slot: addiu a0, a0, 0
        ]);
        _calls.Gpr[8] = 1;
    }

    [Benchmark(OperationsPerInvoke = Instructions)]
    public int IntegerLoop()
    {
        return _integer.Run(Instructions);
    }

    [Benchmark(OperationsPerInvoke = Instructions)]
    public int FpuLoop()
    {
        return _fpu.Run(Instructions);
    }

    [Benchmark(OperationsPerInvoke = Instructions)]
    public int VfpuLoop()
    {
        return _vfpu.Run(Instructions);
    }

    [Benchmark(OperationsPerInvoke = Instructions)]
    public int CallsAndMemory()
    {
        return _calls.Run(Instructions);
    }
}
