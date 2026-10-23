# feat/gpu-dump: WIP notes (checkpoint 2026-10-23 night)

Worktree: `C:\Users\guisa\VitaSharp\.claude\worktrees\gpu-dump`, branch `feat/gpu-dump` from main 0836830.
Scratch: `<scratchpad>\gd\` (bin = build output, cache = shader cache, sc\ = scenario files, rt.ps1 = record+replay+compare
helper, kz_short.vsgd / kz_skin.vsgd = test dumps; delete when done except maybe one small example).

## Design (implemented)

- **Recorder** (`src/VitaSharp.Core/Gpu/GpuDumpRecorder.cs`): hooks in `GpuQueue.Run` (BeforeExecute/AfterExecute
  around `Backend.Execute`, AfterIdle after `Backend.Idle`); one static read per command when off.
  Starts on the GPU worker right after a present (frame boundary): `--gpu-dump-at=F1:F2[:file]` starts after present
  F1-1 and records F2-F1+1 frames; F8 in the window toggles (stops at the next present, auto-stop after 1800 frames).
- **Memory**: `GuestMemory.ReadSpan` (both overloads) calls `GpuDumpRecorder.Tap` when `TapEnabled` (one flag check
  when off). Only reads on the GPU worker thread count. The recorder keeps a shadow copy of every page the backend
  read: first read = whole 4 KB page stored, later reads compare the whole page with the shadow and store only the
  changed byte range. Pages whose DirtyPages byte stayed clear since the last compare in the same sweep epoch are
  skipped (`_verified`). At the start the whole DirtyPages table is set to 1 so the renderer re-hashes (= re-reads) its
  cached textures in the first frame. Memory records are written before the command whose execution read them.
- **GPU-only state**: render targets exist only on the GPU, so at start `IGpuBackend.ExportDumpState()`
  (`VulkanRenderer.GpuDump.cs`) reads back every colour RT (ordered by LastScene); replay `ImportDumpState` recreates
  them via GetRenderTarget + buffer->image copy. Without this, Killzone menu text was missing (glyph RTs). Depth not
  carried (not needed so far).
- **Format** (`GpuDumpFormat.cs`, v1): "VSGPUDMP" u32 version, text header (`key: value`: title, build, commit, GPU,
  renderer.N description lines incl. MSAA/shader settings, env.VITASHARP_*, first-present...), then chunks
  `u8 type, u32 raw, u32 stored, u8 codec (0 stored / 1 Brotli)`; type 1 records, type 2 end summary. One chunk per
  frame (also flushed at 32 MB). Records: Memory, ProgramInfo (GXP bytes), VertexProgram, FragmentProgram,
  BackendState, BeginScene, Draw, EndScene, Finish, MidSceneFlush, Present(+host µs, guest frame, present no.),
  Signal, Callback (write range only; its effect reaches the dump through later reads), Idle.
  Draw uniform blocks: inline / reference to an identical block among the last 4096 / delta vs the previous block of
  the same program+stage (UniformBlocks class).
- **Renderer hooks**: `PresentCount` property, `VulkanRendererOptions.FirstPresent` (replay keeps recorded frame
  numbers so frame_N.png names match), ExportDumpState/ImportDumpState in a new partial file.
- **Replay** (`src/VitaSharp.Desktop/ReplayGpu.cs`): `replay-gpu <dump> [--out --frame=N --dump-every --dump-from
  --trace-draws=N (diagnostic capture writer: draws.txt, shaders, targets, textures) --draw-limit=N[@F] --msaa=
  --debug-fs= --debug-tex0= --env=K=V --no-recorded-env --compare=<dir> --window --hold --speed --fast --no-idle
  --no-targets --info --recompress=out --quality=]`. Fresh GuestMemory, drives VulkanRenderer directly (no GpuQueue),
  recorded env vars re-applied unless overridden; writes PNGs + contact sheets + compare.txt/diff_N.png.
- **Desktop**: `GpuDumpControl.cs` (F8, --gpu-dump-at / VITASHARP_GPU_DUMP_AT, header, PT-BR status strings,
  StopAtExit), WindowHost F8 event, Runner wiring, `Runner.WriteContactSheet` made internal, Program.cs command+usage.

## Verified so far

- kz_short (menu, frames 600-650): 51/51 replayed frames pixel-identical; dump 3 MB (17 MB RT state).
- kz_skin gameplay frames 4300-4449: 29/30 compared frames pixel-identical, 1 with mean diff 0.0000 (a few pixels).
  Dump 119 MB: first frame 24.8 MB (RT state + all textures), then 0.63 MB/frame (~19 MB/s at 30 fps, 6.6 MB per
  recorded second at the 8-10 fps the recording ran at).
- Cost while recording (KZ): GPU worker 11.8 ms/frame for the recorder itself, but the writer (Brotli q5, ~30 MB/s)
  could not keep up -> 63 ms/frame waiting -> game ran ~10 fps. Brotli speed test on 452 MB raw: q1 341 MB/s 141 MB,
  q2 169 MB/s 128 MB, q3 149 MB/s 127 MB, q4 83 MB/s, q6 24 MB/s 119 MB, q9 6 MB/s 118 MB.
  Default changed to q3 in this checkpoint (NOT yet re-measured).
- Build 0 warnings (last build before the q3 change). Unit tests NOT yet run on this branch.

## Next steps (in order)

1. Rebuild (`dotnet build src/VitaSharp.Desktop -c Release -o <scratch>\gd\bin`), rerun `rt.ps1 -name gd_kz_skin
   -dump kz_skin.vsgd`; check writer wait is ~0. If still writer-bound: compress chunks on 2-3 parallel tasks
   (write in order), and/or reduce raw size: vertex uniforms are 347 MB/150 frames (21K distinct blocks of ~10 KB);
   look at why blocks are so big (unbounded UBs?) and consider delta vs any recent block of the same length.
2. Reduce GPU-worker recorder cost (11.8 ms/frame; backend reads 5.2 MB/frame, all compared): profile; maybe compare
   only the read range when the page is dirty, or hash instead of compare.
3. Measure recording cost properly: run `sc\gd_kz_skin_base.txt` (same route without dump) and compare FPS lines
   around frames 4300-4450, plus PERF lines.
4. Look at the one differing kz_skin frame (frame_004415 diff png in kz_skin_replay) - async writeback/visibility
   timing? Try `--no-idle`.
5. Tests: `tests/VitaSharp.Tests/GpuDumpFormatTests.cs` - round trip DrawCommand/BeginScene/... through
   GpuDumpCodec (programs need a GXP: build a minimal one or test non-draw commands + UniformBlocks ref/delta),
   header round trip, chunk Brotli/stored round trip, truncated file. Run
   `dotnet test tests/VitaSharp.Tests -c Release --filter "Category!=Slow"`.
6. Second game: Sonic Mania mania_boot or Uncharted uc_walk with data root
   `<scratchpad>\nrb\data` (contains junctions: rmdir them before deleting anything there).
7. Decide UX for debug mode: ring of last N seconds was judged too expensive (memory: shadow + per-frame chunks ~0.6
   MB/frame compressed, CPU ~12 ms/frame); keep explicit F8 start/stop. Mention in F10 info.txt / debug-mode.md.
8. Trace/bisect check: `replay-gpu kz_skin.vsgd --frame=4420 --trace-draws=4420 --draw-limit=100`, `--msaa=off`.
9. Write `docs/notes/gpu-dump.md` (format, PT-BR user instructions for F8, replay usage, limitations: depth not
   carried, CPU-side writeback effects not replayed, dumps are build-sensitive only through the format version,
   MSAA images not carried, async shader timing). Delete this NOTES file before merging.
10. Clean up scratch outputs (`.vitadata\tests\gd_*`, scratch gd\ dumps/bin).
