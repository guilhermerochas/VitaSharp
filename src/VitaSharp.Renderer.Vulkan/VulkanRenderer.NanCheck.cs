using Silk.NET.Vulkan;
using System.Diagnostics;
using System.Text;
using VitaSharp.Core.Gpu;
using VitaSharp.Core.Gxm.Shader;
using VitaSharp.Core.Util;
using Buffer = Silk.NET.Vulkan.Buffer;
using ShaderKind = Silk.NET.Shaderc.ShaderKind;

namespace VitaSharp.Renderer.Vulkan;

/// <summary>
/// NaN/Inf detection (see docs/notes/debug-mode.md, "NaN/Inf detection").
/// <para>
/// Layer 1, debug mode (follows F9): at the end of every scene whose colour surface is a float format (F16x4 HDR,
/// U2F10F10F10, F11F11F10, F16/F32...), a compute shader reads every pixel of the surface and counts the pixels with
/// a NaN, an Inf, a huge finite value (&gt;= 65000 for 16-bit and packed floats, 1e30 for F32) or a negative value
/// into the frame's host-visible result buffer. The counts are read when the frame retires (non-blocking, see
/// VulkanRenderer.Sync.cs) and fed to a per-target history: `[nan]` lines when NaN/Inf appears in a target, while it
/// stays (rate-limited) and when it is gone; a target going from clean to NaN/Inf raises <see cref="AnomalyDetected"/>
/// ("nan"), i.e. an automatic capture with the replay ring under the usual limits.
/// </para>
/// <para>
/// Layer 2, VITASHARP_DEBUG_NAN=draws (chosen at launch, heavier): every generated fragment shader also records
/// fragments whose colour output has a NaN/Inf component, per draw id, in a 4096-entry table of the frame's buffer
/// (<see cref="ShaderGenerator.NanDraws"/>); at retire the hit draws are logged as `[nan-draw]` lines with their
/// programs, render target and textures (the same fields as draws.txt).
/// </para>
/// Nothing here runs with debug mode off and the draws mode off (one flag test per scene end, one null test per retire).
/// </summary>
public sealed unsafe partial class VulkanRenderer
{
    /// <summary>Anomaly kind raised when NaN/Inf appears in a render target.</summary>
    public const string NanAnomalyKind = "nan";

    private const int NanScanSlots = 64, NanScanWords = 8;
    private const int NanScanBytes = NanScanSlots * NanScanWords * 4;            // 2 KB
    private const int NanDrawBytes = ShaderGenerator.NanDrawSlots * 4 * 4;       // 64 KB (draws mode only)
    /// <summary>Lines of one target: a new episode is logged at most every this many presents (others are counted),
    /// an ongoing one repeats every <see cref="NanStillEvery"/>; NaN gone for <see cref="NanClearAfter"/> presents = clean.</summary>
    private const int NanEpisodeEvery = 30, NanStillEvery = 300, NanClearAfter = 3;
    /// <summary>At most one "nan" anomaly (automatic capture request) every this many presents.</summary>
    private const int NanAnomalyEvery = 300;

    private sealed class NanFrame
    {
        public Buffer Buf;
        public DeviceMemory Mem;
        public uint* Ptr;
        public bool Used;
        public readonly List<NanScan> Scans = new();
        public readonly List<NanDrawInfo> Draws = new();
        public readonly List<Core.Gpu.GxmTextureDesc> Textures = new();
    }

    private struct NanScan
    {
        public uint Address, W, H, ColorFormat;
        public Format Format;
        public long Present, GuestFrame;
        public int Scene, FirstDraw, LastDraw;
    }

    private struct NanDrawInfo
    {
        public int Id, Scene, TexStart;
        public long Present, GuestFrame;
        public ulong Vs, Fs, VsHint, FsHint;
        public uint Rt, RtFormat, RtW, RtH, TexMask, IndexCount, Instances;
        public byte Prim;
        public string Msaa;
    }

    private sealed class NanTargetHistory
    {
        public uint Address, W, H, ColorFormat;
        public bool Bad, EpisodeLogged;
        public long BadSince, LastBad = -1, LastLine = long.MinValue / 2, LastCounted = -1, FirstBad = -1, MaxAt;
        public int Episodes, Suppressed;
        public long BadPresents, Scans, MaxNan, MaxInf, MaxHuge, MaxNeg;
        public string LastResult = "";
        public readonly List<(long From, long To, long MaxNan)> History = new();
    }

    private sealed class NanDrawHistory
    {
        public ulong Vs, Fs, FsHint;
        public uint Rt;
        public int Presents;
        public long Fragments, First = -1, Last = -1, LastPresent = -1, LoggedPresent = -1;
        public string Example = "";
    }

    private DescriptorSetLayout _nanSetLayout;
    private PipelineLayout _nanPipelineLayout;
    private Pipeline _nanPipeline;
    private ShaderModule _nanModule;
    private Sampler _nanSampler;
    private bool _nanFailed, _nanReady;
    private readonly Dictionary<(uint, uint, uint, uint), NanTargetHistory> _nanTargets = new();
    private readonly Dictionary<(ulong, ulong, ulong, uint), NanDrawHistory> _nanDraws = new();
    private long _nanLastAnomaly = long.MinValue / 2, _nanLastDrawSummary = long.MinValue / 2;
    private long _nanSceneEndPresent = -1;
    private int _nanSceneEndDraw;
    // cost accounting
    private long _nanScans, _nanScanTicks, _nanRetireTicks, _nanRetired, _nanDropped, _nanDrawFrames, _nanDrawHits;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct NanPush
    {
        public uint Slot, W, H; public float Huge;
    }

    private const string NanScanGlsl = """
#version 450
layout(local_size_x = 16, local_size_y = 16) in;
layout(set = 0, binding = 0) uniform sampler2D img;
layout(std430, set = 0, binding = 1) buffer Res { uint r[]; } res;
layout(push_constant) uniform P { uint slot; uint w; uint h; float huge; } pc;
shared uint sNan, sInf, sHuge, sNeg, sFirst, sFirstHuge;
void main() {
    if (gl_LocalInvocationIndex == 0u) { sNan = 0u; sInf = 0u; sHuge = 0u; sNeg = 0u; sFirst = 0u; sFirstHuge = 0u; }
    barrier();
    uvec2 p = gl_GlobalInvocationID.xy;
    if (p.x < pc.w && p.y < pc.h) {
        vec4 v = texelFetch(img, ivec2(p), 0);
        bool n = any(isnan(v)), i = any(isinf(v));
        uint inv = 0xFFFFFFFFu - ((p.y << 16) | p.x);
        if (n) atomicAdd(sNan, 1u);
        if (i) atomicAdd(sInf, 1u);
        if (n || i) atomicMax(sFirst, inv);
        else if (any(greaterThanEqual(abs(v), vec4(pc.huge)))) { atomicAdd(sHuge, 1u); atomicMax(sFirstHuge, inv); }
        if (any(lessThan(v, vec4(0.0)))) atomicAdd(sNeg, 1u);
    }
    barrier();
    if (gl_LocalInvocationIndex == 0u) {
        uint b = pc.slot * 8u;
        if (sNan != 0u) atomicAdd(res.r[b], sNan);
        if (sInf != 0u) atomicAdd(res.r[b + 1u], sInf);
        if (sHuge != 0u) atomicAdd(res.r[b + 2u], sHuge);
        if (sNeg != 0u) atomicAdd(res.r[b + 3u], sNeg);
        if (sFirst != 0u) atomicMax(res.r[b + 4u], sFirst);
        if (sFirstHuge != 0u) atomicMax(res.r[b + 5u], sFirstHuge);
    }
}
""";

    private static bool NanScannable(Format f) => f is Format.R16G16B16A16Sfloat or Format.R16G16Sfloat or Format.R16Sfloat
        or Format.R32Sfloat or Format.R32G32Sfloat or Format.B10G11R11UfloatPack32 or Format.E5B9G9R9UfloatPack32;

    /// <summary>Guest formats that cannot hold negative values (a negative value in the host image is an emulation
    /// difference: the hardware stores 0).</summary>
    private static bool NanUnsignedGuest(uint colorFormat) => (colorFormat & 0xF1800000) is 0x41000000 or 0x21000000 or 0x31000000;

    private static string NanFormatName(uint colorFormat) => (colorFormat & 0xF1800000) switch
    {
        0x01000000 => "F16x4",
        0x41000000 => "U2F10F10F10",
        0x21000000 => "F11F11F10",
        0x31000000 => "SE5M9M9M9",
        0x10800000 => "F32",
        0x11000000 => "F32x2",
        0x00800000 => "F16x2",
        0xF0000000 => "F16",
        _ => $"fmt 0x{colorFormat:X8}",
    };

    private bool EnsureNanPipeline()
    {
        if (_nanReady)
        {
            return true;
        }

        if (_nanFailed)
        {
            return false;
        }

        try
        {
            DescriptorSetLayoutBinding* b = stackalloc DescriptorSetLayoutBinding[2];
            b[0] = new DescriptorSetLayoutBinding(0, DescriptorType.CombinedImageSampler, 1, ShaderStageFlags.ComputeBit);
            b[1] = new DescriptorSetLayoutBinding(1, DescriptorType.StorageBuffer, 1, ShaderStageFlags.ComputeBit);
            var ci = new DescriptorSetLayoutCreateInfo { SType = StructureType.DescriptorSetLayoutCreateInfo, BindingCount = 2, PBindings = b };
            _vk.CreateDescriptorSetLayout(_dev, in ci, null, out _nanSetLayout).Ok("nan set layout");
            DescriptorSetLayout layout = _nanSetLayout;
            var pcr = new PushConstantRange(ShaderStageFlags.ComputeBit, 0, (uint)sizeof(NanPush));
            var pli = new PipelineLayoutCreateInfo { SType = StructureType.PipelineLayoutCreateInfo, SetLayoutCount = 1, PSetLayouts = &layout, PushConstantRangeCount = 1, PPushConstantRanges = &pcr };
            _vk.CreatePipelineLayout(_dev, in pli, null, out _nanPipelineLayout).Ok("nan pipeline layout");
            byte[] spv = _compiler.Compile(NanScanGlsl, ShaderKind.ComputeShader, out string? err) ?? throw new InvalidOperationException($"NaN scan shader: {err}");
            _nanModule = CreateShaderModule(spv);
            byte* entry = stackalloc byte[5] { (byte)'m', (byte)'a', (byte)'i', (byte)'n', 0 };
            var cpi = new ComputePipelineCreateInfo
            {
                SType = StructureType.ComputePipelineCreateInfo,
                Stage = new PipelineShaderStageCreateInfo { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.ComputeBit, Module = _nanModule, PName = entry },
                Layout = _nanPipelineLayout,
            };
            _vk.CreateComputePipelines(_dev, default, 1, in cpi, null, out _nanPipeline).Ok("nan compute pipeline");
            var sci = new SamplerCreateInfo
            {
                SType = StructureType.SamplerCreateInfo,
                MagFilter = Filter.Nearest,
                MinFilter = Filter.Nearest,
                MipmapMode = SamplerMipmapMode.Nearest,
                AddressModeU = SamplerAddressMode.ClampToEdge,
                AddressModeV = SamplerAddressMode.ClampToEdge,
                AddressModeW = SamplerAddressMode.ClampToEdge,
                MaxLod = 0,
            };
            _vk.CreateSampler(_dev, in sci, null, out _nanSampler).Ok("nan sampler");
            _nanReady = true;
            Dbg("NaN/Inf scan on: float render targets are checked at the end of every scene");
            return true;
        }
        catch (Exception e)
        {
            _nanFailed = true;
            Log.Warn($"[nan] NaN/Inf scan unavailable ({e.Message})");
            return false;
        }
    }

    private NanFrame EnsureNanFrame(Frame f)
    {
        if (f.Nan is { } n)
        {
            return n;
        }

        ulong size = NanScanBytes + (ShaderGenerator.NanDraws ? (ulong)NanDrawBytes : 0);
        (Buffer buf, DeviceMemory mem, nint ptr) = _core.CreateHostBuffer(size, BufferUsageFlags.StorageBufferBit, MemCat.DebugRing);
        new Span<byte>((void*)ptr, (int)size).Clear();
        return f.Nan = new NanFrame { Buf = buf, Mem = mem, Ptr = (uint*)ptr };
    }

    /// <summary>Debug mode, end of a scene: queue the NaN/Inf scan of its colour surface (no wait).</summary>
    private void NanScanScene(RenderTarget rt)
    {
        int lastDraw = _drawSerial;
        int firstDraw = _nanSceneEndPresent == _presentCount ? _nanSceneEndDraw + 1 : 1;
        _nanSceneEndPresent = _presentCount;
        _nanSceneEndDraw = lastDraw;
        if (!NanScannable(rt.Format) || rt.Width == 0 || rt.Height == 0)
        {
            return;
        }
        // raw register bits (packed G-buffer words in an F16x4 surface...) are not colours
        if (_rawLogged.Contains((rt.Address, RawAttachmentFormat(rt.Format, 16))) || _rawLogged.Contains((rt.Address, RawAttachmentFormat(rt.Format, 32))))
        {
            return;
        }

        if (!EnsureNanPipeline())
        {
            return;
        }

        long t0 = Stopwatch.GetTimestamp();
        // the frame may have been submitted mid-scene (Idle with results pending): record into the next one
        BeginFrameIfNeeded();
        Frame f = F;
        NanFrame nanFrame = EnsureNanFrame(f);
        if (nanFrame.Scans.Count >= NanScanSlots)
        {
            _nanDropped++;
            return;
        }
        int slot = nanFrame.Scans.Count;
        CommandBuffer cmd = f.Cmd;
        // the scene's colour writes (and resolves) before the compute reads
        var mb = new MemoryBarrier { SType = StructureType.MemoryBarrier, SrcAccessMask = AccessFlags.ColorAttachmentWriteBit | AccessFlags.TransferWriteBit, DstAccessMask = AccessFlags.ShaderReadBit };
        _vk.CmdPipelineBarrier(cmd, PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.TransferBit, PipelineStageFlags.ComputeShaderBit, 0, 1, in mb, 0, null, 0, null);
        DescriptorSet set = AllocSet(_nanSetLayout);
        var ii = new DescriptorImageInfo(_nanSampler, rt.View, ImageLayout.General);
        var bi = new DescriptorBufferInfo(nanFrame.Buf, 0, NanScanBytes);
        WriteDescriptorSet* w = stackalloc WriteDescriptorSet[2];
        w[0] = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DstSet = set, DstBinding = 0, DescriptorCount = 1, DescriptorType = DescriptorType.CombinedImageSampler, PImageInfo = &ii };
        w[1] = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DstSet = set, DstBinding = 1, DescriptorCount = 1, DescriptorType = DescriptorType.StorageBuffer, PBufferInfo = &bi };
        _vk.UpdateDescriptorSets(_dev, 2, w, 0, null);
        _vk.CmdBindPipeline(cmd, PipelineBindPoint.Compute, _nanPipeline);
        _vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Compute, _nanPipelineLayout, 0, 1, &set, 0, null);
        bool half = rt.Format is not (Format.R32Sfloat or Format.R32G32Sfloat);
        var pc = new NanPush { Slot = (uint)slot, W = rt.ImgW, H = rt.ImgH, Huge = half ? 65000f : 1e30f };
        _vk.CmdPushConstants(cmd, _nanPipelineLayout, ShaderStageFlags.ComputeBit, 0, (uint)sizeof(NanPush), &pc);
        _vk.CmdDispatch(cmd, (rt.ImgW + 15) / 16, (rt.ImgH + 15) / 16, 1);
        // later writes to the surface wait for the read (execution dependency only)
        _vk.CmdPipelineBarrier(cmd, PipelineStageFlags.ComputeShaderBit, PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.TransferBit | PipelineStageFlags.ComputeShaderBit,
            0, 0, null, 0, null, 0, null);
        nanFrame.Scans.Add(new NanScan
        {
            Address = rt.Address,
            W = rt.Width,
            H = rt.Height,
            ColorFormat = rt.ColorFormat,
            Format = rt.Format,
            Present = _presentCount,
            GuestFrame = DebugMode.Frame,
            Scene = _fScenes,
            FirstDraw = firstDraw,
            LastDraw = lastDraw,
        });
        nanFrame.Used = true;
        _nanScans++;
        _nanScanTicks += Stopwatch.GetTimestamp() - t0;
    }

    /// <summary>Draws mode: set 0 binding 4 of a draw = this frame's NaN draw table.</summary>
    private void NanBindDrawBuffer(DescriptorSet uSet)
    {
        NanFrame nanFrame = EnsureNanFrame(F);
        var bi = new DescriptorBufferInfo(nanFrame.Buf, NanScanBytes, NanDrawBytes);
        var w = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DstSet = uSet, DstBinding = (uint)ShaderGenerator.NanDrawBinding, DescriptorCount = 1, DescriptorType = DescriptorType.StorageBuffer, PBufferInfo = &bi };
        _vk.UpdateDescriptorSets(_dev, 1, in w, 0, null);
    }

    /// <summary>Draws mode: what the draw log needs to name a draw that produced NaN (recorded per draw).</summary>
    private void NanNoteDraw(DrawCommand d, VsEntry vs, FsEntry fs)
    {
        NanFrame nanFrame = EnsureNanFrame(F);
        uint used = d.FragmentProgram.Info.TexturesUsed;
        int start = nanFrame.Textures.Count;
        for (int i = 0; i < 16; i++)
        {
            if ((used & (1u << i)) != 0)
            {
                nanFrame.Textures.Add(d.FragmentTextures[i]);
            }
        }

        nanFrame.Draws.Add(new NanDrawInfo
        {
            Id = _drawSerial,
            Scene = _fScenes,
            TexStart = start,
            Present = _presentCount,
            GuestFrame = DebugMode.Frame,
            Vs = vs.ProgramHash,
            Fs = fs.ProgramHash,
            VsHint = vs.HintKey,
            FsHint = fs.HintKey,
            Rt = _renderTarget?.Address ?? 0,
            RtFormat = _renderTarget?.ColorFormat ?? 0,
            RtW = _renderTarget?.Width ?? 0,
            RtH = _renderTarget?.Height ?? 0,
            TexMask = used,
            IndexCount = d.IndexCount,
            Instances = d.InstanceCount,
            Prim = d.PrimitiveType,
            Msaa = MsaaPath,
        });
        nanFrame.Used = true;
    }

    /// <summary>Before the frame's command buffer ends: shader writes into the result buffer become host-visible.</summary>
    private void NanBeforeSubmit(Frame f)
    {
        var mb = new MemoryBarrier { SType = StructureType.MemoryBarrier, SrcAccessMask = AccessFlags.ShaderWriteBit, DstAccessMask = AccessFlags.HostReadBit };
        _vk.CmdPipelineBarrier(f.Cmd, PipelineStageFlags.ComputeShaderBit | PipelineStageFlags.FragmentShaderBit, PipelineStageFlags.HostBit, 0, 1, in mb, 0, null, 0, null);
    }

    /// <summary>The frame retired: read its scan results and draw hits, update the histories, reset the buffer.</summary>
    private void CompleteNanFrame(Frame f)
    {
        NanFrame nanFrame = f.Nan!;
        long t0 = Stopwatch.GetTimestamp();
        for (int i = 0; i < nanFrame.Scans.Count; i++)
        {
            uint* r = nanFrame.Ptr + i * NanScanWords;
            NanScanResult(nanFrame.Scans[i], r[0], r[1], r[2], r[3], r[4], r[5]);
            for (int k = 0; k < NanScanWords; k++)
            {
                r[k] = 0;
            }
        }
        nanFrame.Scans.Clear();
        if (ShaderGenerator.NanDraws)
        {
            NanDrawResults(nanFrame);
            nanFrame.Draws.Clear();
            nanFrame.Textures.Clear();
        }
        nanFrame.Used = false;
        _nanRetired++;
        _nanRetireTicks += Stopwatch.GetTimestamp() - t0;
    }

    private static string NanAt(uint inv) => inv == 0 ? "?" : $"{(0xFFFFFFFFu - inv) & 0xFFFF},{(0xFFFFFFFFu - inv) >> 16}";

    private void NanScanResult(in NanScan nanScan, uint nan, uint inf, uint huge, uint neg, uint firstInv, uint firstHugeInv)
    {
        (uint Address, uint W, uint H, uint ColorFormat) key = (nanScan.Address, nanScan.W, nanScan.H, nanScan.ColorFormat);
        if (!_nanTargets.TryGetValue(key, out NanTargetHistory? nanTargetHistory))
        {
            _nanTargets[key] = nanTargetHistory = new NanTargetHistory { Address = nanScan.Address, W = nanScan.W, H = nanScan.H, ColorFormat = nanScan.ColorFormat };
        }

        nanTargetHistory.Scans++;
        bool unsigned = NanUnsignedGuest(nanScan.ColorFormat);
        bool bad = nan > 0 || inf > 0 || huge > 0 || (unsigned && neg > 0);
        string where = $"rt 0x{nanScan.Address:X8} {nanScan.W}x{nanScan.H} {NanFormatName(nanScan.ColorFormat)}";
        string counts = $"{nan} NaN, {inf} Inf, {huge} huge, {neg} negative{(unsigned && neg > 0 ? " (unsigned format)" : "")} of {(long)nanScan.W * nanScan.H} px";
        string first = nan + inf > 0 ? $" (first NaN/Inf at {NanAt(firstInv)})" : huge > 0 ? $" (first huge at {NanAt(firstHugeInv)})" : "";
        string scene = $"after scene {nanScan.Scene} (draws {nanScan.FirstDraw}-{nanScan.LastDraw})";
        nanTargetHistory.LastResult = $"p{nanScan.Present} {counts}{first} {scene}";
        if (!bad)
        {
            if (nanTargetHistory.Bad && nanScan.Present - nanTargetHistory.LastBad >= NanClearAfter)
            {
                nanTargetHistory.Bad = false;
                nanTargetHistory.History.Add((nanTargetHistory.BadSince, nanTargetHistory.LastBad, nanTargetHistory.MaxNan));
                if (nanTargetHistory.History.Count > 64)
                {
                    nanTargetHistory.History.RemoveAt(0);
                }

                if (nanTargetHistory.EpisodeLogged)
                {
                    Log.Info($"[nan] f{nanScan.GuestFrame} p{nanScan.Present} {where}: clean again (bad in presents {nanTargetHistory.BadSince}-{nanTargetHistory.LastBad}, {nanTargetHistory.BadPresents} bad presents in all so far)");
                }
            }
            return;
        }
        if (nanScan.Present != nanTargetHistory.LastCounted)
        {
            nanTargetHistory.BadPresents++;
            nanTargetHistory.LastCounted = nanScan.Present;
        }
        if (nan > nanTargetHistory.MaxNan)
        {
            nanTargetHistory.MaxNan = nan;
            nanTargetHistory.MaxAt = nanScan.Present;
        }
        nanTargetHistory.MaxInf = Math.Max(nanTargetHistory.MaxInf, inf);
        nanTargetHistory.MaxHuge = Math.Max(nanTargetHistory.MaxHuge, huge);
        nanTargetHistory.MaxNeg = Math.Max(nanTargetHistory.MaxNeg, neg);
        if (nanTargetHistory.FirstBad < 0)
        {
            nanTargetHistory.FirstBad = nanScan.Present;
        }

        nanTargetHistory.LastBad = nanScan.Present;
        if (!nanTargetHistory.Bad)
        {
            nanTargetHistory.Bad = true;
            nanTargetHistory.BadSince = nanScan.Present;
            nanTargetHistory.Episodes++;
            // huge or negative-unsigned values alone (no NaN/Inf) are logged less often
            nanTargetHistory.EpisodeLogged = nanTargetHistory.Episodes == 1 || nanScan.Present - nanTargetHistory.LastLine >= (nan + inf > 0 ? NanEpisodeEvery : NanStillEvery);
            if (!nanTargetHistory.EpisodeLogged)
            {
                nanTargetHistory.Suppressed++;
                return;
            }
            string kind = nanTargetHistory.Episodes == 1 ? "first time in this target" : $"episode {nanTargetHistory.Episodes}{(nanTargetHistory.Suppressed > 0 ? $", {nanTargetHistory.Suppressed} short ones not logged" : "")}";
            nanTargetHistory.Suppressed = 0;
            nanTargetHistory.LastLine = nanScan.Present;
            string line = $"{where}: {counts}{first} {scene} - {kind}";
            Log.Info($"[nan] f{nanScan.GuestFrame} p{nanScan.Present} {line}");
            if (nan + inf > 0)
            {
                NanAnomaly(nanScan.Present, line);
            }
        }
        else if (nanScan.Present - nanTargetHistory.LastLine >= NanStillEvery)
        {
            nanTargetHistory.LastLine = nanScan.Present;
            Log.Info($"[nan] f{nanScan.GuestFrame} p{nanScan.Present} {where}: still {counts}{first} {scene} (since present {nanTargetHistory.BadSince})");
        }
    }

    private void NanAnomaly(long present, string detail)
    {
        if (!DebugMode.Enabled || present - _nanLastAnomaly < NanAnomalyEvery)
        {
            return;
        }

        _nanLastAnomaly = present;
        _recentEvents.Enqueue(new FrameAnomalyDetector.Event(NanAnomalyKind, detail, true, present));
        while (_recentEvents.Count > 64)
        {
            _recentEvents.Dequeue();
        }

        Log.Info($"[anomaly] p{present} f{DebugMode.Frame} {NanAnomalyKind}: {detail}");
        if (AnomalyDetected is { } handler)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    handler(NanAnomalyKind, detail, present);
                }
                catch (Exception x)
                {
                    Log.Warn($"[anomaly] handler failed: {x.Message}");
                }
            });
        }
    }

    private void NanDrawResults(NanFrame nanFrame)
    {
        uint* t = nanFrame.Ptr + NanScanBytes / 4;
        int hits = 0, lines = 0;
        StringBuilder? summary = null;
        long present = nanFrame.Draws.Count > 0 ? nanFrame.Draws[0].Present : _presentCount;
        for (int id = 0; id < ShaderGenerator.NanDrawSlots; id++)
        {
            uint* e = t + id * 4;
            if (e[0] == 0)
            {
                continue;
            }

            uint frags = e[0], flags = e[1], firstInv = e[2];
            e[0] = e[1] = e[2] = e[3] = 0;
            hits++;
            _nanDrawHits++;
            int idx = NanFindDraw(nanFrame.Draws, id);
            string what = string.Join("+", new[] { (flags & 1) != 0 ? "NaN" : null, (flags & 2) != 0 ? "Inf" : null, (flags & 4) != 0 ? "huge" : null }.Where(s => s != null));
            if (idx < 0)
            {
                Log.Info($"[nan-draw] p{present} draw id={id}{(id == ShaderGenerator.NanDrawSlots - 1 ? "+ (ids above the table share the last slot)" : "")}: {frags} fragments {what} (first at {NanAt(firstInv)}), draw not recorded");
                continue;
            }
            NanDrawInfo nanDrawInfo = nanFrame.Draws[idx];
            (ulong Vs, ulong Fs, ulong FsHint, uint Rt) key = (nanDrawInfo.Vs, nanDrawInfo.Fs, nanDrawInfo.FsHint, nanDrawInfo.Rt);
            if (!_nanDraws.TryGetValue(key, out NanDrawHistory? nanDrawHistory))
            {
                _nanDraws[key] = nanDrawHistory = new NanDrawHistory { Vs = nanDrawInfo.Vs, Fs = nanDrawInfo.Fs, FsHint = nanDrawInfo.FsHint, Rt = nanDrawInfo.Rt, First = nanDrawInfo.Present };
            }

            bool firstInPresent = nanDrawHistory.LastPresent != nanDrawInfo.Present;
            if (firstInPresent)
            {
                nanDrawHistory.Presents++;
                nanDrawHistory.LastPresent = nanDrawInfo.Present;
            }
            nanDrawHistory.Fragments += frags;
            nanDrawHistory.Last = nanDrawInfo.Present;
            var sb = new StringBuilder();
            sb.Append($"draw id={nanDrawInfo.Id} scene {nanDrawInfo.Scene}: {frags} fragments {what} (first at {NanAt(firstInv)}) vs={nanDrawInfo.Vs:X16} fs={nanDrawInfo.Fs:X16} fsHint={nanDrawInfo.FsHint:X16} ");
            sb.Append($"rt=0x{nanDrawInfo.Rt:X8} {nanDrawInfo.RtW}x{nanDrawInfo.RtH} {NanFormatName(nanDrawInfo.RtFormat)} prim={nanDrawInfo.Prim} n={nanDrawInfo.IndexCount}x{nanDrawInfo.Instances} msaa={nanDrawInfo.Msaa}");
            int k = nanDrawInfo.TexStart;
            for (int u = 0; u < 16; u++)
            {
                if ((nanDrawInfo.TexMask & (1u << u)) == 0)
                {
                    continue;
                }

                GxmTextureDesc gxmTextureDesc = nanFrame.Textures[k++];
                sb.Append($" t{u}=0x{gxmTextureDesc.DataAddress:X8}:{gxmTextureDesc.Format:X8}:{gxmTextureDesc.Width}x{gxmTextureDesc.Height}");
            }
            string line = sb.ToString();
            if (nanDrawHistory.Example.Length == 0)
            {
                nanDrawHistory.Example = line;
            }
            // each vs/fs/rt: the presents 1-3, 10, 100... it appears in (all its draws of that present)
            if (DebugMode.ShouldLog(nanDrawHistory.Presents) && (firstInPresent || nanDrawHistory.LoggedPresent == nanDrawInfo.Present) && lines < 40)
            {
                nanDrawHistory.LoggedPresent = nanDrawInfo.Present;
                lines++;
                Log.Info($"[nan-draw] f{nanDrawInfo.GuestFrame} p{nanDrawInfo.Present} {line}{(nanDrawHistory.Presents > 1 ? $" (#{nanDrawHistory.Presents} presents with NaN from this vs/fs/rt)" : "")}");
            }
            summary ??= new StringBuilder();
            if (summary.Length < 600)
            {
                summary.Append($" id={nanDrawInfo.Id}:fs={nanDrawInfo.Fs:X16}({frags})");
            }
        }
        if (hits == 0)
        {
            return;
        }

        _nanDrawFrames++;
        if (present - _nanLastDrawSummary >= NanEpisodeEvery)
        {
            _nanLastDrawSummary = present;
            Log.Info($"[nan-draw] p{present} {hits} draws with NaN/Inf output in this submission:{summary}");
        }
    }

    private static int NanFindDraw(List<NanDrawInfo> draws, int id)
    {
        int lo = 0, hi = draws.Count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            int v = draws[mid].Id;
            if (v == id)
            {
                return mid;
            }

            if (v < id)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }
        return -1;
    }

    /// <summary>"\nPERF nan: ..." when the scan or the draws mode ran this session, else empty.</summary>
    private string NanReport()
    {
        if (_nanScans == 0 && _nanDrawFrames == 0 && !ShaderGenerator.NanDraws)
        {
            return "";
        }

        int bad = _nanTargets.Values.Count(h => h.FirstBad >= 0);
        return $"\nPERF nan: {_nanScans} target scans ({_nanScanTicks * 1e6 / Stopwatch.Frequency / Math.Max(1, _nanScans):F1} us each recording), " +
               $"{_nanRetired} frames read back ({_nanRetireTicks * 1e6 / Stopwatch.Frequency / Math.Max(1, _nanRetired):F1} us each), {_nanDropped} scans dropped (over {NanScanSlots} per submission); " +
               $"{_nanTargets.Count} float targets seen, {bad} with NaN/Inf/huge/negative-unsigned values" +
               (ShaderGenerator.NanDraws ? $"; draws mode: {_nanDrawHits} draw hits in {_nanDrawFrames} submissions, {_nanDraws.Count} distinct vs/fs/rt" : "");
    }

    /// <summary>nan.txt of a capture: per-target history and (draws mode) the programs that produced NaN.</summary>
    public string NanHistory()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# VitaSharp NaN/Inf history of this session (debug mode scans float render targets at every scene end; see docs/notes/debug-mode.md)");
        sb.AppendLine($"# scans {_nanScans}, debug mode {(DebugMode.Enabled ? "on" : "off")}, draws mode (VITASHARP_DEBUG_NAN=draws) {(ShaderGenerator.NanDraws ? "on" : "off")}");
        sb.AppendLine("# target: scans, bad presents, episodes, first/last bad present, max NaN (at present), max Inf/huge/negative; then the last scan and the episodes (present ranges)");
        foreach (NanTargetHistory? nanTargetHistory in _nanTargets.Values.OrderByDescending(h => h.BadPresents).ThenBy(h => h.Address))
        {
            sb.AppendLine($"rt 0x{nanTargetHistory.Address:X8} {nanTargetHistory.W}x{nanTargetHistory.H} {NanFormatName(nanTargetHistory.ColorFormat)}: {nanTargetHistory.Scans} scans, {nanTargetHistory.BadPresents} bad presents, {nanTargetHistory.Episodes} episodes" +
                          (nanTargetHistory.FirstBad >= 0 ? $", first p{nanTargetHistory.FirstBad} last p{nanTargetHistory.LastBad}, max {nanTargetHistory.MaxNan} NaN{(nanTargetHistory.MaxNan > 0 ? $" (p{nanTargetHistory.MaxAt})" : "")} {nanTargetHistory.MaxInf} Inf {nanTargetHistory.MaxHuge} huge {nanTargetHistory.MaxNeg} negative{(nanTargetHistory.Bad ? ", BAD NOW" : "")}" : ""));
            sb.AppendLine($"    last: {nanTargetHistory.LastResult}");
            if (nanTargetHistory.History.Count > 0)
            {
                sb.AppendLine("    episodes: " + string.Join(" ", nanTargetHistory.History.Select(e => $"{e.From}-{e.To}(max {e.MaxNan})")));
            }
        }
        if (ShaderGenerator.NanDraws)
        {
            sb.AppendLine();
            sb.AppendLine("# draws mode: vertex/fragment program + render target combinations whose fragment output had NaN/Inf");
            foreach (NanDrawHistory? nanDrawHistory in _nanDraws.Values.OrderByDescending(d => d.Fragments))
            {
                sb.AppendLine($"vs {nanDrawHistory.Vs:X16} fs {nanDrawHistory.Fs:X16} (hint {nanDrawHistory.FsHint:X16}) rt 0x{nanDrawHistory.Rt:X8}: {nanDrawHistory.Presents} presents, {nanDrawHistory.Fragments} fragments, p{nanDrawHistory.First}-p{nanDrawHistory.Last}\n    e.g. {nanDrawHistory.Example}");
            }

            if (_nanDraws.Count == 0)
            {
                sb.AppendLine("(none)");
            }
        }
        return sb.ToString();
    }

    private void DestroyNan()
    {
        foreach (Frame f in _frames)
        {
            if (f.Nan is { } nf)
            {
                _core.DestroyBuffer(nf.Buf, nf.Mem);
                f.Nan = null;
            }
        }

        if (_nanPipeline.Handle != 0)
        {
            _vk.DestroyPipeline(_dev, _nanPipeline, null);
        }

        if (_nanModule.Handle != 0)
        {
            _vk.DestroyShaderModule(_dev, _nanModule, null);
        }

        if (_nanPipelineLayout.Handle != 0)
        {
            _vk.DestroyPipelineLayout(_dev, _nanPipelineLayout, null);
        }

        if (_nanSetLayout.Handle != 0)
        {
            _vk.DestroyDescriptorSetLayout(_dev, _nanSetLayout, null);
        }

        if (_nanSampler.Handle != 0)
        {
            _vk.DestroySampler(_dev, _nanSampler, null);
        }
    }
}
