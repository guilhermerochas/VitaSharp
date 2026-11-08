namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// paf::inputdevice's module state: the pad's button mask (SetMask ORs bits in), lock counters (Lock counts up,
/// Unlock catches the unlocked count up to it, locked while the first is ahead), the device handlers (Set returns the
/// previous one) and the current port; the touch screens' per-port (front 0, back 1) lock counters, masks, handlers,
/// default region flags and their latest/previous point tables (8 points of 0x20 bytes per port); the screen
/// resolution the touch coordinates are scaled to (the Vita's 960x544 until an application sets another).
/// </summary>
public sealed partial class Paf
{
    uint _padMask, _padLocks, _padUnlocks, _padHandler, _motionHandler, _motionLocks;
    /// <summary>The port pad reads: 0, the handheld's own controls.</summary>
    readonly uint _padPort = 0;
    readonly uint[] _touchLocks = new uint[2], _touchUnlocks = new uint[2], _touchMask = new uint[2], _touchHandler = new uint[2], _touchRegion = new uint[2];
    uint _touchData, _touchPrev;
    float _touchW = 960, _touchH = 544, _touchX, _touchY, _touchVw = 960, _touchVh = 544;

    [Hle("_ZN3paf11inputdevice3pad7SetMaskEj")] void PadSetMask(uint bits) => _padMask |= bits;
    [Hle("_ZN3paf11inputdevice3pad7GetMaskEv")] uint PadGetMask() => _padMask;
    [Hle("_ZN3paf11inputdevice3pad4LockEv")] void PadLock() => _padLocks++;
    [Hle("_ZN3paf11inputdevice3pad6UnlockEv")] void PadUnlock() { if ((int)_padLocks > (int)_padUnlocks) _padUnlocks++; }
    [Hle("_ZN3paf11inputdevice3pad8IsLockedEv")] bool PadIsLocked() => (int)_padLocks > (int)_padUnlocks;
    [Hle("_ZN3paf11inputdevice3pad16SetDeviceHandlerEPFvPNS1_4DataEE")] uint PadSetHandler(uint fn) { uint old = _padHandler; _padHandler = fn; return old; }
    [Hle("_ZN3paf11inputdevice3pad11GetCurrPortEv")] uint PadCurrPort() => _padPort;

    [Hle("_ZN3paf11inputdevice12motionsensor16SetDeviceHandlerEPFvPNS1_4DataEE")] uint MotionSetHandler(uint fn) { uint old = _motionHandler; _motionHandler = fn; return old; }
    [Hle("_ZN3paf11inputdevice12motionsensor4LockEv")] void MotionLock() => _motionLocks++;
    [Hle("_ZN3paf11inputdevice12motionsensor6UnlockEv")] void MotionUnlock() { if ((int)_motionLocks > 0) _motionLocks--; }
    [Hle("_ZN3paf11inputdevice12motionsensor8IsLockedEv")] bool MotionIsLocked() => (int)_motionLocks > 0;

    [Hle("_ZN3paf11inputdevice11touchscreen4LockEj")] void TouchLock(uint port) { if (port < 2) _touchLocks[port]++; }
    [Hle("_ZN3paf11inputdevice11touchscreen6UnlockEj")] void TouchUnlock(uint port) { if (port < 2 && _touchLocks[port] > _touchUnlocks[port]) _touchUnlocks[port]++; }
    [Hle("_ZN3paf11inputdevice11touchscreen8IsLockedEj")] bool TouchIsLocked(uint port) => port < 2 && _touchLocks[port] > _touchUnlocks[port];
    [Hle("_ZN3paf11inputdevice11touchscreen7SetMaskEjj")] uint TouchSetMask(uint port, uint bits) { if (port >= 2) return 0; _touchMask[port] |= bits; return _touchMask[port]; }
    [Hle("_ZN3paf11inputdevice11touchscreen7GetMaskEj")] uint TouchGetMask(uint port) => port < 2 ? _touchMask[port] : 0;
    [Hle("_ZN3paf11inputdevice11touchscreen16SetDeviceHandlerEjPFvPNS1_8DataListEE")] uint TouchSetHandler(uint port, uint fn) { if (port >= 2) return 0; uint old = _touchHandler[port]; _touchHandler[port] = fn; return old; }

    /// <summary>(port, on): the region flag of the port; a port past the back panel is 0x80AF5120.</summary>
    [Hle("_ZN3paf11inputdevice11touchscreen21ActivateDefaultRegionEjb")]
    int TouchActivateRegion(uint port, bool on)
    {
        if (port >= 2) return unchecked((int)0x80AF5120);
        _touchRegion[port] = on ? _touchRegion[port] | 1 : _touchRegion[port] & ~1u;
        return 0;
    }

    uint TouchTable(bool previous)
    {
        if (_touchData == 0) { _touchData = Static(0x200); _touchPrev = Static(0x200); }
        return previous ? _touchPrev : _touchData;
    }

    /// <summary>(port, index, const Data **out): the point record (0x20 bytes) of the latest frame, NULL past 2x8.</summary>
    [Hle("_ZN3paf11inputdevice11touchscreen7GetDataEjjPPKNS1_4DataE")]
    void TouchGetData(uint port, uint index, uint outPtr) => U(outPtr, port < 2 && index < 8 ? TouchTable(false) + port * 0x100 + index * 0x20 : 0);

    [Hle("_ZN3paf11inputdevice11touchscreen10GetDataPreEjjPPKNS1_4DataE")]
    void TouchGetDataPre(uint port, uint index, uint outPtr) => U(outPtr, port < 2 && index < 8 ? TouchTable(true) + port * 0x100 + index * 0x20 : 0);

    /// <summary>(port, index, float &x, float &y): a point's position from the top-left (+8/+0xC) or the centre
    /// (+0x18/+0x1C); -1 for an index outside the table.</summary>
    [Hle("_ZN3paf11inputdevice11touchscreen19GetPointFromTopleftEjjRfS2_")]
    int TouchPointTopLeft(uint port, uint index, uint x, uint y)
    {
        if (port >= 2 || index >= 8) return -1;
        uint d = TouchTable(false) + port * 0x100 + index * 0x20;
        F(x, (int)U(d + 8)); F(y, (int)U(d + 0xC));
        return 0;
    }

    [Hle("_ZN3paf11inputdevice11touchscreen18GetPointFromCenterEjjRfS2_")]
    int TouchPointCenter(uint port, uint index, uint x, uint y)
    {
        if (port >= 2 || index >= 8) return -1;
        uint d = TouchTable(false) + port * 0x100 + index * 0x20;
        F(x, (int)U(d + 0x18)); F(y, (int)U(d + 0x1C));
        return 0;
    }

    [Hle("_ZN3paf11inputdevice11touchscreen19GetScreenResolutionERjS2_")]
    void TouchGetResolution(uint w, uint h) { U(w, (uint)_touchW); U(h, (uint)_touchH); }

    [Hle("_ZN3paf11inputdevice11touchscreen19SetScreenResolutionEjj")]
    void TouchSetResolution(uint w, uint h) { _touchW = w; _touchH = h; }

    [Hle("_ZN3paf11inputdevice11touchscreen17SetScreenViewportEiijj")]
    void TouchSetViewport(int x, int y, uint w, uint h) { _touchX = x; _touchY = y; _touchVw = w; _touchVh = h; }

    [Hle("_ZN3paf11inputdevice11touchscreen17GetScreenViewportERiS2_RjS3_")]
    void TouchGetViewport(uint x, uint y, uint w, uint h) { U(x, (uint)(int)_touchX); U(y, (uint)(int)_touchY); U(w, (uint)_touchVw); U(h, (uint)_touchVh); }
}
