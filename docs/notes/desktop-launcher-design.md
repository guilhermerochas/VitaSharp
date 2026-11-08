# Launcher do desktop: proposta de layout novo (etapa 1)

Proposta para o item do roadmap "Sugestão de layout novo para o launcher do desktop". É só o desenho: o launcher
atual (`src/VitaSharp.Desktop/Launcher/MainWindow.cs`) não mudou. As telas são um protótipo Avalonia de verdade em
`src/VitaSharp.Desktop/Launcher/Prototype/`, renderizado para PNG sem abrir janela (plataforma headless do Avalonia com
Skia), a partir dos jogos instalados na pasta de dados:

```
VitaSharp ui-prototype <pasta-de-saída> --data=<pasta de dados de teste> [--title=<ID do jogo em destaque>] [--size=1440x900]
```

Gera 12 telas em `dark` e `light` (`--screens=a,b` escolhe algumas): `library`, `library-list`, `library-home`,
`game`, `settings`, `settings-general`, `system`, `trophies`, `captures`, `install`, `update` e `empty`. São lidos de
verdade: títulos, IDs, versões, região, `icon0.png`, `pic0.png`, tamanho das pastas, troféus (pacote do jogo +
desbloqueios gravados, de uma CÓPIA da pasta de troféus), estado do firmware (`firmware.json`, pd0), as gravações F8
e os dumps da GPU, e a tela inicial do Vita é o próprio `ShellView` do launcher. São exemplos até a etapa 2: tempo
jogado, estados salvos (miniaturas são recortes do `pic0`), a fila de instalação e as notas do diálogo de atualização
(assuntos de commits reais entre duas builds).

## Identidade

A mesma do app Android (`src/VitaSharp.Android/Ui/Theme.cs`, `docs/android/screens`), adaptada para janela grande.
Não copia outro emulador: a referência é o próprio VitaSharp no celular.

- **Paleta escura** (a do Android): fundo `#0E1116`, barra lateral `#0B0E13`, superfície `#171B22`, destaque
  `#1A2233` com borda `#2B3A55`, bordas `#232A35`/`#2A313D`, texto `#E8ECF2`/`#C9D1DC`, apagado `#9AA4B2`/`#6B7584`,
  acento azul `#6E9BFF` (texto sobre ele `#0B1220`), trilho `#2A3550`.
- **Paleta clara** (nova, mesmos papéis): fundo `#F3F5F9`, barra lateral `#EAEEF4`, superfície branca, destaque
  `#E3EBFD`, bordas `#E1E6EE`/`#D2D9E4`, texto `#121722`, apagado `#5D6878`, acento `#3767DB` (mais escuro, para
  contraste no branco) com texto branco e uma sombra leve nos cartões.
- **Estados e troféus**: ok `#7FD6A4`, aviso `#F2B45A`, perigo `#FF8A8A`; platina/ouro/prata/bronze
  `#A9C7F0`/`#E2B13C`/`#B8C2D0`/`#C98A5A` (versões mais escuras no tema claro).
- **Tipografia**: Sora (títulos, números grandes) e Inter (todo o resto), as fontes do Android (OFL), embutidas no
  executável como recurso Avalonia a partir de `src/VitaSharp.Android/Resources/Fonts`. Título de página 30, seção 16-17,
  texto 13,5-14,5, legendas 11,5-12,5; rótulos de grupo em maiúsculas com espaçamento.
- **Formas**: cantos 12 (campos, itens de menu), 16-18 (botões, cartões, capas), 24 (cartão de destaque), 30-34
  (ícone grande do jogo). Bordas de 1 px em vez de sombras no escuro; botões com 46-50 px de altura.
- **Ícones**: traço de 1,8 px no estilo Lucide (24×24), desenhados como `Path` (sem pacote novo de ícones).

## Navegação

Barra lateral fixa (252 px) com o logo, **Biblioteca** (com a contagem), **Sistema do Vita** (o shell.self do
firmware, como hoje), **Troféus**, **Capturas**, **Instalar pkg / vpk** e **Configurações**. No pé dela: a instalação
em andamento (arquivo, barra, GB e tempo restante; arrastar um pkg/vpk/zip para a janela também instala), o estado do
firmware e o seletor de tema (escuro/claro). A página do jogo abre por cima da biblioteca com "‹ Biblioteca" para
voltar; Esc e o botão Voltar do mouse/controle também voltam.

## Sistema do Vita em um clique

- Botão **Abrir Sistema do Vita** no topo da barra lateral, em destaque (gradiente do acento), visível em todas as
  telas; o atalho é **Ctrl+H** ("Home"), mostrado no próprio botão e na dica. Nenhuma tecla F: F8, F11 e as outras já
  são do jogo. Sem firmware o botão fica apagado com "Instale o firmware antes".
- A página **Sistema do Vita** fica só com os extras: firmware (versão, build, pd0, fontes, verificar), importar do
  Vita3K e troféus, e as opções do sistema (idioma, botão de confirmar, data, tema, música e sons).
- **Configurações > Geral > Início**: "Ao abrir o emulador: Biblioteca / Sistema do Vita" (entrar direto no shell, como
  o "boot BIOS" do PCSX2), "Ao escolher um jogo: Jogar direto / Abrir a LiveArea" e o atalho.
- **Biblioteca**: passar o mouse numa capa (ou numa linha da lista) mostra **Jogar** e **LiveArea** lado a lado; o
  clique simples segue o ajuste acima. A vista "Tela inicial" é a tela inicial do Vita já existente no launcher.

## Telas extras (segunda rodada)

- **Biblioteca em lista** (`library-list_*`): ícone, nome e ID, região, versão, troféus com barra, tamanho real; a
  linha sob o mouse ganha Jogar/LiveArea.
- **Biblioteca na Tela inicial** (`library-home_*`): o `ShellView` (bolhas e papel de parede do firmware) dentro do
  quadro da página.
- **Troféus** (`trophies_*`): total por categoria no topo; à esquerda os jogos (ícone, barra, obtidos/total, busca);
  à direita o jogo escolhido com o `pic0`, contagem por categoria, filtros (todos, obtidos, faltam) e a lista com
  ícone, nome, descrição, categoria e data (bloqueados apagados, ocultos sem texto até serem obtidos).
- **Capturas** (`captures_*`): filtros por tipo (gravações F8 "Gravar tudo", capturas de tela, dumps da GPU) e por
  jogo; cada linha com miniatura, jogo, tipo, arquivo, data, tamanho, abrir pasta e apagar; "Apagar as de mais de 30
  dias" e "Abrir pasta" no topo.
- **Instalar pkg / vpk** (`install_*`): área de soltar (arrastar vale em qualquer tela), escolher arquivos ou pasta
  descriptografada; fila com o item em andamento (barra, GB, tempo), na fila, concluídos (Jogar / Abrir página) e
  falhos com o motivo e a ação (Informar licença).
- **Atualização** (`update_*`, item do roadmap de atualização automática): diálogo no estilo do PCSX2 sobre a
  biblioteca, com versão em uso → nova (build e data), o que mudou (commits, "ver tudo no GitHub"), a garantia de que
  dados não são tocados e **Pular esta versão / Lembrar depois / Atualizar agora**. Os ajustes ficam em
  `settings-general_*`: procurar ao iniciar, canal (builds automáticas ou só versões), versão em uso e "Procurar agora".
- **Primeira execução** (`empty_*`): três passos (firmware, jogos, controles) e a biblioteca vazia como área de
  soltar; barra lateral com o botão do Sistema do Vita apagado e "Firmware não instalado".

## Telas

- **Biblioteca** (`library_*.png`): título com contagem, busca (nome ou ID, Ctrl+F), botão Instalar; filtros em chips
  (Todos, Jogáveis, Recentes, Com troféus), região (do ID: EUA, Europa, Japão, Ásia, homebrew) e ordenação; modo de
  exibição grade/lista/Tela inicial do Vita. Cartão "Continuar jogando" com o `pic0` do último jogo de fundo, ícone,
  tempo, troféus, Jogar e "Carregar estado". Grade de capas grandes: `pic0` como arte (desbotando na superfície), `icon0`
  por cima, região, título, ID e versão, selo de compatibilidade e troféus. Colunas conforme a largura (≥ 240 px por
  cartão). Gênero não aparece: o `param.sfo` não traz esse dado.
- **Página do jogo** (`game_*.png`): `pic0` (ou o fundo da LiveArea) ocupando o topo e desbotando no fundo, ícone grande,
  selos, título, ID · versão · tamanho · data de instalação, botões Jogar / Abrir na LiveArea / Carregar estado,
  pasta e menu "⋯" (remover, verificar arquivos). Quatro números (tempo jogado, última vez, FPS médio, troféus). Abas:
  Visão geral, Estados salvos (miniaturas com data, "Salvar no jogo"), Troféus (por categoria, recentes com ícone),
  Capturas, Ajustes do jogo. À direita, **Ajustes deste jogo**: Resolução interna e Taxa de quadros (as opções do branch
  de desempenho entram aqui como estão, só mudam de lugar), Controles (perfil próprio do jogo), CPU e Mostrar FPS, com
  "Usar os globais" e o aviso de que vale na próxima abertura.
- **Configurações** (`settings_*.png`): seções à esquerda (Geral, Sistema do Vita, Gráficos, Áudio, Controles, Rede e
  PSN, Troféus, Armazenamento, Avançado) e uma página só que rola, com busca de ajustes. Linhas no padrão do Android:
  ícone em quadrado, título, explicação curta e o controle à direita (chave, lista, segmentado ou "›" para uma
  subpágina como Firmware ou o painel de controles atual).

## Componentes (etapa 2)

Os controles do protótipo viram estilos Avalonia sobre os controles comuns (`Button.primary`, `Button.secondary`,
`ToggleButton.chip`, `ComboBox`, `ToggleSwitch`, `ProgressBar`, `TextBox.search`), com as cores em `ThemeDictionaries`
(`ThemeVariant.Dark`/`Light`) e "Sistema" seguindo o Windows. A grade usa `ItemsRepeater`/`UniformGridLayout` virtualizado
e carrega `icon0`/`pic0` reduzidos em segundo plano. O painel de controles, troféus, firmware e o Sistema do Vita
existentes são reaproveitados dentro das novas páginas.

## Para aprovar

1. Barra lateral fixa em vez de abas no topo.
2. Capas em paisagem com `pic0` + `icon0` (alternativa: só `icon0` quadrado, mais jogos por linha).
3. Ajustes por jogo ao lado da visão geral (alternativa: só na aba "Ajustes do jogo").
4. Tema claro com acento `#3767DB`.
