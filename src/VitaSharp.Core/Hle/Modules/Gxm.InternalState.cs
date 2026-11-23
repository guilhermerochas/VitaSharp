using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

// libgxm_es4 3.74's internal entries that the HLE can answer from its own state: the old _sceGxmBeginScene (its
// valid region struct carries the { xMax, yMax } pair at +8), whether GXM is initialized, whether a range is GPU-mapped
// memory, whether the immediate context is inside a scene, and the version-checked setters of the Razor callbacks and
// of the initialize parameters (version 0x10005 only, anything else clears them).
public sealed partial class Gxm
{
    const uint InternalVersion = 0x10005;
    const int ERR_MEMORY_NOT_MAPPED = unchecked((int)0x805B0017);
    [SnapshotMissing] // states of builds before it: the call did not exist, so it was never set
    uint _callbackVersion, _initParamVersion;

    [Hle(Cost = HleCost.KernelEntry)]
    int _sceGxmBeginScene(uint ctx, uint flags, uint renderTarget, uint validRegion, uint vertexSync, uint fragmentSync, uint colorSurface, uint depthStencil)
        => BeginScene(ctx, renderTarget, validRegion != 0 ? validRegion + 8 : 0, fragmentSync, colorSurface, ReadDepthSurface(depthStencil), depthValid: true);

    [Hle] int sceGxmInitializedInternal() => _initialized ? 0 : ERR_UNINITIALIZED;

    /// <summary>libgxm's GPU device data (the PVRSRV_DEV_DATA its own data segment holds: services connection and
    /// device cookie), one block for the process like the module's static data; 0 until first asked for.</summary>
    [SnapshotMissing] // states of builds before it: allocated again when asked for
    uint _deviceData;

    /// <summary>sceGxmGetTopContextInternal(): the address of libgxm's device data once sceGxmInitialize has run, NULL
    /// before (and after sceGxmTerminate).</summary>
    [Hle(ReturnsValue = true)]
    uint sceGxmGetTopContextInternal()
    {
        if (!_initialized)
        {
            return 0;
        }

        if (_deviceData == 0)
        {
            _deviceData = AllocObj(16, "SceGxm device data");
        }

        return _deviceData;
    }

    /// <summary>(const void *addr, SceSize size, uint flags): 0 when the range lies in memory mapped with
    /// sceGxmMapMemory.</summary>
    [Hle]
    int sceGxmCheckMemoryInternal(uint addr, uint size, uint flags)
    {
        if (!_initialized)
        {
            return ERR_UNINITIALIZED;
        }

        ulong end = (ulong)addr + size;
        foreach (var (b, s) in _mapped.Values)
        {
            if (addr >= b && end <= (ulong)b + s)
            {
                return 0;
            }
        }

        return ERR_MEMORY_NOT_MAPPED;
    }

    /// <summary>Whether the immediate rendering context has a scene open (0 before sceGxmInitialize).</summary>
    [Hle] bool sceGxmRenderingContextIsWithinSceneInternal() => _initialized && _contexts.Values.Any(c => !c.Deferred && c.InScene);

    /// <summary>(int version, const SceGxmCallbacks *callbacks): kept for version 0x10005 (the Razor GPU hooks, which
    /// nothing here calls), cleared otherwise.</summary>
    [Hle(Cost = HleCost.UserCall)] void sceGxmSetCallbackInternal(int version, uint callbacks) => _callbackVersion = version == InternalVersion && callbacks != 0 ? InternalVersion : 0;

    /// <summary>(int version, const void *param 0x48): the same for the initialize parameters.</summary>
    [Hle(Cost = HleCost.UserCall)] void sceGxmSetInitializeParamInternal(int version, uint param) => _initParamVersion = version == InternalVersion && param != 0 ? InternalVersion : 0;
}
