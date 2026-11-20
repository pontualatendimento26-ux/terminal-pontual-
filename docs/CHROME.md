# Google Chrome com vários perfis

Decisão de 20/11/2026: as atendentes usam o Google Chrome com vários perfis (um WhatsApp Web em cada). O Terminal libera o Chrome e **só avisa** a Central do TI quando um site não operacional é aberto. A atendente não vê aviso nenhum. O TI decide na Central, aba **Sites no Chrome**, se o site fica liberado ou bloqueado.

## Como funciona

1. O TI cadastra o Chrome na Central como programa (Sites e programas, Adicionar programa), com o caminho `C:\Program Files\Google\Chrome\Application\chrome.exe`.
2. A cada 3 segundos o Terminal lê, pela Automação da Interface do Windows, o endereço da aba visível de cada janela do Chrome, de todos os perfis. Só a barra de endereço é lida; o conteúdo das páginas não.
3. Se o domínio não é operacional para aquela máquina (não está nos sites dela nem na lista liberada pelo TI), o Terminal envia `chrome_visit`. Enquanto o site continua na tela, envia `chrome_stay` uma vez por minuto. Só o domínio vai para a Central.
4. A Central cria um alerta urgente e mostra o domínio em **Sites no Chrome > Esperando decisão**, com o computador, as vezes e o tempo aberto.
5. O TI escolhe:
   - **Liberar**: o domínio (e subdomínios) para de gerar aviso em todos os computadores.
   - **Bloquear**: o Terminal grava o domínio em `HKCU\Software\Policies\Google\Chrome\URLBlocklist` da conta da atendente e o Chrome passa a recusar o site em poucos segundos, sem reiniciar.
   A decisão resolve os alertas abertos daquele domínio, fica na auditoria e pode ser trocada depois.

Os domínios do login do Google (`accounts.google.com`, `myaccount.google.com`, `accounts.youtube.com`) nunca geram aviso e não podem ser bloqueados, porque os perfis do Chrome dependem deles.

## Travas no Chrome (instalador com bloqueio)

`Bloqueio-Comum.ps1` grava na conta da atendente:

- `chrome.exe` na lista de programas permitidos;
- sem janela anônima (`IncognitoModeAvailability = 1`), sem modo convidado (`BrowserGuestModeEnabled = 0`) e sem ferramentas de desenvolvedor (`DeveloperToolsAvailability = 2`). Criar e trocar perfis continua liberado;
- permissão de escrita da conta **só** na chave `URLBlocklist`, para o Terminal aplicar os bloqueios. As demais políticas continuam só de administrador.

Extensões não foram bloqueadas, para não desligar as que as atendentes já usam nos perfis.

## Limites conhecidos

- O WhatsApp instalado como aplicativo do Chrome (janela sem barra de endereço) não é lido.
- Na instalação de teste (sem bloqueio) o Terminal não tem permissão para gravar a política: ele avisa, mas o bloqueio não é aplicado.
- PCs já instalados precisam do instalador novo (`gerar-instalador.ps1 -Bloqueio`) para receber as travas e a permissão da chave.
