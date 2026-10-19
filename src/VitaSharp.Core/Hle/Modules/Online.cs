using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// Network, PSN and web services. VitaSharp behaves like a console that is offline and signed out of PSN:
/// initialisation succeeds, connection attempts fail with the errors games handle gracefully.
/// </summary>
public sealed partial class Net : HleModule
{
    const int ERR_NET_ENOTCONN = unchecked((int)0x80410139), ERR_NET_ENETUNREACH = unchecked((int)0x80410133);
    const int ERR_NETCTL_INVALID_ADDR = unchecked((int)0x80412107), ERR_NETCTL_CALLBACK_MAX = unchecked((int)0x80412103);
    const int ERR_NETCTL_NOT_CONNECTED = unchecked((int)0x80412108);
    [Hle] uint sceNetHtonl(uint v) => System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(v);
    [Hle] uint sceNetNtohl(uint v) => System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(v);
    [Hle] uint sceNetHtons(uint v) => System.Buffers.Binary.BinaryPrimitives.ReverseEndianness((ushort)v);
    [Hle] uint sceNetNtohs(uint v) => System.Buffers.Binary.BinaryPrimitives.ReverseEndianness((ushort)v);

    [Hle]
    int sceNetGetMacAddress(uint addr, int flags)
    {
        if (addr == 0) return unchecked((int)0x80410116);
        byte[] mac = [0x00, 0x1F, 0xA7, 0x56, 0x49, 0x54];
        mac.CopyTo(GuestMemory.Span(addr, 6));
        return 0;
    }

    [Hle]
    uint sceNetInetNtop(int af, uint src, uint dst, uint size)
    {
        if (src == 0 || dst == 0) return 0;
        var b = GuestMemory.Span(src, 4);
        GuestMemory.WriteCString(dst, $"{b[0]}.{b[1]}.{b[2]}.{b[3]}", (int)size);
        return dst;
    }

    [Hle]
    int sceNetInetPton(int af, string src, uint dst)
    {
        if (dst == 0 || !System.Net.IPAddress.TryParse(src, out var ip)) return 0;
        ip.GetAddressBytes().AsSpan(0, 4).CopyTo(GuestMemory.Span(dst, 4));
        return 1;
    }

    [Hle]
    int sceNetGetStatisticsInfo(uint info, int flags)
    {
        if (info == 0) return unchecked((int)0x80410116);
        // SceNetStatisticsInfo: kernel free/min, packet count, qos count, libnet free/min (24 bytes)
        GuestMemory.W32(info, 0x40000); GuestMemory.W32(info + 4, 0x30000); GuestMemory.W32(info + 8, 0);
        GuestMemory.W32(info + 12, 0); GuestMemory.W32(info + 16, 0x20000); GuestMemory.W32(info + 20, 0x18000);
        return 0;
    }
    [Hle] int sceNetShowNetstat() => 0;

    // NetCtl: initialised, never connected
    // NetCtl: initialised, cable unplugged. Registered callbacks are told so once (DISCONNECTED event),
    // which is what games wait for before leaving their "checking connection" states.
    const int NETCTL_EVENT_DISCONNECTED = 1;
    sealed class NetCallback { public uint Func, Arg; public bool Notified; }
    readonly Dictionary<int, NetCallback> _inetCallbacks = new(), _adhocCallbacks = new();
    int _nextCid;


    [Hle]
    int sceNetCtlCheckCallback()
    {
        if (!_netctlInited) return NETCTL_NOT_INITIALIZED;
        var pending = new List<NetCallback>();
        lock (_inetCallbacks)
        {
            foreach (var c in _inetCallbacks.Values.Concat(_adhocCallbacks.Values))
                if (!c.Notified) { c.Notified = true; pending.Add(c); }
        }
        foreach (var c in pending) Kernel.GuestThread.Current?.CallGuest(c.Func, NETCTL_EVENT_DISCONNECTED, c.Arg);
        return 0;
    }

    // The emulated console is on a LAN (IP obtained) but has no route to Sony's servers and is signed out of PSN,
    // the same profile Vita3K presents; socket/HTTP calls fail like an unreachable server.
    [Hle] int sceNetCtlInetGetState(Ptr<int> state) { if (state.Address == 0) return ERR_NETCTL_INVALID_ADDR; state.Value = 3; return 0; }
    [Hle] int sceNetCtlInetGetResult(int eventType, Ptr<int> error) { if (error.Address == 0) return ERR_NETCTL_INVALID_ADDR; error.Value = 0; return 0; }

    [Hle]
    int sceNetCtlInetGetInfo(int code, uint info)
    {
        if (info == 0) return ERR_NETCTL_INVALID_ADDR;
        switch (code)
        {
            case 1: GuestMemory.WriteCString(info, "VitaSharp", 65); break;           // configuration name
            case 2: GuestMemory.W32(info, 0); break;                                // device: wireless
            case 3: sceNetGetMacAddress(info, 0); break;                           // ether address
            case 4: GuestMemory.W32(info, 1500); break;                             // MTU
            case 5: GuestMemory.W32(info, 1); break;                                // link up
            case 6: new byte[] { 0x00, 0x1F, 0xA7, 0x00, 0x00, 0x01 }.CopyTo(GuestMemory.Span(info, 6)); break;
            case 7: GuestMemory.WriteCString(info, "VitaSharpNet", 33); break;     // SSID
            case 8: GuestMemory.W32(info, 3); break;                                // WPA/WPA2
            case 9: GuestMemory.W32(info, 40); break;                               // rssi dBm
            case 10: GuestMemory.W32(info, 100); break;                             // rssi %
            case 11: GuestMemory.W32(info, 6); break;                               // channel
            case 12: GuestMemory.W32(info, 0); break;                               // DHCP
            case 13: GuestMemory.WriteCString(info, "", 256); break;
            case 14: GuestMemory.WriteCString(info, "", 128); break;
            case 15: GuestMemory.WriteCString(info, "192.168.0.10", 16); break;
            case 16: GuestMemory.WriteCString(info, "255.255.255.0", 16); break;
            case 17: GuestMemory.WriteCString(info, "192.168.0.1", 16); break;
            case 18: GuestMemory.WriteCString(info, "192.168.0.1", 16); break;
            case 19: GuestMemory.WriteCString(info, "0.0.0.0", 16); break;
            case 20: GuestMemory.W32(info, 0); break;                               // no proxy
            case 21: GuestMemory.WriteCString(info, "", 256); break;
            case 22: GuestMemory.W32(info, 0); break;
            default: return unchecked((int)0x80412106); // SCE_NET_CTL_ERROR_INVALID_CODE
        }
        return 0;
    }

    int RegisterNetCallback(Dictionary<int, NetCallback> table, uint func, Ptr<int> cid)
    {
        if (func == 0 || cid.Address == 0) return ERR_NETCTL_INVALID_ADDR;
        lock (_inetCallbacks)
        {
            if (table.Count >= 8) return ERR_NETCTL_CALLBACK_MAX;
            int id = _nextCid++;
            table[id] = new NetCallback { Func = func, Arg = 0 };
            cid.Value = id;
            return id;
        }
    }

    [Hle]
    int sceNetCtlInetRegisterCallback(uint func, uint arg, Ptr<int> cid)
    {
        int r = RegisterNetCallback(_inetCallbacks, func, cid);
        if (r < 0) return r;
        lock (_inetCallbacks) _inetCallbacks[r].Arg = arg;
        return 0;
    }

    [Hle] int sceNetCtlInetUnregisterCallback(int cid) { lock (_inetCallbacks) _inetCallbacks.Remove(cid); return 0; }
    [Hle] int sceNetCtlGetNatInfo(uint info) { if (info != 0) { GuestMemory.Fill(info, 0, 16); GuestMemory.W32(info, 16); } return 0; }
    [Hle] int sceNetCtlAdhocGetState(Ptr<int> state) { state.TrySet(0); return 0; }
    [Hle] int sceNetCtlAdhocGetInAddr(uint addr) => ERR_NETCTL_NOT_CONNECTED;
    [Hle]
    int sceNetCtlAdhocRegisterCallback(uint func, uint arg, Ptr<int> cid)
    {
        int r = RegisterNetCallback(_adhocCallbacks, func, cid);
        if (r < 0) return r;
        lock (_inetCallbacks) _adhocCallbacks[r].Arg = arg;
        return 0;
    }

    [Hle] int sceNetCtlAdhocUnregisterCallback(int cid) { lock (_inetCallbacks) _adhocCallbacks.Remove(cid); return 0; }

    // dialogs that check the network report "cancelled"
    [Hle] int sceNetCheckDialogInit(uint param) => 0;
    [Hle] int sceNetCheckDialogGetStatus() => 2;
    [Hle] int sceNetCheckDialogGetResult(uint result) { if (result != 0) { GuestMemory.Fill(result, 0, 0x2C); GuestMemory.W32(result, unchecked((uint)0x80020400 | 1)); } return 0; }
    [Hle] int sceNetCheckDialogTerm() => 0;
    [Hle] int sceNetCheckDialogAbort() => 0;
    [Hle] int sceNetCtlAdhocDisconnect() => 0;

}

public sealed class Np : HleModule
{
    const int ERR_SIGNED_OUT = unchecked((int)0x80550006);

    // NP initialisation, service state and auth live in NpManager (Np.cs); Lookup in NpUtility (NpCommunity.cs).
    [Hle] int sceNpBasicInit(uint opt) => 0;
    [Hle] int sceNpBasicTerm() => 0;
    [Hle] int sceNpBasicCheckCallback() => 0;
    [Hle] int sceNpBasicRegisterHandler(uint handlers, uint ctx, uint arg) => 0;
    [Hle] int sceNpBasicUnregisterHandler() => 0;
    [Hle] int sceNpBasicRegisterJoinGameAckHandler(uint h, uint arg) => 0;
    [Hle] int sceNpBasicUnregisterJoinGameAckHandler() => 0;
    [Hle] int sceNpBasicJoinGameAckResponseSend(uint p) => ERR_SIGNED_OUT;
    [Hle] int sceNpBasicSendInGameDataMessage(uint to, uint data, uint size) => ERR_SIGNED_OUT;
    [Hle] int sceNpBasicSetInGamePresence(uint p) => ERR_SIGNED_OUT;
    [Hle] int sceNpBasicGetFriendListEntryCount(Ptr<uint> count) { count.TrySet(0); return 0; }
    [Hle] int sceNpBasicGetFriendListEntries(uint start, uint entries, uint num, Ptr<uint> retrieved) { retrieved.TrySet(0); return 0; }
    [Hle] int sceNpBasicRecordPlaySessionLog(uint npId, uint type) => 0; // the "recently played with" list lives on PSN
    [Hle] int sceNpManagerGetChatRestrictionFlag(Ptr<int> flag) { flag.TrySet(0); return 0; }
    [Hle] int sceNpBasicGetBlockListEntryCount(Ptr<uint> count) { count.TrySet(0); return 0; }
    [Hle] int sceNpBasicGetBlockListEntries(uint start, uint entries, uint num, Ptr<uint> retrieved) { retrieved.TrySet(0); return 0; }

    [Hle] int sceNpPartyInit(uint p) => 0;
    [Hle] int sceNpPartyTerm() => 0;
    [Hle] int sceNpPartyCheckCallback() => 0;
    [Hle] int sceNpPartyRegisterHandler(uint h, uint arg) => 0;
    [Hle] int sceNpPartyGetMembers(uint members) => unchecked((int)0x80553402);
    [Hle] int sceNpPartyGetMemberInfo(uint id, uint info) => unchecked((int)0x80553402);
    [Hle] int sceNpPartyGetMemberVoiceInfo(uint id, uint info) => unchecked((int)0x80553402);

    [Hle] int sceNpBasicRegisterInGameDataMessageHandler(uint handler, uint arg) => 0; // messages only arrive from PSN
    [Hle] int sceNpBasicUnregisterInGameDataMessageHandler() => 0;

    [Hle] int sceNpMessageDialogInit(uint param) => 0;
    [Hle] int sceNpMessageDialogGetStatus() => 2;
    [Hle] int sceNpMessageDialogGetResult(uint result) { if (result != 0) GuestMemory.W32(result, 0); return 0; }
    [Hle] int sceNpMessageDialogAbort() => 0;
    [Hle] int sceNpMessageDialogTerm() => 0;
}

/// <summary>Common dialogs: they complete immediately with the default choice.</summary>
public sealed class CommonDialog : HleModule
{
    const int StatusNone = 0, StatusRunning = 1, StatusFinished = 2;
    int _msg = StatusNone, _save = StatusNone;

    [Hle] int sceCommonDialogUpdate(uint param) => 0;
    [Hle] int sceCommonDialogSetConfigParam(uint param) => 0; // language / enter button for system dialogs (they complete immediately)
    [Hle] bool sceCommonDialogIsRunning() => false;

    [Hle]
    int sceMsgDialogInit(uint param)
    {
        if (param != 0)
        {
            uint mode = GuestMemory.R32(param + 0x18);
            uint userMsg = GuestMemory.R32(param + 0x1C);
            if (mode == 1 && userMsg != 0) Log.Info($"MsgDialog: {GuestMemory.ReadCString(GuestMemory.R32(userMsg + 4))}");
        }
        _msg = StatusFinished;
        return 0;
    }

    [Hle] int sceMsgDialogGetStatus() => _msg;
    [Hle] int sceMsgDialogTerm() { _msg = StatusNone; return 0; }
    [Hle] int sceMsgDialogClose() { _msg = StatusFinished; return 0; }
    [Hle] int sceMsgDialogAbort() { _msg = StatusFinished; return 0; }

    [Hle]
    int sceMsgDialogGetResult(uint result)
    {
        if (result == 0) return unchecked((int)0x80020401);
        GuestMemory.Fill(result, 0, 0x2C);
        GuestMemory.W32(result + 8, 1); // buttonId: YES / OK
        return 0;
    }

    // ---- SceSaveDataDialog: the dialog "runs" (status RUNNING) and its work completes at once (substatus FINISHED:
    // the user confirmed / the slot was shown); the application then calls Finish to close it (status FINISHED) and
    // Term. Progress-bar dialogs stay running until the application finishes them itself.
    const int SaveModeFixed = 1, SaveModeList = 2, SaveModeUserMsg = 3, SaveModeSystemMsg = 4, SaveModeErrorCode = 5, SaveModeProgressBar = 6;
    int _saveSub = StatusNone, _saveMode, _saveSlot = -1;
    uint _saveUserData, _saveSlotInfo;

    [Hle]
    int sceSaveDataDialogInit(uint param)
    {
        if (param == 0) return unchecked((int)0x80020401);
        if (_save != StatusNone) return unchecked((int)0x80020404); // BUSY
        // SceSaveDataDialogParam: sdkVersion, SceCommonDialogParam (76 bytes), mode, dispType, fixed, list, userMsg,
        // sysMsg, errorCode, progressBar, slotConf, flag, userdata
        _saveMode = (int)GuestMemory.R32(param + 0x50);
        _saveUserData = GuestMemory.R32(param + 0x78);
        uint sub = _saveMode switch { SaveModeFixed => GuestMemory.R32(param + 0x58), SaveModeList => GuestMemory.R32(param + 0x5C),
            SaveModeUserMsg => GuestMemory.R32(param + 0x60), SaveModeSystemMsg => GuestMemory.R32(param + 0x64), _ => 0 };
        _saveSlot = _saveMode switch
        {
            SaveModeFixed => (int)GuestMemory.R32(sub),                                  // targetSlot.id
            SaveModeList => GuestMemory.R32(sub) != 0 ? (int)GuestMemory.R32(GuestMemory.R32(sub)) : -1, // slotList[0].id
            SaveModeUserMsg or SaveModeSystemMsg => (int)GuestMemory.R32(sub + 8),      // targetSlot.id after type/value
            _ => -1,
        };
        _save = StatusRunning;
        _saveSub = _saveMode == SaveModeProgressBar ? StatusRunning : StatusFinished;
        Log.Info($"sceSaveDataDialogInit mode {_saveMode} dispType {GuestMemory.R32(param + 0x54)} slot {_saveSlot} sub {sub:X8} subType {(sub != 0 ? GuestMemory.R32(sub) : 0)} value {(sub != 0 ? GuestMemory.R32(sub + 4) : 0)} flag {GuestMemory.R32(param + 0x74)}");
        return 0;
    }

    [Hle] int sceSaveDataDialogContinue(uint param) { if (_save != StatusRunning) return unchecked((int)0x80020405); _saveSub = StatusFinished; return 0; }
    [Hle] int sceSaveDataDialogGetStatus() => _save;
    [Hle] int sceSaveDataDialogGetSubStatus() => _save == StatusRunning ? _saveSub : StatusNone;
    [Hle] int sceSaveDataDialogFinish(uint param) { if (_save != StatusRunning) return unchecked((int)0x80020405); _save = StatusFinished; return 0; }
    [Hle] int sceSaveDataDialogAbort() { if (_save != StatusRunning) return unchecked((int)0x80020405); _save = StatusFinished; return 0; }
    [Hle] int sceSaveDataDialogTerm() { _save = StatusNone; _saveSub = StatusNone; return 0; }
    [Hle] int sceSaveDataDialogProgressBarInc(int target, uint delta) => 0;
    [Hle] int sceSaveDataDialogProgressBarSetValue(int target, uint rate) => 0;
    [Hle] int sceSaveDataDialogSubClose() { _saveSub = StatusFinished; return 0; }

    [Hle]
    int sceSaveDataDialogGetResult(uint result)
    {
        if (result == 0) return unchecked((int)0x80020401);
        // SceSaveDataDialogResult: mode, result, buttonId, slotId, slotInfo*, userdata, reserved[32]
        GuestMemory.Fill(result, 0, 0x38);
        GuestMemory.W32(result, (uint)_saveMode);
        GuestMemory.W32(result + 8, 1); // SCE_SAVEDATA_DIALOG_BUTTON_ID_OK / YES
        GuestMemory.W32(result + 12, (uint)_saveSlot);
        if (_saveSlot >= 0)
        {
            // SceSaveDataDialogSlotInfo { isExist, slotParam*, reserved[32] }
            if (_saveSlotInfo == 0) _saveSlotInfo = Emu.Memory.Alloc(0x100, "savedata dialog slot info");
            var appUtil = Emu.Hle.Modules.OfType<AppUtil>().First();
            bool exists = appUtil.SlotExists((uint)_saveSlot);
            GuestMemory.Fill(_saveSlotInfo, 0, 0x100);
            GuestMemory.W32(_saveSlotInfo, exists ? 1u : 0u);
            if (exists) { appUtil.ReadSlotParam((uint)_saveSlot, _saveSlotInfo + 0x40); GuestMemory.W32(_saveSlotInfo + 4, _saveSlotInfo + 0x40); }
            GuestMemory.W32(result + 16, _saveSlotInfo);
        }
        GuestMemory.W32(result + 20, _saveUserData);
        return 0;
    }
}

/// <summary>Motion sensors (device at rest, flat), camera shutter sound, system gestures (none). SceCamera is in Camera.cs.</summary>
public sealed class Sensors : HleModule
{
    const int ERR_OUT_OF_BOUNDS = unchecked((int)0x80360205), ERR_NOT_SAMPLING = unchecked((int)0x80360206),
        ERR_ALREADY_SAMPLING = unchecked((int)0x80360207), ERR_NULL_PARAMETER = unchecked((int)0x80360209),
        ERR_ANGLE_OUT_OF_RANGE = unchecked((int)0x8036020A);
    const long SampleIntervalUs = 16667; // sensor sample spacing: unconfirmed, the vblank period like Ctrl/Touch

    // defaults from psp2/motion.h ("Enabled by default") and Vita3K (angle threshold 20 degrees)
    bool _sampling, _magnetometer;
    int _tiltCorrection = 1, _deadband = 1, _gyroBiasCorrection = 1;
    float _angleThreshold = 20.0f;
    uint _counter;

    internal bool Sampling => _sampling;

    [Hle] int sceMotionStartSampling() { if (_sampling) return ERR_ALREADY_SAMPLING; _sampling = true; return 0; }
    [Hle] int sceMotionStopSampling() { if (!_sampling) return ERR_NOT_SAMPLING; _sampling = false; return 0; }
    [Hle] int sceMotionReset() => 0; // static orientation: nothing to reset
    [Hle] int sceMotionMagnetometerOn() { _magnetometer = true; return 0; }
    [Hle] int sceMotionMagnetometerOff() { _magnetometer = false; return 0; }
    [Hle] int sceMotionGetMagnetometerState() => _magnetometer ? 1 : 0;
    [Hle] int sceMotionSetTiltCorrection(int e) { _tiltCorrection = e; return 0; }
    [Hle] int sceMotionGetTiltCorrection() => _tiltCorrection;
    [Hle] int sceMotionSetDeadband(int e) { _deadband = e; return 0; }
    [Hle] int sceMotionGetDeadband() => _deadband;
    [Hle] int sceMotionSetGyroBiasCorrection(int e) { _gyroBiasCorrection = e; return 0; }
    [Hle] int sceMotionGetGyroBiasCorrection() => _gyroBiasCorrection;

    [Hle]
    int sceMotionSetAngleThreshold(float angle)
    {
        if (float.IsNaN(angle) || angle < 0 || angle > 45.0f) return ERR_ANGLE_OUT_OF_RANGE;
        _angleThreshold = angle;
        return 0;
    }

    [Hle] float sceMotionGetAngleThreshold() => _angleThreshold;

    /// <summary>Yaw re-centring of the fused orientation: the host has no motion source, so there is nothing to rotate.</summary>
    [Hle] int sceMotionRotateYaw(float radians) => 0;

    /// <summary>Device at rest, screen up: the axis against gravity is +Z.</summary>
    [Hle]
    int sceMotionGetBasicOrientation(uint v)
    {
        if (!_sampling) return ERR_NOT_SAMPLING;
        if (v == 0) return ERR_NULL_PARAMETER;
        GuestMemory.Write(v, 0.0f);
        GuestMemory.Write(v + 4, 0.0f);
        GuestMemory.Write(v + 8, 1.0f);
        return 0;
    }

    /// <summary>SceMotionDeviceLocation (0x30): accelerometer and gyro positions in mm, OLED model (PCH-1000) values
    /// from psp2/motion.h.</summary>
    [Hle]
    int sceMotionGetDeviceLocation(uint loc)
    {
        if (loc == 0) return ERR_NULL_PARAMETER;
        GuestMemory.Fill(loc, 0, 0x30);
        float[] v = [-32, 12, -8, -33, 6, -8];
        for (int i = 0; i < v.Length; i++) GuestMemory.Write(loc + (uint)i * 4, v[i]);
        return 0;
    }

    /// <summary>
    /// Raw accelerometer/gyro records (SceMotionSensorState, 0x40 each), oldest first: device at rest, so only the
    /// counters and timestamps change. 64 records must be accepted (Invizimals reads 64). Like sceMotionGetState this
    /// stays permissive when sampling was not started.
    /// </summary>
    [Hle]
    int sceMotionGetSensorState(uint states, int numRecords)
    {
        if (states == 0) return ERR_NULL_PARAMETER;
        if (numRecords is <= 0 or > 64) return ERR_OUT_OF_BOUNDS;
        long now = (long)Emu.Kernel.ProcessTimeUs;
        WriteSensorStates(states, numRecords, now, _counter);
        _counter++;
        return 0;
    }

    internal static void WriteSensorStates(uint states, int numRecords, long nowUs, uint counter)
    {
        for (int i = 0; i < numRecords; i++)
        {
            uint p = states + (uint)i * 0x40;
            int age = numRecords - 1 - i;
            long ts = nowUs - age * SampleIntervalUs;
            GuestMemory.Fill(p, 0, 0x40);
            GuestMemory.Write(p + 8, -1.0f);                 // accelerometer z = -1 g
            GuestMemory.W32(p + 0x24, (uint)ts);             // timestamp (us)
            GuestMemory.W32(p + 0x28, counter - (uint)age);  // counter
            GuestMemory.Write(p + 0x30, (ulong)ts);          // hostTimestamp
        }
    }

    [Hle]
    int sceMotionGetState(uint state)
    {
        if (state == 0) return unchecked((int)0x80360001);
        GuestMemory.Fill(state, 0, 0xF8);
        GuestMemory.W32(state, (uint)Emu.Kernel.ProcessTimeUs);
        GuestMemory.Write(state + 12, -1.0f);         // acceleration z
        GuestMemory.Write(state + 52, 1.0f);          // quaternion w
        for (uint i = 0; i < 4; i++)
        {
            GuestMemory.Write(state + 56 + i * 20, 1.0f);   // rotation matrix identity
            GuestMemory.Write(state + 120 + i * 20, 1.0f);  // NED matrix identity
        }
        GuestMemory.Write(state + 188, 0.0f);
        GuestMemory.Write(state + 192, 0.0f);
        GuestMemory.Write(state + 196, 1.0f);         // basic orientation
        GuestMemory.Write(state + 200, Emu.Kernel.ProcessTimeUs);
        return 0;
    }

    [Hle] int sceShutterSoundPlay(uint type) => 0;

}
