<!-- pair: captures.pt-BR.md sha256=782bf75159042c077594d851c18c63a721956ef2efc7a1c28ca2a42b4e644765 -->
# Analysing a Record everything capture without the game

**English** | [Português](captures.pt-BR.md)

A **Record everything** capture (F8 on the PC, pause menu on Android) is made to be sent: someone posts the `.zip` on
GitHub and whoever fixes the problem works from it alone, on a computer that has neither the game, nor its update,
nor the plugins of the person who recorded it.

## What leaves with the capture

Nothing personal. Every text file of the capture (`session.txt`, `log.txt`, `report.txt`, `context.txt`, the thread
lists, `hle_calls.tsv`, `audio/files.txt`) and the GPU dump's header go through a last pass that replaces:

| What | Placeholder |
|---|---|
| the VitaSharp data folder | `<data>` |
| the home folder and the user name | `<user>` |
| the computer's name | `<pc>` |
| the PSN / RPCN online id and account options (`--psn-id`, `--rpcn-user`, `--rpcn-token`) | `<psn-id>` |
| MAC addresses | `<mac>` |
| e-mail addresses | `<email>` |

No licence, key, serial or password is written. No file of the game or of a plugin is copied: the save state holds the
guest memory as the game had it, which is what the re-run needs.

## What the capture says about the setup

`session.txt` names the title and its version without the game: `title-id`, `app-guest-path` (`ux0:app/<id>`),
`app-version` (the APP_VER the system reads; with an installed update, the application's own one too), `category`,
`content-id`, the `firmware` version, `kernel-plugins` on or off, the system language, the emulator options in effect
(`options:` and every `VITASHARP_*` variable set, `env:`), the build, the renderer and GPU, and every loaded module
(`module:` lines: name, guest path, address and image hash, the game's executable, the firmware modules and the
plugins), so two captures of the same version have the same hashes.

## Analysing it

| Analysis | Command | Needs |
|---|---|---|
| Report (frames, stalls, audio, findings) | `vitasharp-perf capture <folder or zip>` | nothing |
| GPU (what was drawn) | `VitaSharp.Desktop replay-gpu <folder>/gpu.vsgd --out=<dir>` | a Vulkan device (any; SwiftShader / lavapipe work) |
| CPU, HLE, audio, logic (the game runs again) | `VitaSharp.Desktop rerun <folder>` | a Vulkan device; no game, no plugins, no firmware |
| The system's side | read `hle_calls.tsv` and `hle_errors.txt` | nothing |

`rerun` brings the program up from the capture's own save state: every module the process and the kernel had loaded
(the game, the firmware modules, the plugins) is rebuilt from the state's memory, then the recorded input is played
back and the re-run is recorded and compared with the original (`reruns/<...>/compare.txt`: the pictures, and the
first HLE call that differs). A path written in the capture is never opened: the re-run uses the data folder it is
given (`--data=<folder>`). `--boot=title` boots the installed game instead and loads the state into it.

`hle_calls.tsv` has one line per HLE call of the recording (guest time, thread, function, library, r0-r3, result,
return address); `hle_errors.txt` sums them by function with the error returns.

What still needs the game: files the game reads from its folder during the recording (the re-run gets "not found" for
them and diverges from there; `hle_calls.tsv` shows where), firmware modules the game loads for the first time
during the recording (they come from the analyst's firmware), and captures of the system shell with applications open
(several processes; booting them from the state is not supported yet).

Details for developers: [`docs/notes/session-capture.md`](notes/session-capture.md).
