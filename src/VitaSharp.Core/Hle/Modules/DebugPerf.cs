using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// ScePerf (ARM PMU counters, timebase, Razor CPU markers), SceRazorCapture / SceRazorHud (Razor GPU capture and live
/// HUD), SceDeci4pUserp (devkit host channel) and the libult support stubs SceUlobjMgr / SceUlobjDbg. On a retail console
/// there is no profiler and no host: everything succeeds without effect and queries report "not capturing".
/// </summary>
public sealed class DebugPerf : HleModule
{
    uint _captureFlagPtr;

    public override void Initialize()
    {
        // _pLibPerfCaptureFlagPtr is an exported variable: a pointer to the "capturing" flag word (0 = not capturing).
        Emu.Hle.RegisterVariable(0x936A5F31u, () =>
        {
            if (_captureFlagPtr == 0)
            {
                _captureFlagPtr = Mem.Alloc(8, "_pLibPerfCaptureFlagPtr", MemoryRegion.Kernel);
                GuestMemory.W32(_captureFlagPtr, _captureFlagPtr + 4);
                GuestMemory.W32(_captureFlagPtr + 4, 0);
            }
            return _captureFlagPtr;
        });
    }

    // ---------------------------------------------------------------- ScePerf

    /// <summary>In case the import comes as a function instead of the variable.</summary>
    [Hle("_pLibPerfCaptureFlagPtr", Nid = 0x936A5F31)] int PerfCaptureFlagPtr() => 0;

    [Hle] int _sceRazorCpuInit(uint param) => 0;
    [Hle] int _sceCpuRazorPushFiberUserMarker(uint name) => 0;
    [Hle] int _sceCpuRazorPopFiberUserMarker() => 0;
    /// <summary>Called by the LLE libult for every fiber/ULT event when Razor is capturing.</summary>
    [Hle] int _sceRazorCpuWriteFiberUltPkt() => 0;

    [Hle] int scePerfArmPmonReset(int thid) => 0;
    [Hle] int scePerfArmPmonSelectEvent(int thid, uint counter, uint eventCode) => 0;
    [Hle] int scePerfArmPmonStart(int thid) => 0;
    [Hle] int scePerfArmPmonStop(int thid) => 0;
    [Hle] int scePerfArmPmonGetCounterValue(int thid, uint counter, Ptr<uint> value) { value.TrySet(0); return 0; }
    [Hle] int scePerfArmPmonSetCounterValue(int thid, uint counter, uint value) => 0;
    [Hle] int scePerfArmPmonSoftwareIncrement(uint mask) => 0;

    /// <summary>Timebase in microseconds of guest process time, paired with a 1 MHz frequency.</summary>
    [Hle] ulong scePerfGetTimebaseValue() => K.ProcessTimeUs;
    [Hle] uint scePerfGetTimebaseFrequency() => TimebaseFrequency;
    internal const uint TimebaseFrequency = 1_000_000;

    [Hle] int sceRazorCpuPushMarker(uint label) => 0;
    [Hle] int sceRazorCpuPushMarkerWithHud(uint label, uint color, int flags) => 0;
    [Hle] int sceRazorCpuPopMarker() => 0;
    [Hle] int sceRazorCpuIsCapturing() => 0;
    [Hle] int sceRazorCpuStartCapture() => 0;
    [Hle] int sceRazorCpuStopCapture() => 0;
    [Hle] int sceRazorCpuSync() => 0;
    [Hle] int sceRazorCpuStartActivityMonitor() => 0;
    [Hle] int sceRazorCpuStopActivityMonitor() => 0;
    /// <summary>Signature unconfirmed: the outputs are not written.</summary>
    [Hle] int sceRazorCpuGetActivityMonitorTraceBuffer() => 0;
    [Hle] int sceRazorCpuStartUserMarkerTrace(uint buffer, uint size) => 0;
    [Hle] int sceRazorCpuStopUserMarkerTrace() => 0;
    /// <summary>Signature unconfirmed: the outputs are not written.</summary>
    [Hle] int sceRazorCpuGetUserMarkerTraceBuffer() => 0;

    // ---------------------------------------------------------------- SceRazorCapture

    [Hle] int sceRazorCaptureIsInProgress() => 0;
    [Hle] int sceRazorCaptureSetTrigger(uint frameIndex, uint filename) => 0;
    [Hle] int sceRazorCaptureSetTriggerNextFrame(uint filename) => 0;
    [Hle] int sceRazorGpuCaptureIsInProgress() => 0;
    [Hle] int sceRazorGpuCaptureSetTrigger(uint frameIndex, uint filename) => 0;
    [Hle] void sceRazorGpuCaptureSetTriggerNextFrame(uint filename) { }
    [Hle] void sceRazorGpuCaptureEnableSalvage(uint filename) { }
    [Hle] int sceRazorGpuCaptureStartSalvageMode() => 0;
    [Hle] int sceRazorGpuCaptureSetCaptureAllMemory(int enable) => 0;
    [Hle] int sceRazorGpuCaptureSetCaptureBeforeKick(int enable) => 0;

    // ---------------------------------------------------------------- SceRazorHud

    /// <summary>
    /// sceRazorGpuLiveSetBuffer(buffer, buf_size, SceRazorGpuLiveResultInfo *results): the result info (0x28) is
    /// cleared and points at the buffer, so a reader sees zero entries.
    /// </summary>
    [Hle]
    int sceRazorGpuLiveSetBuffer(uint buffer, uint bufSize, uint results)
    {
        if (results != 0)
        {
            GuestMemory.Span(results, 0x28).Clear();
            GuestMemory.W32(results + 0x00, buffer);  // result_data
            GuestMemory.W32(results + 0x0C, bufSize); // buffer_size
        }
        return 0;
    }

    [Hle] int sceRazorGpuLiveSetMetricsGroup(uint metrics) => 0;
    [Hle] int sceRazorGpuLiveStart() => 0;
    [Hle] int sceRazorGpuLiveStop() => 0;
    [Hle] int sceRazorGpuLiveTriggerDebugDump() => 0;
    [Hle] int sceRazorGpuPerfGetMode() => 0;
    [Hle] uint sceRazorGpuTraceRingBufferSize() => 0;
    [Hle] uint sceRazorGpuTraceRingBufferUsed() => 0;
    [Hle] int sceRazorGpuTraceSetFilename(uint filename) => 0;
    [Hle] int sceRazorGpuTraceSetMetricsGroup(uint metrics) => 0;
    [Hle] int sceRazorGpuTraceTrigger() => 0;
    [Hle] int sceRazorCpuHudSetUserMarkerTraceBuffer(uint buffer, uint size) => 0;
    [Hle] int sceRazorCpuHudUserMarkerEnableAllThreads() => 0;
    [Hle] int sceRazorCpuHudUserMarkerEnableThreadId(int thid) => 0;
    [Hle] int sceRazorHudReloadSettings() => 0;
    [Hle] int sceRazorHudSetDisplayEnabled(int enable) => 0;
    [Hle] int sceRazorHudSetDisplayFrameCount(uint count) => 0;

    // ---------------------------------------------------------------- SceDeci4pUserp (no host connected)

    [Hle]
    int sceKernelDeci4pOpen(uint protoname, uint protonum, uint bufsize)
    {
        Log.WarnOnce("deci4p-open", $"sceKernelDeci4pOpen({(protoname != 0 ? GuestMemory.ReadCString(protoname, 64) : "NULL")}): no host connected");
        return SceError.ERROR;
    }

    [Hle] int sceKernelDeci4pClose(int socketid) => SceError.UNKNOWN_UID;
    [Hle] int sceKernelDeci4pRead(int socketid, uint buffer, uint size, uint reserved) => SceError.UNKNOWN_UID;
    [Hle] int sceKernelDeci4pWrite(int socketid, uint buffer, uint size, uint reserved) => SceError.UNKNOWN_UID;
    [Hle] int sceKernelDeci4pRegisterCallback(int socketid, int cbid) => SceError.UNKNOWN_UID;
    [Hle] int sceKernelDeci4pIsProcessAttached() => 0;
    [Hle] int sceKernelDeci4pEnableWatchpoint() => 0;
    [Hle] int sceKernelDeci4pDisableWatchpoint() => 0;
    [Hle] int sceKernelDeci4pCreateHostProcess() => SceError.ERROR;
    [Hle] int sceKernelDeci4pCreateHostProcessAndWait() => SceError.ERROR;

    // ---------------------------------------------------------------- SceUlobjMgr / SceUlobjDbg (used by the LLE libult)

    [Hle] int _sceUlobjMgrRegisterLibultProtocolRevision(uint revision) => 0;
    [Hle] int _sceUlobjMgrStartSupportingUserlevelObject() => 0;
    [Hle] int _sceUlobjMgrStopSupportingUserlevelObject() => 0;
    [Hle] int SceUlobjDbg_D7F0F610() => 0;
    [Hle] int SceUlobjDbg_F9C0F5DA() => 0;
}
