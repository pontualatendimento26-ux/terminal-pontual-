# Terminal Pontual — Modelo de ameaças

Status: **primeira versão**, de 16/11/2026. Feita por revisão dos documentos e dos ADRs 0001 a 0004, sem teste em máquina. Complementa a matriz T-01 a T-20 de [SECURITY.md](SECURITY.md), seção 9.

Marcas usadas: **[T]** precisa de teste em máquina · **[I]** inferência ou conhecimento geral, não confirmado em fonte.

## 1. Natureza do bloqueio

No Windows Pro, o bloqueio **contém uma usuária comum, não um atacante determinado**:

- a Microsoft classifica o AppLocker como defesa em profundidade, não como barreira de segurança;
- Ctrl+Alt+Del não pode ser suprimido; só é possível tirar opções da tela;
- o bloqueio não protege contra acesso físico sem disco cifrado, contra o administrador local, nem contra falha de escalada em sistema sem correção.

Todo o resto deste documento parte daí.

## 2. Ativos

| ID | Ativo | Por que importa |
|---|---|---|
| A1 | Integridade de execução dos PCs | Quem a controla controla a máquina e os dados dos clientes |
| A2 | Chaves (configuração, release, código, CA, identidade da máquina) | Cada uma é uma raiz de confiança |
| A3 | Dados de clientes da Pontual vistos pela atendente | Maior valor para um atacante e maior risco de LGPD |
| A4 | Dados pessoais das atendentes | LGPD e relação de trabalho |
| A5 | Disponibilidade do atendimento | Uma configuração ruim para todas as atendentes de uma vez |
| A6 | Trilha de auditoria | Base da responsabilização |
| A7 | Senhas locais do TI e o cofre que as guarda | Cofre comprometido equivale a administrador em todas as máquinas |
| A8 | Contas e sessões do painel | Porta para publicar configuração |

## 3. Atores

| Ator | Capacidade | Probabilidade de agir |
|---|---|---|
| Atendente | Usuária padrão, acesso físico, tempo, curiosidade | Alta em tentativas; sofisticação baixa |
| Pessoa diante de máquina destravada | Acesso físico breve | Média |
| TI mal-intencionado ou com conta roubada | Painel, administrador local, cofre | Baixa |
| Atacante na rede local | Inclusive a partir de um PC comprometido | Baixa a média |
| Servidor comprometido | Chave de configuração, banco, canal em tempo real | Baixa a média |
| Máquina de release comprometida | Chaves de release e de código | Baixa; impacto máximo |

A estação do TI e o cofre de senhas não apareciam como fronteira em nenhum documento. Na prática, são a raiz de A7 e A8.

## 4. Lacunas encontradas

Ameaças que a matriz T-01 a T-20 não cobria, ou cobria mal. Severidade e probabilidade são para este cenário: 10 máquinas, TI pequeno, rede local.

### Críticas

| ID | Ameaça | Sev. / Prob. | Correção proposta |
|---|---|---|---|
| N-04 | **O suporte remoto contradiz a promessa de "sem execução de comando".** O agente da ferramenta de suporte oferece terminal, arquivos e comandos (ADR 0002). Um servidor comprometido que peça uma sessão em modo formal chega a controle total, e a ação aparece como feita pela atendente | Crítica / baixa a média | Ferramenta de suporte em outro servidor, com chaves próprias; agente restrito a tela, sem terminal nem arquivos **[T]**; modo formal com autorização fora do servidor, ou fora da primeira versão |
| N-09 | **Escalada local em Windows sem correção.** Uma falha pública de escalada transforma a usuária padrão em administradora e anula todo o resto. As máquinas com Windows 10 estão fora do suporte normal | Crítica / média (alta no Windows 10 sem atualizações estendidas) | Meta de versão e de correções por máquina; Windows 10 sem atualizações estendidas fica fora do "suportado" até migrar; versão e correções relatadas no heartbeat |
| N-11 | **Máquina de release comprometida** equivale a execução como sistema em todas as máquinas. Inclui o risco de código do Broker e do Service escrito com apoio de agentes de IA e mal revisado | Crítica / baixa | Chaves em tokens físicos de duas pessoas; compilação separada da assinatura; dependências fixadas; revisão humana obrigatória do código do Broker e do Service |

### Altas

| ID | Ameaça | Correção proposta |
|---|---|---|
| N-03 | A lista fixa é uma **lista de negação**, sempre incompleta, e as regras padrão do AppLocker liberam pastas inteiras, com subpastas graváveis. Coleções de DLL, script, instalador e aplicativos empacotados não são citadas **[I][T]** | Lista de **permissão** mínima para o grupo das atendentes, com as demais coleções ligadas; navegador nativo e loja negados; catálogo sem curinga em caminho |
| N-02 | As políticas por usuário e o shell personalizado ficam em área do registro que a **própria usuária pode gravar** **[I][T]** | O Broker verifica e reaplica a cada logon e periodicamente; testar negação de escrita nessas chaves |
| N-08 | **A postura do terminal não é atestada.** AppLocker esquecido em modo de auditoria, serviço parado, conta da atendente virando administradora, shell revertido: nada disso gera alerta | Heartbeat com hash da política efetiva, modo do AppLocker, estado do serviço, grupos da conta, versão do Windows e estado do disco cifrado |
| N-12 | A aprovação por dois administradores **não está ligada ao conteúdo aprovado**, e o segundo aprovador pode não ter como avaliar o que aprova | Aprovação ligada ao hash do conteúdo; tela com editor, versão e caminho; lista de verificação de capacidades; navegadores e ferramentas de acesso remoto proibidos no catálogo |
| N-15 | **Detecção suprimível e sem dono.** Os alertas ficam no painel e podem ser desligados por um administrador; ninguém foi designado para conferir as âncoras de auditoria | Notificação fora do sistema, a um gestor fora do TI, para publicação de configuração, aprovação de executável, sessão formal e uso do acesso de emergência; dono e periodicidade da conferência |
| N-01 | **Acesso físico.** Nenhum documento trata disco cifrado, senha de firmware, ordem de inicialização ou ambiente de recuperação **[I]** | BitLocker, senha de firmware e inicialização só pelo disco interno; decidir no inventário |
| N-10 | **Configuração defeituosa para todas as máquinas de uma vez**, por erro ou de propósito | O terminal guarda a última configuração boa, verifica a saúde após aplicar e volta sozinho; publicação para uma máquina piloto primeiro |
| N-17 | **Chave de configuração em repouso e nos backups.** Não está dito onde fica, quem a lê nem se o backup é cifrado | Chave em processo assinante separado, fora do banco e do backup; backup cifrado com outra chave |
| N-21 | **Credenciais do TI.** Pedido de elevação aparecendo para a usuária padrão, o TI digitando a senha na sessão da atendente, acesso ao cofre **[I][T]** | Negar elevação automática a usuárias padrão; procedimento "o TI nunca digita a senha na sessão da atendente"; segundo fator no cofre |

### Médias e baixas

| ID | Ameaça | Correção proposta |
|---|---|---|
| N-05 | O consentimento de suporte pode ser dado por um processo da atendente, não por ela | Caixa de consentimento em processo iniciado pelo Broker, com integridade superior à da sessão **[T]** |
| N-06 | Sem segmentação de rede: o PC da atendente alcança o painel, o login e o banco | Rotas de máquina e painel em pontos separados; painel só da rede do TI; banco sem exposição |
| N-07 | Relógio deslocado por serviço de horário não autenticado; mensagens de manutenção e suporte sem sequência | Expiração por duração desde o recebimento; o terminal não aceita relógio que volte atrás |
| N-13 | Site liberado com curinga vira saída de dados | Domínios exatos; filtro de navegação e de requisições também no Shell |
| N-14 | Vazamento de dados de clientes por canais permitidos: área de transferência, impressão, captura de tela, foto | Aceitar o risco por escrito, ou decidir quais canais entram no escopo |
| N-16 | A auditoria imutável guarda nomes por 5 anos, mas a retenção promete anonimizar atendentes desligadas | Guardar só o identificador na trilha e resolver o nome na leitura |
| N-18 | Imagem clonada herda a identidade da máquina | Alertar quando a mesma identidade aparecer em dois endereços |
| N-19 | Substituições e tela sem bloqueio quebram a atribuição de uso | Definir substituições (ADR 0003), bloqueio por inatividade e política de senha |
| N-20 | A telemetria vem da máquina e pode ser forjada; conteúdo recebido pode ser exibido sem tratamento | Validar tamanho e formato; nunca usar como prova isolada em medida disciplinar |

## 5. Decisão em aberto: quem autoriza executáveis

Hoje a segunda aprovação é verificada pelo servidor. Ela protege contra abuso de um administrador, mas **não** contra servidor comprometido: quem domina o servidor forja a aprovação.

| | Chave no servidor (como está) | Chave fora do servidor |
|---|---|---|
| Passo manual | Nenhum | Assinar a cada mudança de executável |
| Servidor comprometido | Libera qualquer programa fora da lista de negação | Não consegue adicionar executável |
| Dois administradores | Regra de banco de dados | Duas assinaturas, com dois tokens físicos |
| Risco operacional | Nenhum | Perda de token, ausência de uma das pessoas, atraso em liberação urgente |

**Recomendação do modelo de ameaças: fora do servidor, com o servidor só podendo restringir.**

1. Chave própria do catálogo, em dois tokens físicos de pessoas diferentes, com chave de reserva em cofre físico.
2. O catálogo assinado lista os executáveis por editor, produto e versão mínima, com caminho sem curinga.
3. A configuração do servidor só pode ativar ou desativar itens do catálogo, nunca criar. O servidor consegue **tirar** um aplicativo com urgência, mas não **adicionar**.
4. O painel continua com proposta e aprovação, como fluxo de trabalho e registro.

Se isso for recusado, o mínimo é fixar no terminal uma lista de editores aceitos.

Com mudanças raras de executável, o custo operacional é pequeno. A decisão precisa sair **antes da Fase 1**, porque muda a API, o banco e os contratos.

## 6. Cinco ações antes do código do servidor

1. **Congelar o modelo de confiança e os contratos de mensagem**: decisão da seção 5, formato de toda mensagem assinada, postura do terminal no heartbeat (N-08), última configuração boa (N-10).
2. **Fechar o teste de bloqueio no Windows Pro e o inventário de hardware**: versão, TPM, disco cifrado, e o destino das máquinas com Windows 10.
3. **Projeto de implantação do servidor**: processo assinante separado, segmentação, notificação fora do sistema, backup cifrado.
4. **Reduzir o suporte remoto ao mínimo seguro ou adiá-lo**: decidir se o modo formal existe na primeira versão (N-04, N-05).
5. **Controle da cadeia de entrega**: tokens físicos, compilação separada da assinatura, revisão humana do Broker e do Service (N-11).
