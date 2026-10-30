# System software: the shell and the system apps

Stage 4 of the Vita OS plan. VitaSharp runs the installed firmware's own programs, not a recreation:
`vs0:vsh/shell/shell.self` (SceShell: boot sequence, lock screen, home screen, LiveArea) and the
applications in `vs0:app/NPXS*`. They need the firmware installed (`install-firmware`); everything below
was checked with firmware 3.74 copied from Vita3K into a scratch data folder.

## How to run

```
VitaSharp.Desktop --data=<root> boot-shell [run options]        # the shell
VitaSharp.Desktop --data=<root> run NPXS10008                   # a system app (vs0:app is searched after ux0:app)
VitaSharp.Desktop --data=<root> registry /CONFIG/SYSTEM/        # list registry keys; path=value sets one
```

Test scenarios: `title=vs0:vsh/shell` plus `args=--self=shell.self ...`, or `title=NPXS10008`.
The lock screen opens with `--input=swipe@f400:0.95:0.08:0.3:0.7:f30` (the peel corner).
In the launcher, the system app bubbles of the Vita-style home screen start the app (same `run` path
as a game), and the toolbar button **Sistema do Vita** boots the real shell in the emulator window
(`boot-shell`): games started from its LiveArea run as processes of that same session.

## What is emulated and what is the firmware's

A program under vs0 is a *system program* (`Emulator.IsSystemProgram`); its SELF program authority ID
(0x2800000000000001 for the shell, 0x28...10 for Settings) decides `sceAppMgrIsGameProgram` /
`sceSblACMgrIsGameProgram`. For system programs:

- **User-mode libraries come from the firmware (LLE).** The modulemgr process preloads (libc, libshellsvc,
  libcdlg, libfios2, apputil, libSceFt2, libpvf; preload-disabled bits honoured) are loaded from
  vs0:sys/external, the `SCE_SYSMODULE_INTERNAL_*` ids load their module with its arguments (libpaf gets its
  heap sizes that way), and every firmware module loaded pulls in the providers of the libraries it imports
  that nothing provides yet. `FirmwareLibraries` indexes which vs0 module exports which library
  (`<fs>/firmware-libraries.txt`, read with `ModuleImage` without loading the module). A library VitaSharp
  has any HLE for (NP, AppUtil, Http, ...) stays on HLE so every process sees the same emulated services
  (the PSN mock, saves, trophies); `--hle-modules=` keeps more on HLE.
- **IPC is the firmware's.** libipmi (inside libshellsvc) is built on named kernel objects (message pipes
  `SceShellSvcServerMP`, `...RQM00`, semaphores, event flags), so it needs no HLE: it works because the
  server (the shell) and its clients run as processes of the same kernel (below). libipmi only lets a process
  create a server when its SceAppMgrAppStatus says so (+0x74 bit 0 or type byte +0x71 == 2): the shell's does.
- **A system program's own imports** from firmware libraries the HLE knows nothing of pull in their providers before
  it starts (the shell calls SceNpCommonPs4 in its first initialisation; modules that need libpaf still wait for the
  program's own sysmodule load).
- **HLE, the kernel side:** SceSharedFb (below), SceGxmInternalForVsh (VSH initialise, display sync objects shared by key), SceRegMgr /
  SceRegMgrForSDK / SceRegMgrService, sceSblUsGetSpkgInfo, SceAppMgr's shell services (`AppMgrShell.cs`),
  SceVshBridge / SceSblQafMgr / SceSblPmMgr / SceAVConfig with a retail unit's answers (`VshBridge.cs`).
- **The registry** (`Firmware/Registry.cs`) has every key of the firmware's template `os0:kd/registry.db0`
  (1331 keys in 3.74, with type, size and default) plus the written values in
  `vd0:registry/vitasharp-registry.txt`, shared by all processes. Language, enter button and date/time
  format follow the emulator options until the system writes them.

## Installed games on the home screen

The shell never scans ux0:app. Its database rebuild (`vs0:vsh/common/dbrecovery_plugin.suprx`, run when
app.db is missing or the memory card's `ux0:id.dat` is not this system's) lists `ux0:appmeta`, mounts each
entry with `sceAppMgrAppDataMountById(110, titleid)` (`ux0:appmeta/<id>`, now emulated) and registers the
application from its param.sfo, icon0/pic0 and LiveArea folder. On the Vita that appmeta is written by the
package installer (the promoter). VitaSharp's installers (VPK, PKG) are the emulated installer, so they write
it too (`Package/AppMeta.cs`), and `AppMeta.Sync` does it when the shell starts for titles installed before
or copied in by hand (and drops the entries of titles that are gone). When the set changed, `ux0:id.dat` is
removed: the shell then treats the card as one it has not seen and rebuilds its database itself ("Updating
database"). No row is ever written into app.db by VitaSharp.

## Processes

Each process is its own `Emulator` (kernel state, HLE modules, linker, mounts, code cache) and all share one
`VitaSystem` (`Kernel/VitaSystem.cs`):

- **an address space per process** (`Memory/AddressSpace.cs`), as on the Vita: the shell and a game linked at the
  same fixed address (shell.self and non-PIE eboots like Persona 4 Golden's are both at 0x81000000) run side by side.
  Each space is a host reservation of the 4 GB guest window (fastmem) with its page dirty table below guest address 0.
  The regions the Vita shares are one host memory mapped into every space: the kernel region 0x40000000-0x50000000
  (HLE trampolines, objects visible to the guest) and CDRAM 0x60000000-0x70000000 (SceSharedFb, display buffers, GPU
  memory). On Windows the reservation is split with placeholders (`VirtualAlloc2`) and the shared regions are views of
  pagefile-backed `SEC_RESERVE` sections (`MapViewOfFile3`, committed on demand by the same lazy-commit handler); on
  Linux/Android memfd views. macOS has no anonymous memory file (a temporary file's dirty pages would be written to
  disk), so there every process shares the first one's space. User memory is private: a block exists only in the
  space of the process that allocated it. Relocatable allocations still take ranges no space uses, so a guest address
  names one piece of data for the GPU's caches whichever process submitted it; only fixed executable segments
  (`TryAllocAt(exclusive: false)`) share addresses with other processes' blocks. A process's space comes from
  `GuestMemory.CreateSpace` and goes back to a pool when the process ends (reservations are never released while the
  system runs: a straggling host thread may still touch it). Memory blocks and kernel objects record their owner
  (`ProcessScope`) and go away with the process. `VITASHARP_SHARED_ADDRESS_SPACE=1` (or a host that cannot map memory
  twice) puts every process in the first one's space; an executable linked at addresses in use then falls back to the
  old replace-the-shell launch.
- **which space is used**: guest threads run in their process's (the CPU state's `MemBase`: the interpreter, the IL
  backend and the native trampolines address memory through it, so a direct game run costs nothing extra), and their
  host thread works there (`AddressSpace.Current`, read by every `GuestMemory` static accessor and `Ptr<T>`).
  `ProcessScope.Enter(process)` switches the space for host work done for a process (loading its modules on the
  launch task) and the choice flows into the tasks and threads it starts (an `AsyncLocal`). Code that touches another
  process's memory says so with `AddressSpace.Use(space)`: a simple event's setter writing a waiter's results, the
  foreground switch re-presenting a process's frame. Message pipes copy through a kernel-side queue, so each side
  copies in its own space. Native code looks indirect exits up in its process's dispatch table
  (`BlockCache.DispatchTable`, through the state): two processes can have different code at one address.
- **the GPU** reads and writes the space of the command it executes (`GpuCommand.Space`, set at submission);
  results that land later (render-target writebacks, visibility results, deferred notifications) carry their own.
  The texture cache keeps the space id (`AddressSpace.Id`, renewed when a space is reused) of textures read from user
  memory and re-checks one sampled from another space. The dirty-page sweep folds the tables of every space into one
  set of page versions.
- **one kernel object table**: one UID space (process ids and application ids are UIDs too; the first process is
  0x40010003) and the namespace `sceKernelOpen*` searches, so IPC objects cross processes.
- **the GPU**: one queue and renderer; a scene (BeginScene..EndScene) belongs to one process at a time
  (`GpuQueue` arbitration), otherwise the shell's overlay draws landed in the game's render pass.
- **the display and the input** belong to the foreground process: background processes keep flipping
  (their last frame comes back when they are in front) and read neutral controls and no touches.
- `VITASHARP_SINGLE_PROCESS=1` restores the one-process-at-a-time model.

### SceAppMgr for the shell (from the FW 3.74 kernel appmgr)

The kernel module is embedded in `os0:kd/bootimage.skprx` (decrypt with `self2elf`, the embedded relocatable
ELFs follow each other; appmgr is the one at file offset 0x27B048 in 3.74). Learned from it:

- `SceAppMgrAppStatus` is 0x80 bytes: +0x0C name (title id), +0x30 application id, +0x34 process id,
  +0x38 foreground, +0x44 application id again (the shell stores that one), +0x50 title id, +0x71 type byte,
  +0x74 flags. `sceAppMgrLaunchAppByPath4` has 6 arguments; the 6th (0x80 bytes) holds at +0x28 a pointer to
  the new application's status, from which the shell takes its id.
- Shell events (`sceAppMgrReceiveShellEventNum` returns the count, `sceAppMgrReceiveShellEvent` 0x40 bytes,
  ring of 180 in the kernel). Posted by VitaSharp: 2 {pid, appId} when an application process is created,
  0xA {appId | -1} when the launch finished (the shell then activates it), 4 {pid, .., appId at +0x14} when it
  ends, 0x10 {appId} on the PS button (inferred: the handler that opens the running application's LiveArea).
  The shell's other handlers: 3 status change, 6/8/9/0xC-0xF/0x11-0x13 (see the dispatcher at shell
  0x8127E04C).
- The shell drives the rest: `sceAppMgrActivateApp` (application to the front), `sceAppMgrDeactivateApp`
  (application suspended, shell to the front), `sceAppMgrDestroyAppByAppId`, `GetStatusByAppId/ByName/ById`,
  `GetProcessIdByAppIdForShell`, `GetRawPathOfApp0ByAppIdForShell`. A suspended process's threads stop at
  their next HLE call or yield (not while it has a GPU scene open).
- `sceAppMgrLaunchAppByName` starts a background application (the shell starts NPXS10079, the daily checker,
  with the launch parameter "RemoteDownload").

## Status (firmware 3.74)

| Program | Reaches |
|---|---|
| shell.self | boot, "Updating database", lock screen, home screen with the system app bubbles **and the installed games**, LiveArea of a game or system app, Start launches it as a second process; PS button: the game is suspended and the shell's home screen / LiveArea comes back (top bar shows the running game) |
| initialsetup.self (first boot) | launched by the shell with `/CONFIG/SYSTEM/initialize` = 0, runs against the shell's IPC server: peel screen, language, time zone, date, "no account", country, date of birth, "setup completed", parental note; then it ends itself (`sceAppMgrDestroyAppByName(NULL)`) and the shell takes the screen: health warning, home screen with its first-run hint |
| games from the LiveArea | each in its own process and address space: Devious Dungeon boots to its title screen, Sonic Mania and Persona 4 Golden (fixed-address eboot) run, PS / Continue suspend and resume them |
| NPXS10008 Trophy Collection | started from its LiveArea, talks to the shell's trophy server and lists the installed titles with their unlocked trophies (Knightin'+ after it was started once from the shell); "There are no trophies" when nothing is installed |
| NPXS10015 Settings | starts from the home screen as a process, opens the shell's shared framebuffer, loads system_settings_core.suprx, still draws nothing (black; see Remaining) |

### System apps from the home screen (fix/system-apps-2, firmware 3.74, scratch data)

Each started from its bubble, LiveArea Start, then the PS button (scenario: unlock, page swipe, tap; the LiveArea gate
is at 0.5, 0.54 or, for Browser, Party, Calendar and near, at 0.75, 0.52).

| App | Launch / draw | PS button back to the shell |
|---|---|---|
| NPXS10002 PS Store | draws; "An error has occurred (NW-2288-0)" (no network, expected) | yes, LiveArea with Continue |
| NPXS10003 Browser | draws "Please wait..."; then the process crashes (libpaf assertion: a ScePafFileListJobQueue0 lock on an lwmutex returns UNKNOWN_LW_MUTEX_ID); the shell returns to the lock screen | (crashed) |
| NPXS10008 Trophy Collection | draws ("There are no trophies" on a fresh root) | yes |
| NPXS10006 Friends | black (as Settings) | yes |
| NPXS10014 Messages | draws ("Sign up for PlayStation Network") | yes |
| NPXS10001 Party | launches | yes |
| NPXS10013 PS4 Link | launches, never draws: its main thread ScePS4LinkRPMain goes dormant right after its sysmodule loads (libpaf, common_gui_dialog, libsas, which pulled libfios2 and libc in a second time); the process stays in front with no frame | not tested (no frames, so the frame-timed PS press never comes) |
| Parental Controls | the shell's own passcode screen | - |
| NPXS10010 Videos | draws (Connect to Device / All / Rented / Folders) | yes |
| NPXS10009 Music | **fixed** (was the hang): "Rebuilding database", then the menu | yes |
| NPXS10004 Photos | draws the camera (gray preview: no webcam headless) | yes |
| NPXS10072 Email | draws "A memory card that contains an email application database from another PS Vita..." (C2-14818-2) | yes |
| NPXS10091 Calendar | launches (starts NPXS10092 Calendar BG) | yes |
| NPXS10026 Content Manager | exits at once with 0x80020583 (its libipmi client finds no SceMtpService server; the MTP service is not emulated); the shell then shows the lock screen | - |
| NPXS10000 near | launches | yes |
| NPXS10012 PS3 Remote Play | draws the registration guide | yes |
| NPXS10015 Settings | black (see Remaining) | yes |

## Remaining

- (Fixed) Music (NPXS10009) froze the system when started: the shell's SceShellSvc libipmi server asserted (BKPT in
  libshellsvc, "[SceIpmi] INTERNAL ASSERTION FAILURE") and its thread died. A new client makes the server walk its
  sessions; the one of NPXS10079 (daily checker, ends right after boot) had its pipes removed with its process, and the
  server's receive got UNKNOWN_MSG_PIPE_ID. libipmi handles 0x80028224 (pipe deleted) as "client gone". A message pipe
  deleted (by its creator or with its process) while handles opened by name are open now stays until the last close,
  and its operations fail with MPP_DELETED. Every system app started after the daily checker went through this.
- Shareable memory blocks: the shell's libpaf allocates ScePafCRSurfacePoolOffset and ScePafCRSoundDataStorage with
  attr 0x4000 and access flags at opt +0x14; other system programs open them. They are now allocated in the region
  every address space maps (before, the opener read its own empty memory). It did not change Settings, Friends or the
  Browser crash.
- Browser (NPXS10003): crashes after "Please wait..." in ScePafFileListJobQueue0 (libpaf asserts on an lwmutex lock
  returning UNKNOWN_LW_MUTEX_ID). Not investigated further.
- Content Manager (NPXS10026): needs the SceMtpService IPC server (not emulated) and ends; Email's database check
  reports a database "from another PS Vita" on a fresh ux0.

- PS button: default host bindings are the controller's PS / Xbox guide / Home button (SDL "guide"; raw DirectInput
  button 12 on PlayStation pads) and the Home key; it is the "PS button" row of the controller settings. The press is
  taken by the system (applications never see the bit) and the shell gets event 0x10 {application id}: in the FW 3.74
  kernel appmgr that event is posted only by the function behind SceAppMgrForDriver 0x29421DCD / 0xF3B1D42E, which marks
  the application in front as suspending (state 3). Checked: in Devious Dungeon, PS brings the shell back (the game is
  suspended; first time with the home-screen hint), its LiveArea shows "Continue", and Continue resumes the game.
  The gate's thumbnail of a suspended game is `sceAppMgrCaptureFrameBufIFTUByAppId` (the shell clears it when that
  fails); it now copies the application's last framebuffer: Knightin'+ suspended with PS shows its screen on the gate.
  Also checked with per-process address spaces: Persona 4 Golden (non-PIE eboot at the shell's 0x81000000) runs as a second process, PS shows its LiveArea with Continue and
  Continue resumes it; Sonic Mania stays suspended for 35 s in its LiveArea (the shell closes nothing by itself: no
  timer, no memory check) and Continue resumes it. The shell ends a suspended application only through
  `sceAppMgrDestroyAppByAppId`: closing its LiveArea page (the peel at the top right) or starting another game.
- (Fixed) "Could not install. An error has occurred (C0-11274-5)" with a blank icon after unlocking: C0-11274-5 is
  `error_table.bin` entry 9274, 0x80AF5022, from the shell's game card promoter (`SceGameGcPromoteJobQueue`).
  `sceIoGetstat("gro0:")` succeeded because gro0/grw0 were always mounted on empty host folders, so the shell saw a
  card, `sceIoDopen("gro0:app")` failed and the promotion was shown as a failed install. gro0/grw0 are now mounted only
  when `<fs>/gro0/app` holds a card dump (an empty slot otherwise). NPXS10079 (daily checker) is started twice at boot
  and each run reads its launch parameter, polls `vshAppMgrGetPfsProcessStatus` (0: nothing pending) and exits
  normally; it is not a relaunch loop.
- Shell overlay over a system app: Trophy Collection shows without the shell's status bar on top. The frames do go
  through the shell (SceSharedFb below); whether the shell is meant to draw its status bar there, and why it does not,
  is not checked.
- Settings (NPXS10015): still black. Checked: its libpaf is a SharedFb client and every frame's
  `sceSharedFbBegin`/`End` pair returns 0 (the shell lets it draw each frame); its threads are idle, nothing waits or
  crashed (SceSettingsMain in the paf main loop, ScePafJobDefault on its job semaphore). Startup order (traced): the
  framework loads common_resource.rco, the SceSettingsModuleLoadJob thread loads ini_file_processor, bXCe,
  common_gui_dialog, sqlite, dbutil and `system_settings_core.suprx`, then paf opens `system_settings_main.rco` and runs
  the plugin's init callback (eboot 0x810034C6: `sceAppMgrGetStatusByName(NULL)` finds itself, creates the
  "SceSettingsSharedInfo" event flag). The next step, the plugin start callback (0x810035D8: creates the core module's
  page object, imports 2F3D38BD / 07036707 from SceSystemSettingsCore), leaves no trace: no other plugin rco
  (system_settings_plugin.rco...) is ever opened. Next: break on 0x810035D8 and follow the core module's page creation;
  `Log.WarnOnce` keys unimplemented NIDs per system, so an unimplemented call the shell made first is not reported for
  Settings (trace with `--trace-hle-threads=SceSettings`).
- `install-firmware PSVUPDAT.PUP` does write `os0:kd/registry.db0` (checked on main fb661f7: os0/kd has the same 48
  files as the user's install). Without it the registry has only the keys VitaSharp knows, `/CONFIG/NP/account_id` is
  missing and the shell's first initialisation stops before its NP services and the trophy server: a scratch root
  missing it was not made by the installer.
- Still stubs: SceMotionDev (calibration data and device info of the sensor chip), SceNetPs syscalls, the unnamed
  SceNetCtl / SceNpCommon / SceNpManager / SceLibMtp / SceHttp NIDs, `_sceNpDrmGetFixedRifName`, registry change
  callbacks (recorded, never notified). Implemented from the FW 3.74 kernel (bootimage appmgr, vshbridge, SceError,
  sysmem's sysroot, the registry manager): `sceAppMgrGetAppMgrState` (the 0x80-byte state, +8 = -1),
  `sceAppMgrGetCurrentBgmState2`, `sceAppMgrGetAppIdByAppId` (pseudo ids -2 self, -4 foreground),
  `sceAppMgrDestroyAppByName`, `vshKernelCheckModelCapability` (capabilities 2, 7, 10), `vshIdStorageLookup`,
  `sceErrorGetExternalString` (os0:kd/error_table.bin), `sceRegMgrSrvGetRegionStr` ("UC2").
- (Fixed) The IME plugin's job threads (SceImeSetupJobQueue, SceImePredictionJobQueue) crashed inside libpaf's resource
  loader (ScePaf+0x6CCF4 calling through a freed file object, or executing paf's 0xAF free fill): both reopen
  ime_plugin.rco through the same paf Resource and swap its file shared_ptr without a lock. On the Vita the shell runs
  on the 4th core, the one the system keeps, so its threads never run in parallel; VitaSharp ran them on the three
  application cores. `Scheduler` now gives the shell process its own single core (3 of 4 traced runs crashed before,
  0 of 4 after; `--cores=1` showed the same).
- (Fixed) Shell text cut at the top and overlapping lines: libpaf sizes its line textures from scePvfGetFontInfo
  (ScePaf+0x19B64C) after `scePvfSetEM(lib, 1 / textSize)`. The real libpvf's GetFontInfo ignores the character size and
  scales the face's design metrics by 64 / (unitsPerEm x EM); the HLE used the current character size instead (the
  10.125 pt default = 18 px before any layout, hence the 18-px line textures; whatever size was set last afterwards,
  which is why some runs looked right). libpvf is HLE for system programs too (it has an HLE), so this was not an LLE
  difference.
- pd0 is not needed: it is factory data (Welcome Park, pd0:data/systembgm) that no system update carries. With
  `install-firmware PSVUPDAT.PUP` (os0 + vs0) plus sa0 fonts the shell boots to the home screen; install-firmware says
  when sa0 or pd0 are missing.
- With the old single-process fallback, the switch away from the shell still waits 10 s for one shell thread.

## The trophy server (shell.self)

The trophy service of the Vita is a libipmi server inside the shell: `SceNpTrophyServer` (shell.self 0x81226C10,
started by 0x81226DD6 from the shell's first initialisation at 0x810042AE, on ScePafJobDefault), server name
"SceNpTrophy" (pipe `SceNpTrophyServerMP`), with its job thread `SceNpTrophyServerJob`. Its clients are the
firmware's `np_trophy.suprx` (applications), the trophy setup dialog (`libcdlg_np_trophy_setup.suprx`, the install)
and Trophy Collection (its own copy of libipmi in eboot.bin). It is the real server that runs in VitaSharp; the
protocol is the firmware's on both ends. That initialisation starts the system's services one after another and
stops at the first failure: it needed SceNpCommonPs4 (np_common_ps4.suprx, now pulled in as the shell's dependency)
and SceNpCommon's heap (A385C8CD/BA6310A1 and the allocator exports, HLE).

Storage (what the server reads and writes): `ur0:user/00/trophy/data/sce_trop/TRPUSER.DAT` (mount 302, created on
first use), per title `ur0:user/00/trophy/data/<NPCOMMID>/TRPTITLE.DAT, TRPTRANS.DAT` (mount 303),
`ur0:user/00/trophy/conf/<NPCOMMID>/` (TROP.SFM, ICON0.PNG, TRPPARAM.INI) and the trophy icons in
`ux0:user/00/trophy/conf/<NPCOMMID>/`; Trophy Collection keeps `ur0:user/00/trophy/db/trophy_local.db` (sqlite).
A set is installed from `trophy_sys0:sce_sys/trophy/<NPCOMMID>/TROPHY.TRP` (mount 300 = the application of the
client process).

Applications keep the HLE SceNpTrophy (VitaSharp's store `ux0:user/00/trophy/<NPCOMMID>.json`, notifications,
launcher). With the shell running, `Hle/Np/TrophyServerBridge.cs` loads np_trophy.suprx into the application's
process (the application's own imports stay on the HLE) and installs the set the way the setup dialog does, then
gives the server the trophies already unlocked in VitaSharp's store and every new unlock. So, as on the Vita, a title
appears in Trophy Collection once it has been started from the shell. Fixed-address executables (WRC 4, Killzone)
replace the shell instead of running beside it, so they are not installed this way.

### Importing trophies earned elsewhere

`VitaSharp.Desktop --data=<root> import-trophies <folder>|--vita3k` (launcher: Configurações > Geral > Troféus),
`Hle/Np/TrophyImport.cs`:

- **A PS Vita's `ur0:user/00/trophy`**: the folder (or a dump with `ur0/` and `ux0/` side by side) replaces
  `<fs>/ur0/user/00/trophy` (the old one is moved to `trophy.backup-<date>`); the shell's server and Trophy Collection
  then read the console's own data. Only a *decrypted* copy works. On the console each `data/<NPCOMMID>` folder is a
  protected file system folder (`sce_pfs/`) whose files are sealed with keys unique to that console (derived inside its
  security processor); a copy taken with a file manager (VitaShell over USB/FTP) holds the encrypted bytes, and no
  firmware key decrypts them on a PC. The importer recognises that case (a `sce_pfs` folder, or a TRPUSER.DAT that does
  not start with the server's plain header 7F 15 99 36) and refuses it with that explanation. What works: a copy made on
  the console through the system's own mounts (a homebrew that reads the folders with sceAppMgrTrophyMount 302/303 and
  writes the plain files out). Not verified with a real console's copy: whether the server also checks the console's
  account id inside TRPTITLE.DAT (then `/CONFIG/NP/account_id` must be the console's).
- **Vita3K's trophies** (`ux0/user/00/trophy/data/<NPCOMMID>/TROPUSR.DAT`, Vita3K's own format: magic 0x12D5819A,
  unlock bits at +4, Unix times at +0x70) are merged into VitaSharp's store (union, earliest time kept). Vita3K's folder
  is only read. The real Trophy Collection shows such a title once the game has been started from the shell (the
  bridge then gives the server every trophy of the store).
- Not done: the server's files to VitaSharp's store (the TRPTITLE.DAT layout is not mapped), and giving the server the
  titles of the store without starting each game once from the shell (that needs a client process with the game's
  TROPHY.TRP mounted as trophy_sys0).

## SceSharedFb (from the FW 3.74 kernel appmgr)

The shell's libpaf creates framebuffer 1 (`sceSharedFbCreate`) and draws its frames there; the libpaf of every other
system program opens it (`_sceSharedFbOpen(1)`) and is one of its 16 client slots (pid). Each shell frame:
`sceSharedFbUpdateProcessBegin(uid, info[0x40], &count)` gives the kernel the shell's render target (buffer, stride,
size, format, its VSH sync object) and returns how many clients render this frame; for each,
`sceSharedFbUpdateProcess()` sets the client's event flag bit and waits (20 s) for its End; the client's
`sceSharedFbBegin` waits for that bit (160 ms at a time, giving up with 0x80200008 when it is not rendered this frame)
and gets the shell's target in SceSharedFbInfo +8..+0x47 (+8 the buffer to draw); then the shell draws its own layers
into the same buffer and shows it. `sceSharedFbGetRenderingInfo(pid)` is "the pid is a client". VitaSharp renders the
application in front when it is a client; a system program run without the shell gets its own buffer and End shows it.
