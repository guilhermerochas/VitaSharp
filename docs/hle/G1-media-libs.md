# G1: bibliotecas de mídia sem implementação (P2)

Especificações HLE das bibliotecas de mídia e de entrada de texto que o VitaSharp ainda não implementa (nenhuma
função `[Hle]`). Siga o template e as convenções de [README.md](README.md). Nenhum dos 7 jogos instalados importa
estas bibliotecas hoje: a meta é deixar tudo pesquisado para quando um jogo importar (`VitaSharp.Desktop imports
<TITLEID>` + logs `Unimplemented HLE function`).

Fontes: vita-headers @ `e66ebe9` (`include/psp2/*.h` e `db/360`), Vita3K @ `7059dc2` (`vita3k/modules/<Lib>/`,
`vita3k/codec/`, `vita3k/module/src/load_module.cpp`) e o código atual do VitaSharp. Protótipos sem header estão
marcados **(inferido)**; valores sem fonte estão marcados **a confirmar**, com o modo de confirmar.

## Escopo e contagem

| Biblioteca | Funções | Header | Arquivo HLE proposto |
| --- | ---: | --- | --- |
| SceAtrac | 24 | `psp2/atrac.h` | `Hle/Modules/Atrac.cs` (+ `Hle/Audio/At9Container.cs`) |
| SceSas | 33 | nenhum (inferido do PSP) | `Hle/Modules/Sas.cs` + `Hle/Audio/SasEngine.cs` |
| ScePvf | 43 | `psp2/pvf.h` (10 inferidas) | `Hle/Modules/Pvf.cs` |
| ScePgf | 23 | `psp2/pgf.h` | `Hle/Modules/Pgf.cs` |
| SceJpegUser | 11 | `psp2/jpeg.h` | `Hle/Modules/Jpeg.cs` + `Media/JpegDecoder.cs`, `Media/JpegCommon.cs`, `Media/YuvConvert.cs` |
| SceJpegEncUser | 10 | `psp2/jpegenc.h`, `psp2common/jpegenc.h` | `Hle/Modules/JpegEnc.cs` |
| SceIme | 6 | `psp2/libime.h` | `Hle/Modules/Ime.cs` (+ `ITextInputBackend` em `Host/HostServices.cs`) |
| SceVoice | 28 | `psp2/voice.h` | `Hle/Modules/Voice.cs` |
| SceVoiceQoS | 17 | nenhum (inferido) | `Hle/Modules/VoiceQoS.cs` |
| SceAudioencUser | 14 | `psp2/audioenc.h` (6 inferidas) | `Hle/Modules/Audioenc.cs` |
| SceG729 | 8 | nenhum (inferido); fora do `nids.txt` → `[Hle(Nid = ...)]` | `Hle/Modules/G729.cs` |
| SceJpegArm | 6 | `psp2/jpegarm.h` | `Hle/Modules/Jpeg.cs` (mesma classe) |
| SceJpegEncArm | 8 | `psp2/jpegencarm.h` | `Hle/Modules/JpegEnc.cs` (mesma classe) |
| SceVideoencUser | 21 | nenhum (inferido) | `Hle/Modules/Videoenc.cs` |
| SceLibMp4Recorder | 10 | `psp2/mp4rec.h` | `Hle/Modules/Mp4Recorder.cs` |
| SceHandwriting | 11 | nenhum (inferido; sem fonte pública) | LLE do firmware ou `Hle/Modules/Handwriting.cs` (stub) |
| **Total** | **273** | | |

## Ordem sugerida de implementação

1. **SceAtrac:** é a API padrão do SDK para BGM e voz `.at9` com streaming, loop e seek. ATRAC9 é o formato de áudio
   comprimido mais comum em jogos de Vita, e o decoder já existe (`Hle/Audio/Atrac9/`, usado por `Audiodec.cs`), então
   o custo é baixo.
2. **SceSas:** o mixer ADPCM/VAG herdado do PSP aparece em ports de PSP e em jogos menores (os AAA usam NGS). O
   decode HEVAG já existe em `Ngs.cs`. Vem depois de Atrac porque não tem header e a ABI precisa ser confirmada.
3. **ScePvf, depois ScePgf:** texto com as fontes do sistema. Pvf é a API nativa do Vita; Pgf aparece sobretudo em
   ports de PSP. Muitos jogos usam fontes próprias, mas se um jogo usar uma destas e ela faltar, o texto some.
4. **SceJpegUser, depois SceJpegEncUser:** decodificar fotos e imagens e salvar screenshots (junto com
   ScePhotoExport). Usado por poucos jogos e de forma acessória.
5. **SceIme:** teclado embutido, para nomes de perfil e chat. A maioria dos jogos usa o `sceImeDialog` (ver
   [D-dialogs-app.md](D-dialogs-app.md)).
6. **SceVoice → SceVoiceQoS → SceAudioencUser → SceG729:** chat de voz e gravação, recursos opcionais e quase sempre
   online. Sem AudioIn e sem codec CELP no host, o ganho máximo é o jogo não travar ao abrir o chat.
7. **SceJpegArm / SceJpegEncArm:** variantes por software (ARM) das APIs de Jpeg, raras em jogos. Ficam baratas
   depois do item 4.
8. **SceVideoencUser, SceLibMp4Recorder:** gravação de vídeo. O resultado nunca volta ao jogo, então um stub
   funcional basta.
9. **SceHandwriting:** não há protótipos públicos. Só vale a pena com um jogo concreto, de preferência via LLE.

## Estratégia transversal: LLE dos módulos do firmware

O Vita3K carrega por LLE, por padrão, `libsas`, `libpgf`, `libatrac`, `libpvf` e `libSceFt2`
(`auto_lle_modules` em `module/src/load_module.cpp`), a partir de `vs0:sys/external/*.suprx`. No VitaSharp,
`sceSysmoduleLoadModule` (`Hle/Modules/SystemMisc.cs`) só marca o ID como carregado, mas `Emu.LoadModule(path)` já
existe e é usado por `sceKernelLoadStartModule`. Um mecanismo genérico "ID de sysmodule → lista de `.suprx` → LLE
quando o arquivo existir em `FsRoot/vs0/sys/external/`, senão HLE" resolve Sas, Pgf, Pvf e Handwriting sem precisar
confirmar ABIs. Para Atrac, exige também `sceAudiodecPartlyDecode`. Hoje `fs/vs0` e `fs/sa0` do VitaSharp estão
vazios. Há um dump de firmware em `%APPDATA%\Vita3K\Vita3K`, com `vs0/sys/external` e `sa0/data/font`, que serve
para testes locais. O HLE descrito abaixo continua sendo o caminho para quem não tem firmware.

## SceAtrac (24 funções)

### Arquitetura (SceAtrac)

- **Objetivo:** libatrac é uma camada de conveniência sobre o SceAudiodec para tocar arquivos `.at9` (RIFF/WAVE com ATRAC9)
  inteiros ou em streaming, com loop, seek e buffer em anel gerenciado pela biblioteca. O jogo entrega o começo do
  arquivo num buffer alinhado, recebe um handle e chama `sceAtracDecode` a cada grão de áudio; a PCM sai em S16
  intercalado e o jogo a envia por `sceAudioOutOutput` (ou a injeta no NGS como PCM). Não há saída de áudio própria.
- **Módulo de sistema:** `SCE_SYSMODULE_ATRAC = 0x0030` (`psp2/sysmodule.h`), arquivo `vs0:sys/external/libatrac.suprx`.
- **LLE x HLE (decisão):**
  - *Opção A (principal): HLE*, descrita abaixo. O VitaSharp não exige dump de firmware (o `fs/vs0` do usuário está
    vazio hoje), então o LLE não está disponível na instalação típica.
  - *Opção B: LLE.* O Vita3K carrega `libatrac` por LLE por padrão (`module/src/load_module.cpp`, `auto_lle_modules`).
    A libatrac real chama o SceAudiodec, que o VitaSharp já implementa (`Hle/Modules/Audiodec.cs`), **exceto**
    `sceAudiodecPartlyDecode(SceAudiodecCtrl *ctrl, SceUInt32 samplesOffset, SceUInt32 samplesToDecode)`, que o Vita3K
    diz ser "only called by libatrac" (decodifica superframes inteiros e copia só `[offset, offset+toDecode)` amostras
    para `pPcm + offset*bytesPorAmostra`). Para LLE: implementar essa função em `Audiodec.cs` e, em
    `sceSysmoduleLoadModule` (`Hle/Modules/SystemMisc.cs`, hoje só HLE), chamar `Emu.LoadModule("vs0:sys/external/libatrac.suprx")`
    quando o arquivo existir (o mesmo caminho usado por `sceKernelLoadStartModule`). Se o LLE for adotado, o HLE abaixo
    fica como fallback quando o `.suprx` não existe.
- **Arquivo novo:** `src/VitaSharp.Core/Hle/Modules/Atrac.cs` (`public sealed class Atrac : HleModule`), mais um parser
  de contêiner `src/VitaSharp.Core/Hle/Audio/At9Container.cs` (RIFF → config, totais, loop, offset de dados).
- **Decoder:** `Hle/Audio/Atrac9/Atrac9Decoder` (o mesmo de `Audiodec.cs` e do NGS). Uso: `Initialize(byte[4] config)`,
  `Config.{ChannelCount, SampleRate, FrameSamples, FramesPerSuperframe, SuperframeBytes, SuperframeSamples}`,
  `DecodeFrame(byte[] superframeStaging, int frameIndex, short[][] pcmOut)` → bytes consumidos. Decodificar sempre por
  superframe (o decoder é stateful por frame dentro do superframe; seek só cai em fronteira de superframe).
- **Estado interno (por handle):**
  - `Buffer` (endereço guest), `BufferSize`, `IsAllOnMemory` (o arquivo inteiro coube em `readSize`), `FileSize`
    (do RIFF: `8 + riffSize`), `DataOffset`/`DataSize` (chunk `data`).
  - Anel de streaming: `ReadPos` (offset no arquivo do próximo byte que o jogo deve ler), `WritePtr` (onde o jogo deve
    escrever), `Filled` (bytes válidos no buffer ainda não consumidos), `ConsumePtr` (próximo superframe no buffer).
  - Contêiner: `Config` (4 bytes), `Channels`, `SampleRate`, `TotalSamples` (fact), `EncoderDelay` (fact, amostras a
    descartar no início), `LoopStart`/`LoopEnd` (smpl, -1 se ausentes), `BitRate`.
  - Reprodução: `NextSample` (posição de saída em amostras, já sem delay), `OutputSamples` (por chamada; padrão =
    `FrameSamples`, a confirmar), `LoopNum` (0 = sem loop, -1 = infinito, N = repetições restantes), `Status` (bits de
    `SceAtracDecoderStatus`), `SubBuffer`/`SubBufferSize`, `InternalError`.
  - PCM pendente: o decoder produz superframes de `SuperframeSamples`; guardar o excedente num `short[]` e entregar
    `OutputSamples` por `Decode`.
- **Estado global:** decoder group por tipo (só `0x2003` existe): `Created` (bool), `TotalCh` alocado, `UsedCh`
  (soma de canais dos handles vivos); `InitAudiodec` (se o group chamou `sceAudiodecInitLibrary`). Tabela de handles
  `Dictionary<int, AtracHandle>` com lock.
- **Formato do handle:** `(SCE_ATRAC_TYPE_AT9 << 16) | índice` — o Vita3K valida `sceAtracReleaseHandle` com
  `(handle >> 16) != 0x2003 → INVALID_HANDLE`. Índice 0-based ou 1-based: a confirmar (desmontar libatrac ou logar o
  retorno no Vita3K com LLE).
- **Contêiner `.at9` (RIFF WAVE, WAVEFORMATEXTENSIBLE):**

  | Chunk | Offset no corpo | Campo |
  | --- | --- | --- |
  | `fmt ` (tamanho 0x34) | 0x00 u16 | `wFormatTag = 0xFFFE` |
  | | 0x02 u16 | canais |
  | | 0x04 u32 | sample rate |
  | | 0x08 u32 | avgBytesPerSec |
  | | 0x0C u16 | blockAlign (= bytes do superframe) |
  | | 0x0E u16 | bitsPerSample (0) |
  | | 0x10 u16 | cbSize (0x22) |
  | | 0x12 u16 | samplesPerBlock (= amostras do superframe) |
  | | 0x14 u32 | channelMask |
  | | 0x18 16B | SubFormat GUID `47E142D2-36BA-4D8D-88FC-61654F8C836C` (ATRAC9) |
  | | 0x28 u32 | versionInfo |
  | | 0x2C 4B | **configData** (passar a `Atrac9Decoder.Initialize`) |
  | | 0x30 u32 | reservado |
  | `fact` (8 ou 12) | 0x00 u32 | total de amostras (sem delay) |
  | | 0x04 u32 | encoder delay (amostras a descartar no início) |
  | `smpl` (opcional) | 0x24+ | lista de loops de 24 bytes: `cueId, type, start, end, fraction, playCount` |
  | `data` | — | superframes contíguos |

  Offsets do `fmt` são o padrão WAVEFORMATEXTENSIBLE; a semântica exata de `fact` (se `start/end` do `smpl` incluem o
  delay) está a confirmar com o vgmstream (`riff.c`, ramo ATRAC9) e com um `.at9` real comparando `GetContentInfo` no
  Vita3K LLE. O `Ngs.cs` já pula RIFF procurando o chunk `data` (linha ~974): reaproveitar a lógica no `At9Container`.
- **Dependências:** `Audiodec` (se `initAudiodecFlag != 0` em `CreateDecoderGroup`, chamar a mesma lógica de
  `sceAudiodecInitLibrary(0x1003, …)`; expor um método interno em `Audiodec` ou acessar o módulo via `Emu`), `GuestMemory`.
- **Custo:** nenhuma função bloqueia; `sceAtracDecode` é CPU-bound curto (fica na faixa padrão de `ClassifyCost`).

Códigos de erro (`psp2/atrac.h`):

| Nome | Valor |
| --- | --- |
| SCE_ATRAC_ERROR_INVALID_POINTER | 0x80630000 |
| SCE_ATRAC_ERROR_INVALID_SIZE | 0x80630001 |
| SCE_ATRAC_ERROR_INVALID_WORD_LENGTH | 0x80630002 |
| SCE_ATRAC_ERROR_INVALID_TYPE | 0x80630003 |
| SCE_ATRAC_ERROR_INVALID_TOTAL_CH | 0x80630004 |
| SCE_ATRAC_ERROR_INVALID_ALIGNMENT | 0x80630005 |
| SCE_ATRAC_ERROR_ALREADY_CREATED | 0x80630006 |
| SCE_ATRAC_ERROR_NOT_CREATED | 0x80630007 |
| SCE_ATRAC_ERROR_SHORTAGE_OF_CH | 0x80630008 |
| SCE_ATRAC_ERROR_UNSUPPORTED_DATA | 0x80630009 |
| SCE_ATRAC_ERROR_INVALID_DATA | 0x8063000A |
| SCE_ATRAC_ERROR_READ_SIZE_IS_TOO_SMALL | 0x8063000B |
| SCE_ATRAC_ERROR_INVALID_HANDLE | 0x8063000C |
| SCE_ATRAC_ERROR_READ_SIZE_OVER_BUFFER | 0x8063000D |
| SCE_ATRAC_ERROR_MAIN_BUFFER_SIZE_IS_TOO_SMALL | 0x8063000E |
| SCE_ATRAC_ERROR_SUB_BUFFER_SIZE_IS_TOO_SMALL | 0x8063000F |
| SCE_ATRAC_ERROR_DATA_SHORTAGE_IN_BUFFER | 0x80630010 |
| SCE_ATRAC_ERROR_ALL_DATA_WAS_DECODED | 0x80630011 |
| SCE_ATRAC_ERROR_INVALID_MAX_OUTPUT_SAMPLES | 0x80630012 |
| SCE_ATRAC_ERROR_ADDED_DATA_IS_TOO_BIG | 0x80630013 |
| SCE_ATRAC_ERROR_NEED_SUB_BUFFER | 0x80630014 |
| SCE_ATRAC_ERROR_INVALID_SAMPLE | 0x80630015 |
| SCE_ATRAC_ERROR_NO_NEED_SUB_BUFFER | 0x80630016 |
| SCE_ATRAC_ERROR_INVALID_LOOP_STATUS | 0x80630017 |
| SCE_ATRAC_ERROR_REMAIN_VALID_HANDLE | 0x80630018 |
| SCE_ATRAC_ERROR_INVALID_LOOP_NUM | 0x80630030 |

Constantes (`psp2/atrac.h` / `psp2/audiodec.h`): `SCE_ATRAC_TYPE_AT9 = 0x2003`, `SCE_ATRAC_ALIGNMENT_SIZE = 0x100`,
`SCE_ATRAC_WORD_LENGTH_16BITS = 16`, `SCE_ATRAC_AT9_MAX_TOTAL_CH = 16`, `SCE_ATRAC_AT9_MAX_CH_IN_DECODER = 2`,
`SCE_ATRAC_AT9_MAX_FRAME_SAMPLES = 256`, `SCE_ATRAC_MAX_OUTPUT_SAMPLES = 2048`, `SCE_ATRAC_AT9_MAX_OUTPUT_FRAMES = 8`,
`SCE_ATRAC_AT9_MIN_LOOP_SAMPLES = 3072`, `SCE_ATRAC_INFINITE_LOOP_NUM = -1`, `SCE_ATRAC_INFINITE_SAMPLES = -1`.
Status: `ALL_DATA_WAS_DECODED = 0x1`, `ALL_DATA_IS_ON_MEMORY = 0x2`, `NONLOOP_PART_IS_ON_MEMORY = 0x4`,
`LOOP_PART_IS_ON_MEMORY = 0x8`. Loop status: `NON_RESETABLE_PART = 0`, `RESETABLE_PART = 1`.

Estruturas comuns:

| Struct | Tamanho | Layout |
| --- | --- | --- |
| `SceAtracDecoderGroup` | 0x0C | 0x00 `size` (=0xC), 0x04 `wordLength` (=16), 0x08 `totalCh` |
| `SceAtracContentInfo` | 0x34 | 0x00 `size`, 0x04 `atracType`, 0x08 `channel`, 0x0C `samplingRate`, 0x10 `endSample` (s32), 0x14 `loopStartSample` (s32), 0x18 `loopEndSample` (s32), 0x1C `bitRate`, 0x20 `fixedEncBlockSize`, 0x24 `fixedEncBlockSample`, 0x28 `frameSample`, 0x2C `loopBlockOffset`, 0x30 `loopBlockSize` |
| `SceAtracStreamInfo` | 0x10 | 0x00 `size` (=0x10), 0x04 `pWritePosition` (ptr guest), 0x08 `readPosition`, 0x0C `writableSize` |

**Estado da implementação (HLE):** `Hle/Modules/Atrac.cs` + `Hle/Audio/At9Container.cs` (parser) +
`Hle/Audio/AtracStream.cs` (estado por handle, testável sem guest: o decoder entra por `IAt9SuperframeDecoder`).
`sceSysmoduleLoadModule(0x0030)` tenta antes `libatrac.suprx` por LLE (`app0:sce_module` ou `vs0:sys/external`,
tabela `LleSysmodules` em `SystemMisc.cs`); o HLE fica como fallback. Decisões tomadas onde a doc dizia "a confirmar"
(todas continuam sem verificação em hardware/LLE):
- Handle = `0x2003 << 16 | slot`, slot 0-based (o menor livre).
- Os pontos do `smpl` são lidos como incluindo o encoder delay (como AT3 no PSP) e expostos sem ele; `loopEnd` e
  `endSample` são inclusivos (`endSample = TotalSamples - 1`). `loopBlockOffset` = offset no arquivo do superframe
  que contém o início do loop; `loopBlockSize` = bytes desse superframe até o fim do superframe do fim do loop.
- `OutputSamples` padrão = `FrameSamples`; `SetOutputSamples` exige múltiplo de `FrameSamples`, ≤ 8 frames e ≤ 2048.
- `Decode` para no ponto de loop e no fim (pode devolver menos amostras); depois do fim retorna `ALL_DATA_WAS_DECODED`.
  Ao pular de forma não sequencial com tudo na memória (seek/loop), o decoder é reiniciado e "aquecido" com o
  superframe anterior. Erro do decoder → `InternalError = 0x807F0000` e retorno `0x807F0000`.
- Streaming: o anel recebe os superframes na ordem de reprodução — ao chegar ao fim do bloco de loop com loops
  restantes, `readPosition` volta para o bloco de início do loop. Por isso o sub-buffer nunca é necessário:
  `IsSubBufferNeeded` = 0, `GetSubBufferInfo`/`SetSubBuffer` = `NO_NEED_SUB_BUFFER`. `SetLoopNum` depois de o jogo
  já ter lido além do fim do loop descarta os bytes excedentes do anel e rebobina o leitor. `ResetNextOutputPosition`
  em streaming esvazia o anel e pede a leitura a partir de um superframe antes do alvo (aquecimento).
- `SetLoopNum(≠0)` num arquivo sem loop → `INVALID_LOOP_STATUS`. Status: tudo na memória = `0x2|0x4` (+`0x8` com
  loop); streaming liga `0x4` quando o leitor chegou ao fim dos dados. `MAIN_BUFFER_SIZE_IS_TOO_SMALL` em streaming
  quando `bufferSize < dataOffset + superframe`.

Validação geral: não há jogo instalado que importe SceAtrac. Montar um homebrew de teste com VitaSDK (`-lSceAtrac_stub`)
que carrega um `.at9` de `app0:`, toca em modo "tudo na memória" e em streaming com buffer pequeno, e compara a PCM
(hash) com o mesmo arquivo decodificado pelo `Audiodec` frame a frame. Cruzar os valores de `GetContentInfo` e dos
getters com o Vita3K rodando `libatrac` por LLE.

### sceAtracQueryDecoderGroupMemSize
- [x] **Prioridade:** P2 · **Biblioteca:** SceAtrac · **NID:** 0x1A34B331
- **Protótipo:** `int sceAtracQueryDecoderGroupMemSize(SceUInt32 atracType, const SceAtracDecoderGroup *pDecoderGroup);` (`psp2/atrac.h`)
- **Comportamento:** retorna (em r0, positivo) o tamanho de memória de trabalho que o jogo deve alocar e passar a
  `sceAtracCreateDecoderGroup` para `totalCh` canais.
- **Retorno/erros:** tamanho em bytes; `INVALID_POINTER` (grupo nulo), `INVALID_SIZE` (`size != 0xC`),
  `INVALID_WORD_LENGTH` (`wordLength != 16`), `INVALID_TYPE` (`atracType != 0x2003`), `INVALID_TOTAL_CH`
  (`totalCh == 0 || > 16`). Ordem das checagens igual à do Vita3K.
- **Estruturas:** `SceAtracDecoderGroup`.
- **Implementação no VitaSharp:** `Atrac.cs`. O Vita3K devolve `totalCh << 10` (palpite dele); como o HLE não usa a
  memória, qualquer valor ≥ isso funciona. Valor real a confirmar (LLE no Vita3K ou desmontagem). Arredondar para
  múltiplo de 0x100.
- **Validação:** homebrew de teste imprime o valor para totalCh 1, 2, 16.

### sceAtracCreateDecoderGroup
- [x] **Prioridade:** P2 · **Biblioteca:** SceAtrac · **NID:** 0x01BFB98C
- **Protótipo:** `int sceAtracCreateDecoderGroup(SceUInt32 atracType, const SceAtracDecoderGroup *pDecoderGroup, void *pvWorkMem, int initAudiodecFlag);` (`psp2/atrac.h`)
- **Comportamento:** cria o grupo de decoders (reserva `totalCh` canais de hardware). Se `initAudiodecFlag != 0`,
  inicializa a biblioteca SceAudiodec para AT9 com `totalCh` (equivalente a `sceAudiodecInitLibrary(0x1003, {size, totalCh})`).
- **Retorno/erros:** 0; `INVALID_POINTER` (grupo ou `pvWorkMem` nulos), `INVALID_SIZE`, `INVALID_WORD_LENGTH`,
  `INVALID_ALIGNMENT` (`pvWorkMem & 0xFF`), `INVALID_TYPE`, `INVALID_TOTAL_CH`, `ALREADY_CREATED` (grupo já existe),
  erros propagados do `sceAudiodecInitLibrary`.
- **Estruturas:** `SceAtracDecoderGroup`.
- **Implementação no VitaSharp:** `Atrac.cs`; guarda `TotalCh`, `InitAudiodec = flag != 0`; registra o tipo AT9 no
  `HashSet` `_libraries` de `Audiodec` (expor `internal int InitLibraryInternal(uint type)` ou chamar o método via
  instância do módulo).
- **Validação:** criar duas vezes deve dar `ALREADY_CREATED`.

### sceAtracDeleteDecoderGroup
- [x] **Prioridade:** P2 · **Biblioteca:** SceAtrac · **NID:** 0x6CA2F08A
- **Protótipo:** `int sceAtracDeleteDecoderGroup(SceUInt32 atracType, int termAudiodecFlag);` (`psp2/atrac.h`)
- **Comportamento:** destrói o grupo; se `termAudiodecFlag`, termina a biblioteca SceAudiodec AT9.
- **Retorno/erros:** 0; `INVALID_TYPE`; `NOT_CREATED` (sem grupo); `REMAIN_VALID_HANDLE` (há handles não liberados);
  erro de `sceAudiodecTermLibrary`.
- **Estruturas:** —
- **Implementação no VitaSharp:** `Atrac.cs`; checar `_handles.Count == 0` antes de liberar.
- **Validação:** deletar com handle vivo → `0x80630018`.

### sceAtracGetDecoderGroupInfo
- [x] **Prioridade:** P2 · **Biblioteca:** SceAtrac · **NID:** 0xBF34605D
- **Protótipo:** `int sceAtracGetDecoderGroupInfo(SceUInt32 atracType, SceAtracDecoderGroup *pCreatedDecoder, SceAtracDecoderGroup *pAvailableDecoder);` (`psp2/atrac.h`)
- **Comportamento:** preenche `pCreatedDecoder` com o grupo criado (`wordLength`, `totalCh`) e `pAvailableDecoder` com
  o que ainda está livre (`totalCh - canais em uso`).
- **Retorno/erros:** 0; `INVALID_POINTER` (ponteiros nulos **ou** `size != 0xC` — o Vita3K usa INVALID_POINTER também
  para size errado; a confirmar se é `INVALID_SIZE`), `INVALID_TYPE`, `NOT_CREATED`.
- **Estruturas:** `SceAtracDecoderGroup` ×2 (o jogo preenche `size`).
- **Implementação no VitaSharp:** `Atrac.cs`; escrever `wordLength = 16`.
- **Validação:** após acquire de um stereo, `available.totalCh` cai 2.

### sceAtracSetDataAndAcquireHandle
- [x] **Prioridade:** P2 · **Biblioteca:** SceAtrac · **NID:** 0xD1997678
- **Protótipo:** `int sceAtracSetDataAndAcquireHandle(SceUChar8 *pucBuffer, SceUInt32 uiReadSize, SceUInt32 uiBufferSize);` (`psp2/atrac.h`)
- **Comportamento:** analisa o cabeçalho RIFF no início de `pucBuffer` (`uiReadSize` bytes válidos do arquivo, buffer
  total `uiBufferSize`), reserva canais no grupo e devolve um handle. Se `uiReadSize >= FileSize` → status
  `ALL_DATA_IS_ON_MEMORY` (sem streaming). Senão o buffer vira anel: `ReadPos = uiReadSize`, dados a partir de
  `DataOffset` estão no buffer. O status inicial também liga `NONLOOP_PART_IS_ON_MEMORY`/`LOOP_PART_IS_ON_MEMORY`
  conforme os offsets de loop estejam cobertos.
- **Retorno/erros:** handle (positivo, `0x2003xxxx`); `INVALID_POINTER`; `INVALID_ALIGNMENT` (buffer ou tamanho não
  múltiplos de 0x100); `READ_SIZE_OVER_BUFFER` (`readSize > bufferSize`); `NOT_CREATED`; `READ_SIZE_IS_TOO_SMALL`
  (cabeçalho até o início de `data` não coube em `readSize`); `INVALID_DATA` (não é RIFF/WAVE ou falta `fmt`/`fact`/`data`);
  `UNSUPPORTED_DATA` (GUID não ATRAC9, canais > 2); `MAIN_BUFFER_SIZE_IS_TOO_SMALL` (buffer menor que cabeçalho +
  um superframe; limite exato a confirmar); `SHORTAGE_OF_CH` (canais insuficientes no grupo).
- **Estruturas:** contêiner `.at9` descrito na arquitetura.
- **Implementação no VitaSharp:** `Atrac.cs` + `At9Container.Parse(ReadOnlySpan<byte>)`; criar `Atrac9Decoder` com o
  `configData`; `OutputSamples = Config.FrameSamples` (padrão a confirmar; pode ser o superframe).
- **Validação:** `.at9` inteiro e cabeçalho truncado (`readSize` pequeno → `0x8063000B`).

### sceAtracReleaseHandle
- [x] **Prioridade:** P2 · **Biblioteca:** SceAtrac · **NID:** 0x008EF251
- **Protótipo:** `int sceAtracReleaseHandle(int atracHandle);` (`psp2/atrac.h`)
- **Comportamento:** libera o handle e devolve seus canais ao grupo.
- **Retorno/erros:** 0; `INVALID_HANDLE` (`handle >> 16 != 0x2003` ou índice desconhecido).
- **Estruturas:** —
- **Implementação no VitaSharp:** `Atrac.cs`; `_handles.Remove`.
- **Validação:** liberar duas vezes → `0x8063000C`.

### sceAtracDecode
- [x] **Prioridade:** P2 · **Biblioteca:** SceAtrac · **NID:** 0x29C74EE3
- **Protótipo:** `int sceAtracDecode(int atracHandle, void *pOutputBuffer, SceUInt32 *pOutputSamples, SceUInt32 *pDecoderStatus);` (`psp2/atrac.h`)
- **Comportamento:** decodifica até `OutputSamples` amostras (por canal) a partir de `NextSample` para
  `pOutputBuffer` (S16 intercalado). Descarta `EncoderDelay` amostras no início do arquivo. Ao alcançar `LoopEnd` com
  `LoopNum != 0`, volta para `LoopStart` (decrementa `LoopNum` se > 0) — a última chamada antes do loop pode retornar
  menos amostras. Ao alcançar o fim sem loop, retorna as amostras finais e liga `ALL_DATA_WAS_DECODED`. Em streaming,
  consome bytes do anel (diminui `Filled`, avança `ConsumePtr`, aumenta o espaço vago).
- **Retorno/erros:** 0; `*pOutputSamples` = amostras escritas; `*pDecoderStatus` = status atual. Erros:
  `INVALID_POINTER` (qualquer ponteiro nulo), `INVALID_ALIGNMENT` (`pOutputBuffer & 1`), `INVALID_HANDLE`,
  `ALL_DATA_WAS_DECODED` (chamada após o fim), `DATA_SHORTAGE_IN_BUFFER` (streaming sem superframe completo no anel),
  `NEED_SUB_BUFFER` (loop exige sub-buffer não configurado).
- **Estruturas:** —
- **Implementação no VitaSharp:** `Atrac.cs`; laço: enquanto o PCM pendente < pedido, copiar `SuperframeBytes` do anel
  (tratando wrap) para `Staging`, chamar `DecodeFrame` `FramesPerSuperframe` vezes, intercalar canais como em
  `Audiodec.DecodeFrames`. Em erro do decoder, gravar `InternalError` e retornar `ERR` genérico (a confirmar qual; o
  Audiodec usa `0x807F0000`).
- **Validação:** hash da PCM contra decodificação via `Audiodec`; arquivo com loop deve repetir a partir de
  `loopStartSample` exatamente (comparar amostra a amostra).

### sceAtracGetStreamInfo
- [x] **Prioridade:** P2 · **Biblioteca:** SceAtrac · **NID:** 0xD9409E57
- **Protótipo:** `int sceAtracGetStreamInfo(int atracHandle, SceAtracStreamInfo *pStreamInfo);` (`psp2/atrac.h`)
- **Comportamento:** informa onde o jogo deve escrever mais dados: `pWritePosition` (endereço no anel), `readPosition`
  (offset no arquivo a ler), `writableSize` (bytes contíguos que cabem até o fim do anel ou até o dado ainda não
  consumido). Quando tudo está na memória, `writableSize = 0`.
- **Retorno/erros:** 0; `INVALID_POINTER`, `INVALID_SIZE` (`size != 0x10`), `INVALID_HANDLE`.
- **Estruturas:** `SceAtracStreamInfo`.
- **Implementação no VitaSharp:** `Atrac.cs`; `writableSize` limitado também pelo restante do arquivo (`FileSize - ReadPos`)
  e, com loop ativo, por `LoopEnd` (após o qual `readPosition` volta ao bloco de loop — a confirmar).
- **Validação:** streaming com buffer de 0x4000 toca um arquivo de 1 MB sem erro.

### sceAtracAddStreamData
- [x] **Prioridade:** P2 · **Biblioteca:** SceAtrac · **NID:** 0x53656F1C
- **Protótipo:** `int sceAtracAddStreamData(int atracHandle, SceUInt32 addSize);` (`psp2/atrac.h`)
- **Comportamento:** o jogo informa que escreveu `addSize` bytes em `pWritePosition`; avança `WritePtr`, `ReadPos`,
  `Filled`; atualiza bits `*_IS_ON_MEMORY`.
- **Retorno/erros:** 0; `INVALID_HANDLE`; `ADDED_DATA_IS_TOO_BIG` (`addSize > writableSize`).
- **Estruturas:** —
- **Implementação no VitaSharp:** `Atrac.cs`.
- **Validação:** adicionar mais que o vago → `0x80630013`.

### sceAtracIsSubBufferNeeded
- [x] **Prioridade:** P2 · **Biblioteca:** SceAtrac · **NID:** 0x722F5004
- **Protótipo:** `int sceAtracIsSubBufferNeeded(int atracHandle);` (`psp2/atrac.h`)
- **Comportamento:** retorna 1 se o handle está em streaming com loop e o bloco que contém `LoopStart`
  (`loopBlockOffset/loopBlockSize`) precisa ser mantido num sub-buffer para recomeçar o loop sem reler o arquivo;
  0 caso contrário. Critério exato a confirmar (provável: streaming && há loop && o bloco de loop não está no anel).
- **Retorno/erros:** 0/1; `INVALID_HANDLE`.
- **Estruturas:** —
- **Implementação no VitaSharp:** `Atrac.cs`. Alternativa segura até confirmar: retornar 0 e, no `Decode`, ler o bloco
  de loop direto do anel quando disponível.
- **Validação:** arquivo com loop em streaming; comparar com Vita3K LLE.

### sceAtracGetSubBufferInfo
- [x] **Prioridade:** P2 · **Biblioteca:** SceAtrac · **NID:** 0xE7D9DC4F
- **Protótipo:** `int sceAtracGetSubBufferInfo(int atracHandle, SceUInt32 *pReadPosition, SceUInt32 *pMinSubBufferSize, SceUInt32 *pDataSize);` (`psp2/atrac.h`)
- **Comportamento:** informa o offset no arquivo (`readPosition`) e o tamanho (`dataSize`) do trecho que o jogo deve
  carregar no sub-buffer, e o tamanho mínimo do sub-buffer (`minSubBufferSize`, alinhado a 0x100).
- **Retorno/erros:** 0; `INVALID_POINTER`, `INVALID_HANDLE`, `NO_NEED_SUB_BUFFER` (quando não há necessidade).
- **Estruturas:** —
- **Implementação no VitaSharp:** `Atrac.cs`; `readPosition = DataOffset + loopBlockOffset`, `dataSize = loopBlockSize`,
  `min = ROUND_UP(dataSize)` (a confirmar).
- **Validação:** idem acima.

### sceAtracSetSubBuffer
- [x] **Prioridade:** P2 · **Biblioteca:** SceAtrac · **NID:** 0x272B4CBA
- **Protótipo:** `int sceAtracSetSubBuffer(int atracHandle, SceUChar8 *pSubBuffer, SceUInt32 subBufferSize);` (`psp2/atrac.h`)
- **Comportamento:** registra o sub-buffer já preenchido pelo jogo; o `Decode` usa esse bloco ao voltar ao loop.
- **Retorno/erros:** 0; `INVALID_ALIGNMENT` (ponteiro ou tamanho não múltiplos de 0x100 — checado antes de tudo no
  Vita3K), `INVALID_POINTER`, `INVALID_HANDLE`, `SUB_BUFFER_SIZE_IS_TOO_SMALL`, `NO_NEED_SUB_BUFFER`.
- **Estruturas:** —
- **Implementação no VitaSharp:** `Atrac.cs`.
- **Validação:** idem.

### sceAtracSetLoopNum
- [x] **Prioridade:** P2 · **Biblioteca:** SceAtrac · **NID:** 0x5C22E927
- **Protótipo:** `int sceAtracSetLoopNum(int atracHandle, int loopNum);` (`psp2/atrac.h`)
- **Comportamento:** define quantas vezes o trecho de loop repete (`-1` infinito, `0` sem loop).
- **Retorno/erros:** 0; `INVALID_LOOP_NUM` (`< -1`), `INVALID_HANDLE`; `INVALID_LOOP_STATUS` se o arquivo não tem
  loop e `loopNum != 0` (a confirmar) ou se já passou do ponto de loop (status `NON_RESETABLE_PART`, a confirmar).
- **Estruturas:** —
- **Implementação no VitaSharp:** `Atrac.cs`.
- **Validação:** loopNum=1 deve tocar o loop exatamente duas vezes.

### sceAtracSetOutputSamples
- [x] **Prioridade:** P2 · **Biblioteca:** SceAtrac · **NID:** 0x075B0C63
- **Protótipo:** `int sceAtracSetOutputSamples(int atracHandle, SceUInt32 outputSamples);` (`psp2/atrac.h`)
- **Comportamento:** define quantas amostras por canal cada `sceAtracDecode` entrega.
- **Retorno/erros:** 0; `INVALID_HANDLE`; `INVALID_MAX_OUTPUT_SAMPLES` se `0` ou `> 2048` ou (provável) não múltiplo
  de `frameSample` / mais de 8 frames (`SCE_ATRAC_AT9_MAX_OUTPUT_FRAMES`) — a confirmar.
- **Estruturas:** —
- **Implementação no VitaSharp:** `Atrac.cs`.
- **Validação:** valores 256, 1024, 2048, 2049.

### sceAtracResetNextOutputPosition
- [x] **Prioridade:** P2 · **Biblioteca:** SceAtrac · **NID:** 0xD81D7280
- **Protótipo:** `int sceAtracResetNextOutputPosition(int atracHandle, SceUInt32 resetSample);` (`psp2/atrac.h`)
- **Comportamento:** seek: a próxima saída começa em `resetSample`. Reposiciona o decoder no superframe que contém
  `resetSample` (+ delay), descarta as amostras iniciais do superframe e, em streaming, esvazia o anel e ajusta
  `ReadPos` para o offset do superframe (o jogo volta a ler via `GetStreamInfo`). Limpa `ALL_DATA_WAS_DECODED`.
- **Retorno/erros:** 0; `INVALID_HANDLE`; `INVALID_SAMPLE` (`resetSample > endSample`).
- **Estruturas:** —
- **Implementação no VitaSharp:** `Atrac.cs`; recriar o `Atrac9Decoder` (zerar estado) e decodificar o superframe
  anterior para "esquentar" o MDCT se houver clique (a confirmar se o hardware faz isso).
- **Validação:** seek para metade e comparar PCM com decodificação contínua (tolerância no primeiro superframe).

### sceAtracGetContentInfo
- [x] **Prioridade:** P2 · **Biblioteca:** SceAtrac · **NID:** 0x68DFC199
- **Protótipo:** `int sceAtracGetContentInfo(int atracHandle, SceAtracContentInfo *pContentInfo);` (`psp2/atrac.h`)
- **Comportamento:** preenche informações do arquivo: `atracType = 0x2003`, `channel`, `samplingRate`,
  `endSample` (última amostra, `TotalSamples - 1`, a confirmar), `loopStartSample`/`loopEndSample` (-1 sem loop),
  `bitRate` (kbps: `SuperframeBytes*8*SampleRate/SuperframeSamples/1000`, mesma fórmula de `Audiodec.cs`),
  `fixedEncBlockSize = SuperframeBytes`, `fixedEncBlockSample = SuperframeSamples`, `frameSample = FrameSamples`,
  `loopBlockOffset`/`loopBlockSize` (offset relativo ao arquivo e tamanho do trecho necessário para recomeçar o loop;
  semântica a confirmar).
- **Retorno/erros:** 0; `INVALID_POINTER`, `INVALID_SIZE` (`size != 0x34`), `INVALID_HANDLE`.
- **Estruturas:** `SceAtracContentInfo`.
- **Implementação no VitaSharp:** `Atrac.cs`.
- **Validação:** comparar com Vita3K LLE para 2–3 arquivos `.at9`.

### Getters simples de SceAtrac

Todos: `int f(int atracHandle, T *p)` em `psp2/atrac.h`; erros `INVALID_POINTER` (ponteiro nulo, checado primeiro no
Vita3K) e `INVALID_HANDLE`; implementação em `Atrac.cs` lendo o estado do handle. Validação: imprimir no homebrew de
teste ao longo da reprodução e comparar com Vita3K LLE.

| Função | NID | Protótipo | Comportamento |
| --- | --- | --- | --- |
| - [x] sceAtracGetLoopInfo | 0xDC768033 | `int sceAtracGetLoopInfo(int atracHandle, int *pLoopNum, SceUInt32 *pLoopStatus);` | `*pLoopNum` = loops restantes; `*pLoopStatus` = `RESETABLE_PART` (1) se ainda é possível mudar o loop (antes de `LoopEnd`), senão `NON_RESETABLE_PART` (0) — a confirmar. |
| - [x] sceAtracGetOutputSamples | 0x37B3F335 | `int sceAtracGetOutputSamples(int atracHandle, SceUInt32 *pOutputSamples);` | Devolve `OutputSamples` configurado. |
| - [x] sceAtracGetNextOutputPosition | 0xACA5F9CC | `int sceAtracGetNextOutputPosition(int atracHandle, SceUInt32 *pNextOutputSample);` | Devolve `NextSample`; após o fim, provavelmente retorna `ALL_DATA_WAS_DECODED` (a confirmar). |
| - [x] sceAtracGetRemainSamples | 0xDC27E7C2 | `int sceAtracGetRemainSamples(int atracHandle, SceLong64 *pRemainSamples);` | `SceLong64` (8 bytes, alinhado): amostras ainda a sair contando loops restantes; `-1` (`SCE_ATRAC_INFINITE_SAMPLES`) se `LoopNum == -1`. |
| - [x] sceAtracGetOutputableSamples | 0x5B36CDB5 | `int sceAtracGetOutputableSamples(int atracHandle, SceLong64 *pOutputableSamples);` | `SceLong64`: amostras decodificáveis com os dados já no buffer (superframes completos no anel × `SuperframeSamples` + PCM pendente); com tudo na memória, igual a `RemainSamples`. |
| - [x] sceAtracGetDecoderStatus | 0x90B512AC | `int sceAtracGetDecoderStatus(int atracHandle, SceUInt32 *pDecoderStatus);` | Devolve os bits `SceAtracDecoderStatus` atuais. |
| - [x] sceAtracGetVacantSize | 0x9BDB14F7 | `int sceAtracGetVacantSize(int atracHandle, SceUInt32 *pVacantSize);` | Espaço livre total no anel (`BufferSize - Filled`), sem considerar contiguidade; 0 se tudo na memória. |
| - [x] sceAtracGetInternalError | 0x67981334 | `int sceAtracGetInternalError(int atracHandle, int *pInternalError);` | Devolve o último erro do decoder (`InternalError`, 0 se nenhum), como `sceAudiodecGetInternalError`. |

## SceSas (33 funções)

### Arquitetura (SceSas)

- **Objetivo:** libsas é o mixer de vozes por software herdado do PSP (`sceSas*` do PSP, documentado no PPSSPP
  `Core/HLE/sceSas.cpp` + `Core/HW/SasAudio.cpp`). Até N vozes, cada uma tocando ADPCM VAG (ou PCM 16-bit, ou ruído),
  com pitch, envelope ADSR, volume por lado (dry/wet) e um reverb global. `sceSasCore` gera um grão de PCM estéreo
  S16 num buffer do jogo, que o jogo envia ao `sceAudioOutOutput`. Não toca áudio sozinho.
- **Módulo de sistema:** `SCE_SYSMODULE_SAS = 0x000D`; `vs0:sys/external/libsas.suprx` (módulo `SceSas`, NID
  0xBDF706EB; biblioteca `SceSas`, NID 0xEE47EC43, em `db/360/SceSas.yml` do vita-headers).
- **Diferenças para o PSP (importante):** o vita-headers **não tem header** para SceSas e o Vita3K só tem stubs
  `UNIMPLEMENTED()` sem assinatura (`modules/SceSas/SceSas.cpp`). Todos os protótipos abaixo são **inferidos**. No PSP,
  todas as funções recebem um `SasCore *` como primeiro argumento; no Vita a API é global (sem ponteiro de core) e a
  inicialização recebe uma *string de configuração* (`"numGrains=256 numVoices=32 numReverbs=1"`, lembrança do SDK
  oficial; chaves e defaults a confirmar). Como confirmar: desmontar `libsas.suprx` (Ghidra + vita-loader) ou
  rastrear chamadas num jogo que use Sas com o Vita3K em LLE (logar r0–r3 na entrada de cada export).
- **LLE x HLE (decisão):**
  - *Opção A (recomendada quando houver firmware): LLE.* O Vita3K carrega `libsas` por LLE e funciona
    (`auto_lle_modules` em `module/src/load_module.cpp`), o que indica que o módulo é ARM puro, sem syscalls de
    hardware de áudio. No VitaSharp: em `sceSysmoduleLoadModule(0x000D)` (`Hle/Modules/SystemMisc.cs`), se
    `vs0:sys/external/libsas.suprx` existir, `Emu.LoadModule(...)` como em `sceKernelLoadStartModule`. Isso elimina
    toda a incerteza de ABI listada abaixo.
  - *Opção B: HLE* (abaixo), necessária porque o VitaSharp roda sem dump de firmware (`fs/vs0` vazio hoje). Fazer só
    depois de confirmar as assinaturas (ver acima); até lá, a opção B também serve de documentação de semântica.
- **Arquivos novos:**
  - `src/VitaSharp.Core/Hle/Audio/SasEngine.cs`: vozes, ADSR, reamostragem, mix, reverb (sem dependência de HLE,
    testável por unidade).
  - `src/VitaSharp.Core/Hle/Audio/Adpcm.cs`: helper compartilhado. Extrair o corpo de `Ngs.DecodeHevag`
    (`Hle/Modules/Ngs.cs`, ~linha 868) para `Adpcm.DecodeHevagBlock(ReadOnlySpan<byte> block16, Span<int> hist4, Span<short> out28)`
    e adicionar `Adpcm.DecodeVagBlock(ReadOnlySpan<byte> block16, ref int h1, ref int h2, Span<short> out28)` (PS ADPCM
    clássico do PSP). `Ngs.DecodeHevag` passa a chamar o helper (sem mudar a saída; validar com os cenários do NGS).
  - `src/VitaSharp.Core/Hle/Modules/Sas.cs`: `public sealed class Sas : HleModule` com os `[Hle]`.
- **Formato VAG (PS ADPCM, PSP):** blocos de 16 bytes: byte 0 = `(predictor << 4) | shift`, byte 1 = flags
  (bit0 = loop end, bit1 = loop/repeat, bit2 = loop start; `flag == 7` = fim do stream), bytes 2–15 = 28 nibbles
  (nibble baixo primeiro). Amostra = `(sext(nibble) << 12) >> shift` + `(h1*c0 + h2*c1) >> 6`, com
  `c = {0,0}, {60,0}, {115,-52}, {98,-55}, {122,-60}` e clamp em s16. Arquivos `.vag` têm cabeçalho de 0x30 bytes
  ("VAGp"); o PSP espera que o jogo passe o ponteiro **após** o cabeçalho (a confirmar no Vita). Se o bloco usar
  preditor > 4, pode ser HEVAG (`HevagTables`, 128 preditores, 4 históricos); suporte HEVAG na libsas do Vita é a
  confirmar (decidir pelo byte 1 alto como em `DecodeHevag`: `coef = (b0 >> 4) | (b1 & 0xF0)`).
- **Estado interno (global, um por processo):** `Initialized`, `Grain` (amostras por `sceSasCore`), `NumVoices`,
  `NumReverbs`, `OutputMode` (0 estéreo, 1 multicanal no PSP), `Voice[NumVoices]`, reverb (`Type`, `DryOn`, `WetOn`,
  `VolL/R`, `Delay`, `Feedback`), peaks (dry/wet/pre-master), lock.
  - **Voz:** `Type` (Off, Vag, Pcm, Noise), `Addr`, `Size`, `Loop` (VAG: flag; PCM: `loopSize`), `Pos` (bloco/amostra),
    histórico ADPCM, `Pitch` (0x1000 = taxa nativa, PSP: 1..0x4000), `SampleFrac` (posição fracionária 12 bits),
    `VolL/R` (dry), `WetL/R`, `Distortion`, `NoiseClk`, `On`, `Paused`, `Ended`, envelope (`Height`, fase
    Attack/Decay/Sustain/Release/Off, `Rates[4]`, `Modes[4]`, `SustainLevel`).
- **Envelope (PSP, PPSSPP):** altura 0..`0x40000000`; curvas `0 LINEAR_INCREASE`, `1 LINEAR_DECREASE`,
  `2 LINEAR_BENT`, `3 EXPONENT_DECREASE`, `4 EXPONENT_INCREASE`, `5 DIRECT`; bits de flag `0x1 attack`, `0x2 decay`,
  `0x4 sustain`, `0x8 release`. `SimpleADSR` usa os registradores SPU (`adsr1`/`adsr2` de 16 bits, mesmo layout do
  SPU do PS1/PSP) e é convertido em rates/modes. Fórmulas exatas: seguir `SasAudio.cpp` do PPSSPP como referência de
  semântica, sem copiar código; confirmar contra o hardware Vita (a libsas do Vita pode ter sido reescrita).
- **Mix:** para cada voz ativa e não pausada, gerar `Grain` amostras reamostradas por `Pitch` (interpolação linear
  basta), aplicar envelope (`sample * Height >> 30`), somar `sample*VolL >> 12` / `VolR` no barramento dry e
  `WetL/R` no barramento wet (volume 0x1000 = unidade, PSP); wet passa pelo reverb e é somado ao dry se `WetOn`;
  clamp em s16 e escrever intercalado. `sceSasCoreWithMix` soma ao conteúdo existente do buffer com `lvol/rvol`.
- **Reverb:** tipos do PSP `-1 OFF, 0 ROOM, 1 STUDIO_SMALL, 2 STUDIO_MEDIUM, 3 STUDIO_LARGE, 4 HALL, 5 SPACE,
  6 ECHO, 7 DELAY, 8 PIPE` (valores no Vita a confirmar). Implementação inicial aceitável: delay + feedback simples
  para ECHO/DELAY e passthrough (wet = 0) para os demais, com `Log.WarnOnce`.
- **Dependências:** `GuestMemory`, `Adpcm`/`HevagTables`. Sem host audio.
- **Custo:** `sceSasCore` é CPU-bound (32 vozes × 256 amostras); fica na faixa padrão. Evitar alocação por chamada
  (buffers fixos no engine, como o NGS faz).

Códigos de erro: **valores do PSP** (PPSSPP `sceSas.h`); a família `0x8042xxxx` provavelmente é a mesma no Vita, mas
**todos estão a confirmar** (procurar as constantes `0x8042` na desmontagem de `libsas.suprx`):

| Nome | Valor (PSP) |
| --- | --- |
| SCE_SAS_ERROR_INVALID_GRAIN | 0x80420001 |
| SCE_SAS_ERROR_INVALID_MAX_VOICES | 0x80420002 |
| SCE_SAS_ERROR_INVALID_OUTPUT_MODE | 0x80420003 |
| SCE_SAS_ERROR_INVALID_SAMPLE_RATE | 0x80420004 |
| SCE_SAS_ERROR_BAD_ADDRESS | 0x80420005 |
| SCE_SAS_ERROR_INVALID_VOICE | 0x80420010 |
| SCE_SAS_ERROR_INVALID_NOISE_FREQ | 0x80420011 |
| SCE_SAS_ERROR_INVALID_PITCH | 0x80420012 |
| SCE_SAS_ERROR_INVALID_ADSR_CURVE_MODE | 0x80420013 |
| SCE_SAS_ERROR_INVALID_PARAMETER | 0x80420014 |
| SCE_SAS_ERROR_INVALID_LOOP_POS | 0x80420015 |
| SCE_SAS_ERROR_VOICE_PAUSED | 0x80420016 |
| SCE_SAS_ERROR_INVALID_VOLUME | 0x80420018 |
| SCE_SAS_ERROR_INVALID_ADSR_RATE | 0x80420019 |
| SCE_SAS_ERROR_INVALID_PCM_SIZE | 0x8042001A |
| SCE_SAS_ERROR_REV_INVALID_TYPE | 0x80420020 |
| SCE_SAS_ERROR_REV_INVALID_FEEDBACK | 0x80420021 |
| SCE_SAS_ERROR_REV_INVALID_DELAY_TIME | 0x80420022 |
| SCE_SAS_ERROR_REV_INVALID_VOLUME | 0x80420023 |
| SCE_SAS_ERROR_BUSY | 0x80420030 |
| SCE_SAS_ERROR_NOT_INIT | 0x80420100 |
| SCE_SAS_ERROR_ALREADY_INIT | 0x80420101 |

**Estado da implementação (HLE, ABI ainda inferida):** `Hle/Modules/Sas.cs` + `Hle/Audio/SasEngine.cs` (vozes,
envelope, mixer, efeito) + `Hle/Audio/Adpcm.cs` (bloco VAG/HEVAG; `Ngs.DecodeHevag` não foi migrado para o helper,
para não mexer no NGS sem regressão). `sceSysmoduleLoadModule(0x000D)` tenta antes `libsas.suprx` por LLE
(`LleSysmodules` em `SystemMisc.cs`); o HLE é o fallback. Os protótipos e os códigos `0x8042xxxx` continuam
inferidos do PSP. Decisões tomadas onde a doc dizia "a confirmar":
- Config: pares `chave=valor` separados por espaço/vírgula (`numGrains`, `numVoices`, `numReverbs`, sem
  diferenciar maiúsculas; chaves desconhecidas só geram aviso). Padrões 256/32/1. `numVoices` aceito em 1..256 (folga
  sobre o limite 32 do PSP). Grão: regra do PSP (64..2048, múltiplo de 32) e ≤ `numGrains` quando informado;
  `sceSasInit` usa `numGrains` como grão. `GetNeededMemorySize` devolve 0 e escreve o tamanho em `*outSize`.
  O tamanho do buffer do init não é checado.
- Saída sempre estéreo a 48 kHz (`OutputMode` 1 é aceito e renderizado como estéreo, com aviso). Pitch 0x1000 = 1
  amostra de origem por amostra de saída, interpolação linear, faixa 1..0x4000.
- VAG: o mesmo decodificador cobre VAG clássico e HEVAG (preditor estendido pelo nibble alto do byte 1); histórico
  saturado em s16. Bloco com flag 7 ou fim do buffer encerram a voz; bit2 marca o início do loop, bit0 encerra o
  bloco e, com `loopflag` e bit1, volta ao início do loop (bloco 0 se não houver marca). `SetVoice` no mesmo endereço
  com a voz tocando não reinicia a posição.
- PCM: `size` em amostras (S16 mono), `loopsize` = últimas N amostras repetidas (≤ 0 sem loop); sem limite superior
  de tamanho.
- Envelope por amostra, altura 0..0x40000000; padrões (sem confirmação): attack instantâneo, sustain no máximo até o
  key off, release instantâneo. Curvas: linear ±rate; bent = rate até 3/4 da altura e rate/4 acima; exponencial =
  rate/2^31 da distância a 0/máximo por amostra; direct = altura passa a `rate` e a fase avança. `SimpleADSR`
  converte os registradores do SPU (shift/step) para essas taxas. Modos válidos por fase como no PSP (A: 0/2/5,
  D e R: 1/3/5, S: 0..5).
- `SetPause` é por voz; `GetEndState`/`GetPauseState` são por voz (1/0). `SetEffect` trata qualquer valor ≠ 0 como
  ligado. Efeito: ECHO/DELAY = linha de atraso com feedback (`delayTime` × 192 amostras, `feedback/128`); demais tipos
  de reverb passam o envio wet sem processamento (aviso uma vez); OFF zera o wet. Padrões: dry ligado, wet desligado,
  volume do efeito 0x1000. `SetDistortion` só guarda o valor. Picos: maior |amostra| do barramento dry, da saída do
  efeito e da soma pré-clamp no último grão, escritos em `*pPeakL/*pPeakR`.

Validação geral: nenhum jogo instalado importa SceSas. Teste unitário de `SasEngine` (uma voz VAG conhecida, pitch
0x1000, ADSR direto → PCM idêntica ao decode VAG puro; pitch 0x2000 → metade das amostras). Homebrew VitaSDK com stub
gerado do `db/360/SceSas.yml` (`vita-libs-gen`) chamando init/setvoice/keyon/core e salvando a saída; comparar com o
Vita3K em LLE (`libsas`). Quando um jogo importar, rodar `VitaSharp.Desktop imports <TITLEID>`.

### sceSasGetNeededMemorySize
- [x] **Prioridade:** P2 · **Biblioteca:** SceSas · **NID:** 0x180C6824
- **Protótipo:** `int sceSasGetNeededMemorySize(const char *config, SceSize *outSize);` (inferido)
- **Comportamento:** analisa a string de configuração e devolve o tamanho de buffer de trabalho necessário para
  `sceSasInit`.
- **Retorno/erros:** 0 e `*outSize`; `INVALID_PARAMETER`/`BAD_ADDRESS` para string inválida ou ponteiro nulo (a
  confirmar). Pode também retornar o tamanho direto em r0 (a confirmar).
- **Estruturas:** string `key=value` separada por espaços: `numGrains`, `numVoices`, `numReverbs` (a confirmar).
- **Implementação no VitaSharp:** `Sas.cs`; parser de config compartilhado com `Init`; tamanho arbitrário mas estável
  (ex.: `0x1000 + numVoices*0x100 + numGrains*8`), já que o HLE não usa a memória.
- **Validação:** homebrew imprime o valor; comparar com LLE.

### sceSasInit
- [x] **Prioridade:** P2 · **Biblioteca:** SceSas · **NID:** 0x449B5974
- **Protótipo:** `int sceSasInit(const char *config, void *buffer, SceSize bufferSize);` (inferido)
- **Comportamento:** inicializa o mixer global com a config e o buffer de trabalho do jogo; todas as vozes `Off`,
  envelope zerado, volumes padrão (PSP: 0x1000? a confirmar), reverb OFF, grão padrão (`numGrains`).
- **Retorno/erros:** 0; `ALREADY_INIT`; `INVALID_GRAIN`/`INVALID_MAX_VOICES` (config fora da faixa; PSP: grão 64..2048
  múltiplo de 32, vozes ≤ 32); `BAD_ADDRESS`; buffer pequeno (código a confirmar).
- **Estruturas:** —
- **Implementação no VitaSharp:** `Sas.cs` → `new SasEngine(grain, voices, reverbs)`.
- **Validação:** init duplo → `ALREADY_INIT`.

### sceSasInitWithGrain
- [x] **Prioridade:** P2 · **Biblioteca:** SceSas · **NID:** 0x820D5F82
- **Protótipo:** `int sceSasInitWithGrain(const char *config, unsigned int grain, void *buffer, SceSize bufferSize);` (inferido)
- **Comportamento:** igual a `sceSasInit`, mas com o grão explícito.
- **Retorno/erros:** como `sceSasInit` + `INVALID_GRAIN`.
- **Estruturas:** —
- **Implementação no VitaSharp:** `Sas.cs`, compartilha `InitCore`.
- **Validação:** grão 256 → `sceSasCore` escreve 256×2×2 bytes.

### sceSasExit
- [x] **Prioridade:** P2 · **Biblioteca:** SceSas · **NID:** 0xBB7D6790
- **Protótipo:** `int sceSasExit(void **outBuffer, SceSize *outBufferSize);` (inferido)
- **Comportamento:** finaliza o mixer e devolve ao jogo o buffer de trabalho passado no init (para liberar).
- **Retorno/erros:** 0; `NOT_INIT`. Ponteiros nulos tolerados? (a confirmar).
- **Estruturas:** —
- **Implementação no VitaSharp:** `Sas.cs`; guardar `buffer/size` do init.
- **Validação:** exit sem init → `NOT_INIT`.

### sceSasCore
- [x] **Prioridade:** P2 · **Biblioteca:** SceSas · **NID:** 0x7A4672B2
- **Protótipo:** `int sceSasCore(SceInt16 *out);` (inferido)
- **Comportamento:** processa um grão: avança todas as vozes, aplica envelopes, mistura dry + reverb e escreve
  `Grain` quadros estéreo S16 em `out` (multicanal se `OutputMode == 1`, a confirmar no Vita). Vozes que chegam ao fim
  (flag 7 no VAG, fim do PCM sem loop, envelope de release chegando a 0) ficam `Ended`. Atualiza peaks.
- **Retorno/erros:** 0; `NOT_INIT`; `BAD_ADDRESS` (`out` nulo ou desalinhado — a confirmar).
- **Estruturas:** —
- **Implementação no VitaSharp:** `Sas.cs` → `SasEngine.Mix(Span<short>)`, depois copiar para `GuestMemory.Span(out, grain*4)`.
- **Validação:** teste unitário do engine; homebrew comparando com LLE.

### sceSasCoreWithMix
- [x] **Prioridade:** P2 · **Biblioteca:** SceSas · **NID:** 0xBD496983
- **Protótipo:** `int sceSasCoreWithMix(SceInt16 *inOut, int lvol, int rvol);` (inferido)
- **Comportamento:** como `sceSasCore`, mas mistura o resultado ao PCM já presente em `inOut`; no PSP, o conteúdo
  existente é escalado por `lvol/rvol` (0..0x1000) antes da soma, com clamp.
- **Retorno/erros:** como `sceSasCore`; `INVALID_VOLUME` (volumes fora da faixa, a confirmar).
- **Estruturas:** —
- **Implementação no VitaSharp:** `SasEngine.Mix(…, mixWithExisting: true, lvol, rvol)`.
- **Validação:** buffer de entrada zerado deve dar o mesmo resultado de `sceSasCore` com lvol=rvol=0x1000.

### sceSasSetVoice
- [x] **Prioridade:** P2 · **Biblioteca:** SceSas · **NID:** 0x2B75F9BC
- **Protótipo:** `int sceSasSetVoice(int iVoiceNum, const void *vagBuf, SceSize size, unsigned int loopflag);` (inferido)
- **Comportamento:** associa um stream VAG (sem cabeçalho "VAGp", a confirmar) à voz; `loopflag != 0` faz a voz
  respeitar os flags de loop dos blocos (bit2 início, bit1 repetir). Reseta posição e histórico; não liga a voz
  (isso é `SetKeyOn`).
- **Retorno/erros:** 0; `INVALID_VOICE` (índice fora de `0..NumVoices-1`); `INVALID_PARAMETER` (size 0, não múltiplo
  de 16, `loopflag > 1`); `BAD_ADDRESS`; `NOT_INIT`.
- **Estruturas:** blocos VAG de 16 bytes (arquitetura).
- **Implementação no VitaSharp:** `SasEngine.Voice[i].SetVag(addr, size, loop)`; ler blocos sob demanda da memória
  guest (o jogo pode atualizar o buffer depois).
- **Validação:** VAG com loop toca indefinidamente; sem loop termina e `GetEndState` = 1.

### sceSasSetVoicePCM
- [x] **Prioridade:** P2 · **Biblioteca:** SceSas · **NID:** 0xB1756EFC
- **Protótipo:** `int sceSasSetVoicePCM(int iVoiceNum, const void *pcmBuf, SceSize size, int loopsize);` (inferido)
- **Comportamento:** associa PCM S16 mono à voz; `size` em amostras ou bytes (a confirmar; no PSP é em amostras,
  máx. 0x10000 com `INVALID_PCM_SIZE`); `loopsize` = tamanho do trecho final repetido (≤ 0 sem loop, a confirmar).
- **Retorno/erros:** 0; `INVALID_VOICE`, `INVALID_PCM_SIZE`, `INVALID_LOOP_POS`, `BAD_ADDRESS`, `NOT_INIT`.
- **Estruturas:** —
- **Implementação no VitaSharp:** `SasEngine.Voice[i].SetPcm(...)`.
- **Validação:** senoide PCM com pitch 0x1000 sai idêntica.

### sceSasSetNoise
- [x] **Prioridade:** P2 · **Biblioteca:** SceSas · **NID:** 0xF1C63CB9
- **Protótipo:** `int sceSasSetNoise(int iVoiceNum, unsigned int uClk);` (inferido)
- **Comportamento:** transforma a voz num gerador de ruído com frequência `uClk` (PSP: 0..63, mesmo esquema do
  gerador de ruído do SPU: LFSR atualizado a cada período derivado de `uClk`).
- **Retorno/erros:** 0; `INVALID_VOICE`, `INVALID_NOISE_FREQ`, `NOT_INIT`.
- **Estruturas:** —
- **Implementação no VitaSharp:** `SasEngine`; LFSR de 15 bits (algoritmo exato a confirmar; ruído "parecido" é
  aceitável).
- **Validação:** sai ruído não nulo após KeyOn.

### sceSasSetPitch
- [x] **Prioridade:** P2 · **Biblioteca:** SceSas · **NID:** 0x2C48A08C
- **Protótipo:** `int sceSasSetPitch(int iVoiceNum, int pitch);` (inferido)
- **Comportamento:** define o passo de reamostragem; `0x1000` = taxa original (48 kHz no Vita? 44.1 kHz no PSP — a
  confirmar a taxa de saída do libsas no Vita).
- **Retorno/erros:** 0; `INVALID_VOICE`, `INVALID_PITCH` (PSP: fora de 1..0x4000), `NOT_INIT`.
- **Estruturas:** —
- **Implementação no VitaSharp:** `Voice.Pitch`; posição em ponto fixo 12 bits.
- **Validação:** pitch 0x2000 dobra a frequência.

### sceSasSetVolume
- [x] **Prioridade:** P2 · **Biblioteca:** SceSas · **NID:** 0x0BE8204D
- **Protótipo:** `int sceSasSetVolume(int iVoiceNum, int l, int r, int wl, int wr);` (inferido; no PSP o 5º argumento
  vai na pilha no Vita, pelo AAPCS)
- **Comportamento:** volumes dry esquerdo/direito e wet (envio ao reverb) esquerdo/direito; 0x1000 = unidade;
  negativos invertem fase (PSP: faixa -0x1000..0x1000).
- **Retorno/erros:** 0; `INVALID_VOICE`, `INVALID_VOLUME`, `NOT_INIT`.
- **Estruturas:** —
- **Implementação no VitaSharp:** `Voice.VolL/R`, `WetL/R`.
- **Validação:** l=0x1000,r=0 → canal direito silencioso.

### sceSasSetADSR
- [x] **Prioridade:** P2 · **Biblioteca:** SceSas · **NID:** 0x18A5EFA2
- **Protótipo:** `int sceSasSetADSR(int iVoiceNum, unsigned int flag, unsigned int a, unsigned int d, unsigned int s, unsigned int r);` (inferido)
- **Comportamento:** define as *rates* das fases selecionadas por `flag` (`0x1` A, `0x2` D, `0x4` S, `0x8` R).
- **Retorno/erros:** 0; `INVALID_VOICE`, `INVALID_ADSR_RATE` (PSP: rate > 0x7FFFFFFF), `NOT_INIT`.
- **Estruturas:** —
- **Implementação no VitaSharp:** `Voice.Env.Rates[]`.
- **Validação:** attack rate alto → envelope sobe em poucos grãos (`GetEnvelope`).

### sceSasSetADSRmode
- [x] **Prioridade:** P2 · **Biblioteca:** SceSas · **NID:** 0x5207F9D2
- **Protótipo:** `int sceSasSetADSRmode(int iVoiceNum, unsigned int flag, unsigned int a, unsigned int d, unsigned int s, unsigned int r);` (inferido)
- **Comportamento:** define o tipo de curva (0..5, ver arquitetura) de cada fase selecionada por `flag`.
- **Retorno/erros:** 0; `INVALID_VOICE`, `INVALID_ADSR_CURVE_MODE`, `NOT_INIT`.
- **Estruturas:** —
- **Implementação no VitaSharp:** `Voice.Env.Modes[]`.
- **Validação:** modo exponencial vs linear muda a forma do decaimento.

### sceSasSetSL
- [x] **Prioridade:** P2 · **Biblioteca:** SceSas · **NID:** 0xDE6227B8
- **Protótipo:** `int sceSasSetSL(int iVoiceNum, unsigned int sl);` (inferido)
- **Comportamento:** nível de sustain (altura onde o decay termina; PSP: 0..0x40000000).
- **Retorno/erros:** 0; `INVALID_VOICE`, `INVALID_PARAMETER` (fora da faixa), `NOT_INIT`.
- **Estruturas:** —
- **Implementação no VitaSharp:** `Voice.Env.SustainLevel`.
- **Validação:** envelope estabiliza no valor após o decay.

### sceSasSetSimpleADSR
- [x] **Prioridade:** P2 · **Biblioteca:** SceSas · **NID:** 0xECCE0DB8
- **Protótipo:** `int sceSasSetSimpleADSR(int iVoiceNum, unsigned int adsr1, unsigned int adsr2);` (inferido)
- **Comportamento:** configura o envelope inteiro a partir de dois registradores no formato SPU: `adsr1` =
  `[15] attack exponencial, [14:8] attack rate, [7:4] decay rate, [3:0] sustain level`; `adsr2` = `[15] sustain
  exponencial, [14] sustain decrescente, [12:6] sustain rate, [5] release exponencial, [4:0] release rate` (layout do
  SPU PS1/PSP; a confirmar no Vita). Converte para rates/modes internos.
- **Retorno/erros:** 0; `INVALID_VOICE`, `INVALID_ADSR_CURVE_MODE`, `NOT_INIT`.
- **Estruturas:** bitfields acima.
- **Implementação no VitaSharp:** `SasEnvelope.FromSimple(adsr1, adsr2)`; tabelas de conversão rate→incremento a
  derivar do comportamento do PSP (PPSSPP como referência de semântica).
- **Validação:** valores típicos (`0x000F`, `0x1FC0`) produzem envelope plausível.

### sceSasSetKeyOn
- [x] **Prioridade:** P2 · **Biblioteca:** SceSas · **NID:** 0xC838DB6F
- **Protótipo:** `int sceSasSetKeyOn(int iVoiceNum);` (inferido)
- **Comportamento:** inicia a voz: posição 0, histórico zerado, envelope em Attack com altura 0, `Ended = false`.
  Efetivo no próximo `sceSasCore`.
- **Retorno/erros:** 0; `INVALID_VOICE`; `VOICE_PAUSED` (voz pausada, PSP); `NOT_INIT`.
- **Estruturas:** —
- **Implementação no VitaSharp:** `Voice.KeyOn()` (marcar pendente, aplicar no mix).
- **Validação:** KeyOn + Core produz som.

### sceSasSetKeyOff
- [x] **Prioridade:** P2 · **Biblioteca:** SceSas · **NID:** 0x5E42ADAB
- **Protótipo:** `int sceSasSetKeyOff(int iVoiceNum);` (inferido)
- **Comportamento:** passa o envelope para Release; a voz termina quando a altura chega a 0.
- **Retorno/erros:** 0; `INVALID_VOICE`, `VOICE_PAUSED`, `NOT_INIT`.
- **Estruturas:** —
- **Implementação no VitaSharp:** `Voice.KeyOff()`.
- **Validação:** após release, `GetEndState` = 1.

### sceSasSetPause
- [x] **Prioridade:** P2 · **Biblioteca:** SceSas · **NID:** 0x59C7A9DF
- **Protótipo:** `int sceSasSetPause(int iVoiceNum, unsigned int pauseFlag);` (inferido; no PSP o argumento é uma
  máscara de vozes `voicebit` + `pause`, no Vita é a confirmar)
- **Comportamento:** pausa/retoma a(s) voz(es): o mix pula a voz sem avançar posição nem envelope.
- **Retorno/erros:** 0; `INVALID_VOICE`, `NOT_INIT`.
- **Estruturas:** —
- **Implementação no VitaSharp:** `Voice.Paused`.
- **Validação:** pausar, rodar Core, retomar: a PCM continua de onde parou.

### sceSasSetDistortion
- [x] **Prioridade:** P2 · **Biblioteca:** SceSas · **NID:** 0x011788BE
- **Protótipo:** `int sceSasSetDistortion(int iVoiceNum, int distortion);` (inferido; função nova do Vita, sem
  equivalente no PSP)
- **Comportamento:** efeito de distorção (provável saturação/clipping por voz). Semântica e faixa a confirmar.
- **Retorno/erros:** 0; `INVALID_VOICE`, `INVALID_PARAMETER`, `NOT_INIT` (a confirmar).
- **Estruturas:** —
- **Implementação no VitaSharp:** guardar o valor e ignorar no mix com `Log.WarnOnce` até confirmar.
- **Validação:** confirmar com LLE.

### sceSasSetEffect
- [x] **Prioridade:** P2 · **Biblioteca:** SceSas · **NID:** 0xB0444E69
- **Protótipo:** `int sceSasSetEffect(int drySwitch, int wetSwitch);` (inferido)
- **Comportamento:** liga/desliga a saída dry e a saída wet (reverb) no master.
- **Retorno/erros:** 0; `NOT_INIT`; `INVALID_PARAMETER` (valores ≠ 0/1, a confirmar).
- **Estruturas:** —
- **Implementação no VitaSharp:** `SasEngine.DryOn/WetOn`.
- **Validação:** dry=0 com wet desligado → silêncio.

### sceSasSetEffectType
- [x] **Prioridade:** P2 · **Biblioteca:** SceSas · **NID:** 0xCDF2DDD5
- **Protótipo:** `int sceSasSetEffectType(int type);` (inferido)
- **Comportamento:** seleciona o algoritmo de reverb (`-1..8`, ver arquitetura).
- **Retorno/erros:** 0; `REV_INVALID_TYPE`; `BUSY` (PSP retorna BUSY ao trocar tipo durante processamento — no HLE
  síncrono não ocorre); `NOT_INIT`.
- **Estruturas:** —
- **Implementação no VitaSharp:** `SasEngine.Reverb.Type`; zerar linhas de delay ao trocar.
- **Validação:** tipo 6 (ECHO) gera eco audível.

### sceSasSetEffectParam
- [x] **Prioridade:** P2 · **Biblioteca:** SceSas · **NID:** 0xBAD546A0
- **Protótipo:** `int sceSasSetEffectParam(unsigned int delayTime, unsigned int feedback);` (inferido)
- **Comportamento:** parâmetros de delay/feedback (só efetivos para ECHO/DELAY no PSP; faixa PSP 0..127).
- **Retorno/erros:** 0; `REV_INVALID_DELAY_TIME`, `REV_INVALID_FEEDBACK`, `NOT_INIT`.
- **Estruturas:** —
- **Implementação no VitaSharp:** `SasEngine.Reverb.Delay/Feedback`.
- **Validação:** feedback 0 → eco único.

### sceSasSetEffectVolume
- [x] **Prioridade:** P2 · **Biblioteca:** SceSas · **NID:** 0x55EDDBFA
- **Protótipo:** `int sceSasSetEffectVolume(int valL, int valR);` (inferido)
- **Comportamento:** volume de retorno do reverb no master (0x1000 = unidade; PSP aceita -0x1000..0x1000).
- **Retorno/erros:** 0; `REV_INVALID_VOLUME`, `NOT_INIT`.
- **Estruturas:** —
- **Implementação no VitaSharp:** `SasEngine.Reverb.VolL/R`.
- **Validação:** 0 → sem wet.

### Getters e setters globais simples de SceSas

Seção comum: todos **inferidos** (sem header; assinaturas do PSP sem o `SasCore *`). Retornam `NOT_INIT` antes do
init; os que recebem voz retornam `INVALID_VOICE` fora de `0..NumVoices-1`. Implementação em `Sas.cs` lendo/escrevendo
o `SasEngine` sob o lock. Validação: homebrew de teste comparando com Vita3K em LLE.

| Função | NID | Protótipo (inferido) | Comportamento |
| --- | --- | --- | --- |
| - [x] sceSasSetGrain | 0x2B4A207C | `int sceSasSetGrain(unsigned int grain);` | Muda o número de amostras por `sceSasCore`; `INVALID_GRAIN` fora da faixa (PSP 64..2048, múltiplo de 32; Vita a confirmar, provável ≤ `numGrains` do init). |
| - [x] sceSasGetGrain | 0x2BEA45BC | `int sceSasGetGrain(void);` | Retorna o grão atual em r0. |
| - [x] sceSasSetOutputmode | 0x44DDB3C4 | `int sceSasSetOutputmode(unsigned int outputmode);` | 0 = estéreo, 1 = multicanal (PSP); `INVALID_OUTPUT_MODE` se outro valor. Modo multicanal no Vita a confirmar. |
| - [x] sceSasGetOutputmode | 0x2C36E150 | `int sceSasGetOutputmode(void);` | Retorna o modo atual. |
| - [x] sceSasGetEndState | 0x007E63E6 | `int sceSasGetEndState(int iVoiceNum);` | Retorna 1 se a voz terminou (ou nunca foi ligada), 0 se tocando. No PSP a versão sem voz retorna máscara de bits de todas as vozes; no Vita, por voz (a confirmar). |
| - [x] sceSasGetPauseState | 0xFD1A0CBF | `int sceSasGetPauseState(int iVoiceNum);` | Retorna 1 se a voz está pausada (ou máscara, como acima — a confirmar). |
| - [x] sceSasGetEnvelope | 0x296A9910 | `int sceSasGetEnvelope(int iVoiceNum);` | Retorna a altura atual do envelope (0..0x40000000). |
| - [x] sceSasGetDryPeak | 0xB6642276 | `int sceSasGetDryPeak(int *pPeakL, int *pPeakR);` (inferido, a confirmar se é por voz ou valor de retorno) | Pico absoluto do barramento dry no último `sceSasCore`. Função nova do Vita. |
| - [x] sceSasGetWetPeak | 0x4314F0E9 | `int sceSasGetWetPeak(int *pPeakL, int *pPeakR);` (inferido, a confirmar) | Pico do barramento wet (saída do reverb) no último grão. |
| - [x] sceSasGetPreMasterPeak | 0x1568017A | `int sceSasGetPreMasterPeak(int *pPeakL, int *pPeakR);` (inferido, a confirmar) | Pico da soma dry+wet antes do volume master/clamp no último grão. |

## Fontes do sistema: ScePgf e ScePvf (visão comum)

### Estado da implementação (2026-10-22, branch `feat/hle-g1`)

Implementado em `Hle/Modules/Pgf.cs`, `Hle/Modules/Pvf.cs` e `Hle/Font/` (`PgfFont.cs`, `PvfFace.cs`, `FontBlit.cs`).
Testes: `tests/VitaSharp.Tests/PgfHleTests.cs`, `PvfHleTests.cs` (fontes sintéticas em `FontTestData.cs`, mais
checagens opcionais contra as fontes do firmware quando há uma instalação do Vita3K). Nenhum jogo instalado importa
estas bibliotecas hoje.

**Confirmado desmontando `libpgf.suprx`/`libpvf.suprx` do firmware** (carregados pelo próprio VitaSharp numa
ferramenta de scratch). Isto corrige vários "a confirmar" das seções abaixo; onde o texto antigo diverge, vale esta
lista:

- Fontes: lidas de `sa0:data/font/pgf|pvf/` pelo VFS. O VitaSharp não traz fontes; sem os arquivos do firmware,
  `sceFontOpen` falha com `HANDLER_OPEN_FAILED` (0x80460005) e `scePvfOpen` com `FILEOPEN` (0x80460005).
- `sceFontNewLib`: `errorCode == NULL` ⇒ retorna 0 sem escrever; exige `allocFunc` e `freeFunc`; `numFonts` 1..9
  (fora ⇒ 0x80460003); aloca 0x4C (lib), `numFonts`×0x4C (tabela de fontes = handles) e `numFonts`×0x234 pelo
  `allocFunc`. Defaults: 128 dpi, alt char 0x5F.
- `scePvfNewLib`: `reserved` (+0xC) precisa ser 0; exige alloc/realloc/free; `maxNumFonts` 1..18; aloca 0x460,
  n×0x84 e n×0x114. Defaults: 128 dpi, tamanho 10,125 pt (= 18 px/em), EM 1000, alt char 0x5F.
- Listas do sistema (copiadas do segmento de dados): PGF = 18 entradas `jpn0, ltn0..ltn15, kr0` (jpn0 estilo 103 DB,
  kr0 "AsiaNHH(512Johab)" país 3; arib fora); PVF = 18 entradas `jpn0..3, ltn0..7, kr0..3, cn0..1`, peso 0, nomes
  vazios, só `fileName` (music_arib/psexchar fora). `fontFileName` é só o nome do arquivo.
- `sceFontGetFontInfoByIndexNumber`: o índice é o **terceiro** argumento (o "unknown" do header); o quarto é ignorado.
- Busca (PGF): máscara de critérios não-nulos; peso/tamanho comparados com tolerância de 0,1 %; tamanho = tamanho ×
  resolução (a da lib quando o estilo não traz). `FindOptimumFont`: casamento exato de tamanho ganha na hora, senão o
  mais próximo (H e V dividem o mesmo mínimo), sem critério de tamanho ganha a **última** fonte que casa; sem
  resultado repete só com o idioma. `FindFont`: primeira que casa ou −1. PVF: sem tamanho; Optimum = primeira que casa,
  com o mesmo fallback de idioma, senão 0.
- `SceFontInfo+0x54` ("charMapLength") é o número de glifos (header 0x14); `shadowMapLength` é sempre 0. Fontes de
  usuário têm estilo zerado, exceto os floats de tamanho/resolução tirados do header.
- Caractere sem glifo (PGF): códigos > 0x1F usam o alt char; se nem ele existe, info zerada e nada desenhado, retorno
  0. Revisão 3 (kr0): tabela de faixas comprimida para o charmap, faixas de glifos internos (0x1200/0x1400…) que
  respondem com o alt char, e glifos "overlay" (layout 3) que são listas de 3 códigos desenhados com o deslocamento
  de cada parte; `GetCharImageRect` devolve a união.
- Blit (as duas libs): **soma saturada** no buffer, com a fração de `xPos64` espalhando cada pixel para x+1; só
  formatos 0 (4 bpp, x par no nibble baixo) e 2 (8 bpp) — 1, 3 e 4 ⇒ 0x80460003. PGF: posição = canto superior
  esquerdo do bitmap; PVF: pena na baseline + (bitmapLeft, −bitmapTop). `_Clip`: retângulo em pixels do buffer;
  largura/altura 0, `x+w == 0`, `y+h == 0` ou início fora do buffer ⇒ retorna 0 sem desenhar.
- PVF `GetCharInfo` só carrega métricas (campos de bitmap ficam 0); código ausente ou 0xFFFF usa o alt char; alt char
  0 ⇒ 0x8046000D (sem nome no header). `SetCharSize` > 0, `SetEmboldenRate` −20..40, `SetSkewValue` ±30 (fora ⇒
  0x80460003). `scePvfSetEM` aceita 0 e só é guardado. Conversões ponto↔pixel: `px·72/res` e `pt·res/72`; resultado
  inf/NaN ⇒ 0x8046000E.
- `scePvfGetCharGlyphOutline(font, code, outline)`: o terceiro argumento é uma struct do chamador no formato
  `FT_Outline` (0x14: s16 n_contours, s16 n_points, points*, tags*, contours*, s32 flags) cujos arrays a lib aloca
  com o `allocFunc`; `scePvfReleaseCharGlyphOutline(font, outline)` libera os três ponteiros.
- `__scePvfSetFt2LibCHook`/`DoneLibCHook`: só repassam a tabela ao libSceFt2; o HLE registra e retorna 0.

**Aproximado (honesto):**

- PVF rasteriza com stb_truetype (StbTrueTypeSharp, MIT/Unlicense, gerenciado e compatível com AOT) em vez do
  FreeType da Sony: tamanhos, caixas e posições seguem as regras do FreeType (cbox, grid-fit das métricas), mas sem
  hinting, então os valores de cobertura do antialias diferem um pouco.
- Embolden: força = taxa × tamanho(pt) × 64 / 100 em 26.6 (o valor que a libpvf passa ao libSceFt2; a unidade final
  não foi confirmada). Skew: cisalhamento `x += y·tan(ângulo)`. Kerning só da tabela `kern`, arredondado ao pixel
  (como `FT_Get_Kerning`); as PVF do firmware não têm `kern`, então dá 0.
- `scePvfGetFontInfo`: máximos tirados de head/hhea/vhea no tamanho atual (a fórmula real usa o EM e não foi
  reproduzida). `scePvfIsVertElement` = existe forma vertical via GSUB `vert`/`vrt2`. Imagens verticais posicionadas
  pela origem vertical (pena + vertBearingX/Y).
- `scePvfOpenDefault*FontOnSharedMemory` abrem `jpn0.pvf`/`ltn0.pvf` dos arquivos (a lib real usa blocos de memória
  compartilhada "SceSysDefaultJpnFont"/"SceSysDefaultLtnFont").
- Fontes são lidas inteiras ao abrir (a lib real faz streaming com cache). `sceFontOpenUserFile` em modo ≠ 1 lê pelos
  callbacks de I/O do jogo, como a lib real exige; `scePvfOpenUserFile` lê pelo VFS.
- Shadow flags do glifo PGF seguem a decomposição 2+2+3 bits do PPSSPP.

As duas bibliotecas são renderizadores de fonte em software (CPU) que escrevem bitmaps de glifos em buffers do
jogo. Não há GPU nem áudio envolvidos: o jogo pede métricas e um bitmap 4/8 bpp e depois sobe esse bitmap como
textura. Isso torna as duas fáceis de validar byte a byte contra o firmware real.

- **ScePgf** (`libpgf`, `SCE_SYSMODULE_PGF`): API herdada do PSP (`sceFont*`), lê fontes bitmap pré-rasterizadas
  `.pgf` (4 bpp, RLE). Usada por ports de PSP e por jogos antigos do SDK.
- **ScePvf** (`libpvf`, módulo "preload"): API nativa do Vita (`scePvf*`), lê fontes vetoriais `.pvf`, que são
  OpenType/CFF puros (magic `OTTO`), rasterizadas pelo `libSceFt2` (FreeType portado pela Sony). É a que a maioria
  dos jogos de Vita usa para texto do sistema.

### Arquivos no firmware (confirmado num dump instalado pelo Vita3K)

Encontrados em `%APPDATA%\Vita3K\Vita3K\sa0\data\font\` (pacote de fontes do firmware) e
`...\vs0\sys\external\`:

| Caminho Vita | Conteúdo |
| --- | --- |
| `sa0:data/font/pgf/ltn0.pgf` … `ltn15.pgf` | 16 fontes latinas (ver tabela abaixo) |
| `sa0:data/font/pgf/jpn0.pgf` | "FTT-NewRodin Pro DB", 0x20–0xFFFF, 9.778 glifos |
| `sa0:data/font/pgf/kr0.pgf` | "AsiaKNHH-SONY-uni", revisão 3 (charmap comprimido) |
| `sa0:data/font/pgf/arib.pgf` | "SCE-PS3 NewRodin JPN Regular" (símbolos ARIB) |
| `sa0:data/font/pgf/gb3s1518.bwfon` | fonte chinesa em formato `bwfon` (não PGF; fora do escopo do sceFont, a confirmar quem usa) |
| `sa0:data/font/pvf/ltn0.pvf` … `ltn7.pvf` | latinas OpenType CFF |
| `sa0:data/font/pvf/jpn0..3.pvf`, `kr0..3.pvf`, `cn0..1.pvf` | CJK OpenType CFF |
| `sa0:data/font/pvf/music_arib.pvf`, `psexchar.pvf` | símbolos (notas musicais/ARIB, glifos de botão PlayStation) |
| `vs0:sys/external/libpgf.suprx`, `libpvf.suprx`, `libSceFt2.suprx` | as bibliotecas reais (SELF criptografado, magic `SCE\0`) |

Fontes PGF latinas (lidas do header de cada arquivo; `hSize`/`vSize` em 26.6 → 648 = 10,125 pt, 448 = 7,0 pt):

| Arquivo | fontName | fontType | Tamanho |
| --- | --- | --- | --- |
| ltn0 / ltn8 | FTT-NewRodin Pro Latin | Regular | 10,125 / 7,0 |
| ltn1 / ltn9 | FTT-Matisse Pro Latin | Regular | 10,125 / 7,0 |
| ltn2 / ltn10 | FTT-NewRodin Pro Latin | Italic | 10,125 / 7,0 |
| ltn3 / ltn11 | FTT-Matisse Pro Latin | Italic | 10,125 / 7,0 |
| ltn4 / ltn12 | FTT-NewRodin Pro Latin | Bold | 10,125 / 7,0 |
| ltn5 / ltn13 | FTT-Matisse Pro Latin | Bold | 10,125 / 7,0 |
| ltn6 / ltn14 | FTT-NewRodin Pro Latin | Bold Italic | 10,125 / 7,0 |
| ltn7 / ltn15 | FTT-Matisse Pro Latin | Bold Italic | 10,125 / 7,0 |

NewRodin = `SCE_FONT_FAMILY_SANS_SERIF` (1), Matisse = `SCE_FONT_FAMILY_SERIF` (2) (mesma convenção do PSP; a
confirmar no Vita comparando com `sceFontGetFontList` real, ver "Validação").

No VitaSharp, `Io/Vfs.cs` monta `sa0`/`vs0` em `<FsRoot>/sa0` e `<FsRoot>/vs0` (`FsRoot` = `%APPDATA%\VitaSharp\fs`
no Desktop). Hoje `fs/sa0` e `fs/vs0` estão **vazios**: para testar, copie `sa0/data/font` (e `vs0/sys/external`,
se for usar LLE) do diretório do Vita3K, ou adicione ao `PkgInstaller` a instalação do pacote de fontes do firmware
(a confirmar se o `PkgInstaller` já entende PUP/fontes). Sem as fontes, `sceFontOpen`/`scePvfOpen` devem falhar com
erro de arquivo, nunca devolver um handle "vazio".

### Estratégia: LLE do firmware x HLE

O Vita3K faz **LLE por padrão** de `libpgf` (`SCE_SYSMODULE_PGF`), `libpvf` e `libSceFt2`
(`vita3k/module/src/load_module.cpp`, `auto_lle_module_names = { "libc", "libSceFt2", "libpvf", "libfiber" }`); os
`EXPORT` de `ScePgf.cpp`/`ScePvf.cpp` dele são todos `UNIMPLEMENTED()`. Isso explica a existência de
`__scePvfSetFt2LibCHook`/`__scePvfSetFt2DoneLibCHook`: o `libpvf` real chama o FreeType de `libSceFt2` e repassa a
ele os hooks de libc (malloc/free) do processo.

No VitaSharp já existe metade do caminho LLE: `Emulator.cs` (bloco "Preloaded system libraries", bits
`0x00800000 libSceFt2` e `0x01000000 libpvf` de `ProcessParam+0x24`) carrega `app0:sce_module/libpvf.suprx` **se o
jogo embarcar o módulo**. No console real, quando o bit não está desabilitado, o módulo vem de
`vs0:sys/external/`. Ordem recomendada:

1. **LLE (primeiro, barato):** no loop de preload, se `app0:sce_module/<name>.suprx` não existir, tentar
   `vs0:sys/external/<name>.suprx`. Para `SCE_SYSMODULE_PGF` (id a confirmar em `psp2/sysmodule.h`),
   `sceSysmoduleLoadModule` (`Hle/Modules/SystemMisc.cs`) deve chamar `Emu.LoadModule("vs0:sys/external/libpgf.suprx")`
   e rodar o `module_start` como `sceKernelLoadStartModule` já faz. Pré-requisitos a confirmar: (a) `SelfKeys.Generated.cs`
   decifra SELFs de sistema (`vs0`) — as chaves vêm do mesmo `sce_utils.cpp` que o Vita3K usa para LLE, então devem
   bastar; (b) todas as importações de `libpvf`/`libSceFt2`/`libpgf` (SceLibKernel, SceSysmem, SceIofilemgr, libc)
   já têm HLE (rodar `VitaSharp.Desktop imports` apontando para o módulo, ou logar `Unimplemented HLE function`);
   (c) `scePvfOpenDefault*FontOnSharedMemory` usam memória compartilhada de fontes mapeada pelo sistema (mecanismo a
   confirmar desmontando `libpvf.suprx`) — provavelmente precisa de HLE mesmo com LLE.
2. **HLE (fallback / quando o firmware não estiver instalado ou se o LLE falhar):** módulos `Pgf.cs` e `Pvf.cs`
   descritos abaixo. O HLE com PGF é exato (os bitmaps já estão no arquivo); o HLE com PVF é aproximado (outro
   rasterizador ⇒ antialias e hinting diferentes).

Se o LLE funcionar, as funções HLE abaixo nunca são resolvidas (o linker liga os NIDs ao módulo carregado); manter os
`[Hle]` como fallback exige que o linker prefira o módulo LLE quando presente (a confirmar em `Loader/` como o
VitaSharp resolve conflito NID HLE × módulo carregado).

### Códigos de erro (facility 0x8046, comuns às duas)

| Nome Pgf (`psp2/pgf.h`) | Nome Pvf (`psp2/pvf.h`) | Valor |
| --- | --- | --- |
| `SCE_FONT_ERROR_OUT_OF_MEMORY` | `SCE_PVF_ERROR_NOMEMORY` | 0x80460001 |
| `SCE_FONT_ERROR_INVALID_LIBID` | `SCE_PVF_ERROR_LIBID` | 0x80460002 |
| `SCE_FONT_ERROR_INVALID_PARAMETER` | `SCE_PVF_ERROR_ARG` | 0x80460003 |
| — | `SCE_PVF_ERROR_NOFILE` | 0x80460004 |
| `SCE_FONT_ERROR_HANDLER_OPEN_FAILED` | `SCE_PVF_ERROR_FILEOPEN` | 0x80460005 |
| — | `SCE_PVF_ERROR_FILECLOSE` | 0x80460006 |
| — | `SCE_PVF_ERROR_FILEREAD` | 0x80460007 |
| — | `SCE_PVF_ERROR_FILESEEK` | 0x80460008 |
| `SCE_FONT_ERROR_TOO_MANY_OPEN_FONTS` | `SCE_PVF_ERROR_TOOMANYOPENED` | 0x80460009 |
| `SCE_FONT_ERROR_INVALID_FONT_DATA` | `SCE_PVF_ERROR_ILLEGALVERSION` | 0x8046000A |
| — | `SCE_PVF_ERROR_DATAINCONSISTENT` | 0x8046000B |
| — | `SCE_PVF_ERROR_EXPIRED` | 0x8046000C |
| — | `SCE_PVF_ERROR_NOSUPPORT` | 0x8046000E |
| — | `SCE_PVF_ERROR_NOGLYPH` | 0x8046000F |
| — | `SCE_PVF_ERROR_UNKNOWN` | 0x8046FFFF |

Como o PGF do Vita usa a mesma facility, os códigos 0x80460004/6/7/8 provavelmente também valem para `sceFont*`
(no PSP valiam); a confirmar via LLE (abrir arquivo inexistente com `sceFontOpenUserFile`).

### Convenções de handle (as duas)

`SceFontLibHandle`, `SceFontHandle`, `ScePvfLibId`, `ScePvfFontId` são `void*`. O jogo não deveria desreferenciar,
mas o handle precisa ser um endereço guest válido, não nulo e único. Recomendação: alocar um bloco pequeno (ex.
0x20 bytes) **pelo `allocFunc` do próprio jogo** (é o que a lib real faz com toda a memória dela; jogos com
allocators instrumentados/contadores notam se nada for alocado) via `GuestThread.Current!.CallGuest(allocFunc,
userData, size)`, e mapear endereço → objeto C# num `Dictionary<uint, ...>`. Na liberação, chamar `freeFunc(userData,
ptr)`. Se `allocFunc` for 0, usar `Mem.Alloc(size, "pgf")` (`Memory/GuestMemory.cs`).

Funções que retornam `float` (`*PixelToPoint*`, `*PointToPixel*`): o binder suporta `float` de retorno
(`HleContext.Return(float)` grava em s0 e r0). `float` de entrada vai em s0–s15 (AAPCS-VFP), então
`sceFontSetResolution(lib, float, float)` recebe `lib` em r0 e os floats em s0/s1.

---

## ScePgf (23 funções)

### Arquitetura (ScePgf)

- **Objetivo:** abrir fontes `.pgf` (sistema ou do jogo), devolver métricas e escrever bitmaps de glifo
  (opcionalmente recortados) em buffers 4/8/24/32 bpp. Comportamento idêntico ao `libfont` do PSP (a API e os
  structs são os mesmos, com ponteiros de 32 bits), por isso PPSSPP (`Core/HLE/sceFont.cpp`, `Core/Font/PGF.cpp`) é a
  melhor referência de semântica.
- **Estado interno:**
  - `FontLib` (por `sceFontNewLib`): cópia de `SceFontNewLibParams` (userData, numFonts, callbacks), `hRes`/`vRes`
    (default 128,0 — o mesmo valor do campo `hResolution` = 0x2000 em 26.6 dos headers PGF; a confirmar via LLE),
    `altCharCode` (default a confirmar; no PSP era 0x5F? confirmar com `sceFontGetCharInfo` de um código ausente no
    LLE), lista de fontes abertas (máx. `numFonts`), endereço guest do handle.
  - `PgfFont` (parser imutável e compartilhável por arquivo; cache por caminho) + `OpenFont` (handle: lib dona,
    `PgfFont`, endereço guest).
  - Tabela estática de fontes do sistema (`SystemFonts`): índice → arquivo + `SceFontStyle`. Ordem do índice a
    confirmar com `sceFontGetFontList` real; candidata (ordem do PSP): ltn0..ltn15, jpn0, kr0 (+ arib?).
- **Renderizador:** nenhum externo. O PGF já contém bitmaps 4 bpp; basta descomprimir o RLE e converter o formato de
  pixel. Escrever um parser próprio em `src/VitaSharp.Core/Hle/Font/PgfFont.cs` (formato abaixo).
- **Arquivo novo:** `src/VitaSharp.Core/Hle/Modules/Pgf.cs` (`public sealed class Pgf : HleModule`) +
  `src/VitaSharp.Core/Hle/Font/PgfFont.cs`.
- **Dependências:** `Io/Vfs.cs` (resolver `sa0:data/font/pgf/*.pgf` e caminhos do jogo); `GuestThread.CallGuest`
  para `allocFunc`/`freeFunc` e, quando o jogo passar `openFunc/readFunc/seekFunc/closeFunc` (modo arquivo), para ler
  pelo driver do jogo (a confirmar se a lib do Vita usa esses callbacks para fontes do sistema ou só para
  `sceFontOpenUserFile`; PPSSPP ignora o driver de I/O e lê direto).
- **NIDs:** todos os 23 nomes estão em `Hle/Data/nids.txt`; `[Hle]` sem `Nid` basta.

### Formato do arquivo PGF (verificado em `ltn0.pgf`, `jpn0.pgf`, `kr0.pgf` do firmware)

Tudo little-endian. Os campos marcados ✔ foram conferidos lendo os arquivos reais com um script de teste
(decodificação de `A`, `i`, `.` em ltn0 e `お` em jpn0 produziu glifos corretos, terminando exatamente em
`size*8` bits); o resto segue o layout do PPSSPP (`Core/Font/PGF.h`, de memória) e está marcado "a confirmar".

**Header (0x188 bytes na revisão 2; 0x19C na revisão 3):**

| Offset | Tipo | Campo | Observação |
| --- | --- | --- | --- |
| 0x00 | u16 | headerOffset | 0 ✔ |
| 0x02 | u16 | headerSize | 0x188 (rev 2) / 0x19C (rev 3, kr0) ✔ |
| 0x04 | char[4] | magic | `"PGF0"` ✔ |
| 0x08 | s32 | revision | 2 ou 3 ✔ |
| 0x0C | s32 | version | 6 (ltn0) |
| 0x10 | s32 | charMapLength | = lastGlyph − firstGlyph + 1 ✔ (ltn0: 8774) |
| 0x14 | s32 | charPointerLength | número de glifos ✔ (ltn0: 324) |
| 0x18 | s32 | charMapBpe | bits por entrada do charmap ✔ (9; jpn0: 14) |
| 0x1C | s32 | charPointerBpe | bits por entrada da tabela de ponteiros ✔ (14; jpn0: 19) |
| 0x20 | u8[2] | pad | 04 04 |
| 0x22 | u8 | bpp | 4 ✔ |
| 0x23 | u8 | pad | |
| 0x24 | s32 ×2 | hSize, vSize | tamanho nominal em pontos, 26.6 ✔ (648 = 10,125) |
| 0x2C | s32 ×2 | hResolution, vResolution | dpi em 26.6 ✔ (0x2000 = 128) |
| 0x34 | u8 | pad | |
| 0x35 | char[64] | fontName | ✔ |
| 0x75 | char[64] | fontType | "Regular", "Bold Italic"… ✔ |
| 0xB5 | u8 | pad | |
| 0xB6 | u16 | firstGlyph | primeiro código do charmap ✔ (0x20) |
| 0xB8 | u16 | lastGlyph | ✔ (ltn0: 0x2265; jpn0: 0xFFFF) |
| 0xBA | u8[26] | pad | |
| 0xD4 | s32 | maxAscender | 26.6 |
| 0xD8 | s32 | maxDescender | 26.6 (negativo) |
| 0xDC | s32 | maxLeftXAdjust | |
| 0xE0 | s32 | maxBaseYAdjust | |
| 0xE4 | s32 | minCenterXAdjust | |
| 0xE8 | s32 | maxTopYAdjust | |
| 0xEC | s32 ×2 | maxAdvance (H, V) | |
| 0xF4 | s32 ×2 | maxSize (W, H) | |
| 0xFC | u16 ×2 | maxGlyphWidth, maxGlyphHeight | em pixels (ltn0: 23 × 20) |
| 0x100 | u8[2] | pad | |
| 0x102 | u8 | dimTableLength | ✔ |
| 0x103 | u8 | xAdjustTableLength | ✔ |
| 0x104 | u8 | yAdjustTableLength | ✔ |
| 0x105 | u8 | advanceTableLength | ✔ |
| 0x106 | u8[102] | pad | |
| 0x16C | s32 | shadowMapLength | ✔ (ltn0: 324) |
| 0x170 | s32 | shadowMapBpe | ✔ (16) |
| 0x174 | f32/s32 | unknown | |
| 0x178 | s32 ×2 | shadowScale (x, y) | 0x20, 0x20 (a confirmar o uso) |
| 0x180 | u8[8] | pad | |
| 0x188 | — | (rev 3) extensão de 0x14 bytes | `compCharMapBpe1, compCharMapLength1, compCharMapBpe2, compCharMapLength2, unknown` segundo PPSSPP; em kr0 os bytes são `10 00 00 00 05 00 04 00 10 00 00 00 02 00 04 00 03 00 00 00`, o que não fecha como 5×s32 — layout exato **a confirmar** (comparar com `PGF::ReadPtr` do PPSSPP) |

Mapeamento para `SceFontInfo`: `maxGlyph*I` = campos 26.6 acima; `maxGlyph*F` = I / 64f; `maxGlyphWidth/Height`
= 0xFC/0xFE; `charMapLength` = 0x10; `shadowMapLength` = 0x16C; `BPP` = 0x22; correspondência exata de cada `I` com
cada campo do header a confirmar contra o LLE (PPSSPP: `maxGlyphWidthI = maxSize[0]`, `maxGlyphHeightI = maxSize[1]`,
`maxGlyphAscenderI = maxAscender`, `maxGlyphDescenderI = maxDescender`, `maxGlyphLeftXI = maxLeftXAdjust`,
`maxGlyphBaseYI = maxBaseYAdjust`, `minGlyphCenterXI = minCenterXAdjust`, `maxGlyphTopYI = maxTopYAdjust`,
`maxGlyphAdvanceXI/YI = maxAdvance[0/1]`).

**Corpo, em sequência logo após `headerSize`:**

1. Quatro tabelas de métricas, cada entrada = par `s32 (H, V)` em 26.6 ✔: `dimTable[dimTableLength]` (largura,
   altura), `xAdjustTable[...]` (bearingHX, bearingVX), `yAdjustTable[...]` (bearingHY, bearingVY),
   `advanceTable[...]` (advanceH, advanceV). Ex. `A` de ltn0: dim (936, 916), xAdj (18, −468), yAdj (896, 18),
   adv (972, 1094) — coerente com bitmap 15×15, top 14 ✔.
2. `shadowCharMap`: `shadowMapLength` entradas de `shadowMapBpe` bits, empacotadas LSB-first, alinhado a 4 bytes ✔.
   Cada entrada é um **código de caractere** (ver sombras).
3. (rev 3) tabelas do charmap comprimido — a confirmar.
4. `charMap`: `charMapLength` entradas de `charMapBpe` bits, alinhado a 4 bytes ✔. Índice = `code − firstGlyph`;
   valor = índice do glifo. Valor ≥ `charPointerLength` (ex. `(1<<bpe)−1` = 16383 em jpn0) = caractere ausente ✔.
5. `charPointerTable`: `charPointerLength` entradas de `charPointerBpe` bits, alinhado a 4 bytes ✔. Valor × 4 =
   offset em bytes do glifo dentro da área de glifos ✔ (último ponteiro × 4 cai a 40–164 bytes do fim do arquivo).
6. Área de glifos até o fim do arquivo.

Leitura de bits: fluxo LSB-first (`bit i` = `(data[pos>>3] >> (pos&7)) & 1`, montando o valor a partir do bit 0) ✔.

**Glifo de caractere** (posição de bit `p = ptr*4*8` dentro da área de glifos) ✔:

| Bits | Campo |
| --- | --- |
| 14 | `size`: tamanho total do glifo de caractere em bytes (o RLE termina em `size*8`, arredondado ao byte) |
| 7 | `width` (pixels do bitmap) |
| 7 | `height` |
| 7 | `left` (com sinal: ≥ 64 ⇒ −128) |
| 7 | `top` (com sinal: idem) |
| 6 | `flags`: bits 0–1 = layout do bitmap (1 = linhas horizontais/row-major, 2 = colunas/column-major; 3 = overlay, a confirmar), bit 2 = dimensão por índice, bit 3 = xAdjust por índice, bit 4 = yAdjust por índice, bit 5 = advance por índice |
| 7 | `shadowFlags` (composto; PPSSPP decompõe em 2+2+3 bits — a confirmar) |
| 9 | `shadowId` |
| 8 ou 64, ×4 | métricas na ordem dim, xAdjust, yAdjust, advance: se o bit de `flags` estiver ligado, índice de 8 bits na tabela correspondente; senão, dois `s32` crus (H, V) |
| resto | bitmap RLE 4 bpp |

**RLE** ✔: ler nibble `n` (4 bits). Se `n < 8`: ler um valor de 4 bits e repeti-lo `n + 1` vezes. Se `n ≥ 8`: ler
`16 − n` valores literais de 4 bits. Repetir até `width*height` pixels. Ordem: layout 1 ⇒ `pix[y*w + x]`; layout 2 ⇒
`pix[x*h + y]`.

**Sombras** ✔: o glifo de sombra fica imediatamente depois do glifo de caractere **do caractere
`shadowCharMap[shadowId]`** (posição `p + size*8`). Ex. em jpn0, `一` (U+4E00) tem `shadowId` 310 →
`shadowCharMap[310]` = U+30FC (`ー`), ou seja, compartilha a sombra de `ー`. O header da sombra tem só os 48 bits
iniciais (`size, w, h, left, top, flags`), sem campos de sombra nem métricas, e o RLE começa no bit 48 (sombra de `A`:
15×15, left −3, top 10, flags 1 — um borrão do `A`) ✔. A sombra é exposta pela API do PSP (`sceFontGetShadow*`), que
não existe no Vita; aqui ela só importa para preencher `shadowFlags`/`shadowId` de `SceFontCharInfo`.

### Estruturas (ScePgf, `psp2/pgf.h`)

`SceFontNewLibParams` (0x2C): 0x00 `userData`, 0x04 `numFonts`, 0x08 `cacheData`, 0x0C `allocFunc(userData, size)`,
0x10 `freeFunc(userData, ptr)`, 0x14 `openFunc(userData, fileName, int *err)`, 0x18 `closeFunc(userData, fh)`,
0x1C `readFunc(userData, fh, buf, unitSize, count, int *err)`, 0x20 `seekFunc(userData, fh, absOffset)`,
0x24 `errorFunc(userData, err)`, 0x28 `ioFinishFunc(userData, status)` (assinaturas dos callbacks do Vita3K).

`SceFontStyle` (0xA8): 0x00 `fontH` f32, 0x04 `fontV` f32, 0x08 `fontHRes` f32, 0x0C `fontVRes` f32, 0x10 `fontWeight`
f32, 0x14 `fontFamily` u16, 0x16 `fontStyle` u16, 0x18 `fontStyleSub` u16, 0x1A `fontLanguage` u16, 0x1C `fontRegion`
u16, 0x1E `fontCountry` u16, 0x20 `fontName[64]`, 0x60 `fontFileName[64]`, 0xA0 `fontAttributes` u32, 0xA4
`fontExpire` u32.

`SceFontInfo` (0x108): 0x00–0x24 dez `u32` 26.6 (`maxGlyphWidthI, HeightI, AscenderI, DescenderI, LeftXI, BaseYI,
minGlyphCenterXI, maxGlyphTopYI, AdvanceXI, AdvanceYI`), 0x28–0x4C os mesmos em `f32`, 0x50 `maxGlyphWidth` s16,
0x52 `maxGlyphHeight` s16, 0x54 `charMapLength` u32, 0x58 `shadowMapLength` u32, 0x5C `SceFontStyle fontStyle`
(0xA8), 0x104 `BPP` u8, 0x105 pad[3].

`SceFontCharInfo` (0x3C): 0x00 `bitmapWidth`, 0x04 `bitmapHeight`, 0x08 `bitmapLeft`, 0x0C `bitmapTop`, 0x10
`sfp26Width`, 0x14 `sfp26Height`, 0x18 `sfp26Ascender`, 0x1C `sfp26Descender`, 0x20 `sfp26BearingHX`, 0x24
`sfp26BearingHY`, 0x28 `sfp26BearingVX`, 0x2C `sfp26BearingVY`, 0x30 `sfp26AdvanceH`, 0x34 `sfp26AdvanceV`, 0x38
`shadowFlags` s16, 0x3A `shadowId` s16.

`SceFontGlyphImage` (0x18): 0x00 `pixelFormat` (`SceFontPixelFormatCode`: 0 = 4 bpp natural, 1 = 4 bpp reverso,
2 = 8 bpp, 3 = 24 bpp, 4 = 32 bpp), 0x04 `xPos64` s32 (26.6), 0x08 `yPos64` s32, 0x0C `bufWidth` u16, 0x0E `bufHeight`
u16, 0x10 `bytesPerLine` u16, 0x12 pad, 0x14 `bufferPtr` u32.

`SceFontImageRect` (4): 0x00 `width` u16, 0x02 `height` u16.

---

### sceFontNewLib
- [x] **Prioridade:** P2 · **Biblioteca:** ScePgf · **NID:** 0x1055ABA3
- **Protótipo:** `SceFontLibHandle sceFontNewLib(SceFontNewLibParams *params, unsigned int *errorCode);` (`psp2/pgf.h`)
- **Comportamento:** cria uma instância da biblioteca de fontes. Copia `params` (callbacks e `numFonts` = máximo de
  fontes abertas simultaneamente nesta instância), aloca a memória interna pelo `allocFunc` do jogo e zera
  resolução/alt char para os defaults. Grava o código de erro em `*errorCode`.
- **Retorno/erros:** handle não nulo e `*errorCode = 0`. `params == NULL` ou `errorCode == NULL` ⇒ retorna 0 com
  `SCE_FONT_ERROR_INVALID_PARAMETER` (0x80460003) (se `errorCode` for NULL, não escrever). Falha do `allocFunc`
  (retornou 0) ⇒ 0 e `SCE_FONT_ERROR_OUT_OF_MEMORY` (0x80460001). `numFonts == 0`: a confirmar via LLE.
- **Estruturas:** `SceFontNewLibParams` (acima).
- **Implementação no VitaSharp:** `Pgf.cs`, `uint sceFontNewLib(uint params, Ptr<uint> errorCode)`. Ler os 11 campos,
  alocar o handle via `allocFunc` (ver "Convenções de handle"), registrar `FontLib` em `_libs[handle]`.
- **Validação:** homebrew mínimo (VitaSDK) que chama `sceSysmoduleLoadModule(SCE_SYSMODULE_PGF)` +
  `sceFontNewLib` com allocator que conta chamadas; comparar HLE × LLE (`libpgf.suprx`) quanto a retorno, número
  e tamanho das alocações.

### sceFontDoneLib
- [x] **Prioridade:** P2 · **Biblioteca:** ScePgf · **NID:** 0x07EE1733
- **Protótipo:** `int sceFontDoneLib(SceFontLibHandle libHandle);` (`psp2/pgf.h`)
- **Comportamento:** destrói a instância: fecha fontes ainda abertas dela e devolve a memória pelo `freeFunc`.
- **Retorno/erros:** 0. Handle desconhecido ⇒ `SCE_FONT_ERROR_INVALID_LIBID` (0x80460002).
- **Implementação no VitaSharp:** remover de `_libs`, fechar `OpenFont`s, `CallGuest(freeFunc, userData, handle)`.
- **Validação:** mesmo homebrew; contador de `free` igual ao de `alloc` após `DoneLib`.

### sceFontGetNumFontList
- [x] **Prioridade:** P2 · **Biblioteca:** ScePgf · **NID:** 0x9F842307
- **Protótipo:** `int sceFontGetNumFontList(SceFontLibHandle libHandle, unsigned int *errorCode);` (`psp2/pgf.h`)
- **Comportamento:** número de fontes do sistema registradas (as que `sceFontOpen` aceita por índice).
- **Retorno/erros:** contagem (≥ 0) e `*errorCode = 0`; lib inválida ⇒ retorna `SCE_FONT_ERROR_INVALID_LIBID`
  (0x80460002) no retorno e/ou em `*errorCode` — qual dos dois a confirmar via LLE. Valor esperado: 18 se igual ao
  PSP (ltn0–15, jpn0, kr0) ou 19 incluindo `arib.pgf` — **a confirmar** via LLE.
- **Implementação no VitaSharp:** `SystemFonts.Count`, contando só arquivos que existem em `sa0:data/font/pgf/`
  (a confirmar se a lib real lista fontes ausentes).
- **Validação:** LLE no homebrew de teste, imprimindo o valor.

### sceFontGetFontList
- [x] **Prioridade:** P2 · **Biblioteca:** ScePgf · **NID:** 0xD56DCCEA
- **Protótipo:** `int sceFontGetFontList(SceFontLibHandle libHandle, SceFontStyle *fontStyle, int numFonts);`
  (`psp2/pgf.h`)
- **Comportamento:** preenche `min(numFonts, total)` entradas `SceFontStyle` (0xA8 cada) com o estilo de cada fonte
  do sistema, na ordem dos índices de `sceFontOpen`.
- **Retorno/erros:** 0; lib inválida ⇒ 0x80460002; `fontStyle == NULL` ou `numFonts < 0` ⇒ 0x80460003.
- **Estruturas:** `SceFontStyle`. Por fonte: `fontH/fontV` = `hSize/64`, `fontHRes/VRes` = `hResolution/64` (128),
  `fontWeight` (a confirmar), `fontFamily` 1/2, `fontStyle` (Regular 1, Italic 2, Bold 5, Bold Italic 6 —
  `SceFontStyleCode`), `fontLanguage` (ltn 2, jpn 1, kr 3), `fontRegion`/`fontCountry` (a confirmar),
  `fontName` do header, `fontFileName` = `"ltn0.pgf"` (só nome ou caminho completo: a confirmar),
  `fontAttributes`/`fontExpire` 0 (a confirmar).
- **Implementação no VitaSharp:** tabela `SystemFonts` gerada uma vez lendo o header de cada `.pgf`; os campos não
  deriváveis do header (weight, region, country, attributes) devem vir de um dump do LLE e ficar hardcoded.
- **Validação:** dump hexadecimal das N×0xA8 bytes no LLE vs HLE (devem ser idênticos).

### sceFontGetFontInfoByIndexNumber
- [x] **Prioridade:** P2 · **Biblioteca:** ScePgf · **NID:** 0xAB034738
- **Protótipo:** `int sceFontGetFontInfoByIndexNumber(SceFontLibHandle libHandle, SceFontStyle *fontStyle, int unknown, int fontIndex);` (`psp2/pgf.h`; o terceiro parâmetro é "unknown" no header)
- **Comportamento:** copia o `SceFontStyle` da fonte do sistema `fontIndex` para `*fontStyle`. O `unknown` não tem
  semântica conhecida (no PSP a assinatura era `(lib, style*, index)`; no Vita há um argumento a mais — a confirmar
  desmontando `libpgf.suprx` se ele é ignorado ou é o índice real).
- **Retorno/erros:** 0; lib inválida 0x80460002; índice fora do intervalo ou ponteiro nulo 0x80460003.
- **Implementação no VitaSharp:** mesma tabela de `sceFontGetFontList`; logar `unknown` (`Log.WarnOnce`) até ser
  entendido.
- **Validação:** LLE com `unknown` = 0 e ≠ 0 e índices variados.

### sceFontFindOptimumFont
- [x] **Prioridade:** P2 · **Biblioteca:** ScePgf · **NID:** 0x8DFBAE1B
- **Protótipo:** `int sceFontFindOptimumFont(SceFontLibHandle libHandle, SceFontStyle *fontStyle, unsigned int *errorCode);` (`psp2/pgf.h`)
- **Comportamento:** devolve o índice da fonte do sistema que melhor casa com os critérios em `*fontStyle`. Campos
  zerados/vazios são ignorados (curinga). Sempre devolve algum índice válido (melhor pontuação; empate ⇒ menor
  índice). Critérios no PSP (PPSSPP): `fontFamily`, `fontStyle`, `fontLanguage`, `fontCountry`, `fontName`,
  `fontFileName`, e proximidade de `fontH/fontV` (escolhe entre 10,125 pt e 7 pt). Pesos exatos a confirmar via LLE.
- **Retorno/erros:** índice ≥ 0, `*errorCode = 0`; lib inválida ⇒ 0x80460002 (no retorno e em `*errorCode`, a
  confirmar); `fontStyle == NULL` ⇒ 0x80460003.
- **Implementação no VitaSharp:** função de pontuação sobre `SystemFonts`; manter numa classe `FontMatcher`
  reutilizável pela Pvf.
- **Validação:** bateria de estilos (família × estilo × tamanho) no LLE e HLE; índices iguais.

### sceFontFindFont
- [x] **Prioridade:** P2 · **Biblioteca:** ScePgf · **NID:** 0x51061D87
- **Protótipo:** `int sceFontFindFont(SceFontLibHandle libHandle, SceFontStyle *fontStyle, unsigned int *errorCode);` (`psp2/pgf.h`)
- **Comportamento:** como `FindOptimumFont`, mas só aceita casamento exato de todos os campos não-curinga; sem
  casamento devolve −1 (PSP/PPSSPP) com `*errorCode = 0` (a confirmar no Vita).
- **Retorno/erros:** índice ou −1; erros como `FindOptimumFont`.
- **Implementação no VitaSharp:** `FontMatcher.Exact`.
- **Validação:** mesma bateria, incluindo um estilo inexistente (família 3 = rounded).

### sceFontOpen
- [x] **Prioridade:** P2 · **Biblioteca:** ScePgf · **NID:** 0xBD2DFCFF
- **Protótipo:** `SceFontHandle sceFontOpen(SceFontLibHandle libHandle, int index, int mode, unsigned int *errorCode);` (`psp2/pgf.h`)
- **Comportamento:** abre a fonte do sistema `index`. `mode` escolhe entre ler o arquivo sob demanda (0, usando o
  driver de I/O) e carregar o arquivo inteiro na memória (1) — no PSP era assim; a confirmar no Vita. Conta contra
  `numFonts` da lib.
- **Retorno/erros:** handle e `*errorCode = 0`. Lib inválida ⇒ 0 + 0x80460002. Índice inválido ⇒ 0 + 0x80460003.
  Já há `numFonts` abertas ⇒ 0 + `SCE_FONT_ERROR_TOO_MANY_OPEN_FONTS` (0x80460009). Arquivo ausente ⇒ 0 +
  `SCE_FONT_ERROR_HANDLER_OPEN_FAILED` (0x80460005) (ou 0x80460004, a confirmar). Header inválido ⇒ 0 +
  `SCE_FONT_ERROR_INVALID_FONT_DATA` (0x8046000A).
- **Implementação no VitaSharp:** resolver `sa0:data/font/pgf/<arquivo>` via `Emu.Vfs.Resolve`, carregar `PgfFont`
  (cache estático por caminho, os arquivos são imutáveis), alocar handle guest. Ignorar `mode` (sempre ler tudo no
  host), mas se o modo 1 da lib real aloca o tamanho do arquivo via `allocFunc`, replicar a alocação (a confirmar).
- **Validação:** abrir índices 0..N−1 e N; abrir `numFonts+1` fontes; comparar com LLE.

### sceFontOpenUserFile
- [x] **Prioridade:** P2 · **Biblioteca:** ScePgf · **NID:** 0xE260E740
- **Protótipo:** `SceFontHandle sceFontOpenUserFile(SceFontLibHandle libHandle, char *file, int mode, unsigned int *errorCode);` (`psp2/pgf.h`)
- **Comportamento:** abre um `.pgf` arbitrário (ex. `app0:font/x.pgf`). Mesmo `mode` de `sceFontOpen`. Se a lib tiver
  `openFunc/readFunc/...`, a lib real lê o arquivo por esses callbacks (a confirmar); caso contrário usa `sceIo*`.
- **Retorno/erros:** como `sceFontOpen`; `file == NULL` ⇒ 0x80460003.
- **Implementação no VitaSharp:** ler o arquivo pelo `Vfs` (com overlays de `app0`). Se o jogo forneceu callbacks de
  I/O e o LLE mostrar que eles são chamados, chamar `openFunc/readFunc/closeFunc` pelo `CallGuest` para jogos que
  usam arquivos empacotados (ex. dentro de um arquivo próprio).
- **Validação:** homebrew com um `.pgf` em `app0:`; caminho inexistente.

### sceFontOpenUserMemory
- [x] **Prioridade:** P2 · **Biblioteca:** ScePgf · **NID:** 0xB23ED47C
- **Protótipo:** `SceFontHandle sceFontOpenUserMemory(SceFontLibHandle libHandle, void *pMemoryFont, SceSize pMemoryFontSize, unsigned int *errorCode);` (`psp2/pgf.h`)
- **Comportamento:** abre um PGF já carregado na memória do jogo. A lib real usa os dados in-place (o jogo não pode
  liberar o buffer antes de `sceFontClose`).
- **Retorno/erros:** como `sceFontOpen`; ponteiro nulo ou tamanho < header ⇒ 0x80460003; magic errado ⇒ 0x8046000A.
- **Implementação no VitaSharp:** copiar `GuestMemory.Span(p, size)` para um `byte[]` e parsear (copiar é seguro
  porque o conteúdo não muda; se algum jogo alterar o buffer depois, rever).
- **Validação:** homebrew que carrega `ltn0.pgf` com `sceIoRead` e abre da memória; mesmos bitmaps do `sceFontOpen`.

### sceFontClose
- [x] **Prioridade:** P2 · **Biblioteca:** ScePgf · **NID:** 0x4A7293E9
- **Protótipo:** `int sceFontClose(SceFontHandle fontHandle);` (`psp2/pgf.h`)
- **Comportamento:** fecha a fonte, libera a vaga em `numFonts` e a memória interna (via `freeFunc`).
- **Retorno/erros:** 0; handle inválido ⇒ 0x80460003 (a confirmar; pode ser 0x80460002).
- **Implementação no VitaSharp:** remover de `_fonts` e da lista da lib; `freeFunc`.
- **Validação:** abrir `numFonts`, fechar uma, abrir outra com sucesso.

### sceFontSetResolution
- [x] **Prioridade:** P2 · **Biblioteca:** ScePgf · **NID:** 0xDE47674C
- **Protótipo:** `int sceFontSetResolution(SceFontLibHandle libHandle, float hRes, float vRes);` (`psp2/pgf.h`)
- **Comportamento:** define a resolução (dpi) usada pelas conversões ponto↔pixel da instância. Não muda os bitmaps
  (PGF é bitmap fixo).
- **Retorno/erros:** 0; lib inválida 0x80460002; valores ≤ 0 ⇒ 0x80460003 (a confirmar).
- **Implementação no VitaSharp:** `int sceFontSetResolution(uint lib, float hRes, float vRes)` (floats chegam em s0/s1).
- **Validação:** `SetResolution(72,72)` e `sceFontPointToPixelH(10)` = 10.

### Conversões ponto ↔ pixel (ScePgf)

Fórmulas (PSP/PPSSPP; a confirmar): `pixel = point * res / 72`, `point = pixel * 72 / res`, com `res` = `hRes` (H) ou
`vRes` (V) da instância. Todas retornam `float` (s0) e escrevem `*errorCode` (0 ou 0x80460002 para lib inválida,
retornando 0,0f). Implementação: quatro métodos `float` em `Pgf.cs` com `Ptr<uint> errorCode`. Validação: LLE com
resolução default e após `SetResolution`.

| Função | NID | Protótipo (`psp2/pgf.h`) | Comportamento |
| --- | --- | --- | --- |
| - [x] sceFontPixelToPointH | 0x9E38F4D6 | `float sceFontPixelToPointH(SceFontLibHandle libHandle, float fontPixelsH, unsigned int *errorCode);` | `fontPixelsH * 72 / hRes` |
| - [x] sceFontPixelToPointV | 0x7B45E2D1 | `float sceFontPixelToPointV(SceFontLibHandle libHandle, float fontPixelsV, unsigned int *errorCode);` | `fontPixelsV * 72 / vRes` |
| - [x] sceFontPointToPixelH | 0x39B9AEFF | `float sceFontPointToPixelH(SceFontLibHandle libHandle, float fontPointsH, unsigned int *errorCode);` | `fontPointsH * hRes / 72` |
| - [x] sceFontPointToPixelV | 0x03F10EC8 | `float sceFontPointToPixelV(SceFontLibHandle libHandle, float fontPointsV, unsigned int *errorCode);` | `fontPointsV * vRes / 72` |

Prioridade de todas: P2 · Biblioteca: ScePgf.

### sceFontSetAltCharacterCode
- [x] **Prioridade:** P2 · **Biblioteca:** ScePgf · **NID:** 0x8D5B44DF
- **Protótipo:** `int sceFontSetAltCharacterCode(SceFontLibHandle libHandle, unsigned int charCode);` (`psp2/pgf.h`)
- **Comportamento:** define o caractere usado no lugar de códigos sem glifo em `GetCharInfo`/`GetCharGlyphImage*`/
  `GetCharImageRect` das fontes dessa instância.
- **Retorno/erros:** 0; lib inválida 0x80460002.
- **Implementação no VitaSharp:** `lib.AltChar = charCode`. Default a confirmar via LLE (pedir info de um código
  ausente, ex. U+E000, sem ter chamado esta função).
- **Validação:** como acima, antes e depois de setar.

### sceFontFlush
- [x] **Prioridade:** P2 · **Biblioteca:** ScePgf · **NID:** 0x7D8CB13B
- **Protótipo:** `int sceFontFlush(SceFontHandle fontHandle);` (`psp2/pgf.h`)
- **Comportamento:** descarta o cache de glifos da fonte (`cacheData` da lib). Sem efeito visível.
- **Retorno/erros:** 0; handle inválido 0x80460003 (a confirmar).
- **Implementação no VitaSharp:** validar handle e retornar 0.
- **Validação:** chamada entre renders não muda o resultado.

### sceFontGetFontInfo
- [x] **Prioridade:** P2 · **Biblioteca:** ScePgf · **NID:** 0xF9414FA2
- **Protótipo:** `int sceFontGetFontInfo(SceFontHandle fontHandle, SceFontInfo *fontInfo);` (`psp2/pgf.h`)
- **Comportamento:** preenche `SceFontInfo` (0x108 bytes) com as métricas máximas do header PGF e o `SceFontStyle` da
  fonte (para fonte de usuário: estilo derivado do header, `fontFileName` vazio — a confirmar).
- **Retorno/erros:** 0; handle inválido 0x80460003; `fontInfo == NULL` 0x80460003.
- **Estruturas:** `SceFontInfo` e mapeamento na seção do formato.
- **Implementação no VitaSharp:** `PgfFont.FillInfo(Span<byte>)`; escrever com `GuestMemory.W32`/`Span`.
- **Validação:** diff byte a byte LLE × HLE para ltn0, ltn8, jpn0, kr0.

### sceFontGetCharInfo
- [x] **Prioridade:** P2 · **Biblioteca:** ScePgf · **NID:** 0x6FD1BA65
- **Protótipo:** `int sceFontGetCharInfo(SceFontHandle fontHandle, unsigned int charCode, SceFontCharInfo *charInfo);` (`psp2/pgf.h`)
- **Comportamento:** métricas de um caractere. `bitmapWidth/Height/Left/Top` = campos do glifo (left/top com sinal
  gravados em `unsigned int`); `sfp26*` = valores das tabelas (H/V de dim, xAdjust, yAdjust, advance);
  `sfp26Ascender/Descender` (a confirmar a origem: PPSSPP usa `bearingHY` e `bearingHY − height`); `shadowFlags`,
  `shadowId` do glifo. Código fora do charmap ⇒ usa o alt char da lib.
- **Retorno/erros:** 0; handle inválido/ponteiro nulo 0x80460003. Nem o alt char existe: a confirmar (PSP zerava
  a struct e retornava 0).
- **Estruturas:** `SceFontCharInfo`.
- **Implementação no VitaSharp:** `PgfFont.TryGetGlyph(code, out Glyph)` com cache de glifos decodificados.
- **Validação:** dump de `0x20..0x7E` e alguns kana em LLE × HLE.

### sceFontGetCharImageRect
- [x] **Prioridade:** P2 · **Biblioteca:** ScePgf · **NID:** 0x70C86B3E
- **Protótipo:** `int sceFontGetCharImageRect(SceFontHandle fontHandle, unsigned int charCode, SceFontImageRect *charRect);` (`psp2/pgf.h`)
- **Comportamento:** tamanho em pixels do bitmap do glifo (`width`, `height` u16); o jogo usa para dimensionar o
  buffer antes de `GetCharGlyphImage`.
- **Retorno/erros:** 0; handle inválido/ponteiro nulo 0x80460003.
- **Implementação no VitaSharp:** `glyph.Width/Height` (com alt char).
- **Validação:** igual ao `bitmapWidth/Height` de `GetCharInfo`.

### sceFontGetCharGlyphImage
- [x] **Prioridade:** P2 · **Biblioteca:** ScePgf · **NID:** 0xAB45AAD3
- **Protótipo:** `int sceFontGetCharGlyphImage(SceFontHandle fontHandle, unsigned int charCode, SceFontGlyphImage *glyphImage);` (`psp2/pgf.h`)
- **Comportamento:** desenha o glifo no buffer descrito por `glyphImage`. Posição do canto superior esquerdo:
  `x = xPos64 >> 6`, `y = yPos64 >> 6` (PPSSPP; a parte fracionária é ignorada, e se a posição é a da origem/baseline
  ou do canto do bitmap é **a confirmar** via LLE). Pixels fora de `bufWidth × bufHeight` são descartados. Conversão
  do valor 4 bpp `v` (0–15) por formato: 0 = nibble em ordem natural (pixel par no nibble baixo, a confirmar),
  1 = ordem invertida, 2 = `v * 0x11`, 3 = RGB cinza `v*0x11` nos 3 bytes, 4 = RGBA (a confirmar se é `0xFFFFFF` com
  alfa `v*0x11` ou cinza com alfa). Se a escrita é sobrescrita pura ou só para `v != 0` (preservando o fundo) é **a
  confirmar** (PPSSPP só escreve quando o pixel é visível em parte dos formatos).
- **Retorno/erros:** 0; handle inválido/`glyphImage` nulo/`bufferPtr` 0 ⇒ 0x80460003; `pixelFormat > 4` ⇒ 0x80460003.
- **Estruturas:** `SceFontGlyphImage`.
- **Implementação no VitaSharp:** `PgfRenderer.Blit(glyph, guestBuffer, fmt, x, y, bufW, bufH, bpl, clip)` em
  `Hle/Font/`; escrever direto na memória guest (`GuestMemory.Span`).
- **Validação:** renderizar "Hello" em buffer 8 bpp preenchido com 0xAA no LLE e HLE e comparar o buffer inteiro
  (pega posição, ordem de nibble e política de fundo de uma vez); repetir para os 5 formatos.

### sceFontGetCharGlyphImage_Clip
- [x] **Prioridade:** P2 · **Biblioteca:** ScePgf · **NID:** 0xEB589530
- **Protótipo:** `int sceFontGetCharGlyphImage_Clip(SceFontHandle fontHandle, unsigned int charCode, SceFontGlyphImage *glyphImage, int clipXPos, int clipYPos, int clipWidth, int clipHeight);` (`psp2/pgf.h`; 3 últimos args na pilha)
- **Comportamento:** igual a `GetCharGlyphImage`, mas só escreve pixels dentro do retângulo de recorte
  (`clipXPos, clipYPos, clipWidth, clipHeight`, em coordenadas do buffer — a confirmar se são relativas ao buffer
  ou ao glifo).
- **Retorno/erros:** como acima; recorte negativo: a confirmar.
- **Implementação no VitaSharp:** mesmo `Blit` com retângulo de clip.
- **Validação:** comparação de buffer como acima, com clip parcial cortando o glifo ao meio.

---

## ScePvf (43 funções)

### Arquitetura (ScePvf)

- **Objetivo:** rasterizar fontes OpenType (`.pvf` = OTF/CFF, magic `OTTO`, confirmado em `ltn0.pvf` e `jpn0.pvf`) em
  qualquer tamanho, com negrito sintético (embolden), itálico sintético (skew), kerning, layout vertical e acesso a
  contornos. O próprio `libpvf` real é um wrapper sobre FreeType (`libSceFt2`).
- **Estado interno:**
  - `PvfLib` (por `scePvfNewLib`): `ScePvfInitRec` (userData, `maxNumFonts`, `cache`, alloc/realloc/free), `em`
    (`scePvfSetEM`), `hRes/vRes` (`scePvfSetResolution`), `altChar`, fontes abertas. Defaults (EM, resolução,
    alt char) **a confirmar** via LLE (`scePvfPointToPixelH(lib, 72)` revela a resolução default).
  - `PvfFont` (handle): face carregada (arquivo + subfont index), `charSizeH/V` (pt), `emboldenRate` (−20..40),
    `skewX/Y` (−30..30 graus), cache de glifos rasterizados.
  - Tabela `SystemPvfFonts` (índice → arquivo, `ScePvfFontStyleInfo`). Candidatos: os 20 arquivos de
    `sa0:data/font/pvf/`; quais entram na lista e em que ordem é **a confirmar** via `scePvfGetFontList` no LLE
    (provável: ltn0–7, jpn0–3, kr0–3, cn0–1; `music_arib`/`psexchar` talvez fora).
- **Renderizador (opções):**

  | Opção | Licença / dependência | Prós | Contras |
  | --- | --- | --- | --- |
  | **FreeTypeSharp** (wrapper P/Invoke) | wrapper MIT; FreeType sob FTL (BSD-like) ou GPLv2; traz binários nativos win/linux/mac no NuGet | mesmo motor do `libSceFt2` ⇒ métricas, hinting e embolden (`FT_Outline_Embolden`), skew (`FT_Outline_Transform`), kerning (`FT_Get_Kerning`) e contornos (`FT_Outline_Decompose`) mapeiam 1:1 | dependência nativa (o projeto já tolera: FFmpeg); versão/flags de hinting da Sony desconhecidas |
  | SixLabors.Fonts (+ ImageSharp.Drawing para rasterizar) | Six Labors Split License (Apache 2.0 para open source/receita < US$1M; comercial acima) | 100% gerenciado; lê CFF/OTF (v2+); kerning GPOS | rasterização exige ImageSharp.Drawing (dependência pesada) ou rasterizador próprio; métricas e AA diferentes do FreeType; licença menos simples |
  | StbTrueTypeSharp | domínio público/MIT, 100% gerenciado, sem dependência | leve; stb_truetype lê CFF | AA e métricas mais distantes; sem hinting; embolden/skew manuais |

  **Recomendação:** FreeTypeSharp para o HLE (proximidade com o hardware compensa a DLL nativa, que vai junto do
  FFmpeg em `third_party`); o LLE do firmware continua sendo o caminho exato.
- **Arquivo novo:** `src/VitaSharp.Core/Hle/Modules/Pvf.cs` (`public sealed class Pvf : HleModule`) +
  `src/VitaSharp.Core/Hle/Font/PvfRenderer.cs` (encapsula FreeType). `FontMatcher` compartilhado com Pgf.
- **Dependências:** NuGet `FreeTypeSharp` no `VitaSharp.Core.csproj`; `Vfs` para `sa0:data/font/pvf`;
  `GuestThread.CallGuest` para `allocFunc/reallocFunc/freeFunc` e, se usado, a interface de cache
  (`ScePvfCacheSystemInterface`, 9 callbacks: lock/unlock/find/writeKeyValue/write0/write1/read0/read1 — o HLE pode
  ignorar o cache do jogo; a confirmar se algum jogo depende de ver os callbacks chamados).
- **NIDs:** todos os 43 nomes estão em `Hle/Data/nids.txt`.
- **Funções sem protótipo no `psp2/pvf.h`** (inferidos abaixo e marcados): `__scePvfSetFt2LibCHook`,
  `__scePvfSetFt2DoneLibCHook`, `scePvfGetCharGlyphOutline`, `scePvfReleaseCharGlyphOutline`,
  `scePvfGetVertCharInfo`, `scePvfGetVertCharImageRect`, `scePvfGetVertCharGlyphImage`,
  `scePvfGetVertCharGlyphImage_Clip`, `scePvfGetVertCharGlyphOutline`, `scePvfIsVertElement`. Como confirmar:
  desmontar `vs0:sys/external/libpvf.suprx` (decifrado pelo `SelfDecryptor`) e ver quantos registradores/pilha cada
  export lê, ou procurar por chamadas em jogos que importem esses NIDs.

### Estruturas (ScePvf, `psp2/pvf.h`)

`ScePvfInitRec` (0x1C): 0x00 `userData`, 0x04 `maxNumFonts`, 0x08 `cache` (`ScePvfCacheSystemInterface*`), 0x0C
`reserved`, 0x10 `allocFunc(userData, size)`, 0x14 `reallocFunc(userData, old, size)`, 0x18 `freeFunc(userData, ptr)`.

`ScePvfFontStyleInfo` (0xD8): 0x00 `weight` f32, 0x04 `familyCode` u16, 0x06 `style` u16, 0x08 `subStyle` u16
(bits: 1 vertical, 2 pseudo-bold, 4 pseudo-slant), 0x0A `languageCode` u16, 0x0C `regionCode` u16, 0x0E `countryCode`
u16, 0x10 `fontName[64]`, 0x50 `styleName[64]`, 0x90 `fileName[64]`, 0xD0 `extraAttributes` u32, 0xD4 `expireDate`
u32. Regras de busca documentadas no header: `weight <= 0`, campos de classificação 0 e strings vazias são
ignorados; `subStyle ≠ 0` compara como máscara inteira de 16 bits; `styleName`, `extraAttributes`, `expireDate`
não participam (FW 3.60).

`ScePvfIGlyphMetricsInfo` (0x28, 26.6): `width64, height64, ascender64, descender64, horizontalBearingX64,
horizontalBearingY64, verticalBearingX64, verticalBearingY64, horizontalAdvance64, verticalAdvance64` (offsets
0x00..0x24). `ScePvfFGlyphMetricsInfo` (0x28): os mesmos em `f32`.

`ScePvfCharInfo` (0x40): 0x00 `bitmapWidth`, 0x04 `bitmapHeight`, 0x08 `bitmapPitch`, 0x0C `bitmapLeft` s32,
0x10 `bitmapTop` s32, 0x14 `glyphMetrics` (`ScePvfIGlyphMetricsInfo`), 0x3C `reserved0[2]`, 0x3E `reserved1`.

`ScePvfFontInfo` (0x130): 0x00 `maxIGlyphMetrics`, 0x28 `maxFGlyphMetrics`, 0x50 `numChars` u32, 0x54
`fontStyleInfo` (0xD8), 0x12C `reserved[4]`.

`ScePvfUserImageBufferRec` (0x18): 0x00 `pixelFormat` (0 = `DIRECT4_L`, 2 = `DIRECT8`), 0x04 `xPos64`, 0x08 `yPos64`,
0x0C `rect` (`width` u16, `height` u16), 0x10 `bytesPerLine` u16, 0x12 `reserved`, 0x14 `buffer`.

`ScePvfKerningInfo` (0x10): 0x00 `iKerningInfo` (`xOffset64`, `yOffset64` s32), 0x08 `fKerningInfo` (`xOffset`,
`yOffset` f32).

`ScePvfIrect` (4): `width`, `height` u16. `ScePvfCharCode` é **u16** (só BMP; passado em r1 como inteiro).

---

### __scePvfSetFt2LibCHook
- [x] **Prioridade:** P2 · **Biblioteca:** ScePvf · **NID:** 0x997161C3
- **Protótipo:** sem protótipo no `psp2/pvf.h`. Inferido: `int __scePvfSetFt2LibCHook(void *hooks);` (tabela de
  ponteiros de funções libc — malloc/free/realloc/memcpy… — que o `libpvf` repassa ao `libSceFt2`). Assinatura e
  layout da tabela **a confirmar** desmontando `libpvf.suprx` ou um jogo que a importe.
- **Comportamento:** registra os hooks de libc usados internamente pelo FreeType. Chamado pelo CRT/SDK do jogo antes do
  primeiro `scePvfNewLib` (função com `__` = interna do SDK).
- **Retorno/erros:** 0 (a confirmar).
- **Implementação no VitaSharp:** HLE: guardar o ponteiro (para log) e retornar 0; o FreeType do host não usa os hooks.
- **Validação:** jogo/homebrew que a importe não deve travar; logar o argumento na primeira chamada.

### __scePvfSetFt2DoneLibCHook
- [x] **Prioridade:** P2 · **Biblioteca:** ScePvf · **NID:** 0x58142D71
- **Protótipo:** sem protótipo. Inferido: `int __scePvfSetFt2DoneLibCHook(void *hook);` (callback chamado no
  `DoneLib` do FreeType). A confirmar como acima.
- **Comportamento:** registra/limpa o hook de finalização da libc do FreeType.
- **Retorno/erros:** 0 (a confirmar).
- **Implementação no VitaSharp:** retornar 0.
- **Validação:** idem.

### scePvfNewLib
- [x] **Prioridade:** P2 · **Biblioteca:** ScePvf · **NID:** 0x72E58672
- **Protótipo:** `ScePvfLibId scePvfNewLib(ScePvfInitRec *initParam, ScePvfError *errorCode);` (`psp2/pvf.h`)
- **Comportamento:** cria a instância (como `sceFontNewLib`): copia `ScePvfInitRec`, aloca memória interna pelo
  `allocFunc`, inicializa FreeType, defaults de EM/resolução/alt char.
- **Retorno/erros:** lib id e `*errorCode = 0`. `initParam == NULL` ⇒ 0 + `SCE_PVF_ERROR_ARG` (0x80460003);
  `allocFunc` falha ⇒ 0 + `SCE_PVF_ERROR_NOMEMORY` (0x80460001). Limite de `maxNumFonts` (`SCE_PVF_MAX_OPEN` = 18?
  se é limite de abertura por lib ou validação de `maxNumFonts`: a confirmar).
- **Estruturas:** `ScePvfInitRec`.
- **Implementação no VitaSharp:** `Pvf.cs`; handle guest via `allocFunc`; `PvfRenderer.InitLibrary()` (um
  `FT_Library` por lib, ou um global protegido por lock).
- **Validação:** homebrew com allocator contador; LLE × HLE.

### scePvfDoneLib
- [x] **Prioridade:** P2 · **Biblioteca:** ScePvf · **NID:** 0xE17717EC
- **Protótipo:** `ScePvfError scePvfDoneLib(ScePvfLibId libID);` (`psp2/pvf.h`)
- **Comportamento:** fecha as fontes da lib, finaliza FreeType, libera memória via `freeFunc`.
- **Retorno/erros:** 0; lib inválida `SCE_PVF_ERROR_LIBID` (0x80460002).
- **Implementação no VitaSharp:** espelho de `sceFontDoneLib`.
- **Validação:** contador de alloc/free.

### scePvfSetEM
- [x] **Prioridade:** P2 · **Biblioteca:** ScePvf · **NID:** 0xDFB677C5
- **Protótipo:** `ScePvfError scePvfSetEM(ScePvfLibId libID, ScePvfFloat32 emValue);` (`psp2/pvf.h`)
- **Comportamento:** define o tamanho do EM (unidade de referência para converter tamanhos em pontos) da instância.
  Semântica exata (valor em pontos por EM? fator global de escala?) **a confirmar** via LLE: renderizar o mesmo
  glifo com EM default e EM×2 e comparar `bitmapWidth`.
- **Retorno/erros:** 0; lib inválida 0x80460002; `emValue <= 0` ⇒ 0x80460003 (a confirmar).
- **Implementação no VitaSharp:** `lib.Em = emValue`; entra no cálculo do `FT_Set_Char_Size`.
- **Validação:** como descrito.

### scePvfSetResolution
- [x] **Prioridade:** P2 · **Biblioteca:** ScePvf · **NID:** 0xC4444FB3
- **Protótipo:** `ScePvfError scePvfSetResolution(ScePvfLibId libID, ScePvfFloat32 hResolution, ScePvfFloat32 vResolution);` (`psp2/pvf.h`)
- **Comportamento:** dpi da instância; afeta o tamanho em pixels dos glifos (`FT_Set_Char_Size(face, h*64, v*64,
  hRes, vRes)`) e as conversões ponto↔pixel.
- **Retorno/erros:** 0; 0x80460002; valores ≤ 0 0x80460003 (a confirmar).
- **Implementação no VitaSharp:** `lib.HRes/VRes`; invalidar cache das fontes abertas.
- **Validação:** `PointToPixelH(72)` == `hResolution`.

### scePvfGetNumFontList
- [x] **Prioridade:** P2 · **Biblioteca:** ScePvf · **NID:** 0xF6C4A855
- **Protótipo:** `ScePvfInt scePvfGetNumFontList(ScePvfLibId libID, ScePvfError *errorCode);` (`psp2/pvf.h`)
- **Comportamento:** número de fontes PVF do sistema.
- **Retorno/erros:** contagem e `*errorCode = 0`; lib inválida ⇒ 0x80460002. Valor esperado **a confirmar** (20
  arquivos em `sa0:data/font/pvf/`).
- **Implementação no VitaSharp:** `SystemPvfFonts.Count`.
- **Validação:** LLE.

### scePvfGetFontList
- [x] **Prioridade:** P2 · **Biblioteca:** ScePvf · **NID:** 0x66F2D767
- **Protótipo:** `ScePvfError scePvfGetFontList(ScePvfLibId libID, ScePvfFontStyleInfo *fontStyleInfo, ScePvfInt arraySize);` (`psp2/pvf.h`)
- **Comportamento:** preenche até `arraySize` `ScePvfFontStyleInfo` (0xD8 cada) na ordem dos índices de `scePvfOpen`.
- **Retorno/erros:** 0; 0x80460002; ponteiro nulo/tamanho negativo 0x80460003.
- **Estruturas:** `ScePvfFontStyleInfo`. Campos derivados do arquivo: `fontName` (tabela `name` ID 4 ou 1, a
  confirmar), `styleName` (ID 2), `fileName` (`"ltn0.pvf"`?); família/estilo/idioma/região/país/weight: tabela
  hardcoded gerada a partir do dump do LLE.
- **Implementação no VitaSharp:** tabela estática + strings lidas via FreeType (`FT_Get_Sfnt_Name`).
- **Validação:** diff byte a byte LLE × HLE.

### scePvfGetFontInfoByIndexNumber
- [x] **Prioridade:** P2 · **Biblioteca:** ScePvf · **NID:** 0xF3E1E8BD
- **Protótipo:** `ScePvfError scePvfGetFontInfoByIndexNumber(ScePvfLibId libID, ScePvfFontStyleInfo *fontStyleInfo, ScePvfFontIndex fontIndex);` (`psp2/pvf.h`)
- **Comportamento:** copia o estilo da fonte `fontIndex`.
- **Retorno/erros:** 0; 0x80460002; índice inválido/ponteiro nulo 0x80460003.
- **Implementação no VitaSharp:** mesma tabela.
- **Validação:** igual à entrada correspondente de `GetFontList`.

### scePvfFindOptimumFont
- [x] **Prioridade:** P2 · **Biblioteca:** ScePvf · **NID:** 0x2761FEAC
- **Protótipo:** `ScePvfFontIndex scePvfFindOptimumFont(ScePvfLibId libID, ScePvfFontStyleInfo *fontStyleInfo, ScePvfError *errorCode);` (`psp2/pvf.h`)
- **Comportamento:** índice da fonte que melhor atende aos critérios (regras de curinga do header, ver Estruturas);
  sempre devolve um índice válido.
- **Retorno/erros:** índice, `*errorCode = 0`; 0x80460002; ponteiro nulo 0x80460003.
- **Implementação no VitaSharp:** `FontMatcher` (compartilhado com Pgf), adaptado a `ScePvfFontStyleInfo`
  (`weight > 0` compara proximidade).
- **Validação:** bateria de critérios no LLE × HLE (ex. `languageCode = 1` deve dar jpn0).

### scePvfFindFont
- [x] **Prioridade:** P2 · **Biblioteca:** ScePvf · **NID:** 0x984E5BFE
- **Protótipo:** `ScePvfFontIndex scePvfFindFont(ScePvfLibId libID, ScePvfFontStyleInfo *fontStyleInfo, ScePvfError *errorCode);` (`psp2/pvf.h`)
- **Comportamento:** casamento exato; sem resultado ⇒ −1 (a confirmar se com `*errorCode = 0` ou erro).
- **Retorno/erros:** índice ou −1; demais como acima.
- **Implementação no VitaSharp:** `FontMatcher.Exact`.
- **Validação:** critério impossível (família 3).

### Abertura de fontes (ScePvf)

Comum às 8 funções de abertura: retornam `ScePvfFontId` (0 em erro) e escrevem `*errorCode`. Erros: lib inválida
0x80460002; argumento nulo/índice inválido 0x80460003; arquivo inexistente `SCE_PVF_ERROR_NOFILE` (0x80460004) ou
`FILEOPEN` (0x80460005) (qual dos dois: a confirmar); leitura falhou 0x80460007; mais fontes abertas que
`maxNumFonts` ⇒ `SCE_PVF_ERROR_TOOMANYOPENED` (0x80460009); arquivo não-OpenType ⇒ `ILLEGALVERSION` (0x8046000A) ou
`DATAINCONSISTENT` (0x8046000B). Tamanho de caractere inicial após abrir: **a confirmar** via LLE
(`scePvfGetCharInfo` logo após abrir, sem `SetCharSize`). Implementação: `PvfRenderer.OpenFace(byte[] data, int
subIndex)` (`FT_New_Memory_Face`; manter o `byte[]` pinado enquanto a face existir), handle guest pelo `allocFunc`.
Validação: abrir cada fonte do sistema e conferir `GetFontInfo`; caminho inválido; `maxNumFonts + 1` aberturas.

#### scePvfOpen
- [x] **Prioridade:** P2 · **Biblioteca:** ScePvf · **NID:** 0xE35434BB
- **Protótipo:** `ScePvfFontId scePvfOpen(ScePvfLibId libID, ScePvfFontIndex fontIndex, ScePvfU32 mode, ScePvfError *errorCode);` (`psp2/pvf.h`)
- **Comportamento:** abre a fonte do sistema `fontIndex` de `sa0:data/font/pvf/`. `mode` = `ScePvfDataAccessMode`
  (0 = `SCE_PVF_FILEBASEDSTREAM`, lê do arquivo sob demanda; 1 = `SCE_PVF_MEMORYBASEDSTREAM`, carrega tudo).
- **Implementação no VitaSharp:** ignorar `mode` no host (carregar tudo); replicar a alocação do arquivo inteiro via
  `allocFunc` em modo 1 se o LLE mostrar que a lib real faz isso.

#### scePvfOpenUserFile
- [x] **Prioridade:** P2 · **Biblioteca:** ScePvf · **NID:** 0xD535520F
- **Protótipo:** `ScePvfFontId scePvfOpenUserFile(ScePvfLibId libID, ScePvfPointer filename, ScePvfU32 mode, ScePvfError *errorCode);` (`psp2/pvf.h`)
- **Comportamento:** abre um arquivo de fonte do jogo (`filename` é `char*` de caminho Vita). Aceita qualquer
  formato que o FreeType da Sony suporte (TTF/OTF/TTC; a confirmar se aceita TTF puro).
- **Implementação no VitaSharp:** ler pelo `Vfs` e abrir com subfont 0.

#### scePvfOpenUserFileWithSubfontIndex
- [x] **Prioridade:** P2 · **Biblioteca:** ScePvf · **NID:** 0x10452B86
- **Protótipo:** `ScePvfFontId scePvfOpenUserFileWithSubfontIndex(ScePvfLibId libID, ScePvfPointer filename, ScePvfU32 mode, ScePvfU32 subFontIndex, ScePvfError *errorCode);` (`psp2/pvf.h`; `errorCode` na pilha)
- **Comportamento:** como `OpenUserFile`, escolhendo a face `subFontIndex` de uma coleção (`.ttc`/`.otc`).
- **Retorno/erros:** índice inexistente ⇒ 0x80460003 (a confirmar).
- **Implementação no VitaSharp:** `FT_New_Memory_Face(..., subFontIndex)`.

#### scePvfOpenUserMemory
- [x] **Prioridade:** P2 · **Biblioteca:** ScePvf · **NID:** 0x9E65E4ED
- **Protótipo:** `ScePvfFontId scePvfOpenUserMemory(ScePvfLibId libID, ScePvfPointer addr, ScePvfU32 size, ScePvfError *errorCode);` (`psp2/pvf.h`)
- **Comportamento:** abre fonte já carregada pelo jogo em `addr` (usada in-place; o jogo mantém o buffer vivo).
- **Implementação no VitaSharp:** copiar para `byte[]` (conteúdo imutável).

#### scePvfOpenUserMemoryWithSubfontIndex
- [x] **Prioridade:** P2 · **Biblioteca:** ScePvf · **NID:** 0xA81570EB
- **Protótipo:** `ScePvfFontId scePvfOpenUserMemoryWithSubfontIndex(ScePvfLibId libID, ScePvfPointer addr, ScePvfU32 size, ScePvfU32 subFontIndex, ScePvfError *errorCode);` (`psp2/pvf.h`; `errorCode` na pilha)
- **Comportamento:** `OpenUserMemory` + subfont.

#### scePvfOpenDefaultJapaneseFontOnSharedMemory
- [x] **Prioridade:** P2 · **Biblioteca:** ScePvf · **NID:** 0xFEEE373A
- **Protótipo:** `ScePvfFontId scePvfOpenDefaultJapaneseFontOnSharedMemory(ScePvfLibId libID, ScePvfError *errorCode);` (`psp2/pvf.h`)
- **Comportamento:** abre a fonte japonesa padrão que o sistema mantém carregada numa região de memória
  compartilhada (economiza memória do jogo: não aloca o arquivo). Qual arquivo é o "default japonês" (provável
  `jpn0.pvf`) e qual mecanismo de memória compartilhada é usado: **a confirmar** desmontando `libpvf.suprx`.
- **Implementação no VitaSharp:** abrir `sa0:data/font/pvf/jpn0.pvf` sem alocar o tamanho do arquivo via
  `allocFunc`; cache estático de bytes compartilhado entre libs.

#### scePvfOpenDefaultLatinFontOnSharedMemory
- [x] **Prioridade:** P2 · **Biblioteca:** ScePvf · **NID:** 0x9CBC1A46
- **Protótipo:** `ScePvfFontId scePvfOpenDefaultLatinFontOnSharedMemory(ScePvfLibId libID, ScePvfError *errorCode);` (`psp2/pvf.h`)
- **Comportamento:** idem para a latina padrão (provável `ltn0.pvf`; a confirmar).
- **Implementação no VitaSharp:** idem com `ltn0.pvf`.

#### scePvfClose
- [x] **Prioridade:** P2 · **Biblioteca:** ScePvf · **NID:** 0xD282C23C
- **Protótipo:** `ScePvfError scePvfClose(ScePvfFontId fontID);` (`psp2/pvf.h`)
- **Comportamento:** fecha a fonte (libera face, cache e memória via `freeFunc`).
- **Retorno/erros:** 0; handle inválido 0x80460003 (a confirmar).
- **Implementação no VitaSharp:** `FT_Done_Face`, remover do dicionário.
- **Validação:** reabrir após fechar com a lib cheia.

### scePvfFlush
- [x] **Prioridade:** P2 · **Biblioteca:** ScePvf · **NID:** 0x687FF765
- **Protótipo:** `ScePvfError scePvfFlush(ScePvfFontId fontID);` (`psp2/pvf.h`)
- **Comportamento:** descarta o cache de glifos da fonte (inclusive no cache do jogo, se `ScePvfCacheSystemInterface`
  foi fornecido).
- **Retorno/erros:** 0; handle inválido 0x80460003.
- **Implementação no VitaSharp:** limpar o cache local.
- **Validação:** sem efeito visível.

### scePvfSetCharSize
- [x] **Prioridade:** P2 · **Biblioteca:** ScePvf · **NID:** 0xF17ADE4D
- **Protótipo:** `ScePvfError scePvfSetCharSize(ScePvfFontId fontID, ScePvfFloat32 hSize, ScePvfFloat32 vSize);` (`psp2/pvf.h`)
- **Comportamento:** tamanho do caractere em pontos (H, V). Pixels = `size * res / 72` (com `res` da lib; interação
  com `SetEM` a confirmar).
- **Retorno/erros:** 0; handle inválido 0x80460003; tamanho ≤ 0 0x80460003 (a confirmar).
- **Implementação no VitaSharp:** `FT_Set_Char_Size(face, (int)(h*64), (int)(v*64), (uint)hRes, (uint)vRes)`;
  invalidar cache. Floats em s0/s1, `fontID` em r0.
- **Validação:** `bitmapHeight` de "H" em 12 pt e 24 pt (razão ≈ 2) no LLE × HLE.

### scePvfSetEmboldenRate
- [x] **Prioridade:** P2 · **Biblioteca:** ScePvf · **NID:** 0x6E787722
- **Protótipo:** `ScePvfError scePvfSetEmboldenRate(ScePvfFontId fontID, ScePvfFloat32 emboldenRate);` (`psp2/pvf.h`)
- **Comportamento:** negrito sintético. Faixa `SCE_PVF_MIN_EMBOLDEN_RATE` (−20,0) a `SCE_PVF_MAX_EMBOLDEN_RATE` (40,0);
  valores negativos afinam. Unidade (percentual do EM?) **a confirmar** via LLE.
- **Retorno/erros:** 0; fora da faixa 0x80460003 (ou clamp — a confirmar).
- **Implementação no VitaSharp:** `FT_Outline_Embolden(outline, strength)` com `strength = rate/100 * emPixels*64`
  (hipótese; calibrar contra o LLE medindo a largura do traço).
- **Validação:** largura de "l" com rate 0, 20, 40, −20.

### scePvfSetSkewValue
- [x] **Prioridade:** P2 · **Biblioteca:** ScePvf · **NID:** 0x3DD09BC9
- **Protótipo:** `ScePvfError scePvfSetSkewValue(ScePvfFontId fontID, ScePvfFloat32 angleX, ScePvfFloat32 angleY);` (`psp2/pvf.h`)
- **Comportamento:** itálico sintético: cisalhamento em graus, cada eixo em −30..30 (`SCE_PVF_MIN/MAX_SKEW_VALUE`).
- **Retorno/erros:** 0; fora da faixa 0x80460003 (ou clamp, a confirmar).
- **Implementação no VitaSharp:** matriz `[1, tan(ax); tan(ay), 1]` em 16.16 via `FT_Set_Transform` ou
  `FT_Outline_Transform`; sinal/direção a confirmar contra o LLE.
- **Validação:** bitmap de "I" com 15° deve inclinar para a direita igual ao LLE.

### scePvfIsElement
- [x] **Prioridade:** P2 · **Biblioteca:** ScePvf · **NID:** 0x9F018F25
- **Protótipo:** `ScePvfBool scePvfIsElement(ScePvfFontId fontID, ScePvfCharCode charCode);` (`psp2/pvf.h`)
- **Comportamento:** 1 se a fonte tem glifo para `charCode` (cmap Unicode), 0 caso contrário. Não usa o alt char.
- **Retorno/erros:** `SCE_PVF_TRUE`/`FALSE`; handle inválido: a confirmar (provavelmente 0).
- **Implementação no VitaSharp:** `FT_Get_Char_Index(face, code) != 0`.
- **Validação:** 'A' em ltn0 = 1; U+3042 em ltn0 = 0, em jpn0 = 1.

### scePvfIsVertElement
- [x] **Prioridade:** P2 · **Biblioteca:** ScePvf · **NID:** 0xE676A888
- **Protótipo:** sem protótipo no header. Inferido: `ScePvfBool scePvfIsVertElement(ScePvfFontId fontID, ScePvfCharCode charCode);`
- **Comportamento:** 1 se existe forma vertical própria para o caractere (substituição GSUB `vert`/`vrt2`, ex.
  `ー`, `（`, `、`), 0 caso contrário (a confirmar se retorna 1 para qualquer glifo presente).
- **Implementação no VitaSharp:** FreeType não aplica GSUB; ler a tabela GSUB (`FT_Load_Sfnt_Table`) e procurar o
  glifo na cobertura das lookups da feature `vert`. Implementar um leitor mínimo de GSUB lookup type 1 em
  `Hle/Font/OtfVert.cs` (reutilizado pelas funções `Vert*`).
- **Validação:** LLE com `ー` (deve ser 1) e `A`.

### scePvfGetFontInfo
- [x] **Prioridade:** P2 · **Biblioteca:** ScePvf · **NID:** 0xAB0C7CF2
- **Protótipo:** `ScePvfError scePvfGetFontInfo(ScePvfFontId fontID, ScePvfFontInfo *fontInfo);` (`psp2/pvf.h`)
- **Comportamento:** métricas máximas no tamanho atual (I em 26.6 e F em float), `numChars` (glifos mapeados) e o
  estilo da fonte.
- **Retorno/erros:** 0; handle/ponteiro inválido 0x80460003.
- **Estruturas:** `ScePvfFontInfo`.
- **Implementação no VitaSharp:** `face->size->metrics` (`ascender`, `descender`, `max_advance`) + `face->bbox`
  escalado; `numChars` = contagem do cmap (`FT_Get_First_Char/Next_Char`). Se cada campo `max*` é calculado do bbox
  ou percorrendo glifos: a confirmar no LLE.
- **Validação:** diff LLE × HLE para ltn0 a 12 pt e jpn0 a 16 pt.

### scePvfGetCharInfo
- [x] **Prioridade:** P2 · **Biblioteca:** ScePvf · **NID:** 0xA88EEDB0
- **Protótipo:** `ScePvfError scePvfGetCharInfo(ScePvfFontId fontID, ScePvfCharCode charCode, ScePvfCharInfo *charInfo);` (`psp2/pvf.h`)
- **Comportamento:** métricas e dimensões do bitmap do glifo no tamanho/embolden/skew atuais. Sem glifo ⇒ alt char da
  lib; se nem ele existe, `SCE_PVF_ERROR_NOGLYPH` (0x8046000F) (a confirmar).
- **Retorno/erros:** 0; 0x80460003; 0x8046000F.
- **Estruturas:** `ScePvfCharInfo`: `bitmapWidth/Height` = `bitmap.width/rows`, `bitmapPitch` (bytes por linha em
  8 bpp?, a confirmar), `bitmapLeft/Top` = `bitmap_left/top`, `glyphMetrics` = `FT_Glyph_Metrics` (26.6).
- **Implementação no VitaSharp:** `FT_Load_Char(face, code, FT_LOAD_DEFAULT)` + `FT_Render_Glyph(NORMAL)`; cache por
  (code, size, embolden, skew, vertical).
- **Validação:** dump de 0x20–0x7E LLE × HLE; tolerância de ±1 px em bitmaps (rasterizador pode diferir).

### scePvfGetKerningInfo
- [x] **Prioridade:** P2 · **Biblioteca:** ScePvf · **NID:** 0xBC90F661
- **Protótipo:** `ScePvfError scePvfGetKerningInfo(ScePvfFontId fontID, ScePvfCharCode leftCharCode, ScePvfCharCode rightCharCode, ScePvfKerningInfo *pKerningInfo);` (`psp2/pvf.h`)
- **Comportamento:** ajuste de kerning do par, em 26.6 e float, no tamanho atual. Sem kerning ⇒ zeros.
- **Retorno/erros:** 0; 0x80460003.
- **Estruturas:** `ScePvfKerningInfo`.
- **Implementação no VitaSharp:** `FT_Get_Kerning(..., FT_KERNING_DEFAULT)` só lê a tabela `kern`; as PVF são CFF e
  devem ter kerning em GPOS. Verificar se `ltn0.pvf` tem `kern` (a confirmar com um dump de tabelas: o diretório OTF
  começa em `BASE, CFF , OS/2, cmap…`). Se só houver GPOS, o FreeType da Sony provavelmente também retorna 0 —
  confirmar no LLE com o par "AV" antes de implementar GPOS.
- **Validação:** "AV", "To" no LLE × HLE.

### scePvfGetCharImageRect
- [x] **Prioridade:** P2 · **Biblioteca:** ScePvf · **NID:** 0x6C1B9CAF
- **Protótipo:** `ScePvfError scePvfGetCharImageRect(ScePvfFontId fontID, ScePvfCharCode charCode, ScePvfIrect *rect);` (`psp2/pvf.h`)
- **Comportamento:** largura/altura em pixels do bitmap que `GetCharGlyphImage` vai produzir.
- **Retorno/erros:** 0; 0x80460003; 0x8046000F.
- **Implementação no VitaSharp:** do cache de `GetCharInfo`.
- **Validação:** igual a `bitmapWidth/Height`.

### scePvfGetCharGlyphImage
- [x] **Prioridade:** P2 · **Biblioteca:** ScePvf · **NID:** 0x37DA496A
- **Protótipo:** `ScePvfError scePvfGetCharGlyphImage(ScePvfFontId fontID, ScePvfCharCode charCode, ScePvfUserImageBufferRec *imageBuffer);` (`psp2/pvf.h`)
- **Comportamento:** rasteriza o glifo e escreve no buffer do jogo. Posição: `xPos64/yPos64` (26.6) — se é a origem
  da baseline (com `bitmapLeft/Top` aplicados) ou o canto superior esquerdo, e se a fração subpixel é respeitada
  (`FT_Outline_Translate` antes de rasterizar): **a confirmar** via LLE. Formatos: `DIRECT4_L` (4 bpp, pixel par no
  nibble baixo — "L") e `DIRECT8` (8 bpp, cobertura 0–255). Pixels fora de `rect.width × rect.height` descartados.
  Composição sobre o conteúdo anterior (sobrescreve × max/OR): a confirmar.
- **Retorno/erros:** 0; 0x80460003 (buffer nulo, formato ≠ 0/2); 0x8046000F.
- **Estruturas:** `ScePvfUserImageBufferRec`.
- **Implementação no VitaSharp:** `PvfRenderer.Render(...)` → `byte[]` 8 bpp; `Blit` comum com o Pgf (converter
  8→4 bpp com `v >> 4`).
- **Validação:** buffer pré-preenchido com 0xAA, "Hello" em ltn0 12 pt, DIRECT8 e DIRECT4_L; comparar com LLE
  (esperar pequenas diferenças de AA; posição e tamanho devem bater exatamente).

### scePvfGetCharGlyphImage_Clip
- [x] **Prioridade:** P2 · **Biblioteca:** ScePvf · **NID:** 0xA55F973F
- **Protótipo:** `ScePvfError scePvfGetCharGlyphImage_Clip(ScePvfFontId fontID, ScePvfCharCode charCode, ScePvfUserImageBufferRec *imageBuffer, ScePvfS32 clipX, ScePvfS32 clipY, ScePvfU32 clipWidth, ScePvfU32 clipHeight);` (`psp2/pvf.h`; alias `scePvfGetCharGlyphImageClip`; os 3 últimos na pilha)
- **Comportamento:** como acima, restrito ao retângulo de recorte (coordenadas do buffer, a confirmar).
- **Retorno/erros:** como acima.
- **Implementação no VitaSharp:** `Blit` com clip.
- **Validação:** clip cortando o glifo ao meio.

### scePvfGetCharGlyphOutline
- [x] **Prioridade:** P2 · **Biblioteca:** ScePvf · **NID:** 0x17A7873B
- **Protótipo:** sem protótipo no header. Inferido: `ScePvfError scePvfGetCharGlyphOutline(ScePvfFontId fontID, ScePvfCharCode charCode, void **ppOutline);`
  ou com um struct de saída preenchido pelo chamador — **a confirmar** desmontando `libpvf.suprx` (o par com
  `scePvfReleaseCharGlyphOutline` indica que a lib aloca a estrutura e o jogo devolve depois).
- **Comportamento:** devolve o contorno vetorial do glifo (pontos/tags/contornos, provavelmente no layout do
  `FT_Outline` de 32 bits: `n_contours` s16, `n_points` s16, `points*`, `tags*`, `contours*`, `flags` int — a
  confirmar), alocado pelo `allocFunc` da lib.
- **Retorno/erros:** 0; 0x80460003; 0x8046000F; 0x80460001 se `allocFunc` falhar.
- **Implementação no VitaSharp:** `FT_Load_Char(NO_BITMAP)`, copiar `face->glyph->outline` para memória guest
  alocada via `allocFunc`, com coordenadas 26.6.
- **Validação:** só é possível com um jogo/homebrew que use contornos; até lá, logar `WarnOnce` e retornar
  `SCE_PVF_ERROR_NOSUPPORT` (0x8046000E) é um fallback aceitável.

### scePvfReleaseCharGlyphOutline
- [x] **Prioridade:** P2 · **Biblioteca:** ScePvf · **NID:** 0xB6CE89E1
- **Protótipo:** sem protótipo. Inferido: `ScePvfError scePvfReleaseCharGlyphOutline(ScePvfFontId fontID, void *pOutline);` (a confirmar como acima).
- **Comportamento:** libera o contorno obtido por `GetCharGlyphOutline`/`GetVertCharGlyphOutline` (`freeFunc`).
- **Retorno/erros:** 0; 0x80460003.
- **Implementação no VitaSharp:** `CallGuest(freeFunc, userData, ptr)` para cada bloco alocado.
- **Validação:** contador de alloc/free.

### Variantes verticais (ScePvf)

Mesma semântica das funções horizontais, mas para escrita vertical: o glifo é substituído pela forma vertical
(GSUB `vert`/`vrt2`, quando existe) e as métricas/posição usam a origem vertical (`verticalBearingX/Y`,
`verticalAdvance`). Nenhuma tem protótipo no `psp2/pvf.h`; os protótipos abaixo são **inferidos** por simetria com
as horizontais (a confirmar desmontando `libpvf.suprx`). Implementação: mesmas rotinas com flag `vertical`
(`FT_LOAD_VERTICAL_LAYOUT` + substituição via `OtfVert`). Validação: LLE com `「ー」` em jpn0 e comparação com as
horizontais.

| Função | NID | Protótipo (inferido) | Comportamento |
| --- | --- | --- | --- |
| - [x] scePvfGetVertCharInfo | 0xB8D01915 | `ScePvfError scePvfGetVertCharInfo(ScePvfFontId fontID, ScePvfCharCode charCode, ScePvfCharInfo *charInfo);` | `GetCharInfo` com glifo vertical |
| - [x] scePvfGetVertCharImageRect | 0x64E0EA8B | `ScePvfError scePvfGetVertCharImageRect(ScePvfFontId fontID, ScePvfCharCode charCode, ScePvfIrect *rect);` | tamanho do bitmap vertical |
| - [x] scePvfGetVertCharGlyphImage | 0x8AE8433A | `ScePvfError scePvfGetVertCharGlyphImage(ScePvfFontId fontID, ScePvfCharCode charCode, ScePvfUserImageBufferRec *imageBuffer);` | rasteriza o glifo vertical no buffer |
| - [x] scePvfGetVertCharGlyphImage_Clip | 0xEEDAB884 | `ScePvfError scePvfGetVertCharGlyphImage_Clip(ScePvfFontId fontID, ScePvfCharCode charCode, ScePvfUserImageBufferRec *imageBuffer, ScePvfS32 clipX, ScePvfS32 clipY, ScePvfU32 clipWidth, ScePvfU32 clipHeight);` | idem com recorte |
| - [x] scePvfGetVertCharGlyphOutline | 0xECCB0CEE | `ScePvfError scePvfGetVertCharGlyphOutline(ScePvfFontId fontID, ScePvfCharCode charCode, void **ppOutline);` | contorno do glifo vertical (liberar com `scePvfReleaseCharGlyphOutline`) |

Prioridade de todas: P2 · Biblioteca: ScePvf.

### Conversões ponto ↔ pixel e alt char (ScePvf)

Espelho das funções `sceFont*` correspondentes (ver ScePgf): `pixel = point * res / 72`, `point = pixel * 72 / res`
com `hRes`/`vRes` da lib; retornam `ScePvfFloat32` em s0 e gravam `*errorCode` (0 ou 0x80460002, retornando 0,0f).
`scePvfSetAltCharacterCode` define o caractere de substituição da lib (u16; default a confirmar via LLE) e retorna
0 ou 0x80460002. Validação: LLE com resolução default e após `scePvfSetResolution`.

| Função | NID | Protótipo (`psp2/pvf.h`) | Comportamento |
| --- | --- | --- | --- |
| - [x] scePvfPixelToPointH | 0xF56B5B9B | `ScePvfFloat32 scePvfPixelToPointH(ScePvfLibId libID, ScePvfFloat32 pixel, ScePvfError *errorCode);` | `pixel * 72 / hRes` |
| - [x] scePvfPixelToPointV | 0xCDA282D2 | `ScePvfFloat32 scePvfPixelToPointV(ScePvfLibId libID, ScePvfFloat32 pixel, ScePvfError *errorCode);` | `pixel * 72 / vRes` |
| - [x] scePvfPointToPixelH | 0x91F02F9A | `ScePvfFloat32 scePvfPointToPixelH(ScePvfLibId libID, ScePvfFloat32 point, ScePvfError *errorCode);` | `point * hRes / 72` |
| - [x] scePvfPointToPixelV | 0x35465BD7 | `ScePvfFloat32 scePvfPointToPixelV(ScePvfLibId libID, ScePvfFloat32 point, ScePvfError *errorCode);` | `point * vRes / 72` |
| - [x] scePvfSetAltCharacterCode | 0x830625C2 | `ScePvfError scePvfSetAltCharacterCode(ScePvfLibId libID, ScePvfCharCode charCode);` | define o alt char da lib |

Prioridade de todas: P2 · Biblioteca: ScePvf.

### Tabela-resumo de NIDs (ScePgf + ScePvf)

| Biblioteca | Função | NID |
| --- | --- | --- |
| ScePgf | sceFontClose | 0x4A7293E9 |
| ScePgf | sceFontDoneLib | 0x07EE1733 |
| ScePgf | sceFontFindFont | 0x51061D87 |
| ScePgf | sceFontFindOptimumFont | 0x8DFBAE1B |
| ScePgf | sceFontFlush | 0x7D8CB13B |
| ScePgf | sceFontGetCharGlyphImage | 0xAB45AAD3 |
| ScePgf | sceFontGetCharGlyphImage_Clip | 0xEB589530 |
| ScePgf | sceFontGetCharImageRect | 0x70C86B3E |
| ScePgf | sceFontGetCharInfo | 0x6FD1BA65 |
| ScePgf | sceFontGetFontInfo | 0xF9414FA2 |
| ScePgf | sceFontGetFontInfoByIndexNumber | 0xAB034738 |
| ScePgf | sceFontGetFontList | 0xD56DCCEA |
| ScePgf | sceFontGetNumFontList | 0x9F842307 |
| ScePgf | sceFontNewLib | 0x1055ABA3 |
| ScePgf | sceFontOpen | 0xBD2DFCFF |
| ScePgf | sceFontOpenUserFile | 0xE260E740 |
| ScePgf | sceFontOpenUserMemory | 0xB23ED47C |
| ScePgf | sceFontPixelToPointH | 0x9E38F4D6 |
| ScePgf | sceFontPixelToPointV | 0x7B45E2D1 |
| ScePgf | sceFontPointToPixelH | 0x39B9AEFF |
| ScePgf | sceFontPointToPixelV | 0x03F10EC8 |
| ScePgf | sceFontSetAltCharacterCode | 0x8D5B44DF |
| ScePgf | sceFontSetResolution | 0xDE47674C |
| ScePvf | __scePvfSetFt2DoneLibCHook | 0x58142D71 |
| ScePvf | __scePvfSetFt2LibCHook | 0x997161C3 |
| ScePvf | scePvfClose | 0xD282C23C |
| ScePvf | scePvfDoneLib | 0xE17717EC |
| ScePvf | scePvfFindFont | 0x984E5BFE |
| ScePvf | scePvfFindOptimumFont | 0x2761FEAC |
| ScePvf | scePvfFlush | 0x687FF765 |
| ScePvf | scePvfGetCharGlyphImage | 0x37DA496A |
| ScePvf | scePvfGetCharGlyphImage_Clip | 0xA55F973F |
| ScePvf | scePvfGetCharGlyphOutline | 0x17A7873B |
| ScePvf | scePvfGetCharImageRect | 0x6C1B9CAF |
| ScePvf | scePvfGetCharInfo | 0xA88EEDB0 |
| ScePvf | scePvfGetFontInfo | 0xAB0C7CF2 |
| ScePvf | scePvfGetFontInfoByIndexNumber | 0xF3E1E8BD |
| ScePvf | scePvfGetFontList | 0x66F2D767 |
| ScePvf | scePvfGetKerningInfo | 0xBC90F661 |
| ScePvf | scePvfGetNumFontList | 0xF6C4A855 |
| ScePvf | scePvfGetVertCharGlyphImage | 0x8AE8433A |
| ScePvf | scePvfGetVertCharGlyphImage_Clip | 0xEEDAB884 |
| ScePvf | scePvfGetVertCharGlyphOutline | 0xECCB0CEE |
| ScePvf | scePvfGetVertCharImageRect | 0x64E0EA8B |
| ScePvf | scePvfGetVertCharInfo | 0xB8D01915 |
| ScePvf | scePvfIsElement | 0x9F018F25 |
| ScePvf | scePvfIsVertElement | 0xE676A888 |
| ScePvf | scePvfNewLib | 0x72E58672 |
| ScePvf | scePvfOpen | 0xE35434BB |
| ScePvf | scePvfOpenDefaultJapaneseFontOnSharedMemory | 0xFEEE373A |
| ScePvf | scePvfOpenDefaultLatinFontOnSharedMemory | 0x9CBC1A46 |
| ScePvf | scePvfOpenUserFile | 0xD535520F |
| ScePvf | scePvfOpenUserFileWithSubfontIndex | 0x10452B86 |
| ScePvf | scePvfOpenUserMemory | 0x9E65E4ED |
| ScePvf | scePvfOpenUserMemoryWithSubfontIndex | 0xA81570EB |
| ScePvf | scePvfPixelToPointH | 0xF56B5B9B |
| ScePvf | scePvfPixelToPointV | 0xCDA282D2 |
| ScePvf | scePvfPointToPixelH | 0x91F02F9A |
| ScePvf | scePvfPointToPixelV | 0x35465BD7 |
| ScePvf | scePvfReleaseCharGlyphOutline | 0xB6CE89E1 |
| ScePvf | scePvfSetAltCharacterCode | 0x830625C2 |
| ScePvf | scePvfSetCharSize | 0xF17ADE4D |
| ScePvf | scePvfSetEM | 0xDFB677C5 |
| ScePvf | scePvfSetEmboldenRate | 0x6E787722 |
| ScePvf | scePvfSetResolution | 0xC4444FB3 |
| ScePvf | scePvfSetSkewValue | 0x3DD09BC9 |

### Validação geral (Pgf/Pvf)

Como nenhum jogo instalado importa essas libs, a referência é um homebrew de teste (VitaSDK, `-lScePgf_stub
-lScePvf_stub`) rodado em duas configurações no VitaSharp: (1) LLE de `libpgf.suprx`/`libpvf.suprx`/`libSceFt2.suprx`
(copiados de `vs0:sys/external`) e (2) HLE. O homebrew grava em `ux0:data/fonttest/*.bin` os structs e buffers de
glifo de uma bateria fixa de chamadas; os dois conjuntos de arquivos devem ser idênticos para Pgf e coincidir em
métricas/posições para Pvf (AA com tolerância). Os scripts Python usados para conferir o formato PGF ficaram em
`C:\Users\guisa\AppData\Local\Temp\claude\C--Users-guisa-VitaSharp\2816b272-89dd-49a3-bac3-17c610349d63\scratchpad\pgfdraw.py`
e `...\pgfhdr.py` (Python 2.7, `py -E -s pgfdraw.py <arquivo.pgf> <código hex> [s]`, o terceiro argumento desenha a
sombra) e podem virar a base de um teste unitário de `PgfFont`.

## SceJpegUser (11 funções)

### Arquitetura (SceJpegUser)

- **Objetivo:** decodificador JPEG/MJPEG por hardware (bloco "AVC/JPEG" do Vita) exposto ao jogo. Recebe um JPEG
  baseline completo em memória e devolve YCbCr planar (`DecodeMJpegYCbCr`) ou RGBA/BGRA (`DecodeMJpeg`, que é
  YCbCr + CSC). `Csc`/`MJpegCsc` fazem só a conversão de cor. Uso típico em jogo: carregar fotos/miniaturas,
  imagens da galeria (`ScePhotoExport`/câmera), texturas JPEG empacotadas.
- **Estado interno:** um único estado de biblioteca: `initialized` + `maxSplitDecoder` (de `InitMJpeg`). Os
  decodificadores "split" (decodificação incremental de imagens grandes) são objetos com handle. Não há estado por
  chamada de decode além do buffer temporário — cada `Decode*` é síncrono e autossuficiente.
- **`decodeMode` (bits, segundo Vita3K `modules/SceJpeg/SceJpegUser.cpp`):**
  - bits 0–2 = modo DHT: `0 WITH_DHT`, `1 WITHOUT_DHT` (MJPEG sem tabela Huffman — usar tabelas padrão do Annex K),
    `2 ANY_SAMPLING_WITHOUT_DHT`, `3 ANY_SAMPLING`. Modos 0/1 = "standard" (só 4:2:0 e 4:2:2, saída com pitch
    alinhado a 8 linhas e croma alinhado a 16); 2/3 = "extended" (qualquer amostragem, sem CSC para RGBA).
  - bits 4–6 = downscale: `0x10` 1/2, `0x20` 1/4, `0x40` 1/8 (`0x70` = "any").
- **Formato de saída (`format`):** `-1 NO_CSC_OUTPUT` (YCbCr), `0 PIXEL_RGBA8888`, `4 PIXEL_BGRA8888`; flag
  `0x10 COLORSPACE_BT601` em `colorOption` de Csc (sem ela = JFIF full-range).
- **colorSpace devolvido em `SceJpegOutputInfo`:** `0x10101` GRAYSCALE, `0x20101` YUV444, `0x20102` YUV440,
  `0x20104` YUV441, `0x20201` YUV422, `0x20202` YUV420, `0x20401` YUV411 (`0x20000 | (hdiv<<8) | vdiv`). O argumento
  `sampling` de `Csc` é o mesmo código sem o `0x20000` (ex.: `0x202` = 4:2:0).
- **Pitch (Vita3K `codec/src/mjpeg.cpp: calculate_pitch_info`):** largura/altura alinhadas ao MCU (4:2:0 16×16,
  4:2:2 16×8, 4:4:4/gray 8×8, 4:4:0 8×16, 4:1:1 32×8, 4:4:1 8×32), divididas pelo downscale e realinhadas
  (largura ao bloco, altura a 8 no modo standard, 1 no extended). `pitch[0]` = luma; `pitch[1]=pitch[2]` = croma
  (luma / divisor de croma); no modo standard 4:2:0/4:2:2 a largura do croma é alinhada a 16; `pitch[3]` = 0.
  Fórmula exata (conferida no fonte do Vita3K): `x = align(align(w, mcuW) / ratio, mcuW)`,
  `y = align(align(h, mcuH) / ratio, standard ? 8 : 1)`. Ex.: 4:2:0 100×60 → Y 112×64, Cb/Cr 64×32 (56 → 16);
  4:4:4 100×60 extended → 104×64. `Csc`/`MJpegCsc` usam o tamanho recebido sem alinhamento a MCU (ratio "0").
  Downscale com vários bits (`0x70` "ANY"): o VitaSharp usa o menor (1/2), para que buffers dimensionados por
  `GetOutputInfo` sempre caibam (Vita3K calcula `0x70/8 = 14`, sem sentido).
  Layout do buffer YCbCr: plano Y (`pitch[0].x*pitch[0].y`) seguido de Cb e Cr, contíguos.
- **Limites:** Vita3K rejeita pitch de saída < 64 ou > 2032×1088 com `UNSUPPORT_IMAGE_SIZE` (o teste é sobre o
  pitch já reduzido pelo downscale). Valores exatos do hardware: a confirmar (testar JPEGs de 63/64 e 2032/2048 px
  num Vita real com um homebrew).
- **Decoder (implementado):** decodificador JPEG próprio, 100% gerenciado (sem FFmpeg, o Core continua
  multiplataforma): `src/VitaSharp.Core/Media/JpegDecoder.cs` — baseline e sequencial estendido 8 bits, progressivo
  (seleção espectral + aproximações sucessivas de DC e AC, EOB runs), Huffman, restart markers, grayscale e
  YCbCr 4:4:4/4:4:0/4:4:1/4:2:2/4:2:0/4:1:1; sem DHT (MJPEG) usa as tabelas típicas do Annex K. Saída planar na
  resolução nativa de cada componente (MCUs inteiros). Validado contra o GDI+ do Windows em ~40 JPEGs reais
  (baseline e progressivos, até 6400×4000): PSNR 50–68 dB em 4:4:4. Downscale: média de caixas ratio×ratio sobre o
  layout de pitch completo (como Vita3K; o filtro do hardware real pode diferir). Padding além dos MCUs
  decodificados repete a última coluna/linha.
- **Arquivos:** `Media/JpegCommon.cs` (tabelas Annex K, zig-zag, IDCT/FDCT 8×8, `JpegError`),
  `Media/JpegDecoder.cs`, `Media/YuvConvert.cs` (YCbCr↔RGBA: JFIF full-range e BT.601 limited-range, BGRA,
  croma por replicação/média), `Hle/Modules/Jpeg.cs` (`sealed class Jpeg : HleModule`, SceJpegUser + SceJpegArm).
  `Videodec.cs` não foi tocado (pode migrar para `YuvConvert` depois).
- **Dependências:** `GuestMemory.Span/R32/W32`, `Log.WarnOnce`. Nenhuma dependência de kernel. Custo HLE: a
  faixa padrão (não há classe "pesada" em `ClassifyCost`); ~20 ms de CPU host para 960×544.
- **Buffers curtos:** se `osize`/`tempBufferSize` for menor que o layout, a escrita é truncada no tamanho dado
  (`WarnOnce`), sem erro — o código de erro real é desconhecido e o Vita3K não verifica.

**Códigos de erro (SceJpeg, Vita3K `codec/include/codec/types.h`):**

| Nome | Valor |
| --- | --- |
| SCE_JPEG_ERROR_IMAGE_EMPTY | 0x80650003 |
| SCE_JPEG_ERROR_BAD_MARKER_LENGTH | 0x80650004 |
| SCE_JPEG_ERROR_BAD_DHT_COUNTS | 0x80650005 |
| SCE_JPEG_ERROR_BAD_DHT_INDEX | 0x80650006 |
| SCE_JPEG_ERROR_BAD_DQT_INDEX | 0x80650007 |
| SCE_JPEG_ERROR_DECODE_ERROR | 0x80650009 |
| SCE_JPEG_ERROR_INVALID_POINTER | 0x80650010 |
| SCE_JPEG_ERROR_BAD_COMPONENT_ID | 0x80650011 |
| SCE_JPEG_ERROR_UNSUPPORT_COLORSPACE | 0x80650013 |
| SCE_JPEG_ERROR_BAD_MCU_SIZE | 0x80650014 |
| SCE_JPEG_ERROR_BAD_PRECISION | 0x80650015 |
| SCE_JPEG_ERROR_UNSUPPORT_SAMPLING | 0x80650016 |
| SCE_JPEG_ERROR_COMPONENT_COUNT | 0x80650017 |
| SCE_JPEG_ERROR_CONVERSION_NOTIMPL | 0x80650018 |
| SCE_JPEG_ERROR_EOI_EXPECTED | 0x80650019 |
| SCE_JPEG_ERROR_UNSUPPORT_IMAGE_SIZE | 0x80650020 |
| SCE_JPEG_ERROR_NO_HUFF_TABLE | 0x80650021 |
| SCE_JPEG_ERROR_NO_QUANT_TABLE | 0x80650022 |
| SCE_JPEG_ERROR_NO_SOI | 0x80650023 |
| SCE_JPEG_ERROR_BAD_DQT_MARKER / DHT / DRI / SOF / SOS | 0x80650024 / 25 / 26 / 27 / 28 |
| SCE_JPEG_ERROR_SOF_DUPLICATE | 0x80650029 |
| SCE_JPEG_ERROR_NO_LOSSLESS_SUPPORT | 0x80650031 |
| SCE_JPEG_ERROR_NO_ARITH_SUPPORT | 0x80650032 |
| SCE_JPEG_ERROR_UNKNOWN_MARKER | 0x80650035 |
| SCE_JPEG_ERROR_BAD_RESTART_MARKER | 0x80650036 |
| SCE_JPEG_ERROR_UNEXPECTED_MARKER | 0x80650037 |
| SCE_JPEG_ERROR_INVALID_REGION | 0x80650038 |
| SCE_JPEG_ERROR_INVALID_STATE | 0x80650039 |
| SCE_JPEG_ERROR_CANNOT_CONTINUE | 0x80650040 |
| SCE_JPEG_ERROR_OUT_OF_MEMORY | 0x80650041 |
| SCE_JPEG_ERROR_CANNOT_INIT | 0x80650042 |
| SCE_JPEG_ERROR_CANNOT_FINISH | 0x80650043 |
| SCE_JPEG_ERROR_INVALID_COLOR_FORMAT | 0x80650050 |
| SCE_JPEG_ERROR_NOT_PHY_CONTINUOUS_MEMORY | 0x80650053 |
| SCE_JPEG_ERROR_UNSUPPORT_DOWNSCALE | 0x80650060 |
| SCE_JPEG_ERROR_BAD_PROGRESSIVE_PARAM | 0x80650061 |
| SCE_JPEG_ERROR_EXCEED_MAX_SPLIT_DECODER | 0x80650062 |
| SCE_JPEG_ERROR_INIT_DONE | 0x80650063 |
| SCE_JPEG_ERROR_INPUT_SUSPENDED | 0x80650064 |
| SCE_JPEG_ERROR_INPUT_DATA_TOO_BIG | 0x80650065 |
| SCE_JPEG_ERROR_INVALID_DATA_SIZE | 0x80650066 |

Mapeamento implementado (`Jpeg.MapError`): o parser devolve um `JpegError` por falha — sem `FFD8` → `NO_SOI`,
stream < 2 bytes ou dimensão 0 → `IMAGE_EMPTY`, comprimento de segmento inválido → `BAD_MARKER_LENGTH`, índices
DQT/DHT → `BAD_DQT_INDEX`/`BAD_DHT_INDEX`, contagens DHT → `BAD_DHT_COUNTS`, DQT/DHT/DRI/SOF/SOS malformados →
`BAD_*_MARKER`, SOF repetido → `SOF_DUPLICATE`, precisão ≠ 8 → `BAD_PRECISION`, Nf ∉ {1,3} → `COMPONENT_COUNT`,
fator > 4 ou > 10 blocos/MCU → `BAD_MCU_SIZE`, amostragem não suportada → `UNSUPPORT_SAMPLING`, SOF3/5/6/7/11/15 →
`NO_LOSSLESS_SUPPORT`, SOF9/10/13/14/DAC → `NO_ARITH_SUPPORT`, parâmetros progressivos ilegais →
`BAD_PROGRESSIVE_PARAM`, RSTn ausente → `BAD_RESTART_MARKER`, SOI no meio → `UNEXPECTED_MARKER`, marcador reservado
→ `UNKNOWN_MARKER`, tabela Huffman 2/3 ausente → `NO_HUFF_TABLE` (0/1 ausentes = tabelas padrão), DQT ausente →
`NO_QUANT_TABLE`, resto → `DECODE_ERROR`. Progressivo é **aceito** (Vita3K também aceita via FFmpeg; se o hardware
recusa, a confirmar). Stream sem EOI mas com ao menos um scan é aceito.

**Estrutura `SceJpegOutputInfo` (0x34 bytes, `psp2/jpeg.h`; nomes dos campos desconhecidos vindos do Vita3K):**

| Offset | Tipo | Campo |
| --- | --- | --- |
| 0x00 | int32 | colorSpace |
| 0x04 | u16 | width (imagem original) |
| 0x06 | u16 | height |
| 0x08 | u32 | outputSize (bytes do buffer de saída, alinhado a 256) |
| 0x0C | u32 | tempBufferSize (`unk_0xc` no header; YCbCr intermediário quando há CSC) |
| 0x10 | u32 | coefBufferSize (`unk_0x10`; Vita3K devolve 0x100 fixo — cálculo real a confirmar) |
| 0x14 | SceJpegPitch[4] | {x, y} u32 cada |

### sceJpegInitMJpeg
- [x] **Prioridade:** P2 · **Biblioteca:** SceJpegUser · **NID:** 0xB030773B
- **Protótipo:** `int sceJpegInitMJpeg(SceInt32 decoderCount);` (`psp2/jpeg.h`; `decoderCount` = máximo de
  split decoders simultâneos)
- **Comportamento:** inicializa a biblioteca MJPEG e reserva recursos para até `decoderCount` split decoders.
  Obrigatória antes de qualquer `Decode*`/`GetOutputInfo`/`CreateSplitDecoder`.
- **Retorno/erros:** 0; `SCE_JPEG_ERROR_INIT_DONE` (0x80650063) se já inicializada (a confirmar: Vita3K não
  verifica); `CANNOT_INIT` (0x80650042) se FFmpeg indisponível; `decoderCount < 0` → erro a confirmar.
- **Implementação no VitaSharp (feito):** `Jpeg.cs`: `_initialized = true; MaxSplitDecoders = max(0, decoderCount)`
  sob `lock`. Um segundo Init retorna 0 (como Vita3K; `INIT_DONE` não é emitido até confirmar). Os `Decode*` não
  exigem Init (decodificação sem estado).
- **Validação:** homebrew de teste que chama Init duas vezes e verifica o segundo retorno; jogo que importe
  SceJpegUser deve passar da inicialização sem `Unimplemented HLE function`.

### sceJpegInitMJpegWithParam
- [x] **Prioridade:** P2 · **Biblioteca:** SceJpegUser · **NID:** 0x518163A7
- **Protótipo:** `int sceJpegInitMJpegWithParam(const SceJpegMJpegInitParam *params);` (`psp2/jpeg.h`)
- **Comportamento:** igual a `InitMJpeg(params->decoderCount)`; `options` reservado (0).
- **Retorno/erros:** como `InitMJpeg`; `params == NULL` → `INVALID_POINTER` (0x80650010); `size != 0xC` → erro a
  confirmar (provável `INVALID_POINTER` ou `INVALID_DATA_SIZE` 0x80650066).
- **Estruturas:** `SceJpegMJpegInitParam` 0xC: +0 size, +4 decoderCount, +8 options.
- **Implementação no VitaSharp (feito):** ler +4 e delegar a `InitMJpeg`; `size` não é verificado (como Vita3K).
- **Validação:** idem `sceJpegInitMJpeg`.

### sceJpegFinishMJpeg
- [x] **Prioridade:** P2 · **Biblioteca:** SceJpegUser · **NID:** 0x62842598
- **Protótipo:** `int sceJpegFinishMJpeg(void);` (`psp2/jpeg.h`)
- **Comportamento:** finaliza a biblioteca e libera os split decoders restantes.
- **Retorno/erros:** 0; `CANNOT_FINISH` (0x80650043) ou `INVALID_STATE` (0x80650039) se não inicializada — qual
  dos dois a confirmar (Vita3K sempre retorna 0; fazer o mesmo até confirmar).
- **Implementação no VitaSharp:** `_initialized = false`, descartar dicionário de split decoders.
- **Validação:** Init → Finish → Init deve retornar 0 nas três.

### sceJpegGetOutputInfo
- [x] **Prioridade:** P2 · **Biblioteca:** SceJpegUser · **NID:** 0x353BA9B0
- **Protótipo:** `int sceJpegGetOutputInfo(const SceUInt8 *jpegData, SceSize jpegSize, SceInt32 format,
  SceInt32 mode, SceJpegOutputInfo *output);` (`psp2/jpeg.h`)
- **Comportamento:** analisa os cabeçalhos (SOI/SOF/DHT) sem decodificar e informa espaço de cor, dimensões,
  pitches e tamanhos de buffer necessários para o `format` (RGBA/BGRA/YCbCr) e `mode` (DHT + downscale) pedidos.
- **Retorno/erros:** 0. `INVALID_POINTER` se `jpegData`/`output` nulos ou `jpegSize == 0`;
  `INVALID_COLOR_FORMAT` (0x80650050) se `format ∉ {-1, 0, 4}`; no modo standard: gray →
  `UNSUPPORT_COLORSPACE` (0x80650013), amostragem ≠ 4:2:0/4:2:2 → `UNSUPPORT_SAMPLING` (0x80650016); modo
  extended com `format != -1` → `UNSUPPORT_DOWNSCALE` (0x80650060) (comportamento observado pelo Vita3K); erros de
  parse → códigos de marcador da tabela.
- **Estruturas:** `SceJpegOutputInfo` (acima). Preenchimento: zerar tudo; `pitch[]` pela regra da arquitetura;
  `yuvTotal = Σ pitch[i].x*pitch[i].y`. Se `format == -1`: `outputSize = align(yuvTotal, 256)`. Senão:
  `pitch[0].x *= 4`, `pitch[1..3] = 0`, `tempBufferSize = align(yuvTotal, 256)`,
  `outputSize = align(pitch[0].x * pitch[0].y, 256)`.
- **Implementação no VitaSharp (feito):** `JpegDecoder.ReadInfo` percorre os segmentos até o primeiro SOS
  (valida DQT/DHT/DRI/SOF). Escreve exatamente 0x34 bytes; nos erros de modo/amostragem a struct fica zerada com
  `colorSpace` preenchido (como Vita3K). `coefBufferSize`: 0x100 para baseline (valor do Vita3K, que evita
  `malloc(0)` nos jogos); progressivo = todos os coeficientes do frame em 16 bits (`blocos × 128`, alinhado a 256).
  Também usado por `sceJpegArmGetOutputInfo`.
- **Validação:** teste unitário com JPEGs 4:2:0, 4:2:2, 4:4:4 e gray (gerar com FFmpeg/`System.Drawing` fora do
  repo) comparando pitches com a tabela; cenário `tests/scenarios` quando um jogo usar.

### sceJpegDecodeMJpeg
- [x] **Prioridade:** P2 · **Biblioteca:** SceJpegUser · **NID:** 0x6215B095
- **Protótipo (inferido — ausente do header; ordem do Vita3K):** `int sceJpegDecodeMJpeg(const SceUInt8 *pJpeg,
  SceSize isize, void *pRGBA, SceSize osize, SceInt32 decodeMode, void *pTempBuffer, SceSize tempBufferSize,
  void *pCoefBuffer, SceSize coefBufferSize);` — 9 argumentos (5 a 9 na pilha).
- **Comportamento:** decodifica o JPEG para YCbCr no `pTempBuffer` e converte para RGBA8888 (JFIF) em `pRGBA`, com
  largura de linha = `pitch[0].x` (pitch de luma). Downscale conforme `decodeMode`.
- **Retorno/erros:** sucesso = `(pitchWidth << 16) | pitchHeight` (valor positivo, não 0!). Erros: `INVALID_POINTER`,
  `UNSUPPORT_IMAGE_SIZE` (0x80650020), erros de parse; `osize` menor que o necessário → erro a confirmar
  (provável `INVALID_DATA_SIZE` 0x80650066 ou `INPUT_DATA_TOO_BIG` 0x80650065).
- **Implementação no VitaSharp (feito, ordem do Vita3K):** `JpegDecoder.Decode` → layout de pitch (host) →
  `YuvConvert.ToRgba` JFIF full-range de `pitch[0].x × pitch[0].y` pixels (inclui o padding) em `pRGBA`; o YCbCr
  também é copiado para `pTempBuffer` quando não nulo (nulo é aceito). Limite 64..2032 × 64..1088 sobre o pitch
  após downscale → `UNSUPPORT_IMAGE_SIZE`. `osize` curto: escrita truncada, sem erro.
- **Validação:** confirmar a ordem dos argumentos desmontando o stub do jogo que importar (ver quais registradores
  são carregados antes do `BLX`), ou com homebrew; comparar o RGBA com decodificação de referência (PSNR > 40 dB).

### sceJpegDecodeMJpegYCbCr
- [x] **Prioridade:** P2 · **Biblioteca:** SceJpegUser · **NID:** 0x2A769BD8
- **Protótipo:** `psp2/jpeg.h` declara `int sceJpegDecodeMJpegYCbCr(const SceUInt8 *jpegData, SceSize jpegSize,
  SceInt32 mode, SceUInt8 *output, SceSize outputSize, void *buffer, SceSize bufferSize);`. **Vita3K usa ordem
  diferente:** `(pJpeg, isize, pYCbCr, osize, decodeMode, pCoefBuffer, coefBufferSize)`. A ordem correta está a
  confirmar; a do Vita3K roda jogos comerciais, então é a mais provável — o header vitasdk pode estar errado.
- **Comportamento:** decodifica para YCbCr planar (Y, Cb, Cr contíguos, layout de pitch da arquitetura), sem CSC.
- **Retorno/erros:** sucesso = `(pitch[0].x << 16) | pitch[0].y`; erros como `DecodeMJpeg`.
- **Implementação no VitaSharp (feito, ordem do Vita3K):** mesmo helper; o padding do pitch recebe os dados
  decodificados dos MCUs (o hardware decodifica MCUs inteiros) e, além deles (croma alinhado a 16), repete a última
  coluna/linha. Sem limite de tamanho (Vita3K também não aplica aqui).
- **Validação:** checar ordem de argumentos (heurística: o argumento que vale ≤ 0x7F com bits só em 0–2/4–6 é o
  `decodeMode`); comparar planos com FFmpeg.

### sceJpegCsc
- [x] **Prioridade:** P2 · **Biblioteca:** SceJpegUser · **NID:** 0x6263AEC2
- **Protótipo (inferido — ausente do header; Vita3K):** `int sceJpegCsc(SceUInt8 *pRGBA, const SceUInt8 *pYCbCr,
  SceUInt32 xysize, SceInt32 iFrameWidth, SceInt32 colorOption, SceInt32 sampling);`
- **Comportamento:** conversão YCbCr→RGBA8888 genérica. `xysize = (width << 16) | height`; `iFrameWidth` = largura
  de linha do destino em pixels; `colorOption` = formato (só RGBA8888 = 0) | `0x10` BT.601; `sampling` só
  `0x101` (4:4:4) ou `0x10101 & 0xFFFF`→gray (segundo Vita3K: só YUV400/YUV444).
- **Retorno/erros:** 0; `INVALID_COLOR_FORMAT` (0x80650050) para formato ≠ RGBA ou amostragem não suportada.
- **Implementação no VitaSharp (feito):** `YuvConvert` com pitch não-standard (planos `width × height`, sem
  alinhamento). `sampling` aceito: `0x101` (4:4:4) e o código completo `0x10101` (gray). Flag BT.601
  implementada (entrada limited-range; Vita3K a ignora). Ponteiro nulo → `INVALID_POINTER`.
- **Validação:** teste unitário: Y=Cb=Cr=128 → cinza 128; vermelho puro ida e volta.

### sceJpegMJpegCsc
- [x] **Prioridade:** P2 · **Biblioteca:** SceJpegUser · **NID:** 0xC2380E3A
- **Protótipo:** `int sceJpegMJpegCsc(SceUInt8 *rgba, const SceUInt8 *yuv, SceSize yuvSize, SceInt32 imageWidth,
  SceInt32 format, SceInt32 sampling);` (`psp2/jpeg.h`; `yuvSize` é na verdade `(width<<16)|height`, como em Csc)
- **Comportamento:** converte saída de `DecodeMJpegYCbCr` (layout standard, croma alinhado a 16) para RGBA ou BGRA.
  `sampling` 4:2:0 (0x202) ou 4:2:2 (0x201).
- **Retorno/erros:** 0; `INVALID_COLOR_FORMAT` se formato ∉ {0, 4} (após remover 0x10); `UNSUPPORT_IMAGE_SIZE` se
  w/h fora de 64..2032 × 64..1088.
- **Implementação no VitaSharp (feito):** pitch standard sem alinhamento a MCU (luma = `width`, croma alinhado a 16)
  → `YuvConvert.ToRgba(bgra: format == 4, bt601: flag 0x10)`. Amostragens conhecidas (4:4:4 também) são aceitas;
  código desconhecido → `UNSUPPORT_SAMPLING`.
- **Validação:** decodificar com `DecodeMJpegYCbCr` + `MJpegCsc` e comparar com `DecodeMJpeg` (devem ser iguais).

### sceJpegCreateSplitDecoder
- [x] **Prioridade:** P2 · **Biblioteca:** SceJpegUser · **NID:** 0x123B4734
- **Protótipo (inferido; nem header nem Vita3K):** `int sceJpegCreateSplitDecoder(SceJpegSplitDecodeCtrl *pCtrl);`
  — estrutura e assinatura a confirmar.
- **Comportamento (inferido):** cria um decodificador incremental para JPEG grande/streaming (dados entregues em
  pedaços; códigos `INPUT_SUSPENDED` 0x80650064 e `EXCEED_MAX_SPLIT_DECODER` 0x80650062 indicam isso). Limite =
  `decoderCount` de `InitMJpeg`.
- **Retorno/erros:** 0 ou handle; `EXCEED_MAX_SPLIT_DECODER` ao passar do limite.
- **Implementação no VitaSharp:** até confirmar o layout: log `WarnOnce` com os 4 primeiros argumentos e dump de
  0x40 bytes do ponteiro, retornar `EXCEED_MAX_SPLIT_DECODER` (faz o jogo cair no caminho não-split, se tiver).
  Implementação real: acumular os pedaços num `MemoryStream` e decodificar tudo no último pedaço.
  **Feito apenas o stub seguro acima** (layout continua desconhecido; idem para `SplitDecode`/`Delete` e as
  variantes Arm).
- **Validação:** quando um jogo importar: desmontar o uso (campos lidos/escritos no ctrl) e cruzar com o PSP2 SDK
  doc se disponível; não há referência pública hoje.

### sceJpegSplitDecodeMJpeg
- [x] **Prioridade:** P2 · **Biblioteca:** SceJpegUser · **NID:** 0x4598EC9C
- **Protótipo (inferido):** `int sceJpegSplitDecodeMJpeg(SceJpegSplitDecodeCtrl *pCtrl);` — a confirmar.
- **Comportamento (inferido):** consome o próximo pedaço de entrada; retorna `INPUT_SUSPENDED` enquanto faltar
  dado, e ao final escreve a saída como `DecodeMJpegYCbCr`.
- **Retorno/erros:** ver acima; a confirmar.
- **Implementação no VitaSharp:** ver `CreateSplitDecoder`; enquanto inferido, retornar `INVALID_STATE` (0x80650039)
  com `WarnOnce`.
- **Validação:** idem.

### sceJpegDeleteSplitDecoder
- [x] **Prioridade:** P2 · **Biblioteca:** SceJpegUser · **NID:** 0xDE8D5FA1
- **Protótipo (inferido):** `int sceJpegDeleteSplitDecoder(SceJpegSplitDecodeCtrl *pCtrl);` — a confirmar.
- **Comportamento:** libera o split decoder.
- **Retorno/erros:** 0; handle inválido → `INVALID_STATE`? a confirmar.
- **Implementação no VitaSharp:** remover do dicionário; retornar 0 mesmo se desconhecido (inofensivo).
- **Validação:** idem.

## SceJpegArm (6 funções)

### Arquitetura (SceJpegArm)

- **Objetivo:** mesma API de decodificação, mas implementada em software ARM (sem o bloco de hardware) — útil para
  decodificar em paralelo ao hardware ou com limites diferentes. Módulo interno
  (`SCE_SYSMODULE_INTERNAL_JPEG_ARM`, `psp2/jpegarm.h`), raro em jogos. Vita3K: todas `UNIMPLEMENTED`.
- **Estado interno:** nenhuma função `Init` própria na lista; não depende de `sceJpegInitMJpeg` (a confirmar). Split
  decoders próprios.
- **Implementação (feita):** mesma classe `Jpeg.cs` e helpers (`JpegDecoder`, `YuvConvert`); não exige
  `sceJpegInitMJpeg`; o limite 2032×1088 não é aplicado. Diferenças conhecidas pelo header: não
  há `pTempBuffer` em `DecodeMJpeg` (só coef buffer) e a ordem é `(pJpeg, isize, decodeMode, out, osize, coef,
  coefSize)`. Erros: header só define `SCE_JPEG_ARM_OK = 0`; supor os mesmos códigos `0x8065xxxx` do SceJpeg (a
  confirmar). Limites de tamanho do decoder ARM podem ser maiores que 2032×1088 (a confirmar; não aplicar o
  limite do hardware até confirmar).

| Função | NID | Protótipo | Comportamento |
| --- | --- | --- | --- |
| sceJpegArmGetOutputInfo | 0x23AE3BEA | `int sceJpegArmGetOutputInfo(const SceUInt8 *pJpeg, SceSize isize, SceInt decodeMode, SceInt outputFormat, SceJpegOutputInfo *pOutputInfo);` (`psp2/jpegarm.h`) — atenção: `decodeMode` antes de `outputFormat`, inverso do SceJpeg | Igual a `sceJpegGetOutputInfo`. |
| sceJpegArmDecodeMJpeg | 0xA4ABFCE3 | `int sceJpegArmDecodeMJpeg(const SceUInt8 *pJpeg, SceSize isize, SceInt decodeMode, void *pRGBA, SceSize osize, void *pCoefBuffer, SceSize coefBufferSize);` (`psp2/jpegarm.h`) | Decodifica direto para RGBA8888; retorno `(pitchW<<16)|pitchH` (a confirmar se igual ao SceJpeg). |
| sceJpegArmDecodeMJpegYCbCr | 0xE9B1B86F | `int sceJpegArmDecodeMJpegYCbCr(const SceUInt8 *pJpeg, SceSize isize, SceInt decodeMode, SceUInt8 *pYCbCr, SceSize osize, void *pCoefBuffer, SceSize coefBufferSize);` (`psp2/jpegarm.h`) | Decodifica para YCbCr planar. |
| sceJpegArmCreateSplitDecoder | 0x9DA48DB6 | inferido: `int sceJpegArmCreateSplitDecoder(SceJpegSplitDecodeCtrl *pCtrl);` | Como `sceJpegCreateSplitDecoder`. |
| sceJpegArmSplitDecodeMJpeg | 0x5D83C606 | inferido: `int sceJpegArmSplitDecodeMJpeg(SceJpegSplitDecodeCtrl *pCtrl);` | Como `sceJpegSplitDecodeMJpeg`. |
| sceJpegArmDeleteSplitDecoder | 0xE9CB3DFD | inferido: `int sceJpegArmDeleteSplitDecoder(SceJpegSplitDecodeCtrl *pCtrl);` | Como `sceJpegDeleteSplitDecoder`. |

### sceJpegArmGetOutputInfo
- [x] **Prioridade:** P2 · **Biblioteca:** SceJpegArm · **NID:** 0x23AE3BEA
- **Protótipo:** ver tabela (`psp2/jpegarm.h`).
- **Comportamento:** como `sceJpegGetOutputInfo`, com os argumentos 3/4 trocados.
- **Retorno/erros:** 0 ou códigos SceJpeg (a confirmar).
- **Estruturas:** `SceJpegOutputInfo` (0x34).
- **Implementação no VitaSharp:** `[Hle] int sceJpegArmGetOutputInfo(uint jpeg, uint size, int mode, int format, uint info) => GetOutputInfoCore(jpeg, size, format, mode, info, arm: true);`
- **Validação:** teste unitário reaproveitando os vetores do SceJpeg.

### sceJpegArmDecodeMJpeg
- [x] **Prioridade:** P2 · **Biblioteca:** SceJpegArm · **NID:** 0xA4ABFCE3
- **Protótipo:** ver tabela.
- **Comportamento:** YCbCr interno (host) + CSC para RGBA, sem temp buffer do guest.
- **Retorno/erros:** como `sceJpegDecodeMJpeg` (a confirmar).
- **Implementação no VitaSharp:** helper comum com `tempBuffer = 0`.
- **Validação:** comparar com saída de `sceJpegDecodeMJpeg`.

### sceJpegArmDecodeMJpegYCbCr
- [x] **Prioridade:** P2 · **Biblioteca:** SceJpegArm · **NID:** 0xE9B1B86F
- **Protótipo:** ver tabela.
- **Comportamento/Retorno:** como `sceJpegDecodeMJpegYCbCr`.
- **Implementação no VitaSharp:** helper comum.
- **Validação:** idem.

### sceJpegArmCreateSplitDecoder / sceJpegArmSplitDecodeMJpeg / sceJpegArmDeleteSplitDecoder
- [x] **Prioridade:** P2 · **Biblioteca:** SceJpegArm · **NID:** 0x9DA48DB6 / 0x5D83C606 / 0xE9CB3DFD
- **Protótipo:** inferidos (tabela); a confirmar.
- **Comportamento:** idem às versões SceJpegUser.
- **Retorno/erros:** idem.
- **Implementação no VitaSharp:** mesmos stubs com `WarnOnce`.
- **Validação:** idem.

## SceJpegEncUser (10 funções)

### Arquitetura (SceJpegEncUser)

- **Objetivo:** codificador JPEG por hardware. O jogo aloca um contexto (`GetContextSize`), inicializa com
  dimensões/formato/buffer de saída, opcionalmente converte ARGB→YCbCr (`Csc`) e chama `Encode`, que devolve o
  tamanho do JPEG gerado. Uso típico: salvar foto/screenshot na galeria (junto com `ScePhotoExport`), miniaturas de
  save.
- **Estado interno:** por contexto (chave = endereço guest do contexto): `inWidth, inHeight, pixelFormat,
  outBuffer, outSize, option, compressRatio (padrão 64), headerMode (0 JPEG), validW/validH`. Guardar num
  `Dictionary<uint, EncCtx>` no módulo (não depender do layout do contexto guest; só zerar/escrever um marcador).
- **Formatos (`psp2common/jpegenc.h` + Vita3K):** `0 ARGB8888` (Vita3K: RGBA8888), `4 BGRA8888` (só Vita3K),
  `8 YCBCR420`, `9 YCBCR422`, flag `16 CSC_ARGB_YCBCR` (conversão de cor por hardware dentro do Encode).
  `pixelFormat & 0xF` dá a amostragem de saída. Ordem real dos bytes de "ARGB8888" no Vita (memória little-endian
  R,G,B,A?) a confirmar com uma foto de cor conhecida — o framebuffer do Vita é A8B8G8R8 (bytes R,G,B,A), e o
  Vita3K trata 0 como RGBA, o que bate.
- **Layout YCbCr de entrada:** planar Y, Cb, Cr contíguos com largura = `inWidth` (alinhamento de pitch a 16? a
  confirmar; Vita3K usa `av_image_fill_arrays(..., align 1)`). **Implementado como Vita3K:** Y `inWidth×inHeight`,
  depois Cb e Cr de `ceil(inWidth/2) × ceil(inHeight/vdiv)` cada (vdiv 2 para 4:2:0, 1 para 4:2:2), sem padding.
  `Csc` produz exatamente esse layout.
- **Flag 16 (CSC_ARGB_YCBCR):** aceita no Init, mas `Encode` **sempre** lê YCbCr (como Vita3K, que roda os jogos
  que usam a lib — Uncharted chama `Csc` e depois `Encode` com o buffer YCbCr). A leitura "Encode converte ARGB
  internamente" não foi adotada: quebraria o fluxo Csc→Encode. A confirmar no hardware.
- **Encoder (implementado):** encoder baseline JPEG próprio, 100% gerenciado (`Media/JpegEncoder.cs`): FDCT 8×8,
  quantização, Huffman com as tabelas típicas do Annex K, MCU de borda por replicação, restart opcional.
  Qualidade: `scale = 1 + ratio*15/255` (1..16, mesmo mapeamento ratio→qscale do Vita3K) e tabelas = Annex K ×
  scale/8 (ratio 64 → tabelas "qualidade 75"; ratio 0 → ~94; 255 → ~25). `headerMode == JPEG`: SOI, APP0 JFIF,
  DQT, SOF0, DHT, SOS, EOI. `headerMode == MJPEG`: sem APP0 e sem DHT (decodificadores MJPEG usam as tabelas
  padrão; conteúdo exato do hardware a confirmar). Saída validada decodificando no GDI+ do Windows.
- **Arquivos:** `src/VitaSharp.Core/Hle/Modules/JpegEnc.cs` (`sealed class JpegEnc : HleModule`, SceJpegEncUser +
  SceJpegEncArm, dicionários de contexto separados), `Media/JpegEncoder.cs`, `Media/YuvConvert.FromRgba`.
- **Dependências:** `GuestMemory`. Custo HLE na faixa padrão (não existe classe "pesada").

**Códigos de erro (`psp2common/jpegenc.h`):**

| Nome | Valor |
| --- | --- |
| SCE_JPEGENC_ERROR_IMAGE_SIZE | 0x80650200 |
| SCE_JPEGENC_ERROR_INSUFFICIENT_BUFFER | 0x80650201 |
| SCE_JPEGENC_ERROR_INVALID_COMPRATIO | 0x80650202 |
| SCE_JPEGENC_ERROR_INVALID_PIXELFORMAT | 0x80650203 |
| SCE_JPEGENC_ERROR_INVALID_HEADER_MODE | 0x80650204 |
| SCE_JPEGENC_ERROR_INVALID_POINTER | 0x80650205 |
| SCE_JPEGENC_ERROR_NOT_PHY_CONTINUOUS_MEMORY | 0x80650206 |

`NOT_PHY_CONTINUOUS_MEMORY` não deve ser emitido pelo HLE (toda memória guest é "contígua" para nós). Alinhamento de
256 bytes de `outBuffer`/`inBuffer` (exigido pelo header) → `INVALID_POINTER` se violado (a confirmar se o hardware
checa; implementar checagem só depois de confirmar, para não quebrar jogos).

### sceJpegEncoderGetContextSize
- [x] **Prioridade:** P2 · **Biblioteca:** SceJpegEncUser · **NID:** 0x2B55844D
- **Protótipo:** `int sceJpegEncoderGetContextSize(void);` (`psp2/jpegenc.h`)
- **Comportamento:** devolve o tamanho de memória que o jogo deve alocar para o contexto.
- **Retorno/erros:** tamanho em bytes (> 0). Valor real a confirmar (ler o retorno num Vita real com homebrew).
  Vita3K devolve `sizeof` da sua própria struct (~0x20).
- **Implementação no VitaSharp (feito):** devolve `0x20` (igual ao Vita3K); o estado fica host-side e a memória
  do contexto guest nunca é escrita. Valor real do hardware continua a confirmar.
- **Validação:** homebrew; jogo passa do `malloc` do contexto.

### sceJpegEncoderInit
- [x] **Prioridade:** P2 · **Biblioteca:** SceJpegEncUser · **NID:** 0x88DA92B4
- **Protótipo:** `int sceJpegEncoderInit(SceJpegEncoderContext context, int inWidth, int inHeight,
  SceJpegEncoderPixelFormat pixelformat, void *outBuffer, SceSize outSize);` (`psp2/jpegenc.h`; 5º e 6º args na pilha)
- **Comportamento:** registra o contexto com as dimensões, formato e buffer de saída; ratio = 64, header = JPEG.
- **Retorno/erros:** 0; `INVALID_POINTER` (0x80650205) se `context`/`outBuffer` nulos; `INVALID_PIXELFORMAT`
  (0x80650203) se `pixelformat & 0xF ∉ {0, 4, 8, 9}`/bits desconhecidos; `IMAGE_SIZE` (0x80650200) se dimensões
  inválidas (faixa real a confirmar — provavelmente até 2048 ou 4096 px, múltiplos de 2 para 4:2:x).
- **Implementação no VitaSharp (feito):** `_ctx[context] = new Ctx { ..., Ratio = 64, HeaderMode = JPEG,
  região válida = Init }`. Formato aceito: bits só em `0x1F` e `& 0xF ∈ {0, 4, 8, 9}` (0/4 são aceitos no Init, mas
  `Csc`/`Encode` exigem 8/9). Dimensões aceitas: 1..65535 (limite JPEG; o do hardware é desconhecido, não é
  aplicado para não quebrar jogos). Sem checagem de alinhamento de 256 bytes.
- **Validação:** homebrew que codifica 960×544 e grava o .jpg; abrir no PC.

### sceJpegEncoderInitWithParam
- [x] **Prioridade:** P2 · **Biblioteca:** SceJpegEncUser · **NID:** 0x2E8FE45D
- **Protótipo:** `int sceJpegEncoderInitWithParam(SceJpegEncoderContext context, const SceJpegEncoderInitParam *initParam);` (`psp2/jpegenc.h`)
- **Comportamento:** como `Init`, com `option` (`1 = LPDDR2_MEMORY`: buffers em LPDDR2 em vez de CDRAM — sem efeito
  no HLE).
- **Retorno/erros:** como `Init`; `initParam == NULL` → `INVALID_POINTER`; `size != 0x1C` → erro a confirmar.
- **Estruturas:** `SceJpegEncoderInitParam` 0x1C: +0 size, +4 inWidth, +8 inHeight, +0xC pixelFormat,
  +0x10 outBuffer, +0x14 outSize, +0x18 option.
- **Implementação no VitaSharp (feito):** ler a struct e chamar o helper de `Init`; `size` não é verificado (como
  Vita3K); `option` guardado sem efeito.
- **Validação:** idem.

### sceJpegEncoderEnd
- [x] **Prioridade:** P2 · **Biblioteca:** SceJpegEncUser · **NID:** 0xC87AA849
- **Protótipo:** `int sceJpegEncoderEnd(SceJpegEncoderContext context);` (`psp2/jpegenc.h`)
- **Comportamento:** finaliza o contexto (o jogo libera a memória depois).
- **Retorno/erros:** 0; contexto desconhecido → `INVALID_POINTER`? a confirmar (retornar 0).
- **Implementação no VitaSharp (feito):** `_ctx.Remove(context)`, sempre 0. Chamadas posteriores com o contexto
  encerrado (ou nunca inicializado) devolvem `INVALID_POINTER`.
- **Validação:** Init → End → Init no mesmo endereço funciona.

### sceJpegEncoderEncode
- [x] **Prioridade:** P2 · **Biblioteca:** SceJpegEncUser · **NID:** 0xC60DE94C
- **Protótipo:** `int sceJpegEncoderEncode(SceJpegEncoderContext context, const void *inBuffer);` (`psp2/jpegenc.h`)
- **Comportamento:** codifica a imagem de `inBuffer` (YCbCr planar no formato do contexto; ou ARGB se o formato tem
  a flag 16 — conversão interna) para `outBuffer`. Se `SetValidRegion` foi chamado, codifica só o retângulo
  (0,0)-(validW,validH) da imagem de largura `inWidth`.
- **Retorno/erros:** **tamanho do JPEG em bytes** (> 0); `INSUFFICIENT_BUFFER` (0x80650201) se não couber em
  `outSize`; `INVALID_PIXELFORMAT` se formato sem amostragem 8/9; `INVALID_POINTER` se `inBuffer` nulo.
- **Implementação no VitaSharp (feito):** lê os planos YCbCr do guest no layout da arquitetura (a flag 16 **não**
  faz o Encode ler ARGB — ver Arquitetura), `JpegEncoder.Encode(validW, validH, strides do Init, tabelas do ratio,
  headerMode)` e copia para `outBuffer`; nada é escrito se não couber (`INSUFFICIENT_BUFFER`). Contexto
  desconhecido ou `inBuffer` nulo → `INVALID_POINTER`; formato sem 8/9 → `INVALID_PIXELFORMAT`.
- **Validação:** homebrew/jogo: arquivo resultante abre num visualizador; tamanho cresce quando ratio diminui.

### sceJpegEncoderSetCompressionRatio
- [x] **Prioridade:** P2 · **Biblioteca:** SceJpegEncUser · **NID:** 0xB2B828EC
- **Protótipo:** `int sceJpegEncoderSetCompressionRatio(SceJpegEncoderContext context, int ratio);` (`psp2/jpegenc.h`)
- **Comportamento:** 0..255, maior = mais compressão/menor qualidade.
- **Retorno/erros:** 0; `INVALID_COMPRATIO` (0x80650202) fora de 0..255.
- **Implementação no VitaSharp (feito):** guardar no contexto; tabelas = Annex K × (1 + ratio·15/255)/8.
- **Validação:** teste unitário de faixa.

### sceJpegEncoderSetOutputAddr
- [x] **Prioridade:** P2 · **Biblioteca:** SceJpegEncUser · **NID:** 0x25D52D97
- **Protótipo:** `int sceJpegEncoderSetOutputAddr(SceJpegEncoderContext context, void *outBuffer, SceSize outSize);` (`psp2/jpegenc.h`)
- **Comportamento:** troca o buffer de saída para os próximos `Encode`.
- **Retorno/erros:** 0; `INVALID_POINTER` se nulo.
- **Implementação no VitaSharp (feito):** atualizar o contexto.
- **Validação:** dois encodes em buffers diferentes.

### sceJpegEncoderCsc
- [x] **Prioridade:** P2 · **Biblioteca:** SceJpegEncUser · **NID:** 0x824A7D4F
- **Protótipo:** `int sceJpegEncoderCsc(SceJpegEncoderContext context, void *outBuffer, const void *inBuffer,
  int inPitch, SceJpegEncoderPixelFormat inPixelFormat);` (`psp2/jpegenc.h`)
- **Comportamento:** converte ARGB8888 (pitch `inPitch` em pixels) com as dimensões do contexto para YCbCr planar na
  amostragem do contexto (`pixelFormat & 0xF` = 8 ou 9), em `outBuffer`, pronto para `Encode`.
- **Retorno/erros:** 0; `INVALID_PIXELFORMAT` se contexto não é YCbCr420/422 ou `inPixelFormat ∉ {0, 4}`.
- **Implementação no VitaSharp (feito):** `YuvConvert.FromRgba(src, inPitch, w, h, bgra: inPixelFormat == 4, 2,
  vdiv)` sobre o tamanho do Init, JFIF full-range (coeficientes BT.601 sem offset de 16; Vita3K usa swscale em
  limited-range e depois codifica como full-range, o que desbota as cores — a confirmar no hardware). Croma =
  média dos pixels cobertos. 0 = bytes R,G,B,A (framebuffer do Vita), 4 = B,G,R,A. `inPitch < inWidth` → usa
  `inWidth`. Nulos → `INVALID_POINTER`.
- **Validação:** teste unitário com cores primárias; Csc + Encode + decode volta ao RGBA original (PSNR > 30 dB).

### sceJpegEncoderSetValidRegion
- [x] **Prioridade:** P2 · **Biblioteca:** SceJpegEncUser · **NID:** 0x9511F3BC
- **Protótipo:** `int sceJpegEncoderSetValidRegion(SceJpegEncoderContext context, int inWidth, int inHeight);` (`psp2/jpegenc.h`)
- **Comportamento:** define a região a codificar a partir de (0,0) (descrição do `jpegencarm.h`); o buffer de
  entrada continua com largura do Init. Vita3K: não implementado.
- **Retorno/erros:** 0; `IMAGE_SIZE` se maior que as dimensões do Init.
- **Implementação no VitaSharp (feito):** guardar `validW/H` (padrão = Init; 1..Init, senão `IMAGE_SIZE`);
  `Encode` codifica o canto superior esquerdo mantendo os strides do Init.
- **Validação:** codificar 960×544 com região 480×272 e conferir dimensões do JPEG.

### sceJpegEncoderSetHeaderMode
- [x] **Prioridade:** P2 · **Biblioteca:** SceJpegEncUser · **NID:** 0x2F58B12C
- **Protótipo:** `int sceJpegEncoderSetHeaderMode(SceJpegEncoderContext context, int mode);` (`psp2/jpegenc.h`)
- **Comportamento:** `0` JPEG completo (JFIF com DQT/DHT), `1` MJPEG (frame sem DHT, para streams MJPEG).
  Conteúdo exato dos marcadores no modo MJPEG a confirmar (gerar no Vita e inspecionar).
- **Retorno/erros:** 0; `INVALID_HEADER_MODE` (0x80650204) para outros valores.
- **Implementação no VitaSharp (feito):** guardar; `MJPEG` omite APP0 e DHT (Vita3K não suporta o modo MJPEG).
  O `sceJpegDecodeMJpeg*` do VitaSharp decodifica esses frames com as tabelas padrão.
- **Validação:** inspeção hex do resultado.

## SceJpegEncArm (8 funções)

### Arquitetura (SceJpegEncArm)

- **Objetivo:** encoder JPEG em software ARM, API espelhada do SceJpegEnc (`psp2/jpegencarm.h`). Sem CSC (só
  YCbCr 4:2:0 = 8 e 4:2:2 = 9), dimensões `SceUInt16`, ratio `SceUInt8` 1..255 (padrão 64,
  `SCE_JPEGENCARM_DEFAULT_COMP_RATIO`), buffers alinhados a 8 bytes, contexto alinhado a 4. Vita3K: todas
  `UNIMPLEMENTED`.
- **Implementação (feita):** mesma classe `JpegEnc.cs`, dicionário de contextos separado do SceJpegEnc, mesmos
  helpers e layout de entrada. `GetContextSize` = 0x20. Init aceita só 8/9 (sem flag 16). Checagens de alinhamento
  (8 bytes nos buffers, 4 no contexto) não implementadas até confirmar. Códigos de erro próprios:

| Nome | Valor |
| --- | --- |
| SCE_JPEGENCARM_ERROR_IMAGE_SIZE | 0x80650300 |
| SCE_JPEGENCARM_ERROR_INSUFFICIENT_BUFFER | 0x80650301 |
| SCE_JPEGENCARM_ERROR_INVALID_COMP_RATIO | 0x80650302 |
| SCE_JPEGENCARM_ERROR_INVALID_PIXELFORMAT | 0x80650303 |
| SCE_JPEGENCARM_ERROR_INVALID_HEADER_MODE | 0x80650304 |
| SCE_JPEGENCARM_ERROR_INVALID_POINTER | 0x80650305 |

| Função | NID | Protótipo (`psp2/jpegencarm.h`) | Comportamento |
| --- | --- | --- | --- |
| sceJpegArmEncoderGetContextSize | 0x8D15B36D | `SceSize sceJpegArmEncoderGetContextSize(void);` | Tamanho do contexto (valor real a confirmar; usar constante como no SceJpegEnc). |
| sceJpegArmEncoderInit | 0x8C23875A | `int sceJpegArmEncoderInit(SceJpegArmEncoderContext context, SceUInt16 inWidth, SceUInt16 inHeight, SceJpegArmEncoderPixelFormat pixelformat, void *outBuffer, SceSize outSize);` | Registra contexto; ratio 64, header JPEG. Erros: `INVALID_POINTER` (nulo/desalinhado), `INVALID_PIXELFORMAT` (≠ 8/9), `IMAGE_SIZE`. |
| sceJpegArmEncoderEnd | 0x8A42D6EC | `int sceJpegArmEncoderEnd(SceJpegArmEncoderContext context);` | Remove o contexto; 0. |
| sceJpegArmEncoderEncode | 0x648FFAA3 | `int sceJpegArmEncoderEncode(SceJpegArmEncoderContext context, const void *inBuffer);` | Codifica YCbCr → JPEG; retorna tamanho ou `INSUFFICIENT_BUFFER` (0x80650301). |
| sceJpegArmEncoderSetCompressionRatio | 0x32B8E159 | `int sceJpegArmEncoderSetCompressionRatio(SceJpegArmEncoderContext context, SceUInt8 ratio);` | 1..255; 0 → `INVALID_COMP_RATIO` (0x80650302). |
| sceJpegArmEncoderSetOutputAddr | 0xDA0D0868 | `int sceJpegArmEncoderSetOutputAddr(SceJpegArmEncoderContext context, void *outBuffer, SceSize outSize);` | Troca buffer de saída. |
| sceJpegArmEncoderSetValidRegion | 0xD561E2E5 | `int sceJpegArmEncoderSetValidRegion(SceJpegArmEncoderContext context, SceUInt16 regionWidth, SceUInt16 regionHeight);` | Região a partir de (0,0); maior que Init → `IMAGE_SIZE` (0x80650300). |
| sceJpegArmEncoderSetHeaderMode | 0x6D24A669 | `int sceJpegArmEncoderSetHeaderMode(SceJpegArmEncoderContext context, SceJpegArmEncoderHeaderMode mode);` | 0 JPEG / 1 MJPEG; outro → `INVALID_HEADER_MODE` (0x80650304). |

### sceJpegArmEncoderGetContextSize
- [x] **Prioridade:** P2 · **Biblioteca:** SceJpegEncArm · **NID:** 0x8D15B36D
- **Protótipo:** ver tabela. **Comportamento:** tamanho do contexto. **Retorno/erros:** tamanho (> 0), a confirmar.
- **Implementação no VitaSharp:** constante (estado host-side). **Validação:** homebrew.

### sceJpegArmEncoderInit
- [x] **Prioridade:** P2 · **Biblioteca:** SceJpegEncArm · **NID:** 0x8C23875A
- **Protótipo:** ver tabela (u16 em r1/r2: mascarar `& 0xFFFF`).
- **Comportamento/Retorno:** ver tabela. **Implementação no VitaSharp:** helper comum com `arm: true` (sem flag 16,
  erros 0x806503xx, checagem de alinhamento 8 só depois de confirmada). **Validação:** homebrew codificando YCbCr.

### sceJpegArmEncoderEnd
- [x] **Prioridade:** P2 · **Biblioteca:** SceJpegEncArm · **NID:** 0x8A42D6EC
- **Protótipo/Comportamento:** ver tabela. **Retorno/erros:** 0. **Implementação no VitaSharp:** remover do
  dicionário. **Validação:** Init/End/Init.

### sceJpegArmEncoderEncode
- [x] **Prioridade:** P2 · **Biblioteca:** SceJpegEncArm · **NID:** 0x648FFAA3
- **Protótipo:** ver tabela. **Comportamento:** como `sceJpegEncoderEncode` sem CSC. **Retorno/erros:** tamanho em
  bytes; `INSUFFICIENT_BUFFER` 0x80650301; `INVALID_POINTER` 0x80650305.
- **Implementação no VitaSharp:** `JpegEncoder.Encode` (feito). **Validação:** arquivo abre no PC.

### sceJpegArmEncoderSetCompressionRatio
- [x] **Prioridade:** P2 · **Biblioteca:** SceJpegEncArm · **NID:** 0x32B8E159
- **Protótipo/Comportamento/Retorno:** ver tabela (faixa 1..255, `SCE_JPEGENCARM_MIN/MAX_COMP_RATIO`).
- **Implementação no VitaSharp:** `ratio & 0xFF`; mesmo mapeamento para qscale. **Validação:** teste de faixa.

### sceJpegArmEncoderSetOutputAddr
- [x] **Prioridade:** P2 · **Biblioteca:** SceJpegEncArm · **NID:** 0xDA0D0868
- **Protótipo/Comportamento:** ver tabela. **Retorno/erros:** 0; nulo → 0x80650305. **Implementação no VitaSharp:**
  atualizar contexto. **Validação:** dois buffers.

### sceJpegArmEncoderSetValidRegion
- [x] **Prioridade:** P2 · **Biblioteca:** SceJpegEncArm · **NID:** 0xD561E2E5
- **Protótipo/Comportamento:** ver tabela. **Retorno/erros:** 0; 0x80650300. **Implementação no VitaSharp:** recorte
  no Encode. **Validação:** dimensões do JPEG.

### sceJpegArmEncoderSetHeaderMode
- [x] **Prioridade:** P2 · **Biblioteca:** SceJpegEncArm · **NID:** 0x6D24A669
- **Protótipo/Comportamento:** ver tabela. **Retorno/erros:** 0; 0x80650304. **Implementação no VitaSharp:** como
  SceJpegEnc. **Validação:** inspeção hex.

## SceVideoencUser (21 funções)

### Arquitetura (SceVideoencUser)

- **Objetivo:** encoder H.264/AVC por hardware (mesmo bloco do `SceVideodec`/`SceAvcdec`), usado para gravar vídeo
  (gameplay, câmera) — normalmente indiretamente via `SceLibMp4Recorder`. O módulo de usuário (`SceAvcodecUser`)
  é um wrapper fino sobre as syscalls `_sceAvcenc*`/`_sceVideoenc*` do kernel `SceAvcodec` (NIDs em
  `vita-headers/db/360/SceAvcodec.yml`; também em `Hle/Data/nids.txt`).
- **Fontes:** **não há header no vitasdk nem módulo no Vita3K.** Todos os protótipos abaixo são **inferidos** por
  analogia com o par `sceVideodec*`/`sceAvcdec*` já implementado em `Hle/Modules/Videodec.cs`
  (`sceVideodecInitLibrary(codecType, initInfo)`, `sceVideodecQueryMemSize(codecType, initInfo, memInfo)`,
  `sceAvcdecQueryDecoderMemSize(codecType, query, info)`, `sceAvcdecCreateDecoder(codecType, ctrl, query)`).
  Como confirmar: (1) desmontar `os0:us/avcodec_us.suprx` (SceAvcodecUser) — cada export é um thunk que move
  r0–r3/pilha para a syscall, então o número de argumentos e quais são ponteiros (copiados com
  `sceKernelCopyFromUser`-like no kernel `SceAvcodec`) aparece direto; (2) Henkaku wiki "SceAvcodec"; (3) o
  `SceLibMp4Recorder` é cliente dessa API — desmontá-lo mostra os layouts de struct reais.
- **Variantes:** `*Basic` = perfil Baseline com parâmetros simplificados (inferido pelo nome); `*Internal` =
  versões para módulos do sistema (provavelmente com struct extra ou sem validação de permissão); `WithUnmapMem` =
  o jogo fornece a memória do codec (igual ao `sceVideodecInitLibraryWithUnmapMem`).
- **Estado interno:** `_libraryInitialized[codecType]`; encoders por handle (gravado no ctrl do jogo, como
  `Videodec` faz em `ctrl+0`): largura/altura, bitrate, preset, fila de NAL units produzidas.
- **Encoder:** FFmpeg `avcodec_find_encoder(AV_CODEC_ID_H264)`. O build embarcado é LGPL (BtbN n8.1): `libx264`
  (GPL) **não** deve estar presente; o DLL contém as strings `h264_mf` (Media Foundation, Windows), `libopenh264` e
  `h264_nvenc` — quais estão realmente habilitados está a confirmar com `avcodec_find_encoder_by_name` em tempo de
  execução. Se nenhum existir, o encoder vira **stub funcional**: aceita frames, não produz NAL units (ou produz um
  stream mínimo pré-gerado), sem crash.
- **Recomendação:** nenhum jogo conhecido importa `SceVideoencUser` diretamente; implementar **stub funcional**
  primeiro (validação de argumentos + handles + `GetNalUnit` sem dados) e só ligar o FFmpeg se um jogo depender
  do conteúdo do vídeo (o que é improvável: o resultado vai para arquivo/rede, não volta ao jogo).
- **Arquivo novo:** `src/VitaSharp.Core/Hle/Modules/Videoenc.cs` (`sealed class Videoenc : HleModule`). Opcional:
  `Media/H264Encoder.cs` espelhando `H264Decoder.cs`.
- **Dependências:** `GuestMemory`, `FFmpegLoader` (opcional), `YuvConvert` (para `sceAvcencCsc`).
- **Códigos de erro:** desconhecidos. Provável família do SceVideodec (`0x8062xxxx`; `Videodec.cs` usa
  `0x80620002 INVALID_PARAM` e `0x80620009 INVALID_POINTER`), mas os do encoder **a confirmar** (procurar
  constantes `0x806x....` no `avcodec_us.suprx`/kernel `SceAvcodec`). Até lá, usar os dois de `Videodec.cs`.
- **Tipo de codec:** provavelmente `0x2001` (SCE_VIDEOENC_TYPE_HW_AVCENC), por analogia com
  `TYPE_HW_AVCDEC = 0x1001` — a confirmar; enquanto não confirmado, aceitar qualquer valor e logar `WarnOnce`.
- **Implementado (stub funcional, `Hle/Modules/Videoenc.cs`):** sem FFmpeg/encoder; bibliotecas e handles rastreados,
  handle em `ctrl+0`, w/h lidos de `query+0/+4` (limite 1920×1088 como o decoder), tamanhos escritos só em
  `memInfo+0`/`info+0`; `Encode` conta e descarta o quadro; nenhuma struct de imagem/NAL é escrita. `CreateEncoder`
  não exige `InitLibrary`; handle desconhecido → `INVALID_PARAM` (0x80620002); `sceAvcencCsc` fica desmarcado (ver).

### sceVideoencInitLibrary
- [x] **Prioridade:** P2 · **Biblioteca:** SceVideoencUser · **NID:** 0xACA59885
- **Protótipo (inferido):** `int sceVideoencInitLibrary(SceUInt32 codecType, const SceVideoencInitInfo *pInitInfo);`
- **Comportamento:** inicializa o encoder de hardware para o tipo de codec, reservando a memória interna do codec
  (tamanho dado por `sceVideoencQueryMemSize`).
- **Retorno/erros:** 0; tipo inválido → `INVALID_PARAM` (código a confirmar); já inicializado → a confirmar.
- **Estruturas:** `SceVideoencInitInfo` desconhecida (provável `{size, ...}` como no decoder) — a confirmar.
- **Implementação no VitaSharp:** marcar `_initialized.Add(codecType)`; retornar 0.
- **Validação:** desmontagem (ver arquitetura); jogo/app que importe deve seguir para `QueryEncoderMemSize`.

### sceVideoencInitLibraryWithUnmapMem
- [x] **Prioridade:** P2 · **Biblioteca:** SceVideoencUser · **NID:** 0x8F970948
- **Protótipo (inferido):** `int sceVideoencInitLibraryWithUnmapMem(SceUInt32 codecType, const SceVideoencInitInfo *pInitInfo, const SceVideoencMemInfo *pMemInfo);`
- **Comportamento:** como `InitLibrary`, mas usa memória fornecida pelo jogo (`pMemInfo`: provável `{size, pMem}`).
- **Retorno/erros:** como `InitLibrary`; `pMemInfo` nulo → `INVALID_POINTER`.
- **Implementação no VitaSharp:** ignorar a memória (estado host-side); delegar.
- **Validação:** idem.

### sceVideoencInitLibraryInternal / sceVideoencInitLibraryWithUnmapMemInternal
- [x] **Prioridade:** P2 · **Biblioteca:** SceVideoencUser · **NID:** 0xAF0F17AD / 0x54D750B8
- **Protótipo (inferido):** mesmos argumentos das versões públicas (possivelmente com um argumento extra de
  flags/pid — a confirmar pela desmontagem).
- **Comportamento:** variantes para módulos do sistema (ex.: o próprio `SceLibMp4Recorder`).
- **Retorno/erros:** como as públicas.
- **Implementação no VitaSharp:** delegar às públicas.
- **Validação:** idem.

### sceVideoencQueryMemSize / sceVideoencQueryMemSizeInternal
- [x] **Prioridade:** P2 · **Biblioteca:** SceVideoencUser · **NID:** 0x7772157D / 0x3E459BA8
- **Protótipo (inferido):** `int sceVideoencQueryMemSize(SceUInt32 codecType, const SceVideoencInitInfo *pInitInfo, SceVideoencMemInfo *pMemInfo);`
- **Comportamento:** escreve em `pMemInfo` (offset 0, provável `size`) a memória que a biblioteca precisa.
- **Retorno/erros:** 0; `pMemInfo` nulo → `INVALID_POINTER`.
- **Implementação no VitaSharp:** escrever um valor fixo (ex. 0x40000, como `sceVideodecQueryMemSize`); layout de
  `pMemInfo` a confirmar (no decoder `Videodec.cs` escreve em `memInfo+0`).
- **Validação:** idem.

### sceVideoencTermLibrary
- [x] **Prioridade:** P2 · **Biblioteca:** SceVideoencUser · **NID:** 0x7FE7EDA3
- **Protótipo (inferido):** `int sceVideoencTermLibrary(SceUInt32 codecType);`
- **Comportamento:** finaliza a biblioteca do codec.
- **Retorno/erros:** 0.
- **Implementação no VitaSharp:** remover de `_initialized`; destruir encoders restantes.
- **Validação:** idem.

### sceAvcencQueryEncoderMemSize
- [x] **Prioridade:** P2 · **Biblioteca:** SceVideoencUser · **NID:** 0xAF317203
- **Protótipo (inferido):** `int sceAvcencQueryEncoderMemSize(SceUInt32 codecType, const SceAvcencQueryEncoder *pQuery, SceAvcencEncoderInfo *pInfo);`
- **Comportamento:** dado largura/altura/parâmetros máximos (`pQuery`, provável `{width, height, ...}`), devolve o
  tamanho de memória do encoder em `pInfo` (provável `{frameMemSize}`).
- **Retorno/erros:** 0; ponteiro nulo → `INVALID_POINTER`; dimensões fora do limite (provável ≤ 1920×1088 como o
  decoder; a confirmar) → `INVALID_PARAM`.
- **Implementação no VitaSharp:** ler w/h de +0/+4 (a confirmar); escrever `align(w*h*3/2 * 3, 0x40000)` (estimativa
  arbitrária e inofensiva: o jogo só aloca).
- **Validação:** desmontagem do `SceLibMp4Recorder` (que chama esta função com modos conhecidos 640×368 etc.).

### sceAvcencQueryEncoderMemSizeBasic / sceAvcencQueryEncoderMemSizeInternal
- [x] **Prioridade:** P2 · **Biblioteca:** SceVideoencUser · **NID:** 0xC6616EA8 / 0x55F2FD3C
- **Protótipo (inferido):** mesmos argumentos de `sceAvcencQueryEncoderMemSize` com struct de query "Basic"/interna.
- **Comportamento/Retorno:** idem.
- **Implementação no VitaSharp:** delegar ao helper comum.
- **Validação:** idem.

### sceAvcencCreateEncoder
- [x] **Prioridade:** P2 · **Biblioteca:** SceVideoencUser · **NID:** 0x7F5728CC
- **Protótipo (inferido):** `int sceAvcencCreateEncoder(SceUInt32 codecType, SceAvcencCtrl *pCtrl, const SceAvcencQueryEncoder *pQuery);`
- **Comportamento:** cria o encoder na memória `pCtrl->frameBuf` fornecida e grava o handle em `pCtrl` (provável
  +0, como `SceAvcdecCtrl {handle, frameBuf{p,size}}`).
- **Retorno/erros:** 0; `INVALID_POINTER`; `INVALID_PARAM`; biblioteca não inicializada → a confirmar.
- **Implementação no VitaSharp:** `new EncoderState { W, H, Codec = H264Encoder.Create(...) /* pode ser null */ }`,
  handle incremental em `ctrl+0`.
- **Validação:** idem.

### sceAvcencCreateEncoderBasic / sceAvcencCreateEncoderInternal
- [x] **Prioridade:** P2 · **Biblioteca:** SceVideoencUser · **NID:** 0xC70B7E2E / 0x11CB9524
- **Protótipo (inferido):** mesmos argumentos de `sceAvcencCreateEncoder`.
- **Comportamento/Retorno:** idem (Basic = perfil Baseline).
- **Implementação no VitaSharp:** helper comum.
- **Validação:** idem.

### sceAvcencDeleteEncoder
- [x] **Prioridade:** P2 · **Biblioteca:** SceVideoencUser · **NID:** 0xEC08A60C
- **Protótipo (inferido):** `int sceAvcencDeleteEncoder(SceAvcencCtrl *pCtrl);`
- **Comportamento:** destrói o encoder.
- **Retorno/erros:** 0; handle desconhecido → `INVALID_PARAM`? a confirmar.
- **Implementação no VitaSharp:** remover handle, `Dispose` do codec.
- **Validação:** idem.

### sceAvcencSetEncoderParameter
- [x] **Prioridade:** P2 · **Biblioteca:** SceVideoencUser · **NID:** 0x5B4A70BD
- **Protótipo (inferido):** `int sceAvcencSetEncoderParameter(SceAvcencCtrl *pCtrl, const SceAvcencEncoderParam *pParam);`
- **Comportamento:** define bitrate, framerate, GOP, perfil/nível, rate control. Layout a confirmar.
- **Retorno/erros:** 0; parâmetro inválido → `INVALID_PARAM`.
- **Implementação no VitaSharp:** logar a struct (dump 0x40 bytes) uma vez; guardar bitrate se identificado;
  retornar 0.
- **Validação:** idem.

### sceAvcencSetAvailablePreset
- [x] **Prioridade:** P2 · **Biblioteca:** SceVideoencUser · **NID:** 0x2238693E
- **Protótipo (inferido):** `int sceAvcencSetAvailablePreset(SceAvcencCtrl *pCtrl, SceInt32 preset);` (talvez sem
  `pCtrl` — global; a confirmar).
- **Comportamento:** escolhe um preset de qualidade/resolução pré-definido (os modos do Mp4Recorder
  640×368/480×272/368×208 a 1/2 Mbps sugerem os presets).
- **Retorno/erros:** 0; preset inválido → `INVALID_PARAM`.
- **Implementação no VitaSharp:** guardar e retornar 0.
- **Validação:** idem.

### sceAvcencCsc
- [x] **Prioridade:** P2 · **Biblioteca:** SceVideoencUser · **NID:** 0xAC758FF7
- **Protótipo (inferido):** `int sceAvcencCsc(SceAvcencCtrl *pCtrl, SceAvcencPicture *pDst, const SceAvcencPicture *pSrc);`
- **Comportamento:** conversão de cor/escala por hardware RGBA (A8B8G8R8) → YUV420 (packed/NV12, entrada do
  encoder), equivalente ao `sceMp4RecCsc`.
- **Retorno/erros:** 0; `INVALID_POINTER`.
- **Implementação no VitaSharp:** `YuvConvert.RgbaToYuv420` com escala bilinear se dimensões diferirem; layout de
  `SceAvcencPicture` a confirmar (usar como hipótese o `SceMp4RecFrame`: pixelformat +4, stride +8, width +0xC,
  height +0x10, buffer +0x24).
  **Estado atual:** `Videoenc.cs` só valida `ctrl`/ponteiros e retorna 0 **sem escrever** — escrever pixels num
  ponteiro tirado de um layout hipotético poderia corromper memória do jogo; marcado como stub correto (2026-10-22): valida `ctrl` e ponteiros, retorna 0 e não escreve nada até o layout ser confirmado
  (não existe `YuvConvert`; a conversão RGBA→YUV420 packed real está em `Mp4Recorder.RgbaToYuv420Packed`).
- **Validação:** idem.

### sceAvcencEncode
- [x] **Prioridade:** P2 · **Biblioteca:** SceVideoencUser · **NID:** 0x4B171BEA
- **Protótipo (inferido):** `int sceAvcencEncode(SceAvcencCtrl *pCtrl, const SceAvcencInputPicture *pIn, SceAvcencEncodeResult *pOut);`
- **Comportamento:** envia um quadro YUV420 ao encoder; as NAL units resultantes são retiradas com
  `sceAvcencGetNalUnit`.
- **Retorno/erros:** 0; `INVALID_POINTER`.
- **Implementação no VitaSharp:** se houver `H264Encoder`, `avcodec_send_frame` + enfileirar pacotes divididos em
  NAL units (procurar start codes `00 00 01`); senão, descartar o quadro.
- **Validação:** idem; arquivo gravado pelo Mp4Recorder toca no PC (quando houver encoder real).

### sceAvcencEncodeFlush
- [x] **Prioridade:** P2 · **Biblioteca:** SceVideoencUser · **NID:** 0x03D1CFED
- **Protótipo (inferido):** `int sceAvcencEncodeFlush(SceAvcencCtrl *pCtrl);`
- **Comportamento:** força a saída dos quadros retidos (B-frames/lookahead).
- **Retorno/erros:** 0.
- **Implementação no VitaSharp:** `avcodec_send_frame(null)` e drenar; sem codec: 0.
- **Validação:** idem.

### sceAvcencEncodeStop
- [x] **Prioridade:** P2 · **Biblioteca:** SceVideoencUser · **NID:** 0x4BE87709
- **Protótipo (inferido):** `int sceAvcencEncodeStop(SceAvcencCtrl *pCtrl);` (pode ter argumento de saída — a confirmar)
- **Comportamento:** encerra a sessão de codificação (fim de stream).
- **Retorno/erros:** 0.
- **Implementação no VitaSharp:** flush + marcar EOS.
- **Validação:** idem.

### sceAvcencGetNalUnit
- [x] **Prioridade:** P2 · **Biblioteca:** SceVideoencUser · **NID:** 0xC5609479
- **Protótipo (inferido):** `int sceAvcencGetNalUnit(SceAvcencCtrl *pCtrl, SceAvcencNalUnit *pNal);`
- **Comportamento:** retira a próxima NAL unit (ponteiro/tamanho/pts) produzida pelo encoder.
- **Retorno/erros:** 0 com dado; "sem dados" → código a confirmar (provável erro específico ou `size = 0`).
- **Implementação no VitaSharp:** com fila vazia (stub), preencher tamanho 0 e retornar o código de "sem dados"
  assim que confirmado — até lá, `size = 0` e retorno 0, observando se o jogo entra em loop (se entrar, trocar para
  erro). **Implementado:** como o layout de `SceAvcencNalUnit` não é conhecido, a struct **não é escrita** (nem o
  `size`); retorna 0 com `WarnOnce`.
- **Validação:** idem.

## SceLibMp4Recorder (10 funções)

### Arquitetura (SceLibMp4Recorder)

- **Objetivo:** biblioteca de alto nível para gravar gameplay em MP4 (H.264 + AAC) em `ux0:video`
  (`SCE_SYSMODULE_MP4_RECORDER`, `psp2/mp4rec.h`). Internamente usa SceVideoenc (H.264) e SceAudioenc (AAC) e uma
  thread própria (`affinity`/`priority` no init). Vita3K: todas `UNIMPLEMENTED`.
- **Constantes (`psp2/mp4rec.h`):** timescale comum 240000; vídeo 30000 Hz com amostra de 1001 (29,97 fps); áudio
  48000 Hz, amostra de 1024; `SCE_MP4REC_AUDIO_BUFFER_SIZE = 4096` bytes (1024 amostras × 2 canais × S16).
  Modos: 0 640×368 2 Mbps, 1 640×368 1 Mbps, 2 480×272 2 Mbps, 3 480×272 1 Mbps, 4 368×208 2 Mbps,
  5 368×208 1 Mbps. Formatos de pixel: `0x00` A8B8G8R8, `0x20` YUV420 packed.
- **Estado interno:** por recorder (chave = endereço do `SceMp4RecRecorder` ou `base`): modo, `initialized`, contador
  de amostras de vídeo/áudio, e (opcional) `AVFormatContext` de saída.
- **Estratégia recomendada — stub funcional em duas fases:**
  1. Fase 1 (suficiente para qualquer jogo): validar argumentos, aceitar amostras, contar, e em `Term` com
     `discard == 0` não gerar arquivo (log `WarnOnce("mp4rec", ...)`). O jogo nunca lê o vídeo de volta.
  2. Fase 2 (opcional): gerar MP4 real com FFmpeg: encoder H.264 (ver disponibilidade em SceVideoenc — a
     confirmar) ou, se ausente, `AV_CODEC_ID_MPEG4` (nativo LGPL), AAC nativo do FFmpeg (`AV_CODEC_ID_AAC`), muxer
     `mp4` do avformat; arquivo em `Vfs` → `ux0:video/<titleid>/...` (nome/padrão de pasta do sistema a confirmar).
- **Arquivo novo:** `src/VitaSharp.Core/Hle/Modules/Mp4Recorder.cs` (`sealed class Mp4Recorder : HleModule`).
  Não confundir com `Hle/Modules/Mp4.cs` (SceMp4, demuxer/leitura).
- **Dependências:** `GuestMemory`, `YuvConvert` (Csc), `Emu.Vfs` (fase 2), `FFmpegLoader` (fase 2).
- **Códigos de erro:** nenhum documentado em header ou Vita3K — **a confirmar** (desmontar `libmp4recorder`
  (`vs0:sys/external/libmp4_recorder.suprx`, nome exato a confirmar) e procurar constantes `0x80xxxxxx`). Até lá,
  usar `SceError.INVALID_ARGUMENT` (0x80020005) só para ponteiros nulos e retornar 0 no resto.
- **Implementado (fase 1, `Hle/Modules/Mp4Recorder.cs`):** recorders por endereço do `SceMp4RecRecorder`;
  `Init` exige recorder criado e `mode` 0..5; `Add*Sample` contam e descartam (aceitos mesmo antes do `Init`, com
  `WarnOnce`); `Term` só loga (nenhum arquivo); `size != 4096` no áudio só gera log; erros = `INVALID_ARGUMENT`
  (0x80020005) para ponteiro nulo/recorder desconhecido/modo inválido. `sceMp4RecCsc` é real: A8B8G8R8 → YUV420
  packed (plano Y com `stride`, depois croma intercalada **V antes de U** no offset `stride × height`, como o
  `PIXEL_YUV420_PACKED_RASTER` do `Videodec.cs`), BT.601 faixa limitada, escala bilinear; mesmo formato e tamanho →
  cópia; outras combinações → `INVALID_ARGUMENT`. A ordem U/V para o encoder real continua a confirmar.

**Estruturas (`psp2/mp4rec.h`):**

| Struct | Offset | Campo |
| --- | --- | --- |
| SceMp4RecRecorder (0x10) | 0x00 / 0x04 / 0x08 / 0x0C | size / base / base_size / unk |
| SceMp4RecInitParam (0x20) | 0x00 / 0x04 / 0x08 / 0x0C / 0x10 / 0x14 / 0x18 / 0x1C | size / mode / encoder_mem / encoder_size / av_mem / av_size / affinity / priority |
| SceMp4RecFrame (0x2C) | 0x00 / 0x04 / 0x08 / 0x0C / 0x10 / 0x14 / 0x24 / 0x28 | size / pixelformat / stride (px) / width / height / unk[16] / buffer / reserved |
| SceMp4RecTermParam (0x20C) | 0x00 / 0x04 / 0x08 / 0x208 | size / discard (SceBool) / reserved[128] / metadata |

### sceMp4RecCreateRecorder
- [x] **Prioridade:** P2 · **Biblioteca:** SceLibMp4Recorder · **NID:** 0x21053FDE
- **Protótipo:** `int sceMp4RecCreateRecorder(SceMp4RecRecorder *rec);` (`psp2/mp4rec.h`)
- **Comportamento:** cria o recorder usando o memblock `rec->base`/`base_size` fornecido pelo jogo (tamanho
  necessário possivelmente obtido de `sceMp4RecRecorderInit`/documentação — a confirmar).
- **Retorno/erros:** 0; `rec` nulo ou `size != 0x10` → erro a confirmar.
- **Implementação no VitaSharp:** `_recorders[rec] = new RecState()`.
- **Validação:** homebrew de gravação (ex.: projetos open-source que usam `mp4rec.h` — buscar no GitHub) deve
  completar Create→Init→Add*→Term→Delete sem erro.

### sceMp4RecDeleteRecorder
- [x] **Prioridade:** P2 · **Biblioteca:** SceLibMp4Recorder · **NID:** 0x5EBA2AE2
- **Protótipo:** `int sceMp4RecDeleteRecorder(SceMp4RecRecorder *rec);` (`psp2/mp4rec.h`)
- **Comportamento:** destrói o recorder (o jogo libera o memblock depois).
- **Retorno/erros:** 0; desconhecido → erro a confirmar.
- **Implementação no VitaSharp:** remover estado; fechar arquivo se ainda aberto (discard).
- **Validação:** idem.

### sceMp4RecQueryPhysicalMemSize
- [x] **Prioridade:** P2 · **Biblioteca:** SceLibMp4Recorder · **NID:** 0x9A48028F
- **Protótipo:** `int sceMp4RecQueryPhysicalMemSize(SceMp4RecRecorder *rec, int mode, uint32_t *encoder_size, uint32_t *av_size);` (`psp2/mp4rec.h`)
- **Comportamento:** devolve os tamanhos dos blocos fisicamente contíguos para o encoder de vídeo e de áudio do
  `mode`.
- **Retorno/erros:** 0; `mode` fora de 0..5 → erro a confirmar; ponteiros nulos → erro a confirmar.
- **Implementação no VitaSharp:** valores reais a confirmar (medir num Vita); como o HLE não usa essa memória,
  escrever valores plausíveis alinhados a 256 KiB (ex. vídeo `align(w*h*3/2*4, 0x40000)`, áudio 0x40000) — o jogo
  aloca com `sceKernelAllocMemBlock` tipo PHYCONT, que precisa de tamanho múltiplo de 256 KiB/1 MiB (checar regra
  de alinhamento do tipo de memblock usado).
- **Validação:** alocação do jogo bem-sucedida.

### sceMp4RecInit
- [x] **Prioridade:** P2 · **Biblioteca:** SceLibMp4Recorder · **NID:** 0xE9193B80
- **Protótipo:** `int sceMp4RecInit(SceMp4RecRecorder *rec, SceMp4RecInitParam *params);` (`psp2/mp4rec.h`)
- **Comportamento:** inicia uma sessão de gravação no modo pedido, cria a thread do recorder e o arquivo de saída.
- **Retorno/erros:** 0; `params` inválido / modo inválido → a confirmar.
- **Estruturas:** `SceMp4RecInitParam` (tabela acima).
- **Implementação no VitaSharp:** guardar modo (w/h/bitrate da tabela); fase 2: abrir `AVFormatContext` + encoders.
  Não criar thread guest (tudo host-side, síncrono no `Add*`).
- **Validação:** idem.

### sceMp4RecTerm
- [x] **Prioridade:** P2 · **Biblioteca:** SceLibMp4Recorder · **NID:** 0xB13DAFF3
- **Protótipo:** `int sceMp4RecTerm(SceMp4RecRecorder *rec, SceMp4RecTermParam *params);` (`psp2/mp4rec.h`)
- **Comportamento:** encerra a sessão; se `discard == 0`, finaliza e salva em `ux0:video` com `metadata`; se
  `discard != 0`, apaga.
- **Retorno/erros:** 0; sessão não iniciada → a confirmar.
- **Estruturas:** `SceMp4RecTermParam` (0x20C); layout de `metadata` desconhecido (a confirmar).
- **Implementação no VitaSharp:** fase 1: `WarnOnce` "gravação de vídeo não suportada, arquivo descartado"; fase 2:
  flush dos encoders, `av_write_trailer`, mover para `ux0:video`.
- **Validação:** idem; fase 2: arquivo abre no PC.

### sceMp4RecCsc
- [x] **Prioridade:** P2 · **Biblioteca:** SceLibMp4Recorder · **NID:** 0x7B3184E4
- **Protótipo:** `int sceMp4RecCsc(SceMp4RecFrame *dst, SceMp4RecFrame *src);` (`psp2/mp4rec.h`)
- **Comportamento:** conversão de cor + reescala por hardware: tipicamente framebuffer A8B8G8R8 960×544 →
  YUV420 packed na resolução do modo (ex. 640×368), pronto para `AddVideoSample`.
- **Retorno/erros:** 0; formato inválido → a confirmar.
- **Estruturas:** `SceMp4RecFrame` (0x2C).
- **Implementação no VitaSharp:** **implementar de verdade** (barato e o jogo pode usar a saída): escala bilinear +
  `YuvConvert.RgbaToYuv420`. "YUV420 packed" = NV12 (Y plano + UV intercalado)? a confirmar — no `Videodec.cs`
  `PIXEL_YUV420_PACKED_RASTER = 0x20` já é tratado; reutilizar a mesma interpretação de layout.
- **Validação:** teste unitário com framebuffer sintético; comparar com saída de `sceAvcdec` no mesmo formato.

### sceMp4RecAddVideoSample
- [x] **Prioridade:** P2 · **Biblioteca:** SceLibMp4Recorder · **NID:** 0xB263D8FB
- **Protótipo:** `int sceMp4RecAddVideoSample(SceMp4RecRecorder *rec, void *buffer, int size);` (`psp2/mp4rec.h`)
- **Comportamento:** enfileira um quadro (YUV420 packed da resolução do modo) para codificação; duração 1001/30000 s.
- **Retorno/erros:** 0; sessão não iniciada/tamanho errado → a confirmar.
- **Implementação no VitaSharp:** fase 1: contar e retornar 0; fase 2: enviar ao encoder H.264/MPEG-4.
- **Validação:** idem.

### sceMp4RecAddAudioSample
- [x] **Prioridade:** P2 · **Biblioteca:** SceLibMp4Recorder · **NID:** 0x2C39D932
- **Protótipo:** `int sceMp4RecAddAudioSample(SceMp4RecRecorder *rec, void *buffer, int size);` (`psp2/mp4rec.h`)
- **Comportamento:** enfileira 4096 bytes de PCM S16 estéreo 48 kHz (1024 amostras) para AAC.
- **Retorno/erros:** 0; `size != 4096` → erro (código a confirmar).
- **Implementação no VitaSharp:** fase 1: validar `size == 4096` (só log se diferente, até confirmar o código) e
  retornar 0; fase 2: encoder AAC do FFmpeg.
- **Validação:** idem.

### sceMp4RecRecorderInit
- [x] **Prioridade:** P2 · **Biblioteca:** SceLibMp4Recorder · **NID:** 0x4045786D
- **Protótipo (inferido — ausente do header):** `int sceMp4RecRecorderInit(SceMp4RecRecorder *rec, ...);` —
  argumentos a confirmar. Hipótese: inicialização da biblioteca/memblock do recorder (paralela a
  `CreateRecorder`), ou versão antiga da API.
- **Comportamento:** a confirmar (desmontar o módulo e um usuário do export).
- **Retorno/erros:** a confirmar.
- **Implementação no VitaSharp:** `WarnOnce` com r0–r3 e retornar 0.
- **Validação:** quando houver importador, verificar se o fluxo seguinte (Create/Init) funciona.

### sceMp4RecRecorderEnd
- [x] **Prioridade:** P2 · **Biblioteca:** SceLibMp4Recorder · **NID:** 0xB51FEBAF
- **Protótipo (inferido — ausente do header):** `int sceMp4RecRecorderEnd(SceMp4RecRecorder *rec);` — a confirmar.
- **Comportamento:** par de `sceMp4RecRecorderInit` (a confirmar).
- **Retorno/erros:** a confirmar.
- **Implementação no VitaSharp:** `WarnOnce` e retornar 0.
- **Validação:** idem.

## SceIme (6 funções)

### Arquitetura (SceIme)

- **Objetivo:** teclado de sistema "embutido" (sem diálogo modal). Diferente do `sceImeDialog*` (SceCommonDialog, ver
  [D-dialogs-app.md](D-dialogs-app.md)), aqui o jogo desenha a própria caixa de texto, o sistema mostra o teclado na
  parte de baixo da tela e entrega o texto por **eventos** a um callback do jogo, despachados dentro de `sceImeUpdate()`
  (chamado uma vez por frame pelo jogo, na thread do jogo). Sysmodule `SCE_SYSMODULE_IME = 0x0011` (`libime`); o
  `libime` é só um stub cliente do IME do SceShell, então LLE do firmware não é opção.
- **Estado interno** (uma instância por processo; só pode haver um IME aberto):
  - `bool Open`; cópia do `SceImeParam` (handler, arg, filter, `inputTextBuffer`, `maxTextLength`, `type`, `option`,
    `enterLabel`);
  - texto atual (`List<char>`/`StringBuilder` UTF-16), `caretIndex`, pré-edição (`preeditIndex/Length`, sempre 0 no
    HLE — não há composição de kana/hangul);
  - fila de eventos pendentes (`Queue<SceImeEventData>`), produzida pelo front-end (thread da UI) e consumida em
    `sceImeUpdate` → precisa de `lock`;
  - endereço de scratch guest (`Mem.Alloc(0x1000, "ime")`) para o `SceImeEventData` (0x2C) e para os buffers do
    `filter`.
- **Entrada do host:** hoje `HostServices` (`src/VitaSharp.Core/Host/HostServices.cs`) só tem `Input` (controle/toque)
  e `Audio`. Criar `ITextInputBackend` opcional:
  ```csharp
  public interface ITextInputBackend
  {
      // abre o teclado do host com texto inicial; o back-end chama os callbacks na thread da UI
      void Begin(string initial, int maxLength, int type, bool multiline,
                 Action<string, int> onTextChanged /* texto, caret */, Action onEnter, Action onClose);
      void End();
  }
  ```
  e `HostServices.TextInput`. Desktop (Avalonia): um `TextBox` sobreposto ou captura do teclado físico. Headless/cenários:
  um back-end roteirizado que lê o texto de uma variável de ambiente/arquivo do cenário e gera `UPDATE_TEXT` +
  `PRESS_ENTER` após N frames.
  Sem back-end: `sceImeOpen` funciona, entrega só `OPEN` e nunca mais nada (o jogo fica esperando entrada, mas não
  trava).
  **Implementado:** `ITextInputBackend` (com `SetText`) e `HostServices.TextInput` existem (nenhum front-end o fornece
  ainda). Sem back-end, se `--ime-text` estiver definido (`CommonDialog.ImeDefaultText`, a mesma fonte do
  `sceImeDialog`), o `Ime` entrega `OPEN → UPDATE_TEXT(texto) → PRESS_ENTER` nas próximas `sceImeUpdate`. Callbacks
  vindos do back-end de uma sessão já fechada são descartados (contador de sessão).
- **Arquivo novo:** `src/VitaSharp.Core/Hle/Modules/Ime.cs`, `public sealed class Ime : HleModule`. Todos os seis
  nomes estão em `Hle/Data/nids.txt` (não precisa `Nid =`).
- **Chamada do handler:** `GuestThread.Current!.CallGuest(handler, arg, eventAddr)` (mesmo padrão de
  `AvPlayer.cs:58`). O handler tem retorno `void`. O `filter` é `SceInt32 filter(SceWChar16 *outText,
  SceUInt32 *outTextLength, const SceWChar16 *srcText, SceUInt32 srcTextLength)`.
- **Dependências:** `GuestMemory` (R/W UTF-16), `GuestThread.CallGuest`, novo `ITextInputBackend`; front-end Desktop.
  Interação com `sceCtrl`: no hardware, enquanto o teclado está aberto os botões continuam chegando ao jogo (o IME usa
  toque); não bloquear o Ctrl.

**Códigos de erro** (`psp2/libime.h`, enum `SceImeErrorCode`):

| Nome | Valor |
| --- | --- |
| SCE_IME_ERROR_ALREADY_OPENED | 0x80100700 |
| SCE_IME_ERROR_NOT_OPENED | 0x80100701 |
| SCE_IME_ERROR_INVALID_POINTER | 0x80100702 |
| SCE_IME_ERROR_INVALID_PARAM | 0x80100703 |
| SCE_IME_ERROR_NO_MEMORY | 0x80100704 |
| SCE_IME_ERROR_CONNECTION_FAILED | 0x80100705 |
| SCE_IME_ERROR_INVALID_TEXT | 0x80100706 |
| SCE_IME_ERROR_TOO_MANY_REQUESTS | 0x80100707 |
| SCE_IME_ERROR_INVALID_SIZE | 0x80100710 |
| SCE_IME_ERROR_INVALID_INPUT_METHOD | 0x80100712 |
| SCE_IME_ERROR_INVALID_SUPPORTED_LANGUAGES | 0x80100713 |
| SCE_IME_ERROR_INVALID_TYPE | 0x80100714 |
| SCE_IME_ERROR_INVALID_OPTION | 0x80100715 |
| SCE_IME_ERROR_INVALID_WORK | 0x80100716 |
| SCE_IME_ERROR_INVALID_ARG | 0x80100717 |
| SCE_IME_ERROR_INVALID_HANDLER | 0x80100718 |
| SCE_IME_ERROR_INVALID_MAX_TEXT_LENGTH | 0x80100719 |
| SCE_IME_ERROR_INVALID_INPUT_TEXT_BUFFER | 0x8010071A |
| SCE_IME_ERROR_INVALID_RESERVED | 0x8010071B |
| SCE_IME_ERROR_INVALID_ENTER_LABEL | 0x8010071C |
| SCE_IME_ERROR_INTERNAL | 0x80100750 |

Constantes: `SCE_IME_WORK_BUFFER_SIZE = 20*1024`, `SCE_IME_MAX_PREEDIT_LENGTH = 30`,
`SCE_IME_MAX_TEXT_LENGTH = 2048`. Eventos (`SceImeEvent`): `OPEN=0`, `UPDATE_TEXT=1`, `UPDATE_CARET=2`,
`CHANGE_SIZE=3`, `PRESS_CLOSE=4`, `PRESS_ENTER=5`. Tipos (`SceImeType`): `DEFAULT=0`, `BASIC_LATIN=1`, `NUMBER=2`,
`EXTENDED_NUMBER=3`, `URL=4`, `MAIL=5`. Opções: `MULTILINE=0x01`, `NO_AUTO_CAPITALIZATION=0x02`,
`NO_ASSISTANCE=0x04`. Enter label: `DEFAULT=0`, `SEND=1`, `SEARCH=2`, `GO=3`.

**Estruturas** (`psp2/libime.h`, tamanhos com `VITASDK_BUILD_ASSERT_EQ`):

`SceImeParam` (0x40):

| Offset | Campo | Tipo |
| --- | --- | --- |
| 0x00 | sdkVersion | u32 |
| 0x04 | inputMethod | u32 |
| 0x08 | supportedLanguages | u64 (bitmask `SceImeLanguage`) |
| 0x10 | languagesForced | SceBool |
| 0x14 | type | u32 (`SceImeType`) |
| 0x18 | option | u32 (`SceImeOption`) |
| 0x1C | work | void* (20 KiB do jogo) |
| 0x20 | arg | void* (repassado ao handler) |
| 0x24 | handler | `SceImeEventHandler` |
| 0x28 | filter | `SceImeTextFilter` (pode ser NULL) |
| 0x2C | initialText | SceWChar16* (pode ser NULL) |
| 0x30 | maxTextLength | u32 |
| 0x34 | inputTextBuffer | SceWChar16* (≥ maxTextLength+1 wchar) |
| 0x38 | enterLabel | u8 |
| 0x39 | reserved[7] | u8 (deve ser zero) |

`SceImeEventData` (0x2C): `0x00 id` (u32) + `0x04 param` (`SceImeEventParam`, union de 0x28):
- `SceImeRect` (0x10): `x, y, width, height` — usado por `OPEN` e `CHANGE_SIZE` (retângulo do teclado na tela 960x544).
- `SceImeEditText` (0x18): `0x00 preeditIndex`, `0x04 preeditLength`, `0x08 caretIndex`, `0x0C str` (SceWChar16*,
  aponta para o `inputTextBuffer` do jogo), `0x10 editIndex`, `0x14 editLengthChange` (s32) — usado por `UPDATE_TEXT`.
- `caretIndex` (u32, offset 0) — usado por `UPDATE_CARET`.

`SceImeCaret` (0x10): `x, y, height, index`. `SceImePreeditGeometry` (0xC): `x, y, height`.

### sceImeOpen
- [x] **Prioridade:** P2 · **Biblioteca:** SceIme · **NID:** 0x0E050613
- **Protótipo:** `SceInt32 sceImeOpen(const SceImeParam *param);` (`psp2/libime.h`)
- **Comportamento:** valida o parâmetro, guarda a configuração, copia `initialText` (se houver) para o estado interno e
  para `inputTextBuffer`, posiciona o caret no fim do texto e pede ao sistema para mostrar o teclado. O evento `OPEN`
  (com `param.rect` = retângulo do teclado) é entregue na próxima `sceImeUpdate`, não dentro do `Open`. O `work` de
  20 KiB é memória do jogo que o IME usa como contexto; o HLE não precisa escrever nele.
- **Retorno/erros:** `0`. `ALREADY_OPENED` se já aberto; `INVALID_POINTER` se `param == NULL`; `INVALID_HANDLER` se
  `handler == 0`; `INVALID_WORK` se `work == 0` (alinhamento exigido a confirmar); `INVALID_INPUT_TEXT_BUFFER` se
  `inputTextBuffer == 0`; `INVALID_MAX_TEXT_LENGTH` se `maxTextLength == 0` ou `> 2048`; `INVALID_TYPE` se
  `type > 5`; `INVALID_OPTION` se `option & ~0x7`; `INVALID_ENTER_LABEL` se `enterLabel > 3`; `INVALID_RESERVED` se
  algum `reserved[i] != 0`; `INVALID_SUPPORTED_LANGUAGES` se o bitmask tiver bits acima de `0x80000`;
  `INVALID_TEXT` se `initialText` for mais longo que `maxTextLength`. A **ordem** das validações e se
  `inputMethod != 0` gera `INVALID_INPUT_METHOD` são **a confirmar** (homebrew de teste chamando com um campo inválido
  por vez, no hardware).
- **Estruturas:** `SceImeParam` acima.
- **Implementação no VitaSharp:** `Ime.cs`. Ler os campos por offset com `GuestMemory.R32/R8`; ler `initialText` como
  UTF-16 terminado em 0 (até `maxTextLength`). Escrever o texto inicial + `\0` em `inputTextBuffer`. Enfileirar
  `OPEN` com rect `{0, 544 - h, 960, h}` — altura do teclado real **a confirmar** (captura de tela de um jogo com IME;
  usar 272 provisoriamente e registrar `Log.WarnOnce`). Chamar `HostServices.TextInput?.Begin(...)`; os callbacks
  enfileiram `UPDATE_TEXT`/`UPDATE_CARET`/`PRESS_ENTER`/`PRESS_CLOSE`. Não aloque `inputTextBuffer` próprio (o
  Vita3K faz isso, mas o texto tem de chegar ao buffer do jogo).
- **Validação:** homebrew de exemplo do VitaSDK (`samples/ime`, se existir; senão um ELF mínimo que abre o IME e
  imprime os eventos); teste unitário do `Ime` com back-end roteirizado verificando a sequência `OPEN → UPDATE_TEXT →
  PRESS_ENTER`.

### sceImeUpdate
- [x] **Prioridade:** P2 · **Biblioteca:** SceIme · **NID:** 0x71D6898A
- **Protótipo:** `SceInt32 sceImeUpdate(void);` (`psp2/libime.h`)
- **Comportamento:** despacha, na thread chamadora, todos os eventos pendentes para `handler(arg, &event)`, na ordem
  em que ocorreram. Antes de `UPDATE_TEXT`, o texto novo já está em `inputTextBuffer` (terminado em 0) e o evento
  traz `str = inputTextBuffer`, `caretIndex`, `editIndex` (posição onde mudou) e `editLengthChange` (wchar
  inseridos > 0 / removidos < 0). Se `filter != 0`, o texto passa pelo filtro antes de ir ao buffer.
- **Retorno/erros:** `0`; `NOT_OPENED` se não houver IME aberto.
- **Estruturas:** `SceImeEventData` (0x2C).
- **Implementação no VitaSharp:** drenar a fila sob `lock`, montar cada evento no scratch guest (zerar 0x2C bytes,
  `id` em +0, union em +4) e chamar `CallGuest(handler, arg, scratch)`. O handler pode chamar `sceImeClose`
  (comum após `PRESS_ENTER`/`PRESS_CLOSE`): checar `Open` depois de cada evento e parar de despachar se fechou.
  Filtro: copiar o texto para `scratch+0x100` (srcText), `outText` em `scratch+0x100+2*(max+1)`, `outTextLength` em
  outro u32 do scratch (o scratch precisa de `≥ 0x100 + 4*(2048+1)` bytes, então aloque 0x2400); retorno `< 0`
  descarta a edição (semântica exata a confirmar). Registrar o custo em `HleFunction.ClassifyCost` como chamada leve.
- **Validação:** mesmo cenário de `sceImeOpen`; conferir que `editLengthChange` bate com o diff do texto.

### sceImeSetText
- [x] **Prioridade:** P2 · **Biblioteca:** SceIme · **NID:** 0xF3BD9A76
- **Protótipo:** `SceInt32 sceImeSetText(const SceWChar16 *text, SceUInt32 length);` (`psp2/libime.h`)
- **Comportamento:** o jogo substitui o texto do IME (ex.: botão "limpar"). O IME atualiza o estado interno e o
  `inputTextBuffer`, e coloca o caret no fim. Se gera um `UPDATE_TEXT` de volta é a confirmar (provável que não, já que
  a mudança veio do próprio jogo). O Vita3K não implementa.
- **Retorno/erros:** `0`; `NOT_OPENED`; `INVALID_POINTER` se `text == NULL` com `length > 0`; `INVALID_TEXT` (ou
  `INVALID_SIZE`, a confirmar) se `length > maxTextLength`.
- **Implementação no VitaSharp:** ler `length` wchar de `text`, gravar no estado e no buffer + `\0`, repassar ao
  back-end (`ITextInputBackend` precisa de `SetText(string, int caret)`).
- **Validação:** teste unitário; homebrew que chama `SetText` e depois lê `inputTextBuffer`.

### sceImeSetCaret
- [x] **Prioridade:** P2 · **Biblioteca:** SceIme · **NID:** 0xD8342D2A
- **Protótipo:** `SceInt32 sceImeSetCaret(const SceImeCaret *caret);` (`psp2/libime.h`)
- **Comportamento:** o jogo informa onde desenhou o caret (`x, y, height` em pixels de tela, para o sistema posicionar
  a lista de sugestões) e o índice lógico (`index`) no texto. O IME move o caret interno para `index` (limitado ao
  tamanho do texto). O Vita3K dispara o handler com `caretIndex` dentro de `SetCaret`, o que provavelmente está errado
  (callback fora de `sceImeUpdate`); no HLE, só atualize o estado e, se o índice mudou, enfileire `UPDATE_CARET`
  (a confirmar se o hardware ecoa).
- **Retorno/erros:** `0`; `NOT_OPENED`; `INVALID_POINTER` se `caret == NULL`; `INVALID_ARG` se `index > len` (a
  confirmar).
- **Estruturas:** `SceImeCaret` (0x10: x@0, y@4, height@8, index@0xC).
- **Implementação no VitaSharp:** `Ime.cs`; repassar posição ao back-end (opcional).
- **Validação:** teste unitário.

### sceImeSetPreeditGeometry
- [x] **Prioridade:** P2 · **Biblioteca:** SceIme · **NID:** 0x7B1EFAA5
- **Protótipo:** `SceInt32 sceImeSetPreeditGeometry(const SceImePreeditGeometry *preedit);` (`psp2/libime.h`)
- **Comportamento:** posição (`x, y, height`) onde o jogo desenha o texto em pré-edição; o sistema usa para a janela de
  conversão (japonês/chinês). Sem efeito visível no HLE porque não há pré-edição.
- **Retorno/erros:** `0`; `NOT_OPENED`; `INVALID_POINTER` se `preedit == NULL`.
- **Estruturas:** `SceImePreeditGeometry` (0xC).
- **Implementação no VitaSharp:** guardar e ignorar. Não chamar o handler (o Vita3K chama, sem `id`; não reproduzir).
- **Validação:** retorno 0 com IME aberto, `0x80100701` fechado.

### sceImeClose
- [x] **Prioridade:** P2 · **Biblioteca:** SceIme · **NID:** 0x889A8421
- **Protótipo:** `SceInt32 sceImeClose(void);` (`psp2/libime.h`)
- **Comportamento:** esconde o teclado e libera a sessão. Eventos ainda na fila são descartados. Depois disso o jogo
  pode reaproveitar `work`/`inputTextBuffer`.
- **Retorno/erros:** `0`; `NOT_OPENED` se não estiver aberto.
- **Implementação no VitaSharp:** `Open = false`, limpar fila, `HostServices.TextInput?.End()`. Seguro de chamar de
  dentro do handler.
- **Validação:** `Open → Close → Open` deve dar `0` nas três; `Close` duplo dá `0x80100701`.

## SceHandwriting (11 funções)

### Arquitetura (SceHandwriting)

- **Objetivo:** reconhecimento de escrita à mão (traços no touch → caracteres), usado por jogos de quiz/kanji e
  aplicativos de desenho. Sysmodule `SCE_SYSMODULE_HANDWRITING = 0x002F` (`libhandwriting`, segundo o
  `load_module.cpp` do Vita3K). Módulo NID `0x18983E63`, biblioteca NID `0x2AA55635` (`db/360/SceHandwriting.yml`).
- **Fontes:** **não há header** no vita-headers (só o `.yml` com os NIDs), o Vita3K só tem stubs `UNIMPLEMENTED()` sem
  parâmetros, e não há documentação pública. **Todos os protótipos abaixo são inferidos pelo nome** e marcados como
  tal. Nenhum jogo instalado importa.
- **Como confirmar os protótipos (pré-requisito para qualquer implementação além de stub):**
  1. Extrair `vs0:sys/external/libhandwriting.suprx` de um firmware 3.60+ (PUP descriptografado / dump do console).
  2. Desmontar as exportações da biblioteca `SceHandwriting` (Ghidra + vita-loader / `vita-elf-inject` /
     `prx-tool`) e mapear cada NID para o código; contar argumentos pelo uso de r0–r3/pilha e achar os códigos de erro
     (`0x80xxxxxx`) por constantes `movw/movt`.
  3. Procurar um jogo que importe a lib (`VitaSharp.Desktop imports <TITLEID>`) para ver o padrão de chamada.
- **Alternativa recomendada:** **LLE do módulo do firmware.** O reconhecedor é algoritmo puro em ARM (dicionário de
  traços embutido no `.suprx` ou em arquivo de dados ao lado — a confirmar), sem hardware dedicado; carregar o
  `libhandwriting.suprx` com `Emu.LoadModule(...)` quando o jogo chamar `sceSysmoduleLoadModule(0x2F)` (hoje
  `SystemMisc.cs` só marca o ID como carregado) resolve as 11 funções sem engenharia reversa. Requer o mecanismo genérico
  "sysmodule → LLE quando o arquivo existir em `FsRoot/vs0/sys/external/`", o mesmo que o Vita3K usa para
  `libpgf`/`libsas`/`libatrac` (ver seções correspondentes).
- **HLE mínimo (se LLE não for viável):** `Hle/Modules/Handwriting.cs` com stubs que devolvem sucesso nas funções de
  ciclo de vida e "nenhum candidato" em `Recognize`, para o jogo seguir sem a funcionalidade. Valores de retorno de
  "nenhum resultado" e códigos de erro: **a confirmar** pelo passo 2.
- **Estado interno (HLE mínimo):** `bool Initialized`; conjunto de handles de "Register" (o par `RegisterInit/Set/
  GetResult/Term` sugere um contexto de reconhecimento incremental, traço por traço; a confirmar).
- **Dependências:** nenhuma no HLE mínimo; no LLE, o carregador de módulos e as importações do próprio
  `libhandwriting` (provavelmente SceLibKernel/SceSysmem/libc — a confirmar).
- **Códigos de erro:** desconhecidos (nenhuma fonte pública). A confirmar pelo disassembly.

### Funções (protótipos inferidos)

Todas: **Prioridade:** P2 · **Biblioteca:** SceHandwriting · header: nenhum (inferido).

| Checkbox | Função | NID | Protótipo inferido | Comportamento inferido | HLE mínimo |
| --- | --- | --- | --- | --- | --- |
| [x] | sceHandwritingGetBufferSize | 0x4C3A8D49 | `SceSize sceHandwritingGetBufferSize(const void *param);` (inferido; pode ser `(void)` ou receber modo/idioma) | Tamanho do buffer de trabalho que o jogo deve alocar e passar a `Init`. | Retornar um tamanho fixo generoso (ex. 0x40000, valor real a confirmar). |
| [x] | sceHandwritingInit | 0x446F171E | `SceInt32 sceHandwritingInit(void *buffer, SceSize size, const void *param);` (inferido) | Inicializa o reconhecedor sobre o buffer do jogo; carrega dicionário. | `Initialized = true`, retorna 0. |
| [x] | sceHandwritingTerm | 0x356B2931 | `SceInt32 sceHandwritingTerm(void);` (inferido) | Encerra o reconhecedor. | `Initialized = false`, retorna 0. |
| [x] | sceHandwritingSetMode | 0x09AF9BF8 | `SceInt32 sceHandwritingSetMode(SceUInt32 mode);` (inferido) | Seleciona conjunto de caracteres (kana, kanji, latino, números…). | Guardar, retornar 0. |
| [x] | sceHandwritingRecognize | 0x12E4F99A | `SceInt32 sceHandwritingRecognize(const void *strokes, SceUInt32 numStrokes, void *result, SceUInt32 maxCandidates);` (inferido) | Reconhece um conjunto completo de traços e devolve candidatos (provavelmente códigos UTF-16 + pontuação). | Escrever 0 candidatos no resultado (layout a confirmar) e retornar 0; se o layout não for conhecido, não escrever e retornar 0. |
| [x] | sceHandwritingRegisterInit | 0x76395D93 | `SceInt32 sceHandwritingRegisterInit(void *ctx, ...);` (inferido) | Abre um contexto de "registro" (reconhecimento incremental ou registro de caracteres do usuário — a confirmar). | Retornar 0. |
| [x] | sceHandwritingRegisterSet | 0x79CA5D3F | `SceInt32 sceHandwritingRegisterSet(void *ctx, const void *strokes, ...);` (inferido) | Adiciona traços/amostra ao contexto. | Retornar 0. |
| [x] | sceHandwritingRegisterGetResult | 0xB886450E | `SceInt32 sceHandwritingRegisterGetResult(void *ctx, void *result);` (inferido) | Lê o resultado do contexto. | 0 candidatos, retornar 0. |
| [x] | sceHandwritingRegisterInfo | 0x69324689 | `SceInt32 sceHandwritingRegisterInfo(void *ctx, void *info);` (inferido) | Consulta informações do contexto (contagem, capacidade). | Zerar `info` só se o tamanho for conhecido; retornar 0. |
| [x] | sceHandwritingRegisterDelete | 0x6BA4A521 | `SceInt32 sceHandwritingRegisterDelete(void *ctx, SceUInt32 index);` (inferido) | Remove uma entrada registrada. | Retornar 0. |
| [x] | sceHandwritingRegisterTerm | 0x6ABBD675 | `SceInt32 sceHandwritingRegisterTerm(void *ctx);` (inferido) | Fecha o contexto de registro. | Retornar 0. |

- **Implementação no VitaSharp:** preferir LLE (acima). No HLE mínimo, usar `[Hle(Nid = 0x...)]` só se o nome não estiver
  em `Hle/Data/nids.txt` (os 11 estão lá), registrar `Log.WarnOnce("handwriting", ...)` na primeira chamada para deixar
  claro que o reconhecimento não existe, e **não** escrever em ponteiros cujo layout não foi confirmado.
  **Implementado (HLE mínimo, `Hle/Modules/Handwriting.cs`):** `GetBufferSize` → 0x40000; `Init/Term/SetMode`
  guardam estado e retornam 0; `Recognize` e todos os `Register*` retornam 0 sem escrever nada (argumentos r0–r3
  logados uma vez por função). O LLE continua sendo o caminho para reconhecimento real.
- **Validação:** só é possível com um jogo/homebrew que importe a lib. Critério mínimo: o jogo passa pela tela de
  escrita sem crash nem erro; critério completo (LLE): um caractere desenhado via toque roteirizado no cenário é
  reconhecido.

## SceVoice (28 funções)

### Arquitetura (SceVoice)

- **Objetivo:** chat de voz. O app cria *portas* de entrada (microfone, PCM do app, voz codificada recebida da rede)
  e de saída (PCM para o app, voz codificada para enviar pela rede, alto-falante/fone), liga entrada→saída e o
  módulo mistura, codifica (CELP 8 kHz) e decodifica em uma thread própria. Usado por jogos com chat (party/online);
  hoje nenhum jogo instalado importa.
- **Fonte:** `psp2/voice.h` do vita-headers (documentado contra FW 3.60, com comportamentos e limites detalhados).
  Vita3K (`modules/SceVoice/SceVoice.cpp`) só tem `UNIMPLEMENTED()` em todas as 28 funções: não serve de referência.
- **Módulo/biblioteca:** módulo `SceVoice` (NID 0xF5F3E7CA), biblioteca `SceVoice` (NID 0xCE56E214),
  `SCE_SYSMODULE_VOICE = 0x001A`. Todos os nomes estão no `nids.txt` (resolução por nome funciona).
- **Estratégia proposta (stub funcional):** implementar o grafo de portas inteiro (estado, validações, conexões,
  ring buffers, `GetPortInfo` coerente), mas **sem codec real e sem microfone**:
  - `IN_DEVICE` (mic): não há AudioIn no VitaSharp — `sceAudioInOpenPort` em `Audio.cs` devolve `0x80260106` e
    `sceAudioInInput` devolve `0x80260107`. Logo a porta de microfone produz **silêncio** (zeros) no ritmo de 16 kHz.
    Isso basta para jogos que só abrem o chat; quando AudioIn existir, a porta passa a ler dele.
  - `OUT_DEVICE` (alto-falante): abrir um `IAudioStream` via `Emu.Host.Audio?.OpenStream(16000, 1, grain)` (mesma
    interface usada por `Audio.cs`) e submeter a mistura das entradas conectadas. Pode começar descartando (silêncio)
    e só tocar o PCM de `IN_PCMAUDIO`; `IN_VOICE` não é decodificável (ver codec).
  - `OUT_VOICE` (enviar pela rede): gerar frames CELP **do tamanho correto** preenchidos com zeros (o conteúdo só seria
    ouvido por outro console; como online real não é objetivo, frames vazios mantêm o protocolo do jogo andando).
  - `IN_VOICE` (recebido da rede): aceitar e descartar frames; contribui silêncio para as saídas.
  - `IN_PCMAUDIO` → `OUT_PCMAUDIO`: mistura real (soma com volume, clamp s16) — é o único caminho 100% emulável.
- **Codec:** CELP MPEG-4 (ISO/IEC 14496-3, MPE, banda estreita 8 kHz). A tabela de bitrates do SDK (3850…12200) bate
  com a do CELP NB do MPEG-4. **FFmpeg não tem decoder nem encoder MPEG-4 CELP** (por isso `Audiodec.cs` rejeita
  `TYPE_CELP = 0x1006`). Sem codec, a estratégia acima (frames vazios / silêncio) é a única viável sem portar um
  codec. Ver também SceAudioencUser (mesmo codec).
- **Ritmo de processamento:** no hardware uma thread do SceVoice processa a cada 10 ms (a confirmar: período exato;
  confirmar medindo, em hardware, a taxa com que `data_size` de uma `OUT_PCMAUDIO` cresce). No VitaSharp, sem thread
  própria: processar "preguiçosamente" em cada chamada de `Read/Write/GetPortInfo`, calculando quantos frames de 10 ms
  passaram desde o último processamento pelo relógio do emulador (mesma ideia de pacing de `Audio.cs`). Não usar
  `System.Threading.Timer` (ordem não determinística).
- **Eventos:** se `SceVoiceInitParam.event_callback != 0`, o SceVoice real chama o callback numa thread de evento com
  `SCE_VOICE_EVENT_TYPE_PORT_DATA_READY` quando uma `OUT_VOICE` tem dados. Implementação inicial: **não disparar**
  eventos (jogos que fazem polling via `GetPortInfo/ReadFromOPort` funcionam). Se um jogo depender do callback, criar
  uma thread guest de serviço e usar `GuestThread.CallGuest(cb, eventAddr)` (padrão de `AvPlayer.cs:58`), com a struct
  `SceVoiceEvent` (0x18 bytes) alocada em memória guest.
- **Estado interno (classe `Voice : HleModule`):**
  - `bool _initialized, _started; uint _cbAddr, _userData; int _memBlockUid; bool _muteAll; bool _allPaused;`
  - `Port?[64] _ports` (ID = índice 0..63), cada `Port { uint Type; ushort Threshold; bool Mute; float Volume;
    uint BitRate; uint BufferSize; uint State; bool Paused; byte[] Ring; int Head, Count; IAudioStream? Stream; }`
  - `HashSet<(int inPort, int outPort)> _connections` (máx. 128).
  - Contadores por tipo para os limites de `GetResourceInfo` (7 IN_VOICE, 7 OUT_VOICE, 1 IN_DEVICE, 1 OUT_DEVICE,
    64 no total).
  - `lock (_ports)` em toda função (chamadas de várias threads).
- **Arquivo novo:** `src/VitaSharp.Core/Hle/Modules/Voice.cs` (`public sealed class Voice : HleModule`).
- **Dependências:** `Ptr<T>` (`Memory/Ptr.cs`), `K.Objects.Get<MemBlock>(uid)` (`Hle/Modules/SysMem.cs`) para validar
  `mem_block_id`, `IAudioBackend` (`Host/HostServices.cs`), `GuestMemory`. Argumento `float volume` chega em `s0`
  (binder AAPCS-VFP já trata `float`).

Constantes: `SCE_VOICE_VERSION = 100`, `SCE_VOICE_WORKING_MEMORY_SIZE = 0x40000` (FW 3.60 usa 0x28000 privados +
0x18000 compartilhados), `SCE_VOICE_MAX_EVENT_PORTS = 7`, `SCE_VOICE_PORT_ATTR_AUDIO_INPUT_OWNERSHIP = 1000`.

Tipos de porta: `IN_DEVICE=0, IN_PCMAUDIO=1, IN_VOICE=2, OUT_PCMAUDIO=3, OUT_VOICE=4, OUT_DEVICE=5`.
Estados: `INACTIVE=0, IDLE=1, BUFFERING=2, ACTIVE=3`.

Bitrates de porta VOICE (do header): 

| Bitrate | Bytes/frame | Amostras/frame (8 kHz) | Duração |
| --- | --- | --- | --- |
| 3850 | 20 | 320 | 40 ms |
| 4650 | 24 | 320 | 40 ms |
| 5700 | 15 | 160 | 20 ms |
| 7300 | 19 | 160 | 20 ms |
| 12200 | 16 | 80 | 10 ms |

Códigos de erro:

| Nome | Valor |
| --- | --- |
| SCE_VOICE_ERROR_NOT_INITIALIZED | 0x804E0801 |
| SCE_VOICE_ERROR_ALREADY_INITIALIZED | 0x804E0802 |
| SCE_VOICE_ERROR_INTERNAL | 0x804E0803 |
| SCE_VOICE_ERROR_INVALID_PORT_ID | 0x804E0804 |
| SCE_VOICE_ERROR_INVALID_ARGUMENT | 0x804E0805 |
| SCE_VOICE_ERROR_INVALID_MEMBLOCK | 0x804E0806 |
| SCE_VOICE_ERROR_INVALID_PORT | 0x804E0807 |
| SCE_VOICE_ERROR_RESOURCE_LIMIT | 0x804E0808 |
| SCE_VOICE_ERROR_NOT_STARTED | 0x804E080A |

Regra geral de validação de `port_id` (todas as funções que recebem porta): `port_id >= 64` →
`INVALID_PORT_ID`; slot vazio → `INVALID_PORT_ID` (o header diz isso explicitamente para `SetPortAttr`; para as
demais a distinção `INVALID_PORT_ID` vs `INVALID_PORT` é a confirmar — `INVALID_PORT` é usado quando a porta existe
mas o tipo não serve para a operação, ex.: `SetBitRate` numa porta PCM). Toda função exceto `Init` devolve
`NOT_INITIALIZED` antes de `sceVoiceInit`. Ponteiro de saída nulo → `INVALID_ARGUMENT`.

Validação comum de toda a biblioteca: não há jogo instalado que importe. Escrever um teste unitário em
`tests/VitaSharp.Tests` que instancia o módulo e chama os métodos diretamente (sequência Init→Start→CreatePort×2→
Connect→Write PCM→Read PCM→Stop→End), e conferir contra hardware com um homebrew (VitaSDK, `-lSceVoice_stub`) quando
houver dúvida "a confirmar".

**Implementado** (`Hle/Modules/Voice.cs`, testes em `tests/VitaSharp.Tests/MediaMiscHleTests.cs`). Escolhas
provisórias (a confirmar no hardware): o `Audio.cs` hoje tem um microfone virtual (SceAudioIn abre e entrega silêncio),
mas o Voice não depende dele — IN_DEVICE gera silêncio direto; `CheckTopology` devolve `INVALID_PORT` para as três
regras; `Connect` com tipos invertidos ou IN_VOICE→OUT_VOICE → `INVALID_PORT`, duplicata → `INVALID_ARGUMENT`;
`Disconnect` inexistente → `INVALID_ARGUMENT`; `Write/ReadFromOPort` sem `Start` → `NOT_STARTED` com `*size = 0`;
`frame_size` = 320 para portas PCM/device (10 ms a 16 kHz); estados após Start: entradas do app IDLE (BUFFERING/ACTIVE
conforme o threshold efetivo = max(threshold, 2 frames)), demais ACTIVE; OUT_PCMAUDIO/OUT_VOICE só produzem dados quando
alguma entrada conectada produziu no tick (o mic sempre produz); ring de saída cheio descarta o mais antigo; OUT_DEVICE
abre `IAudioStream(16000, 1, 160)` sob demanda e descarta ticks quando o host já tem ~100 ms na fila (nunca bloqueia);
eventos (`event_callback`) não são disparados (log `WarnOnce`); `GetPortAttr` com `attr != 1000` → `INVALID_ARGUMENT`.

### sceVoiceInit
- [x] **Prioridade:** P2 · **Biblioteca:** SceVoice · **NID:** 0x805CC20F
- **Protótipo:** `int sceVoiceInit(const SceVoiceInitParam *param, SceUInt32 version);` (`psp2/voice.h`)
- **Comportamento:** inicializa o SceVoice do processo, guarda callback de evento e `user_data`, cria a thread de
  eventos (no HLE: nada). Não aloca memória de trabalho (isso é em `sceVoiceStart`).
- **Retorno/erros:** 0; `ALREADY_INITIALIZED` (0x804E0802) se já inicializado; `INVALID_ARGUMENT` (0x804E0805) se
  `param == NULL` ou `version != 100` (a confirmar qual erro para versão errada); `application_type` fora de
  {0x20000000, 0x80000000} → a confirmar (testar no hardware).
- **Estruturas:** `SceVoiceInitParam` (0x20): +0x00 `application_type`, +0x04 `event_callback`, +0x08 `user_data`,
  +0x0C `reserved[5]` (ignorado).
- **Implementação no VitaSharp:** `Voice.cs`; `[Hle] int sceVoiceInit(uint param, uint version)`; lê os três campos e
  marca `_initialized`.
- **Validação:** teste unitário; segunda chamada deve devolver 0x804E0802.

### sceVoiceEnd
- [x] **Prioridade:** P2 · **Biblioteca:** SceVoice · **NID:** 0xAC98853E
- **Protótipo:** `int sceVoiceEnd(void);` (`psp2/voice.h`)
- **Comportamento:** se iniciado, para o processamento e desanexa o memblock (como `sceVoiceStop`); destrói todas as
  portas (fecha `IAudioStream`s) e conexões; zera estado. Não libera o memblock (é do app).
- **Retorno/erros:** 0; `NOT_INITIALIZED` (0x804E0801).
- **Estruturas:** —
- **Implementação no VitaSharp:** `Voice.cs`; chamar `Stop()` interno, `Dispose` dos streams, limpar `_ports` e
  `_connections`, `_initialized = false`.
- **Validação:** teste unitário: após End, `sceVoiceCreatePort` → 0x804E0801; `sceVoiceInit` volta a funcionar.

### sceVoiceStart
- [x] **Prioridade:** P2 · **Biblioteca:** SceVoice · **NID:** 0xB2ED725B
- **Protótipo:** `int sceVoiceStart(const SceVoiceStartParam *param);` (`psp2/voice.h`)
- **Comportamento:** anexa o memblock de trabalho (≥ 0x40000, tamanho **não** validado no FW 3.60) e inicia o
  processamento; recria recursos de processamento das portas existentes. Se já iniciado: sucesso sem trocar o
  memblock, mas `param` ainda precisa ser não-nulo.
- **Retorno/erros:** 0; `NOT_INITIALIZED`; `INVALID_ARGUMENT` se `param == NULL`; `INVALID_MEMBLOCK` (0x804E0806) se
  `mem_block_id` não resolve para um memblock.
- **Estruturas:** `SceVoiceStartParam` (0x20): +0x00 `mem_block_id` (SceUID), +0x04 `reserved[7]`.
- **Implementação no VitaSharp:** `Voice.cs`; `K.Objects.Get<MemBlock>(uid) == null` → 0x804E0806. Não escrever no
  memblock (o HLE não precisa dele). Marcar `_started`, guardar o tick inicial para o pacing, portas passam a `IDLE`
  (portas de app) ou `ACTIVE` (device) — estados exatos após Start a confirmar via `GetPortInfo` no hardware.
- **Validação:** teste unitário com memblock criado por `sceKernelAllocMemBlock`; uid inválido → 0x804E0806.

### sceVoiceStop
- [x] **Prioridade:** P2 · **Biblioteca:** SceVoice · **NID:** 0xC3868DF6
- **Protótipo:** `int sceVoiceStop(void);` (`psp2/voice.h`)
- **Comportamento:** para o processamento e desanexa o memblock. Portas e conexões permanecem, mas buffers são
  descartados. Já parado mas inicializado → sucesso.
- **Retorno/erros:** 0; `NOT_INITIALIZED`.
- **Implementação no VitaSharp:** `Voice.cs`; zerar `Count` de todos os rings, estados para `INACTIVE`,
  `_started = false`.
- **Validação:** teste unitário: após Stop, `GetPortInfo` → 0x804E080A.

### sceVoiceGetResourceInfo
- [x] **Prioridade:** P2 · **Biblioteca:** SceVoice · **NID:** 0x6E46950E
- **Protótipo:** `int sceVoiceGetResourceInfo(SceVoiceResourceInfo *info);` (`psp2/voice.h`)
- **Comportamento:** escreve os limites fixos do FW 3.60: 7 IN_VOICE, 7 OUT_VOICE, 1 IN_DEVICE, 1 OUT_DEVICE, 64 portas.
- **Retorno/erros:** 0; `NOT_INITIALIZED`; `INVALID_ARGUMENT` se `info == NULL`.
- **Estruturas:** `SceVoiceResourceInfo` (0xA): +0x0 `max_voice_input_ports` u16, +0x2 `max_voice_output_ports`,
  +0x4 `max_device_input_ports`, +0x6 `max_device_output_ports`, +0x8 `max_ports`.
- **Implementação no VitaSharp:** `Voice.cs`; 5× `W16`. Usar as mesmas constantes no `CreatePort`.
- **Validação:** teste unitário.

### sceVoiceCheckTopology
- [x] **Prioridade:** P2 · **Biblioteca:** SceVoice · **NID:** 0x874054CE
- **Protótipo:** `int sceVoiceCheckTopology(void);` (`psp2/voice.h`)
- **Comportamento:** valida o grafo: toda porta criada tem ≥ 1 conexão; portas OUT_VOICE adjacentes na lista interna
  têm o mesmo bitrate, mesmo mute (zero/não-zero) e volume exatamente igual; se existem IN_DEVICE e IN_PCMAUDIO,
  rejeita OUT_VOICE ligada a IN_PCMAUDIO cujo encoder ainda não foi atribuído (antes do Start).
- **Retorno/erros:** 0 se válido; erro negativo caso contrário — qual código para cada regra é a confirmar
  (provável `INVALID_PORT` 0x804E0807 ou `INVALID_ARGUMENT`; confirmar em hardware criando uma porta sem conexão).
  `NOT_INITIALIZED`.
- **Implementação no VitaSharp:** `Voice.cs`; a "lista interna" das OUT_VOICE = ordem de criação (iterar `_ports` por
  índice). Implementar as três regras; a terceira equivale a "não iniciado e existe IN_DEVICE e IN_PCMAUDIO e alguma
  OUT_VOICE conectada de IN_PCMAUDIO".
- **Validação:** teste unitário com cada caso.

### sceVoiceCreatePort
- [x] **Prioridade:** P2 · **Biblioteca:** SceVoice · **NID:** 0xFA4E57B1
- **Protótipo:** `int sceVoiceCreatePort(SceVoicePortId *port_id, const SceVoicePortParam *param);` (`psp2/voice.h`)
- **Comportamento:** escreve 0xFF em `*port_id` antes de validar. Valida o tipo (0..5); para PCMAUDIO:
  `pcm_data_type == 0` (S16LE), `sampling_rate == 16000`, capacidade efetiva = `buffer_size & ~511`, faixa
  1024..64512 (o FW trunca para 16 bits antes de validar — não reproduzir o wraparound, só validar a faixa); para
  VOICE: `bit_rate` ∈ tabela; para device: membro `data` deve ser 0. Para entradas alimentadas pelo app (IN_PCMAUDIO,
  IN_VOICE), `threshold` (ms) deve ser menor que a duração do buffer (o FW usa no mínimo 2 durações de frame).
  Respeita limites por tipo e total. Aloca o menor slot livre (0..63). Se o processamento estiver iniciado e a
  inicialização imediata da porta falhar, a porta fica criada, `*port_id` recebe o ID válido e o erro é devolvido.
- **Retorno/erros:** 0; `NOT_INITIALIZED`; `INVALID_ARGUMENT` (ponteiros nulos, tipo/formato/rate/bitrate inválido,
  threshold grande demais); `RESOURCE_LIMIT` (0x804E0808) ao exceder limites.
- **Estruturas:** `SceVoicePortParam` (0x18): +0x00 `port_type` u32, +0x04 `threshold` u16 (ms), +0x06 `mute_flag`
  u16, +0x08 `volume` float, +0x0C `data` (union: `buffer_size` p/ PCM, `bit_rate` p/ VOICE), +0x10 `pcm_data_type`,
  +0x14 `sampling_rate`.
- **Implementação no VitaSharp:** `Voice.cs`. Duração do buffer PCM = `cap / 2 / 16000` s. Para IN_VOICE a "duração
  do buffer" depende da capacidade interna do ring (a confirmar; usar 16 frames). Ring de bytes: PCM = `cap`;
  OUT_VOICE/IN_VOICE = 16 frames × bytes/frame (a confirmar o tamanho real). OUT_DEVICE: abrir `IAudioStream`
  (16 kHz, mono, grain 160) via `Emu.Host.Audio?.OpenStream` dentro de `try` (como `Audio.cs`).
- **Validação:** teste unitário: `*port_id == 0xFF` após erro de validação; buffer_size 1000 → erro; 1024 → ok;
  oitava IN_VOICE → 0x804E0808.

### sceVoiceUpdatePort
- [x] **Prioridade:** P2 · **Biblioteca:** SceVoice · **NID:** 0xCE855C50
- **Protótipo:** `int sceVoiceUpdatePort(SceVoicePortId port_id, const SceVoicePortParam *param);` (`psp2/voice.h`)
- **Comportamento:** troca configuração de uma porta existente; o tipo não pode mudar. Mudar bitrate recria o
  codec e descarta dados. Não realoca rings PCM (no FW 3.60 mudar `buffer_size` corrompe a contabilidade — o HLE
  pode simplesmente ignorar `buffer_size` em updates). Não revalida `threshold`.
- **Retorno/erros:** 0; `NOT_INITIALIZED`; `INVALID_PORT_ID`; `INVALID_ARGUMENT` se `param` nulo, tipo diferente ou
  bitrate inválido.
- **Estruturas:** `SceVoicePortParam` (ver CreatePort).
- **Implementação no VitaSharp:** `Voice.cs`; atualizar `Threshold`, `Mute`, `Volume`, `BitRate` (limpando o ring se
  mudou).
- **Validação:** teste unitário.

### sceVoiceConnectIPortToOPort
- [x] **Prioridade:** P2 · **Biblioteca:** SceVoice · **NID:** 0x698BDAAE
- **Protótipo:** `int sceVoiceConnectIPortToOPort(SceVoicePortId input_port_id, SceVoicePortId output_port_id);`
  (`psp2/voice.h`)
- **Comportamento:** liga entrada (tipo 0..2) a saída (3..5). Rejeita IN_VOICE→OUT_VOICE direto, conexão duplicada e
  mais de 128 conexões.
- **Retorno/erros:** 0; `NOT_INITIALIZED`; `INVALID_PORT_ID`; `INVALID_ARGUMENT` ou `INVALID_PORT` para tipos
  invertidos / IN_VOICE→OUT_VOICE / duplicata (código exato de cada caso a confirmar em hardware);
  `RESOURCE_LIMIT` ao passar de 128.
- **Implementação no VitaSharp:** `Voice.cs`; `_connections.Add((i, o))`.
- **Validação:** teste unitário.

### sceVoiceDisconnectIPortFromOPort
- [x] **Prioridade:** P2 · **Biblioteca:** SceVoice · **NID:** 0x5F0260F4
- **Protótipo:** `int sceVoiceDisconnectIPortFromOPort(SceVoicePortId input_port_id, SceVoicePortId output_port_id);`
  (`psp2/voice.h`)
- **Comportamento:** remove a conexão.
- **Retorno/erros:** 0; `NOT_INITIALIZED`; `INVALID_PORT_ID`; conexão inexistente → erro a confirmar
  (provável `INVALID_ARGUMENT`).
- **Implementação no VitaSharp:** `Voice.cs`; `_connections.Remove`.
- **Validação:** teste unitário.

### sceVoiceDeletePort
- [x] **Prioridade:** P2 · **Biblioteca:** SceVoice · **NID:** 0xAE46564D
- **Protótipo:** `int sceVoiceDeletePort(SceVoicePortId port_id);` (`psp2/voice.h`)
- **Comportamento:** apaga a porta e todas as conexões que a envolvem; libera o slot e a contagem de recursos.
- **Retorno/erros:** 0; `NOT_INITIALIZED`; `INVALID_PORT_ID`.
- **Implementação no VitaSharp:** `Voice.cs`; `Stream?.Dispose()`, `_connections.RemoveWhere(...)`, `_ports[id] = null`.
- **Validação:** teste unitário: ID volta a ser reutilizado pelo próximo `CreatePort`.

### sceVoiceWriteToIPort
- [x] **Prioridade:** P2 · **Biblioteca:** SceVoice · **NID:** 0x0A22EC0E
- **Protótipo:** `int sceVoiceWriteToIPort(SceVoicePortId port_id, const void *data, SceSize *size, SceInt16 frame_gap);`
  (`psp2/voice.h`)
- **Comportamento:** escreve dados do app numa IN_PCMAUDIO ou IN_VOICE; `*size` volta com os bytes aceitos (limitado
  ao espaço livre do ring). PCM ignora `frame_gap`. IN_VOICE: `frame_gap >= 0` insere N frames perdidos antes dos
  dados; negativo faz backfill de frames marcados como perdidos. Se falhar após validar argumentos, `*size = 0`.
  Ao acumular `threshold` ms a porta passa de BUFFERING para ACTIVE.
- **Retorno/erros:** 0; `NOT_INITIALIZED`; `NOT_STARTED` (a confirmar se exige Start); `INVALID_PORT_ID`;
  `INVALID_PORT` se o tipo não for 1 ou 2; `INVALID_ARGUMENT` se `data`/`size` nulos.
- **Implementação no VitaSharp:** `Voice.cs`; parâmetro `short frameGap` (binder: passar como `int` e truncar). Para
  IN_VOICE, aceitar só múltiplos de frame e descartar o conteúdo (sem decoder CELP); para `frame_gap` só contabilizar.
  Antes de copiar, rodar `Process()` (pacing) para consumir o que já "tocou".
- **Validação:** teste unitário com PCM: escrever 2048 bytes num ring de 1024 → `*size == 1024`.

### sceVoiceReadFromOPort
- [x] **Prioridade:** P2 · **Biblioteca:** SceVoice · **NID:** 0x09E4D18C
- **Protótipo:** `int sceVoiceReadFromOPort(SceVoicePortId port_id, void *data, SceSize *size);` (`psp2/voice.h`)
- **Comportamento:** lê de OUT_PCMAUDIO ou OUT_VOICE; `*size` (capacidade) volta com os bytes copiados. Voz codificada
  é arredondada para baixo a frames completos; PCM para número par de bytes. Falha pós-validação → `*size = 0`.
- **Retorno/erros:** 0 (inclusive com 0 bytes disponíveis); `NOT_INITIALIZED`; `INVALID_PORT_ID`; `INVALID_PORT` se
  tipo não for 3 ou 4; `INVALID_ARGUMENT` para ponteiros nulos.
- **Implementação no VitaSharp:** `Voice.cs`; `Process()` primeiro: para cada 10 ms decorridos (só se `_started` e
  não pausado), cada saída soma as entradas conectadas (IN_PCMAUDIO: tira 320 bytes do ring, aplica volume da entrada
  e da saída, clamp s16; IN_DEVICE/IN_VOICE: zeros) e enfileira no ring da saída (OUT_PCMAUDIO: PCM 16 kHz;
  OUT_VOICE: frame zerado de `bytes/frame` a cada duração de frame; OUT_DEVICE: `Stream.Submit`). Mute = zeros.
  Limitar a quantidade de frames gerados por chamada ao tamanho do ring (ring cheio descarta o mais antigo — política
  real a confirmar).
- **Validação:** teste unitário IN_PCMAUDIO→OUT_PCMAUDIO com volume 0.5: amostra 1000 deve sair 500.

### sceVoiceGetPortInfo
- [x] **Prioridade:** P2 · **Biblioteca:** SceVoice · **NID:** 0x5933CCFB
- **Protótipo:** `int sceVoiceGetPortInfo(SceVoicePortId port_id, SceVoicePortInfo *info);` (`psp2/voice.h`)
- **Comportamento:** devolve tipo, estado, `data_size` (bytes livres no ring de entrada ou bytes enfileirados no de
  saída) e `frame_size` (tamanho do frame de processamento, codificado ou PCM). Se não iniciado, zera a struct inteira e
  retorna `NOT_STARTED`. Em sucesso escreve só os 4 campos (reserved intocados). Durante pausa o estado reportado fica
  congelado.
- **Retorno/erros:** 0; `NOT_INITIALIZED`; `NOT_STARTED` (0x804E080A); `INVALID_PORT_ID`; `INVALID_ARGUMENT` se
  `info` nulo.
- **Estruturas:** `SceVoicePortInfo` (0x18): +0x00 `port_type`, +0x04 `state`, +0x08 `reserved0` (não escrito),
  +0x0C `data_size`, +0x10 `frame_size`, +0x14 `reserved1` (não escrito).
- **Implementação no VitaSharp:** `Voice.cs`; chamar `Process()` antes. `frame_size` para VOICE = bytes/frame da
  tabela; para PCM = a confirmar (hipótese: 320 bytes = 10 ms a 16 kHz; confirmar em hardware lendo `frame_size` de uma
  OUT_PCMAUDIO); para device = a confirmar.
- **Validação:** teste unitário + homebrew em hardware para os valores "a confirmar".

### sceVoiceResetPort
- [x] **Prioridade:** P2 · **Biblioteca:** SceVoice · **NID:** 0x5E1CE910
- **Protótipo:** `int sceVoiceResetPort(SceVoicePortId port_id);` (`psp2/voice.h`)
- **Comportamento:** limpa os dados do ring; ACTIVE vira IDLE; INACTIVE/IDLE/BUFFERING mantêm estado interno. O estado
  visível em `GetPortInfo` só muda no próximo ciclo de processamento.
- **Retorno/erros:** 0; `NOT_INITIALIZED`; `INVALID_PORT_ID`.
- **Implementação no VitaSharp:** `Voice.cs`; `Head = Count = 0`; ajustar `State`. Ignorar a defasagem do estado
  reportado (o HLE processa sob demanda).
- **Validação:** teste unitário.

### Família: atributos simples de porta (mute, volume, bitrate, attr) e pausa

Todas: `NOT_INITIALIZED` (0x804E0801) antes do Init; `INVALID_PORT_ID` (0x804E0804) para ID ≥ 64 ou slot vazio;
`INVALID_ARGUMENT` (0x804E0805) para ponteiro de saída nulo. Implementação em `Voice.cs`, leitura/escrita direta nos
campos de `Port`. Validação: teste unitário set→get. Protótipos em `psp2/voice.h`.

| Função | NID | Protótipo | Comportamento / particularidades |
| --- | --- | --- | --- |
| - [x] sceVoiceSetMuteFlagAll | 0xDB90EAC4 | `int sceVoiceSetMuteFlagAll(SceBool mute);` | Liga/desliga o override global que só afeta o que `GetMuteFlag` **reporta** (todas as portas = 1); não silencia o áudio. `_muteAll = mute != 0`. |
| - [x] sceVoiceSetMuteFlag | 0x0B9E4AE2 | `int sceVoiceSetMuteFlag(SceVoicePortId port_id, SceBool mute);` | `Port.Mute = mute != 0`; porta mutada contribui/produz zeros no processamento. |
| - [x] sceVoiceGetMuteFlag | 0xC913F7E9 | `int sceVoiceGetMuteFlag(SceVoicePortId port_id, SceUInt16 *mute);` | Escreve u16 `1` se `_muteAll` ou `Port.Mute`, senão `0`. |
| - [x] sceVoiceSetVolume | 0xD93769E6 | `int sceVoiceSetVolume(SceVoicePortId port_id, float volume);` | Guarda sem limitar faixa (volume em `s0`); amostras escaladas são clampadas a s16 no processamento. OUT_DEVICE: também `Stream.Volume`? Não — aplicar no mix para manter clamp. |
| - [x] sceVoiceGetVolume | 0x875CC80D | `int sceVoiceGetVolume(SceVoicePortId port_id, float *volume);` | Escreve o float guardado (`W32(BitConverter.SingleToUInt32Bits(v))`). |
| - [x] sceVoiceSetBitRate | 0x02F58D6F | `int sceVoiceSetBitRate(SceVoicePortId port_id, SceUInt32 bit_rate);` | Só IN_VOICE/OUT_VOICE (outros → `INVALID_PORT` 0x804E0807, a confirmar); bitrate fora da tabela → `INVALID_ARGUMENT`; recria codec e descarta o ring. |
| - [x] sceVoiceGetBitRate | 0xD02C00B4 | `int sceVoiceGetBitRate(SceVoicePortId port_id, SceUInt32 *bit_rate);` | Só portas VOICE (outras → `INVALID_PORT`, a confirmar); escreve `Port.BitRate`. |
| - [x] sceVoiceSetPortAttr | 0x9B0D3490 | `int sceVoiceSetPortAttr(SceVoicePortId port_id, SceVoicePortAttr attr, const SceInt32 *value);` | No FW 3.60 **sempre falha**: porta existente → `INVALID_ARGUMENT`; ID válido sem porta → `INVALID_PORT_ID`. |
| - [x] sceVoiceGetPortAttr | 0x73F72940 | `int sceVoiceGetPortAttr(SceVoicePortId port_id, SceVoicePortAttr attr, SceInt32 *value);` | `attr == 1000` (AUDIO_INPUT_OWNERSHIP) numa IN_DEVICE escreve o estado de posse do microfone — HLE: escrever `0` (sem mic; valor exato "possui" a confirmar, provavelmente 1). Outros tipos: retorna 0 sem escrever. `attr` desconhecido → `INVALID_ARGUMENT` (a confirmar). |
| - [x] sceVoicePausePort | 0x23C6B16B | `int sceVoicePausePort(SceVoicePortId port_id);` | `Paused = true`; mantém buffers; device/saídas vão a INACTIVE internamente, mas o estado reportado congela até retomar. |
| - [x] sceVoiceResumePort | 0x2DE35411 | `int sceVoiceResumePort(SceVoicePortId port_id);` | `Paused = false`; reiniciar o relógio de pacing da porta para não "recuperar" o tempo pausado. |
| - [x] sceVoicePausePortAll | 0x39AA3884 | `int sceVoicePausePortAll(void);` | `_allPaused = true`; mantém buffers e estados internos e reportados. |
| - [x] sceVoiceResumePortAll | 0x1F93FC0C | `int sceVoiceResumePortAll(void);` | `_allPaused = false`; reiniciar o relógio de pacing global. |

---

## SceVoiceQoS (17 funções)

### Arquitetura (SceVoiceQoS)

- **Objetivo:** camada de "qualidade de serviço" sobre o SceVoice para chat em rede: o app cria *endpoints* locais
  (cada um ligado a portas do SceVoice) e remotos, conecta local↔remoto e troca pacotes (`ReadPacket` produz o
  pacote a enviar pela rede; `WritePacket` entrega o pacote recebido). O próprio QoS faz jitter buffer, controle de
  bitrate e mixagem. Usado por jogos com voice chat online (geralmente via NpMatching2/Rudp).
- **Fontes:** **não há header** no vita-headers nem implementação no Vita3K (`modules/SceVoiceQoS/SceVoiceQoS.cpp`:
  17× `UNIMPLEMENTED()`). Módulo `SceVoiceQoS` (NID 0x9A89A62B), biblioteca `SceVoiceQoS` (NID 0x650551BC, db
  `360/SceVoiceQoS.yml`), `SCE_SYSMODULE_VOICEQOS = 0x001B`. Os nomes estão no `nids.txt`.
- **Protótipos:** todos **inferidos** da biblioteca homônima do PS4 (`libSceVoiceQoS`, mesma API da Sony: IDs
  `SceVoiceQoSLocalId/RemoteId/ConnectionId` = `int32_t`, atributos com `(id, attrId, void*, size)`). Os IDs de
  atributo/status e códigos de erro **não** são conhecidos para Vita: a confirmar por desmontagem de
  `vs0:sys/external/libvoiceqos.suprx` (Ghidra + vita-loader, procurar as comparações de `attributeId` e as constantes
  `0x80xxxxxx` retornadas) ou por homebrew em hardware. Não implementar valores específicos antes disso.
- **Estratégia proposta (stub offline):** como o VitaSharp não emula rede real para jogos, o QoS deve funcionar
  como "sessão sem pares que falam":
  - Init/End/Create/Delete/Connect/Disconnect mantêm tabelas e devolvem IDs válidos.
  - `ReadPacket` devolve `*pSize = 0` (nada a enviar) — ou um pacote mínimo se o jogo exigir (a confirmar).
  - `WritePacket` aceita e descarta (`*pSize` inalterado = tudo consumido).
  - Atributos: guardar o blob por `(id, attrId)` num dicionário e devolver o que foi escrito; leitura de atributo
    nunca escrito → zeros. `GetStatus` → zeros (sem dados de entrada/saída).
  - `CreateLocalEndpoint` no hardware cria portas do SceVoice internamente; o HLE **não** precisa (as portas do app
    são criadas pelo app). Se `sceVoiceQoSInit` exigir SceVoice inicializado, checar `Voice` (a confirmar).
- **Estado interno (classe `VoiceQoS : HleModule`):** `bool _init; int _nextId = 1; Dictionary<int, LocalEp>;
  Dictionary<int, RemoteEp>; Dictionary<int, (int local, int remote)> _conns; Dictionary<(int id, int attr), byte[]>
  _attrs;` com `lock`.
- **Arquivo novo:** `src/VitaSharp.Core/Hle/Modules/VoiceQoS.cs` (`public sealed class VoiceQoS : HleModule`).
- **Dependências:** `GuestMemory`, `Ptr<T>`; opcionalmente o estado de `Voice` (acessar via `Emu` → módulo registrado;
  ver como `Emulator.InitProcess` guarda instâncias de `HleModule`).

Códigos de erro: **todos a confirmar** (nenhuma fonte pública para Vita). Usar provisoriamente `SceError.ERROR`
(0x80020001) para "não inicializado"/"ID inválido", com `Log.WarnOnce`, e substituir após desmontagem. Hipótese a
verificar: o PS4 usa a faixa `0x80A30000` para VoiceQoS; a Vita pode usar a faixa do SceVoice `0x804E08xx` (QoS e
Voice são da mesma equipe) — não usar sem confirmar.

Validação comum: teste unitário (Init→CreateLocal→CreateRemote→Connect→Read/WritePacket→Disconnect→Delete×2→End).
Em jogo: quando algum importar, verificar no log que o jogo passa pela tela de chat/lobby sem travar.

**Implementado** (`Hle/Modules/VoiceQoS.cs`) como sessão offline: IDs de endpoints/conexões vêm de um contador único
(começa em 1); erros provisórios `SceError.ERROR` (0x80020001, com `WarnOnce`) para não inicializado / já inicializado /
ID desconhecido / conexão duplicada, e `SceError.INVALID_ARGUMENT` (0x80020005) para ponteiro nulo ou tamanho de
atributo ≤ 0 ou > 0x10000; apagar um endpoint remove as conexões que o usam; `Init` não exige o SceVoice.

### sceVoiceQoSInit
- [x] **Prioridade:** P2 · **Biblioteca:** SceVoiceQoS · **NID:** 0x4B5FFF1C
- **Protótipo (inferido, PS4):** `int sceVoiceQoSInit(void *pMemBlock, SceUInt32 memSize, SceInt32 appType);`
- **Comportamento:** inicializa a biblioteca usando memória fornecida pelo app. Segunda chamada → erro "já
  inicializado".
- **Retorno/erros:** 0; erros a confirmar (ponteiro nulo, tamanho insuficiente — tamanho mínimo a confirmar).
- **Implementação no VitaSharp:** `VoiceQoS.cs`; não usar o bloco; `_init = true`. Logar os três argumentos com
  `Log.Debug` para confirmar a assinatura quando um jogo chamar.
- **Validação:** teste unitário; em jogo, conferir que `r0` é ponteiro alinhado e `r1` um tamanho plausível.

### sceVoiceQoSEnd
- [x] **Prioridade:** P2 · **Biblioteca:** SceVoiceQoS · **NID:** 0xFB0B747B
- **Protótipo (inferido):** `int sceVoiceQoSEnd(void);`
- **Comportamento:** destrói conexões e endpoints e finaliza.
- **Retorno/erros:** 0; não inicializado → a confirmar.
- **Implementação no VitaSharp:** `VoiceQoS.cs`; limpar tudo.
- **Validação:** teste unitário.

### sceVoiceQoSCreateLocalEndpoint
- [x] **Prioridade:** P2 · **Biblioteca:** SceVoiceQoS · **NID:** 0xAAB54BE4
- **Protótipo (inferido):** `int sceVoiceQoSCreateLocalEndpoint(SceVoiceQoSLocalId *pLocalId);`
- **Comportamento:** cria endpoint local (no hardware também as portas internas do SceVoice) e devolve o ID.
- **Retorno/erros:** 0; ponteiro nulo / limite de endpoints → a confirmar (limite também a confirmar).
- **Implementação no VitaSharp:** `VoiceQoS.cs`; `_nextId++`, escrever em `*pLocalId`.
- **Validação:** teste unitário.

### sceVoiceQoSCreateRemoteEndpoint
- [x] **Prioridade:** P2 · **Biblioteca:** SceVoiceQoS · **NID:** 0xBAB98727
- **Protótipo (inferido):** `int sceVoiceQoSCreateRemoteEndpoint(SceVoiceQoSRemoteId *pRemoteId);`
- **Comportamento:** cria endpoint que representa um par remoto.
- **Retorno/erros:** 0; erros a confirmar.
- **Implementação no VitaSharp:** `VoiceQoS.cs`; idem local.
- **Validação:** teste unitário.

### sceVoiceQoSConnect
- [x] **Prioridade:** P2 · **Biblioteca:** SceVoiceQoS · **NID:** 0xE0C5CEEE
- **Protótipo (inferido):** `int sceVoiceQoSConnect(SceVoiceQoSConnectionId *pConnectionId, SceVoiceQoSLocalId localId, SceVoiceQoSRemoteId remoteId);`
- **Comportamento:** cria uma conexão local↔remoto e devolve o ID.
- **Retorno/erros:** 0; IDs inexistentes / conexão duplicada → a confirmar.
- **Implementação no VitaSharp:** `VoiceQoS.cs`; validar que ambos existem; `_conns[id] = (l, r)`.
- **Validação:** teste unitário.

### sceVoiceQoSDisconnect
- [x] **Prioridade:** P2 · **Biblioteca:** SceVoiceQoS · **NID:** 0x3C7A08B0
- **Protótipo (inferido):** `int sceVoiceQoSDisconnect(SceVoiceQoSConnectionId connectionId);`
- **Comportamento:** encerra a conexão.
- **Retorno/erros:** 0; ID inválido → a confirmar.
- **Implementação no VitaSharp:** `VoiceQoS.cs`; remover de `_conns` e os atributos da conexão.
- **Validação:** teste unitário.

### sceVoiceQoSReadPacket
- [x] **Prioridade:** P2 · **Biblioteca:** SceVoiceQoS · **NID:** 0x2D613549
- **Protótipo (inferido):** `int sceVoiceQoSReadPacket(SceVoiceQoSConnectionId connectionId, void *pData, SceUInt32 *pSize);`
- **Comportamento:** copia para `pData` o próximo pacote de voz a enviar ao remoto da conexão; `*pSize` entra como
  capacidade e sai como bytes escritos.
- **Retorno/erros:** 0; sem dados: no PS4 é 0 com `*pSize = 0` ou um erro "sem dados" — a confirmar.
- **Implementação no VitaSharp:** `VoiceQoS.cs`; `*pSize = 0`, retornar 0. Se o jogo tratar 0 bytes como erro
  (observar log), considerar devolver um pacote de silêncio cujo formato é a confirmar.
- **Validação:** teste unitário.

### sceVoiceQoSWritePacket
- [x] **Prioridade:** P2 · **Biblioteca:** SceVoiceQoS · **NID:** 0x2FE1F28F
- **Protótipo (inferido):** `int sceVoiceQoSWritePacket(SceVoiceQoSConnectionId connectionId, const void *pData, SceUInt32 *pSize);`
- **Comportamento:** entrega ao QoS um pacote recebido do remoto (vai para o jitter buffer → porta IN_VOICE).
  `*pSize` sai com os bytes aceitos.
- **Retorno/erros:** 0; erros a confirmar.
- **Implementação no VitaSharp:** `VoiceQoS.cs`; descartar; manter `*pSize`.
- **Validação:** teste unitário.

### Família: consulta de endpoints, atributos e status

Protótipos todos **inferidos** do PS4. Erros comuns: não inicializado, ID inválido, ponteiro nulo, tamanho de
atributo incorreto — todos a confirmar. IDs de atributo (`SceVoiceQoSAttributeId`) e de status
(`SceVoiceQoSStatusId`) a confirmar (no PS4 incluem mic/speaker mute, volume e "remote bit rate"; não assumir
valores). Implementação em `VoiceQoS.cs` com o dicionário `_attrs`; validação por teste unitário set→get.

| Função | NID | Protótipo (inferido) | Comportamento |
| --- | --- | --- | --- |
| - [x] sceVoiceQoSDeleteLocalEndpoint | 0x68FABF6F | `int sceVoiceQoSDeleteLocalEndpoint(SceVoiceQoSLocalId localId);` | Remove o endpoint local (e conexões que o usam — ou erro se houver conexões; a confirmar). |
| - [x] sceVoiceQoSDeleteRemoteEndpoint | 0xC2F2C771 | `int sceVoiceQoSDeleteRemoteEndpoint(SceVoiceQoSRemoteId remoteId);` | Idem para remoto. |
| - [x] sceVoiceQoSGetLocalEndpoint | 0xE5B4527D | `int sceVoiceQoSGetLocalEndpoint(SceVoiceQoSConnectionId connectionId, SceVoiceQoSLocalId *pLocalId);` | Escreve o ID local da conexão. |
| - [x] sceVoiceQoSGetRemoteEndpoint | 0x876A9B9C | `int sceVoiceQoSGetRemoteEndpoint(SceVoiceQoSConnectionId connectionId, SceVoiceQoSRemoteId *pRemoteId);` | Escreve o ID remoto da conexão. |
| - [x] sceVoiceQoSSetLocalEndpointAttribute | 0x540CEBA5 | `int sceVoiceQoSSetLocalEndpointAttribute(SceVoiceQoSLocalId localId, SceVoiceQoSAttributeId attributeId, const void *pAttributeValue, SceInt32 attributeSize);` | Guarda `attributeSize` bytes em `_attrs[(localId, attr)]`. |
| - [x] sceVoiceQoSGetLocalEndpointAttribute | 0xC981AB3B | `int sceVoiceQoSGetLocalEndpointAttribute(SceVoiceQoSLocalId localId, SceVoiceQoSAttributeId attributeId, void *pAttributeValue, SceInt32 attributeSize);` | Copia o blob guardado (zeros se nunca escrito). |
| - [x] sceVoiceQoSSetConnectionAttribute | 0xE757806F | `int sceVoiceQoSSetConnectionAttribute(SceVoiceQoSConnectionId connectionId, SceVoiceQoSAttributeId attributeId, const void *pAttributeValue, SceInt32 attributeSize);` | Idem, chave da conexão (usar espaço de chaves separado do local). |
| - [x] sceVoiceQoSGetConnectionAttribute | 0xE81B8D44 | `int sceVoiceQoSGetConnectionAttribute(SceVoiceQoSConnectionId connectionId, SceVoiceQoSAttributeId attributeId, void *pAttributeValue, SceInt32 attributeSize);` | Copia o blob guardado (zeros se nunca escrito). |
| - [x] sceVoiceQoSGetStatus | 0xC9DC1425 | `int sceVoiceQoSGetStatus(SceVoiceQoSConnectionId connectionId, SceVoiceQoSStatusId statusId, void *pStatusValue, SceInt32 statusSize);` | Escreve zeros em `statusSize` bytes (sem tráfego/atividade de voz). |

---

## SceG729 (8 funções)

### Arquitetura (SceG729)

- **Objetivo:** codec de voz ITU-T G.729 (CS-ACELP, 8 kHz, 8 kbit/s; frame de 10 ms = 80 amostras s16 ↔ 10 bytes)
  exposto ao app. Uso típico: voz em VoIP/chat (Skype da Vita, apps de chamada); jogos raramente.
- **Fontes:** **não há header** no vita-headers nem módulo no Vita3K. db `360/SceLibG729.yml`: módulo `SceLibG729`
  (NID 0xC1C50DCF), biblioteca `SceG729` (NID 0x9372381A, `kernel: false`). Carregado via
  `SCE_SYSMODULE_INTERNAL_G729 = 0x80000004` (sysmodule interno).
- **NIDs fora do `nids.txt`:** nenhum dos 8 nomes está em `src/VitaSharp.Core/Hle/Data/nids.txt`. Cada método precisa
  de `[Hle(Nid = 0x...)]` (e `Library = "SceG729"` não é necessário se o NID for explícito — conferir em
  `HleFramework.cs`/registro como o NID explícito é associado à biblioteca). Alternativa: acrescentar as 8 linhas ao
  `nids.txt` no formato `0x918BE529 SceLibG729 SceG729 sceG729DecodeCore` — mas o arquivo é gerado do Vita3K, então
  preferir `Nid =` no atributo.
- **Protótipos:** todos **inferidos** pelo padrão "context-based" das libs de voz Sony (GetContextSize → app aloca →
  Init(ctx) → Core por frame → Reset). Assinaturas exatas, modos (Annex A/B com VAD/DTX, Annex D 6.4k) e códigos de
  erro são a confirmar por desmontagem de `vs0:sys/external/libg729.suprx` (nome do arquivo a confirmar na listagem de
  `vs0:sys/external/`) — identificar nº de argumentos pelo uso de r0–r3/pilha no prólogo das exportações.
- **Engine:**
  - Decoder: **FFmpeg tem decoder `g729`** (`AV_CODEC_ID_G729`, também G.729D 6.4k, 8 kHz mono, s16 planar/float).
    Reaproveitar `Media/FrameDecoder.cs` (`FrameDecoder.Create(AVCodecID.AV_CODEC_ID_G729, 8000, 1)`) — verificar se
    `FrameDecoder.Decode` aceita formato de saída float/s16 do g729 do FFmpeg e converter para s16.
  - Encoder: **FFmpeg não tem encoder G.729.** Opções: (a) portar `bcg729` (GPLv3 — conferir compatibilidade de
    licença do VitaSharp antes); (b) emitir frames de silêncio válidos. Recomendação: (b) até um jogo precisar de
    áudio real enviado; bits de um frame de silêncio G.729 a confirmar (gerar codificando 80 zeros com bcg729 ou a
    implementação de referência ITU e embutir os 10 bytes como constante). Se o modo VAD/DTX (Annex B) estiver ativo,
    a saída de silêncio pode ser frame SID (2 bytes) ou "não transmitir" (0 bytes) — a confirmar.
- **Estado interno (classe `G729 : HleModule`):** contexto vive na memória do app; o HLE guarda
  `Dictionary<uint ctxAddr, EncState|DecState>` (DecState com `FrameDecoder?`). Escrever um magic/ID nos 4 primeiros
  bytes do ctx no Init não é necessário (a chave é o endereço).
- **Arquivo novo:** `src/VitaSharp.Core/Hle/Modules/G729.cs`.
- **Dependências:** `Media/FrameDecoder.cs`, `FFmpegLoader.Available` (sem FFmpeg → decodificar como silêncio com
  `Log.WarnOnce`, igual a `Audiodec.cs`).

Códigos de erro: todos a confirmar (nenhuma fonte). Provisoriamente: argumento nulo → `SceError.INVALID_ARGUMENT`
(0x80020005) com `Log.WarnOnce`.

Validação comum: teste unitário decodificando um vetor de teste G.729 da ITU (frames de 10 bytes → comparar com o PCM
de referência, tolerância ±1 LSB do FFmpeg); em jogo, quando algum importar, conferir no log que `DecodeGetContextSize`
devolve um tamanho que o jogo aceita.

**Implementado** (`Hle/Modules/G729.cs`, NIDs explícitos): `*GetContextSize` retorna 0x1000 sem escrever em `r0`
(o valor de `r0` vai para o log, para decidir a variante com `*pSize`); `Init/Core/Reset` chaveados pelo endereço do
contexto (contexto desconhecido ou ponteiro nulo → `INVALID_ARGUMENT`); `DecodeCore(ctx, in, out)` usa o g729 do FFmpeg
(silêncio sem FFmpeg); `EncodeCore(ctx, in, out)` escreve 10 bytes zero (não é um frame de silêncio G.729 verificado);
o 2º argumento de `EncodeInit` (VAD?) só é guardado. Ainda falta o teste com vetores ITU.

### sceG729DecodeGetContextSize
- [x] **Prioridade:** P2 · **Biblioteca:** SceG729 · **NID:** 0x57DB3ADD
- **Protótipo (inferido):** `SceInt32 sceG729DecodeGetContextSize(void);` (retorna o tamanho em bytes; pode ser
  `int sceG729DecodeGetContextSize(SceUInt32 *pSize)` — a confirmar)
- **Comportamento:** informa quantos bytes o app deve alocar para um contexto de decodificação.
- **Retorno/erros:** tamanho (>0) ou 0+saída; valor real a confirmar (desmontagem: constante retornada).
- **Implementação no VitaSharp:** `G729.cs`, `[Hle(Nid = 0x57DB3ADD)]`. Retornar um tamanho generoso (ex.: 0x1000)
  enquanto o real não é confirmado; se o jogo passar ponteiro em r0 (não-zero e mapeado), tratar como a variante com
  `*pSize` — logar `r0` para decidir.
- **Validação:** log no primeiro jogo que importar.

### sceG729DecodeInit
- [x] **Prioridade:** P2 · **Biblioteca:** SceG729 · **NID:** 0x7A74B98F
- **Protótipo (inferido):** `int sceG729DecodeInit(void *pContext /*, modo/flags a confirmar */);`
- **Comportamento:** inicializa o estado do decoder no buffer do app.
- **Retorno/erros:** 0; ctx nulo → erro a confirmar.
- **Implementação no VitaSharp:** `G729.cs`, `[Hle(Nid = 0x7A74B98F)]`; criar `DecState` com `FrameDecoder` g729
  associado ao endereço do contexto.
- **Validação:** teste unitário.

### sceG729DecodeCore
- [x] **Prioridade:** P2 · **Biblioteca:** SceG729 · **NID:** 0x918BE529
- **Protótipo (inferido):** `int sceG729DecodeCore(void *pContext, const void *pIn /*10 bytes*/, SceInt16 *pOut /*80 amostras*/ /*, int bfi ou tamanho do frame — a confirmar */);`
- **Comportamento:** decodifica um frame de 10 ms. Com flag de frame perdido (bfi) faz ocultação.
- **Retorno/erros:** 0 (ou nº de amostras/bytes — a confirmar).
- **Implementação no VitaSharp:** `G729.cs`, `[Hle(Nid = 0x918BE529)]`; `FrameDecoder.Decode(ptr, 10, pcm, 1)` e copiar
  80 s16 para `pOut`. Sem FFmpeg/erro: zeros. Frame perdido: zeros (o g729 do FFmpeg não expõe bfi — a confirmar).
- **Validação:** vetores de teste ITU-T G.729.

### sceG729DecodeReset
- [x] **Prioridade:** P2 · **Biblioteca:** SceG729 · **NID:** 0x60C5F97E
- **Protótipo (inferido):** `int sceG729DecodeReset(void *pContext);`
- **Comportamento:** reinicia o estado do decoder (filtros/preditor) sem realocar.
- **Retorno/erros:** 0; ctx desconhecido → erro a confirmar.
- **Implementação no VitaSharp:** `G729.cs`, `[Hle(Nid = 0x60C5F97E)]`; `FrameDecoder.Flush()` (já existe e é usado em
  `sceAudiodecClearContext`).
- **Validação:** teste unitário: Reset e redecodificar o mesmo frame dá o mesmo PCM da primeira vez.

### sceG729EncodeGetContextSize
- [x] **Prioridade:** P2 · **Biblioteca:** SceG729 · **NID:** 0xFB053ABE
- **Protótipo (inferido):** `SceInt32 sceG729EncodeGetContextSize(void);` (ou variante com `SceUInt32 *pSize`, a confirmar)
- **Comportamento:** tamanho do contexto de codificação.
- **Retorno/erros:** como `DecodeGetContextSize`.
- **Implementação no VitaSharp:** `G729.cs`, `[Hle(Nid = 0xFB053ABE)]`; mesmo tratamento.
- **Validação:** log no primeiro jogo.

### sceG729EncodeInit
- [x] **Prioridade:** P2 · **Biblioteca:** SceG729 · **NID:** 0xAB9DA641
- **Protótipo (inferido):** `int sceG729EncodeInit(void *pContext /*, int enableVad (Annex B) — a confirmar */);`
- **Comportamento:** inicializa o encoder; possivelmente habilita VAD/DTX.
- **Retorno/erros:** 0; erros a confirmar.
- **Implementação no VitaSharp:** `G729.cs`, `[Hle(Nid = 0xAB9DA641)]`; registrar `EncState` (guardar flag de VAD se o
  2º argumento existir).
- **Validação:** teste unitário.

### sceG729EncodeCore
- [x] **Prioridade:** P2 · **Biblioteca:** SceG729 · **NID:** 0xFF57E664
- **Protótipo (inferido):** `int sceG729EncodeCore(void *pContext, const SceInt16 *pIn /*80 amostras*/, void *pOut /*10 bytes*/ /*, SceUInt32 *pOutSize — se VAD/DTX, a confirmar */);`
- **Comportamento:** codifica 10 ms de PCM 8 kHz num frame de 10 bytes (ou SID/vazio com DTX).
- **Retorno/erros:** 0 (ou bytes gerados — a confirmar).
- **Implementação no VitaSharp:** `G729.cs`, `[Hle(Nid = 0xFF57E664)]`; escrever o frame de silêncio constante (10 bytes,
  valor a confirmar — ver Arquitetura); `Log.WarnOnce("g729-enc", ...)`.
- **Validação:** decodificar a saída com o decoder (FFmpeg) e obter silêncio.

### sceG729EncodeReset
- [x] **Prioridade:** P2 · **Biblioteca:** SceG729 · **NID:** 0xA5B87504
- **Protótipo (inferido):** `int sceG729EncodeReset(void *pContext);`
- **Comportamento:** reinicia o estado do encoder.
- **Retorno/erros:** 0.
- **Implementação no VitaSharp:** `G729.cs`, `[Hle(Nid = 0xA5B87504)]`; no-op sobre `EncState`.
- **Validação:** teste unitário.

---

## SceAudioencUser (14 funções)

### Arquitetura (SceAudioencUser)

- **Objetivo:** codificador de áudio por hardware (Avcodec). Na Vita o único tipo é **CELP** (`SCE_AUDIOENC_TYPE_CELP =
  0x2006`, 8 kHz, 1 stream, até 320 amostras/frame, até 24 bytes/frame) — o par do decoder CELP do SceAudiodec
  (`0x1006`). Uso: gravação/envio de voz (chat, mensagens de voz).
- **Fontes:** `psp2/audioenc.h` (8 das 14 funções; as variantes External/Resident, `EncodeNFrames` e
  `GetContextSize` não estão no header — inferir pelo `psp2/audiodec.h`, que tem as mesmas variantes). Vita3K
  (`modules/SceAudioenc/SceAudioencUser.cpp`) só tem `UNIMPLEMENTED()`. db: módulo `SceAvcodecUser` (NID 0x5AA351E8),
  biblioteca `SceAudioencUser` (NID 0xA938B1A6). Nomes presentes no `nids.txt`.
- **Engine:** CELP = MPEG-4 CELP NB (MPE). **FFmpeg não tem encoder nem decoder MPEG-4 CELP.** Estratégia: aceitar o
  PCM e produzir frames **do tamanho correto preenchidos com zeros**; `GetOptInfo` devolve cabeçalho a confirmar.
  Como o decoder CELP do VitaSharp (`Audiodec.cs`) também não existe, nada no emulador consome esses bytes; o que
  importa é o protocolo (tamanhos, ponteiros, retornos). Portar um codec CELP MPEG-4 (referência ISO 14496-5) fica
  fora de escopo.
- **Modelo de API:** espelho exato de `Hle/Modules/Audiodec.cs` (InitLibrary por tipo, `SceAudioencCtrl` com
  `size`/`handle`, handle guardado num `Dictionary<uint, Encoder>`, erros por ponteiro/tamanho/handle). Reaproveitar a
  mesma estrutura de código (copiar o esqueleto de `Audiodec.Create/Find`).
- **Tamanho de frame por bitrate:** `bytes = ceil(bitRate × duração / 8000)`; durações conhecidas pelo
  `voice.h`: 3850/4650 → 40 ms (320 amostras; 20 e 24 bytes), 5700/7300 → 20 ms (160 amostras; 15 e 19 bytes),
  12200 → 10 ms (80 amostras; 16 bytes). Para 6600, 8700, 9900, 10700, 11800: duração **a confirmar** (pela regra do
  CELP MPEG-4 NB e pelo limite `MAX_ES_SIZE = 24`, 9900/10700/11800 devem ser 10 ms → 13/14/15 bytes; 6600/8700
  provavelmente 20 ms → 17/22 bytes). Confirmar em hardware lendo `maxPcmSize`/`maxEsSize` preenchidos por
  `sceAudioencCreateEncoder` para cada bitrate.
- **Estado interno (classe `Audioenc : HleModule`):** `HashSet<uint> _libraries`; `Dictionary<uint, Encoder>
  _encoders` com `Encoder { uint BitRate; int FrameSamples, FrameBytes; bool External, Resident; }`; `uint
  _nextHandle = 1`.
- **Arquivo novo:** `src/VitaSharp.Core/Hle/Modules/Audioenc.cs`.
- **Dependências:** `GuestMemory`, `Ptr<T>`, `Log`. Nenhuma lib externa.

Constantes: `SCE_AUDIOENC_WORD_LENGTH_16BITS = 16`, `TYPE_CELP = 0x2006`, `CELP_MAX_STREAMS = 1`,
`CELP_MAX_SAMPLES = 320`, `CELP_MAX_ES_SIZE = 24`, `CELP_MPE = 0`, `CELP_SAMPLING_RATE_8KHZ = 8000`.
Bitrates válidos: 3850, 4650, 5700, 6600, 7300, 8700, 9900, 10700, 11800, 12200.

Códigos de erro (`psp2/audioenc.h`):

| Nome | Valor |
| --- | --- |
| SCE_AUDIOENC_ERROR_API_FAIL | 0x80860000 |
| SCE_AUDIOENC_ERROR_INVALID_TYPE | 0x80860001 |
| SCE_AUDIOENC_ERROR_INVALID_INIT_PARAM | 0x80860002 |
| SCE_AUDIOENC_ERROR_ALREADY_INITIALIZED | 0x80860003 |
| SCE_AUDIOENC_ERROR_OUT_OF_MEMORY | 0x80860004 |
| SCE_AUDIOENC_ERROR_NOT_INITIALIZED | 0x80860005 |
| SCE_AUDIOENC_ERROR_A_HANDLE_IN_USE | 0x80860006 |
| SCE_AUDIOENC_ERROR_ALL_HANDLES_IN_USE | 0x80860007 |
| SCE_AUDIOENC_ERROR_INVALID_PTR | 0x80860008 |
| SCE_AUDIOENC_ERROR_INVALID_HANDLE | 0x80860009 |
| SCE_AUDIOENC_ERROR_NOT_HANDLE_IN_USE | 0x8086000A |
| SCE_AUDIOENC_ERROR_CH_SHORTAGE | 0x8086000B |
| SCE_AUDIOENC_ERROR_INVALID_WORD_LENGTH | 0x8086000C |
| SCE_AUDIOENC_ERROR_INVALID_SIZE | 0x8086000D |
| SCE_AUDIOENC_ERROR_INVALID_ALIGNMENT | 0x8086000E |
| SCE_AUDIOENC_ERROR_UNSUPPORTED | 0x8086000F |
| SCE_AUDIOENC_CELP_ERROR_INVALID_CONFIG | 0x80861001 |

Estruturas (`psp2/audioenc.h`):

- `SceAudioencCtrl` (0x2C): +0x00 `size` (=0x2C), +0x04 `handle`, +0x08 `pInputPcm`, +0x0C `inputPcmSize` (bytes
  consumidos, saída), +0x10 `maxPcmSize` (saída do Create), +0x14 `pOutputEs`, +0x18 `outputEsSize` (saída), +0x1C
  `maxEsSize` (saída do Create), +0x20 `wordLength` (deve ser 16), +0x24 `pInfo`, +0x28 `pOptInfo`.
  (Os comentários do header trocam "elementary stream"/"PCM" — ignorar; os nomes dos campos são os corretos.)
- `SceAudioencInitParam` (union, 8): `size`; `celp = SceAudioencInitStreamParam { +0 size (=8), +4 totalStreams }`.
- `SceAudioencInfoCelp` (0x10): +0 `size` (=0x10), +4 `excitationMode` (0 = MPE), +8 `samplingRate` (8000), +0xC
  `bitRate`.
- `SceAudioencOptInfoCelp` (0x2C): +0x00 `size` (=0x2C), +0x04 `header[32]`, +0x24 `headerSize`, +0x28
  `encoderVersion`.

Validação comum: teste unitário InitLibrary→Create→Encode×N→GetOptInfo→Delete→TermLibrary verificando
`maxPcmSize = FrameSamples*2`, `maxEsSize = FrameBytes`, `inputPcmSize`/`outputEsSize` após Encode.

**Implementado** (`Hle/Modules/Audioenc.cs`): CELP com frames de payload zero. Escolhas provisórias: `totalStreams`
só aceita 1; `CreateEncoderResident` não exige `InitLibrary` nem ocupa stream; `CreateEncoderExternal` exige
`contextSize ≥ 0x10000`; qualquer variante de `Delete*` apaga qualquer tipo de encoder (`DeleteEncoderExternal`
escreve o `vaContext` em `*pvaContext` se não nulo); `EncodeNFrames(0)` = 1 frame; `TermLibrary` com encoder da
biblioteca vivo → `A_HANDLE_IN_USE`; alinhamento de buffers não validado.

### sceAudioencInitLibrary
- [x] **Prioridade:** P2 · **Biblioteca:** SceAudioencUser · **NID:** 0x76EE4DC6
- **Protótipo:** `int sceAudioencInitLibrary(int codecType, SceAudioencInitParam *pInitParam);` (`psp2/audioenc.h`)
- **Comportamento:** reserva recursos do encoder do tipo dado para `totalStreams` streams.
- **Retorno/erros:** 0; `INVALID_TYPE` (0x80860001) se `codecType != 0x2006`; `INVALID_PTR` (0x80860008) se
  `pInitParam` nulo; `INVALID_INIT_PARAM` (0x80860002) se `size != 8` ou `totalStreams` fora de 1..1
  (`CELP_MAX_STREAMS`) — qual erro exato para cada campo a confirmar; `ALREADY_INITIALIZED` (0x80860003) na 2ª
  chamada.
- **Estruturas:** `SceAudioencInitParam` (acima).
- **Implementação no VitaSharp:** `Audioenc.cs`; espelhar `sceAudiodecInitLibrary` + checagem de duplicidade.
- **Validação:** teste unitário.

### sceAudioencTermLibrary
- [x] **Prioridade:** P2 · **Biblioteca:** SceAudioencUser · **NID:** 0xAB32D022
- **Protótipo:** `int sceAudioencTermLibrary(unsigned int codecType);` (`psp2/audioenc.h`)
- **Comportamento:** libera a biblioteca do tipo.
- **Retorno/erros:** 0; `INVALID_TYPE`; `NOT_INITIALIZED` (0x80860005); com encoders vivos → `A_HANDLE_IN_USE`
  (0x80860006) (a confirmar).
- **Implementação no VitaSharp:** `Audioenc.cs`; espelhar `sceAudiodecTermLibrary`.
- **Validação:** teste unitário.

### sceAudioencCreateEncoder
- [x] **Prioridade:** P2 · **Biblioteca:** SceAudioencUser · **NID:** 0x64C04AE8
- **Protótipo:** `int sceAudioencCreateEncoder(SceAudioencCtrl *pCtrl, int codecType);` (`psp2/audioenc.h`)
- **Comportamento:** valida `pCtrl->size == 0x2C`, `wordLength == 16`, `pInfo` (size 0x10, `excitationMode == 0`,
  `samplingRate == 8000`, `bitRate` na tabela); cria o encoder, escreve `handle`, `maxPcmSize` (= amostras do
  frame × 2) e `maxEsSize` (= bytes do frame).
- **Retorno/erros:** 0; `INVALID_PTR` (ctrl/pInfo nulos); `INVALID_SIZE` (0x8086000D) se `size` errado;
  `INVALID_TYPE`; `NOT_INITIALIZED` sem InitLibrary; `INVALID_WORD_LENGTH` (0x8086000C);
  `CELP_ERROR_INVALID_CONFIG` (0x80861001) para modo/rate/bitrate inválidos; `ALL_HANDLES_IN_USE` (0x80860007) além de
  `totalStreams`.
- **Estruturas:** `SceAudioencCtrl`, `SceAudioencInfoCelp`.
- **Implementação no VitaSharp:** `Audioenc.cs`; `Create(ctrl, type, needsLibrary: true)` no estilo de
  `Audiodec.Create`.
- **Validação:** teste unitário para cada bitrate conhecido (tabela da Arquitetura).

### sceAudioencEncode
- [x] **Prioridade:** P2 · **Biblioteca:** SceAudioencUser · **NID:** 0xD85DB29C
- **Protótipo:** `int sceAudioencEncode(SceAudioencCtrl *pCtrl);` (`psp2/audioenc.h`)
- **Comportamento:** codifica um frame: lê `FrameSamples` s16 de `pInputPcm`, escreve `FrameBytes` em `pOutputEs`,
  atualiza `inputPcmSize` e `outputEsSize`.
- **Retorno/erros:** 0; erros de `Find` (INVALID_PTR, INVALID_SIZE, INVALID_HANDLE 0x80860009, NOT_HANDLE_IN_USE
  0x8086000A); ponteiros de buffer nulos → INVALID_PTR; alinhamento de buffers → `INVALID_ALIGNMENT` (0x8086000E;
  requisito exato a confirmar — o Audiodec da Vita exige ES/PCM alinhados? não validado em `Audiodec.cs`).
- **Implementação no VitaSharp:** `Audioenc.cs`; `GuestMemory.Span(pOutputEs, FrameBytes).Clear()`; W32 dos tamanhos.
  `Log.WarnOnce("audioenc-celp", "CELP encode emits silent frames")`.
- **Validação:** teste unitário.

### sceAudioencEncodeNFrames
- [x] **Prioridade:** P2 · **Biblioteca:** SceAudioencUser · **NID:** 0xAFCEDA50
- **Protótipo (inferido de `sceAudiodecDecodeNFrames`):** `int sceAudioencEncodeNFrames(SceAudioencCtrl *pCtrl, SceUInt32 nFrames);`
- **Comportamento:** como Encode para `nFrames` frames consecutivos; tamanhos de saída acumulados.
- **Retorno/erros:** como Encode; `nFrames == 0` → a confirmar (Audiodec.cs trata como 1).
- **Implementação no VitaSharp:** `Audioenc.cs`; loop sobre a mesma rotina (padrão de `Audiodec.DecodeFrames`).
- **Validação:** teste unitário com 3 frames: `outputEsSize == 3*FrameBytes`.

### Família: variantes External/Resident, contexto e consultas

Protótipos das linhas marcadas "(inferido)" seguem `psp2/audiodec.h` (`sceAudiodecGetContextSize`,
`sceAudiodecCreateDecoderExternal`, `sceAudiodecDeleteDecoderExternal`). As variantes *Resident* não têm análogo
no header do Audiodec; assinatura inferida igual à normal (a confirmar por desmontagem: contar argumentos usados). Erros
iguais aos de Create/Delete. Implementação em `Audioenc.cs`; validação por teste unitário.

| Função | NID | Protótipo | Comportamento |
| --- | --- | --- | --- |
| - [x] sceAudioencGetContextSize | 0xEAD4AF38 | `int sceAudioencGetContextSize(SceAudioencCtrl *pCtrl, SceUInt32 codecType);` (inferido) | Retorna o nº de bytes de contexto que o app deve alocar para um encoder External com essa configuração (`pInfo` lido). Valor real a confirmar; usar 0x10000 como `Audiodec.cs`. |
| - [x] sceAudioencCreateEncoderExternal | 0x9B1E8DE2 | `int sceAudioencCreateEncoderExternal(SceAudioencCtrl *pCtrl, SceUInt32 codecType, SceUIntVAddr vaContext, SceUInt32 contextSize);` (inferido) | Como Create, mas usa memória do app e não exige InitLibrary (`needsLibrary: false`, como Audiodec). `vaContext` nulo → INVALID_PTR; `contextSize` < GetContextSize → INVALID_SIZE (a confirmar). |
| - [x] sceAudioencCreateEncoderResident | 0xDD13004C | `int sceAudioencCreateEncoderResident(SceAudioencCtrl *pCtrl, SceUInt32 codecType);` (inferido) | Como Create, encoder "residente" (memória reservada do sistema); HLE = Create com `Resident = true`. Se exige InitLibrary: a confirmar. |
| - [x] sceAudioencDeleteEncoder | 0xC6BA5EE6 | `int sceAudioencDeleteEncoder(SceAudioencCtrl *pCtrl);` (`psp2/audioenc.h`) | Valida ctrl/size e remove o handle (NOT_HANDLE_IN_USE se não existe). |
| - [x] sceAudioencDeleteEncoderExternal | 0x552E8882 | `int sceAudioencDeleteEncoderExternal(SceAudioencCtrl *pCtrl, SceUIntVAddr *pvaContext);` (inferido) | Remove o handle e escreve em `*pvaContext` o endereço do contexto passado no Create (para o app liberar). Obs.: `Audiodec.cs` declara o 2º arg como valor, não ponteiro — não repetir aqui. |
| - [x] sceAudioencDeleteEncoderResident | 0x80765849 | `int sceAudioencDeleteEncoderResident(SceAudioencCtrl *pCtrl);` (inferido) | Remove handle residente. |
| - [x] sceAudioencClearContext | 0x9386F42D | `int sceAudioencClearContext(SceAudioencCtrl *pCtrl);` (`psp2/audioenc.h`) | Reinicia o estado do encoder (no HLE: valida handle e retorna 0). |
| - [x] sceAudioencGetOptInfo | 0xD01C63A3 | `int sceAudioencGetOptInfo(SceAudioencCtrl *pCtrl);` (`psp2/audioenc.h`) | Preenche `*pCtrl->pOptInfo` (`SceAudioencOptInfoCelp`, size 0x2C): `header[32]`, `headerSize`, `encoderVersion`. Conteúdo do header CELP e versão a confirmar (hardware: chamar após Create para cada bitrate e despejar). HLE inicial: header zerado, `headerSize = 0`, `encoderVersion = 0`; `pOptInfo` nulo → INVALID_PTR; `size != 0x2C` → INVALID_SIZE. |
| - [x] sceAudioencGetInternalError | 0x452246D0 | `int sceAudioencGetInternalError(SceAudioencCtrl *pCtrl, int *pInternalError);` (`psp2/audioenc.h`) | Valida handle; escreve 0 em `*pInternalError` (igual a `sceAudiodecGetInternalError`). |
