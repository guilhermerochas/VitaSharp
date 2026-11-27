using System.Buffers.Binary;
using VitaSharp.Core.Gxm.Shader;

namespace VitaSharp.Tests;

/// <summary>
/// Index registers built from partly written temporaries (Tearaway's skinned meshes, vs E4A5458A1E5E81D8): a 16-bit
/// VBW writes the bone offset into the low half of r0.w, then a 32-bit VBW moves the whole word to idx1. The upper
/// half of r0.w was never written: with an uninitialised GLSL temporary it was driver garbage (bone matrices read far
/// out of range, black spiky "vertex explosions" changing every frame).
/// </summary>
[Collection("guest")]
public class IndexRegisterTests
{
    // 19: VBW r0.w.u16 = (o0.x.u16 << 3); 20: VBW idx1 = r0.w | 0
    private static readonly ulong[] s_boneIndex = [0x6081000460600003, 0x50C9000A20200180];

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
    public void WholeWordMoveToIndexKeepsTheLow16Bits()
    {
        string glsl = Generate(s_boneIndex);
        string line = glsl.Split('\n').Single(l => l.TrimStart().StartsWith("idx[0] = ") && l.Contains("r[0].w"));
        Assert.Contains("& 65535", line);
    }

    [Fact]
    public void TemporariesStartAtZero()
    {
        string glsl = Generate(s_boneIndex);
        int init = glsl.IndexOf("r[k] = vec4(0.0);", StringComparison.Ordinal);
        Assert.True(init > 0);
        Assert.True(init < glsl.IndexOf("r[0].w = ", StringComparison.Ordinal));
    }

    // GTA: Vice City's port (reVC through vitaGL and the firmware's shader compiler), skinned-ped vertex program
    // AD4D4B2E21DD78EE: the lighting loop indexes the light table in the secondary attributes through idx0.
    // 77: VTST.F16 in its double form, src1 = 7-bit field 0x6E (indexed bank sa, offset 14 pairs = word 28)
    private const ulong LightEnabledTest = 0x488B01811003B700;
    // 89: VMAD2 (F16 x4), src1 = 6-bit field 0x3E (indexed bank sa, offset 14 pairs = word 28), the same light colour
    private const ulong LightColourMad = 0x048292822183CF86;

    /// <summary>An indexed operand's base bank is the two top bits of its register field, whatever the field's width:
    /// the doubled 7-bit field of a double-form VTST and the doubled 6-bit field of VMAD2 name the same secondary-attribute
    /// word. Reading the bank from fixed bits 5-6 of the doubled number took the VTST's operand from the primary
    /// attributes: the light test always saw 0, the loop ended before the first light and the lit models (the peds,
    /// the bottles on the table) came out black.</summary>
    [Theory]
    [InlineData(LightEnabledTest)]
    [InlineData(LightColourMad)]
    public void IndexedOperandsKeepTheirBankInEveryFieldWidth(ulong instruction)
    {
        string glsl = Generate([instruction]);
        Assert.Contains("sa[((idx[0] * 2 + 28)", glsl);
        Assert.DoesNotContain("pa[((idx[0]", glsl);
    }
}
