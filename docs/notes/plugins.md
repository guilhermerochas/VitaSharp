# Plugins (kernel `.skprx` and user `.suprx`)

*Português: [plugins.pt-BR.md](plugins.pt-BR.md).*

VitaSharp loads the plugins a homebrew-enabled Vita loads, from the same plugin configuration, and runs their original
code: nothing in the emulator is written for a particular plugin or homebrew, and no plugin is replaced by an
emulator-side imitation.

## Setting it up

Copy the plugins from your own Vita into the data folder (`ur0:tai` is `fs/ur0/tai` in it) and list them in
`fs/ur0/tai/config.txt` (or `fs/ux0/tai/config.txt`, which wins when both exist), the file taiHEN reads on the Vita:

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

Without a `config.txt` no plugin is loaded.

## What happens

- `*KERNEL` modules load into the kernel region (an area every process maps, owned by the kernel process) and their
  `module_start` runs at once on a kernel thread; a module whose start does not report success is unloaded again, as
  the firmware's module manager does. The log says what happened to each one (`kernel plugin ...: loaded and started`,
  `... module_start of X returned 0x2, unloaded`).
- `*ALL` and `*TITLEID` modules load into the program's process and start before its main thread.
- A module that imports a library nothing provides (no loaded module and no firmware library VitaSharp implements) is
  not started; the log names the libraries (`not started: no module provides ...`).
- Kernel modules call the firmware's kernel libraries (SceSysmemForDriver, SceThreadmgrForDriver,
  SceModulemgrForKernel, SceCpuForDriver, ...): VitaSharp implements them with the firmware's behaviour and error codes.
  User programs reach a kernel module only through its syscall-exported libraries; kernel modules never link against
  user code.
- The emulated CPU has no privilege levels, so the permission check of the user/kernel copies is done by the kernel
  library: a user range must be the caller's own memory, not kernel memory.

## taiHEN, and what needs it

On the Vita, taiHEN (`taihen.skprx`) is loaded first by the homebrew boot chain and provides the plugin loader plus the
hooking API other plugins import (`taihenForKernel`, `taihenModuleUtils`) and that homebrew calls (`taihen`,
`taihenUnsafe`). Listed in `config.txt`, VitaSharp loads the real `taihen.skprx` like any kernel module: its imports
bind to the firmware kernel libraries and its own `module_start` runs. It then fails on its own (it returns
SCE_KERNEL_START_FAILED) because its start installs patches into the firmware's signature and DRM checks, which
VitaSharp does not make work, and it is unloaded.

So everything that needs taiHEN does not run, as on a Vita without it:

- kernel plugins built on taiHEN, such as **kubridge** and **fd_fix** (`not started: no module provides
  taihenForKernel`);
- homebrew that calls taiHEN's user API or needs such a plugin, among them **Dead Space** (the so_loader Android port
  needs kubridge) and the **Sonic Mania** port (taiHEN's module lookups).

## Limits

- Kernel modules live with the process of the program they were loaded for (the kernel is not yet one system-wide
  process); they are unloaded when that process ends.
- A plugin that patches the firmware's own code by offset finds no firmware code there (VitaSharp implements the
  firmware's libraries itself).
- Exceptions are not delivered to kernel handlers (the emulated CPU reports a guest fault as a crash); interrupt
  masking is state only (there is no interrupt controller).
