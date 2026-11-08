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
    const uint NidInit = 0x34516838, NidCreateContext = 0xC49FD33F, NidDestroyContext = 0x56F5CBA5, NidDestroyHandle = 0xFF142071, NidTerm = 0xBFE0F28F, NidCreateHandle = 0x4EBC6977, NidUnlock = 0xB397AA24;
    // the internal exports the trophy setup dialog uses to install a set (names unknown)
    const uint NidOpenInstall = 0xD70DAFA7, NidInstall = 0x62E37599, NidInstallProgress = 0x0AC3CEA8, NidCloseInstall = 0x97D19AB6,
        NidOpenInstallHandle = 0x17FB1D9E, NidCloseInstallHandle = 0xFD34F7CF, NidCommit = 0x2B0CC831, NidCommit3 = 0x3AC04020, NidCommit4 = 0x10182F1B,
        NidDebugUnlock = 0xC1406504, NidCreateDebugContext = 0xC80794CF;
    const int AlreadyUnlocked = unchecked((int)0x8055160F);

    bool _tried, _ready;
    uint _init, _createContext, _destroyContext, _createHandle, _unlock, _openInstall, _install, _installProgress, _closeInstall,
        _openInstallHandle, _closeInstallHandle, _commit, _commit3, _commit4, _debugUnlock, _createDebugContext, _scratch;
    int _handle;
    readonly Dictionary<string, int> _contexts = new();

    /// <summary>The libipmi server pipe of the shell's trophy server.</summary>
    internal const string ServerPipe = "SceNpTrophyServerMP";

    /// <summary>The shell's trophy server is up (its libipmi server pipe exists) and this process is one of its possible
    /// clients: an application, or (<paramref name="system"/>) a system program other than the shell.</summary>
    bool ServerRunning(bool system) => (system ? emu.IsSystemProgram : !emu.IsSystemProgram)
                                       && emu.Vita.Shell != null && !ReferenceEquals(emu, emu.Vita.Shell)
                                       && emu.Vita.Objects.All<MsgPipe>().Any(p => p.Name == ServerPipe);

    bool Attach(bool system = false)
    {
        if (_tried) return _ready;
        _tried = true;
        if (!ServerRunning(system) || GuestThread.Current is not { } thread) return false;
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
            _destroyContext = Export(NidDestroyContext);
            (_debugUnlock, _createDebugContext) = (Export(NidDebugUnlock), Export(NidCreateDebugContext));
            if (new[] { _init, _createContext, _createHandle, _unlock, _openInstall, _install, _installProgress, _closeInstall,
                    _openInstallHandle, _closeInstallHandle, _commit, _commit3, _commit4 }.Contains(0u))
            {
                Log.Warn("trophy server bridge: the client library lacks an export it needs");
                return false;
            }
            _scratch = emu.Memory.Alloc(0x200, "trophy server bridge");
            if (!Connect(thread)) return false;
            Log.Info($"Trophy server bridge: {emu.Describe()} is a client of the shell's trophy server");
            return _ready = true;
        }
        catch (Exception e) when (e is not GuestThreadExitException and not OutOfMemoryException)
        {
            Log.Warn($"trophy server bridge: {e.Message}");
            return false;
        }
    }

    /// <summary>Opens this process's connection to the server (np_trophy init and a handle).</summary>
    bool Connect(GuestThread thread)
    {
        int ri = (int)thread.CallGuest(_init, 0);
        int rh = (int)thread.CallGuest(_createHandle, _scratch);
        if (ri < 0 || rh < 0) { Log.Warn($"trophy server bridge: init 0x{ri:X8}, handle 0x{rh:X8}"); return false; }
        _handle = (int)GuestMemory.R32(_scratch);
        return true;
    }

    /// <summary>Closes the connection: its handle, then np_trophy itself.</summary>
    void Disconnect(GuestThread thread)
    {
        if (Export(NidDestroyHandle) is var dh and not 0) thread.CallGuest(dh, (uint)_handle);
        if (Export(NidTerm) is var term and not 0) thread.CallGuest(term);
    }

    /// <summary>The application created a context for <paramref name="commId"/> (SceNpCommunicationId at
    /// <paramref name="commIdAddr"/>, its signature at <paramref name="signAddr"/>, both may be 0): the server installs the
    /// set, then gets the trophies VitaSharp already has for it.</summary>
    public void ContextCreated(string commId, uint commIdAddr, uint signAddr, IEnumerable<int> unlocked, IReadOnlyDictionary<int, ulong>? dates = null)
    {
        lock (_contexts) if (_contexts.ContainsKey(commId)) return;
        if (!Attach() || GuestThread.Current is not { } thread) return;
        // copies of the id (0x10) and the signature (0xA0) the application passed
        uint id = commIdAddr != 0 ? _scratch + 0x10 : 0, sign = signAddr != 0 ? _scratch + 0x20 : 0;
        if (id != 0) GuestMemory.Copy(id, commIdAddr, 0x10);
        if (sign != 0) GuestMemory.Copy(sign, signAddr, 0xA0);
        // without both, the signature the title gave before (sceNpInit or an earlier context): np_trophy takes an
        // explicit id only with a signature, and the server checks it
        if ((id == 0 || sign == 0) && TrophyUnlocks.LoadSignature(emu.Options.FsRoot, commId) is { } stored)
        {
            (id, sign) = (_scratch + 0x10, _scratch + 0x20);
            // SceNpCommunicationId: data[9] "NPWR01234", term, num, dummy
            GuestMemory.Fill(id, 0, 0x10);
            GuestMemory.WriteCString(id, commId[..9], 10);
            GuestMemory.Write(id + 10, byte.Parse(commId[10..]));
            stored.CopyTo(GuestMemory.Span(sign, TrophyUnlocks.SignatureSize));
        }
        int r = (int)thread.CallGuest(_createContext, _scratch, id, sign, 0, 0, 0);
        if (r < 0) { Log.Warn($"trophy server bridge: context for {commId} = 0x{r:X8}"); return; }
        int ctx = (int)GuestMemory.R32(_scratch);
        var given = unlocked.ToList();
        int n = Register(thread, ctx, commId, given, dates);
        if (n < 0)
        {
            // the server keeps only a few contexts per client
            if (_destroyContext != 0) thread.CallGuest(_destroyContext, (uint)ctx);
            return;
        }
        lock (_contexts) _contexts[commId] = ctx;
        // what the silent path could not give goes through the normal unlock (the shell shows its notice)
        foreach (int t in given.Skip(n)) if (Unlock(thread, ctx, t)) n++;
        Log.Info($"Trophy server bridge: {commId} installed in the trophy server ({n} unlocked trophies given)");
    }

    /// <summary>
    /// A system program (Trophy Collection, NPXS10008) is about to talk to the trophy server: the titles that only have
    /// VitaSharp's store (ux0:user/00/trophy/&lt;NPCOMMID&gt;.json, games started without the shell) and no data in the server
    /// (ur0:user/00/trophy/data/&lt;NPCOMMID&gt;) are installed there from this process, each with its unlocked trophies, so
    /// the program lists them. The server reads each set's TROPHY.TRP through sceAppMgrTrophyMount(300, pid of this
    /// process), which points at the installed application that ships the set while its context is created, and checks
    /// the title's signature: the one VitaSharp kept when the title ran, or else the one in the application's code.
    /// </summary>
    public void InstallStoredTitles()
    {
        string root = emu.Options.FsRoot;
        string store = Path.Combine(root, "ux0", "user", "00", "trophy"), data = Path.Combine(root, "ur0", "user", "00", "trophy", "data");
        if (!Directory.Exists(store)) return;
        var pending = Directory.GetFiles(store, "*.json").Select(Path.GetFileNameWithoutExtension)
            .Where(id => id is { Length: 12 } && id[9] == '_' && !File.Exists(Path.Combine(data, id, "TRPTITLE.DAT")))
            .Select(id => (Id: id!, App: AppWithSet(root, id!))).Where(t => t.App != null && HasSignature(root, t.Id, t.App)).ToList();
        if (pending.Count == 0 || !Attach(system: true) || GuestThread.Current is not { } thread) return;
        bool connected = true;
        foreach (var (id, app) in pending)
        {
            var pkg = TrophyPackage.Load(Path.Combine(app!, "sce_sys", "trophy", id, "TROPHY.TRP"), id);
            if (pkg == null) continue;
            // one connection per title: the server mounts a client's application (sceAppMgrTrophyMount 300) once per
            // connection and keeps it until the client goes, so a second title on the same one reads the first
            // title's folder (0x80551619)
            if (!connected && !(connected = Connect(thread))) break;
            var dates = TrophyUnlocks.Load(TrophyUnlocks.PathFor(root, id));
            var unlocked = dates.Keys
                .Where(i => pkg.Trophies.FirstOrDefault(t => t.Id == i) is { } t && t.Grade != TrophyGrade.Platinum).Order().ToList();
            emu.Vita.TrophyApps[emu.Pid] = app!;
            try { ContextCreated(id, 0, 0, unlocked, dates); }
            finally { emu.Vita.TrophyApps.TryRemove(emu.Pid, out _); }
            // the program only needed the install: its context goes, then the connection
            int ctx;
            bool had;
            lock (_contexts) had = _contexts.Remove(id, out ctx);
            if (had && _destroyContext != 0) thread.CallGuest(_destroyContext, (uint)ctx);
            Disconnect(thread);
            connected = false;
        }
        // the program's own connection is refused while this process has another ("Could not restore the trophy
        // information")
        if (connected) Disconnect(thread);
    }

    /// <summary>The set's signature is known: kept from a run of the title, or read now from the application's code
    /// (and kept).</summary>
    static bool HasSignature(string root, string commId, string app)
    {
        if (TrophyUnlocks.LoadSignature(root, commId) != null) return true;
        if (TrophySignature.FromApp(root, app, commId) is not { } sign) return false;
        TrophyUnlocks.SaveSignature(root, commId, sign);
        Log.Info($"Trophy: {commId} signature read from {Path.GetFileName(app)}");
        return true;
    }

    uint Export(uint nid) => emu.Linker.TryGetFunctionExport(nid, out uint a) ? a : 0;

    /// <summary>The installed application (ux0:app/&lt;title id&gt;) that ships trophy set <paramref name="commId"/>.</summary>
    static string? AppWithSet(string root, string commId)
    {
        var apps = Path.Combine(root, "ux0", "app");
        if (!Directory.Exists(apps)) return null;
        return Directory.EnumerateDirectories(apps).Order(StringComparer.Ordinal)
            .FirstOrDefault(a => File.Exists(Path.Combine(a, "sce_sys", "trophy", commId, "TROPHY.TRP")));
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
    /// the request and the handle closed (97D19AB6, FD34F7CF). The trophies VitaSharp already has for the set
    /// (<paramref name="unlocked"/>) are given on that handle with the server's debug unlock (np_trophy C1406504, IPC
    /// 0x90036), which records them without the "You have earned a trophy!" notice the normal unlock makes the shell
    /// show: they were earned before.
    /// Returns how many were given that way, or -1 when the install failed.
    /// </summary>
    int Register(GuestThread thread, int ctx, string commId, List<int> unlocked, IReadOnlyDictionary<int, ulong>? dates)
    {
        uint hp = _scratch + 0xCC, req = _scratch + 0xD0, state = _scratch + 0xD4, progress = _scratch + 0xD8;
        GuestMemory.W32(req, 0);
        GuestMemory.W32(state, 0);
        int r = (int)thread.CallGuest(_openInstallHandle, hp);
        if (r < 0) { Log.Warn($"trophy server bridge: install handle for {commId} = 0x{r:X8}"); return -1; }
        uint h = GuestMemory.R32(hp);
        try
        {
            r = (int)thread.CallGuest(_openInstall, h, 0x01230001, 0x10, 0, 0, 0, req);
            if (r < 0) { Log.Warn($"trophy server bridge: install request for {commId} = 0x{r:X8}"); return -1; }
            uint q = GuestMemory.R32(req);
            try
            {
                r = (int)thread.CallGuest(_install, h, q, (uint)ctx, state);
                if (r < 0) { Log.Warn($"trophy server bridge: install of {commId} = 0x{r:X8}"); return -1; }
                // the server answers with what the install needs (the dialog shows it, the user confirms): 1, 2 and 5
                // continue with 2B0CC831, 3 with 3AC04020, 4 with 10182F1B
                uint st = GuestMemory.R32(state);
                uint commit = st switch { 1 or 2 or 5 => _commit, 3 => _commit3, 4 => _commit4, _ => 0 };
                if (commit == 0) { Log.Warn($"trophy server bridge: install of {commId}: state {st}"); return -1; }
                GuestMemory.W32(progress, 0xFFFFFFFF);
                r = (int)thread.CallGuest(commit, h, q, (uint)ctx, progress);
                if (r < 0) { Log.Warn($"trophy server bridge: install of {commId} (state {st}) = 0x{r:X8}"); return -1; }
                return DebugUnlock(thread, h, q, commId, unlocked, dates);
            }
            finally { thread.CallGuest(_closeInstall, q); }
        }
        finally { thread.CallGuest(_closeInstallHandle, h); }
    }

    /// <summary>Gives <paramref name="unlocked"/> with the debug unlock on install handle <paramref name="h"/> and
    /// request <paramref name="q"/>; stops at the first failure. Returns how many were given. The server takes the debug
    /// unlock only on a context of the debug kind (context[+0x20] == 1): np_trophy C80794CF creates one for a
    /// communication id alone (no signature, IPC create-context with kind 1), next to the title's normal context.</summary>
    int DebugUnlock(GuestThread thread, uint h, uint q, string commId, List<int> unlocked, IReadOnlyDictionary<int, ulong>? dates)
    {
        if (_debugUnlock == 0 || _createDebugContext == 0 || unlocked.Count == 0) return 0;
        uint target = _scratch + 0xE0, result = _scratch + 0xE8, idp = _scratch + 0x130, ctxp = _scratch + 0x140;
        // SceNpCommunicationId: data[9] "NPWR01234", term, num, dummy
        GuestMemory.Fill(idp, 0, 0x10);
        GuestMemory.WriteCString(idp, commId[..9], 10);
        GuestMemory.Write(idp + 10, byte.Parse(commId[10..]));
        int r = (int)thread.CallGuest(_createDebugContext, ctxp, idp, 0, 0);
        if (r < 0) { Log.Warn($"trophy server bridge: debug context for {commId} = 0x{r:X8}"); return 0; }
        uint ctx = GuestMemory.R32(ctxp);
        int n = 0;
        try
        {
            foreach (int t in unlocked)
            {
                // {kind, id}: 3 = one trophy (2 = a group, 1 = all)
                GuestMemory.W32(target, 3);
                GuestMemory.W32(target + 4, (uint)t);
                GuestMemory.Fill(result, 0, 0x40);
                // the server stamps the unlock with the date VitaSharp kept (the trophy was earned then)
                Interlocked.Exchange(ref emu.Vita.TrophyUnlockTick, dates != null && dates.TryGetValue(t, out var tick) ? (long)tick : 0);
                try { r = (int)thread.CallGuest(_debugUnlock, h, q, ctx, target, result); }
                finally { Interlocked.Exchange(ref emu.Vita.TrophyUnlockTick, 0); }
                if (r < 0 && r != AlreadyUnlocked)
                {
                    Log.Warn($"trophy server bridge: silent unlock {commId} {t} = 0x{r:X8}");
                    break;
                }
                n++;
            }
        }
        finally { if (_destroyContext != 0) thread.CallGuest(_destroyContext, ctx); }
        return n;
    }

    bool Unlock(GuestThread thread, int ctx, int trophyId)
    {
        int r = (int)thread.CallGuest(_unlock, (uint)ctx, (uint)_handle, (uint)trophyId, _scratch + 0xC);
        if (r < 0 && r != AlreadyUnlocked) Log.Warn($"trophy server bridge: unlock {trophyId} = 0x{r:X8}");
        return r >= 0;
    }
}
