using System.Reflection;
using System.Text;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;

namespace VitaSharp.Tests;

/// <summary>SceCommonDialog: the shared dialog slot, per-family results and struct offsets.</summary>
[Collection("guest")]
public sealed class DialogHleTests(GuestMemoryFixture fx)
{
    const int BUSY = unchecked((int)0x80020401), NULL = unchecked((int)0x80020402), NOT_RUNNING = unchecked((int)0x80020404),
        NOT_SUPPORTED = unchecked((int)0x80020405), NOT_AVAILABLE = unchecked((int)0x80020408), NOT_FINISHED = unchecked((int)0x80020410),
        NOT_IN_USE = unchecked((int)0x80020411);

    static int Call(CommonDialog d, string name, params object[] args) =>
        Convert.ToInt32(typeof(CommonDialog).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.Invoke(d, args)!);

    uint Alloc(uint size)
    {
        uint p = fx.Memory.Alloc(size, "dialog test");
        GuestMemory.Fill(p, 0, size);
        return p;
    }

    uint MsgParam(int mode, int buttonType, string msg = "hello")
    {
        uint param = Alloc(0x88), up = Alloc(0x10), text = Alloc(64);
        GuestMemory.WriteCString(text, msg);
        GuestMemory.W32(up, (uint)buttonType);
        GuestMemory.W32(up + 4, text);
        GuestMemory.W32(param + 0x50, (uint)mode);
        GuestMemory.W32(param + 0x54, up);
        return param;
    }

    [Fact]
    public void MsgDialogUserMessageFinishesWithOk()
    {
        var d = new CommonDialog();
        Assert.Equal(0, Call(d, "sceMsgDialogGetStatus"));
        Assert.Equal(0, Call(d, "sceMsgDialogInit", MsgParam(1, 1)));                  // USER_MSG, YESNO
        Assert.Equal(2, Call(d, "sceMsgDialogGetStatus"));
        uint res = Alloc(0x2C);
        Assert.Equal(NULL, Call(d, "sceMsgDialogGetResult", 0u));
        Assert.Equal(0, Call(d, "sceMsgDialogGetResult", res));
        Assert.Equal(1u, GuestMemory.R32(res));      // mode
        Assert.Equal(0u, GuestMemory.R32(res + 4));  // result OK
        Assert.Equal(1u, GuestMemory.R32(res + 8));  // YES
        Assert.Equal(0, Call(d, "sceMsgDialogTerm"));
        Assert.Equal(0, Call(d, "sceMsgDialogGetStatus"));
    }

    uint ImeParam()
    {
        uint param = Alloc(0xA8);
        GuestMemory.W32(param + 0x2C, 16);
        GuestMemory.W32(param + 0x34, Alloc(64));
        return param;
    }

    [Fact]
    public void MsgDialogWithoutButtonsRunsUntilClose()
    {
        var d = new CommonDialog();
        Assert.Equal(0, Call(d, "sceMsgDialogInit", MsgParam(1, 2)));                  // buttonType NONE
        Assert.Equal(1, Call(d, "sceMsgDialogGetStatus"));
        Assert.Equal(NOT_FINISHED, Call(d, "sceMsgDialogTerm"));
        Assert.Equal(BUSY, Call(d, "sceImeDialogInit", ImeParam()));             // one dialog per process
        Assert.Equal(0, Call(d, "sceMsgDialogClose"));
        Assert.Equal(2, Call(d, "sceMsgDialogGetStatus"));
        Assert.Equal(NOT_RUNNING, Call(d, "sceMsgDialogClose"));
        Assert.Equal(0, Call(d, "sceMsgDialogTerm"));
    }

    [Fact]
    public void MsgDialogProgressBar()
    {
        var d = new CommonDialog();
        Assert.Equal(NOT_RUNNING, Call(d, "sceMsgDialogProgressBarInc", 0, 10u));
        Assert.Equal(0, Call(d, "sceMsgDialogInit", MsgParam(1, 0)));
        Assert.Equal(NOT_SUPPORTED, Call(d, "sceMsgDialogProgressBarInc", 0, 10u));
        Assert.Equal(0, Call(d, "sceMsgDialogTerm"));

        uint param = Alloc(0x88);
        GuestMemory.W32(param + 0x50, 4);
        Assert.Equal(0, Call(d, "sceMsgDialogInit", param));
        Assert.Equal(1, Call(d, "sceMsgDialogGetStatus"));
        Assert.Equal(0, Call(d, "sceMsgDialogProgressBarInc", 0, 60u));
        Assert.Equal(0, Call(d, "sceMsgDialogProgressBarInc", 0, 60u));
        Assert.Equal(100u, d.MsgBarPercent);
        Assert.Equal(0, Call(d, "sceMsgDialogProgressBarSetValue", 0, 30u));
        Assert.Equal(30u, d.MsgBarPercent);
        Assert.Equal(0, Call(d, "sceMsgDialogAbort"));
        uint res = Alloc(0x2C);
        Assert.Equal(0, Call(d, "sceMsgDialogGetResult", res));
        Assert.Equal(2u, GuestMemory.R32(res + 4)); // ABORTED
        Assert.Equal(0, Call(d, "sceMsgDialogTerm"));
    }

    [Fact]
    public void NetCheckDialogIsCanceledOffline()
    {
        var d = new CommonDialog();
        Assert.Equal(0, Call(d, "sceNetCheckDialogGetStatus"));
        uint param = Alloc(0xE0);
        Assert.Equal(unchecked((int)0x80100C02), Call(d, "sceNetCheckDialogInit", param)); // mode 0 is invalid
        GuestMemory.W32(param + 0x50, 4);                                                   // PS3_CONNECT
        Assert.Equal(0, Call(d, "sceNetCheckDialogInit", param));
        Assert.Equal(2, Call(d, "sceNetCheckDialogGetStatus"));
        uint res = Alloc(0x100);
        GuestMemory.Fill(res, 0xCC, 0x100);
        Assert.Equal(0, Call(d, "sceNetCheckDialogGetResult", res));
        Assert.Equal(1u, GuestMemory.R32(res));            // USER_CANCELED
        Assert.Equal(0u, GuestMemory.R32(res + 4));        // psnModeSucceeded
        Assert.Equal(0u, GuestMemory.R32(res + 0x80));
        Assert.Equal(0xCCCCCCCCu, GuestMemory.R32(res + 0x84)); // 0x84-byte struct
        Assert.Equal(NOT_AVAILABLE, Call(d, "sceNetCheckDialogGetPS3ConnectInfo", Alloc(0x90)));
        Assert.Equal(0, Call(d, "sceNetCheckDialogTerm"));
        Assert.Equal(NOT_IN_USE, Call(d, "sceNetCheckDialogTerm"));
        Assert.Equal(NOT_IN_USE, Call(d, "sceNetCheckDialogGetPS3ConnectInfo", Alloc(0x90)));
    }

    [Fact]
    public void ImeDialogWritesTextAndEnter()
    {
        var d = new CommonDialog();
        uint param = Alloc(0xA8), title = Alloc(64), init = Alloc(64), buf = Alloc(64);
        var t = Encoding.Unicode.GetBytes("Name\0");
        t.CopyTo(GuestMemory.Span(title, t.Length));
        var i = Encoding.Unicode.GetBytes("Abcdefgh\0");
        i.CopyTo(GuestMemory.Span(init, i.Length));
        GuestMemory.W32(param + 0x28, title);
        GuestMemory.W32(param + 0x2C, 4);
        GuestMemory.W32(param + 0x30, init);
        Assert.Equal(unchecked((int)0x80102001), Call(d, "sceImeDialogInit", param)); // no output buffer
        GuestMemory.W32(param + 0x34, buf);
        Assert.Equal(0, Call(d, "sceImeDialogInit", param));
        Assert.Equal(2, Call(d, "sceImeDialogGetStatus"));
        Assert.Equal("Abcd", Encoding.Unicode.GetString(GuestMemory.Span(buf, 8)));   // truncated to maxTextLength
        Assert.Equal(0, GuestMemory.Read<ushort>(buf + 8));
        uint res = Alloc(0x24);
        Assert.Equal(0, Call(d, "sceImeDialogGetResult", res));
        Assert.Equal(0u, GuestMemory.R32(res));
        Assert.Equal(2u, GuestMemory.R32(res + 4));  // ENTER
        Assert.Equal(0, Call(d, "sceImeDialogTerm"));
        Assert.Equal(NOT_FINISHED, Call(d, "sceImeDialogGetResult", res));
    }

    [Fact]
    public void UnnamedNpDialogAndOtherFamilies()
    {
        var d = new CommonDialog();
        Assert.Equal(0, Call(d, "KzDialogGetStatus"));                  // not started: NONE, not FINISHED
        Assert.Equal(0, Call(d, "KzDialogInit", Alloc(0x100)));
        Assert.Equal(2, Call(d, "KzDialogGetStatus"));
        uint res = Alloc(0x2C);
        GuestMemory.Fill(res, 0xCC, 0x2C);
        Assert.Equal(0, Call(d, "KzDialogGetResult", res));
        Assert.Equal(1u, GuestMemory.R32(res));
        Assert.Equal(0u, GuestMemory.R32(res + 4));
        Assert.Equal(0xCCCCCCCCu, GuestMemory.R32(res + 8));
        Assert.Equal(0, Call(d, "KzDialogTerm"));

        Assert.Equal(0, Call(d, "sceNpTrophySetupDialogInit", Alloc(0xD8)));
        Assert.Equal(0, Call(d, "sceNpTrophySetupDialogGetResult", res));
        Assert.Equal(0u, GuestMemory.R32(res));                          // trophies work offline: OK
        Assert.Equal(0, Call(d, "sceNpTrophySetupDialogTerm"));

        Assert.Equal(0, Call(d, "sceNpFriendListDialogInit", Alloc(0x100)));
        Assert.Equal(NOT_IN_USE, Call(d, "sceNpMessageDialogTerm"));
        Assert.Equal(NOT_FINISHED, Call(d, "sceNpMessageDialogGetResult", res));
        Assert.Equal(0, Call(d, "sceNpFriendListDialogTerm"));
        Assert.Equal(0, Call(d, "scePhotoImportDialogInit", Alloc(0x80)));
        Assert.Equal(0, Call(d, "scePhotoImportDialogGetResult", res));
        Assert.Equal(1u, GuestMemory.R32(res));
        Assert.Equal(0, Call(d, "scePhotoImportDialogTerm"));
        Assert.False((bool)typeof(CommonDialog).GetMethod("sceCommonDialogIsRunning", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(d, [])!);
    }

    /// <summary>SceSaveDataDialogParam with a system message (mode 4) or a user message (mode 3) sub-parameter.</summary>
    uint SaveParam(int mode, uint subWord0)
    {
        uint param = Alloc(0x88), sub = Alloc(0x40);
        GuestMemory.W32(sub, subWord0); // sysMsgType / buttonType
        GuestMemory.W32(param + 0x50, (uint)mode);
        GuestMemory.W32(param + 0x54, 1); // SCE_SAVEDATA_DIALOG_TYPE_SAVE
        GuestMemory.W32(param + (mode == 3 ? 0x60u : 0x64u), sub);
        return param;
    }

    [Fact]
    public void SaveDataDialogProgressMessageRunsUntilSubClose()
    {
        // Sengoku Musou 2 Empires: Continue to the "Saving..." system message (SYSMSG_TYPE_PROGRESS), save, wait for the
        // sub-dialog to be RUNNING, then SubClose. A sub-dialog without buttons cannot finish on its own.
        var d = new CommonDialog();
        Assert.Equal(0, Call(d, "sceSaveDataDialogInit", SaveParam(4, 2)));         // SYSTEM_MSG CONFIRM (yes/no)
        Assert.Equal(2, Call(d, "sceSaveDataDialogGetSubStatus"));                 // answered at once
        Assert.Equal(0, Call(d, "sceSaveDataDialogContinue", SaveParam(4, 5)));    // SYSTEM_MSG PROGRESS
        Assert.Equal(1, Call(d, "sceSaveDataDialogGetSubStatus"));
        Assert.Equal(1, Call(d, "sceSaveDataDialogGetStatus"));
        Assert.Equal(0, Call(d, "sceSaveDataDialogSubClose"));
        Assert.Equal(2, Call(d, "sceSaveDataDialogGetSubStatus"));
        Assert.Equal(0, Call(d, "sceSaveDataDialogContinue", SaveParam(3, 2)));    // USER_MSG, BUTTON_TYPE_NONE
        Assert.Equal(1, Call(d, "sceSaveDataDialogGetSubStatus"));
        Assert.Equal(0, Call(d, "sceSaveDataDialogContinue", SaveParam(3, 0)));    // USER_MSG, OK button
        Assert.Equal(2, Call(d, "sceSaveDataDialogGetSubStatus"));
        Assert.Equal(0, Call(d, "sceSaveDataDialogFinish", 0u));
        Assert.Equal(2, Call(d, "sceSaveDataDialogGetStatus"));
        Assert.Equal(0, Call(d, "sceSaveDataDialogTerm"));
    }
}
