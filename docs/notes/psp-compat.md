<!-- pair: psp-compat.pt-BR.md sha256=8f9bfdc0277b75618f96ba19db4fe138c46b2313a5547da2d399cff823a78943 -->
# PSP compatibility core (SceCompat, ScePspemu, Allegrex)

*Português: [psp-compat.pt-BR.md](psp-compat.pt-BR.md).*

Study and design note for running PSP and PSone titles through the Vita's own PSP emulator (`vs0:app/NPXS10028`,
ScePspemu) on top of the real compatibility core, as a real Vita does.
Product code so far: the Allegrex interpreter (below). Sources: the FW 3.74
`SceCompat` (bootimage), `NPXS10028/eboot.bin`, `pcbc.skprx` and `pcff.skprx` decompiled or dumped with the local RE
tools (`%LOCALAPPDATA%\VitaSharpTools\fw374`, study only, nothing copied), the Adrenaline source (GPL-3), the VitaMips
notes (Hykem) and the existing HLE (`src/VitaSharp.Core/Hle/Modules/Compat.cs`). psdevwiki pages refuse automated
fetches; read them by hand when a point below says "to confirm".

## How the Vita does it

The Vita SoC has a MIPS core compatible with the PSP's Allegrex. It runs PSP code natively: a small boot code
(`pcbc`) and a PSP 6.6x kernel (`pcff`, a flash0 image whose drivers are the `kermit_*` modules). Everything the PSP
would do through its own hardware (Memory Stick, flash, audio, the Media Engine codecs, Wi-Fi, USB, power, the OSK,
dialogs) the kermit drivers send as **requests** to the ARM side, where ScePspemu (user ARM code, already running in
LLE in VitaSharp) serves them with Vita APIs. `SceCompat` (kernel ARM) is the bridge: it owns the memory the MIPS core
sees, loads the firmware, starts and stops the core, and moves requests, answers and interrupts through a shared SRAM
and a mailbox. PSone titles take the same path (the PSP-side `pops` emulator, with display/audio help from ScePspemu);
PocketStation is emulated on the ARM side by ScePspemu.

Evidence that the compat core is MIPS (the 10/11 roadmap note said "an ARM core", which is wrong):

- Adrenaline patches, after the core reports "booted", the word at user address `0x70602D70` (window + `0x602D70`,
  inside the area where `pcbc` is loaded) with `0x0E3F0000`, the MIPS encoding of `jal 0x88FC0000`.
- VitaMips places its own boot code at PSP `0x88600000` (window + `0x600000`, the `pcbc` slot) and starts it with
  `sceCompatStart`.
- ScePspemu has no GE or CPU emulation threads; its threads are the "Remote" servers (`ScePspemuRemoteMsfs`,
  `...Flashfs`, `...Me`, `...AudioOut...`, `...Net`, `...Usb`, `...Osk`, `...Dmacplus`) of the kermit requests.

## 1. What SceCompat 3.74 does

### Memory

| Item | Value | Notes |
|---|---|---|
| Window (`sceCompatAllocCdramWithHole`) | 64 MB, seen by the PSP at `0x88000000` | three blocks of 32/16/16 MB, each allocated cached, uncached and as kernel CDRAM; the middle one is unmapped (the "hole", PSP `0x8A000000`-`0x8B000000`); the last block must sit at base + `0x3000000` (else `0x80010078`) |
| Allegrex address windows | `SceGrabForDriver_E9C25A28(i, paddr)` for i = 0..3, bit 0 of paddr set | 0: base, 1 and 2: base + `0x1000000`, 3: base + `0x3000000` (Adrenaline rewrites window 2 to give the PSP the extra 16 MB of the hole) |
| Boot code `pcbc` | CDRAM + `0x600000` (PSP `0x88600000`), at most `0xA00000` | `os0:kd/pcbc.skprx`, `SceSblPcbcBin`, `0x5CE0` bytes |
| flash0 package `pcff` | CDRAM + `0x3000000` (PSP `0x8B000000`), at most `0x2000000` | `vs0:app/NPXS10028/pcff.skprx`, `SceSblPcffBin`, `0x920000` bytes: a table of {name, buffer, size} with PSP pointers, then the files |
| Random seed | CDRAM + `0x3FFF00` (PSP `0x883FFF00`), 4 bytes | from `SceSblSsMgrForDriver_4F9BFBE5` (random) when the window is allocated |
| Boot/suspend handshake word | CDRAM + `0x3D78000` (PSP `0x8BD78000`) | states 1 (core ready), 2 (ARM acknowledged), 3 (core running), 0 (idle) |
| Debug area | CDRAM + `0x3D80000`, `0x40000` | mapped for Deci2p at start (development units) |
| PSP scratchpad / VRAM views | PSP `0x8BD00000` / `0x8BE00000` | Adrenaline's names for the top of window 3 |
| `SceCompatMailbox` | ARM physical `0xE5070000`, `0x1000` | doorbells: writing `0xFFFFFFFF` to `+0x80 + n*4` then `+n*4` signals line n to the core and acknowledges it |
| `SceCompatSharedSram` | ARM physical `0xE8100000`, `0x1000`; PSP `0xBFC00000` | the shared block below |
| `SceCompatLCDDMA` | ARM physical `0xE5071000`, `0x1000` | the LCD DMA that copies the PSP framebuffer (480x272, pitch `0x800` bytes) to the Vita framebuffer block (`0x88000` bytes) |
| "dat" block | `0x1038` bytes in SceCompat | filled at start by `SceNpDrmForDriver_D91C3BCE`; `0x1038` is the size of `act.dat` (the activation the PSP-side npdrm reads through `sceCompatDatRead`) |

Shared SRAM offsets (ARM view `+x` = PSP `0xBFC00000 + x`):

| Offset | Use |
|---|---|
| `0x100`, `0x104`, `0x108`, `0x170`..`0x188`, `0x10C`..`0x114` | 32-bit shared registers: `sceCompatWriteShared32(0x80..0x88)` writes `0x100/0x104/0x108/0x170/0x174/0x178/0x17C/0x180/0x184`; `sceCompatReadShared32(0..7, not 3)` reads `0x100/0x170/0x17C/-/0x10C/0x110/0x114/0x188` |
| `0x120`..`0x128`, `0x140` | `sceCompatWriteSharedCtrl`: the three controller words, then the first again at `0x140` (written last, the "new data" marker) |
| `0x800 + i*8` | command entries the core posts (IRQ `0x44+i`): word 0 high 16 bits = channel (< 16), word 1 = the request's PSP address |
| `0x804 + k*8` | the next entry, read again when a channel's queue overflowed |
| `0x840 + c*0x20` | answers (`sceCompatReturnValueEx(c < 3, value[2])`): value, the request's semaphore/response words, then doorbell `c + 7` |
| `0x8C0 + n*8` | virtual interrupts (`sceCompatInterrupt(n = 1..15)`): SceCompat waits up to 1 s for the slot to be 0, writes 1, rings doorbell `n + 0x10` |

### Interrupts and threads (`module_start`)

- IRQ `0x44`-`0x46` ("SceCompat0c".."2c"): the core posted a request. The handler reads the shared SRAM entry, puts
  the request address in the channel's ring (16 channels x 16 entries; full ring: overflow flag) and acknowledges the
  doorbell. Sixteen "SceCompatHandler%d"/"SceCompatVirtualHandler" semaphores wake the channel waiters.
- IRQ `0x47`-`0x49` ("SceCompat0r".."2r"): the core answered; they signal the semaphores `sceCompatWaitIntr(0..2)`
  waits on.
- IRQ `0x4A` ("SceCompatLcdc"): the LCD controller's vsync; `sceCompatLCDCSync` waits on it (through the display's
  scan line: lines `0x1E6`/`0x226`/`1000`/`0x28A` depending on the head).
- A suspend handler (`SceKernelSuspendForDriver_04C05D10`) and a process event handler (process exit stops the core).

### Exports (SceCompat, every entry checks the caller is a system program: `0x80010058`)

| Export | What it does |
|---|---|
| `sceCompatAllocCdramWithHole(out[2])` | allocates the window above (once: `0x80010078`), records the owner thread (other callers: `0x8002F001`) |
| `sceCompatCache(op 1/2/3, addr, size)` | invalidate / clean / clean+invalidate on the window, 64-byte lines |
| `sceCompatInitEx(bootParam)` | clears the request rings; releases the Grab windows and maps them again; `SceSblFwLoader` loads `pcff` to CDRAM + `0x3000000` and `pcbc` to CDRAM + `0x600000` (decrypted by the secure processor), writes back the cache; writes the random seed; sets the compat clock (`ScePowerForDriver_FFC84E69(0x14D)`, 333 MHz); `SceGrabForDriver_072B8D93(bootParam)` takes the core; secure command `0x10006` (below) |
| `sceCompatStart()` | maps Deci2p; fills the "dat" block (activation; its error is returned unless the unit is a DEX); checks the rif set by `sceCompatSetRif` (`SceNpDrmForDriver_DACB71F4`, on a kernel stack), invalid and not a DEX: `0x8001000D`; `SceGrabForDriver_379130B2` releases the core from reset; programs the LCD DMA when a framebuffer block exists; state 2 (running). On failure the stop path runs |
| `sceCompatWaitSpecialRequest(1)` | boot handshake: polls the word at `0x8BD78000` until 1 (5 s, else `0x80028005`); secure command `0x30006`; unmasks the doorbells `0x40`-`0x5F` except `0x41` and `0x4E`; writes 2 and waits for 3 (5 s), then 0. `2`/`3`: suspend/resume handshakes (30 s) |
| `sceCompatWaitAndGetRequest(channel < 16, kind < 3)` | waits on the channel's semaphore, pops its ring; returns the request address (>> 1; the low 26 bits locate the 0x80-byte request in the window, invalidated before returning); empty: `0x80010016` |
| `sceCompatReturnValueEx(c < 3, value[2])` | writes the answer (shared SRAM `0x840 + c*0x20`) and rings doorbell `c + 7`; core not started: `0x80027205` |
| `sceCompatInterrupt(1..15)` | virtual interrupt to the core (slot `0x8C0`, doorbell `n + 0x10`); no core: `0x80010086` |
| `sceCompatWaitIntr(0..2)` | waits for an answer interrupt (IRQ `0x47`-`0x49`) |
| `sceCompatRead/WriteShared32`, `sceCompatWriteSharedCtrl` | the shared registers above |
| `sceCompatStop`, `sceCompatUninit`, `sceCompatGetStatus` | stop (5 s lock, `SceGrabForDriver_F13C63DD`, clock 0, state 1); release the framebuffer; state 0/1/2 |
| `sceCompatSuspendResume(1)` | resume a suspended core: Grab, clock, secure command `0x10006` with the saved token, start, handshake 1-2-3; nothing to resume: `0x8001008D` |
| `sceCompatFrameBufferInit`, `sceCompatSetDisplayConfig`, `sceCompatLCDCSync`, `sceCompatGetPrimaryHead`, `sceCompatAvailableColorSpaceSetting` | the display: the framebuffer block, `ksceDisplaySetFrameBuf` 480x272 pitch 512 (wide: `0x100000`), the PSP vsync |
| `sceCompatDatRead`, `sceCompatSetRif`, `sceCompatIdStorageLookup`, `sceCompatGetDevInf`, `sceCompatGetCurrentSecureTick`, `sceCompatGet/SetUpdateState`, `sceCompatGetPeripheralState`, `sceCompatGetPspSystemSoftwareVersion` (`0x295`), `sceCompatGetTitleList`, `sceCompatSetSuspendSema`, `sceCompatCheckPocketStation`, `sceCompatIsPocketStationTitle` | services that do not need the core (already followed by `Compat.cs`) |

### Secure processor (`os0:sm/compat_sm.self`)

SceCompat loads `compat_sm` for each command (`SceSblSmCommForKernel_7863A0CC`, with a `0x90`-byte parameter from
the module), sends one command and stops it again:

- `0x10006` (argument: 0 at InitEx, the resume token at resume): called right after the core is taken. Name unknown
  (to confirm on psdevwiki); by position it prepares the protected compat memory for the boot.
- `0x30006`: Adrenaline calls the function `sceCompatSecSetSSRAMAcl`: it sets the access control of the shared SRAM
  once the core reports "ready" (handshake state 1), before the ARM side answers.

Neither returns data to SceCompat beyond a status word; in VitaSharp both become "status 0" in the SM service
emulation, with no cryptography.

### Protocol with ScePspemu

ScePspemu creates one thread per kermit channel and loops `sceCompatWaitAndGetRequest(mode)` →
convert the address → serve → `sceCompatReturnValueEx`. A request is 0x80 bytes: `cmd`, the PSP semaphore id,
the PSP address of the 64-bit response, then 14 64-bit arguments. Channels (Adrenaline's `KermitModes`): 3 msfs
(Memory Stick), 4 flashfs, 5 audio out, 6 ME (the Media Engine codecs run on the ARM side), 7 lowio, 8 PocketStation
/ USB PSPCM, 9 peripheral, 10 WLAN, 11 audio in, 12 USB, 13 utility, 14-15 extra (Adrenaline uses 15). Virtual
interrupts to the core (`sceCompatInterrupt`): 1-3 audio, 4-6 ME DMA, 7-8 WLAN, 9 impose, 10 power, 12 USB GPS,
13 USB PSPCM. Controller: ScePspemu writes the pad each frame with `sceCompatWriteSharedCtrl`. Display: the PSP draws
into its VRAM, the LCD DMA copies the framebuffer to `ScePspemuFrameBuffer` and ScePspemu shows it with
`sceDisplaySetFrameBufForCompat`/`sceCompatSetDisplayConfig`. Audio: `kermit_audio` posts PCM on channel 5 and
ScePspemu plays it with `sceAudioOut`.

### Boot order seen in VitaSharp

The real ScePspemu already runs up to `sceCompatInitEx(1)` (log: `SceCompat: InitEx(1): the PSP compat core is not
emulated`): it chose 444 MHz, created its threads, started GXM, took the window, cleaned the two halves
(`sceCompatCache`), registered the suspend semaphores and the framebuffer. After InitEx it calls `sceCompatStart`,
then `sceCompatWaitSpecialRequest(1)`, and starts its request threads.

## 2. What the Allegrex side needs

- **CPU:** the Allegrex ISA: MIPS32 integer core of the R4000 family without 64-bit instructions, plus the Allegrex
  additions (`clz/clo`, `min/max`, `madd/msub(u)`, `ext/ins`, `seb/seh`, `wsbh/wsbw`, `bitrev`, `movz/movn`, `rotr`,
  `halt`, `mfic/mtic`), the single-precision FPU (COP1), COP0 (status, cause, EPC, exceptions, interrupts, cache
  instructions) and the **VFPU** (COP2: 128 registers as 4x4 matrices, prefixes, the transcendental and conversion
  instructions; bit-exact results matter for games). Little endian, 32-bit physical addressing through fixed segments
  (`0x0`/`0x4`/`0x8`/`0xA` mirrors), no TLB.
- **Media Engine:** not needed. The kermit firmware sends ME work (`kermit_me_wrapper`) to ScePspemu on the ARM side.
- **GE (graphics):** the pcff has the real `ge.prx`/`ge_2.prx` and ScePspemu has no GE thread, so the GE display lists
  are executed by compat hardware in the SoC (inferred; to confirm). VitaSharp needs a GE implementation (display
  lists, vertex decode, rasterisation to Vulkan, VRAM at the top of window 3). This is the biggest piece.
- **Other PSP-side hardware the kermit kernel touches:** interrupt controller, system timer, DMA controller
  (`dmacman`), the mailbox and shared SRAM above, the LCD controller, cache control; and the KIRK crypto service
  (memlmd decrypts every `~PSP` module of flash0 and of the games). The exact register set must be traced from the
  boot (an "unknown MMIO" log in phase 3 below) rather than guessed.
- **What runs on it:** `pcbc` (the boot code at `0x88600000`), which starts the PSP kernel from the `pcff` package at
  `0x8B000000` (`loadcore`, `sysmem`, the `kermit_*` drivers, `init`, then VSH or the game through `loadexec`). With
  Adrenaline, its own patches (running as third-party code, unchanged) swap in the 6.61 flash0 that its installer
  extracted from `661.PBP` (on the PSP side, with `libpsardumper`) into `ux0:app/PSPEMUCFW/flash0`, and add
  `systemctrl`/`inferno`/`popcorn`.

### The Allegrex interpreter in VitaSharp

`src/VitaSharp.Core/Compat/Allegrex` (`AllegrexCore` on an `IAllegrexBus` that receives PSP physical addresses,
`address & 0x1FFFFFFF`): the whole integer ISA with the Allegrex additions, delay slots and branch-likely, the
exceptions through EBase (`0xBFC00200` while Status.BEV is set), COP0 (Status, Cause, EPC, ErrorEPC, EBase, BadVAddr,
Count, the syscall code, the CPU id, the cfc0/ctc0 control registers), interrupt lines Cause.IP0..IP7 (the PSP's
interrupt controller is IP2) taken with Status.IE or `mtic`, halt, syscall/break offered to the host first. The
results MIPS32 leaves unpredictable follow PPSSPP's hardware-checked notes (pspautotests): division by zero,
`0x80000000 / -1`, the partial decode of rotr/rotrv and of the seb/seh/wsbh/wsbw/bitrev group, `ins` with msb < pos,
the reset state. Tests: `tests/VitaSharp.Tests/Allegrex*Tests.cs`.

The FPU (COP1) computes every result exactly in software for the four FCR31 rounding modes, so it does not depend on
the host's FPU mode, with the five IEEE causes, flags and enables (an enabled one takes ExcCode 15), the PSP's
`0x7FC00000` for generated NaNs and its flush-to-zero rule (FS flushes results, tininess before rounding, never
inputs). The VFPU (COP2) is complete: the S/C/R/M/E register views, the prefixes with each instruction's own reach,
every opcode, its arithmetic (round to nearest, denormals as zeros, payload-free NaNs `0x7F800001`), the exact dot
product, and `vrcp`/`vrsq`/`vsqrt`/`vsin`/`vcos`/`vasin`/`vexp2`/`vlog2` through the hardware's interpolator tables
([third_party/ppsspp](../../third_party/ppsspp/README.md)); they and the random generator match outputs recorded on a
PSP (pspautotests `cpu/vfpu/exact`, `cpu/vfpu/vrnd`) bit for bit. COP0 Count reaching Compare raises Cause.IP7.

Three points have no hardware test we know of; VitaSharp follows the MIPS32 architecture, which the Allegrex
implements:

- `add`, `addi` and `sub` raise the overflow exception (PPSSPP and Jpcsp treat them as `addu`/`subu`).
- `di`/`ei` (`mfmc0`) work as in MIPS32 release 2: rt = Status, then Status.IE = the instruction's sc bit.
- An interrupt can be taken with a delay slot next: EPC = the branch, Cause.BD set (PPSSPP only checks outside a slot).

### What needs keys and what does not

| Item | Who decrypts on a Vita | VitaSharp |
|---|---|---|
| `pcff.skprx` SELF layer | the secure processor (SceSblFwLoader) | the existing SELF decryption (`self2elf`) already yields the plain package (names and PSP pointers readable): no new key |
| `pcbc.skprx` inner layer | the secure processor (SceSblFwLoader / `compat_sm`) | after `self2elf` the `0x5CE0` bytes are still encrypted (7.99 bits/byte). Keys the project does not have and will not search for or derive (lead decision, 11/11/2026). Only path: a `pcbc.bin` dumped by the user from their own console (CDRAM + `0x600000` after InitEx), never in the repository |
| `compat_sm` commands `0x10006`/`0x30006` | the secure processor | no data to decrypt: emulated as status 0 |
| `~PSP` modules inside flash0 and the games' EBOOT/PRX | the PSP kernel (memlmd) through KIRK | KIRK must be emulated. Where KIRK lives on the Vita (compat hardware or `compat_sm`) is to confirm. The KIRK keys are PSP keys published in open-source emulators (PPSSPP's libkirk, Jpcsp): using them is a **lead decision** (not taken here; no key is written in this note) |
| `661.PBP` (Adrenaline) | the PSP side (PSAR through KIRK) | follows from KIRK |
| rif / act.dat / NPDRM | Vita NpDrm (HLE) + PSP npdrm (KIRK) | existing HLE on the Vita side; PSP side follows from KIRK |

## 3. References

| Reference | What to study | License | Reuse |
|---|---|---|---|
| PPSSPP | Allegrex interpreter and IR JIT, VFPU (prefixes, exact math tables), GE (display lists, vertex decoder, Vulkan backend), KIRK (`ext/libkirk`) | GPL-2.0-or-later | compatible with VitaSharp's GPL-3; by project rule we study and write our own C#; opcode tables and VFPU constants may be ported with attribution if the lead decides |
| Jpcsp | the only open LLE of PSP hardware: pre-IPL/IPL boot, real kernel modules, interrupt controller, timers, DMAC, KIRK as hardware | GPL-3.0 | as above; the best reference for phase 3 |
| Adrenaline (TheOfficialFloW, and the "Adrenaline 8" fork the user runs) | kermit request/interrupt layout, flash0 package, window/Grab layout, ScePspemu internals | GPL-3.0 | study; it runs unchanged in VitaSharp as third-party code |
| VitaMips (Hykem) | custom boot code at `0x88600000` started with `sceCompatStart`, KIRK emulated in software | check | design of the synthetic test boot code |
| psdevwiki (SceCompat, compat_sm, Kermit, ScePspemu pages) and vita-headers | names of the secure commands, Grab, mailbox | wiki / MIT | facts only |
| pspautotests | CPU/VFPU/GE behaviour tests | check | ideas for our own synthetic tests (no PSP binaries in the suite) |

## 4. Plan

Phases (sizes as in the roadmap: `[M]` hours, `[G]` a day or more):

1. **Minimal testable delivery [G, ~2-3 days].** An Allegrex integer interpreter (no FPU/VFPU) on its own guest
   thread; the PSP address view of the SceCompat window (the four Grab windows, the hole); the shared SRAM, the
   mailbox doorbells and the IRQ `0x44`-`0x4A` path in SceCompat, written as the module does; `sceCompatInitEx` loads
   the boot code from the SceSblFwLoader HLE (a test fixture in tests); `sceCompatStart` runs it. Test: a synthetic
   MIPS boot code built in the test (our own bytes, no firmware) that sets the handshake word to 1, waits for 2,
   writes 3, posts a request on channel 3, waits for the answer, and handles a virtual interrupt. Asserts:
   `sceCompatWaitSpecialRequest(1)` = 0, `sceCompatWaitAndGetRequest(3, 0)` returns the request, the core sees the
   `sceCompatReturnValueEx` value and the `sceCompatWriteSharedCtrl` words, `sceCompatInterrupt(9)` reaches its
   handler, `sceCompatStop` stops it. Fails today (InitEx answers `0x80027205`).
2. **Complete Allegrex [G, 1-2 weeks].** FPU, VFPU, COP0 exceptions/interrupts, cache instructions, cycle counting
   for the compat clock; ISA tests per instruction group, VFPU checked against values computed by hand from the
   documented semantics.
3. **PSP-side hardware [G, 2-4 weeks, GE dominates].** Boot `pcbc` (user's `pcbc.bin`) + `pcff` with an "unknown
   MMIO" log; implement what it touches: interrupt controller, timer, DMAC, LCD DMA to the ScePspemu framebuffer,
   KIRK (after the lead's key decision), GE to Vulkan. Target: the core reaches handshake state 1 and the real
   ScePspemu serves its first msfs/flashfs requests.
4. **ScePspemu end to end [G, ~1 week].** Follow the logs: the HLE gaps ScePspemu hits (audio, ME codecs, msfs,
   power, display), a PSP homebrew, then a PSP game and a PSone game; suspend/resume (`0x10006` with token, states
   2/3) and save states of the core.
5. **Adrenaline 6.61 [M-G].** Adrenaline's own hooks and its flash0 (no code for Adrenaline in VitaSharp); its
   `sceCompatSecSetSSRAMAcl` patch must find the decrypted `pcbc` at `0x88602D70`.
6. **Performance [G].** Allegrex JIT (PPSSPP IR JIT, Dolphin and the existing ARM JIT as references), GE batching;
   measured in gameplay.

Risks and blockers:

- **`pcbc` inner encryption** blocks phase 3 onwards with the real boot code until the user provides `pcbc.bin`.
  Phases 1-2 do not need it.
- **KIRK keys:** without the lead's decision no `~PSP` module loads (phase 3).
- **GE hardware** on the Vita is undocumented; the PSP GE (PPSSPP/Jpcsp) is the model, with the risk that the compat
  hardware differs.
- **Unknown compat registers** (Grab, mailbox banks, LCD DMA bits): inferred from SceCompat only; the boot trace
  fixes them.
- **Cost:** emulation inside emulation; the interpreter will be slow until phase 6.
