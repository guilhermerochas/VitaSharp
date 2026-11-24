using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

// System-imported functions without public names of SceShell (shell.self's exports to its plugins) and SceNpUtility
// (FW 3.74 shell.self and np_utility read in their code, study only).

/// <summary>
/// SceShell: the functions shell.self exports to its plugins (impose_net_plugin, gamecard_installer_plugin,
/// online_storage_plugin). The real shell runs as LLE and answers them itself; these entries answer for a process
/// without it: the shell's network service is not up (its 0x80412181 after the argument checks), there is no game
/// card installer object (0x80AF0001), and the online storage hooks do nothing.
/// </summary>
public sealed partial class Shell : HleModule
{
    internal const int SHELL_NOT_READY = unchecked((int)0x80412181), SHELL_NULL = unchecked((int)0x80412184),
        SHELL_INVALID = unchecked((int)0x8041219E), INSTALLER_NONE = unchecked((int)0x80AF0001);

    /// <summary>The shell's network service checks that it is up before the pointers it needs, so without it every
    /// call answers 0x80412181.</summary>
    static int NetService(string nid)
    {
        Log.InfoOnce("shell-" + nid, $"SceShell NID_{nid}: the shell's network service is not up (no LLE shell)");
        return SHELL_NOT_READY;
    }

    [Hle("NID_0025417F", Nid = 0x0025417F, Library = "SceShell", Default = true)]
    int ShellNet0025417F(uint a, uint b) => NetService("0025417F");

    [Hle("NID_05D6FCB0", Nid = 0x05D6FCB0, Library = "SceShell", Default = true)]
    int ShellNet05D6FCB0(uint a) => NetService("05D6FCB0");

    [Hle("NID_29A14252", Nid = 0x29A14252, Library = "SceShell", Default = true)]
    int ShellNet29A14252() => NetService("29A14252");

    [Hle("NID_2E0B6FA6", Nid = 0x2E0B6FA6, Library = "SceShell", Default = true)]
    int ShellNet2E0B6FA6(uint a, uint b, uint c) => NetService("2E0B6FA6");

    [Hle("NID_3059EEBC", Nid = 0x3059EEBC, Library = "SceShell", Default = true)]
    int ShellNet3059EEBC(uint a, uint b) => NetService("3059EEBC");

    [Hle("NID_36788D70", Nid = 0x36788D70, Library = "SceShell", Default = true)]
    int ShellNet36788D70(uint a) => NetService("36788D70");

    [Hle("NID_3E737FBE", Nid = 0x3E737FBE, Library = "SceShell", Default = true)]
    int ShellNet3E737FBE(uint a, uint b) => NetService("3E737FBE");

    [Hle("NID_3F41E440", Nid = 0x3F41E440, Library = "SceShell", Default = true)]
    int ShellNet3F41E440() => NetService("3F41E440");

    [Hle("NID_42053414", Nid = 0x42053414, Library = "SceShell", Default = true)]
    int ShellNet42053414(uint a, uint b, uint c) => NetService("42053414");

    /// <summary>NID_449348B6 (a, b, const param *p): with the service up, p NULL or of size 0x38 (else 0x8041219E).</summary>
    [Hle("NID_449348B6", Nid = 0x449348B6, Library = "SceShell", Default = true)]
    int ShellNet449348B6(uint a, uint b, uint param) => NetService("449348B6");

    [Hle("NID_5DC4A753", Nid = 0x5DC4A753, Library = "SceShell", Default = true)]
    int ShellNet5DC4A753(uint a) => NetService("5DC4A753");

    /// <summary>NID_60129092 (a, uint count): with the service up, a non-NULL a and a count 1..30 (0x8041219E).</summary>
    [Hle("NID_60129092", Nid = 0x60129092, Library = "SceShell", Default = true)]
    int ShellNet60129092(uint a, uint count) => NetService("60129092");

    [Hle("NID_77642C92", Nid = 0x77642C92, Library = "SceShell", Default = true)]
    int ShellNet77642C92(uint a) => NetService("77642C92");

    [Hle("NID_8858B876", Nid = 0x8858B876, Library = "SceShell", Default = true)]
    int ShellNet8858B876(uint a) => NetService("8858B876");

    [Hle("NID_8C1D932A", Nid = 0x8C1D932A, Library = "SceShell", Default = true)]
    int ShellNet8C1D932A(uint a) => NetService("8C1D932A");

    [Hle("NID_B4D888D5", Nid = 0xB4D888D5, Library = "SceShell", Default = true)]
    int ShellNetB4D888D5() => NetService("B4D888D5");

    [Hle("NID_C1886828", Nid = 0xC1886828, Library = "SceShell", Default = true)]
    int ShellNetC1886828() => NetService("C1886828");

    [Hle("NID_EC75C2F3", Nid = 0xEC75C2F3, Library = "SceShell", Default = true)]
    int ShellNetEC75C2F3(uint a, uint b) => NetService("EC75C2F3");

    [Hle("NID_FDAA5D2E", Nid = 0xFDAA5D2E, Library = "SceShell", Default = true)]
    int ShellNetFDAA5D2E() => NetService("FDAA5D2E");

    /// <summary>NID_548A3111 / NID_F197032C (a, b, c): the game card installer's requests: no installer 0x80AF0001;
    /// NID_6A6123E3 (): its state, 0 without one.</summary>
    [Hle("NID_548A3111", Nid = 0x548A3111, Library = "SceShell", Default = true)]
    int ShellInstaller548A3111(uint a, uint b, uint c) => INSTALLER_NONE;

    [Hle("NID_F197032C", Nid = 0xF197032C, Library = "SceShell", Default = true)]
    int ShellInstallerF197032C(uint a, uint b, uint c) => INSTALLER_NONE;

    [Hle("NID_6A6123E3", Nid = 0x6A6123E3, Library = "SceShell", Default = true)]
    int ShellInstallerState() => 0;

    /// <summary>NID_7FE4E732 / NID_D4C7D2B7 (online storage) and NID_83D16CA8 / NID_E3D3A162: the shell's hooks
    /// around its plugins' work (busy indicators); nothing to show here.</summary>
    [Hle("NID_7FE4E732", Nid = 0x7FE4E732, Library = "SceShell", Default = true)]
    int ShellHook7FE4E732() => 0;

    [Hle("NID_D4C7D2B7", Nid = 0xD4C7D2B7, Library = "SceShell", Default = true)]
    int ShellHookD4C7D2B7() => 0;

    [Hle("NID_83D16CA8", Nid = 0x83D16CA8, Library = "SceShell", Default = true)]
    int ShellHook83D16CA8() => 0;

    [Hle("NID_E3D3A162", Nid = 0xE3D3A162, Library = "SceShell", Default = true)]
    int ShellHookE3D3A162() => 0;

    /// <summary>NID_99893E59 (const char *name, param, launchParam): the shell's launch for its plugins:
    /// sceAppMgrLaunchAppByName2.</summary>
    [Hle("NID_99893E59", Nid = 0x99893E59, Library = "SceShell")]
    int ShellLaunchAppByName(uint name, uint param, uint launchParam) =>
        Emu.Hle.Modules.OfType<AppMgrShell>().First().LaunchAppByName2(name, param, launchParam);
}

public sealed partial class AppMgrShell
{
    internal int LaunchAppByName2(uint name, uint param, uint launchParam) => sceAppMgrLaunchAppByName2ThroughEntry(name, param, launchParam);
}

public sealed partial class NpUtility
{
    const int UTIL_REQUEST_NOT_FOUND = unchecked((int)0x8055070E), UTIL_ABORTED = unchecked((int)0x80550707),
        UTIL_NULL = unchecked((int)0x8055070C), UTIL_NOT_EMPTY = unchecked((int)0x80550704),
        UTIL_BAD_VALUE = unchecked((int)0x80550704), UTIL_TIMEOUT = unchecked((int)0x80550704),
        UTIL_NO_JID = unchecked((int)0x80550708);

    /// <summary>
    /// np_utility 3.74's request managers keep their requests (id: the manager in its high nibbles) in the firmware's
    /// own tables; the HLE keeps none there, so every lookup of an id is "not found" (0x8055070E) after the checks
    /// that come first.
    /// </summary>
    static int UnknownRequest(string nid)
    {
        Log.InfoOnce("nputil-" + nid, $"SceNpUtility NID_{nid}: no request of the utility's managers (none kept in HLE)");
        return UTIL_REQUEST_NOT_FOUND;
    }

    /// <summary>NID_25D26B6F (uint id, void **request): the request of an id (managers by its 0x00F00000,
    /// 0xF0000000 or 0x0F000000 nibble); *request = NULL, 0x8055070E.</summary>
    [Hle("NID_25D26B6F", Nid = 0x25D26B6F, Library = "SceNpUtility", Default = true)]
    int UtilFindRequest(uint id, uint request)
    {
        if (request != 0 && ((id & 0xF00000) == 0x200000 || (id & 0xF0000000) == 0x20000000 || (id & 0xF000000) == 0x2000000))
        {
            GuestMemory.W32(request, 0);
        }

        return UnknownRequest("25D26B6F");
    }

    /// <summary>NID_B9FFB253 (int key, void **request, uint slot): a slot's request by key (slot 3 or more, or key 0:
    /// 0x8055070E).</summary>
    [Hle("NID_B9FFB253", Nid = 0xB9FFB253, Library = "SceNpUtility", Default = true)]
    int UtilFindSlotRequest(int key, uint request, uint slot) => UnknownRequest("B9FFB253");

    /// <summary>NID_ED2B86ED (int key, int slot): aborts a slot's request by key.</summary>
    [Hle("NID_ED2B86ED", Nid = 0xED2B86ED, Library = "SceNpUtility", Default = true)]
    int UtilAbortSlotRequest(int key, int slot) => UnknownRequest("ED2B86ED");

    /// <summary>NID_4013244C (int key, uint slot): aborts a request of a started manager (slot 3 or more 0x8055070E,
    /// the manager stopped 0x80550702).</summary>
    [Hle("NID_4013244C", Nid = 0x4013244C, Library = "SceNpUtility", Default = true)]
    int UtilAbortRequest(int key, uint slot)
    {
        if (slot >= UtilSlots)
        {
            return UTIL_INVALID_ARGUMENT;
        }

        return _utilA[slot] ? UnknownRequest("4013244C") : UTIL_NOT_INITIALIZED;
    }

    /// <summary>NID_485F368A (request, uint slot) / NID_31D3542E (request) / NID_7A86C37E (int slot): release of a
    /// request block and the cleanup of a manager's finished requests: nothing is kept, 0.</summary>
    [Hle("NID_485F368A", Nid = 0x485F368A, Library = "SceNpUtility", Default = true)]
    int UtilReleaseRequest(uint request, uint slot) => 0;

    [Hle("NID_31D3542E", Nid = 0x31D3542E, Library = "SceNpUtility", Default = true)]
    int UtilReleaseManagedRequest(uint request) => 0;

    [Hle("NID_7A86C37E", Nid = 0x7A86C37E, Library = "SceNpUtility", Default = true)]
    int UtilCleanup(int slot) => 0;

    /// <summary>NID_CD3A3712 (request): marks a request aborted (+8 = 1) when its slot (+0x44) is below 3.</summary>
    [Hle("NID_CD3A3712", Nid = 0xCD3A3712, Library = "SceNpUtility")]
    int UtilMarkAborted(uint request)
    {
        if (request != 0 && GuestMemory.R32(request + 0x44) < UtilSlots)
        {
            GuestMemory.W32(request + 8, 1);
        }

        return 0;
    }

    /// <summary>NID_31423835 (record, a, b, c): the 0x548-byte record zeroed with a at +0, b at +0x540, c at +0x544.</summary>
    [Hle("NID_31423835", Nid = 0x31423835, Library = "SceNpUtility")]
    int UtilInitRecord(uint record, uint a, uint b, uint c)
    {
        GuestMemory.Fill(record, 0, 0x548);
        GuestMemory.W32(record, a);
        GuestMemory.W32(record + 0x540, b);
        GuestMemory.W32(record + 0x544, c);
        return 0;
    }

    /// <summary>NID_866D737A (request): drops the request's HTTP request (+0x10) and connection (+0xC).</summary>
    [Hle("NID_866D737A", Nid = 0x866D737A, Library = "SceNpUtility")]
    int UtilDropHttp(uint request)
    {
        if ((int)GuestMemory.R32(request + 0x10) > 0)
        {
            GuestMemory.W32(request + 0x10, 0);
        }

        if ((int)GuestMemory.R32(request + 0xC) > 0)
        {
            GuestMemory.W32(request + 0xC, 0);
        }

        return 0;
    }

    /// <summary>NID_34B42669 (request): sends its HTTP request (aborted +0x18 = 1: 0x80550707); no server: the
    /// network's error.</summary>
    [Hle("NID_34B42669", Nid = 0x34B42669, Library = "SceNpUtility", Default = true)]
    int UtilSend(uint request) => GuestMemory.R32(request + 0x18) == 1 ? UTIL_ABORTED : PsnAccount.Current.ServerError;

    /// <summary>NID_A3E828ED (request, buffer, size, int *read): reads the answer (aborted 0x80550707); nothing to read:
    /// *read = 0.</summary>
    [Hle("NID_A3E828ED", Nid = 0xA3E828ED, Library = "SceNpUtility", Default = true)]
    int UtilRead(uint request, uint buffer, int size, uint read)
    {
        if (size != 0 && GuestMemory.R32(request + 0x18) == 1)
        {
            return UTIL_ABORTED;
        }

        GuestMemory.W32(read, 0);
        return 0;
    }

    /// <summary>NID_01C44B07 (bool reuse, request, ...) / NID_A08E60BA: the HTTP connection / request of a request
    /// (libhttp's create calls on the request's template); no server: the network's error.</summary>
    [Hle("NID_01C44B07", Nid = 0x01C44B07, Library = "SceNpUtility", Default = true)]
    int UtilCreateConnection(int reuse, uint request, uint host, uint scheme, ushort port) => PsnAccount.Current.ServerError;

    [Hle("NID_A08E60BA", Nid = 0xA08E60BA, Library = "SceNpUtility", Default = true)]
    int UtilCreateHttpRequest(int reuse, uint request, int method, uint path, uint lengthLow, uint lengthHigh) => PsnAccount.Current.ServerError;

    /// <summary>NID_418E1324 (int slot) / NID_6C1161AD (void *block, int slot): a slot heap's reset and free; the HLE
    /// requests allocate nothing there.</summary>
    [Hle("NID_418E1324", Nid = 0x418E1324, Library = "SceNpUtility", Default = true)]
    int UtilHeapReset(int slot) => 0;

    [Hle("NID_6C1161AD", Nid = 0x6C1161AD, Library = "SceNpUtility", Default = true)]
    int UtilHeapFree(uint block, int slot) => 0;

    NpManager Np => Emu.Hle.Modules.OfType<NpManager>().First();

    /// <summary>NID_54C48F92 (SceNpId *npId, const char *jid, int slot): an NP id from a JID text (SceNpCommon's split
    /// then its NP id; an empty split 0x80550708).</summary>
    [Hle("NID_54C48F92", Nid = 0x54C48F92, Library = "SceNpUtility")]
    int UtilNpIdFromJid(uint npId, uint jid, int slot)
    {
        uint needed = Emu.Memory.Alloc(4, "np util jid size");
        try
        {
            GuestMemory.W32(needed, 0);
            int result = Np.ParseJid(0, jid, 0, 0, needed);
            if (result < 0)
            {
                return result;
            }

            uint size = GuestMemory.R32(needed);
            if (size == 0)
            {
                return UTIL_NO_JID;
            }

            uint buffer = Emu.Memory.Alloc(size + 0x10, "np util jid");
            try
            {
                uint parts = buffer + size;
                result = Np.ParseJid(parts, jid, buffer, size, 0);
                return result < 0 ? result : Np.NpIdOfJid(npId, parts);
            }
            finally
            {
                Emu.Memory.Free(buffer);
            }
        }
        finally
        {
            Emu.Memory.Free(needed);
        }
    }

    /// <summary>NID_14437509 (char **jid, const SceNpId *npId, int slot): the JID text of an NP id, in a block of
    /// the slot's heap (*jid; freed with NID_6C1161AD).</summary>
    [Hle("NID_14437509", Nid = 0x14437509, Library = "SceNpUtility")]
    int UtilJidFromNpId(uint jid, uint npId, int slot)
    {
        uint needed = Emu.Memory.Alloc(0x14, "np util jid parts");
        try
        {
            int result = Np.JidOfNpId(0, npId, 0, 0, needed);
            if (result < 0)
            {
                return result;
            }

            uint size = GuestMemory.R32(needed);
            uint buffer = Emu.Memory.Alloc(size, "np util jid");
            result = Np.JidOfNpId(needed + 4, npId, buffer, size, 0);
            if (result < 0)
            {
                Emu.Memory.Free(buffer);
                return result;
            }

            Np.JoinJid(needed + 4, 0, 0, needed);
            uint length = GuestMemory.R32(needed);
            uint text = Emu.Memory.Alloc(length, "np util jid text");
            result = Np.JoinJid(needed + 4, text, length, 0);
            Emu.Memory.Free(buffer);
            if (result < 0)
            {
                Emu.Memory.Free(text);
                return result;
            }

            GuestMemory.W32(jid, text);
            return 0;
        }
        finally
        {
            Emu.Memory.Free(needed);
        }
    }

    /// <summary>The utility's server requests (lookup, word filter, title storage, presence): each takes a request id
    /// and first checks its pointers (NULL 0x8055070C / 0x80550704), then looks the id up: 0x8055070E.</summary>
    [Hle("NID_08893FA1", Nid = 0x08893FA1, Library = "SceNpUtility", Default = true)]
    int UtilRequest08893FA1(uint id, uint data, uint a, uint b, uint c, uint reserved) =>
        data == 0 ? UTIL_NULL : reserved != 0 ? UTIL_NOT_EMPTY : UnknownRequest("08893FA1");

    [Hle("NID_E64B4101", Nid = 0xE64B4101, Library = "SceNpUtility", Default = true)]
    int UtilRequestE64B4101(uint id, uint data, uint a, uint b, uint c, uint reserved) =>
        data == 0 ? UTIL_NULL : reserved != 0 ? UTIL_NOT_EMPTY : UnknownRequest("E64B4101");

    [Hle("NID_23B387A5", Nid = 0x23B387A5, Library = "SceNpUtility", Default = true)]
    int UtilRequest23B387A5(uint id, uint a, uint b, uint reserved) =>
        a == 0 || b == 0 ? UTIL_NULL : reserved != 0 ? UTIL_NOT_EMPTY : UnknownRequest("23B387A5");

    [Hle("NID_B2FD5E9D", Nid = 0xB2FD5E9D, Library = "SceNpUtility", Default = true)]
    int UtilRequestB2FD5E9D(uint id, uint a, uint b, uint reserved) =>
        a == 0 || b == 0 ? UTIL_NULL : reserved != 0 ? UTIL_NOT_EMPTY : UnknownRequest("B2FD5E9D");

    [Hle("NID_9D8E0730", Nid = 0x9D8E0730, Library = "SceNpUtility", Default = true)]
    int UtilRequest9D8E0730(uint id, uint a, uint b, uint c, uint d, uint e, uint f, uint g, uint h, uint i, uint j, uint reserved) =>
        b == 0 ? UTIL_NULL : reserved != 0 ? UTIL_NOT_EMPTY : UnknownRequest("9D8E0730");

    [Hle("NID_C2598A46", Nid = 0xC2598A46, Library = "SceNpUtility", Default = true)]
    int UtilRequestC2598A46(uint id, uint a, uint count, uint b, uint c, uint result, uint results) =>
        result == 0 ? UTIL_BAD_VALUE : UnknownRequest("C2598A46");

    [Hle("NID_C9283605", Nid = 0xC9283605, Library = "SceNpUtility", Default = true)]
    int UtilRequestC9283605(uint id, uint a, uint text, uint output, int mode) => UnknownRequest("C9283605");

    [Hle("NID_7BC7E7F7", Nid = 0x7BC7E7F7, Library = "SceNpUtility", Default = true)]
    int UtilRequest7BC7E7F7(uint id, uint npId, uint a, uint b, uint output) => output == 0 ? UTIL_BAD_VALUE : UnknownRequest("7BC7E7F7");

    /// <summary>NID_A0991531 (id, int a, uint timeouts...): a request's timeouts: at least one given, each 0 or at
    /// least 1 s (connect) / 10 s (the others), else 0x80550704; then the id.</summary>
    [Hle("NID_A0991531", Nid = 0xA0991531, Library = "SceNpUtility", Default = true)]
    int UtilSetTimeouts(uint id, int a, uint resolve, uint connect, uint send, uint receive)
    {
        if (a == 0 && resolve == 0 && receive == 0 && connect == 0 && send == 0)
        {
            return UTIL_TIMEOUT;
        }

        if (a < 0 || (resolve != 0 && resolve < 1_000_000) || (connect != 0 && connect < 10_000_000) || (send != 0 && send < 10_000_000)
            || (receive != 0 && receive < 10_000_000))
        {
            return UTIL_TIMEOUT;
        }

        return UnknownRequest("A0991531");
    }

    /// <summary>NID_44A2F444 (out, const id *src, a, uint slot) / NID_F4B21646 (const npId *a, b, int slot): new
    /// requests of a started manager (slot 3 or more 0x8055070E, stopped 0x80550702, NULL 0x8055070C): the manager
    /// has no server to send them to, so the creation reports the network's error.</summary>
    [Hle("NID_44A2F444", Nid = 0x44A2F444, Library = "SceNpUtility", Default = true)]
    int UtilCreateRequest44A2F444(uint output, uint source, uint a, uint slot)
    {
        if (slot >= UtilSlots)
        {
            return UTIL_INVALID_ARGUMENT;
        }

        if (!_utilA[slot])
        {
            return UTIL_NOT_INITIALIZED;
        }

        return source == 0 ? UTIL_NULL : PsnAccount.Current.ServerError;
    }

    [Hle("NID_F4B21646", Nid = 0xF4B21646, Library = "SceNpUtility", Default = true)]
    int UtilCreateRequestF4B21646(uint npId, uint a, int slot) => (uint)slot >= UtilSlots ? UTIL_INVALID_ARGUMENT : PsnAccount.Current.ServerError;

    /// <summary>NID_548625FC (context, a, b, c) / NID_B7A52202 / NID_D46E45DE (a, b): the managers' callouts
    /// (SceNpCommon) of a context; none run here.</summary>
    [Hle("NID_548625FC", Nid = 0x548625FC, Library = "SceNpUtility", Default = true)]
    int UtilQueue548625FC(uint context, uint a, uint b, uint c) => 0;

    [Hle("NID_B7A52202", Nid = 0xB7A52202, Library = "SceNpUtility", Default = true)]
    int UtilQueueB7A52202(uint a, uint b) => 0;

    [Hle("NID_D46E45DE", Nid = 0xD46E45DE, Library = "SceNpUtility", Default = true)]
    int UtilQueueD46E45DE(uint a, uint b) => 0;

    /// <summary>NID_4A479D1D (id) / NID_944FE83C (id): delete / abort of a request by id; NID_89C8F74E (...): a
    /// request's start: the id is not known (0x8055070E).</summary>
    [Hle("NID_4A479D1D", Nid = 0x4A479D1D, Library = "SceNpUtility", Default = true)]
    int UtilDeleteRequest(uint id) => UnknownRequest("4A479D1D");

    [Hle("NID_944FE83C", Nid = 0x944FE83C, Library = "SceNpUtility", Default = true)]
    int UtilAbortRequestById(uint id) => UnknownRequest("944FE83C");

    [Hle("NID_89C8F74E", Nid = 0x89C8F74E, Library = "SceNpUtility", Default = true)]
    int UtilStartRequest(uint id, uint a, uint b, uint c, uint d, uint e) => UnknownRequest("89C8F74E");

    /// <summary>NID_E2135903 (parser, data, size): the utility's XML parser on a chunk, as SceNpCommon's.</summary>
    [Hle("NID_E2135903", Nid = 0xE2135903, Library = "SceNpUtility")]
    int UtilXmlParse(uint parser, uint data, int size) => Np.XmlParse(parser, data, size);
}
