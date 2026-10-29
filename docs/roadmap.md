# Roadmap

Lista de itens futuros do VitaSharp. Marque `[x]` quando um item for concluído.

## Diagnóstico

- [ ] Teclas para alternar opções gráficas com o jogo rodando (MSAA real / antigo / desligado e outras)

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

## Android

- [ ] App .NET MAUI com interface declarativa em C# (MauiReactor), seguindo os mockups em `docs/android/`
- [ ] Controles na tela (horizontal), mapeamento de controles Bluetooth e toque traseiro
- [ ] Sensores do celular alimentando o SceMotion (interface `IMotionSource` já existe no Core) e câmera do celular
  no SceCamera
- [ ] Validar num aparelho Adreno e Mali a camada de quirks de GPU (`docs/notes/mobile-gpu.md`): quais contornos
  continuam necessários, `VK_EXT_rasterization_order_attachment_access` (nunca executado) e o ganho real dos
  load/store por cena no perfilador do aparelho
- [ ] Menos interrupções de cena no renderizador (no Killzone cada cena vira ~13 renderizações por uploads de
  textura e cópias; em GPU tile-based cada uma custa um store e um load da memória do tile)
- [ ] Texturas BC/PVRTC convertidas para ASTC (hoje BC vira RGBA8 na CPU, com cache em memória) e cache em disco
- [ ] Caminho para Vulkan 1.1 (Adreno 6xx com drivers antigos): render passes no lugar de dynamic rendering
- [ ] Driver de GPU customizado: compilar e empacotar a libadrenotools, tela "Driver da GPU" e teste num aparelho
  (a interface e as configurações já existem em `src/VitaSharp.Android/Gpu`)
- [ ] Vídeo pelo MediaCodec e áudio pelo AAudio/Oboe (hoje só existem esqueletos em `src/VitaSharp.Android`)
- [ ] Energia e telas: Performance Hint API (ADPF), Swappy (90/120 Hz), threads nos núcleos rápidos
- [ ] Armazenamento: escolher a pasta dos jogos (Storage Access Framework), instalar VPK, saves

## Firmware e sistema

- [ ] Sistema do Vita de verdade (ver docs/notes/system-apps.md): o shell já mostra os jogos, roda vários processos e faz a
  configuração inicial; falta o app de Troféus/Configurações desenharem (composição da tela compartilhada), o botão PS
  no PC, o texto cortado no boot direto, o travamento do teclado na tela (IME), o aviso "Could not install" e as
  funções restantes (sensor de movimento, sockets do sistema)

## Bugs em aberto

- [ ] Gravity Rush: um toque em 0,46/0,48 no quadro 1000 da tela de título não registrou (o toque no centro funciona)
- [ ] Killzone: depois da correção dos blocos, cada tiro clareia a cena por 1–3 quadros e às vezes deixa a tela inteira com um brilho forte (a luz do disparo agora é aplicada); confirmar a intensidade com o Vita real
- [ ] Killzone: congelamento raro com comandos da GPU pendentes (visto uma vez no quadro 1275)
- [ ] Killzone: em algumas áreas do kzl_walk3 o jogo cai para 5–10 fps por 20–40 s; a thread principal fica em polling com `sceKernelDelayThread` (loop em 8100D692) esperando algo que ninguém produz (jobs e GPU ociosos); ver docs/notes/kz-mainthread.md
- [ ] Anexo da palavra extra do registrador de saída (TileWord1): falta nas cenas supersampled, no caminho tile-based (Android) e com VITASHARP_MSAA_FETCH=copy (lá a palavra continua perdida, com aviso no log)
- [ ] Skullgirls: elementos do HUD (contador de combo, barra de cima) aparecem como caixas brancas por 1 quadro durante as lutas
- [ ] Fibers: `_sceFiberAttachContext*` exige status Init (o firmware aceita qualquer fiber ocioso sem área de contexto; 0/0 é switch simples) e o topo da pilha deveria ser `(addr+size-1)&~7`
- [ ] MSAA real: leitura da imagem pelo próprio desenho (input attachment) é indefinida quando triângulos do mesmo desenho se sobrepõem (sem `VK_EXT_rasterization_order_attachment_access` na GTX 1650/Iris Xe); causa diferenças de ±1 entre execuções
- [ ] Sonic Mania: ~18 estalos por segundo na saída de música (44,1 kHz); pode ser do próprio conteúdo, não investigado
- [ ] ATRAC9: a saída do nosso decodificador tem a polaridade invertida em relação ao FFmpeg (inaudível; confirmar qual está certa)
- [ ] Cenário `rbs_boot.txt` do Resistance não serve mais com o save existente (usar `rbs_continue.txt`)
- [ ] Killzone: commit 3dc7d41 (sincronização U4U4U4U4) segurado porque deixava o rodapé do EULA em itálico
- [ ] Persona 4: digitar o nome no começo do jogo exige o teclado na tela (IME); os testes automáticos só conseguem digitar "A"
- [ ] Ninja Gaiden Sigma Plus: travamento intermitente no "Now Loading" do boot (antes dos logos), visto 1 vez em 3 execuções do `ngs_boot.txt` com dados novos; não depende da conta PSN (as outras execuções, com `--psn=on` e `--psn=off`, chegaram ao jogo)
- [ ] `sceNpManagerGetCachedParam`: layout não confirmado, por isso responde "sem conta" mesmo com a conta PSN emulada conectada (o UnityNpToolkit do 99Vidas chama e segue em frente)
