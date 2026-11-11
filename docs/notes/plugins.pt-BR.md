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

## O que acontece

- Os módulos de `*KERNEL` são carregados na região do kernel (uma área que todo processo mapeia, do processo do kernel)
  e o `module_start` deles roda na hora numa thread de kernel; um módulo cujo início não informa sucesso é descarregado
  de novo, como faz o gerenciador de módulos do firmware. O log diz o que aconteceu com cada um
  (`kernel plugin ...: loaded and started`, `... module_start of X returned 0x2, unloaded`).
- Os módulos de `*ALL` e `*TITLEID` são carregados no processo do programa e iniciam antes da thread principal.
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
