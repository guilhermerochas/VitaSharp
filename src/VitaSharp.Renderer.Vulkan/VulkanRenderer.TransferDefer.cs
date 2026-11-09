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
/// queued behind it until it ran, so they keep their order. A memory texture over a pending transfer's destination
/// waits for the frame first when it was submitted already.
/// </summary>
public sealed unsafe partial class VulkanRenderer
{
    static readonly bool TransferDefer = Environment.GetEnvironmentVariable(EnvVars.TransferDefer) == "1";

    /// <summary>Frame the last deferred transfer runs at (later ones queue behind it until it retired).</summary>
    Frame? _xferDeferFrame;
    /// <summary>Destinations of deferred transfers not run yet, with the frame they run at.</summary>
    readonly List<(uint Address, uint Size, Frame Frame)> _xferPendingDst = new();
    /// <summary>Destinations of deferred transfers that ran: their cached textures and surfaces are refreshed at the
    /// next command boundary (a retirement can happen in the middle of a draw).</summary>
    readonly List<(uint Address, uint Size)> _xferInvalidations = new();
    long _statXferDeferred;

    public bool DeferCallback(CallbackCommand cb)
    {
        if (!TransferDefer) return false;
        Frame? target = _xferDeferFrame is { Retired: false } d && (d.Recording || d.Submitted) ? d : null;
        if (target == null)
        {
            if (cb.ReadSize == 0) return false;
            // a source written back now: its copy is recorded into this frame, which then publishes it
            WriteBackTransferSource(cb);
            target = PendingWritebackFrame(cb.ReadAddress, (ulong)cb.ReadAddress + cb.ReadSize);
            if (target == null) return false;
        }
        else if (target != F && F.Recording && cb.ReadSize > 0)
        {
            // behind the queued transfers, and after a source written back now (which lands with the current frame)
            WriteBackTransferSource(cb);
            if (PendingWritebackFrame(cb.ReadAddress, (ulong)cb.ReadAddress + cb.ReadSize) == F) target = F;
        }
        var space = AddressSpace.Current;
        var frame = target;
        frame.Signals.Add(() =>
        {
            using (AddressSpace.Use(space))
            {
                try { cb.Action(); }
                catch (Exception e) { Log.Error($"GPU callback failed: {e}"); }
            }
            if (cb.WriteSize > 0) _xferInvalidations.Add((cb.WriteAddress, cb.WriteSize));
            _xferPendingDst.RemoveAll(p => p.Frame == frame);
            if (_xferDeferFrame == frame) _xferDeferFrame = null;
        });
        if (cb.WriteSize > 0) _xferPendingDst.Add((cb.WriteAddress, cb.WriteSize, frame));
        _xferDeferFrame = frame;
        if (_statXferDeferred++ < 20 || TraceSync)
            Log.Info($"[gpu] transfer 0x{cb.ReadAddress:X8} -> 0x{cb.WriteAddress:X8} deferred to the {(frame == F && frame.Recording ? "recording" : "submitted")} frame's retirement (present {_presentCount})");
        return true;
    }

    void DrainTransferInvalidations()
    {
        foreach (var (a, s) in _xferInvalidations) InvalidateTextures(a, s);
        _xferInvalidations.Clear();
    }

    /// <summary>A texture is about to read [addr, addr + size) from memory: a deferred transfer into it whose frame was
    /// submitted is waited for (its frame retires, the transfer runs); one queued on the recording frame cannot be
    /// (the draw is being recorded into it) and is read as it is now.</summary>
    void WaitPendingTransferInto(uint addr, uint size)
    {
        if (_xferPendingDst.Count == 0) return;
        foreach (var (a, s, f) in _xferPendingDst.ToArray())
        {
            if (a >= addr + size || addr >= a + s) continue;
            if (f.Submitted && !f.Retired) RetireFrame(f, RetireCause.Finish);
            else if (f.Recording) Log.WarnOnce("xfer-defer-texture", $"[gpu] texture 0x{addr:X8} reads a transfer queued on the frame being recorded: read as it is now");
        }
        // only the texture cache now (a surface reload records commands: left to the command boundary)
        foreach (var (a, s) in _xferInvalidations)
            foreach (var t in _textures.Values)
                if (t.DataStart < a + s && a < t.DataStart + t.DataSize) { t.CheckedFrame = -1; t.ForceCheck = true; }
    }
}
