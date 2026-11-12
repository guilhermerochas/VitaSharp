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
    private const int UNINITIALIZED = unchecked((int)0x805B0000), ALREADY_INITIALIZED = unchecked((int)0x805B0001),
        INVALID_VALUE = unchecked((int)0x805B0003), INVALID_POINTER = unchecked((int)0x805B0004), INVALID_ALIGNMENT = unchecked((int)0x805B0005),
        NOT_WITHIN_SCENE = unchecked((int)0x805B0006), WITHIN_SCENE = unchecked((int)0x805B0007), NULL_PROGRAM = unchecked((int)0x805B0008),
        UNSUPPORTED = unchecked((int)0x805B0009), WITHIN_COMMAND_LIST = unchecked((int)0x805B002C), NOT_WITHIN_COMMAND_LIST = unchecked((int)0x805B002D);

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
            }
        }
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

    private uint RenderTarget(ushort w, ushort h)
    {
        uint p = _harness.Alloc(0x14), o = Out();
        GuestMemory.Write(p + 4, w);
        GuestMemory.Write(p + 6, h);
        GuestMemory.Write(p + 8, (ushort)1);
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
        Assert.Equal(INVALID_POINTER, Call(Gxm, "sceGxmBeginScene", ctx, 0u, 0x1234u, 0u, 0u, 0u, color, depth));
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
        Assert.Equal(0, Call(Gxm, "_sceGxmBeginScene", ctx, 0u, rt, region, 0u, 0u, off, 0u));
        Assert.Equal(0, Call(Gxm, "sceGxmEndScene", ctx, 0u, 0u));
        Finish();
        Assert.Null(_rec.LastBegin!.Color);
        Assert.Equal(0, Call(Gxm, "sceGxmBeginSceneEx", ctx, 0u, rt, 0u, 0u, 0u, color, depth, 0u));
        Assert.Equal(0, Call(Gxm, "sceGxmEndScene", ctx, 0u, 0u));
        Assert.Equal(0, Call(Gxm, "sceGxmTransferFinish"));
        Assert.NotNull(_rec.LastBegin!.Color);
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
        Assert.Equal(0, Call(Gxm, "sceGxmBeginScene", ctx, 0u, rt, 0u, 0u, 0u, 0u, 0u));
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
        Assert.Equal(0, Call(Gxm, "sceGxmBeginScene", imm, 0u, rt, 0u, 0u, 0u, 0u, 0u));
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
        Assert.Equal(0, Call(Gxm, "sceGxmBeginScene", ctx, 0u, rt, 0u, 0u, 0u, 0u, 0u));
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
        Assert.Equal(0, Call(Gxm, "sceGxmColorSurfaceSetFormat", cs, 0x0u));
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
}
