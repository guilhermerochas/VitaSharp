using System.Collections.Concurrent;
using VitaSharp.Core.Gpu;
using VitaSharp.Core.Gxm;
using VitaSharp.Core.Gxm.Shader;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// SceGxm as a game drives it, with a recording GPU backend in place of Vulkan: initialize, contexts, render targets,
/// sync objects, surfaces, scenes (BeginScene / EndScene and their GPU commands, notifications written in GPU order),
/// the shader patcher with small GXP programs, draws, deferred contexts and command lists, precomputed states,
/// textures and the CPU transfers. Error codes are SCE_GXM_ERROR_* (0x805B00xx).
/// </summary>
[Collection("guest")]
public sealed class GxmFlowTests : IDisposable
{
    private const int UNINITIALIZED = unchecked((int)0x805B0000), ALREADY_INITIALIZED = unchecked((int)0x805B0001),
        INVALID_VALUE = unchecked((int)0x805B0003), INVALID_POINTER = unchecked((int)0x805B0004), INVALID_ALIGNMENT = unchecked((int)0x805B0005),
        NOT_WITHIN_SCENE = unchecked((int)0x805B0006), WITHIN_SCENE = unchecked((int)0x805B0007), NULL_PROGRAM = unchecked((int)0x805B0008),
        UNSUPPORTED = unchecked((int)0x805B0009), DRIVER = unchecked((int)0x805B0017), WITHIN_COMMAND_LIST = unchecked((int)0x805B002C), NOT_WITHIN_COMMAND_LIST = unchecked((int)0x805B002D);

    private sealed class Recorder : IGpuBackend
    {
        public readonly ConcurrentQueue<string> Kinds = new();
        public BeginSceneCommand? LastBegin;
        public int Draws;
        public (uint Index, uint Count, byte Prim, uint Instances) LastDraw;
        /// <summary>Scenes and flips in GPU order, with their colour buffer: "begin 0x...", "end", "present 0x...".</summary>
        public readonly ConcurrentQueue<string> Frames = new();
        public void Execute(GpuCommand command)
        {
            Kinds.Enqueue(command.GetType().Name);
            if (command is BeginSceneCommand b)
            {
                LastBegin = b;
                Frames.Enqueue($"begin 0x{b.Color?.Data ?? 0:X8}");
            }

            if (command is PresentCommand p)
            {
                Frames.Enqueue($"present 0x{p.Frame.Address:X8}");
            }

            if (command is EndSceneCommand)
            {
                Frames.Enqueue("end");
            }

            if (command is DrawCommand d)
            {
                Draws++;
                LastDraw = (d.IndexAddress, d.IndexCount, d.PrimitiveType, d.InstanceCount);
                if (d.Streams[0] != 0 && d.IndexCount > 0)
                {
                    // what a renderer reads of the draw's geometry: its indices and the vertices they use (F32 x3)
                    ushort[] indices = GuestMemory.ReadSpan<ushort>(d.IndexAddress, (int)d.IndexCount).ToArray();
                    uint last = 0;
                    foreach (ushort index in indices)
                    {
                        last = Math.Max(last, index);
                    }

                    float[] vertices = GuestMemory.ReadSpan<float>(d.Streams[0], (int)(last + 1) * 3).ToArray();
                    Geometry.Enqueue((indices, vertices, d.Geometry != null));
                }
            }
        }
        /// <summary>The indices and vertices each draw with a stream read on the GPU worker, and whether they came from
        /// a copy taken at the scene's kick.</summary>
        public readonly ConcurrentQueue<(ushort[] Indices, float[] Vertices, bool Copied)> Geometry = new();
        /// <summary>Runs on the GPU worker at each <see cref="IGpuBackend.Idle"/> (also inside a sync object wait).</summary>
        public Action? OnIdle;
        public void Idle()
        {
            OnIdle?.Invoke();
        }
        public void Dispose()
        {
        }
    }

    private readonly HleHarness _harness;
    private readonly Recorder _rec = new();
    private Gxm Gxm => _harness.Module<Gxm>();

    public GxmFlowTests(GuestMemoryFixture fixture)
    {
        _harness = new HleHarness(fixture, "gxmflow");
        _harness.Emu.Gpu.Backend = _rec;
    }

    public void Dispose()
    {
        Call(Gxm, "sceGxmTerminate");
        _harness.Dispose();
    }

    /// <summary>libgxm 3.74's notification block: NULL before sceGxmInitialize; then the public region is block + 0x20 and
    /// the two SceGxmInternal entries give the block and block + 0x10.</summary>
    [Fact]
    public void NotificationRegionLiesInTheBlockTheInternalEntriesGive()
    {
        Assert.Equal(0u, (uint)Invoke(Gxm, "sceGxmGetNotificationRegion")!);
        Assert.Equal(0u, (uint)Invoke(Gxm, "GxmNotificationBlock")!);
        Assert.Equal(0u, (uint)Invoke(Gxm, "GxmNotificationBlockSecondHalf")!);
        Init();
        uint block = (uint)Invoke(Gxm, "GxmNotificationBlock")!;
        Assert.NotEqual(0u, block);
        Assert.Equal(block + 0x10, (uint)Invoke(Gxm, "GxmNotificationBlockSecondHalf")!);
        Assert.Equal(block + 0x20, (uint)Invoke(Gxm, "sceGxmGetNotificationRegion")!);
        Assert.Equal(0u, GuestMemory.R32(block + 0x81C)); // the 0x820 bytes are cleared
        Call(Gxm, "sceGxmTerminate");
        Assert.Equal(0u, (uint)Invoke(Gxm, "GxmNotificationBlock")!);
        _harness.Dispose();
    }

    private uint Out() => _harness.Word(0);

    private void Init()
    {
        uint p = _harness.Alloc(0x14);
        GuestMemory.W32(p + 4, 2);       // displayQueueMaxPendingCount
        GuestMemory.W32(p + 12, 4);      // display callback data size
        GuestMemory.W32(p + 16, 0x200000);
        Assert.Equal(0, Call(Gxm, "sceGxmInitialize", p));
    }

    private uint Context()
    {
        uint p = _harness.Alloc(0x24), o = Out();
        GuestMemory.W32(p + 8, _harness.Alloc(0x1000));
        GuestMemory.W32(p + 12, 0x1000);    // VDM ring
        GuestMemory.W32(p + 16, _harness.Alloc(0x4000));
        GuestMemory.W32(p + 20, 0x4000);   // vertex ring
        GuestMemory.W32(p + 24, _harness.Alloc(0x4000));
        GuestMemory.W32(p + 28, 0x4000);   // fragment ring
        Assert.Equal(0, Call(Gxm, "sceGxmCreateContext", p, new Ptr<uint>(o)));
        return GuestMemory.R32(o);
    }

    private uint RenderTarget(ushort w, ushort h, ushort multisampleMode = 0)
    {
        uint p = _harness.Alloc(0x14), o = Out();
        GuestMemory.Write(p + 4, w);
        GuestMemory.Write(p + 6, h);
        GuestMemory.Write(p + 8, (ushort)1);
        GuestMemory.Write(p + 10, multisampleMode);
        GuestMemory.W32(p + 16, 0x1234);
        Assert.Equal(0, Call(Gxm, "sceGxmCreateRenderTarget", p, new Ptr<uint>(o)));
        return GuestMemory.R32(o);
    }

    /// <summary>A GXP program (magic "GXP\0", size at +8): a vertex one with two instructions, or an empty fragment one;
    /// <paramref name="defaultUniforms"/> words of default uniform buffer (+0x64).</summary>
    private uint Program(bool fragment, uint defaultUniforms = 0)
    {
        ulong[] code = fragment ? [] : [0x6081000460600003, 0x50C9000A20200180];
        uint size = 0x100 + (uint)code.Length * 8;
        uint p = _harness.Alloc(size);
        GuestMemory.W32(p, 0x00505847);
        GuestMemory.W32(p + 8, size);
        if (fragment)
        {
            GuestMemory.W32(p + 0x14, GxpProgram.FlagFragment);
        }

        GuestMemory.W32(p + 0x3C, (uint)code.Length);
        GuestMemory.W32(p + 0x40, 0x100 - 0x40);
        GuestMemory.W32(p + 0x64, defaultUniforms);
        for (int i = 0; i < code.Length; i++)
        {
            GuestMemory.W32(p + 0x100 + (uint)i * 8, (uint)code[i]);
            GuestMemory.W32(p + 0x104 + (uint)i * 8, (uint)(code[i] >> 32));
        }
        return p;
    }

    private (uint Vp, uint Fp, uint Patcher) Programs(uint vUniforms = 0, uint fUniforms = 0)
    {
        uint pp = _harness.Alloc(0x40), o = Out();
        GuestMemory.W32(pp, 0xABCD); // user data
        Assert.Equal(0, Call(Gxm, "sceGxmShaderPatcherCreate", pp, new Ptr<uint>(o)));
        uint patcher = GuestMemory.R32(o);
        uint vid = Out(), fid = Out();
        Assert.Equal(0, Call(Gxm, "sceGxmShaderPatcherRegisterProgram", patcher, Program(false, vUniforms), new Ptr<uint>(vid)));
        Assert.Equal(0, Call(Gxm, "sceGxmShaderPatcherRegisterProgram", patcher, Program(true, fUniforms), new Ptr<uint>(fid)));
        uint attr = _harness.Alloc(8), stream = _harness.Alloc(4), vp = Out(), fp = Out();
        GuestMemory.Write(attr + 4, (byte)0);
        GuestMemory.Write(attr + 5, (byte)3); // F32 x3 from stream 0
        GuestMemory.Write(stream, (ushort)12);
        Assert.Equal(0, Call(Gxm, "sceGxmShaderPatcherCreateVertexProgram", patcher, GuestMemory.R32(vid), attr, 1u, stream, 1u, new Ptr<uint>(vp)));
        Assert.Equal(0, Call(Gxm, "sceGxmShaderPatcherCreateFragmentProgram", patcher, GuestMemory.R32(fid), 0u, 0u, 0u, GuestMemory.R32(vp), new Ptr<uint>(fp)));
        return (GuestMemory.R32(vp), GuestMemory.R32(fp), patcher);
    }

    private void Finish() => Assert.Equal(0, Call(Gxm, "sceGxmFinish", 0u));

    /// <summary>An enabled U8U8U8U8 colour surface of <paramref name="width"/> x <paramref name="height"/> over its own
    /// memory: what a scene needs at least when it has no depth/stencil surface.</summary>
    private uint SceneColor(uint width, uint height)
    {
        uint surface = _harness.Alloc(0x40);
        Assert.Equal(0, Call(Gxm, "sceGxmColorSurfaceInit", surface, 0u, 0u, 0u, 0u, width, height, width, _harness.Alloc(width * height * 4)));
        return surface;
    }

    [Fact]
    public void InitializeAndTerminate()
    {
        Assert.Equal(UNINITIALIZED, Call(Gxm, "sceGxmGetDisplayQueueThreadIdInternal"));
        Assert.Equal(UNINITIALIZED, Call(Gxm, "sceGxmInitializedInternal"));
        Assert.Equal(UNINITIALIZED, Call(Gxm, "sceGxmCheckMemoryInternal", 0u, 0u, 0u));
        Assert.Equal(UNINITIALIZED, Call(Gxm, "sceGxmDisplayQueueAddEntry", 1u, 2u, 0u));
        Assert.Equal(INVALID_POINTER, Call(Gxm, "sceGxmInitialize", 0u));
        Init();
        Assert.Equal(ALREADY_INITIALIZED, Call(Gxm, "sceGxmInitialize", _harness.Alloc(0x14)));
        Assert.Equal(0, Call(Gxm, "sceGxmInitializedInternal"));
        Assert.True(Call(Gxm, "sceGxmGetDisplayQueueThreadIdInternal") > 0);
        Assert.NotEqual(0u, (uint)Invoke(Gxm, "sceGxmGetNotificationRegion")!);
        uint t = Out();
        Assert.Equal(0, Call(Gxm, "sceGxmGetParameterBufferThreshold", new Ptr<uint>(t)));
        Assert.Equal(0x200000u, GuestMemory.R32(t));
        Assert.Equal(INVALID_POINTER, Call(Gxm, "sceGxmGetParameterBufferThreshold", new Ptr<uint>(0)));
        // mapping: the checked range must lie in mapped memory
        uint mem = _harness.Alloc(0x10000);
        Assert.Equal(INVALID_POINTER, Call(Gxm, "sceGxmMapMemory", 0u, 0x1000u, 0u));
        Assert.Equal(0, Call(Gxm, "sceGxmMapMemory", mem, 0x10000u, 3u));
        Assert.Equal(0, Call(Gxm, "sceGxmCheckMemoryInternal", mem + 0x100, 0x100u, 0u));
        Assert.Equal(unchecked((int)0x805B0017), Call(Gxm, "sceGxmCheckMemoryInternal", mem + 0xFF00, 0x200u, 0u));
        Assert.Equal(0, Call(Gxm, "sceGxmUnmapMemory", mem));
        Assert.Equal(INVALID_VALUE, Call(Gxm, "sceGxmUnmapMemory", mem));
        uint off = Out();
        Assert.Equal(0, Call(Gxm, "sceGxmMapVertexUsseMemory", mem, 0x1000u, new Ptr<uint>(off)));
        Assert.Equal(mem, GuestMemory.R32(off));
        Assert.Equal(0, Call(Gxm, "sceGxmMapFragmentUsseMemoryInternal", mem, 0x1000u, new Ptr<uint>(off)));
        Assert.Equal(INVALID_POINTER, Call(Gxm, "sceGxmMapVertexUsseMemory", 0u, 0x1000u, new Ptr<uint>(off)));
        Assert.Equal(INVALID_POINTER, Call(Gxm, "sceGxmMapFragmentUsseMemory", mem, 0x1000u, new Ptr<uint>(0)));
        Assert.Equal(0, Call(Gxm, "sceGxmUnmapVertexUsseMemory", mem));
        Assert.Equal(0, Call(Gxm, "sceGxmUnmapFragmentUsseMemory", mem));
        foreach (string? fn in new[] { "sceGxmIsDebugVersion", "sceGxmIsInitializationInternal", "sceGxmPadTriggerGpuPaTrace" })
        {
            Assert.Equal(0, Call(Gxm, fn));
        }

        Assert.Equal(0, Call(Gxm, "sceGxmSetWarningEnabled", 1u, 0u));
        Assert.Equal(0, Call(Gxm, "sceGxmPadHeartbeat", 0u, 0u));
        Assert.Equal(INVALID_POINTER, Call(Gxm, "sceGxmAddRazorGpuCaptureBuffer", 0u, 0u));
        Assert.Equal(0, Call(Gxm, "sceGxmAddRazorGpuCaptureBuffer", mem, 0x100u));
        Assert.Equal(0, Call(Gxm, "sceGxmRemoveRazorGpuCaptureBuffer", mem));
        Assert.Equal(INVALID_POINTER, Call(Gxm, "sceGxmRemoveRazorGpuCaptureBuffer", 0u));
        Invoke(Gxm, "sceGxmSetCallbackInternal", 0x10005, mem);
        Invoke(Gxm, "sceGxmSetInitializeParamInternal", 0x10004, mem);
        Assert.Equal(0, Call(Gxm, "sceGxmTerminate"));
        Assert.Equal(UNINITIALIZED, Call(Gxm, "sceGxmInitializedInternal"));
    }

    [Fact]
    public void TopContextIsTheDeviceDataOnceInitialized()
    {
        // libgxm 3.74 hands out the address of its device data while it is initialized, NULL otherwise
        Assert.Equal(0u, (uint)Invoke(Gxm, "sceGxmGetTopContextInternal")!);
        Init();
        uint top = (uint)Invoke(Gxm, "sceGxmGetTopContextInternal")!;
        Assert.NotEqual(0u, top);
        Assert.Equal(top, (uint)Invoke(Gxm, "sceGxmGetTopContextInternal")!);
        Assert.Equal(0, Call(Gxm, "sceGxmTerminate"));
        Assert.Equal(0u, (uint)Invoke(Gxm, "sceGxmGetTopContextInternal")!);
        // the same static block after a new initialize
        Init();
        Assert.Equal(top, (uint)Invoke(Gxm, "sceGxmGetTopContextInternal")!);
        Assert.Equal(0, Call(Gxm, "sceGxmTerminate"));
    }

    [Fact]
    public void FragmentProgramForGlesChecksTheRemapAndSharesThePlainProgram()
    {
        uint pp = _harness.Alloc(0x40), o = Out();
        Assert.Equal(0, Call(Gxm, "sceGxmShaderPatcherCreate", pp, new Ptr<uint>(o)));
        uint patcher = GuestMemory.R32(o);
        uint fid = Out(), plain = Out(), gles = Out();
        Assert.Equal(0, Call(Gxm, "sceGxmShaderPatcherRegisterProgram", patcher, Program(true), new Ptr<uint>(fid)));
        uint id = GuestMemory.R32(fid);
        const string ForGles = "sceGxmShaderPatcherCreateFragmentProgramForGles";
        Assert.Equal(INVALID_POINTER, Call(Gxm, ForGles, 0u, id, 0u, 0u, 0u, 0u, 0u, new Ptr<uint>(gles)));
        Assert.Equal(INVALID_POINTER, Call(Gxm, ForGles, patcher, 0u, 0u, 0u, 0u, 0u, 0u, new Ptr<uint>(gles)));
        Assert.Equal(INVALID_POINTER, Call(Gxm, ForGles, patcher, id, 0u, 0u, 0u, 0u, 0u, new Ptr<uint>(0)));
        Assert.Equal(INVALID_VALUE, Call(Gxm, ForGles, patcher, id, 9u, 0u, 0u, 0u, 0u, new Ptr<uint>(gles)));
        Assert.Equal(INVALID_VALUE, Call(Gxm, ForGles, patcher, id, 0u, 3u, 0u, 0u, 0u, new Ptr<uint>(gles)));
        // no remap: the program sceGxmShaderPatcherCreateFragmentProgram gives (the patcher's cache)
        Assert.Equal(0, Call(Gxm, "sceGxmShaderPatcherCreateFragmentProgram", patcher, id, 0u, 0u, 0u, 0u, new Ptr<uint>(plain)));
        Assert.Equal(0, Call(Gxm, ForGles, patcher, id, 0u, 0u, 0u, 0u, 0u, new Ptr<uint>(gles)));
        Assert.Equal(GuestMemory.R32(plain), GuestMemory.R32(gles));
        // the identity remap is no remap
        uint remap = _harness.Alloc(16);
        for (uint i = 0; i < 10; i++)
        {
            GuestMemory.Write(remap + i, (byte)i);
        }

        Assert.Equal(0, Call(Gxm, ForGles, patcher, id, 0u, 0u, 0u, 0u, remap, new Ptr<uint>(gles)));
        Assert.Equal(GuestMemory.R32(plain), GuestMemory.R32(gles));
        // an entry of 10 or more is refused
        GuestMemory.Write(remap + 9, (byte)10);
        Assert.Equal(INVALID_VALUE, Call(Gxm, ForGles, patcher, id, 0u, 0u, 0u, 0u, remap, new Ptr<uint>(gles)));
        // another remap: its own program, carrying the table
        GuestMemory.Write(remap, (byte)1);
        GuestMemory.Write(remap + 1, (byte)0);
        GuestMemory.Write(remap + 9, (byte)9);
        Assert.Equal(0, Call(Gxm, ForGles, patcher, id, 0u, 0u, 0u, 0u, remap, new Ptr<uint>(gles)));
        Assert.NotEqual(GuestMemory.R32(plain), GuestMemory.R32(gles));
        var fragmentPrograms = (System.Collections.Concurrent.ConcurrentDictionary<uint, VitaSharp.Core.Gxm.GxmFragmentProgram>)typeof(Gxm)
            .GetField("_fragmentPrograms", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(Gxm)!;
        ulong packed = fragmentPrograms[GuestMemory.R32(gles)].TexcoordRemap;
        Assert.Equal(GxmTexcoordRemap.Pack([1, 0, 2, 3, 4, 5, 6, 7, 8, 9]), packed);
        Assert.Equal(1, GxmTexcoordRemap.Source(packed, 0));
        Assert.Equal(0, GxmTexcoordRemap.Source(packed, 1));
        Assert.Equal(9, GxmTexcoordRemap.Source(packed, 9));
        Assert.Equal(0ul, fragmentPrograms[GuestMemory.R32(plain)].TexcoordRemap);
        // every entry 0 is a remap too (not the identity's packed 0)
        for (uint i = 0; i < 10; i++)
        {
            GuestMemory.Write(remap + i, (byte)0);
        }

        uint zeros = Out();
        Assert.Equal(0, Call(Gxm, ForGles, patcher, id, 0u, 0u, 0u, 0u, remap, new Ptr<uint>(zeros)));
        Assert.NotEqual(0ul, fragmentPrograms[GuestMemory.R32(zeros)].TexcoordRemap);
        Assert.Equal(0, GxmTexcoordRemap.Source(fragmentPrograms[GuestMemory.R32(zeros)].TexcoordRemap, 7));
        // a save state keeps each program's remap (the programs are rebuilt by the load)
        var writer = new VitaSharp.Core.SaveState.SnapshotWriter();
        ((VitaSharp.Core.SaveState.ISnapshotState)Gxm).SaveState(writer);
        ((VitaSharp.Core.SaveState.ISnapshotState)Gxm).LoadState(new VitaSharp.Core.SaveState.SnapshotReader(writer.Data.ToArray()));
        Assert.Equal(packed, fragmentPrograms[GuestMemory.R32(gles)].TexcoordRemap);
        Assert.Equal(0ul, fragmentPrograms[GuestMemory.R32(plain)].TexcoordRemap);
        Assert.NotEqual(0ul, fragmentPrograms[GuestMemory.R32(zeros)].TexcoordRemap);
        // a released program's remap goes with it
        Assert.Equal(0, Call(Gxm, "sceGxmShaderPatcherReleaseFragmentProgram", patcher, GuestMemory.R32(zeros)));
        Assert.False(fragmentPrograms.ContainsKey(GuestMemory.R32(zeros)));
        var remaps = (ConcurrentDictionary<uint, ulong>)typeof(Gxm)
            .GetField("_texcoordRemaps", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(Gxm)!;
        Assert.False(remaps.ContainsKey(GuestMemory.R32(zeros)));
        Assert.True(remaps.ContainsKey(GuestMemory.R32(gles)));
    }

    [Fact]
    public void ContextsTargetsAndSyncObjects()
    {
        Init();
        Assert.Equal(INVALID_POINTER, Call(Gxm, "sceGxmCreateContext", 0u, new Ptr<uint>(Out())));
        uint ctx = Context(), type = _harness.Word(9);
        Assert.Equal(0, Call(Gxm, "sceGxmGetContextType", ctx, new Ptr<uint>(type)));
        Assert.Equal(0u, GuestMemory.R32(type)); // IMMEDIATE
        Assert.Equal(INVALID_POINTER, Call(Gxm, "sceGxmGetContextType", 0x1234u, new Ptr<uint>(type)));
        uint size = Out();
        Assert.Equal(0, Call(Gxm, "sceGxmGetRenderTargetMemSize", _harness.Alloc(0x14), new Ptr<uint>(size)));
        Assert.True(GuestMemory.R32(size) > 0);
        Assert.Equal(INVALID_POINTER, Call(Gxm, "sceGxmGetRenderTargetMemSize", 0u, new Ptr<uint>(size)));
        Assert.Equal(INVALID_POINTER, Call(Gxm, "sceGxmCreateRenderTarget", 0u, new Ptr<uint>(size)));
        uint rt = RenderTarget(960, 544), blk = Out();
        Assert.Equal(0, Call(Gxm, "sceGxmRenderTargetGetDriverMemBlock", rt, new Ptr<int>(blk)));
        Assert.Equal(0x1234u, GuestMemory.R32(blk));
        Assert.Equal(INVALID_POINTER, Call(Gxm, "sceGxmRenderTargetGetDriverMemBlock", 0x1234u, new Ptr<int>(blk)));
        Assert.Equal(0, Call(Gxm, "sceGxmDestroyRenderTarget", rt));

        uint so = Out();
        Assert.Equal(INVALID_POINTER, Call(Gxm, "sceGxmSyncObjectCreate", new Ptr<uint>(0)));
        Assert.Equal(0, Call(Gxm, "sceGxmSyncObjectCreate", new Ptr<uint>(so)));
        uint sync = GuestMemory.R32(so);
        Assert.NotEqual(0u, sync);
        // the vsh sync objects shared by key: open finds what create registered; an unknown key gets a new one
        Assert.Equal(INVALID_POINTER, Call(Gxm, "sceGxmVshSyncObjectCreate", 1u, 0u));
        Assert.Equal(0, Call(Gxm, "sceGxmVshSyncObjectCreate", 1u, sync));
        uint opened = Out();
        Assert.Equal(0, Call(Gxm, "sceGxmVshSyncObjectOpen", 1u, new Ptr<uint>(opened)));
        Assert.Equal(sync, GuestMemory.R32(opened));
        Assert.Equal(0, Call(Gxm, "sceGxmVshSyncObjectOpen", 2u, new Ptr<uint>(opened)));
        Assert.NotEqual(sync, GuestMemory.R32(opened));
        Assert.Equal(INVALID_POINTER, Call(Gxm, "sceGxmVshSyncObjectOpen", 2u, new Ptr<uint>(0)));
        Assert.Equal(0, Call(Gxm, "sceGxmVshSyncObjectClose", 1u, sync));
        Assert.Equal(0, Call(Gxm, "sceGxmVshSyncObjectDestroy", 1u));
        Assert.Equal(0, Call(Gxm, "sceGxmSyncObjectDestroy", sync));

        // markers need a context
        Assert.Equal(INVALID_POINTER, Call(Gxm, "sceGxmPushUserMarker", 0x1234u, 0u));
        Assert.Equal(0, Call(Gxm, "sceGxmPushUserMarker", ctx, 0u));
        Assert.Equal(0, Call(Gxm, "sceGxmPopUserMarker", ctx));
        Assert.Equal(0, Call(Gxm, "sceGxmPopUserMarker", ctx)); // nothing pushed: still OK
        Assert.Equal(INVALID_POINTER, Call(Gxm, "sceGxmPopUserMarker", 0x1234u));
        Assert.Equal(0, Call(Gxm, "sceGxmSetUserMarker", ctx, 0u));
        Assert.Equal(INVALID_POINTER, Call(Gxm, "sceGxmSetUserMarker", 0x1234u, 0u));
        Assert.Equal(0, Call(Gxm, "sceGxmDestroyContext", ctx));
    }

    [Fact]
    public void SceneLifecycleAndNotifications()
    {
        Init();
        uint ctx = Context(), rt = RenderTarget(960, 544);
        uint color = _harness.Alloc(0x40), data = _harness.Alloc(960 * 544 * 4);
        Assert.Equal(0, Call(Gxm, "sceGxmColorSurfaceInit", color, 0u, 0u, 0u, 0u, 960u, 544u, 960u, data));
        uint depth = _harness.Alloc(0x20);
        Assert.Equal(0, Call(Gxm, "sceGxmDepthStencilSurfaceInit", depth, 0x00044000u, 0u, 960u, _harness.Alloc(960 * 544 * 4), 0u));

        Assert.Equal(NOT_WITHIN_SCENE, Call(Gxm, "sceGxmEndScene", ctx, 0u, 0u));
        Assert.Equal(NOT_WITHIN_SCENE, Call(Gxm, "sceGxmVertexFence", ctx));
        Assert.False((bool)Invoke(Gxm, "sceGxmRenderingContextIsWithinSceneInternal")!);
        // a render target the GPU driver does not know (libgxm 3.74): SCE_GXM_ERROR_DRIVER
        Assert.Equal(DRIVER, Call(Gxm, "sceGxmBeginScene", ctx, 0u, 0x1234u, 0u, 0u, 0u, color, depth));
        Assert.Equal(INVALID_POINTER, Call(Gxm, "sceGxmBeginScene", 0x1234u, 0u, rt, 0u, 0u, 0u, color, depth));
        Assert.Equal(0, Call(Gxm, "sceGxmBeginScene", ctx, 0u, rt, 0u, 0u, 0u, color, depth));
        Assert.True((bool)Invoke(Gxm, "sceGxmRenderingContextIsWithinSceneInternal")!);
        Assert.Equal(WITHIN_SCENE, Call(Gxm, "sceGxmBeginScene", ctx, 0u, rt, 0u, 0u, 0u, color, depth));
        Assert.Equal(0, Call(Gxm, "sceGxmVertexFence", ctx));
        Assert.Equal(0, Call(Gxm, "sceGxmMidSceneFlush", ctx, 0u, 0u, 0u));
        // fragment notification: written by the GPU once the scene is done
        uint word = _harness.Word(0), notif = _harness.Alloc(8);
        GuestMemory.W32(notif, word);
        GuestMemory.W32(notif + 4, 0x77);
        Assert.Equal(0, Call(Gxm, "sceGxmEndScene", ctx, 0u, notif));
        Finish();
        Assert.Equal(0x77u, GuestMemory.R32(word));
        Assert.Equal(0, Call(Gxm, "sceGxmNotificationWait", notif)); // already reached: returns at once
        Assert.Equal(INVALID_POINTER, Call(Gxm, "sceGxmNotificationWait", 0u));
        BeginSceneCommand begin = _rec.LastBegin!;
        Assert.Equal((960u, 544u), (begin.Width, begin.Height));
        Assert.Equal(data, begin.Color!.Data);
        Assert.Equal(960u, begin.Depth!.StrideInSamples);
        Assert.Contains("MidSceneFlushCommand", _rec.Kinds);
        Assert.Contains("EndSceneCommand", _rec.Kinds);
        Assert.Contains("FinishCommand", _rec.Kinds);

        // the old SDK's entry carries the valid region at +8; a disabled colour surface renders without colour
        uint region = _harness.Alloc(16), off = _harness.Alloc(0x40);
        GuestMemory.W32(region + 8, 479);
        GuestMemory.W32(region + 12, 271);
        Assert.Equal(0, Call(Gxm, "sceGxmColorSurfaceInitDisabled", off));
        Assert.False((bool)Invoke(Gxm, "sceGxmColorSurfaceIsEnabled", off)!);
        Assert.Equal(0, Call(Gxm, "_sceGxmBeginScene", ctx, 0u, rt, region, 0u, 0u, off, depth));
        Assert.Equal(0, Call(Gxm, "sceGxmEndScene", ctx, 0u, 0u));
        Finish();
        Assert.Null(_rec.LastBegin!.Color);
        // libgxm rejects a load surface without a store surface; the same surface as both is a plain scene
        Assert.Equal(INVALID_VALUE, Call(Gxm, "sceGxmBeginSceneEx", ctx, 0u, rt, 0u, 0u, 0u, color, depth, 0u));
        Assert.Equal(0, Call(Gxm, "sceGxmBeginSceneEx", ctx, 0u, rt, 0u, 0u, 0u, color, depth, depth));
        Assert.Equal(0, Call(Gxm, "sceGxmEndScene", ctx, 0u, 0u));
        Assert.Equal(0, Call(Gxm, "sceGxmTransferFinish"));
        Assert.NotNull(_rec.LastBegin!.Color);
    }

    /// <summary>
    /// libgxm (FW 3.74) refuses a scene with nothing to render to: in sceGxmBeginScene, sceGxmBeginSceneEx and the old
    /// _sceGxmBeginScene, a NULL, disabled or never initialised (zero-filled) colour surface together with a NULL or
    /// disabled depth/stencil surface is SCE_GXM_ERROR_INVALID_VALUE, and the context stays outside a scene (nothing
    /// reaches the GPU). A colour surface alone or a depth/stencil surface alone is a scene.
    /// </summary>
    [Fact]
    public void BeginSceneRefusesASceneWithoutColourAndDepth()
    {
        Init();
        uint ctx = Context(), rt = RenderTarget(64, 64), color = SceneColor(64, 64);
        uint offColor = _harness.Alloc(0x40), zeroColor = _harness.Alloc(0x40), offDepth = _harness.Alloc(0x20);
        uint depth = _harness.Alloc(0x20);
        Assert.Equal(0, Call(Gxm, "sceGxmColorSurfaceInitDisabled", offColor));
        Assert.Equal(0, Call(Gxm, "sceGxmDepthStencilSurfaceInitDisabled", offDepth));
        Assert.Equal(0, Call(Gxm, "sceGxmDepthStencilSurfaceInit", depth, 0x00044000u, 0u, 64u, _harness.Alloc(64 * 64 * 4), 0u));

        foreach (uint colorSurface in new[] { 0u, offColor, zeroColor })
        {
            foreach (uint depthSurface in new[] { 0u, offDepth })
            {
                Assert.Equal(INVALID_VALUE, Call(Gxm, "sceGxmBeginScene", ctx, 0u, rt, 0u, 0u, 0u, colorSurface, depthSurface));
                Assert.Equal(INVALID_VALUE, Call(Gxm, "_sceGxmBeginScene", ctx, 0u, rt, 0u, 0u, 0u, colorSurface, depthSurface));
                Assert.Equal(INVALID_VALUE, Call(Gxm, "sceGxmBeginSceneEx", ctx, 0u, rt, 0u, 0u, 0u, colorSurface, depthSurface, depthSurface));
            }
        }

        Assert.Equal(NOT_WITHIN_SCENE, Call(Gxm, "sceGxmEndScene", ctx, 0u, 0u));
        Finish();
        Assert.DoesNotContain("BeginSceneCommand", _rec.Kinds);

        Assert.Equal(0, Call(Gxm, "sceGxmBeginScene", ctx, 0u, rt, 0u, 0u, 0u, color, offDepth));
        Assert.Equal(0, Call(Gxm, "sceGxmEndScene", ctx, 0u, 0u));
        Finish();
        Assert.NotNull(_rec.LastBegin!.Color);
        Assert.Null(_rec.LastBegin!.Depth);
        Assert.Equal(0, Call(Gxm, "sceGxmBeginSceneEx", ctx, 0u, rt, 0u, 0u, 0u, offColor, depth, depth));
        Assert.Equal(0, Call(Gxm, "sceGxmEndScene", ctx, 0u, 0u));
        Finish();
        Assert.Null(_rec.LastBegin!.Color);
        Assert.NotNull(_rec.LastBegin!.Depth);
        Assert.Equal(2, _rec.Kinds.Count(kind => kind == "BeginSceneCommand"));
    }

    /// <summary>
    /// sceGxmBeginScene, sceGxmBeginSceneEx and the old _sceGxmBeginScene (libgxm, FW 3.74) check their arguments in
    /// this order: NULL context INVALID_POINTER; deferred context or scene flags above bit 3 INVALID_VALUE (the old entry
    /// does not check flags); NULL render target or a vertex sync object INVALID_POINTER; already in a scene
    /// WITHIN_SCENE; surfaces INVALID_VALUE; and only then a render target the GPU driver does not know,
    /// SCE_GXM_ERROR_DRIVER. No refused call opens a scene or reaches the GPU.
    /// </summary>
    [Fact]
    public void BeginSceneChecksItsArgumentsInLibgxmOrder()
    {
        Init();
        uint ctx = Context(), rt = RenderTarget(64, 64), color = SceneColor(64, 64);
        uint vertexSync = _harness.Alloc(16), unknownTarget = _harness.Alloc(0x40);
        string[] entries = ["sceGxmBeginScene", "_sceGxmBeginScene", "sceGxmBeginSceneEx"];

        int Begin(string entry, uint flags, uint target, uint sync, uint colorSurface)
            => entry == "sceGxmBeginSceneEx"
                ? Call(Gxm, entry, ctx, flags, target, 0u, sync, 0u, colorSurface, 0u, 0u)
                : Call(Gxm, entry, ctx, flags, target, 0u, sync, 0u, colorSurface, 0u);

        foreach (string entry in entries)
        {
            Assert.Equal(INVALID_POINTER, Begin(entry, 0u, 0u, 0u, color));
            Assert.Equal(INVALID_POINTER, Begin(entry, 0u, rt, vertexSync, color));
            Assert.Equal(DRIVER, Begin(entry, 0u, unknownTarget, 0u, color));
            // the surfaces are checked before the driver looks the render target up
            Assert.Equal(INVALID_VALUE, Begin(entry, 0u, unknownTarget, 0u, 0u));
            Assert.Equal(entry == "_sceGxmBeginScene" ? 0 : INVALID_VALUE, Begin(entry, 0x10u, rt, 0u, color));
            if (entry == "_sceGxmBeginScene")
            {
                // inside a scene: WITHIN_SCENE comes after the render target and sync checks, before the surfaces
                Assert.Equal(INVALID_POINTER, Begin(entry, 0u, rt, vertexSync, color));
                Assert.Equal(WITHIN_SCENE, Begin(entry, 0u, rt, 0u, 0u));
                Assert.Equal(WITHIN_SCENE, Call(Gxm, "sceGxmBeginScene", ctx, 0u, rt, 0u, 0u, 0u, color, 0u));
                // the scene flags are checked before it
                Assert.Equal(INVALID_VALUE, Call(Gxm, "sceGxmBeginScene", ctx, 0x10u, rt, 0u, 0u, 0u, color, 0u));
                Assert.Equal(0, Call(Gxm, "sceGxmEndScene", ctx, 0u, 0u));
            }
        }

        Finish();
        Assert.Equal(1, _rec.Kinds.Count(kind => kind == "BeginSceneCommand"));
        foreach (string entry in entries)
        {
            Assert.Equal(0, Begin(entry, 0xFu, rt, 0u, color));
            Assert.Equal(0, Call(Gxm, "sceGxmEndScene", ctx, 0u, 0u));
        }

        Finish();
        Assert.Equal(4, _rec.Kinds.Count(kind => kind == "BeginSceneCommand"));
    }

    /// <summary>
    /// What sceGxmBeginScene, sceGxmBeginSceneEx and _sceGxmBeginScene (libgxm, FW 3.74) check once the driver gave the
    /// render target, in this order: a 2X multisample target needs a colour surface with the MSAA downscale mode
    /// (UNSUPPORTED, before the region); the valid region { xMax, yMax } inside the target; the colour surface covering
    /// the region in target pixels (a multisampled surface without downscale counts its size over the sample grid, a
    /// downscaled one without multisampling twice its size); the depth/stencil stride covering the region's width
    /// rounded up to 32, times the horizontal samples (before SDK 2.00 the width itself). Refused scenes stay closed.
    /// </summary>
    [Fact]
    public void BeginSceneChecksTheSurfacesAgainstTheRenderTarget()
    {
        Init();
        uint ctx = Context();
        uint region = _harness.Alloc(0x20);
        string[] entries = ["sceGxmBeginScene", "_sceGxmBeginScene", "sceGxmBeginSceneEx"];

        uint Color(uint width, uint height, uint scaleMode)
        {
            uint surface = _harness.Alloc(0x40), stride = (width + 1) & ~1u;
            Assert.Equal(0, Call(Gxm, "sceGxmColorSurfaceInit", surface, 0u, 0u, scaleMode, 0u, width, height, stride, _harness.Alloc(stride * height * 4)));
            return surface;
        }

        uint Depth(uint strideInSamples)
        {
            uint surface = _harness.Alloc(0x20);
            Assert.Equal(0, Call(Gxm, "sceGxmDepthStencilSurfaceInit", surface, 0x00044000u, 0u, strideInSamples, _harness.Alloc(strideInSamples * 256 * 4), 0u));
            return surface;
        }

        // the old entry's region struct carries { xMax, yMax } at +8
        int Begin(string entry, uint target, uint xMax, uint yMax, uint colorSurface, uint depthSurface)
        {
            GuestMemory.W32(region, xMax);
            GuestMemory.W32(region + 4, yMax);
            GuestMemory.W32(region + 8, xMax);
            GuestMemory.W32(region + 12, yMax);
            uint regionArgument = xMax == uint.MaxValue ? 0u : region;
            int result = entry switch
            {
                "sceGxmBeginSceneEx" => Call(Gxm, entry, ctx, 0u, target, regionArgument, 0u, 0u, colorSurface, depthSurface, depthSurface),
                _ => Call(Gxm, entry, ctx, 0u, target, regionArgument, 0u, 0u, colorSurface, depthSurface),
            };
            if (result == 0)
            {
                Assert.Equal(0, Call(Gxm, "sceGxmEndScene", ctx, 0u, 0u));
            }

            return result;
        }

        const uint Whole = uint.MaxValue;
        const uint None = 0, Downscale = 1;
        uint rt = RenderTarget(96, 64), rt2x = RenderTarget(96, 64, 1), rt4x = RenderTarget(96, 64, 2);
        foreach (string entry in entries)
        {
            // the region lies inside the target
            Assert.Equal(0, Begin(entry, rt, 95, 63, Color(96, 64, None), 0u));
            Assert.Equal(INVALID_VALUE, Begin(entry, rt, 96, 63, Color(128, 128, None), 0u));
            Assert.Equal(INVALID_VALUE, Begin(entry, rt, 95, 64, Color(128, 128, None), 0u));
            // the colour surface covers it; with downscale and no multisampling, twice its size does
            Assert.Equal(0, Begin(entry, rt, 31, 15, Color(32, 16, None), 0u));
            Assert.Equal(INVALID_VALUE, Begin(entry, rt, Whole, 0, Color(95, 64, None), 0u));
            Assert.Equal(INVALID_VALUE, Begin(entry, rt, 32, 15, Color(32, 16, None), 0u));
            Assert.Equal(INVALID_VALUE, Begin(entry, rt, 31, 16, Color(32, 16, None), 0u));
            Assert.Equal(0, Begin(entry, rt, Whole, 0, Color(48, 32, Downscale), 0u));
            Assert.Equal(INVALID_VALUE, Begin(entry, rt, Whole, 0, Color(47, 32, Downscale), 0u));

            // 2X (one sample across, two down) takes only a downscaled colour surface, before the region is looked at
            Assert.Equal(UNSUPPORTED, Begin(entry, rt2x, Whole, 0, Color(96, 128, None), 0u));
            Assert.Equal(UNSUPPORTED, Begin(entry, rt2x, 200, 63, Color(96, 128, None), 0u));
            Assert.Equal(0, Begin(entry, rt2x, Whole, 0, Color(96, 64, Downscale), 0u));
            Assert.Equal(INVALID_VALUE, Begin(entry, rt2x, Whole, 0, Color(96, 63, Downscale), 0u));
            // 4X without downscale: a surface at the sample grid (two by two)
            Assert.Equal(0, Begin(entry, rt4x, Whole, 0, Color(192, 128, None), 0u));
            Assert.Equal(INVALID_VALUE, Begin(entry, rt4x, Whole, 0, Color(190, 128, None), 0u));
            Assert.Equal(0, Begin(entry, rt4x, Whole, 0, Color(96, 64, Downscale), 0u));

            // the depth/stencil stride covers the region's width rounded up to 32 samples, times the samples across;
            // with no colour surface the 2X rule does not apply
            Assert.Equal(0, Begin(entry, rt, Whole, 0, 0u, Depth(96)));
            Assert.Equal(INVALID_VALUE, Begin(entry, rt, Whole, 0, 0u, Depth(64)));
            Assert.Equal(0, Begin(entry, rt, 64, 63, 0u, Depth(96)));
            Assert.Equal(INVALID_VALUE, Begin(entry, rt, 64, 63, 0u, Depth(64)));
            Assert.Equal(0, Begin(entry, rt, 63, 63, 0u, Depth(64)));
            Assert.Equal(0, Begin(entry, rt2x, Whole, 0, 0u, Depth(96)));
            Assert.Equal(INVALID_VALUE, Begin(entry, rt4x, Whole, 0, 0u, Depth(160)));
            Assert.Equal(0, Begin(entry, rt4x, Whole, 0, 0u, Depth(192)));
            // the colour is checked before the depth/stencil
            Assert.Equal(INVALID_VALUE, Begin(entry, rt, Whole, 0, Color(32, 32, None), Depth(96)));
        }

        Assert.Equal(NOT_WITHIN_SCENE, Call(Gxm, "sceGxmEndScene", ctx, 0u, 0u));

        // before SDK 2.00 the depth/stencil stride only has to cover the region's width (without rounding it up to 32):
        // with two samples across, 65 pixels need 130 samples instead of 192
        Assert.Equal(INVALID_VALUE, Begin("sceGxmBeginScene", rt4x, 64, 63, 0u, Depth(160)));
        Assert.Equal(0, Begin("sceGxmBeginScene", rt4x, 64, 63, 0u, Depth(192)));
        SetMainModuleSdkVersion(0x01FFFFFF);
        Assert.Equal(0, Begin("sceGxmBeginScene", rt4x, 64, 63, 0u, Depth(160)));
        Assert.Equal(INVALID_VALUE, Begin("sceGxmBeginScene", rt4x, 64, 63, 0u, Depth(128)));
        SetMainModuleSdkVersion(0x02000000);
        Assert.Equal(INVALID_VALUE, Begin("sceGxmBeginScene", rt4x, 64, 63, 0u, Depth(160)));
    }

    /// <summary>
    /// sceGxmColorSurfaceSetFormat (libgxm, FW 3.74): NULL INVALID_POINTER; a disabled surface UNSUPPORTED, which before
    /// SDK 3.10 (compatibility bit 0x8000) is a surface without data, from 3.10 on one sceGxmColorSurfaceInit did not
    /// describe (InitDisabled, zeroed memory); an unknown format INVALID_VALUE (0xD0800000 only before SDK 3.10); a
    /// format of another pixel size UNSUPPORTED (bits per pixel, bytes before SDK 1.60: 0x60000000 is 16 bits in 4
    /// bytes); a format a downscaled surface cannot take UNSUPPORTED. The format and the surface's texture change only
    /// when it is taken.
    /// </summary>
    [Theory]
    [InlineData(0x03570011u)]
    [InlineData(0x03100000u)]
    [InlineData(0x030FFFFFu)]
    [InlineData(0x01500011u)]
    public void ColorSurfaceSetFormatFollowsTheFirmwareRules(uint sdkVersion)
    {
        SetMainModuleSdkVersion(sdkVersion);
        bool enabledByData = sdkVersion < 0x03100000;
        bool bytesPerPixel = sdkVersion < 0x01600000;
        uint described = _harness.Alloc(0x40), disabled = _harness.Alloc(0x40), zeroed = _harness.Alloc(0x40);
        uint data = _harness.Alloc(64 * 64 * 4);
        Assert.Equal(0, Call(Gxm, "sceGxmColorSurfaceInit", described, 0x00000000u, 0u, 0u, 0u, 64u, 64u, 64u, 0u));
        Assert.Equal(0, Call(Gxm, "sceGxmColorSurfaceInitDisabled", disabled));

        Assert.Equal(INVALID_POINTER, Call(Gxm, "sceGxmColorSurfaceSetFormat", 0u, 0u));
        Assert.Equal(UNSUPPORTED, Call(Gxm, "sceGxmColorSurfaceSetFormat", disabled, 0x00100000u));
        Assert.Equal(UNSUPPORTED, Call(Gxm, "sceGxmColorSurfaceSetFormat", zeroed, 0x00100000u));
        // described without data: enabled from SDK 3.10 on, disabled before
        Assert.Equal(enabledByData ? UNSUPPORTED : 0, Call(Gxm, "sceGxmColorSurfaceSetFormat", described, 0x00100000u));
        // before 3.10 a surface is enabled by its data, and SetData refuses a disabled one: give it data by hand
        Assert.Equal(enabledByData ? UNSUPPORTED : 0, Call(Gxm, "sceGxmColorSurfaceSetData", described, data));
        GuestMemory.W32(described + 16, data);

        Assert.Equal(0, Call(Gxm, "sceGxmColorSurfaceSetFormat", described, 0x00100000u));
        Assert.Equal(0x00100000u, (uint)Invoke(Gxm, "sceGxmColorSurfaceGetFormat", described)!);
        // the surface's texture follows: U8U8U8U8 with the ARGB swizzle
        Assert.Equal(0x0C001000u, (uint)Invoke(Gxm, "sceGxmTextureGetFormat", described + 32)!);

        // unknown formats; 0xD0800000 (32 bits) only before SDK 3.10
        Assert.Equal(INVALID_VALUE, Call(Gxm, "sceGxmColorSurfaceSetFormat", described, 0x20000000u));
        Assert.Equal(INVALID_VALUE, Call(Gxm, "sceGxmColorSurfaceSetFormat", described, 0x01800000u));
        Assert.Equal(enabledByData ? 0 : INVALID_VALUE, Call(Gxm, "sceGxmColorSurfaceSetFormat", described, 0xD0800000u));
        Assert.Equal(0, Call(Gxm, "sceGxmColorSurfaceSetFormat", described, 0x00000000u));

        // another pixel size: 16 or 64 bits on a 32-bit surface; 0x60000000 is 16 bits, but 4 bytes before SDK 1.60
        Assert.Equal(UNSUPPORTED, Call(Gxm, "sceGxmColorSurfaceSetFormat", described, 0x50000000u));
        Assert.Equal(UNSUPPORTED, Call(Gxm, "sceGxmColorSurfaceSetFormat", described, 0x01000000u));
        Assert.Equal(bytesPerPixel ? 0 : UNSUPPORTED, Call(Gxm, "sceGxmColorSurfaceSetFormat", described, 0x60000000u));
        Assert.Equal(0, Call(Gxm, "sceGxmColorSurfaceSetFormat", described, 0x00000000u));
        Assert.Equal(0x00000000u, (uint)Invoke(Gxm, "sceGxmColorSurfaceGetFormat", described)!);

        // a downscaled surface takes only the formats that can be downscaled (0x10800000 cannot)
        Assert.Equal(0, Call(Gxm, "sceGxmColorSurfaceSetFormat", described, 0x10800000u));
        Assert.Equal(0, Call(Gxm, "sceGxmColorSurfaceSetFormat", described, 0x00000000u));
        Invoke(Gxm, "sceGxmColorSurfaceSetScaleMode", described, 1u);
        Assert.Equal(UNSUPPORTED, Call(Gxm, "sceGxmColorSurfaceSetFormat", described, 0x10800000u));
        Assert.Equal(0, Call(Gxm, "sceGxmColorSurfaceSetFormat", described, 0x00800000u));
        Assert.Equal(0x00800000u, (uint)Invoke(Gxm, "sceGxmColorSurfaceGetFormat", described)!);
    }

    /// <summary>Points the main module's SceProcessParam at an SDK version (+0xC, magic "PSP2" at +4).</summary>
    private void SetMainModuleSdkVersion(uint sdkVersion)
    {
        uint processParam = _harness.Alloc(0x34);
        GuestMemory.W32(processParam, 0x34);
        GuestMemory.W32(processParam + 4, 0x32505350);
        GuestMemory.W32(processParam + 8, 6);
        GuestMemory.W32(processParam + 0xC, sdkVersion);
        typeof(VitaSharp.Core.Loader.Linker).GetProperty("ProcessParam")!.SetValue(_harness.Emu.Linker, processParam);
    }

    /// <summary>
    /// A colour surface described with NULL data (libgxm, FW 3.74). sceGxmColorSurfaceInit takes it (Zero Time Dilemma
    /// does this); from SDK 3.10 on (Zero Time Dilemma is 3.57) the surface is enabled, a scene with it is
    /// INVALID_VALUE (before the depth/stencil checks, also with a valid depth surface) until sceGxmColorSurfaceSetData
    /// gives it memory. Before SDK 3.10 (compatibility bit 0x8000) a surface is enabled by its data: NULL data is a
    /// disabled surface, the scene renders without colour (and with no depth either it is INVALID_VALUE), and SetData on
    /// it is UNSUPPORTED. SetData also refuses a NULL surface (INVALID_POINTER) and unaligned data (INVALID_VALUE).
    /// </summary>
    [Theory]
    [InlineData(0u)]
    [InlineData(0x03100000u)]
    [InlineData(0x03570011u)]
    [InlineData(0x030FFFFFu)]
    [InlineData(0x01500000u)]
    public void SceneColourSurfaceWithoutDataFollowsTheSdkVersion(uint sdkVersion)
    {
        if (sdkVersion != 0)
        {
            SetMainModuleSdkVersion(sdkVersion);
        }

        bool enabledByData = sdkVersion != 0 && sdkVersion < 0x03100000;
        Init();
        uint ctx = Context(), rt = RenderTarget(64, 64);
        uint color = _harness.Alloc(0x40), depth = _harness.Alloc(0x20), data = _harness.Alloc(64 * 64 * 4);
        uint offColor = _harness.Alloc(0x40), zeroColor = _harness.Alloc(0x40);
        Assert.Equal(0, Call(Gxm, "sceGxmColorSurfaceInit", color, 0u, 0u, 0u, 0u, 64u, 64u, 64u, 0u));
        Assert.Equal(0, Call(Gxm, "sceGxmColorSurfaceInitDisabled", offColor));
        Assert.Equal(0, Call(Gxm, "sceGxmDepthStencilSurfaceInit", depth, 0x00044000u, 0u, 64u, _harness.Alloc(64 * 64 * 4), 0u));
        Assert.Equal(!enabledByData, (bool)Invoke(Gxm, "sceGxmColorSurfaceIsEnabled", color)!);
        Assert.False((bool)Invoke(Gxm, "sceGxmColorSurfaceIsEnabled", offColor)!);
        Assert.False((bool)Invoke(Gxm, "sceGxmColorSurfaceIsEnabled", zeroColor)!);

        if (enabledByData)
        {
            Assert.Equal(INVALID_VALUE, Call(Gxm, "sceGxmBeginScene", ctx, 0u, rt, 0u, 0u, 0u, color, 0u));
            Assert.Equal(0, Call(Gxm, "sceGxmBeginScene", ctx, 0u, rt, 0u, 0u, 0u, color, depth));
            Assert.Equal(0, Call(Gxm, "sceGxmEndScene", ctx, 0u, 0u));
            Finish();
            Assert.Null(_rec.LastBegin!.Color);
            Assert.NotNull(_rec.LastBegin!.Depth);
            Assert.Equal(UNSUPPORTED, Call(Gxm, "sceGxmColorSurfaceSetData", color, data));
            Assert.Equal(0u, GuestMemory.R32(color + 16));
            return;
        }

        foreach (uint depthSurface in new[] { 0u, depth })
        {
            Assert.Equal(INVALID_VALUE, Call(Gxm, "sceGxmBeginScene", ctx, 0u, rt, 0u, 0u, 0u, color, depthSurface));
            Assert.Equal(INVALID_VALUE, Call(Gxm, "_sceGxmBeginScene", ctx, 0u, rt, 0u, 0u, 0u, color, depthSurface));
            Assert.Equal(INVALID_VALUE, Call(Gxm, "sceGxmBeginSceneEx", ctx, 0u, rt, 0u, 0u, 0u, color, depthSurface, depthSurface));
        }

        Finish();
        Assert.DoesNotContain("BeginSceneCommand", _rec.Kinds);

        Assert.Equal(INVALID_POINTER, Call(Gxm, "sceGxmColorSurfaceSetData", 0u, data));
        Assert.Equal(UNSUPPORTED, Call(Gxm, "sceGxmColorSurfaceSetData", offColor, data));
        Assert.Equal(INVALID_VALUE, Call(Gxm, "sceGxmColorSurfaceSetData", color, data + 2));
        Assert.Equal(0, Call(Gxm, "sceGxmColorSurfaceSetData", color, data));
        Assert.Equal(0, Call(Gxm, "sceGxmBeginScene", ctx, 0u, rt, 0u, 0u, 0u, color, 0u));
        Assert.Equal(0, Call(Gxm, "sceGxmEndScene", ctx, 0u, 0u));
        Finish();
        Assert.Equal(data, _rec.LastBegin!.Color!.Data);
    }

    /// <summary>
    /// sceGxmMidSceneFlush (libgxm, FW 3.74) only inside a scene of the immediate context: a NULL or unknown context and
    /// a vertex sync object are SCE_GXM_ERROR_INVALID_POINTER, flags other than PRESERVE_DEFAULT_UNIFORM_BUFFERS (1) are
    /// SCE_GXM_ERROR_INVALID_VALUE (the old SDK's _sceGxmMidSceneFlush masks them instead), outside a scene
    /// SCE_GXM_ERROR_NOT_WITHIN_SCENE; a refused flush neither reaches the GPU nor writes its vertex notification.
    /// </summary>
    [Fact]
    public void MidSceneFlushRefusesWhatLibgxmRefuses()
    {
        Init();
        uint ctx = Context(), rt = RenderTarget(64, 64), color = SceneColor(64, 64);
        uint word = _harness.Word(0), notif = _harness.Alloc(8);
        GuestMemory.W32(notif, word);
        GuestMemory.W32(notif + 4, 0x55);

        Assert.Equal(NOT_WITHIN_SCENE, Call(Gxm, "sceGxmMidSceneFlush", ctx, 0u, 0u, notif));
        Assert.Equal(NOT_WITHIN_SCENE, Call(Gxm, "_sceGxmMidSceneFlush", ctx, 0u, 0u, notif));
        Assert.Equal(0, Call(Gxm, "sceGxmBeginScene", ctx, 0u, rt, 0u, 0u, 0u, color, 0u));
        Assert.Equal(INVALID_POINTER, Call(Gxm, "sceGxmMidSceneFlush", 0u, 0u, 0u, notif));
        Assert.Equal(INVALID_POINTER, Call(Gxm, "sceGxmMidSceneFlush", 0x1234u, 0u, 0u, notif));
        Assert.Equal(INVALID_POINTER, Call(Gxm, "sceGxmMidSceneFlush", ctx, 0u, _harness.Alloc(16), notif));
        Assert.Equal(INVALID_VALUE, Call(Gxm, "sceGxmMidSceneFlush", ctx, 2u, 0u, notif));
        Finish();
        Assert.Equal(0u, GuestMemory.R32(word));
        Assert.DoesNotContain("MidSceneFlushCommand", _rec.Kinds);

        Assert.Equal(0, Call(Gxm, "_sceGxmMidSceneFlush", ctx, 2u, 0u, 0u));
        Assert.Equal(0, Call(Gxm, "sceGxmMidSceneFlush", ctx, 1u, 0u, notif));
        Assert.Equal(0, Call(Gxm, "sceGxmEndScene", ctx, 0u, 0u));
        Finish();
        Assert.Equal(0x55u, GuestMemory.R32(word));
        Assert.Equal(2, _rec.Kinds.Count(kind => kind == "MidSceneFlushCommand"));
    }

    /// <summary>
    /// sceGxmBeginSceneEx (libgxm, FW 3.74) merges its two depth/stencil surfaces: the force-load bit, the background
    /// depth and stencil and the load addresses come from the load surface; the force-store bit and the store addresses
    /// from the store surface. Both surfaces enabled or both disabled, the same stride, and the store surface's format
    /// stores what the load surface loads (a stencil loaded must be stored), else SCE_GXM_ERROR_INVALID_VALUE without
    /// beginning the scene.
    /// </summary>
    [Fact]
    public void SceneExTakesTheLoadBitFromTheLoadSurfaceAndTheStoreBitFromTheStoreSurface()
    {
        Init();
        uint ctx = Context(), rt = RenderTarget(64, 64);
        uint color = _harness.Alloc(0x40);
        Assert.Equal(0, Call(Gxm, "sceGxmColorSurfaceInit", color, 0u, 0u, 0u, 0u, 64u, 64u, 64u, _harness.Alloc(64 * 64 * 4)));
        uint load = _harness.Alloc(0x20), store = _harness.Alloc(0x20);
        uint loadDepth = _harness.Alloc(64 * 64 * 4), loadStencil = _harness.Alloc(64 * 64);
        uint storeDepth = _harness.Alloc(64 * 64 * 4), storeStencil = _harness.Alloc(64 * 64);
        const uint S8D24 = 0x01266000;
        Assert.Equal(0, Call(Gxm, "sceGxmDepthStencilSurfaceInit", load, S8D24, 0u, 64u, loadDepth, loadStencil));
        Assert.Equal(0, Call(Gxm, "sceGxmDepthStencilSurfaceInit", store, S8D24, 0u, 64u, storeDepth, storeStencil));
        Invoke(Gxm, "sceGxmDepthStencilSurfaceSetForceLoadMode", load, 2u);
        Invoke(Gxm, "sceGxmDepthStencilSurfaceSetForceStoreMode", store, 4u);
        Invoke(Gxm, "sceGxmDepthStencilSurfaceSetBackgroundDepth", load, 0.25f);
        Invoke(Gxm, "sceGxmDepthStencilSurfaceSetBackgroundStencil", load, 0x11u);
        Invoke(Gxm, "sceGxmDepthStencilSurfaceSetBackgroundDepth", store, 0.75f);
        Invoke(Gxm, "sceGxmDepthStencilSurfaceSetBackgroundStencil", store, 0x22u);

        Assert.Equal(0, Call(Gxm, "sceGxmBeginSceneEx", ctx, 0u, rt, 0u, 0u, 0u, color, load, store));
        Assert.Equal(0, Call(Gxm, "sceGxmEndScene", ctx, 0u, 0u));
        Finish();
        DepthSurfaceInfo scene = _rec.LastBegin!.Depth!;
        Assert.True(scene.ForceLoad, "the load surface asks for the load");
        Assert.True(scene.ForceStore, "the store surface asks for the store");
        Assert.Equal((storeDepth, storeStencil), (scene.DepthData, scene.StencilData));
        Assert.Equal((loadDepth, loadStencil), (scene.LoadDepthData, scene.LoadStencilData));
        Assert.True(scene.LoadsFromOtherSurface);
        Assert.Equal((0.25f, (byte)0x11), (scene.BackgroundDepth, scene.BackgroundStencil));
        Assert.Equal(S8D24, scene.Format);

        // swapped: the store surface's load bit and the load surface's store bit do not count
        Assert.Equal(0, Call(Gxm, "sceGxmBeginSceneEx", ctx, 0u, rt, 0u, 0u, 0u, color, store, load));
        Assert.Equal(0, Call(Gxm, "sceGxmEndScene", ctx, 0u, 0u));
        Finish();
        scene = _rec.LastBegin!.Depth!;
        Assert.False(scene.ForceLoad, "the load surface (without the load bit) does not load");
        Assert.False(scene.ForceStore, "the store surface (without the store bit) does not store");
        Assert.Equal((loadDepth, storeDepth), (scene.DepthData, scene.LoadDepthData));

        // both disabled: a scene without depth/stencil
        uint off = _harness.Alloc(0x20);
        Assert.Equal(0, Call(Gxm, "sceGxmDepthStencilSurfaceInitDisabled", off));
        Assert.Equal(0, Call(Gxm, "sceGxmBeginSceneEx", ctx, 0u, rt, 0u, 0u, 0u, color, off, 0u));
        Assert.Equal(0, Call(Gxm, "sceGxmEndScene", ctx, 0u, 0u));
        Finish();
        Assert.Null(_rec.LastBegin!.Depth);

        // rejected pairs: one side disabled, another stride, a loaded stencil the store surface does not store
        uint otherStride = _harness.Alloc(0x20), depthOnly = _harness.Alloc(0x20);
        Assert.Equal(0, Call(Gxm, "sceGxmDepthStencilSurfaceInit", otherStride, S8D24, 0u, 96u, storeDepth, storeStencil));
        Assert.Equal(0, Call(Gxm, "sceGxmDepthStencilSurfaceInit", depthOnly, 0x00044000u, 0u, 64u, storeDepth, 0u));
        Assert.Equal(INVALID_VALUE, Call(Gxm, "sceGxmBeginSceneEx", ctx, 0u, rt, 0u, 0u, 0u, color, load, off));
        Assert.Equal(INVALID_VALUE, Call(Gxm, "sceGxmBeginSceneEx", ctx, 0u, rt, 0u, 0u, 0u, color, 0u, store));
        Assert.Equal(INVALID_VALUE, Call(Gxm, "sceGxmBeginSceneEx", ctx, 0u, rt, 0u, 0u, 0u, color, load, otherStride));
        Assert.Equal(INVALID_VALUE, Call(Gxm, "sceGxmBeginSceneEx", ctx, 0u, rt, 0u, 0u, 0u, color, load, depthOnly));
        Assert.False((bool)Invoke(Gxm, "sceGxmRenderingContextIsWithinSceneInternal")!);
        // a depth-only load surface into a surface that also stores stencil is fine
        Assert.Equal(0, Call(Gxm, "sceGxmBeginSceneEx", ctx, 0u, rt, 0u, 0u, 0u, color, depthOnly, store));
        Assert.Equal(0, Call(Gxm, "sceGxmEndScene", ctx, 0u, 0u));
        Finish();
    }

    [Fact]
    public void ShaderPatcherProgramsAndDraws()
    {
        Init();
        uint ctx = Context(), rt = RenderTarget(64, 64);
        (uint vp, uint fp, uint patcher) = Programs(vUniforms: 4, fUniforms: 2);
        Assert.Equal(0xABCDu, (uint)Invoke(Gxm, "sceGxmShaderPatcherGetUserData", patcher)!);
        Assert.Equal(0, Call(Gxm, "sceGxmShaderPatcherSetUserData", patcher, 0x1111u));
        Assert.Equal(0x1111u, (uint)Invoke(Gxm, "sceGxmShaderPatcherGetUserData", patcher)!);
        uint prog = (uint)Invoke(Gxm, "sceGxmVertexProgramGetProgram", vp)!;
        Assert.Equal(0, Call(Gxm, "sceGxmProgramCheck", prog));
        Assert.Equal(INVALID_POINTER, Call(Gxm, "sceGxmProgramCheck", 0u));
        Assert.Equal(INVALID_VALUE, Call(Gxm, "sceGxmProgramCheck", _harness.Alloc(16)));
        Assert.Equal(0u, (uint)Invoke(Gxm, "sceGxmProgramGetType", prog)!);   // vertex
        Assert.Equal(16u, (uint)Invoke(Gxm, "sceGxmProgramGetDefaultUniformBufferSize", prog)!);
        Assert.Equal(0x110u, (uint)Invoke(Gxm, "sceGxmProgramGetSize", prog)!);
        uint fprog = (uint)Invoke(Gxm, "sceGxmFragmentProgramGetProgram", fp)!;
        Assert.Equal(1u, (uint)Invoke(Gxm, "sceGxmProgramGetType", fprog)!);
        Assert.True((bool)Invoke(Gxm, "sceGxmFragmentProgramIsEnabled", fp)!);
        Assert.Equal(0u, (uint)Invoke(Gxm, "sceGxmFragmentProgramGetPassType", fp)!); // opaque
        // a vertex program with the same attributes is the cached one, with one more reference
        uint count = Out();
        Assert.Equal(0, Call(Gxm, "sceGxmShaderPatcherAddRefVertexProgram", patcher, vp));
        Assert.Equal(0, Call(Gxm, "sceGxmShaderPatcherGetVertexProgramRefCount", patcher, vp, new Ptr<uint>(count)));
        Assert.Equal(2u, GuestMemory.R32(count));
        Assert.Equal(0, Call(Gxm, "sceGxmShaderPatcherReleaseVertexProgram", patcher, vp));
        Assert.Equal(0, Call(Gxm, "sceGxmShaderPatcherAddRefFragmentProgram", patcher, fp));
        Assert.Equal(0, Call(Gxm, "sceGxmShaderPatcherGetFragmentProgramRefCount", patcher, fp, new Ptr<uint>(count)));
        Assert.Equal(2u, GuestMemory.R32(count));
        Assert.Equal(0, Call(Gxm, "sceGxmShaderPatcherReleaseFragmentProgram", patcher, fp));
        uint mask = Out();
        Assert.Equal(0, Call(Gxm, "sceGxmShaderPatcherCreateMaskUpdateFragmentProgram", patcher, new Ptr<uint>(mask)));
        Assert.Equal(0x06000000u, (uint)Invoke(Gxm, "sceGxmFragmentProgramGetPassType", GuestMemory.R32(mask))!);
        Assert.False((bool)Invoke(Gxm, "sceGxmFragmentProgramIsEnabled", GuestMemory.R32(mask))!);
        Assert.Equal(INVALID_POINTER, Call(Gxm, "sceGxmShaderPatcherRegisterProgram", 0x1234u, prog, new Ptr<uint>(count)));
        Assert.Equal(INVALID_POINTER, Call(Gxm, "sceGxmShaderPatcherCreate", 0u, new Ptr<uint>(count)));

        uint idx = _harness.Alloc(16), verts = _harness.Alloc(36);
        Assert.Equal(NOT_WITHIN_SCENE, Call(Gxm, "sceGxmDraw", ctx, 0u, 0u, idx, 3u));
        Assert.Equal(INVALID_POINTER, Call(Gxm, "sceGxmDraw", 0x1234u, 0u, 0u, idx, 3u));
        Assert.Equal(0, Call(Gxm, "sceGxmBeginScene", ctx, 0u, rt, 0u, 0u, 0u, SceneColor(64, 64), 0u));
        Assert.Equal(NULL_PROGRAM, Call(Gxm, "sceGxmDraw", ctx, 0u, 0u, idx, 3u));
        Invoke(Gxm, "sceGxmSetVertexProgram", ctx, vp);
        Invoke(Gxm, "sceGxmSetFragmentProgram", ctx, fp);
        Assert.Equal(INVALID_POINTER, Call(Gxm, "sceGxmSetVertexStream", ctx, 0u, 0u));
        Assert.Equal(INVALID_VALUE, Call(Gxm, "sceGxmSetVertexStream", ctx, 16u, verts));
        Assert.Equal(0, Call(Gxm, "sceGxmSetVertexStream", ctx, 0u, verts));
        // the default uniform buffer comes from the context's ring, sized by the program
        uint ub = Out();
        Assert.Equal(0, Call(Gxm, "sceGxmReserveVertexDefaultUniformBuffer", ctx, new Ptr<uint>(ub)));
        Assert.NotEqual(0u, GuestMemory.R32(ub));
        Assert.Equal(0, Call(Gxm, "sceGxmReserveFragmentDefaultUniformBuffer", ctx, new Ptr<uint>(ub)));
        Assert.NotEqual(0u, GuestMemory.R32(ub));
        Assert.Equal(INVALID_POINTER, Call(Gxm, "sceGxmReserveVertexDefaultUniformBuffer", ctx, new Ptr<uint>(0)));
        Assert.Equal(INVALID_POINTER, Call(Gxm, "sceGxmDraw", ctx, 0u, 0u, 0u, 3u)); // no index data
        Assert.Equal(0, Call(Gxm, "sceGxmDraw", ctx, 0u, 0u, idx, 3u));
        Assert.Equal(INVALID_VALUE, Call(Gxm, "sceGxmDrawInstanced", ctx, 0u, 0u, idx, 6u, 4u)); // 6 not a multiple of 4
        Assert.Equal(INVALID_VALUE, Call(Gxm, "sceGxmDrawInstanced", ctx, 0u, 0u, idx, 6u, 0u));
        Assert.Equal(0, Call(Gxm, "sceGxmDrawInstanced", ctx, 0u, 0u, idx, 6u, 3u));
        Assert.Equal(0, Call(Gxm, "sceGxmEndScene", ctx, 0u, 0u));
        Finish();
        Assert.Equal(2, _rec.Draws);
        Assert.Equal((idx, 3u, (byte)0, 2u), _rec.LastDraw); // 6 indices, wrap 3: two instances of 3

        Assert.Equal(0, Call(Gxm, "sceGxmShaderPatcherUnregisterProgram", patcher, 0u));
        Assert.Equal(0, Call(Gxm, "sceGxmShaderPatcherForceUnregisterProgram", patcher, 0u));
        Assert.Equal(INVALID_POINTER, Call(Gxm, "sceGxmShaderPatcherUnregisterProgram", 0x1234u, 0u));
        foreach (string? fn in new[] { "sceGxmShaderPatcherGetHostMemAllocated", "sceGxmShaderPatcherGetBufferMemAllocated",
            "sceGxmShaderPatcherGetVertexUsseMemAllocated", "sceGxmShaderPatcherGetFragmentUsseMemAllocated" })
        {
            Assert.Equal(0u, (uint)Invoke(Gxm, fn, patcher)!);
        }

        Assert.Equal(0, Call(Gxm, "sceGxmShaderPatcherDestroy", patcher));
    }

    [Fact]
    public void StateSettersValidateTheirArguments()
    {
        Init();
        uint ctx = Context(), buf = _harness.Alloc(64), tex = _harness.Alloc(16);
        Assert.Equal(0, Call(Gxm, "sceGxmTextureInitLinear", tex, _harness.Alloc(64 * 64 * 4), 0x0C000000u, 64u, 64u, 0u));
        Assert.Equal(INVALID_POINTER, Call(Gxm, "sceGxmSetFragmentTexture", ctx, 0u, 0u));
        Assert.Equal(INVALID_VALUE, Call(Gxm, "sceGxmSetFragmentTexture", ctx, 16u, tex));
        Assert.Equal(0, Call(Gxm, "sceGxmSetFragmentTexture", ctx, 15u, tex));
        Assert.Equal(INVALID_VALUE, Call(Gxm, "sceGxmSetVertexTexture", ctx, 16u, tex));
        Assert.Equal(0, Call(Gxm, "sceGxmSetVertexTexture", ctx, 0u, tex));
        Assert.Equal(INVALID_POINTER, Call(Gxm, "sceGxmSetVertexTexture", 0x1234u, 0u, tex));
        Assert.Equal(INVALID_VALUE, Call(Gxm, "sceGxmSetVertexUniformBuffer", ctx, 14u, buf)); // 0-13 (14 is the default one)
        Assert.Equal(0, Call(Gxm, "sceGxmSetVertexUniformBuffer", ctx, 13u, buf));
        Assert.Equal(INVALID_POINTER, Call(Gxm, "sceGxmSetFragmentUniformBuffer", ctx, 0u, 0u));
        Assert.Equal(INVALID_VALUE, Call(Gxm, "sceGxmSetFragmentUniformBuffer", ctx, 14u, buf));
        Assert.Equal(0, Call(Gxm, "sceGxmSetFragmentUniformBuffer", ctx, 0u, buf));
        Assert.Equal(0, Call(Gxm, "sceGxmSetVertexDefaultUniformBuffer", ctx, buf));
        Assert.Equal(0, Call(Gxm, "sceGxmSetFragmentDefaultUniformBuffer", ctx, buf));
        // no program bound: nothing to reserve
        uint ub = _harness.Word(9);
        Assert.Equal(0, Call(Gxm, "sceGxmReserveFragmentDefaultUniformBuffer", ctx, new Ptr<uint>(ub)));
        Assert.Equal(0u, GuestMemory.R32(ub));
        Assert.Equal(INVALID_POINTER, Call(Gxm, "sceGxmSetYuvProfile", 0x1234u, 0u, 0u));
        Assert.Equal(INVALID_VALUE, Call(Gxm, "sceGxmSetYuvProfile", ctx, 2u, 0u));
        Assert.Equal(INVALID_VALUE, Call(Gxm, "sceGxmSetYuvProfile", ctx, 0u, 4u));
        Assert.Equal(0, Call(Gxm, "sceGxmSetYuvProfile", ctx, 1u, 3u));
        Assert.Equal(INVALID_ALIGNMENT, Call(Gxm, "sceGxmSetVisibilityBuffer", ctx, buf + 4, 64u));
        Assert.Equal(0, Call(Gxm, "sceGxmSetVisibilityBuffer", ctx, buf, 64u));
        Assert.Equal(INVALID_POINTER, Call(Gxm, "sceGxmSetVisibilityBuffer", 0x1234u, buf, 64u));
        // the void setters take any value on a known context and ignore an unknown one
        foreach (uint c in new[] { ctx, 0x1234u })
        {
            Invoke(Gxm, "sceGxmSetCullMode", c, 1u);
            Invoke(Gxm, "sceGxmSetTwoSidedEnable", c, 1u);
            Invoke(Gxm, "sceGxmSetFrontDepthFunc", c, 0x00400000u);
            Invoke(Gxm, "sceGxmSetBackDepthFunc", c, 0x00400000u);
            Invoke(Gxm, "sceGxmSetFrontDepthWriteEnable", c, 0u);
            Invoke(Gxm, "sceGxmSetBackDepthWriteEnable", c, 1u);
            Invoke(Gxm, "sceGxmSetFrontFragmentProgramEnable", c, 0u);
            Invoke(Gxm, "sceGxmSetBackFragmentProgramEnable", c, 0u);
            Invoke(Gxm, "sceGxmSetFrontPolygonMode", c, 0x8000u);
            Invoke(Gxm, "sceGxmSetBackPolygonMode", c, 0u);
            Invoke(Gxm, "sceGxmSetWBufferEnable", c, 0x4000u);
            Invoke(Gxm, "sceGxmSetWClampEnable", c, 1u);
            Invoke(Gxm, "sceGxmSetWClampValue", c, 1f);
            Invoke(Gxm, "sceGxmSetFrontLineFillLastPixelEnable", c, 1u);
            Invoke(Gxm, "sceGxmSetBackLineFillLastPixelEnable", c, 1u);
            Invoke(Gxm, "sceGxmSetFrontPointLineWidth", c, 2u);
            Invoke(Gxm, "sceGxmSetBackPointLineWidth", c, 2u);
            Invoke(Gxm, "sceGxmSetFrontDepthBias", c, 1, 2);
            Invoke(Gxm, "sceGxmSetBackDepthBias", c, 1, 2);
            Invoke(Gxm, "sceGxmSetFrontStencilFunc", c, 0x0E000000u, 1u, 2u, 3u, 0xFFu, 0xFFu);
            Invoke(Gxm, "sceGxmSetBackStencilFunc", c, 0x0E000000u, 1u, 2u, 3u, 0xFFu, 0xFFu);
            Invoke(Gxm, "sceGxmSetFrontStencilRef", c, 5u);
            Invoke(Gxm, "sceGxmSetBackStencilRef", c, 6u);
            Invoke(Gxm, "sceGxmSetViewport", c, 480f, 480f, 272f, -272f, 0.5f, 0.5f);
            Invoke(Gxm, "sceGxmSetViewportEnable", c, 0u);
            Invoke(Gxm, "sceGxmSetRegionClip", c, 0x40000000u, 0u, 0u, 959u, 543u);
            Invoke(Gxm, "sceGxmSetDefaultRegionClipAndViewport", c, 959u, 543u);
            Invoke(Gxm, "sceGxmSetFrontVisibilityTestEnable", c, 1u);
            Invoke(Gxm, "sceGxmSetBackVisibilityTestEnable", c, 1u);
            Invoke(Gxm, "sceGxmSetFrontVisibilityTestIndex", c, 1u);
            Invoke(Gxm, "sceGxmSetBackVisibilityTestIndex", c, 1u);
            Invoke(Gxm, "sceGxmSetFrontVisibilityTestOp", c, 1u);
            Invoke(Gxm, "sceGxmSetBackVisibilityTestOp", c, 0u);
            Invoke(Gxm, "sceGxmSetPrecomputedVertexState", c, 0u);
            Invoke(Gxm, "sceGxmSetPrecomputedFragmentState", c, 0u);
            Invoke(Gxm, "sceGxmSetVertexProgram", c, 0u);
            Invoke(Gxm, "sceGxmSetFragmentProgram", c, 0u);
        }
        Assert.Equal(0, Call(Gxm, "sceGxmSetValidationEnable", ctx, 1u));
    }

    /// <summary>sceGxmSetYuvProfile and sceGxmSetVisibilityBuffer as libgxm 3.74: CSC0 BT601 / CSC1 BT709 after
    /// sceGxmCreateContext; a deferred context refused before the other checks; base and stride 16-byte aligned; both
    /// refused inside a scene (the values kept); profiles above 3 only for programs built with an SDK before 2.50.</summary>
    [Fact]
    public void YuvProfileAndVisibilityBufferFollowTheFirmwareChecks()
    {
        Init();
        uint ctx = Context(), rt = RenderTarget(64, 64), buf = _harness.Alloc(0x100) & ~0xFu;
        var context = (VitaSharp.Core.Gxm.GxmContext)Invoke(Gxm, "Ctx", ctx)!;
        Assert.Equal(new byte[] { 0, 1 }, context.YuvProfile);

        Assert.Equal(INVALID_ALIGNMENT, Call(Gxm, "sceGxmSetVisibilityBuffer", ctx, buf + 0x10, 0x18u));
        Assert.Equal(INVALID_ALIGNMENT, Call(Gxm, "sceGxmSetVisibilityBuffer", ctx, buf + 4, 0x20u));
        Assert.Equal(0u, context.VisBuffer);
        Assert.Equal(0, Call(Gxm, "sceGxmSetVisibilityBuffer", ctx, buf, 0x20u));
        Assert.Equal(0, Call(Gxm, "sceGxmSetYuvProfile", ctx, 0u, 2u));

        uint color = _harness.Alloc(0x40), data = _harness.Alloc(64 * 64 * 4);
        Assert.Equal(0, Call(Gxm, "sceGxmColorSurfaceInit", color, 0u, 0u, 0u, 0u, 64u, 64u, 64u, data));
        Assert.Equal(0, Call(Gxm, "sceGxmBeginScene", ctx, 0u, rt, 0u, 0u, 0u, color, 0u));
        Assert.Equal(WITHIN_SCENE, Call(Gxm, "sceGxmSetVisibilityBuffer", ctx, buf + 0x40, 0x40u));
        Assert.Equal(WITHIN_SCENE, Call(Gxm, "sceGxmSetYuvProfile", ctx, 1u, 3u));
        // the argument checks still come first inside a scene
        Assert.Equal(INVALID_ALIGNMENT, Call(Gxm, "sceGxmSetVisibilityBuffer", ctx, buf + 4, 0x40u));
        Assert.Equal(INVALID_VALUE, Call(Gxm, "sceGxmSetYuvProfile", ctx, 2u, 0u));
        Assert.Equal(0, Call(Gxm, "sceGxmEndScene", ctx, 0u, 0u));
        Finish();
        Assert.Equal(buf, context.VisBuffer);
        Assert.Equal(0x20u, context.VisStride);
        Assert.Equal(new byte[] { 2, 1 }, context.YuvProfile);

        uint p = _harness.Alloc(0x30), o = Out();
        GuestMemory.W32(p + 0x18, _harness.Alloc(0x2000));
        GuestMemory.W32(p + 0x1C, 0x2000);
        GuestMemory.W32(p + 0x20, _harness.Alloc(0x4000));
        GuestMemory.W32(p + 0x24, 0x4000);
        GuestMemory.W32(p + 0x28, _harness.Alloc(0x4000));
        GuestMemory.W32(p + 0x2C, 0x4000);
        Assert.Equal(0, Call(Gxm, "sceGxmCreateDeferredContext", p, new Ptr<uint>(o)));
        uint deferred = GuestMemory.R32(o);
        Assert.Equal(INVALID_VALUE, Call(Gxm, "sceGxmSetVisibilityBuffer", deferred, buf + 4, 0x18u));
        Assert.Equal(INVALID_VALUE, Call(Gxm, "sceGxmSetYuvProfile", deferred, 0u, 0u));

        // a profile above 3 is taken only from a program built with an SDK before 2.50: libgxm asks
        // sceKernelGetMainModuleSdkVersion (SceProcessParam +0xC); +0x10 is the main thread's name pointer, NULL for
        // many programs, and does not make a recent program lenient
        Assert.Equal(INVALID_VALUE, Call(Gxm, "sceGxmSetYuvProfile", ctx, 0u, 4u));
        SetMainModuleSdkVersion(0x03570011);
        Assert.Equal(INVALID_VALUE, Call(Gxm, "sceGxmSetYuvProfile", ctx, 0u, 4u));
        Assert.Equal(INVALID_VALUE, Call(Gxm, "GxmTestSetMode", ctx, 2u, 0u));
        SetMainModuleSdkVersion(0x02400011);
        uint processParam = _harness.Emu.Linker.ProcessParam;
        GuestMemory.W32(processParam + 0x10, _harness.Alloc(0x10));
        Assert.Equal(0x02400011, Call(_harness.Module<SystemSoftware>(), "sceKernelGetMainModuleSdkVersion"));
        Assert.Equal(0, Call(Gxm, "sceGxmSetYuvProfile", ctx, 0u, 4u));
        Assert.Equal((byte)4, context.YuvProfile[0]);
        Assert.Equal(0, Call(Gxm, "GxmTestSetMode", ctx, 2u, 0u));
        SetMainModuleSdkVersion(0x02500000);
        Assert.Equal(INVALID_VALUE, Call(Gxm, "sceGxmSetYuvProfile", ctx, 0u, 5u));
    }

    /// <summary>libgxm 3.74 copies the context's two YUV profiles into the scene's state in sceGxmBeginScene (and the
    /// setter is refused inside a scene): every scene carries the profiles set before it, the sceGxmCreateContext
    /// defaults (CSC0 BT.601 standard, CSC1 BT.709 standard) when none was set, and a refused set changes nothing.</summary>
    [Fact]
    public void EachSceneCarriesTheYuvProfilesSetBeforeIt()
    {
        Init();
        uint ctx = Context(), rt = RenderTarget(64, 64);
        uint color = _harness.Alloc(0x40), data = _harness.Alloc(64 * 64 * 4);
        Assert.Equal(0, Call(Gxm, "sceGxmColorSurfaceInit", color, 0u, 0u, 0u, 0u, 64u, 64u, 64u, data));
        GxmYuvProfiles Scene()
        {
            Assert.Equal(0, Call(Gxm, "sceGxmBeginScene", ctx, 0u, rt, 0u, 0u, 0u, color, 0u));
            Assert.Equal(WITHIN_SCENE, Call(Gxm, "sceGxmSetYuvProfile", ctx, 0u, 1u));
            Assert.Equal(0, Call(Gxm, "sceGxmEndScene", ctx, 0u, 0u));
            Finish();
            return _rec.LastBegin!.YuvProfiles;
        }

        Assert.Equal(new GxmYuvProfiles(GxmYuv.Bt601Standard, GxmYuv.Bt709Standard), Scene());
        Assert.Equal(0, Call(Gxm, "sceGxmSetYuvProfile", ctx, 1u, (uint)GxmYuv.Bt601FullRange));
        Assert.Equal(new GxmYuvProfiles(GxmYuv.Bt601Standard, GxmYuv.Bt601FullRange), Scene());
        Assert.Equal(0, Call(Gxm, "sceGxmSetYuvProfile", ctx, 0u, (uint)GxmYuv.Bt709FullRange));
        Assert.Equal(INVALID_VALUE, Call(Gxm, "sceGxmSetYuvProfile", ctx, 1u, 4u));
        Assert.Equal(new GxmYuvProfiles(GxmYuv.Bt709FullRange, GxmYuv.Bt601FullRange), Scene());
    }

    [Fact]
    public void DeferredContextsRecordCommandLists()
    {
        Init();
        uint imm = Context(), rt = RenderTarget(64, 64);
        (uint vp, uint fp, uint _) = Programs();
        uint p = _harness.Alloc(0x30), o = Out();
        GuestMemory.W32(p + 0x18, _harness.Alloc(0x2000));
        GuestMemory.W32(p + 0x1C, 0x2000);
        GuestMemory.W32(p + 0x20, _harness.Alloc(0x4000));
        GuestMemory.W32(p + 0x24, 0x4000);
        GuestMemory.W32(p + 0x28, _harness.Alloc(0x4000));
        GuestMemory.W32(p + 0x2C, 0x4000);
        Assert.Equal(INVALID_POINTER, Call(Gxm, "sceGxmCreateDeferredContext", 0u, new Ptr<uint>(o)));
        Assert.Equal(0, Call(Gxm, "sceGxmCreateDeferredContext", p, new Ptr<uint>(o)));
        uint def = GuestMemory.R32(o), type = Out();
        Assert.Equal(0, Call(Gxm, "sceGxmGetContextType", def, new Ptr<uint>(type)));
        Assert.Equal(1u, GuestMemory.R32(type)); // DEFERRED
        // immediate-only calls refuse it, deferred-only calls refuse the immediate one
        Assert.Equal(INVALID_VALUE, Call(Gxm, "sceGxmBeginScene", def, 0u, rt, 0u, 0u, 0u, 0u, 0u));
        Assert.Equal(INVALID_VALUE, Call(Gxm, "sceGxmEndScene", def, 0u, 0u));
        Assert.Equal(INVALID_VALUE, Call(Gxm, "sceGxmMidSceneFlush", def, 0u, 0u, 0u));
        Assert.Equal(INVALID_VALUE, Call(Gxm, "sceGxmVertexFence", def));
        Assert.Equal(INVALID_VALUE, Call(Gxm, "sceGxmSetVisibilityBuffer", def, 0u, 0u));
        Assert.Equal(INVALID_VALUE, Call(Gxm, "sceGxmBeginCommandList", imm));
        Assert.Equal(INVALID_POINTER, Call(Gxm, "sceGxmBeginCommandList", 0x1234u));

        uint mem = Out();
        Assert.Equal(0, Call(Gxm, "sceGxmGetDeferredContextVdmBuffer", def, new Ptr<uint>(mem)));
        Assert.Equal(GuestMemory.R32(p + 0x18), GuestMemory.R32(mem));
        Assert.Equal(0, Call(Gxm, "sceGxmGetDeferredContextVertexBuffer", def, new Ptr<uint>(mem)));
        Assert.Equal(0, Call(Gxm, "sceGxmGetDeferredContextFragmentBuffer", def, new Ptr<uint>(mem)));
        Assert.Equal(INVALID_POINTER, Call(Gxm, "sceGxmGetDeferredContextVdmBuffer", def, new Ptr<uint>(0)));
        // a buffer must be at least the minimum size, and a non-NULL one needs a size
        Assert.Equal(INVALID_VALUE, Call(Gxm, "sceGxmSetDeferredContextVertexBuffer", def, GuestMemory.R32(p + 0x20), 0u));
        Assert.Equal(INVALID_VALUE, Call(Gxm, "sceGxmSetDeferredContextFragmentBuffer", def, GuestMemory.R32(p + 0x28), 16u));
        Assert.Equal(0, Call(Gxm, "sceGxmSetDeferredContextVertexBuffer", def, GuestMemory.R32(p + 0x20), 0x4000u));
        Assert.Equal(0, Call(Gxm, "sceGxmSetDeferredContextFragmentBuffer", def, GuestMemory.R32(p + 0x28), 0x4000u));
        Assert.Equal(0, Call(Gxm, "sceGxmSetDeferredContextVdmBuffer", def, GuestMemory.R32(p + 0x18), 0x2000u));

        uint idx = _harness.Alloc(16), list = _harness.Alloc(32), empty = _harness.Alloc(32);
        Assert.Equal(NOT_WITHIN_COMMAND_LIST, Call(Gxm, "sceGxmDraw", def, 0u, 0u, idx, 3u));
        Assert.Equal(NOT_WITHIN_COMMAND_LIST, Call(Gxm, "sceGxmEndCommandList", def, list));
        // an empty list
        Assert.Equal(0, Call(Gxm, "sceGxmBeginCommandList", def));
        Assert.Equal(0, Call(Gxm, "sceGxmEndCommandList", def, empty));
        // a list with one draw
        Assert.Equal(0, Call(Gxm, "sceGxmBeginCommandList", def));
        Assert.Equal(WITHIN_COMMAND_LIST, Call(Gxm, "sceGxmBeginCommandList", def));
        Assert.Equal(WITHIN_COMMAND_LIST, Call(Gxm, "sceGxmGetDeferredContextVdmBuffer", def, new Ptr<uint>(mem)));
        Assert.Equal(WITHIN_COMMAND_LIST, Call(Gxm, "sceGxmSetDeferredContextVdmBuffer", def, 0u, 0u));
        Assert.Equal(WITHIN_COMMAND_LIST, Call(Gxm, "sceGxmDestroyDeferredContext", def));
        Invoke(Gxm, "sceGxmSetVertexProgram", def, vp);
        Invoke(Gxm, "sceGxmSetFragmentProgram", def, fp);
        Call(Gxm, "sceGxmSetVertexStream", def, 0u, _harness.Alloc(36));
        Assert.Equal(0, Call(Gxm, "sceGxmDraw", def, 0u, 0u, idx, 3u));
        Assert.Equal(INVALID_POINTER, Call(Gxm, "sceGxmEndCommandList", def, 0u));
        Assert.Equal(0, Call(Gxm, "sceGxmEndCommandList", def, list));

        // executing needs an immediate context inside a scene
        Assert.Equal(INVALID_POINTER, Call(Gxm, "sceGxmExecuteCommandList", imm, 0u));
        Assert.Equal(INVALID_VALUE, Call(Gxm, "sceGxmExecuteCommandList", def, list));
        Assert.Equal(NOT_WITHIN_SCENE, Call(Gxm, "sceGxmExecuteCommandList", imm, list));
        Assert.Equal(0, Call(Gxm, "sceGxmBeginScene", imm, 0u, rt, 0u, 0u, 0u, SceneColor(64, 64), 0u));
        Assert.Equal(INVALID_POINTER, Call(Gxm, "sceGxmExecuteCommandList", imm, _harness.Alloc(32))); // not a list
        Assert.Equal(0, Call(Gxm, "sceGxmExecuteCommandList", imm, empty));
        Assert.Equal(0, Call(Gxm, "sceGxmExecuteCommandList", imm, list));
        Assert.Equal(0, Call(Gxm, "sceGxmExecuteCommandList", imm, list)); // a list runs as often as needed
        Assert.Equal(0, Call(Gxm, "sceGxmEndScene", imm, 0u, 0u));
        Finish();
        Assert.Equal(2, _rec.Draws);
        Assert.Equal(0, Call(Gxm, "sceGxmDestroyDeferredContext", def));
        Assert.Equal(INVALID_POINTER, Call(Gxm, "sceGxmDestroyDeferredContext", def));
        Assert.Equal(INVALID_VALUE, Call(Gxm, "sceGxmDestroyDeferredContext", imm));
    }

    [Fact]
    public void PrecomputedDrawsAndStates()
    {
        Init();
        uint ctx = Context(), rt = RenderTarget(64, 64);
        (uint vp, uint fp, uint _) = Programs();
        Assert.Equal(4u, (uint)Invoke(Gxm, "sceGxmGetPrecomputedDrawSize", vp)!); // one stream
        Assert.Equal(0u, (uint)Invoke(Gxm, "sceGxmGetPrecomputedDrawSize", 0x1234u)!);
        Assert.Equal(8u, (uint)Invoke(Gxm, "sceGxmGetPrecomputedVertexStateSize", vp)!); // no buffers or textures
        Assert.Equal(8u, (uint)Invoke(Gxm, "sceGxmGetPrecomputedFragmentStateSize", fp)!);
        uint draw = _harness.Alloc(32), extra = _harness.Alloc(64), idx = _harness.Alloc(16), vtx = _harness.Alloc(36);
        Assert.Equal(INVALID_POINTER, Call(Gxm, "sceGxmPrecomputedDrawInit", 0u, vp, extra));
        Assert.Equal(INVALID_ALIGNMENT, Call(Gxm, "sceGxmPrecomputedDrawInit", draw, vp, extra + 4));
        Assert.Equal(0, Call(Gxm, "sceGxmPrecomputedDrawInit", draw, vp, extra));
        Assert.Equal(INVALID_VALUE, Call(Gxm, "sceGxmPrecomputedDrawSetVertexStream", draw, 16u, vtx));
        Assert.Equal(INVALID_POINTER, Call(Gxm, "sceGxmPrecomputedDrawSetVertexStream", draw, 0u, 0u));
        Assert.Equal(0, Call(Gxm, "sceGxmPrecomputedDrawSetVertexStream", draw, 0u, vtx));
        Assert.Equal(vtx, GuestMemory.R32(extra));
        uint streams = _harness.Alloc(4);
        GuestMemory.W32(streams, vtx);
        Assert.Equal(0, Call(Gxm, "sceGxmPrecomputedDrawSetAllVertexStreams", draw, streams));
        Assert.Equal(INVALID_POINTER, Call(Gxm, "sceGxmPrecomputedDrawSetAllVertexStreams", draw, 0u));
        Assert.Equal(INVALID_POINTER, Call(Gxm, "sceGxmPrecomputedDrawSetParams", draw, 0u, 0u, 0u, 3u));
        Assert.Equal(0, Call(Gxm, "sceGxmPrecomputedDrawSetParams", draw, 0u, 0u, idx, 3u));
        Assert.Equal(INVALID_VALUE, Call(Gxm, "sceGxmPrecomputedDrawSetParamsInstanced", draw, 0u, 0u, idx, 6u, 0u));
        Assert.Equal(0, Call(Gxm, "sceGxmPrecomputedDrawSetParamsInstanced", draw, 0u, 0u, idx, 6u, 3u));
        Assert.Equal(2u, GuestMemory.R32(draw + 28)); // instances

        uint vs = _harness.Alloc(16), vsExtra = _harness.Alloc(16), fs = _harness.Alloc(16), fsExtra = _harness.Alloc(16);
        Assert.Equal(0, Call(Gxm, "sceGxmPrecomputedVertexStateInit", vs, vp, vsExtra));
        Assert.Equal(0, Call(Gxm, "sceGxmPrecomputedFragmentStateInit", fs, fp, fsExtra));
        Assert.Equal(INVALID_ALIGNMENT, Call(Gxm, "sceGxmPrecomputedFragmentStateInit", fs, fp, fsExtra + 8));
        Assert.Equal(INVALID_POINTER, Call(Gxm, "sceGxmPrecomputedVertexStateInit", vs, 0u, vsExtra));
        uint ubs = _harness.Alloc(14 * 4);
        Assert.Equal(0, Call(Gxm, "sceGxmPrecomputedVertexStateSetAllUniformBuffers", vs, ubs));
        Assert.Equal(0, Call(Gxm, "sceGxmPrecomputedFragmentStateSetAllUniformBuffers", fs, ubs));
        Assert.Equal(INVALID_POINTER, Call(Gxm, "sceGxmPrecomputedVertexStateSetAllUniformBuffers", vs, 0u));
        Assert.Equal(0, Call(Gxm, "sceGxmPrecomputedVertexStateSetAllTextures", vs, _harness.Alloc(16)));
        Assert.Equal(0, Call(Gxm, "sceGxmPrecomputedFragmentStateSetAllTextures", fs, _harness.Alloc(16)));
        Assert.Equal(INVALID_VALUE, Call(Gxm, "sceGxmPrecomputedVertexStateSetUniformBuffer", vs, 14u, ubs));
        Assert.Equal(0, Call(Gxm, "sceGxmPrecomputedFragmentStateSetUniformBuffer", fs, 13u, ubs));
        // no default uniform buffer slot (no buffers): set is accepted and ignored, get reads 0
        Assert.Equal(0, Call(Gxm, "sceGxmPrecomputedVertexStateSetDefaultUniformBuffer", vs, ubs));
        Assert.Equal(0, Call(Gxm, "sceGxmPrecomputedFragmentStateSetDefaultUniformBuffer", fs, ubs));
        Assert.Equal(INVALID_POINTER, Call(Gxm, "sceGxmPrecomputedFragmentStateSetDefaultUniformBuffer", fs, 0u));
        Assert.Equal(0u, (uint)Invoke(Gxm, "sceGxmPrecomputedVertexStateGetDefaultUniformBuffer", vs)!);
        Assert.Equal(0u, (uint)Invoke(Gxm, "sceGxmPrecomputedFragmentStateGetDefaultUniformBuffer", fs)!);

        Assert.Equal(INVALID_POINTER, Call(Gxm, "sceGxmDrawPrecomputed", ctx, 0u));
        Assert.Equal(NOT_WITHIN_SCENE, Call(Gxm, "sceGxmDrawPrecomputed", ctx, draw));
        Assert.Equal(0, Call(Gxm, "sceGxmBeginScene", ctx, 0u, rt, 0u, 0u, 0u, SceneColor(64, 64), 0u));
        Invoke(Gxm, "sceGxmSetPrecomputedVertexState", ctx, vs);
        Invoke(Gxm, "sceGxmSetPrecomputedFragmentState", ctx, fs);
        Assert.Equal(0, Call(Gxm, "sceGxmDrawPrecomputed", ctx, draw)); // the states give the programs
        Invoke(Gxm, "sceGxmSetVertexProgram", ctx, vp);
        Invoke(Gxm, "sceGxmSetFragmentProgram", ctx, fp);
        Call(Gxm, "sceGxmSetVertexStream", ctx, 0u, vtx);
        Assert.Equal(0, Call(Gxm, "sceGxmDraw", ctx, 0u, 0u, idx, 3u)); // the precomputed states also apply to sceGxmDraw
        Assert.Equal(0, Call(Gxm, "sceGxmEndScene", ctx, 0u, 0u));
        Finish();
        Assert.Equal(2, _rec.Draws);
    }

    [Fact]
    public void SurfacesAndTextures()
    {
        // depth/stencil: the stride must be a non-zero multiple of 32 samples
        uint ds = _harness.Alloc(0x20);
        Assert.Equal(INVALID_POINTER, Call(Gxm, "sceGxmDepthStencilSurfaceInit", 0u, 0x00044000u, 0u, 64u, 0u, 0u));
        Assert.Equal(INVALID_VALUE, Call(Gxm, "sceGxmDepthStencilSurfaceInit", ds, 0x00044000u, 0u, 48u, 0u, 0u));
        Assert.Equal(0, Call(Gxm, "sceGxmDepthStencilSurfaceInit", ds, 0x00044000u, 0u, 64u, 0x81000000u, 0u));
        Assert.Equal(64u, (uint)Invoke(Gxm, "sceGxmDepthStencilSurfaceGetStrideInSamples", ds)!);
        Assert.Equal(0x00044000u, (uint)Invoke(Gxm, "sceGxmDepthStencilSurfaceGetFormat", ds)!);
        Assert.True((bool)Invoke(Gxm, "sceGxmDepthStencilSurfaceIsEnabled", ds)!);
        Assert.Equal(1.0f, (float)Invoke(Gxm, "sceGxmDepthStencilSurfaceGetBackgroundDepth", ds)!);
        Invoke(Gxm, "sceGxmDepthStencilSurfaceSetBackgroundDepth", ds, 0.25f);
        Assert.Equal(0.25f, (float)Invoke(Gxm, "sceGxmDepthStencilSurfaceGetBackgroundDepth", ds)!);
        Invoke(Gxm, "sceGxmDepthStencilSurfaceSetBackgroundStencil", ds, 0x1A5u);
        Assert.Equal(0xA5u, (uint)Invoke(Gxm, "sceGxmDepthStencilSurfaceGetBackgroundStencil", ds)!);
        Invoke(Gxm, "sceGxmDepthStencilSurfaceSetBackgroundMask", ds, false);
        Assert.False((bool)Invoke(Gxm, "sceGxmDepthStencilSurfaceGetBackgroundMask", ds)!);
        Invoke(Gxm, "sceGxmDepthStencilSurfaceSetForceLoadMode", ds, 2u);
        Invoke(Gxm, "sceGxmDepthStencilSurfaceSetForceStoreMode", ds, 4u);
        Assert.Equal(2u, (uint)Invoke(Gxm, "sceGxmDepthStencilSurfaceGetForceLoadMode", ds)!);
        Assert.Equal(4u, (uint)Invoke(Gxm, "sceGxmDepthStencilSurfaceGetForceStoreMode", ds)!);
        Assert.Equal(0, Call(Gxm, "sceGxmDepthStencilSurfaceInitDisabled", ds));
        Assert.False((bool)Invoke(Gxm, "sceGxmDepthStencilSurfaceIsEnabled", ds)!);

        // colour surface limits and fields
        uint cs = _harness.Alloc(0x40);
        Assert.Equal(INVALID_VALUE, Call(Gxm, "sceGxmColorSurfaceInit", cs, 0u, 0u, 0u, 0u, 0u, 64u, 64u, 0u));
        Assert.Equal(INVALID_VALUE, Call(Gxm, "sceGxmColorSurfaceInit", cs, 0u, 0u, 0u, 0u, 4097u, 64u, 4098u, 0u));
        Assert.Equal(INVALID_ALIGNMENT, Call(Gxm, "sceGxmColorSurfaceInit", cs, 0u, 0u, 0u, 0u, 64u, 64u, 65u, 0u));
        Assert.Equal(INVALID_VALUE, Call(Gxm, "sceGxmColorSurfaceInit", cs, 0u, 0u, 0u, 0u, 64u, 64u, 32u, 0u));
        Assert.Equal(INVALID_VALUE, Call(Gxm, "sceGxmColorSurfaceInit", cs, 0u, 0u, 0u, 0u, 64u, 64u, 64u, 0x81000002u));
        Assert.Equal(0, Call(Gxm, "sceGxmColorSurfaceInit", cs, 0x10000000u, 0x04000000u, 1u, 0u, 64u, 32u, 64u, 0x81000000u));
        Assert.Equal(0x10000000u, (uint)Invoke(Gxm, "sceGxmColorSurfaceGetFormat", cs)!);
        Assert.Equal(0x04000000u, (uint)Invoke(Gxm, "sceGxmColorSurfaceGetType", cs)!);
        Assert.Equal(1u, (uint)Invoke(Gxm, "sceGxmColorSurfaceGetScaleMode", cs)!);
        Invoke(Gxm, "sceGxmColorSurfaceSetScaleMode", cs, 0u);
        Assert.Equal(0u, (uint)Invoke(Gxm, "sceGxmColorSurfaceGetScaleMode", cs)!);
        // a 24-bit format takes another 24-bit one (here another swizzle), not a 32-bit one
        Assert.Equal(UNSUPPORTED, Call(Gxm, "sceGxmColorSurfaceSetFormat", cs, 0x0u));
        Assert.Equal(0, Call(Gxm, "sceGxmColorSurfaceSetFormat", cs, 0x10100000u));
        Assert.Equal(INVALID_POINTER, Call(Gxm, "sceGxmColorSurfaceSetDitherMode", 0u, 8u));

        // textures: power-of-two sizes for swizzled and cube, limits on size and mips
        uint t = _harness.Alloc(16);
        Assert.Equal(INVALID_POINTER, Call(Gxm, "sceGxmTextureInitSwizzled", 0u, 0u, 0u, 64u, 64u, 0u));
        Assert.Equal(INVALID_ALIGNMENT, Call(Gxm, "sceGxmTextureInitSwizzled", t, 0x81000000u, 0x0C000000u, 60u, 64u, 0u));
        Assert.Equal(INVALID_ALIGNMENT, Call(Gxm, "sceGxmTextureInitCube", t, 0x81000000u, 0x0C000000u, 64u, 0u, 0u));
        Assert.Equal(INVALID_VALUE, Call(Gxm, "sceGxmTextureInitLinear", t, 0x81000000u, 0x0C000000u, 8192u, 64u, 0u));
        Assert.Equal(INVALID_VALUE, Call(Gxm, "sceGxmTextureInitTiled", t, 0x81000000u, 0x0C000000u, 64u, 64u, 14u));
        foreach (string? fn in new[] { "sceGxmTextureInitSwizzledArbitrary", "sceGxmTextureInitTiled", "sceGxmTextureInitCubeArbitrary", "sceGxmTextureInitCube", "sceGxmTextureInitSwizzled" })
        {
            Assert.Equal(0, Call(Gxm, fn, t, 0x81000000u, 0x0C000000u, 64u, 32u, 2u));
            Assert.Equal(64u, (uint)Invoke(Gxm, "sceGxmTextureGetWidth", t)!);
            Assert.Equal(32u, (uint)Invoke(Gxm, "sceGxmTextureGetHeight", t)!);
            Assert.Equal(0x81000000u, (uint)Invoke(Gxm, "sceGxmTextureGetData", t)!);
        }
        Assert.Equal(0x0C000000u, (uint)Invoke(Gxm, "sceGxmTextureGetFormat", t)!);
        Assert.Equal(0u, (uint)Invoke(Gxm, "sceGxmTextureGetType", t)!); // swizzled
        Assert.Equal(UNSUPPORTED, Call(Gxm, "sceGxmTextureSetStride", t, 256u));
        Assert.Equal(0, Call(Gxm, "sceGxmTextureSetData", t, 0x82000000u));
        Assert.Equal(0x82000000u, (uint)Invoke(Gxm, "sceGxmTextureGetData", t)!);
        Assert.Equal(0, Call(Gxm, "sceGxmTextureSetMagFilter", t, 1u));
        Assert.Equal(1u, (uint)Invoke(Gxm, "sceGxmTextureGetMagFilter", t)!);
        Assert.Equal(0, Call(Gxm, "sceGxmTextureSetMinFilter", t, 1u));
        Assert.Equal(1u, (uint)Invoke(Gxm, "sceGxmTextureGetMinFilter", t)!);
        Assert.Equal(0, Call(Gxm, "sceGxmTextureSetMipFilter", t, 1u));
        Assert.Equal(1u, (uint)Invoke(Gxm, "sceGxmTextureGetMipFilter", t)!);
        Assert.Equal(0, Call(Gxm, "sceGxmTextureSetUAddrMode", t, 3u));
        Assert.Equal(0, Call(Gxm, "sceGxmTextureSetVAddrMode", t, 1u));
        Assert.Equal(3u, (uint)Invoke(Gxm, "sceGxmTextureGetUAddrMode", t)!);
        Assert.Equal(1u, (uint)Invoke(Gxm, "sceGxmTextureGetVAddrMode", t)!);
        Assert.Equal(0, Call(Gxm, "sceGxmTextureSetVAddrModeSafe", t, 2u));
        Assert.Equal(2u, (uint)Invoke(Gxm, "sceGxmTextureGetVAddrModeSafe", t)!);
        Assert.Equal(0, Call(Gxm, "sceGxmTextureSetLodBias", t, 40u));
        Assert.Equal(40u, (uint)Invoke(Gxm, "sceGxmTextureGetLodBias", t)!);
        Assert.Equal(0, Call(Gxm, "sceGxmTextureSetLodMin", t, 5u));
        Assert.Equal(5u, (uint)Invoke(Gxm, "sceGxmTextureGetLodMin", t)!);
        Assert.Equal(0, Call(Gxm, "sceGxmTextureSetMipmapCount", t, 4u));
        Assert.Equal(4u, (uint)Invoke(Gxm, "sceGxmTextureGetMipmapCount", t)!);
        Assert.Equal(0, Call(Gxm, "sceGxmTextureSetPalette", t, 0x81000040u));
        Assert.Equal(0x81000040u, (uint)Invoke(Gxm, "sceGxmTextureGetPalette", t)!);
        Assert.Equal(0, Call(Gxm, "sceGxmTextureSetWidth", t, 128u));
        Assert.Equal(128u, (uint)Invoke(Gxm, "sceGxmTextureGetWidth", t)!);
        Assert.Equal(0, Call(Gxm, "sceGxmTextureSetHeight", t, 16u));
        Assert.Equal(16u, (uint)Invoke(Gxm, "sceGxmTextureGetHeight", t)!);
        Assert.Equal(0, Call(Gxm, "sceGxmTextureSetFormat", t, 0x05000000u));
        Assert.Equal(0x05000000u, (uint)Invoke(Gxm, "sceGxmTextureGetFormat", t)!);
        Assert.Equal(INVALID_POINTER, Call(Gxm, "sceGxmTextureSetFormat", 0u, 0u));
        Assert.Equal(INVALID_POINTER, Call(Gxm, "sceGxmTextureSetWidth", 0u, 1u));
        Assert.Equal(INVALID_POINTER, Call(Gxm, "sceGxmTextureSetHeight", 0u, 1u));
        Assert.Equal(0, Call(Gxm, "sceGxmTextureValidate", t));
        Assert.Equal(INVALID_POINTER, Call(Gxm, "sceGxmTextureValidate", 0u));
        // linear strided: 4-byte aligned strides between 4 and 128 KiB; mip and filter setters do not apply
        Assert.Equal(INVALID_ALIGNMENT, Call(Gxm, "sceGxmTextureInitLinearStrided", t, 0x81000000u, 0x0C000000u, 16u, 16u, 66u));
        Assert.Equal(INVALID_VALUE, Call(Gxm, "sceGxmTextureInitLinearStrided", t, 0x81000000u, 0x0C000000u, 16u, 16u, 0u));
        Assert.Equal(INVALID_VALUE, Call(Gxm, "sceGxmTextureInitLinearStrided", t, 0x81000000u, 0x0C000000u, 5000u, 16u, 64u));
        Assert.Equal(0, Call(Gxm, "sceGxmTextureInitLinearStrided", t, 0x81000000u, 0x0C000000u, 16u, 16u, 64u));
        Assert.Equal(0xC0000000u, (uint)Invoke(Gxm, "sceGxmTextureGetType", t)!);
        Assert.Equal(INVALID_VALUE, Call(Gxm, "sceGxmTextureSetStride", t, 0u));
        Assert.Equal(0u, (uint)Invoke(Gxm, "sceGxmTextureGetMinFilter", t)!);
        Assert.Equal(0u, (uint)Invoke(Gxm, "sceGxmTextureGetLodBias", t)!);
        Assert.Equal(0, Call(Gxm, "sceGxmTextureSetMipmapCount", t, 4u)); // ignored
    }

    [Fact]
    public void TransfersRunInGpuOrder()
    {
        Init();
        const int Width = 8, Height = 4;
        uint src = _harness.Alloc(Width * Height * 4), dst = _harness.Alloc(Width * Height * 4), word = _harness.Word(0), notif = _harness.Alloc(8);
        GuestMemory.W32(notif, word);
        GuestMemory.W32(notif + 4, 5);
        // fill a 4x2 block at (2,1) of a linear 8x4 surface
        Assert.Equal(INVALID_POINTER, Call(Gxm, "sceGxmTransferFill", 0u, 0x000C0000u, 0u, 0u, 0u, 1u, 1u, Width * 4, 0u, 0u, 0u));
        Assert.Equal(0, Call(Gxm, "sceGxmTransferFill", 0xFF00FF00u, 0x000C0000u, src, 2u, 1u, 4u, 2u, Width * 4, 0u, 0u, notif));
        Assert.Equal(0, Call(Gxm, "sceGxmTransferFinish"));
        Assert.Equal(5u, GuestMemory.R32(word));
        Assert.Equal(0xFF00FF00u, GuestMemory.R32(src + (1 * Width + 2) * 4));
        Assert.Equal(0xFF00FF00u, GuestMemory.R32(src + (2 * Width + 5) * 4));
        Assert.Equal(0u, GuestMemory.R32(src + (1 * Width + 1) * 4));
        Assert.Equal(0u, GuestMemory.R32(src + (3 * Width + 2) * 4));
        // copy with a colour key: key mode 1 copies only the pixels matching the key
        for (uint i = 0; i < Width * Height; i++)
        {
            if (GuestMemory.R32(src + i * 4) == 0)
            {
                GuestMemory.W32(src + i * 4, 0x11111111);
            }
        }

        Assert.Equal(INVALID_POINTER, Call(Gxm, "sceGxmTransferCopy", 1u, 1u, 0u, 0u, 0u, 0x000C0000u, 0u, 0u, 0u, 0u, Width * 4, 0x000C0000u, 0u, dst, 0u, 0u, Width * 4, 0u, 0u, 0u));
        Assert.Equal(0, Call(Gxm, "sceGxmTransferCopy", (uint)Width, (uint)Height, 0xFF00FF00u, 0xFFFFFFFFu, 1u, 0x000C0000u, 0u, src, 0u, 0u, Width * 4,
            0x000C0000u, 0u, dst, 0u, 0u, Width * 4, 0u, 0u, 0u));
        Assert.Equal(0, Call(Gxm, "sceGxmTransferFinish"));
        Assert.Equal(0xFF00FF00u, GuestMemory.R32(dst + (1 * Width + 2) * 4));
        Assert.Equal(0u, GuestMemory.R32(dst)); // not the key: left alone
        // a plain copy, then a 2x downscale averaging each 2x2 block
        Assert.Equal(0, Call(Gxm, "sceGxmTransferCopy", (uint)Width, (uint)Height, 0u, 0u, 0u, 0x000C0000u, 0u, src, 0u, 0u, Width * 4,
            0x000C0000u, 0u, dst, 0u, 0u, Width * 4, 0u, 0u, 0u));
        uint half = _harness.Alloc(Width / 2 * Height / 2 * 4);
        Assert.Equal(INVALID_POINTER, Call(Gxm, "sceGxmTransferDownscale", 0x000C0000u, 0u, 0u, 0u, (uint)Width, (uint)Height, Width * 4, 0x000C0000u, half, 0u, 0u, Width / 2 * 4, 0u, 0u, 0u));
        Assert.Equal(0, Call(Gxm, "sceGxmTransferDownscale", 0x000C0000u, dst, 0u, 0u, (uint)Width, (uint)Height, Width * 4, 0x000C0000u, half, 0u, 0u, Width / 2 * 4, 0u, 0u, 0u));
        Assert.Equal(0, Call(Gxm, "sceGxmTransferFinish"));
        Assert.Equal(0x11111111u, GuestMemory.R32(half));             // block (0,0): all 0x11
        // block (1,0) of the source rows 0-1, columns 2-3: two 0x11 pixels and two fill pixels
        uint mixed = GuestMemory.R32(half + 4);
        Assert.Equal((uint)((0x11 * 2 + 0xFF * 2 + 2) / 4), mixed >> 24);
        Assert.Equal((uint)((0x11 * 2 + 0x00 * 2 + 2) / 4), (mixed >> 16) & 0xFF);
    }

    [Fact]
    public void DisplayQueueCallsBackAndFinishes()
    {
        Init();
        uint so = Out(), a, b;
        Call(Gxm, "sceGxmSyncObjectCreate", new Ptr<uint>(so));
        a = GuestMemory.R32(so);
        Call(Gxm, "sceGxmSyncObjectCreate", new Ptr<uint>(so));
        b = GuestMemory.R32(so);
        Assert.Equal(INVALID_POINTER, Call(Gxm, "sceGxmDisplayQueueAddEntry", 0u, b, 0u));
        uint data = _harness.Word(0x1234);
        _harness.OnGuest(_ =>
        {
            for (int i = 0; i < 4; i++)
            {
                Assert.Equal(0, Call(Gxm, "sceGxmDisplayQueueAddEntry", i % 2 == 0 ? a : b, i % 2 == 0 ? b : a, data));
            }

            Assert.Equal(0, Call(Gxm, "sceGxmDisplayQueueFinish"));
            Assert.Equal(0, Call(Gxm, "sceGxmWaitEvent"));
        });
    }

    /// <summary>Two 960x544 colour buffers A and B with their sync objects, and a display queue of one pending entry
    /// (displayQueueMaxPendingCount 2) whose callback flips with sceDisplaySetFrameBuf. The callback runs
    /// <paramref name="beforeFlip"/> and <paramref name="afterFlip"/> with the flip's number (1-based), blocked as a
    /// guest waiting for a vblank. Emu.Running is set: the display queue and the sync object waits only wait while the
    /// emulation runs.</summary>
    private TwoBuffers TwoBufferDisplay(Action<int> beforeFlip, Action<int> afterFlip)
    {
        const uint Width = 960, Height = 544;
        int flips = 0;
        uint callback = new VitaSharp.Core.Hle.HleGuestStubs(Gxm, "gxmflow").Host("display callback", c =>
        {
            uint buffer = GuestMemory.R32(c.Gpr(0));
            int flip = Interlocked.Increment(ref flips);
            using (VitaSharp.Core.Kernel.Scheduler.Block())
            {
                beforeFlip(flip);
            }

            uint frameBuf = _harness.Alloc(0x18);
            GuestMemory.W32(frameBuf, 0x18);
            GuestMemory.W32(frameBuf + 4, buffer);
            GuestMemory.W32(frameBuf + 8, Width);
            GuestMemory.W32(frameBuf + 12, 0); // A8B8G8R8
            GuestMemory.W32(frameBuf + 16, Width);
            GuestMemory.W32(frameBuf + 20, Height);
            Assert.Equal(0, Call(_harness.Module<Display>(), "SetFrameBuf", frameBuf, 1u));
            afterFlip(flip);
            c.Return(0);
        });
        uint p = _harness.Alloc(0x14);
        GuestMemory.W32(p + 4, 2);        // displayQueueMaxPendingCount
        GuestMemory.W32(p + 8, callback);
        GuestMemory.W32(p + 12, 4);       // display callback data: the buffer's address
        GuestMemory.W32(p + 16, 0x200000);
        Assert.Equal(0, Call(Gxm, "sceGxmInitialize", p));
        uint ctx = Context(), rt = RenderTarget((ushort)Width, (ushort)Height);
        uint dataA = _harness.Alloc(Width * Height * 4), dataB = _harness.Alloc(Width * Height * 4);
        uint colorA = _harness.Alloc(0x40), colorB = _harness.Alloc(0x40);
        Assert.Equal(0, Call(Gxm, "sceGxmColorSurfaceInit", colorA, 0u, 0u, 0u, 0u, Width, Height, Width, dataA));
        Assert.Equal(0, Call(Gxm, "sceGxmColorSurfaceInit", colorB, 0u, 0u, 0u, 0u, Width, Height, Width, dataB));
        uint so = Out();
        Assert.Equal(0, Call(Gxm, "sceGxmSyncObjectCreate", new Ptr<uint>(so)));
        uint syncA = GuestMemory.R32(so);
        Assert.Equal(0, Call(Gxm, "sceGxmSyncObjectCreate", new Ptr<uint>(so)));
        uint syncB = GuestMemory.R32(so);
        _harness.Emu.Running = true;
        return new TwoBuffers(ctx, rt, colorA, colorB, dataA, dataB, syncA, syncB);
    }

    /// <summary>What <see cref="TwoBufferDisplay"/> set up: the context, the render target, the colour surfaces of A
    /// and B, their memory and their sync objects.</summary>
    private readonly record struct TwoBuffers(uint Ctx, uint Rt, uint ColorA, uint ColorB, uint DataA, uint DataB, uint SyncA, uint SyncB);

    /// <summary>The scenes and flips of buffers A and B in GPU order ("begin 0x...", "end", "present 0x...").</summary>
    private string[] SceneOrder(uint dataA, uint dataB)
    {
        string a = $"0x{dataA:X8}", b = $"0x{dataB:X8}";
        return _rec.Frames.Where(e => e.EndsWith(a) || e.EndsWith(b) || e == "end").ToArray();
    }

    /// <summary>
    /// A display callback that flips (sceDisplaySetFrameBuf) only after the game began its next scene into the buffer
    /// being flipped, as when the callback waits for a vblank and the game runs ahead. The scene into that buffer waits
    /// for the flip away from it (its fragment sync object), so on the console the display shows the finished frame
    /// first, and only then does the GPU render into the buffer again. The flip must come before that scene in GPU
    /// order: behind it, the display showed the buffer cleared or half drawn (F1 2011's isolated black frames, Killzone
    /// Mercenary's HUD flicker, 11/11/2026).
    /// </summary>
    [Fact]
    public void DisplayQueueFlipComesBeforeALaterSceneIntoTheSameBuffer()
    {
        using var sceneBegun = new ManualResetEventSlim();
        // the first flip waits (for a vblank) while the game goes on into its next frame
        TwoBuffers d = TwoBufferDisplay(flip => Assert.True(flip != 1 || sceneBegun.Wait(10_000), "the game did not begin its next scene"), _ => { });
        uint entryA = _harness.Word(d.DataA), entryB = _harness.Word(d.DataB);
        _harness.OnGuest(_ =>
        {
            // frame 0 into A, frame 1 into B, each queued for display
            Assert.Equal(0, Call(Gxm, "sceGxmBeginScene", d.Ctx, 0u, d.Rt, 0u, 0u, d.SyncA, d.ColorA, 0u));
            Assert.Equal(0, Call(Gxm, "sceGxmEndScene", d.Ctx, 0u, 0u));
            Assert.Equal(0, Call(Gxm, "sceGxmDisplayQueueAddEntry", d.SyncB, d.SyncA, entryA));
            Assert.Equal(0, Call(Gxm, "sceGxmBeginScene", d.Ctx, 0u, d.Rt, 0u, 0u, d.SyncB, d.ColorB, 0u));
            Assert.Equal(0, Call(Gxm, "sceGxmEndScene", d.Ctx, 0u, 0u));
            Assert.Equal(0, Call(Gxm, "sceGxmDisplayQueueAddEntry", d.SyncA, d.SyncB, entryB));
            // frame 2 into A again while frame 0 is still being flipped: the GPU holds it until A leaves the screen
            Assert.Equal(0, Call(Gxm, "sceGxmBeginScene", d.Ctx, 0u, d.Rt, 0u, 0u, d.SyncA, d.ColorA, 0u));
            sceneBegun.Set();
            Assert.Equal(0, Call(Gxm, "sceGxmEndScene", d.Ctx, 0u, 0u));
            Assert.Equal(0, Call(Gxm, "sceGxmDisplayQueueAddEntry", d.SyncB, d.SyncA, entryA));
            Assert.Equal(0, Call(Gxm, "sceGxmDisplayQueueFinish"));
            Finish();
        });
        _harness.Emu.Running = false;
        string a = $"0x{d.DataA:X8}", b = $"0x{d.DataB:X8}";
        string[] order = SceneOrder(d.DataA, d.DataB).Where(e => e != "end").ToArray();
        Assert.True(order.SequenceEqual(new[] { "begin " + a, "begin " + b, "present " + a, "present " + b, "begin " + a, "present " + a }), "GPU order: " + string.Join(", ", order));
    }

    /// <summary>
    /// The flip of frame 0 (buffer A) arrives while the GPU worker is still behind the game: frame 1's scene, into the
    /// other buffer B, is queued but not run yet. Outside a sync object wait the flip keeps its place in GPU order, after
    /// that scene. Running every flip at the worker's next command (5ce57aa6) put it before the queued scene, and Metal
    /// Gear Solid HD then fell into stretches of alternating pictures ([flash] / [picture-order]) about five times as
    /// often as with the queue order (12/11/2026).
    /// </summary>
    /// <summary>
    /// The game begins its next frame's scene into buffer B before it queues the current frame (A) for display: that
    /// scene's sync object wait needs the flip of the entry not queued yet. libgxm's entry waits for the new buffer's
    /// scenes (its sync object), which come before the held scene, so the flip happens and releases B. With the entry
    /// waiting for every command submitted before it, its fence sat behind the held scene: the GPU worker waited for the
    /// flip and the display thread for the fence, until the 250 ms limit (Wipeout 2048, 0.3-1.4 s stalls under load).
    /// The wait can only end on that limit then, so the count of limit hits is the check, not a time.
    /// </summary>
    [Fact]
    public void ASceneBegunBeforeTheEntryThatReleasesItsBufferDoesNotStallTheGpu()
    {
        TwoBuffers d = TwoBufferDisplay(_ => { }, _ => { });
        uint entryA = _harness.Word(d.DataA), entryB = _harness.Word(d.DataB);
        int timeoutsBefore = Volatile.Read(ref VitaSharp.Core.Hle.Modules.Gxm.SyncTimeouts);
        _harness.OnGuest(_ =>
        {
            // frames 0 (A) and 1 (B), each queued after its scene
            Assert.Equal(0, Call(Gxm, "sceGxmBeginScene", d.Ctx, 0u, d.Rt, 0u, 0u, d.SyncA, d.ColorA, 0u));
            Assert.Equal(0, Call(Gxm, "sceGxmEndScene", d.Ctx, 0u, 0u));
            Assert.Equal(0, Call(Gxm, "sceGxmDisplayQueueAddEntry", d.SyncB, d.SyncA, entryA));
            Assert.Equal(0, Call(Gxm, "sceGxmBeginScene", d.Ctx, 0u, d.Rt, 0u, 0u, d.SyncB, d.ColorB, 0u));
            Assert.Equal(0, Call(Gxm, "sceGxmEndScene", d.Ctx, 0u, 0u));
            Assert.Equal(0, Call(Gxm, "sceGxmDisplayQueueAddEntry", d.SyncA, d.SyncB, entryB));
            // frame 2 into A, then frame 3's scene into B begins before frame 2 is queued: B leaves the screen only
            // with frame 2's flip
            Assert.Equal(0, Call(Gxm, "sceGxmBeginScene", d.Ctx, 0u, d.Rt, 0u, 0u, d.SyncA, d.ColorA, 0u));
            Assert.Equal(0, Call(Gxm, "sceGxmEndScene", d.Ctx, 0u, 0u));
            Assert.Equal(0, Call(Gxm, "sceGxmBeginScene", d.Ctx, 0u, d.Rt, 0u, 0u, d.SyncB, d.ColorB, 0u));
            Assert.Equal(0, Call(Gxm, "sceGxmDisplayQueueAddEntry", d.SyncB, d.SyncA, entryA));
            Assert.Equal(0, Call(Gxm, "sceGxmEndScene", d.Ctx, 0u, 0u));
            Assert.Equal(0, Call(Gxm, "sceGxmDisplayQueueAddEntry", d.SyncA, d.SyncB, entryB));
            Assert.Equal(0, Call(Gxm, "sceGxmDisplayQueueFinish"));
            Finish();
        });
        _harness.Emu.Running = false;
        Assert.Equal(timeoutsBefore, Volatile.Read(ref VitaSharp.Core.Hle.Modules.Gxm.SyncTimeouts));
        string a = $"0x{d.DataA:X8}", b = $"0x{d.DataB:X8}";
        string[] order = SceneOrder(d.DataA, d.DataB).Where(e => e != "end").ToArray();
        // frame 2 (A) is shown before frame 3's scene draws into B
        int presentA2 = Array.LastIndexOf(order, "present " + a);
        int beginB3 = Array.LastIndexOf(order, "begin " + b);
        Assert.True(presentA2 >= 0 && presentA2 < beginB3, "GPU order: " + string.Join(", ", order));
    }

    /// <summary>A transfer naming the display buffer's sync object after its last scene writes the buffer too: the entry
    /// then waits for everything submitted before it (the transfer included), not only for the scene.</summary>
    [Fact]
    public void ATransferIntoTheBufferAfterItsSceneIsWaitedForByTheEntry()
    {
        var shown = new List<uint>();
        TwoBuffers d = default;
        d = TwoBufferDisplay(flip =>
        {
            lock (shown)
            {
                shown.Add(GuestMemory.R32(d.DataA));
            }
        }, _ => { });
        uint entryA = _harness.Word(d.DataA);
        _harness.OnGuest(_ =>
        {
            Assert.Equal(0, Call(Gxm, "sceGxmBeginScene", d.Ctx, 0u, d.Rt, 0u, 0u, d.SyncA, d.ColorA, 0u));
            Assert.Equal(0, Call(Gxm, "sceGxmEndScene", d.Ctx, 0u, 0u));
            Assert.Equal(0, Call(Gxm, "sceGxmTransferFill", 0xFF3366CCu, 0x000C0000u, d.DataA, 0u, 0u, 4u, 1u, 960 * 4, d.SyncA, 0u, 0u));
            Assert.Equal(0, Call(Gxm, "sceGxmDisplayQueueAddEntry", d.SyncB, d.SyncA, entryA));
            Assert.Equal(0, Call(Gxm, "sceGxmDisplayQueueFinish"));
            Finish();
        });
        _harness.Emu.Running = false;
        Assert.Equal([0xFF3366CCu], shown);
    }

    [Fact]
    public void DisplayQueueFlipKeepsItsQueuePositionOutsideASyncWait()
    {
        using var sceneQueued = new ManualResetEventSlim();
        using var flipped = new ManualResetEventSlim();
        using var workerGo = new ManualResetEventSlim();
        TwoBuffers d = TwoBufferDisplay(flip => Assert.True(flip != 1 || sceneQueued.Wait(10_000), "the game did not queue its next scene"),
            flip =>
            {
                if (flip == 1)
                {
                    flipped.Set();
                }
            });
        uint entryA = _harness.Word(d.DataA), entryB = _harness.Word(d.DataB);
        _harness.OnGuest(_ =>
        {
            Assert.Equal(0, Call(Gxm, "sceGxmBeginScene", d.Ctx, 0u, d.Rt, 0u, 0u, d.SyncA, d.ColorA, 0u));
            Assert.Equal(0, Call(Gxm, "sceGxmEndScene", d.Ctx, 0u, 0u));
            Assert.Equal(0, Call(Gxm, "sceGxmDisplayQueueAddEntry", d.SyncB, d.SyncA, entryA));
            // the worker is busy (a long transfer) while frame 1 into B is queued behind it and frame 0 is flipped
            _harness.Emu.Gpu.Submit(new CallbackCommand { Action = () => workerGo.Wait(10_000) });
            Assert.Equal(0, Call(Gxm, "sceGxmBeginScene", d.Ctx, 0u, d.Rt, 0u, 0u, d.SyncB, d.ColorB, 0u));
            Assert.Equal(0, Call(Gxm, "sceGxmEndScene", d.Ctx, 0u, 0u));
            sceneQueued.Set();
            using (VitaSharp.Core.Kernel.Scheduler.Block())
            {
                Assert.True(flipped.Wait(10_000), "the display queue did not flip frame 0");
            }

            workerGo.Set();
            Assert.Equal(0, Call(Gxm, "sceGxmDisplayQueueAddEntry", d.SyncA, d.SyncB, entryB));
            Assert.Equal(0, Call(Gxm, "sceGxmDisplayQueueFinish"));
            Finish();
        });
        _harness.Emu.Running = false;
        string a = $"0x{d.DataA:X8}", b = $"0x{d.DataB:X8}";
        string[] order = SceneOrder(d.DataA, d.DataB);
        Assert.True(order.SequenceEqual(new[] { "begin " + a, "end", "begin " + b, "end", "present " + a, "present " + b }), "GPU order: " + string.Join(", ", order));
    }

    /// <summary>
    /// The flips that release the buffer of a held scene arrive while the worker is not looking (here: in its
    /// <see cref="IGpuBackend.Idle"/> inside the sync object wait), and the release comes right after them. The wait
    /// ends on that release, and those flips must still run before the scene it held: ending the wait without them put
    /// the scene into buffer A before the flip that shows A's previous frame (a cleared or half drawn picture, as in F1
    /// 2011's black frames).
    /// </summary>
    /// <summary>Writes a triangle (indices 0, 1, 2; three F32 x3 vertices from <paramref name="first"/> on) into a vertex
    /// pool slot, as vita2d does for each draw.</summary>
    private static void WriteTriangle(uint indices, uint vertices, float first)
    {
        for (uint i = 0; i < 3; i++)
        {
            GuestMemory.Write(indices + i * 2, (ushort)i);
        }

        for (uint i = 0; i < 9; i++)
        {
            GuestMemory.Write(vertices + i * 4, first + i);
        }
    }

    private void DrawTriangle(uint ctx, uint vp, uint fp, uint indices, uint vertices)
    {
        Invoke(Gxm, "sceGxmSetVertexProgram", ctx, vp);
        Invoke(Gxm, "sceGxmSetFragmentProgram", ctx, fp);
        Assert.Equal(0, Call(Gxm, "sceGxmSetVertexStream", ctx, 0u, vertices));
        Assert.Equal(0, Call(Gxm, "sceGxmDraw", ctx, 0u, 0u, indices, 3u));
    }

    /// <summary>
    /// A scene into a buffer still being shown is held on the GPU until the flip away from it (its fragment sync
    /// object), but on the console only its fragment phase waits: the vertex phase reads the indices and vertex streams
    /// when the scene is kicked (sceGxmEndScene / sceGxmMidSceneFlush), and the game reuses that memory for its next
    /// frame right after (vita2d resets its vertex pool at every sceGxmBeginScene). The draws of the held scene, of the
    /// part before a mid-scene flush, and of a later scene queued behind the hold all read what the memory held at their
    /// kick, not the next frame's data (PSVitaAlive: triangles shot out of the screen's corner, keyboard keys missing,
    /// 17/11/2026). A scene that is not held reads guest memory as before (no copy).
    /// </summary>
    [Fact]
    public void DrawsBehindADisplayHoldReadTheGeometryOfTheirKick()
    {
        using var rewritten = new ManualResetEventSlim();
        // frame 0's flip (buffer A shown) waits until the game reused its vertex pool: A stays held until then
        TwoBuffers d = TwoBufferDisplay(flip => Assert.True(flip != 1 || rewritten.Wait(10_000), "the game did not reuse its pool"), _ => { });
        (uint vp, uint fp, _) = Programs();
        uint entryA = _harness.Word(d.DataA), entryB = _harness.Word(d.DataB);
        uint pool = _harness.Alloc(0x400);
        uint i0 = pool, v0 = pool + 0x10, i1 = pool + 0x40, v1 = pool + 0x50, i2 = pool + 0x80, v2 = pool + 0x90;
        uint i3 = pool + 0xC0, v3 = pool + 0xD0;
        _harness.OnGuest(_ =>
        {
            // frame 0 (A, one draw, not held) and frame 1 (B), each queued for display
            Assert.Equal(0, Call(Gxm, "sceGxmBeginScene", d.Ctx, 0u, d.Rt, 0u, 0u, d.SyncA, d.ColorA, 0u));
            WriteTriangle(i0, v0, 1);
            DrawTriangle(d.Ctx, vp, fp, i0, v0);
            Assert.Equal(0, Call(Gxm, "sceGxmEndScene", d.Ctx, 0u, 0u));
            Assert.Equal(0, Call(Gxm, "sceGxmDisplayQueueAddEntry", d.SyncB, d.SyncA, entryA));
            Assert.Equal(0, Call(Gxm, "sceGxmBeginScene", d.Ctx, 0u, d.Rt, 0u, 0u, d.SyncB, d.ColorB, 0u));
            Assert.Equal(0, Call(Gxm, "sceGxmEndScene", d.Ctx, 0u, 0u));
            Assert.Equal(0, Call(Gxm, "sceGxmDisplayQueueAddEntry", d.SyncA, d.SyncB, entryB));
            // frame 2 into A, held: a draw, a mid-scene flush, a draw; then an off-screen scene queued behind it
            Assert.Equal(0, Call(Gxm, "sceGxmBeginScene", d.Ctx, 0u, d.Rt, 0u, 0u, d.SyncA, d.ColorA, 0u));
            WriteTriangle(i1, v1, 10);
            DrawTriangle(d.Ctx, vp, fp, i1, v1);
            Assert.Equal(0, Call(Gxm, "sceGxmMidSceneFlush", d.Ctx, 0u, 0u, 0u));
            WriteTriangle(i2, v2, 20);
            DrawTriangle(d.Ctx, vp, fp, i2, v2);
            Assert.Equal(0, Call(Gxm, "sceGxmEndScene", d.Ctx, 0u, 0u));
            Assert.Equal(0, Call(Gxm, "sceGxmBeginScene", d.Ctx, 0u, d.Rt, 0u, 0u, 0u, d.ColorB, 0u));
            WriteTriangle(i3, v3, 30);
            DrawTriangle(d.Ctx, vp, fp, i3, v3);
            Assert.Equal(0, Call(Gxm, "sceGxmEndScene", d.Ctx, 0u, 0u));
            // the next frame reuses the pool: other indices and vertices in the same memory
            for (uint slot = 1; slot < 4; slot++)
            {
                uint indices = pool + slot * 0x40;
                WriteTriangle(indices, indices + 0x10, 100 + slot);
                GuestMemory.Write(indices, (ushort)2);
                GuestMemory.Write(indices + 4, (ushort)0);
            }

            rewritten.Set();
            Assert.Equal(0, Call(Gxm, "sceGxmDisplayQueueAddEntry", d.SyncB, d.SyncA, entryA));
            Assert.Equal(0, Call(Gxm, "sceGxmDisplayQueueFinish"));
            Finish();
        });
        _harness.Emu.Running = false;
        (ushort[] Indices, float[] Vertices, bool Copied)[] draws = _rec.Geometry.ToArray();
        Assert.Equal(4, draws.Length);
        Assert.False(draws[0].Copied, "a scene that was not held must read guest memory");
        float[] firsts = [1, 10, 20, 30];
        for (int i = 0; i < 4; i++)
        {
            Assert.Equal(new ushort[] { 0, 1, 2 }, draws[i].Indices);
            Assert.Equal(Enumerable.Range(0, 9).Select(n => firsts[i] + n).ToArray(), draws[i].Vertices);
        }
        Assert.True(draws[1].Copied && draws[2].Copied && draws[3].Copied, "the draws behind the hold read a copy");
    }

    [Fact]
    public void FlipsSubmittedJustBeforeTheReleaseRunBeforeTheHeldScene()
    {
        using var sceneBegun = new ManualResetEventSlim();
        using var workerHeld = new ManualResetEventSlim();
        using var flipsDone = new ManualResetEventSlim();
        int heldIdles = 0;
        // the first flip waits until the worker sits in the sync object wait, past the flips it has looked at
        TwoBuffers d = TwoBufferDisplay(flip => Assert.True(flip != 1 || workerHeld.Wait(10_000), "the worker did not wait for the sync object"), _ => { });
        _rec.OnIdle = () =>
        {
            // the first Idle after frame 2's scene was queued is inside its sync object wait (the wait is the next
            // command in the queue): the display thread flips both frames and releases A meanwhile
            if (sceneBegun.IsSet && Interlocked.Increment(ref heldIdles) == 1)
            {
                workerHeld.Set();
                Assert.True(flipsDone.Wait(10_000), "the display queue did not flip frames 0 and 1");
            }
        };
        uint entryA = _harness.Word(d.DataA), entryB = _harness.Word(d.DataB);
        _harness.OnGuest(_ =>
        {
            Assert.Equal(0, Call(Gxm, "sceGxmBeginScene", d.Ctx, 0u, d.Rt, 0u, 0u, d.SyncA, d.ColorA, 0u));
            Assert.Equal(0, Call(Gxm, "sceGxmEndScene", d.Ctx, 0u, 0u));
            Assert.Equal(0, Call(Gxm, "sceGxmDisplayQueueAddEntry", d.SyncB, d.SyncA, entryA));
            Assert.Equal(0, Call(Gxm, "sceGxmBeginScene", d.Ctx, 0u, d.Rt, 0u, 0u, d.SyncB, d.ColorB, 0u));
            Assert.Equal(0, Call(Gxm, "sceGxmEndScene", d.Ctx, 0u, 0u));
            Assert.Equal(0, Call(Gxm, "sceGxmDisplayQueueAddEntry", d.SyncA, d.SyncB, entryB));
            // frame 2 into A: held until the flip to B releases A
            Assert.Equal(0, Call(Gxm, "sceGxmBeginScene", d.Ctx, 0u, d.Rt, 0u, 0u, d.SyncA, d.ColorA, 0u));
            sceneBegun.Set();
            // both entries flipped and A released (the display thread's pending count drops after the release)
            Assert.Equal(0, Call(Gxm, "sceGxmDisplayQueueFinish"));
            flipsDone.Set();
            Assert.Equal(0, Call(Gxm, "sceGxmEndScene", d.Ctx, 0u, 0u));
            Assert.Equal(0, Call(Gxm, "sceGxmDisplayQueueAddEntry", d.SyncB, d.SyncA, entryA));
            Assert.Equal(0, Call(Gxm, "sceGxmDisplayQueueFinish"));
            Finish();
        });
        _harness.Emu.Running = false;
        string a = $"0x{d.DataA:X8}", b = $"0x{d.DataB:X8}";
        string[] order = SceneOrder(d.DataA, d.DataB).Where(e => e != "end").ToArray();
        Assert.True(heldIdles >= 1, "the worker never idled inside the sync object wait");
        Assert.True(order.SequenceEqual(new[] { "begin " + a, "begin " + b, "present " + a, "present " + b, "begin " + a, "present " + a }), "GPU order: " + string.Join(", ", order));
    }

    /// <summary>
    /// A load waits for the display queue to drain, as a save does. The display queue thread is kept across a load:
    /// an entry of the running session still in its callback (here blocked as for a vblank wait) would finish on the
    /// loaded state, with the old session's callback data on the loaded memory, freeing that address in the loaded heap
    /// and leaving the pending count at -1. Uncharted loaded on its title screen then waited for good in
    /// sceDisplayWaitSetFrameBufMulti inside the callback, its main thread in sceGxmDisplayQueueAddEntry (0 fps on the
    /// phone, 13/11/2026). Without the check the load sees no module busy while the entry is in flight.
    /// </summary>
    [Fact]
    public void LoadWaitsForTheDisplayQueueToDrain()
    {
        using var inCallback = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        TwoBuffers d = TwoBufferDisplay(_ =>
        {
            inCallback.Set();
            Assert.True(release.Wait(10_000), "the test did not release the callback");
        }, _ => { });
        Assert.Null(VitaSharp.Core.SaveState.SaveStates.ModulesBusyForLoad(_harness.Emu));
        uint entryA = _harness.Word(d.DataA);
        string? busyInFlight = null;
        _harness.OnGuest(_ =>
        {
            Assert.Equal(0, Call(Gxm, "sceGxmBeginScene", d.Ctx, 0u, d.Rt, 0u, 0u, d.SyncA, d.ColorA, 0u));
            Assert.Equal(0, Call(Gxm, "sceGxmEndScene", d.Ctx, 0u, 0u));
            Assert.Equal(0, Call(Gxm, "sceGxmDisplayQueueAddEntry", d.SyncB, d.SyncA, entryA));
            Assert.True(inCallback.Wait(10_000), "the display queue did not call back");
            // the entry is in the game's callback on the display queue thread: a load must wait
            busyInFlight = VitaSharp.Core.SaveState.SaveStates.ModulesBusyForLoad(_harness.Emu);
            release.Set();
            Assert.Equal(0, Call(Gxm, "sceGxmDisplayQueueFinish"));
        });
        _harness.Emu.Running = false;
        Assert.Equal("module Gxm: a frame is in the GXM display queue", busyInFlight);
        // drained: the load may go on
        Assert.Null(VitaSharp.Core.SaveState.SaveStates.ModulesBusyForLoad(_harness.Emu));
    }

    /// <summary>
    /// The neighbours of <see cref="LoadWaitsForTheDisplayQueueToDrain"/>: the entry waiting for its frame's GPU fence
    /// (the display thread blocked outside any callback, which the load counted as a safe point: the phone's case, a
    /// slow GPU), and an entry still queued behind it (the queue full). Load and save both wait while either is there,
    /// and both go on once the queue drained; a load before any entry was never held.
    /// </summary>
    [Fact]
    public void LoadAndSaveWaitWhileAnEntryWaitsForItsFrameOrIsQueued()
    {
        using var gpuHeld = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        TwoBuffers d = TwoBufferDisplay(_ => { }, _ => { });
        Assert.Null(VitaSharp.Core.SaveState.SaveStates.ModulesBusyForLoad(_harness.Emu));
        uint entryA = _harness.Word(d.DataA), entryB = _harness.Word(d.DataB);
        string? busyWaitingForFrame = null, busyQueued = null;
        Exception? saveWhileBusy = null;
        _harness.OnGuest(_ =>
        {
            // the GPU worker is held: the frames' fences stay pending
            _harness.Emu.Gpu.Submit(new CallbackCommand
            {
                Action = () =>
                {
                    gpuHeld.Set();
                    Assert.True(release.Wait(10_000), "the test did not release the GPU");
                },
            });
            Assert.True(gpuHeld.Wait(10_000), "the GPU worker did not run the held command");
            Assert.Equal(0, Call(Gxm, "sceGxmDisplayQueueAddEntry", d.SyncB, d.SyncA, entryA));
            // the display thread takes the entry and waits for its fence, blocked in the host outside any callback (the
            // entry counts from the call on, whether the thread has taken it yet or not)
            busyWaitingForFrame = VitaSharp.Core.SaveState.SaveStates.ModulesBusyForLoad(_harness.Emu);
            Assert.Equal(0, Call(Gxm, "sceGxmDisplayQueueAddEntry", d.SyncA, d.SyncB, entryB));
            busyQueued = VitaSharp.Core.SaveState.SaveStates.ModulesBusyForLoad(_harness.Emu);
            saveWhileBusy = Record.Exception(() => ((VitaSharp.Core.SaveState.ISnapshotState)Gxm).SaveState(new VitaSharp.Core.SaveState.SnapshotWriter()));
            release.Set();
            Assert.Equal(0, Call(Gxm, "sceGxmDisplayQueueFinish"));
        });
        _harness.Emu.Running = false;
        Assert.Equal("module Gxm: a frame is in the GXM display queue", busyWaitingForFrame);
        Assert.Equal("module Gxm: a frame is in the GXM display queue", busyQueued);
        Assert.IsType<VitaSharp.Core.SaveState.SnapshotRetryException>(saveWhileBusy);
        Assert.Null(VitaSharp.Core.SaveState.SaveStates.ModulesBusyForLoad(_harness.Emu));
        ((VitaSharp.Core.SaveState.ISnapshotState)Gxm).SaveState(new VitaSharp.Core.SaveState.SnapshotWriter());
    }
}
