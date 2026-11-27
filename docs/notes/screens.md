# Game screens: what a screen waits for, and the answer

Tools that tell, without guessing, whether a game is **waiting for the player**, **loading** or **busy**, read the
text on the screen, and apply the known answer of that screen. A still screen is not "compiling" or "loading" until
the `[input-wait]` line says so (Killzone: Mercenary's SECURITY SCAN waits for a thumb held on the panel).

## 1. The WAITING / LOADING / BUSY line (the emulator says it)

The detector (`src/VitaSharp.Core/Input/InputWaitMonitor.cs`, rules in [debug-mode.md](debug-mode.md)) runs in debug
mode, while an F8 "Gravar tudo" capture records, or with `VITASHARP_INPUT_WAIT=1` (Android: `config/env.txt`). Every
change is a log line:

```
[input-wait] WAITING FOR INPUT: PCSC00045 'Killzone: Mercenary' — reads front touch 60/s, ctrl buttons 60/s, rear touch 0/s; screen static since frame 5321
[input-wait] LOADING: PCSC00045 'Killzone: Mercenary' — file reads 4100 KB/s, 3.0 opens/s, 0 modules loaded, 2 shaders compiled, 15 code blocks/s; reads ...; screen static since frame 5100
[input-wait] BUSY: ... — no input read, no file I/O or compilation (the CPU/GPU works or the game hangs); screen static since frame F
[input-wait] ACTIVE: ... — the picture changes (frame F); reads ...
```

- **WAITING FOR INPUT**: a still picture, nothing loading, and the game reads the input every frame. What it reads
  points at the answer: `front touch` = touch or hold the screen, `rear touch` = the rear pad, `ctrl buttons` = a
  button. When only `front touch` is read, buttons do nothing.
- **LOADING**: a file is read, a module loaded or shaders/code compiled. Wait.
- **BUSY**: neither input nor loading: the game computes or hangs (worth an F8 capture).
- The F8 `report.txt` has a **Screen state** section (the state at the stop and the last changes) and
  `context.txt` a `screen:` line.

## 2. The text on the screen (screen-text)

```
VitaSharp.Desktop screen-text <png|capture folder|frames.vsfr> [--lang=en,ja] [--frame=N]
```

Prints each line of text with its box in pixels and its centre in 0..1 (the coordinates of `--nav` taps and of the
Android `touch:` lines):

```
screen text of shot.png (960x544, windows-ocr (Windows.Media.Ocr)):
  "SECURITY SCAN"  at 377,117 278x25  (centre 0.54, 0.24)
  "TAP to open Drake's Journal"  at 603,484 246x18  (centre 0.76, 0.91)
```

- Windows: the system OCR (Windows.Media.Ocr, through Windows PowerShell 5.1; no new dependency). Japanese needs the
  Japanese language with its OCR installed (Settings > Time & language > Language) and `--lang=ja` (or `ja,en`); a
  language that is not installed is named in the header and the installed ones are used.
- Linux/macOS: the `tesseract` command (`apt install tesseract-ocr tesseract-ocr-jpn`, `brew install tesseract
  tesseract-lang`); without it the command prints `(no OCR: ...)` and exits with code 3.
- The F8 report has the text of the last frame ("Screen text (last frame)").

## 3. The screen knowledge base (docs/games/screens.json)

Versioned in the repository. Each known screen has: `title` (the title ID), `screen` (its name), `match` (texts the
OCR reads on it, any one matching part of a line), `after` (how one gets there), `expects` (what it waits for),
`action` (the answer), `result`, `status` (`confirmed` or `investigating`) and `notes`. Each title has `toGameplay`
(the way from the boot to the gameplay).

Actions: `tap` (x, y), `hold` (x, y, seconds), `rear-tap` (x, y), `swipe` (x, y, x2, y2, seconds), `press` (button,
seconds), `wait` (seconds: the screen moves on by itself), `none` (not known yet). Positions in 0..1 of the screen.

```
VitaSharp.Desktop screens validate            # the base is valid (ScreenKnowledgeTests checks it too)
VitaSharp.Desktop screens list PCSC00045      # the way to the gameplay and each screen's answer (--nav and Android lines)
VitaSharp.Desktop screens match PCSC00045 <png|capture>   # OCR + which known screen it is + what to do
```

It is tooling data (navigation scripts, automated runs): the emulator never reads it to change its behaviour.
`VITASHARP_SCREENS` names another file.

### In navigation scripts

- **Desktop `--nav`**: `waitinput [TIMEOUT]` waits for WAITING FOR INPUT; `answer [TIMEOUT]` waits, reads the text of
  a full-size shot, finds the screen of the title in front in the base and runs its action (an unknown screen is a
  `NAV` warning with the text read).
- **Android developer builds (`VITASHARP_ANDROID_UI_INPUT`)**: the line `waitinput:SECONDS` holds the following lines
  until the game waits for input; `hold:x:y:ms` keeps the finger still. Each screen's Android lines are printed by
  `screens list` (`android:`).

### Adding a screen

Once the answer of a screen is known, add its entry (`confirmed` only after it was seen working; until then
`investigating` with what is known in `notes`) and run `screens validate` and `ScreenKnowledgeTests`.
