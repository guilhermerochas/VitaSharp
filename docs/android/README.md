# Android UI (planned)

Mockups of how the Android port's interface is meant to look. Nothing here is implemented yet; the screens are a
visual reference for when the port starts. On-screen text is in Portuguese, like the desktop launcher.

## Stack

- **.NET MAUI** for the app shell, reusing `VitaSharp.Core` and the Vulkan renderer (the game view is a native
  surface hosting the Vulkan swapchain).
- **Declarative C#, no XAML**: [MauiReactor](https://github.com/adospace/reactorui-maui) (MVU components written in
  C#). Existing/community libraries only, such as the .NET MAUI Community Toolkit; no custom UI framework.
- **Own visual style, not Material**: dark surfaces, one blue accent, rounded cards, Sora for headings and Inter for
  text.
- **Input**: the platform-neutral model in `src/VitaSharp.Core/Input` (profiles, mapping, deadzone, touch layers)
  is shared with the desktop controller screen ([notes](../notes/controller-config.md)).
- **CPU**: the native ARM64 JIT, picked automatically on ARM64 ([notes](../notes/native-jit.md)).
- **Icon**: an adaptive launcher icon (foreground, monochrome and background layers) generated from
  `docs/assets/logo.svg` by `build/make-icons.ps1` into `src/VitaSharp.Android/Resources`.

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

- **Biblioteca**: search, filters, a "Continuar jogando" card for the last game, a "Sistema do Vita" entry, a grid of
  games with compatibility badges and trophies earned/total with a progress bar ("Sem troféus" for games without a
  trophy set), and "Instalar VPK".
- **Detalhes do jogo**: play button, playtime/FPS stats, a trophy card (earned/total, progress, platinum/gold/silver/
  bronze counts; opens Troféus), per-game settings (CPU backend, "Nativo (ARM64)" by default, resolution, VSync, FPS
  overlay) and saves.
- **Troféus**: the game's trophy set (from its `TROPHY.TRP`, unlocks in `ux0:user/00/trophy`): progress, counts per
  grade, filters, and the list with icon, grade, description and unlock date; locked and hidden trophies are dimmed.
- **Controles**: connected controller, live Vita layout that lights up the pressed input, mapping rows (including
  the PS button), deadzone and the O/X confirm swap.
- **Menu de pausa**: bottom sheet over the paused game with save/load state, screenshot, controls, performance and
  settings; the header shows the session's trophy progress.
- **Ajustes**: Sistema (firmware status, Sistema do Vita, system language), Desempenho (CPU, GPU driver, GPU
  information) and Rede e PSN (account, real internet).
- **Firmware**: the installed version and source, each partition (os0, vs0 with the shell, pd0, the sa0 font
  package), an install in progress with cancel, and "Instalar firmware (.PUP)" / "Instalar pacote de fontes (.PUP)",
  which open the Storage Access Framework picker; the PUP is read in place. VitaSharp ships no firmware.
- **Conta PSN e rede**: the emulated PSN account (signed in, online ID), the local PSN services, the opt-in "Internet
  real" toggle with its privacy note always visible, and the optional RPCN server, user, password and token (editable
  only with real internet on, shown switched on here). Same meaning and defaults as the desktop launcher
  ([E2-np §0.000](../hle/E2-np.md)); the password lives in the Android Keystore.
- **Driver da GPU**: the GPU and driver in use, the system driver or an installed Turnip/Adreno package (zip with
  `meta.json`) loaded through libadrenotools, "Instalar driver (.zip)", and the BCeNabler, turbo and file-redirect
  options. A change applies to the next game: a process loads one driver. Offered on Adreno GPUs only.
- **Informações da GPU**: what the renderer found at startup ([mobile GPU notes](../notes/mobile-gpu.md)): device,
  driver, Vulkan version, tile-based/unified memory, the features it uses, the active quirk workarounds and the
  quirks that change nothing; the share button sends the same block as the startup log / info.txt.

### In game (landscape)

<img src="screens/jogo.png" width="640" alt="Game with on-screen controls">

The 960×544 image is scaled to the screen height and centred. Translucent controls sit over the side margins: L/R in
the corners, D-pad and left stick on the left, face buttons and right stick on the right, SELECT/PS/START at the
bottom, a rear-touch toggle (TRÁS) and a small FPS + pause bar at the top. Pressed controls switch to the accent
colour. A trophy unlock shows a toast ("Troféu desbloqueado", name and grade) at the top right of the image, clear of the
face buttons, as on the desktop. In a game started on its own (no shell running) the PS button opens the pause menu.

### Sistema do Vita (landscape)

<img src="screens/sistema.png" width="640" alt="The firmware's shell running full screen">

"Sistema do Vita" boots the installed firmware's own `vs0:vsh/shell/shell.self` full screen
([system apps notes](../notes/system-apps.md)). The home screen, LiveArea and system apps are the firmware's, drawn
by the emulator; the app never redraws or imitates them, so the mockup shows the emulator output as a placeholder.
Only the app's chrome sits around it: exit, a status chip, a menu, the PS button (it reaches the shell like the
console's: a running game goes to its LiveArea) and a button that shows the full on-screen controls. Touches on the
image are the front touch screen.

## Code

`src/VitaSharp.Android` (skeletons, compile-checked against a desktop target only):

- `Gpu/AdrenoToolsDriverLoader.cs`: the libadrenotools loader; `Gpu/GpuDriverScreen.cs`: the model of Driver da GPU
  (list, select, install from a SAF stream, remove, options) and Informações da GPU (rows, features, quirks).
- `Firmware/FirmwareSetup.cs`: firmware status (version, fonts, pd0, shell) and the install from a seekable SAF stream.
- `Input/OnScreenControls.cs`: the on-screen layout of jogo.html (PS button included) and the PS-only layout of the
  Vita system view, as an `IInputBackend`.
- `Settings/AppSettings.cs`: the CPU choices ("Nativo (ARM64)") and the PSN/network settings mapped to the emulator
  options.
- `Audio/`, `Media/`: AAudio and MediaCodec backends; `Resources/`: the adaptive launcher icon.

## Files

- [`mockups/`](mockups): standalone HTML for each screen (open in a browser).
- [`screens/`](screens): PNG renders of the mockups (2× scale), made with `build/render-android-mockups.ps1`
  (headless Edge, the centred phone frame cropped out).
