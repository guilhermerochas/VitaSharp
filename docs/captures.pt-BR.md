<!-- pair: captures.md sha256=23cb83c2b4ee5bfba119ab790d25c4590a3dc67d281ed4443b26efb6595a6aae -->
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

Nenhuma licença, chave, número de série ou senha é gravada. Nenhum arquivo de plugin é copiado, nem arquivo inteiro
do jogo: o estado salvo guarda a memória do guest como o jogo estava, e `files/` guarda só os bytes que o jogo leu das
montagens dele (app0, addcont0) durante a gravação, cada conteúdo distinto uma vez, até 256 MB (64 MB no Android;
`VITASHARP_CAPTURE_FILES_MB=<MB>` ou `--capture-files-mb=<MB>`; 0 guarda só os caminhos, posições e hashes). É o
que a reexecução precisa; o `report.txt` avisa quando a gravação leva dados do jogo, para ela ser compartilhada só
onde isso pode ir. Os dados salvos (savedata0) são do jogador: deles ficam só os caminhos, posições e hashes das
leituras, a não ser que a gravação seja feita com `--capture-savedata` (ou `VITASHARP_CAPTURE_SAVEDATA=1`); a
reexecução lê o save da própria pasta de dados.

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

Os arquivos que o jogo leu durante a gravação voltam sobre as montagens dele na reexecução, então ela lê os mesmos
bytes sem o jogo; o resumo da reexecução diz se ela leu algo que a gravação não leu (linha `files:`), e o
`report.txt` lista o que foi lido e se tudo saiu junto com a gravação.

O que ainda precisa do jogo: leituras além do limite de `files/` (a reexecução lê zeros ali), módulos do firmware
que o jogo carrega pela primeira vez durante a gravação (vêm do firmware de quem analisa), e gravações do shell do
sistema com aplicativos abertos (vários processos; subir esses a partir do estado ainda não é suportado).

Detalhes para desenvolvedores: [`docs/notes/session-capture.md`](notes/session-capture.md).
