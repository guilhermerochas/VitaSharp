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
In the launcher, the system app bubbles of the Vita-style home screen now start the app (same `run` path
as a game); the launcher comes back when it exits.

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
  `SceShellSvcServerMP`, `...RQM00`, semaphores, event flags), so it needs no HLE: it works as soon as the
  server and the client run in the same kernel. Today the app's ShellSvc client finds no server (only one
  process runs at a time) and logs `IPC Server is not available`.
- **HLE, the kernel side:** SceSharedFb (the shell's two 960x544 framebuffers; End shows the finished
  buffer), SceGxmInternalForVsh (VSH initialise, display sync objects shared by key), SceRegMgr /
  SceRegMgrForSDK / SceRegMgrService, sceSblUsGetSpkgInfo, sceAppMgrLaunchAppByPath4, a few process and
  power queries (`SystemSoftware.cs`).
- **The registry** (`Firmware/Registry.cs`) has every key of the firmware's template `os0:kd/registry.db0`
  (1331 keys in 3.74, with type, size and default) plus the written values in
  `vd0:registry/vitasharp-registry.txt`, shared by all processes. Language, enter button and date/time
  format follow the emulator options until the system writes them.

## Status (firmware 3.74)

| Program | Reaches |
|---|---|
| shell.self | health warning, SONY logo, "Updating database" (app.db created), lock screen, home screen with the system app bubbles and page dots, LiveArea of a system app, its Start gate launches the app |
| NPXS10008 Trophy Collection | renders its background through libpaf (alone or launched from the shell); no trophy list yet |
| NPXS10015 Settings | loads libpaf, system_settings_core and its RCOs, presents frames, draws nothing yet (no page) |

Screens: `shell_boot_sony`, `shell_updating_database`, `shell_lock_screen`, `shell_home_screen`,
`shell_trophy_livearea_gate`, `trophy_first_frame` (agent scratchpad, not in the repository).

### Process model (trade-off)

VitaSharp keeps **one guest process at a time** (the processes would overlap in the single guest address
space: the shell and the apps all link at 0x81000000). `sceAppMgrLaunchAppByPath4` suspends the caller
(`Emulator.SuspendedPrograms`) and replaces it with the application; `sceKernelExitProcess` brings the
shell back, which boots again (health warning, lock screen) instead of resuming. Background launches
(`sceAppMgrLaunchAppByName`, e.g. NPXS10079 RemoteDownload at boot) are only logged.

Real multi-process needs: several `Emulator` process contexts (kernel, linker, HLE state) sharing one
`GuestMemory` with relocatable executables placed apart (or one host address space per process), a
system-wide kernel object namespace so named objects (`sceKernelOpenMsgPipe`...) cross processes, one UID
space, and a compositor for the shared framebuffer plus the game's own framebuffer
(`sceSharedFbUpdateProcess*`, `sceSharedFbGetShellRenderPort`, `sceAppMgrSetDisplayMergeConf`).

## Remaining (what the logs show)

- **Installed games are not on the home screen yet.** The shell's database rebuild only registers the
  system apps; ux0:app titles reach app.db through the promoter (ScePromoterUtil -> shell IPC) or the
  memory-card mount handling (`vshIoCreateMountEvent`, `ux0:id.dat`), neither emulated. Launching a game
  through `sceAppMgrLaunchAppByPath4("ux0:app/<id>/eboot.bin")` already works once it is listed.
- **First boot**: with the factory registry (`/CONFIG/SYSTEM/initialize` = 0) the shell launches
  `vs0:vsh/initialsetup/initialsetup.self` (NPXS10062), which now starts as the next process but needs the
  shell's IPC server; for the runs above the key was set to 1 (`registry /CONFIG/SYSTEM/initialize=1`).
- A notification "!" stays in the corner and a "Could not install" dialog appears after unlocking
  (the NPXS10079 background download that cannot start).
- Unimplemented calls still logged by the shell (stubs answering 0): SceVshBridge (~25 used: power, rtc,
  syscon, Aimgr model checks, IdStorage), SceAppMgr shell-side queries (`GetAppMgrState`,
  `ReceiveShellEventNum`, `ReceiveNotificationRequestForShell`, `GetStatusByAppId`), SceAVConfig,
  SceSblQafMgr, SceSblPmMgr, SceBt, SceUsbd, SceMotionDev, SceNetPs syscalls, SceErrorUser history,
  `sceKernelRegisterProcessTerminationCallback`, `sceRegMgr*` callbacks are recorded but never notified.
- Settings (NPXS10015) shows no page; Trophy Collection no list (both likely wait for shell IPC or NP
  trophy internals).
- `imports vs0:vsh/shell shell.self` (all shell plugins counted): 4130 imports, of which the firmware
  libraries now cover the ScePaf*, SceIpmi, SceShellSvc/Util, SceDbutil, SceSqliteVsh, ScebXCe,
  SceIniFileProcessor, SceCommonGuiDialog, SceLiveAreaUtil and ScePromoterUtil groups.
