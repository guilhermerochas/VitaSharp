# VitaSharp test game

*[Português](README.pt-BR.md)*

A PS Vita homebrew that exercises the parts of the system an emulator has to get right, shows OK / FAIL for each
check on screen and writes the results to a machine-readable file. It was made to test the VitaSharp emulator
automatically, and runs the same way on a real PS Vita with HENkaku /
h-encore (install the `.vpk` with VitaShell).

It is a plain homebrew built with the open-source [VitaSDK](https://vitasdk.org): no Sony SDK, no Sony headers, no
decompiled code, no signing, no licence or DRM handling. The package is VitaSDK's normal `vita-make-fself` /
`vita-pack-vpk` output (title ID `VSTEST001`, safe homebrew).

## What it checks

At boot the app runs every check once (about 30 s, plus up to 20 s for the input window) and then shows a menu where
each area can be run again, the live input values can be watched and the results browsed.

| Area | Checks |
| --- | --- |
| `gfx` | raw `sceGxm`, drawn offscreen and read back by the CPU after `sceGxmFinish`: clear, triangles, strips, fans, lines, vertex colour interpolation, CW/CCW culling; 28 texture cases (U8U8U8U8 ABGR/ARGB/X8, U8U8U8, 565 RGB/BGR, 4444, 1555, 2-10-10-10, U8, U8U8, F16x4, P8 and P4 palettes, UBC1/2/3, ETC1) in linear, linear strided, swizzled and tiled layouts; samplers (point, linear, repeat / mirror / clamp, mipmaps); alpha, additive and multiply blending, colour masks; depth less / greater / write off; stencil replace + equal; render to texture; 4x MSAA edges |
| `input` | `sceCtrl` peek and sampling mode, sticks at rest; every button, both sticks, front and rear touch and a tilt seen during a 20 s window (ends early when all were seen; START+SELECT stops it); touch panel info; motion state and 1 g at rest; a live screen with every value |
| `audio` | `sceAudioOut` MAIN 48 kHz and BGM 44.1 kHz ports playing a 1 kHz and a 1.5 kHz tone at the same time from two threads, the measured block period of each against the clock, volume and config calls, bad-rate error, rest samples, VOICE mono port |
| `files` | `sceIo` mkdir, 64 KB write / read back, `getstat` size / type / time, `lseek` set / end / cur, append, `dopen` / `dread`, rename, remove, rmdir, missing file error; `app0:` reads (and that it is read-only); `savedata0:` write / read, save slot create / get param; the system save data dialog |
| `threads` | thread create / start / exit status, mutex (two threads, try-lock, recursive), lightweight mutex, semaphores (producer / consumer, poll, info, wait timeout), event flags (AND wait, OR + clear poll, set / clear), message pipe (32 messages in order, empty try-receive), condition variable, `sceKernelDelayThread` 1 / 10 / 100 ms against the clock, RTC against the process clock, monotonic process time |
| `system` | RTC local time, tick, resolution, leap years, days in month, day of week, tick round trip; system parameters (language, enter button, date / time format, time zone, daylight savings, user name length); battery and clocks; display refresh rate and 30 vblanks; free memory and model; network module, `sceNetCtl` state (no traffic); message dialog; IME dialog |
| `perf` | a heavy GXM scene (about 6000 depth-tested rotating triangles, four full-screen alpha layers, text) with an fps / frame-time counter: average fps (OK at 29 or more), worst frame, frames over 34 ms. 5 s in the automatic run, 60 s from the menu |

Not covered yet (recorded as SKIP): PVRTC, YUV and cube textures. A vitaGL path was left out: the raw GXM path
already covers what vitaGL would draw, and vitaGL's own shader compiler needs a system module that homebrew has to
extract from the console.

### Results file

`ux0:data/VSTEST/results.txt` is rewritten after each area. One line per check, tab separated:

```
area<TAB>check<TAB>OK|FAIL|SKIP<TAB>measured value
```

Lines starting with `#` are comments; a complete run ends with `# done ok=N fail=N skip=N`. Every line is also
printed to the system log (`sceClibPrintf`) prefixed with `VSTEST`, so an emulator log shows them too. The user name
is never written (only its length).

SKIP means the check could not be decided without the user (input items when the window is stopped with
START+SELECT, battery values a console without a battery reports as 0, `app0:` writable when installed as unsafe).

### Layout conventions the test assumes

- Swizzled textures: Morton order with y in bit 0 and x in bit 1 (block-compressed formats swizzle whole 4x4 blocks).
- Tiled textures: 32x32-texel tiles stored row by row, texels inside a tile row by row.
- P4 textures: the even texel in the low nibble.

These follow how Vita3K decodes the formats; a run on a real console confirms or refutes them.

## Build

The build needs VitaSDK (`arm-vita-eabi-gcc`, `vita-elf-create`, `vita-make-fself`, `vita-mksfoex`,
`vita-pack-vpk`), `make`, `git`, `curl` and `bash`. CMake is not needed. The output is
`build/VitaSharpTest.vpk`.

### Linux

```sh
sudo apt install git curl make python3     # Debian / Ubuntu (Fedora: dnf, Arch: pacman)
./build.sh --install-sdk                   # first time: installs VitaSDK into ~/vitasdk with the official vdpm bootstrap
./build.sh                                 # later builds
```

### macOS

```sh
xcode-select --install                     # git, make, curl
./build.sh --install-sdk
./build.sh
```

### Windows (WSL)

VitaSDK runs inside WSL 2 (Debian or Ubuntu; `wsl --install -d Debian`). Inside the distribution:
`sudo apt install git curl make python3`. Then, from PowerShell in this folder (`tests/VitaSharp.TestGame`):

```powershell
.\build.ps1 -InstallSdk                    # first time: installs VitaSDK into ~/vitasdk inside WSL
.\build.ps1                                # later builds (-Clean rebuilds everything, -Distro picks another distribution)
```

`build.ps1` runs `build.sh` inside WSL. Set `VITASDK` to use a VitaSDK installed elsewhere.

## Run

- **Real PS Vita** (HENkaku / h-encore, any firmware they support): copy `build/VitaSharpTest.vpk` to the console,
  install it with VitaShell and start *VitaSharp Test*. Results: `ux0:data/VSTEST/results.txt`.
- **VitaSharp**: `VitaSharp.Desktop run build/VitaSharpTest.vpk` installs and boots it in a window. For an automatic
  run without a window and without touching your own data folder:

  ```sh
  VitaSharp.Desktop run build/VitaSharpTest.vpk --reinstall --data=<scratch folder> --vulkan --seconds=75 \
      --dialogs=auto --ime-text=VitaSharp --dump-frames=<frames folder> --dump-every=120 --log=<log file> \
      --input=cross@f60:10,circle@f75:10,square@f90:10,triangle@f105:10,up@f120:10,down@f135:10,left@f150:10,right@f165:10,ltrigger@f180:10,rtrigger@f195:10,start@f210:10,select@f225:10,lstick@f240:1:0:15,rstick@f260:0:-1:15,tap@f280:0.5:0.5:15,btap@f300:0.3:0.6:15,tilt@f320:40:20:60
  ```

  `--vulkan` is needed headless (without a renderer nothing is drawn and the read-back checks fail). The results
  file is then `<scratch folder>/fs/ux0/data/VSTEST/results.txt`, and the log has every `VSTEST` line. The input
  window opens a few frames after the automatic dialogs (the log says at which frame: `VSTEST input window opens at
  frame N`), so the steps above, timed in presented frames, land inside it. Firmware is optional: with the
  `os0`, `vs0` and `sa0` folders of an installed firmware in the scratch folder (junctions or copies) the run is the
  same.

## Folder layout

```
src/                  the app (C11, VitaSDK)
  main.c              boot, automatic run, menu
  app.c, app.h        pad, progress frames, result lists, dialog loop, test entry points
  gfx.c, gfx.h        raw sceGxm renderer: display, shader patcher, offscreen targets + readback, rectangles, text
  results.c, .h       results table, log lines, results.txt
  test_*.c            one file per area (graphics, input, audio, files, threads, system, perf)
  savedata_dialog.h   sceSaveDataDialog declarations (VitaSDK has the stubs but no header)
  shaders.S           embeds the precompiled shaders
third_party/
  vita2d-shaders/     precompiled GXP shaders (+ their Cg sources) from libvita2d, MIT
  font8x8/            8x8 bitmap font, public domain
assets/icon0.png      LiveArea icon (generated, 128x128 indexed)
Makefile              build rules (no CMake)
build.sh, build.ps1   build scripts (Linux / macOS; Windows through WSL)
```

## Dependencies and licences

| Dependency | Used for | Licence |
| --- | --- | --- |
| [VitaSDK](https://github.com/vitasdk) toolchain, newlib and stub libraries | compiler, C library, system call stubs, packaging tools | MIT / GPL (tools) / newlib licences, as each VitaSDK package states; nothing of it is copied into this repository |
| [libvita2d](https://github.com/xerpi/libvita2d) shaders (`third_party/vita2d-shaders`) | the four precompiled GXP shader pairs (clear, colour, texture, tinted texture) | MIT (`third_party/vita2d-shaders/LICENSE`) |
| [font8x8](https://github.com/dhepper/font8x8) (`third_party/font8x8`) | the on-screen text | public domain |

The shader binaries were extracted from libvita2d's prebuilt objects (`libvita2d/shader/compiled/*_gxp.o`); their Cg
sources are next to them.

## Licence

Part of the VitaSharp repository, under its licence (GNU GPL v3, see `LICENSE` at the repository root).
The third-party files keep their own licences listed above.
