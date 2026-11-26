<!-- pair: captures.md sha256=9134911046b3859b20c7589bf957b72802e0647c4ac7cc7b716792ed4ee4b84c -->
# Analisar uma gravação do Gravar tudo sem o jogo

[English](captures.md) | **Português**

Uma gravação do **Gravar tudo** (F8 no PC, menu de pausa no Android) é feita para ser enviada: alguém publica o
`.zip` no GitHub e quem corrige o problema trabalha só com ele, num computador que não tem o jogo, nem a atualização
dele, nem os plugins de quem gravou.

## O que sai junto com a gravação

Nada pessoal. Todo arquivo de texto da gravação (`session.txt`, `log.txt`, `report.txt`, `context.txt`, as listas de
threads, `hle_calls.tsv`, `audio/files.txt`) e o cabeçalho do dump da GPU passam por uma última etapa que troca:

| O quê | Marcador |
|---|---|
| a pasta de dados do VitaSharp | `<data>` |
| a pasta do usuário e o nome do usuário | `<user>` |
| o nome do computador | `<pc>` |
| o online id da PSN / RPCN e as opções de conta (`--psn-id`, `--rpcn-user`, `--rpcn-token`) | `<psn-id>` |
| endereços MAC | `<mac>` |
| endereços de e-mail | `<email>` |

Nenhuma licença, chave, número de série ou senha é gravada. Nenhum arquivo do jogo ou de plugin é copiado: o estado
salvo guarda a memória do guest como o jogo estava, e `files/` lista o que o jogo leu das montagens dele (app0,
addcont0) durante a gravação, só por caminho, posição, tamanho e hash. Os bytes só vão junto quando a gravação é
feita com `--capture-files-mb=<MB>` (ou `VITASHARP_CAPTURE_FILES_MB=<MB>`), cada conteúdo distinto uma vez até esse
tamanho, e o `report.txt` então avisa que a gravação leva dados do jogo, para ela ser compartilhada só onde isso pode
ir. Os dados salvos (savedata0) são do jogador: deles ficam só os hashes, a não ser que a gravação seja feita com
`--capture-savedata` (ou `VITASHARP_CAPTURE_SAVEDATA=1`); a reexecução lê o save da própria pasta de dados.
O microfone é o que o jogador e quem está perto dele dizem: o `audio.txt` traz só o formato, a fonte e o ritmo da porta
do microfone, e o som dele só entra (`audio/NN_microphone_RATE.wav`) com `--capture-mic-audio` (ou
`VITASHARP_CAPTURE_MIC_AUDIO=1`).
A posição é onde o jogador está: enquanto a captura está armada ou gravando, o serviço de localização do Vita
responde "posição indeterminada" (nenhuma posição entra na memória do guest que a captura guarda) e o `session.txt`
guarda `--location=<location>`; só `--capture-location` (ou `VITASHARP_CAPTURE_LOCATION=1`) deixa responder e guarda o
valor da opção.

## O que a gravação diz sobre o ambiente

O `session.txt` identifica o título e a versão sem o jogo: `title-id`, `app-guest-path` (`ux0:app/<id>`),
`app-version` (o APP_VER que o sistema lê; com uma atualização instalada, também o do aplicativo), `category`,
`content-id`, a versão do `firmware`, `kernel-plugins` ligado ou não, o idioma do sistema, as opções do emulador em
vigor (`options:` e toda variável `VITASHARP_*` definida, `env:`), o build, o renderizador e a GPU, e todo módulo
carregado (linhas `module:`: nome, caminho no guest, endereço e hash da imagem, do executável do jogo, dos módulos do
firmware e dos plugins), então duas gravações da mesma versão têm os mesmos hashes. A linha `touch:` conta o caminho
do toque desde o início do emulador: os eventos de toque brutos do host por tipo (down, pointer down/up, move, up,
cancel), os pontos históricos e o máximo de dedos ao mesmo tempo, e por painel as leituras, os buffers entregues, as
amostras do driver (uma por vblank do histórico de toque do host, mais a da própria leitura), as amostras com contato,
os contatos novos e um hash de todos os relatórios (id e posição no painel): contagens e um hash, sem posições. O
Android também a grava no `last.log` a cada 30 s (`[perf] touch:`), com o tamanho da view e onde a imagem fica
(`Touch: view ...`).

## Analisar

| Análise | Comando | Precisa de |
|---|---|---|
| Relatório (quadros, travadas, áudio, achados) | `vitasharp-perf capture <pasta ou zip>` | nada |
| GPU (o que foi desenhado) | `VitaSharp.Desktop replay-gpu <pasta>/gpu.vsgd --out=<dir>` | um dispositivo Vulkan (qualquer um; SwiftShader / lavapipe servem) |
| CPU, HLE, áudio, lógica (o jogo roda de novo) | `VitaSharp.Desktop rerun <pasta>` | um dispositivo Vulkan; sem jogo, sem plugins, sem firmware |
| O lado do sistema | ler `hle_calls.tsv` e `hle_errors.txt` | nada |

O `rerun` sobe o programa a partir do próprio estado salvo da gravação: todo módulo que o processo e o kernel tinham
carregado (o jogo, os módulos do firmware, os plugins) é reconstruído da memória do estado; depois a entrada gravada
é reproduzida e a reexecução é gravada e comparada com a original (`reruns/<...>/compare.txt`: as imagens e a
primeira chamada HLE diferente). Um caminho escrito na gravação nunca é aberto: a reexecução usa a pasta de dados
indicada (`--data=<pasta>`). `--boot=title` sobe o jogo instalado e carrega o estado nele. `--from=preroll` (o
padrão com pré-gravação: um estado no máximo 5-10 s antes dela), `--from=start`, ou `--from=markN`: o estado do momento
em que o jogador marcou o problema (Shift+F8 durante a gravação, ou "Marcar o problema" no menu de pausa do Android;
`state_mark_N.vss`, listado no `session.txt` e no relatório).

O `hle_calls.tsv` tem uma linha por chamada HLE da gravação (tempo do guest, thread, função, biblioteca, r0-r3,
resultado, endereço de retorno); o `hle_errors.txt` soma por função, com os retornos de erro.

Os arquivos que o jogo leu durante a gravação voltam sobre as montagens dele na reexecução, no tamanho que tinham e,
quando a gravação guardou, com os bytes lidos, então ela lê os mesmos bytes sem o jogo; o resumo da reexecução (linhas
`files:`) diz se ela leu algo que a gravação não leu e quais leituras não pôde repor (guardadas só com hash: a
reexecução lê zeros ali). O `report.txt` lista o que foi lido e se os bytes saíram junto com a gravação.

O que ainda precisa do jogo: os bytes de uma gravação feita sem `--capture-files-mb`, módulos do firmware
que o jogo carrega pela primeira vez durante a gravação (vêm do firmware de quem analisa), e gravações do shell do
sistema com aplicativos abertos (vários processos; subir esses a partir do estado ainda não é suportado).

## Conferir uma correção

```
VitaSharp.Desktop verify <pasta ou zip da gravação> [--out=<dir>] [--rerun] [opções do replay]
```

Diz se o problema que a gravação registrou ainda está lá com o build que roda o comando, sem o jogo e sem ninguém
jogar. Ele reproduz o dump da GPU da gravação com este build (`replay-gpu --findings`, num processo próprio), aplica
as regras de imagem do relatório aos quadros reproduzidos (os mesmos quadros que uma gravação vê, até 480x272:
quadros pretos, `[flash]` da imagem inteira ou de uma célula da grade 4x4, imagens fora de ordem) e a varredura de
NaN/Inf dos alvos de ponto flutuante, e compara cada achado da gravação dentro do dump com eles:

```
  [flash] present 1587 cell 1,1: ainda presente (replay: present 1587 cell 1,1: ...)
  [black-flash] present 1600: corrigido (replay: nada nos presents 1599-1601)
  por tipo (gravação -> replay):
    flash: 20 -> 0 (corrigido)
resultado: corrigido
```

*corrigido*: todo achado da gravação sumiu e nada novo apareceu; *ainda presente*: um achado está no mesmo present
(um a mais ou a menos) e na mesma célula; *mudou*: ele se deslocou, ou o replay mostra um achado que a gravação não
tinha (*novo*). O relatório sai no idioma do emulador (`VITASHARP_LANGUAGE`, o do sistema por padrão). Achados antes do dump (a pré-gravação) ou depois dele aparecem como não verificados. Código de saída
0 corrigido, 1 ainda presente, 2 mudou, 3 nada a verificar, 4 o replay falhou. Tudo vai para `--out` (padrão
`<gravação>/verify`: `verify.txt`, o `log.txt` e o `findings.txt` do replay); a gravação não é alterada. Opções do
replay (`--res-scale=2`, `--env=CHAVE=VALOR`, `--msaa=...`) vão para o replay; um dump gravado numa escala de
resolução leva os alvos de renderização nessa escala, então o replay na mesma escala começa exatamente dos pixels que
o jogador viu.

O replay executa os comandos gravados: um achado que continua nele é desenhado pelo renderizador a partir desses
comandos, ou já está neles (a causa vem antes da GPU). `--rerun` também roda de novo a lógica do jogo a partir do
estado da gravação (`rerun`) e compara os dois relatórios tipo por tipo (travadas, ordem de apresentação, buracos e
estalos do áudio); uma reexecução não é determinística, então só as contagens são comparadas.

### Qual draw

Para cada pisca que continua (no máximo três), o `verify` aponta o draw responsável, sem bisseção manual: mais um
replay com a sonda de draws (`replay-gpu <dump> --probe=PRESENT:CÉLULA`, que também pode ser usado sozinho) copia a
região do achado na superfície mostrada naquele present e nos dois vizinhos antes e depois de cada draw nela, com a
profundidade e o estêncil sob ela antes do draw. Os draws do quadro do pisca são alinhados com os do vizinho (mesmos
programas, na ordem) e o primeiro que difere é apontado, com os programas (hashes vs/fs), o estado e o motivo:

```
  draw responsável por [flash] present 1587 cell 1,1:
    draw responsável: draw 41823 vs=... fs=... rt=0x66600000 depth=0/True st=7/7 blend=none ... t0=0x81200000:0C000000
      por quê: não escreveu na região no quadro do pisca (no vizinho: 412 px); depth 0/True (vizinho 7/True), profundidade antes 0.2500 (vizinho 1.0000)
```

Os motivos: *falta no quadro do pisca* (o vizinho tem o draw, o quadro do pisca não), *só no quadro do pisca*, *não
escreveu na região* (o mesmo draw não escreveu nada ali: rejeitado pelo teste de profundidade, estêncil ou alfa,
recortado ou deslocado; os campos do estado e a profundidade/estêncil sob ele que diferem são listados), *estado
diferente*. Quando nenhum draw na superfície mostrada difere, a causa é uma superfície desenhada antes e amostrada,
ou qual buffer foi apresentado. Os arquivos: `probe_P/probe_P.txt` (cada draw sondado: present, número do draw,
alvo, programas, pixels da região que mudou, profundidade e estêncil antes, estado) e
`probe_P/attribution_P.txt`.

### Qual interruptor (A/B)

O `verify --ab` reproduz o dump mais uma vez para cada interruptor do renderizador que muda como a imagem é
desenhada (cada um num processo próprio, até dois presents depois do último achado) e compara os achados com os do
replay sem eles: *o achado some* (o interruptor aponta a classe do bug), *muda* ou *igual*. Os interruptores
(`--ab=NOME,...` escolhe alguns; `--ab-env=CHAVE=VALOR` acrescenta qualquer outro, repetível):

| Nome | Variável | O que muda |
|---|---|---|
| `depth-seed-off` | `VITASHARP_DEPTH_SEED=0` | profundidade/estêncil carregados começam indefinidos em vez da imagem anterior da superfície |
| `msaa-off` / `msaa-ss` / `msaa-native` | `VITASHARP_MSAA` | cenas MSAA com uma amostra, como superamostragem 2x, multiamostradas |
| `store-writeback-off` | `VITASHARP_GPU_STORE_WRITEBACK=0` | o que os shaders gravam fica no espelho do renderizador |
| `scene-breaks-strict` | `VITASHARP_SCENE_BREAKS=strict` | todo envio ou cópia dentro de uma cena termina a renderização |
| `fetch-pass-merge-*`, `fetch-large-*`, `fetch-pass-join-*`, `depth-feedback-*` | `VITASHARP_FETCH_PASS_MERGE`, `_FETCH_LARGE`, `_FETCH_PASS_JOIN`, `_DEPTH_FEEDBACK` (0 e 1) | os passes de fetch do blending programável e da leitura de profundidade |
| `depth-ops-none-off` | `VITASHARP_DEPTH_OPS_NONE=0` | profundidade/estêncil sem uso continuam carregados e gravados |
| `gpu-profile-mali` | `VITASHARP_GPU_PROFILE=mali` | os caminhos do renderizador para GPUs tile-based (celular) |
| `gpu-poison` | `VITASHARP_GPU_POISON=1` | conteúdo indefinido ganha valores gritantes: a leitura de algo nunca desenhado aparece |
| `res-scale-1` | `VITASHARP_RES_SCALE=1` | resolução nativa |

```
A/B dos interruptores do renderizador (18; o replay de cada um contra o replay sem eles):
  depth-seed-off (VITASHARP_DEPTH_SEED=0: ...): igual
  msaa-off (VITASHARP_MSAA=off: cenas MSAA desenhadas com uma amostra): o achado some
  ...
  o achado some com: msaa-off (a classe do bug: cenas MSAA desenhadas com uma amostra)
```

Os replays ficam em `<out>/ab/<nome>/`.

Detalhes para desenvolvedores: [`docs/notes/session-capture.md`](notes/session-capture.md).
