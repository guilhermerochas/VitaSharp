<!-- pair: user-guide.pt-BR.md sha256=a6f675d1a5465548744fca939effa87ce50f23d939cd4732f8ebe6cd968e8c8d -->
# User guide

**English** | [Português](user-guide.pt-BR.md)

The details behind the [README](../README.md): every option, where each file goes and the Android app. Start with the
README's [Quick start](../README.md#quick-start); come here when you need more.

**Contents:** [Games and where they go](#games-and-where-they-go) · [Firmware](#firmware) ·
[Vita System](#vita-system) · [Language](#language) · [Graphics and loading](#graphics-and-loading) ·
[Console model](#console-model) · [Microphone](#microphone) · [Location](#location) ·
[Bluetooth keyboard and mouse](#bluetooth-keyboard-and-mouse) · [Ad hoc (local multiplayer)](#ad-hoc-local-multiplayer) ·
[Kernel plugins](#kernel-plugins) · [Android](#android) · [Updates](#updates)

## Games and where they go

**Install.** On **Install pkg / vpk** (or dragging the files onto any screen):

- **VPK or zip**;
- **PKG with license**: the package with its license, a `work.bin`/`.rif` file or the pasted zRIF text;
- **Decrypted folder**: a game folder that is already decrypted.

**Where things go.** Everything is kept where the console keeps it (games in `ux0:app`, updates in `ux0:patch`, DLC in
`ux0:addcont`, PSP/PSone games in `ux0:pspemu/PSP/GAME`, PSM games in `ux0:psm`, saves in `ux0:user/00/savedata`,
licenses in `ux0:license/app/<TITLEID>`). Licenses that earlier versions kept in `ux0:license/<TITLEID>` move to
`ux0:license/app/<TITLEID>` by themselves at the next start (a license whose new place already holds a different one
stays where it was). An installed update counts the way the console counts it: the game sees the update's files
over its own (`ux0:patch/<TITLEID>`, or `grw0:patch/<TITLEID>` for a game card), its version is the update's (the
library shows it too), and the firmware's own apps take none. A Unity PSM game keeps its own runtime in
`ux0:psm/<TITLEID>/runtime` with that runtime's license in `ux0:license/app/<TITLEID>`; one that an earlier version
left in `RO/runtime` moves there by itself at the next start.

On the host, `ux0:` is `fs/ux0` inside the data folder (see the README's
[Where your data lives](../README.md#where-your-data-lives)); `--data=<folder>` or `VITASHARP_DATA` change the folder,
and **Settings > Advanced > Data folder > Change** moves it for good (default, portable beside the executable, or any
folder; the data is copied or moved without overwriting anything, then VitaSharp restarts).

**PlayStation Mobile (PSM).** A PSM game `.pkg` installs like a PS Vita one, with the license of your own account: the
1024-byte PSM `.rif` (or its zRIF) that your console has for that game; a PS Vita license or another game's is refused.
It goes where the console puts it, `ux0:psm/<TITLEID>` (`RO` with the game and `RO/License`, `RW/Documents` for the
saves, which a reinstall keeps); the game stays protected as sold and is opened by the PSM runtime with your license.
It also needs the **PSM Runtime Package** (`PCSI00011`, a normal PS Vita `.pkg` with its license). The library lists
an installed PSM game with the name and icon from the game itself, and the **Vita System** home screen shows its bubble
once the system has rebuilt its database (at its next start after the install, as on the console). Running PSM games
is still in progress (see the [roadmap](roadmap.md)); a PSM game installed with an earlier version must be installed
again.

## Firmware

Games run without firmware. Install it for the system fonts and libraries (`libSceXml`, `libSceSqlite`...) and the
**Vita System**: on the **Vita System** page, **Install firmware (.PUP)...**, or **Use Vita3K's firmware** (copies
`os0`/`vs0`/`sa0`/`pd0` from a Vita3K installation). From the command line: `install-firmware <PSVUPDAT.PUP>`.
**Uninstall firmware** (same page; `uninstall-firmware [--yes]`) removes only what the install created
(`os0`/`vs0`/`sa0`/`pd0` and the index files `firmware.json`, `firmware-libraries.txt`): games, saves and `ux0`/`ur0`
stay, and a partition linked to another folder loses only the link.

**System fonts (font package).** The system update (`PSVUPDAT.PUP`) carries no `sa0`: on the console the system
fonts (`sa0:data/font/pgf` and `pvf`) come from the factory, here from the firmware font package (a separate .PUP,
installed with the same **Install firmware (.PUP)...** / **Install font package (.PUP)...** buttons, or
`install-firmware <font package .PUP>`) or from Vita3K's `sa0` (copied by itself after a system-update install when
Vita3K has it). Without them the Vita System and many games draw no text, so the launcher (desktop and Android) does not
start the Vita System until they are installed and says what is missing; `firmware.json`'s `font_package` and the
firmware page show whether both font folders are there.

**PSP compat core (in progress).** The PSP emulator of the firmware boots a MIPS core from `os0:kd/pcbc.skprx`, whose
content only the console's secure processor decrypts. To run it, dump the decrypted boot code from your own console
(the 0x5CE0 bytes SceCompat loads at its window + 0x600000) and place it at `os0:kd/pcbc.bin` in the data folder
(`%APPDATA%\VitaSharp\fs\os0\kd\pcbc.bin` on Windows, the same `fs/os0/kd` under the data folder on Linux and macOS).
VitaSharp never ships it. Status and limits: the [PSP compatibility notes](notes/psp-compat.md) and the
[roadmap](roadmap.md) (the PSP compat core item).

## Vita System

With the firmware installed, the launcher's **Open Vita System** button (Ctrl+H, or `boot-shell`) runs the firmware's
`shell.self`: initial setup the first time, lock screen, home screen with the installed games and LiveArea. A game
opened from the LiveArea runs as a second process; the PS button (Home key) goes back to the system and **Continue**
resumes the game. Details and what is missing in [`docs/notes/system-apps.md`](notes/system-apps.md).

## Language

The launcher and the game window follow the system's language (English or Brazilian Portuguese);
**Settings > General > Appearance > Language** picks one (on Android: **Settings > Appearance > Language**). Logs stay
in English. The language a game sees is the Vita's system language; for a game without yours (e.g. an Asian release
with only Japanese and English, which falls back to Japanese), its **System language** setting (the game's page in
the launcher, the game's details on Android; `--system-language=<0-19>` on the command line) makes only that game see
another one.

## Graphics and loading

**Default internal resolution.** The internal resolution every game without its own choice starts with is a global
setting: desktop launcher **Settings > Graphics > Internal resolution** (1x-4x), Android **Settings > Internal
resolution** (50%-4x). A game's own choice on its page (native included) wins; the page shows **Default (...)** with
the global value. The loading screen shows the resolution the game starts with, and the session log has an
`Internal resolution: ...` line. `F1` / `Shift+F1` change it for the current session.

**Loading window.** A click on a game opens its window at once (960x544 times `--scale`), before the game process does
any of its start-up, with the game's start-up picture (`sce_sys/pic0.png`), its icon and name, the current step
(firmware, controls, audio, GPU, the game, the shader cache, the ARM code) with a bar and, during the shader cache
and the ARM code waits, "Cancel". The game's own window waits hidden at the same place
(`--loading-screen=launcher --window-pos=X,Y`) and appears when the game is ready; the steps are the game process's
`[boot-step] <step>` log lines and `[boot-log] <path>` its log file. A second click on the game brings the same
window back; its X stops the game. A start that fails (a title that cannot be loaded, a missing license, an error
exit) leaves the reason and the log's place on the window, with "Close". Android shows the same screen over the
game. Nothing extra to install, every OS.

**Load the shader cache before playing** (launcher: **Settings > Graphics**, next to "Compile shaders in the
background", and each game's page; `--load-shader-cache=on|off`, default on; the earlier name `--precompile-shaders`
is still accepted with a warning): before a game starts, its shader cache (the shaders and pipelines recorded in its
earlier sessions, `shadercache/titles/<ID>/`) is built behind a progress bar in the launcher's status bar; "Cancel"
(the line `cancel-shader-cache-load` on the game process's standard input) starts the game at once and the rest builds
in the background. It is a cache load, not a precompilation: a game's first session has nothing recorded. Nothing
extra to install, same on Windows, Linux and macOS.

**Precompile ARM code** (launcher: **Settings > Graphics**, after the shader cache option, and each game's page;
`--precompile-code=on|off`, default on): before a game starts, the native JIT translates its ARM code on every free
core, behind its own progress bar in the launcher's status bar ("Cancel" = the line `cancel-code-precompile` on the
game process's standard input; log lines `[code-precompile] progress ...`): the blocks its earlier sessions ran, in
the order they first ran (the code cache: `codecache/<module image hash>.vsjc` in the data folder,
`--code-cache=<folder>|off`; one file per module image, valid at any load address, rejected after a translator
change), or in a first session the code the static analysis reaches from the modules' entry points (module start,
exports, direct branches and call returns; a module with a code cache file is not walked again: the cache already
says what it runs, and the walk only translated code that never ran). The game starts as soon as those are ready; then a few low-priority
threads translate the code near what the game runs (a few blocks past every block it reaches, and the rest of its
function, found through the `.ARM.exidx` function index), at most 64 MB of the code arena. Precompiled code is
published when the game first reaches it. The log lines `ARM code precompilation: boot step ...` and, at the end,
`... % of the run blocks were precompiled` give the time and the coverage. Android waits for the same step behind its
loading screen (the option is in the app's settings and each game's settings). Nothing extra to install, every OS.

More shader options for developers (compile at program load, shader workers, pipeline libraries):
[`docs/development.md`](development.md#developer-options).

## Console model

**Settings > Emulation > Model** (or `--model=pstv`, `VITASHARP_MODEL=pstv`) makes the emulated unit a PlayStation TV
instead of the PS Vita handheld (PCH-1000, the default): games see a PS TV (several controllers supported), each
connected controller is a player on ports 1-4 (the keyboard plays with the first controller) and their vibration
reaches the controllers. The PS TV's own system software, HDMI settings and USB host are not emulated yet.

## Microphone

The Vita microphone (voice chat, apps that record) hears the PC's capture device through OpenAL Soft, the library
that also plays the sound (WASAPI on Windows, PipeWire/PulseAudio/ALSA on Linux, Core Audio on macOS; nothing to
install). **Settings > Audio > Microphone** picks the system default (the default), a capture device, off (silence)
or a 440 Hz test tone; from the command line `--microphone=<name|index|none|tone[:HZ]>`, and `mic-test` lists the
devices and shows the level a game would get. The device only turns on while a game or app has its microphone port
open; without a device (or, on macOS, without the microphone permission for the terminal or app that runs VitaSharp:
**System Settings > Privacy & Security > Microphone**) the game hears silence, as from a quiet room. Nothing the
microphone captures is saved; a **Record everything** capture keeps only its format and pace, unless
`--capture-mic-audio` (or `VITASHARP_CAPTURE_MIC_AUDIO=1`) asks for its sound too.

## Location

Apps and games that ask the Vita where it is get no position by default: the Vita answers that the application is
not allowed, as when the player declines. **Settings > Audio > Location** (or
`--location=<latitude,longitude[,altitude[,accuracy]]>`, decimal degrees with a point, metres) gives the Vita a fixed
position, which it answers with that accuracy (10 m when not given); empty or `none` is off. The PC's own location
service is not read yet. The position is never written to the log; a **Record everything** capture withholds it (the
Vita answers "position undetermined" while the capture is armed or recording, and `session.txt` keeps
`--location=<location>`) unless `--capture-location` (or `VITASHARP_CAPTURE_LOCATION=1`).

## Bluetooth keyboard and mouse

**Settings > Emulation > Bluetooth keyboard and mouse** (or `--bt-keyboard-mouse=on`) pairs the PC keyboard and mouse
with the Vita, for games and apps that read a keyboard or mouse (SceHid: the browser, chat). Off by default; the keys
still work as the controller while it is on. The keyboard's media keys (play/pause, tracks, volume) also reach the
Vita as its consumer controls, on Windows, Linux (X11), macOS and Android.

## Ad hoc (local multiplayer)

**Settings > Emulation > Ad hoc over the local network** (or `--adhoc-lan=on`; Android: Settings) puts the Vita's ad
hoc Wi-Fi on the local network: other VitaSharp instances with it on, on the same network or the same PC, find each
other with no server. The firewall must allow UDP port 30860 in on the local network (Windows asks the first time).
Where multicast does not pass (a VPN, some routers), `--adhoc-peer=host[:port],...` reaches the other PCs by address.
Off by default. Today: the PSP ad hoc groups (`pspnet_adhoc`: the network check dialog's create/join, peer list, PDP
datagrams); PTP, Matching and the Vita's own ad hoc mode come next.

## Kernel plugins

An emulator option, **Enable Kernel Plugins**, like pausing or recording the screen (the Vita has no such setting).
**Off by default: the pure firmware runs and no plugin loads.** On, VitaSharp loads the plugins the configuration lists
and offers them the plugin framework libraries they import (module information, export and import lookups, data
injections, function hooks), as a console with the homebrew environment set up.

- **Turn it on:** launcher **Settings > Advanced**; Android **Settings > Debugging**; command line
  `--kernel-plugins=on|off`; environment `VITASHARP_KERNEL_PLUGINS=1` (the switches win over the variable).
- **Folder:** `ur0:kernel_plugins/` (on the host `fs/ur0/kernel_plugins/`, same on Windows, Linux, macOS and Android),
  next to `ur0:tai`, holds `config.txt`. Its lines are Vita paths, as on the Vita (`ur0:tai/kubridge.skprx`): the
  plugins themselves stay in `fs/ur0/tai` (or `fs/ux0/tai`), where homebrew checks their files. A line without a device
  (`my.skprx`) names a file you put in `ur0:kernel_plugins/`. Sections as on the Vita: `*KERNEL` for kernel plugins,
  `*ALL` / `*TITLEID` for user plugins, `*main` is the home screen.
- **Fallback:** without `ur0:kernel_plugins/config.txt`, the Vita's `fs/ux0/tai/config.txt` (else
  `fs/ur0/tai/config.txt`) is read, as copied from a Vita.
- **Automatic move:** the first time the option is on, the Vita's `config.txt` moves into `ur0:kernel_plugins/`
  unchanged (a copy kept as `ur0:kernel_plugins/config.txt.bak`); the plugins, the framework's own file and the plugins'
  data files stay in `ur0:tai`. The framework's own file is never loaded: the emulator provides its libraries. The
  folder of earlier versions (`kernel_plugins/` next to `fs/` in the data folder) moves into `ur0:kernel_plugins/` the
  same way, and the empty old folder is deleted.
- **Log:** one line per plugin, `kernel plugin <path>: loaded and started (<name>) (at system boot)`, `... not started:
  no module provides ...` or `... not loaded (...)`. The `*KERNEL` plugins load at the system's boot, before the title's
  launch, even when that launch then fails.

Details in [`docs/notes/plugins.md`](notes/plugins.md).

## Android

The Android app (`src/VitaSharp.Android`) is a .NET MAUI app that runs the same emulator core as the desktop on arm64
phones: the Release APK is compiled with Native AOT by default, the Vita's ARMv7 code runs on the native ARM64 JIT and
the picture is drawn with Vulkan. It has its own screens: game library, game details (CPU backend, clock, GPU profile,
internal resolution, MSAA, frame rate, VSync and the FPS counter per game, saves), trophies, controls, settings,
firmware, GPU driver, PSN account and network, and a performance page. The app follows the language setting
(**Ajustes > Aparência > Idioma**; the menu names below are the Portuguese ones). Screens and design:
[`docs/android/README.md`](android/README.md#screens).

**Requirements.** An arm64 phone with Android 8.0 or newer (API 26) and a Vulkan 1.1 or newer driver.

**State (measured on a Redmi Note 14, Helio G99, Mali-G57).** The mobile version has reached a certain stability;
performance is still the main work in progress. Measured today:

| Game | Where it was measured | fps (native) |
| --- | --- | --- |
| Killzone: Mercenary | campaign | about 22-25 (30) |
| Uncharted: Golden Abyss | the Prologue | 30 (30) |
| F1 2011 | the race | 30 (30) |

Lighter games reach their native 30 or 60 fps; other games have not been measured on the phone yet. Adreno phones are
barely tested.

**Install the APK.** Download `VitaSharp-<version>-android-arm64.apk` from the **Releases** page (every version and
every automatic `build-<commit>` prerelease carries it). Every CI run also builds one for arm64 phones: on the run's
page in **Actions**, the artifact **VitaSharp-android-arm64-packages** holds `VitaSharp-ci-<run>-android-arm64.apk`.
The APK is the Release build (Native AOT, with the device shader compiler and libadrenotools). Install it with
`adb install` or by opening it on the phone.

**Signing and updates.** The Releases and CI APKs are signed with the project's fixed key, so each one installs over
the last and the phone keeps its data. A build from a fork without that key, or one made on another PC, is signed with
a different key: Android refuses to install it over the other until that one is uninstalled. With "All files access"
the data lives in `/sdcard/.vitasharp` and survives the uninstall; without it the data is in the app's own folder and
the uninstall deletes it, so copy `fs/ux0/user/00/savedata` out first. Details: [Signing](android/README.md#signing).

**Build it on a PC:** the .NET 10 SDK with the MAUI Android workload (`dotnet workload install maui-android`), a JDK
17, the Android SDK (platform 36) and the NDK (r30: `sdkmanager "ndk;30.0.16248370"`), then
`build/build-android-apk.ps1` / `.sh`, the CI's own script (it builds the native libraries the first time, then the
APK into `artifacts/android/packages`, and checks it). `ANDROID_KEYSTORE` signs with your own fixed keystore. Steps and
options: [`docs/android/README.md`](android/README.md#building).

```sh
build/build-android-apk.sh          # Windows: build/build-android-apk.ps1
adb install -r artifacts/android/packages/VitaSharp-dev-android-arm64.apk
```

**Games, licenses, firmware and saves.** Each file you install comes in through Android's file picker (Storage Access
Framework) and is read in place. In **Biblioteca**, **Instalar jogo** takes a `.vpk` (or `.zip`) or a `.pkg` with its
license (`work.bin`, `.rif` or the pasted zRIF). The firmware is optional, as on the PC: **Ajustes > Firmware >
Instalar firmware (.PUP)** with `PSVUPDAT.PUP` (and the font package); then **Sistema do Vita** in the library boots
the real home screen. Custom GPU drivers (Adreno, through libadrenotools) are installed in **Ajustes > Driver da GPU**.

**Data folder and permission.** Everything lives in one data folder with the desktop's layout (games in
`fs/ux0/app`, licenses in `fs/ux0/license/app`, saves in `fs/ux0/user/00/savedata`, `config/env.txt`...). Without root
it is **`.vitasharp`** at the root of the phone's storage (`/sdcard/.vitasharp`), visible and editable in any file
manager and kept when the app is uninstalled; the app asks for **"All files access"** on its first start for that and
moves the old `Android/data/io.github.vitasharp/files` there (checked file by file, resumed if interrupted). Until the
permission is given, and on a rooted phone, the data stays in `Android/data/io.github.vitasharp/files`. After a
reinstall Android takes the permission back: the app finds the data in `.vitasharp` and asks again instead of starting
empty ([details](android/README.md#storage)).

**Controls.** Translucent on-screen controls over the sides of the picture (D-pad, sticks, face buttons, L/R,
SELECT/PS/START, a TRÁS toggle for the rear touchpad); touches on the picture are the front touch screen. Bluetooth or
USB controllers are detected on their own with the desktop's positional layout (the on-screen controls hide while one
is connected), and a hardware keyboard uses the desktop's keys. **Controles** remaps each controller's buttons (tap a
control, then press the button on the controller; **Restore defaults** goes back), and sets the O/X swap, deadzone,
opacity, vibration (on every press, also with the other thumb on the stick; weak, medium or strong) and auto-hide. The
phone's gyroscope and accelerometer feed the Vita's motion sensor, and its magnetometer the Vita's compass (when the
game turns the magnetometer on).

**System, cameras, microphone and location.** **Ajustes > Sistema > Ao abrir o emulador** can boot the Vita system
(the firmware's shell) as the app opens, as on the desktop. **Ajustes > Câmeras** chooses what each Vita camera shows,
as the desktop's camera options: the phone's camera of that side (the default), a PNG/JPEG picture or none (grey
frames). **Ajustes > Microfone** chooses what the Vita microphone hears: the phone's microphone (the default; a
connected headset's when there is one), off (silence) or the test tone. Android asks for the microphone permission the
first time a game opens its microphone, never before; refused, the game hears silence. **Ajustes > Localização** gives
the Vita the phone's position (off by default): Android asks for the location permission the first time a game asks
where the Vita is, never before; refused, the position stays undetermined.

**Pause menu, save states and Record everything.** The PS button (on screen or on the controller) pauses a game
started on its own: **Continuar**, **Salvar estado** / **Carregar estado** (6 slots with a picture), **Ajustes** (rear
touch, on-screen controls, MSAA, frame rate, pre-recording), **Recentralizar** (motion), **Gravar tudo** and **Sair do
jogo**. **Gravar tudo** records the same bug report as the desktop's F8 (save state, input, GPU dump, frames, audio and
detailed log) into `captures/` until **Parar gravação**; **Marcar o problema** marks the moment. Send the `.zip` as in the
README's [Report a bug](../README.md#report-a-bug).

**States and recordings on the game page.** The game's details show the 6 slots with their picture and date; tapping a
slot opens the game already loading that state (as the desktop's `--load-state`; with the game open, it loads into
it). Below them are the game's **Gravar tudo** recordings with their date and size; tapping one shares the `.zip`.
From adb: `am start ... --es titleId <ID> --ei loadSlot <1-6>`.

**Per-game settings.** In the game's details: internal resolution (50%, 75%, native, 2x, 3x or 4x, as the desktop's),
MSAA, frame rate (**Nativo**, the game's own pacing, by default; **Fixo em 30**, **Fixo em 60** or **Sem limite**, as
the desktop's `--frame-rate`; also in the pause menu, applied right away, and shown next to the FPS counter), VSync,
the FPS counter, CPU backend, clock and GPU profile and the system language the game sees (**Idioma do sistema**),
saved per game (**Voltar ao padrão de Ajustes** goes back to the global settings). The global default resolution is
described in [Graphics and loading](#graphics-and-loading).

**Known limitations.**

- Killzone: Mercenary's movies can play slowly for a while before they catch up.
- Minecraft loses performance as the session goes on (under investigation).
- Adreno GPUs (and custom drivers) are barely tested; the measurements above are from a Mali-G57.

Open items are in the [roadmap](roadmap.md) (Android section).

## Updates

- **Versions**: a `v*` tag publishes a release with the Native AOT packages for Windows, Linux and macOS, the Android
  APK (`VitaSharp-<version>-android-arm64.apk`) and the `SHA256SUMS.txt`.
- **Automatic builds**: every push to `main` whose CI passed becomes a `build-<commit>` prerelease with that CI run's
  packages (the 10 newest stay).
- **Automatic update**: the packages check for a new version when they open (Settings > General > Updates: on/off,
  channel automatic builds or versions only, **Check now**). The dialog shows the version in use, the new one, its
  date and what's new, with **Skip this version**, **Remind me later** and **Update now**: the platform's package is
  downloaded, checked against `SHA256SUMS.txt` and swapped in (the installer, on Windows installed by the setup), and
  VitaSharp reopens. Only the emulator's files change; the data folder never does. A build compiled from source is
  updated with git (from the command line: `update [--channel=releases] [--apply]`).
- The desktop packages are not signed (Windows SmartScreen and macOS Gatekeeper warn) and never include firmware.
