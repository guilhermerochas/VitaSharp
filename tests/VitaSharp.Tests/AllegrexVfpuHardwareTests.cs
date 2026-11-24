using System.Globalization;
using VitaSharp.Core.Compat.Allegrex;

namespace VitaSharp.Tests;

/// <summary>
/// The VFPU against outputs recorded on a PSP, bit for bit: Fixtures/Allegrex/vfpu-exact.expected (pspautotests
/// tests/cpu/vfpu/exact: vrcp, vrsq, vsqrt, vsin, vcos, vnsin, vasin, vexp2, vrexp2, vlog2 and vnrcp over special
/// values, an exponent/mantissa sweep and random inputs) and vfpu-vrnd.expected (tests/cpu/vfpu/vrnd: the generator's
/// state and draws for six seeds). The files are the hardware's printed results (github.com/hrydgard/pspautotests).
/// </summary>
public sealed class AllegrexVfpuHardwareTests
{
    private static readonly (string Name, int Operation)[] s_operations =
    [
        ("rcp", 16), ("rsq", 17), ("sqrt", 22), ("sin", 18), ("cos", 19), ("nsin", 26), ("asin", 23), ("exp2", 20),
        ("rexp2", 28), ("log2", 21), ("nrcp", 24),
    ];

    private static string[] Fixture(string name)
    {
        return File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Allegrex", name));
    }

    private static uint Hex(string text)
    {
        return uint.Parse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }

    [Fact]
    public void UnaryFunctionsMatchThePspBitForBit()
    {
        int checkedValues = 0;
        List<string> mismatches = [];
        foreach (string line in Fixture("vfpu-exact.expected"))
        {
            if (line.StartsWith("--", StringComparison.Ordinal) || line.Length == 0)
            {
                continue;
            }

            string[] fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            uint input = Hex(fields[0].TrimEnd(':'));
            for (int i = 1; i + 1 < fields.Length; i += 2)
            {
                int operation = Array.Find(s_operations, entry => entry.Name == fields[i]).Operation;
                uint expected = Hex(fields[i + 1]);
                uint actual = AllegrexVfpuMath.Transcendental(operation, input);
                checkedValues++;
                if (actual != expected)
                {
                    mismatches.Add($"{fields[i]}({input:x8}) = {actual:x8}, PSP {expected:x8}");
                }
            }
        }

        Assert.True(checkedValues > 4000, $"only {checkedValues} values read");
        Assert.True(mismatches.Count == 0, string.Join(Environment.NewLine, mismatches.Take(40)));
    }

    [Fact]
    public void TheInstructionsUseTheSameFunctions()
    {
        // vsin.q of the fixture's 1/3 row and vrcp.s through the core
        AllegrexRig rig = new(0xD0000000u | (18u << 16) | (1u << 8) | 0u | 0x8080u, 0xD0000000u | (16u << 16) | (5u << 8) | 4u);
        rig.Core.WriteVfpuRegister(0x01, 0x3EAAAAAB);
        rig.Core.WriteVfpuRegister(0x21, 0x3E2AAAAB);
        rig.Core.WriteVfpuRegister(0x41, 0x40490FDB);
        rig.Core.WriteVfpuRegister(0x61, 0x3F000000);
        rig.Core.WriteVfpuRegister(0x05, 0x3E432777);
        rig.Step(2);
        Assert.Equal(0x3EFFFFF8u, rig.Core.ReadVfpuRegister(0x00));
        Assert.Equal(0x3E8483E8u, rig.Core.ReadVfpuRegister(0x20));
        Assert.Equal(0xBF79B1B4u, rig.Core.ReadVfpuRegister(0x40));
        Assert.Equal(0x3F3504F0u, rig.Core.ReadVfpuRegister(0x60));
        Assert.Equal(0x40A7E884u, rig.Core.ReadVfpuRegister(0x04));
    }

    private static uint[] ParseWords(string text)
    {
        return text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Hex).ToArray();
    }

    [Fact]
    public void GeneratorMatchesThePspForEverySeed()
    {
        string[] lines = Fixture("vfpu-vrnd.expected");
        Span<uint> state = stackalloc uint[8];
        int seeds = 0;
        int draws = 0;
        for (int index = 0; index < lines.Length; index++)
        {
            string line = lines[index];
            if (!line.StartsWith("-- seed ", StringComparison.Ordinal))
            {
                continue;
            }

            seeds++;
            uint seed = Hex(line.Substring(8, 8));
            AllegrexVfpuMath.SeedRandom(seed, state);
            string after = lines[++index];
            Assert.Equal(ParseWords(after[(after.IndexOf(':') + 1)..]), state.ToArray());
            while (lines[index + 1].StartsWith("vrndi=", StringComparison.Ordinal))
            {
                string draw = lines[++index];
                uint expected = Hex(draw.Substring(6, 8));
                Assert.Equal(expected, AllegrexVfpuMath.NextRandom(state));
                Assert.Equal(ParseWords(draw[(draw.IndexOf(':') + 1)..]), state.ToArray());
                draws++;
            }

            List<uint> next = [.. ParseWords(lines[++index]["next 40:".Length..])];
            while (next.Count < 40)
            {
                next.AddRange(ParseWords(lines[++index]));
            }

            foreach (uint expected in next)
            {
                Assert.Equal(expected, AllegrexVfpuMath.NextRandom(state));
                draws++;
            }
        }

        Assert.Equal(6, seeds);
        Assert.True(draws >= 6 * 46, $"{draws} draws");
    }

    [Fact]
    public void VectorDrawsFillFromTheLastLane()
    {
        // vrnds.s S000 (seed 1), then vrndi.q C000 and vrndf1.q C000, as the fixture's "vector draws, seed 1"
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
