# Plugins (de kernel `.skprx` e de usuário `.suprx`)

*English: [plugins.md](plugins.md).*

O VitaSharp carrega os plugins que um Vita com homebrew carrega, pela mesma configuração de plugins, e roda o código
original deles: nada no emulador é escrito para um plugin ou homebrew específico, e nenhum plugin é trocado por uma
imitação feita no emulador.

## Como configurar

Copie os plugins do seu próprio Vita para a pasta de dados (`ur0:tai` é `fs/ur0/tai` nela) e liste-os em
`fs/ur0/tai/config.txt` (ou `fs/ux0/tai/config.txt`, que vale quando os dois existem), o arquivo que o taiHEN lê no
Vita:

```
# plugins de kernel: carregados no kernel e iniciados quando um programa inicia
*KERNEL
ur0:tai/algum_plugin_de_kernel.skprx
# plugins de usuário para todo programa
*ALL
# plugins de usuário para um título
*PCSE00000
ux0:tai/algum_plugin.suprx
```

Sem `config.txt` nenhum plugin é carregado.

O arquivo é lido com as regras do próprio taiHEN (o parser dele, taihen-parser, e o `plugin.c`/`hen.c` do taiHEN): só
um arquivo vale (`ux0:tai/config.txt`, senão `ur0:tai/config.txt`); `#` começa um comentário, `*` uma seção, e
qualquer outra linha é o caminho de um módulo (sem os espaços do começo e do fim); os nomes de seção são comparados
exatos, com maiúsculas e minúsculas (`*main` é o shell, os outros nomes são IDs de título); `*ALL` vale para todo
programa, mas não para `*KERNEL`; uma seção escrita `*!NOME` que bate termina a leitura na próxima seção; uma linha de
256 caracteres ou mais, um caminho antes da primeira seção ou uma seção sem nome faz o taiHEN recusar o arquivo
inteiro, e aí nenhum plugin carrega. Os módulos de uma seção carregam na ordem do arquivo, e um que falha não impede
os outros.

## O que acontece

- Os módulos de `*KERNEL` são carregados na região do kernel (uma área que todo processo mapeia, do processo do kernel)
  e o `module_start` deles roda na hora numa thread de kernel; um módulo cujo início não informa sucesso é descarregado
  de novo, como faz o gerenciador de módulos do firmware. O log diz o que aconteceu com cada um
  (`kernel plugin ...: loaded and started`, `... module_start of X returned 0x2, unloaded`).
- Os módulos de `*KERNEL` carregam uma vez, com o primeiro processo da sessão (o shell quando o sistema inicia, como o
  taiHEN os carrega no boot); um processo depois os encontra carregados.
- Os módulos de `*ALL` e `*TITLEID` são carregados no processo de todo programa, também nos do próprio sistema (o taiHEN
  os carrega no gancho de `sceKernelStartPreloadedModules`, depois dos módulos pré-carregados do processo), e iniciam
  antes da thread principal. A seção é o ID de título do processo no kernel: `main` para o shell, o NPXS de um app do
  sistema.
- Um módulo que importa uma biblioteca que ninguém fornece (nenhum módulo carregado e nenhuma biblioteca do firmware que
  o VitaSharp implementa) não é iniciado; o log diz quais bibliotecas (`not started: no module provides ...`).
- Os módulos de kernel chamam as bibliotecas de kernel do firmware (SceSysmemForDriver, SceThreadmgrForDriver,
  SceModulemgrForKernel, SceCpuForDriver...): o VitaSharp as implementa com o comportamento e os códigos de erro do
  firmware. Programas de usuário só chegam a um módulo de kernel pelas bibliotecas exportadas como syscall; módulos de
  kernel nunca se ligam a código de usuário.
- A CPU emulada não tem níveis de privilégio, então a checagem de permissão das cópias usuário/kernel é feita pela
  biblioteca do kernel: o intervalo do usuário tem de ser memória do próprio chamador, não memória do kernel.

## O taiHEN, e o que precisa dele

No Vita, o taiHEN (`taihen.skprx`) é carregado primeiro pela cadeia de boot do homebrew e fornece o carregador de
plugins e a API de hooks que os outros plugins importam (`taihenForKernel`, `taihenModuleUtils`) e que os homebrews
chamam (`taihen`, `taihenUnsafe`). Listado no `config.txt`, o VitaSharp carrega o `taihen.skprx` real como qualquer
módulo de kernel: as importações dele se ligam às bibliotecas de kernel do firmware e o próprio `module_start` dele
roda. Ele então falha sozinho (devolve SCE_KERNEL_START_FAILED), porque o início dele instala patches nas checagens de
assinatura e de DRM do firmware, que o VitaSharp não faz funcionar, e é descarregado.

Por isso nada que precise do taiHEN roda, como num Vita sem ele:

- plugins de kernel feitos sobre o taiHEN, como o **kubridge** e o **fd_fix** (`not started: no module provides
  taihenForKernel`);
- homebrews que chamam a API de usuário do taiHEN ou precisam de um desses plugins, entre eles o **Dead Space** (o port
  de Android com so_loader precisa do kubridge) e o port do **Sonic Mania** (as buscas de módulo do taiHEN).

## Limites

- Os módulos de kernel vivem com o processo do programa para o qual foram carregados (o kernel ainda não é um processo
  único do sistema); são descarregados quando esse processo termina.
- Um plugin que corrige por deslocamento o código do próprio firmware não acha código do firmware ali (o VitaSharp
  implementa ele mesmo as bibliotecas do firmware).
- Exceções não são entregues a handlers de kernel (a CPU emulada relata uma falha do guest como queda); o mascaramento
  de interrupções é só estado (não há controlador de interrupções).
