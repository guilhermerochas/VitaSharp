# PlayStation Mobile: censo do HLE dos runtimes

Tudo o que os módulos do runtime de PSM importam e quem fornece cada função no VitaSharp. Fonte: as tabelas de import
de cada módulo do `PCSI00011` instalado (`ux0:app/PCSI00011` 1.00 e a atualização `ux0:patch/PCSI00011` 2.01:
`eboot.bin`, `sce_module/*.suprx`, `module/*.suprx`; só leitura), medidas com
`VitaSharp.Desktop imports PCSI00011 --tsv=<arquivo>` (uma linha por função: módulo, biblioteca, NID, nome e quem
fornece, pelas mesmas regras do carregador). Firmware instalado: 3.74. O runtime Unity (`PCSI00010`, em
`ux0:psm/<id>/runtime` de cada título Unity) continua cifrado no PFS do título e não entra no censo (B5 cuida da
decifração).

Categorias (as do `hle-coverage`): **HLE** função real; **default** valor documentado do firmware para o que o
aparelho emulado não tem; **LLE do runtime** um módulo do próprio pacote exporta (libc, libm, libstdcxx, fios2, ult,
mono, mono_bridge, psm, shacccg); **LLE do firmware** o HLE não conhece a biblioteca e um módulo do firmware a exporta
(o carregador carrega esse módulo); **falta** ninguém fornece.

## Resumo (união 1.00 + 2.01, NIDs distintos)

| | HLE | default | LLE do runtime | LLE do firmware | falta | total |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| antes (main 6b52a109) | 486 | 6 | 542 | 19 | 9 | 1062 |
| depois (feat/psm-hle) | 491 | 13 | 542 | 14 | 2 | 1062 |

Por módulo (2.01): `eboot.bin` 122 imports, `libc` 56, `libfios2` 66, `libmono_bridge` 173, `libmono` 376,
`libpsm` 494, `libshacccg` 27, `libult` 26.

## O que não era HLE real antes

| Biblioteca | NID | Função | Quem importa | Antes | Depois |
| --- | --- | --- | --- | --- | --- |
| SceSysmem | 0xEC636BCB | sceKernelAllocUnmapMemBlock | eboot | falta | HLE |
| SceAppMgrUser | 0x9D6D8058 | (sem nome) | eboot 1.00 | falta | default: nenhum módulo do FW 3.74 exporta; o stub do próprio módulo devolve -1 |
| SceAppUtilPsm | 0xDEB51A38 | (sem nome) | libpsm | LLE do firmware (apputil.suprx inteiro) | HLE |
| SceLibTargetTransport | 0x11EBD107, 0x1AD7239E, 0x4CF1664B, 0x4F644C0F, 0x9D3FC23C, 0xD45C74BE | (sem nome) | libmono_bridge | falta | default: biblioteca só do devkit; o stub do módulo devolve -1 |
| SceIpmi | 0xB282B430 | IPMI::Client::create | eboot (não chama), libpsm | falta | falta (em estudo: cliente do serviço "SceDownload" do shell) |
| ScePsmDrm | 0xE31A6220 | scePsmDrmGetRifInfo | libmono_bridge, libpsm, psmkdc | default | HLE (PSM-B, feat/psm-hle-14) |
| ScePsmDrm | 0xA89653B3 | scePsmDrmGetActInfo | idem | default | HLE (PSM-B) |
| ScePsmDrm | 0x207A2C53 | scePsmDrmGetRifKey | idem | default | HLE (como o SceNpDrm 3.74: só o runtime de PSM; sem ativação 0x808F0106; a chave só vem de um plugin de kernel que faz hook da exportação) |
| ScePsmDrm | 0x0E193CBB | scePsmDrmRemoveActData | idem | default | HLE (PSM-B) |
| ScePsmDrm | 0x3E881391, 0x0D6470DA | scePsmDrmGetRifName(ForInstall) | idem | default | HLE (nome AES do SceNpDrm; ForInstall com a checagem da licença, a parte da ativação é do processador seguro) |
| SceDrmPsmKdc | 0x7EE0F1BC, 0xC16EDCD9, 0xE768E9D1, 0xF562D06D | (sem nome) | eboot | LLE do firmware (psmkdc.suprx) | idem (sceSysmoduleLoadModuleInternal é privilegiado: qualquer chamador carrega o psmkdc) |
| SceFt2 | 14 NIDs (FT_*) | FreeType | libpsm | LLE do firmware (libSceFt2.suprx) | idem |

Os módulos do firmware carregados por LLE importam, por sua vez: `psmkdc.suprx` usa SceHttp, ScePsmDrm (acima) e
`sceSblGcAuthMgrPsmactCreateC1`/`VerifyR1` (default: a ativação de PSM do processador seguro); `libSceFt2.suprx` só
SceLibKernel/SceIofilemgr (HLE).

## Como o console faz (FW 3.74, módulos do firmware instalado, só estudo)

- **Título de PSM do processo.** O runtime chama `sceAppMgrInitSafeMemoryById(const char *id)` (SceAppUtilPsm
  0xDEB51A38 é o envoltório do apputil, com os erros dele). O appmgr só aceita um runtime de PSM (0x80800009), uma vez
  (0x8080004A); o runtime do pacote passa o ID do título (4 maiúsculas + 5 dígitos, senão 0x80800001), um runtime que
  o título traz passa um nome `[0-9A-Za-z_-]{1,63}`. O ID fica nas informações do processo (`AppMgr.PsmTitleId` no
  VitaSharp, o único lugar) e a memória segura passa para `ux0:psm/<id>/RW/System/pm.dat`
  (`ux0:cache/<title id>/<nome>/System/pm.dat`): um pm.dat de 64 KiB é lido para a área, senão é criado a partir dela;
  a pasta tem de existir (0x80800049).
- **Montagens 106/107.** `sceAppMgrAppDataMountById(106|107, id, mp)` exige um ID de PSM e o ponto de montagem
  (0x80800001) e cria a pasta `ux0:psm/<id>/RO|RW` se faltar, antes de montar.
- **0x9D6D8058.** Só o runtime 1.00 (SDK 2.00) importa; o FW 3.74 não exporta (o `driver_us` não tem esse NID). O
  carregador deixa o stub do módulo (`mvn r0, #0; bx lr`), a chamada devolve -1, a montagem 106 recebe um ID vazio e
  falha: o 1.00 não roda títulos no 3.74, por isso o shell pede a atualização do runtime.
- **ScePsmDrm** (implementação no PSM-B, feat/psm-hle-14; revalidada contra o SceNpDrm 3.74 no fix/psm4). O PSM tem
  ativação própria (`tm0:/psmdrm/act.dat`, "PSM-ACT", assinada e presa à conta e a um resumo do aparelho); o aparelho
  emulado não tem: `GetActInfo` responde 0x808F0106. As syscalls do usuário passam os argumentos como estão para as
  exportações `kscePsmDrm*` (pela entrada da exportação: um plugin de kernel que faz hook delas responde no lugar,
  como no console). As três funções de licença (`GetRifNameForInstall`, `GetRifInfo`, `GetRifKey`) fazem a mesma
  checagem, nesta ordem: assinatura RSA-2048 da licença (SHA-256 dos 0x300 primeiros bytes, assinatura em +0x300,
  chave 5 do módulo lida do firmware instalado do usuário em tempo de execução, nunca no repositório: 0x808F0104),
  cabeçalho ("PSM-RIF\0" versão 1: 0x808F0105), a palavra em +0x18 (diferente de zero: 0x808F0109), as palavras em
  +0x1C e +0xC zeradas e início <= fim (0x808F0105), a conta da ativação carregada (0x808F0108) e o resumo dela
  (0x808F0109). Com `act.dat` presente a parte da ativação é do processador seguro (0x80020004); sem ele o módulo
  não tem ativação carregada (conta 0, resumo zerado). Sem a chave do firmware (outra versão, sem firmware):
  0x80020004.
  - `GetRifInfo(licença, contentId, conta, início, fim)`: 5 argumentos (início e fim em ponteiros separados). NULL na
    licença 0x808F0101. A resposta é a da checagem. Com 0x808F0104 (assinatura que não confere, o caso das licenças
    sem assinatura dos plugins de licença) **nada é preenchido**; com qualquer outra resposta, uma licença com a
    marca "PSM-RIF" tem os campos escritos nos ponteiros não NULL: ID do conteúdo (+0x50), conta (+0x10, big-endian)
    e os tempos (+0x20/+0x28, ms desde 1970 em big-endian) como ticks do SceRtc: `v*1000 + 0xDCBFFEFF2BC000`, com 0
    virando 0x7FFFFFFFFFFFFFFF ("nunca").
  - `GetRifKey(licença, chaves, versão, início, fim)`: a syscall só atende o runtime de PSM
    (`ksceSblACMgrIsPSMRuntime`, senão 0x80010058). No kernel: NULL na licença ou nas chaves 0x808F0101; sem
    ativação carregada 0x808F0106, antes de ler a licença; depois a checagem; o conjunto de chaves de uma licença que
    passa é decifrado com as chaves da ativação e os tempos limitados aos dela (trabalho do processador seguro:
    0x80020004). O VitaSharp não entrega a chave por conta própria: um título de PSM só abre com um plugin de kernel
    que faz hook de `kscePsmDrmGetRifKey` carregado (opção "Ativar Plugins do Kernel"), como no console.
  - O leitor de arquivos PSSE do runtime (`pss_crypto_open`, HLE da mono_bridge) pega a licença do conteúdo em
    `RO/License` e pede a chave a `scePsmDrmGetRifKey`, como o runtime faz no console: sem a chave dela, 0x80870005.
- **Nome da licença.** O libpsm 2.01 lê a conta do registro (`sceRegMgrSystemParamGetBin(0x450F32)`,
  `/CONFIG/NP/account_id`, 0 sem conta), pede `scePsmDrmGetRifName(conta)` e abre `RO/License/<nome>`; o nome é a
  cifra AES (chave do SceNpDrm) de {flag 0, conta}. O instalador de PSM grava a licença com esse nome e a migração
  `PsmLicenseNameMigration` renomeia as gravadas antes com o nome fixo. `scePsmDrmGetRifNameForInstall` (função
  interna do SceNpDrm atrás da syscall) confere a assinatura RSA-2048 da licença (SHA-256 dos 0x300 primeiros bytes,
  chave 5 do módulo, lida do firmware instalado: 0x808F0104), o cabeçalho (0x808F0105) e então a ativação carregada
  (conta 0x808F0108, resumo 0x808F0109): com `tm0:psmdrm/act.dat` presente essa parte é do processador seguro
  (0x80020004); sem ele o módulo não tem ativação e responde como nesse caso. A conta de uma licença da loja é a da
  ativação, logo o nome de instalação é o mesmo que o runtime pede. Fonte: SceNpDrm 3.74 e libpsm/eboot do PCSI00011
  2.01 (só estudo).

## Falta

- `IPMI::Client::create` (libpsm, serviço "SceDownload" do shell): verificar se o servidor do shell sobe no VitaSharp
  e ligar o cliente do firmware a ele pelo IPC do kernel.
- Runtime Unity (`PCSI00010`): censo depois que o PFS do título puder ser lido (B5).
