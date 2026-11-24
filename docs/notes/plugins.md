# Plugins (kernel `.skprx` and user `.suprx`)

*Português: [plugins.pt-BR.md](plugins.pt-BR.md).*

Plugins are an emulator option, **Enable Kernel Plugins**, off by default (the Vita has no such setting, like pausing or
recording the screen). Off, the pure firmware runs and no plugin loads. On, VitaSharp loads the plugins a
homebrew-enabled Vita loads, from the same plugin configuration, runs their original code and offers them the plugin
framework libraries they import. Nothing in the emulator is written for a particular plugin or homebrew.

Turn it on in Settings > Advanced (Android: Settings > Debugging), with --kernel-plugins=on on the command line or
VITASHARP_KERNEL_PLUGINS=1; the launcher's and the app's switch win over the variable.

## Setting it up

Copy the plugins from your own Vita into the data folder (`ur0:tai` is `fs/ur0/tai` in it) and list them in
`fs/ur0/tai/config.txt` (or `fs/ux0/tai/config.txt`, which wins when both exist), the file the homebrew environment reads on the Vita:

```
# kernel plugins: loaded into the kernel and started when a program starts
*KERNEL
ur0:tai/some_kernel_plugin.skprx
# user plugins for every program
*ALL
# user plugins for one title
*PCSE00000
ux0:tai/some_plugin.suprx
```

Without a `config.txt`, or with the option off, no plugin is loaded.

The plugin configuration goes in the emulator's own folder, `ur0:kernel_plugins/` next to `ur0:tai` (on the host
`fs/ur0/kernel_plugins/`; Windows, Linux, macOS and Android alike): `ur0:kernel_plugins/config.txt`, read with the same
rules. Its lines are Vita paths (`ur0:tai/kubridge.skprx`): the plugins stay in `ur0:tai` / `ux0:tai`, where homebrew
checks their files; a line without a device (`my.skprx`) names a file of `ur0:kernel_plugins/` (a path relative to the
configuration); a file that is not there is skipped with a warning in the log. Without `ur0:kernel_plugins/config.txt`
the Vita's configuration above is read. Only the emulator's loader reads the configuration. The first time the option is
on and the folder has no configuration, the Vita's `config.txt` moves into `ur0:kernel_plugins/` unchanged (a copy kept
as `ur0:kernel_plugins/config.txt.bak`; the log says so); a plugin an earlier version had moved into the folder goes back
to the Vita path the `.bak` names for it. The folder of earlier versions, `kernel_plugins/` next to `fs/` in the data
folder, moves once into `ur0:kernel_plugins/` (each entry not already there; the emptied old folder is deleted). The
plugins, the plugin framework's own file and the plugins' data files are never moved. Nothing else is deleted.

The file is read with the rules of the homebrew environment's own parser and plugin loader: only one
file is used (`ux0:tai/config.txt`, else `ur0:tai/config.txt`); `#` starts a comment, `*` a section, anything else is
a module path (leading and trailing spaces dropped); section names compare exactly, upper and lower case included
(`*main` is the shell, other names are title IDs); `*ALL` is for every program but not for `*KERNEL`; a section
written `*!NAME` that matches ends the reading at the next section; a line of 256 characters or more, a path before
the first section or a section without a name makes the plugin loader refuse the whole file, and then no plugin loads. The
modules of a section load in file order, and one that fails does not stop the others.

## What happens

- `*KERNEL` modules load into the kernel region (an area every process maps, owned by the kernel process) and their
  `module_start` runs at once on a kernel thread; a module whose start does not report success is unloaded again, as
  the firmware's module manager does. The log says what happened to each one (`kernel plugin ...: loaded and started`,
  `... module_start of X returned 0x2, unloaded`).
- `*KERNEL` modules load once, into the kernel process: the one kernel of the system, made at boot (with the shell, or with
  the first game when no shell runs), as the plugin loader loads them at boot on the Vita. They load at the system's boot,
  before the first program's launch starts and whatever the title is, so a launch that then fails (an app0 that does not
  mount, a PSM title without its runtime) still has them loaded; the log says `kernel plugins: N of M loaded and started
  at system boot, before any application is launched`, and the recordings' plugin lines end with `system boot` (a user
  module with `with process TITLEID`). Every process sees them: the
  shell, an application the shell opens next to it, the program a LoadExec starts. They stay until the system ends; a
  kernel module a program loads (the kernel module manager) is the kernel's too and outlives that program.
- Kernel code's process event handlers (`ksceKernelRegisterProcEventHandler`) are called as the firmware's process
  manager calls them, in registration order: create when a process's main thread exists and before it starts (a create
  handler that fails refuses the process, and the handlers called before it get exit), exit when a process ends by
  itself, kill when it is ended (closed by the shell, a crash); stop when it is suspended (types 0x200 and 0x800 before
  its threads stop, 0x1000 after) and start when it resumes (0x20000, then 0x40000); an application also sends the switch
  event 0x11 when it is suspended and 0x10 when it resumes.
- `*ALL` and `*TITLEID` modules load into every program's process, the system's own too (the plugin loader loads them after
  the process's preloaded modules), and start before its main thread. The
  section is the process's kernel title ID: `main` for the shell, the NPXS ID for a system application.
- A module that imports a library nothing provides (no loaded module and no firmware library VitaSharp implements) is
  not started; the log names the libraries (`not started: no module provides ...`).
- Kernel modules call the firmware's kernel libraries (SceSysmemForDriver, SceThreadmgrForDriver,
  SceModulemgrForKernel, SceCpuForDriver, ...): VitaSharp implements them with the firmware's behaviour and error codes.
  User programs reach a kernel module only through its syscall-exported libraries; kernel modules never link against
  user code.
- The firmware's own kernel modules (every `os0:kd/*.skprx` and the modules inside `bootimage.skprx`) are in the
  kernel process's module list before the first other kernel module loads, with their names, module NIDs and export
  and import tables (`ksceKernelGetModuleList`, `ksceKernelGetModuleCB`). A kernel module's imports bind to their
  export entries, which are real ARM code (an `svc` into the HLE function), so code a kernel module writes over an
  entry runs for the kernel callers that go through it. User programs keep reaching the syscall exports through their
  HLE stubs. This needs the firmware installed (`os0` from PSVUPDAT.PUP); without it no firmware module is listed.
- The emulated CPU has no privilege levels, so the permission check of the user/kernel copies is done by the kernel
  library: a user range must be the caller's own memory, not kernel memory.

## The plugin framework libraries

On the Vita the homebrew environment's framework module is loaded first and provides the plugin loader and the libraries
other plugins and homebrew import. With the option on, VitaSharp provides those libraries itself (they are declared in
the NID table only): module information by name or module NID (the 0x38-byte record, newest module first), a module's
segment offsets, its export and import entries, data injections (the original bytes kept and written back on release)
and the framework's error codes (0x9001xxxx). A listed module that exports those libraries (the user's copy of the
framework) is not loaded: the log says `not loaded (the emulator provides its libraries)`. With the option off the
functions do nothing, as imports nothing provides.

Function hooks (export, import, offset and absolute address; kernel and user) follow the framework's published
contract: the hooked function's first instructions become a jump to the first hook; each hook gets a reference record
(next, func, old) and continues the call as the framework's continuation macro does (the next hook's function, or,
after the last one, a trampoline that runs the replaced instructions and the rest of the original). Hooks on one
function are linked as the framework links them: the first stays the one the function jumps to and each new one goes
right after it (so they run first, newest, ..., second; the framework leaves the order undefined). Releasing a hook
keeps the others; releasing the last writes the original bytes back. Every patch drops only the translated code it
covers, in every process. A hook works the same on a guest (LLE) function and on a firmware function the HLE serves:
a kernel hook on a firmware export also takes, in every process (those made later too), the import stubs of that
library's function, so a hook on a syscall export sees the user programs' calls; the kernel library's function with
the same NID (a kernel twin) is another function. The hook runs on the calling thread with the caller's process as
the current one, as kernel code a syscall runs does (ksceKernelGetProcessId gives the user process). A kernel
plugin's hook or data injection in a user process is made in that process's memory, and the patches of a process go
when it ends.

### Is a hook used? (the log)

Every hook says in the log whether calls reach it, for any plugin and any kind of hook (export, import, offset,
absolute; kernel and user; on guest code and on a function the HLE serves, the calls of user programs through a syscall
export included):

- installing it: `plugin hook N on 0xADDR (<function> of library 0xLIB; export hook, kernel, 1 on the address): hook
  function 0x... (pid 0x..., by <module>)`; a hook the framework refuses: `plugin hook N refused (0x9001xxxx): ...`;
- the first call that enters it: `plugin hook N called: <function> of library 0xLIB from pid 0x... (thread <name>) ->
  hook function 0x...`;
- the first time it continues the call: `plugin hook N continued to the original: ...` (its record's old) or
  `plugin hook N continued to the next hook (plugin hook M): ...` (the next hook's function);
- at the end of the session, after the PERF lines (the desktop's log and the Android app's): `Plugin hooks: I installed,
  C called, U never called, R refused` and one line per hook with the module that installed it, its calls (and the
  first caller), its continuations to the original and to the next hook, `never called` for a hook no call entered,
  `released` for one the plugin released.

N numbers the hooks of the session in installation order. The count is guest code: the record's func and old are a small
ARM stub of the hook (in kernel memory) that adds one to the hook's counter (ldrex/strex, every core) and jumps on to
the hook function or to the trampoline, keeping the caller's registers and stack, so it holds in every CPU backend
(interpreter, IL and native JIT, precompiled code) and on every path into the hook. Only the call that takes a counter
from 0 to 1, and the traced calls below, enter C# for their log lines; after that a call costs a few instructions and allocates nothing. A save state
keeps the counters (they are guest memory); a state saved by an older build gets the stubs when it loads.

What a hook does, in its first calls (8 by default; `VITASHARP_PLUGIN_HOOK_TRACE=N` sets how many, 0 none, at most
256): one line per call, `plugin hook N call K: r0=... r1=... r2=... r3=... stack=...; continued to the original, which
returned 0x...; returned 0x... to the caller (changed); wrote r1 0x... bytes 0-3 (now ...)`. It says whether the hook
continued (to the original or to the next hook, with the value that came back) or `answered itself`, what the caller
got (`(changed)` when that is not what the original or the next hook returned), and which of the 32 bytes each argument
points to changed during the call. The stub keeps the caller's lr in a word of its own and gives the hook the stub's
return word, which puts it back: the guest's registers and stack stay as they were, and a call in flight survives a
save state. The summary adds, per hook, the calls that answered themselves and, for the traced calls, the values
returned (top 5), how many were changed and how many wrote an output. After the traced calls a hook costs only its
counters.

A recording (F8) keeps the hooks too: its context.txt lists each hook with its counters at the start and at the stop
(one tab-separated `plugin hook:` line each), its log.txt has the `called` lines, and report.txt has a "Plugin hooks"
section with each hook's kind, function, process, address, installer, hook function, calls and continuations during the
recording and in the whole session, and `never called` or the error of a refused hook. The analysis reads only the
recording, so whoever looks at it needs neither the game nor the plugins. It also has a "Plugins" section: the
emulator's build, the installed firmware, the plugin configuration that was read (its Vita path and lines), and each
plugin module of it (Vita path, kernel or user, module name, SHA-256 and size of its file, what happened to it, the
imported functions nothing provides). Only Vita paths go in it; a host path in a message becomes `<data>` or
`%USERPROFILE%`, and no plugin file is copied.

The framework's module calls for user programs (taiLoadKernelModule, taiLoadStartKernelModuleForUser,
taiStartKernelModuleForUser, taiLoadStartModuleForPidForUser, the four stop calls, taiUnloadKernelModule,
taiUnloadModuleForPid, taiMemcpyUserToKernel and taiMemcpyKernelToUser) follow its published contract: only a program
with the system class may call them (ksceSblACMgrIsShell: capability bit 1, which homebrew built as an unsafe fake SELF
has and safe homebrew and retail games do not; otherwise 0x90010009); the arguments come in the 0x14-byte record (size,
pid, args, argp, flags), up to 256 bytes of argp are copied to a kernel buffer that module_start/module_stop receive,
the path is at most 255 characters and opt must be NULL, with the framework's error codes for each check. The module
loads through the firmware's kernel module manager, the loader of the `*KERNEL` plugins: a kernel module goes to the
kernel and outlives the program that loaded it; the stop calls write module_stop's status to `*res`. The UID returned
is the module's own (VitaSharp has one UID space for the kernel and the processes). This is how a homebrew program
loads its own kernel module (Adrenaline's eboot does it first). taiReloadConfig, taiReloadConfigForKernel and
taiLoadPluginsForTitleForKernel are not offered yet.

The firmware's user modules the HLE serves (the os0:us modules every process has preloaded, such as SceLibKernel, and
the vs0 modules a process loads) are in the process's module list as the framework sees it: the first time the
framework looks at a process's modules, each firmware user module exporting a library the process imports, that no
loaded module of the process exports, is listed with the firmware module's name, module NID and export tables, at a
fixed address of the process's memory, each exported function's entry an HLE stub. They come before the loaded modules
(they were preloaded), so the newest module is still the program's. A user plugin's export hook on one patches its
entry and takes that process's import stubs of the function (those bound later too); other processes are not
affected, as on the Vita a user module's code is its process's. These modules are not linked: imports keep binding to
the HLE as before. They need the firmware installed (os0 and vs0 from PSVUPDAT.PUP); without it the search finds no
such module (0x90010002), as before.

The framework keeps no list of patches by module: a plugin releases its hooks and injections itself, normally in its
module_stop, in any order, while the patched code is still in place; a plugin that stops and unloads without
releasing them leaves them installed (as the framework's published source does; a hook whose code went with the
module then jumps to freed memory, as on the Vita). Only when a process ends are its patches dropped, without writing
the original bytes back (the code went with the process); kernel patches stay until they are released.

| Mechanism | Status |
| --- | --- |
| Export hook, kernel (kernel modules, the firmware's included) | Done |
| Export hook, user, on a loaded user module | Done |
| Export hook, user, on a firmware user module the HLE serves | Done (listed in the process's module list; needs the firmware) |
| Import hook (kernel and user) | Done |
| Offset and absolute-address hooks (kernel and user, ARM and Thumb) | Done |
| Hook chain with continuation (next/func/old), in the framework's order | Done |
| Release in the middle, at the head, of the last one | Done |
| Data injection (absolute and by segment offset), restored on release | Done |
| Kernel hook on a firmware syscall export: every process's calls, those made later too | Done |
| A process ends: its patches are dropped, nothing written back | Done |
| A plugin stops and unloads: its module_stop releases its patches; nothing is released for it | Done (as the framework) |
| Translated code: each patch drops the blocks it covers; a block decoded during a patch is checked again | Done |
| Module calls for user programs (load, start, stop, unload a kernel or process module; user/kernel copies) | Done |
| Patching the firmware's own code by offset | Not possible (no firmware code: the HLE serves it) |
| A firmware function's own internal callers (the HLE calling it) go through a hook on its entry | Yes: every internal call of an exported function goes through its generated `XxxThroughEntry` wrapper (see below) |

### Validation matrix

Each cell is a test that warms the caller's code on the path, installs the hook, checks the hook runs with the
caller's arguments, its continuation reaches the original and its result reaches the caller, releases it and checks
the original bytes and result are back (`PluginHookMatrixTests`, `PluginHookMatrixProcessTests`); the chain order is
fuzzed against a model of the framework (`PluginHookChainFuzzTests`, fixed seeds, 300 steps on the interpreter, 40
with compiled code). Paths: the interpreter, the IL backend, the host's native backend (x64 on x64; ARM64 on ARM64
hosts, the macOS runners and Android, the same core) and code translated ahead (the code cache's precompilation:
translated before it ever ran, then hooked).

| Hook | Interpreter | IL | Native | Precompiled |
| --- | --- | --- | --- | --- |
| Kernel export, A32 and Thumb | ok | ok | ok | ok |
| Kernel import (stub) | ok | ok | ok | ok |
| Kernel offset (Thumb), absolute (A32) | ok | ok | ok | ok |
| Kernel export of a kernel module's syscall library, user caller | ok | ok | ok | ok |
| Kernel export of a firmware syscall the HLE serves, user caller (sees the calling process) | ok | ok | ok | ok |
| User export, A32 and Thumb; user import; user offset (A32 and Thumb) | ok | ok | ok | ok |
| User export of a firmware user module the HLE serves (shell) | ok | ok | ok | ok |
| Kernel hooks seen by an application opened after them, on its threads | ok | ok | ok | ok |
| Save state with the hooks: loads them active; one from before loads without | ok | ok | ok | ok |
| Chain fuzz (kernel and user, 3 seeds each) | ok | ok | ok | ok |
| Firmware-internal caller (the HLE's own call of the hooked function) | ok (`HleEntryCallTests`, `HleEntryCallLibraryTests`, `IoVfsMountTests.AHookOnTheVopLookupEntrySeesTheModulesOwnPathWalks`) | same | same | same |

The ARM64 sandbox (Unicorn) runs single translated blocks, not the dispatcher, so it is not a path of the matrix; the
matrix runs on ARM64 natively where the host is ARM64. Loading a state with `VITASHARP_LOAD_KEEP_CODE=1` (translations
kept and revalidated by their bytes) is read once per process and is not a cell (the default load drops every
translation).

### The firmware's own calls of a hooked function

On the Vita a hook on an export patches the function's code, so every caller runs it, the firmware module's own
calls included. The firmware functions the HLE serves are C# methods; when one calls another exported function it calls
the generated wrapper `XxxThroughEntry` (source generator `HleEntryCallGenerator`, made for every `[Hle]` method some code
calls that way). Without a hook the wrapper reads three static fields (how many processes of the host have a user
hook, and whether the firmware kernel modules of any emulated system have a patched entry, counted again only when
guest code changes) and calls the C# method, allocating nothing (`HleEntryCallBenchmarks`, `HleEntryCallTests`). With a hook it finds it where the framework put it: the patched
entry of the function in the firmware kernel modules (a kernel plugin's hook, or any code written over the entry) or the
thunk of a user plugin's hook on the function in the module's process (found by library and NID: a NID two libraries
export is two functions, and a hook on one is not the other's); the hooked code runs on the calling thread with
the arguments as AAPCS words (64-bit values in an even register pair or an aligned stack slot) and its r0/r1 is the
result. A function the guest cannot be called with by address (it takes a C# string, a float, the HLE context or
variadic arguments) has no wrapper. The analyzer VSHLE010 reports a direct call of an exported function in VitaSharp.Core;
the calls that are not the firmware's call of that function stay direct with a `#pragma` saying why: the syscall forms
behind a user library's wrappers (`_sceIo*`, `_sceKernel*`, ...) and the kernel entries that share the C# body of the
user call of the same name (on the Vita the user call reaches the kernel by its syscall, so a hook on one is not seen by
the other).

The firmware's process start calls modulemgr's export ksceKernelStartPreloadingModules(pid) by its address once a new
process's main thread exists (processmgr 3.74). VitaSharp starts the preloaded modules itself, so the call is made
through the export's entry when kernel code hooked it: the hook runs on a kernel thread with the new process's pid,
before the process's main thread runs, for every process opened after the hook. A kernel module adds its own module
to a process this way (Adrenaline's kernel module adds its user module to the PSP emulator's process). The process the
shell starts by path (sceAppMgrLaunchAppByPath4, the firmware's PSP emulator vs0:app/NPXS10028) has the title the
launch names as its process title (ksceKernelGetProcessTitleId, the plugin configuration's section): the title of the
bubble the shell opened.

### The launch's app0 mount

An application folder that is still a PFS image is mounted as SceAppMgr 3.74 mounts it when it launches the title
(decompiled appmgr, study only): the licence file is found, read (0x200 bytes) with the kernel's ksceIoOpen (flags
0x10001), ksceIoRead and ksceIoClose, checked by SceNpDrm (ksceNpDrmGetRifInfo, ksceNpDrmGetRifVitaKey; pfsmgr itself
imports nothing of SceNpDrm, the key comes in with kscePfsMount), and app0 is mounted through ScePfsMgr with the key. When
kernel code hooked one of those three I/O functions, the licence read runs through their entries on a kernel thread, so
the hook sees the launch's read (and its continuation reaches the HLE); without a hook the file is read directly. The
title key step is the secure processor's and answers 0x80020004 on the emulated unit, hooked or not. A failure names its
step: `<title>: the launch stopped at app0's mount, step N of 4 (<step>): ... (0x........)` (1 licence, 2 licence read,
3 title key, 4 PFS mount); the message to the user is unchanged. The mount itself is appmgr's call of kscePfsMount
(ScePfsMgrForKernel 0xA067B56F / 0xA772209C) with the application's path, a mount point named "ts", twelve hex digits
and "d", the process's program authority ID, the 16-byte title key and mode 5: when kernel code hooked that function,
the launch makes the call through the entry on a kernel thread, the original the hook continues to mounts the image as
the launch does without a hook, and an error the hook answers fails the launch at step 4. The title key is the one the
launch already has (no check of the licence changes); the authority ID is 0 for a first launch, since VitaSharp reads it
from the executable inside the image after the mount.

### Path walks on the emulator's devices

On the Vita iofilemgr walks every path element by element with ksceVopLookup from the device's root vnode (3.74,
0x81004bb8, study only), so a kernel hook on it (fd_fix's) sees every open. In VitaSharp ux0:, app0: and the other
devices are host folders; when a hook took ksceVopLookup, sceIoOpen (the user's syscall and ksceIoOpen), sceIoDopen,
sceIoGetstat, sceIoRemove, sceIoMkdir, sceIoRmdir, sceIoChstat and sceIoRename (the old path, then the new one), with
their ksceIo* forms, walk the path through the function's entry over the host vnodes, as iofilemgr 3.74 walks it for
each of them (`IoFileMgr.VfsHost.cs`; the element a mkdir or a rename's new path makes is looked up, not found, and
made): the hook gets each
element, and what it answers counts (an error fails the call, another vnode sends it to that vnode's path, an open that
creates still creates). Without such a hook nothing is walked and nothing is allocated (`IoVopLookupHookTests`).
The asynchronous forms (sceIoOpenAsync, sceIoDopenAsync, sceIoGetstatAsync and the other path requests) run on
iofilemgr's own kernel thread on the Vita (SceIoScheduler), which walks the path the same way: with such a hook the
I/O scheduler runs a path request on a kernel thread of that name, in the submitting process's context, so the hook is
called for it and its answer is the request's result; without one the request stays on the scheduler's host thread.

### The launch's process creation

SceAppMgr 3.74 creates a title's process with ksceKernelCreateProcess(title ID, type 0x1000000 for an application or
0x2000000 for a system application, the executable's path, a 0x40-byte option) before it mounts app0, and launches
nothing when the call fails. When kernel code hooked that function (SceProcessmgrForKernel 0xEB1F8EF7 / 0x68068618 from
FW 3.63 on; 3.60's 0x7A69DE86 / 0x71CF71FD is not exported by 3.74, so a hook on it is refused as on the console),
every launch (the first title, the shell's launches, LoadExec) calls the entry on a kernel thread: the hook runs, the
original it continues to answers with the launched process, and an error fails the launch with it
(`KernelPluginBootOrderTests`). Without a hook nothing runs.

## Limits

- A plugin that patches the firmware's own code by offset finds no firmware code there (VitaSharp implements the
  firmware's libraries itself).
- The firmware user modules the HLE serves are listed only in the plugin framework's searches (module information,
  exports, hooks, offsets); the module manager's own list of the process (sceKernelGetModuleList) shows the loaded
  modules only. Their exported variables are not in the tables.
- Undefined instructions and prefetch aborts (jumps to unmapped addresses) reach the handlers registered with
  SceExcpmgr, in undefined/abort mode with the banked registers and SPSR of the ARM exception modes; with no handler,
  or when no handler takes it, the fault is reported as a crash as before. Data aborts are not delivered yet (an
  access to unmapped memory reads zeros). Interrupt masking is state only (there is no interrupt controller).
