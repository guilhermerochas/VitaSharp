# VitaSharp.PerfTools (`vitasharp-perf`)

Performance analysis CLIs for developers. Built with `VitaSharp.sln` (CI builds it), never part of the release
packages: `build/publish.ps1` / `build/publish.sh` publish `VitaSharp.Desktop` only.

```
dotnet build tools/VitaSharp.PerfTools -c Release -m:2 -o <dir>
<dir>/vitasharp-perf --help
<dir>/vitasharp-perf mali-cost <dump.vsgd> [--gpu Mali-G57] [--frames a-b] [--fp16-whatif]
```

| Command | What it does | Docs |
|---|---|---|
| `mali-cost` | replays a GPU dump (`.vsgd`, F8 / `--gpu-dump-at`) headless and estimates each frame's GPU time on a Mali GPU with Arm's Mali Offline Compiler (`malioc`): per-draw fragments and vertices, top draws, bound units, spilling shaders, tile load/store bytes, an fp16 what-if | [mobile-gpu.md](../../docs/notes/mobile-gpu.md#mali-cost-estimator-vitasharp-perf-mali-cost) |

Adding a command: one class implementing `IPerfCommand` (`PerfCommand.cs`) per file in `Commands/`, registered in
`Program.cs`. Commands that replay a dump use `GpuDumpPlayer` (VitaSharp.Renderer.Vulkan), the loop `replay-gpu`
uses. Like `replay-gpu`, pin a run to 4 CPUs at below-normal priority on a shared PC, and give it a scratch
`--shader-cache`.
