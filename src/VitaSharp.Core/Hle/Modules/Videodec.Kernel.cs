using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

// SceAvcodecForDriver 0xC84AFFC4 (os0:bootimage SceAvcodec 3.74, read in the module, study only): the application
// manager (SceAppMgr's module start) registers its codec resource callback, which the module calls when a decoder
// library of a process is initialized and when its instance is released. One callback only: a second registration is
// 0x80620808. The HLE decoders keep their libraries themselves and have no AppMgr module of the firmware to call
// back into, so the callback is kept and not called.
public sealed partial class Videodec
{
    const int ERR_CALLBACK_REGISTERED = unchecked((int)0x80620808);

    readonly object _resourceCallbackLock = new();

    /// <summary>The application manager's codec resource callback (0: none registered).</summary>
    [SnapshotMissing] // states of builds before it: none registered
    uint _resourceCallback;

    /// <summary>The callback the last registration stored (0: none).</summary>
    public uint ResourceCallback
    {
        get
        {
            lock (_resourceCallbackLock)
            {
                return _resourceCallback;
            }
        }
    }

    /// <summary>NID_C84AFFC4(callback): registers the application manager's codec resource callback; a second
    /// registration is 0x80620808 and keeps the first.</summary>
    [Hle(Nid = 0xC84AFFC4, Library = "SceAvcodecForDriver")]
    int ksceAvcodecRegisterResourceCallback(uint callback)
    {
        lock (_resourceCallbackLock)
        {
            if (_resourceCallback != 0)
            {
                return ERR_CALLBACK_REGISTERED;
            }

            _resourceCallback = callback;
            return 0;
        }
    }
}
