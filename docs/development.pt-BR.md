<!-- pair: development.md sha256=76c40dd979d9605e804c81d2b9295a971b2541596ab924b45cb3cf2d510ad5e4 -->
# Desenvolvimento

[English](development.md) | **Português**

Como compilar, testar e mudar o VitaSharp; de volta ao [README](../README.pt-BR.md). Builds para o celular:
[`docs/android/README.md`](android/README.md). Ferramentas de diagnóstico e de desempenho:
[`docs/notes/developer-tools.md`](notes/developer-tools.md).

**Conteúdo:** [Compilar e testar](#compilar-e-testar) · [Antes de um push](#antes-de-um-push) ·
[Estilo de código](#estilo-de-código) · [Opções de desenvolvedor](#opções-de-desenvolvedor) ·
[Requisitos dos testes](#requisitos-dos-testes) · [Releases](#releases)

## Compilar e testar

Precisa do [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0). O primeiro build baixa o FFmpeg (vídeos
e MP3/AAC); as outras bibliotecas nativas vêm do NuGet. No macOS o FFmpeg vem do sistema (`brew install ffmpeg`), e
o Vulkan do MoltenVK (`brew install molten-vk vulkan-loader`).

```sh
dotnet build -c Release
dotnet run --project src/VitaSharp.Desktop -c Release          # abre o launcher
dotnet run --project src/VitaSharp.Desktop -c Release -- run <TITLEID>   # abre um jogo direto
dotnet test tests/VitaSharp.Tests -c Release --filter "Category!=Slow"  # testes rápidos (sem o fuzz da CPU)
build/coverage.ps1                                              # relatório de cobertura dos testes (HTML + resumo; também .sh)
```

A linha de comando tem também `install`, `install-firmware`, `boot-shell`, `test <cenário>` (cenários automáticos em
[`tests/scenarios`](../tests/scenarios)), `replay-gpu`, `rerun`, `verify`, `imports`, `update` e outras:
`VitaSharp.Desktop --help`. Pacotes prontos (zip, instalador, tar.gz, AppImage, .app, .dmg; ReadyToRun ou Native AOT):
`build/publish.ps1` / `build/publish.sh`.

Os textos da interface ficam em `src/VitaSharp.Core/Localization` (`Texts.resx` em inglês, `Texts.pt-BR.resx`); a
documentação externa também vem nos dois idiomas (`README.md` + `README.pt-BR.md`, e todo `docs/*.md` com um
`docs/*.pt-BR.md`, conferidos pelo `ExternalDocPairsTests`: a primeira linha de cada um guarda o SHA-256 do outro, e a
falha mostra as linhas a escrever). Para contribuir com funções do sistema, veja as especificações em
[`docs/hle`](hle/README.md). `build/hle-abi-check.ps1` / `build/hle-abi-check.sh` lista as funções HLE cujos
registradores de argumento diferem dos protótipos do [vita-headers](https://github.com/vitasdk/vita-headers) (precisa
de `git` e rede para o clone, ou de um clone local). `build/hle-status.ps1` / `.sh` gera de novo os selos e os mapas de
progresso do README; `-CompatOnly` / `--compat-only` refaz só o mapa do Compat, a partir de `docs/compat/coverage.json`, sem o
firmware e os jogos.

## Antes de um push

Todo script vem em par `.ps1` + `.sh`, conferido localmente pelo `build/prepush-check`. Antes de um push rode
`build/prepush-check.ps1` (Windows) / `build/prepush-check.sh` (Linux, macOS, Git Bash): sobre exatamente o que está
commitado ele roda a paridade dos scripts, o `--help` dos scripts, o ShellCheck, o estilo do código, um build com 0
avisos e a suíte `Category!=Slow`, que falha quando um teste falha, quando o processo de testes caiu (execução
abortada) ou quando rodaram menos testes do que o `dotnet test --list-tests` lista; só suba depois de
"prepush-check: ok". No Windows ele também roda a suíte no Linux pelo WSL quando o WSL tem um .NET SDK em `~/.dotnet`
(`dotnet-install.sh --channel 10.0 --install-dir ~/.dotnet`) e os [requisitos dos testes](#requisitos-dos-testes) do
Linux (o Debian 12 precisa do Mesa mais novo do bookworm-backports). Nenhuma etapa grava despejo de memória (Linux:
tamanho de core 0 e `coredump_filter` 0, porque o coletor de falhas do WSL ignora o limite de tamanho).

## Estilo de código

O estilo do C# é o do Ryujinx/Ryubing, e o [`.editorconfig`](../.editorconfig) é a regra (o que é cobrado, o que está
desligado e por quê). `build/format-check.ps1` (Windows) / `build/format-check.sh` (Linux, macOS) roda
`dotnet format whitespace` e `dotnet format style --severity warn` com `--verify-no-changes` só nos arquivos `.cs`
versionados (`git ls-files`: nunca arquivos fora do git nem ignorados por ele), como o job "Code style" do CI (esse job
está comentado no `.github/workflows/ci.yml` por enquanto: rode localmente), com o mesmo código de saída (2 quando
algo difere); `-Fix` / `--fix` reescreve os arquivos em vez de conferir (rodando também o `build/NamingTool`, que
renomeia os símbolos fora das regras de nomes junto com todas as referências, já que o `dotnet format` aponta o
IDE1006 mas não sabe corrigi-lo, e o `build/FormatTool`, que quebra os blocos escritos numa linha e mantém
`{ get; set; }` numa linha; a correção precisa dos pacotes NuGet do `build/NamingTool`, baixados na primeira vez). As
pastas ainda não convertidas ficam em [`build/format-pending.txt`](../build/format-pending.txt) e são puladas. A lista
de arquivos vai para o `dotnet format` num arquivo de resposta, e os scripts rodam direto o `dotnet-format.dll` do SDK
ativo (`<sdk>/DotnetTools/dotnet-format/`) para que ele mesmo leia esse arquivo: o `dotnet format` poria todos os
caminhos numa linha de comando, maior do que o Windows aceita quando a árvore inteira é listada (outros layouts de SDK
voltam para o `dotnet format`). O `OneLineBlockTests` (na suíte de testes) recusa blocos escritos numa linha só
(`if (x) { a; }`): todo corpo vai em linhas próprias.

**Opções nos dois apps:** toda opção nova do usuário (ajustes, ajustes por jogo, menu de pausa, controles) entra no
launcher do desktop e no app Android na mesma mudança, com o mesmo nome, padrão e significado (os textos vêm do
`Texts.resx` compartilhado); um teste confere que os dois levam a opção ao mesmo ajuste do emulador
(`AndroidOptionParityTests`).

Os scripts shell seguem o Google Shell Style Guide e precisam passar no ShellCheck (job "ShellCheck" do CI; o job
"Scripts on macOS" também roda o `build/check-scripts-help.sh` no bash 3.2 do sistema). Instale o ShellCheck com
`winget install koalaman.shellcheck` (Windows), `sudo apt install shellcheck` / `sudo dnf install ShellCheck`
(Linux) ou `brew install shellcheck` (macOS) e rode `shellcheck --severity=style build/*.sh build/package/*.sh`.

## Opções de desenvolvedor

- Tempos-limite precisos (`VITASHARP_PRECISE_TIMEOUTS=0` desliga, A/B): uma espera com tempo-limite num objeto do
  kernel (sceKernelWaitSema, WaitEventFlag e os outros argumentos `timeout`, sceKernelDelayThreadCB) termina no prazo
  em microssegundos, como o timer do threadmgr no Vita, e não no próximo milissegundo inteiro do `Monitor.Wait` (um
  tempo-limite de 500 us durava 1,1-1,3 ms). Uma thread do host, "HLE timeouts", dorme num timer de alta resolução e
  acorda quem espera: um `timerfd` no Linux e no Android, um waitable timer de alta resolução no Windows (ele dispara
  em ticks de 0,5 ms, então lá um tempo-limite ainda pode terminar até 0,5 ms depois). Nada gira. O macOS continua
  com o arredondamento em milissegundos: não tem `timerfd`, e o equivalente (um `EVFILT_TIMER` do kqueue com
  `NOTE_USECONDS`, remarcado enquanto a thread espera) ainda não foi escrito. Os sleeps curtos do host
  (`PreciseSleep`) giram só o atraso medido do timer no desktop (dezenas de microssegundos no Linux e no macOS,
  ~0,5 ms no Windows) e nunca no celular; no Linux e no Android as threads que dormem recebem timer slack de 1 us.
- Dump do backend IL: `VITASHARP_IL_DUMP=<pasta>` grava os blocos do jogo compilados como assemblies para ler no ILSpy
  (`dotnet tool install -g ilspycmd`, depois `ilspycmd -il <arquivo>.dll`); `DOTNET_JitDisasm=<método>` mostra o
  código de máquina que o RyuJIT gera. Veja [`docs/notes/il-jit.md`](notes/il-jit.md).
- Validação do Vulkan: `--validation` (`run`, `test`, `replay-gpu`) ou `VITASHARP_VK_VALIDATION=1` (`=sync` inclui a
  validação de sincronização) precisa da camada da Khronos: o [Vulkan SDK](https://vulkan.lunarg.com/sdk/home)
  (Windows, Linux, macOS) ou, no Linux, o pacote da distribuição (`vulkan-validationlayers` /
  `vulkan-validation-layers`); no macOS o SDK também traz o MoltenVK. Captura de quadros:
  [RenderDoc](https://renderdoc.org) (Windows, Linux) e os dumps da GPU (`replay-gpu`,
  [`docs/notes/gpu-dump.md`](notes/gpu-dump.md)) em todos os sistemas.
- Bibliotecas de pipeline gráfico (desligadas por padrão, mais lentas nas medições com caches frios):
  `VITASHARP_PIPELINE_LIBRARY=1` liga pipelines de primeiro uso ligados a partir de bibliotecas de shader e otimizados
  em segundo plano, onde o driver tem `VK_EXT_graphics_pipeline_library` com ligação rápida (NVIDIA/AMD/Intel de
  desktop, Mesa); sem ela (MoltenVK, Dozen, drivers de celular) os pipelines são compilados inteiros de qualquer jeito.
  A linha `[shadercache] session` do log conta o que a thread da GPU compilou; ver
  [`docs/notes/pipeline-skip.md`](notes/pipeline-skip.md).
- Compilar shaders ao carregar o programa (`--compile-on-program-load=on|off`, ligado por padrão;
  `VITASHARP_COMPILE_ON_PROGRAM_LOAD=0|1` tem prioridade, também no Android): quando o jogo cria um programa de vértice
  ou de fragmento (sceGxmShaderPatcherCreate*, normalmente atrás da tela de carregamento), os workers de shader
  traduzem e compilam os shaders com os hints previstos pelo programa e montam o pipeline do programa de vértice para
  o qual ele foi feito, então o primeiro desenho da primeira sessão já os encontra prontos. Um desenho cujo formato de
  textura ou de superfície difere do previsto mas traduz para o mesmo shader usa o mesmo módulo. As superfícies de cor
  que o jogo inicializa e as cenas que ele começa são lembradas, e os programas que dependem da superfície (blending
  programável, saída de cor nativa ou inteira) também são montados para cada uma delas, com os pipelines de fetch pass
  e de integer view. A linha `[shadercache] session` da sessão termina com o que foi enfileirado e usado ("at program
  load: ..."); `replay-gpu --compile-on-program-load=on` anuncia os programas do dump antes do primeiro comando. Nada a
  mais para instalar, em todos os sistemas.
- Workers de shader (`--shader-workers=N` fixa o número, `VITASHARP_SHADER_WORKERS=N` sobrepõe): por padrão o número
  segue o orçamento de CPU, no máximo 90% dos processadores lógicos que o processo pode usar. Enquanto a thread da GPU
  trabalha, dois deles ficam com a thread da CPU emulada e a thread da GPU (8 processadores: 5 workers). Enquanto ela
  espera um shader ou pipeline, ou o jogo espera o carregamento do cache de shaders, o orçamento inteiro compila (8
  processadores: 7): os workers acima da parte fixa são de folga, em prioridade abaixo do normal, e só usam núcleos
  livres (`VITASHARP_SHADER_SPARE_WORKERS=N` define quantos, A/B). No Android rodam menos, nos núcleos LITTLE (2 big +
  6 LITTLE: 2, 4 na espera). A linha de início do renderizador (`[gpu] shaders: ...`) mostra os números. Nada a mais
  para instalar, em todos os sistemas.
- As opções de carregamento para o usuário (janela de carregamento, carga do cache de shaders, pré-compilação do código
  ARM) estão no [guia do usuário](user-guide.pt-BR.md#gráficos-e-carregamento).
- Perfil: `dotnet-trace` / `dotnet-counters` (`dotnet tool install -g dotnet-trace`, todos os sistemas); no Windows
  também o PerfView e o WPR/WPA (Windows Performance Toolkit). Mais em
  [`docs/notes/developer-tools.md`](notes/developer-tools.md).
- Toda variável de ambiente é declarada em `src/VitaSharp.Core/Util/EnvVars.cs`, com o que ela faz.

## Requisitos dos testes

O que o `dotnet test tests/VitaSharp.Tests` precisa em cada sistema; um teste sem o seu requisito é pulado e diz o porquê.

| | Windows | Linux | macOS |
|---|---|---|---|
| SDK | .NET 10 SDK | .NET 10 SDK | .NET 10 SDK |
| Dispositivo Vulkan (testes do renderizador, dos dumps da GPU e dos jogos rodando) | o lavapipe do Mesa (Vulkan 1.4, o mesmo do CI do Linux): descompacte a pasta `x64` de uma versão do [mesa-dist-win](https://github.com/pal1000/mesa-dist-win/releases) (`vulkan_lvp.dll` + `lvp_icd.x86_64.json`) em `%LOCALAPPDATA%\VitaSharpTools\mesa\<versão>\x64`; sem ele, ou quando o carregador do sistema `vulkan-1.dll` não pode usá-lo, o SwiftShader que vem com o Microsoft Edge ou o Google Chrome (os dois achados sozinhos) | o lavapipe de um Mesa mais novo que o 22.3 (o CI tem o 25.2): `sudo apt install mesa-vulkan-drivers libvulkan1` (Fedora: `mesa-vulkan-drivers vulkan-loader`; Debian 12: `sudo apt install -t bookworm-backports mesa-vulkan-drivers`, o 22.3 dele derruba os testes de textura) | o MoltenVK: o Vulkan SDK da LunarG, ou `brew install molten-vk vulkan-loader` |
| shaderc (testes do renderizador e de compilação de shaders; o renderizador também precisa dele) | vem do pacote Silk.NET.Shaderc.Native | vem do pacote (achado em `runtimes/linux-x64` também onde a busca do Silk.NET não acha, como no Ubuntu); quando essa cópia não carrega: `sudo apt install libshaderc-dev` (Fedora, Arch: `shaderc`; carregado como `libshaderc.so.1`) | vem do pacote, senão o `brew install shaderc` do Homebrew |
| Uma janela do desktop (testes do overlay do jogo) | sempre | uma tela X11 ou Wayland; sem tela: `xvfb-run dotnet test tests/VitaSharp.Tests` (`sudo apt install xvfb`) | pulados: o AppKit só cria janelas na thread principal, e os testes rodam em threads de trabalho |
| OCR (`screen-text`, `ScreenTextTests`, o texto da tela no relatório do F8) | o Windows.Media.Ocr do sistema (Windows 10/11, pelo Windows PowerShell 5.1) com um idioma de OCR instalado (Configurações > Hora e idioma > Idioma; japonês para menus em japonês) | `sudo apt install tesseract-ocr` (`tesseract-ocr-jpn` para japonês) | `brew install tesseract` (`tesseract-lang` para japonês) |
| FFmpeg (testes de decodificação de vídeo e áudio) | baixado pelo primeiro build | baixado pelo primeiro build | o do sistema: `brew install ffmpeg` |

- Qualquer sistema: `VK_DRIVER_FILES` (ou `VK_ICD_FILENAMES`) com o manifesto ICD de um SwiftShader ou Mesa baixado é
  usado no lugar.
- O `RendererPipelineLibraryTests` confere os pipelines ligados onde o dispositivo por software tem a extensão com
  ligação rápida (SwiftShader, Mesa lavapipe) e a compilação inteira em todos os outros.
- Firmware: sem ele os testes dos módulos reais do firmware (`libSceJson`, `libSceXml`, `libpaf`, a instalação do
  arquivo de atualização, as fontes do `sa0`; o `dbutil` então roda só o lado HLE) são pulados. `VITASHARP_FIRMWARE_PUP`
  aponta um `PSVUPDAT.PUP`, `VITASHARP_FIRMWARE_DIR` uma pasta `vs0` ou de dados do Vita3K; os testes do compilador do
  Piglet precisam do `libshacccg.suprx` em `ur0:data` da pasta de dados do Vita3K ou da pasta indicada por
  `VITASHARP_TEST_INSTALLED_DATA`.
- `VITASHARP_TEST_INSTALLED_DATA` (opcional, só leitura): uma pasta de dados do VitaSharp com jogos e firmware
  instalados, para os testes dos módulos reais instalados (`LleLibrariesTests`, o compilador do Piglet); sem ela, eles
  são pulados.
- Os testes do microfone (`MicrophoneTests`) usam um tom roteirizado num relógio que o próprio teste avança: nenhum
  dispositivo de captura, permissão ou placa de som é preciso em nenhum sistema.
- Os testes de localização (`LocationSourceTests`) usam uma posição fixa e uma fonte que o teste move: nenhum
  serviço de localização nem permissão em nenhum sistema.
- Os testes nunca usam a sua pasta de dados: antes de qualquer teste, o assembly de testes aponta o `VITASHARP_DATA`
  para uma pasta temporária nova da execução (também por cima de um valor definido no seu shell), então `input.json`,
  `launcher.json`, logs, cache de shaders e save states são da execução; os testes que precisam de uma pasta própria
  criam uma, e os processos filhos recebem `--data=`. A detecção do Vita3K do launcher não acha nada nos testes. O
  `TestDataIsolationTests` falha se algum teste resolver ou montar o caminho da sua pasta de dados. Os testes de jogo
  rodando iniciam um executável pequeno que os próprios testes escrevem.
- Opcional: as camadas de validação do Vulkan SDK da LunarG rodam as cargas do renderizador sob validação com
  `dotnet test tests/VitaSharp.Tests --filter Category=Slow` (`RendererValidationTests`; pulado sem a camada).
- O teste de janela abre uma janela oculta (`VITASHARP_HIDDEN_WINDOW=1`): no Linux precisa de um display (`DISPLAY`), no
  Windows do carregador Vulkan do sistema (`vulkan-1.dll`, instalado com qualquer driver de GPU), que recebe o driver
  por software em `VK_DRIVER_FILES`. Num processo elevado (Administrador) o carregador ignora essa variável: rode os
  testes sem elevação, ou registre o `*_icd.json` do driver como um valor DWORD `0` em
  `HKLM\SOFTWARE\Khronos\Vulkan\Drivers` (o CI faz isso com o SwiftShader do Edge); senão o teste é pulado. No macOS
  ele é pulado. Um processo elevado também usa o SwiftShader no lugar de um lavapipe não registrado.
- O lavapipe do Windows não tem `VK_EXT_headless_surface`: o teste do renderizador sem janela
  (`RendererDeviceHandoffTests`) é pulado com ele e roda com o SwiftShader (o CI do Windows) e no Linux.
- Paridade de mídia (`MediaParityTests`): o lado do desktop (FFmpeg) roda sempre; o lado do Android precisa do APK de
  desenvolvedor (`-p:VitaSharpDeveloper=true`) num celular ou no Android Emulator (qualquer sistema com o Android SDK:
  uma imagem x86_64 Google APIs e o build `android-x64`). `build/android-media-parity.sh` (ou `.ps1`) grava o traço no
  aparelho; `VITASHARP_ANDROID_MEDIA_TRACE=<traço> dotnet test tests/VitaSharp.Tests --filter MediaParityTests` compara
  com o do desktop (pulado sem a variável). `SYNC=0` (`-Sync 0`) grava com o tempo antigo do MediaCodec.
- Paridade da porta de áudio (`AudioPortParityTests`): os grãos de uma porta, no volume da porta, pelo mixer do OpenAL
  Soft (lido de um dispositivo `ALC_SOFT_loopback`, sem placa de som; o OpenAL Soft do pacote tem isso em todo
  sistema) e pelo fluxo do Android (`BufferedPcmStream`, a fila e o volume antes do AAudio/AudioTrack) têm de dar o
  mesmo PCM, com no máximo um passo de 16 bits de diferença. Pulado com o motivo quando o OpenAL Soft não carrega.
- .NET 11 (opcional, para comparações; o padrão e o CI continuam no .NET 10): `-p:VitaSharpDotnet=net11.0` compila
  todos os projetos para o .NET 11 em qualquer sistema. Precisa do SDK do .NET 11 (e, no Android, do workload
  `maui-android` dele e da plataforma Android API 37; o .NET 11 não tem Mono, então só `VitaSharpAndroidRuntime=coreclr`
  ou `nativeaot` compilam). Com o SDK instalado fora do local padrão, o app do desktop roda com `DOTNET_ROOT`
  apontando para ele.

### Código ARM64 num PC x64 (qemu-user)

Os caminhos ARM64 (o backend nativo ARM64 do JIT, o runtime .NET ARM64 que o celular usa) rodam num PC x64, sem o
celular, sob o qemu-user: `build/qemu-arm64.sh` (Linux) / `build/qemu-arm64.ps1` (Windows, pelo WSL) compila o
projeto de testes para `linux-arm64` e o roda com um test host arm64 sob o `qemu-aarch64`; lá os testes do backend
ARM64 executam o código gerado nativamente (`Arm64HostRunner`) em vez do sandbox Unicorn das execuções x64 (filtro
padrão: `NativeJit`, `Vfp`, `Neon`, `Arm64`; `--filter` / `-Filter` escolhe outros). `--testgame <vpk>` / `-TestGame`
também publica o `VitaSharp.Desktop` para `linux-arm64` (self-contained) e inicia o test game sem janela sob o qemu
(`--seconds`, padrão 300; o log ganha uma linha `# run under qemu-user`). Logs, o `.trx` e o `results.txt` do test
game vão para `--out` / `-Out`. O qemu emula o processador, então os tempos não são os do celular; só os resultados
contam.

| | Windows | Linux | macOS |
|---|---|---|---|
| Requisitos | WSL 2 (Debian/Ubuntu) com um SDK do .NET 10 em `~/.dotnet`; `-Install` faz o resto (como root no WSL, sem senha) | Debian/Ubuntu x64 com SDK do .NET 10; `--install` (sudo): `qemu-user-static binfmt-support` e as bibliotecas `arm64` (`libc6 libstdc++6 libgcc-s1 libicu libssl3 zlib1g`, multiarch), mais o runtime .NET arm64 em `~/.dotnet-arm64` (`DOTNET_ARM64_ROOT`) | não se aplica: não há qemu-user; o Apple silicon roda o `dotnet test` nativamente em ARM64 |

O WSL sem systemd perde a entrada binfmt do `qemu-aarch64` quando a distribuição reinicia: o `.ps1` a registra de novo
a cada execução (`wsl -u root -- update-binfmts --enable qemu-aarch64`). `QEMU_CPU` escolhe o modelo de processador
do qemu (padrão `max`). Os testes rodam na plataforma de testes portátil (`Microsoft.TestPlatform.Portable`, em
`~/.vstest-portable`) iniciada pelo runtime arm64: o `vstest.console.dll` do SDK é compilado para x64, e o x64 não acha
host arm64 no Linux. O script define `DOTNET_EnableWriteXorExecute=0` (o qemu-user roda código velho que o JIT do .NET
escreve pelo segundo mapeamento da sua área de código W^X) e `VITASHARP_NATIVE_COUNTERS=1` (os testes do backend
conferem as contagens de blocos, que o runtime ARM64 do host só mantém com ela). O `/Blame` diz em que teste um crash
parou.

## Releases

- **Versões**: uma tag `v*` (ou **Actions > Release > Run workflow**) publica uma release com os pacotes Native AOT de
  Windows, Linux e macOS, o APK do Android (`VitaSharp-<versão>-android-arm64.apk`) e o `SHA256SUMS.txt`.
- **Builds automáticos**: todo push na `main` cujo CI passou vira uma prerelease `build-<commit>` com os pacotes
  daquele CI (as 10 mais novas ficam).
- A atualização automática que os pacotes fazem está no [guia do usuário](user-guide.pt-BR.md#atualizações).
- Os pacotes do desktop não são assinados (o Windows SmartScreen avisa) e nunca incluem firmware. O APK é assinado
  com o keystore fixo do repositório (secrets `ANDROID_KEYSTORE_BASE64`, `ANDROID_KEYSTORE_PASS`, `ANDROID_KEY_ALIAS`;
  [Signing](android/README.md#signing)); um fork sem eles assina com uma chave de debug nova a cada execução.
