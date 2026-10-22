# Controller configuration (branch feat/controller-config)

User request: a controller recognition / mapping screen like the ones that react when you move a stick, with a
drawn controller layout, automatic recognition and automatic default mapping.

## Pieces

| Where | What |
| --- | --- |
| `src/VitaSharp.Core/Input/` | Platform-neutral model (reusable by Android): `VitaControl`, `InputBinding` (SDL GameControllerDB-style strings `key:Space`, `pad:a`, `pad:leftx-`, `joy:b3`, `joy:a2+`, `joy:h0.4`), `ControllerProfile` / `InputConfig` (input.json, source-generated JSON = AOT-safe), `DefaultMappings` (historic fixed mapping, positional), `InputMapper` (radial deadzone, sensitivity, invert Y, Cross/Circle swap, touch pad layers, touch points), `InputCapture` (detect the moved input), `InputProfileStore` (match by GUID, then name). |
| `src/VitaSharp.Desktop/Input/SdlGamepads.cs` | SDL2 (Silk.NET.SDL 2.23, native from Ultz.Native.SDL 2.32) on its own thread: hot-plug, GameControllerDB mapping, family/model, touch pads, virtual pads for tests. Any SDL failure -> `Available = false`. |
| `src/VitaSharp.Desktop/Input/HostInput.cs` | Game window input: keyboard + every connected pad through their profiles (defaults when none). Falls back to GLFW gamepads (WindowHost.PollFallbackGamepad) when SDL is unavailable. |
| `src/VitaSharp.Desktop/Launcher/SettingsDialog.cs` | "Configurações" window with tabs Geral / Controles. |
| `src/VitaSharp.Desktop/Launcher/ControllerSettingsPanel.cs` | Controles tab: device list, live layout, options, binding rows, save. |
| `src/VitaSharp.Desktop/Launcher/VitaLayoutView.cs` | Vector drawing of the Vita (no artwork), hit testing. |
| `src/VitaSharp.Desktop/Launcher/ControlNames.cs` | Portuguese labels (Core labels stay English). |
| `src/VitaSharp.Desktop/Launcher/UiSnapshot.cs` | `ui-snapshot <dir>`: renders the dialog driven by an SDL virtual DualSense, saves PNGs, checks input.json round trip. |
| `src/VitaSharp.Desktop/Input/PadTest.cs` | `pad-test [--seconds=N] [--virtual]`: lists pads, `--virtual` checks mapping + HostInput end to end. |

## Behaviour

- No input.json -> exactly the historic mapping (unit tests `DefaultKeyboardMatchesTheHistoricFixedMapping`,
  `DefaultGamepadMatchesTheHistoricFixedMapping`).
- Default gamepad mapping is positional: South (A / Cross / Nintendo B) = Cross. Swap option exchanges Cross/Circle.
- Triggers also press L/R by default (option). Touch pad -> front; L2 held -> rear; R2 held -> both.
- input.json is only written when the user edits something and presses Salvar.
- `test` scenarios / `--input=` replace the window input entirely (ScriptedInput), so they are unaffected.

## Verification commands (scratch data root, never the user's)

```
dotnet build src/VitaSharp.Desktop -c Release -o <scratch>/inbin
VITASHARP_DATA=<scratch>/uidata <scratch>/inbin/VitaSharp.Desktop.exe ui-snapshot <scratch>/shots
VITASHARP_DATA=<scratch>/uidata <scratch>/inbin/VitaSharp.Desktop.exe pad-test --virtual
dotnet test tests/VitaSharp.Tests -c Release --filter "Category!=Slow"
```

SDL-missing check: rename `<scratch>/inbin/runtimes/win-x64/native/SDL2.dll`, run pad-test (prints "SDL
unavailable"), ui-snapshot (keyboard only, message in the panel), and a windowed `run` (warning in the log, game runs).

## Status

Done and verified 2026-10-22 (see commit log). No real gamepad was connected on the dev machine: tested with SDL
virtual controllers only.

Possible follow-ups: rumble, gyro (DualShock 4/DualSense motion -> SceMotion), per-game profiles, multiple Vita
ports for PS TV multiplayer (currently every pad drives port 0).
