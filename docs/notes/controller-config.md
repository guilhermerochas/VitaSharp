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

Possible follow-ups: rumble, per-game profiles, multiple Vita ports for PS TV multiplayer (currently every pad drives
port 0).

## Motion sensors (branch feat/motion-gyro)

Controller gyroscope + accelerometer drive SceMotion.

| Where | What |
| --- | --- |
| `Core/Host/Motion.cs` | `MotionSample` (Vita axes: x right, y top, z out of the screen; accel in G, flat = (0,0,-1); gyro rad/s) and `IMotionSource` (`HostServices.Motion`; Android will feed the phone sensors through `MotionAxes.FromDevice`). |
| `Core/Input/MotionAxes.cs` | SDL gamepad -> Vita: `Natural` = Vita3K's (x, -z, y), accel / -g (controller face = Vita screen; lying flat = Vita lying screen up). `FlatIsUpright` = SDL axes as is (controller held flat = Vita upright in front of you, rear camera ahead: AR games). `TiltGravity` for scripts/tests. |
| `Core/Hle/Motion/MotionFusion.cs` | Vita3K MotionInput behaviour: Mahony filter (kp 0.3, ki 0.3/s), deadband 0.007 rev/s, bias learned while still, clamps, accel-only fallback gains. Orientation = device -> world, world y up and z towards the player: identity = Vita upright, flat = -90 deg about x (where Vita3K's filter settles). Initialised from the first gravity sample; `Reset` = tilt from gravity, heading 0; `RotateYaw` about world y. Basic orientation with hysteresis (switches 45 deg + angle threshold away from the current axis). |
| `Core/Hle/Motion/MotionDevice.cs` | Drains the source at every SceMotion call, integrates each sample with its own host dt, keeps the last 64 raw records (sceMotionGetSensorState, oldest first, one counter per sample, guest timestamps), writes SceMotionState (0xF8; nedMatrix = rotationMatrix, magFieldStability UNSTABLE like Vita3K). Without a live source: exactly the old at-rest values. |
| `Desktop/Input/SdlGamepads.cs` | Enables `SDL_SENSOR_GYRO`/`ACCEL` when both exist, polls `GameControllerGetSensorDataWithTimestamp` every 4 ms into a 128-sample ring per pad (`ReadMotion`). `PadInfo.HasMotion`, `PadSnapshot.Accel/Gyro` (live view). |
| `Desktop/Input/HostInput.cs` | `IMotionSource`: first connected pad with sensors whose profile has `MotionEnabled`. |
| `ControllerProfile` | `MotionEnabled` (default true), `MotionOrientation` (input.json, string enum). |
| Controles tab | "Usar giroscópio do controle", orientation picker, live tilt/rotation readout. |
| `ScriptedInput` | `tilt@T:pitch:roll[:len]` (degrees, from flat) makes a script the motion source (`--input=`). |

No mouse fallback: the right mouse button already drives the rear touch pad.

Verified: unit tests `MotionTests` (synthetic samples); Invizimals (PCSA00552, calls sceMotionGetState every frame
without starting sampling) with `--input=tilt@...` and `--peek` of its SceMotionState: flat, 30 deg pitch, -40 deg
roll and 80 deg pitch give the expected acceleration, quaternion and basic orientation. No controller with sensors
was connected on the dev machine (SDL2 virtual controllers cannot emulate sensors), so the live SDL path is untested.
