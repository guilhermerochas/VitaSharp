# Plugins (kernel `.skprx` and user `.suprx`)

*Português: [plugins.pt-BR.md](plugins.pt-BR.md).*

VitaSharp runs the plugins a homebrew-enabled Vita runs, from the same plugin configuration and with their original
code: nothing in the emulator is written for a particular plugin. Some homebrew needs one (the Android ports built on
so_loader, such as Dead Space, need **kubridge**).

## Setting it up

Copy the plugins from your own Vita into the data folder (`ur0:tai` is `fs/ur0/tai` in it) and list them in
`fs/ur0/tai/config.txt` (or `fs/ux0/tai/config.txt`, which wins when both exist), the file taiHEN reads on the Vita:

```
# kernel plugins: loaded into the kernel and started when a program starts
*KERNEL
ur0:tai/kubridge.skprx
ur0:tai/fd_fix.skprx
# user plugins for every program
*ALL
# user plugins for one title
*PCSE00000
ux0:tai/some_plugin.suprx
```

Without a `config.txt` no plugin is loaded, and a program that needs one fails as it does on a Vita without it.
`taihen.skprx` itself does not need to be listed (see below).

## What happens

- `*KERNEL` modules load into the kernel region (an area every process maps, owned by the kernel process) and their
  `module_start` runs at once on a kernel thread; a module whose start does not report success is unloaded again, as
  the firmware's module manager does. The log says what happened to each one (`kernel plugin ...: loaded and started`).
- `*ALL` and `*TITLEID` modules load into the program's process and start before its main thread.
- A module that imports a library nothing provides (no loaded module and no HLE) is not started; the log names the
  libraries.
- Kernel modules call the firmware's kernel libraries (SceSysmemForDriver, SceThreadmgrForDriver,
  SceModulemgrForKernel, SceCpuForDriver, ...): VitaSharp implements them with the firmware's behaviour and error codes.
  User programs reach a kernel module only through its syscall-exported libraries (kubridge's `kubridge` library, for
  example); kernel modules never link against user code.
- The emulated CPU has no privilege levels, so the permission check of the user/kernel copies is done by the kernel
  library: a user range must be the caller's own memory, not kernel memory.

## taiHEN

On the Vita, taiHEN (`taihen.skprx`) is loaded by the exploit and provides the plugin loader plus the hooking API
other plugins import (`taihenForKernel`, `taihenModuleUtils`) and that homebrew calls (`taihen`, `taihenUnsafe`).
VitaSharp does not run `taihen.skprx`: its `module_start` only succeeds after installing its own patches into the
firmware's signature and DRM checks, and VitaSharp does not make those work. So taiHEN is the one module that is not
the firmware's and is still implemented by the emulator (`Hle/Modules/Taihen.cs`, `Taihen.Kernel.cs`), with taiHEN's
semantics and error codes; listed in `config.txt`, the real module loads, its start fails and it is unloaded.

## Limits

- Kernel modules live with the process of the program they were loaded for (the kernel is not yet one system-wide
  process); they are unloaded when that process ends.
- Hooks on firmware functions that VitaSharp implements in C# reach the calls made through import stubs; the
  emulator's own internal calls are not hooked. A plugin that patches the firmware's code by offset (kubridge's
  memory-protection calls `kuKernelMemProtect` / `MemReserve` / `MemCommit`, fd_fix's VFS hook) finds no firmware code
  there: fd_fix's hook is not installed, and those kubridge calls stop the program (they jump through firmware
  addresses the plugin could not find), as on a firmware whose offsets the plugin does not know. Dead Space does not
  use them.
- Exceptions are not delivered to kernel handlers (the emulated CPU reports a guest fault as a crash); interrupt
  masking is state only (there is no interrupt controller).
