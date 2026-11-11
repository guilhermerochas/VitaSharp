using Silk.NET.Vulkan;
using VitaSharp.Core.Gpu;
using VitaSharp.Core.Util;

namespace VitaSharp.Renderer.Vulkan;

/// <summary>
/// Scenes of several processes at once (<see cref="GpuQueue.InterleaveScenes"/>). On the Vita every process has its own
/// GXM context and the GPU interleaves their work: the shell can keep a scene open (its main thread waits for the vblank
/// with the scene begun) while an application renders its frames. Here the commands of all processes come through one
/// queue, so the renderer keeps the open scene of each process: when a command of another process arrives, the current
/// scene's rendering ends as for any mid-scene break (colour and depth stored, nothing resolved away) and its state is
/// put aside; the process's next command resumes it, and its next draw begins a rendering that loads what was stored.
/// Host work (pid 0) runs in whatever scene is current, as before.
/// </summary>
public sealed unsafe partial class VulkanRenderer
{
    sealed class SuspendedScene
    {
        public BeginSceneCommand? Scene;
        public RenderTarget? Rt;
        public Format RtAttachFormat;
        public bool RtGamma, DepthNeedsClear;
        public DepthTarget? Depth;
        public uint SceneW, SceneH;
        public long SceneSerial;
        public bool Ss, SsUnresolved, Ms, MsNeedsLoad, MsUnresolved, MsSceneRaw, MsaaNoDownscale;
        public SampleCountFlags MsSamples;
        public bool Tw1Active;
        public TileWordImage? Tw1;
        public (uint X, uint Y, uint W, uint H)[] Regions = [];
    }

    /// <summary>The process whose scene state is current (0: none named yet).</summary>
    int _scenePid;
    readonly Dictionary<int, SuspendedScene> _suspendedScenes = new();
    long _statSceneSwitches;
    /// <summary>Allocates <see cref="_sceneSerial"/>: a resumed scene keeps its serial, so new scenes cannot count on
    /// the current one's.</summary>
    long _sceneSerialNext;

    /// <summary>Makes <paramref name="c"/>'s process the current scene context (see the class comment).</summary>
    void SwitchSceneProcess(GpuCommand c)
    {
        int pid = c.Pid;
        if (pid == 0 || pid == _scenePid) return;
        // a scene begun before processes were told apart (pid 0) stays the current one's
        if (_scenePid != 0 && _inScene) SuspendScene(_scenePid);
        _scenePid = pid;
        if (_suspendedScenes.Remove(pid, out var s)) ResumeScene(s);
    }

    void SuspendScene(int pid)
    {
        // a mid-scene break: the rendering ends with its attachments stored (multisampled samples kept unresolved) and
        // the renderings waiting to be merged are begun now, before the other process's work is recorded
        EndRendering(resolve: !_ms);
        FlushChain(false);
        _suspendedScenes[pid] = new SuspendedScene
        {
            Scene = _scene, Rt = _rt, RtAttachFormat = _rtAttachFormat, RtGamma = _rtGamma, DepthNeedsClear = _depthNeedsClear,
            Depth = _depth, SceneW = _sceneW, SceneH = _sceneH, SceneSerial = _sceneSerial,
            Ss = _ss, SsUnresolved = _ssUnresolved, Ms = _ms, MsNeedsLoad = _msNeedsLoad, MsUnresolved = _msUnresolved, MsSceneRaw = _msSceneRaw,
            MsaaNoDownscale = _msaaNoDownscale, MsSamples = _msSamples, Tw1Active = _tw1Active, Tw1 = _tw1, Regions = [.. _sceneRegions],
        };
        _inScene = false;
        _scene = null;
        _rt = null;
        _depth = null;
        _ss = _ssUnresolved = _ms = _msUnresolved = _msaaNoDownscale = false;
        _sceneRegions.Clear();
        EndTileWord1();
        if ((++_statSceneSwitches & (_statSceneSwitches - 1)) == 0)
            Log.Info($"[gpu] scenes of several processes interleaved: {_statSceneSwitches} open scenes suspended for another process's work");
    }

    void ResumeScene(SuspendedScene s)
    {
        // the current context has no open rendering (its scene ended, or it never had one)
        EndRendering();
        FlushChain(false);
        _inScene = true;
        (_scene, _rt, _rtAttachFormat, _rtGamma, _depthNeedsClear, _depth, _sceneW, _sceneH, _sceneSerial) =
            (s.Scene, s.Rt, s.RtAttachFormat, s.RtGamma, s.DepthNeedsClear, s.Depth, s.SceneW, s.SceneH, s.SceneSerial);
        (_ss, _ssUnresolved, _ms, _msNeedsLoad, _msUnresolved, _msSceneRaw, _msaaNoDownscale, _msSamples) =
            (s.Ss, s.SsUnresolved, s.Ms, s.MsNeedsLoad, s.MsUnresolved, s.MsSceneRaw, s.MsaaNoDownscale, s.MsSamples);
        (_tw1Active, _tw1) = (s.Tw1Active, s.Tw1);
        _sceneRegions.Clear();
        _sceneRegions.AddRange(s.Regions);
        _rt?.LastTouchedFrame = FrameCount;
        _depth?.LastUsedFrame = _depth.LastTouchedFrame = FrameCount;
    }

    /// <summary>A process ended (<see cref="ProcessEndedCommand"/>, already made current): the scene it left open ends.</summary>
    void EndProcessScene(ProcessEndedCommand c)
    {
        if (_inScene) EndScene(new EndSceneCommand { Space = c.Space, Pid = c.Pid });
        _scenePid = 0;
    }

    /// <summary>A surface a suspended scene renders into (never evicted).</summary>
    bool InSuspendedScene(RenderTarget rt)
    {
        foreach (var s in _suspendedScenes.Values) if (s.Rt == rt) return true;
        return false;
    }

    bool InSuspendedScene(DepthTarget dt)
    {
        foreach (var s in _suspendedScenes.Values) if (s.Depth == dt) return true;
        return false;
    }
}
