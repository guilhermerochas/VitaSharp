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

Mouse: the right button drives the rear touch pad, so mouse look (below) uses the middle button.

Verified: unit tests `MotionTests` (synthetic samples); Invizimals (PCSA00552, calls sceMotionGetState every frame
without starting sampling) with `--input=tilt@...` and `--peek` of its SceMotionState: flat, 30 deg pitch, -40 deg
roll and 80 deg pitch give the expected acceleration, quaternion and basic orientation. No controller with sensors
was connected on the dev machine (SDL2 virtual controllers cannot emulate sensors), so the live SDL path is untested.

## Keyboard / mouse motion (branch feat/keyboard-motion)

For players without a controller gyroscope: keys tilt the emulated Vita, the mouse rotates it.

| Where | What |
| --- | --- |
| `Core/Input/VirtualMotion.cs` | Platform-neutral integrator. Neutral = lying flat, screen up (the historic at-rest values). Pose = `Yaw(z) * Pitch(x) * Roll(y)` in the flat frame. Each key-driven angle follows its target (held = ±max angle, released = 0) through a critically damped spring (omega 12 rad/s x speed): 30 deg reached ~70% at 0.2 s, ~97% at 0.45 s, peak ~130 deg/s, no overshoot; release eases back the same way. Mouse look adds pitch/yaw offsets (0.2 deg/px x sensitivity, pitch clamped to ±80 deg, total pitch ±89.5 deg) that stay until re-centred; spread over the poll interval and capped at 720 deg/s (below SceMotion's 5 rev/s gyro clamp). Samples every 4 ms of host time: accel = gravity seen by the pose, gyro = body-frame rotation between consecutive poses, so MotionFusion's quaternion / matrices / basic orientation agree with the accelerometer. Gaps over 1 s are skipped (springs still advance). |
| `VitaControl` | `TiltForward/Back/Left/Right`, `TwistLeft/Right`, `MotionRecenter` (appended: older enum values unchanged). Any profile can bind them (a pad without a gyro too); they press no Vita button. |
| `DefaultMappings.MotionKeys` | Keypad 8/2/4/6 tilt forward/back/left/right, 7/9 twist, 5 re-centres; plus T/G/F/H, R/Y, B for keyboards without a keypad. input.json version 2; a version 1 keyboard profile gets the defaults whose key it does not already use. |
| `ControllerProfile` | `MotionSource` (keyboard profile; `Auto` = controller with sensors if connected, else keys/mouse; `Controller`, `KeyboardMouse`, `Off`) and `Tilt` (`MaxAngle` 30, `Speed` 1, `MouseLook` on, `MouseSensitivity` 1, `MouseInvertY`). |
| `Desktop/Input/HostInput.cs` | Picks the source. The virtual motion stays silent (historic at-rest SceMotion state) until a tilt key or mouse look is first used. |
| `Desktop/Host/WindowHost.cs` | Middle mouse button held + move = mouse look. |
| `Hle/Motion/MotionDevice.cs` | A sample over 1 s older than the previous one starts a new stream (switching between the controller's clock and the keyboard's). |
| Controles tab (Teclado) | "Movimento (giroscópio)": source, max angle, speed, mouse look + sensitivity + invert, live readout; rows "Inclinar para frente/trás/esquerda/direita", "Girar para esquerda/direita", "Recentralizar". |
| `ScriptedInput` | `key@T:Name[:len]` holds a key (Silk.NET name) through the default keyboard profile (buttons, sticks and tilt), `mlook@T:dx:dy[:len]` moves the mouse with the middle button held. |

Verified: unit tests `VirtualMotionTests`; ui-snapshot (`09_keyboard_tilt`, `10_keyboard_motion_rows`); Invizimals
(PCSA00552, SceMotionState at 0x823C70F8) with `--input=key@f300:Keypad8:f240,key@f300:H:f240,mlook@f800:150:0:f30`
and `--peek`: at rest before the keys (identity quaternion), (0.433, 0.5, -0.75) G with keypad 8 + H held (30 deg
forward + 30 deg right), back to (0, 0, -1) after release, and the quaternion Rx(-90) * Rz(-30) after 150 px of mouse look.

## Pause hotkey (branch feat/pause-key)

A host hotkey pauses and resumes the whole emulation. Default key: `P` (Space, the usual choice, is a Cross key of the
historic mapping); controllers have none by default and can bind a button combination.

| Where | What |
| --- | --- |
| `Core/EmulationPause.cs` | The Core pause API (`Pause`/`Resume`/`Toggle`/`IsPaused`, `Changed`). Freezes the guest clock, interrupts every live guest thread of every process (shell + applications): each parks at its next safe point, like a suspended process. The thread with a GPU scene open (`Emulator.OpenSceneThread`, set by `GpuQueue.Submit` on BeginScene/EndScene) runs on until it ends the scene, so the GPU finishes the frame. `WaitWhilePaused` blocks host-side clock waits; `ActiveHostTicks` leaves the pause out of host-time statistics (audio gaps). |
| `Util/GuestClock.cs` | `Freeze`/`Thaw`: the clock reads a constant while paused and continues from that value (no time jump); `WaitUntil` waits for the resume (turbo moves are dropped). CPU pacing does not sleep while paused. |
| `IAudioBackend.SetPaused` | The first `Emulator` follows `Changed`: OpenAL pauses its sources (AL_PAUSED, not counted as an underrun) and starts none until the resume; Android pauses its streams. |
| Renderer (`VulkanRenderer.Pause.cs`) | While paused the idle GPU worker presents the last frame again when an overlay changes, on resize and about once a second; a pending F10 / --diag-at capture captures that frame at once. `SetStatusOverlay`: the top-left "Pausado (P)" badge (`Desktop/Host/PauseBadge.cs`). |
| `VitaControl.PauseEmulation` | Host hotkey control (appended). Keyboard default `key:P`; input.json version 3, a version 2 keyboard profile gets `P` when free. `InputCombo` ("pad:back&pad:start") = inputs held together, stored with the single bindings (which skip it). |
| Controles tab | "Atalhos do emulador" > "Pausar emulação"; on a controller the ⊕ button records a combination (hold the buttons, release). |
| Window / CLI | `WindowHost.PauseToggled` (key event, never passed to the game; controllers polled at 100 Hz on the window thread since the paused guest polls nothing). `--start-paused`. Scripted input: `key@f300:P` toggles once when it starts, `fN+S` = S wall seconds after frame N (steps during a pause), `key@...:F10` captures. |
| Android | The pause menu and onPause/onResume call the same Core API (`EmulationSession.Pause/Resume`). |
| Watchdog | No stall reports while paused. |
