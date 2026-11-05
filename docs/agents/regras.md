# Regras para agentes (VitaSharp)

Todo agente que trabalha no VitaSharp lê este arquivo inteiro antes de começar e segue tudo o que está aqui. Quando uma
regra daqui e o pedido do agente divergirem, vale a regra daqui; em dúvida, pergunte ao agente principal (lead).

## Trabalho e entrega

- Trabalhe numa worktree própria (`git worktree add -b <branch> .claude/worktrees/<nome> main`). **Nunca edite a
  checkout principal** `C:\Users\guisa\VitaSharp` (`src`, `docs`, `tests`): use caminhos ABSOLUTOS da sua worktree em
  todo Edit/Write e em toda API de arquivo do .NET/PowerShell (cuidado com caminhos relativos). Preserve CRLF.
- Commits com o trailer `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`; um commit por correção. **Não faça
  merge nem push** (o lead faz).
- `dotnet test tests/VitaSharp.Tests -c Release --filter "Category!=Slow" -m:2 -- RunConfiguration.MaxCpuCount=2`
  passa com 0 avisos; `replay-gpu --compare` em todos os dumps quando mexer no renderizador (só as mudanças
  intencionais podem aparecer).
- Variáveis de ambiente novas em `src/VitaSharp.Core/Util/EnvVars.cs`. Todo script vem em par `.ps1` + `.sh` (CI).
- Só correções genéricas: nada de hack por jogo (title ID, endereço do jogo). Vita3K é a referência de comportamento.
- O Sistema do Vita é o firmware de verdade (shell.self/libpaf LLE), nunca uma imitação.

## Validação e relato

- Nada é "corrigido" sem validação **com o jogo rodando** e prova (print, dump, contagem de detector, gravação de
  áudio) com o caminho. Código, testes ou replay sozinhos não bastam; diga "correção feita, ainda não validada".
- "Validar" um app ou jogo = exercitar cada tela, menu e ação principal, não só abrir.
- **Qualquer outra coisa estranha** notada no caminho (visual, áudio, engasgo, diferença do Vita de verdade) vai para
  o lead **antes** de validar ou fechar o item.
- Vídeos do YouTube do Vita de verdade são referência válida (yt-dlp instalado).
- Procure primeiro a solução genérica mais simples que deixa certo o que o usuário vê ou ouve, antes de mudanças
  grandes (modelos de tempo, reescritas).
- Timebox: 1-2 tentativas por item (desempenho: 1-2 ciclos de build/medida). Achou a causa, corrija; sem solução,
  escreva os achados no `docs/roadmap.md` e siga. Não ande em círculos.
- Desempenho: só o que dá ganho grande; meça primeiro; meça no build LLVM (`mono-aot-llvm`) e só em gameplay real
  (Uncharted: tocar no caderno no canto inferior direito depois do tutorial; no filme, esperar no máximo 40 s e pular).

## CPU (o PC do usuário: 8 CPUs lógicas)

- CPU total **até 92%**: meça a carga (Win32_Processor LoadPercentage, 2-3 amostras) **antes de cada build e de cada
  execução** do emulador e espere se não houver folga. Nunca rode um teste com janela ao mesmo tempo que um build.
- Emuladores fixados na metade menos carregada (0x0F ou 0xF0), prioridade BelowNormal; `dotnet build/test` com `-m:2`.
- Build sempre para a pasta temporária com `-o` (o VitaSharp do usuário trava o `bin` da checkout).
- Pare seus emuladores e rode `dotnet build-server shutdown` ao terminar.

## Dados do usuário

- **Nunca grave em `%APPDATA%\VitaSharp`** (a pasta de dados do usuário) nem na pasta do Vita3K. Use uma pasta de
  dados de teste com junctions por jogo para `%APPDATA%\VitaSharp\fs\ux0\app\<id>`, `license`, `os0`, `vs0`, `sa0`
  (só leitura) e **copie** saves e save states (nunca link). Sempre passe `--data=<pasta de teste>`; `replay-gpu`
  sempre com `--out=<pasta temporária>`.
- Os cenários em `tests/scenarios/*.txt` têm `data=` com a pasta do usuário: sempre rode com `--data=` (ele vence o
  `data=`) ou use cópias sem essa linha.
- **Remova as junctions (rmdir) ANTES de apagar qualquer pasta**, e nunca enquanto uma execução as usa.
- Não faça buscas recursivas em `C:\Users\guisa`. Não mate processos do usuário. Peça ao lead antes de qualquer teste
  que tome a tela do usuário (janela, tela cheia).
- Nunca commite o firmware (PSVUPDAT.PUP) nem capturas de tela ("Captura de tela*.png").
- Ao terminar um item, apague as saídas, builds e capturas dele que não servem mais.

## Celular (Redmi Note 14, adb `haqwzhl7zxu49xfi`)

- **Um agente por vez** no celular; só quem o lead disser que é o dono usa adb. Os outros esperam a vez.
- `export MSYS_NO_PATHCONV=1` no Git Bash.
- **A tela fica acesa o tempo todo** em que você tem o celular: a **primeira** coisa ao conectar é ligar o vigia
  (reabre a AwakeActivity do build de desenvolvedor **só quando** `dumpsys power` mostra `mWakefulness` diferente de
  `Awake`, a cada 30 s). Ele fica ligado durante o trabalho no PC e os builds, até a limpeza final. Depois de cada
  `am force-stop` do app, reabra a AwakeActivity.
- Se o celular estiver offline, sem autorização ou com a tela apagada: toque um alerta sonoro no Windows a cada 1-2
  min (`powershell -c "[System.Media.SystemSounds]::Exclamation.Play()"`) e trabalhe no PC enquanto isso.
- Pode instalar e abrir mesmo que o usuário esteja jogando.
- APK: um build só com todas as variantes atrás de chaves de runtime (`config/env.txt`), testadas na mesma sessão, 3x
  alternadas. A worktree precisa de junctions `third_party\android-shaderc` e `third_party\android-adrenotools` para
  a checkout principal; confira que `lib/arm64-v8a/libshaderc*.so` está no APK antes de instalar; confirme por print
  que o jogo chegou ao gameplay antes de medir.
- No fim: apague `env.txt` (e `input_live.txt`), feche o app, feche a AwakeActivity, pare o vigia.

## Relatórios

- Status curto (2-3 linhas, com números e caminhos das provas) depois de cada item; relatório final no fim.
