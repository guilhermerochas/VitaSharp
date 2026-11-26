<!-- pair: user-guide.md sha256=c3934feb3f83cf759824c1a41c12fa1ad7a1a7c2eaf15c629e604dc6e43ba077 -->
# Guia do usuário

[English](user-guide.md) | **Português**

Os detalhes por trás do [README](../README.pt-BR.md): todas as opções, onde fica cada arquivo e o app Android. Comece
pelo [Começo rápido](../README.pt-BR.md#começo-rápido) do README; venha aqui quando precisar de mais.

**Conteúdo:** [Jogos e onde ficam](#jogos-e-onde-ficam) · [Firmware](#firmware) ·
[Sistema do Vita](#sistema-do-vita) · [Idioma](#idioma) · [Gráficos e carregamento](#gráficos-e-carregamento) ·
[Modelo do console](#modelo-do-console) · [Microfone](#microfone) · [Localização](#localização) ·
[Teclado e mouse Bluetooth](#teclado-e-mouse-bluetooth) · [Ad hoc (multijogador local)](#ad-hoc-multijogador-local) ·
[Plugins do kernel](#plugins-do-kernel) · [Android](#android) · [Atualizações](#atualizações)

## Jogos e onde ficam

**Instalar.** Em **Instalar pkg / vpk** (ou arrastando os arquivos para qualquer tela):

- **VPK ou zip**;
- **PKG com licença**: o pacote junto com a licença, um `work.bin`/`.rif` ou o texto zRIF colado;
- **Pasta descriptografada**: uma pasta de jogo já descriptografada.

**Onde fica cada coisa.** Tudo fica onde o console guarda (jogos em `ux0:app`, atualizações em `ux0:patch`, DLC em
`ux0:addcont`, jogos de PSP/PSone em `ux0:pspemu/PSP/GAME`, jogos de PSM em `ux0:psm`, saves em
`ux0:user/00/savedata`, licenças em `ux0:license/app/<TITLEID>`). As licenças que as versões anteriores guardavam em
`ux0:license/<TITLEID>` vão sozinhas para `ux0:license/app/<TITLEID>` na inicialização seguinte (uma licença cujo novo
lugar já tem outra diferente fica onde estava). Uma atualização instalada vale como no console: o jogo vê os
arquivos dela por cima dos seus (`ux0:patch/<TITLEID>`, ou `grw0:patch/<TITLEID>` para um cartão), a versão dele passa
a ser a da atualização (a Biblioteca também mostra) e os apps do próprio firmware não recebem nenhuma. Um jogo de PSM
feito em Unity guarda o próprio runtime em `ux0:psm/<TITLEID>/runtime`, com a licença desse runtime em
`ux0:license/app/<TITLEID>`; um que uma versão anterior deixou em `RO/runtime` vai sozinho para lá na inicialização
seguinte.

No computador, o `ux0:` é o `fs/ux0` dentro da pasta de dados (veja
[Onde ficam os seus dados](../README.pt-BR.md#onde-ficam-os-seus-dados) no README); `--data=<pasta>` ou
`VITASHARP_DATA` trocam a pasta.

**PlayStation Mobile (PSM).** Um `.pkg` de jogo de PSM se instala como um do PS Vita, com a licença da sua própria
conta: o `.rif` de PSM de 1024 bytes (ou o zRIF dele) que o seu console tem para o jogo; licença de PS Vita ou de
outro jogo é recusada. Ele vai para onde o console põe, `ux0:psm/<TITLEID>` (`RO` com o jogo e `RO/License`,
`RW/Documents` com os saves, que uma reinstalação mantém); o jogo continua protegido como foi vendido e quem o abre é
o runtime do PSM, com a sua licença. Também precisa do **PSM Runtime Package** (`PCSI00011`, um `.pkg` normal de PS
Vita com a licença dele). A Biblioteca mostra um jogo de PSM instalado com o nome e o ícone do próprio jogo, e a tela
inicial do **Sistema do Vita** mostra a bolha dele depois que o sistema reconstrói a sua base (na inicialização seguinte
à instalação, como no console). Rodar jogos de PSM ainda está em andamento (veja o [roadmap](roadmap.md)); um jogo de
PSM instalado por uma versão anterior precisa ser instalado de novo.

## Firmware

Os jogos rodam sem firmware. Instale-o para ter as fontes e bibliotecas do sistema (`libSceXml`, `libSceSqlite`...) e
o **Sistema do Vita**: na página **Sistema do Vita**, **Instalar firmware (.PUP)…**, ou **Usar firmware do Vita3K**
(copia o `os0`/`vs0`/`sa0`/`pd0` de uma instalação do Vita3K). Pela linha de comando: `install-firmware <PSVUPDAT.PUP>`.
**Desinstalar firmware** (na mesma página; `uninstall-firmware [--yes]`) remove só o que a instalação criou
(`os0`/`vs0`/`sa0`/`pd0` e os arquivos de índice `firmware.json`, `firmware-libraries.txt`): os jogos, os saves e o
`ux0`/`ur0` ficam, e uma partição ligada a outra pasta perde só o link.

**Fontes do sistema (pacote de fontes).** A atualização do sistema (`PSVUPDAT.PUP`) não traz o `sa0`: no console as
fontes do sistema (`sa0:data/font/pgf` e `pvf`) vêm de fábrica, aqui vêm do pacote de fontes do firmware (outro .PUP,
instalado pelos mesmos botões **Instalar firmware (.PUP)…** / **Instalar pacote de fontes (.PUP)...**, ou
`install-firmware <.PUP do pacote de fontes>`) ou do `sa0` do Vita3K (copiado sozinho depois de instalar a atualização,
quando o Vita3K o tem). Sem elas o Sistema do Vita e vários jogos ficam sem texto, por isso o launcher (desktop e
Android) não abre o Sistema do Vita antes de elas estarem instaladas e diz o que falta; o `font_package` do
`firmware.json` e a página do firmware mostram se as duas pastas de fontes estão lá.

**Núcleo de compatibilidade PSP (em andamento).** O emulador de PSP do firmware dá boot num núcleo MIPS a partir do
`os0:kd/pcbc.skprx`, cujo conteúdo só o processador seguro do console decifra. Para rodá-lo, tire do seu próprio
console o código de boot decifrado (os 0x5CE0 bytes que o SceCompat carrega na janela + 0x600000) e coloque em
`os0:kd/pcbc.bin` na pasta de dados (`%APPDATA%\VitaSharp\fs\os0\kd\pcbc.bin` no Windows, o mesmo `fs/os0/kd` dentro
da pasta de dados no Linux e no macOS). O VitaSharp nunca o distribui. Situação e limites: as
[notas de compatibilidade PSP](notes/psp-compat.pt-BR.md) e o [roadmap](roadmap.md) (item do núcleo de compatibilidade
PSP).

## Sistema do Vita

Com o firmware instalado, o botão **Abrir Sistema do Vita** do launcher (Ctrl+H, ou `boot-shell`) roda o `shell.self`
do firmware: configuração inicial na primeira vez, tela de bloqueio, tela inicial com os jogos instalados e LiveArea.
Um jogo aberto pela LiveArea roda como um segundo processo; o botão PS (tecla Home) volta ao sistema e **Continuar**
retoma o jogo. Detalhes e o que falta em [`docs/notes/system-apps.md`](notes/system-apps.md).

## Idioma

O launcher e a janela do jogo seguem o idioma do sistema (inglês ou português do Brasil);
**Configurações > Geral > Aparência > Idioma** escolhe um (no Android: **Ajustes > Aparência > Idioma**). Os logs
continuam em inglês. O idioma que um jogo vê é o idioma do sistema do Vita; para um jogo sem o seu (ex.: um lançamento
asiático só com japonês e inglês, que cai no japonês), o ajuste **Idioma do sistema** dele (na página do jogo no
launcher, nos detalhes do jogo no Android; `--system-language=<0-19>` na linha de comando) faz só esse jogo ver outro.

## Gráficos e carregamento

**Resolução interna padrão.** A resolução interna com que abre todo jogo sem escolha própria é um ajuste geral: no
launcher do desktop **Configurações > Gráficos > Resolução interna** (1x-4x), no Android **Ajustes > Resolução
interna** (50%-4x). A escolha do próprio jogo na página dele (inclusive a nativa) vence; a página mostra
**Padrão (...)** com o valor geral. A tela de carregamento mostra a resolução com que o jogo abre, e o log da sessão
tem a linha `Internal resolution: ...`. `F1` / `Shift+F1` mudam a resolução na sessão atual.

**Janela de carregamento.** O clique num jogo abre a janela dele na hora (960x544 vezes o `--scale`), antes de o
processo do jogo fazer qualquer parte da inicialização, com a imagem de abertura do jogo (`sce_sys/pic0.png`), o ícone
e o nome, a etapa atual (firmware, controles, áudio, GPU, o jogo, o cache de shaders, o código ARM) com uma barra e,
durante as esperas do cache de shaders e do código ARM, "Cancelar". A janela do próprio jogo espera escondida no
mesmo lugar (`--loading-screen=launcher --window-pos=X,Y`) e aparece quando o jogo está pronto; as etapas são as
linhas `[boot-step] <etapa>` do log do processo do jogo e `[boot-log] <caminho>` o arquivo de log dele. Um segundo
clique no jogo traz a mesma janela de volta; o X dela para o jogo. Uma inicialização que falha (título que não
carrega, licença ausente, saída com erro) deixa o motivo e o lugar do log na janela, com "Fechar". O Android mostra a
mesma tela sobre o jogo. Nada a mais para instalar, em todos os sistemas.

**Carregar o cache de shaders antes de jogar** (launcher: **Configurações > Gráficos**, ao lado de "Compilar shaders
em segundo plano", e a página de cada jogo; `--load-shader-cache=on|off`, ligado por padrão; o nome antigo
`--precompile-shaders` ainda é aceito, com um aviso): antes de o jogo começar, o cache de shaders dele (os shaders e
pipelines gravados nas sessões anteriores, `shadercache/titles/<ID>/`) é montado atrás de uma barra de progresso na
barra de status do launcher; "Cancelar" (a linha `cancel-shader-cache-load` na entrada padrão do processo do jogo)
começa o jogo na hora e o resto é montado em segundo plano. É um carregamento de cache, não uma pré-compilação: a
primeira sessão de um jogo não tem nada gravado. Nada a mais para instalar, igual no Windows, Linux e macOS.

**Pré-compilar código ARM** (launcher: **Configurações > Gráficos**, depois da opção do cache de shaders, e a página
de cada jogo; `--precompile-code=on|off`, ligado por padrão): antes de o jogo começar, o JIT nativo traduz o código
ARM dele em todos os núcleos livres, atrás de uma barra de progresso própria na barra de status do launcher
("Cancelar" = a linha `cancel-code-precompile` na entrada padrão do processo do jogo; linhas de log
`[code-precompile] progress ...`): os blocos que as sessões anteriores executaram, na ordem em que rodaram pela
primeira vez (o cache de código: `codecache/<hash da imagem do módulo>.vsjc` na pasta de dados,
`--code-cache=<pasta>|off`; um arquivo por imagem de módulo, vale em qualquer endereço de carga, rejeitado depois de
uma mudança no tradutor), ou, na primeira sessão, o código que a análise estática alcança a partir das entradas dos
módulos (início do módulo, exportações, branches diretos e retornos de chamada). O jogo começa assim que esses ficam
prontos; depois, algumas threads de prioridade baixa traduzem o código perto do que o jogo executa (alguns blocos
além de cada bloco que ele alcança, e o resto da função dele, achada pelo índice de funções `.ARM.exidx`), no máximo
64 MB da arena de código. O código pré-compilado é publicado quando o jogo chega nele pela primeira vez. As linhas de
log `ARM code precompilation: boot step ...` e, no fim, `... % of the run blocks were precompiled` dão o tempo e a
cobertura. O Android espera a mesma etapa atrás da tela de carregamento (a opção fica nos ajustes do app e nos de
cada jogo). Nada a mais para instalar, em todos os sistemas.

Mais opções de shader para desenvolvedores (compilar ao carregar o programa, workers de shader, bibliotecas de
pipeline): [`docs/development.pt-BR.md`](development.pt-BR.md#opções-de-desenvolvedor).

## Modelo do console

**Configurações > Emulação > Modelo** (ou `--model=pstv`, `VITASHARP_MODEL=pstv`) faz o aparelho emulado ser um
PlayStation TV em vez do PS Vita portátil (PCH-1000, o padrão): os jogos veem um PS TV (vários controles suportados),
cada controle conectado é um jogador nas portas 1-4 (o teclado joga junto com o primeiro controle) e a vibração chega
aos controles. O software de sistema próprio do PS TV, os ajustes de HDMI e o USB host ainda não são emulados.

## Microfone

O microfone do Vita (chat de voz, apps que gravam) ouve o dispositivo de captura do PC pelo OpenAL Soft, a mesma
biblioteca que toca o som (WASAPI no Windows, PipeWire/PulseAudio/ALSA no Linux, Core Audio no macOS; nada a
instalar). **Configurações > Áudio > Microfone** escolhe o padrão do sistema (o padrão), um dispositivo de captura,
desligado (silêncio) ou um tom de teste de 440 Hz; na linha de comando `--microphone=<nome|índice|none|tone[:HZ]>`, e
`mic-test` lista os dispositivos e mostra o nível que um jogo receberia. O dispositivo só liga enquanto um jogo ou app
está com a porta do microfone aberta; sem dispositivo (ou, no macOS, sem a permissão de microfone para o terminal ou
app que roda o VitaSharp: **Ajustes do Sistema > Privacidade e Segurança > Microfone**) o jogo ouve silêncio, como numa
sala quieta. Nada do que o microfone capta é gravado; uma captura **Gravar tudo** guarda só o formato e o ritmo dele,
a não ser que `--capture-mic-audio` (ou `VITASHARP_CAPTURE_MIC_AUDIO=1`) peça o som também.

## Localização

Apps e jogos que perguntam ao Vita onde ele está não recebem posição por padrão: o Vita responde que o aplicativo não
tem permissão, como quando o jogador recusa. **Configurações > Áudio > Localização** (ou
`--location=<latitude,longitude[,altitude[,precisão]]>`, graus decimais com ponto, metros) dá ao Vita uma posição
fixa, respondida com essa precisão (10 m quando não informada); vazio ou `none` desliga. O serviço de localização do
próprio PC ainda não é lido. A posição nunca vai para o log; uma captura **Gravar tudo** a omite (o Vita responde
"posição indeterminada" enquanto a captura está armada ou gravando, e o `session.txt` guarda `--location=<location>`),
a não ser com `--capture-location` (ou `VITASHARP_CAPTURE_LOCATION=1`).

## Teclado e mouse Bluetooth

**Configurações > Emulação > Teclado e mouse Bluetooth** (ou `--bt-keyboard-mouse=on`) pareia o teclado e o mouse do
PC com o Vita, para jogos e apps que leem teclado ou mouse (SceHid: o navegador, chat). Desligado por padrão; as teclas
continuam valendo como controle com ele ligado. As teclas de mídia do teclado (reproduzir/pausar, faixas, volume)
também chegam ao Vita como seus controles de consumidor, no Windows, Linux (X11), macOS e Android.

## Ad hoc (multijogador local)

**Configurações > Emulação > Ad hoc pela rede local** (ou `--adhoc-lan=on`; Android: Ajustes) põe o Wi-Fi ad hoc do
Vita na rede local: outras instâncias do VitaSharp com a opção ligada, na mesma rede ou no mesmo PC, se acham sem
servidor. O firewall precisa liberar a entrada da porta UDP 30860 na rede local (o Windows pergunta na primeira vez).
Onde o multicast não passa (VPN, alguns roteadores), `--adhoc-peer=host[:porta],...` alcança os outros PCs pelo
endereço. Desligado por padrão. Hoje: os grupos ad hoc do PSP (`pspnet_adhoc`: criar/entrar pelo diálogo de checagem
de rede, lista de pares, datagramas PDP); PTP, Matching e o modo ad hoc do próprio Vita vêm depois.

## Plugins do kernel

Uma opção do emulador, **Ativar Plugins do Kernel**, como pausar ou gravar a tela (o Vita não tem essa configuração).
**Desligada por padrão: roda o firmware puro e nenhum plugin carrega.** Ligada, o VitaSharp carrega os plugins que a
configuração lista e oferece a eles as bibliotecas do framework de plugins que eles importam (informação de módulo,
busca de exports e imports, injeção de dados, hooks de função), como um console com o ambiente homebrew instalado.

- **Como ligar:** launcher **Configurações > Avançado**; Android **Configurações > Depuração**; linha de comando
  `--kernel-plugins=on|off`; ambiente `VITASHARP_KERNEL_PLUGINS=1` (as chaves valem sobre a variável).
- **Pasta:** `ur0:kernel_plugins/` (no computador `fs/ur0/kernel_plugins/`, igual no Windows, Linux, macOS e Android),
  ao lado de `ur0:tai`, guarda o `config.txt`. As linhas dele são caminhos do Vita, como no Vita
  (`ur0:tai/kubridge.skprx`): os plugins ficam em `fs/ur0/tai` (ou `fs/ux0/tai`), onde os homebrews conferem os
  arquivos. Uma linha sem dispositivo (`meu.skprx`) é um arquivo que você coloca em `ur0:kernel_plugins/`. Seções como
  no Vita: `*KERNEL` para os plugins de kernel, `*ALL` / `*TITLEID` para os de usuário, `*main` é a tela inicial.
- **Alternativa:** sem `ur0:kernel_plugins/config.txt`, é lido o `fs/ux0/tai/config.txt` do Vita (senão
  `fs/ur0/tai/config.txt`), como copiado de um Vita.
- **Mudança automática:** na primeira vez com a opção ligada, o `config.txt` do Vita vai para `ur0:kernel_plugins/`
  sem mudanças (uma cópia fica como `ur0:kernel_plugins/config.txt.bak`); os plugins, o arquivo do próprio framework e
  os arquivos de dados dos plugins ficam em `ur0:tai`. O arquivo do próprio framework nunca é carregado: o emulador
  oferece as bibliotecas dele. A pasta das versões anteriores (`kernel_plugins/` ao lado de `fs/` na pasta de dados)
  vai do mesmo jeito para `ur0:kernel_plugins/`, e a pasta antiga vazia é apagada.
- **Log:** uma linha por plugin, `kernel plugin <caminho>: loaded and started (<nome>) (at system boot)`, `... not
  started: no module provides ...` ou `... not loaded (...)`. Os plugins de `*KERNEL` carregam no boot do sistema, antes
  do lançamento do título, mesmo quando esse lançamento falha.

Detalhes em [`docs/notes/plugins.pt-BR.md`](notes/plugins.pt-BR.md).

## Android

O app Android (`src/VitaSharp.Android`) é um app .NET MAUI que roda o mesmo núcleo do emulador do desktop em celulares
arm64: o APK Release é compilado com Native AOT por padrão, o código ARMv7 do Vita roda no JIT nativo ARM64 e a imagem é
desenhada com Vulkan. Tem telas próprias: biblioteca de jogos, detalhes do jogo (backend de CPU, clock, perfil de GPU,
resolução interna, MSAA, taxa de quadros, VSync e contador de FPS por jogo, saves), troféus, controles, ajustes,
firmware, driver da GPU, conta PSN e rede, e uma página de desempenho. O app segue o idioma escolhido (**Ajustes >
Aparência > Idioma**; os nomes de menu abaixo são os em português). Telas e design:
[`docs/android/README.md`](android/README.md#screens).

**Requisitos.** Um celular arm64 com Android 8.0 ou mais novo (API 26) e driver Vulkan 1.1 ou mais novo.

**Estado (medido num Redmi Note 14, Helio G99, Mali-G57).** A versão mobile chegou a uma certa estabilidade; o
desempenho ainda é o principal trabalho em andamento. Medido hoje:

| Jogo | Onde foi medido | fps (nativo) |
| --- | --- | --- |
| Killzone: Mercenary | campanha | cerca de 22-25 (30) |
| Uncharted: Golden Abyss | o Prólogo | 30 (30) |
| F1 2011 | a corrida | 30 (30) |

Jogos mais leves chegam aos seus 30 ou 60 fps nativos; os outros jogos ainda não foram medidos no celular. Celulares
Adreno foram pouco testados.

**Instalar o APK.** Baixe o `VitaSharp-<versão>-android-arm64.apk` da página de **Releases** (toda versão e toda
prerelease automática `build-<commit>` o traz). Toda execução do CI também gera um para celulares arm64: na página da
execução em **Actions**, o artefato **VitaSharp-android-arm64-packages** traz o
`VitaSharp-ci-<execução>-android-arm64.apk`. O APK é o build Release (Native AOT, com o compilador de shaders do
aparelho e o libadrenotools). Instale com `adb install` ou abrindo-o no celular.

**Assinatura e atualizações.** Os APKs das Releases e do CI são assinados com a chave fixa do projeto, então cada um
instala por cima do anterior e o celular mantém os dados. Um build de um fork sem essa chave, ou feito em outro PC, é
assinado com outra chave: o Android recusa instalá-lo por cima do outro até ele ser desinstalado. Com "Acesso a todos
os arquivos" os dados ficam em `/sdcard/.vitasharp` e sobrevivem à desinstalação; sem ele os dados ficam na pasta do
próprio app e a desinstalação os apaga, então copie o `fs/ux0/user/00/savedata` para fora antes. Detalhes:
[Signing](android/README.md#signing).

**Compilar num PC:** o SDK do .NET 10 com o workload MAUI Android (`dotnet workload install maui-android`), um JDK
17, o Android SDK (plataforma 36) e o NDK (r30: `sdkmanager "ndk;30.0.16248370"`), e então
`build/build-android-apk.ps1` / `.sh`, o mesmo script do CI (compila as bibliotecas nativas na primeira vez, depois o
APK em `artifacts/android/packages`, e o confere). O `ANDROID_KEYSTORE` assina com o seu próprio keystore fixo. Passos
e opções: [`docs/android/README.md`](android/README.md#building).

```sh
build/build-android-apk.sh          # Windows: build/build-android-apk.ps1
adb install -r artifacts/android/packages/VitaSharp-dev-android-arm64.apk
```

**Jogos, licenças, firmware e saves.** Cada arquivo que você instala entra pelo seletor de arquivos do Android
(Storage Access Framework) e é lido no lugar. Em **Biblioteca**, **Instalar jogo** aceita um `.vpk` (ou `.zip`) ou um
`.pkg` com a licença (`work.bin`, `.rif` ou o zRIF colado). O firmware é opcional, como no PC: **Ajustes > Firmware >
Instalar firmware (.PUP)** com o `PSVUPDAT.PUP` (e o pacote de fontes); depois **Sistema do Vita** na biblioteca
inicia a tela inicial de verdade. Drivers de GPU próprios (Adreno, pela libadrenotools) são instalados em **Ajustes >
Driver da GPU**.

**Pasta de dados e permissão.** Tudo fica numa pasta de dados com a organização do desktop (jogos em `fs/ux0/app`,
licenças em `fs/ux0/license/app`, saves em `fs/ux0/user/00/savedata`, `config/env.txt`...). Sem root ela é a
**`.vitasharp`** na raiz do armazenamento do celular (`/sdcard/.vitasharp`), visível e editável em qualquer
gerenciador de arquivos e mantida se o app for desinstalado; para isso o app pede **"Acesso a todos os arquivos"** na
primeira abertura e move a antiga `Android/data/io.github.vitasharp/files` para lá (conferida arquivo por arquivo,
retomada se for interrompida). Enquanto a permissão não é dada, e num celular com root, os dados ficam em
`Android/data/io.github.vitasharp/files`. Depois de uma reinstalação o Android tira a permissão: o app acha os dados
na `.vitasharp` e pede de novo em vez de abrir vazio ([detalhes](android/README.md#storage)).

**Controles.** Controles translúcidos na tela, sobre as laterais da imagem (direcional, analógicos, botões de ação,
L/R, SELECT/PS/START, um botão TRÁS para o touchpad traseiro); toques na imagem são a tela de toque frontal. Controles
Bluetooth ou USB são detectados sozinhos com o layout posicional do desktop (os controles na tela somem enquanto um está
conectado), e um teclado físico usa as teclas do desktop. **Controles** remapeia os botões de cada controle (toque
no controle da lista e aperte o botão no controle; **Restaurar padrão** volta ao padrão) e ajusta a troca O/X, zona
morta, opacidade, vibração (a cada toque, também com o outro dedo no analógico; fraca, média ou forte) e esconder
sozinho. O giroscópio e o acelerômetro do celular alimentam o sensor de movimento do Vita, e o magnetômetro, a bússola
do Vita (quando o jogo liga o magnetômetro).

**Sistema, câmeras, microfone e localização.** **Ajustes > Sistema > Ao abrir o emulador** pode dar boot no Sistema do
Vita (o shell do firmware) ao abrir o app, como no desktop. **Ajustes > Câmeras** escolhe o que cada câmera do Vita
mostra, como as opções de câmera do desktop: a câmera do celular daquele lado (o padrão), uma imagem PNG/JPEG ou
nenhuma (quadros cinza). **Ajustes > Microfone** escolhe o que o microfone do Vita ouve: o microfone do celular (o
padrão; o do fone conectado, quando houver), desligado (silêncio) ou o tom de teste. O Android pede a permissão do
microfone na primeira vez que um jogo abre o microfone, nunca antes; recusada, o jogo ouve silêncio. **Ajustes >
Localização** dá ao Vita a posição do celular (desligada por padrão): o Android pede a permissão de localização na
primeira vez que um jogo pergunta onde o Vita está, nunca antes; recusada, a posição fica indeterminada.

**Menu de pausa, save states e Gravar tudo.** O botão PS (na tela ou no controle) pausa um jogo iniciado sozinho:
**Continuar**, **Salvar estado** / **Carregar estado** (6 slots com imagem), **Ajustes** (toque traseiro, controles na
tela, MSAA, taxa de quadros, pré-gravação), **Recentralizar** (movimento), **Gravar tudo** e **Sair do jogo**. **Gravar
tudo** grava o mesmo relatório de bug do F8 do desktop (save state, entrada, GPU dump, quadros, áudio e log detalhado) em
`captures/` até **Parar gravação**; **Marcar o problema** marca o momento. Envie o `.zip` como em
[Relatar um bug](../README.pt-BR.md#relatar-um-bug) no README.

**Estados e gravações na página do jogo.** Os detalhes do jogo mostram os 6 slots com a imagem e a data; tocar num
slot abre o jogo já carregando aquele estado (como o `--load-state` do desktop; com o jogo aberto, carrega nele). Logo
abaixo ficam as gravações do **Gravar tudo** do jogo, com data e tamanho; tocar numa compartilha o `.zip`. Pelo adb:
`am start ... --es titleId <ID> --ei loadSlot <1-6>`.

**Ajustes por jogo.** Nos detalhes do jogo: resolução interna (50%, 75%, nativa, 2x, 3x ou 4x, como no desktop), MSAA,
taxa de quadros (**Nativo**, o ritmo do próprio jogo, por padrão; **Fixo em 30**, **Fixo em 60** ou **Sem limite**,
como o `--frame-rate` do desktop; também no menu de pausa, vale na hora, e aparece ao lado do contador de FPS), VSync,
o contador de FPS, backend de CPU, clock, perfil de GPU e o idioma do sistema que o jogo vê (**Idioma do sistema**),
salvos por jogo (**Voltar ao padrão de Ajustes** volta aos ajustes gerais). A resolução padrão geral está em
[Gráficos e carregamento](#gráficos-e-carregamento).

**Limitações conhecidas.**

- Os vídeos do Killzone: Mercenary podem ficar lentos por um tempo até se recuperarem.
- O Minecraft perde desempenho com o tempo de sessão (em investigação).
- GPUs Adreno (e drivers próprios) foram pouco testadas; as medidas acima são de uma Mali-G57.

Os itens abertos estão no [roadmap](roadmap.md) (seção Android).

## Atualizações

- **Versões**: uma tag `v*` publica uma release com os pacotes Native AOT de Windows, Linux e macOS, o APK do Android
  (`VitaSharp-<versão>-android-arm64.apk`) e o `SHA256SUMS.txt`.
- **Builds automáticos**: todo push na `main` cujo CI passou vira uma prerelease `build-<commit>` com os pacotes
  daquele CI (as 10 mais novas ficam).
- **Atualização automática**: os pacotes procuram uma versão nova ao abrir (Configurações > Geral > Atualizações:
  liga/desliga, canal builds automáticos ou só versões, **Procurar agora**). O diálogo mostra a versão em uso, a nova,
  a data e as novidades, com **Pular esta versão**, **Lembrar depois** e **Atualizar agora**: o pacote da plataforma
  é baixado, conferido pelo `SHA256SUMS.txt` e trocado (o instalador, no Windows instalado pelo setup), e o VitaSharp
  reabre. Só os arquivos do emulador mudam; a pasta de dados nunca. Um build compilado do código se atualiza pelo git
  (pela linha de comando: `update [--channel=releases] [--apply]`).
- Os pacotes do desktop não são assinados (o Windows SmartScreen e o Gatekeeper do macOS avisam) e nunca incluem
  firmware.
