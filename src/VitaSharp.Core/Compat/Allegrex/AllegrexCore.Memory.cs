namespace VitaSharp.Core.Compat.Allegrex;

/// <summary>
/// Loads and stores. Little endian; a misaligned lh/lhu/lw/ll or sh/sw/sc raises the address error exception (BadVAddr
/// = the address) and leaves rt and memory unchanged; lwl/lwr/swl/swr do the unaligned halves through aligned words.
/// </summary>
public sealed partial class AllegrexCore
{
    private uint EffectiveAddress(uint instruction)
    {
        return _gpr[Rs(instruction)] + SignedImmediate(instruction);
    }

    private void ExecuteLoad(uint instruction)
    {
        uint address = EffectiveAddress(instruction);
        int rt = Rt(instruction);
        switch (instruction >> 26)
        {
            case 0x20:
                _gpr[rt] = (uint)(int)(sbyte)_bus.Read8(address & PhysicalMask);
                break;
            case 0x24:
                _gpr[rt] = _bus.Read8(address & PhysicalMask);
                break;
            case 0x21:
                if ((address & 1) != 0)
                {
                    AddressError(address, AllegrexExceptionCode.AddressErrorLoad);
                    return;
                }

                _gpr[rt] = (uint)(int)(short)_bus.Read16(address & PhysicalMask);
                break;
            case 0x25:
                if ((address & 1) != 0)
                {
                    AddressError(address, AllegrexExceptionCode.AddressErrorLoad);
                    return;
                }

                _gpr[rt] = _bus.Read16(address & PhysicalMask);
                break;
            case 0x23:
            case 0x30:
                if ((address & 3) != 0)
                {
                    AddressError(address, AllegrexExceptionCode.AddressErrorLoad);
                    return;
                }

                _gpr[rt] = _bus.Read32(address & PhysicalMask);
                if ((instruction >> 26) == 0x30)
                {
                    _linkBit = true;
                }

                break;
            case 0x22:
                LoadWordLeft(address, rt);
                break;
            case 0x26:
                LoadWordRight(address, rt);
                break;
        }
    }

    private void ExecuteStore(uint instruction)
    {
        uint address = EffectiveAddress(instruction);
        int rt = Rt(instruction);
        uint value = _gpr[rt];
        switch (instruction >> 26)
        {
            case 0x28:
                Store8(address & PhysicalMask, (byte)value);
                break;
            case 0x29:
                if ((address & 1) != 0)
                {
                    AddressError(address, AllegrexExceptionCode.AddressErrorStore);
                    return;
                }

                Store16(address & PhysicalMask, (ushort)value);
                break;
            case 0x2B:
                if ((address & 3) != 0)
                {
                    AddressError(address, AllegrexExceptionCode.AddressErrorStore);
                    return;
                }

                Store32(address & PhysicalMask, value);
                break;
            case 0x38:
                StoreConditional(address, rt, value);
                break;
            case 0x2A:
                StoreWordLeft(address, value);
                break;
            case 0x2E:
                StoreWordRight(address, value);
                break;
        }
    }

    /// <summary>sc: stores and sets rt = 1 while the LL bit (set by ll, cleared by eret and exceptions) holds, else rt = 0.</summary>
    private void StoreConditional(uint address, int rt, uint value)
    {
        if ((address & 3) != 0)
        {
            AddressError(address, AllegrexExceptionCode.AddressErrorStore);
            return;
        }

        if (_linkBit)
        {
            Store32(address & PhysicalMask, value);
            _gpr[rt] = 1;
        }
        else
        {
            _gpr[rt] = 0;
        }
    }

    /// <summary>lwl: the bytes from the aligned word's start up to <paramref name="address"/> go to the top of rt.</summary>
    private void LoadWordLeft(uint address, int rt)
    {
        uint word = _bus.Read32(address & PhysicalMask & ~3u);
        int shift = (int)(address & 3) * 8;
        _gpr[rt] = (_gpr[rt] & (0x00FFFFFFu >> shift)) | (word << (24 - shift));
    }

    /// <summary>lwr: the bytes from <paramref name="address"/> to the aligned word's end go to the bottom of rt.</summary>
    private void LoadWordRight(uint address, int rt)
    {
        uint word = _bus.Read32(address & PhysicalMask & ~3u);
        int shift = (int)(address & 3) * 8;
        _gpr[rt] = (_gpr[rt] & ~(0xFFFFFFFFu >> shift)) | (word >> shift);
    }

    private void StoreWordLeft(uint address, uint value)
    {
        uint aligned = address & PhysicalMask & ~3u;
        int shift = (int)(address & 3) * 8;
        uint word = _bus.Read32(aligned);
        Store32(aligned, (value >> (24 - shift)) | (word & ~(0xFFFFFFFFu >> (24 - shift))));
    }

    private void StoreWordRight(uint address, uint value)
    {
        uint aligned = address & PhysicalMask & ~3u;
        int shift = (int)(address & 3) * 8;
        uint word = _bus.Read32(aligned);
        Store32(aligned, (value << shift) | (word & ~(0xFFFFFFFFu << shift)));
    }
}
