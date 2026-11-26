using System.Buffers.Binary;
using VitaSharp.Core.Gxm.Shader;

namespace VitaSharp.Tests;

/// <summary>GXP parameter/container parsing into program inputs (<see cref="ProgramInput"/>).</summary>
[Collection("guest")]
public class ProgramInputTests
{
    /// <summary>A fragment GXP with one UNIFORM_BUFFER parameter (buffer <paramref name="buffer"/>, 4 bytes) and,
    /// optionally, a container that makes the buffer register resident at <paramref name="sa"/>.</summary>
    private static GxpProgram Program(uint buffer, bool container, ushort sa = 0, ushort words = 1)
    {
        byte[] d = new byte[0x100];
        void W32(int o, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(o), v);
        void W16(int o, ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(o), v);
        d[0x14] = 1; // fragment
        W32(0x24, 1); // parameter count
        W32(0x28, 0xC0 - 0x28); // parameter table
        W32(0xC0, 0xF0 - 0xC0); // name (empty)
        d[0xC4] = (byte)ParamCategory.UniformBuffer | ((byte)ParamType.Aggregate << 4);
        W32(0xC8, 4); // array size: bytes of the buffer
        W32(0xCC, buffer); // resource index: the buffer
        if (container)
        {
            W32(0x90, 1); // container count
            W32(0x94, 0xD0 - 0x94);
            W16(0xD0, (ushort)buffer);
            W16(0xD4, sa);
            W16(0xD6, words);
        }
        return new GxpProgram(d, 0x81000000);
    }

    [Fact]
    public void WholeUniformBufferWithContainerIsRegisterResident()
    {
        UniformBufferDesc b = Assert.Single(ProgramInput.From(Program(4, true, sa: 3, words: 1)).UniformBuffers);
        Assert.Equal(4u, b.Index);
        Assert.Equal(1u, b.RegBlockSize);
        Assert.Equal(3u, b.RegStartOffset);
    }

    [Fact]
    public void WholeUniformBufferWithoutContainerIsMemoryOnly()
    {
        UniformBufferDesc b = Assert.Single(ProgramInput.From(Program(4, false)).UniformBuffers);
        Assert.Equal(0u, b.RegBlockSize);
    }

    /// <summary>The copy's load follows <see cref="ShaderGenerator.UncheckedUniformLoads"/>, a process-wide flag the
    /// renderer sets from its device (on by default on tile-based ones: MoltenVK on the macOS runner, Mali): both
    /// values are pinned here, so the test does not depend on which renderer an earlier test built.</summary>
    [Theory]
    [InlineData(false, "sa[0].x = fetchMemory(")]
    [InlineData(true, "sa[0].x = uintBitsToFloat(ub[")]
    public void RegisterResidentBufferIsCopiedIntoSecondaryAttributes(bool uncheckedLoads, string expected)
    {
        bool savedUnchecked = ShaderGenerator.UncheckedUniformLoads;
        bool savedSplit = ShaderGenerator.SplitUniformSlots;
        ShaderGenerator.UncheckedUniformLoads = uncheckedLoads;
        ShaderGenerator.SplitUniformSlots = false;
        try
        {
            GeneratedShader gen = ShaderGenerator.Generate(Program(4, true, sa: 0, words: 1), new ShaderHints([], [], 0x01000000, false));
            Assert.Contains(expected, gen.Glsl);
        }
        finally
        {
            ShaderGenerator.UncheckedUniformLoads = savedUnchecked;
            ShaderGenerator.SplitUniformSlots = savedSplit;
        }
    }
}
