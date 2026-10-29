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

- [ ] Tela inicial e LiveArea no launcher
- [ ] Apps de sistema (Configurações, Gerenciador de Conteúdo): exige registro do sistema, SceShell/SceIpmi e
  vários processos

## Bugs em aberto

- [ ] Gravity Rush: um toque em 0,46/0,48 no quadro 1000 da tela de título não registrou (o toque no centro funciona)
- [ ] Killzone: depois da correção dos blocos, cada tiro clareia a cena por 1–3 quadros e às vezes deixa a tela inteira com um brilho forte (a luz do disparo agora é aplicada); confirmar a intensidade com o Vita real
- [ ] Killzone: congelamento raro com comandos da GPU pendentes (visto uma vez no quadro 1275)
- [ ] Killzone: em algumas áreas do kzl_walk3 o jogo cai para 5–10 fps por 20–40 s; a thread principal fica em polling com `sceKernelDelayThread` (loop em 8100D692) esperando algo que ninguém produz (jobs e GPU ociosos); ver docs/notes/kz-mainthread.md
- [ ] Skullgirls: elementos do HUD (contador de combo, barra de cima) aparecem como caixas brancas por 1 quadro durante as lutas
- [ ] WRC 4: com o save copiado, o jogo fica parado em "Please wait" (já acontecia antes das correções)
- [ ] MSAA real: leitura da imagem pelo próprio desenho (input attachment) é indefinida quando triângulos do mesmo desenho se sobrepõem (sem `VK_EXT_rasterization_order_attachment_access` na GTX 1650/Iris Xe); causa diferenças de ±1 entre execuções
- [ ] Sonic Mania: ~18 estalos por segundo na saída de música (44,1 kHz); pode ser do próprio conteúdo, não investigado
- [ ] ATRAC9: a saída do nosso decodificador tem a polaridade invertida em relação ao FFmpeg (inaudível; confirmar qual está certa)
- [ ] Cenário `rbs_boot.txt` do Resistance não serve mais com o save existente (usar `rbs_continue.txt`)
- [ ] Killzone: commit 3dc7d41 (sincronização U4U4U4U4) segurado porque deixava o rodapé do EULA em itálico
- [ ] Persona 4: digitar o nome no começo do jogo exige o teclado na tela (IME); os testes automáticos só conseguem digitar "A"
- [ ] Ninja Gaiden Sigma Plus: travamento intermitente no "Now Loading" do boot (antes dos logos), visto 1 vez em 3 execuções do `ngs_boot.txt` com dados novos; não depende da conta PSN (as outras execuções, com `--psn=on` e `--psn=off`, chegaram ao jogo)
- [ ] `sceNpManagerGetCachedParam`: layout não confirmado, por isso responde "sem conta" mesmo com a conta PSN emulada conectada (o UnityNpToolkit do 99Vidas chama e segue em frente)
