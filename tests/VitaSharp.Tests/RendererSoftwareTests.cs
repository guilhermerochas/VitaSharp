using VitaSharp.Core.Gpu;
using VitaSharp.Core.Gxm.Shader;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using VitaSharp.Renderer.Vulkan;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// The Vulkan renderer on a software device (<see cref="SoftwareVulkan"/>): the GXM calls of a game drive it through
/// the GPU queue as in the emulator, headless. What the Vita guarantees is checked on the guest memory the GPU writes
/// back: a scene without draws keeps its colour surface (the tiles are loaded and stored again), transfers and
/// notifications come in GPU order, and draws with translated shaders render and present.
/// </summary>
[Collection("guest")]
public sealed class RendererSoftwareTests(GuestMemoryFixture fx)
{
    const int W = 64, H = 64;

    static uint Program(HleHarness h, bool fragment)
    {
        ulong[] code = fragment ? [] : [0x6081000460600003, 0x50C9000A20200180];
        uint size = 0x100 + (uint)code.Length * 8, p = h.Alloc(size);
        GuestMemory.W32(p, 0x00505847);
        GuestMemory.W32(p + 8, size);
        if (fragment)
        {
            GuestMemory.W32(p + 0x14, GxpProgram.FlagFragment);
        }

        GuestMemory.W32(p + 0x3C, (uint)code.Length);
        GuestMemory.W32(p + 0x40, 0x100 - 0x40);
        for (int i = 0; i < code.Length; i++)
        {
            GuestMemory.W32(p + 0x100 + (uint)i * 8, (uint)code[i]);
            GuestMemory.W32(p + 0x104 + (uint)i * 8, (uint)(code[i] >> 32));
        }
        return p;
    }

    [SoftwareVulkanFact]
    public void ScenesTransfersDrawsAndPresentsOnASoftwareDevice()
    {
        using var driver = SoftwareVulkan.Use();
        using var h = new HleHarness(fx, "vkrender");
        string frames = Path.Combine(h.Root, "frames");
        Directory.CreateDirectory(frames);
        var renderer = new VulkanRenderer(new VulkanRendererOptions
        {
            VSync = false,
            AsyncShaders = false,
            AsyncPipelines = false,
            DumpFramesDir = frames,
            DumpEvery = 1,
        });
        try
        {
            Assert.False(string.IsNullOrEmpty(renderer.DeviceName));
            renderer.BeginTitle("PCSA00000");
            h.Emu.Gpu.Backend = renderer;
            var g = h.Module<Gxm>();
            uint ip = h.Alloc(0x14);
            GuestMemory.W32(ip + 4, 2);
            Assert.Equal(0, Call(g, "sceGxmInitialize", ip));
            try
            {
                uint cp = h.Alloc(0x24), o = h.Word(0);
                GuestMemory.W32(cp + 8, h.Alloc(0x1000));
                GuestMemory.W32(cp + 12, 0x1000);
                GuestMemory.W32(cp + 16, h.Alloc(0x4000));
                GuestMemory.W32(cp + 20, 0x4000);
                GuestMemory.W32(cp + 24, h.Alloc(0x4000));
                GuestMemory.W32(cp + 28, 0x4000);
                Assert.Equal(0, Call(g, "sceGxmCreateContext", cp, new Ptr<uint>(o)));
                uint ctx = GuestMemory.R32(o);
                uint rp = h.Alloc(0x14);
                GuestMemory.Write(rp + 4, (ushort)W);
                GuestMemory.Write(rp + 6, (ushort)H);
                GuestMemory.Write(rp + 8, (ushort)1);
                Assert.Equal(0, Call(g, "sceGxmCreateRenderTarget", rp, new Ptr<uint>(o)));
                uint rt = GuestMemory.R32(o);
                // an A8B8G8R8 linear colour surface holding a pattern
                uint data = h.Alloc(W * H * 4), color = h.Alloc(0x40);
                for (uint i = 0; i < W * H; i++)
                {
                    GuestMemory.W32(data + i * 4, 0xFF000000u | (i * 2654435761u >> 8));
                }

                var pattern = GuestMemory.ReadSpan(data, W * H * 4).ToArray();
                Assert.Equal(0, Call(g, "sceGxmColorSurfaceInit", color, 0u, 0u, 0u, 0u, (uint)W, (uint)H, (uint)W, data));

                // 1. a scene without draws: the surface comes back as it was
                uint word = h.Word(0), notif = h.Alloc(8);
                GuestMemory.W32(notif, word);
                GuestMemory.W32(notif + 4, 1);
                Assert.Equal(0, Call(g, "sceGxmBeginScene", ctx, 0u, rt, 0u, 0u, 0u, color, 0u));
                Assert.Equal(0, Call(g, "sceGxmEndScene", ctx, 0u, notif));
                Assert.Equal(0, Call(g, "sceGxmFinish", ctx));
                Assert.Equal(1u, GuestMemory.R32(word));
                Assert.True(GuestMemory.ReadSpan(data, W * H * 4).SequenceEqual(pattern), "the empty scene changed its colour surface");

                // 2. a CPU transfer into the surface, then a scene that loads it: the transfer's pixels survive
                Assert.Equal(0, Call(g, "sceGxmTransferFill", 0xFF00FF00u, 0x000C0000u, data, 0u, 0u, 8u, 8u, W * 4, 0u, 0u, 0u));
                Assert.Equal(0, Call(g, "sceGxmBeginScene", ctx, 0u, rt, 0u, 0u, 0u, color, 0u));
                Assert.Equal(0, Call(g, "sceGxmEndScene", ctx, 0u, 0u));
                Assert.Equal(0, Call(g, "sceGxmFinish", ctx));
                Assert.Equal(0xFF00FF00u, GuestMemory.R32(data + (7 * W + 7) * 4));
                Assert.Equal(BitConverter.ToUInt32(pattern, (8 * W + 8) * 4), GuestMemory.R32(data + (8 * W + 8) * 4));

                // 3. draws: the shader patcher's programs translated and compiled, pipelines built, the scene stored
                uint pp = h.Alloc(0x40);
                Assert.Equal(0, Call(g, "sceGxmShaderPatcherCreate", pp, new Ptr<uint>(o)));
                uint patcher = GuestMemory.R32(o), vid = h.Word(0), fid = h.Word(0);
                Call(g, "sceGxmShaderPatcherRegisterProgram", patcher, Program(h, false), new Ptr<uint>(vid));
                Call(g, "sceGxmShaderPatcherRegisterProgram", patcher, Program(h, true), new Ptr<uint>(fid));
                uint attr = h.Alloc(8), stream = h.Alloc(4), vp = h.Word(0), fpo = h.Word(0);
                GuestMemory.Write(attr + 5, (byte)3);
                GuestMemory.Write(stream, (ushort)12);
                Assert.Equal(0, Call(g, "sceGxmShaderPatcherCreateVertexProgram", patcher, GuestMemory.R32(vid), attr, 1u, stream, 1u, new Ptr<uint>(vp)));
                Assert.Equal(0, Call(g, "sceGxmShaderPatcherCreateFragmentProgram", patcher, GuestMemory.R32(fid), 0u, 0u, 0u, GuestMemory.R32(vp), new Ptr<uint>(fpo)));
                uint verts = h.Alloc(36), idx = h.Alloc(8);
                float[] v = [-1, -1, 0.5f, 3, -1, 0.5f, -1, 3, 0.5f];
                for (int i = 0; i < v.Length; i++)
                {
                    GuestMemory.Write(verts + (uint)i * 4, v[i]);
                }

                GuestMemory.Write(idx, (ushort)0);
                GuestMemory.Write(idx + 2, (ushort)1);
                GuestMemory.Write(idx + 4, (ushort)2);
                uint depth = h.Alloc(0x20), depthData = h.Alloc(W * H * 4);
                Assert.Equal(0, Call(g, "sceGxmDepthStencilSurfaceInit", depth, 0x00044000u, 0u, (uint)W, depthData, 0u));
                for (int frame = 0; frame < 2; frame++)
                {
                    Assert.Equal(0, Call(g, "sceGxmBeginScene", ctx, 0u, rt, 0u, 0u, 0u, color, depth));
                    Invoke(g, "sceGxmSetVertexProgram", ctx, GuestMemory.R32(vp));
                    Invoke(g, "sceGxmSetFragmentProgram", ctx, GuestMemory.R32(fpo));
                    Invoke(g, "sceGxmSetFrontDepthFunc", ctx, 0x00E00000u); // ALWAYS
                    Invoke(g, "sceGxmSetCullMode", ctx, 0u);
                    Assert.Equal(0, Call(g, "sceGxmSetVertexStream", ctx, 0u, verts));
                    Assert.Equal(0, Call(g, "sceGxmDraw", ctx, 0u, 0u, idx, 3u));        // triangles, u16 indices
                    Assert.Equal(0, Call(g, "sceGxmDraw", ctx, 3u, 0u, idx, 3u));        // a strip
                    Assert.Equal(0, Call(g, "sceGxmDrawInstanced", ctx, 0u, 0u, idx, 6u, 3u));
                    Assert.Equal(0, Call(g, "sceGxmEndScene", ctx, 0u, 0u));
                    h.Emu.Gpu.Submit(new PresentCommand { Frame = new DisplayFrame(data, W, W, H, DisplayPixelFormat.A8B8G8R8) });
                }
                Assert.Equal(0, Call(g, "sceGxmFinish", ctx));
                Assert.True(renderer.PresentCount >= 2);
                Assert.NotEmpty(Directory.EnumerateFiles(frames, "*.png"));
            }
            finally
            {
                Call(g, "sceGxmTerminate");
            }
        }
        finally
        {
            h.Emu.Gpu.WaitIdle();
            h.Emu.Gpu.Backend = null;
            renderer.Dispose();
        }
    }
}
