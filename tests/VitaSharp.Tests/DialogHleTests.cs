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

    sealed class FakeImeBackend : VitaSharp.Core.Host.IImeDialogBackend
    {
        public VitaSharp.Core.Host.ImeDialogRequest? Request;
        public Action<string?>? Done;
        public int Closed;
        public void Open(VitaSharp.Core.Host.ImeDialogRequest request, Action<string?> done) { Request = request; Done = done; }
        public void Close() => Closed++;
    }

    /// <summary>An IME dialog param: title, initial text, max length, type, option, text box mode; returns the buffer.</summary>
    (uint Param, uint Buffer) ImeDialogParam(string title, string initial, uint maxLen, uint type = 0, uint option = 0, uint textBoxMode = 0)
    {
        uint param = Alloc(0xA8), t = Alloc(0x110), init = Alloc(0x1010), buf = Alloc(0x1010);
        var tb = Encoding.Unicode.GetBytes(title + "\0");
        tb.CopyTo(GuestMemory.Span(t, tb.Length));
        var ib = Encoding.Unicode.GetBytes(initial + "\0");
        ib.CopyTo(GuestMemory.Span(init, ib.Length));
        GuestMemory.Fill(buf, 0xEE, 0x40); // garbage: the dialog must NUL-terminate
        GuestMemory.W32(param + 0x14, type);
        GuestMemory.W32(param + 0x18, option);
        GuestMemory.W32(param + 0x24, textBoxMode);
        GuestMemory.W32(param + 0x28, t);
        GuestMemory.W32(param + 0x2C, maxLen);
        GuestMemory.W32(param + 0x30, init);
        GuestMemory.W32(param + 0x34, buf);
        return (param, buf);
    }

    static string ReadUtf16Z(uint addr)
    {
        int n = 0;
        while (GuestMemory.Read<ushort>(addr + (uint)n * 2) != 0) n++;
        return Encoding.Unicode.GetString(GuestMemory.Span(addr, n * 2));
    }

    static T WithoutScriptedText<T>(Func<T> f)
    {
        string? saved = CommonDialog.ImeDefaultText;
        CommonDialog.ImeDefaultText = null;
        try { return f(); }
        finally { CommonDialog.ImeDefaultText = saved; }
    }

    [Fact]
    public void ImeDialogHostKeyboardConfirmWritesUtf16WithinMaxLength() => WithoutScriptedText(() =>
    {
        var host = new FakeImeBackend();
        var d = new CommonDialog { ImeBackendOverride = host };
        var (param, buf) = ImeDialogParam("Nome", "Yu", 6, type: 1, textBoxMode: 1);
        Assert.Equal(0, Call(d, "sceImeDialogInit", param));
        // waits for the user: RUNNING, no result yet, the buffer holds the initial text
        Assert.Equal(1, Call(d, "sceImeDialogGetStatus"));
        uint res = Alloc(0x24);
        Assert.Equal(NOT_FINISHED, Call(d, "sceImeDialogGetResult", res));
        Assert.Equal(NOT_FINISHED, Call(d, "sceImeDialogTerm"));
        Assert.Equal("Yu", ReadUtf16Z(buf));
        Assert.Equal(new VitaSharp.Core.Host.ImeDialogRequest("Nome", "Yu", 6, 1, false, 1), host.Request);
        Assert.True(host.Request!.Password);

        // the line break is dropped (single line), then the text is cut to 6 UTF-16 units without splitting the emoji's
        // surrogate pair (it would need units 6 and 7): "Yu\nNar😀x" -> "YuNar"
        host.Done!("Yu\nNar😀x");
        Assert.Equal(2, Call(d, "sceImeDialogGetStatus"));
        Assert.Equal("YuNar", ReadUtf16Z(buf));
        Assert.Equal(0, Call(d, "sceImeDialogGetResult", res));
        Assert.Equal(0u, GuestMemory.R32(res));      // OK
        Assert.Equal(2u, GuestMemory.R32(res + 4));  // ENTER
        host.Done!("late");                          // a second answer is ignored
        Assert.Equal("YuNar", ReadUtf16Z(buf));
        Assert.Equal(0, Call(d, "sceImeDialogTerm"));
        return 0;
    });

    [Fact]
    public void ImeDialogHostKeyboardMultilineAndNonAscii() => WithoutScriptedText(() =>
    {
        var host = new FakeImeBackend();
        var d = new CommonDialog { ImeBackendOverride = host };
        var (param, buf) = ImeDialogParam("Msg", "", 32, option: 1);
        Assert.Equal(0, Call(d, "sceImeDialogInit", param));
        Assert.True(host.Request!.Multiline);
        host.Done!("Olá\r\nmundo ção 鳴上");
        Assert.Equal("Olá\nmundo ção 鳴上", ReadUtf16Z(buf));
        var bytes = GuestMemory.Span(buf, 4).ToArray();
        Assert.Equal(new byte[] { (byte)'O', 0, (byte)'l', 0 }, bytes); // UTF-16LE
        Assert.Equal(0, Call(d, "sceImeDialogTerm"));
        return 0;
    });

    [Fact]
    public void ImeDialogHostKeyboardCancelKeepsInitialText() => WithoutScriptedText(() =>
    {
        var host = new FakeImeBackend();
        var d = new CommonDialog { ImeBackendOverride = host };
        var (param, buf) = ImeDialogParam("Nome", "Souji", 16);
        Assert.Equal(0, Call(d, "sceImeDialogInit", param));
        host.Done!(null);
        Assert.Equal(2, Call(d, "sceImeDialogGetStatus"));
        uint res = Alloc(0x24);
        Assert.Equal(0, Call(d, "sceImeDialogGetResult", res));
        Assert.Equal(1u, GuestMemory.R32(res));      // USER_CANCELED
        Assert.Equal(1u, GuestMemory.R32(res + 4));  // CLOSE
        Assert.Equal("Souji", ReadUtf16Z(buf));
        Assert.Equal(0, Call(d, "sceImeDialogTerm"));
        return 0;
    });

    [Fact]
    public void ImeDialogAbortClosesHostKeyboard() => WithoutScriptedText(() =>
    {
        var host = new FakeImeBackend();
        var d = new CommonDialog { ImeBackendOverride = host };
        var (param, buf) = ImeDialogParam("Nome", "A", 16);
        Assert.Equal(0, Call(d, "sceImeDialogInit", param));
        Assert.Equal(0, Call(d, "sceImeDialogAbort"));
        Assert.Equal(1, host.Closed);
        host.Done!("too late");                      // the answer after Abort is dropped
        uint res = Alloc(0x24);
        Assert.Equal(0, Call(d, "sceImeDialogGetResult", res));
        Assert.Equal(2u, GuestMemory.R32(res));      // ABORTED
        Assert.Equal(0u, GuestMemory.R32(res + 4));  // NONE
        Assert.Equal("A", ReadUtf16Z(buf));
        Assert.Equal(0, Call(d, "sceImeDialogAbort")); // already finished: no second Close
        Assert.Equal(1, host.Closed);
        Assert.Equal(0, Call(d, "sceImeDialogTerm"));
        return 0;
    });

    [Fact]
    public void ImeDialogScriptedTextWinsOverHostKeyboard()
    {
        string? saved = CommonDialog.ImeDefaultText;
        CommonDialog.ImeDefaultText = "Yu Narukami";
        try
        {
            var host = new FakeImeBackend();
            var d = new CommonDialog { ImeBackendOverride = host };
            var (param, buf) = ImeDialogParam("Nome", "", 8);
            Assert.Equal(0, Call(d, "sceImeDialogInit", param));
            Assert.Null(host.Request);
            Assert.Equal(2, Call(d, "sceImeDialogGetStatus"));
            Assert.Equal("Yu Narukami"[..8], ReadUtf16Z(buf));
            uint res = Alloc(0x24);
            Assert.Equal(0, Call(d, "sceImeDialogGetResult", res));
            Assert.Equal((0u, 2u), (GuestMemory.R32(res), GuestMemory.R32(res + 4)));
            Assert.Equal(0, Call(d, "sceImeDialogTerm"));
        }
        finally { CommonDialog.ImeDefaultText = saved; }
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
