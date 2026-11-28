<!-- pair: development.pt-BR.md sha256=f07dfcbc75f04bc6fed3d86189b182b6ec53f3ce3a4312b333066835825d0247 -->
# Development

**English** | [Português](development.pt-BR.md)

How to build, test and change VitaSharp; back to the [README](../README.md). Phone builds:
[`docs/android/README.md`](android/README.md). Diagnostic and performance tools:
[`docs/notes/developer-tools.md`](notes/developer-tools.md).

**Contents:** [Build and test](#build-and-test) · [Before a push](#before-a-push) · [Code style](#code-style) ·
[Developer options](#developer-options) · [Test requirements](#test-requirements) · [Releases](#releases)

## Build and test

Needs the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0). The first build downloads FFmpeg (videos
and MP3/AAC); the other native libraries come from NuGet. On macOS FFmpeg comes from the system (`brew install
ffmpeg`), and Vulkan from MoltenVK (`brew install molten-vk vulkan-loader`).

```sh
dotnet build -c Release
dotnet run --project src/VitaSharp.Desktop -c Release          # opens the launcher
dotnet run --project src/VitaSharp.Desktop -c Release -- run <TITLEID>   # opens a game directly
dotnet test tests/VitaSharp.Tests -c Release --filter "Category!=Slow"  # quick tests (without the CPU fuzz)
build/coverage.ps1                                              # test coverage report (HTML + summary; also .sh)
```

The command line also has `install`, `install-firmware`, `boot-shell`, `test <scenario>` (automatic scenarios in
[`tests/scenarios`](../tests/scenarios)), `replay-gpu`, `rerun`, `verify`, `imports`, `update` and more:
`VitaSharp.Desktop --help`. Ready packages (zip, installer, tar.gz, AppImage, .app, .dmg; ReadyToRun or Native AOT):
`build/publish.ps1` / `build/publish.sh`.

Interface texts live in `src/VitaSharp.Core/Localization` (`Texts.resx` in English, `Texts.pt-BR.resx`); the external
docs come in both languages too (`README.md` + `README.pt-BR.md`, and every `docs/*.md` with a `docs/*.pt-BR.md`,
checked by `ExternalDocPairsTests`: the first line of each holds the SHA-256 of the other, and the failure prints the
lines to write). To contribute system functions, see the specifications in [`docs/hle`](hle/README.md).
`build/hle-abi-check.ps1` / `build/hle-abi-check.sh` lists the HLE functions whose argument registers differ from the
[vita-headers](https://github.com/vitasdk/vita-headers) prototypes (needs `git` and network for the clone, or a local
checkout). `build/hle-status.ps1` / `.sh` regenerates the README's progress badges and maps; `-CompatOnly` / `--compat-only`
redoes only the Compat map, from `docs/compat/coverage.json`, without the firmware and games.

## Before a push

Every script comes as a `.ps1` + `.sh` pair, checked locally by `build/prepush-check`. Before a push run
`build/prepush-check.ps1` (Windows) / `build/prepush-check.sh` (Linux, macOS, Git Bash): on exactly what is committed
it runs script parity, the scripts' `--help`, ShellCheck, the code style, a 0-warning build and the `Category!=Slow`
suite, which fails when a test fails, when the test host crashed (an aborted run) or when fewer tests ran than
`dotnet test --list-tests` lists; push only after "prepush-check: ok". On Windows it also runs the suite on Linux
through WSL when WSL has a .NET SDK in `~/.dotnet` (`dotnet-install.sh --channel 10.0 --install-dir ~/.dotnet`) and the
Linux [test requirements](#test-requirements) (Debian 12 needs the newer Mesa of bookworm-backports). No step writes a
crash dump (Linux: core size 0 and `coredump_filter` 0, as WSL's crash collector ignores the size limit).

## Code style

The C# style is the Ryujinx/Ryubing one, and [`.editorconfig`](../.editorconfig) is the rule (what is enforced, what is
off and why). `build/format-check.ps1` (Windows) / `build/format-check.sh` (Linux, macOS) runs
`dotnet format whitespace` and `dotnet format style --severity warn` with `--verify-no-changes` over the tracked `.cs`
files only (`git ls-files`: never untracked or git-ignored files), as the CI job "Code style" does (that job is
commented out in `.github/workflows/ci.yml` for now: run it locally), with the same exit code (2 when something
differs); `-Fix` / `--fix` rewrites the files instead (also running `build/NamingTool`, which renames the symbols that
break the naming rules with all their references, since `dotnet format` reports IDE1006 but cannot fix it, and
`build/FormatTool`, which splits the blocks written on one line and keeps `{ get; set; }` on one line; the fix needs
the NuGet packages of `build/NamingTool`, restored on its first run). Folders not converted yet are listed in
[`build/format-pending.txt`](../build/format-pending.txt) and skipped. The file list goes to `dotnet format` as a
response file, and the scripts run the `dotnet-format.dll` of the active SDK (`<sdk>/DotnetTools/dotnet-format/`)
directly so that it reads that file itself: `dotnet format` would put every path on one command line, longer than
Windows allows once the whole tree is listed (other SDK layouts fall back to `dotnet format`). `OneLineBlockTests` (in
the test suite) rejects blocks written on one line (`if (x) { a; }`): every body goes on its own lines.

**Options on both apps:** every new user option (settings, per-game settings, pause menu, controls) goes into the
desktop launcher and the Android app in the same change, with the same name, default and meaning (the texts come from
the shared `Texts.resx`); a test checks that both map it to the same emulator setting (`AndroidOptionParityTests`).

Shell scripts follow the Google Shell Style Guide and must pass ShellCheck (CI job "ShellCheck"; the job
"Scripts on macOS" also runs `build/check-scripts-help.sh` under the system bash 3.2). Install ShellCheck with
`winget install koalaman.shellcheck` (Windows), `sudo apt install shellcheck` / `sudo dnf install ShellCheck`
(Linux) or `brew install shellcheck` (macOS), then `shellcheck --severity=style build/*.sh build/package/*.sh`.

## Developer options

- IL backend dump: `VITASHARP_IL_DUMP=<folder>` writes the compiled guest blocks as assemblies to read with ILSpy
  (`dotnet tool install -g ilspycmd`, then `ilspycmd -il <file>.dll`); `DOTNET_JitDisasm=<method>` prints the
  machine code RyuJIT generates. See [`docs/notes/il-jit.md`](notes/il-jit.md).
- Vulkan validation: `--validation` (`run`, `test`, `replay-gpu`) or `VITASHARP_VK_VALIDATION=1` (`=sync` adds
  synchronization validation) needs the Khronos layer: the [Vulkan SDK](https://vulkan.lunarg.com/sdk/home)
  (Windows, Linux, macOS), or on Linux the distribution package (`vulkan-validationlayers` / `vulkan-validation-layers`);
  on macOS the SDK also brings MoltenVK. Frame captures: [RenderDoc](https://renderdoc.org) (Windows, Linux) and
  GPU dumps (`replay-gpu`, [`docs/notes/gpu-dump.md`](notes/gpu-dump.md)) on every OS.
- Precise wait timeouts (`VITASHARP_PRECISE_TIMEOUTS=0` turns them off, A/B): a timed wait on a kernel object
  (sceKernelWaitSema, WaitEventFlag and the other `timeout` arguments, sceKernelDelayThreadCB) ends at its microsecond
  deadline, as threadmgr's timer does on the Vita, instead of at `Monitor.Wait`'s next whole millisecond (a 500 us
  timeout used to last 1.1-1.3 ms). One host thread, "HLE timeouts", sleeps on a high-resolution timer and wakes the
  waiter: a `timerfd` on Linux and Android, a high-resolution waitable timer on Windows (it fires on a 0.5 ms tick, so
  a timeout there can still end up to 0.5 ms late). Nothing spins. macOS keeps the millisecond rounding: it has no
  timerfd, and the equivalent (a kqueue `EVFILT_TIMER` with `NOTE_USECONDS`, re-armed while the thread waits) is not
  written yet. Short host sleeps (`PreciseSleep`) spin only the timer's measured lateness on the desktop (tens of
  microseconds on Linux and macOS, ~0.5 ms on Windows) and never on phones; on Linux and Android the threads that
  sleep get a 1 us timer slack.
- Graphics pipeline libraries (off by default, measured slower with cold caches): `VITASHARP_PIPELINE_LIBRARY=1`
  links first-use pipelines from shader libraries and optimises them in the background where the driver has
  `VK_EXT_graphics_pipeline_library` with fast linking (desktop NVIDIA/AMD/Intel, Mesa); without it (MoltenVK,
  Dozen, phone drivers) pipelines are compiled whole either way. The `[shadercache] session` log line counts what the
  GPU thread compiled; see [`docs/notes/pipeline-skip.md`](notes/pipeline-skip.md).
- Compile shaders at program load (`--compile-on-program-load=on|off`, default on;
  `VITASHARP_COMPILE_ON_PROGRAM_LOAD=0|1` overrides it, also on Android): when a game creates a vertex or fragment
  program (sceGxmShaderPatcherCreate*, usually behind its loading screen), the shader workers translate and compile its
  shaders with the hints predicted from the program, and build the pipeline of the vertex program it is made for, so a
  first session's first draw finds them ready. A draw whose texture or surface format differs from the prediction but
  translates to the same shader uses the same module. The colour surfaces the game initialises and the scenes it
  begins are remembered, and the programs that depend on the surface (programmable blending, native-colour or integer
  outputs) are also built for each of them, with their fetch-pass and integer-view pipelines. The session's
  `[shadercache] session` line ends with what was queued and used ("at program load: ..."); `replay-gpu
  --compile-on-program-load=on` announces the dump's programs before its first command. Nothing extra to install,
  every OS.
- Shader workers (`--shader-workers=N` fixes the count, `VITASHARP_SHADER_WORKERS=N` overrides it): by default the
  count follows the CPU budget, at most 90% of the logical processors the process may use. While the GPU thread works,
  two of them stay with the guest CPU thread and the GPU thread (8 processors: 5 workers). While it waits for a shader
  or pipeline, or the game waits for the shader cache load, the whole budget compiles (8 processors: 7): the workers
  above the steady share are spare ones at below-normal priority, so they only take idle cores
  (`VITASHARP_SHADER_SPARE_WORKERS=N` sets how many, A/B). On Android fewer run, on the LITTLE cores (2 big + 6 LITTLE:
  2, 4 while waiting). The renderer's start line (`[gpu] shaders: ...`) gives the numbers. Nothing extra to install,
  every OS.
- The user-facing loading options (loading window, shader cache load, ARM code precompilation) are in the
  [user guide](user-guide.md#graphics-and-loading).
- Profiling: `dotnet-trace` / `dotnet-counters` (`dotnet tool install -g dotnet-trace`, every OS); on Windows also
  PerfView and WPR/WPA (Windows Performance Toolkit). More in [`docs/notes/developer-tools.md`](notes/developer-tools.md).
- Every environment variable is declared in `src/VitaSharp.Core/Util/EnvVars.cs`, with what it does.

## Test requirements

What `dotnet test tests/VitaSharp.Tests` needs per system; a test whose requirement is missing is skipped and says why.

| | Windows | Linux | macOS |
|---|---|---|---|
| SDK | .NET 10 SDK | .NET 10 SDK | .NET 10 SDK |
| Vulkan device (renderer, GPU dump and game-run tests) | Mesa lavapipe (Vulkan 1.4, the Linux CI's driver): unpack the `x64` folder of a [mesa-dist-win](https://github.com/pal1000/mesa-dist-win/releases) release (`vulkan_lvp.dll` + `lvp_icd.x86_64.json`) into `%LOCALAPPDATA%\VitaSharpTools\mesa\<version>\x64`; without it, or when the system loader `vulkan-1.dll` cannot take it, SwiftShader as Microsoft Edge or Google Chrome ship it (both found by themselves) | Mesa lavapipe newer than 22.3 (the CI has 25.2): `sudo apt install mesa-vulkan-drivers libvulkan1` (Fedora: `mesa-vulkan-drivers vulkan-loader`; Debian 12: `sudo apt install -t bookworm-backports mesa-vulkan-drivers`, its own 22.3 crashes the texture tests) | MoltenVK: the LunarG Vulkan SDK, or `brew install molten-vk vulkan-loader` |
| shaderc (renderer and shader-compile tests; the renderer needs it too) | from the Silk.NET.Shaderc.Native package | from the package (found under `runtimes/linux-x64` also where Silk.NET's search misses it, as on Ubuntu); when that copy does not load: `sudo apt install libshaderc-dev` (Fedora, Arch: `shaderc`; loaded as `libshaderc.so.1`) | from the package, else Homebrew's `brew install shaderc` |
| A desktop window (game overlay tests) | always | an X11 or Wayland display; headless: `xvfb-run dotnet test tests/VitaSharp.Tests` (`sudo apt install xvfb`) | skipped: AppKit creates windows only on the main thread, and the tests run on worker threads |
| OCR (`screen-text`, `ScreenTextTests`, the F8 report's screen text) | the system's Windows.Media.Ocr (Windows 10/11, through Windows PowerShell 5.1) with an OCR language installed (Settings > Time & language > Language; Japanese for Japanese menus) | `sudo apt install tesseract-ocr` (`tesseract-ocr-jpn` for Japanese) | `brew install tesseract` (`tesseract-lang` for Japanese) |
| FFmpeg (video and audio decode tests) | downloaded by the first build | downloaded by the first build | the system's: `brew install ffmpeg` |

- Any system: `VK_DRIVER_FILES` (or `VK_ICD_FILENAMES`) set to the ICD manifest of a downloaded SwiftShader or Mesa
  build is used instead.
- `RendererPipelineLibraryTests` checks the linked pipelines where the software device has the extension with fast
  linking (SwiftShader, Mesa lavapipe) and the whole-pipeline fallback everywhere else.
- Firmware: without it the tests of the real firmware modules (`libSceJson`, `libSceXml`, `libpaf`, the update file
  install, the `sa0` fonts; `dbutil` then runs the HLE side only) are skipped. `VITASHARP_FIRMWARE_PUP` names a
  `PSVUPDAT.PUP`, `VITASHARP_FIRMWARE_DIR` a `vs0` or Vita3K data folder; the Piglet compiler tests need
  `libshacccg.suprx` in `ur0:data` of Vita3K's data folder or of the folder named by `VITASHARP_TEST_INSTALLED_DATA`.
- `VITASHARP_TEST_INSTALLED_DATA` (optional, read only): a VitaSharp data folder with installed games and firmware for
  the tests of real installed modules (`LleLibrariesTests`, the Piglet compiler); unset, they are skipped.
- The microphone tests (`MicrophoneTests`) use a scripted tone on a clock the test moves: no capture device, no
  permission and no sound card are needed on any system.
- The location tests (`LocationSourceTests`) use a fixed position and a source the test moves: no location service
  and no permission are needed on any system.
- The tests never use your data folder: the test assembly points `VITASHARP_DATA` at a new temporary folder of the
  run before any test starts (also over a value set in your shell), so `input.json`, `launcher.json`, logs, shader
  cache and save states are the run's own; tests that need a folder of their own make one, and the child processes
  get `--data=`. The launcher's Vita3K detection finds nothing in the tests. `TestDataIsolationTests` fails if a test
  resolves or builds the path of your data folder. The game-run tests boot a small executable the tests write
  themselves.
- Optional: the LunarG Vulkan SDK's validation layers run the renderer workloads under validation with
  `dotnet test tests/VitaSharp.Tests --filter Category=Slow` (`RendererValidationTests`; skipped without the layer).
- The window test runs a hidden window (`VITASHARP_HIDDEN_WINDOW=1`): on Linux it needs a display (`DISPLAY`), on
  Windows the system Vulkan loader (`vulkan-1.dll`, installed with any GPU driver), which gets the software driver
  from `VK_DRIVER_FILES`. An elevated process (Administrator) makes the loader ignore that variable: run the tests
  unelevated, or register the driver's `*_icd.json` as a DWORD value `0` under
  `HKLM\SOFTWARE\Khronos\Vulkan\Drivers` (the CI does this for Edge's SwiftShader); otherwise the test is skipped. It
  is skipped on macOS. An elevated process also takes SwiftShader instead of an unregistered lavapipe.
- Windows' lavapipe has no `VK_EXT_headless_surface`: the test of a window-less renderer
  (`RendererDeviceHandoffTests`) is skipped with it and runs with SwiftShader (the Windows CI) and on Linux.
- Media parity (`MediaParityTests`): the desktop side (FFmpeg) always runs; the Android side needs the developer APK
  (`-p:VitaSharpDeveloper=true`) on a phone or the Android Emulator (any system with the Android SDK: an x86_64
  Google APIs image and the `android-x64` build). `build/android-media-parity.sh` (or `.ps1`) records the trace on the
  device; `VITASHARP_ANDROID_MEDIA_TRACE=<trace> dotnet test tests/VitaSharp.Tests --filter MediaParityTests` compares
  it with the desktop's (skipped without the variable). `SYNC=0` (`-Sync 0`) records the old MediaCodec timing.
- Audio port parity (`AudioPortParityTests`): one port's grains at the port volume through OpenAL Soft's mixer (read
  from an `ALC_SOFT_loopback` device, no sound card needed; the bundled OpenAL Soft has it on every system) and through
  the Android stream (`BufferedPcmStream`, the buffering and volume in front of AAudio/AudioTrack) must give the same
  PCM within one 16-bit step. Skipped with the reason when OpenAL Soft does not load.
- .NET 11 (optional, for comparisons; the default and the CI stay on .NET 10): `-p:VitaSharpDotnet=net11.0` builds
  every project against .NET 11 on any system. It needs the .NET 11 SDK (and, for Android, its `maui-android` workload
  and the Android API 37 platform; .NET 11 has no Mono, so only `VitaSharpAndroidRuntime=coreclr` or `nativeaot`
  build). An SDK installed outside the default location runs the desktop app with `DOTNET_ROOT` set to it.

### ARM64 code on an x64 PC (qemu-user)

The ARM64 code paths (the native ARM64 JIT backend, the .NET ARM64 runtime the phone uses) run on an x64 PC without a
phone under qemu-user: `build/qemu-arm64.sh` (Linux) / `build/qemu-arm64.ps1` (Windows, through WSL) builds the test
project for `linux-arm64` and runs it with an arm64 test host under `qemu-aarch64`; there the ARM64 backend's tests
execute the generated code natively (`Arm64HostRunner`) instead of in the Unicorn sandbox of the x64 runs (default
filter: `NativeJit`, `Vfp`, `Neon`, `Arm64`, `HostCodeCoherence`; `--filter` / `-Filter` picks others). `--testgame <vpk>` / `-TestGame`
also publishes `VitaSharp.Desktop` for `linux-arm64` (self-contained) and boots the test game headless under qemu
(`--seconds`, default 300; the log gets a `# run under qemu-user` line). Logs, the `.trx` and the test game's
`results.txt` go to `--out` / `-Out`. qemu emulates the processor, so times are not the phone's; only results count.

| | Windows | Linux | macOS |
|---|---|---|---|
| Requirements | WSL 2 (Debian/Ubuntu) with a .NET 10 SDK in `~/.dotnet`; `-Install` does the rest (as root in WSL, no password) | x64 Debian/Ubuntu with a .NET 10 SDK; `--install` (sudo): `qemu-user-static binfmt-support` and the `arm64` libraries (`libc6 libstdc++6 libgcc-s1 libicu libssl3 zlib1g`, multiarch), qemu-user 10 in `~/.qemu-arm64` (`QEMU_ARM64_DIR`; `--install-qemu`, no sudo), plus the arm64 .NET runtime in `~/.dotnet-arm64` (`DOTNET_ARM64_ROOT`) | not applicable: no qemu-user; Apple silicon runs `dotnet test` natively on ARM64 |

**qemu 10, not 7.2:** qemu-user 7.2 (Debian bookworm's `qemu-user-static`) keeps running its translation of code
that the JIT rewrites through the second (read/write) mapping of its W^X code arena (a range reused after
`ExecutableMemory.Free`, a word patched by linking): it only notices writes to the address the code runs at, and IC
IVAU does nothing there. The old code's literal loads then read the new bytes and branch to an instruction pair taken
as an address (SIGSEGV, e.g. pc `0x806b01009f` with x16 = `0x540000806b01009f` in `Arm64SuperblocksMatchInterpreter`;
also `Arm64GroupedDirtyMarksCoverStores`, `Arm64MediaMatchesInterpreter`, the VFP short-vector and vector-cache tests).
On hardware the arena's cache maintenance (DC CVAU, DSB ISH, IC IVAU, DSB ISH, ISB) makes the new code run;
`HostCodeCoherenceTests` checks those rewrites on every host. `--install` / `-Install` put Debian trixie's static
qemu-user 10 (10.0.13 tested; 10.0.2 too) in `~/.qemu-arm64` and the script registers it as the arm64 binfmt
interpreter; it exports `VITASHARP_QEMU_USER` (the version), and under a qemu that runs stale code the ARM64 host tests
skip themselves with that reason instead of crashing the test host (only then: without qemu a stale run fails).
WSL without systemd loses the `qemu-aarch64` binfmt entry when the distribution restarts: the `.ps1` registers it
again on each run (`qemu-arm64.sh --register-binfmt` as root, or `update-binfmts --enable qemu-aarch64` without
qemu 10). `QEMU_CPU` picks qemu's processor model
(default `max`). The tests run on the portable test platform (`Microsoft.TestPlatform.Portable`, in
`~/.vstest-portable`) started by the arm64 runtime: the SDK's `vstest.console.dll` is built for x64, and the x64 one
finds no arm64 host on Linux. The script sets `DOTNET_EnableWriteXorExecute=0` (qemu-user runs stale code that the .NET
JIT writes through the second mapping of its W^X code heap) (the test assembly itself asks the ARM64 host runtime for the
counting trampoline, `Arm64Runner.CountBlocksOnArm64Hosts`). `/Blame` names the test a crash stopped in.

## Releases

- **Versions**: a `v*` tag (or **Actions > Release > Run workflow**) publishes a release with the Native AOT packages
  for Windows, Linux and macOS, the Android APK (`VitaSharp-<version>-android-arm64.apk`) and the `SHA256SUMS.txt`.
- **Automatic builds**: every push to `main` whose CI passed becomes a `build-<commit>` prerelease with that CI run's
  packages (the 10 newest stay).
- The automatic update the packages run is described in the [user guide](user-guide.md#updates).
- The desktop packages are not signed (Windows SmartScreen warns) and never include firmware. The APK is signed with
  the repository's fixed keystore (secrets `ANDROID_KEYSTORE_BASE64`, `ANDROID_KEYSTORE_PASS`, `ANDROID_KEY_ALIAS`;
  [Signing](android/README.md#signing)); a fork without them signs with a new debug key each run.
