using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

// Exports without public names that no installed module imports (the "General" group) of SceAppUtil, SceNpCommon,
// SceCommonDialog, SceNpWebApi, SceTriggerUtil, SceShell, SceNpBasic and SceNpManager (FW 3.74 apputil, np_common,
// libcdlg, np_webapi, trigger_util, shell.self, np_basic and np_manager read in their code, study only).

public sealed partial class AppUtil
{
    const int ERR_UTF8 = unchecked((int)0x801006FF);

    /// <summary>NID_1E2A6158 (SceAppUtil)(slot, files, fileNum, mountPoint, SceSize *requiredSizeKiB): the save of
    /// sceAppUtilSaveDataDataSave through SceAppMgr's other save request (0x84DE76C7): before sceAppUtilInit
    /// 0x80100601, then the same checks (the slot param's rules included) and the same write.</summary>
    [Hle("NID_1E2A6158", Nid = 0x1E2A6158, Library = "SceAppUtil")]
    int AppUtilSaveDataDataSave2(uint slot, uint files, uint fileNum, uint mountPoint, uint requiredKiB)
    {
        if (!_initialized)
        {
            return ERR_NOT_INITIALIZED;
        }

        int check = AppUtilSaveDataDataSaveCheckThroughEntry(slot, files, fileNum, mountPoint, requiredKiB);
        return check != 0 ? check : sceAppUtilSaveDataDataSaveThroughEntry(slot, files, fileNum, mountPoint, new Ptr<uint>(requiredKiB));
    }

    /// <summary>NID_7DA34311 (SceAppUtil)(const char *text, uint size): the UTF-8 check of the slot texts: the text (up
    /// to its terminator within <paramref name="size"/> bytes) must be valid UTF-8 and must not start with a byte
    /// order mark, else 0x801006FF.</summary>
    [Hle("NID_7DA34311", Nid = 0x7DA34311, Library = "SceAppUtil")]
    int AppUtilCheckUtf8(uint text, uint size)
    {
        if (text == 0 || size == 0)
        {
            return ERR_UTF8;
        }

        ReadOnlySpan<byte> bytes = GuestMemory.Span(text, (int)Math.Min(size, 0x10000u));
        int end = bytes.IndexOf((byte)0);
        ReadOnlySpan<byte> content = bytes[..(end < 0 ? bytes.Length : end)];
        if (content.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            return ERR_UTF8;
        }

        return Utf8Valid(text, (int)Math.Min(size, 0x10000u)) ? 0 : ERR_UTF8;
    }
}

public sealed partial class NpManager
{
    /// <summary>
    /// NID_13293ADC (SceNpCommon)(char *out, uint outSize, const uint8 *text, uint length, uint *taken, uint *needed):
    /// one UTF-8 character of the text (a NULL text or 0 length 0x80550601; without an output a NULL taken
    /// 0x80550601): its length (1 for ASCII and for a byte that starts no valid sequence, 2..4 for a valid one), a
    /// sequence cut by the length 0x80550602 with *needed its full length, an output shorter than the character
    /// 0x80550602; the character is copied to the output.
    /// </summary>
    [Hle("NID_13293ADC", Nid = 0x13293ADC, Library = "SceNpCommon")]
    int NpCommonUtf8Character(uint output, uint outputSize, uint text, uint length, uint taken, uint needed)
    {
        if (text == 0 || length == 0 || (output == 0 && taken == 0))
        {
            return NPC_TEXT_NULL;
        }

        ReadOnlySpan<byte> bytes = GuestMemory.Span(text, (int)Math.Min(length, 8u));
        byte lead = bytes[0];
        int size;
        if ((lead & 0x80) == 0)
        {
            size = 1;
        }
        else if ((lead & 0xE0) == 0xC0 && lead >= 0xC2)
        {
            size = 2;
        }
        else if ((lead & 0xF0) == 0xE0)
        {
            size = 3;
        }
        else if ((lead & 0xF8) == 0xF0)
        {
            size = 4;
        }
        else
        {
            size = 1;
        }

        if (size > 1)
        {
            if (length < size)
            {
                if (taken != 0)
                {
                    GuestMemory.W32(taken, 0);
                }

                if (needed != 0)
                {
                    GuestMemory.W32(needed, (uint)size);
                }

                return NPC_TEXT_SPACE;
            }

            for (int i = 1; i < size; i++)
            {
                if ((bytes[i] & 0xC0) != 0x80)
                {
                    size = 1;
                    break;
                }
            }
        }

        if (output != 0)
        {
            if (outputSize < size)
            {
                return NPC_TEXT_SPACE;
            }

            bytes[..size].CopyTo(GuestMemory.Span(output, size));
        }

        if (taken != 0)
        {
            GuestMemory.W32(taken, (uint)size);
        }

        if (needed != 0)
        {
            GuestMemory.W32(needed, (uint)size);
        }

        return 0;
    }

    /// <summary>NID_92DF18F6 (SceNpCommon)(char *out, uint size): copies the library's 0x26-byte text field (never set
    /// in 3.74: empty); a size below 0x26 0x80550602.</summary>
    [Hle("NID_92DF18F6", Nid = 0x92DF18F6, Library = "SceNpCommon")]
    int NpCommonCopyField(uint output, uint size)
    {
        if (size < 0x26)
        {
            return NPC_TEXT_SPACE;
        }

        GuestMemory.Fill(output, 0, Math.Min(size, 0x1000u));
        return 0;
    }
}

public sealed partial class CommonDialog
{
    /// <summary>NID_2CD0649B(param): the system's Init of the IME dialog (family 0x4, OnInit): sceImeDialogInit.</summary>
    [Hle("NID_2CD0649B", Nid = 0x2CD0649B, Library = "SceCommonDialog")]
    int CommonDialogImeInit(uint param) => sceImeDialogInitThroughEntry(param);

    /// <summary>NID_D474A749(param, block): the video import dialog (family 0x1C) with its block (OnInit2): as
    /// NID_FBE52F6D.</summary>
    [Hle("NID_D474A749", Nid = 0xD474A749, Library = "SceCommonDialog")]
    int CommonDialogVideoImportInit2(uint param, uint block) => NID_FBE52F6DThroughEntry(param, block);

    /// <summary>NID_64D21971(a, b): a request with a two-word block to the running Facebook dialog (family 0x11);
    /// NOT_IN_USE when it is not the active one.</summary>
    [Hle("NID_64D21971", Nid = 0x64D21971, Library = "SceCommonDialog")]
    int CommonDialogFacebookRequest(uint a, uint b) => FamilyRequest(DialogType.NpSnsFacebook);
}

public sealed partial class NpWebApi
{
    /// <summary>NID_93BBA612 (SceNpWebApi)(int libCtxId, apiGroup, path, int method, int64 *requestId): the system's
    /// create request without content (0 context 0x80552902, unknown 0x80552904).</summary>
    [Hle("NID_93BBA612", Nid = 0x93BBA612, Library = "SceNpWebApi")]
    int NpWebApiCreateRequestForSystem2(int libCtxId, uint apiGroup, uint path, int method, uint requestId) =>
        NpWebApiCreateRequestForSystemThroughEntry(libCtxId, apiGroup, path, method, 0, 0, requestId);

    /// <summary>NID_1140FA82 / NID_6E6AFA39 (SceNpWebApi)(int libCtxId, int id): unregister a context's push event
    /// callback and filter (an unknown context 0x80552904); nothing is kept for them here: 0.</summary>
    [Hle("NID_1140FA82", Nid = 0x1140FA82, Library = "SceNpWebApi")]
    int NpWebApiUnregisterPushCallback(int libCtxId, int id) => KnownContext(libCtxId) ? 0 : ERR_WEBAPI_CONTEXT;

    [Hle("NID_6E6AFA39", Nid = 0x6E6AFA39, Library = "SceNpWebApi")]
    int NpWebApiDeletePushFilter(int libCtxId, int id) => KnownContext(libCtxId) ? 0 : ERR_WEBAPI_CONTEXT;
}

public sealed partial class TriggerUtil
{
    /// <summary>NID_1B615264 / NID_2AD6A378 (SceTriggerUtil)(uint kind, callback, arg): the trigger service requests
    /// 0x100009 / 0x10000E (kind below 8) and NID_921FF8E1 the request 0x100004 (kind below 4): a bad kind
    /// 0x80103605; one request at a time. No automatic start is scheduled: accepted at once (a default).</summary>
    [Hle("NID_1B615264", Nid = 0x1B615264, Library = "SceTriggerUtil", Default = true)]
    int TriggerUtilRequest100009(uint kind, uint callback, uint argument) => TriggerRequest("1B615264", kind, 8);

    [Hle("NID_2AD6A378", Nid = 0x2AD6A378, Library = "SceTriggerUtil", Default = true)]
    int TriggerUtilRequest10000E(uint kind, uint callback, uint argument) => TriggerRequest("2AD6A378", kind, 8);

    [Hle("NID_921FF8E1", Nid = 0x921FF8E1, Library = "SceTriggerUtil", Default = true)]
    int TriggerUtilRequest100004(uint kind, uint callback, uint argument) => TriggerRequest("921FF8E1", kind, 4);

    static int TriggerRequest(string nid, uint kind, uint kinds)
    {
        if (kind >= kinds)
        {
            return TRIGGER_ERR_KIND;
        }

        Log.InfoOnce("trigger-" + nid, $"SceTriggerUtil NID_{nid}: kind {kind} accepted (no automatic start is scheduled)");
        return 0;
    }

    /// <summary>NID_845BF533 (SceTriggerUtil)(): tells the shell (notification 0x31) the trigger list changed; 0.</summary>
    [Hle("NID_845BF533", Nid = 0x845BF533, Library = "SceTriggerUtil", Default = true)]
    int TriggerUtilNotifyChange() => 0;
}

public sealed partial class Shell
{
    [Hle("NID_5928D835", Nid = 0x5928D835, Library = "SceShell", Default = true)]
    int ShellNet5928D835() => NetService("5928D835");

    [Hle("NID_6B8772A2", Nid = 0x6B8772A2, Library = "SceShell", Default = true)]
    int ShellNet6B8772A2() => NetService("6B8772A2");

    [Hle("NID_794BF5BE", Nid = 0x794BF5BE, Library = "SceShell", Default = true)]
    int ShellNet794BF5BE() => NetService("794BF5BE");

    [Hle("NID_7AD86CE1", Nid = 0x7AD86CE1, Library = "SceShell", Default = true)]
    int ShellNet7AD86CE1(uint a, uint b) => NetService("7AD86CE1");

    [Hle("NID_871A8E92", Nid = 0x871A8E92, Library = "SceShell", Default = true)]
    int ShellNet871A8E92() => NetService("871A8E92");

    [Hle("NID_A25CD3F2", Nid = 0xA25CD3F2, Library = "SceShell", Default = true)]
    int ShellNetA25CD3F2(uint a, uint b) => NetService("A25CD3F2");
}

public sealed partial class NpSocial
{
    const int BASIC_SYSTEM_NOT_INITIALIZED = unchecked((int)0x80551D04), BASIC_SYSTEM_ALREADY = unchecked((int)0x80551D05);

    /// <summary>np_basic 3.74's system connection (NID_999CC1E9: "SceNpBasicSystem", a second init 0x80551D05);
    /// the system calls need it (0x80551D04).</summary>
    [SnapshotMissing]
    bool _basicSystemInitialized;

    /// <summary>The NP service's answer to the system's friend and presence requests: no friends, no presence of
    /// others (with the local services the "not found" the module maps, else the server's error).</summary>
    int BasicSystemRequest(string nid, bool argumentsValid = true)
    {
        if (!_basicSystemInitialized)
        {
            return BASIC_SYSTEM_NOT_INITIALIZED;
        }

        if (!argumentsValid)
        {
            return BASIC_INVALID_ARGUMENT;
        }

        Log.InfoOnce("npbasic-" + nid, $"SceNpBasic NID_{nid}: no friend data on the local NP service");
        return PsnAccount.Current.ServerError;
    }

    [Hle("NID_999CC1E9", Nid = 0x999CC1E9, Library = "SceNpBasic")]
    int NpBasicSystemInit(uint param)
    {
        if (_basicSystemInitialized)
        {
            return BASIC_SYSTEM_ALREADY;
        }

        _basicSystemInitialized = true;
        return 0;
    }

    /// <summary>NID_0C9BAF57 / NID_B93ADCDA (npId, out): a friend's presence (request 0x9003A; NULL 0x80551D02).</summary>
    [Hle("NID_0C9BAF57", Nid = 0x0C9BAF57, Library = "SceNpBasic", Default = true)]
    int NpBasicSystemFriendPresence(uint npId, uint output) => BasicSystemRequest("0C9BAF57", npId != 0 && output != 0);

    [Hle("NID_B93ADCDA", Nid = 0xB93ADCDA, Library = "SceNpBasic", Default = true)]
    int NpBasicSystemFriendPresenceSummary(uint npId, uint output) => BasicSystemRequest("B93ADCDA", npId != 0 && output != 0);

    /// <summary>NID_375EABF2 (a, b, c): the own presence and its state (request 0x9003C; NULL 0x80551D02).</summary>
    [Hle("NID_375EABF2", Nid = 0x375EABF2, Library = "SceNpBasic", Default = true)]
    int NpBasicSystemOwnPresence(uint a, uint b, uint c) => BasicSystemRequest("375EABF2", a != 0 && b != 0 && c != 0);

    /// <summary>NID_748925A4 (a, b, c): request 0x9003E with two blocks (NULL 0x80551D02).</summary>
    [Hle("NID_748925A4", Nid = 0x748925A4, Library = "SceNpBasic", Default = true)]
    int NpBasicSystemRequest9003E(uint a, uint b, uint c) => BasicSystemRequest("748925A4", a != 0 && c != 0);

    /// <summary>NID_A1C1A8E2 (value) / NID_A6BFBBFF (): requests 0x9003B and 0x9002B.</summary>
    [Hle("NID_A1C1A8E2", Nid = 0xA1C1A8E2, Library = "SceNpBasic", Default = true)]
    int NpBasicSystemRequest9003B(uint value) => BasicSystemRequest("A1C1A8E2");

    [Hle("NID_A6BFBBFF", Nid = 0xA6BFBBFF, Library = "SceNpBasic", Default = true)]
    int NpBasicSystemRequest9002B() => BasicSystemRequest("A6BFBBFF");
}

public sealed partial class NpManager
{
    const int NPM_INVALID = unchecked((int)0x80550503), NPM_NO_CLIENT = unchecked((int)0x80550502),
        NPM_CALLBACK_NOT_FOUND = unchecked((int)0x80550509), NPM_NOT_INITIALIZED = unchecked((int)0x80550002);

    /// <summary>NID_06EBD8B1 (): a request to the NP service once the library is up (0x80550002 before).</summary>
    [Hle("NID_06EBD8B1", Nid = 0x06EBD8B1, Library = "SceNpManager", Default = true)]
    int NpManagerServiceRequest() => NpManagerReady ? 0 : NPM_NOT_INITIALIZED;

    /// <summary>NID_2EA038D8 / NID_C80D8C3E (SceNpManager)(void *ticket, uint *size): the account's ticket (requests
    /// 0xB0017 / 0xB0019 with a 0x600-byte buffer; NULL 0x80550503). A licence ticket is the PSN server's, which
    /// cannot be reached: its error, never a ticket.</summary>
    [Hle("NID_2EA038D8", Nid = 0x2EA038D8, Library = "SceNpManager", Default = true)]
    int NpManagerReadTicket(uint ticket, uint size) => ticket == 0 || size == 0 ? NPM_INVALID : PsnAccount.Current.ServerError;

    [Hle("NID_C80D8C3E", Nid = 0xC80D8C3E, Library = "SceNpManager", Default = true)]
    int NpManagerReadTicket2(uint ticket, uint size) => ticket == 0 || size == 0 ? NPM_INVALID : PsnAccount.Current.ServerError;

    /// <summary>NID_8C6EACC4 (int id): unregisters a service callback of the library's four slots (unknown
    /// 0x80550509); none is registered through it here.</summary>
    [Hle("NID_8C6EACC4", Nid = 0x8C6EACC4, Library = "SceNpManager", Default = true)]
    int NpManagerUnregisterServiceCallback(int id) => NPM_CALLBACK_NOT_FOUND;

    /// <summary>NID_BA49FD24 (int *out): the library's service handle, set when its connection comes up; none here:
    /// 0x80550002.</summary>
    [Hle("NID_BA49FD24", Nid = 0xBA49FD24, Library = "SceNpManager", Default = true)]
    int NpManagerServiceHandle(uint output) => NPM_NOT_INITIALIZED;

    /// <summary>NID_BBCD1B6D (a, b, ..., f) / NID_C7F6ADC4 (a, b, ..., l): the auth server's asynchronous requests
    /// 0xB003C / 0xB003A (NULL pointers 0x80550503, no auth client 0x80550502); the server cannot be reached.</summary>
    [Hle("NID_BBCD1B6D", Nid = 0xBBCD1B6D, Library = "SceNpManager", Default = true)]
    int NpManagerAuthRequestB003C(uint a, uint b, uint c, uint d, uint e, uint f) =>
        b == 0 || f == 0 ? NPM_INVALID : NPM_NO_CLIENT;

    [Hle("NID_C7F6ADC4", Nid = 0xC7F6ADC4, Library = "SceNpManager", Default = true)]
    int NpManagerAuthRequestB003A(uint a, uint b, uint c, uint d, uint e, uint f, uint g, uint h, uint i, uint j, uint k, uint l) =>
        b == 0 || d == 0 || g == 0 || h == 0 || k == 0 || l == 0 ? NPM_INVALID : NPM_NO_CLIENT;
}
