<!-- pair: captures.md sha256=a82e08e94550ce6d2be24d617658e080082f7e43d0c99d1bcc3cddaac9aef3bf -->
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
salvo guarda a memória do guest como o jogo estava, que é o que a reexecução precisa.

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

O que ainda precisa do jogo: arquivos que o jogo lê da pasta dele durante a gravação (a reexecução recebe "não
encontrado" e diverge dali; o `hle_calls.tsv` mostra onde), módulos do firmware que o jogo carrega pela primeira vez
durante a gravação (vêm do firmware de quem analisa), e gravações do shell do sistema com aplicativos abertos
(vários processos; subir esses a partir do estado ainda não é suportado).

Detalhes para desenvolvedores: [`docs/notes/session-capture.md`](notes/session-capture.md).
