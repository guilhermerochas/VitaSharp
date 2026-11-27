# Perfil de fases da CPU e alertas de orçamento

*[English](cpu-phases.md)*

Toda thread do guest guarda um perfil de onde vai o seu tempo do host no despachante da CPU (`Cpu/PhaseProfile.cs`).
Ele existe para achar caminhos lentos do emulador só pelo log ou por uma captura F8, sem o celular e sem
instrumentação temporária. O som do Dead Space parava porque uma rodada do mixer passava dos 11 ms do consumidor, e as
causas eram caminhos lentos escondidos uns atrás dos outros: blocos em modo VFP short-vector só no interpretador
(~2000 ciclos do host por instrução vetorial), uma compilação de 13 ms na thread do guest, o JIT do .NET no primeiro
uso, pausas de GC contadas no relógio do guest.

## O que é contado

| Fase | O que é |
|---|---|
| `dispatch` | busca do bloco, republicação na tabela de despacho, ligação de saídas, a contabilidade do laço (troca de bloco) |
| `interp` | um bloco rodado pelo interpretador |
| `interp-vector` | um bloco rodado pelo interpretador em modo VFP short-vector |
| `vector-code` | a expansão short-vector de um bloco rodando como código compilado |
| `native` | código nativo (blocos encadeados e chamadas ao interpretador incluídos, chamadas HLE de dentro dele não) |
| `il` | blocos compilados em IL |
| `verify` | verificação diferencial e ganchos de depuração |
| `hle` | chamadas HLE (inclui esperas, por isso fora do orçamento) |
| `throttle` | dormindo o ritmo da CPU (fora do orçamento) |

O perfil conta enquanto uma captura F8 grava ou guarda o pré-roll (o log detalhado, `Util/DebugMode.cs`), com
`--debug-mode` e, na execução inteira, com `VITASHARP_PHASE_PROF=1`; `VITASHARP_PHASE_PROF=0` o mantém desligado. Fora
disso fica desligado: medido no x64, custa 1,3% de um laço em código nativo e 26% de um laço interpretado de blocos de
duas instruções. Ligado, toda volta do despachante conta os seus ciclos do guest e a sua fase (exato), e o tempo do
host é amostrado: uma volta em 31 é cronometrada e escalada de volta. `VITASHARP_PHASE_PROF=1` cronometra toda volta
(toda volta lenta isolada aparece); a captura mantém a amostragem, porque cronometrar toda volta dobra o tempo do host
do código interpretado, que o relógio do guest conta. O tempo de troca de bloco de uma volta cronometrada inclui as
próprias leituras do relógio (dezenas de ns).

Eventos raros são sempre cronometrados, fora das somas das fases: blocos decodificados na thread do guest, blocos
compilados na thread do guest (uma compilação que a thread espera: o relógio do guest conta como tempo da própria
thread) e chamadas ao interpretador a partir do código nativo (instruções que o backend não traduz).

## Alertas

A cada 256 K ciclos do guest a thread confere o orçamento. Uma linha `[phase-alert]` no log diz a thread e a fase
quando:

- a thread gastou mais de 70 ns de tempo do host por ciclo do guest na janela (~200 ciclos do host a 3 GHz;
  `VITASHARP_PHASE_BUDGET_NS` muda). Código nativo roda a 1-5 ns por ciclo do guest, o interpretador a 10-40, uma
  instrução vetorial VFP interpretada a ~600;
- um quarto ou mais da janela rodou interpretado em blocos quentes (1024+ execuções sem código compilado);
- um bloco compilado na thread do guest levou mais de 1 ms;
- uma única volta cronometrada levou mais de 2 ms (compilação do JIT do .NET ou pausa de GC no primeiro uso,
  compilação na própria thread, uma execução longa sem checagem de ritmo).

Os três primeiros alertas de cada tipo por thread vão para o log, depois um em 64; todos são contados.

## Onde aparece

- **Log** (enquanto o perfil conta): `PERF phases <thread>: ...` no resumo PERF periódico (desde o último relatório) e no fim da execução (a
  execução inteira), para as seis threads que mais rodaram ciclos do guest; linhas `[phase-alert]` quando acontecem.
- **Captura F8**: o `context.txt` tem uma linha `cpu-phases:` por thread no início e no fim da gravação; o
  `report.txt` tem a seção "CPU phases" com o que cada thread fez durante a gravação e as últimas linhas
  `[phase-alert]` do `log.txt`. Igual no desktop e no Android: não há tecla, menu nem janela para isso.

Exemplo (um mixer em modo vetorial antes de os blocos serem compilados; números sintéticos do mesmo formato):

```
PERF phases mixer: 0.30 M guest cycles in 180.0 ms of host time (600.0 ns/cycle); interp-vector 0.30 M cycles 100% 600.0 ns/cycle (longest 0.60 ms); hle 0.0 ms, pacing sleeps 0.0 ms; blocks decoded on the thread 0 (0.0 ms), compiled on the thread 0 (0.0 ms, longest 0.00 ms); interpreter callouts from native code 0; hot blocks interpreted 0 K cycles; alerts 1; timed turns 300
[phase-alert] thread 'mixer': 600 ns of host time per guest cycle over 300 K cycles (budget 70 ns, ~200 host cycles): interp-vector took 100% (600 ns/cycle over 300 K cycles)
```

## Testes de orçamento

`tests/VitaSharp.Tests/CpuBudgetTests.cs` (categoria `Budget`, parte do `Category!=Slow`) confere os caminhos que um
laço aquecido tem que seguir com contagens determinísticas do perfil, nunca tempo de parede: bloco quente roda como
código (nenhum bloco do corpo do laço interpretado, nenhuma chamada ao interpretador), nada é compilado na thread do
guest, as trocas de bloco ficam no código nativo (voltas do despachante só nas checagens de ritmo), um laço em modo
vetorial VFP não interpreta nada depois de aquecido. Tempos-limite curtos estão no `PreciseTimeoutTests`.

## Reproduzir o tempo do celular no PC

O celular roda .NET compilado antes (sem JIT em tempo de execução) em núcleos mais lentos. O mais próximo no PC:

1. Publique o build Native AOT do desktop (sem JIT do .NET em tempo de execução, como no celular; as pausas de JIT no
   primeiro uso somem):
   `dotnet publish src/VitaSharp.Desktop -c Release -r win-x64 -p:VitaSharpRelease=true -p:VitaSharpAot=true -o <pasta>`
   (`linux-x64` / `osx-arm64` nesses sistemas; os requisitos estão no `VitaSharp.Desktop.csproj`). O backend IL da CPU
   não existe nele, como no celular.
2. Mantenha o relógio do guest como o do celular: os 333 MHz padrão (`VITASHARP_CPU_MHZ`), sem `--speed`.
3. Dê menos núcleos do host: fixe o processo em dois núcleos (Windows: `start /affinity 3`; Linux: `taskset -c 0,1`).
4. Leia as linhas `PERF phases` e `[phase-alert]`: uma fase que no PC já passa do orçamento em ns por ciclo do guest
   fica ainda mais lenta no celular.

O que isso não reproduz: o código do backend ARM64 (o PC roda o backend x64), os timers do celular (nanosleep do Linux,
sem giro: `HostTuning.AvoidBusyWaits`) e os clocks térmicos dele.
