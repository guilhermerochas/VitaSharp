using System.Buffers.Binary;
using VitaSharp.Core.Gxm.Shader;

namespace VitaSharp.Tests;

/// <summary>
/// Point sprites above native resolution: a vertex program that writes the point size scales it by the internal
/// resolution (rinfo.res_multiplier) in its ScaledPointSize variant, which has a cache key of its own; every other
/// program keeps its GLSL and its key (no shader cache is invalidated).
/// </summary>
[Collection("shadergen")]
public class PointSizeTests
{
    // the vertex program of MaliDeviceTests, with the varyings block moved to 0xA0 so its vertex outputs can be set
    static GxpProgram Program(bool pointSize)
    {
        ulong[] code = [0x6081000460600003, 0x50C9000A20200180];
        var d = new byte[0x100 + code.Length * 8];
        void W32(int o, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(o), v);
        W32(0x2C, 0xA0 - 0x2C); // varyings offset (relative to the field)
        if (pointSize)
        {
            W32(0xA0 + 16, 0x100); // vertex outputs 1: point size
        }

        W32(0x3C, (uint)code.Length);
        W32(0x40, 0x100 - 0x40);
        for (int i = 0; i < code.Length; i++)
        {
            W32(0x100 + i * 8, (uint)code[i]);
            W32(0x104 + i * 8, (uint)(code[i] >> 32));
        }
        return new GxpProgram(d, 0x81000000);
    }

    static string Glsl(GxpProgram p, bool scaled) =>
        ShaderGenerator.Generate(p, new ShaderHints([], [], 0x00000000, ScaledPointSize: scaled)).Glsl;

    [Fact]
    public void PointSizeFollowsTheInternalResolution()
    {
        var p = Program(pointSize: true);
        Assert.True(p.WritesPointSize);
        Assert.Contains("* rinfo.res_multiplier;", Glsl(p, scaled: true).Split('\n').Single(l => l.Contains("gl_PointSize =")));
        // the variant without the hint (recipes recorded before it) still translates as before
        Assert.DoesNotContain("res_multiplier", Glsl(p, scaled: false).Split('\n').Single(l => l.Contains("gl_PointSize =")));
    }

    [Fact]
    public void ProgramsWithoutPointSizeAreUnchanged()
    {
        var p = Program(pointSize: false);
        Assert.False(p.WritesPointSize);
        Assert.DoesNotContain("gl_PointSize =", Glsl(p, scaled: false));
    }
}
