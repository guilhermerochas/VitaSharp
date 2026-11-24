namespace VitaSharp.Core.Compat.Allegrex;

/// <summary>
/// The VFPU (COP2): 128 single-precision registers seen as eight 4x4 matrices, addressed as single lanes (S),
/// columns (C) and rows (R) of 2, 3 or 4 lanes, and matrices (M) or transposed matrices (E); the 16 control registers
/// (the S, T and D prefixes, the condition codes, the random generator state); the prefixes that swizzle, take the
/// absolute value, negate or replace with constants the source lanes, and saturate or mask the destination lanes of
/// the next VFPU instruction. Register file layout: matrix * 16 + column * 4 + row, where a register number has the
/// matrix in bits 2-4, the column in bits 0-1 and the row (or for vectors the start lane and the row/column choice) in
/// bits 5-6. Behaviour from PPSSPP's hardware-checked interpreter and notes (pspautotests cpu/vfpu), studied only.
/// </summary>
public sealed partial class AllegrexCore
{
    public const int VfpuSPrefix = 0;
    public const int VfpuTPrefix = 1;
    public const int VfpuDPrefix = 2;
    public const int VfpuConditionCodes = 3;
    public const int VfpuInf4 = 4;
    public const int VfpuRevision = 7;
    public const int VfpuRandom0 = 8;
    public const int VfpuControlCount = 16;

    /// <summary>The prefix that leaves the S and T lanes as they are (x, y, z, w).</summary>
    public const uint VfpuIdentityPrefix = 0xE4;

    private const uint VfpuRevisionValue = 0x7772CEAB;

    // the prefix constants, by swizzle + 4 * abs: 0, 1, 2, 1/2, 3, 1/3, 1/4, 1/6
    private static readonly uint[] s_prefixConstants =
    [
        0x00000000, 0x3F800000, 0x40000000, 0x3F000000, 0x40400000, 0x3EAAAAAB, 0x3E800000, 0x3E2AAAAB,
    ];

    private readonly uint[] _vfpr = new uint[128];
    private readonly uint[] _vfpuControl = new uint[VfpuControlCount];

    // scratch lanes for the instructions (no allocation per instruction)
    private readonly uint[] _laneS = new uint[16];
    private readonly uint[] _laneT = new uint[16];
    private readonly uint[] _laneD = new uint[16];

    /// <summary>Reads VFPU register <paramref name="register"/> (S-notation number 0..127) as bits.</summary>
    public uint ReadVfpuRegister(int register)
    {
        return _vfpr[VfpuIndex(register)];
    }

    public void WriteVfpuRegister(int register, uint bits)
    {
        _vfpr[VfpuIndex(register)] = bits;
    }

    /// <summary>Reads a VFPU control register (0 S prefix, 1 T, 2 D, 3 CC, 4 INF4, 7 REV, 8..15 the random state).</summary>
    public uint ReadVfpuControl(int register)
    {
        return _vfpuControl[register & 15];
    }

    /// <summary>Sets a VFPU control register as is (no write mask), for tests and save states.</summary>
    public void WriteVfpuControl(int register, uint value)
    {
        _vfpuControl[register & 15] = value;
    }

    private void ResetVfpu()
    {
        Array.Clear(_vfpuControl);
        _vfpuControl[VfpuSPrefix] = VfpuIdentityPrefix;
        _vfpuControl[VfpuTPrefix] = VfpuIdentityPrefix;
        _vfpuControl[VfpuConditionCodes] = 0x3F;
        _vfpuControl[VfpuRevision] = VfpuRevisionValue;
        _vfpuControl[VfpuRandom0] = 0x3F800001;
        _vfpuControl[VfpuRandom0 + 1] = 0x3F800002;
        _vfpuControl[VfpuRandom0 + 2] = 0x3F800004;
        _vfpuControl[VfpuRandom0 + 3] = 0x3F800008;
        for (int i = 4; i < 8; i++)
        {
            _vfpuControl[VfpuRandom0 + i] = 0x3F800000;
        }
    }

    private static int VfpuIndex(int register)
    {
        return (((register >> 2) & 7) * 16) + ((register & 3) * 4) + ((register >> 5) & 3);
    }

    private static int VfpuD(uint instruction)
    {
        return (int)(instruction & 0x7F);
    }

    private static int VfpuS(uint instruction)
    {
        return (int)((instruction >> 8) & 0x7F);
    }

    private static int VfpuT(uint instruction)
    {
        return (int)((instruction >> 16) & 0x7F);
    }

    /// <summary>The vector size (1..4 lanes) of bits 7 and 15.</summary>
    private static int VfpuSize(uint instruction)
    {
        return (int)(((instruction >> 7) & 1) + ((instruction >> 14) & 2) + 1);
    }

    private static int VectorStartLane(int size, int register)
    {
        return size == 3 ? (register >> 6) & 1 : (register >> 5) & 2;
    }

    /// <summary>Reads <paramref name="size"/> lanes of a vector register (a column, or a row when bit 5 is set).</summary>
    private void ReadVector(Span<uint> destination, int size, int register)
    {
        if (size == 1)
        {
            destination[0] = _vfpr[VfpuIndex(register)];
            return;
        }

        int start = VectorStartLane(size, register);
        int matrixBase = ((register >> 2) & 7) * 16;
        int column = register & 3;
        bool transposed = ((register >> 5) & 1) != 0;
        for (int i = 0; i < size; i++)
        {
            int lane = (start + i) & 3;
            destination[i] = transposed ? _vfpr[matrixBase + column + (lane * 4)] : _vfpr[matrixBase + (column * 4) + lane];
        }
    }

    /// <summary>Writes a vector, skipping the lanes the D prefix masks (bits 8..11) unless <paramref name="ignoreMask"/>.</summary>
    private void WriteVector(ReadOnlySpan<uint> source, int size, int register, bool ignoreMask = false)
    {
        uint mask = ignoreMask ? 0 : (_vfpuControl[VfpuDPrefix] >> 8) & 0xF;
        if (size == 1)
        {
            if ((mask & 1) == 0)
            {
                _vfpr[VfpuIndex(register)] = source[0];
            }

            return;
        }

        int start = VectorStartLane(size, register);
        int matrixBase = ((register >> 2) & 7) * 16;
        int column = register & 3;
        bool transposed = ((register >> 5) & 1) != 0;
        for (int i = 0; i < size; i++)
        {
            if (((mask >> i) & 1) != 0)
            {
                continue;
            }

            int lane = (start + i) & 3;
            if (transposed)
            {
                _vfpr[matrixBase + column + (lane * 4)] = source[i];
            }
            else
            {
                _vfpr[matrixBase + (column * 4) + lane] = source[i];
            }
        }
    }

    private static void MatrixOrigin(int side, int register, out int row, out bool transposed)
    {
        transposed = ((register >> 5) & 1) != 0;
        switch (side)
        {
            case 1:
                transposed = false;
                row = (register >> 5) & 3;
                break;
            case 3:
                row = (register >> 6) & 1;
                break;
            default:
                row = (register >> 5) & 2;
                break;
        }
    }

    /// <summary>Reads a side x side matrix: destination[j * 4 + i] is lane i of its vector j (wrapping inside the 4x4 block).</summary>
    private void ReadMatrix(Span<uint> destination, int side, int register)
    {
        MatrixOrigin(side, register, out int row, out bool transposed);
        int matrixBase = ((register >> 2) & 7) * 16;
        int column = register & 3;
        for (int j = 0; j < side; j++)
        {
            for (int i = 0; i < side; i++)
            {
                int index = transposed ? (((row + i) & 3) * 4) + ((column + j) & 3) : (((column + j) & 3) * 4) + ((row + i) & 3);
                destination[(j * 4) + i] = _vfpr[matrixBase + index];
            }
        }
    }

    /// <summary>Writes a matrix; the D prefix mask applies to its last vector only.</summary>
    private void WriteMatrix(ReadOnlySpan<uint> source, int side, int register)
    {
        MatrixOrigin(side, register, out int row, out bool transposed);
        int matrixBase = ((register >> 2) & 7) * 16;
        int column = register & 3;
        uint mask = (_vfpuControl[VfpuDPrefix] >> 8) & 0xF;
        for (int j = 0; j < side; j++)
        {
            for (int i = 0; i < side; i++)
            {
                if (j == side - 1 && ((mask >> i) & 1) != 0)
                {
                    continue;
                }

                int index = transposed ? (((row + i) & 3) * 4) + ((column + j) & 3) : (((column + j) & 3) * 4) + ((row + i) & 3);
                _vfpr[matrixBase + index] = source[(j * 4) + i];
            }
        }
    }

    /// <summary>
    /// Applies an S or T prefix to <paramref name="count"/> lanes: each lane takes lane swizzle(i) of the original
    /// (lanes past the vector read <paramref name="invalid"/>), optionally its absolute value, or a constant, then the
    /// negate bit flips its sign.
    /// </summary>
    private static void ApplyPrefixST(Span<uint> lanes, uint prefix, int count, uint invalid = 0)
    {
        if (prefix == VfpuIdentityPrefix)
        {
            return;
        }

        Span<uint> original = stackalloc uint[4];
        for (int i = 0; i < 4; i++)
        {
            original[i] = i < count ? lanes[i] : invalid;
        }

        for (int i = 0; i < count; i++)
        {
            int swizzle = (int)((prefix >> (i * 2)) & 3);
            bool absolute = ((prefix >> (8 + i)) & 1) != 0;
            bool constant = ((prefix >> (12 + i)) & 1) != 0;
            bool negate = ((prefix >> (16 + i)) & 1) != 0;
            uint value;
            if (constant)
            {
                value = s_prefixConstants[swizzle + (absolute ? 4 : 0)];
            }
            else
            {
                value = original[swizzle];
                if (absolute)
                {
                    value &= 0x7FFFFFFF;
                }
            }

            lanes[i] = negate ? value ^ 0x80000000 : value;
        }
    }

    private void ApplyPrefixS(Span<uint> lanes, int count, uint invalid = 0)
    {
        ApplyPrefixST(lanes, _vfpuControl[VfpuSPrefix], count, invalid);
    }

    private void ApplyPrefixT(Span<uint> lanes, int count, uint invalid = 0)
    {
        ApplyPrefixST(lanes, _vfpuControl[VfpuTPrefix], count, invalid);
    }

    /// <summary>The prefix register with the <paramref name="remove"/> bits replaced by <paramref name="add"/>.</summary>
    private uint RewritePrefix(int control, uint remove, uint add)
    {
        return (_vfpuControl[control] & ~remove) | add;
    }

    /// <summary>The D prefix saturation: 1 clamps to [0, 1], 3 to [-1, 1] (NaN stays, -0 becomes +0 at [0, 1]).</summary>
    private void ApplyPrefixD(Span<uint> lanes, int count, bool onlyWriteMask = false)
    {
        uint prefix = _vfpuControl[VfpuDPrefix];
        if (prefix == 0 || onlyWriteMask)
        {
            return;
        }

        for (int i = 0; i < count; i++)
        {
            uint saturation = (prefix >> (i * 2)) & 3;
            if (saturation == 1)
            {
                lanes[i] = Clamp(lanes[i], 0x00000000, 0x3F800000);
            }
            else if (saturation == 3)
            {
                lanes[i] = Clamp(lanes[i], 0xBF800000, 0x3F800000);
            }
        }
    }

    private static uint Clamp(uint bits, uint minimumBits, uint maximumBits)
    {
        float value = BitConverter.UInt32BitsToSingle(bits);
        if (value >= BitConverter.UInt32BitsToSingle(maximumBits))
        {
            return maximumBits;
        }

        if (value <= BitConverter.UInt32BitsToSingle(minimumBits))
        {
            return minimumBits;
        }

        return bits;
    }

    /// <summary>After a VFPU operation the prefixes go back to their defaults.</summary>
    private void EatPrefixes()
    {
        _vfpuControl[VfpuSPrefix] = VfpuIdentityPrefix;
        _vfpuControl[VfpuTPrefix] = VfpuIdentityPrefix;
        _vfpuControl[VfpuDPrefix] = 0;
    }

    /// <summary>For the operations whose D prefix only reaches the last lane: moves its lane-0 mask and saturation there.</summary>
    private void RestrictDPrefixToLastLane(int count)
    {
        uint prefix = _vfpuControl[VfpuDPrefix];
        uint lastMask = (prefix & (1u << 8)) << (count - 1);
        uint lastSaturation = (prefix & 3) << ((count * 2) - 2);
        _vfpuControl[VfpuDPrefix] = lastMask | lastSaturation;
    }

    /// <summary>Lanes whose S or T swizzle names a lane past the vector (and is not a constant) come out as zero.</summary>
    private void ZeroInvalidSwizzles(Span<uint> lanes, int count)
    {
        uint sPrefix = _vfpuControl[VfpuSPrefix];
        uint tPrefix = _vfpuControl[VfpuTPrefix];
        for (int i = 0; i < count; i++)
        {
            bool sInvalid = ((sPrefix >> (i * 2)) & 3) >= count && ((sPrefix >> (12 + i)) & 1) == 0;
            bool tInvalid = ((tPrefix >> (i * 2)) & 3) >= count && ((tPrefix >> (12 + i)) & 1) == 0;
            if (sInvalid || tInvalid)
            {
                lanes[i] = 0;
            }
        }
    }

    /// <summary>The single-lane forms read their lane from prefix position 0; naming another lane gives zero.</summary>
    private bool LastLaneSwizzleInvalid(int control)
    {
        uint prefix = _vfpuControl[control];
        return (prefix & 3) != 0 && (prefix & (1u << 12)) == 0;
    }

    private static bool VfpuControlWritable(int register, out uint mask, out uint setBits)
    {
        setBits = 0;
        switch (register)
        {
            case VfpuSPrefix:
            case VfpuTPrefix:
                mask = 0x000FFFFF;
                return true;
            case VfpuDPrefix:
                mask = 0x00000FFF;
                return true;
            case VfpuConditionCodes:
                mask = 0x0000003F;
                return true;
            case VfpuInf4:
                mask = 0xFFFFFFFF;
                return true;
            default:
                if (register >= VfpuRandom0 && register < VfpuControlCount)
                {
                    mask = 0x000FFFFF;
                    setBits = 0x3F800000;
                    return true;
                }

                // 5, 6 and the revision are read-only
                mask = 0;
                return false;
        }
    }

    private void WriteVfpuControlMasked(int register, uint value)
    {
        if (VfpuControlWritable(register, out uint mask, out uint setBits))
        {
            _vfpuControl[register] = (value & mask) | setBits;
        }
    }

    /// <summary>COP2: mfv/mfvc and mtv/mtvc (imm8: 0..127 a register, 128..143 a control register), bvf/bvt(l).</summary>
    private void ExecuteCop2(uint instruction)
    {
        int rt = Rt(instruction);
        int immediate = (int)(instruction & 0xFF);
        switch (Rs(instruction))
        {
            case 0x03:
                if (immediate < 128)
                {
                    _gpr[rt] = _vfpr[VfpuIndex(immediate)];
                }
                else if (immediate < 128 + VfpuControlCount)
                {
                    _gpr[rt] = _vfpuControl[immediate - 128];
                }

                break;
            case 0x07:
                if (immediate < 128)
                {
                    // like the loads: every lane, whatever a pending D prefix masks, and the prefix stays
                    _vfpr[VfpuIndex(immediate)] = _gpr[rt];
                }
                else if (immediate < 128 + VfpuControlCount)
                {
                    WriteVfpuControlMasked(immediate - 128, _gpr[rt]);
                }

                break;
            case 0x08:
                VfpuBranch(instruction);
                break;
            default:
                RaiseException(AllegrexExceptionCode.ReservedInstruction);
                break;
        }
    }

    private void VfpuBranch(uint instruction)
    {
        int conditionBit = (int)((instruction >> 18) & 7);
        bool condition = ((_vfpuControl[VfpuConditionCodes] >> conditionBit) & 1) != 0;
        switch ((instruction >> 16) & 3)
        {
            case 0:
                BranchIf(!condition, instruction);
                break;
            case 1:
                BranchIf(condition, instruction);
                break;
            case 2:
                BranchLikelyIf(!condition, instruction);
                break;
            default:
                BranchLikelyIf(condition, instruction);
                break;
        }
    }

    /// <summary>vpfxs/vpfxt/vpfxd (the D prefix keeps 12 bits), viim and vfim.</summary>
    private void ExecuteVfpu5(uint instruction)
    {
        uint type = (instruction >> 23) & 7;
        if (type < 6)
        {
            int control = (int)((instruction >> 24) & 3);
            uint data = instruction & 0x000FFFFF;
            _vfpuControl[control] = control == VfpuDPrefix ? data & 0xFFF : data;
            return;
        }

        Span<uint> lane = _laneD.AsSpan(0, 1);
        ushort immediate = (ushort)instruction;
        lane[0] = type == 6
            ? BitConverter.SingleToUInt32Bits((short)immediate)
            : AllegrexVfpuMath.HalfToSingle(immediate);
        ApplyPrefixD(lane, 1);
        WriteVector(lane, 1, VfpuT(instruction));
        EatPrefixes();
    }

    /// <summary>
    /// lv.s/sv.s (vt from bits 16-20 and 0-1) and lv.q/sv.q, lvl.q/lvr.q, svl.q/svr.q (vt from bits 16-20 and 0). The
    /// loads write every lane whatever a pending D prefix masks and leave the prefix pending (pspautotests
    /// cpu/vfpu/prefix_load). Misaligned addresses (4 bytes; 16 for lv.q/sv.q) raise the address error exception.
    /// </summary>
    private void ExecuteVfpuMemory(uint instruction)
    {
        uint address = _gpr[Rs(instruction)] + (uint)(int)(short)(ushort)(instruction & 0xFFFC);
        uint opcode = instruction >> 26;
        bool store = opcode >= 0x38;
        if (opcode == 0x32 || opcode == 0x3A)
        {
            int single = (int)(((instruction >> 16) & 0x1F) | ((instruction & 3) << 5));
            if ((address & 3) != 0)
            {
                AddressError(address, store ? AllegrexExceptionCode.AddressErrorStore : AllegrexExceptionCode.AddressErrorLoad);
                return;
            }

            if (store)
            {
                _bus.Write32(address & PhysicalMask, _vfpr[VfpuIndex(single)]);
            }
            else
            {
                _vfpr[VfpuIndex(single)] = _bus.Read32(address & PhysicalMask);
            }

            return;
        }

        int quad = (int)(((instruction >> 16) & 0x1F) | ((instruction & 1) << 5));
        bool whole = opcode == 0x36 || opcode == 0x3E;
        if ((address & (whole ? 0xFu : 3u)) != 0)
        {
            AddressError(address, store ? AllegrexExceptionCode.AddressErrorStore : AllegrexExceptionCode.AddressErrorLoad);
            return;
        }

        Span<uint> lanes = _laneD.AsSpan(0, 4);
        ReadVector(lanes, 4, quad);
        int offset = (int)((address >> 2) & 3);
        bool right = (instruction & 2) != 0;
        uint physical = address & PhysicalMask;
        if (whole)
        {
            for (int i = 0; i < 4; i++)
            {
                if (store)
                {
                    _bus.Write32(physical + (uint)(i * 4), lanes[i]);
                }
                else
                {
                    lanes[i] = _bus.Read32(physical + (uint)(i * 4));
                }
            }
        }
        else if (!right)
        {
            // lvl.q/svl.q: the lanes from the top down, the address and the words below it in its quadword
            for (int i = 0; i <= offset; i++)
            {
                if (store)
                {
                    _bus.Write32(physical - (uint)(i * 4), lanes[3 - i]);
                }
                else
                {
                    lanes[3 - i] = _bus.Read32(physical - (uint)(i * 4));
                }
            }
        }
        else
        {
            // lvr.q/svr.q: the lanes from the bottom up, the address and the words above it in its quadword
            for (int i = 0; i <= 3 - offset; i++)
            {
                if (store)
                {
                    _bus.Write32(physical + (uint)(i * 4), lanes[i]);
                }
                else
                {
                    lanes[i] = _bus.Read32(physical + (uint)(i * 4));
                }
            }
        }

        if (!store)
        {
            WriteVector(lanes, 4, quad, ignoreMask: true);
        }
    }

    /// <summary>Opcode 0x3F: vnop, vsync, vflush and the rest do nothing; all but 0xFFFF0000.. reset the prefixes.</summary>
    private void ExecuteVfpuFlush(uint instruction)
    {
        if ((instruction & 0xFFFF0000) != 0xFFFF0000)
        {
            EatPrefixes();
        }
    }

    /// <summary>vmfvc vd, control (control in the vs field; past 15 reads 0) and vmtvc control, vs.</summary>
    private void MoveFromVfpuControl(uint instruction)
    {
        int control = VfpuS(instruction);
        _vfpr[VfpuIndex(VfpuD(instruction))] = control < VfpuControlCount ? _vfpuControl[control] : 0;
    }

    private void MoveToVfpuControl(uint instruction)
    {
        int control = VfpuD(instruction);
        if (control < VfpuControlCount)
        {
            WriteVfpuControlMasked(control, _vfpr[VfpuIndex(VfpuS(instruction))]);
        }
    }
}
