using VitaSharp.Core.Hle;
using VitaSharp.Core.Util;

namespace VitaSharp.Tests;

/// <summary>printf-style formatting from a va_list in guest memory: doubleword arguments (%lld, %llx, double) sit at the
/// next 8-byte aligned address, wherever the list starts (AAPCS; GCC's va_arg rounds the pointer up).</summary>
public sealed class VaListTests
{
    /// <summary>A va_list over a word array that starts at <paramref name="start"/>.</summary>
    sealed class Words : IVaSource
    {
        readonly uint _start;
        readonly uint[] _words;
        uint _p;
        public Words(uint start, params uint[] words)
        {
            _start = start;
            _words = words;
            _p = start;
        }
        public uint Next()
        {
            uint v = _words[(_p - _start) / 4];
            _p += 4;
            return v;
        }
        public uint? Address => _p;
    }

    [Fact]
    public void DoublewordAfterTwoWordsOfAListStartingAtFourModEight()
    {
        // printf(fmt, s1, s2, (long long)x, ...) -> va_start points at the saved r1 (4 mod 8): r1 r2 | r3 (padding) | x
        var va = new VarArgs(new Words(0x1004, 1, 2, 0xDEAD, 0x4001000D, 0, 3), 0);
        Assert.Equal(1u, va.NextU32());
        Assert.Equal(2u, va.NextU32());
        Assert.Equal(0x4001000DUL, va.NextU64());
        Assert.Equal(3u, va.NextU32());
    }

    [Fact]
    public void DoublewordRightAtAnAlignedStart()
    {
        var va = new VarArgs(new Words(0x1000, 0x89ABCDEF, 0x01234567, 7), 0);
        Assert.Equal(0x0123456789ABCDEFUL, va.NextU64());
        Assert.Equal(7u, va.NextU32());
    }

    [Fact]
    public void FormatsThe64BitFieldOfAnUnalignedList()
    {
        var va = new VarArgs(new Words(0x2004, 0, 0, 0xBAD, 0x4001000D, 0), 0);
        Assert.Equal("0 0 0x4001000d", CFormat.Format("%d %d 0x%08llx", va));
    }
}
