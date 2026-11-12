using System.Buffers.Binary;
using VitaSharp.Core.Gxm.Shader;

namespace VitaSharp.Tests;

/// <summary>
/// A test instruction with a dot-product ALU op reduces the whole vector. The Silent Hill 4 port's fogged vitaGL vertex
/// program (vs A6BA1847A8F22B82) computes clip w with "VTST.F32 p0 = (fi1.x = dot(fi0, fi1)) == 0" (the guard before
/// 1/w, also the fog factor's input): translated as fi0.x * fi1.x the intro scene went off screen and only the clear
/// colour was left.
/// </summary>
[Collection("shadergen")]
public class VtstDotTests
{
    private static string Generate(ulong[] code)
    {
        byte[] d = new byte[0x100 + code.Length * 8];
        void W32(int o, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(o), v);
        W32(0x3C, (uint)code.Length);
        W32(0x40, 0x100 - 0x40); // primary program
        for (int i = 0; i < code.Length; i++)
        {
            W32(0x100 + i * 8, (uint)code[i]);
            W32(0x104 + i * 8, (uint)(code[i] >> 32));
        }
        var p = new GxpProgram(d, 0x81000000);
        Assert.True(p.IsVertex);
        return ShaderGenerator.Generate(p, new ShaderHints([], [], 0x00000000, false, 0)).Glsl;
    }

    [Fact]
    public void DotProductTestUsesAllFourComponents()
    {
        string glsl = Generate([0x48C081800FB23E7D]);
        string line = glsl.Split('\n').Single(l => l.Contains("fi[0]") && l.Contains("fi[1]") && l.Contains(" = "));
        Assert.Contains("dot(vec4(fi[0].x, fi[0].y, fi[0].z, fi[0].w), vec4(fi[1].x, fi[1].y, fi[1].z, fi[1].w))", line);
    }
}
