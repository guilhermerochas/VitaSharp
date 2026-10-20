using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// SceHandwriting (libhandwriting, sysmodule 0x2F): handwriting recognition. There is no header, no Vita3K
/// implementation and no public documentation, so every prototype is inferred from the export names. Minimal HLE per
/// the spec: lifecycle calls succeed, recognition finds nothing, and nothing is ever written through a guest pointer
/// (no struct layout is known). The preferred long-term route is LLE of the firmware module.
/// </summary>
public sealed class Handwriting : HleModule
{
    /// <summary>Work buffer size reported to the game (real value to confirm).</summary>
    internal const int BufferSize = 0x40000;

    readonly object _lock = new();
    bool _initialized;
    uint _mode;

    internal bool Initialized { get { lock (_lock) return _initialized; } }
    internal uint Mode { get { lock (_lock) return _mode; } }

    static void Warn(string fn, uint r0 = 0, uint r1 = 0, uint r2 = 0, uint r3 = 0) =>
        Log.WarnOnce("handwriting-" + fn, $"{fn}(0x{r0:X8}, 0x{r1:X8}, 0x{r2:X8}, 0x{r3:X8}): handwriting recognition is not emulated (prototype inferred)");

    [Hle]
    int sceHandwritingGetBufferSize(uint r0)
    {
        Warn("sceHandwritingGetBufferSize", r0);
        return BufferSize;
    }

    [Hle]
    int sceHandwritingInit(uint buffer, uint size, uint param)
    {
        Warn("sceHandwritingInit", buffer, size, param);
        lock (_lock) _initialized = true;
        return 0;
    }

    [Hle]
    int sceHandwritingTerm()
    {
        lock (_lock) _initialized = false;
        return 0;
    }

    [Hle]
    int sceHandwritingSetMode(uint mode)
    {
        lock (_lock) _mode = mode;
        return 0;
    }

    /// <summary>No candidates. The result layout is unknown, so it is not written.</summary>
    [Hle]
    int sceHandwritingRecognize(uint strokes, uint numStrokes, uint result, uint maxCandidates)
    {
        Warn("sceHandwritingRecognize", strokes, numStrokes, result, maxCandidates);
        return 0;
    }

    [Hle] int sceHandwritingRegisterInit(uint r0, uint r1, uint r2, uint r3) { Warn("sceHandwritingRegisterInit", r0, r1, r2, r3); return 0; }
    [Hle] int sceHandwritingRegisterSet(uint r0, uint r1, uint r2, uint r3) { Warn("sceHandwritingRegisterSet", r0, r1, r2, r3); return 0; }
    [Hle] int sceHandwritingRegisterGetResult(uint r0, uint r1) { Warn("sceHandwritingRegisterGetResult", r0, r1); return 0; }
    [Hle] int sceHandwritingRegisterInfo(uint r0, uint r1) { Warn("sceHandwritingRegisterInfo", r0, r1); return 0; }
    [Hle] int sceHandwritingRegisterDelete(uint r0, uint r1) { Warn("sceHandwritingRegisterDelete", r0, r1); return 0; }
    [Hle] int sceHandwritingRegisterTerm(uint r0) { Warn("sceHandwritingRegisterTerm", r0); return 0; }
}
