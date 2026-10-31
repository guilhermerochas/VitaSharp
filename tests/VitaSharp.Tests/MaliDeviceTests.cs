using System.Buffers.Binary;
using VitaSharp.Android.Input;
using VitaSharp.Core.Gxm.Shader;
using VitaSharp.Core.Host;
using VitaSharp.Core.Media;

namespace VitaSharp.Tests;

/// <summary>
/// What the first real-device run (Mali-G57 MC2, Redmi Note 14) needed: the GXM far plane without shaderClipDistance
/// (GpuQuirk.FarPlaneDiscard: a varying plus a fragment discard; the desktop GLSL unchanged), the raw AAC frame lengths
/// SceMp4 hands out (MediaCodec reports no bytes consumed), and the scripted pad of unattended device runs.
/// </summary>
[Collection("shadergen")]
public class MaliDeviceTests
{
    // a vertex program (position is always an output) and an empty fragment program
    static GxpProgram Program(bool fragment)
    {
        ulong[] code = fragment ? [] : [0x6081000460600003, 0x50C9000A20200180];
        var d = new byte[0x100 + code.Length * 8];
        void W32(int o, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(o), v);
        if (fragment) W32(0x14, GxpProgram.FlagFragment);
        W32(0x3C, (uint)code.Length);
        W32(0x40, 0x100 - 0x40);
        for (int i = 0; i < code.Length; i++)
        {
            W32(0x100 + i * 8, (uint)code[i]);
            W32(0x104 + i * 8, (uint)(code[i] >> 32));
        }
        return new GxpProgram(d, 0x81000000);
    }

    static string Glsl(bool fragment, ShaderGenerator.FarPlaneClip mode)
    {
        var saved = ShaderGenerator.FarPlane;
        ShaderGenerator.FarPlane = mode;
        try { return ShaderGenerator.Generate(Program(fragment), new ShaderHints([], [], 0x00000000, false, 0)).Glsl; }
        finally { ShaderGenerator.FarPlane = saved; }
    }

    [Fact]
    public void ClipDistanceIsTheDefaultAndUnchanged()
    {
        Assert.Equal(ShaderGenerator.FarPlaneClip.ClipDistance, ShaderGenerator.FarPlane);
        var vs = Glsl(false, ShaderGenerator.FarPlaneClip.ClipDistance);
        Assert.Contains("gl_ClipDistance[0] = gl_Position.w - gl_Position.z;", vs);
        Assert.DoesNotContain("v_FarClip", vs);
        Assert.DoesNotContain("v_FarClip", Glsl(true, ShaderGenerator.FarPlaneClip.ClipDistance));
    }

    [Fact]
    public void DiscardPassesTheDistanceAndDiscardsLast()
    {
        var vs = Glsl(false, ShaderGenerator.FarPlaneClip.Discard);
        Assert.DoesNotContain("gl_ClipDistance", vs);
        Assert.Contains("layout(location = 14) out float v_FarClip;", vs);
        Assert.Contains("v_FarClip = gl_Position.w - gl_Position.z;", vs);
        var fs = Glsl(true, ShaderGenerator.FarPlaneClip.Discard);
        Assert.Contains("layout(location = 14) in float v_FarClip;", fs);
        // after the colour output: texture derivatives stay in uniform control flow
        int discard = fs.IndexOf("if (v_FarClip < 0.0) discard;", StringComparison.Ordinal);
        Assert.True(discard > fs.IndexOf("out_color = ", StringComparison.Ordinal));
    }

    [Fact]
    public void HardwareClipWritesNoDistance()
    {
        var vs = Glsl(false, ShaderGenerator.FarPlaneClip.Hardware);
        Assert.DoesNotContain("gl_ClipDistance", vs);
        Assert.DoesNotContain("v_FarClip", vs);
    }

    [Fact]
    public void RawAacFrameLengthsComeFromSceMp4Units()
    {
        RawAudioFrameLengths.Clear();
        byte[] big = [.. Enumerable.Range(0, 300).Select(i => (byte)(i * 7 + 3))];
        byte[] small = [0x21, 0x10, 0x05, 0x00, 0xA0, 0x19, 0x33, 0x87, 0x00]; // F1 2011's 9-byte silent frames
        byte[] tiny = [0x21, 0x10, 0x05];
        RawAudioFrameLengths.Record(big);
        RawAudioFrameLengths.Record(small);
        RawAudioFrameLengths.Record(tiny);
        // a frame in a game's slot: stale bytes follow it
        Assert.Equal(300, RawAudioFrameLengths.Find([.. big, .. new byte[200]]));
        Assert.Equal(9, RawAudioFrameLengths.Find([.. small, 0x55, 0x66, 0x77]));
        Assert.Equal(3, RawAudioFrameLengths.Find([.. tiny, 0x99, 0x99]));
        Assert.Equal(0, RawAudioFrameLengths.Find([1, 2, 3, 4, 5, 6, 7, 8, 9]));
        Assert.Equal(0, RawAudioFrameLengths.Find(big.AsSpan(0, 299)));
        RawAudioFrameLengths.Clear();
    }

    [Fact]
    public void ScriptedPadPressesButtonsAndSticks()
    {
        var pad = new ScriptedPad("cross@0:100, lstick@0:1:-1:100, start@50");
        Assert.Equal(3, pad.Count);
        var s = pad.Apply(ControllerState.Neutral);
        Assert.Equal(VitaButtons.Cross, s.Buttons);
        Assert.Equal(255, s.Lx);
        Assert.Equal(1, s.Ly);
        Assert.Equal(128, s.Rx);
        var taps = new ScriptedPad("tap@0:0.25:0.75:100,btap@0:0.5:0.5:100,tap@50:0.1:0.1");
        var m = VitaSharp.Core.Input.MappedInput.Neutral;
        taps.AddTouches(ref m);
        Assert.Equal(1, m.FrontCount);
        Assert.Equal(1, m.BackCount);
        Assert.Equal(0.25f, m.Front[0].X);
        Assert.NotEqual(m.Front[0].Id, m.Back[0].Id);
        Assert.Throws<FormatException>(() => new ScriptedPad("tap@1:0.5"));
        Assert.Throws<FormatException>(() => new ScriptedPad("jump@1"));
        Assert.Throws<FormatException>(() => new ScriptedPad("cross"));
    }

    // God of War Collection on Mali: F16 attributes at offsets 3 and 11 of a 19-byte stride (Vulkan requires the
    // component size alignment; such attributes are fetched as bytes, ShaderGenerator.ByteFetchFlag)
    [Theory]
    [InlineData(8, 3, 19, true)]   // F16 at an odd offset
    [InlineData(8, 0, 19, true)]   // F16 at 0, odd stride: every other vertex misaligned
    [InlineData(8, 4, 20, false)]
    [InlineData(5, 0, 19, false)]  // S8N: bytes need no alignment
    [InlineData(9, 6, 24, true)]   // F32 at offset 6
    [InlineData(9, 8, 24, false)]
    [InlineData(10, 2, 16, true)]  // untyped words
    public void MisalignedAttributesAreFetchedAsBytes(byte format, int offset, int stride, bool misaligned)
    {
        Assert.Equal(misaligned, ShaderGenerator.AttributeMisaligned(format, offset, stride));
    }
}
