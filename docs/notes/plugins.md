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
  the first game when no shell runs), as the plugin loader loads them at boot on the Vita. Every process sees them: the
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
