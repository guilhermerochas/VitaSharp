# Roadmap

Lista de itens futuros do VitaSharp. Marque `[x]` quando um item for concluído.

## Diagnóstico

- [ ] Teclas para alternar opções gráficas com o jogo rodando (MSAA real / antigo / desligado e outras)
- [ ] Detecção de NaN e infinito nas imagens intermediárias a cada quadro, e versão instrumentada que aponta o desenho
  que gerou o valor inválido
- [ ] Lista compacta de desenhos dos últimos ~60 quadros sempre em memória, incluída nas capturas
- [ ] Gravação completa da GPU (comandos GXM + memória usada) para reproduzir um quadro fora do jogo, como os
  "GS dumps" do PCSX2

## Save states

- [ ] Salvar e carregar o estado inteiro do emulador: CPU (registradores, NEON/VFP, monitor exclusivo), memória,
  objetos do kernel (threads, semáforos, mutexes, event flags, timers), estado dos módulos HLE (áudio, NGS, GXM,
  arquivos abertos, câmera, rede simulada) e estado da GPU (imagens renderizadas, texturas, cache)
- [ ] Formato versionado e tolerante a mudanças do emulador; aviso claro quando um estado antigo não puder ser carregado
- [ ] Interface: slots por jogo com miniatura, atalhos (salvar/carregar rápido) e menu de pausa
- [ ] Uso em diagnóstico: o jogador salva o estado no momento do problema e manda junto com a captura

## Desempenho (PC)

- [ ] Opção de pular o desenho enquanto o pipeline não fica pronto (tira os últimos engasgos da primeira vez)
- [ ] JIT: multiplicações Thumb (SMULxy/SMLAxy/SMULW/SMMUL) e somas/subtrações paralelas (Mania, Devious Dungeon)
- [ ] JIT: VLD2-4/VST2-4, VSHL.reg.64, VMULL, VCLS inline
- [ ] JIT: registradores NEON presos em registradores XMM do PC e comparação + desvio juntos (medir antes)
- [ ] JIT: compilação mais barata por bloco
- [ ] Queda para 50-54 fps no menu do Sonic Mania
- [ ] Native AOT no Windows (falta instalar as ferramentas de C++ do Visual Studio)
- [ ] Prioridade das threads da CPU emulada e da GPU (depois de medir)

## Android

- [ ] JIT nativo ARM64 (plano em `docs/notes/native-jit.md`) — base de todo o resto
- [ ] App .NET MAUI com interface declarativa em C# (MauiReactor), seguindo os mockups em `docs/android/`
- [ ] Controles na tela (horizontal), mapeamento de controles Bluetooth e toque traseiro
- [ ] Sensores do celular alimentando o SceMotion (interface `IMotionSource` já existe no Core) e câmera do celular
  no SceCamera
- [ ] Renderização ajustada para GPU de celular (tile-based): load/store corretos em cada cena, MSAA resolvido no tile
- [ ] Texturas BC/PVRTC convertidas para ASTC ou RGBA8, com cache em disco
- [ ] Vídeo pelo MediaCodec e áudio pelo AAudio/Oboe (hoje só existem esqueletos em `src/VitaSharp.Android`)
- [ ] Energia e telas: Performance Hint API (ADPF), Swappy (90/120 Hz), threads nos núcleos rápidos
- [ ] Armazenamento: escolher a pasta dos jogos (Storage Access Framework), instalar VPK, saves
- [ ] Compatibilidade com drivers Vulkan de celular (Adreno, Mali, PowerVR) e testes em aparelhos reais

## HLE

- [ ] `sce::Json` (SceLibJson, 179 métodos C++, importado pelo Devious Dungeon): implementar em HLE para não
  depender do `libSceJson.suprx` do firmware; exige reproduzir o layout de `Value`/`Array`/`Object`, os iteradores e
  o `MemAllocator` que o jogo herda (ver `docs/hle/G2-support-libs.md` §3.1)

## Firmware e sistema

- [ ] Usar automaticamente o firmware da instalação do Vita3K (vs0: Json, Xml, Sqlite; sa0: fontes)
- [ ] Instalador de firmware próprio (`PSP2UPDAT.PUP`)
- [ ] Tela inicial e LiveArea no launcher
- [ ] Apps de sistema (Configurações, Gerenciador de Conteúdo): exige registro do sistema, SceShell/SceIpmi e
  vários processos

## Bugs em aberto

- [ ] Killzone: blocos verdes e azuis passageiros na área subterrânea (esperando captura com replay)
- [ ] Killzone: congelamento raro com comandos da GPU pendentes (visto uma vez no quadro 1275)
- [ ] sceAudiodec AAC: bytes consumidos reportados como `esMax` em vez do tamanho real do quadro
- [ ] PS TV: L2/R2 como botões separados
- [ ] Cenário `rbs_boot.txt` do Resistance não serve mais com o save existente (usar `rbs_continue.txt`)
- [ ] Killzone: commit 3dc7d41 (sincronização U4U4U4U4) segurado porque deixava o rodapé do EULA em itálico
