using System.Buffers.Binary;
using System.Numerics;

namespace VitaSharp.Core.Compat.Allegrex;

/// <summary>
/// The Allegrex additions to the MIPS integer set: ext/ins and the bit operations of SPECIAL3, clz/clo, max/min and
/// movz/movn of SPECIAL.
/// </summary>
public sealed partial class AllegrexCore
{
    void ExecuteSpecial3(uint instruction)
    {
        switch (instruction & 0x3F)
        {
            case 0x00:
                Extract(instruction);
                break;
            case 0x04:
                Insert(instruction);
                break;
            case 0x20:
                BitOperation(instruction);
                break;
            default:
                RaiseException(AllegrexExceptionCode.ReservedInstruction);
                break;
        }
    }

    /// <summary>ext rt, rs, pos, size: rt = the size bits of rs from pos (size - 1 in the rd field).</summary>
    void Extract(uint instruction)
    {
        int position = Sa(instruction);
        int sizeMinusOne = Rd(instruction);
        uint mask = 0xFFFFFFFFu >> (31 - sizeMinusOne);
        _gpr[Rt(instruction)] = (_gpr[Rs(instruction)] >> position) & mask;
    }

    /// <summary>
    /// ins rt, rs, pos, size: the low bits of rs replace bits pos..msb of rt (msb in the rd field). The mask is built
    /// from msb and moved down by pos, so msb &lt; pos (unpredictable in MIPS32) is consistent: nothing above msb changes.
    /// </summary>
    void Insert(uint instruction)
    {
        int position = Sa(instruction);
        int mostSignificantBit = Rd(instruction);
        uint sourceMask = (0xFFFFFFFFu >> (31 - mostSignificantBit)) >> position;
        uint destinationMask = sourceMask << position;
        int rt = Rt(instruction);
        _gpr[rt] = (_gpr[rt] & ~destinationMask) | ((_gpr[Rs(instruction)] & sourceMask) << position);
    }

    /// <summary>
    /// SPECIAL3 function 0x20: the Allegrex decodes only some bits of the sa field (PPSSPP, pspautotests
    /// cpu_alu/allegrex_encodings): bit 4 clear = wsbh/wsbw by bit 0; else bit 2 = bitrev, else bit 3 = seh, else seb.
    /// </summary>
    void BitOperation(uint instruction)
    {
        int operation = Sa(instruction);
        uint value = _gpr[Rt(instruction)];
        uint result;
        if ((operation & 0x10) == 0)
        {
            if ((operation & 1) == 0)
            {
                result = ((value & 0xFF00FF00) >> 8) | ((value & 0x00FF00FF) << 8);
            }
            else
            {
                result = BinaryPrimitives.ReverseEndianness(value);
            }
        }
        else if ((operation & 4) != 0)
        {
            result = ReverseBits(value);
        }
        else if ((operation & 8) != 0)
        {
            result = (uint)(int)(short)(ushort)value;
        }
        else
        {
            result = (uint)(int)(sbyte)(byte)value;
        }

        _gpr[Rd(instruction)] = result;
    }

    static uint ReverseBits(uint value)
    {
        value = ((value >> 1) & 0x55555555) | ((value & 0x55555555) << 1);
        value = ((value >> 2) & 0x33333333) | ((value & 0x33333333) << 2);
        value = ((value >> 4) & 0x0F0F0F0F) | ((value & 0x0F0F0F0F) << 4);
        return BinaryPrimitives.ReverseEndianness(value);
    }

    void CountLeadingZeros(uint instruction)
    {
        _gpr[Rd(instruction)] = (uint)BitOperations.LeadingZeroCount(_gpr[Rs(instruction)]);
    }

    void CountLeadingOnes(uint instruction)
    {
        _gpr[Rd(instruction)] = (uint)BitOperations.LeadingZeroCount(~_gpr[Rs(instruction)]);
    }

    void Maximum(uint instruction)
    {
        _gpr[Rd(instruction)] = (uint)Math.Max((int)_gpr[Rs(instruction)], (int)_gpr[Rt(instruction)]);
    }

    void Minimum(uint instruction)
    {
        _gpr[Rd(instruction)] = (uint)Math.Min((int)_gpr[Rs(instruction)], (int)_gpr[Rt(instruction)]);
    }

    void MoveIfZero(uint instruction)
    {
        if (_gpr[Rt(instruction)] == 0)
        {
            _gpr[Rd(instruction)] = _gpr[Rs(instruction)];
        }
    }

    void MoveIfNotZero(uint instruction)
    {
        if (_gpr[Rt(instruction)] != 0)
        {
            _gpr[Rd(instruction)] = _gpr[Rs(instruction)];
        }
    }
}
