using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// ScePamgr (the kernel's Performance Analyzer service that libperf.suprx 3.74 drives: the ARM PMU per thread, the
/// timebase, the Razor trace buffers and bookmarks) and SceDTrace (the DTrace client of libperf's probes). The PMU and
/// timebase answer like ScePerf here (counters at 0, a 1 MHz timebase of process time); the trace buffers, the PA
/// register block and DTrace need the analyzer hardware / the devkit's /dev/dtrace, which the emulator does not have:
/// they keep a documented default (nothing is captured, the DTrace device does not exist).
/// </summary>
public sealed partial class DebugPerf
{
    const int ERRNO_ENOENT = unchecked((int)0x80010002), ERRNO_EBADF = unchecked((int)0x80010009);

    // ---------------------------------------------------------------- ARM PMU (as ScePerf: nothing counts)

    [Hle] int sceKernelPerfArmPmonOpen(int thid) => 0;
    [Hle] int sceKernelPerfArmPmonClose(int thid) => 0;
    [Hle] int sceKernelPerfArmPmonReset(int thid) => 0;
    [Hle] int sceKernelPerfArmPmonSelectEvent(int thid, uint counter, uint eventCode) => 0;
    [Hle] int sceKernelPerfArmPmonStart(int thid) => 0;
    [Hle] int sceKernelPerfArmPmonStop(int thid) => 0;
    [Hle] int sceKernelPerfArmPmonSetCounterValue(int thid, uint counter, uint value) => 0;

    /// <summary>libkernel's copy of PMUSERENR (read at its start): user mode may use the PMU, which is what ScePerf's
    /// counters here assume.</summary>
    [Hle] uint sceKernelGetPMUSERENR() => 1;

    /// <summary>(SceUID thid, int counter, uint *value): a thread's PMU counter (with value NULL the count is the
    /// answer); nothing counts, as in ScePerf.</summary>
    [Hle("_sceKernelPMonThreadGetCounter")]
    [Hle("sceKernelPMonThreadGetCounter")] // libkernel forwards to the syscall
    int PMonThreadGetCounter(int thid, int counter, uint value) { if (value != 0) GuestMemory.W32(value, 0); return 0; }

    // ---------------------------------------------------------------- timebase

    [Hle] uint sceKernelPaGetTimebaseFrequency() => TimebaseFrequency;
    [Hle] ulong sceKernelPaGetTimebaseValue() => K.ProcessTimeUs;

    // ---------------------------------------------------------------- the Razor trace hardware (none)

    uint _paIo;

    /// <summary>The PA register block libperf maps at module start (it refuses to start on 0): a zeroed page here.</summary>
    [Hle(Default = true)]
    uint sceKernelPaGetIoBaseAddress()
    {
        if (_paIo == 0) { _paIo = Mem.Alloc(GuestMemory.PageSize, "PA registers (HLE)"); GuestMemory.Fill(_paIo, 0, GuestMemory.PageSize); }
        return _paIo;
    }

    [Hle(Default = true)] int sceKernelPaRegister() => 0;
    [Hle(Default = true)] int sceKernelPaRegisterShared() => 0;
    [Hle(Default = true)] int sceKernelPaUnregister(int key) => 0;
    [Hle(Default = true)] int sceKernelPaGetSharedKey() => 0;
    [Hle(Default = true)] int _sceKernelPaSetupTraceBufferByKey(int key, uint param) => 0;
    [Hle(Default = true)] int _sceKernelPaAddArmTraceByKey(int key, uint param) => 0;
    [Hle(Default = true)] int _sceKernelPaAddBusTraceByKey(int key, uint param) => 0;
    [Hle(Default = true)] int _sceKernelPaAddCounterTraceByKey(int key, uint param) => 0;
    [Hle(Default = true)] int _sceKernelPaAddGpuTraceByKey(int key, uint param) => 0;
    [Hle(Default = true)] int sceKernelPaRemoveArmTraceByKey(int key) => 0;
    [Hle(Default = true)] int sceKernelPaRemoveBusTraceByKey(int key) => 0;
    [Hle(Default = true)] int sceKernelPaRemoveCounterTraceByKey(int key) => 0;
    [Hle(Default = true)] int sceKernelPaRemoveGpuTraceByKey(int key) => 0;
    [Hle(Default = true)] int sceKernelPaStartByKey(int key) => 0;
    [Hle(Default = true)] int sceKernelPaStopByKey(int key) => 0;
    [Hle(Default = true)] int sceKernelPaSetBookmarkChannelEnableByKey(int key, uint channel, int enable) => 0;
    [Hle(Default = true)] int sceKernelPaInsertBookmark(uint channel, uint data, uint a2) => 0;
    /// <summary>(int key, void *buf): nothing was sampled.</summary>
    [Hle(Default = true)] int _sceKernelPaGetGpuSampledData(int key, uint buf) => 0;
    [Hle(Default = true)] uint sceKernelPaGetTraceBufferSize(int key) => 0;
    [Hle(Default = true)] int sceKernelPaGetTraceBufferStatus(int key) => 0;
    [Hle(Default = true)] uint sceKernelPaGetWritePointer(int key) => 0;

    // ---------------------------------------------------------------- SceDTrace (no /dev/dtrace on the unit)

    [Hle(Default = true)] int sceDTraceClientOpen(uint path, int flags) => ERRNO_ENOENT;
    [Hle(Default = true)] int sceDTraceClientClose(int fd) => ERRNO_EBADF;
    [Hle(Default = true)] int sceDTraceClientIoctl(int fd, uint cmd, uint buf, uint size) => ERRNO_EBADF;
    [Hle(Default = true)] int sceDTraceHelperIoctl(uint cmd, uint buf, uint size) => ERRNO_ENOENT;
    [Hle(Default = true)] int sceDTraceAddHelperDof(uint dof, uint size) => ERRNO_ENOENT;
    [Hle(Default = true)] int sceDTraceRemoveHelperDof(int handle) => ERRNO_ENOENT;
}
