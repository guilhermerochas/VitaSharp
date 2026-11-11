using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// The shell side of SceAppMgr (FW 3.74 kernel appmgr layouts): application status records (0x80 bytes), the kernel's
/// pseudo application ids (-2 the caller, -4 the one in front), the appmgr state block, the BGM port, launch parameters
/// and the shell event queue, on one emulated process. Error codes: 0x80802012 not found, 0x80802016 NULL,
/// 0x80802013 no event, 0x8080201C name refused, 0x8080201F not the shell.
/// </summary>
[Collection("guest")]
public sealed class AppMgrShellHleTests(GuestMemoryFixture fx) : IDisposable
{
    const int ERR_NOT_FOUND = unchecked((int)0x80802012), ERR_NULL = unchecked((int)0x80802016),
        ERR_NO_EVENT = unchecked((int)0x80802013), ERR_NAME = unchecked((int)0x8080201C), ERR_NOT_SHELL = unchecked((int)0x8080201F);

    readonly HleHarness _h = new(fx, "appmgrshell");
    AppMgrShell S => _h.Module<AppMgrShell>();
    VitaSharp.Core.Emulator Emu => _h.Emu;

    public void Dispose() => _h.Dispose();

    [Fact]
    public void StatusOfTheCallingApplication()
    {
        uint st = _h.Alloc(0x80);
        Assert.Equal(0, Call(S, "sceAppMgrGetStatusByName", 0u, st));
        Assert.Equal((uint)Emu.AppId, GuestMemory.R32(st + 0x30));
        Assert.Equal((uint)Emu.Pid, GuestMemory.R32(st + 0x34));
        Assert.Equal((uint)Emu.AppId, GuestMemory.R32(st + 0x44));
        Assert.Equal(Emu.TitleId, GuestMemory.ReadCString(st + 0x50, 0x20));
        Assert.Equal(0, GuestMemory.Read<byte>(st + 0x71)); // not the shell
        Assert.Equal(ERR_NULL, Call(S, "sceAppMgrGetStatusByName", 0u, 0u));
        // only a system program may name another application
        Assert.Equal(ERR_NAME, Call(S, "sceAppMgrGetStatusByName", _h.Str("NPXS19999"), st));

        Assert.Equal(0, Call(S, "sceAppMgrGetStatusByAppId", -2, st)); // -2: the caller
        Assert.Equal((uint)Emu.Pid, GuestMemory.R32(st + 0x34));
        Assert.Equal(0, Call(S, "_sceAppMgrGetStatusByAppId", Emu.AppId, st));
        Assert.Equal(ERR_NOT_FOUND, Call(S, "sceAppMgrGetStatusByAppId", 0x7FFF0001, st));
        Assert.Equal(ERR_NULL, Call(S, "sceAppMgrGetStatusByAppId", Emu.AppId, 0u));
        Assert.Equal(0, Call(S, "sceAppMgrGetStatusById", Emu.Pid, st));
        Assert.Equal(ERR_NOT_FOUND, Call(S, "sceAppMgrGetStatusById", 0x7FFF0001, st));
        Assert.Equal(Emu.Pid, Call(S, "sceAppMgrGetProcessIdByAppIdForShell", Emu.AppId));
        Assert.Equal(ERR_NOT_FOUND, Call(S, "sceAppMgrGetProcessIdByAppIdForShell", 0x7FFF0001));
    }

    [Fact]
    public void ShellOnlyCallsAndTheShellRecord()
    {
        Assert.Equal(ERR_NOT_SHELL, Call(S, "sceAppMgrGetAppIdByAppId", -2));
        Assert.Equal(0, Call(S, "sceAppMgrReceiveShellEventNum"));
        Assert.Equal(ERR_NO_EVENT, Call(S, "sceAppMgrReceiveShellEvent", _h.Alloc(0x40)));
        Assert.Equal(0, Call(S, "sceAppMgrDeclareShellProcess2", _h.Str("NPXS19999")));
        Assert.True(Emu.IsShell);
        Assert.Equal(Emu.AppId, Call(S, "sceAppMgrGetAppIdByAppId", -2));
        Assert.Equal(Emu.AppId, Call(S, "sceAppMgrGetAppIdByAppId", Emu.AppId));
        Assert.Equal(-1, Call(S, "sceAppMgrGetAppIdByAppId", 0x7FFF0001));
        Assert.Equal(-1, Call(S, "sceAppMgrGetAppIdByAppId", -3)); // no such pseudo id
        // the shell's own status: type 2 and flag bit 0 (libipmi lets it create IPC servers)
        uint st = _h.Alloc(0x80);
        Call(S, "sceAppMgrGetStatusByName", 0u, st);
        Assert.Equal(2, GuestMemory.Read<byte>(st + 0x71));
        Assert.Equal(1u, GuestMemory.R32(st + 0x74));
        Assert.Equal(ERR_NULL, Call(S, "sceAppMgrReceiveShellEvent", 0u));
        Assert.True(Call(S, "sceAppMgrReceiveShellEventNum") >= 0);
        // notification requests: none queued
        Assert.NotEqual(0, Call(S, "sceAppMgrReceiveNotificationRequestForShell", _h.Alloc(0x40)));
    }

    [Fact]
    public void AppMgrStateBlockAndBgmPort()
    {
        uint state = _h.Alloc(0x80);
        Assert.Equal(ERR_NULL, Call(S, "sceAppMgrGetAppMgrState", 0u));
        Assert.Equal(0, Call(S, "sceAppMgrEnablePrioritizingSystemChat", 1));
        Assert.Equal(0, Call(S, "sceAppMgrSetSystemImposeState2", 0x55));
        Assert.Equal(0, Call(S, "sceAppMgrGetAppMgrState", state));
        Assert.Equal(1u, GuestMemory.R32(state));
        Assert.Equal(0x55u, GuestMemory.R32(state + 0x14));

        uint bgm = _h.Alloc(0x20);
        Assert.Equal(ERR_NULL, Call(S, "sceAppMgrGetCurrentBgmState2", 0u));
        Assert.Equal(0, Call(S, "sceAppMgrGetCurrentBgmState2", bgm));
        // Layout as in the kernel appmgr: +0 pid, +4 priority, +0xC app id. With no positive priority the BGM goes to the
        // process in front with the default priority 0xFFFF.
        Assert.Equal((uint)Emu.Pid, GuestMemory.R32(bgm));
        Assert.Equal(0xFFFFu, GuestMemory.R32(bgm + 4));
        Assert.Equal((uint)Emu.AppId, GuestMemory.R32(bgm + 0xC));
        Assert.Equal(0, Call(S, "sceAppMgrAcquireBgmPortWithPriority", 0x90));
        Call(S, "sceAppMgrGetCurrentBgmState2", bgm);
        Assert.Equal((uint)Emu.Pid, GuestMemory.R32(bgm));
        Assert.Equal(0x90u, GuestMemory.R32(bgm + 4));
        Assert.Equal((uint)Emu.AppId, GuestMemory.R32(bgm + 0xC));
        Assert.Equal(0, Call(S, "sceAppMgrRestoreBgmSettingForShell"));
        Assert.Equal(0, Call(S, "sceAppMgrSetPowerSaveMode", 1, 0));
        Assert.Equal(0, Call(S, "sceAppMgrSetDisplayMergeConf", 0, 0));
        Assert.Equal(0, Call(S, "sceAppMgrRestoreDisplaySettingForShell"));
        Assert.Equal(0, Call(S, "sceAppMgrSetShellScreenOrientation", 0, 0));
    }

    [Fact]
    public void LaunchParametersAndLaunchErrors()
    {
        uint buf = _h.Alloc(0x400);
        Assert.Equal(0, Call(S, "sceAppMgrOverwriteLaunchParamForShell", Emu.AppId, _h.Str("-param value")));
        Assert.Equal(0, Call(S, "sceAppMgrPeekLaunchParamForShell", Emu.AppId, buf));
        Assert.Equal("-param value", GuestMemory.ReadCString(buf, 0x400));
        Assert.Equal(ERR_NULL, Call(S, "sceAppMgrPeekLaunchParamForShell", Emu.AppId, 0u));
        Assert.Equal(ERR_NOT_FOUND, Call(S, "sceAppMgrPeekLaunchParamForShell", 0x7FFF0001, buf));
        Assert.Equal(ERR_NOT_FOUND, Call(S, "sceAppMgrOverwriteLaunchParamForShell", 0x7FFF0001, 0u));

        Assert.Equal(ERR_NULL, Call(S, "sceAppMgrLaunchAppByName", 0, 0u, 0u));
        Assert.Equal(unchecked((int)0x80800006), Call(S, "sceAppMgrLaunchAppByName", 0x20000, _h.Str("NPXS99999"), 0u)); // not installed
        Assert.Equal(ERR_NOT_SHELL, Call(S, "sceAppMgrLaunchAppByName2", _h.Str("NPXS10083"), 0u, 0u));      // not a system program
        Assert.Equal(ERR_NOT_FOUND, Call(S, "sceAppMgrLaunchAppByName2ndStage", 0x7FFF0001, 0u, 0, 0, 0u));
        Assert.Equal(ERR_NOT_FOUND, Call(S, "sceAppMgrLaunchAppCancel", 0x7FFF0001));
        Assert.Equal(ERR_NAME, Call(S, "sceAppMgrDestroyAppByName", _h.Str("NPXS99999")));
        Assert.Equal(ERR_NOT_FOUND, Call(S, "sceAppMgrDestroyAppByAppId", 0x7FFF0001));
        Assert.Equal(ERR_NOT_FOUND, Call(S, "sceAppMgrActivateApp", 0x7FFF0001));
        Assert.Equal(ERR_NOT_FOUND, Call(S, "sceAppMgrDeactivateApp", 0x7FFF0001));
        Assert.Equal(0, Call(S, "sceAppMgrDeactivateApp", Emu.AppId)); // the caller itself: nothing to suspend
        Assert.Equal(ERR_NULL, Call(S, "sceAppMgrGetRawPathOfApp0ByAppIdForShell", Emu.AppId, 0u));
        Assert.Equal(ERR_NOT_FOUND, Call(S, "sceAppMgrGetRawPathOfApp0ByAppIdForShell", 0x7FFF0001, buf));
    }

    [Fact]
    public void FrameCaptureChecksTheTarget()
    {
        uint fb = _h.Alloc(0x1C);
        Assert.Equal(ERR_NULL, Call(S, "sceAppMgrCaptureFrameBufIFTUByAppId", -2, 0u));
        Assert.Equal(ERR_NOT_FOUND, Call(S, "sceAppMgrCaptureFrameBufDMACByAppId", 0x7FFF0001, fb));
        GuestMemory.W32(fb, 0x10); // not a SceDisplayFrameBuf size
        Assert.Equal(ERR_NULL, Call(S, "sceAppMgrCaptureFrameBufIFTUByAppId", -2, fb));
        GuestMemory.W32(fb, 0x18); // no base
        Assert.Equal(ERR_NULL, Call(S, "_sceAppMgrCaptureFrameBufIFTUByAppId", -2, fb));
        GuestMemory.W32(fb + 4, _h.Alloc(64 * 64 * 4));
        GuestMemory.W32(fb + 8, 64);
        GuestMemory.W32(fb + 16, 64);
        GuestMemory.W32(fb + 20, 64);
        // nothing displayed yet by this process (and no shell composing for it)
        Assert.Equal(ERR_NOT_FOUND, Call(S, "_sceAppMgrCaptureFrameBufDMACByAppId", -2, fb));
    }
}
