# Android app

`src/VitaSharp.Android` is a .NET MAUI Android app (MauiReactor screens, a native game activity with a Vulkan
SurfaceView) that builds into an installable APK. It has **not been run yet**: no device or Android emulator was used
while writing it, so everything below that depends on a running process (surface, JIT, audio, input) is unverified;
the checklist at the end lists what the first run must check. On-screen text is in Portuguese, like the desktop
launcher.

## Building

Needs the .NET MAUI Android workload (`dotnet workload install maui-android`), the Android SDK (platform 36,
build-tools) and a JDK 17. The desktop build does not: the app is outside `VitaSharp.sln`, which only gained the
platform-neutral `VitaSharp.Android.Common` (plain `net10.0`).

```
dotnet build src/VitaSharp.Android -c Release -m:2
# or the whole Android side: dotnet build VitaSharp.Android.sln -c Release -m:2
# without ANDROID_HOME in the environment: add -p:AndroidSdkDirectory=<sdk>
#   (Windows %LOCALAPPDATA%\Android\Sdk, Linux ~/Android/Sdk, macOS ~/Library/Android/sdk)
```

The renderer needs shaderc on the device, and `Silk.NET.Shaderc.Native` has no Android build: once per checkout,
`build/build-android-shaderc.ps1` (or `.sh`) compiles `libshaderc_shared.so` for arm64-v8a and x86_64 from the shaderc sources
the NDK ships (`sources/third_party/shaderc`, ndk-build, libc++ static, 16 KiB-page aligned) into
`third_party/android-shaderc/libs/<abi>/` (not committed); the app packages them when they exist. It needs the NDK
(`sdkmanager "ndk;30.0.16248370"`, r30 was used) and takes long (glslang + SPIRV-Tools, about 460 files per ABI;
`-Jobs 2` by default).

Custom GPU drivers need libadrenotools: `build/build-android-adrenotools.ps1` (or `.sh`) fetches its sources at a
pinned commit (git, once) into `third_party/android-adrenotools/src` and builds `libadrenotools.so` and its four hook
libraries for arm64-v8a with ndk-build (no CMake needed, 2 jobs, under a minute) into
`third_party/android-adrenotools/libs/arm64-v8a/` (not committed); the app packages them when they exist, else the
system Vulkan driver is always used. Details: [mobile GPU notes](../notes/mobile-gpu.md#custom-drivers-android).

Output: `src/VitaSharp.Android/bin/Release/net10.0-android/io.github.vitasharp-Signed.apk` (debug-keystore signed,
arm64-v8a + x86_64). Size on 30/10/2026: 43,713,556 bytes (41.7 MiB) with libshaderc (38,974,327 without); with the
MediaCodec/AAudio backends, libadrenotools and `extractNativeLibs` (native libraries stored compressed), without
libshaderc: 39,253,246 (+278,919; libadrenotools and hooks are about 142 KB of it compressed); a Release rebuild of both
ABIs takes about 3 minutes with `-m:2`. `-r android-arm64` builds one ABI (faster, for iterating).

| Project | Target | In `VitaSharp.sln` | What |
|---|---|---|---|
| `src/VitaSharp.Android.Common` | `net10.0` | yes | models behind the screens, on-screen pad, gamepad mapping, logcat formatting (unit-tested: `AndroidAppTests`) |
| `src/VitaSharp.Android` | `net10.0-android` (minSdk 26) | no (`VitaSharp.Android.sln`) | the MAUI app, game activity, Android backends |

## Code

- `Pages/` (MauiReactor components, no XAML) and `Ui/` (theme, the shared building blocks of the mockups:
  header, cards, rows, chips, progress bars, pickers). Fonts: Sora and Inter (SIL OFL 1.1, `Resources/Fonts`).
- `Platforms/Android/Game/`: `GameActivity` (the in-game screen), `EmulationSession` (the Android counterpart of the
  desktop `Runner`), `AndroidVulkanSurface` (`IPresentationSurface`), `OnScreenControlsView`.
- `Platforms/Android/Services/`: `LogcatSink`, `Saf` (Storage Access Framework picker); `Services/AppServices.cs`:
  data folder, settings, controller profiles, Keystore.
- `Audio/AndroidAudioBackend.cs` (AAudio, AudioTrack), `Media/MediaCodecVideoDecoder.cs`,
  `Media/AndroidMediaPlayer.cs`, `Gpu/AdrenoToolsDriverLoader.cs`: the audio, video and GPU driver backends
  ([below](#audio-video-and-gpu-drivers); compiled into the app, not run yet). Their platform-neutral halves are in
  `VitaSharp.Android.Common` (`Audio/BufferedPcmStream.cs`, `Media/MediaCodecSupport.cs`,
  `Storage/GameProcessMarker.cs`; unit-tested: `AndroidMediaDriverTests`).
- `src/VitaSharp.Android.Common`: `Library/` (game scan, trophy progress and list), `Settings/` (global and per-game
  settings, CPU/clock/GPU choices, PSN/network, `RuntimeReport`), `Firmware/FirmwareSetup.cs`,
  `Gpu/GpuDriverScreen.cs` (Driver/Informações da GPU models), `Input/` (`OnScreenPad`, `AndroidGamepads`,
  `AndroidInput`), `Logging/` (`LogcatFormat`, `SessionLog`), `Storage/DataLayout.cs`.
- Icon: the adaptive launcher icon (`Platforms/Android/Resources/mipmap-*`) generated from `docs/assets/logo.svg` by
  `build/make-icons.ps1`.

## Runtime and CPU backends

- **.NET for Android 10 runs on Mono** (the MAUI default; CoreCLR on Android is experimental in .NET 10 and not
  used). The APK carries `libmonosgen-2.0.so` (JIT) and, in Release, profiled AOT images of the startup code
  (`libaot-*.so`); everything else is JIT-compiled at run time. So `RuntimeFeature.IsDynamicCodeSupported` and
  `IsDynamicCodeCompiled` are true: the **IL backend** (DynamicMethod/Reflection.Emit) is available, as on the
  desktop. `UseInterpreter` is off, and the app does not use full AOT or Native AOT (either would remove the IL
  backend; the native backend and the interpreter would remain).
- **Native ARM64 JIT**: it does not depend on Reflection.Emit (generated code is called through
  `delegate* unmanaged`, callouts are `[UnmanagedCallersOnly]`), so it works under Mono as under CoreCLR or Native
  AOT. Its code arena is the Android dual mapping of `ExecutableMemory` (memfd on API 30+, ASharedMemory on 26-29;
  RWX only as the last resort). On the x86_64 Android emulator the **native x64 backend** is the default instead.
- Default chain on a phone: native > il > interpreter (`Jit.All`); `RuntimeReport` writes runtime, dynamic-code
  flags, each backend's availability and the selected chain to the log at startup; Desempenho shows the same.
- Trimming: `TrimMode=partial` with `VitaSharp.Core` and `VitaSharp.Renderer.Vulkan` rooted (the HLE tables and
  Silk.NET are not trim-annotated). JSON uses source-generated contexts.

## Process model and the game screen

- The screens run in the app's main process; **each game runs in its own process** (`GameActivity`,
  `android:process=":game"`), started fresh and ended when the game is left, like the desktop launcher's `run`: no
  emulator state carries over between games, and a GPU driver change applies to the next game. The two processes
  share only files (settings, statistics, logs); the UI reloads them when it comes back to the front.
- **Surface**: a `SurfaceView`; `surfaceCreated` wraps its `Surface` in an `ANativeWindow` and the renderer creates
  a `VkSurfaceKHR` through `VK_KHR_android_surface` (with the renderer's own `Vk`, so a libadrenotools driver
  creates it too). The first surface boots the game on a `boot` thread (renderer, `LoadTitle`, `Start`);
  `surfaceDestroyed` calls `VulkanRenderer.DetachSurface` (waits for the GPU, destroys swapchain and surface; frames
  are still rendered but not presented) and the next `surfaceCreated` calls `AttachSurface`. The swapchain asks for
  the IDENTITY transform when the display reports a rotation (the compositor rotates).
- **Lifecycle**: `onPause` suspends the guest process (`Emulator.Suspend`) and pauses the AAudio streams; `onResume`
  resumes both. The guest clock keeps running meanwhile (as the Vita's RTC across a suspend). Leaving the game
  (pause menu > Sair) records the play time and average FPS in the game's statistics.
- **Input** (`AndroidInput`, the core's `IInputBackend`): the on-screen pad (`OnScreenPad`, drawn by
  `OnScreenControlsView`) combined with every physical controller (`AndroidGamepads`): `InputDevice` hot-plug,
  `KeyEvent` buttons and `MotionEvent` axes become a standard-layout `PadSnapshot` mapped through the same controller
  profiles as the desktop (`<data>/config/input.json`; positional A = Cross, triggers also L/R, Mode/Home = PS, the
  right stick on AXIS_Z/RZ or RX/RY by what the device reports). TRÁS toggles rear touch (touches on the image go to
  the rear pad). The on-screen controls hide while a controller is connected (setting). In a game on its own the
  PS button (on-screen or the controller's) opens the pause menu; in Sistema do Vita it goes to the shell.
- **Sistema do Vita** boots `vs0:vsh/shell/shell.self` from the installed firmware in the same activity with the
  PS-only layout.

## Storage

The data root is the **app-specific external storage** (`Context.GetExternalFilesDir(null)`,
`/sdcard/Android/data/io.github.vitasharp/files`), or the internal files folder when it is not mounted; the layout is
the desktop's (`fs/ux0/app` games, `fs/ux0/user/00/savedata` saves, `fs/vs0`... firmware, `logs`, `shadercache`,
`np`, `config/app.json`, `config/games/TITLEID.json`, `config/input.json`, `gpu_driver.json`; `DataLayout`).
GPU driver packages (`gpu_drivers/`, `gpu_driver_files/`) and the running game's marker (`run/game.json`) are in the
**internal** files folder (`Context.FilesDir`) instead: the linker loads no library from shared storage. Why not a folder chosen with the Storage Access Framework: SAF gives `content://` URIs, not paths, and
the emulator opens, maps and seeks guest files directly (and keeps the shader cache and saves next to them); the only
way to a real path in a user folder is `MANAGE_EXTERNAL_STORAGE`, which needs a Play policy exception. So no storage
permission is requested, and files come in through SAF: **Instalar VPK**, the firmware `.PUP` and GPU driver
packages are picked with `ACTION_OPEN_DOCUMENT` and read in place through `ContentResolver.OpenFileDescriptor`
(a provider that only streams, such as a cloud drive, is copied into the cache first). Uninstalling the app deletes
the data folder (Android's rule for app-specific storage); a USB/MTP connection or the Files app can reach it on most
phones.

## Audio, video and GPU drivers

- **Audio** (`AndroidAudioBackend`): one device stream per guest port, NDK **AAudio** on Android 8.1+ (low-latency
  performance mode, exclusive/MMAP sharing when granted, usage GAME, 16-bit) and the Java **AudioTrack** in
  low-latency mode on 8.0 (Oboe's rule: 8.0's AAudio is buggy) or when AAudio does not open. The stream runs at the
  rate the device reports (`AudioManager` `PROPERTY_OUTPUT_SAMPLE_RATE`), so AAudio adds no resampler of its own;
  the guest's 48 kHz is converted (linear) only when the device differs. `BufferedPcmStream` (Android.Common) holds
  the policy of the desktop OpenAL backend: up to `max(4, 2 × period grains + 2)` grains queued (the device buffer is
  sized to that), playback starting after one period plus a grain (primed before `requestStart`), Submit blocking
  while full (the guest's pacing). A dry spell (the device's xrun counter moved) counts once in `IAudioStream.Underruns`,
  so it shows in the `AUDIO summary` line and the debug-mode AUDIO lines; when the buffer was completely empty the next
  grain is preceded by silence up to the start threshold, as OpenAL re-buffers. The AAudio error callback (headphones,
  Bluetooth, USB) and `AudioDeviceCallback` (an output added or removed) make the stream reopen on its next grain, on
  the guest thread, at the new device's rate; with no device at all the guest is paced by the clock. The device line
  of the AUDIO summary (`AudioDiagnostics.DeviceInfo`) has the backend, mode, rate, burst, queue depth, latency and
  underruns.
- **Video** (`MediaCodecVideoBackend`, the default `VideoDecoders.Backend` in the game process): H.264 through the
  `android.media.MediaCodec` bindings without a surface. Hardware decoders first, then Android's own software decoder
  (`c2.android.avc.decoder` / `OMX.google.h264.decoder`, on every device); a decoder that fails while running hands
  over to the next one from the last IDR picture (`DecoderReplay`, the FFmpeg hwaccel fallback's scheme). Output is
  `COLOR_FormatYUV420Flexible` read with `getOutputImage` (plane row/pixel strides, crop rectangle; vendor layouts
  work), the raw buffer in the two standard layouts otherwise; `KEY_LOW_LATENCY` (API 30) where supported; colour
  range/standard from the output format. The pictures are the planar `DecodedPicture` the existing upload path takes
  (SceAvcdec, SceVideodec).
- **Movies** (`AndroidMediaPlayer`, installed as `MediaPlayers.Backend`): SceAvPlayer on the desktop demuxes and
  decodes AAC with FFmpeg, which the app does not carry. On Android `MediaExtractor` demuxes the MP4, H.264 goes
  through the same `IVideoDecoder` (csd-0/csd-1 in front of key frames) and AAC through a MediaCodec audio decoder, into
  the same NV21/16-bit queues as the desktop player (`MediaPlayerBase` in the core). SceAudiodec (MP3/AAC) and SceMp4
  still need FFmpeg: they report "no media" on Android.
- **GPU drivers** (`AdrenoToolsDriverLoader`, see the [mobile GPU notes](../notes/mobile-gpu.md#custom-drivers-android)):
  a Turnip or Qualcomm package (zip with `meta.json`, the Skyline/yuzu format) installed from "Driver da GPU" goes to
  internal storage; the game process opens it through libadrenotools with the file-redirect, BCeNabler and turbo
  options. Each game process records the configuration it loaded (`run/game.json`); after a change the screen offers
  to restart the running game (kills the `:game` process and starts the game again).

## Logging

`LogcatSink` routes every line of VitaSharp's `Log` to logcat through `__android_log_write` (liblog): tag
**VitaSharp**, priorities Trace = VERBOSE, Debug = DEBUG, Info = INFO, Warn = WARN, Error = ERROR; guest output
(`sceClibPrintf`, TTY writes, `sceKernelPrintf`: the `[guest...]` lines) goes to **VitaSharp-Guest**. Lines are split
at line breaks and at 4,000 bytes (liblog truncates near 4 KB). The console copy is off (stdout would duplicate every
line). The file logs stay: `logs/app.log` (UI process, truncated at each start) and `logs/last.log` (the current or
last game; the previous ones kept as `<time>_<TITLEID>.log`, 10 at most). "Modo de depuração" turns on debug level.

```
adb logcat -s VitaSharp VitaSharp-Guest
```

## Screens

| Biblioteca | Detalhes do jogo | Troféus | Controles |
| --- | --- | --- | --- |
| <img src="screens/biblioteca.png" width="200" alt="Game library"> | <img src="screens/detalhes.png" width="200" alt="Game details"> | <img src="screens/trofeus.png" width="200" alt="Trophy list"> | <img src="screens/controles.png" width="200" alt="Controller settings"> |

| Menu de pausa | Ajustes | Firmware | Conta PSN e rede |
| --- | --- | --- | --- |
| <img src="screens/pausa.png" width="200" alt="Pause menu"> | <img src="screens/ajustes.png" width="200" alt="Settings"> | <img src="screens/firmware.png" width="200" alt="Firmware"> | <img src="screens/psn.png" width="200" alt="PSN account and network"> |

| Driver da GPU | Informações da GPU |
| --- | --- |
| <img src="screens/driver-gpu.png" width="200" alt="GPU driver"> | <img src="screens/gpu-info.png" width="200" alt="GPU information"> |

The mockups (`mockups/*.html`, rendered to `screens/*.png` by `build/render-android-mockups.ps1` or `.sh`) are the visual
reference; the app follows them with these differences:

- **Biblioteca**: as the mockup, plus a fourth tab "Desempenho" (runtime/CPU backends, GPU pages, play statistics).
  Covers are the game's `icon0.png`, or coloured initials. The badge says "Jogável" for applications (no
  compatibility database yet).
- **Detalhes do jogo**: CPU backend, clock (333/444 MHz), GPU profile (`--gpu-profile`), VSync and FPS per game,
  each "Padrão" following Ajustes; no resolution row (the renderer has no internal-resolution scaling). Saves: size,
  date, delete. The "⋯" menu uninstalls.
- **Menu de pausa**: an Android dialog (continue, rear touch, on-screen controls, leave); no save states or
  screenshot (the emulator has no save states yet).
- **Controles**: connected controllers, the mapping of the selected one, O/X swap, deadzone, the on-screen controls'
  opacity, vibration and auto-hide; no live layout lighting up (the screens' process does not receive the
  controller's events) and no remapping editor yet.
- **Ajustes**: no system-language row (`EmulatorOptions.Language` is not used by the core); adds data-folder and
  debugging sections.
- **Driver da GPU / Informações da GPU**: the information comes from a surface-less `VkCore` probe in the screens'
  process (the system driver); the driver list, install (SAF picker, then "Usar este driver agora?"), select, remove
  and the three libadrenotools switches work on files and apply to the next start of a game; a game running with
  another configuration is offered a restart.

## Before the first run: what the emulator/device step must verify

1. **Shader compiler**: `libshaderc_shared.so` (built from the NDK r30 sources, 42 `shaderc_*` exports, 16 KiB
   aligned, needs only libc/libm/libdl) is in the APK when `build/build-android-shaderc.ps1` (or `.sh`) ran; check that
   Silk.NET.Shaderc loads it by that name and that the first shaders compile (a missing library shows as a
   DllNotFoundException in the session dialog and the log).
2. Install, launch, navigate every screen (portrait), fonts and colours; SAF pickers return (VPK, PUP, driver zip);
   VPK install and firmware install end to end; the Keystore stores the RPCN password.
3. Game process: `surfaceCreated` → renderer → `LoadTitle` → frames on screen; landscape orientation and the
   swapchain extent/transform (the IDENTITY choice); home → back (detach/attach, `Suspend`/`Resume`, audio pause);
   rotation by 180°; leaving the game kills the `:game` process and the library shows the new play time.
4. CPU: the log's `RuntimeReport` lines (Mono, dynamic code true); the native backend selected (x64 on the x86_64
   emulator, ARM64 on a phone) and that SELinux lets the memfd/ashmem RX view execute; `--cpu`-equivalent choices from
   Detalhes.
5. Input: on-screen buttons and sticks, front touch on the image, TRÁS rear touch, PS opens the pause menu; a
   Bluetooth/USB controller (hot-plug, right stick axes, analog and digital triggers, Home = PS).
6. Logcat tags and priorities (`adb logcat -s VitaSharp VitaSharp-Guest`), the file logs.
7. **Audio**: the log's `Audio:` lines (AAudio on 8.1+, low-latency and exclusive or shared, the device rate and burst;
   AudioTrack on the x86_64 emulator image if it is API 26); sound plays at the right pitch and pace (a 44.1 kHz
   device exercises the resampler), the `AUDIO summary` device line and underruns after a session (debug mode for
   the periodic AUDIO lines); home → back (pause/resume, no burst of stale grains); plugging/unplugging headphones and
   connecting a Bluetooth headset during a game ("reopening the output", sound continues on the new device, at its
   rate); writes before `requestStart` (priming) are accepted by AAudio.
8. **Video**: SceAvcdec/SceVideodec games and SceAvPlayer movies (an intro movie, F1 2011's): the log's
   `MediaCodec: H.264 with <decoder>` and output-format lines; frames correct (no green/garbled chroma: the
   `getOutputImage` plane strides and the crop), the fallback to `c2.android.avc.decoder` when a hardware decoder fails
   (`MediaCodec: ... failed`), movie sound (AAC through MediaCodec) in sync; on the x86_64 emulator only the software
   decoder exists. SceAudiodec MP3/AAC is expected to be silent (no FFmpeg).
9. **GPU drivers** on an Adreno phone: the APK has `lib/arm64-v8a/libadrenotools.so` and the four hooks (when
   `build/build-android-adrenotools.ps1` or `.sh` ran) and they are extracted to the native library directory
   (`extractNativeLibs`); install a Turnip package (K11MCH1/AdrenoToolsDrivers or Mesa Turnip releases) from "Driver da
   GPU", select it, start a game: the log's `Vulkan driver: ... via libadrenotools` line, the capability block shows
   Turnip (`TurnipDriver` quirk, none of the blob quirks); BCeNabler on a stock driver without BCn (`BCeNabler:` line);
   file redirect and turbo; change the driver while a game runs and accept the restart (the `:game` process is killed,
   the game starts again with the new driver); a Mali phone uses the system loader. The quirk table on Adreno/Mali
   ([mobile GPU notes](../notes/mobile-gpu.md)).
10. **Renderer paths** ([mobile renderer notes](../notes/mobile-renderer.md)): the capability block's `RenderPasses`
    line (proprietary Adreno/Mali/PowerVR drivers and every device without Vulkan 1.3 dynamic rendering render with
    render passes; `VITASHARP_GPU_QUIRKS=-RenderPasses` tries dynamic rendering on a 1.3 driver); an Adreno 6xx on an
    old driver (Vulkan 1.1) boots instead of "No Vulkan 1.1 capable device", with `StaticExtendedDynamicState` and
    `StaticDepthBiasEnable`; the `[texcache] BC/PVRTC textures are transcoded to ASTC 4x4` line and the ASTC part of
    `PERF mobile` (levels encoded, M texels/s, memory/disk hits) after a session, BC games look as on desktop (no
    blocky or discoloured textures, BC4/BC5 normal maps lit correctly), a second start of the same game hits the disk
    cache (`<data>/shadercache/titles/<TITLEID>/astc`, at most 512 MB, `VITASHARP_ASTC_CACHE_MB`); `PERF gpu`'s
    renderings per scene and hoisted breaks, and the tile load/store counters in the device's profiler (Snapdragon
    Profiler / Arm Performance Studio: fewer GMEM loads and stores per frame than with `VITASHARP_SCENE_BREAKS=strict`).

## Stack

- **.NET MAUI** for the app shell, reusing `VitaSharp.Core` and the Vulkan renderer; the game view is a native
  activity with a SurfaceView hosting the Vulkan swapchain.
- **Declarative C#, no XAML**: [MauiReactor](https://github.com/adospace/reactorui-maui) 4 (MVU components written
  in C#). Existing/community libraries only; no custom UI framework.
- **Own visual style, not Material**: dark surfaces, one blue accent (#6E9BFF), rounded cards, Sora for headings and
  Inter for text.
- **Input**: the platform-neutral model in `src/VitaSharp.Core/Input` (profiles, mapping, deadzone, touch layers)
  is shared with the desktop controller screen ([notes](../notes/controller-config.md)).
- **CPU**: the native ARM64 JIT, picked automatically on ARM64 ([notes](../notes/native-jit.md)).

### In game (landscape)

<img src="screens/jogo.png" width="640" alt="Game with on-screen controls">

The 960×544 image is scaled to the screen height and centred. Translucent controls sit over the side margins: L/R in
the corners, D-pad and left stick on the left, face buttons and right stick on the right, SELECT/PS/START at the
bottom, a rear-touch toggle (TRÁS) and a small FPS + pause bar at the top. Pressed controls switch to the accent
colour. A trophy unlock shows a toast ("Troféu desbloqueado", name and grade) at the top right. In a game started on
its own (no shell running) the PS button opens the pause menu.

### Sistema do Vita (landscape)

<img src="screens/sistema.png" width="640" alt="The firmware's shell running full screen">

"Sistema do Vita" boots the installed firmware's own `vs0:vsh/shell/shell.self` full screen
([system apps notes](../notes/system-apps.md)). The home screen, LiveArea and system apps are the firmware's, drawn
by the emulator; the app never redraws or imitates them. Only the app's chrome sits around it: the PS button (it
reaches the shell like the console's: a running game goes to its LiveArea), the FPS/pause bar and the back gesture
for the menu. Touches on the image are the front touch screen.

## Files

- [`mockups/`](mockups): standalone HTML for each screen (open in a browser).
- [`screens/`](screens): PNG renders of the mockups (2× scale), made with `build/render-android-mockups.ps1` (Windows) or `build/render-android-mockups.sh` (Linux/macOS)
  (headless Edge, the centred phone frame cropped out).
