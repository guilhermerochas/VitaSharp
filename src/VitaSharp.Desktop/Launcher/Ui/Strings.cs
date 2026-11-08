using System.Globalization;
using VitaSharp.Core.Hle.Np;

namespace VitaSharp.Desktop.Launcher.Ui;

/// <summary>
/// The launcher's texts (pt-BR). Shaped like the class the .resx designer generates (one static property per text,
/// methods only where a number or name goes in), so the planned en/pt-BR resource files replace this file without
/// touching the pages: each property becomes a resource key, each method a format string.
/// </summary>
public static class Strings
{
    public static CultureInfo Culture { get; } = CultureInfo.GetCultureInfo("pt-BR");

    static string F(string format, params object?[] args) => string.Format(Culture, format, args);

    // ------------------------------------------------------------------ navigation and shared
    public static string Library => "Biblioteca";
    public static string VitaSystem => "Sistema do Vita";
    public static string Trophies => "Troféus";
    public static string Captures => "Capturas";
    public static string InstallNav => "Instalar pkg / vpk";
    public static string Settings => "Configurações";
    public static string Controls => "Controles";
    public static string Firmware => "Firmware";
    public static string OpenVitaSystem => "Abrir Sistema do Vita";
    public static string OpenVitaSystemTip => "Abrir Sistema do Vita (Ctrl+H)\nO shell.self do firmware: tela inicial, LiveArea e apps do sistema, numa janela do emulador. Funciona em qualquer tela do launcher.";
    public static string ShortcutAnywhere => "Ctrl+H · em qualquer tela";
    public static string InstallFirmwareFirst => "Instale o firmware antes";
    public static string GameRunningShort => "Um jogo está rodando";
    public static string FirmwareInstalledShort(string version) => F("Firmware {0} instalado", version);
    public static string FirmwareMissingShort => "Firmware não instalado";
    public static string FirmwareTip => "Abre a página do Sistema do Vita (firmware e opções)";
    public static string ThemeSystem => "Sistema";
    public static string ThemeDark => "Escuro";
    public static string ThemeLight => "Claro";
    public static string RunningNow => "Rodando agora";
    public static string Stop => "Parar";
    public static string ForceStop => "Forçar parada";
    public static string ShowLog => "Mostrar log";
    public static string HideLog => "Esconder log";
    public static string Ok => "OK";
    public static string Yes => "Sim";
    public static string No => "Não";
    public static string Cancel => "Cancelar";
    public static string Open => "Abrir";
    public static string OpenFolder => "Abrir pasta";
    public static string Delete => "Apagar";
    public static string More => "Mais";
    public static string SeeAll => "Ver todas";
    public static string AllGames => "Todos os jogos";
    public static string AllShort => "todos";
    public static string Play => "Jogar";
    public static string LiveArea => "LiveArea";
    public static string LiveAreaTip => "Abre a LiveArea do jogo (o quadro do jogo, como no Vita); Iniciar começa o jogo";
    public static string OpenLiveArea => "Abrir a LiveArea";
    public static string OpenGamePage => "Abrir a página do jogo";
    public static string RunningBadge => "Rodando";
    public static string NeverPlayed => "Nunca jogado";
    public static string StatusReady(int games, string dataRoot) => F("{0} jogo(s) instalado(s) · pasta de dados: {1}", games, dataRoot);
    public static string Running(string title) => F("Rodando {0}", title);
    public static string AlreadyRunning(string title) => F("{0} já está rodando: feche-o antes de abrir outro.", title);
    public static string CouldNotStart(string error) => F("Não foi possível iniciar o emulador: {0}", error);
    public static string Closing => "Fechando o jogo (uma gravação em andamento é salva antes)…";
    public static string Closed(string title) => F("{0} fechado.", title);
    public static string StoppedWithCode(string title, int code) => F("{0} parou com o código {1}. Veja o log.", title, code);

    public static string RegionUs => "EUA";
    public static string RegionEurope => "Europa";
    public static string RegionJapan => "Japão";
    public static string RegionAsia => "Ásia";
    public static string RegionOther => "Outra";
    public static string RegionHomebrew => "Homebrew";

    /// <summary>"12h 40m", "35 min", "menos de 1 min".</summary>
    public static string Duration(long seconds) => seconds switch
    {
        >= 3600 => F("{0}h {1:00}m", seconds / 3600, seconds % 3600 / 60),
        >= 60 => F("{0} min", seconds / 60),
        _ => "menos de 1 min",
    };

    /// <summary>"hoje, 21:14", "ontem, 20:02", "05/11/2026 22:47".</summary>
    public static string When(DateTime local)
    {
        var today = DateTime.Today;
        if (local.Date == today) return F("hoje, {0:HH:mm}", local);
        if (local.Date == today.AddDays(-1)) return F("ontem, {0:HH:mm}", local);
        return local.ToString("dd/MM/yyyy HH:mm", Culture);
    }

    public static string Size(long bytes) => bytes switch
    {
        >= 1L << 30 => (bytes / (double)(1L << 30)).ToString("0.0", Culture) + " GB",
        >= 1L << 20 => (bytes / (double)(1L << 20)).ToString("0.0", Culture) + " MB",
        _ => (bytes / 1024.0).ToString("0", Culture) + " KB",
    };

    public static string Grade(TrophyGrade g) => g switch
    {
        TrophyGrade.Platinum => "Platina", TrophyGrade.Gold => "Ouro", TrophyGrade.Silver => "Prata", _ => "Bronze",
    };

    // ------------------------------------------------------------------ library
    public static string LibrarySubtitle(int games, int playable) => F("{0} jogos instalados · {1} jogáveis", games, playable);
    public static string SearchGames => "Buscar por nome ou ID";
    public static string RefreshTip => "Ler a pasta de jogos de novo (F5)";
    public static string Install => "Instalar";
    public static string InstallVpkMenu => "VPK ou zip…";
    public static string InstallPkgMenu => "PKG com licença…";
    public static string AddFolderMenu => "Pasta de jogo descriptografado…";
    public static string FilterAll(int n) => F("Todos · {0}", n);
    public static string FilterAllShort => "Todos";
    public static string FilterPlayable(int n) => F("Jogos · {0}", n);
    public static string FilterRecent(int n) => F("Jogados · {0}", n);
    public static string FilterTrophies => "Com troféus";
    public static string AllRegions => "Todas as regiões";
    public static string AllRegionsShort => "todas";
    public static string RegionChip(string region) => F("Região: {0}", region);
    public static string SortChip(string sort) => F("Ordenar: {0}", sort);
    public static string SortAz => "A–Z";
    public static string SortRecent => "Jogados por último";
    public static string SortTime => "Mais jogados";
    public static string ViewGrid => "Capas";
    public static string ViewList => "Lista";
    public static string ViewHome => "Tela inicial";
    public static string ViewHomeTip => "Tela inicial recriada (provisória): bolhas e LiveArea com o visual do firmware";
    public static string NoMatch => "Nenhum jogo corresponde à busca ou aos filtros.";
    public static string ContinuePlaying => "Continuar jogando";
    public static string PlayedSummary(PlayRecord? r) => r == null ? NeverPlayed : F("{0} · {1}", Duration(r.Seconds), When(r.LastPlayedUtc.ToLocalTime()));
    public static string LoadLatestState => "Carregar último estado";
    public static string ColGame => "Jogo";
    public static string ColRegion => "Região";
    public static string ColPlayed => "Tempo";
    public static string ColTrophies => "Troféus";
    public static string ColSize => "Tamanho";
    public static string Welcome => "Bem-vindo ao VitaSharp";
    public static string WelcomeSubtitle => "Dois passos e a biblioteca está pronta. Nada sai do seu computador.";
    public static string StepFirmware => "Instale o firmware";
    public static string StepFirmwareText => "O PSVUPDAT.PUP oficial (3.74): liga o Sistema do Vita e as bibliotecas que muitos jogos usam.";
    public static string StepFirmwareDone(string version) => F("Firmware {0} instalado. O Sistema do Vita já abre pelo botão da barra lateral.", version);
    public static string InstallFirmware => "Instalar firmware";
    public static string StepGames => "Adicione jogos";
    public static string StepGamesText => "Arraste um pkg, vpk ou zip para a janela, ou escolha os arquivos. pkg pede a licença (zRIF ou work.bin).";
    public static string StepControls => "Confira os controles";
    public static string StepControlsText => "O teclado já vem configurado. Controles de PlayStation, Xbox e genéricos são reconhecidos sozinhos.";
    public static string ConfigureControls => "Configurar controles";
    public static string EmptyLibrary => "Sua biblioteca está vazia";
    public static string EmptyLibraryText => "Os jogos instalados aparecem aqui com capa, troféus e estados salvos.";

    // ------------------------------------------------------------------ game page
    public static string CategoryGame => "Jogo";
    public static string InstalledOn(DateTime local) => F("instalado em {0:dd/MM/yyyy}", local);
    public static string OpenGameFolder => "Abrir pasta do jogo";
    public static string OpenSaveData => "Abrir dados salvos";
    public static string OpenStatesFolder => "Abrir pasta dos estados salvos";
    public static string Uninstall => "Desinstalar";
    public static string TimePlayed => "Tempo jogado";
    public static string LastPlayed => "Última vez";
    public static string NoTrophies => "Sem troféus";
    public static string SaveStatesTitle => "Estados salvos";
    public static string QuickState => "Rápido";
    public static string Slot(string n) => F("Slot {0}", n);
    public static string StateUnreadable => "Não é um estado salvo legível";
    public static string Incompatible => "Incompatível";
    public static string LoadStateTip => "Inicia o jogo e carrega este estado";
    public static string NoStates => "Nenhum estado salvo. No jogo: F5 salva o rápido, F7 carrega; F6 escolhe o slot e Shift+F5 / Shift+F7 salvam e carregam nele.";
    public static string SeeAllTrophies => "Ver lista completa";
    public static string NoTrophyYet => "Nenhum troféu obtido ainda.";
    public static string NoGameCaptures => "Nenhuma gravação ou dump deste jogo. No jogo: F8 grava tudo, Ctrl+F8 só o dump da GPU, F10 um diagnóstico.";
    public static string GameSettings => "Ajustes deste jogo";
    public static string UseGlobal => "Usar os padrões";
    public static string UseGlobalTip => "Volta este jogo às opções padrão";
    public static string InternalResolution => "Resolução interna";
    public static string InternalResolutionDetail => "Mais nítido, custa placa de vídeo";
    public static string InternalResolutionTip => "Renderiza o jogo numa resolução maior que a do Vita (como o PCSX2): imagem mais nítida na janela ou em tela cheia. " +
        "Cada passo custa bem mais da placa de vídeo (2x = 4 vezes os pixels, 4x = 16 vezes); se o jogo ficar lento, volte para 1x.";
    public static string FrameRate => "Taxa de quadros";
    public static string FrameRateDetail => "Limite deste jogo";
    public static string FrameRateTip => "Nativo: o jogo roda como no Vita. Fixo em 30/60: cada quadro dura 2 ou 1 vblank (ex.: um jogo a 20 fps passa a 30). " +
        "Sem limite: sem espera de vblank, o jogo roda o mais rápido que o PC aguentar. Jogos cuja lógica anda por quadro ficam " +
        "mais rápidos ou mais lentos com isso (velocidade, física, música fora de sincronia); se acontecer, volte para Nativo.";
    public static string ControlsDetail => "Teclado e controles (valem para todos os jogos)";
    public static string AppliesOnStart => "Vale quando o jogo abrir.";
    public static string AppliesNextStart => "Vale na próxima vez que o jogo abrir.";

    // ------------------------------------------------------------------ trophies
    public static string TrophiesSubtitle(int got, int total, int games) => F("{0} de {1} troféus em {2} jogos", got, total, games);
    public static string NoTrophyGames => "Nenhum jogo instalado tem troféus.";
    public static string SearchGame => "Buscar jogo";
    public static string TrophiesGot(int n) => F("Obtidos · {0}", n);
    public static string TrophiesMissing(int n) => F("Faltam · {0}", n);
    public static string HiddenTrophy => "Troféu oculto";
    public static string HiddenTrophyText => "Os detalhes aparecem quando ele for obtido.";
    public static string Locked => "Bloqueado";
    public static string Unlocked => "Obtido";

    // ------------------------------------------------------------------ captures
    public static string CapturesSubtitle(int count, string size) => F("{0} arquivos · {1} na pasta de dados", count, size);
    public static string DeleteOld(int n) => F("Apagar as de mais de 30 dias ({0})", n);
    public static string RecordingsFolder => "Gravações (captures)";
    public static string GpuDumpsFolder => "Dumps da GPU (gpudumps)";
    public static string DiagnosticsFolder => "Pasta de diagnósticos";
    public static string Recordings(int n) => F("Gravações F8 · {0}", n);
    public static string GpuDumps(int n) => F("Dumps da GPU · {0}", n);
    public static string DiagnosticsChip(int n) => F("Diagnósticos F10 · {0}", n);
    public static string GameChip(string game) => F("Jogo: {0}", game);
    public static string NoCaptures => "Nada aqui. No jogo: F8 inicia e para \"Gravar tudo\", Ctrl+F8 grava só o dump da GPU, F10 salva um diagnóstico.";
    public static string RecordingBadge => "Gravação F8";
    public static string GpuDumpBadge => "Dump da GPU";
    public static string DiagnosticBadge => "Diagnóstico";
    public static string ShowInFolder => "Mostrar na pasta";
    public static string ConfirmDeleteOne(string name) => F("Apagar {0}? Não dá para desfazer.", name);
    public static string ConfirmDeleteMany(int n, string size) => F("Apagar {0} arquivos ({1})? Não dá para desfazer.", n, size);
    public static string Deleted(int n) => F("{0} arquivo(s) apagado(s).", n);

    // ------------------------------------------------------------------ install
    public static string InstallTitle => "Instalar pkg / vpk";
    public static string InstallSubtitle => "Jogos, atualizações e DLCs. Vão para ux0:app da pasta de dados; o arquivo original não é apagado.";
    public static string DropHere => "Arraste arquivos .pkg, .vpk ou .zip para cá (ou para qualquer tela)";
    public static string DropHint => "pkg precisa da licença: zRIF, work.bin ou .rif. O firmware é instalado na página Sistema do Vita.";
    public static string ChooseVpk => "VPK ou zip";
    public static string ChoosePkg => "PKG com licença";
    public static string ChooseFolder => "Pasta descriptografada";
    public static string Queue => "Fila";
    public static string ClearFinished => "Limpar concluídos";
    public static string QueueEmpty => "Nada na fila. O que for instalado aparece aqui com o progresso.";
    public static string Installing => "Instalando";
    public static string InstallingMore(int waiting) => F("Instalando (+{0} na fila)", waiting);
    public static string Waiting => "Na fila";
    public static string InstalledAt(string detail, DateTime when) => F("{0} · {1}", detail, When(when));
    public static string FailedBecause(string why) => F("Falhou: {0}", why);
    public static string RemoveFromQueue => "Tirar da fila";
    public static string OpenPage => "Abrir página";
    public static string InstallCancelled => "Instalação cancelada.";
    public static string InstallCancelledOf(string name) => F("Instalação de {0} cancelada.", name);
    public static string InstallFailed(string error) => F("A instalação falhou: {0}", error);
    public static string InstallFailedOf(string name) => F("{0} não pôde ser instalado. Veja a fila.", name);
    public static string NothingInstalled(string name) => F("Nada instalado de {0} (já estava instalado).", name);
    public static string Installed(string title) => F("{0} instalado.", title);
    public static string Reinstalled(string title) => F("{0} reinstalado.", title);
    public static string InstalledPatch(string id, string version) => F("Atualização de {0} instalada{1}.", id, version.Length > 0 ? " (v" + version + ")" : "");
    public static string InstalledAddOn(string id) => F("DLC de {0} instalado.", id);
    public static string ReplacePatch(string id, string version) => F("Já há uma atualização de {0}. Trocar pela {1}?", id, version.Length > 0 ? "versão " + version : "deste arquivo");
    public static string ReplaceAddOn(string id) => F("Este DLC de {0} já está instalado. Trocar?", id);
    public static string ReplaceGame(string title, string id) => F("\"{0}\" ({1}) já está instalado. Trocar? Os dados salvos ficam.", title, id);
    public static string ReplaceFolder(string id) => F("{0} já está instalado. Trocar?", id);
    public static string NotAGameFolder => "Essa pasta não é um jogo: não tem eboot.bin.";
    public static string Copying => "Copiando…";
    public static string Added(string title) => F("{0} adicionado.", title);
    public static string PickVpkTitle => "Escolha VPKs ou zips de PS Vita";
    public static string VpkFileType => "VPK / zip de PS Vita";
    public static string PickPkgTitle => "Escolha um pacote de PS Vita (.pkg)";
    public static string PkgFileType => "Pacote de PS Vita";
    public static string PickFolderTitle => "Escolha a pasta de um jogo descriptografado (com eboot.bin e sce_sys)";
    public static string ZrifWatermark => "zRIF (KO5ifR1dQ+eHBlOi…)";
    public static string ZrifEmpty => "Cole um zRIF ou escolha o arquivo de licença.";
    public static string ZrifNot => "Isso não é um zRIF (ele começa com KO5ifR1dQ+…).";
    public static string ZrifUnreadable(string error) => F("Não deu para ler o zRIF: {0}", error);
    public static string ChooseLicenseFile => "Escolher work.bin / .rif…";
    public static string PickLicenseTitle => "Escolha a licença deste pacote (work.bin ou .rif)";
    public static string LicenseFileType => "Licença";
    public static string LicenseFor(string name) => F("Licença de {0}", name);
    public static string LicenseText => "Cole o zRIF deste pacote ou escolha o arquivo de licença (work.bin ou .rif).";
    public static string ConfirmUninstall(string title, string id) => F("Desinstalar \"{0}\" ({1})? Os dados salvos ficam.", title, id);
    public static string Uninstalled(string title) => F("{0} desinstalado.", title);
    public static string UninstallFailed(string error) => F("A desinstalação falhou: {0}", error);

    // ------------------------------------------------------------------ Vita system page
    public static string VitaSystemSubtitle => "O firmware de verdade (shell.self e libpaf) rodando no emulador. Para abrir: o botão da barra lateral ou Ctrl+H.";
    public static string FirmwareVersionRow(string? version) => version != null ? F("Firmware {0}", version) : "Firmware";
    public static string FirmwareMissingDetail => "Não instalado: instale o PSVUPDAT.PUP acima";
    public static string FirmwareDetail(uint build, string source, DateTime? installedUtc) =>
        string.Join(" · ", new[] { build > 0 ? F("build {0}", build) : "", source, installedUtc is { } t ? F("instalado em {0:dd/MM/yyyy}", t.ToLocalTime()) : "" }.Where(s => s.Length > 0));
    public static string InstalledBadge => "Instalado";
    public static string MissingBadge => "Ausente";
    public static string OkBadge => "OK";
    public static string ShellDetail => "A tela inicial e a LiveArea do Vita (vs0:vsh/shell)";
    public static string Pd0Detail => "Música da tela inicial, temas e dados de fábrica";
    public static string SystemFonts => "Fontes do sistema";
    public static string FontsInstalled => "Pacote de fontes instalado";
    public static string FontsMissing => "Sem o pacote de fontes: o sistema usa a fonte substituta";
    public static string HleModulesTitle => "Bibliotecas";
    public static string HleModules => "Manter em HLE";
    public static string HleModulesDetail => "Bibliotecas que continuam na implementação do VitaSharp mesmo quando o jogo ou o firmware traz o módulo. Vazio: usa os módulos disponíveis.";
    public static string HleModulesWatermark => "ex.: libSceJson, libult";
    public static string RecreatedHomeTitle => "Tela inicial recriada (provisória)";
    public static string RecreatedHome => "Mostrar na biblioteca";
    public static string RecreatedHomeDetail => "A vista \"Tela inicial\" da biblioteca: bolhas e LiveArea desenhadas pelo launcher com os arquivos do firmware, até o shell.self substituí-la";
    public static string HomeTheme => "Tema";
    public static string HomeBgm => "Música do sistema";
    public static string HomeBgmDetail => "Toca a música do tema (pd0:data/systembgm/home.at9 no tema padrão)";
    public static string HomeBgmMissing => "Requer um firmware com pd0 ou um tema com música";
    public static string SystemSounds => "Efeitos sonoros do sistema";
    public static string SystemSoundsDetail => "Sons de confirmar e voltar do Vita (common_resource.rco)";
    public static string TrophyImportTitle => "Importar troféus";

    // ------------------------------------------------------------------ settings
    public static string SectionGeneral => "Geral";
    public static string SectionGraphics => "Gráficos";
    public static string SectionEmulation => "Emulação";
    public static string SectionNetwork => "Rede e PSN";
    public static string SectionAdvanced => "Avançado";
    public static string StartSection => "Início";
    public static string StartupView => "Ao abrir o emulador";
    public static string StartupViewDetail => "Sistema do Vita entra direto no shell, como ligar o console; o launcher fica atrás";
    public static string GameClick => "Ao escolher um jogo";
    public static string GameClickDetail => "O botão em destaque na capa, o duplo clique e Enter; o outro fica ao lado";
    public static string PlayDirectly => "Jogar direto";
    public static string VitaShortcut => "Atalho do Sistema do Vita";
    public static string VitaShortcutDetail => "Em qualquer tela do launcher";
    public static string Appearance => "Aparência";
    public static string Theme => "Tema";
    public static string ThemeDetail => "Sistema segue o tema do sistema operacional";
    public static string Updates => "Atualizações";
    public static string CheckUpdates => "Procurar atualizações ao iniciar";
    public static string CheckUpdatesDetail => "Pelas releases do GitHub do VitaSharp";
    public static string UpdateChannel => "Canal";
    public static string UpdateChannelDetail => "Builds automáticas saem a cada mudança; versões, só nas marcadas";
    public static string ChannelBuilds => "Builds automáticas";
    public static string ChannelReleases => "Só versões";
    public static string VersionInUse => "Versão em uso";
    public static string CheckNow => "Procurar agora";
    public static string CheckNowSoon => "A atualização automática ainda está sendo feita (item do roadmap)";
    public static string TrophySound => "Som de troféu";
    public static string TrophySoundDetail => "Toca um som quando um troféu é obtido no jogo: o aviso do firmware, ou um som próprio sem firmware";
    public static string Fullscreen => "Iniciar jogos em tela cheia";
    public static string FullscreenDetail => "F11 alterna durante o jogo";
    public static string WindowSize => "Tamanho da janela";
    public static string WindowSizeDetail => "Múltiplo da tela do Vita (a resolução interna é por jogo)";
    public static string VSync => "VSync";
    public static string VSyncDetail => "Sincroniza com o monitor (sem rasgos na imagem)";
    public static string FastShaders => "Compilação rápida de shaders";
    public static string FastShadersDetail => "Reduz travamentos quando shaders novos aparecem; desligue se notar perda de desempenho";
    public static string AsyncShaders => "Compilar shaders em segundo plano";
    public static string AsyncShadersDetail => "Prepara shaders e pipelines em outras threads: menos engasgos ao entrar em áreas novas";
    public static string Validation => "Camadas de validação do Vulkan";
    public static string ValidationDetail => "Depuração do renderizador; deixa tudo bem mais lento";
    public static string PerGameGraphicsNote => "Resolução interna e taxa de quadros ficam na página de cada jogo.";
    public static string Cpu => "CPU";
    public static string CpuDetail => "Como o código ARM do Vita roda neste PC";
    public static string CpuAuto => "Automático (o melhor para este PC)";
    public static string CpuNativeX64 => "JIT nativo (código de máquina x64)";
    public static string CpuNativeArm => "JIT nativo (código de máquina ARM64)";
    public static string CpuIl => "JIT IL (.NET DynamicMethod)";
    public static string CpuInterp => "Interpretador (mais lento, depuração)";
    public static string CpuClock => "Clock da CPU";
    public static string CpuClockDetail => "444 MHz carrega mais rápido, mas jogos que dependem do tempo do console podem falhar";
    public static string Clock333 => "333 MHz (como o Vita, recomendado)";
    public static string Clock444 => "444 MHz (carregamentos mais rápidos)";
    public static string Camera => "Câmera";
    public static string CameraDetail => "Webcam usada pelas câmeras do Vita";
    public static string CameraDefault => "Padrão (primeira câmera)";
    public static string CameraNone => "Nenhuma (imagem cinza)";
    public static string CameraDisconnected(string name) => F("{0} (desconectada)", name);
    public static string PsnSignedIn => "Simular conta PSN conectada";
    public static string PsnSignedInDetail => "Desligado (padrão): o console fica sem rede e sem conta, como no Vita3K. Ligue só para jogos que exigem uma conta PSN; nada vai para os servidores da Sony";
    public static string PsnId => "ID online";
    public static string PsnIdDetail => "De 3 a 16 letras, números, '-' ou '_', começando com uma letra";
    public static string PsnIdInvalid => "ID online inválido: use de 3 a 16 letras, números, '-' ou '_', começando com uma letra.";
    public static string PsnLocal => "Serviços PSN locais";
    public static string PsnLocalDetail => "Nuvem (TUS / Cross-Save), placares, tickets e DLC guardados neste PC (pasta np)";
    public static string RealInternet => "Internet real";
    public static string RealInternetDetail => "Sockets, DNS e HTTP dos jogos usam a rede deste PC (os servidores da Sony continuam bloqueados)";
    public static string RealInternetPrivacy => "Privacidade: com a internet real ligada, o jogo envia dados (seu IP, o ID online, pontuações e salvamentos na nuvem) para os servidores dele e para o servidor RPCN abaixo. Desligada (padrão), nada sai do PC.";
    public static string RpcnServer => "Servidor RPCN";
    public static string RpcnServerDetail => "Placares e nuvem com outros jogadores (o servidor aberto do RPCS3). Vazio: os serviços locais";
    public static string RpcnServerWatermark => "ex.: np.rpcs3.net";
    public static string RpcnUser => "Usuário RPCN";
    public static string RpcnPassword => "Senha RPCN";
    public static string RpcnPasswordDetail => "Fica gravada neste PC (launcher.json)";
    public static string RpcnToken => "Token RPCN";
    public static string RpcnTokenDetail => "Só no primeiro login de uma conta nova";
    public static string RpcnTokenWatermark => "só no primeiro login";
    public static string DebugMode => "Modo de depuração";
    public static string DebugModeDetail => "Logs detalhados e diagnóstico automático quando a imagem parece corrompida; custa alguns por cento de desempenho";
    public static string PreRoll => "Pré-gravação";
    public static string PreRollDetail => "F8 inclui os segundos antes de apertar (um estado salvo a cada 30 s pausa o jogo por um instante)";
    public static string DataFolder => "Pasta de dados";
    public static string Shortcuts => "Atalhos no jogo";
    public static string ShortcutsText => "F11 tela cheia · P pausa (Controles > Atalhos do emulador) · F5 / F7 salvar e carregar o estado rápido, F6 escolhe o slot · " +
        "F8 \"Gravar tudo\" (estado, controles, imagens, áudio e dump da GPU num .zip em captures) · Ctrl+F8 só o dump da GPU · " +
        "F9 liga e desliga o modo de depuração · F10 diagnóstico (.zip na pasta de diagnósticos) · " +
        "Mouse: botão esquerdo = tela frontal, botão direito = touchpad traseiro.";
}
