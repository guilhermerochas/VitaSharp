using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// SceSysclibForDriver, the kernel's C library in SceSysmem (FW 3.74, studied in the decompiled module): the string,
/// memory, conversion and runtime helpers kernel modules import, with the firmware's own results where they differ
/// from the C standard: memcmp answers -1/1 when it compares whole words; strtol and strtoul take an "O" prefix as
/// octal and do not check for overflow (strtoul has no sign and treats base 0 as 10); strncat writes nothing for an
/// empty source; the formatter (__prnt) has no floating point and counts the sign and the "0x" prefix in the
/// precision. The _chk functions and __stack_chk_fail stop the system (the firmware runs a BKPT there, which the
/// kernel turns into a panic) when the caller's buffer is too small.
/// </summary>
public sealed partial class SysmemKernel
{
    /// <summary>The BKPT immediate of the firmware's checks (the _chk functions, __stack_chk_fail).</summary>
    const int CheckBreakpoint = 0x81;

    /// <summary>The formatter's first and last calls of its output callback (before and after the text).</summary>
    const int PrntBegin = 0x200, PrntEnd = 0x201;

    /// <summary>The character classes of the kernel's ctype table (look_ctype_table): upper, lower, digit, space,
    /// punctuation, control, hex letter.</summary>
    const byte CtypeUpper = 0x01, CtypeLower = 0x02, CtypeDigit = 0x04, CtypeSpace = 0x08, CtypePunct = 0x10,
        CtypeControl = 0x20, CtypeHex = 0x40;

    /// <summary>A failed check of the kernel C library: on the Vita the firmware runs BKPT #0x81 and the kernel stops
    /// (panic); the calling thread ends with the emulator's fatal error.</summary>
    static void CheckFailed(string function)
    {
        Log.Error($"kernel panic: {function} check failed (BKPT #0x{CheckBreakpoint:X})");
        throw new GuestFaultException($"BKPT #0x{CheckBreakpoint:X} in {function} (kernel panic)");
    }

    static byte R8(uint address) => GuestMemory.Read<byte>(address);

    static void W8(uint address, byte value) => GuestMemory.Write(address, value);

    static void Charge(uint bytes)
    {
        GuestThread.Current?.Core.ChargeInstructions(bytes / 4);
    }

    /// <summary>The kernel's ctype class of <paramref name="c"/> (0 outside 0..0x7F): the classic table, where the
    /// space is a space and a punctuation character and 0x09-0x0D are spaces.</summary>
    static byte Ctype(uint c)
    {
        if (c >= 0x80)
        {
            return 0;
        }

        if (c >= 'A' && c <= 'Z')
        {
            return (byte)(CtypeUpper | (c <= 'F' ? CtypeHex : 0));
        }

        if (c >= 'a' && c <= 'z')
        {
            return (byte)(CtypeLower | (c <= 'f' ? CtypeHex : 0));
        }

        if (c >= '0' && c <= '9')
        {
            return CtypeDigit;
        }

        if (c == ' ')
        {
            return CtypeSpace | CtypePunct;
        }

        if (c >= 0x09 && c <= 0x0D)
        {
            return CtypeSpace;
        }

        if (c < 0x20 || c == 0x7F)
        {
            return CtypeControl;
        }

        return CtypePunct;
    }

    // ------------------------------------------------------------------ memory

    /// <summary>memcmp(a, b, n). When the two addresses differ by a multiple of 4, b is aligned and n &gt; 3, the
    /// firmware compares big-endian words: a difference in a whole word answers -1 or 1, one in the last partial word
    /// the difference of its first n bytes as a big-endian number; otherwise the difference of the first bytes that
    /// differ.</summary>
    [Hle(Library = "SceSysclibForDriver", Cost = HleCost.UserCall)]
    int kmemcmp(uint a, uint b, uint n)
    {
        Charge(n);
        uint difference = a - b;
        if (difference == 0)
        {
            return 0;
        }

        bool wordPath = n > 3 ? (difference & 3) == 0 : a == 0;
        if (wordPath && (b & 3) == 0)
        {
            while (true)
            {
                uint wordA = System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(GuestMemory.R32(b + difference));
                uint wordB = System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(GuestMemory.R32(b));
                if (wordA != wordB)
                {
                    if (n >= 4)
                    {
                        return wordA > wordB ? 1 : -1;
                    }

                    int shift = (int)(4 - n) * 8;
                    return (int)((wordA >> shift) - (wordB >> shift));
                }

                bool more = n > 3;
                n -= 4;
                b += 4;
                if (!more || n == 0)
                {
                    return 0;
                }
            }
        }

        if (n == 0)
        {
            return 0;
        }

        for (uint i = 0; ; i++)
        {
            int byteA = R8(a + i);
            int byteB = R8(b + i);
            if (byteA != byteB || i == n - 1)
            {
                return byteA - byteB;
            }
        }
    }

    /// <summary>memmove(dst, src, n): a copy of overlapping ranges; returns dst.</summary>
    [Hle(Library = "SceSysclibForDriver", Cost = HleCost.UserCall)]
    uint kmemmove(uint dst, uint src, uint n)
    {
        Charge(n);
        if (n == 0 || dst == src)
        {
            return dst;
        }

        // Buffer.MemoryCopy (under GuestMemory.Copy) copies overlapping ranges as memmove does
        GuestMemory.Copy(dst, src, n);
        return dst;
    }

    /// <summary>memchr(s, c, n): the first byte (uint8) c in n bytes, or 0.</summary>
    [Hle(Library = "SceSysclibForDriver", Cost = HleCost.UserCall)]
    uint kmemchr(uint s, uint c, uint n)
    {
        byte value = (byte)c;
        for (uint i = 0; i < n; i++)
        {
            if (R8(s + i) == value)
            {
                Charge(i);
                return s + i;
            }

            if (s + i == uint.MaxValue)
            {
                break;
            }
        }

        Charge(n);
        return 0;
    }

    /// <summary>timingsafe_memcmp(a, b, n): -1, 0 or 1 for the first byte that differs, reading every byte.</summary>
    [Hle(Library = "SceSysclibForDriver", Cost = HleCost.UserCall)]
    int ktimingsafe_memcmp(uint a, uint b, uint n)
    {
        Charge(n * 4);
        int result = 0;
        bool found = false;
        for (uint i = 0; i < n; i++)
        {
            int difference = R8(a + i) - R8(b + i);
            if (!found && difference != 0)
            {
                result = difference > 0 ? 1 : -1;
                found = true;
            }
        }

        return result;
    }

    // ------------------------------------------------------------------ strings

    /// <summary>strlen(s).</summary>
    [Hle(Library = "SceSysclibForDriver", Cost = HleCost.UserCall)]
    uint kstrlen(uint s) => StringLength(s, uint.MaxValue);

    /// <summary>strnlen(s, max).</summary>
    [Hle(Library = "SceSysclibForDriver", Cost = HleCost.UserCall)]
    uint strnlen(uint s, uint max) => StringLength(s, max);

    static uint StringLength(uint s, uint max)
    {
        uint length = 0;
        while (length < max && R8(s + length) != 0)
        {
            length++;
        }

        Charge(length);
        return length;
    }

    /// <summary>strcmp(a, b): the difference of the first bytes (uint8) that differ, 0 when equal.</summary>
    [Hle(Library = "SceSysclibForDriver", Cost = HleCost.UserCall)]
    int kstrcmp(uint a, uint b)
    {
        for (uint i = 0; ; i++)
        {
            int byteA = R8(a + i);
            int byteB = R8(b + i);
            if (byteB == 0 || byteA != byteB)
            {
                Charge(i);
                return byteA - byteB;
            }
        }
    }

    /// <summary>strchr(s, c): the first c (uint8), the terminator for c = 0, or 0.</summary>
    [Hle(Library = "SceSysclibForDriver", Cost = HleCost.UserCall)]
    uint kstrchr(uint s, uint c)
    {
        byte value = (byte)c;
        for (uint i = 0; ; i++)
        {
            byte current = R8(s + i);
            if (current == value)
            {
                Charge(i);
                return s + i;
            }

            if (current == 0)
            {
                Charge(i);
                return 0;
            }
        }
    }

    /// <summary>strrchr(s, c): the last c (uint8), the terminator for c = 0, or 0.</summary>
    [Hle(Library = "SceSysclibForDriver", Cost = HleCost.UserCall)]
    uint kstrrchr(uint s, uint c)
    {
        byte value = (byte)c;
        uint last = 0;
        for (uint i = 0; ; i++)
        {
            byte current = R8(s + i);
            if (current == value)
            {
                last = s + i;
            }

            if (current == 0)
            {
                Charge(i);
                return last;
            }
        }
    }

    /// <summary>strstr(haystack, needle): the first occurrence, the haystack for an empty needle, or 0.</summary>
    [Hle(Library = "SceSysclibForDriver", Cost = HleCost.UserCall)]
    uint kstrstr(uint haystack, uint needle)
    {
        uint needleLength = StringLength(needle, uint.MaxValue);
        if (needleLength == 0)
        {
            return haystack;
        }

        byte first = R8(needle);
        uint at = haystack;
        while (true)
        {
            uint found = kstrchrThroughEntry(at, first);
            if (found == 0)
            {
                return 0;
            }

            at = found + 1;
            if (kmemcmpThroughEntry(at, needle + 1, needleLength - 1) == 0)
            {
                return found;
            }
        }
    }

    /// <summary>strncpy(dst, src, n): copies up to n bytes and fills the rest of the n with zeros; returns dst.</summary>
    [Hle(Library = "SceSysclibForDriver", Cost = HleCost.UserCall)]
    uint kstrncpy(uint dst, uint src, uint n)
    {
        Charge(n);
        uint i = 0;
        while (i < n)
        {
            byte current = R8(src + i);
            if (current == 0)
            {
                break;
            }

            W8(dst + i, current);
            i++;
        }

        if (i < n)
        {
            GuestMemory.Fill(dst + i, 0, n - i);
        }

        return dst;
    }

    /// <summary>strncpy_s(dst, src, n): copies strnlen(src, n) bytes and a terminator when it fits in n; returns
    /// dst.</summary>
    [Hle(Library = "SceSysclibForDriver", Cost = HleCost.UserCall)]
    uint kstrncpy_s(uint dst, uint src, uint n)
    {
        uint length = StringLength(src, n);
        if (length < n)
        {
            W8(dst + length, 0);
        }

        if (length != 0)
        {
            GuestMemory.Copy(dst, src, length);
        }

        return dst;
    }

    /// <summary>strncat(dst, src, n): appends strnlen(src, n) bytes and a terminator; an empty source writes
    /// nothing. Returns dst.</summary>
    [Hle(Library = "SceSysclibForDriver", Cost = HleCost.UserCall)]
    uint kstrncat(uint dst, uint src, uint n)
    {
        uint length = StringLength(src, n);
        if (length != 0)
        {
            uint end = dst + StringLength(dst, uint.MaxValue);
            W8(end + length, 0);
            GuestMemory.Copy(end, src, length);
        }

        return dst;
    }

    /// <summary>strlcpy(dst, src, size): copies up to size - 1 bytes and a terminator (none for size 0); returns
    /// strlen(src).</summary>
    [Hle(Library = "SceSysclibForDriver", Cost = HleCost.UserCall)]
    uint strlcpy(uint dst, uint src, uint size)
    {
        uint length = StringLength(src, uint.MaxValue);
        if (size != 0)
        {
            uint copied = Math.Min(length, size - 1);
            GuestMemory.Copy(dst, src, copied);
            W8(dst + copied, 0);
        }

        return length;
    }

    /// <summary>strlcat(dst, src, size): appends to the string in the first size bytes of dst, up to size - 1 bytes
    /// in all, with a terminator; returns that string's length (at most size) plus strlen(src).</summary>
    [Hle(Library = "SceSysclibForDriver", Cost = HleCost.UserCall)]
    uint strlcat(uint dst, uint src, uint size)
    {
        uint dstLength = StringLength(dst, size);
        uint srcLength = StringLength(src, uint.MaxValue);
        uint room = size - dstLength;
        if (room == 0)
        {
            return dstLength + srcLength;
        }

        uint copied = Math.Min(srcLength, room - 1);
        GuestMemory.Copy(dst + dstLength, src, copied);
        W8(dst + dstLength + copied, 0);
        return dstLength + srcLength;
    }

    // ------------------------------------------------------------------ ctype

    /// <summary>look_ctype_table(c): the class bits of c, 0 outside 0..0x7F.</summary>
    [Hle(Library = "SceSysclibForDriver", Cost = HleCost.UserInline)]
    uint look_ctype_table(uint c) => Ctype(c);

    /// <summary>toupper(c): only a lower-case ASCII letter changes.</summary>
    [Hle(Library = "SceSysclibForDriver", Cost = HleCost.UserInline)]
    uint ktoupper(uint c) => (Ctype(c) & CtypeLower) != 0 ? c - 0x20 : c;

    /// <summary>tolower(c): only an upper-case ASCII letter changes.</summary>
    [Hle(Library = "SceSysclibForDriver", Cost = HleCost.UserInline)]
    uint ktolower(uint c) => (Ctype(c) & CtypeUpper) != 0 ? c + 0x20 : c;

    // ------------------------------------------------------------------ conversions

    /// <summary>The digit value strtol and strtoul read: 0-9, then 10-35 for letters of either case, 0xFF for any
    /// other byte.</summary>
    static uint DigitValue(byte c)
    {
        byte type = Ctype(c);
        if ((type & CtypeDigit) != 0)
        {
            return (uint)(c & 0xF);
        }

        return (type & (CtypeUpper | CtypeLower)) != 0 ? (uint)(c & 0x1F) + 9 : 0xFF;
    }

    /// <summary>The kernel's strtol(s, end, base): leading spaces, a sign, then "0b"/"0B" (base 0 or 2), "0x"/"0X"
    /// (base 0 or 16), a leading 0 (base 0 or 8) or a leading "o"/"O" (always octal); base 0 or one outside 2..36 is
    /// 10 without a prefix. The digits accumulate with no overflow check; *end is where the digits stopped.</summary>
    [Hle(Library = "SceSysclibForDriver", Cost = HleCost.UserCall)]
    int kstrtol(uint s, uint end, uint numberBase)
    {
        uint at = SkipSpaces(s);
        int sign = 1;
        byte c = R8(at);
        if (c == '-')
        {
            sign = -1;
            at++;
        }
        else if (c == '+')
        {
            at++;
        }

        if (numberBase - 2 > 0x22)
        {
            numberBase = 0;
        }

        c = R8(at);
        if (c == '0')
        {
            uint upper = ktoupperThroughEntry(R8(at + 1));
            if (upper == 'B' && (numberBase & ~2u) == 0)
            {
                numberBase = 2;
                at += 2;
            }
            else if (upper == 'X' && (numberBase & ~0x10u) == 0)
            {
                numberBase = 16;
                at += 2;
            }
            else
            {
                if ((numberBase & ~8u) == 0)
                {
                    numberBase = 8;
                }

                at++;
            }
        }
        else
        {
            if (numberBase == 0)
            {
                numberBase = 10;
            }

            if (ktoupperThroughEntry(c) == 'O')
            {
                numberBase = 8;
                at++;
            }
        }

        uint value = AccumulateDigits(ref at, numberBase);
        if (end != 0)
        {
            GuestMemory.W32(end, at);
        }

        return sign * (int)value;
    }

    /// <summary>The kernel's strtoul(s, end, base): leading spaces (no sign), then "0b"/"0B" (binary), "0x"/"0X"
    /// (hexadecimal) or a leading "o"/"O" (octal) whatever the base; base 0 or one outside 2..36 is 10. No overflow
    /// check; *end is where the digits stopped.</summary>
    [Hle(Library = "SceSysclibForDriver", Cost = HleCost.UserCall)]
    uint kstrtoul(uint s, uint end, uint numberBase)
    {
        uint at = SkipSpaces(s);
        if (numberBase - 2 > 0x22)
        {
            numberBase = 10;
        }

        byte c = R8(at);
        if (c == '0')
        {
            uint upper = ktoupperThroughEntry(R8(at + 1));
            if (upper == 'B')
            {
                numberBase = 2;
                at += 2;
            }
            else if (upper == 'X')
            {
                numberBase = 16;
                at += 2;
            }
            else
            {
                at++;
            }
        }
        else if (ktoupperThroughEntry(c) == 'O')
        {
            numberBase = 8;
            at++;
        }

        uint value = AccumulateDigits(ref at, numberBase);
        if (end != 0)
        {
            GuestMemory.W32(end, at);
        }

        return value;
    }

    static uint SkipSpaces(uint s)
    {
        while ((Ctype(R8(s)) & CtypeSpace) != 0)
        {
            s++;
        }

        return s;
    }

    static uint AccumulateDigits(ref uint at, uint numberBase)
    {
        uint value = 0;
        while (true)
        {
            uint digit = DigitValue(R8(at));
            if (digit >= numberBase)
            {
                return value;
            }

            value = unchecked(numberBase * value + digit);
            at++;
        }
    }

    /// <summary>
    /// The kernel's strtoll(s, end, base) (r0:r1): the standard one (BSD): spaces, a sign, "0x" before a hex digit
    /// for base 0 or 16, a leading 0 as octal for base 0; an overflow answers LLONG_MAX or LLONG_MIN with every digit
    /// read. A base outside 0 and 2..36, or no digit, answers 0 with *end = s.
    /// </summary>
    [Hle(Library = "SceSysclibForDriver", Cost = HleCost.UserCall)]
    void kstrtoll(HleContext context)
    {
        uint s = context.Gpr(0);
        uint end = context.Gpr(1);
        uint numberBase = context.Gpr(2);
        uint at = SkipSpaces(s);
        bool negative = false;
        byte c = R8(at);
        if (c == '-')
        {
            negative = true;
            at++;
        }
        else if (c == '+')
        {
            at++;
        }

        c = R8(at);
        if ((numberBase & ~0x10u) == 0)
        {
            if (c != '0')
            {
                if (numberBase == 0)
                {
                    numberBase = 10;
                }
            }
            else if ((R8(at + 1) & 0xDF) == 'X' && (Ctype(R8(at + 2)) & (CtypeDigit | CtypeHex)) != 0)
            {
                numberBase = 16;
                at += 2;
            }
            else if (numberBase == 0)
            {
                numberBase = 8;
            }
        }
        else if (numberBase - 2 > 0x22)
        {
            if (end != 0)
            {
                GuestMemory.W32(end, s);
            }

            context.Return(0UL);
            return;
        }

        ulong limit = negative ? 0x8000000000000000UL : 0x7FFFFFFFFFFFFFFFUL;
        ulong cutoff = limit / numberBase;
        ulong cutoffDigit = limit % numberBase;
        ulong value = 0;
        int any = 0;
        while (true)
        {
            byte current = R8(at);
            uint digit;
            if (current >= '0' && current <= '9')
            {
                digit = (uint)(current - '0');
            }
            else if (current >= 'A' && current <= 'Z')
            {
                digit = (uint)(current - 'A' + 10);
            }
            else if (current >= 'a' && current <= 'z')
            {
                digit = (uint)(current - 'a' + 10);
            }
            else
            {
                break;
            }

            if (digit >= numberBase)
            {
                break;
            }

            if (any < 0 || value > cutoff || (value == cutoff && digit > cutoffDigit))
            {
                any = -1;
            }
            else
            {
                any = 1;
                value = value * numberBase + digit;
            }

            at++;
        }

        if (any == 0)
        {
            if (end != 0)
            {
                GuestMemory.W32(end, s);
            }

            context.Return(0UL);
            return;
        }

        if (end != 0)
        {
            GuestMemory.W32(end, at);
        }

        if (any < 0)
        {
            context.Return(limit);
            return;
        }

        context.Return(negative ? unchecked(0UL - value) : value);
    }

    // ------------------------------------------------------------------ the ARM runtime (RTABI helpers)

    /// <summary>__aeabi_uidiv(n, d): n / d; d = 0 answers 0xFFFFFFFF (0 for n = 0), as the firmware's
    /// __aeabi_idiv0 returns what it is given.</summary>
    [Hle(Library = "SceSysclibForDriver", Cost = HleCost.UserInline)]
    uint __aeabi_uidiv(uint n, uint d) => d == 0 ? (n != 0 ? uint.MaxValue : 0) : n / d;

    /// <summary>__aeabi_uidivmod(n, d): quotient in r0, remainder in r1 (d = 0: r0 as __aeabi_uidiv, r1 = 0).</summary>
    [Hle(Library = "SceSysclibForDriver", Cost = HleCost.UserInline)]
    unsafe void __aeabi_uidivmod(HleContext context)
    {
        uint n = context.Gpr(0);
        uint d = context.Gpr(1);
        if (d == 0)
        {
            context.Cpu->R[0] = n != 0 ? uint.MaxValue : 0;
            context.Cpu->R[1] = 0;
            return;
        }

        context.Cpu->R[0] = n / d;
        context.Cpu->R[1] = n % d;
    }

    /// <summary>__aeabi_idiv(n, d): n / d, INT_MIN / -1 wrapping; d = 0 answers INT_MAX, INT_MIN or 0 by the sign of
    /// n.</summary>
    [Hle(Library = "SceSysclibForDriver", Cost = HleCost.UserInline)]
    int __aeabi_idiv(int n, int d) => SignedDivide(n, d, out _);

    /// <summary>__aeabi_idivmod(n, d): quotient in r0, remainder (the sign of n) in r1; d = 0: r0 as __aeabi_idiv,
    /// r1 = 0.</summary>
    [Hle(Library = "SceSysclibForDriver", Cost = HleCost.UserInline)]
    unsafe void k__aeabi_idivmod(HleContext context)
    {
        int quotient = SignedDivide((int)context.Gpr(0), (int)context.Gpr(1), out int remainder);
        context.Cpu->R[0] = (uint)quotient;
        context.Cpu->R[1] = (uint)remainder;
    }

    static int SignedDivide(int n, int d, out int remainder)
    {
        remainder = 0;
        if (d == 0)
        {
            return n > 0 ? int.MaxValue : n < 0 ? int.MinValue : 0;
        }

        if (d == -1)
        {
            return unchecked(-n);
        }

        remainder = n % d;
        return n / d;
    }

    /// <summary>A shift of a 32-bit value by a register amount (its low byte), as ARM does: 32 or more gives 0.</summary>
    static uint ShiftLeft(uint value, uint amount)
    {
        amount &= 0xFF;
        return amount >= 32 ? 0 : value << (int)amount;
    }

    static uint ShiftRight(uint value, uint amount)
    {
        amount &= 0xFF;
        return amount >= 32 ? 0 : value >> (int)amount;
    }

    static uint ShiftRightArithmetic(uint value, uint amount)
    {
        amount &= 0xFF;
        return (uint)((int)value >> (int)Math.Min(amount, 31));
    }

    /// <summary>__aeabi_llsl(lo, hi, n): the 64-bit value shifted left (ARM register shifts).</summary>
    [Hle(Library = "SceSysclibForDriver", Cost = HleCost.UserInline)]
    void k__aeabi_llsl(HleContext context)
    {
        uint low = context.Gpr(0);
        uint high = context.Gpr(1);
        uint amount = context.Gpr(2);
        uint resultHigh = ShiftLeft(high, amount) | ShiftLeft(low, amount - 32) | ShiftRight(low, 32 - amount);
        context.Return(((ulong)resultHigh << 32) | ShiftLeft(low, amount));
    }

    /// <summary>__aeabi_llsr(lo, hi, n): the 64-bit value shifted right, logical.</summary>
    [Hle(Library = "SceSysclibForDriver", Cost = HleCost.UserInline)]
    void k__aeabi_llsr(HleContext context)
    {
        uint low = context.Gpr(0);
        uint high = context.Gpr(1);
        uint amount = context.Gpr(2);
        uint resultLow = ShiftRight(low, amount) | ShiftRight(high, amount - 32) | ShiftLeft(high, 32 - amount);
        context.Return(((ulong)ShiftRight(high, amount) << 32) | resultLow);
    }

    /// <summary>__aeabi_lasr (named rshift in the NID list) (lo, hi, n): the 64-bit value shifted right,
    /// arithmetic.</summary>
    [Hle(Library = "SceSysclibForDriver", Cost = HleCost.UserInline)]
    void rshift(HleContext context)
    {
        uint low = context.Gpr(0);
        uint high = context.Gpr(1);
        uint amount = context.Gpr(2);
        uint resultLow = ShiftRight(low, amount);
        if ((int)(amount - 32) >= 0)
        {
            resultLow |= ShiftRightArithmetic(high, amount - 32);
        }

        resultLow |= ShiftLeft(high, 32 - amount);
        context.Return(((ulong)ShiftRightArithmetic(high, amount) << 32) | resultLow);
    }

    /// <summary>__aeabi_lcmp(a, b) (signed 64-bit): -1, 0 or 1.</summary>
    [Hle(Library = "SceSysclibForDriver", Cost = HleCost.UserInline)]
    void __aeabi_lcmp(HleContext context)
    {
        long a = (long)HleContext.ArgU64(context, 0);
        long b = (long)HleContext.ArgU64(context, 2);
        context.Return(a < b ? -1 : a > b ? 1 : 0);
    }

    /// <summary>__aeabi_ulcmp(a, b) (unsigned 64-bit): -1, 0 or 1.</summary>
    [Hle(Library = "SceSysclibForDriver", Cost = HleCost.UserInline)]
    void __aeabi_ulcmp(HleContext context)
    {
        ulong a = HleContext.ArgU64(context, 0);
        ulong b = HleContext.ArgU64(context, 2);
        context.Return(a < b ? -1 : a > b ? 1 : 0);
    }

    /// <summary>__aeabi_lmul(a, b): the low 64 bits of the product.</summary>
    [Hle(Library = "SceSysclibForDriver", Cost = HleCost.UserInline)]
    void __aeabi_lmul(HleContext context)
    {
        context.Return(unchecked(HleContext.ArgU64(context, 0) * HleContext.ArgU64(context, 2)));
    }

    /// <summary>__aeabi_ldivmod(n, d): quotient in r0:r1, remainder in r2:r3, LLONG_MIN / -1 wrapping. d = 0: the
    /// quotient is LLONG_MIN when the sign word (the high word, or the low word when the high word is 0) is negative,
    /// LLONG_MAX for any other nonzero n, 0 for 0; the remainder 0.</summary>
    [Hle(Library = "SceSysclibForDriver", Cost = HleCost.UserInline)]
    unsafe void __aeabi_ldivmod(HleContext context)
    {
        uint low = context.Gpr(0);
        uint high = context.Gpr(1);
        long n = (long)(((ulong)high << 32) | low);
        long d = (long)HleContext.ArgU64(context, 2);
        long quotient;
        long remainder = 0;
        if (d == 0)
        {
            int signWord = (int)(high != 0 ? high : low);
            quotient = signWord < 0 ? long.MinValue : n != 0 ? long.MaxValue : 0;
        }
        else if (d == -1)
        {
            quotient = unchecked(-n);
        }
        else
        {
            quotient = n / d;
            remainder = n % d;
        }

        SetPair(context, (ulong)quotient, (ulong)remainder);
    }

    /// <summary>__aeabi_uldivmod(n, d): quotient in r0:r1, remainder in r2:r3; d = 0: all ones (0 for n = 0), the
    /// remainder 0.</summary>
    [Hle(Library = "SceSysclibForDriver", Cost = HleCost.UserInline)]
    void k__aeabi_uldivmod(HleContext context)
    {
        ulong n = HleContext.ArgU64(context, 0);
        ulong d = HleContext.ArgU64(context, 2);
        if (d == 0)
        {
            SetPair(context, n != 0 ? ulong.MaxValue : 0, 0);
            return;
        }

        SetPair(context, n / d, n % d);
    }

    static unsafe void SetPair(HleContext context, ulong first, ulong second)
    {
        context.Cpu->R[0] = (uint)first;
        context.Cpu->R[1] = (uint)(first >> 32);
        context.Cpu->R[2] = (uint)second;
        context.Cpu->R[3] = (uint)(second >> 32);
    }

    // ------------------------------------------------------------------ the formatter

    /// <summary>Where the formatter's output goes, one call per character (sign-extended, as the firmware passes a
    /// char), plus <see cref="PrntBegin"/> and <see cref="PrntEnd"/>.</summary>
    interface IPrntSink
    {
        void Put(int character);
    }

    /// <summary>snprintf's sink (the firmware's: size, count, write pointer): counts every character, stores those
    /// before the last byte of the buffer, and writes the terminator at the end when the size is not 0.</summary>
    struct BufferSink(uint buffer, uint size) : IPrntSink
    {
        public uint Count;
        uint _write = buffer;

        public void Put(int character)
        {
            if (character == PrntBegin)
            {
                return;
            }

            if (character == PrntEnd)
            {
                if (size != 0)
                {
                    W8(_write, 0);
                    _write++;
                }

                return;
            }

            if (Count == uint.MaxValue)
            {
                return;
            }

            Count++;
            if (size > Count)
            {
                W8(_write, (byte)character);
                _write++;
            }
        }
    }

    /// <summary>__prnt's sink for a guest callback: callback(arg, character) on the calling thread.</summary>
    readonly struct GuestSink(GuestThread thread, uint callback, uint argument) : IPrntSink
    {
        public void Put(int character)
        {
            thread.CallGuest(callback, argument, (uint)character);
        }
    }

    /// <summary>
    /// The kernel's formatter (__prnt): text and conversions of <paramref name="format"/> with the arguments of the
    /// va_list <paramref name="va"/> (a pointer to the words; 64-bit ones 8-byte aligned). Flags " #+-0", a width and
    /// a precision (digits or *), the sizes h, l, ll and L (64-bit), and the conversions d i D o O u U x X p c s; any
    /// other character (%, f...) is written as is. The sign and the "0x" prefix count toward the precision; "0" pads
    /// strings too; %p is "0x" and 8 zero-padded digits by default. Returns the number of characters.
    /// </summary>
    static int Prnt<TSink>(ref TSink sink, uint format, uint va)
        where TSink : struct, IPrntSink
    {
        if (format == 0)
        {
            return 0;
        }

        sink.Put(PrntBegin);
        int count = 0;
        Span<byte> digits = stackalloc byte[24];
        uint at = format;
        while (true)
        {
            byte c = R8(at);
            if (c == 0)
            {
                break;
            }

            if (c != '%')
            {
                count++;
                sink.Put((sbyte)c);
                at++;
                continue;
            }

            at++;
            int width = 0;
            uint flags = 0;
            bool alternate = false;
            bool longLong = false;
            bool isLong = false;
            int precision = -1;
            byte sign = 0;
            bool prefix = false;
            bool upper = false;
            bool done = false;
            while (!done)
            {
                c = R8(at);
                switch (c)
                {
                    case 0:
                        sink.Put(PrntEnd);
                        return count;
                    case (byte)' ':
                        if (sign == 0)
                        {
                            sign = (byte)' ';
                        }

                        at++;
                        break;
                    case (byte)'#':
                        alternate = true;
                        at++;
                        break;
                    case (byte)'*':
                        width = (int)GuestMemory.R32(va);
                        va += 4;
                        if (width < 0)
                        {
                            width = -width;
                            flags |= 0x10;
                        }

                        at++;
                        break;
                    case (byte)'-':
                        flags |= 0x10;
                        at++;
                        break;
                    case (byte)'+':
                        sign = (byte)'+';
                        at++;
                        break;
                    case (byte)'.':
                        at++;
                        if (R8(at) == '*')
                        {
                            precision = (int)GuestMemory.R32(va);
                            va += 4;
                            at++;
                        }
                        else
                        {
                            precision = 0;
                            while ((Ctype(R8(at)) & CtypeDigit) != 0)
                            {
                                precision = unchecked(precision * 10 + (R8(at) - '0'));
                                at++;
                            }
                        }

                        if (precision < 0)
                        {
                            precision = -1;
                        }

                        break;
                    case (byte)'0':
                        flags |= 0x20;
                        at++;
                        break;
                    case >= (byte)'1' and <= (byte)'9':
                        width = 0;
                        while ((Ctype(R8(at)) & CtypeDigit) != 0)
                        {
                            width = unchecked(width * 10 + (R8(at) - '0'));
                            at++;
                        }

                        break;
                    case (byte)'h':
                        at++;
                        break;
                    case (byte)'l':
                        if (isLong)
                        {
                            isLong = false;
                            longLong = true;
                        }
                        else
                        {
                            isLong = true;
                        }

                        at++;
                        break;
                    case (byte)'L':
                        longLong = true;
                        at++;
                        break;
                    case (byte)'c':
                    {
                        digits[0] = (byte)GuestMemory.R32(va);
                        va += 4;
                        count += PrntField(ref sink, digits[..1], 0, width, flags, 0, false, c);
                        at++;
                        done = true;
                        break;
                    }
                    case (byte)'s':
                    {
                        uint text = GuestMemory.R32(va);
                        va += 4;
                        count += PrntString(ref sink, text, precision, width, flags);
                        at++;
                        done = true;
                        break;
                    }
                    case (byte)'d' or (byte)'i' or (byte)'D' or (byte)'o' or (byte)'O' or (byte)'u' or (byte)'U'
                        or (byte)'x' or (byte)'X' or (byte)'p':
                    {
                        uint numberBase = c switch
                        {
                            (byte)'o' or (byte)'O' => 8u,
                            (byte)'x' or (byte)'X' or (byte)'p' => 16u,
                            _ => 10u,
                        };
                        upper = c == 'X';
                        ulong value;
                        bool signedConversion = c is (byte)'d' or (byte)'i' or (byte)'D';
                        if (c == 'p')
                        {
                            if (width == 0)
                            {
                                width = 10;
                            }

                            flags |= 0x60;
                            prefix = true;
                            value = GuestMemory.R32(va);
                            va += 4;
                            if (longLong)
                            {
                                value = 0;
                            }

                            sign = 0;
                        }
                        else if (longLong)
                        {
                            va = (va + 7) & ~7u;
                            value = GuestMemory.R32(va) | ((ulong)GuestMemory.R32(va + 4) << 32);
                            va += 8;
                            if (signedConversion && (long)value < 0)
                            {
                                value = unchecked(0UL - value);
                                sign = (byte)'-';
                            }
                            else if (c is (byte)'o' or (byte)'O' or (byte)'u' or (byte)'U')
                            {
                                sign = 0;
                            }

                            if (c is (byte)'x' or (byte)'X' && alternate && value != 0)
                            {
                                prefix = true;
                            }
                        }
                        else
                        {
                            uint word = GuestMemory.R32(va);
                            va += 4;
                            value = word;
                            if (signedConversion)
                            {
                                if ((int)word < 0)
                                {
                                    value = unchecked(0u - word);
                                    sign = (byte)'-';
                                }
                            }
                            else
                            {
                                sign = 0;
                            }

                            if (c is (byte)'x' or (byte)'X' && alternate && word != 0)
                            {
                                prefix = true;
                            }
                        }

                        int length = 0;
                        if (precision != -1)
                        {
                            flags &= ~0x20u;
                        }

                        if (precision != 0 || value != 0)
                        {
                            length = FormatDigits(digits, value, numberBase, upper);
                            if (alternate && numberBase == 8 && digits[digits.Length - length] != '0')
                            {
                                length++;
                                digits[digits.Length - length] = (byte)'0';
                            }
                        }

                        count += PrntField(ref sink, digits[(digits.Length - length)..], sign, width, flags, precision, prefix,
                            c == 'p' ? (byte)'x' : c);
                        at++;
                        done = true;
                        break;
                    }
                    default:
                        count++;
                        sink.Put((sbyte)c);
                        at++;
                        done = true;
                        break;
                }
            }
        }

        sink.Put(PrntEnd);
        return count;
    }

    /// <summary>Writes the digits of <paramref name="value"/> at the end of <paramref name="buffer"/>; returns how
    /// many (at least one).</summary>
    static int FormatDigits(Span<byte> buffer, ulong value, uint numberBase, bool upper)
    {
        ReadOnlySpan<byte> lowerDigits = "0123456789abcdef"u8;
        ReadOnlySpan<byte> upperDigits = "0123456789ABCDEF"u8;
        int at = buffer.Length;
        do
        {
            at--;
            buffer[at] = upper ? upperDigits[(int)(value % numberBase)] : lowerDigits[(int)(value % numberBase)];
            value /= numberBase;
        }
        while (value != 0);

        return buffer.Length - at;
    }

    /// <summary>%s: the string's first strnlen(s, precision) bytes ("(null)" for 0), laid out like any field.</summary>
    static int PrntString<TSink>(ref TSink sink, uint text, int precision, int width, uint flags)
        where TSink : struct, IPrntSink
    {
        if (text == 0)
        {
            return PrntField(ref sink, "(null)"u8[..(int)Math.Min(6u, (uint)precision)], 0, width, flags, 0, false, (byte)'s');
        }

        uint length = StringLength(text, (uint)precision);
        int total = (int)length;
        if ((flags & 0x30) == 0 && total < width)
        {
            Pad(ref sink, (byte)' ', width - total);
        }

        if ((flags & 0x30) == 0x20 && total < width)
        {
            Pad(ref sink, (byte)'0', width - total);
        }

        for (uint i = 0; i < length; i++)
        {
            sink.Put((sbyte)R8(text + i));
        }

        if ((flags & 0x10) != 0 && total < width)
        {
            Pad(ref sink, (byte)' ', width - total);
        }

        return Math.Max(total, width);
    }

    /// <summary>A field as the firmware lays it out: leading spaces (no flag), the sign, the "0x" prefix, zeros for
    /// the "0" flag, zeros up to the precision (counting the sign and prefix), the text, trailing spaces ("-" flag).
    /// Returns its length.</summary>
    static int PrntField<TSink>(ref TSink sink, ReadOnlySpan<byte> text, byte sign, int width, uint flags, int precision,
        bool prefix, byte conversion)
        where TSink : struct, IPrntSink
    {
        int length = text.Length;
        if (sign != 0)
        {
            length++;
        }

        if (prefix)
        {
            length += 2;
        }

        int total = Math.Max(length, precision);
        if ((flags & 0x30) == 0 && total < width)
        {
            Pad(ref sink, (byte)' ', width - total);
        }

        if (sign != 0)
        {
            sink.Put(sign);
        }

        if (prefix)
        {
            sink.Put('0');
            sink.Put(conversion);
        }

        if ((flags & 0x30) == 0x20 && total < width)
        {
            Pad(ref sink, (byte)'0', width - total);
        }

        if (length < precision)
        {
            Pad(ref sink, (byte)'0', precision - length);
        }

        foreach (byte b in text)
        {
            sink.Put((sbyte)b);
        }

        if ((flags & 0x10) != 0 && total < width)
        {
            Pad(ref sink, (byte)' ', width - total);
        }

        return Math.Max(total, width);
    }

    static void Pad<TSink>(ref TSink sink, byte character, int count)
        where TSink : struct, IPrntSink
    {
        for (int i = 0; i < count; i++)
        {
            sink.Put(character);
        }
    }

    /// <summary>__prnt(callback, arg, format, va_list): the formatter writing through the guest's callback
    /// (callback(arg, 0x200), one call per character, callback(arg, 0x201)); returns the character count.</summary>
    [Hle(Library = "SceSysclibForDriver", Cost = HleCost.UserLibrary)]
    int k__prnt(uint callback, uint argument, uint format, uint va)
    {
        var sink = new GuestSink(GuestThread.Current!, callback, argument);
        return Prnt(ref sink, format, va);
    }

    /// <summary>vsnprintf(buffer, size, format, va_list): what snprintf writes; returns the full length.</summary>
    [Hle(Library = "SceSysclibForDriver", Cost = HleCost.UserLibrary)]
    uint kvsnprintf(uint buffer, uint size, uint format, uint va) => FormatToBuffer(buffer, size, format, va);

    /// <summary>snprintf(buffer, size, format, ...): the firmware pushes r3 below the stack pointer so the variadic
    /// words are contiguous, and formats from there.</summary>
    [Hle(Library = "SceSysclibForDriver", Cost = HleCost.UserLibrary)]
    unsafe void ksnprintf(HleContext context)
    {
        uint va = context.Cpu->R[13] - 4;
        GuestMemory.W32(va, context.Gpr(3));
        context.Return(FormatToBuffer(context.Gpr(0), context.Gpr(1), context.Gpr(2), va));
    }

    static uint FormatToBuffer(uint buffer, uint size, uint format, uint va)
    {
        var sink = new BufferSink(buffer, size);
        Prnt(ref sink, format, va);
        return sink.Count;
    }

    // ------------------------------------------------------------------ the checked (_chk) functions

    /// <summary>__memcpy_chk(dst, src, n, dstSize): memcpy, or a panic when dstSize &lt; n.</summary>
    [Hle(Library = "SceSysclibForDriver", Cost = HleCost.UserCall)]
    uint __memcpy_chk(uint dst, uint src, uint n, uint dstSize)
    {
        if (dstSize < n)
        {
            CheckFailed("__memcpy_chk");
        }

        Charge(n);
        GuestMemory.Copy(dst, src, n);
        return dst;
    }

    /// <summary>__memmove_chk(dst, src, n, dstSize): memmove, or a panic when dstSize &lt; n.</summary>
    [Hle(Library = "SceSysclibForDriver", Cost = HleCost.UserCall)]
    uint __memmove_chk(uint dst, uint src, uint n, uint dstSize)
    {
        if (dstSize < n)
        {
            CheckFailed("__memmove_chk");
        }

        return kmemmoveThroughEntry(dst, src, n);
    }

    /// <summary>__memset_chk(dst, c, n, dstSize): memset, or a panic when dstSize &lt; n.</summary>
    [Hle(Library = "SceSysclibForDriver", Cost = HleCost.UserCall)]
    uint __memset_chk(uint dst, uint c, uint n, uint dstSize)
    {
        if (dstSize < n)
        {
            CheckFailed("__memset_chk");
        }

        Charge(n);
        GuestMemory.Fill(dst, (byte)c, n);
        return dst;
    }

    /// <summary>__strncpy_chk(dst, src, n, dstSize): strncpy, or a panic when dstSize &lt; n.</summary>
    [Hle(Library = "SceSysclibForDriver", Cost = HleCost.UserCall)]
    uint __strncpy_chk(uint dst, uint src, uint n, uint dstSize)
    {
        if (dstSize < n)
        {
            CheckFailed("__strncpy_chk");
        }

        return kstrncpyThroughEntry(dst, src, n);
    }

    /// <summary>__strncpy_chk2(dst, src, n): strncpy (its check, strnlen(src, n) &gt; n, never fails).</summary>
    [Hle(Library = "SceSysclibForDriver", Cost = HleCost.UserCall)]
    uint k__strncpy_chk2(uint dst, uint src, uint n)
    {
        if (StringLength(src, n) > n)
        {
            CheckFailed("__strncpy_chk2");
        }

        return kstrncpyThroughEntry(dst, src, n);
    }

    /// <summary>__strncat_chk(dst, src, n, dstSize): strncat, or a panic when dstSize &lt; n.</summary>
    [Hle(Library = "SceSysclibForDriver", Cost = HleCost.UserCall)]
    uint __strncat_chk(uint dst, uint src, uint n, uint dstSize)
    {
        if (dstSize < n)
        {
            CheckFailed("__strncat_chk");
        }

        return kstrncatThroughEntry(dst, src, n);
    }

    /// <summary>__strncat_chk2(dst, src, n): strncat into a buffer of n bytes; a panic when the string in dst does
    /// not end inside it or the appended bytes would not leave room for the terminator.</summary>
    [Hle(Library = "SceSysclibForDriver", Cost = HleCost.UserCall)]
    uint k__strncat_chk2(uint dst, uint src, uint n)
    {
        uint dstLength = StringLength(dst, n);
        if (n <= dstLength)
        {
            CheckFailed("__strncat_chk2");
        }

        if (n - dstLength <= StringLength(src, n))
        {
            CheckFailed("__strncat_chk2");
        }

        return kstrncatThroughEntry(dst, src, n);
    }

    /// <summary>__strlcpy_chk(dst, src, size, dstSize): strlcpy, or a kernel panic when dstSize &lt; size.</summary>
    [Hle(Library = "SceSysclibForDriver", Cost = HleCost.UserCall)]
    uint k__strlcpy_chk(uint dst, uint src, uint size, uint dstSize)
    {
        if (dstSize < size)
        {
            CheckFailed("__strlcpy_chk");
        }

        return strlcpyThroughEntry(dst, src, size);
    }

    /// <summary>__strlcat_chk(dst, src, size, dstSize): strlcat, or a kernel panic when dstSize &lt; size.</summary>
    [Hle(Library = "SceSysclibForDriver", Cost = HleCost.UserCall)]
    uint k__strlcat_chk(uint dst, uint src, uint size, uint dstSize)
    {
        if (dstSize < size)
        {
            CheckFailed("__strlcat_chk");
        }

        return strlcatThroughEntry(dst, src, size);
    }

    /// <summary>__vsnprintf_chk(buffer, size, flag, bufferSize, format, va_list): vsnprintf, or a kernel panic when
    /// bufferSize &lt; size.</summary>
    [Hle(Library = "SceSysclibForDriver", Cost = HleCost.UserLibrary)]
    void k__vsnprintf_chk(HleContext context)
    {
        if (context.Gpr(3) < context.Gpr(1))
        {
            CheckFailed("__vsnprintf_chk");
        }

        context.Return(FormatToBuffer(context.Gpr(0), context.Gpr(1), context.Gpr(4), context.Gpr(5)));
    }

    /// <summary>__snprintf_chk(buffer, size, flag, bufferSize, format, ...): __vsnprintf_chk with the stacked
    /// arguments after the format.</summary>
    [Hle(Library = "SceSysclibForDriver", Cost = HleCost.UserLibrary)]
    unsafe void k__snprintf_chk(HleContext context)
    {
        if (context.Gpr(3) < context.Gpr(1))
        {
            CheckFailed("__snprintf_chk");
        }

        context.Return(FormatToBuffer(context.Gpr(0), context.Gpr(1), context.Gpr(4), context.Cpu->R[13] + 4));
    }

    /// <summary>__stack_chk_fail(): a function's stack guard was overwritten; the firmware stops the system.</summary>
    [Hle(Library = "SceSysclibForDriver", Cost = HleCost.UserCall)]
    void __kstack_chk_fail()
    {
        CheckFailed("__stack_chk_fail");
    }
}
