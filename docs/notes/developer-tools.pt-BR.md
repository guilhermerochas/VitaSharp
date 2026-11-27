<!-- pair: developer-tools.md sha256=6067e7104aefa0afe4605954a3a05e3401af9aeb8732036ef1ca021222325bed -->
# Versão de desenvolvedor (`-p:VitaSharpDeveloper=true`)

*English: [developer-tools.md](developer-tools.md).*

Ferramentas para o trabalho de desempenho que itera em segundos: sem reconstruir o APK e sem iniciar um jogo a cada experimento.

```
dotnet publish src/VitaSharp.Android -c Release -r android-arm64 -p:VitaSharpDeveloper=true [-p:VitaSharpAndroidRuntime=mono-aot] -m:2
dotnet build tools/VitaSharp.PerfTools -c Release -p:VitaSharpDeveloper=true -m:2     # vitasharp-perf bench, ...
```

- A propriedade define `VITASHARP_DEVELOPER` em todos os projetos (`Directory.Build.props`). Otimização, modo AOT e
  trimming são os do build Release normal.
- As ferramentas ficam em pastas `Developer/` (namespaces `*.Developer`) e atrás de ganchos `#if VITASHARP_DEVELOPER`.
  Um build normal não tem nada disso: o `DeveloperGatingTests` confere os assemblies, e
  `build/check-dev-gating.{ps1,sh} APK|PASTA` confere um pacote (`--expect-dev` / `-ExpectDev` para a checagem positiva
  num APK de desenvolvedor).
- Nada roda até um bench ou trace ser iniciado. Os ganchos testam chaves estáticas que começam desligadas.
- O APK de desenvolvedor se chama **VitaSharp Dev** (nome no launcher e "VitaSharp Dev" no cabeçalho do log). Ele mantém
  o id de pacote `io.github.vitasharp`, então `adb install -r` preserva os dados do usuário.
- Fixtures contêm dados de jogos, então nunca vão para o git. No celular ficam em
  `<data>/bench/` (`<data>`: `/sdcard/.vitasharp`, ou `/sdcard/Android/data/io.github.vitasharp/files` num celular com
  root ou sem "Acesso a todos os arquivos"; a pasta em uso é o que `adb shell "$(cat build/android-data-root.txt)"`
  imprime, docs/android/README.md, "Storage"); no PC, em qualquer pasta com o mesmo layout:

```
bench/fixtures/NOME.vsgd           fixtures de GPU (vitasharp-perf bench cut)
bench/variants/NOME/               uma variante: shaders substitutos + params.txt (estado de render), sem rebuild
bench/results/RUN.tsv              uma linha por repetição medida (fixture, variante, config, rep, ms do frame, ms da cena)
bench/shadercache/                 o cache de shaders do próprio bench
```

## 1. Bench da GPU

**Fixtures.** Uma fixture é um dump de GPU (`VITASHARP_GPU_DUMP_AT`, `build/android-gpu-dump.*`) cortado depois do seu
frame alvo. Os frames anteriores ao alvo são o estado dela: memória do guest, definições de shaders e os render targets
que o dump carrega. O bench reproduz esse estado uma vez e depois reproduz o frame alvo N vezes, com um par de
timestamps em volta de cada cena. Uma cena é um render pass, e o tráfego de tiles da Mali é por pass.

```
vitasharp-perf bench cut DUMP.vsgd                        # lista os frames (cenas, draws, offset no arquivo)
vitasharp-perf bench cut DUMP.vsgd --frame 3 --name uc_title --root bench
```

**Draws isolados.** `draw=K` (o serial, a partir de 1, do draw no frame alvo; a execução imprime o intervalo de draws de
cada cena) funciona de dois modos:
- `drawmode=without` alterna o frame inteiro com o frame sem o draw K. A diferença é o custo marginal do draw em
  contexto.
- `drawmode=only` alterna a cena de K contendo só o draw K com a cena sem draws. A diferença é o draw sozinho, sem
  load/store e clear.

Timestamps em volta de draws isolados dentro de um pass não significam nada num tiler, então os draws são medidos por
diferença.

**Variantes** (`bench/variants/NOME/`, tudo lido em tempo de execução):
- Shaders: `HASH_HINT.frag` / `.vert` (GLSL), ou o mesmo nome + `.spv` (SPIR-V), substitui o shader traduzido que tem
  aquele hash de programa e chave de hint. `HASH.frag` substitui o shader para todos os hints do programa. A interface do
  tradutor (bindings, saídas) continua a mesma. `vitasharp-perf bench shaders --root bench --fixture NOME` grava o GLSL
  traduzido da fixture em `variants/_translated/NOME/`, pronto para copiar e editar.
- `params.txt`: linhas `chave=valor`, as mesmas chaves dos parâmetros do bench:
  - `depthload=clear|dontcare`: load op de profundidade dos renderings que não carregam profundidade.
  - `depthstore=store|dontcare`: store op de profundidade dos renderings em tiles.
  - `barrier=full`: uma barreira de memória completa depois de cada rendering em tiles.
  - `msaa=...`: `VITASHARP_MSAA`.
  - `fetchmerge=0|1`: `VITASHARP_FETCH_PASS_MERGE`, a fusão dos passes de fetch.
  - `fetchsplit`, `msaafetch`, `scenebreaks`, `gpuquirks`.
  - `gpuprofile=mali|...`.
  - `stamps=1`: um timestamp de GPU antes de cada início de pass e de cada cópia do frame medido; o log recebe uma
    linha `STAMP fixture variante sN #k rótulo: ms` por intervalo (mediana das repetições), com o rótulo sendo a
    operação que o inicia (`start` = do início da cena até a primeira operação), para dividir o tempo de uma cena entre
    seus passes e cópias.
  - `env.VITASHARP_X=...`: qualquer outra chave do renderizador.

  O renderizador lê suas chaves de ambiente uma vez por processo, então cada execução é um processo próprio. O
  renderizador não tem ajuste de escala de resolução; `msaa` escolhe os caminhos de render 1x/2x.

**Desktop:**
```
vitasharp-perf bench run --root bench --fixtures uc_title,f1_race --variants base,v1 --rounds 4 --reps 3 [--param gpuprofile=mali] [--scenes 3]
```
- `bench run` inicia um processo filho por (fixture, variante, rodada). As variantes são intercaladas A/B, B/A. Cada
  filho roda com afinidade 0x0F em prioridade abaixo do normal.
- `bench table results/RUN.tsv --base base` imprime a tabela de novo.

**Celular** (APK de desenvolvedor instalado):
```
build/android-bench.sh uc_title,f1_race base,v1 [ROUNDS=4] [REPS=3]      # BENCH_DIR=bench, PARAM='k=v;...'
build/android-bench.ps1 -Fixtures uc_title -Variants base,v1 -Rounds 4 -Reps 3
```
- O script envia as fixtures (só quando o tamanho mudou) e as variantes. Cada execução é
  `am start -n io.github.vitasharp/io.github.vitasharp.BenchActivity --es bench 'fixture=..;variant=..;reps=..;run=..'`:
  um processo `:bench` que termina quando a execução acaba.
- O script baixa `results/RUN.tsv` e imprime a tabela.
- O mesmo extra na activity do launcher (`--es bench ...`) encaminha para o bench.
- As linhas BENCH também vão para o logcat e para `logs/bench.log`.
- No próprio celular: Ajustes > Depuração > **Opções de desenvolvedor** lista as fixtures, roda uma contra todas as
  variantes (3 rodadas intercaladas) e mostra a tabela.

**Lendo a tabela:**
- `GPU ms` é a mediana de todas as repetições medidas de todas as rodadas.
- `spread` é máximo - mínimo.
- `delta` é contra a variante base (a primeira). Os benches de draw comparam contra a própria configuração de
  referência.
- Só decida quando o delta for claramente maior que o spread das duas linhas.
- O tempo de uma cena vai do fim do trabalho anterior até o seu próprio fim: timestamps de bottom-of-pipe nas duas
  pontas.

## 2. Trace do Perfetto no celular

```
build/android-trace.sh PCSE00002 10 40 [INPUT]       # 10 s gravados depois de 40 s de jogo; OUT=android-trace
build/android-trace.ps1 PCSE00002 -Seconds 10 -Delay 40
```

O script grava `VITASHARP_TRACE=1` (e `VITASHARP_TRACE_HLE=N`) em `files/config/env.txt`, inicia o jogo e grava
`tools/VitaSharp.PerfTools/perfetto-vitasharp.pbtx` com `perfetto --txt`. Depois baixa `TITLE.perfetto-trace` e
`last.log`, remove `env.txt` e o trace do celular e força a parada do app.

O trace registra:
- Escalonamento de CPU (`sched_switch`/`sched_waking`), frequência e idle da CPU, frequência da GPU
  (`power/gpu_frequency`) e memória da GPU.
- Contadores e estágios de render da GPU, quando o driver os expõe. O produtor `gpu.counters` da Mali depende do
  fabricante: a trilha simplesmente não existe quando o driver não tem.
- Os marcadores do app. Eles só são compilados no APK de desenvolvedor, e cada um custa um teste de static readonly
  quando o trace está desligado (`Core/Util/Developer/DevTrace.cs`, ATrace via `libandroid.so`):

| Marcador | Thread | Significado |
|---|---|---|
| `guest frame` (contador) + `guest flip` | a thread de display do guest | um `sceDisplaySetFrameBuf`: a taxa de frames do guest é a inclinação do contador |
| `gpu record frame` | worker da GPU | do primeiro comando depois de um present até o present: o custo de CPU de gravar um frame |
| `gpu commands/frame` (contador) | worker da GPU | comandos naquele frame |
| `vkQueueSubmit` | worker da GPU | a própria chamada de submit (tempo de CPU do driver) |
| `hle NOME` | threads do guest | uma chamada HLE em `VITASHARP_TRACE_HLE` (padrão 64), então uma função quente aparece em proporção ao número de chamadas |
| `jit compile` | threads do guest | a compilação de um bloco ARM64 nativo |
| `audio grain` | thread de saída de áudio | uma escrita da saída mixada no AAudio/AudioTrack |

### Lendo um trace (ui.perfetto.dev)

- **É a CPU ou a GPU?** Fixe o worker da GPU, a thread principal do guest e a trilha `gpu_frequency`.
  - A GPU é o limite quando as fatias `gpu record frame` são curtas mas o worker espera (dormindo na espera da fence
    entre os frames), com `gpu_frequency` no máximo.
  - A CPU é o limite quando o worker ou a thread principal fica Running de ponta a ponta na taxa de frames.
- **Núcleo grande ou pequeno?** Colora por CPU: as fatias da trilha `sched` mostram em qual núcleo cada thread rodou. No
  Redmi Note 14 (2x A76 + 6x A55), os A76 são as duas CPUs com a maior `cpu_frequency`. Uma thread quente num A55 é
  problema de escalonamento (`VITASHARP_ANDROID_AFFINITY`), não de código.
- **Quedas de frequência.** As trilhas `cpu_frequency` das CPUs 6/7 e `gpu_frequency`: um dente de serra é térmico ou
  DVFS. Uma decisão de bench não deve se apoiar numa execução com throttling térmico.
- **Pontos quentes de HLE.** Use Query (SQL):
  `select name, count(*) * 64 as calls, sum(dur)/1e6 * 64 as est_ms from slice where name like 'hle %' group by name order by est_ms desc limit 20`
  (64 = `VITASHARP_TRACE_HLE`).
- **Engasgos.** Um intervalo longo entre `guest flip` que coincide com uma rajada de `jit compile` ou um pico de
  `vkQueueSubmit` aponta a causa.
- **Áudio.** As fatias `audio grain` devem ter espaçamento uniforme. Um intervalo maior que o buffer do dispositivo é um
  underrun, e um estalo.

## Fixtures de elementos, checagem de imagem e modo flicker

- **Selecionando elementos.** `bench cut DUMP --frame I --name N --select "scenes=0,3;draws=A-B;vs=HEX|HEX;fs=HEX"`
  grava uma seleção na fixture. Só esses draws do frame alvo rodam, então o tempo é o daquele elemento. As mesmas chaves
  funcionam como parâmetros de execução.
  - As cenas não selecionadas ainda começam e terminam: o tráfego de load/store delas continua no frame.
  - `fragment=discard` troca todo fragment shader por um discard, o que deixa o custo do lado do vértice da seleção.
    Compare com a execução normal para separar o custo de vértice do de fragmento.
- **Checagem de imagem.** Toda execução salva a imagem do frame alvo (`results/pictures/FIXTURE_VARIANTE.png`). A
  coluna `picture` da tabela diz `same` ou `DIFFERS (diferença média por canal)` contra a variante base. Uma variante que
  muda a imagem não é uma otimização válida, qualquer que seja o tempo: `depthload=dontcare` é o exemplo, 32% mais
  rápida no título do Uncharted só porque sombreia os fragmentos errados.
- **Modo flicker.** `mode=flicker;loops=3;speed=real|max` reproduz em loop todos os frames de um dump inteiro, lê de
  volta cada frame apresentado em tamanho pequeno e roda o detector de flicker/frame escuro (`FlickerDetector`, a regra
  do `vitasharp-perf flicker` na imagem inteira e numa grade 4x4).
  - Os frames sinalizados são salvos com seus vizinhos em `results/RUN_flicker/`.
  - `skipscene=K` e `drawlimit=N` fazem bisseção.
  - No próprio jogo (APK de desenvolvedor), `VITASHARP_FLICKER_CHECK=1` no `env.txt` roda a mesma checagem em cada frame
    apresentado e escreve linhas FLICKER no log. Os cenários de teste do desktop a rodam por padrão
    (`--flicker-check`).

| Fixture | De | Mede |
|---|---|---|
| `uc_title` | Uncharted, título (phone3 `uc3.vsgd`, frame 3) | as 8 cenas do título: buffer de iluminação s0 (draws 1-217) e a cena de fetch-pass s3 (220-264) |
| `uc3` (dump inteiro) | Uncharted, título, 301 frames | modo flicker (um frame escuro do filme no frame 277 está no próprio dump) |
| `f1_race` | F1 2011, largada (dump do phone2, frame 1) | 8 cenas, ~1.630 draws |

As fixtures ficam fora do git. Elas são cortadas de novo dos dumps temporários com os comandos acima.

## Gravar tudo: a captura de sessão (todo build, desktop e Android)

Uma gravação que basta para diagnosticar um bug offline; detalhes, formatos, tamanhos e custo em
[`session-capture.md`](session-capture.md).

- **Desktop:** F8 inicia e para, Shift+F8 marca o problema: a única tecla de depuração do jogador (a gravação tem o
  log detalhado e o dump da GPU; não há F9, F10 nem Ctrl+F8). Por script: `--capture-at=A:B` (frames do guest) ou
  `VITASHARP_CAPTURE_AT`; os passos `F10` / `Ctrl+F8` de um roteiro `--input` ainda fazem uma captura de diagnóstico /
  um dump da GPU sozinho. Pré-gravação: `--capture-preroll[=S]`, `VITASHARP_CAPTURE_PREROLL=S` ou a "Pré-gravação" do
  launcher.
- **Android:** menu de pausa > **Gravar tudo** / **Parar gravação**, e **Pré-gravação: ligada/desligada**; ou
  `VITASHARP_CAPTURE_PREROLL=S` no `env.txt`. Saída em `files/captures/TITLE_DATA_tudo/` e `.zip`.
- Uma captura contém um save state no início (`state_start.vss`; com a pré-gravação também `state_preroll.vss`), a
  entrada (`input.tsv`), o dump de GPU (`gpu.vsgd`), todo frame apresentado com seu tempo de present/flip/vblank
  (`frames.vsfr`, `timeline.tsv`), as portas de áudio, as vozes NGS e sua mixagem, o log, os erros de HLE e os estados
  das threads.

```
vitasharp-perf capture <pasta|zip> [--frames <dir>] [--compare <original>]   # report.txt + findings/*.png
VitaSharp.Desktop rerun <pasta> [--from=preroll|start] [--window] [--rerun-gpu-dump]
build/android-capture-analyze.sh [NOME_DA_CAPTURA|PASTA]       # baixa do celular, roda vitasharp-perf capture
```

- O analisador encontra frames e telas pretas, flashes de um frame, flips fora de ordem / repetidos / pulados, imagens
  trocadas, imagens congeladas e travadas. Ele posiciona cada um no dump de GPU (ou na pré-gravação) e ao lado da entrada.
- A re-execução carrega o estado da captura, reproduz a entrada pelo relógio do guest e se grava em
  `<pasta>/reruns/`, com `compare.txt` contra o original.

## Capturas de desenvolvedor pelo menu de pausa (APK de desenvolvedor)

**Gravando, para o jogador:**
1. Num jogo, aperte PS para abrir o menu de pausa e escolha **Gravar tempo** ou **Gravar conteúdo**.
2. Jogue a cena. "● REC" aparece ao lado do contador de fps enquanto grava.
3. Abra o menu de novo e escolha **Parar gravação (desenvolvedor)**. Escreva uma linha sobre o que aconteceu (ou edite
   `scene:` no `info.txt`).

| Opção | Grava |
|---|---|
| `VITASHARP_CAPTURE=tudo@INICIO:FIM` (só por script; o **Gravar tudo** do menu agora é a captura de sessão acima) | <ul><li>Os primeiros 5 s são só de tempo: fps, as linhas PERF do renderizador e os marcadores do trace, sem dump atrasando o jogo. Vão para o `info.txt` como a "parte de tempo".</li><li>Depois um dump de GPU de até 600 frames: comandos, shaders e toda faixa de memória do guest que a GPU leu, ou seja, as texturas como enviadas.</li><li>As portas de áudio do guest como WAVs (`audio/`), desde o início.</li><li>`log.txt` e `info.txt`.</li></ul> |
| **Gravar tempo** | só a parte de tempo, durante toda a gravação |
| **Gravar conteúdo** | só o dump e o áudio |

- Saída: `files/captures/TITLE_DATA_TIPO/`.
- Por script: `VITASHARP_CAPTURE=tudo@INICIO:FIM` (segundos depois do início do jogo) no `env.txt`.
- Ainda fora de uma captura:
  - O áudio de saída do lado do dispositivo (as portas do guest são gravadas).
  - Um bench do caminho de texturas (tempos de detile/convert/upload). Os dados estão no dump.
  - simpleperf, que não roda de dentro do app. Use `adb shell simpleperf record --app io.github.vitasharp` num build
    `<profileable>` (docs/android/README.md, Phone run 2).

**Analisando uma captura:**
```
build/android-capture-analyze.sh [NOME_DA_CAPTURA|PASTA_LOCAL]      # sem argumento: a captura mais nova do celular
build/android-capture-analyze.ps1 [-Capture NOME|PASTA] [-Desktop VitaSharp.Desktop.exe]
```
O script baixa a captura para `./captures/` e imprime:
- `info.txt`: a parte de tempo e as linhas PERF na parada;
- as janelas de fps e as linhas de captura do log, e as linhas de HLE/threads;
- `vitasharp-perf audio-check` sobre os WAVs das portas;
- a checagem de flicker em todos os frames do dump, reproduzido no PC;
- os ms de GPU por cena do frame do meio (GPU do PC, então custos relativos);
- com `DESKTOP` / `-Desktop`, uma reprodução com a validação do Vulkan e validação de sincronização.

Para os ms de GPU do celular, corte fixtures do dump (`bench cut`, seleções de elementos) e rode `build/android-bench.*`.

## Micro benchmarks (tests/VitaSharp.Benchmarks)

Benchmarks BenchmarkDotNet dos caminhos quentes, para medir uma otimização isolada antes e depois. O projeto não está no
`VitaSharp.sln`: o build normal, o CI e a suíte de testes nunca o compilam nem o executam. Toda execução relata alocações
(MemoryDiagnoser) e o código do JIT de cada benchmark e do que ele chama (DisassemblyDiagnoser, dois níveis, em
`<nome>.artifacts/results/*-asm.md`).

```
build/bench.ps1 -Filter '*Uniform*' -Name before          # build/bench.sh --filter '*Uniform*' --name before
build/bench.ps1 -Filter '*Uniform*' -Name after -Compare <out>/before.md
build/bench.ps1 -Filter '*GpuWorker*' -Dump kz_game.vsgd -Variants 'VITASHARP_VK_DIRECT=1'
build/bench.ps1 -Filter list                              # os benchmarks
```

- Resultados: `<out>/<nome>.md` (out padrão: `<temp>/vitasharp-bench`), a tabela GitHub Markdown unida, com o commit na
  primeira linha; `-Compare` imprime uma tabela de razões contra um resultado anterior.
- `-Job short` (padrão) é uma execução rápida; `-Job default` para números que valem anotar. Os processos dos benchmarks
  rodam fixados em `-Affinity` (padrão 15: quatro CPUs lógicas); inicie o script em prioridade abaixo do normal numa
  máquina compartilhada.
- Benchmarks: `GpuWorkerBenchmarks` (o Execute do renderizador sobre um dump de GPU inteiro na GPU do host, caches
  quentes; `-Variants 'K=V,K2=V2|K3=V3'` adiciona variantes A/B de chaves no mesmo build), `ShaderTranslationBenchmarks`
  (USSE para GLSL de todos os programas do dump), `UniformArenaBenchmarks` (alugar, cópia de slot, liberar),
  `CpuTranslatorBenchmarks` (decode, IL e compilação x64 nativa de um bloco; `-Corpus` = um corpus de
  `VITASHARP_NATIVE_CORPUS`), `CpuDispatchBenchmarks` (CpuCore.Run de um loop do guest em interp/il/native),
  `HleSyncBenchmarks` (mutex leve sem disputa, mutex e semáforo no kernel), `Atrac9Benchmarks` (decodificação de
  superframe; `-At9` = um arquivo .at9, senão superframes gerados que o decodificador aceita), `TextureBenchmarks`
  (swizzled/tiled para linear e suas versões por texel, decodificação PVRTC 4 bpp).
- Fixtures com dados de jogos (`-Dump`, `-At9`, `-Corpus`) nunca são commitadas.
