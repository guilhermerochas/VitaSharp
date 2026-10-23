using System.Reflection;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;

namespace VitaSharp.Tests;

/// <summary>SceGxm / SceDisplay / SceScreenShot HLE functions that only touch guest memory.</summary>
[Collection("guest")]
public sealed class GxmHleTests(GuestMemoryFixture fx)
{
    static object? Call<T>(T module, string name, params object[] args) =>
        typeof(T).GetMethod(name, BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!.Invoke(module, args);

    static uint U(object? o) => o switch { uint u => u, int i => (uint)i, bool b => b ? 1u : 0u, _ => 0 };

    [Fact]
    public void ColorSurfaceClipAndDither()
    {
        var gxm = new Gxm();
        uint s = fx.Memory.Alloc(0x40, "surface");
        uint data = fx.Memory.Alloc(0x1000, "surfdata");
        uint outs = fx.Memory.Alloc(0x10, "clipout");
        Assert.Equal(0u, U(Call(gxm, "sceGxmColorSurfaceInit", s, 0u, 0u, 0u, 0u, 640u, 368u, 640u, data)));
        Call(gxm, "sceGxmColorSurfaceGetClip", s, new Ptr<uint>(outs), new Ptr<uint>(outs + 4), new Ptr<uint>(outs + 8), new Ptr<uint>(outs + 12));
        Assert.Equal([0u, 0u, 639u, 367u], [GuestMemory.R32(outs), GuestMemory.R32(outs + 4), GuestMemory.R32(outs + 8), GuestMemory.R32(outs + 12)]);

        Call(gxm, "sceGxmColorSurfaceSetClip", s, 16u, 32u, 300u, 4000u);
        Call(gxm, "sceGxmColorSurfaceGetClip", s, new Ptr<uint>(outs), new Ptr<uint>(outs + 4), new Ptr<uint>(outs + 8), new Ptr<uint>(outs + 12));
        Assert.Equal([16u, 32u, 300u, 367u], [GuestMemory.R32(outs), GuestMemory.R32(outs + 4), GuestMemory.R32(outs + 8), GuestMemory.R32(outs + 12)]);

        Assert.Equal(0u, U(Call(gxm, "sceGxmColorSurfaceGetDitherMode", s)));
        Call(gxm, "sceGxmColorSurfaceSetGammaMode", s, 0x2000u);
        Call(gxm, "sceGxmColorSurfaceSetDitherMode", s, 8u);
        Assert.Equal(8u, U(Call(gxm, "sceGxmColorSurfaceGetDitherMode", s)));
        Assert.Equal(0x2000u, U(Call(gxm, "sceGxmColorSurfaceGetGammaMode", s)));
        Assert.True((bool)Call(gxm, "sceGxmColorSurfaceIsEnabled", s)!);
        // the clip survives the other setters
        Call(gxm, "sceGxmColorSurfaceGetClip", s, new Ptr<uint>(outs), new Ptr<uint>(outs + 4), new Ptr<uint>(outs + 8), new Ptr<uint>(outs + 12));
        Assert.Equal(300u, GuestMemory.R32(outs + 8));
    }

    [Fact]
    public void TextureGettersAndStride()
    {
        var gxm = new Gxm();
        uint t = fx.Memory.Alloc(0x10, "tex");
        Assert.Equal(0u, U(Call(gxm, "sceGxmTextureInitLinearStrided", t, 0x81000000u, 0x0C000000u, 100u, 50u, 512u)));
        Assert.Equal(512u, U(Call(gxm, "sceGxmTextureGetStride", t)));
        Assert.Equal(0u, U(Call(gxm, "sceGxmTextureSetStride", t, 1024u)));
        Assert.Equal(1024u, U(Call(gxm, "sceGxmTextureGetStride", t)));
        Assert.Equal(100u, U(Call(gxm, "sceGxmTextureGetWidth", t)));
        Assert.Equal(unchecked((uint)0x805B0005), U(Call(gxm, "sceGxmTextureSetStride", t, 1026u)));
        Assert.Equal(2u, U(Call(gxm, "sceGxmTextureGetUAddrModeSafe", t)));
        Assert.Equal(0u, U(Call(gxm, "sceGxmTextureSetUAddrModeSafe", t, 0u))); // ignored on strided textures
        Assert.Equal(1024u, U(Call(gxm, "sceGxmTextureGetStride", t)));

        uint l = fx.Memory.Alloc(0x10, "tex2");
        Assert.Equal(0u, U(Call(gxm, "sceGxmTextureInitLinear", l, 0x81000000u, 0x0C000000u, 64u, 64u, 3u)));
        Assert.Equal(unchecked((uint)0x805B0009), U(Call(gxm, "sceGxmTextureSetStride", l, 256u)));
        Assert.Equal(3u, U(Call(gxm, "sceGxmTextureGetMipmapCountUnsafe", l)));
        Call(gxm, "sceGxmTextureSetUAddrModeSafe", l, 1u);
        Assert.Equal(1u, U(Call(gxm, "sceGxmTextureGetUAddrModeSafe", l)));
        Call(gxm, "sceGxmTextureSetGammaMode", l, 0x08000000u);
        Assert.Equal(0x08000000u, U(Call(gxm, "sceGxmTextureGetGammaMode", l)));
        Call(gxm, "sceGxmTextureSetNormalizeMode", l, 0u);
        Assert.Equal(0u, U(Call(gxm, "sceGxmTextureGetNormalizeMode", l)));
        Call(gxm, "sceGxmTextureSetNormalizeMode", l, 0x80000000u);
        Assert.Equal(0x80000000u, U(Call(gxm, "sceGxmTextureGetNormalizeMode", l)));
    }

    [Fact]
    public void ProgramIsEquivalent()
    {
        var gxm = new Gxm();
        uint a = fx.Memory.Alloc(0x40, "gxpA"), b = fx.Memory.Alloc(0x40, "gxpB");
        for (uint i = 0; i < 0x40; i += 4) { GuestMemory.W32(a + i, i * 7); GuestMemory.W32(b + i, i * 7); }
        GuestMemory.W32(a + 8, 0x40); GuestMemory.W32(b + 8, 0x40);
        Assert.True((bool)Call(gxm, "sceGxmProgramIsEquivalent", a, b)!);
        GuestMemory.W32(b + 0x30, 1);
        Assert.False((bool)Call(gxm, "sceGxmProgramIsEquivalent", a, b)!);
        Assert.False((bool)Call(gxm, "sceGxmProgramIsEquivalent", a, 0u)!);
    }

    [Fact]
    public void ScreenShotParamRoundTrip()
    {
        var ss = new ScreenShot();
        uint p = fx.Memory.Alloc(0x10, "ssparam"), title = fx.Memory.Alloc(0x100, "sstitle"), outBuf = fx.Memory.Alloc(0x100, "ssout");
        ScreenShot.WriteUtf32(title, "Foto サ\U0001F600", 63);
        GuestMemory.Fill(p, 0, 0x10);
        GuestMemory.W32(p, title);
        Assert.Equal(0, (int)Call(ss, "sceScreenShotSetParam", p)!);
        Assert.Equal("Foto サ\U0001F600", ss.PhotoTitle);
        Assert.Null(ss.GameTitle);
        GuestMemory.W32(p + 12, 1);
        Assert.Equal(unchecked((int)0x80102F01), (int)Call(ss, "sceScreenShotSetParam", p)!);
        GuestMemory.W32(p + 12, 0);
        GuestMemory.W32(p, outBuf);
        Assert.Equal(0, (int)Call(ss, "sceScreenShotGetParam", p)!);
        Assert.Equal("Foto サ\U0001F600", ScreenShot.ReadUtf32(outBuf, 64));
        Call(ss, "sceScreenShotDisable");
        Assert.False(ss.Enabled);
        Call(ss, "sceScreenShotEnable");
        Assert.True(ss.Enabled);
    }

    [Fact]
    public void DisplayResolutionInfo()
    {
        var d = new Display();
        uint info = fx.Memory.Alloc(0x20, "resinfo");
        GuestMemory.W32(info, 0x10);
        Assert.Equal(unchecked((int)0x80290001), (int)Call(d, "sceDisplayGetResolutionInfoInternal", 0, info)!);
        GuestMemory.W32(info, 0x1C);
        Assert.Equal(unchecked((int)0x80290000), (int)Call(d, "sceDisplayGetResolutionInfoInternal", 1, info)!);
        Assert.Equal(0, (int)Call(d, "sceDisplayGetResolutionInfoInternal", 0, info)!);
        Assert.Equal(960u, GuestMemory.R32(info + 8));
        Assert.Equal(544u, GuestMemory.R32(info + 12));
        Assert.Equal(59.94005f, GuestMemory.Read<float>(info + 0x18));
    }

    /// <summary>A fragment program with stripped symbols has no sampler parameter: its non-dependent texture reads are
    /// only described by the fragment varying descriptors, and those units count as used (Vita3K get_textures_used),
    /// so precomputed states get a slot for them.</summary>
    [Fact]
    public void TexturesUsedIncludesAnonymousVaryingTextures()
    {
        var gxp = new byte[0x200];
        void W(int o, uint v) => System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(gxp.AsSpan(o), v);
        W(0x14, VitaSharp.Core.Gxm.Shader.GxpProgram.FlagFragment);
        W(0x2C, 0x100 - 0x2C);          // varyings block at 0x100
        gxp[0x100 + 12] = 2;            // two fragment varying descriptors
        W(0x110, 0x20);                 // descriptors at 0x110 + 0x20
        W(0x130, 0xF000); W(0x134, 3);  // texture read: texcoord 0 sampled through unit 3
        W(0x140, 0xF00F); W(0x144, 5);  // plain varying, no texture (texcoord index 0xF)
        var p = new VitaSharp.Core.Gxm.Shader.GxpProgram(gxp, 0);
        Assert.Empty(p.Parameters);
        Assert.Equal(1u << 3, p.TexturesUsed());
    }

    /// <summary>Precomputed setters validate the index against the hardware limits (16 texture units, 14 uniform
    /// buffers, 16 streams), not against what the program uses: an unused slot is accepted and ignored (Vita3K).
    /// Persona 4 Golden's libgmq sets texture unit 4 on every material state and aborts on INVALID_VALUE.</summary>
    [Fact]
    public void PrecomputedSettersAcceptUnusedSlots()
    {
        const uint InvalidValue = 0x805B0003, InvalidPointer = 0x805B0004;
        var gxm = new Gxm();
        uint state = fx.Memory.Alloc(0x40, "pstate");
        uint extra = fx.Memory.Alloc(0x100, "pextra");
        uint tex = fx.Memory.Alloc(0x10, "ptex");
        GuestMemory.Fill(extra, 0xEE, 0x100);
        for (uint i = 0; i < 4; i++) GuestMemory.W32(tex + i * 4, 0x11111111u * (i + 1));
        // 2 textures + 3 uniform buffers (state layout: textures@4 textureCount@8 bufferCount@10 uniformBuffers@12)
        GuestMemory.W32(state + 4, extra);
        GuestMemory.Write(state + 8, (ushort)2);
        GuestMemory.Write(state + 10, (ushort)3);
        GuestMemory.W32(state + 12, extra + 32);

        foreach (var f in new[] { "sceGxmPrecomputedFragmentStateSetTexture", "sceGxmPrecomputedVertexStateSetTexture" })
        {
            Assert.Equal(0u, U(Call(gxm, f, state, 1u, tex)));
            Assert.Equal(0x22222222u, GuestMemory.R32(extra + 16 + 4));
            Assert.Equal(0u, U(Call(gxm, f, state, 4u, tex)));   // unused unit: ignored
            Assert.Equal(0u, U(Call(gxm, f, state, 15u, tex)));
            Assert.Equal(InvalidValue, U(Call(gxm, f, state, 16u, tex)));
            Assert.Equal(InvalidPointer, U(Call(gxm, f, state, 0u, 0u)));
            Assert.Equal(InvalidPointer, U(Call(gxm, f, 0u, 0u, tex)));
        }
        Assert.Equal(0xEEEEEEEEu, GuestMemory.R32(extra + 32)); // the uniform buffer slots were not overwritten

        foreach (var f in new[] { "sceGxmPrecomputedFragmentStateSetUniformBuffer", "sceGxmPrecomputedVertexStateSetUniformBuffer" })
        {
            Assert.Equal(0u, U(Call(gxm, f, state, 2u, 0x81234560u)));
            Assert.Equal(0x81234560u, GuestMemory.R32(extra + 32 + 8));
            Assert.Equal(0u, U(Call(gxm, f, state, 13u, 0x81234560u))); // unused buffer: ignored
            Assert.Equal(InvalidValue, U(Call(gxm, f, state, 14u, 0x81234560u)));
            Assert.Equal(InvalidPointer, U(Call(gxm, f, state, 0u, 0u)));
        }
        Assert.Equal(0xEEEEEEEEu, GuestMemory.R32(extra + 32 + 12));

        // precomputed draw with 2 streams (streams@8 streamCount@12)
        uint draw = fx.Memory.Alloc(0x20, "pdraw");
        uint streams = fx.Memory.Alloc(0x40, "pstreams");
        GuestMemory.Fill(streams, 0xEE, 0x40);
        GuestMemory.W32(draw + 8, streams);
        GuestMemory.Write(draw + 12, (ushort)2);
        Assert.Equal(0u, U(Call(gxm, "sceGxmPrecomputedDrawSetVertexStream", draw, 1u, 0x81000040u)));
        Assert.Equal(0x81000040u, GuestMemory.R32(streams + 4));
        Assert.Equal(0u, U(Call(gxm, "sceGxmPrecomputedDrawSetVertexStream", draw, 5u, 0x81000040u)));
        Assert.Equal(0xEEEEEEEEu, GuestMemory.R32(streams + 20)); // no slot for stream 5: not written
        Assert.Equal(InvalidValue, U(Call(gxm, "sceGxmPrecomputedDrawSetVertexStream", draw, 16u, 0x81000040u)));
        Assert.Equal(InvalidPointer, U(Call(gxm, "sceGxmPrecomputedDrawSetVertexStream", draw, 0u, 0u)));
    }
}
