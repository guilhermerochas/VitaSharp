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
- **HLE, the kernel side:** SceSharedFb (the shell's two 960x544 framebuffers; End shows the finished
  buffer), SceGxmInternalForVsh (VSH initialise, display sync objects shared by key), SceRegMgr /
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

- **one guest address space**: executables are relocatable (ET_SCE_RELEXEC) and the allocator hands each
  process free ranges, so the shell (from 0x81000000) and an application live side by side. Memory blocks and
  kernel objects record their owner (`ProcessScope`) and go away with the process. An executable linked at fixed
  addresses that are taken falls back to the old replace-the-shell launch.
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
| games from the LiveArea | Devious Dungeon boots to its title screen inside the shell session |
| NPXS10008 Trophy Collection | renders its background through libpaf; no trophy list yet |
| NPXS10015 Settings | starts from the home screen as a process, presents frames, still draws nothing |

## Remaining

- Resuming the game from its LiveArea gate ("Continue") was not exercised in a test yet (ActivateApp resumes).
- The "Could not install" dialog and the "!" notification after unlocking remain: not the background download
  launch. NPXS10079 (daily checker) is started twice at boot and each run reads its launch parameter, polls
  `vshAppMgrGetPfsProcessStatus` (0: nothing pending) and exits normally; it is not a relaunch loop. Lead: the
  background download queue the shell writes in `ux0:bgdl/w/patch_bg.bin` (`_vshAppMgrBgdlSetQueueStatus` takes it).
- Settings and Trophy Collection draw nothing: system apps render into the shell's shared framebuffer and the
  shell composites them (`sceSharedFbUpdateProcess*`); here each process has its own SceSharedFb buffers.
- Still stubs: SceMotionDev (calibration data and device info of the sensor chip), SceNetPs syscalls, the unnamed
  SceNetCtl / SceNpCommon / SceNpManager / SceLibMtp / SceHttp NIDs, `_sceNpDrmGetFixedRifName`, registry change
  callbacks (recorded, never notified). Implemented from the FW 3.74 kernel (bootimage appmgr, vshbridge, SceError,
  sysmem's sysroot, the registry manager): `sceAppMgrGetAppMgrState` (the 0x80-byte state, +8 = -1),
  `sceAppMgrGetCurrentBgmState2`, `sceAppMgrGetAppIdByAppId` (pseudo ids -2 self, -4 foreground),
  `sceAppMgrDestroyAppByName`, `vshKernelCheckModelCapability` (capabilities 2, 7, 10), `vshIdStorageLookup`,
  `sceErrorGetExternalString` (os0:kd/error_table.bin), `sceRegMgrSrvGetRegionStr` ("UC2").
- The IME plugin's job threads (SceImeSetupJobQueue, SceImePredictionJobQueue) sometimes crash (jump to 0, or a BKPT
  after unlocking a lightweight mutex it does not own) inside libpaf's resource loader: both load from
  vs0:vsh/shell/ime_plugin.rco through the same paf Resource and each reopens the RCO; the file object one thread
  holds is freed under the other (shared_ptr refcount reaching 0 in ScePaf+0x6D3B4). It is intermittent (2 of 4 user
  sessions, 1 of ~10 test runs), only the IME (on-screen keyboard) is lost, and the shell keeps running.
- Shell text is placed too high in its line textures: the tops of glyphs are cut (lock screen clock, date, status
  bar) and the health warning's lines overlap. Same with libpvf on LLE or HLE (`--hle-modules=libpvf`), with or
  without pd0, and no texture write is missed (VITASHARP_TEXDIRTY=verify), so it is in libpaf's text layout inputs;
  libpaf takes the line height from scePvfGetFontInfo's ascender/descender (FontInfo +8/+0xC, 26.6) before it sets the
  character size of the text it lays out (ScePaf+0x19B64C). It depends on how the shell started: on a direct boot the
  text is cut; when the shell first ran the first-boot setup (same registry afterwards), its health warning and home
  screen text come out right. Not the registry, not the model capabilities.
- pd0 is not needed: it is factory data (Welcome Park, pd0:data/systembgm) that no system update carries. With
  `install-firmware PSVUPDAT.PUP` (os0 + vs0) plus sa0 fonts the shell boots to the home screen; install-firmware says
  when sa0 or pd0 are missing.
- With the old single-process fallback, the switch away from the shell still waits 10 s for one shell thread.
