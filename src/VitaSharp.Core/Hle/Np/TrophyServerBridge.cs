using VitaSharp.Core.Kernel;
using VitaSharp.Core.Loader;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Np;

/// <summary>
/// Keeps the system's trophy server in step with an application's trophies. On the Vita the trophy server runs in the
/// shell (shell.self: SceNpTrophyServer, the libipmi server "SceNpTrophy") and an application's np_trophy.suprx is its
/// client: creating a trophy context installs the title's trophy set in the user's trophy data (the server reads the
/// application's TROPHY.TRP through sceAppMgrTrophyMount(300, pid)) and every unlock is recorded there; Trophy Collection
/// (NPXS10008) lists what the server has. In VitaSharp an application keeps the HLE SceNpTrophy (its unlocks also go to
/// the emulator's store, notifications and launcher). When the shell's server runs, this bridge also loads the
/// firmware's client library into the application's process and repeats there what the application does: the context
/// (the title is installed), the trophies already unlocked in VitaSharp's store, then each new unlock. The application's
/// own imports stay bound to the HLE.
/// </summary>
internal sealed class TrophyServerBridge(Emulator emu)
{
    const string ClientModule = "vs0:sys/external/np_trophy.suprx";
    const uint NidInit = 0x34516838, NidCreateContext = 0xC49FD33F, NidCreateHandle = 0x4EBC6977, NidUnlock = 0xB397AA24;
    // the internal exports the trophy setup dialog uses to install a set (names unknown)
    const uint NidOpenInstall = 0xD70DAFA7, NidInstall = 0x62E37599, NidInstallProgress = 0x0AC3CEA8, NidCloseInstall = 0x97D19AB6,
        NidOpenInstallHandle = 0x17FB1D9E, NidCloseInstallHandle = 0xFD34F7CF, NidCommit = 0x2B0CC831, NidCommit3 = 0x3AC04020, NidCommit4 = 0x10182F1B;
    const int AlreadyUnlocked = unchecked((int)0x8055160F);

    bool _tried, _ready;
    uint _init, _createContext, _createHandle, _unlock, _openInstall, _install, _installProgress, _closeInstall,
        _openInstallHandle, _closeInstallHandle, _commit, _commit3, _commit4, _scratch;
    int _handle;
    readonly Dictionary<string, int> _contexts = new();

    /// <summary>The shell's trophy server is up (its libipmi server pipe exists) and this is an application.</summary>
    bool ServerRunning => !emu.IsSystemProgram && emu.Vita.Shell != null
                          && emu.Vita.Objects.All<MsgPipe>().Any(p => p.Name == "SceNpTrophyServerMP");

    bool Attach()
    {
        if (_tried) return _ready;
        _tried = true;
        if (!ServerRunning || GuestThread.Current is not { } thread) return false;
        var host = emu.Vfs.Resolve(ClientModule);
        if (host == null || !File.Exists(host)) return false;
        try
        {
            // the application's import stubs keep the HLE while the client and its dependencies load
            emu.Linker.KeepBindingsIn = emu.Kernel.Modules.Values.SelectMany(m => m.Segments.Values).Select(s => (s.Address, s.Size)).ToList();
            LoadedModule client;
            List<LoadedModule> deps;
            try
            {
                client = emu.LoadModule(ClientModule);
                deps = emu.LoadFirmwareDependencies(client);
            }
            finally { emu.Linker.KeepBindingsIn = null; }
            foreach (var m in deps.Append(client).Where(m => m.StartEntry != 0))
                if ((int)thread.CallGuest(m.StartEntry, 0, 0) is var r and < 0)
                    Log.Warn($"trophy server bridge: {m.Path} module_start = 0x{r:X8}");
            uint Export(uint nid) => emu.Linker.TryGetFunctionExport(nid, out uint a) ? a : 0;
            (_init, _createContext, _createHandle, _unlock) = (Export(NidInit), Export(NidCreateContext), Export(NidCreateHandle), Export(NidUnlock));
            (_openInstall, _install, _installProgress, _closeInstall) = (Export(NidOpenInstall), Export(NidInstall), Export(NidInstallProgress), Export(NidCloseInstall));
            (_openInstallHandle, _closeInstallHandle) = (Export(NidOpenInstallHandle), Export(NidCloseInstallHandle));
            (_commit, _commit3, _commit4) = (Export(NidCommit), Export(NidCommit3), Export(NidCommit4));
            if (new[] { _init, _createContext, _createHandle, _unlock, _openInstall, _install, _installProgress, _closeInstall,
                    _openInstallHandle, _closeInstallHandle, _commit, _commit3, _commit4 }.Contains(0u))
            {
                Log.Warn("trophy server bridge: the client library lacks an export it needs");
                return false;
            }
            _scratch = emu.Memory.Alloc(0x200, "trophy server bridge");
            int ri = (int)thread.CallGuest(_init, 0);
            int rh = (int)thread.CallGuest(_createHandle, _scratch);
            if (ri < 0 || rh < 0) { Log.Warn($"trophy server bridge: init 0x{ri:X8}, handle 0x{rh:X8}"); return false; }
            _handle = (int)GuestMemory.R32(_scratch);
            Log.Info($"Trophy server bridge: {emu.Describe()} is a client of the shell's trophy server");
            return _ready = true;
        }
        catch (Exception e) when (e is not GuestThreadExitException and not OutOfMemoryException)
        {
            Log.Warn($"trophy server bridge: {e.Message}");
            return false;
        }
    }

    /// <summary>The application created a context for <paramref name="commId"/> (SceNpCommunicationId at
    /// <paramref name="commIdAddr"/>, its signature at <paramref name="signAddr"/>, both may be 0): the server installs the
    /// set, then gets the trophies VitaSharp already has for it.</summary>
    public void ContextCreated(string commId, uint commIdAddr, uint signAddr, IEnumerable<int> unlocked)
    {
        lock (_contexts) if (_contexts.ContainsKey(commId)) return;
        if (!Attach() || GuestThread.Current is not { } thread) return;
        // copies of the id (0x10) and the signature (0xA0) the application passed
        uint id = commIdAddr != 0 ? _scratch + 0x10 : 0, sign = signAddr != 0 ? _scratch + 0x20 : 0;
        if (id != 0) GuestMemory.Copy(id, commIdAddr, 0x10);
        if (sign != 0) GuestMemory.Copy(sign, signAddr, 0xA0);
        int r = (int)thread.CallGuest(_createContext, _scratch, id, sign, 0, 0, 0);
        if (r < 0) { Log.Warn($"trophy server bridge: context for {commId} = 0x{r:X8}"); return; }
        int ctx = (int)GuestMemory.R32(_scratch);
        if (!Register(thread, ctx, commId)) return;
        lock (_contexts) _contexts[commId] = ctx;
        int n = 0;
        foreach (int t in unlocked) if (Unlock(thread, ctx, t)) n++;
        Log.Info($"Trophy server bridge: {commId} installed in the trophy server ({n} unlocked trophies given)");
    }

    /// <summary>The application unlocked <paramref name="trophyId"/> of <paramref name="commId"/>.</summary>
    public void Unlocked(string commId, int trophyId)
    {
        int ctx;
        lock (_contexts) if (!_contexts.TryGetValue(commId, out ctx)) return;
        if (GuestThread.Current is { } thread) Unlock(thread, ctx, trophyId);
    }

    /// <summary>
    /// Installs the context's trophy set the way the firmware's trophy setup dialog does (libcdlg_np_trophy_setup.suprx,
    /// FW 3.74, what an application's sceNpTrophySetupDialogInit runs): a handle of the dialog's kind (np_trophy 17FB1D9E),
    /// an install request on it (D70DAFA7 with 0x01230001), the check of the context's set (62E37599: the server reads
    /// TROPHY.TRP and answers with a state; the dialog shows its details, 0AC3CEA8, and the user confirms), the install
    /// itself for that state (2B0CC831, 3AC04020 or 10182F1B: the server writes the title's conf and data files), then
    /// the request and the handle closed (97D19AB6, FD34F7CF).
    /// </summary>
    bool Register(GuestThread thread, int ctx, string commId)
    {
        uint hp = _scratch + 0xCC, req = _scratch + 0xD0, state = _scratch + 0xD4, progress = _scratch + 0xD8;
        GuestMemory.W32(req, 0);
        GuestMemory.W32(state, 0);
        int r = (int)thread.CallGuest(_openInstallHandle, hp);
        if (r < 0) { Log.Warn($"trophy server bridge: install handle for {commId} = 0x{r:X8}"); return false; }
        uint h = GuestMemory.R32(hp);
        try
        {
            r = (int)thread.CallGuest(_openInstall, h, 0x01230001, 0x10, 0, 0, 0, req);
            if (r < 0) { Log.Warn($"trophy server bridge: install request for {commId} = 0x{r:X8}"); return false; }
            uint q = GuestMemory.R32(req);
            try
            {
                r = (int)thread.CallGuest(_install, h, q, (uint)ctx, state);
                if (r < 0) { Log.Warn($"trophy server bridge: install of {commId} = 0x{r:X8}"); return false; }
                // the server answers with what the install needs (the dialog shows it, the user confirms): 1, 2 and 5
                // continue with 2B0CC831, 3 with 3AC04020, 4 with 10182F1B
                uint st = GuestMemory.R32(state);
                uint commit = st switch { 1 or 2 or 5 => _commit, 3 => _commit3, 4 => _commit4, _ => 0 };
                if (commit == 0) { Log.Warn($"trophy server bridge: install of {commId}: state {st}"); return false; }
                GuestMemory.W32(progress, 0xFFFFFFFF);
                r = (int)thread.CallGuest(commit, h, q, (uint)ctx, progress);
                if (r < 0) { Log.Warn($"trophy server bridge: install of {commId} (state {st}) = 0x{r:X8}"); return false; }
                return true;
            }
            finally { thread.CallGuest(_closeInstall, q); }
        }
        finally { thread.CallGuest(_closeInstallHandle, h); }
    }

    bool Unlock(GuestThread thread, int ctx, int trophyId)
    {
        int r = (int)thread.CallGuest(_unlock, (uint)ctx, (uint)_handle, (uint)trophyId, _scratch + 0xC);
        if (r < 0 && r != AlreadyUnlocked) Log.Warn($"trophy server bridge: unlock {trophyId} = 0x{r:X8}");
        return r >= 0;
    }
}
