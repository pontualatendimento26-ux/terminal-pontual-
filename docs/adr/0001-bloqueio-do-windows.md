# ADR 0001 — Mecanismos de bloqueio do Windows para a conta da atendente

- **Status:** Proposto. Apoiado em documentação oficial; nenhum teste em máquina foi feito.
- **Data:** 16/11/2026
- **Hipóteses:** H-01, H-02, H-03, H-04

## Contexto

As máquinas das atendentes rodam **Windows 10 Pro, todas** (confirmado pela Pontual em 16/11/2026; a primeira informação citava também Windows 11), em grupo de trabalho, sem domínio. A versão exata de cada uma (`winver`) ainda precisa ser levantada: o AppLocker só é aplicado no Windows 10 Pro a partir da versão 2004 com as atualizações de outubro de 2022.

Como o suporte normal ao Windows 10 terminou em outubro de 2025, **todas as máquinas estão sem correções de segurança**, a menos que estejam no programa pago de atualizações estendidas. Isso vale para o parque inteiro, não para uma parte dele. O bloqueio precisa usar mecanismos oficiais, valer só para a conta da atendente e deixar a conta do TI intacta.

## O que a documentação oficial diz

| Mecanismo | Windows Pro | Enterprise / Education | Por usuário? | Observação |
|---|---|---|---|---|
| Shell Launcher | Não | Sim | Sim | Reinício do shell configurável. Não pode ser combinado com Acesso Atribuído |
| Política "Interface de usuário personalizada" | Sim | Sim | Sim | A documentação **não informa** o que acontece quando o shell termina |
| Acesso Atribuído, quiosque único | Sim | Sim | Sim | Documentado para UWP ou Edge. Para executável próprio a documentação é contraditória |
| Acesso Atribuído, experiência restrita | Sim | Sim | Sim | Carrega a área de trabalho restrita, não um shell próprio. Algumas políticas atingem **todas** as contas, inclusive a do TI |
| Filtro de teclado | Não | Sim | Não (contas padrão) | Único meio documentado de suprimir Ctrl+Alt+Del |
| AppLocker | Sim | Sim | Sim (usuário ou grupo) | Windows 10 exige a versão 2004 ou mais nova, com as atualizações de outubro de 2022. Configurável por política local. A Microsoft o classifica como defesa em profundidade, **não** como barreira de segurança |
| Controle de Aplicativos (WDAC) | Sim | Sim | **Não** (dispositivo inteiro) | Atingiria também o TI |
| Política local por usuário (MLGPO) | Não declarado em página atual | Não declarado em página atual | Sim | Documentado para máquinas fora de domínio, mas só em página arquivada |
| Políticas de Ctrl+Alt+Del, Painel de Controle, registro, prompt, mídia removível | Sim | Sim | Sim | "Executar apenas aplicativos especificados" só bloqueia o que parte do Explorer; não serve como lista de permissão |
| Políticas de Restrição de Software (SRP) | Depreciado | Depreciado | — | Não usar |

Fontes principais (learn.microsoft.com): `windows/configuration/shell-launcher/`, `windows/configuration/assigned-access/overview`, `windows/configuration/keyboard-filter/`, `windows/client-management/mdm/policy-csp-admx-winlogon`, `policy-csp-admx-ctrlaltdel`, `windows/security/application-security/application-control/app-control-for-business/applocker/requirements-to-use-applocker` e `applocker-overview`, e support.microsoft.com/help/5024351.

## Decisão proposta

Para **Windows Pro em grupo de trabalho**, a combinação a levar ao teste é:

1. **Conta local padrão** para a atendente.
2. **Shell:** política "Interface de usuário personalizada", aplicada só à conta da atendente, apontando para o Shell do Terminal Pontual.
3. **Execução:** AppLocker por política local, com regras atribuídas a um grupo local das atendentes, começando em modo de auditoria.
4. **Sistema:** política local por usuário (camada do usuário ou de não administradores) com as políticas de Ctrl+Alt+Del, Painel de Controle e Configurações, registro, prompt de comando e mídia removível.
5. **Reinício do Shell:** como a documentação é omissa para a política do item 2, o Broker assume o reinício, salvo se o teste mostrar que o Windows já o faz.

Não usar: Controle de Aplicativos, SRP, nem "Executar apenas aplicativos especificados" como controle principal.

Se as máquinas forem **Enterprise ou Education**, Shell Launcher e filtro de teclado substituem os itens 2 e 5, e este ADR é revisto.

## Consequências

- **Ctrl+Alt+Del continua abrindo** a tela de segurança no Pro. Só é possível tirar opções dela.
- O AppLocker **não é barreira contra atacante determinado**, segundo a própria Microsoft. O conjunto contém uma usuária comum; o texto de SECURITY.md deve refletir isso.
- O PowerShell não é coberto pela política do prompt de comando e precisa de regra no AppLocker.
- **Windows Home não é suportado.** Máquinas Home precisam ser atualizadas para Pro.
- **Windows 10:** o suporte normal terminou em outubro de 2025. Sem o programa pago de atualizações estendidas, essas máquinas não recebem correções, o que enfraquece qualquer bloqueio.

## O que só o teste em máquina confirma

1. Edição e versão exatas de cada PC (`winver`).
2. Se a política de shell personalizado, aplicada só à atendente, substitui o Explorer no Windows 10 e 11 atuais, e o que acontece quando o shell fecha ou trava.
3. Se o AppLocker bloqueia de fato em Pro, em grupo de trabalho, com regra para grupo local, e se a conta do TI fica livre inclusive em sessão não elevada.
4. Se a política local por usuário existe e se aplica no Pro atual (conferir com `gpresult` nas duas contas).
5. Se o serviço de Identidade do Aplicativo permanece ativo após reinícios e atualizações.
6. Se há pastas graváveis pela atendente dentro de caminhos liberados.
7. O roteiro de fuga completo de SECURITY.md, seção 5.
8. Reversão: todas as políticas removidas e a máquina de volta ao estado anterior.
