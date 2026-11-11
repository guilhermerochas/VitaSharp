using System.Collections.Concurrent;
using VitaSharp.Core.Gpu;
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
    const int UNINITIALIZED = unchecked((int)0x805B0000), ALREADY_INITIALIZED = unchecked((int)0x805B0001),
        INVALID_VALUE = unchecked((int)0x805B0003), INVALID_POINTER = unchecked((int)0x805B0004), INVALID_ALIGNMENT = unchecked((int)0x805B0005),
        NOT_WITHIN_SCENE = unchecked((int)0x805B0006), WITHIN_SCENE = unchecked((int)0x805B0007), NULL_PROGRAM = unchecked((int)0x805B0008),
        UNSUPPORTED = unchecked((int)0x805B0009), WITHIN_COMMAND_LIST = unchecked((int)0x805B002C), NOT_WITHIN_COMMAND_LIST = unchecked((int)0x805B002D);

    sealed class Recorder : IGpuBackend
    {
        public readonly ConcurrentQueue<string> Kinds = new();
        public BeginSceneCommand? LastBegin;
        public int Draws;
        public (uint Index, uint Count, byte Prim, uint Instances) LastDraw;
        public void Execute(GpuCommand command)
        {
            Kinds.Enqueue(command.GetType().Name);
            if (command is BeginSceneCommand b)
            {
                LastBegin = b;
            }

            if (command is DrawCommand d)
            {
                Draws++;
                LastDraw = (d.IndexAddress, d.IndexCount, d.PrimitiveType, d.InstanceCount);
            }
        }
        public void Dispose()
        {
        }
    }

    readonly HleHarness _h;
    readonly Recorder _rec = new();
    Gxm G => _h.Module<Gxm>();

    public GxmFlowTests(GuestMemoryFixture fx)
    {
        _h = new HleHarness(fx, "gxmflow");
        _h.Emu.Gpu.Backend = _rec;
    }

    public void Dispose()
    {
        Call(G, "sceGxmTerminate");
        _h.Dispose();
    }

    uint Out() => _h.Word(0);

    void Init()
    {
        uint p = _h.Alloc(0x14);
        GuestMemory.W32(p + 4, 2);       // displayQueueMaxPendingCount
        GuestMemory.W32(p + 12, 4);      // display callback data size
        GuestMemory.W32(p + 16, 0x200000);
        Assert.Equal(0, Call(G, "sceGxmInitialize", p));
    }

    uint Context()
    {
        uint p = _h.Alloc(0x24), o = Out();
        GuestMemory.W32(p + 8, _h.Alloc(0x1000));
        GuestMemory.W32(p + 12, 0x1000);    // VDM ring
        GuestMemory.W32(p + 16, _h.Alloc(0x4000));
        GuestMemory.W32(p + 20, 0x4000);   // vertex ring
        GuestMemory.W32(p + 24, _h.Alloc(0x4000));
        GuestMemory.W32(p + 28, 0x4000);   // fragment ring
        Assert.Equal(0, Call(G, "sceGxmCreateContext", p, new Ptr<uint>(o)));
        return GuestMemory.R32(o);
    }

    uint RenderTarget(ushort w, ushort h)
    {
        uint p = _h.Alloc(0x14), o = Out();
        GuestMemory.Write(p + 4, w);
        GuestMemory.Write(p + 6, h);
        GuestMemory.Write(p + 8, (ushort)1);
        GuestMemory.W32(p + 16, 0x1234);
        Assert.Equal(0, Call(G, "sceGxmCreateRenderTarget", p, new Ptr<uint>(o)));
        return GuestMemory.R32(o);
    }

    /// <summary>A GXP program (magic "GXP\0", size at +8): a vertex one with two instructions, or an empty fragment one;
    /// <paramref name="defaultUniforms"/> words of default uniform buffer (+0x64).</summary>
    uint Program(bool fragment, uint defaultUniforms = 0)
    {
        ulong[] code = fragment ? [] : [0x6081000460600003, 0x50C9000A20200180];
        uint size = 0x100 + (uint)code.Length * 8;
        uint p = _h.Alloc(size);
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

    (uint Vp, uint Fp, uint Patcher) Programs(uint vUniforms = 0, uint fUniforms = 0)
    {
        uint pp = _h.Alloc(0x40), o = Out();
        GuestMemory.W32(pp, 0xABCD); // user data
        Assert.Equal(0, Call(G, "sceGxmShaderPatcherCreate", pp, new Ptr<uint>(o)));
        uint patcher = GuestMemory.R32(o);
        uint vid = Out(), fid = Out();
        Assert.Equal(0, Call(G, "sceGxmShaderPatcherRegisterProgram", patcher, Program(false, vUniforms), new Ptr<uint>(vid)));
        Assert.Equal(0, Call(G, "sceGxmShaderPatcherRegisterProgram", patcher, Program(true, fUniforms), new Ptr<uint>(fid)));
        uint attr = _h.Alloc(8), stream = _h.Alloc(4), vp = Out(), fp = Out();
        GuestMemory.Write(attr + 4, (byte)0);
        GuestMemory.Write(attr + 5, (byte)3); // F32 x3 from stream 0
        GuestMemory.Write(stream, (ushort)12);
        Assert.Equal(0, Call(G, "sceGxmShaderPatcherCreateVertexProgram", patcher, GuestMemory.R32(vid), attr, 1u, stream, 1u, new Ptr<uint>(vp)));
        Assert.Equal(0, Call(G, "sceGxmShaderPatcherCreateFragmentProgram", patcher, GuestMemory.R32(fid), 0u, 0u, 0u, GuestMemory.R32(vp), new Ptr<uint>(fp)));
        return (GuestMemory.R32(vp), GuestMemory.R32(fp), patcher);
    }

    void Finish() => Assert.Equal(0, Call(G, "sceGxmFinish", 0u));

    [Fact]
    public void InitializeAndTerminate()
    {
        Assert.Equal(UNINITIALIZED, Call(G, "sceGxmGetDisplayQueueThreadIdInternal"));
        Assert.Equal(UNINITIALIZED, Call(G, "sceGxmInitializedInternal"));
        Assert.Equal(UNINITIALIZED, Call(G, "sceGxmCheckMemoryInternal", 0u, 0u, 0u));
        Assert.Equal(UNINITIALIZED, Call(G, "sceGxmDisplayQueueAddEntry", 1u, 2u, 0u));
        Assert.Equal(INVALID_POINTER, Call(G, "sceGxmInitialize", 0u));
        Init();
        Assert.Equal(ALREADY_INITIALIZED, Call(G, "sceGxmInitialize", _h.Alloc(0x14)));
        Assert.Equal(0, Call(G, "sceGxmInitializedInternal"));
        Assert.True(Call(G, "sceGxmGetDisplayQueueThreadIdInternal") > 0);
        Assert.NotEqual(0u, (uint)Invoke(G, "sceGxmGetNotificationRegion")!);
        uint t = Out();
        Assert.Equal(0, Call(G, "sceGxmGetParameterBufferThreshold", new Ptr<uint>(t)));
        Assert.Equal(0x200000u, GuestMemory.R32(t));
        Assert.Equal(INVALID_POINTER, Call(G, "sceGxmGetParameterBufferThreshold", new Ptr<uint>(0)));
        // mapping: the checked range must lie in mapped memory
        uint mem = _h.Alloc(0x10000);
        Assert.Equal(INVALID_POINTER, Call(G, "sceGxmMapMemory", 0u, 0x1000u, 0u));
        Assert.Equal(0, Call(G, "sceGxmMapMemory", mem, 0x10000u, 3u));
        Assert.Equal(0, Call(G, "sceGxmCheckMemoryInternal", mem + 0x100, 0x100u, 0u));
        Assert.Equal(unchecked((int)0x805B0017), Call(G, "sceGxmCheckMemoryInternal", mem + 0xFF00, 0x200u, 0u));
        Assert.Equal(0, Call(G, "sceGxmUnmapMemory", mem));
        Assert.Equal(INVALID_VALUE, Call(G, "sceGxmUnmapMemory", mem));
        uint off = Out();
        Assert.Equal(0, Call(G, "sceGxmMapVertexUsseMemory", mem, 0x1000u, new Ptr<uint>(off)));
        Assert.Equal(mem, GuestMemory.R32(off));
        Assert.Equal(0, Call(G, "sceGxmMapFragmentUsseMemoryInternal", mem, 0x1000u, new Ptr<uint>(off)));
        Assert.Equal(INVALID_POINTER, Call(G, "sceGxmMapVertexUsseMemory", 0u, 0x1000u, new Ptr<uint>(off)));
        Assert.Equal(INVALID_POINTER, Call(G, "sceGxmMapFragmentUsseMemory", mem, 0x1000u, new Ptr<uint>(0)));
        Assert.Equal(0, Call(G, "sceGxmUnmapVertexUsseMemory", mem));
        Assert.Equal(0, Call(G, "sceGxmUnmapFragmentUsseMemory", mem));
        foreach (var fn in new[] { "sceGxmIsDebugVersion", "sceGxmIsInitializationInternal", "sceGxmPadTriggerGpuPaTrace" })
        {
            Assert.Equal(0, Call(G, fn));
        }

        Assert.Equal(0, Call(G, "sceGxmSetWarningEnabled", 1u, 0u));
        Assert.Equal(0, Call(G, "sceGxmPadHeartbeat", 0u, 0u));
        Assert.Equal(INVALID_POINTER, Call(G, "sceGxmAddRazorGpuCaptureBuffer", 0u, 0u));
        Assert.Equal(0, Call(G, "sceGxmAddRazorGpuCaptureBuffer", mem, 0x100u));
        Assert.Equal(0, Call(G, "sceGxmRemoveRazorGpuCaptureBuffer", mem));
        Assert.Equal(INVALID_POINTER, Call(G, "sceGxmRemoveRazorGpuCaptureBuffer", 0u));
        Invoke(G, "sceGxmSetCallbackInternal", 0x10005, mem);
        Invoke(G, "sceGxmSetInitializeParamInternal", 0x10004, mem);
        Assert.Equal(0, Call(G, "sceGxmTerminate"));
        Assert.Equal(UNINITIALIZED, Call(G, "sceGxmInitializedInternal"));
    }

    [Fact]
    public void ContextsTargetsAndSyncObjects()
    {
        Init();
        Assert.Equal(INVALID_POINTER, Call(G, "sceGxmCreateContext", 0u, new Ptr<uint>(Out())));
        uint ctx = Context(), type = _h.Word(9);
        Assert.Equal(0, Call(G, "sceGxmGetContextType", ctx, new Ptr<uint>(type)));
        Assert.Equal(0u, GuestMemory.R32(type)); // IMMEDIATE
        Assert.Equal(INVALID_POINTER, Call(G, "sceGxmGetContextType", 0x1234u, new Ptr<uint>(type)));
        uint size = Out();
        Assert.Equal(0, Call(G, "sceGxmGetRenderTargetMemSize", _h.Alloc(0x14), new Ptr<uint>(size)));
        Assert.True(GuestMemory.R32(size) > 0);
        Assert.Equal(INVALID_POINTER, Call(G, "sceGxmGetRenderTargetMemSize", 0u, new Ptr<uint>(size)));
        Assert.Equal(INVALID_POINTER, Call(G, "sceGxmCreateRenderTarget", 0u, new Ptr<uint>(size)));
        uint rt = RenderTarget(960, 544), blk = Out();
        Assert.Equal(0, Call(G, "sceGxmRenderTargetGetDriverMemBlock", rt, new Ptr<int>(blk)));
        Assert.Equal(0x1234u, GuestMemory.R32(blk));
        Assert.Equal(INVALID_POINTER, Call(G, "sceGxmRenderTargetGetDriverMemBlock", 0x1234u, new Ptr<int>(blk)));
        Assert.Equal(0, Call(G, "sceGxmDestroyRenderTarget", rt));

        uint so = Out();
        Assert.Equal(INVALID_POINTER, Call(G, "sceGxmSyncObjectCreate", new Ptr<uint>(0)));
        Assert.Equal(0, Call(G, "sceGxmSyncObjectCreate", new Ptr<uint>(so)));
        uint sync = GuestMemory.R32(so);
        Assert.NotEqual(0u, sync);
        // the vsh sync objects shared by key: open finds what create registered; an unknown key gets a new one
        Assert.Equal(INVALID_POINTER, Call(G, "sceGxmVshSyncObjectCreate", 1u, 0u));
        Assert.Equal(0, Call(G, "sceGxmVshSyncObjectCreate", 1u, sync));
        uint opened = Out();
        Assert.Equal(0, Call(G, "sceGxmVshSyncObjectOpen", 1u, new Ptr<uint>(opened)));
        Assert.Equal(sync, GuestMemory.R32(opened));
        Assert.Equal(0, Call(G, "sceGxmVshSyncObjectOpen", 2u, new Ptr<uint>(opened)));
        Assert.NotEqual(sync, GuestMemory.R32(opened));
        Assert.Equal(INVALID_POINTER, Call(G, "sceGxmVshSyncObjectOpen", 2u, new Ptr<uint>(0)));
        Assert.Equal(0, Call(G, "sceGxmVshSyncObjectClose", 1u, sync));
        Assert.Equal(0, Call(G, "sceGxmVshSyncObjectDestroy", 1u));
        Assert.Equal(0, Call(G, "sceGxmSyncObjectDestroy", sync));

        // markers need a context
        Assert.Equal(INVALID_POINTER, Call(G, "sceGxmPushUserMarker", 0x1234u, 0u));
        Assert.Equal(0, Call(G, "sceGxmPushUserMarker", ctx, 0u));
        Assert.Equal(0, Call(G, "sceGxmPopUserMarker", ctx));
        Assert.Equal(0, Call(G, "sceGxmPopUserMarker", ctx)); // nothing pushed: still OK
        Assert.Equal(INVALID_POINTER, Call(G, "sceGxmPopUserMarker", 0x1234u));
        Assert.Equal(0, Call(G, "sceGxmSetUserMarker", ctx, 0u));
        Assert.Equal(INVALID_POINTER, Call(G, "sceGxmSetUserMarker", 0x1234u, 0u));
        Assert.Equal(0, Call(G, "sceGxmDestroyContext", ctx));
    }

    [Fact]
    public void SceneLifecycleAndNotifications()
    {
        Init();
        uint ctx = Context(), rt = RenderTarget(960, 544);
        uint color = _h.Alloc(0x40), data = _h.Alloc(960 * 544 * 4);
        Assert.Equal(0, Call(G, "sceGxmColorSurfaceInit", color, 0u, 0u, 0u, 0u, 960u, 544u, 960u, data));
        uint depth = _h.Alloc(0x20);
        Assert.Equal(0, Call(G, "sceGxmDepthStencilSurfaceInit", depth, 0x00044000u, 0u, 960u, _h.Alloc(960 * 544 * 4), 0u));

        Assert.Equal(NOT_WITHIN_SCENE, Call(G, "sceGxmEndScene", ctx, 0u, 0u));
        Assert.Equal(NOT_WITHIN_SCENE, Call(G, "sceGxmVertexFence", ctx));
        Assert.False((bool)Invoke(G, "sceGxmRenderingContextIsWithinSceneInternal")!);
        Assert.Equal(INVALID_POINTER, Call(G, "sceGxmBeginScene", ctx, 0u, 0x1234u, 0u, 0u, 0u, color, depth));
        Assert.Equal(INVALID_POINTER, Call(G, "sceGxmBeginScene", 0x1234u, 0u, rt, 0u, 0u, 0u, color, depth));
        Assert.Equal(0, Call(G, "sceGxmBeginScene", ctx, 0u, rt, 0u, 0u, 0u, color, depth));
        Assert.True((bool)Invoke(G, "sceGxmRenderingContextIsWithinSceneInternal")!);
        Assert.Equal(WITHIN_SCENE, Call(G, "sceGxmBeginScene", ctx, 0u, rt, 0u, 0u, 0u, color, depth));
        Assert.Equal(0, Call(G, "sceGxmVertexFence", ctx));
        Assert.Equal(0, Call(G, "sceGxmMidSceneFlush", ctx, 0u, 0u, 0u));
        // fragment notification: written by the GPU once the scene is done
        uint word = _h.Word(0), notif = _h.Alloc(8);
        GuestMemory.W32(notif, word);
        GuestMemory.W32(notif + 4, 0x77);
        Assert.Equal(0, Call(G, "sceGxmEndScene", ctx, 0u, notif));
        Finish();
        Assert.Equal(0x77u, GuestMemory.R32(word));
        Assert.Equal(0, Call(G, "sceGxmNotificationWait", notif)); // already reached: returns at once
        Assert.Equal(INVALID_POINTER, Call(G, "sceGxmNotificationWait", 0u));
        var begin = _rec.LastBegin!;
        Assert.Equal((960u, 544u), (begin.Width, begin.Height));
        Assert.Equal(data, begin.Color!.Data);
        Assert.Equal(960u, begin.Depth!.StrideInSamples);
        Assert.Contains("MidSceneFlushCommand", _rec.Kinds);
        Assert.Contains("EndSceneCommand", _rec.Kinds);
        Assert.Contains("FinishCommand", _rec.Kinds);

        // the old SDK's entry carries the valid region at +8; a disabled colour surface renders without colour
        uint region = _h.Alloc(16), off = _h.Alloc(0x40);
        GuestMemory.W32(region + 8, 479);
        GuestMemory.W32(region + 12, 271);
        Assert.Equal(0, Call(G, "sceGxmColorSurfaceInitDisabled", off));
        Assert.False((bool)Invoke(G, "sceGxmColorSurfaceIsEnabled", off)!);
        Assert.Equal(0, Call(G, "_sceGxmBeginScene", ctx, 0u, rt, region, 0u, 0u, off, 0u));
        Assert.Equal(0, Call(G, "sceGxmEndScene", ctx, 0u, 0u));
        Finish();
        Assert.Null(_rec.LastBegin!.Color);
        Assert.Equal(0, Call(G, "sceGxmBeginSceneEx", ctx, 0u, rt, 0u, 0u, 0u, color, depth, 0u));
        Assert.Equal(0, Call(G, "sceGxmEndScene", ctx, 0u, 0u));
        Assert.Equal(0, Call(G, "sceGxmTransferFinish"));
        Assert.NotNull(_rec.LastBegin!.Color);
    }

    [Fact]
    public void ShaderPatcherProgramsAndDraws()
    {
        Init();
        uint ctx = Context(), rt = RenderTarget(64, 64);
        var (vp, fp, patcher) = Programs(vUniforms: 4, fUniforms: 2);
        Assert.Equal(0xABCDu, (uint)Invoke(G, "sceGxmShaderPatcherGetUserData", patcher)!);
        Assert.Equal(0, Call(G, "sceGxmShaderPatcherSetUserData", patcher, 0x1111u));
        Assert.Equal(0x1111u, (uint)Invoke(G, "sceGxmShaderPatcherGetUserData", patcher)!);
        uint prog = (uint)Invoke(G, "sceGxmVertexProgramGetProgram", vp)!;
        Assert.Equal(0, Call(G, "sceGxmProgramCheck", prog));
        Assert.Equal(INVALID_POINTER, Call(G, "sceGxmProgramCheck", 0u));
        Assert.Equal(INVALID_VALUE, Call(G, "sceGxmProgramCheck", _h.Alloc(16)));
        Assert.Equal(0u, (uint)Invoke(G, "sceGxmProgramGetType", prog)!);   // vertex
        Assert.Equal(16u, (uint)Invoke(G, "sceGxmProgramGetDefaultUniformBufferSize", prog)!);
        Assert.Equal(0x110u, (uint)Invoke(G, "sceGxmProgramGetSize", prog)!);
        uint fprog = (uint)Invoke(G, "sceGxmFragmentProgramGetProgram", fp)!;
        Assert.Equal(1u, (uint)Invoke(G, "sceGxmProgramGetType", fprog)!);
        Assert.True((bool)Invoke(G, "sceGxmFragmentProgramIsEnabled", fp)!);
        Assert.Equal(0u, (uint)Invoke(G, "sceGxmFragmentProgramGetPassType", fp)!); // opaque
        // a vertex program with the same attributes is the cached one, with one more reference
        uint count = Out();
        Assert.Equal(0, Call(G, "sceGxmShaderPatcherAddRefVertexProgram", patcher, vp));
        Assert.Equal(0, Call(G, "sceGxmShaderPatcherGetVertexProgramRefCount", patcher, vp, new Ptr<uint>(count)));
        Assert.Equal(2u, GuestMemory.R32(count));
        Assert.Equal(0, Call(G, "sceGxmShaderPatcherReleaseVertexProgram", patcher, vp));
        Assert.Equal(0, Call(G, "sceGxmShaderPatcherAddRefFragmentProgram", patcher, fp));
        Assert.Equal(0, Call(G, "sceGxmShaderPatcherGetFragmentProgramRefCount", patcher, fp, new Ptr<uint>(count)));
        Assert.Equal(2u, GuestMemory.R32(count));
        Assert.Equal(0, Call(G, "sceGxmShaderPatcherReleaseFragmentProgram", patcher, fp));
        uint mask = Out();
        Assert.Equal(0, Call(G, "sceGxmShaderPatcherCreateMaskUpdateFragmentProgram", patcher, new Ptr<uint>(mask)));
        Assert.Equal(0x06000000u, (uint)Invoke(G, "sceGxmFragmentProgramGetPassType", GuestMemory.R32(mask))!);
        Assert.False((bool)Invoke(G, "sceGxmFragmentProgramIsEnabled", GuestMemory.R32(mask))!);
        Assert.Equal(INVALID_POINTER, Call(G, "sceGxmShaderPatcherRegisterProgram", 0x1234u, prog, new Ptr<uint>(count)));
        Assert.Equal(INVALID_POINTER, Call(G, "sceGxmShaderPatcherCreate", 0u, new Ptr<uint>(count)));

        uint idx = _h.Alloc(16), verts = _h.Alloc(36);
        Assert.Equal(NOT_WITHIN_SCENE, Call(G, "sceGxmDraw", ctx, 0u, 0u, idx, 3u));
        Assert.Equal(INVALID_POINTER, Call(G, "sceGxmDraw", 0x1234u, 0u, 0u, idx, 3u));
        Assert.Equal(0, Call(G, "sceGxmBeginScene", ctx, 0u, rt, 0u, 0u, 0u, 0u, 0u));
        Assert.Equal(NULL_PROGRAM, Call(G, "sceGxmDraw", ctx, 0u, 0u, idx, 3u));
        Invoke(G, "sceGxmSetVertexProgram", ctx, vp);
        Invoke(G, "sceGxmSetFragmentProgram", ctx, fp);
        Assert.Equal(INVALID_POINTER, Call(G, "sceGxmSetVertexStream", ctx, 0u, 0u));
        Assert.Equal(INVALID_VALUE, Call(G, "sceGxmSetVertexStream", ctx, 16u, verts));
        Assert.Equal(0, Call(G, "sceGxmSetVertexStream", ctx, 0u, verts));
        // the default uniform buffer comes from the context's ring, sized by the program
        uint ub = Out();
        Assert.Equal(0, Call(G, "sceGxmReserveVertexDefaultUniformBuffer", ctx, new Ptr<uint>(ub)));
        Assert.NotEqual(0u, GuestMemory.R32(ub));
        Assert.Equal(0, Call(G, "sceGxmReserveFragmentDefaultUniformBuffer", ctx, new Ptr<uint>(ub)));
        Assert.NotEqual(0u, GuestMemory.R32(ub));
        Assert.Equal(INVALID_POINTER, Call(G, "sceGxmReserveVertexDefaultUniformBuffer", ctx, new Ptr<uint>(0)));
        Assert.Equal(INVALID_POINTER, Call(G, "sceGxmDraw", ctx, 0u, 0u, 0u, 3u)); // no index data
        Assert.Equal(0, Call(G, "sceGxmDraw", ctx, 0u, 0u, idx, 3u));
        Assert.Equal(INVALID_VALUE, Call(G, "sceGxmDrawInstanced", ctx, 0u, 0u, idx, 6u, 4u)); // 6 not a multiple of 4
        Assert.Equal(INVALID_VALUE, Call(G, "sceGxmDrawInstanced", ctx, 0u, 0u, idx, 6u, 0u));
        Assert.Equal(0, Call(G, "sceGxmDrawInstanced", ctx, 0u, 0u, idx, 6u, 3u));
        Assert.Equal(0, Call(G, "sceGxmEndScene", ctx, 0u, 0u));
        Finish();
        Assert.Equal(2, _rec.Draws);
        Assert.Equal((idx, 3u, (byte)0, 2u), _rec.LastDraw); // 6 indices, wrap 3: two instances of 3

        Assert.Equal(0, Call(G, "sceGxmShaderPatcherUnregisterProgram", patcher, 0u));
        Assert.Equal(0, Call(G, "sceGxmShaderPatcherForceUnregisterProgram", patcher, 0u));
        Assert.Equal(INVALID_POINTER, Call(G, "sceGxmShaderPatcherUnregisterProgram", 0x1234u, 0u));
        foreach (var fn in new[] { "sceGxmShaderPatcherGetHostMemAllocated", "sceGxmShaderPatcherGetBufferMemAllocated",
            "sceGxmShaderPatcherGetVertexUsseMemAllocated", "sceGxmShaderPatcherGetFragmentUsseMemAllocated" })
        {
            Assert.Equal(0u, (uint)Invoke(G, fn, patcher)!);
        }

        Assert.Equal(0, Call(G, "sceGxmShaderPatcherDestroy", patcher));
    }

    [Fact]
    public void StateSettersValidateTheirArguments()
    {
        Init();
        uint ctx = Context(), buf = _h.Alloc(64), tex = _h.Alloc(16);
        Assert.Equal(0, Call(G, "sceGxmTextureInitLinear", tex, _h.Alloc(64 * 64 * 4), 0x0C000000u, 64u, 64u, 0u));
        Assert.Equal(INVALID_POINTER, Call(G, "sceGxmSetFragmentTexture", ctx, 0u, 0u));
        Assert.Equal(INVALID_VALUE, Call(G, "sceGxmSetFragmentTexture", ctx, 16u, tex));
        Assert.Equal(0, Call(G, "sceGxmSetFragmentTexture", ctx, 15u, tex));
        Assert.Equal(INVALID_VALUE, Call(G, "sceGxmSetVertexTexture", ctx, 16u, tex));
        Assert.Equal(0, Call(G, "sceGxmSetVertexTexture", ctx, 0u, tex));
        Assert.Equal(INVALID_POINTER, Call(G, "sceGxmSetVertexTexture", 0x1234u, 0u, tex));
        Assert.Equal(INVALID_VALUE, Call(G, "sceGxmSetVertexUniformBuffer", ctx, 14u, buf)); // 0-13 (14 is the default one)
        Assert.Equal(0, Call(G, "sceGxmSetVertexUniformBuffer", ctx, 13u, buf));
        Assert.Equal(INVALID_POINTER, Call(G, "sceGxmSetFragmentUniformBuffer", ctx, 0u, 0u));
        Assert.Equal(INVALID_VALUE, Call(G, "sceGxmSetFragmentUniformBuffer", ctx, 14u, buf));
        Assert.Equal(0, Call(G, "sceGxmSetFragmentUniformBuffer", ctx, 0u, buf));
        Assert.Equal(0, Call(G, "sceGxmSetVertexDefaultUniformBuffer", ctx, buf));
        Assert.Equal(0, Call(G, "sceGxmSetFragmentDefaultUniformBuffer", ctx, buf));
        // no program bound: nothing to reserve
        uint ub = _h.Word(9);
        Assert.Equal(0, Call(G, "sceGxmReserveFragmentDefaultUniformBuffer", ctx, new Ptr<uint>(ub)));
        Assert.Equal(0u, GuestMemory.R32(ub));
        Assert.Equal(INVALID_POINTER, Call(G, "sceGxmSetYuvProfile", 0x1234u, 0u, 0u));
        Assert.Equal(INVALID_VALUE, Call(G, "sceGxmSetYuvProfile", ctx, 2u, 0u));
        Assert.Equal(INVALID_VALUE, Call(G, "sceGxmSetYuvProfile", ctx, 0u, 4u));
        Assert.Equal(0, Call(G, "sceGxmSetYuvProfile", ctx, 1u, 3u));
        Assert.Equal(INVALID_ALIGNMENT, Call(G, "sceGxmSetVisibilityBuffer", ctx, buf + 4, 64u));
        Assert.Equal(0, Call(G, "sceGxmSetVisibilityBuffer", ctx, buf, 64u));
        Assert.Equal(INVALID_POINTER, Call(G, "sceGxmSetVisibilityBuffer", 0x1234u, buf, 64u));
        // the void setters take any value on a known context and ignore an unknown one
        foreach (uint c in new[] { ctx, 0x1234u })
        {
            Invoke(G, "sceGxmSetCullMode", c, 1u);
            Invoke(G, "sceGxmSetTwoSidedEnable", c, 1u);
            Invoke(G, "sceGxmSetFrontDepthFunc", c, 0x00400000u);
            Invoke(G, "sceGxmSetBackDepthFunc", c, 0x00400000u);
            Invoke(G, "sceGxmSetFrontDepthWriteEnable", c, 0u);
            Invoke(G, "sceGxmSetBackDepthWriteEnable", c, 1u);
            Invoke(G, "sceGxmSetFrontFragmentProgramEnable", c, 0u);
            Invoke(G, "sceGxmSetBackFragmentProgramEnable", c, 0u);
            Invoke(G, "sceGxmSetFrontPolygonMode", c, 0x8000u);
            Invoke(G, "sceGxmSetBackPolygonMode", c, 0u);
            Invoke(G, "sceGxmSetWBufferEnable", c, 0x4000u);
            Invoke(G, "sceGxmSetWClampEnable", c, 1u);
            Invoke(G, "sceGxmSetWClampValue", c, 1f);
            Invoke(G, "sceGxmSetFrontLineFillLastPixelEnable", c, 1u);
            Invoke(G, "sceGxmSetBackLineFillLastPixelEnable", c, 1u);
            Invoke(G, "sceGxmSetFrontPointLineWidth", c, 2u);
            Invoke(G, "sceGxmSetBackPointLineWidth", c, 2u);
            Invoke(G, "sceGxmSetFrontDepthBias", c, 1, 2);
            Invoke(G, "sceGxmSetBackDepthBias", c, 1, 2);
            Invoke(G, "sceGxmSetFrontStencilFunc", c, 0x0E000000u, 1u, 2u, 3u, 0xFFu, 0xFFu);
            Invoke(G, "sceGxmSetBackStencilFunc", c, 0x0E000000u, 1u, 2u, 3u, 0xFFu, 0xFFu);
            Invoke(G, "sceGxmSetFrontStencilRef", c, 5u);
            Invoke(G, "sceGxmSetBackStencilRef", c, 6u);
            Invoke(G, "sceGxmSetViewport", c, 480f, 480f, 272f, -272f, 0.5f, 0.5f);
            Invoke(G, "sceGxmSetViewportEnable", c, 0u);
            Invoke(G, "sceGxmSetRegionClip", c, 0x40000000u, 0u, 0u, 959u, 543u);
            Invoke(G, "sceGxmSetDefaultRegionClipAndViewport", c, 959u, 543u);
            Invoke(G, "sceGxmSetFrontVisibilityTestEnable", c, 1u);
            Invoke(G, "sceGxmSetBackVisibilityTestEnable", c, 1u);
            Invoke(G, "sceGxmSetFrontVisibilityTestIndex", c, 1u);
            Invoke(G, "sceGxmSetBackVisibilityTestIndex", c, 1u);
            Invoke(G, "sceGxmSetFrontVisibilityTestOp", c, 1u);
            Invoke(G, "sceGxmSetBackVisibilityTestOp", c, 0u);
            Invoke(G, "sceGxmSetPrecomputedVertexState", c, 0u);
            Invoke(G, "sceGxmSetPrecomputedFragmentState", c, 0u);
            Invoke(G, "sceGxmSetVertexProgram", c, 0u);
            Invoke(G, "sceGxmSetFragmentProgram", c, 0u);
        }
        Assert.Equal(0, Call(G, "sceGxmSetValidationEnable", ctx, 1u));
    }

    [Fact]
    public void DeferredContextsRecordCommandLists()
    {
        Init();
        uint imm = Context(), rt = RenderTarget(64, 64);
        var (vp, fp, _) = Programs();
        uint p = _h.Alloc(0x30), o = Out();
        GuestMemory.W32(p + 0x18, _h.Alloc(0x2000));
        GuestMemory.W32(p + 0x1C, 0x2000);
        GuestMemory.W32(p + 0x20, _h.Alloc(0x4000));
        GuestMemory.W32(p + 0x24, 0x4000);
        GuestMemory.W32(p + 0x28, _h.Alloc(0x4000));
        GuestMemory.W32(p + 0x2C, 0x4000);
        Assert.Equal(INVALID_POINTER, Call(G, "sceGxmCreateDeferredContext", 0u, new Ptr<uint>(o)));
        Assert.Equal(0, Call(G, "sceGxmCreateDeferredContext", p, new Ptr<uint>(o)));
        uint def = GuestMemory.R32(o), type = Out();
        Assert.Equal(0, Call(G, "sceGxmGetContextType", def, new Ptr<uint>(type)));
        Assert.Equal(1u, GuestMemory.R32(type)); // DEFERRED
        // immediate-only calls refuse it, deferred-only calls refuse the immediate one
        Assert.Equal(INVALID_VALUE, Call(G, "sceGxmBeginScene", def, 0u, rt, 0u, 0u, 0u, 0u, 0u));
        Assert.Equal(INVALID_VALUE, Call(G, "sceGxmEndScene", def, 0u, 0u));
        Assert.Equal(INVALID_VALUE, Call(G, "sceGxmMidSceneFlush", def, 0u, 0u, 0u));
        Assert.Equal(INVALID_VALUE, Call(G, "sceGxmVertexFence", def));
        Assert.Equal(INVALID_VALUE, Call(G, "sceGxmSetVisibilityBuffer", def, 0u, 0u));
        Assert.Equal(INVALID_VALUE, Call(G, "sceGxmBeginCommandList", imm));
        Assert.Equal(INVALID_POINTER, Call(G, "sceGxmBeginCommandList", 0x1234u));

        uint mem = Out();
        Assert.Equal(0, Call(G, "sceGxmGetDeferredContextVdmBuffer", def, new Ptr<uint>(mem)));
        Assert.Equal(GuestMemory.R32(p + 0x18), GuestMemory.R32(mem));
        Assert.Equal(0, Call(G, "sceGxmGetDeferredContextVertexBuffer", def, new Ptr<uint>(mem)));
        Assert.Equal(0, Call(G, "sceGxmGetDeferredContextFragmentBuffer", def, new Ptr<uint>(mem)));
        Assert.Equal(INVALID_POINTER, Call(G, "sceGxmGetDeferredContextVdmBuffer", def, new Ptr<uint>(0)));
        // a buffer must be at least the minimum size, and a non-NULL one needs a size
        Assert.Equal(INVALID_VALUE, Call(G, "sceGxmSetDeferredContextVertexBuffer", def, GuestMemory.R32(p + 0x20), 0u));
        Assert.Equal(INVALID_VALUE, Call(G, "sceGxmSetDeferredContextFragmentBuffer", def, GuestMemory.R32(p + 0x28), 16u));
        Assert.Equal(0, Call(G, "sceGxmSetDeferredContextVertexBuffer", def, GuestMemory.R32(p + 0x20), 0x4000u));
        Assert.Equal(0, Call(G, "sceGxmSetDeferredContextFragmentBuffer", def, GuestMemory.R32(p + 0x28), 0x4000u));
        Assert.Equal(0, Call(G, "sceGxmSetDeferredContextVdmBuffer", def, GuestMemory.R32(p + 0x18), 0x2000u));

        uint idx = _h.Alloc(16), list = _h.Alloc(32), empty = _h.Alloc(32);
        Assert.Equal(NOT_WITHIN_COMMAND_LIST, Call(G, "sceGxmDraw", def, 0u, 0u, idx, 3u));
        Assert.Equal(NOT_WITHIN_COMMAND_LIST, Call(G, "sceGxmEndCommandList", def, list));
        // an empty list
        Assert.Equal(0, Call(G, "sceGxmBeginCommandList", def));
        Assert.Equal(0, Call(G, "sceGxmEndCommandList", def, empty));
        // a list with one draw
        Assert.Equal(0, Call(G, "sceGxmBeginCommandList", def));
        Assert.Equal(WITHIN_COMMAND_LIST, Call(G, "sceGxmBeginCommandList", def));
        Assert.Equal(WITHIN_COMMAND_LIST, Call(G, "sceGxmGetDeferredContextVdmBuffer", def, new Ptr<uint>(mem)));
        Assert.Equal(WITHIN_COMMAND_LIST, Call(G, "sceGxmSetDeferredContextVdmBuffer", def, 0u, 0u));
        Assert.Equal(WITHIN_COMMAND_LIST, Call(G, "sceGxmDestroyDeferredContext", def));
        Invoke(G, "sceGxmSetVertexProgram", def, vp);
        Invoke(G, "sceGxmSetFragmentProgram", def, fp);
        Call(G, "sceGxmSetVertexStream", def, 0u, _h.Alloc(36));
        Assert.Equal(0, Call(G, "sceGxmDraw", def, 0u, 0u, idx, 3u));
        Assert.Equal(INVALID_POINTER, Call(G, "sceGxmEndCommandList", def, 0u));
        Assert.Equal(0, Call(G, "sceGxmEndCommandList", def, list));

        // executing needs an immediate context inside a scene
        Assert.Equal(INVALID_POINTER, Call(G, "sceGxmExecuteCommandList", imm, 0u));
        Assert.Equal(INVALID_VALUE, Call(G, "sceGxmExecuteCommandList", def, list));
        Assert.Equal(NOT_WITHIN_SCENE, Call(G, "sceGxmExecuteCommandList", imm, list));
        Assert.Equal(0, Call(G, "sceGxmBeginScene", imm, 0u, rt, 0u, 0u, 0u, 0u, 0u));
        Assert.Equal(INVALID_POINTER, Call(G, "sceGxmExecuteCommandList", imm, _h.Alloc(32))); // not a list
        Assert.Equal(0, Call(G, "sceGxmExecuteCommandList", imm, empty));
        Assert.Equal(0, Call(G, "sceGxmExecuteCommandList", imm, list));
        Assert.Equal(0, Call(G, "sceGxmExecuteCommandList", imm, list)); // a list runs as often as needed
        Assert.Equal(0, Call(G, "sceGxmEndScene", imm, 0u, 0u));
        Finish();
        Assert.Equal(2, _rec.Draws);
        Assert.Equal(0, Call(G, "sceGxmDestroyDeferredContext", def));
        Assert.Equal(INVALID_POINTER, Call(G, "sceGxmDestroyDeferredContext", def));
        Assert.Equal(INVALID_VALUE, Call(G, "sceGxmDestroyDeferredContext", imm));
    }

    [Fact]
    public void PrecomputedDrawsAndStates()
    {
        Init();
        uint ctx = Context(), rt = RenderTarget(64, 64);
        var (vp, fp, _) = Programs();
        Assert.Equal(4u, (uint)Invoke(G, "sceGxmGetPrecomputedDrawSize", vp)!); // one stream
        Assert.Equal(0u, (uint)Invoke(G, "sceGxmGetPrecomputedDrawSize", 0x1234u)!);
        Assert.Equal(8u, (uint)Invoke(G, "sceGxmGetPrecomputedVertexStateSize", vp)!); // no buffers or textures
        Assert.Equal(8u, (uint)Invoke(G, "sceGxmGetPrecomputedFragmentStateSize", fp)!);
        uint draw = _h.Alloc(32), extra = _h.Alloc(64), idx = _h.Alloc(16), vtx = _h.Alloc(36);
        Assert.Equal(INVALID_POINTER, Call(G, "sceGxmPrecomputedDrawInit", 0u, vp, extra));
        Assert.Equal(INVALID_ALIGNMENT, Call(G, "sceGxmPrecomputedDrawInit", draw, vp, extra + 4));
        Assert.Equal(0, Call(G, "sceGxmPrecomputedDrawInit", draw, vp, extra));
        Assert.Equal(INVALID_VALUE, Call(G, "sceGxmPrecomputedDrawSetVertexStream", draw, 16u, vtx));
        Assert.Equal(INVALID_POINTER, Call(G, "sceGxmPrecomputedDrawSetVertexStream", draw, 0u, 0u));
        Assert.Equal(0, Call(G, "sceGxmPrecomputedDrawSetVertexStream", draw, 0u, vtx));
        Assert.Equal(vtx, GuestMemory.R32(extra));
        uint streams = _h.Alloc(4);
        GuestMemory.W32(streams, vtx);
        Assert.Equal(0, Call(G, "sceGxmPrecomputedDrawSetAllVertexStreams", draw, streams));
        Assert.Equal(INVALID_POINTER, Call(G, "sceGxmPrecomputedDrawSetAllVertexStreams", draw, 0u));
        Assert.Equal(INVALID_POINTER, Call(G, "sceGxmPrecomputedDrawSetParams", draw, 0u, 0u, 0u, 3u));
        Assert.Equal(0, Call(G, "sceGxmPrecomputedDrawSetParams", draw, 0u, 0u, idx, 3u));
        Assert.Equal(INVALID_VALUE, Call(G, "sceGxmPrecomputedDrawSetParamsInstanced", draw, 0u, 0u, idx, 6u, 0u));
        Assert.Equal(0, Call(G, "sceGxmPrecomputedDrawSetParamsInstanced", draw, 0u, 0u, idx, 6u, 3u));
        Assert.Equal(2u, GuestMemory.R32(draw + 28)); // instances

        uint vs = _h.Alloc(16), vsExtra = _h.Alloc(16), fs = _h.Alloc(16), fsExtra = _h.Alloc(16);
        Assert.Equal(0, Call(G, "sceGxmPrecomputedVertexStateInit", vs, vp, vsExtra));
        Assert.Equal(0, Call(G, "sceGxmPrecomputedFragmentStateInit", fs, fp, fsExtra));
        Assert.Equal(INVALID_ALIGNMENT, Call(G, "sceGxmPrecomputedFragmentStateInit", fs, fp, fsExtra + 8));
        Assert.Equal(INVALID_POINTER, Call(G, "sceGxmPrecomputedVertexStateInit", vs, 0u, vsExtra));
        uint ubs = _h.Alloc(14 * 4);
        Assert.Equal(0, Call(G, "sceGxmPrecomputedVertexStateSetAllUniformBuffers", vs, ubs));
        Assert.Equal(0, Call(G, "sceGxmPrecomputedFragmentStateSetAllUniformBuffers", fs, ubs));
        Assert.Equal(INVALID_POINTER, Call(G, "sceGxmPrecomputedVertexStateSetAllUniformBuffers", vs, 0u));
        Assert.Equal(0, Call(G, "sceGxmPrecomputedVertexStateSetAllTextures", vs, _h.Alloc(16)));
        Assert.Equal(0, Call(G, "sceGxmPrecomputedFragmentStateSetAllTextures", fs, _h.Alloc(16)));
        Assert.Equal(INVALID_VALUE, Call(G, "sceGxmPrecomputedVertexStateSetUniformBuffer", vs, 14u, ubs));
        Assert.Equal(0, Call(G, "sceGxmPrecomputedFragmentStateSetUniformBuffer", fs, 13u, ubs));
        // no default uniform buffer slot (no buffers): set is accepted and ignored, get reads 0
        Assert.Equal(0, Call(G, "sceGxmPrecomputedVertexStateSetDefaultUniformBuffer", vs, ubs));
        Assert.Equal(0, Call(G, "sceGxmPrecomputedFragmentStateSetDefaultUniformBuffer", fs, ubs));
        Assert.Equal(INVALID_POINTER, Call(G, "sceGxmPrecomputedFragmentStateSetDefaultUniformBuffer", fs, 0u));
        Assert.Equal(0u, (uint)Invoke(G, "sceGxmPrecomputedVertexStateGetDefaultUniformBuffer", vs)!);
        Assert.Equal(0u, (uint)Invoke(G, "sceGxmPrecomputedFragmentStateGetDefaultUniformBuffer", fs)!);

        Assert.Equal(INVALID_POINTER, Call(G, "sceGxmDrawPrecomputed", ctx, 0u));
        Assert.Equal(NOT_WITHIN_SCENE, Call(G, "sceGxmDrawPrecomputed", ctx, draw));
        Assert.Equal(0, Call(G, "sceGxmBeginScene", ctx, 0u, rt, 0u, 0u, 0u, 0u, 0u));
        Invoke(G, "sceGxmSetPrecomputedVertexState", ctx, vs);
        Invoke(G, "sceGxmSetPrecomputedFragmentState", ctx, fs);
        Assert.Equal(0, Call(G, "sceGxmDrawPrecomputed", ctx, draw)); // the states give the programs
        Invoke(G, "sceGxmSetVertexProgram", ctx, vp);
        Invoke(G, "sceGxmSetFragmentProgram", ctx, fp);
        Call(G, "sceGxmSetVertexStream", ctx, 0u, vtx);
        Assert.Equal(0, Call(G, "sceGxmDraw", ctx, 0u, 0u, idx, 3u)); // the precomputed states also apply to sceGxmDraw
        Assert.Equal(0, Call(G, "sceGxmEndScene", ctx, 0u, 0u));
        Finish();
        Assert.Equal(2, _rec.Draws);
    }

    [Fact]
    public void SurfacesAndTextures()
    {
        // depth/stencil: the stride must be a non-zero multiple of 32 samples
        uint ds = _h.Alloc(0x20);
        Assert.Equal(INVALID_POINTER, Call(G, "sceGxmDepthStencilSurfaceInit", 0u, 0x00044000u, 0u, 64u, 0u, 0u));
        Assert.Equal(INVALID_VALUE, Call(G, "sceGxmDepthStencilSurfaceInit", ds, 0x00044000u, 0u, 48u, 0u, 0u));
        Assert.Equal(0, Call(G, "sceGxmDepthStencilSurfaceInit", ds, 0x00044000u, 0u, 64u, 0x81000000u, 0u));
        Assert.Equal(64u, (uint)Invoke(G, "sceGxmDepthStencilSurfaceGetStrideInSamples", ds)!);
        Assert.Equal(0x00044000u, (uint)Invoke(G, "sceGxmDepthStencilSurfaceGetFormat", ds)!);
        Assert.True((bool)Invoke(G, "sceGxmDepthStencilSurfaceIsEnabled", ds)!);
        Assert.Equal(1.0f, (float)Invoke(G, "sceGxmDepthStencilSurfaceGetBackgroundDepth", ds)!);
        Invoke(G, "sceGxmDepthStencilSurfaceSetBackgroundDepth", ds, 0.25f);
        Assert.Equal(0.25f, (float)Invoke(G, "sceGxmDepthStencilSurfaceGetBackgroundDepth", ds)!);
        Invoke(G, "sceGxmDepthStencilSurfaceSetBackgroundStencil", ds, 0x1A5u);
        Assert.Equal(0xA5u, (uint)Invoke(G, "sceGxmDepthStencilSurfaceGetBackgroundStencil", ds)!);
        Invoke(G, "sceGxmDepthStencilSurfaceSetBackgroundMask", ds, false);
        Assert.False((bool)Invoke(G, "sceGxmDepthStencilSurfaceGetBackgroundMask", ds)!);
        Invoke(G, "sceGxmDepthStencilSurfaceSetForceLoadMode", ds, 2u);
        Invoke(G, "sceGxmDepthStencilSurfaceSetForceStoreMode", ds, 4u);
        Assert.Equal(2u, (uint)Invoke(G, "sceGxmDepthStencilSurfaceGetForceLoadMode", ds)!);
        Assert.Equal(4u, (uint)Invoke(G, "sceGxmDepthStencilSurfaceGetForceStoreMode", ds)!);
        Assert.Equal(0, Call(G, "sceGxmDepthStencilSurfaceInitDisabled", ds));
        Assert.False((bool)Invoke(G, "sceGxmDepthStencilSurfaceIsEnabled", ds)!);

        // colour surface limits and fields
        uint cs = _h.Alloc(0x40);
        Assert.Equal(INVALID_VALUE, Call(G, "sceGxmColorSurfaceInit", cs, 0u, 0u, 0u, 0u, 0u, 64u, 64u, 0u));
        Assert.Equal(INVALID_VALUE, Call(G, "sceGxmColorSurfaceInit", cs, 0u, 0u, 0u, 0u, 4097u, 64u, 4098u, 0u));
        Assert.Equal(INVALID_ALIGNMENT, Call(G, "sceGxmColorSurfaceInit", cs, 0u, 0u, 0u, 0u, 64u, 64u, 65u, 0u));
        Assert.Equal(INVALID_VALUE, Call(G, "sceGxmColorSurfaceInit", cs, 0u, 0u, 0u, 0u, 64u, 64u, 32u, 0u));
        Assert.Equal(INVALID_VALUE, Call(G, "sceGxmColorSurfaceInit", cs, 0u, 0u, 0u, 0u, 64u, 64u, 64u, 0x81000002u));
        Assert.Equal(0, Call(G, "sceGxmColorSurfaceInit", cs, 0x10000000u, 0x04000000u, 1u, 0u, 64u, 32u, 64u, 0x81000000u));
        Assert.Equal(0x10000000u, (uint)Invoke(G, "sceGxmColorSurfaceGetFormat", cs)!);
        Assert.Equal(0x04000000u, (uint)Invoke(G, "sceGxmColorSurfaceGetType", cs)!);
        Assert.Equal(1u, (uint)Invoke(G, "sceGxmColorSurfaceGetScaleMode", cs)!);
        Invoke(G, "sceGxmColorSurfaceSetScaleMode", cs, 0u);
        Assert.Equal(0u, (uint)Invoke(G, "sceGxmColorSurfaceGetScaleMode", cs)!);
        Assert.Equal(0, Call(G, "sceGxmColorSurfaceSetFormat", cs, 0x0u));
        Assert.Equal(INVALID_POINTER, Call(G, "sceGxmColorSurfaceSetDitherMode", 0u, 8u));

        // textures: power-of-two sizes for swizzled and cube, limits on size and mips
        uint t = _h.Alloc(16);
        Assert.Equal(INVALID_POINTER, Call(G, "sceGxmTextureInitSwizzled", 0u, 0u, 0u, 64u, 64u, 0u));
        Assert.Equal(INVALID_ALIGNMENT, Call(G, "sceGxmTextureInitSwizzled", t, 0x81000000u, 0x0C000000u, 60u, 64u, 0u));
        Assert.Equal(INVALID_ALIGNMENT, Call(G, "sceGxmTextureInitCube", t, 0x81000000u, 0x0C000000u, 64u, 0u, 0u));
        Assert.Equal(INVALID_VALUE, Call(G, "sceGxmTextureInitLinear", t, 0x81000000u, 0x0C000000u, 8192u, 64u, 0u));
        Assert.Equal(INVALID_VALUE, Call(G, "sceGxmTextureInitTiled", t, 0x81000000u, 0x0C000000u, 64u, 64u, 14u));
        foreach (var fn in new[] { "sceGxmTextureInitSwizzledArbitrary", "sceGxmTextureInitTiled", "sceGxmTextureInitCubeArbitrary", "sceGxmTextureInitCube", "sceGxmTextureInitSwizzled" })
        {
            Assert.Equal(0, Call(G, fn, t, 0x81000000u, 0x0C000000u, 64u, 32u, 2u));
            Assert.Equal(64u, (uint)Invoke(G, "sceGxmTextureGetWidth", t)!);
            Assert.Equal(32u, (uint)Invoke(G, "sceGxmTextureGetHeight", t)!);
            Assert.Equal(0x81000000u, (uint)Invoke(G, "sceGxmTextureGetData", t)!);
        }
        Assert.Equal(0x0C000000u, (uint)Invoke(G, "sceGxmTextureGetFormat", t)!);
        Assert.Equal(0u, (uint)Invoke(G, "sceGxmTextureGetType", t)!); // swizzled
        Assert.Equal(UNSUPPORTED, Call(G, "sceGxmTextureSetStride", t, 256u));
        Assert.Equal(0, Call(G, "sceGxmTextureSetData", t, 0x82000000u));
        Assert.Equal(0x82000000u, (uint)Invoke(G, "sceGxmTextureGetData", t)!);
        Assert.Equal(0, Call(G, "sceGxmTextureSetMagFilter", t, 1u));
        Assert.Equal(1u, (uint)Invoke(G, "sceGxmTextureGetMagFilter", t)!);
        Assert.Equal(0, Call(G, "sceGxmTextureSetMinFilter", t, 1u));
        Assert.Equal(1u, (uint)Invoke(G, "sceGxmTextureGetMinFilter", t)!);
        Assert.Equal(0, Call(G, "sceGxmTextureSetMipFilter", t, 1u));
        Assert.Equal(1u, (uint)Invoke(G, "sceGxmTextureGetMipFilter", t)!);
        Assert.Equal(0, Call(G, "sceGxmTextureSetUAddrMode", t, 3u));
        Assert.Equal(0, Call(G, "sceGxmTextureSetVAddrMode", t, 1u));
        Assert.Equal(3u, (uint)Invoke(G, "sceGxmTextureGetUAddrMode", t)!);
        Assert.Equal(1u, (uint)Invoke(G, "sceGxmTextureGetVAddrMode", t)!);
        Assert.Equal(0, Call(G, "sceGxmTextureSetVAddrModeSafe", t, 2u));
        Assert.Equal(2u, (uint)Invoke(G, "sceGxmTextureGetVAddrModeSafe", t)!);
        Assert.Equal(0, Call(G, "sceGxmTextureSetLodBias", t, 40u));
        Assert.Equal(40u, (uint)Invoke(G, "sceGxmTextureGetLodBias", t)!);
        Assert.Equal(0, Call(G, "sceGxmTextureSetLodMin", t, 5u));
        Assert.Equal(5u, (uint)Invoke(G, "sceGxmTextureGetLodMin", t)!);
        Assert.Equal(0, Call(G, "sceGxmTextureSetMipmapCount", t, 4u));
        Assert.Equal(4u, (uint)Invoke(G, "sceGxmTextureGetMipmapCount", t)!);
        Assert.Equal(0, Call(G, "sceGxmTextureSetPalette", t, 0x81000040u));
        Assert.Equal(0x81000040u, (uint)Invoke(G, "sceGxmTextureGetPalette", t)!);
        Assert.Equal(0, Call(G, "sceGxmTextureSetWidth", t, 128u));
        Assert.Equal(128u, (uint)Invoke(G, "sceGxmTextureGetWidth", t)!);
        Assert.Equal(0, Call(G, "sceGxmTextureSetHeight", t, 16u));
        Assert.Equal(16u, (uint)Invoke(G, "sceGxmTextureGetHeight", t)!);
        Assert.Equal(0, Call(G, "sceGxmTextureSetFormat", t, 0x05000000u));
        Assert.Equal(0x05000000u, (uint)Invoke(G, "sceGxmTextureGetFormat", t)!);
        Assert.Equal(INVALID_POINTER, Call(G, "sceGxmTextureSetFormat", 0u, 0u));
        Assert.Equal(INVALID_POINTER, Call(G, "sceGxmTextureSetWidth", 0u, 1u));
        Assert.Equal(INVALID_POINTER, Call(G, "sceGxmTextureSetHeight", 0u, 1u));
        Assert.Equal(0, Call(G, "sceGxmTextureValidate", t));
        Assert.Equal(INVALID_POINTER, Call(G, "sceGxmTextureValidate", 0u));
        // linear strided: 4-byte aligned strides between 4 and 128 KiB; mip and filter setters do not apply
        Assert.Equal(INVALID_ALIGNMENT, Call(G, "sceGxmTextureInitLinearStrided", t, 0x81000000u, 0x0C000000u, 16u, 16u, 66u));
        Assert.Equal(INVALID_VALUE, Call(G, "sceGxmTextureInitLinearStrided", t, 0x81000000u, 0x0C000000u, 16u, 16u, 0u));
        Assert.Equal(INVALID_VALUE, Call(G, "sceGxmTextureInitLinearStrided", t, 0x81000000u, 0x0C000000u, 5000u, 16u, 64u));
        Assert.Equal(0, Call(G, "sceGxmTextureInitLinearStrided", t, 0x81000000u, 0x0C000000u, 16u, 16u, 64u));
        Assert.Equal(0xC0000000u, (uint)Invoke(G, "sceGxmTextureGetType", t)!);
        Assert.Equal(INVALID_VALUE, Call(G, "sceGxmTextureSetStride", t, 0u));
        Assert.Equal(0u, (uint)Invoke(G, "sceGxmTextureGetMinFilter", t)!);
        Assert.Equal(0u, (uint)Invoke(G, "sceGxmTextureGetLodBias", t)!);
        Assert.Equal(0, Call(G, "sceGxmTextureSetMipmapCount", t, 4u)); // ignored
    }

    [Fact]
    public void TransfersRunInGpuOrder()
    {
        Init();
        const int W = 8, H = 4;
        uint src = _h.Alloc(W * H * 4), dst = _h.Alloc(W * H * 4), word = _h.Word(0), notif = _h.Alloc(8);
        GuestMemory.W32(notif, word);
        GuestMemory.W32(notif + 4, 5);
        // fill a 4x2 block at (2,1) of a linear 8x4 surface
        Assert.Equal(INVALID_POINTER, Call(G, "sceGxmTransferFill", 0u, 0x000C0000u, 0u, 0u, 0u, 1u, 1u, W * 4, 0u, 0u, 0u));
        Assert.Equal(0, Call(G, "sceGxmTransferFill", 0xFF00FF00u, 0x000C0000u, src, 2u, 1u, 4u, 2u, W * 4, 0u, 0u, notif));
        Assert.Equal(0, Call(G, "sceGxmTransferFinish"));
        Assert.Equal(5u, GuestMemory.R32(word));
        Assert.Equal(0xFF00FF00u, GuestMemory.R32(src + (1 * W + 2) * 4));
        Assert.Equal(0xFF00FF00u, GuestMemory.R32(src + (2 * W + 5) * 4));
        Assert.Equal(0u, GuestMemory.R32(src + (1 * W + 1) * 4));
        Assert.Equal(0u, GuestMemory.R32(src + (3 * W + 2) * 4));
        // copy with a colour key: key mode 1 copies only the pixels matching the key
        for (uint i = 0; i < W * H; i++)
        {
            if (GuestMemory.R32(src + i * 4) == 0)
            {
                GuestMemory.W32(src + i * 4, 0x11111111);
            }
        }

        Assert.Equal(INVALID_POINTER, Call(G, "sceGxmTransferCopy", 1u, 1u, 0u, 0u, 0u, 0x000C0000u, 0u, 0u, 0u, 0u, W * 4, 0x000C0000u, 0u, dst, 0u, 0u, W * 4, 0u, 0u, 0u));
        Assert.Equal(0, Call(G, "sceGxmTransferCopy", (uint)W, (uint)H, 0xFF00FF00u, 0xFFFFFFFFu, 1u, 0x000C0000u, 0u, src, 0u, 0u, W * 4,
            0x000C0000u, 0u, dst, 0u, 0u, W * 4, 0u, 0u, 0u));
        Assert.Equal(0, Call(G, "sceGxmTransferFinish"));
        Assert.Equal(0xFF00FF00u, GuestMemory.R32(dst + (1 * W + 2) * 4));
        Assert.Equal(0u, GuestMemory.R32(dst)); // not the key: left alone
        // a plain copy, then a 2x downscale averaging each 2x2 block
        Assert.Equal(0, Call(G, "sceGxmTransferCopy", (uint)W, (uint)H, 0u, 0u, 0u, 0x000C0000u, 0u, src, 0u, 0u, W * 4,
            0x000C0000u, 0u, dst, 0u, 0u, W * 4, 0u, 0u, 0u));
        uint half = _h.Alloc(W / 2 * H / 2 * 4);
        Assert.Equal(INVALID_POINTER, Call(G, "sceGxmTransferDownscale", 0x000C0000u, 0u, 0u, 0u, (uint)W, (uint)H, W * 4, 0x000C0000u, half, 0u, 0u, W / 2 * 4, 0u, 0u, 0u));
        Assert.Equal(0, Call(G, "sceGxmTransferDownscale", 0x000C0000u, dst, 0u, 0u, (uint)W, (uint)H, W * 4, 0x000C0000u, half, 0u, 0u, W / 2 * 4, 0u, 0u, 0u));
        Assert.Equal(0, Call(G, "sceGxmTransferFinish"));
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
        Call(G, "sceGxmSyncObjectCreate", new Ptr<uint>(so));
        a = GuestMemory.R32(so);
        Call(G, "sceGxmSyncObjectCreate", new Ptr<uint>(so));
        b = GuestMemory.R32(so);
        Assert.Equal(INVALID_POINTER, Call(G, "sceGxmDisplayQueueAddEntry", 0u, b, 0u));
        uint data = _h.Word(0x1234);
        _h.OnGuest(_ =>
        {
            for (int i = 0; i < 4; i++)
            {
                Assert.Equal(0, Call(G, "sceGxmDisplayQueueAddEntry", i % 2 == 0 ? a : b, i % 2 == 0 ? b : a, data));
            }

            Assert.Equal(0, Call(G, "sceGxmDisplayQueueFinish"));
            Assert.Equal(0, Call(G, "sceGxmWaitEvent"));
        });
    }
}
