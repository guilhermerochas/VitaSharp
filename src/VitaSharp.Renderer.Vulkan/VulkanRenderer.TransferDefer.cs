using VitaSharp.Core.Gpu;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Renderer.Vulkan;

/// <summary>
/// VITASHARP_TRANSFER_DEFER=1: a CPU transfer (sceGxmTransferCopy/Downscale/Fill) that reads a render-target writeback
/// still in flight does not hold the GPU worker until that frame completes (240a8de made it finish every frame; on a
/// phone that was one full GPU wait per frame in Killzone). The transfer is queued on the frame that publishes the
/// writeback and runs when it retires, right after the writebacks and in order with the sync points queued after it
/// (a notification after the transfer is deferred behind it). Like the transfers of the Vita's GPU, which run after
/// the scenes before them without stopping the CPU. Once one transfer is deferred, every later transfer and fill is
/// queued behind it until it ran, so they keep their order. A memory texture over a pending transfer's destination reads
/// it as it is (<see cref="WaitPendingTransferInto"/>): the transfer's result can arrive a frame late.
/// </summary>
public sealed unsafe partial class VulkanRenderer
{
    /// <summary>On by default on Android (the worker waiting for the GPU there costs a frame), off on desktop;
    /// VITASHARP_TRANSFER_DEFER=0/1 overrides.</summary>
    private static readonly bool s_transferDefer = Environment.GetEnvironmentVariable(EnvVars.TransferDefer)?.Trim() is { Length: > 0 } v ? v == "1" : OperatingSystem.IsAndroid();

    /// <summary>Frame the last deferred transfer runs at (later ones queue behind it until it retired).</summary>
    private Frame? _xferDeferFrame;
    /// <summary>Destinations of deferred transfers not run yet, with the frame they run at.</summary>
    private readonly List<(uint Address, uint Size, Frame Frame)> _xferPendingDst = new();
    /// <summary>Destinations of deferred transfers that ran: their cached textures and surfaces are refreshed at the
    /// next command boundary (a retirement can happen in the middle of a draw).</summary>
    private readonly List<(uint Address, uint Size)> _xferInvalidations = new();
    private long _statXferDeferred, _statXferLateReads;

    public bool DeferCallback(CallbackCommand cb)
    {
        if (!s_transferDefer)
        {
            return false;
        }

        Frame? target = _xferDeferFrame is { Retired: false } d && (d.Recording || d.Submitted) ? d : null;
        if (target == null)
        {
            if (cb.ReadSize == 0)
            {
                return false;
            }
            // a source written back now: its copy is recorded into this frame, which then publishes it
            WriteBackTransferSource(cb);
            target = PendingWritebackFrame(cb.ReadAddress, (ulong)cb.ReadAddress + cb.ReadSize);
            if (target == null)
            {
                return false;
            }
        }
        else if (target != F && F.Recording && cb.ReadSize > 0)
        {
            // behind the queued transfers, and after a source written back now (which lands with the current frame)
            WriteBackTransferSource(cb);
            if (PendingWritebackFrame(cb.ReadAddress, (ulong)cb.ReadAddress + cb.ReadSize) == F)
            {
                target = F;
            }
        }
        AddressSpace? space = AddressSpace.Current;
        Frame frame = target;
        frame.Signals.Add(() =>
        {
            using (AddressSpace.Use(space))
            {
                try
                {
                    cb.Action();
                }
                catch (Exception e)
                {
                    Log.Error($"GPU callback failed: {e}");
                }
            }
            if (cb.WriteSize > 0)
            {
                _xferInvalidations.Add((cb.WriteAddress, cb.WriteSize));
            }

            _xferPendingDst.RemoveAll(p => p.Frame == frame);
            if (_xferDeferFrame == frame)
            {
                _xferDeferFrame = null;
            }
        });
        if (cb.WriteSize > 0)
        {
            _xferPendingDst.Add((cb.WriteAddress, cb.WriteSize, frame));
        }

        _xferDeferFrame = frame;
        if (_statXferDeferred++ < 20 || s_traceSync)
        {
            Log.Info($"[gpu] transfer 0x{cb.ReadAddress:X8} -> 0x{cb.WriteAddress:X8} deferred to the {(frame == F && frame.Recording ? "recording" : "submitted")} frame's retirement (present {_presentCount})");
        }

        return true;
    }

    private void DrainTransferInvalidations()
    {
        foreach ((uint a, uint s) in _xferInvalidations)
        {
            InvalidateTextures(a, s);
        }

        _xferInvalidations.Clear();
    }

    /// <summary>A texture is about to read [addr, addr + size) from memory while a deferred transfer into it has not run:
    /// the frames the GPU already finished are published (their transfers run); a transfer whose frame is still on the
    /// GPU or being recorded is not waited for, the texture reads the destination as it is now (the previous run of that
    /// transfer: Killzone's mips of its 384x192 reflection target one frame late). Waiting there was a full GPU drain per
    /// frame (with 8 frames in flight: 7-10 vblank stalls).</summary>
    private void WaitPendingTransferInto(uint addr, uint size)
    {
        if (_xferPendingDst.Count == 0)
        {
            return;
        }

        bool overlaps = false;
        foreach ((uint a, uint s, Frame _) in _xferPendingDst)
        {
            if (a < addr + size && addr < a + s)
            {
                overlaps = true;
                break;
            }
        }

        if (!overlaps)
        {
            return;
        }

        PollRetire();
        foreach ((uint a, uint s, Frame? f) in _xferPendingDst)
        {
            if (a >= addr + size || addr >= a + s)
            {
                continue;
            }

            if (_statXferLateReads++ < 20 || s_traceSync)
            {
                Log.Info($"[gpu] texture 0x{addr:X8} reads transfer destination 0x{a:X8} before its deferred transfer ran ({(f.Recording ? "frame being recorded" : "frame on the GPU")}): read as it is now (present {_presentCount})");
            }
        }
        // only the texture cache now (a surface reload records commands: left to the command boundary)
        foreach ((uint a, uint s) in _xferInvalidations)
        {
            foreach (Texture t in _textures.Values)
            {
                if (t.DataStart < a + s && a < t.DataStart + t.DataSize)
                {
                    t.CheckedFrame = -1;
                    t.ForceCheck = true;
                }
            }
        }
    }
}
