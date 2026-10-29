using System.Text;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>Families of SceCommonDialog dialogs; only one of them can be active per process.</summary>
public enum DialogType
{
    None, Msg, SaveData, Ime, NetCheck, NpMessage, NpTrophySetup, NpFriendList, NpFriendList2, KzUnknown,
    PhotoImport, PhotoReview, CameraImport, VideoImport, CompanionUtil, CrossController, RemoteOsk, StoreCheckout,
    NpProfile, NpSnsFacebook, Tw, TwLogin, PspSaveData,
}

/// <summary>SceCommonDialogErrorCode (psp2/common_dialog.h).</summary>
public static class CommonDialogError
{
    public const int BUSY = unchecked((int)0x80020401);
    public const int NULL = unchecked((int)0x80020402);
    public const int INVALID_ARGUMENT = unchecked((int)0x80020403);
    public const int NOT_RUNNING = unchecked((int)0x80020404);
    public const int NOT_SUPPORTED = unchecked((int)0x80020405);
    public const int NOT_AVAILABLE = unchecked((int)0x80020408);
    public const int NOT_FINISHED = unchecked((int)0x80020410);
    public const int NOT_IN_USE = unchecked((int)0x80020411);
    public const int INVALID_LANGUAGE = unchecked((int)0x80020431);
    public const int INVALID_ENTER_BUTTON_ASSIGN = unchecked((int)0x80020432);
}

/// <summary>
/// The process-wide common dialog slot (the hardware model, like Vita3K's DialogState): Init of any family while
/// another dialog has not been terminated returns BUSY; GetStatus of a family that is not the active one is NONE.
/// </summary>
public sealed class CommonDialogState
{
    public const int StatusNone = 0, StatusRunning = 1, StatusFinished = 2;
    public const int ResultOk = 0, ResultUserCanceled = 1, ResultAborted = 2;

    public readonly object Lock = new();
    public DialogType Type { get; private set; }
    public int Status { get; private set; }
    public int Result { get; private set; }

    /// <summary>Takes the slot (RUNNING, result OK) or returns BUSY.</summary>
    public int Begin(DialogType t)
    {
        lock (Lock)
        {
            if (Type != DialogType.None) return CommonDialogError.BUSY;
            Type = t; Status = StatusRunning; Result = ResultOk;
            return 0;
        }
    }

    public void Finish(int result) { lock (Lock) { Status = StatusFinished; Result = result; } }

    public int StatusOf(DialogType t) { lock (Lock) return Type == t ? Status : StatusNone; }

    public bool IsRunning { get { lock (Lock) return Type != DialogType.None && Status == StatusRunning; } }

    /// <summary>GetResult precondition: the family is active and FINISHED.</summary>
    public int ResultCheck(DialogType t) { lock (Lock) return Type == t && Status == StatusFinished ? 0 : CommonDialogError.NOT_FINISHED; }

    /// <summary>Term: NOT_IN_USE for another family, NOT_FINISHED while running; frees the slot.</summary>
    public int Term(DialogType t)
    {
        lock (Lock)
        {
            if (Type != t) return CommonDialogError.NOT_IN_USE;
            if (Status != StatusFinished) return CommonDialogError.NOT_FINISHED;
            Type = DialogType.None; Status = StatusNone; Result = ResultOk;
            return 0;
        }
    }

    /// <summary>Abort: FINISHED with ABORTED (an already finished dialog keeps its result).</summary>
    public int Abort(DialogType t)
    {
        lock (Lock)
        {
            if (Type != t) return CommonDialogError.NOT_IN_USE;
            if (Status == StatusRunning) { Status = StatusFinished; Result = ResultAborted; }
            return 0;
        }
    }
}

/// <summary>
/// SceCommonDialog: message, save data, IME, net check, NP and the other system dialogs over one shared state machine.
/// No PSN server is reachable, so online dialogs finish as "user canceled" (the NetCheckDialog PSN modes follow the
/// emulated account, see <see cref="PsnAccount"/>); message and
/// IME dialogs complete at once with the default choice (no host UI yet).
/// </summary>
public sealed class CommonDialog : HleModule
{
    const int StatusNone = CommonDialogState.StatusNone, StatusRunning = CommonDialogState.StatusRunning, StatusFinished = CommonDialogState.StatusFinished;
    const int ResultOk = CommonDialogState.ResultOk, ResultUserCanceled = CommonDialogState.ResultUserCanceled, ResultAborted = CommonDialogState.ResultAborted;

    public CommonDialogState State { get; } = new();

    /// <summary>Text returned by the IME dialog when there is no host keyboard (--ime-text); null = initial text.</summary>
    public static string? ImeDefaultText;

    /// <summary>sceCommonDialogSetConfigParam values (0xFFFFFFFF = use the system setting).</summary>
    public uint ConfigLanguage = 0xFFFFFFFF, ConfigEnterButton = 0xFFFFFFFF;

    // ------------------------------------------------------------------ helpers

    internal static string ReadUtf16(uint addr, int maxChars)
    {
        if (addr == 0) return "";
        int n = 0;
        while (n < maxChars && GuestMemory.Read<ushort>(addr + (uint)n * 2) != 0) n++;
        return Encoding.Unicode.GetString(GuestMemory.Span(addr, n * 2));
    }

    /// <summary>Writes UTF-16LE truncated to <paramref name="maxChars"/> units (never splitting a surrogate pair) plus a NUL.</summary>
    internal static void WriteUtf16(uint addr, string s, int maxChars)
    {
        if (s.Length > maxChars) s = s[..maxChars];
        if (s.Length > 0 && char.IsHighSurrogate(s[^1])) s = s[..^1];
        var bytes = Encoding.Unicode.GetBytes(s);
        bytes.CopyTo(GuestMemory.Span(addr, bytes.Length));
        GuestMemory.Write(addr + (uint)bytes.Length, (ushort)0);
    }

    /// <summary>Minimal "dialog that cannot do anything offline": takes the slot and finishes with <paramref name="result"/>.</summary>
    int SimpleInit(DialogType t, uint param, int result)
    {
        if (param == 0) return CommonDialogError.NULL;
        int r = State.Begin(t);
        if (r < 0) return r;
        State.Finish(result);
        Log.Info($"{t} dialog: finished ({(result == ResultOk ? "ok" : "canceled")})");
        return 0;
    }

    /// <summary>GetResult for families whose result struct is only known to start with SceCommonDialogResult:
    /// zeroes <paramref name="zeroBytes"/> (when known) and writes the result at +0.</summary>
    int SimpleResult(DialogType t, uint result, int zeroBytes = 0)
    {
        if (result == 0) return CommonDialogError.NULL;
        int r = State.ResultCheck(t);
        if (r < 0) return r;
        if (zeroBytes > 0) GuestMemory.Fill(result, 0, (uint)zeroBytes);
        GuestMemory.W32(result, (uint)State.Result);
        return 0;
    }

    // ------------------------------------------------------------------ global functions

    [Hle] bool sceCommonDialogIsRunning() => State.IsRunning;

    [Hle] int sceCommonDialogUpdate(uint updateParam) => updateParam == 0 ? CommonDialogError.NULL : 0; // the dialog is not drawn

    /// <summary>SceCommonDialogConfigParam (0x2C): +0 sdkVersion, +4 language, +8 enterButtonAssign, +0xC reserved.</summary>
    [Hle]
    int sceCommonDialogSetConfigParam(uint configParam)
    {
        if (configParam == 0) return CommonDialogError.NULL;
        uint lang = GuestMemory.R32(configParam + 4), enter = GuestMemory.R32(configParam + 8);
        if (lang != 0xFFFFFFFF && lang > 19) return CommonDialogError.INVALID_LANGUAGE;      // SCE_SYSTEM_PARAM_LANG_* 0..19
        if (enter != 0xFFFFFFFF && enter > 1) return CommonDialogError.INVALID_ENTER_BUTTON_ASSIGN; // CIRCLE 0, CROSS 1
        ConfigLanguage = lang; ConfigEnterButton = enter;
        return 0;
    }

    /// <summary>No worker thread exists here; the caller's own thread is a valid UID for priority/affinity changes.</summary>
    [Hle] int sceCommonDialogGetWorkerThreadId() => Kernel.GuestThread.Current?.Uid ?? 0;

    // ------------------------------------------------------------------ MsgDialog

    const int MsgModeUser = 1, MsgModeSystem = 2, MsgModeErrorCode = 3, MsgModeProgressBar = 4;
    const int MsgButtonInvalid = 0, MsgButtonOk = 1;
    int _msgMode, _msgButton;
    uint _msgBarPercent;

    /// <summary>SceMsgDialogParam (0x88): +0 sdkVersion, +4 commonParam, +0x50 mode, +0x54 userMsgParam*,
    /// +0x58 sysMsgParam*, +0x5C errorCodeParam*, +0x60 progBarParam*, +0x64 flag.</summary>
    [Hle]
    int sceMsgDialogInit(uint param)
    {
        if (param == 0) return CommonDialogError.NULL;
        int mode = (int)GuestMemory.R32(param + 0x50);
        if (mode is < MsgModeUser or > MsgModeProgressBar) return CommonDialogError.INVALID_ARGUMENT;
        int r = State.Begin(DialogType.Msg);
        if (r < 0) return r;
        _msgMode = mode;
        _msgButton = MsgButtonInvalid;
        _msgBarPercent = 0;
        bool stayRunning = false;
        int result = ResultOk;
        switch (mode)
        {
            case MsgModeUser:
            {
                uint up = GuestMemory.R32(param + 0x54);
                int buttonType = up != 0 ? (int)GuestMemory.R32(up) : 0;
                string msg = up != 0 ? GuestMemory.ReadCString(GuestMemory.R32(up + 4)) : "";
                Log.Info($"MsgDialog: {msg} (buttons {buttonType})");
                switch (buttonType)
                {
                    case 2: stayRunning = true; break;                                   // NONE: closed by the application
                    case 4: result = ResultUserCanceled; break;                          // CANCEL only
                    default: _msgButton = MsgButtonOk; break;                            // OK, YES, OK of OK_CANCEL, BUTTON1
                }
                break;
            }
            case MsgModeSystem:
            {
                uint sp = GuestMemory.R32(param + 0x58);
                int type = sp != 0 ? (int)GuestMemory.R32(sp) : 0;
                Log.Info($"MsgDialog: system message {type}");
                if (type is 1 or 5 or 6) stayRunning = true;                              // WAIT, WAIT_SMALL, WAIT_CANCEL
                else _msgButton = MsgButtonOk;
                break;
            }
            case MsgModeErrorCode:
            {
                uint ep = GuestMemory.R32(param + 0x5C);
                Log.Info($"MsgDialog: error code 0x{(ep != 0 ? GuestMemory.R32(ep) : 0):X8}");
                _msgButton = MsgButtonOk;
                break;
            }
            case MsgModeProgressBar:
            {
                uint pp = GuestMemory.R32(param + 0x60);
                if (pp != 0 && GuestMemory.R32(pp + 0x2C) != 0) Log.Info($"MsgDialog: progress bar: {GuestMemory.ReadCString(GuestMemory.R32(pp + 0x2C))}");
                stayRunning = true;
                break;
            }
        }
        if (!stayRunning) State.Finish(result);
        return 0;
    }

    [Hle] int sceMsgDialogGetStatus() => State.StatusOf(DialogType.Msg);

    [Hle]
    int sceMsgDialogClose()
    {
        lock (State.Lock)
        {
            if (State.StatusOf(DialogType.Msg) != StatusRunning) return CommonDialogError.NOT_RUNNING;
            State.Finish(ResultOk);
            return 0;
        }
    }

    [Hle] int sceMsgDialogAbort() => State.Abort(DialogType.Msg);
    [Hle] int sceMsgDialogTerm() => State.Term(DialogType.Msg);

    /// <summary>SceMsgDialogResult (0x2C): +0 mode, +4 result, +8 buttonId.</summary>
    [Hle]
    int sceMsgDialogGetResult(uint result)
    {
        if (result == 0) return CommonDialogError.NULL;
        int r = State.ResultCheck(DialogType.Msg);
        if (r < 0) return r;
        GuestMemory.Fill(result, 0, 0x2C);
        GuestMemory.W32(result, (uint)_msgMode);
        GuestMemory.W32(result + 4, (uint)State.Result);
        GuestMemory.W32(result + 8, State.Result == ResultAborted ? MsgButtonInvalid : (uint)_msgButton);
        return 0;
    }

    int ProgressBarCheck()
    {
        if (State.StatusOf(DialogType.Msg) == StatusNone) return CommonDialogError.NOT_RUNNING;
        return _msgMode == MsgModeProgressBar ? 0 : CommonDialogError.NOT_SUPPORTED;
    }

    [Hle]
    int sceMsgDialogProgressBarInc(int target, uint delta)
    {
        int r = ProgressBarCheck();
        if (r < 0) return r;
        _msgBarPercent = Math.Min(100, _msgBarPercent + delta);
        return 0;
    }

    [Hle]
    int sceMsgDialogProgressBarSetValue(int target, uint rate)
    {
        int r = ProgressBarCheck();
        if (r < 0) return r;
        _msgBarPercent = Math.Min(100, rate);
        return 0;
    }

    [Hle]
    int sceMsgDialogProgressBarSetMsg(int target, uint barMsg)
    {
        int r = ProgressBarCheck();
        if (r < 0) return r;
        if (barMsg == 0) return CommonDialogError.NULL;
        Log.Info($"MsgDialog: progress bar: {GuestMemory.ReadCString(barMsg)}");
        return 0;
    }

    /// <summary>Progress of the running progress-bar message dialog (for tests/UI).</summary>
    public uint MsgBarPercent => _msgBarPercent;

    // ------------------------------------------------------------------ SaveDataDialog
    // The dialog "runs" (status RUNNING) and its work completes at once (substatus FINISHED: the user confirmed / the
    // slot was shown); the application then calls Finish to close it (status FINISHED) and Term. Sub-dialogs without a
    // button (progress bar, "Saving..." message) stay running until the application closes them (SaveSubStatusFor).

    const int SaveModeFixed = 1, SaveModeList = 2, SaveModeUserMsg = 3, SaveModeSystemMsg = 4, SaveModeErrorCode = 5, SaveModeProgressBar = 6;
    int _saveSub = StatusNone, _saveMode, _saveSlot = -1;
    uint _saveUserData, _saveSlotInfo;

    [Hle]
    int sceSaveDataDialogInit(uint param)
    {
        if (param == 0) return CommonDialogError.NULL;
        int r = State.Begin(DialogType.SaveData);
        if (r < 0) return r;
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
        _saveSub = SaveSubStatusFor(param);
        Log.Info($"sceSaveDataDialogInit mode {_saveMode} dispType {GuestMemory.R32(param + 0x54)} slot {_saveSlot} sub {sub:X8} subType {(sub != 0 ? GuestMemory.R32(sub) : 0)} value {(sub != 0 ? GuestMemory.R32(sub + 4) : 0)} flag {GuestMemory.R32(param + 0x74)}");
        return 0;
    }

    bool SaveRunning => State.StatusOf(DialogType.SaveData) == StatusRunning;

    const int SaveSysMsgProgress = 5, SaveButtonTypeNone = 2;

    /// <summary>
    /// Sub-dialogs without a button ("Saving..." system message, a user message with SCE_SAVEDATA_DIALOG_BUTTON_TYPE_NONE,
    /// the progress bar) cannot be answered: they stay RUNNING until the application closes them with SubClose (or
    /// Finish), as on the console and in Vita3K. Everything else completes at once (auto-confirmed).
    /// </summary>
    static int SaveSubStatusFor(uint param)
    {
        int mode = (int)GuestMemory.R32(param + 0x50);
        uint user = GuestMemory.R32(param + 0x60), sys = GuestMemory.R32(param + 0x64);
        bool waits = mode switch
        {
            SaveModeProgressBar => true,
            SaveModeSystemMsg => sys != 0 && GuestMemory.R32(sys) == SaveSysMsgProgress,
            SaveModeUserMsg => user != 0 && GuestMemory.R32(user) == SaveButtonTypeNone,
            _ => false,
        };
        return waits ? StatusRunning : StatusFinished;
    }

    [Hle]
    int sceSaveDataDialogContinue(uint param)
    {
        if (!SaveRunning) return CommonDialogError.NOT_RUNNING;
        if (param == 0) return CommonDialogError.NULL;
        _saveMode = (int)GuestMemory.R32(param + 0x50);
        _saveSub = SaveSubStatusFor(param);
        return 0;
    }
    [Hle] int sceSaveDataDialogGetStatus() => State.StatusOf(DialogType.SaveData);
    [Hle] int sceSaveDataDialogGetSubStatus() => SaveRunning ? _saveSub : StatusNone;
    [Hle] int sceSaveDataDialogFinish(uint param) { if (!SaveRunning) return CommonDialogError.NOT_RUNNING; State.Finish(ResultOk); return 0; }
    [Hle] int sceSaveDataDialogAbort() { if (!SaveRunning) return CommonDialogError.NOT_RUNNING; State.Finish(ResultAborted); return 0; }
    [Hle] int sceSaveDataDialogTerm() { int r = State.Term(DialogType.SaveData); if (r == 0) _saveSub = StatusNone; return r; }
    [Hle] int sceSaveDataDialogProgressBarInc(int target, uint delta) => 0;
    [Hle] int sceSaveDataDialogProgressBarSetValue(int target, uint rate) => 0;
    [Hle] int sceSaveDataDialogSubClose() { _saveSub = StatusFinished; return 0; }

    [Hle]
    int sceSaveDataDialogGetResult(uint result)
    {
        if (result == 0) return CommonDialogError.NULL;
        if (State.StatusOf(DialogType.SaveData) == StatusNone) return CommonDialogError.NOT_FINISHED;
        // SceSaveDataDialogResult: mode, result, buttonId, slotId, slotInfo*, userdata, reserved[32]
        GuestMemory.Fill(result, 0, 0x38);
        GuestMemory.W32(result, (uint)_saveMode);
        GuestMemory.W32(result + 4, (uint)State.Result);
        GuestMemory.W32(result + 8, State.Result == ResultAborted ? 0u : 1u); // SCE_SAVEDATA_DIALOG_BUTTON_ID_OK / YES
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

    // ------------------------------------------------------------------ ImeDialog

    const int ImeErrParam = unchecked((int)0x80102001), ImeErrDialogMode = unchecked((int)0x80102003),
        ImeErrTextBoxMode = unchecked((int)0x80102004), ImeErrTitle = unchecked((int)0x80102005);
    const int ImeButtonNone = 0, ImeButtonClose = 1, ImeButtonEnter = 2;
    int _imeButton;

    /// <summary>SceImeDialogParam (0xA8): +0x14 type, +0x18 option, +0x20 dialogMode, +0x24 textBoxMode, +0x28 title*,
    /// +0x2C maxTextLength, +0x30 initialText*, +0x34 inputTextBuffer*, +0x38 commonParam.</summary>
    [Hle]
    int sceImeDialogInit(uint param)
    {
        if (param == 0) return CommonDialogError.NULL;
        uint option = GuestMemory.R32(param + 0x18), dialogMode = GuestMemory.R32(param + 0x20), textBoxMode = GuestMemory.R32(param + 0x24);
        uint titlePtr = GuestMemory.R32(param + 0x28), maxLen = GuestMemory.R32(param + 0x2C);
        uint initPtr = GuestMemory.R32(param + 0x30), buffer = GuestMemory.R32(param + 0x34);
        if (maxLen == 0 || maxLen > 2048 || buffer == 0) return ImeErrParam;
        if (dialogMode > 1) return ImeErrDialogMode;
        if (textBoxMode > 2) return ImeErrTextBoxMode;
        string title = ReadUtf16(titlePtr, 129);
        if (title.Length > 128) return ImeErrTitle;
        string initial = ReadUtf16(initPtr, (int)maxLen);
        int r = State.Begin(DialogType.Ime);
        if (r < 0) return r;
        // no host keyboard yet: the user "types" --ime-text (or keeps the initial text) and presses Enter
        string text = ImeDefaultText ?? initial;
        if (text.Length == 0) text = "Player"; // many games refuse an empty name and reopen the keyboard forever
        text = text.Replace("\r\n", "\n");
        if ((option & 1) == 0) text = text.Replace("\n", "");
        WriteUtf16(buffer, text, (int)maxLen);
        _imeButton = ImeButtonEnter;
        State.Finish(ResultOk);
        Log.Info($"ImeDialog \"{title}\": entered \"{text}\"");
        return 0;
    }

    [Hle] int sceImeDialogGetStatus() => State.StatusOf(DialogType.Ime);

    /// <summary>SceImeDialogResult (0x24): +0 result, +4 button.</summary>
    [Hle]
    int sceImeDialogGetResult(uint result)
    {
        if (result == 0) return CommonDialogError.NULL;
        int r = State.ResultCheck(DialogType.Ime);
        if (r < 0) return r;
        GuestMemory.Fill(result, 0, 0x24);
        GuestMemory.W32(result, (uint)State.Result);
        GuestMemory.W32(result + 4, State.Result == ResultAborted ? ImeButtonNone : (uint)_imeButton);
        return 0;
    }

    [Hle] int sceImeDialogTerm() => State.Term(DialogType.Ime);
    [Hle] int sceImeDialogAbort() => State.Abort(DialogType.Ime);

    // ------------------------------------------------------------------ NetCheckDialog

    const int NetCheckErrInvalidMode = unchecked((int)0x80100C02);
    // SceNetCheckDialogMode (psp2/netcheck_dialog.h): ADHOC_CONN 1, PSN 2, PSN_ONLINE 3, PS3_CONNECT 4, PSP_ADHOC_* 5..7
    internal const int NetCheckModePsn = 2, NetCheckModePsnOnline = 3, NetCheckModePs3Connect = 4;
    int _netCheckMode;
    bool _netCheckPsnSucceeded;

    /// <summary>
    /// SceNetCheckDialogParam (0xE0): +0x50 mode (1..7), +0x54 npCommunicationId. The PSN modes (sign in, and sign in +
    /// connect) finish OK with psnModeSucceeded when the emulated PSN account is signed in (<see cref="PsnAccount"/>, as
    /// Vita3K does); otherwise, and for the ad hoc / PS3 modes (no peers), the user cancels. A successful PSN_ONLINE check
    /// leaves the console connected, so the NP service state is ONLINE afterwards (<see cref="NpManager.ServiceState"/>).
    /// </summary>
    [Hle]
    int sceNetCheckDialogInit(uint param)
    {
        if (param == 0) return CommonDialogError.NULL;
        int mode = (int)GuestMemory.R32(param + 0x50);
        if (mode is < 1 or > 7) return NetCheckErrInvalidMode;
        int r = State.Begin(DialogType.NetCheck);
        if (r < 0) return r;
        _netCheckMode = mode;
        var acct = PsnAccount.Current;
        _netCheckPsnSucceeded = acct.SignedIn && mode is NetCheckModePsn or NetCheckModePsnOnline;
        State.Finish(_netCheckPsnSucceeded ? ResultOk : ResultUserCanceled);
        // the PSN_ONLINE check connects the console: NP reports ONLINE from now on
        if (_netCheckPsnSucceeded && mode == NetCheckModePsnOnline)
            Emu?.Hle.Modules.OfType<NpManager>().FirstOrDefault()?.OnPsnOnlineCheckSucceeded();
        Log.Info(_netCheckPsnSucceeded ? $"NetCheckDialog mode {mode}: signed in to PSN as {acct.OnlineId}"
                                       : $"NetCheckDialog mode {mode}: {(acct.SignedIn ? "no peer" : "offline")}, canceled");
        return 0;
    }

    [Hle] int sceNetCheckDialogGetStatus() => State.StatusOf(DialogType.NetCheck);

    /// <summary>SceNetCheckDialogResult (0x84): +0 result, +4 psnModeSucceeded.</summary>
    [Hle]
    int sceNetCheckDialogGetResult(uint result)
    {
        if (result == 0) return CommonDialogError.NULL;
        lock (State.Lock)
        {
            if (State.Type != DialogType.NetCheck) return CommonDialogError.NOT_IN_USE;
            if (State.Status != StatusFinished) return CommonDialogError.NOT_FINISHED;
        }
        GuestMemory.Fill(result, 0, 0x84);
        GuestMemory.W32(result, (uint)State.Result);
        GuestMemory.W32(result + 4, _netCheckPsnSucceeded && State.Result == ResultOk ? 1u : 0u);
        return 0;
    }

    [Hle] int sceNetCheckDialogTerm() => State.Term(DialogType.NetCheck);
    [Hle] int sceNetCheckDialogAbort() => State.Abort(DialogType.NetCheck);

    /// <summary>Only meaningful after a successful PS3_CONNECT check, which never happens offline.</summary>
    [Hle]
    int sceNetCheckDialogGetPS3ConnectInfo(uint info)
    {
        if (info == 0) return CommonDialogError.NULL;
        lock (State.Lock)
        {
            if (State.Type != DialogType.NetCheck) return CommonDialogError.NOT_IN_USE;
            if (State.Status != StatusFinished) return CommonDialogError.NOT_FINISHED;
            if (_netCheckMode != NetCheckModePs3Connect || State.Result != ResultOk) return CommonDialogError.NOT_AVAILABLE;
        }
        return CommonDialogError.NOT_AVAILABLE;
    }

    // ------------------------------------------------------------------ NP dialogs (no PSN server: canceled)

    [Hle] int sceNpMessageDialogInit(uint param) => SimpleInit(DialogType.NpMessage, param, ResultUserCanceled);
    [Hle] int sceNpMessageDialogGetStatus() => State.StatusOf(DialogType.NpMessage);
    [Hle] int sceNpMessageDialogGetResult(uint result) => SimpleResult(DialogType.NpMessage, result);
    [Hle] int sceNpMessageDialogAbort() => State.Abort(DialogType.NpMessage);
    [Hle] int sceNpMessageDialogTerm() => State.Term(DialogType.NpMessage);

    // The first-run "Preparing trophies" dialog: trophies work offline, so it finishes OK at once.
    // SceNpTrophySetupDialogParam: +0x50 context, +0x54 options; result (0x84): +0 result.
    [Hle] int sceNpTrophySetupDialogInit(uint param) => SimpleInit(DialogType.NpTrophySetup, param, ResultOk);
    [Hle] int sceNpTrophySetupDialogGetStatus() => State.StatusOf(DialogType.NpTrophySetup);
    [Hle] int sceNpTrophySetupDialogGetResult(uint result) => SimpleResult(DialogType.NpTrophySetup, result, 0x84);
    [Hle] int sceNpTrophySetupDialogAbort() => State.Abort(DialogType.NpTrophySetup);
    [Hle] int sceNpTrophySetupDialogTerm() => State.Term(DialogType.NpTrophySetup);

    [Hle] int sceNpFriendListDialogInit(uint param) => SimpleInit(DialogType.NpFriendList, param, ResultUserCanceled);
    [Hle] int sceNpFriendListDialogGetStatus() => State.StatusOf(DialogType.NpFriendList);
    [Hle] int sceNpFriendListDialogGetResult(uint result) => SimpleResult(DialogType.NpFriendList, result);
    [Hle] int sceNpFriendListDialogTerm() => State.Term(DialogType.NpFriendList);
    [Hle] int sceNpFriendListDialogAbort() => State.Abort(DialogType.NpFriendList);

    [Hle] int sceNpFriendList2DialogInit(uint param) => SimpleInit(DialogType.NpFriendList2, param, ResultUserCanceled);
    [Hle] int sceNpFriendList2DialogGetStatus() => State.StatusOf(DialogType.NpFriendList2);
    [Hle] int sceNpFriendList2DialogGetResult(uint result) => SimpleResult(DialogType.NpFriendList2, result);
    [Hle] int sceNpFriendList2DialogTerm() => State.Term(DialogType.NpFriendList2);
    [Hle] int sceNpFriendList2DialogAbort() => State.Abort(DialogType.NpFriendList2);

    [Hle] int sceNpProfileDialogInit(uint param) => SimpleInit(DialogType.NpProfile, param, ResultUserCanceled);
    [Hle] int sceNpProfileDialogGetStatus() => State.StatusOf(DialogType.NpProfile);
    [Hle] int sceNpProfileDialogGetResult(uint result) => SimpleResult(DialogType.NpProfile, result);
    [Hle] int sceNpProfileDialogTerm() => State.Term(DialogType.NpProfile);
    [Hle] int sceNpProfileDialogAbort() => State.Abort(DialogType.NpProfile);

    // Unnamed NP dialog family imported by Killzone Mercenary (Init/GetStatus/GetResult/Term/Abort, param with an NP
    // communication id at +0x50). Finishes canceled; GetResult writes only +0 (result) and +4 (0) since the size is unknown.
    [Hle("sceCommonDialog_33626E2C", Nid = 0x33626E2C, Library = "SceCommonDialog")]
    int KzDialogInit(uint param)
    {
        if (param != 0) Log.Info($"sceCommonDialog_33626E2C: commId {Encoding.ASCII.GetString(GuestMemory.Span(param + 0x50, 9))} +0x5C={GuestMemory.R32(param + 0x5C):X8}");
        return SimpleInit(DialogType.KzUnknown, param, ResultUserCanceled);
    }

    [Hle("sceCommonDialog_B7D4C911", Nid = 0xB7D4C911, Library = "SceCommonDialog")]
    int KzDialogGetStatus() => State.StatusOf(DialogType.KzUnknown);

    [Hle("sceCommonDialog_D1858A78", Nid = 0xD1858A78, Library = "SceCommonDialog")]
    int KzDialogGetResult(uint result)
    {
        int r = SimpleResult(DialogType.KzUnknown, result);
        if (r == 0) GuestMemory.W32(result + 4, 0);
        return r;
    }

    [Hle("sceCommonDialog_BEBAEF25", Nid = 0xBEBAEF25, Library = "SceCommonDialog")]
    int KzDialogTerm() => State.Term(DialogType.KzUnknown);

    [Hle("sceCommonDialog_B55CF0E3", Nid = 0xB55CF0E3, Library = "SceCommonDialog")]
    int KzDialogAbort() => State.Abort(DialogType.KzUnknown);

    // ------------------------------------------------------------------ other families (P1): nothing to pick or no device

    /// <summary>ScePhotoImportDialogResult (0x28): +0 result, +4 item count.</summary>
    [Hle] int scePhotoImportDialogInit(uint param) => SimpleInit(DialogType.PhotoImport, param, ResultUserCanceled);
    [Hle] int scePhotoImportDialogGetStatus() => State.StatusOf(DialogType.PhotoImport);
    [Hle] int scePhotoImportDialogGetResult(uint result) => SimpleResult(DialogType.PhotoImport, result, 0x28);
    [Hle] int scePhotoImportDialogTerm() => State.Term(DialogType.PhotoImport);
    [Hle] int scePhotoImportDialogAbort() => State.Abort(DialogType.PhotoImport);

    /// <summary>ScePhotoReviewDialogParam: +0x54 path[0x400]; result (0x24). Just "shows" the photo.</summary>
    [Hle]
    int scePhotoReviewDialogInit(uint param)
    {
        if (param != 0) Log.Info($"PhotoReviewDialog: {GuestMemory.ReadCString(param + 0x54, 0x400)}");
        return SimpleInit(DialogType.PhotoReview, param, ResultOk);
    }
    [Hle] int scePhotoReviewDialogGetStatus() => State.StatusOf(DialogType.PhotoReview);
    [Hle] int scePhotoReviewDialogGetResult(uint result) => SimpleResult(DialogType.PhotoReview, result, 0x24);
    [Hle] int scePhotoReviewDialogTerm() => State.Term(DialogType.PhotoReview);
    [Hle] int scePhotoReviewDialogAbort() => State.Abort(DialogType.PhotoReview);

    [Hle] int sceCameraImportDialogInit(uint param) => SimpleInit(DialogType.CameraImport, param, ResultUserCanceled);
    [Hle] int sceCameraImportDialogGetStatus() => State.StatusOf(DialogType.CameraImport);
    [Hle] int sceCameraImportDialogGetResult(uint result) => SimpleResult(DialogType.CameraImport, result);
    [Hle] int sceCameraImportDialogTerm() => State.Term(DialogType.CameraImport);
    [Hle] int sceCameraImportDialogAbort() => State.Abort(DialogType.CameraImport);

    [Hle] int sceVideoImportDialogInit(uint param) => SimpleInit(DialogType.VideoImport, param, ResultUserCanceled);
    [Hle] int sceVideoImportDialogGetStatus() => State.StatusOf(DialogType.VideoImport);
    [Hle] int sceVideoImportDialogGetResult(uint result) => SimpleResult(DialogType.VideoImport, result);
    [Hle] int sceVideoImportDialogTerm() => State.Term(DialogType.VideoImport);
    [Hle] int sceVideoImportDialogAbort() => State.Abort(DialogType.VideoImport);

    [Hle] int sceCompanionUtilDialogInit(uint param) => SimpleInit(DialogType.CompanionUtil, param, ResultUserCanceled);
    [Hle] int sceCompanionUtilDialogGetStatus() => State.StatusOf(DialogType.CompanionUtil);
    [Hle] int sceCompanionUtilDialogGetResult(uint result) => SimpleResult(DialogType.CompanionUtil, result);
    [Hle] int sceCompanionUtilDialogTerm() => State.Term(DialogType.CompanionUtil);
    [Hle] int sceCompanionUtilDialogAbort() => State.Abort(DialogType.CompanionUtil);

    [Hle] int sceCrossControllerDialogInit(uint param) => SimpleInit(DialogType.CrossController, param, ResultUserCanceled);
    [Hle] int sceCrossControllerDialogGetStatus() => State.StatusOf(DialogType.CrossController);
    [Hle] int sceCrossControllerDialogGetResult(uint result) => SimpleResult(DialogType.CrossController, result);
    [Hle] int sceCrossControllerDialogTerm() => State.Term(DialogType.CrossController);
    [Hle] int sceCrossControllerDialogAbort() => State.Abort(DialogType.CrossController);

    [Hle] int sceRemoteOSKDialogInit(uint param) => SimpleInit(DialogType.RemoteOsk, param, ResultUserCanceled);
    [Hle] int sceRemoteOSKDialogGetStatus() => State.StatusOf(DialogType.RemoteOsk);
    [Hle] int sceRemoteOSKDialogGetResult(uint result) => SimpleResult(DialogType.RemoteOsk, result);
    [Hle] int sceRemoteOSKDialogTerm() => State.Term(DialogType.RemoteOsk);
    [Hle] int sceRemoteOSKDialogAbort() => State.Abort(DialogType.RemoteOsk);

    [Hle] int sceStoreCheckoutDialogInit(uint param) => SimpleInit(DialogType.StoreCheckout, param, ResultUserCanceled);
    [Hle] int sceStoreCheckoutDialogGetStatus() => State.StatusOf(DialogType.StoreCheckout);
    [Hle] int sceStoreCheckoutDialogGetResult(uint result) => SimpleResult(DialogType.StoreCheckout, result);
    [Hle] int sceStoreCheckoutDialogTerm() => State.Term(DialogType.StoreCheckout);
    [Hle] int sceStoreCheckoutDialogAbort() => State.Abort(DialogType.StoreCheckout);

    [Hle] int sceNpSnsFacebookDialogInit(uint param) => SimpleInit(DialogType.NpSnsFacebook, param, ResultUserCanceled);
    [Hle] int sceNpSnsFacebookDialogGetStatus() => State.StatusOf(DialogType.NpSnsFacebook);
    [Hle] int sceNpSnsFacebookDialogGetResult(uint result) => SimpleResult(DialogType.NpSnsFacebook, result);
    [Hle] int sceNpSnsFacebookDialogGetResultLongToken(uint result) => result == 0 ? CommonDialogError.NULL : CommonDialogError.NOT_AVAILABLE; // never logged in
    [Hle] int sceNpSnsFacebookDialogTerm() => State.Term(DialogType.NpSnsFacebook);
    [Hle] int sceNpSnsFacebookDialogAbort() => State.Abort(DialogType.NpSnsFacebook);

    [Hle] int sceTwDialogInit(uint param) => SimpleInit(DialogType.Tw, param, ResultUserCanceled);
    [Hle] int sceTwDialogGetStatus() => State.StatusOf(DialogType.Tw);
    [Hle] int sceTwDialogGetResult(uint result) => SimpleResult(DialogType.Tw, result);
    [Hle] int sceTwDialogTerm() => State.Term(DialogType.Tw);
    [Hle] int sceTwDialogAbort() => State.Abort(DialogType.Tw);

    // PSP save data dialog: a regular common dialog family in libcdlg (Init/Continue/GetResult/Term over the shared slot)
    // that only the PSP emulator process drives. Its param/result layouts are not documented, so a Vita title calling
    // it gets a dialog that is canceled at once and only the leading SceCommonDialog result word is written.
    [Hle] int scePspSaveDataDialogInit(uint param) => SimpleInit(DialogType.PspSaveData, param, ResultUserCanceled);
    [Hle]
    int scePspSaveDataDialogContinue(uint param)
    {
        if (param == 0) return CommonDialogError.NULL;
        return State.StatusOf(DialogType.PspSaveData) == StatusNone ? CommonDialogError.NOT_IN_USE : 0;
    }
    [Hle] int scePspSaveDataDialogGetResult(uint result) => SimpleResult(DialogType.PspSaveData, result);
    [Hle] int scePspSaveDataDialogTerm() => State.Term(DialogType.PspSaveData);

    // the Init NID of TwLoginDialog is unknown, so it is never active
    [Hle] int sceTwLoginDialogGetStatus() => State.StatusOf(DialogType.TwLogin);
    [Hle] int sceTwLoginDialogGetResult(uint result) => SimpleResult(DialogType.TwLogin, result);
    [Hle] int sceTwLoginDialogTerm() => State.Term(DialogType.TwLogin);
    [Hle] int sceTwLoginDialogAbort() => State.Abort(DialogType.TwLogin);
}
