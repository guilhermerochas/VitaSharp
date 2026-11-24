using System.Globalization;
using VitaSharp.Core.Compat.Allegrex;

namespace VitaSharp.Tests;

/// <summary>
/// The VFPU against results measured on a PSP, bit for bit. The data below is a small subset of the hardware outputs
/// printed by pspautotests (github.com/hrydgard/pspautotests): tests/cpu/vfpu/exact.expected at commit
/// abdde27b98562ce4f984386c133aa1fa527dfda8 (rows of vrcp, vrsq, vsqrt, vsin, vcos, vnsin, vasin, vexp2, vrexp2, vlog2,
/// vnrcp: the special values, sweep inputs from both signs and random inputs) and tests/cpu/vfpu/vrnd.expected at commit
/// 011a777e6623632ce7dca581782fb0f78cca94f8 (the generator after each of its six seeds). Measurement data, no code.
/// </summary>
public sealed class AllegrexVfpuHardwareTests
{
    // input, then rcp rsq sqrt sin cos nsin asin exp2 rexp2 log2 nrcp as the PSP gave them
    private static readonly string[] s_unaryResults =
    [
        "00000000 7f800000 7f800000 00000000 00000000 3f800000 80000000 00000000 3f800000 3f800000 ff800000 ff800000",
        "80000000 ff800000 ff800000 00000000 80000000 3f800000 00000000 80000000 3f800000 3f800000 ff800000 7f800000",
        "7f800000 00000000 00000000 7f800000 7f800001 7f800001 ff800001 7f800001 7f800000 00000000 7f800000 80000000",
        "ff800000 80000000 ff800001 7f800001 ff800001 7f800001 7f800001 ff800001 00000000 7f800000 7f800001 00000000",
        "7fc00000 7f800001 7f800001 7f800001 7f800001 7f800001 ff800001 7f800001 7f800001 7f800001 7f800001 ff800001",
        "ffc00000 ff800001 ff800001 7f800001 ff800001 7f800001 7f800001 ff800001 7f800001 7f800001 7f800001 7f800001",
        "00000001 7f800000 7f800000 00000000 00000000 3f800000 80000000 00000000 3f800000 3f800000 ff800000 ff800000",
        "00800000 7e800000 5f000000 20000000 00000000 3f800000 80000000 00000000 3f800000 3f7ffffc c2fc0000 fe800000",
        "7f7fffff 00000000 1f800000 5f7ffffc 00000000 3f800000 80000000 7f800001 7f800000 00000000 42fffffe 80000000",
        "3f800000 3f800000 3f800000 3f800000 3f800000 80000000 bf800000 3f800000 40000000 3efffffc 00000000 bf800000",
        "bf800000 bf800000 ff800001 7f800001 bf800000 80000000 3f800000 bf800000 3efffffc 40000000 7f800001 3f800000",
        "40000000 3f000000 3f3504f0 3fb504f0 80000000 bf800000 00000000 7f800001 40800000 3e7ffffc 3f800000 bf000000",
        "3f000000 40000000 3fb504f0 3f3504f0 3f3504f0 3f3504f0 bf3504f0 3eaaaaa8 3fb504f0 3f3504f0 bf800000 c0000000",
        "40490fdb 3ea2f980 3f106eb8 3fe2dfc0 bf79b1b4 3e61e0d0 3f79b1b4 7f800001 410d331c 3de8118c 3fd36438 bea2f980",
        "3eaaaaab 403ffffc 3fddb3d8 3f13cd38 3efffff8 3f5db3d4 befffff8 3e5d8a08 3fa14514 3f4b2ff4 bfcae000 c03ffffc",
        "4b800000 33800000 39800000 45800000 00000000 3f800000 80000000 7f800001 7f800000 00000000 41c00000 b3800000",
        "4b800001 337ffffc 39800000 45800000 80000000 bf800000 00000000 7f800001 7f800000 00000000 41c00000 b37ffffc",
        "00aaaaaa 7e400000 5eddb3d8 2013cd38 00000000 3f800000 80000000 00000000 3f800000 3f7ffffc c2fb2b80 fe400000",
        "3e400000 40aaaaa8 4013cd38 3eddb3d4 3e94a030 3f74fa08 be94a030 3df5eae0 3f91c3d0 3f60ccdc c01a9000 c0aaaaa8",
        "40000001 3efffffc 3f3504f0 3fb504f0 b4c80000 bf7ffffc 34c80000 7f800001 40800000 3e7ffff8 3f800000 befffffc",
        "4eda5a5a 301611a4 37c40114 47272e18 00000000 3f800000 80000000 7f800001 7f800000 00000000 41f62a02 b01611a4",
        "be400000 c0aaaaa8 ff800001 7f800001 be94a030 3f74fa08 3e94a030 bdf5eae0 3f60ccdc 3f91c3d0 7f800001 40aaaaa8",
        "c0000001 befffffc ff800001 7f800001 34c80000 bf7ffffc b4c80000 ff800001 3e7ffff8 40800000 7f800001 3efffffc",
        "3e432777 40a7e884 40129a34 3edf8420 3e96fe98 3f749d40 be96fe98 3dfa01d0 3f921394 3f605220 c0190e80 c0a7e884",
        "bd3075ff c1b9b20c ff800001 7f800001 bd8a7c64 3f7f69fc 3d8a7c64 bce0bef8 3f7877ec 3f83e140 7f800001 41b9b20c",
        "3a0f9587 44e436f4 422ae9e4 3cbfb8f4 3a618240 3f7ffff8 ba618240 39b6ca80 3f800c70 3f7fe720 c12d5920 c4e436f4",
        "44f11a1f 3a07e8c4 3cba8738 422fac50 3f756a00 3e91b5f4 bf756a00 7f800001 7f800000 00000000 412e9db0 ba07e8c4",
        "4384bda7 3b76db60 3d7b630c 4182594c 3f3a289c bf2fbad0 bf3a289c 7f800001 7f800000 00000000 4100d6e8 bb76db60",
        "c2d4d22f bc19f840 ff800001 7f800001 3f19df80 bf4c9848 bf19df80 ff800001 0a409a50 74aa21e8 7f800001 3c19f840",
    ];

    private static readonly int[] s_operations = [16, 17, 22, 18, 19, 26, 23, 20, 28, 21, 24];

    // per seed: the state after vrnds; the first vrndi value and the state after it; the seed, the 2nd to 4th
    // vrndi values and the 7th and 8th
    private static readonly string[] s_generator =
    [
        // seed 00000000
        "3f800000 3f800000 3f800000 3f800000 3f800000 3f800000 3f800000 3f800000",
        "00000001 3f800001 3f800000 3f800000 3f800000 3f800000 3f800000 3f800000 3f800000",
        "00000000 00010dce 1c5983f7 c35937cc e3cc94b3 63132a58",
        // seed 00000001
        "3f810001 3f800001 3f800001 3f800001 3f800000 3f800000 3f800000 3f800000",
        "00052df3 3f800dce 3f802021 3f800001 3f800004 3f800001 3f800004 3f800000 3f800000",
        "00000001 20618a01 6125e0a7 4068a3e1 88c5605c 93d08434",
        // seed 12345678
        "3f885678 3f875678 3f865678 3f855678 3f841234 3f831234 3f821234 3f811234",
        "632f0a9e 3f805619 3f805aa5 3f805678 3f8059e0 3f8092c5 3f808798 3f801234 3f8048d1",
        "12345678 9cb065e1 a483c0e3 752e3534 d8fa70d8 213c6031",
        // seed ffffffff
        "3f8fffff 3f8fffff 3f8fffff 3f8fffff 3f8fffff 3f8fffff 3f8fffff 3f8fffff",
        "0002d24f 3f82f234 3f80e01f 3f80ffff 3f80fffc 3f80fffe 3f800003 3f80ffff 3f80ffff",
        "ffffffff dfb0959d ea156854 4a6beded ca27fc91 5cfc70f2",
        // seed 80000000
        "3f800000 3f800000 3f800000 3f800000 3f808000 3f808000 3f808000 3f888000",
        "00084001 3f810001 3f804000 3f800000 3f800000 3f808000 3f808008 3f808000 3f800000",
        "80000000 89099253 46eac541 99113a65 188d8119 e4e6d3c3",
        // seed deadbeef
        "3f8fbeef 3f8ebeef 3f8ebeef 3f8bbeef 3f8ddead 3f8adead 3f8edead 3f8ddead",
        "a4f724d7 3f820864 3f8020b7 3f80beef 3f80fbbc 3f80e2c3 3f80477d 3f80dead 3f807ab6",
        "deadbeef e83813c0 e6f60ab9 73f242ed 98e70f39 280d21d9",
    ];

    private static uint[] Words(string text)
    {
        string[] fields = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        uint[] words = new uint[fields.Length];
        for (int i = 0; i < fields.Length; i++)
        {
            words[i] = uint.Parse(fields[i], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }

        return words;
    }

    [Fact]
    public void UnaryFunctionsMatchThePspBitForBit()
    {
        List<string> mismatches = [];
        foreach (string row in s_unaryResults)
        {
            uint[] words = Words(row);
            for (int i = 0; i < s_operations.Length; i++)
            {
                uint actual = AllegrexVfpuMath.Transcendental(s_operations[i], words[0]);
                if (actual != words[i + 1])
                {
                    mismatches.Add($"operation {s_operations[i]} of {words[0]:x8} = {actual:x8}, PSP {words[i + 1]:x8}");
                }
            }
        }

        Assert.True(mismatches.Count == 0, string.Join(Environment.NewLine, mismatches));
    }

    [Fact]
    public void TheInstructionsUseTheSameFunctions()
    {
        // vsin.q of four of the rows above and vrcp.s, through the core
        AllegrexRig rig = new(0xD0000000u | (18u << 16) | (1u << 8) | 0u | 0x8080u, 0xD0000000u | (16u << 16) | (5u << 8) | 4u);
        rig.Core.WriteVfpuRegister(0x01, 0x3EAAAAAB);
        rig.Core.WriteVfpuRegister(0x21, 0x40000001);
        rig.Core.WriteVfpuRegister(0x41, 0x40490FDB);
        rig.Core.WriteVfpuRegister(0x61, 0x3F000000);
        rig.Core.WriteVfpuRegister(0x05, 0x3E432777);
        rig.Step(2);
        Assert.Equal(0x3EFFFFF8u, rig.Core.ReadVfpuRegister(0x00));
        Assert.Equal(0xB4C80000u, rig.Core.ReadVfpuRegister(0x20));
        Assert.Equal(0xBF79B1B4u, rig.Core.ReadVfpuRegister(0x40));
        Assert.Equal(0x3F3504F0u, rig.Core.ReadVfpuRegister(0x60));
        Assert.Equal(0x40A7E884u, rig.Core.ReadVfpuRegister(0x04));
    }

    [Fact]
    public void GeneratorMatchesThePspForEverySeed()
    {
        Span<uint> state = stackalloc uint[8];
        Assert.Equal(18, s_generator.Length);
        for (int i = 0; i < s_generator.Length; i += 3)
        {
            uint[] afterSeed = Words(s_generator[i]);
            uint[] firstDraw = Words(s_generator[i + 1]);
            uint[] later = Words(s_generator[i + 2]);
            AllegrexVfpuMath.SeedRandom(later[0], state);
            Assert.Equal(afterSeed, state.ToArray());
            Assert.Equal(firstDraw[0], AllegrexVfpuMath.NextRandom(state));
            Assert.Equal(firstDraw[1..], state.ToArray());
            Assert.Equal(later[1], AllegrexVfpuMath.NextRandom(state));
            Assert.Equal(later[2], AllegrexVfpuMath.NextRandom(state));
            Assert.Equal(later[3], AllegrexVfpuMath.NextRandom(state));
            AllegrexVfpuMath.NextRandom(state);
            AllegrexVfpuMath.NextRandom(state);
            Assert.Equal(later[4], AllegrexVfpuMath.NextRandom(state));
            Assert.Equal(later[5], AllegrexVfpuMath.NextRandom(state));
        }
    }

    [Fact]
    public void VectorDrawsFillFromTheLastLane()
    {
        // vrnds.s S000 (seed 1), then vrndi.q C000 and vrndf1.q C000 (vrnd.expected, "vector draws, seed 1")
        uint vrnds = 0xD0200000u;
        uint vrndiQuad = 0xD0210000u | 0x8080u;
        uint vrndf1Quad = 0xD0220000u | 0x8080u;
        AllegrexRig rig = new(vrnds, vrndiQuad, 0xF8000000u | (5u << 21), vrndf1Quad);
        rig.Core.WriteVfpuRegister(0, 1);
        rig[5] = 0x88010000;
        rig.Step(4);
        uint[] integers = new uint[4];
        for (int i = 0; i < 4; i++)
        {
            integers[i] = rig.Bus.Read32(0x08010000 + (uint)(i * 4));
        }

        Assert.Equal(new uint[] { 0x4068A3E1, 0x6125E0A7, 0x20618A01, 0x00052DF3 }, integers);
        Assert.Equal(new uint[] { 0x3FD08434, 0x3FC5605C, 0x3FBBF1B8, 0x3F9C1DCB }, new[] { rig.Core.ReadVfpuRegister(0), rig.Core.ReadVfpuRegister(0x20), rig.Core.ReadVfpuRegister(0x40), rig.Core.ReadVfpuRegister(0x60) });
    }
}
