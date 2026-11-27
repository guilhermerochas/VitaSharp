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

## Reproduzir o tempo do celular no PC (o perfil celular)

O celular roda .NET compilado antes (sem JIT em tempo de execução) em núcleos mais lentos, com esperas que nunca giram,
a GPU Mali e um orçamento de gen0 do GC de 4 MB. O perfil celular do desktop liga tudo isso de uma vez:

```
build/phone-profile.sh  --data <pasta vazia> -- run <jogo ou .vpk> [opções do run]   # Linux, macOS (Git Bash também)
build/phone-profile.ps1 -Data <pasta vazia> run <jogo ou .vpk> [opções do run]       # Windows
```

O roteiro publica o build Native AOT do desktop uma vez numa pasta fixa (`<temp>/vitasharp-phone-profile`;
`--publish` / `-Publish` de novo depois de mudar o código; os requisitos do AOT estão no `VitaSharp.Desktop.csproj`:
ferramentas de build do MSVC no Windows, clang + zlib no Linux, as ferramentas de linha de comando do Xcode no macOS),
deixa uma pasta de dados nova sensível a maiúsculas como a do Android (Windows: `fsutil file setCaseSensitiveInfo`, numa
pasta vazia, sem administrador; o Linux já é; macOS só num volume sensível a maiúsculas), roda com
`VITASHARP_PHASE_PROF=1` e mostra as linhas `Phone profile:`, fps, `PERF phases` e `[phase-alert]` do log.

`--phone-profile` (ou `VITASHARP_PHONE_PROFILE=1`) em qualquer comando do `VitaSharp.Desktop` (`Core/Util/PhoneProfile.cs`):

| Opção | Perfil celular | Android |
|---|---|---|
| esperas | `HostTuning.AvoidBusyWaits` (dorme, nunca gira) | o mesmo (GamePerformance) |
| GPU | `VITASHARP_GPU_PROFILE=mali` (sem BCn: transcodificação ASTC, quirks de tile-based, limites da Mali) | a Mali-G57 de verdade |
| relógio do guest | `VITASHARP_CPU_MHZ=333` | 333 MHz |
| núcleos do host | dois (`VITASHARP_PHONE_AFFINITY`, padrão CPUs n-4 e n-2; `0` = sem fixar) | dois rápidos + seis lentos |
| GC | `DOTNET_GCgen0size=0x400000` (o processo se reinicia quando falta: o GC só lê na partida) | o mesmo, embutido na biblioteca |
| runtime | Native AOT quando roda pelo roteiro (o log diz "runtime with JIT" senão) | Native AOT |

Um valor que já está no ambiente vence o do perfil. O log tem uma linha `Phone profile:` com o que o processo recebeu
de verdade (núcleos, gen0, runtime, se a pasta de dados é sensível a maiúsculas), e as notas de uma captura F8 têm o
mesmo como `phone-profile:` (`off` sem ele).

O que isso não reproduz: o código do backend ARM64 (o PC roda o backend x64), os núcleos mais lentos do celular (um
núcleo do PC roda o mesmo código do guest várias vezes mais rápido; os seis núcleos lentos, onde vão as threads
auxiliares, também não existem), os timers do Linux e os clocks térmicos do celular. A pasta de um jogo ligada (junction)
numa pasta de dados de teste mantém a sensibilidade a maiúsculas da pasta para onde aponta.
