using System.Security.Cryptography;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

// The rest of SceVshBridge (os0:kd/bootimage.skprx, FW 3.74). Every vsh* entry checks that the caller is a system
// program (ksceSblACMgrIsSystemProgram, else 0x80010058; the shell and the vs0 applications always are) and calls the
// kernel function of the same name; the _vsh* syscalls first copy their user structures in (an option block whose
// first word is a size, checked against the kernel buffer: too big = 0x8002710B; a path of 0x100/0x400 characters
// without its terminator: 0x8002710B) and the results out. The kernel side is answered for the emulated unit: a retail
// PCH-1000 with no QA flags, no test hardware, a memory card always inserted and no crash reports.

public sealed partial class VshBridge
{
    const int ERR_TOO_BIG = unchecked((int)0x8002710B);
    const int ERR_BAD_ADDR = SceError.ILLEGAL_ADDR;
    const int ERR_EINVAL = unchecked((int)0x80010016);

    /// <summary>The size word of an 8-byte option block (bridge copy of the first word); false when the pointer is bad.</summary>
    static bool OptSize(uint opt, out uint size)
    {
        size = 0;
        if (opt == 0) return false;
        size = GuestMemory.R32(opt);
        return true;
    }

    /// <summary>A user path the bridge copies with ksceKernelStrncpyFromUser(max): null = "", unterminated = null.</summary>
    static string? UserPath(uint p, int max)
    {
        if (p == 0) return "";
        var s = GuestMemory.ReadCString(p, max);
        return s.Length >= max ? null : s;
    }

    // ---- QA flags (SceQafMgr through the bridge): a retail unit has none
    [Hle] int vshSblQafMgrIsAllowRemotePlayDebug() => 0;
    [Hle] int vshSblQafMgrIsAllowMarlinTest() => 0;
    [Hle] int vshSblQafMgrIsAllowPSPEmuSelectNPEnv() => 0;
    [Hle] int vshSblQafMgrIsAllowShowTitleUpgradeInfo() => 0;
    [Hle] int vshSblQafMgrIsAllowLoadPSPEmuWhiteList() => 0;
    [Hle] int vshSblQafMgrIsAllowPSPEmuDeleteParameter() => 0;
    [Hle] int vshSblQafMgrIsAllowControlIduAutoUpdate() => 0;
    [Hle] int vshSblQafMgrIsAllowFakeACInstall() => 0;
    [Hle] int vshSblQafMgrIsAllowPSPEmuShowQAInfo() => 0;
    [Hle] int vshSblQafMgrIsAllowDtcpIpReset() => 0;
    [Hle] int vshSblQafMgrIsAllowPSPEmuScreenShot() => 0;

    /// <summary>ksceSblAimgrIsVITA: a PCH-1000 handheld, yes.</summary>
    [Hle] int vshSblAimgrIsVITA() => 1;

    /// <summary>(char console_id[0x10]): the unit's 16-byte console id (ksceSblAimgrGetConsoleId, then copied out).
    /// Layout of SceConsoleId: +0 unknown, +2 company 1 (SCE), +4 product 0x104 (CEX, the region of the pscode),
    /// +6 sub code 0x10 (PCH-1000), +8 factory/chassis, +9..+15 the serial part, here derived from the emulated
    /// OpenPSID so it stays the same between runs.</summary>
    [Hle]
    int _vshSblAimgrGetConsoleId(uint id)
    {
        if (id == 0) return ERR_BAD_ADDR;
        Span<byte> b = stackalloc byte[16];
        b.Clear();
        b[3] = 1; b[4] = 0x01; b[5] = 0x04; b[7] = 0x10; b[8] = 0x04;
        for (int i = 9; i < 16; i++) b[i] = (byte)(0x10 + i); // the OpenPSID bytes of sceKernelGetOpenPsId
        b.CopyTo(GuestMemory.Span(id, 16));
        return 0;
    }

    // ---- syscon: IDU (store demo) mode, logs, temperature history, the boot mode
    bool _iduMode;
    /// <summary>vshSysconIsIduMode answers the flag these set (the store demo mode of a kiosk unit).</summary>
    [Hle] int vshSysconIduModeSet() { _iduMode = true; return 0; }
    [Hle] int vshSysconIduModeClear() { _iduMode = false; return 0; }
    /// <summary>Not started by the update/downloader (syscon boot mode): 0.</summary>
    [Hle] int vshSysconIsDownLoaderMode() => 0;
    /// <summary>The temperature history syscon keeps; the bridge also asks whether the unit is a genuine PS TV
    /// (ksceSblAimgrIsGenuineDolce) first. The emulated unit keeps no history: nothing to clear.</summary>
    [Hle] int vshSysconClearTemperatureLog() => 0;

    /// <summary>(void *log 0x10): the temperature log (0x10 bytes copied out): empty.</summary>
    [Hle]
    int _vshSysconGetTemperatureLog(uint log)
    {
        if (log == 0) return ERR_BAD_ADDR;
        GuestMemory.Fill(log, 0, 0x10);
        return 0;
    }

    /// <summary>(int offset, void *buf, const opt *8: size &lt;= 0x20): a block of the syscon error log: empty (zeros).</summary>
    [Hle]
    int _vshSysconLogReadData(int offset, uint buf, uint opt)
    {
        if (!OptSize(opt, out uint size)) return ERR_BAD_ADDR;
        if (buf != 0 && size > 0x20) return ERR_TOO_BIG;
        if (buf != 0) GuestMemory.Fill(buf, 0, size);
        return 0;
    }

    // ---- SblSs: the NVS area of syscon and the per-console data encryption
    readonly Dictionary<int, byte[]> _nvs = new();

    /// <summary>(int offset, void *buf, int unk, const opt *8: size &lt;= 0x20): reads bytes of the syscon non-volatile
    /// area; what _vshSblSsSetNvsData wrote comes back, the rest reads as zeros (a factory-fresh area).</summary>
    [Hle]
    int _vshSblSsGetNvsData(int offset, uint buf, int unk, uint opt)
    {
        if (!OptSize(opt, out uint size)) return ERR_BAD_ADDR;
        if (buf == 0) return 0;
        if (size > 0x20) return ERR_TOO_BIG;
        var data = _nvs.GetValueOrDefault(offset);
        for (uint i = 0; i < size; i++) GuestMemory.Write(buf + i, data != null && i < data.Length ? data[i] : (byte)0);
        return 0;
    }

    /// <summary>(int offset, const void *buf, int unk, const opt *8: size &lt;= 0x20): writes bytes of the NVS area.</summary>
    [Hle]
    int _vshSblSsSetNvsData(int offset, uint buf, int unk, uint opt)
    {
        if (!OptSize(opt, out uint size)) return ERR_BAD_ADDR;
        if (buf == 0) return 0;
        if (size > 0x20) return ERR_TOO_BIG;
        _nvs[offset] = GuestMemory.ReadSpan(buf, (int)size).ToArray();
        return 0;
    }

    /// <summary>The console's portability key: secrets the emulated unit does not have, so a fixed key derived from
    /// the request is used both ways (what was encrypted here decrypts here).</summary>
    static byte[] PortabilityKey(int keyId) => SHA256.HashData(BitConverter.GetBytes(keyId).Concat("VitaSharp portability"u8.ToArray()).ToArray())[..16];

    static void PortabilityCrypt(int keyId, ReadOnlySpan<byte> iv16, Span<byte> data, bool encrypt)
    {
        using var aes = Aes.Create();
        aes.Key = PortabilityKey(keyId);
        int n = data.Length & ~15;
        if (n == 0) return;
        var src = data[..n].ToArray();
        var res = encrypt ? aes.EncryptCbc(src, iv16, PaddingMode.None) : aes.DecryptCbc(src, iv16, PaddingMode.None);
        res.CopyTo(data);
    }

    /// <summary>(int keyId, const void *iv 0x10, const SceSblSsData *in 0x24, SceSblSsData *out 0x24): decrypts the
    /// data block (+0x20 = size, at most 0x20 bytes) with the console's portability key (see PortabilityKey).</summary>
    [Hle]
    int _vshSblSsDecryptWithPortability(int keyId, uint iv, uint input, uint output)
    {
        if (input == 0 || output == 0) return ERR_BAD_ADDR;
        var block = GuestMemory.ReadSpan(input, 0x24).ToArray();
        Span<byte> ivb = stackalloc byte[16];
        if (iv != 0) GuestMemory.ReadSpan(iv, 16).CopyTo(ivb); else ivb.Clear();
        int size = Math.Min(BitConverter.ToInt32(block, 0x20), 0x20);
        if (size > 0) PortabilityCrypt(keyId, ivb, block.AsSpan(0, size), false);
        block.CopyTo(GuestMemory.Span(output, 0x24));
        return 0;
    }

    /// <summary>(const SceSblSsCreatePassPhraseParam *args 0x18, void *passPhrase, const opt *8: size &lt;= 0x200):
    /// the pass phrase PS Vita TV/Remote Play pairing shows, generated from the request by the security processor;
    /// here a printable string derived from the request (stable between runs), zero-filled to the requested size.</summary>
    [Hle]
    int _vshSblSsCreatePassPhrase(uint args, uint passPhrase, uint opt)
    {
        if (!OptSize(opt, out uint size)) return ERR_BAD_ADDR;
        if (args == 0) return ERR_BAD_ADDR;
        if (passPhrase != 0 && size > 0x200) return ERR_TOO_BIG;
        if (passPhrase == 0) return 0;
        var hash = SHA256.HashData(GuestMemory.ReadSpan(args, 0x18));
        GuestMemory.Fill(passPhrase, 0, size);
        const string digits = "0123456789";
        for (uint i = 0; i < Math.Min(size, 32u) - (size > 0 ? 1u : 0u); i++) GuestMemory.Write(passPhrase + i, (byte)digits[hash[i] % 10]);
        return 0;
    }

    /// <summary>(const opt *in 0x18, void *out 0x40, const opt *8): the first step of the PSN "mlnpsnl" (Marlin) game
    /// card authentication with the game card security chip. No game card is inserted: the request fails like the
    /// gcauthmgr does without a card (SCE_SBL_ERROR_GC_AUTH no device, 0x80870001), after the bridge's size checks.</summary>
    [Hle]
    int _vshSblGcAuthMgrMlnpsnlAuth1(uint a0, uint a1, uint a2, uint opt)
    {
        if (!OptSize(opt, out _)) return ERR_BAD_ADDR;
        return unchecked((int)0x80870001);
    }

    /// <summary>(const char *path, void *verified, SceSize capacity, const opt *8): verifies a signed PSN file
    /// (spsfo: the act.dat/...). The security module's keys do not exist here: the file is read as is (its bytes
    /// copied to the buffer, up to the capacity) when it exists, otherwise the IO error of the open.</summary>
    [Hle]
    int _vshSblAuthMgrVerifySpsfo(uint path, uint verified, uint capacity, uint opt)
    {
        if (!OptSize(opt, out _)) return ERR_BAD_ADDR;
        var p = UserPath(path, 0x100);
        if (p == null) return ERR_TOO_BIG;
        var host = Emu.Vfs.Resolve(p);
        if (host == null || !File.Exists(host)) return unchecked((int)0x80010002); // ENOENT
        var data = File.ReadAllBytes(host);
        int n = (int)Math.Min((uint)data.Length, capacity);
        if (verified != 0 && n > 0) data.AsSpan(0, n).CopyTo(GuestMemory.Span(verified, n));
        return 0;
    }

    // ---- IO: mounts and their events
    /// <summary>(SceVshMountId id, const char *path, int permission, void *buf 0x18): mount a device (game card,
    /// PSP emulator image, add-on content...). The emulator's devices are folders that are always there; the mount
    /// succeeds without changing anything (checks: a 0x400-character path = 0x8002710B).</summary>
    [Hle]
    int _vshIoMount(int id, uint path, int permission, uint buf)
    {
        if (buf == 0) return ERR_BAD_ADDR;
        if (UserPath(path, 0x400) == null) return ERR_TOO_BIG;
        return 0;
    }

    /// <summary>(SceVshMountId id, int force, int, int): unmounts it again; nothing to release.</summary>
    [Hle] int vshIoUmount(int id, int force, int a2, int a3) => 0;

    /// <summary>(const char *path, const SceIoStat *stat 0x58, unsigned bits, const opt *8): chstat for the shell
    /// (any file, through ksceIoChstatForVshbridge): the regular sceIoChstat on the path.</summary>
    [Hle]
    int _vshIoChstat(uint path, uint stat, int bits, uint opt)
    {
        if (!OptSize(opt, out _) || stat == 0) return ERR_BAD_ADDR;
        var p = UserPath(path, 0x400);
        if (p == null) return ERR_TOO_BIG;
        return Emu.Hle.Modules.OfType<IoFileMgr>().First().sceIoChstat(p, stat, bits);
    }

    /// <summary>(SceUID event): deletes an event made by vshIoCreateMountEvent (which keeps none: every id is fine).</summary>
    [Hle] int vshIoDeleteMountEvent(int ev) => 0;
    /// <summary>(SceUID event): deletes an event made by vshIoCreateErrorEvent (likewise).</summary>
    [Hle] int vshIoDeleteErrorEvent(int ev) => 0;

    /// <summary>(SceMsInfo *info 0x40): the memory card's information (ksceMsifGetMsInfo): a 16 GB Sony card with
    /// 512-byte sectors (nbytes and nbytes2 = the capacity, the file system at sector 0x8000), id = the OpenPSID bytes.</summary>
    [Hle]
    int vshMsifGetMsInfo(uint info)
    {
        if (info == 0) return ERR_BAD_ADDR;
        GuestMemory.Fill(info, 0, 0x40);
        const ulong bytes = 16UL * 1024 * 1024 * 1024;
        GuestMemory.Write(info + 8, bytes);
        GuestMemory.Write(info + 0x10, bytes);
        GuestMemory.W32(info + 0x18, 512);
        GuestMemory.W32(info + 0x20, 0x8000);
        for (uint i = 0; i < 16; i++) GuestMemory.Write(info + 0x30 + i, (byte)(0x20 + i));
        return 0;
    }

    /// <summary>(int leaf, void *buf 0x200): reads a whole ID storage leaf (ksceIdStorageReadLeaf). The factory leaves
    /// of the emulated unit hold no data: 0x200 zero bytes, like vshIdStorageLookup's reads.</summary>
    [Hle]
    int vshIdStorageReadLeaf(int leaf, uint buf)
    {
        if (buf == 0) return ERR_BAD_ADDR;
        GuestMemory.Fill(buf, 0, 0x200);
        return 0;
    }

    // ---- controller settings (SceCtrl through the bridge)
    int _portAssign0 = 0, _portAssign1 = 1, _vibration = 1, _turnOffInterval;

    /// <summary>(int port, int newAssign): the controller-to-port assignment of a PS TV with DualShocks; the handheld
    /// keeps the assignment it is told.</summary>
    [Hle] int vshCtrlChangePortAssign(int port, int assign) { if (port == 0) _portAssign0 = assign; else _portAssign1 = assign; return 0; }
    /// <summary>(int enable): the "vibration" system setting for external controllers, kept.</summary>
    [Hle] int vshCtrlSetVibrationSetting(int enable) { _vibration = enable; return 0; }
    /// <summary>(int seconds): the external controllers' auto turn-off time, kept.</summary>
    [Hle] int vshCtrlSetTurnOffInterval(int interval) { _turnOffInterval = interval; return 0; }
    /// <summary>The port the user is using: 0, the handheld's own buttons (a PS TV with a DualShock reports 1).</summary>
    [Hle] int vshCtrlGetActiveControllerPort() => 0;
    /// <summary>A Blu-ray remote (PS TV only): never connected.</summary>
    [Hle] int vshCtrlIsBdRemoconConnected() => 0;
    /// <summary>(SceUID cb): undoes vshCtrlRegisterNotifyCallBack (which keeps nothing).</summary>
    [Hle] int vshCtrlUnregisterNotifyCallBack(int cb) => 0;

    /// <summary>(SceTouchEmulationData *data 0x90): the touch emulation data PS TV's controller-to-touch mapping
    /// feeds; the handheld has real touch panels, the data is accepted and not used.</summary>
    [Hle] int _vshTouchSetTouchEmulationData(uint data) => data == 0 ? ERR_BAD_ADDR : 0;

    // ---- RTC: network / secure clocks and the secure alarm
    long _networkTickOffset, _debugNetworkTickOffset, _secureTickOffset;
    ulong _secureAlarm;

    static ulong UserTick(uint p) => GuestMemory.Read<ulong>(p);

    /// <summary>(const SceRtcTick *tick): sets the network clock (from the PSN time server); sceRtcGetCurrentNetworkTick
    /// follows the host clock, so only the difference is kept.</summary>
    [Hle] int _vshRtcSetCurrentNetworkTick(uint tick) { if (tick == 0) return ERR_BAD_ADDR; _networkTickOffset = (long)(UserTick(tick) - Rtc.NowTick); return 0; }
    [Hle] int _vshRtcSetCurrentDebugNetworkTick(uint tick) { if (tick == 0) return ERR_BAD_ADDR; _debugNetworkTickOffset = (long)(UserTick(tick) - Rtc.NowTick); return 0; }
    /// <summary>(const SceRtcTick *tick): sets the trusted clock (the shell does it after a PSN time sync).</summary>
    [Hle] int _vshRtcSetCurrentSecureTick(uint tick) { if (tick == 0) return ERR_BAD_ADDR; _secureTickOffset = (long)(UserTick(tick) - Rtc.NowTick); return 0; }
    /// <summary>(const SceRtcTick *tick): the secure alarm (the PS Plus auto-update/download timer); 0 clears it.</summary>
    [Hle] int _vshRtcSetSecureAlarmTick(uint tick) { if (tick == 0) return ERR_BAD_ADDR; _secureAlarm = UserTick(tick); return 0; }
    /// <summary>The secure alarm went off: its tick is set and the secure clock has passed it.</summary>
    [Hle] int vshRtcIsSecureAlarmed() => _secureAlarm != 0 && (ulong)((long)Rtc.NowTick + _secureTickOffset) >= _secureAlarm ? 1 : 0;
    /// <summary>(SceUID cb, int arg): a callback for the secure alarm. The emulator does not sleep, so the alarm is
    /// checked with vshRtcIsSecureAlarmed; the registration is accepted.</summary>
    [Hle] int vshRtcRegisterSecureAlarmCallback(int cb, int arg) => 0;
    /// <summary>(SceUID cb): undoes vshRtcRegisterCallback.</summary>
    [Hle] int vshRtcUnregisterCallback(int cb) => 0;

    // ---- power
    /// <summary>A cold reset of the unit (the "restart" after a system update or a format). VitaSharp never restarts
    /// the guest (like scePowerRequestColdReset): logged and ignored.</summary>
    [Hle] int vshPowerRequestColdReset() { Util.Log.Warn("vshPowerRequestColdReset (ignored)"); return 0; }
    /// <summary>(int time): how long the power button is held for standby, kept for the setting.</summary>
    [Hle] int vshPowerSetStandbyButtonPushTime(int time) => 0;
    /// <summary>(const SceBatteryFakeStatus *status, const opt *8: size &lt;= 0x10): a debug battery override; only a
    /// QA unit honours it, the bridge still checks the sizes.</summary>
    [Hle]
    int _vshPowerSetBatteryFakeStatus(uint status, uint opt)
    {
        if (!OptSize(opt, out uint size)) return ERR_BAD_ADDR;
        return size > 0x10 ? ERR_TOO_BIG : 0;
    }

    /// <summary>(int mode, const opt *8: size &lt;= 8): shut the system down (power off / restart). Like the power
    /// requests, VitaSharp does not power off from the guest (the user closes the emulator): logged and ignored.</summary>
    [Hle]
    int _vshKernelShutdownSystem(int mode, uint opt)
    {
        if (!OptSize(opt, out uint size)) return ERR_BAD_ADDR;
        if (size > 8) return ERR_TOO_BIG;
        Util.Log.Warn($"_vshKernelShutdownSystem({mode}) (ignored)");
        return 0;
    }

    // ---- display
    readonly uint[] _viewport = [0, 0, 960, 544, 0];

    /// <summary>(int head, const SceDisplayViewportConf *conf, const opt *8: size &lt;= 0x14): the HDMI viewport
    /// (overscan) of a PS TV; the handheld's LCD keeps its full 960x544, the values are kept for Get.</summary>
    [Hle]
    int _vshDisplaySetViewportConf(int head, uint conf, uint opt)
    {
        if (!OptSize(opt, out uint size)) return ERR_BAD_ADDR;
        if (size > 0x14) return ERR_TOO_BIG;
        if (conf != 0) for (int i = 0; i < size / 4; i++) _viewport[i] = GuestMemory.R32(conf + (uint)i * 4);
        return 0;
    }

    /// <summary>(int head, SceDisplayViewportConf *conf, const opt *8: size &lt;= 0x14): the viewport in use.</summary>
    [Hle]
    int _vshDisplayGetActualViewportConf(int head, uint conf, uint opt)
    {
        if (!OptSize(opt, out uint size)) return ERR_BAD_ADDR;
        if (conf == 0) return 0;
        if (size > 0x14) return ERR_TOO_BIG;
        for (int i = 0; i < size / 4; i++) GuestMemory.W32(conf + (uint)i * 4, _viewport[i]);
        return 0;
    }

    // ---- system event log and process information
    /// <summary>(const void *data, const opt *0x10: size &lt;= 0x7C0): one entry of the system event log
    /// (ksceEventLogPut): written to the emulator's log.</summary>
    [Hle]
    int _vshEventLogPut(uint data, uint opt)
    {
        if (!OptSize(opt, out uint size)) return ERR_BAD_ADDR;
        if (size > 0x7C0) return ERR_TOO_BIG;
        if (data != 0 && size > 0) Util.Log.Debug($"vsh event log: {Convert.ToHexString(GuestMemory.ReadSpan(data, (int)Math.Min(size, 64u)))}");
        return 0;
    }

    /// <summary>(SceUID pid, SceUInt32 *version): the SDK version the process was built with (from its
    /// SceProcessParam; the current process for pid 0 or its own pid, an unknown pid = 0x80020005 like processmgr).</summary>
    [Hle]
    int _vshKernelGetCompiledSdkVersionByPid(int pid, Ptr<uint> version)
    {
        var e = pid == 0 || pid == Emu.Pid ? Emu : Emu.Vita.ByPid(pid);
        if (e == null) return SceError.INVALID_ARGUMENT;
        uint pp = e.Linker.ProcessParam;
        uint v = pp != 0 && GuestMemory.R32(pp) >= 0x14 ? GuestMemory.R32(pp + 0x10) : 0x03740011;
        if (version.IsNull) return ERR_BAD_ADDR;
        version.Value = v;
        return 0;
    }

    // ---- crash reports (coredump CAF archives): the emulated unit keeps no core dumps
    /// <summary>Starts/ends the crash archive service; there is no dump to package.</summary>
    [Hle] int vshCoredumpCafInit() => 0;
    [Hle] int vshCoredumpCafFinal() => 0;
    /// <summary>Deletes the stored crash report (none exists): 0.</summary>
    [Hle] int vshCoredumpDeleteCrashReportCaf() => 0;

    sealed class CafContext
    {
        public IncrementalHash? Header, Segment;
        public Aes? Aes;
        public bool Encrypt;
        public byte[] Iv = new byte[16];
    }
    readonly Dictionary<int, CafContext> _caf = new();
    int _nextCaf = 0x40E10001;

    static byte[] CafKey(uint keyId, string use) => SHA256.HashData(BitConverter.GetBytes(keyId).Concat(System.Text.Encoding.ASCII.GetBytes(use)).ToArray());

    /// <summary>A context for packaging a crash archive (header HMAC, segments HMAC + AES): a new id.</summary>
    [Hle] int vshCoredumpCafContextCreate() { int id = _nextCaf++; _caf[id] = new CafContext(); return id; }

    [Hle]
    int vshCoredumpCafContextDestroy(int ctx)
    {
        if (!_caf.Remove(ctx, out var c)) return SceError.UNKNOWN_UID;
        c.Header?.Dispose(); c.Segment?.Dispose(); c.Aes?.Dispose();
        return 0;
    }

    /// <summary>(SceUID ctx, SceUInt32 hmacKeyId): starts the header HMAC (key derived from the id: the unit's keys
    /// are not available, see PortabilityKey).</summary>
    [Hle]
    int vshCoredumpCafHeaderInit(int ctx, uint keyId)
    {
        if (!_caf.TryGetValue(ctx, out var c)) return SceError.UNKNOWN_UID;
        c.Header?.Dispose();
        c.Header = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, CafKey(keyId, "caf header"));
        return 0;
    }

    /// <summary>(SceUID ctx, const void *data, const opt *8: size &lt;= 0x400): adds header bytes to the HMAC.</summary>
    [Hle]
    int _vshCoredumpCafHeaderTransform(int ctx, uint data, uint opt)
    {
        if (!OptSize(opt, out uint size)) return ERR_BAD_ADDR;
        if (size > 0x400) return ERR_TOO_BIG;
        if (!_caf.TryGetValue(ctx, out var c) || c.Header == null) return SceError.UNKNOWN_UID;
        if (data != 0) c.Header.AppendData(GuestMemory.ReadSpan(data, (int)size));
        return 0;
    }

    /// <summary>(SceUID ctx, void *digest, const opt *8: size &lt;= 0x20): the header HMAC.</summary>
    [Hle]
    int _vshCoredumpCafHeaderFinal(int ctx, uint digest, uint opt) => CafFinal(ctx, digest, opt, header: true);

    int CafFinal(int ctx, uint digest, uint opt, bool header)
    {
        if (!OptSize(opt, out uint size)) return ERR_BAD_ADDR;
        if (digest != 0 && size > 0x20) return ERR_TOO_BIG;
        if (!_caf.TryGetValue(ctx, out var c) || (header ? c.Header : c.Segment) is not { } h) return SceError.UNKNOWN_UID;
        var mac = h.GetHashAndReset();
        if (digest != 0) mac.AsSpan(0, (int)size).CopyTo(GuestMemory.Span(digest, (int)size));
        return 0;
    }

    /// <summary>(SceUID ctx, SceUInt32 hmacKeyId, int mode, SceUInt32 aesKeyId, const opt *0x18: iv, ivSize &lt;= 0x10):
    /// starts a segment: HMAC over the data and AES-128-CBC (mode 1 encrypt, 2 decrypt, 0 HMAC only).</summary>
    [Hle]
    int _vshCoredumpCafSegmentInit(int ctx, uint hmacKeyId, int mode, uint aesKeyId, uint opt)
    {
        if (opt == 0) return ERR_BAD_ADDR;
        uint iv = GuestMemory.R32(opt + 8), ivSize = GuestMemory.R32(opt + 0xC);
        if (ivSize > 0x10) return ERR_TOO_BIG;
        if (!_caf.TryGetValue(ctx, out var c)) return SceError.UNKNOWN_UID;
        c.Segment?.Dispose(); c.Aes?.Dispose(); c.Aes = null;
        c.Segment = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, CafKey(hmacKeyId, "caf segment"));
        Array.Clear(c.Iv);
        if (iv != 0 && ivSize > 0) GuestMemory.ReadSpan(iv, (int)ivSize).CopyTo(c.Iv);
        if (mode is 1 or 2)
        {
            c.Aes = Aes.Create();
            c.Aes.Key = CafKey(aesKeyId, "caf aes")[..16];
            c.Encrypt = mode == 1;
        }
        return 0;
    }

    /// <summary>(SceUID ctx, const void *src, void *dst, const opt *0x10: size &lt;= 0x400): one segment block through the
    /// cipher (the CBC chain continues from the last block) and the HMAC (over the plain text).</summary>
    [Hle]
    int _vshCoredumpCafSegmentTransform(int ctx, uint src, uint dst, uint opt)
    {
        if (!OptSize(opt, out uint size)) return ERR_BAD_ADDR;
        if (size > 0x400) return ERR_TOO_BIG;
        if (!_caf.TryGetValue(ctx, out var c) || c.Segment == null) return SceError.UNKNOWN_UID;
        if (src == 0) return ERR_BAD_ADDR;
        var data = GuestMemory.ReadSpan(src, (int)size).ToArray();
        if (c.Aes != null && (size & 15) != 0) return ERR_EINVAL;
        if (c.Aes != null && c.Encrypt) c.Segment.AppendData(data);
        byte[] outData = data;
        if (c.Aes != null && size > 0)
        {
            outData = c.Encrypt ? c.Aes.EncryptCbc(data, c.Iv, PaddingMode.None) : c.Aes.DecryptCbc(data, c.Iv, PaddingMode.None);
            (c.Encrypt ? outData : data).AsSpan((int)size - 16, 16).CopyTo(c.Iv);
        }
        if (c.Aes == null || !c.Encrypt) c.Segment.AppendData(c.Aes == null ? data : outData);
        if (dst != 0) outData.CopyTo(GuestMemory.Span(dst, (int)size));
        return 0;
    }

    [Hle] int _vshCoredumpCafSegmentFinal(int ctx, uint digest, uint opt) => CafFinal(ctx, digest, opt, header: false);

    /// <summary>(const SceCoredumpCreateDumpParam *param 0x28, ...): writes a core dump of a process on request (the
    /// crash reporter's "save dump"). Core files are a QA feature (vshSblQafMgrIsAllowKeepCoreFile = 0): the unit
    /// refuses like the coredump module without the flag (0x80020008, not permitted), after the bridge's checks
    /// (name size &lt;= 0x10, path size &lt;= 0x200).</summary>
    [Hle]
    int _vshCoredumpCreateDump(int pid, uint name, uint opt)
    {
        if (opt == 0) return ERR_BAD_ADDR;
        if (GuestMemory.R32(opt) > 0x10 || GuestMemory.R32(opt + 4) > 0x200) return ERR_TOO_BIG;
        return SceError.ILLEGAL_PERMISSION;
    }

    // ---- NpDrm: PSP/PS1 eboot signatures, legacy document keys, account and debug settings
    /// <summary>(int debug settings): the NpDrm debug switches (only a QA unit uses them): accepted.</summary>
    [Hle] int vshNpDrmUpdateDebugSettings(int a) => 0;
    /// <summary>(const SceUInt64 *accountId): tells NpDrm the PSN account the unit is activated for (after sign-in);
    /// the emulated licenses are not bound to an account (see vshNpDrmIsLooseAccountBind), nothing to update.</summary>
    [Hle] int vshNpDrmUpdateAccountId(uint accountId) => 0;

    /// <summary>The PSP emulator's eboot signature (__sce_ebootpbp), made the same way as the package installer makes it
    /// (<see cref="Package.PspEbootSignature"/>).</summary>
    static byte[] EbootSignature(string path, ReadOnlySpan<byte> sha256, int kind) => Package.PspEbootSignature.Create(path, sha256, kind);

    int EbootSigGen(uint path, uint sha, uint sig, uint swVersion, int shaSize, int kind)
    {
        var p = UserPath(path, 0x100);
        if (p == null) return ERR_TOO_BIG;
        if (sha == 0 || sig == 0) return ERR_BAD_ADDR;
        EbootSignature(p, GuestMemory.ReadSpan(sha, shaSize), kind).CopyTo(GuestMemory.Span(sig, 0x200));
        if (swVersion != 0) GuestMemory.W32(swVersion, 0x03740011);
        return 0;
    }

    /// <summary>(const char *path, const void *sha256 0x20, void *sig 0x200, int *swVersion): sign a PSP eboot.</summary>
    [Hle] int _vshNpDrmEbootSigGenPsp(uint path, uint sha, uint sig, uint sw) => EbootSigGen(path, sha, sig, sw, 0x20, 1);
    /// <summary>(... same, a PS1 eboot).</summary>
    [Hle] int _vshNpDrmEbootSigGenPs1(uint path, uint sha, uint sig, uint sw) => EbootSigGen(path, sha, sig, sw, 0x20, 2);
    /// <summary>(const char *path, const void *sceDiscinfo 0x100, void *sig 0x200, int *swVersion): a multi-disc PS1 eboot.</summary>
    [Hle] int _vshNpDrmEbootSigGenMultiDisc(uint path, uint discinfo, uint sig, uint sw) => EbootSigGen(path, discinfo, sig, sw, 0x100, 3);

    int EbootSigCheck(uint path, uint sig, uint header)
    {
        var p = UserPath(path, 0x100);
        if (p == null) return ERR_TOO_BIG;
        if (sig == 0) return ERR_BAD_ADDR;
        var s = GuestMemory.ReadSpan(sig, 0x200);
        if (!s[..8].SequenceEqual("NPUMDSIG"u8)) return unchecked((int)0x80870003); // signature does not verify
        var expect = EbootSignature(p, s.Slice(0x30, s[8] == 3 ? 0x100 : 0x20), s[8]);
        if (!s.Slice(0x10, 0x20).SequenceEqual(expect.AsSpan(0x10, 0x20))) return unchecked((int)0x80870003);
        if (header != 0) GuestMemory.W32(header, 0);
        return s[8];
    }

    /// <summary>(const char *path, const char *sig 0x200, char **header, opt): checks a signature EbootSigGen made;
    /// returns the eboot kind (1 PSP, 2 PS1, 3 multi-disc).</summary>
    [Hle] int _vshNpDrmEbootSigVerify(uint path, uint sig, uint header, uint opt) => EbootSigCheck(path, sig, header);
    /// <summary>(const char *path, const char *sig 0x100, char **header, opt): the PSP eboot check of the PSP emulator.</summary>
    [Hle] int _vshNpDrmPspEbootVerify(uint path, uint sig, uint header, uint opt) => EbootSigCheck(path, sig, header);

    /// <summary>(const SceUInt8 rif[0x200], const void *doc, SceSize docSize, const opt *0x10: documentKey,
    /// size &lt;= 0x200): the key of a PSP game manual (DOCUMENT.DAT) from its license. PSP licenses are not
    /// supported by the emulator's NpDrm: 0x80870005 (no license), like npdrm with a rif it cannot use.</summary>
    [Hle]
    int _vshNpDrmGetLegacyDocKey(uint rif, uint doc, uint size, uint opt)
    {
        if (opt == 0 || rif == 0) return ERR_BAD_ADDR;
        if (GuestMemory.R32(opt + 4) > 0x200) return ERR_TOO_BIG;
        return unchecked((int)0x80870005);
    }

    // ---- AppMgr through the bridge: cloud (PS Plus) save data, local backups, content install, PFS
    /// <summary>(int, const SceUInt64 *mcId): registers the memory card for PS Plus online storage (8 bytes in);
    /// _vshAppMgrCloudDataGetMcId then reports it.</summary>
    [Hle]
    int _vshAppMgrCloudDataSetMcId(int a, uint mcId)
    {
        _cloudMcId = mcId == 0 ? 0 : GuestMemory.Read<ulong>(mcId);
        return 0;
    }
    ulong _cloudMcId;
    /// <summary>Clears the online-storage registration.</summary>
    [Hle] int vshAppMgrCloudDataClearMcId() { _cloudMcId = 0; return 0; }

    /// <summary>(SceUInt8 setupKey[0x18]): the key the online-storage upload is set up with (generated by the
    /// security processor per upload): random bytes.</summary>
    [Hle]
    int _vshAppMgrCloudDataSetupKey(uint key)
    {
        if (key == 0) return ERR_BAD_ADDR;
        RandomNumberGenerator.Fill(GuestMemory.Span(key, 0x18));
        return 0;
    }

    /// <summary>(const SceUInt8 setupKey[0x18], SceSize dataSize, const SceUInt8 digest[0x20], SceUInt8 header[0x170]):
    /// the header of an uploaded save (run on a 0x2000-byte kernel stack): key, size and digest with an HMAC over them.</summary>
    [Hle]
    int _vshAppMgrCloudDataCreateHeader(uint setupKey, uint dataSize, uint digest, uint header)
    {
        if (setupKey == 0 || digest == 0 || header == 0) return ERR_BAD_ADDR;
        var h = new byte[0x170];
        "SCECDHDR"u8.CopyTo(h);
        BitConverter.GetBytes(dataSize).CopyTo(h, 8);
        GuestMemory.ReadSpan(setupKey, 0x18).CopyTo(h.AsSpan(0x10));
        GuestMemory.ReadSpan(digest, 0x20).CopyTo(h.AsSpan(0x28));
        HMACSHA256.HashData(CafKey(0, "cloud data"), h.AsSpan(0, 0x48)).CopyTo(h, 0x48);
        h.CopyTo(GuestMemory.Span(header, 0x170));
        return 0;
    }

    /// <summary>(const SceUInt8 header[0x170], ...): checks a header CreateHeader made (HMAC); a downloaded save
    /// from elsewhere does not verify (0x80802013, like appmgr with a bad header).</summary>
    [Hle]
    int _vshAppMgrCloudDataVerifyHeader(uint header)
    {
        if (header == 0) return ERR_BAD_ADDR;
        var h = GuestMemory.ReadSpan(header, 0x170);
        return h[..8].SequenceEqual("SCECDHDR"u8) && HMACSHA256.HashData(CafKey(0, "cloud data"), h[..0x48]).AsSpan().SequenceEqual(h.Slice(0x48, 0x20))
            ? 0 : unchecked((int)0x80802013);
    }

    /// <summary>(const opt *0x10, SceAppMgrMountResult *out 0x10): mounts the save data being uploaded (online
    /// storage source). Online storage needs PSN: 0x80802014 (not available offline).</summary>
    [Hle] int _vshAppMgrCloudDataSrcMount(uint param, uint result) => param == 0 ? ERR_BAD_ADDR : unchecked((int)0x80802014);
    /// <summary>(const opt *0x20, SceAppMgrMountResult *out 0x10): mounts the place a downloaded save goes: likewise.</summary>
    [Hle] int _vshAppMgrCloudDataDstCreateMount(uint param, uint result) => param == 0 ? ERR_BAD_ADDR : unchecked((int)0x80802014);
    /// <summary>(const char *path 0x124, const opt *8, SceAppMgrMountResult *out): a temporary save-data mount for the
    /// copy tools (Content Manager); the emulator's save folders are always there: the path is checked and nothing
    /// is mounted (0x80800007 when it does not exist, appmgr's "not found").</summary>
    [Hle]
    int _vshAppMgrFakeSaveDataCreateMount(uint path, uint opt, uint result)
    {
        var p = UserPath(path, 0x124);
        if (p == null) return ERR_TOO_BIG;
        var host = Emu.Vfs.Resolve(p);
        if (host == null || !Directory.Exists(host)) return unchecked((int)0x80800007);
        if (result != 0) GuestMemory.Fill(result, 0, 0x10);
        return 0;
    }

    /// <summary>(const char *path 0x124, const SceVshAppMgrCheckPfsMountedOpt *opt): whether a PFS (encrypted) mount
    /// is active at the path: the emulator decrypts installed content on install, nothing is PFS-mounted: 0.</summary>
    [Hle] int _vshAppMgrCheckPfsMounted(uint path, uint opt) => UserPath(path, 0x124) == null ? ERR_TOO_BIG : 0;

    /// <summary>(const SceAppMgrExclusiveProcessParam *param 0x10): whether an exclusive process (a game with the
    /// exclusive attribute, the PSP emulator) is running: only the shell and the caller run here: 0.</summary>
    [Hle] int _vshAppMgrIsExclusiveProcessRunning(uint param) => 0;

    /// <summary>(const SceAppMgrRifInfo *info 0x10): refreshes appmgr's cached license info after a license change;
    /// the emulator reads licenses when a title starts.</summary>
    [Hle] int _vshAppMgrUpdateRifInfo(uint info) => info == 0 ? ERR_BAD_ADDR : 0;
    /// <summary>Whether a content install period (a download's install window) is open: no installs run: 0.</summary>
    [Hle] int vshAppMgrCheckContentInstallPeriod() => 0;

    /// <summary>(const char titleId[0x10], const char addcontId[0x14], SceAppMgrAcInstResult *result): the keystone and
    /// localized title of an add-on being installed (run on a 0x2000 kernel stack). The emulator installs add-ons
    /// from packages on the host: none is being installed through appmgr: 0x80800007 (not found).</summary>
    [Hle]
    int _vshAppMgrAcInstGetAcdirParam(uint titleId, uint addcontId, uint result)
    {
        if (titleId == 0 || addcontId == 0 || result == 0) return ERR_BAD_ADDR;
        return unchecked((int)0x80800007);
    }

    // local backups (the PS Plus / Content Manager save backup to the PC)
    readonly List<byte[]> _backupTargets = [];

    /// <summary>(SceAppMgrLocalBackupTargetList *list 0xC0): the save data marked for backup (up to 0xC0 bytes of
    /// entries of 0x10): the ones RemoveItem has not dropped, none at the start.</summary>
    [Hle]
    int _vshAppMgrSaveDataLocalBackupTargetGetList(uint list)
    {
        if (list == 0) return ERR_BAD_ADDR;
        GuestMemory.Fill(list, 0, 0xC0);
        for (int i = 0; i < _backupTargets.Count && i < 12; i++) _backupTargets[i].CopyTo(GuestMemory.Span(list + (uint)i * 0x10, 0x10));
        return 0;
    }

    /// <summary>(const SceAppMgrLocalBackupTarget *item 0x10): drops an entry from the list.</summary>
    [Hle]
    int _vshAppMgrSaveDataLocalBackupTargetRemoveItem(uint item)
    {
        if (item == 0) return ERR_BAD_ADDR;
        var it = GuestMemory.ReadSpan(item, 0x10).ToArray();
        _backupTargets.RemoveAll(t => t.AsSpan(0, 0xC).SequenceEqual(it.AsSpan(0, 0xC)));
        return 0;
    }

    /// <summary>(const SceAppMgrBackupFinished *param 0x10): the backup of a title's saves finished; it leaves the list.</summary>
    [Hle] int _vshAppMgrSaveDataNotifyBackupFinished(uint param) => _vshAppMgrSaveDataLocalBackupTargetRemoveItem(param);

    /// <summary>(SceUInt64 *offlineId, int *idType): the offline id backups are bound to without PSN: derived from the
    /// console id (8 bytes) with type 1 (console-bound).</summary>
    [Hle]
    int _vshAppMgrLocalBackupGetOfflineId(uint offlineId, uint type)
    {
        if (offlineId != 0) GuestMemory.Write(offlineId, 0x1F1E1D1C1B1A1918UL);
        if (type != 0) GuestMemory.W32(type, 1);
        return 0;
    }

    /// <summary>(const void *header, ...): checks a backup header against the offline id. The backups this unit makes
    /// carry its own id (GetOfflineId is fixed), so a header given here is accepted.</summary>
    [Hle] int _vshAppMgrLocalBackupVerifyOfflineHeader(uint header) => header == 0 ? ERR_BAD_ADDR : 0;
}
