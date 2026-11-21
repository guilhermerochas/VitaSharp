# Save states

Saving and loading the whole emulated machine: a game run directly (one process) or the Vita shell with the
applications it opened (several processes, saved together; see "Several processes").
Code: `src/VitaSharp.Core/SaveState`, the `*.Snapshot.cs` parts next to each class, the generator
`src/VitaSharp.Core.Generators/SnapshotGenerator.cs`. Module coverage: the inventory in `dev-snapshots.md`.

## Use

- **Desktop window:** F5 quick save, F7 quick load, F6 selects slot 1-9, Shift+F5 / Shift+F7 save / load that slot.
  The title bar shows the result. The launcher lists a game's states with their pictures; clicking one starts the
  game and loads it (`--load-state=PATH`, after 120 presented frames).
- **Android:** pause menu "Salvar estado" / "Carregar estado", slots 1-6 with picture and date.
- **Headless tests:** `--save-state=fN[,fN...]` (to `test_fN.vss`), `--load-state-at=fN[,...]` (the last state this
  run saved), `--load-state=PATH[@fN]`.
- Files: `<data>/savestates/<TITLEID>/quick.vss`, `slotN.vss`.

## File

`"VSSTATE\0"`, format version (2), uncompressed header (emulator version, `SnapshotSchema.Hash` = the version of
the hand-written layouts, title id/name, app version, time, guest seconds, PNG picture), then Brotli sections:
`layouts` (first, so a slot list checks it cheaply), `system`, `hle.functions`, `kernel.objects`, `hle:<Module>`,
`memory.blocks`, `mem:<address>` (a bitmap of the non-zero pages, then those pages). Since several processes are
saved: `processes` (each process's pid, application id, title id, suspended, on screen; the first process first),
`process<N>/system`, `process<N>/hle.functions` and `process<N>/hle:<Module>` for the other processes, and
`memory.spaces` (before `memory.blocks`: the pid of the process whose address space holds each block, 0 for CDRAM
and kernel memory); a block in another process's space than the first one's is `mem:<address>@<pid>`. A state of
one process has the sections of the builds before.

### Several processes

Under the Vita shell every process has its own user address space (the shell and a game are both linked at
0x81000000), its threads, HLE function table and modules, around the one kernel the system shares (the kernel object
table, the kernel process, CDRAM and kernel memory). A save holds them all: every process's threads are held at
their safe points (a GPU scene open in any process waits), each private block is saved with the process whose space
holds it, and blocks that a process that ended left behind are not saved. A load needs the same processes running
(same pids, application ids and titles, each suspended or on screen as when saved: open the same applications in the
same session, then load); otherwise it is refused before anything stops, with the difference in the message. Covered
by `SaveStateMultiProcessTests` (a synthetic shell and application at the same addresses).

### States of other builds

Each `[GenerateSnapshot]` type writes every field in its own length-prefixed block and its `SaveExtra` data in one
more block; `layouts` records, for each type the state holds, its field names in that order with the shape of their
data (primitive kinds, enums by underlying type, nested states by type, no names) and a signature of its
`SaveExtra`/`LoadExtra` (the snapshot reader/writer calls they make, in their control flow: renamed locals, braces and
comments do not change it). A load maps the saved fields to this build's by name, as PPSSPP's versioned `DoState`
sections keep old states loading:

- a field this build no longer has is skipped; a renamed field declares its old names with `[SnapshotAlias("old")]`;
- a field this build added takes the value the older build behaved as, declared on the field with
  `[SnapshotMissing(value)]` (`[SnapshotMissing]`: empty/zero). **Every field added to a saved type needs it**: without
  it, states of older builds are refused with the module's name, never loaded with a guess;
- a field whose data changed shape, or hooks that read or write differently, refuse the state with the module's name;
- a module the saving build did not have keeps its initial state when the game has not used it in this session.

The check runs before anything stops (`SaveStates.CheckSections`), so a refused state leaves the game running. The
format version or `SnapshotSchema.ManualVersion` (raise it whenever a hand-written layout changes: kernel objects,
memory, the container, the HLE function table) still refuse every state of another value. Format 1 (positional,
before 13/11/2026) is refused. Tests: `SaveStateLayoutTests`.

## Safe point

1. Every guest thread is *held*: it parks at its next block boundary at call depth 0 while the emulation and the guest
   clock still run, so threads inside guest callbacks finish them and module service threads (the GXM display queue,
   whose callback waits for vblanks) go idle. The thread with a GPU scene open runs on until the scene ends.
2. The emulation is paused (clock frozen), the GPU queue drained, and every thread checked:
   - parked at depth 0: saved as is;
   - blocked in an HLE wait at depth 0 (`Wait.Until`, `Scheduler.Block`): saved as "about to issue the call" (PC =
     the SVC, registers as at the call); on load the call is issued again and blocks again, with its timeout
     restarted. A reissued condition wait does not release its mutex again (another thread may own it by
     now): it waits for the signal and then takes back the lock count it had released, which is saved;
   - anything else (a nested callback, host work, a condition wait taking its mutex back, a frame in the GXM display
     queue): "not at a safe point", the save resumes for a moment and retries (3 s at most).
3. The whole state is copied to memory (~0.1-0.2 s with the game stopped), the game resumes, the file is compressed
   and written in the background. A per-thread HLE sequence number checks that no thread woke while it was taken.

## Load

The file is decompressed while the game still runs. Then the threads are held, the service threads idle, the
emulation paused, every guest thread stopped, and: GPU backend replaced (its caches rebuild from guest memory, as
after a LoadExec; render targets the game reads back before drawing are black for a frame; the new renderer keeps the
window's Vulkan instance, device, surface and swapchain, `VulkanRendererOptions.KeepDevice`, on by default for every
front-end: recreating them per load crashed NVIDIA and lost the device on Mali at the second recreation), translated code dropped,
memory blocks and contents restored, HLE function table, kernel objects (new threads with their saved registers;
module service threads kept and given their saved UIDs; one the state has and this session does not run yet is
taken back by its owner from the saved record, and one this session made after the save is let go and stops,
`IServiceThreadOwner`: the kernel's global work queue worker, the interrupt thread), modules (with which export
entries are in the library database: what kernel modules registered or released after loading), guest clock (exactly the saved time), then the
threads start. The loaded modules must be the saved ones at the same addresses: a game that loads modules late
refuses a state from a different point with a message (restart the game and load again).

A state saved after a LoadExec (Jak 1 or God of War 1 started from their collection's menu) loads in a new session:
the executable it was saved in is its first module record (`SaveStates.ProgramOf`, read from the sections before the
memory). `--load-state` (the launcher's state list, `rerun` of a Gravar tudo capture) boots that executable directly
(`--self` still wins); F7 or a slot load while another executable of the game runs performs the LoadExec first and
loads once the new program presented 120 frames. The modules are matched by file and segment address, not by UID,
and take the saved UIDs (a direct boot numbers the kernel objects differently from the session that did the LoadExec).
The LoadExec's arguments are not passed again: the saved memory and registers replace whatever the boot set up.
Android does not do the LoadExec step yet (its load from the pause menu refuses such a state with the module message).

## Host resources

Recreated on load: audio ports, AT9/MP3/AAC and H.264 decoders (empty; movies restart on the key frame before the
saved position, a few pictures of drift), MP4 demuxers and AvPlayer media (reopened and seeked), open files
(reopened at their offset), camera captures, system fonts. Dropped: network sockets (the game sees a disconnect).
Saved data, trophies and
registry values the game wrote are the user's (on disk) and are not rolled back.

## Tested (desktop, 04/11/2026)

Save, load ~10 s later, play on (headless runs with the scenario inputs). "Pass" = saved, loaded, no crash, the
picture continues from the saved point, frame rate as before the load.

| Game | Family | Result |
|---|---|---|
| Knightin'+ | Construct 2, no video | pass (save select, then the dungeon entered after the load) |
| Uncharted: Golden Abyss | gameplay, NGS, fibers | pass (title and gameplay) |
| F1 2011 | movies, NGS, race | pass (race; intro movie reopened by Mp4) |
| Persona 4 Golden | movies | pass (title) |
| LittleBigPlanet, Devious Dungeon | NGS audio | pass |
| Killzone: Mercenary | AT9, touch gestures | pass (after SystemGesture state) |
| Invizimals | camera / motion | pass (menu) |
| The Deer God, Stardew Valley | Unity | pass |
| Sonic Mania, Blast 'Em Bunnies, Jak and Daxter Collection | misc., AvPlayer (LLE) | pass |
| Tearaway | deferred contexts | pass (06/11/2026: loaded twice in a row in debug mode, Gravar tudo pre-roll states around the loads, moves with the stick after them; no device loss in 3 headless loads) |
| Minecraft, Gravity Rush | NP matching / voice ports, touch / motion, PVF game fonts | pass |
| Jak and Daxter Collection, God of War Collection | LoadExec after a load | pass (06/11/2026: state in the collection menu, loaded, then Jak 1 to PRESS START / God of War 1 to its menu at 30 fps) |
| Jak and Daxter Collection, God of War Collection, Uncharted | state saved after a LoadExec, loaded in a new session | pass (06/11/2026: Jak 1 / God of War 1 gameplay and Uncharted's title (its eboot.bin LoadExecs uncharted.self) with `--load-state` (boots the saved executable) and from the collection's menu (LoadExec first, as F7), 20 / 30 / 60 fps as when saved; rerun of a Gravar tudo capture of Jak 1: 98% of the frames as recorded) |

Modules in use at the save (16/11/2026): Final Fantasy X HD's launcher (Sas) and PCSG00845 (Atrac, its music)
save and load with the sound going on from the saved point. Audioenc/Videoenc/Mp4Recorder, G729, Location,
NetAdhocMatching/Rudp, TriggerUtil and Ime are covered by `SaveStateModulesInUseTests` only (no installed game reaches
them in a short run). The Vita shell with its applications saves and loads since 16/11/2026 (see "Several
processes"); not yet confirmed in a real shell session.
