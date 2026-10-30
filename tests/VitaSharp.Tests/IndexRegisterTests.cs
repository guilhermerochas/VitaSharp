using System.Buffers.Binary;
using VitaSharp.Core.Gxm.Shader;

namespace VitaSharp.Tests;

/// <summary>
/// Index registers built from partly written temporaries (Tearaway's skinned meshes, vs E4A5458A1E5E81D8): a 16-bit
/// VBW writes the bone offset into the low half of r0.w, then a 32-bit VBW moves the whole word to idx1. The upper
/// half of r0.w was never written: with an uninitialised GLSL temporary it was driver garbage (bone matrices read far
/// out of range, black spiky "vertex explosions" changing every frame).
/// </summary>
public class IndexRegisterTests
{
    // 19: VBW r0.w.u16 = (o0.x.u16 << 3); 20: VBW idx1 = r0.w | 0
    static readonly ulong[] BoneIndex = [0x6081000460600003, 0x50C9000A20200180];

    static string Generate(ulong[] code)
    {
        var d = new byte[0x100 + code.Length * 8];
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
    public void WholeWordMoveToIndexKeepsTheLow16Bits()
    {
        var glsl = Generate(BoneIndex);
        var line = glsl.Split('\n').Single(l => l.TrimStart().StartsWith("idx[0] = ") && l.Contains("r[0].w"));
        Assert.Contains("& 65535", line);
    }

    [Fact]
    public void TemporariesStartAtZero()
    {
        var glsl = Generate(BoneIndex);
        int init = glsl.IndexOf("r[k] = vec4(0.0);", StringComparison.Ordinal);
        Assert.True(init > 0);
        Assert.True(init < glsl.IndexOf("r[0].w = ", StringComparison.Ordinal));
    }
}
