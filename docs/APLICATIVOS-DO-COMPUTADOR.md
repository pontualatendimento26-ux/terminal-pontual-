# Aplicativos do computador (Windows, instalados e baixados)

Decisão de 20/11/2026, pedido do dono: "quero que as atendentes tenham acesso aos aplicativos do Windows e aos baixados, e quando entrar a primeira vez mande uma solicitação pedindo para a Central de TI liberar". Segue o mesmo modelo dos [sites nos navegadores](CHROME.md): o Terminal **deixa abrir** e **avisa**; o TI decide na Central, aba **Aplicativos**.

## O que a atendente vê

- Na tela inicial do Terminal (com servidor), o botão **Aplicativos**, ao lado de **Arquivos**, abre uma folha com três grupos: **Do Windows** (Calculadora, Bloco de Notas, Paint, Ferramenta de Captura, WordPad, Mapa de Caracteres, Notas Autoadesivas, os que existirem no computador), **Instalados neste computador** (atalhos `.lnk` do menu Iniciar, de todos os usuários e da conta dela) e **Baixados** (arquivos `.exe` na pasta Downloads).
- Cada aplicativo mostra a situação: *Liberado pelo TI*, *Bloqueado pelo TI* (não dá para clicar) ou *Na primeira vez, o TI recebe um pedido para liberar*.
- Ao abrir um aplicativo novo, ele abre e aparece o aviso "foi aberto. O TI recebeu um pedido para liberar este aplicativo". Ao tentar um bloqueado: "O TI bloqueou X neste computador". Se o TI bloquear um aplicativo que está aberto, o Terminal pede para ele fechar e, se em cinco segundos continuar aberto, encerra.
- O aplicativo aberto entra em **Abertos agora** (voltar e fechar) e no "em uso" que a Central mostra.

## Como funciona

1. O Terminal varre o computador quando a folha é aberta (`AppCatalog`, numa linha fora da interface): atalhos do menu Iniciar resolvidos pela interface `IShellLink` do Windows, lista fixa de aplicativos do Windows em `System32`, aplicativos da Loja sem lançador (`StoreApps.InstalledWithoutLauncher`: o executável do pacote registrado para a conta, achado pelo registro do usuário e pelo `AppxManifest.xml`), e `.exe` da pasta Downloads. Entram só caminhos completos de `.exe` que existem; ficam de fora atalhos de desinstalação e tudo o que `AppPolicy.IsRefused` recusa (ver abaixo).
2. O Terminal abre o aplicativo direto (`CreateProcess`, sem passar pelo Explorador), por isso a lista `RestrictRun` do bloqueio da conta não o impede. Isso é de propósito: o controle passa a ser a decisão do TI.
3. A decisão é por **nome do arquivo** (`spotify.exe`, em minúsculas), porque o caminho muda de um computador para outro (`C:\Users\<conta>\...`). O veredito (`AppPolicy.Decide`):
   - **Bloqueado** (`appsBlocked` da configuração): não abre; a tentativa vai para a Central.
   - **Liberado**: programa cadastrado para a máquina (mesmo caminho) ou nome em `appsAllowed`: abre e gera só o evento `opened` comum.
   - **Pedido**: tudo o mais abre e gera o evento `app_opened` (`item` = nome, `detail` = caminho).
4. Na Central, `app_opened` vira um pedido **dela** (tabela `DeviceApps`, um por computador e `.exe`), com alerta urgente ("Abriu o aplicativo spotify.exe, que espera a decisão do TI") e notificação ("Gabi (PC-01) abriu o aplicativo Spotify (spotify.exe) pela primeira vez. Decida se ela pode usar."). Abrir de novo soma **Vezes**, sem segundo alerta.
5. O TI escolhe **Vale para** ("Só <atendente>" ou "Todas as atendentes") e **Liberar** ou **Bloquear**. Decidir para todas (tabela `AppDecisions`) apaga os pedidos esperando daquele `.exe` em todas as atendentes e resolve os alertas. A decisão de uma atendente vale mais que a de todas; em **Já decididos**, decisões "Só <atendente>" têm o botão **Tirar** (`decision: "reset"`).
6. Tentativa num aplicativo **bloqueado para todas** (sem decisão só para ela) também vira pedido dela, com o selo "Bloqueado para todas: ela pediu"; o aplicativo continua bloqueado até o TI decidir. Bloqueado **só para ela**, a tentativa fica só no histórico.
7. Cada Terminal recebe em `GET /device/config` as listas `appsAllowed` e `appsBlocked` já calculadas para o computador dele (`AppEndpoints.DecidedAsync`). A configuração muda de versão a cada decisão, então o Terminal aplica em até 15 segundos.

## Rotas

| Rota | Permissão | Corpo / resposta |
|---|---|---|
| `GET /device-apps` | `apps.read` | `[{ exe, name, path, status, scope, deviceId, deviceLabel, opens, firstAt, lastAt, decidedBy, decidedAt, blockedForAll }]`; os `pending` primeiro |
| `POST /device-apps/decide` | `apps.executables.approve` | `{ exe, decision: "allow" \| "block" \| "reset", deviceId?, name? }`. `exe` aceita o nome ou um caminho; `reset` só com `deviceId` |

Liberar `cmd.exe`, `powershell.exe` e os demais recusados do contrato dá 400; bloqueá-los pode (é só reforço).

## O que nunca abre pela lista

Além dos recusados do contrato (`cmd`, `powershell`, `pwsh`, `wscript`, `cscript`, `mshta`, `regedit`, `rundll32`, `msiexec`, `bash`, `wsl`, `wt`), o Terminal não lista nem abre ferramentas de administração do Windows (`taskmgr`, `control`, `mmc`, `msconfig`, `gpedit`, `services`, `eventvwr`, `netsh`, `reg`, `sc`, `schtasks`, `certutil`, `bitsadmin`, `diskpart`, `wmic`, `dism`, `bcdedit`, `icacls`, `runas`, `psexec`, `net`, `shutdown`, `explorer`, `sethc`, `utilman`, entre outros), nem interpretadores e ferramentas de desenvolvedor (`git-bash`, `python`, `node`, `java`, `ssh`, `curl`…; lista completa em `AppPolicy.RefusedNames`), nem o próprio Terminal. Atalhos do menu Iniciar cujo destino fica na pasta do Windows (Ferramentas Administrativas, Sistema do Windows, Acessibilidade) também ficam de fora; da pasta do Windows só entram os aplicativos da lista fixa. Mesmo liberados pelo TI, continuam recusados. Se o TI precisar de um deles numa máquina, cadastra como programa em **Sites e programas**.

## Calculadora própria do Terminal (20/11/2026)

Como a Calculadora da Loja não abria na conta bloqueada, o dono pediu uma calculadora dentro do Terminal. Ela fica na tela inicial (botão **Calculadora**, ao lado de Aplicativos), em todos os modos, inclusive o local. Quatro operações e porcentagem, vírgula como decimal, teclado e mouse (Enter = igual, Backspace apaga, Esc fecha). A conta é lida por um analisador próprio em `app.js` (`avaliarCalc`), sem `eval`. Por ser parte da tela servida pela Central, chega a todos os Terminais ao publicar a Central, sem atualizar o programa.

**Janela própria (21/11/2026).** O dono pediu que ela se comportasse como a Calculadora do Windows: mover para o canto, mudar de tamanho e continuar na tela ao trocar de aba. Desde o Terminal 2.11.0, o botão **Calculadora** manda `{type:"calc"}` ao programa (a página sabe que pode porque `window.PONTUAL.calc` é `true`), e o Terminal abre uma janela comum do Windows (`CalculatorWindow`, em `MainWindow.Calculator.cs`: barra de título, redimensionável, sem aparecer na barra de tarefas) com um WebView2 na mesma página em modo calculadora, `?calc=1`. Nesse modo `app.js` mostra só o visor e as teclas, ocupando a janela toda, e não liga nada com o servidor. Esc ou o X só escondem a janela (`calc-close`), então a conta não se perde; ela fecha de verdade junto com o Terminal. Nos Terminais anteriores à 2.11.0, no celular e no navegador, a calculadora continua como folha sobre a tela inicial.

## Microsoft Store e aplicativos da Microsoft (23/11/2026)

Pedido do dono: "todas as atendentes usam os aplicativos da microsoft store, libere o acesso a todos os apps da microsoft e acesso a microsoft store".

- A folha **Aplicativos** ganhou o grupo **Microsoft Store**: todos os aplicativos da Loja registrados para a conta da atendente (`StoreApps.InstalledPackagedApps`: registro do usuário `AppModel\Repository\Packages` + `AppxManifest.xml`, versão mais nova de cada pacote, nome lido do pacote com `SHLoadIndirectString`), a própria **Microsoft Store** entre eles. Ficam de fora os que não aparecem no menu Iniciar (`AppListEntry="none"`), os pacotes de base e de recursos, os pacotes do sistema (pasta do Windows, como as Configurações) e os de `StoreApps.ExcludedFamilies` (Terminal do Windows, PowerShell, Linux/WSL, Python, Instalador de Aplicativo/winget, Assistência Rápida, Área de Trabalho Remota, Windows 365, Dev Home, PowerToys e Paleta de Comandos, WinDbg, Segurança do Windows). Os nomes de `.exe` desses também estão em `AppPolicy.RefusedNames`.
- Os aplicativos da Loja abrem pelo gerenciador de ativação do Windows, pelo identificador do aplicativo (`AppEntry.Aumid`), como a Calculadora.
- **Os da Microsoft abrem liberados**, sem pedido: os pacotes da Loja dos editores Microsoft (`8wekyb3d8bbwe`) e Skype (`kzf8qxf38zg5c`), e os programas clássicos de `AppPolicy.MicrosoftExes` (Word, Excel, PowerPoint, Outlook, OneNote, Access, Publisher, Visio, Project, Teams, Outlook novo, OneDrive) quando o atalho aponta para Arquivos de Programas. Se o TI bloquear um deles na Central, o bloqueio vale mais.
- Os aplicativos da Loja de **outras empresas** (WhatsApp, por exemplo) também abrem e, na primeira vez, viram pedido ao TI, como qualquer aplicativo instalado.
- O que a atendente instala pela Loja entra na lista na próxima vez que ela abrir a folha Aplicativos.
- A `RestrictRun` do bloqueio inclui os `.exe` do Office, do Teams, do Outlook novo, do OneDrive e da Loja, para abrirem também por arquivo no Explorador. Ela é regravada quando o Terminal é atualizado.

## Notas Autoadesivas sem acesso (23/11/2026, Terminal 2.12.1)

O dono avisou: "as atendentes não estão conseguindo o acesso às notas adesivas". Na Central, nenhum dos seis computadores tinha "Abriu Notas Autoadesivas" no histórico, só o Bloco de Notas. Até a 2.12.0, quando o pacote não estava registrado para a conta, as Notas simplesmente não apareciam na lista, e uma falha ao abrir só mostrava um aviso na tela, sem avisar a Central.

- Se o pacote das Notas não estiver registrado para a conta da atendente, o Terminal o registra sozinho, uma vez por sessão, ao varrer a lista: `Add-AppxPackage -RegisterByFamilyName -MainPackage Microsoft.MicrosoftStickyNotes_8wekyb3d8bbwe`, escondido, sem administrador. É o mesmo registro que o Windows faz no primeiro logon e usa a cópia que já está no computador. Conferido neste PC: registra e a lista passa a ter as Notas.
- Se não der (o pacote não está no computador, por exemplo), o Terminal manda o evento `app_failed` (item = nome, detail = mensagem do Windows). Na Central ele vira histórico "Não conseguiu abrir Notas Autoadesivas: <motivo>" e um alerta. A solução, nesse caso, é a atendente instalar as Notas pela Microsoft Store, que agora fica na lista.
- Qualquer aplicativo da lista que não abrir também gera `app_failed` com o motivo, e a atendente vê "O TI foi avisado".

## Por que nenhum aplicativo da Loja abria, e o Explorer escondido (23/11/2026, Terminal 2.13.0)

O dono: "nada relacionado a microsoft store está abrindo". O que os dados da Central mostraram:

- Nenhum aplicativo da Loja abriu nenhuma vez em nenhuma conta bloqueada desde 20/11. A Calculadora sempre teve "Abriu" e "Fechou" no mesmo segundo, sem janela. O "Teams" do histórico é o site cadastrado, não o aplicativo.
- Às 12:40 a Kamila (2.12.1) tentou as Notas Autoadesivas e a Microsoft Store: o gerenciador de ativação recusou as duas com **HRESULT 0x87B20C15**, e o `.exe` do pacote também não abre direto. As Notas já estavam registradas na conta dela: não era registro.
- A causa é o próprio bloqueio. Ele troca a área de trabalho (Explorer) pelo Terminal, e a Microsoft diz que "não é possível usar um shell personalizado para abrir aplicativos Universais do Windows" ([Shell Launcher, IoT Enterprise](https://learn.microsoft.com/pl-pl/windows/iot/iot-enterprise/customize/shell-launcher)). Só o Shell Launcher v2 abre esses aplicativos, e ele não existe no Windows Pro dos computadores.

A solução escolhida pelo dono foi o **Explorer escondido**, ligado computador por computador:

- Na Central, em cada computador, a chave **Aplicativos da Microsoft Store** (começa desligada; `Devices.StoreApps`, `PATCH /devices/{id}` com `storeApps`, vai na auditoria). O Terminal recebe em `GET /device/config` (`storeApps`).
- Ligada, o Terminal (2.13.0+, só no modo bloqueado) liga o `explorer.exe` sem pasta, para ele virar a área de trabalho (`MainWindow.StoreShell.cs`). A cada 300 ms esconde a barra de tarefas de todos os monitores (`Shell_TrayWnd`, `Shell_SecondaryTrayWnd`) e devolve a faixa da barra do Terminal. As teclas Windows e Ctrl+Esc, que abririam o Iniciar, são seguradas por um gancho de teclado. Win+R continua desligado pela política `NoRun`, e o Gerenciador de Tarefas pela `DisableTaskMgr`.
- O resultado vai para o histórico do computador (evento `store_shell`): "Aplicativos da Microsoft Store: o Explorer está rodando escondido…" ou "o Windows não aceitou o Explorer como área de trabalho nesta conta…". Nesse segundo caso, a janela de pastas que abrir é fechada.
- Desligar a chave vale a partir da próxima entrada da atendente (o Explorer não é fechado no meio da sessão).
- Com a chave desligada, ao tentar um aplicativo da Loja a atendente vê que ele só abre depois que o TI ligar, e o `app_failed` diz que a chave está desligada.
- **Efeito colateral esperado:** com o Explorer como área de trabalho, o Windows roda os programas de início automático da conta (pasta Inicializar, chaves Run), como OneDrive ou Teams.
- **Não testado em conta bloqueada antes de ir para a Kamila:** a sessão de desenvolvimento não é administradora e não dá para criar uma conta de teste. Não se sabe ainda se o Windows aceita o Explorer como área de trabalho enquanto a política `Shell` da conta aponta para o Terminal. O histórico da Central responde isso no primeiro teste.

## Imagens baixadas não abriam nem editavam (25/11/2026)

O dono: "as atendentes não estão conseguindo abrir imagens baixadas nos arquivos e editar". O botão **Arquivos** abre o Explorador do Windows, e por ele só abrem os programas da `RestrictRun` (`$ProgramasFixos` em `Bloqueio-Comum.ps1`). A lista tinha PDF, texto, planilhas, navegadores e Office, mas nenhum programa de imagem: o **Editar** do botão direito (que chama o `mspaint.exe`) e o **Abrir com** eram recusados pelo Windows.

- Entraram na lista: `mspaint.exe` (Paint), `Microsoft.Photos.exe` e `Photos.exe` (Fotos), `PaintStudio.View.exe` (Paint 3D) e `OpenWith.exe` (a janela "Abrir com", que só oferece programas da própria lista).
- A lista é regravada na conta quando o Terminal é atualizado pelo botão **Atualizar** (ou reinstalado); vale depois de o computador reiniciar.
- **Paint** é um programa clássico no Windows 10 (cinco dos seis computadores): abre sem depender do Explorer escondido. O **Fotos** é aplicativo da Loja: só abre nos computadores com a chave **Aplicativos da Microsoft Store** ligada.
- Não testado em conta bloqueada (a sessão de desenvolvimento não é administradora). Se os dois cliques continuarem sem abrir num computador, o caminho garantido é botão direito > **Editar**, ou **Abrir com** > Paint marcando "Sempre usar este aplicativo".

## Limites conhecidos

- Aplicativos da Microsoft Store que não têm executável no pacote (aplicativos antigos em HTML) não aparecem. Calculadora, Bloco de Notas (Windows 11), Paint e Ferramenta de Captura aparecem porque o Windows mantém um lançador em `System32`. As **Notas Autoadesivas** não têm lançador desde o Windows 10 1607 (o `StikyNot.exe` não existe mais; em 20/11/2026 elas não apareciam nos PCs da Ana e da Sara): o Terminal acha o pacote `Microsoft.MicrosoftStickyNotes` registrado para a conta no registro do usuário (`AppModel\Repository\Packages`, valor `PackageRootFolder`), lê o executável no `AppxManifest.xml` (`Microsoft.Notes.exe`, em `C:\Program Files\WindowsApps\...`) e lista esse caminho; a decisão do TI fica pelo nome `microsoft.notes.exe`. Se o pacote não estiver registrado para a conta da atendente, elas não aparecem (e não abririam mesmo). O lançador da System32 **não abre nada na conta bloqueada** (conferido em 20/11/2026 nos PCs da Kamila e da Ana, Windows 10 22H2: "Abriu Calculadora" e "Fechou Calculadora" um segundo depois). Por isso, para esses aplicativos da Loja (Notas Autoadesivas incluídas), o Terminal pede a abertura direto ao gerenciador de ativação do Windows pelo identificador do aplicativo (`StoreApps`, `IApplicationActivationManager`) e recebe o processo, que entra em **Abertos agora**; se o aplicativo da Loja não existir na conta (Bloco de Notas clássico no Windows 10), volta ao `.exe` comum.
- Um aplicativo **cadastrado como programa** em Sites e programas conta como liberado: abre sem pedido e não aparece na aba Aplicativos. Em 20/11/2026 a "calculadora" e o "Bloco de notas" já estavam cadastrados, por isso o teste com a Calculadora não gerou pedido. Para ver o pedido, teste com um aplicativo que não esteja cadastrado (Paint, por exemplo), ou exclua o cadastro.
- Um `.exe` baixado que seja instalador roda como a conta da atendente: instala só o que não pede administrador.
- A decisão por nome de arquivo não distingue dois programas diferentes com o mesmo `.exe`; o caminho fica visível no pedido (passe o mouse sobre o arquivo) para o TI conferir.
- O que a atendente abre por fora da lista (por exemplo, pelo Explorador de Arquivos) continua sob a `RestrictRun` do Windows: só os programas cadastrados abrem por lá.
