using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// The SceAppUtil exports that apputil.suprx 3.74 (decompiled, study only) gives the other system modules
/// (apputil_ext, the save data and message dialogs): the SDK version record, the event callback, the background
/// download count.
/// </summary>
public sealed partial class AppUtil
{
    const int ERR_NULL_RECORD = unchecked((int)0x801006D0);

    /// <summary>The callback NID_CD0E5C59 registered and its argument (0 when none).</summary>
    [SnapshotMissing] // states of builds before it: the call did not exist, so nothing was registered
    uint _eventCallback;

    [SnapshotMissing]
    uint _eventCallbackArgument;

    /// <summary>NID_A3E0694F(record): a 0x40-byte record, zeroed, with the main module's SDK version first;
    /// 0x801006D0 for NULL, 0x80100601 before sceAppUtilInit.</summary>
    [Hle(Nid = 0xA3E0694F, Library = "SceAppUtil")]
    int NID_A3E0694F(uint record)
    {
        if (record == 0)
        {
            return ERR_NULL_RECORD;
        }

        if (!_initialized)
        {
            return ERR_NOT_INITIALIZED;
        }

        GuestMemory.Fill(record, 0, 0x40);
        GuestMemory.W32(record, MainModuleSdkVersion());
        return 0;
    }

    /// <summary>NID_CD0E5C59(callback, argument): keeps the callback NID_F93C691C calls (NULL removes it, and the
    /// argument with it); 0.</summary>
    [Hle(Nid = 0xCD0E5C59, Library = "SceAppUtil")]
    int NID_CD0E5C59(uint callback, uint argument)
    {
        _eventCallback = callback;
        _eventCallbackArgument = callback != 0 ? argument : 0;
        return 0;
    }

    /// <summary>NID_F93C691C(first, second): calls the registered callback with (first, second, its argument).</summary>
    [Hle(Nid = 0xF93C691C, Library = "SceAppUtil")]
    void NID_F93C691C(uint first, uint second)
    {
        if (_eventCallback != 0 && GuestThread.Current is { } thread)
        {
            thread.CallGuest(_eventCallback, first, second, _eventCallbackArgument);
        }
    }

    /// <summary>NID_B2FE3BB1(int *waiting, int *running): the background download queue's entries of type 1 that are
    /// waiting (state 0) and running (state 1), from sceAppMgrBgdlGetQueueStatus; 0x80100601 before
    /// sceAppUtilInit.</summary>
    [Hle(Nid = 0xB2FE3BB1, Library = "SceAppUtil")]
    int NID_B2FE3BB1(uint waitingOut, uint runningOut)
    {
        if (!_initialized)
        {
            return ERR_NOT_INITIALIZED;
        }

        const uint EntrySize = 0x38;
        const int Entries = 0x20;
        uint status = Mem.Alloc(0x700, "apputil:bgdl");
        try
        {
            int result = Am.BgdlGetQueueStatusThroughEntry(status);
            if (result != 0)
            {
                return result;
            }

            int waiting = 0;
            int running = 0;
            for (uint index = 0; index < Entries; index++)
            {
                uint entry = status + index * EntrySize;
                if (GuestMemory.Read<byte>(entry + 8) == 0)
                {
                    break;
                }

                if (GuestMemory.R32(entry) == 1)
                {
                    int state = (int)GuestMemory.R32(entry + 4);
                    if (state == 0)
                    {
                        waiting++;
                    }
                    else if (state == 1)
                    {
                        running++;
                    }
                }
            }

            if (waitingOut != 0)
            {
                GuestMemory.W32(waitingOut, (uint)waiting);
            }

            if (runningOut != 0)
            {
                GuestMemory.W32(runningOut, (uint)running);
            }

            return 0;
        }
        finally
        {
            Mem.Free(status);
        }
    }
}
