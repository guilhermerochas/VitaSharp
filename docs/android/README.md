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

## Screens

| Biblioteca | Detalhes do jogo | Controles | Menu de pausa |
| --- | --- | --- | --- |
| <img src="screens/biblioteca.png" width="200" alt="Game library"> | <img src="screens/detalhes.png" width="200" alt="Game details"> | <img src="screens/controles.png" width="200" alt="Controller settings"> | <img src="screens/pausa.png" width="200" alt="Pause menu"> |

- **Biblioteca**: search, filters, a "Continuar jogando" card for the last game, a grid of games with
  compatibility badges, and "Instalar VPK".
- **Detalhes do jogo**: play button, playtime/FPS stats, per-game settings (CPU backend, resolution, VSync, FPS
  overlay) and saves.
- **Controles**: connected controller, live Vita layout that lights up the pressed input, mapping rows, deadzone and
  the O/X confirm swap.
- **Menu de pausa**: bottom sheet over the paused game with save/load state, screenshot, controls, performance and
  settings.

### In game (landscape)

<img src="screens/jogo.png" width="640" alt="Game with on-screen controls">

The 960×544 image is scaled to the screen height and centred. Translucent controls sit over the side margins: L/R in
the corners, D-pad and left stick on the left, face buttons and right stick on the right, SELECT/START at the
bottom, a rear-touch toggle (TRÁS) and a small FPS + pause bar at the top. Pressed controls switch to the accent
colour.

## Files

- [`mockups/`](mockups): standalone HTML for each screen (open in a browser).
- [`screens/`](screens): PNG renders of the mockups (2× scale).
