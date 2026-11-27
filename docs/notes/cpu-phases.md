# CPU phase profile and budget alerts

*[Português](cpu-phases.pt-BR.md)*

Every guest thread keeps a profile of where its host time goes in the CPU dispatcher (`Cpu/PhaseProfile.cs`). It
exists to find slow emulator paths from the log or an F8 capture alone, without the phone and without temporary
instrumentation. Dead Space's audio stopped because a mixer round took longer than the consumer's 11 ms, and the causes
were slow paths hidden behind each other: VFP short-vector blocks run only by the interpreter (~2000 host cycles per
vector instruction), a 13 ms compile on the guest thread, the .NET JIT on first use, GC pauses counted in the guest
clock.

## What is counted

| Phase | What it is |
|---|---|
| `dispatch` | block lookup, dispatch-table republish, exit linking, the loop's bookkeeping (switching blocks) |
| `interp` | a block run by the interpreter |
| `interp-vector` | a block run by the interpreter in VFP short-vector mode |
| `vector-code` | a block's short-vector expansion run as compiled code |
| `native` | native code (chained blocks and interpreter callouts included, HLE calls from it excluded) |
| `il` | IL-compiled blocks |
| `verify` | differential verification and debugging hooks |
| `hle` | HLE calls (waits included, so not budgeted) |
| `throttle` | sleeping off CPU pacing (not budgeted) |

The profile counts while an F8 capture records or keeps its pre-roll (the detailed log, `Util/DebugMode.cs`), with
`--debug-mode`, and for the whole run with `VITASHARP_PHASE_PROF=1`; `VITASHARP_PHASE_PROF=0` keeps it off. It is
off otherwise: measured on x64, it costs 1.3% of a native-code loop and 26% of an interpreted loop of two-instruction
blocks. While on, every dispatcher turn counts its guest cycles and its phase (exact), and host time is sampled: one
turn in 31 is timed and scaled back. `VITASHARP_PHASE_PROF=1` times every turn (every slow single turn is seen); a
capture keeps the sampling, because timing every turn doubles the host time of interpreted code, which the guest
clock counts. A timed turn's dispatch time includes the clock reads themselves (tens of ns).

Rare events are always timed, outside the phase sums: blocks decoded on the guest thread, blocks compiled on the guest
thread (a compile the thread waits for: its guest clock counts it as the thread's own time), and interpreter callouts
from native code (instructions the backend does not translate).

## Alerts

Every 256 K guest cycles a thread checks its budget. A `[phase-alert]` line in the log names the thread and the phase
when:

- the thread spent more than 70 ns of host time per guest cycle in the window (about 200 host cycles at 3 GHz;
  `VITASHARP_PHASE_BUDGET_NS` changes it). Native code runs at 1-5 ns per guest cycle, the interpreter at 10-40, an
  interpreted VFP vector instruction at ~600;
- a quarter or more of the window ran interpreted in hot blocks (1024+ executions without compiled code);
- a block compiled on the guest thread took more than 1 ms;
- a single timed turn took more than 2 ms (a .NET JIT compile or a GC pause on first use, an inline compile, a long
  run without a pacing check).

The first three alerts of each kind per thread are logged, then one in 64; all are counted.

## Where it shows

- **Log** (while the profile counts): `PERF phases <thread>: ...` in the periodic PERF summary (since the last report) and at the end of the run
  (over the run), for the six threads that ran the most guest cycles; `[phase-alert]` lines when they happen.
- **F8 capture**: `context.txt` holds a `cpu-phases:` line per thread at the start and at the stop of the recording;
  `report.txt` has a "CPU phases" section with what each thread did during the recording and the last
  `[phase-alert]` lines of `log.txt`. Desktop and Android alike: there is no key, menu or window for it.

Example (a vector-mode mixer before its blocks were compiled; synthetic numbers of the same shape):

```
PERF phases mixer: 0.30 M guest cycles in 180.0 ms of host time (600.0 ns/cycle); interp-vector 0.30 M cycles 100% 600.0 ns/cycle (longest 0.60 ms); hle 0.0 ms, pacing sleeps 0.0 ms; blocks decoded on the thread 0 (0.0 ms), compiled on the thread 0 (0.0 ms, longest 0.00 ms); interpreter callouts from native code 0; hot blocks interpreted 0 K cycles; alerts 1; timed turns 300
[phase-alert] thread 'mixer': 600 ns of host time per guest cycle over 300 K cycles (budget 70 ns, ~200 host cycles): interp-vector took 100% (600 ns/cycle over 300 K cycles)
```

## Budget tests

`tests/VitaSharp.Tests/CpuBudgetTests.cs` (category `Budget`, part of `Category!=Slow`) checks the paths a warm loop
must take with deterministic counts of the profile, never wall-clock time: a hot block runs as code (no interpreted
block in the loop body, no interpreter callouts), nothing is compiled on the guest thread, block switches stay in
native code (dispatcher turns only at the pacing checks), a VFP vector-mode loop interprets nothing once warm. Short
wait timeouts are covered by `PreciseTimeoutTests`.

## Reproducing the phone's timing on the PC

The phone runs ahead-of-time compiled .NET (no JIT at run time) on slower cores. The closest PC setup:

1. Publish the Native AOT desktop build (no .NET JIT at run time, as on the phone; first-use JIT pauses go away):
   `dotnet publish src/VitaSharp.Desktop -c Release -r win-x64 -p:VitaSharpRelease=true -p:VitaSharpAot=true -o <dir>`
   (`linux-x64` / `osx-arm64` on those systems; the requirements are in `VitaSharp.Desktop.csproj`). The IL CPU
   backend does not exist there, as on the phone.
2. Keep the guest clock as the phone's: the default 333 MHz (`VITASHARP_CPU_MHZ`), no `--speed`.
3. Give it fewer host cores: pin the process to two cores (Windows: `start /affinity 3`; Linux: `taskset -c 0,1`).
4. Read the `PERF phases` lines and the `[phase-alert]` lines: a phase that runs at more ns per guest cycle on the PC
   than its budget runs slower still on the phone.

What it does not reproduce: the ARM64 backend's code (the PC runs the x64 backend), the phone's timers (Linux
nanosleep, no spinning: `HostTuning.AvoidBusyWaits`) and its thermal clocks.
