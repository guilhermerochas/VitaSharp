using static VitaSharp.Core.Cpu.Arm;

namespace VitaSharp.Core.Cpu;

internal static unsafe partial class Neon
{
    /// <summary>Element / structure loads and stores: 1111 0100 A D L 0 Rn Vd type size align Rm</summary>
    public static void LoadStore(CpuState* s, uint ins, uint pc)
    {
        bool all = Bit(ins, 23) == 0;
        bool load = Bit(ins, 21) != 0;
        uint rn = Bits(ins, 19, 16), rm = ins & 0xF;
        uint d = Rd(ins);
        uint addr = s->R[rn];
        uint transferred;

        if (all) transferred = MultipleStructures(s, ins, load, d, addr);
        else if (Bits(ins, 11, 10) == 3) transferred = AllLanes(s, ins, d, addr);
        else transferred = SingleLane(s, ins, load, d, addr);

        if (rm == 15) return;
        s->R[rn] = rm == 13 ? addr + transferred : addr + s->R[rm];
    }

    static uint MultipleStructures(CpuState* s, uint ins, bool load, uint d, uint addr)
    {
        uint type = Bits(ins, 11, 8);
        int esize = 8 << (int)Bits(ins, 7, 6);
        int ebytes = esize / 8;
        int elements = 8 / ebytes;
        int nStruct, regs, inc;
        switch (type)
        {
            case 0x7: nStruct = 1; regs = 1; inc = 1; break;
            case 0xA: nStruct = 1; regs = 2; inc = 1; break;
            case 0x6: nStruct = 1; regs = 3; inc = 1; break;
            case 0x2: nStruct = 1; regs = 4; inc = 1; break;
            case 0x8: nStruct = 2; regs = 1; inc = 1; break;
            case 0x9: nStruct = 2; regs = 1; inc = 2; break;
            case 0x3: nStruct = 2; regs = 2; inc = 2; break;
            case 0x4: nStruct = 3; regs = 1; inc = 1; break;
            case 0x5: nStruct = 3; regs = 1; inc = 2; break;
            case 0x0: nStruct = 4; regs = 1; inc = 1; break;
            case 0x1: nStruct = 4; regs = 1; inc = 2; break;
            default: Undefined(s, 0); return 0;
        }

        uint a = addr;
        if (nStruct == 1)
        {
            // VLD1/VST1: plain contiguous copy of `regs` D registers
            for (int r = 0; r < regs; r++)
            {
                ulong* reg = &s->D[(d + (uint)r) & 31];
                if (load) *reg = Mem.R64(s, a); else Mem.W64(s, a, *reg);
                a += 8;
            }
            return (uint)(8 * regs);
        }

        if (nStruct == 2 && regs == 2)
        {
            // two interleaved pairs: (d, d+2) then (d+1, d+3)
            for (int r = 0; r < 2; r++)
                a = Interleave(s, load, d + (uint)r, 2, 2, esize, elements, a);
            return a - addr;
        }

        a = Interleave(s, load, d, nStruct, inc, esize, elements, a);
        return a - addr;
    }

    static uint Interleave(CpuState* s, bool load, uint d, int nStruct, int inc, int esize, int elements, uint a)
    {
        int ebytes = esize / 8;
        for (int e = 0; e < elements; e++)
        {
            for (int k = 0; k < nStruct; k++)
            {
                byte* reg = (byte*)&s->D[(d + (uint)(k * inc)) & 31];
                if (load) Set(reg, e, esize, ReadElem(s, a, esize));
                else WriteElem(s, a, esize, GetU(reg, e, esize));
                a += (uint)ebytes;
            }
        }
        return a;
    }

    static ulong ReadElem(CpuState* s, uint a, int esize) => esize switch
    {
        8 => Mem.R8(s, a),
        16 => Mem.R16(s, a),
        32 => Mem.R32(s, a),
        _ => Mem.R64(s, a),
    };

    static void WriteElem(CpuState* s, uint a, int esize, ulong v)
    {
        switch (esize)
        {
            case 8: Mem.W8(s, a, (uint)v); break;
            case 16: Mem.W16(s, a, (uint)v); break;
            case 32: Mem.W32(s, a, (uint)v); break;
            default: Mem.W64(s, a, v); break;
        }
    }

    static uint SingleLane(CpuState* s, uint ins, bool load, uint d, uint addr)
    {
        int nStruct = (int)Bits(ins, 9, 8) + 1;
        uint size = Bits(ins, 11, 10);
        uint ia = Bits(ins, 7, 4);
        int esize = 8 << (int)size;
        int index, inc;
        switch (size)
        {
            case 0: index = (int)(ia >> 1); inc = 1; break;
            case 1: index = (int)(ia >> 2); inc = (ia & 2) != 0 ? 2 : 1; break;
            default: index = (int)(ia >> 3); inc = (ia & 4) != 0 ? 2 : 1; break;
        }
        if (nStruct == 1) inc = 1;
        uint a = addr;
        for (int k = 0; k < nStruct; k++)
        {
            byte* reg = (byte*)&s->D[(d + (uint)(k * inc)) & 31];
            if (load) Set(reg, index, esize, ReadElem(s, a, esize));
            else WriteElem(s, a, esize, GetU(reg, index, esize));
            a += (uint)(esize / 8);
        }
        return a - addr;
    }

    static uint AllLanes(CpuState* s, uint ins, uint d, uint addr)
    {
        int nStruct = (int)Bits(ins, 9, 8) + 1;
        uint size = Bits(ins, 7, 6);
        int esize = size == 3 ? 32 : 8 << (int)size; // VLD4 with size=11 means 32-bit, 128-bit aligned
        bool t = Bit(ins, 5) != 0;
        int count = 64 / esize;
        uint a = addr;
        if (nStruct == 1)
        {
            ulong v = ReadElem(s, a, esize);
            ulong rep = esize switch { 8 => v * 0x0101010101010101ul, 16 => v * 0x0001000100010001ul, _ => v | (v << 32) };
            s->D[d & 31] = rep;
            if (t) s->D[(d + 1) & 31] = rep;
            return (uint)(esize / 8);
        }
        int inc = t ? 2 : 1;
        for (int k = 0; k < nStruct; k++)
        {
            ulong v = ReadElem(s, a, esize);
            byte* reg = (byte*)&s->D[(d + (uint)(k * inc)) & 31];
            for (int e = 0; e < count; e++) Set(reg, e, esize, v);
            a += (uint)(esize / 8);
        }
        return a - addr;
    }
}
