<!-- pair: captures.md sha256=e524d01580430e8442f786942aa601049c7ceec16319432a287a213c22facb35 -->
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
firmware e dos plugins), então duas gravações da mesma versão têm os mesmos hashes.

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
indicada (`--data=<pasta>`). `--boot=title` sobe o jogo instalado e carrega o estado nele.

O `hle_calls.tsv` tem uma linha por chamada HLE da gravação (tempo do guest, thread, função, biblioteca, r0-r3,
resultado, endereço de retorno); o `hle_errors.txt` soma por função, com os retornos de erro.

Os arquivos que o jogo leu durante a gravação voltam sobre as montagens dele na reexecução, no tamanho que tinham e,
quando a gravação guardou, com os bytes lidos, então ela lê os mesmos bytes sem o jogo; o resumo da reexecução (linhas
`files:`) diz se ela leu algo que a gravação não leu e quais leituras não pôde repor (guardadas só com hash: a
reexecução lê zeros ali). O `report.txt` lista o que foi lido e se os bytes saíram junto com a gravação.

O que ainda precisa do jogo: os bytes de uma gravação feita sem `--capture-files-mb`, módulos do firmware
que o jogo carrega pela primeira vez durante a gravação (vêm do firmware de quem analisa), e gravações do shell do
sistema com aplicativos abertos (vários processos; subir esses a partir do estado ainda não é suportado).

Detalhes para desenvolvedores: [`docs/notes/session-capture.md`](notes/session-capture.md).
