<!-- pair: hardware-coverage.pt-BR.md sha256=e7889f5c0efeb7486609a4790dc34f7fa56f490ad192c1289543f88cf94e2b6e -->
# Hardware coverage (PS Vita SoC and console)

*Português: [hardware-coverage.pt-BR.md](hardware-coverage.pt-BR.md).*

An inventory of the hardware blocks of the PS Vita (CXD5315GG SoC on the PCH-1000, its chips and the PS TV
variant). For each block: what the FW 3.74 driver expects from the hardware, how VitaSharp serves it today, and what
is missing. This is a study note: there is no product code in it. Sources: the FW 3.74 modules decrypted and
decompiled with the local RE tools (`%LOCALAPPDATA%\VitaSharpTools\fw374`, study only, nothing copied), the HLE in
`src/VitaSharp.Core` (the header comment of each driver file, which records what the module does), the roadmap,
`docs/notes/psp-compat.md`, `docs/notes/online-features.md`, `docs/hle/F-devices-media.md`, and these public pages:
psdevwiki ([Hardware](https://psdevwiki.com/vita/Hardware), [Pinout](https://psdevwiki.com/vita/Pinout); the site
refuses automated fetches, so points marked "to confirm" need a manual read), Rodrigo Copetti's
[PlayStation Vita architecture](https://www.copetti.org/writings/consoles/playstation-vita), the
[VVNZRunner](https://gamebrew.org/wiki/VVNZRunner_Vita) page and the
[VitaLoader Redux](https://github.com/CreepNT/VitaLoaderRedux) README.

## How VitaSharp meets the hardware

The firmware's kernel drivers talk to registers, buses and coprocessors. VitaSharp has no SoC behind them. A driver is
served in one of three ways:

- **HLE of the driver:** the `[Hle]` entries do what the module does, on emulated state (most of `os0:kd` and the boot
  image). Where a device exists, the state is backed by a host device (audio, webcam, gamepad, sockets).
- **Documented default (`[Hle(Default = true)]`):** the module's own checks, then the answer the module gives without
  the device (no cable, no card, no paired device, no secure processor). `hle-coverage` counts these apart.
- **Higher-level emulation:** the hardware's work is done one layer up (the GPU at the GXM command level, the codec
  engine at the decoder library level, the storage at the file level).

What sets the compatibility core (Allegrex) apart from the rest: it is a **coprocessor that runs its own code**, and
no higher layer can stand in for it, because the code running on it (the PSP kernel and the PSP games) is the content
itself. The survey below looked for other blocks of that kind. The coprocessors of the Vita that run their own
firmware are: the Allegrex (`pcbc`/`pcff`), Venezia (`os0:kd/vnzimg.skprx`), the VIP units (`os0:kd/vipimg.skprx`),
the secure processor (`os0:sm/*.self`), the Wi-Fi/Bluetooth chip (`os0:kd/wlanbt_robin_img_ax.skprx`), the syscon
and the motion sensor (their firmware is in the update package, not run by the ARM side). Only the Allegrex and
Venezia run code that does not come from Sony's firmware: PSP titles on the first, homebrew MeP code (VVNZRunner) on
the second.

## Summary

Classes: **(a)** missing hardware that blocks real functionality; **(b)** partial; **(c)** out of scope (a limit
recorded, not to be emulated); **ok** covered.

| Block | FW 3.74 driver | VitaSharp today | Class |
|---|---|---|---|
| ARM Cortex-A9 MPCore (4 cores, VFPv3, NEON) | the kernel itself | LLE: interpreter, IL and native JIT for A32/T16/T32/VFP/NEON (`Cpu/`), exception modes | ok |
| GPU SGX543MP4+ | `gpu_es4.skprx`, `SceGpuEs4`, `libgpu_es4`/`libgxm_es4` | GXM command streams drawn in Vulkan (`Gpu/`, `Gxm/`); `SceGpuEs4ForDriver` services as records, never locks up | ok (high level) |
| Secure processor (Toshiba MeP, "F00D") | `os0:sm/*.self`, `sm_comm`, `ss_mgr`, `SceSblFwLoader` | SELF/PFS decryption by the emulator's own code; secure commands refuse (`0x80020004`) or answer status 0 where no data is returned | (c) |
| PSP compatibility core (Allegrex) | `SceCompat`, `os0:kd/pcbc.skprx`, `vs0:app/NPXS10028/pcff.skprx` | the real ScePspemu runs up to `sceCompatInitEx`, which refuses: no core | **(a)** (roadmap item exists) |
| Venezia media processor (MeP + IVC2 cores) | `SceCodecEngineWrapper` loads `vnzimg.skprx`; `SceAvcodec`, `SceAudioin`, `SceNgs` use it | not emulated; the decoder libraries are HLE at user level (own ATRAC9, FFmpeg for AVC/MP3/AAC/MPEG-4/G.729); the wrapper's bookkeeping is HLE, its RPCs answer 0 | **(a)** for MeP code, **(b)** for codecs |
| VIP image units | `SceCodecEngineWrapper` ("SceVipDriverPu0/1") loads `vipimg.skprx` | not emulated; the camera and JPEG work is done by the HLE libraries | ok (high level); what it serves is to confirm |
| DMA controllers, DMAC5 crypto DMA | `dmacmgr.skprx`, `ss_mgr` (DMAC5) | copies in HLE; DMAC5 AES/DES/HMAC with a key in memory implemented; key slots filled by the secure processor refuse | ok; key slots (c) |
| Display: IFTU planes, DSI heads, OLED / LCD panel | `display.skprx`, `lowio` (IFTU, DSI), `oled.skprx`, `lcd.skprx` | SceDisplay HLE composes the planes; head 0 = OLED panel (DDB id 0); LCD answers "no panel" (PCH-1000) | ok (PCH-1000) |
| HDMI transmitter + CEC (PS TV) | `hdmi.skprx`, DSI head 1, `SceAVConfig` HDMI | "no transmitter, no monitor"; AVConfig HDMI calls `0x803A0109` (not a PS TV) | **(b)**, part of the PS TV model |
| Cameras (front/back), MIPI CSI-2, ISP | `SceCamera`, `lowio` (CSI) | host webcam / still picture / grey per camera (`Host/Camera.cs`, `CameraSetup.cs`); sensor registers kept | ok |
| Front touch screen, rear touchpad | `SceTouch` | host mouse/touch for both panels | ok |
| Motion: accelerometer, gyroscope | `SceMotionDev`, `SceMotion` | host gyro/accelerometer (SDL controller, phone sensors) | ok |
| Motion: magnetometer | `SceMotionDev`, `SceMotion` | host field (phone magnetometer on Android, scripted `heading@` on the PC): NED matrix and stable field while the magnetometer is on | done (was **(b)**); the stored calibration as 3.74; the raw sensor ring missing |
| GPS / location | `SceGps` (driver from the modem), `liblocation`, `location_provider` | off by default ("this application not allowed"); a fixed position (PC) or the phone's location (Android) when the setting gives one | **(b)** |
| 3G modem (PCH-1100) | `SceBbmc`, USB modem | absent, as on the Wi-Fi model the unit is (`0x80330003`) | (c) (faithful to the model; the 3G service is gone) |
| Wi-Fi infrastructure | `SceWlanBt`, `SceNetPs`, `wlanbt_robin_img_ax.skprx` | host sockets (`--net=real`), offline profile otherwise | ok |
| Wi-Fi ad hoc | `SceNetPs` ad hoc, `pspnet_adhoc`, `sceNetAdhocctl*`, Matching | offline profile: library works, no peer is ever found | **(b)** |
| Bluetooth | `SceBt`, `SceHid` | nothing paired: no headset, keyboard or mouse (`NoHardware.KernelServices.cs`) | **(b)** (keyboard/mouse; controllers under PS TV) |
| USB device port (multi connector) | `SceUdcd`, `usbstor*`, `SceUsbMtp`, `usbpspcm`, `usbdev_serial` | cable always disconnected (`0x10`) | **(b)** (Content Manager, USB storage) |
| USB host ports (PS TV) | `SceUsbd`, `SceUsbMass`, `SceUsbAudio`, `SceUsbEtherRtl/Smsc`, `SceUsbServ` | nothing attached | **(b)**, part of the PS TV model |
| Game card slot | `sdif` host 1, `gcauthmgr` | card dump served at the file level (`gro0:`/`grw0:`); authentication refuses | ok; authentication (c) |
| Memory card, internal eMMC | `msif.skprx`, `sdif` host 0, `sdstor.skprx` | partitions served at the file level; raw sector reads/writes fail (`0x803D0000`, `0x8032001A`) | **(b)** |
| Syscon, power, battery, RTC, buttons | `syscon.skprx`, `ScePower`, `rtc.skprx` | HLE with battery, AC, events, idle timers, power button, alarms | ok |
| Audio out, codec chip, headset jack | `SceAudio`, `SceCodec` (I2C 0x34), `SceHpremote` | host audio device; codec settings accepted; nothing in the jack | ok |
| Microphone | `SceAudioin`, `SceCodec` | host capture (OpenAL Soft on the desktop, `AudioRecord` on Android) resampled to the port's rate at the guest's grain pace; silent without a device | done (was **(b)**) |
| Controllers beyond the built-in pad, vibration, light bar | `SceCtrl`, `SceDs3`, `SceHid`, `SceBt` | only port 0 (the built-in pad); `sceCtrlSetActuator`/`SetLightBar` ignored; `sceCtrlIsMultiControllerSupported` = 0 | **(b)**, part of the PS TV model |
| Interrupts, timers, GPIO, I2C, clock generator, pervasive registers | `intrmgr`, `systimer`, `lowio`, `SceClockgen` | HLE models; system timers raise interrupts in guest time; GPIO/I2C have no devices behind them | ok |
| Console model (PCH-1000 / PCH-1100 / PCH-2000 / VTE-1000 PS TV) | SceSblAIMgr, syscon hardware info, per-model `vs0` | fixed PCH-1000 CEX (`Hle/ConsoleIdentity.cs`) | **(b)** (PS TV; PCH-2000's internal card) |

## The gaps, in detail

### (a) Allegrex compatibility core

Study, plan and phases in [psp-compat.md](psp-compat.md) and the roadmap item "Núcleo de compatibilidade PSP".
Impact: every PSP and PSone title, PSP homebrew, Adrenaline. The largest piece is the PSP GE.

### (a)/(b) Venezia media processor

- **What the firmware does:** `SceCodecEngineWrapper` (boot image) sets up the engine's memory windows, has
  `SceSblFwLoader` load `os0:kd/vnzimg.skprx` ("SceVeneziaImage") into them, starts the cores through the pervasive
  registers and waits for the engine to report state 8. The decoders (`SceAvcodec`: AVC, MPEG-4, MP3, AAC, ATRAC9,
  CELP, JPEG), the microphone path (`SceAudioin`) and `SceNgs` send RPCs to its firmware. The same module loads
  `os0:kd/vipimg.skprx` for its two VIP processing units ("SceVipDriverPu0/1"; what they serve is to confirm).
- **Today:** the libraries are HLE at user level; the wrapper keeps the per-process bookkeeping, the unmap memblocks
  and the transfer lists, and takes the RPCs (0) without running anything (`Hle/Modules/CodecEngine.Kernel.cs`).
- **(a) part:** code that sends its own MeP programs to Venezia has nothing to run on. The homebrew kernel plugin
  VVNZRunner does exactly that (an API to run Toshiba MeP code on up to 6 cores, 7 in the extended mode of
  `sceCodecEngineChangeNumWorkerCoresMax`), and homebrew built on it gets no result. By project rule the plugin runs
  unchanged (LLE), so the only path is emulating the engine. Unlike `pcbc`, `vnzimg.skprx` comes out of `self2elf`
  in plain form (measured entropy 4.4-5.5 bits/byte, readable strings): no key blocks it. The IVC2 coprocessor (the
  vector unit of each core) is documented nowhere public; VitaLoader Redux lists it as not implemented.
- **(b) part (codecs):** the CELP decoder takes the frames of its bit rate but outputs silence (no synthesis yet; no
  open MPEG-4 CELP implementation exists, the standard's tables come from the ISO/IEC 14496-5 reference software), and the CELP and G.729
  encoders produce zero frames; the split JPEG decoder is refused. Users: voice chat (`SceVoice`, Party), voice
  recording, games that record and play back the microphone.
- **Size:** MeP core interpreter [G]; IVC2 [G, unknown ISA]; running `vnzimg` to state 8 and serving RPCs [G]. CELP
  decoder from ISO/IEC 14496-3 [G].
- **First testable delivery:** a MeP-c5 core interpreter (the base ISA is documented by Toshiba's MeP manuals) on a
  guest thread, with a synthetic test program built in the test (our bytes: arithmetic, loads/stores, branches, a
  mailbox write the ARM side reads). For CELP: the frame-size table per bit-rate mode checked against the
  `SceAudiodec` contract, then decoded frames compared with values computed from the standard.

### (b) PS TV model (controllers, vibration, HDMI, USB host, Ethernet)

- **What the firmware does:** a PS TV (VTE-1000, "dolce") is told apart by the KBL identity (product sub code bit
  `0x200`, hardware info board kind `0x51`/`0x70`/`0x72`), which `ksceSblAimgrIsDolce` and the per-model `vs0` patches
  (target 2 of the firmware package) follow. On it `SceDs3` and `SceBt`/`SceHid`/`SceUsbd` serve up to four DS3/DS4
  controllers (ports 1-4, rumble through `sceCtrlSetActuator`, light bar), the HDMI transmitter and CEC are live,
  `SceCameraDummy`, `SceTouchDummy` and `SceMotionDevDummy` replace the handheld's devices, and the USB host ports and
  the USB Ethernet adapter work.
- **Today:** the model option (`--model=pstv`, `VITASHARP_MODEL`, Settings > Emulation > Model; `ConsoleModel`) makes
  the unit a PS TV: sub code bit 0x200 (IsDolce / IsGenuineDolce = 1), `sceCtrlIsMultiControllerSupported` = 1, ports
  1-4 = the host's controllers one by one, `sceCtrlSetActuator` to the controller's motors (SDL rumble). Missing: the
  PS TV `vs0`, the PS TV devices (camera/touch/motion dummies), HDMI ("a monitor that accepts every mode"), USB host,
  light bar, the hardware info board kind, Android controllers.
- **Impact:** local multiplayer with several controllers (titles that check `sceCtrlIsMultiControllerSupported` and
  read ports 1-4; which titles is to confirm), rumble on host gamepads, the PS TV's own system software and its
  settings (HDMI resolution, controllers, Ethernet).
- **Size:** [G] (model option, the PS TV `vs0` install, the controllers on host gamepads, rumble sink, HDMI as "a
  monitor that accepts every mode").
- **First testable delivery:** a console model option (one place, `ConsoleIdentity` from it, the variable in
  `EnvVars.cs`). Test: with the PS TV model `ksceSblAimgrIsDolce` = 1, the AVConfig HDMI calls stop answering
  `0x803A0109`, `sceCtrlIsMultiControllerSupported` = 1 and `sceCtrlGetControllerPortInfo` lists a scripted second
  host gamepad on port 1, and `sceCtrlSetActuator` on that port reaches a recording rumble sink; with the default
  model every answer stays as today.

### (b) Microphone

- **What the firmware does:** `SceAudioin` reads the codec chip's ADC through Venezia's audio path; ports of 16-bit
  mono at 16 or 48 kHz, one per process, adopted by AppMgr.
- **Today (done):** the port delivers the host's microphone (`Host/Microphone.cs`, `Host/MicrophoneFeed.cs`,
  `Hle/Modules/Audio.In.cs`; OpenAL Soft capture on the desktop, `AudioRecord` on Android), resampled to the port's
  rate, one grain per grain/rate seconds of guest time; muted, without a device or permission, or for a process the
  microphone is not adopted for, silence. Before: the samples were always zero
  ("virtual microphone (no host capture)").
- **Impact:** voice chat in online games and Party, Skype/Near voice, voice recording in apps, game mechanics that
  react to sound (titles to confirm).
- **Size:** [M]: a host capture interface next to the camera's (`Host/`), one implementation per system (WASAPI /
  PulseAudio or PipeWire / Core Audio / Android `AudioRecord`) and a "none" source.
- **First testable delivery:** the interface plus a scripted source in the test (a sine wave): `sceAudioInInput`
  returns those samples at the grain pace, the mute gives zeros, and no source gives zeros as today.

### (b) Wi-Fi ad hoc

- **Today:** `pspnet_adhoc` and Matching initialise and answer as with no peer around (`Hle/Modules/NetAdhoc*.cs`).
- **Impact:** local multiplayer between two units (`docs/notes/online-features.md` lists Persona 4 Golden, Minecraft,
  Sakatsuku and Soul Sacrifice Delta among the users); later the PSP titles' ad hoc through ScePspemu's WLAN channel.
- **Size:** [G]. **First testable delivery:** two emulated stacks in one test over host UDP loopback: one creates a
  group, the other finds it with `sceNetAdhocctlScan`, joins, and a PDP datagram goes both ways.

### (b) GPS / location

- **Today:** off by default, the application is "not allowed" (`Hle/Modules/Location.cs`); with the location
  setting (`--location=latitude,longitude`, Android "Localização do celular") the application is allowed and gets
  that position (`Host/Location.cs`); the location callbacks report it from the "SceLocationCallback" thread as 3.74
  (`Hle/Modules/Location.Callbacks.cs`: the first position, then every `distance` metres). Missing: the PC's
  location service, the heading callbacks (no compass in the service), the firmware path (liblocation LLE against the shell's provider, `SceGps` of a 3G model).
- **Impact:** Near, Maps and the location features of apps and games.
- **Size:** [M]: a host location interface (Windows Geolocation, GeoClue, Core Location, Android `LocationManager`)
  and a fixed position option. **First testable delivery:** a scripted position; with the application allowed,
  `sceLocationGetLocation` returns it with the method and accuracy given; without a source the answer stays as today.

### (b) Magnetometer

- **Today:** the host motion sample carries the magnetic field (phone magnetometer, scripted `heading@`); with the
  magnetometer on and a stable field, `sceMotionGetState` gives the NED matrix and STABLE (`Hle/Motion/MotionDevice.cs`).
  The calibration the shell's updater stores is kept as 3.74 (`sceMotionDevUpdateMagnCalibData` /
  `GetCurrentMagnCalibData`: 12 words and an update counter; the raw reads check their arguments as 3.74). Missing:
  the driver's raw sensor ring (the records `sceMotionDevRead`/`ReadForMagnCalib` copy: none yet) and the field in
  the fused quaternion.
- **Impact:** compass heading (Maps, Near, AR titles that use the north), `magFieldStability` and the NED matrix.
- **Size:** [M]. **First testable delivery:** the host motion reading gets a magnetic field; a scripted field gives
  the heading and a stable `magFieldStability` in `sceMotionGetSensorState`/`GetState`.

### (b) Bluetooth keyboard and mouse

- **Today:** with the option (`--bt-keyboard-mouse=on`, Settings > Emulation; Android: Ajustes) the host keyboard and
  mouse are the paired devices: `sceHid{Keyboard,Mouse}Enumerate/Read/Peek` as FW 3.74 (`Hle/Modules/Hid.cs`,
  `Host/Hid.cs`), with the hint callbacks notified; SceBt lists them (`sceBtGetRegisteredInfo`) and queues their
  connections and disconnections as events 5 and 6 for `sceBtReadEvent` (`SystemServicesVsh.BluetoothEvents.cs`).
  Consumer controls (`sceHidConsumerControl*`) answer as 3.74 with no such device paired; the controller calls answer
  0x803B0004, as 3.74. Missing: a host source of consumer-control keys (media keys), the shell's Bluetooth settings
  screen checked by hand.
- **Impact:** typing in the browser, the IME and apps that read a keyboard directly; mouse in the browser.
- **Size:** [M]. **First testable delivery:** the host keyboard offered as one paired keyboard (an option):
  `sceHidKeyboardEnumerate` returns one handle and `sceHidKeyboardRead` reports scripted key presses.

### (b) USB device port (Content Manager, USB storage)

- **Today:** the cable is never connected.
- **Impact:** Content Manager with a PC or a PS3 (backup, transfer), USB storage mode, PSP-to-PS3 features.
- **Size:** [G] (an emulated host on the other end of the cable). **First testable delivery:** a "cable connected"
  event: `SceUdcd` moves to `0x20` and the state wait returns, and a scripted host completes one control request.

### (b) Memory card and eMMC as block devices

- **Today:** every partition is a host folder; raw sector access fails as a failed card command.
- **Impact:** what the firmware does at the block level: formatting the memory card from Settings, the system update
  from Settings writing `os0`/`vs0`, homebrew that reads or writes raw partitions (the storage drivers of the scene).
- **Size:** [G]. **First testable delivery:** a log of every sector access per caller (to list the real users), then
  an image file as the backing of one `sdstor` device: a sector written is read back, out of range answers as the
  module does.

## Out of scope (c)

- The secure processor and per-console keys: VitaSharp does not emulate the secure processor nor derive keys. What
  needs it (DMAC5 key slots, game card authentication, licence activation, decrypting `pcbc`'s inner layer) answers
  as a retail unit without the token. This is a limit, recorded here and in `psp-compat.md`.
- The 3G modem: the emulated unit is the Wi-Fi model, and the 3G service no longer exists.
- Factory and development hardware (Deci4p, the UART console, the test hooks).

## Order suggested

1. Allegrex (existing item, phase 1).
2. Microphone (smallest change with the widest effect).
3. PS TV model (several controllers, rumble, HDMI, PS TV software).
4. Wi-Fi ad hoc.
5. GPS/location, magnetometer, Bluetooth keyboard.
6. Venezia MeP execution, CELP, block storage, USB device port.
