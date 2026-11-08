<!-- pair: README.md sha256=7684d6e6ca2101985c626f9a38f51610c77e5749b298056fdd5849d67a36d31e -->
<div align="center">

[English](README.md) | **Português**

<img src="docs/assets/banner.svg" alt="VitaSharp" width="520">

### Um emulador de PlayStation Vita escrito em C#

Roda jogos comerciais do Vita e o próprio sistema do console, sem núcleo nativo: um JIT ARMv7, um renderizador
Vulkan e mais de 1.100 funções do sistema reimplementadas, tudo em .NET 10.

[![Licença: GPL v3](https://img.shields.io/badge/license-GPLv3-blue.svg)](LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10-512BD4.svg?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![Vulkan 1.3](https://img.shields.io/badge/Vulkan-1.3-AC162C.svg)](https://www.vulkan.org/)
[![Estado: experimental](https://img.shields.io/badge/status-experimental-orange.svg)](docs/compatibility.md)

<br>

<img src="docs/assets/screenshots/shell-home.webp" alt="A tela inicial do PS Vita com os jogos instalados, rodando no VitaSharp" width="760">

<sub><i>A tela inicial de verdade do PS Vita (o <code>shell.self</code> do firmware) rodando no VitaSharp.</i></sub>

</div>

---

## O que é

O VitaSharp emula o PS Vita. Ele executa o código ARMv7 do console (interpretador e JITs nativos para x86-64 e
ARM64), traduz os shaders da GPU do Vita para Vulkan e reimplementa as bibliotecas do sistema que os jogos chamam
(HLE). Com o firmware oficial instalado, ele também roda o software do próprio Vita: tela de bloqueio, tela inicial,
LiveArea e jogos abertos por elas. É um projeto de pesquisa em desenvolvimento ativo, todo em C#.

> [!IMPORTANT]
> O VitaSharp não inclui jogos, firmware nem código da Sony. Use jogos e licenças tirados do seu próprio console e
> baixe a atualização do sistema (`PSVUPDAT.PUP`) no site da PlayStation. Pirataria não é suportada.

<table>
<tr>
<td width="33%"><img src="docs/assets/screenshots/killzone-mercenary.webp" alt="Killzone: Mercenary" width="300"><br><sub><b>Killzone: Mercenary</b>, campanha</sub></td>
<td width="33%"><img src="docs/assets/screenshots/uncharted.webp" alt="Uncharted: Golden Abyss" width="300"><br><sub><b>Uncharted: Golden Abyss</b>, primeira fase</sub></td>
<td width="33%"><img src="docs/assets/screenshots/gravity-rush.webp" alt="Gravity Rush" width="300"><br><sub><b>Gravity Rush</b>, abertura</sub></td>
</tr>
<tr>
<td><img src="docs/assets/screenshots/god-of-war.webp" alt="God of War Collection" width="300"><br><sub><b>God of War Collection</b>, combate</sub></td>
<td><img src="docs/assets/screenshots/persona-4-golden.webp" alt="Persona 4 Golden" width="300"><br><sub><b>Persona 4 Golden</b>, abertura</sub></td>
<td><img src="docs/assets/screenshots/shell-livearea.webp" alt="LiveArea do Uncharted" width="300"><br><sub><b>Sistema do Vita</b>, LiveArea de um jogo</sub></td>
</tr>
</table>

## Jogos

38 jogos testados até agora ([`docs/compatibility.md`](docs/compatibility.md), com o estado e o cenário de teste de
cada um): 25 chegam ao gameplay (entre eles Killzone: Mercenary, Uncharted: Golden Abyss, Gravity Rush, God of War
Collection, FIFA 13, Persona 4 Golden, LittleBigPlanet, Tearaway, WRC 4, Skullgirls e Devious Dungeon, vários que
não abrem no Vita3K) e o resto chega aos menus. Os defeitos conhecidos e o trabalho futuro estão no
[roadmap](docs/roadmap.md).

## Plataformas

| Plataforma | Estado |
| --- | --- |
| **Windows** (x64) | A mais testada: launcher, janela do jogo, Sistema do Vita, todas as ferramentas de diagnóstico |
| **Android** (arm64) | App próprio (biblioteca, ajustes, controles na tela, menu de pausa); testado num Redmi Note 14 (Mali-G57) e no emulador x86_64. Por enquanto o APK é compilado localmente ([`docs/android/README.md`](docs/android/README.md)) |
| Linux, macOS | Compilam e geram pacotes; Linux x64 testado no Debian 12, macOS ainda sem teste num Mac |

Requisitos no PC: um driver Vulkan 1.3 (NVIDIA, AMD ou Intel recentes) e um processador com vários núcleos.

## Instalar e jogar

1. Baixe o pacote da página de **Releases** (zip ou instalador para Windows; veja [Releases](#releases)) ou compile
   (veja [Compilar e testar](#compilar-e-testar)).
2. Abra o VitaSharp: o launcher (biblioteca de jogos) aparece.
3. Em **Instalar pkg / vpk** (ou arrastando os arquivos para qualquer tela):
   - **VPK ou zip**;
   - **PKG com licença**: o pacote junto com a licença, um `work.bin`/`.rif` ou o texto zRIF colado;
   - **Pasta descriptografada**: uma pasta de jogo já descriptografada.
4. Escolha o jogo e aperte **Jogar**.

**Firmware (opcional).** Os jogos rodam sem firmware. Instale-o para ter as fontes e bibliotecas do sistema
(`libSceXml`, `libSceSqlite`...) e o **Sistema do Vita**: na página **Sistema do Vita**, **Instalar firmware
(.PUP)…**, ou **Usar firmware do Vita3K** (copia o `os0`/`vs0`/`sa0`/`pd0` de uma instalação do Vita3K). Pela linha
de comando: `install-firmware <PSVUPDAT.PUP>`.

**Sistema do Vita.** Com o firmware instalado, o botão **Abrir Sistema do Vita** do launcher (Ctrl+H, ou `boot-shell`) roda o
`shell.self` do firmware: configuração inicial na primeira vez, tela de bloqueio, tela inicial com os jogos
instalados e LiveArea. Um jogo aberto pela LiveArea roda como um segundo processo; o botão PS (tecla Home) volta ao
sistema e **Continuar** retoma o jogo. Detalhes e o que falta em [`docs/notes/system-apps.md`](docs/notes/system-apps.md).

**Android.** No app: **Instalar VPK**, o `.PUP` do firmware e pacotes de driver da GPU são escolhidos pelo seletor
de arquivos do Android; o menu de pausa (botão PS) tem os estados salvos e o **Gravar tudo**.

**Idioma.** O launcher e a janela do jogo seguem o idioma do sistema (inglês ou português do Brasil);
**Configurações > Geral > Aparência > Idioma** escolhe um. Os logs continuam em inglês.

Os dados ficam em `%APPDATA%\VitaSharp` no Windows (`~/.local/share/VitaSharp` no Linux,
`~/Library/Application Support/VitaSharp` no macOS; `--data=<pasta>` ou `VITASHARP_DATA` trocam a pasta): jogos em
`fs/ux0/app`, firmware em `fs/os0`... , `logs/`, `savestates/`, `captures/` (Gravar tudo), `gpudumps/`,
`diagnostics/`, `shadercache/`.

## Teclas

| Tecla | Função |
| --- | --- |
| Setas, `W` `A` `S` `D`, `I` `J` `K` `L` | Direcional, analógico esquerdo, analógico direito |
| `X` ou `Espaço` / `C` / `Z` / `V` | Cruz / Círculo / Quadrado / Triângulo |
| `Q` / `E`, `Enter` / `Backspace` | L / R, Start / Select |
| `Home` | Botão PS |
| Botão esquerdo / direito do mouse | Toque na tela frontal / no touchpad traseiro |
| `F1` / `Shift+F1` | Resolução interna seguinte / anterior com o jogo rodando (1x, 2x, 3x, 4x; só nesta sessão, com aviso na tela) |
| `F2` / `Shift+F2` | Taxa de quadros seguinte / anterior com o jogo rodando (nativa, fixa em 30, fixa em 60, sem limite) |
| `F4` | Modo do mouse: giroscópio (botão do meio), analógico direito, analógico esquerdo ou desligado, salvo por jogo (`Esc` solta o cursor) |
| `F5` / `F7` | Salvar / carregar o estado rápido; `F6` escolhe um slot (1-9), `Shift+F5` / `Shift+F7` salvam / carregam nele |
| `F8` | Inicia / para o **Gravar tudo** (ver [Relatar um bug](#relatar-um-bug)) |
| `Ctrl+F8` | Inicia / para um dump da GPU (`gpudumps/*.vsgd`; `replay-gpu` refaz desenho por desenho, sem o jogo) |
| `F9` | Liga / desliga o modo de depuração (logs detalhados, pré-gravação de 10 s, capturas automáticas) |
| `F10` | Captura de diagnóstico: o próximo quadro com seus desenhos, texturas, alvos, print e log (`diagnostics/`) |
| `F11` | Tela cheia |
| `P` | Pausa / retoma a emulação |

Controles (SDL2) são detectados sozinhos, com layout posicional (o botão de baixo é sempre Cruz) e giroscópio. Tudo
pode ser remapeado em **Configurações > Controles**, por controle ([`docs/notes/controller-config.md`](docs/notes/controller-config.md)).

## Relatar um bug

Use o **Gravar tudo**: aperte **F8** durante o jogo (a barra de título mostra *"● REC | Gravando tudo (F8 para
parar)"*), jogue até o problema aparecer e aperte **F8** de novo. Sai um `.zip` em `captures/` com um estado salvo,
os controles, todas as imagens com seus tempos, o dump da GPU, o áudio (mix e cada voz) e o log: dá para reproduzir o
problema sem jogar de novo. No Android: menu de pausa > **Gravar tudo**. Para defeitos rápidos, ligue a
**Pré-gravação** (Configurações > Geral, ou o modo de depuração com F9): o arquivo inclui os segundos antes da tecla.

Envie o `.zip` com o nome do jogo (title ID) e o que aconteceu. Toda sessão também grava `logs/last.log`. Detalhes
em [`docs/notes/session-capture.md`](docs/notes/session-capture.md); quem analisa usa `vitasharp-perf capture <zip>` e
`VitaSharp.Desktop rerun <pasta>`.

## Compilar e testar

Precisa do [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0). O primeiro build baixa o FFmpeg
(vídeos e MP3/AAC); as outras bibliotecas nativas vêm do NuGet.

```sh
dotnet build -c Release
dotnet run --project src/VitaSharp.Desktop -c Release          # abre o launcher
dotnet run --project src/VitaSharp.Desktop -c Release -- run <TITLEID>   # abre um jogo direto
dotnet test tests/VitaSharp.Tests -c Release --filter "Category!=Slow"  # testes rápidos (sem o fuzz da CPU)
```

A linha de comando tem também `install`, `boot-shell`, `test <cenário>` (cenários automáticos em
[`tests/scenarios`](tests/scenarios)), `replay-gpu`, `rerun`, `imports`, `update` e outras: `VitaSharp.Desktop --help`. O app
Android: [`docs/android/README.md`](docs/android/README.md). Pacotes prontos (zip, instalador, tar.gz, AppImage, .app,
.dmg; ReadyToRun ou Native AOT): `build/publish.ps1` / `build/publish.sh`.

Todo script vem em par `.ps1` + `.sh`, e o CI confere isso. Os textos da interface ficam em
`src/VitaSharp.Core/Localization` (`Texts.resx` em inglês, `Texts.pt-BR.resx`); a documentação externa também vem nos
dois idiomas (`README.md` + `README.pt-BR.md`, conferidos pelo `ExternalDocPairsTests`). Para contribuir com funções do
sistema, veja as especificações em [`docs/hle`](docs/hle/README.md).

## Releases

- **Versões**: uma tag `v*` (ou **Actions > Release > Run workflow**) publica uma release com os pacotes Native AOT de
  Windows, Linux e macOS e o `SHA256SUMS.txt`.
- **Builds automáticos**: todo push na `main` cujo CI passou vira uma prerelease `build-<commit>` com os pacotes
  daquele CI (as 10 mais novas ficam).
- **Atualização automática**: os pacotes procuram uma versão nova ao abrir (Configurações > Geral > Atualizações:
  liga/desliga, canal builds automáticos ou só versões, **Procurar agora**). O diálogo mostra a versão em uso, a nova,
  a data e as novidades, com **Pular esta versão**, **Lembrar depois** e **Atualizar agora**: o pacote da plataforma
  é baixado, conferido pelo `SHA256SUMS.txt` e trocado (o instalador, no Windows instalado pelo setup), e o VitaSharp
  reabre. Só os arquivos do emulador mudam; a pasta de dados nunca. Um build compilado do código se atualiza pelo git
  (pela linha de comando: `update [--channel=releases] [--apply]`).
- Os pacotes não são assinados (o Windows SmartScreen avisa) e nunca incluem firmware.

## Créditos e licença

O VitaSharp se apoia no trabalho da comunidade do Vita: [Vita3K](https://github.com/Vita3K/Vita3K) (referência de
comportamento, NIDs, modelo de registradores USSE), [VitaSDK](https://vitasdk.org/) e vita-headers, a
[wiki de desenvolvimento do PS Vita](https://wiki.henkaku.xyz/), [VGAudio](https://github.com/Thealexbarney/VGAudio)
(decodificador ATRAC9, MIT), [RPCN](https://github.com/RipleyTom/rpcn), [Silk.NET](https://github.com/dotnet/Silk.NET),
[Avalonia](https://avaloniaui.net/) e [FFmpeg](https://ffmpeg.org/) (LGPL, baixado no build).

Licença **GPL v3** ([LICENSE](LICENSE)); componentes de terceiros mantêm as suas. PlayStation e PS Vita são marcas da
Sony Interactive Entertainment; o VitaSharp não tem ligação com a Sony.
