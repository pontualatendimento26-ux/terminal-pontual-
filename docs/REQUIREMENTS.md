# Terminal Pontual — Requisitos

Status: **revisão 2**. Incorpora a revisão do agente `ecc:architect` e as decisões aprovadas pela Pontual.

As tabelas abaixo são também a **matriz de rastreabilidade**: cada requisito aponta os componentes que o implementam e a forma de verificação.

Legendas:

- **Prioridade:** E essencial · I importante · D desejável.
- **Fase:** ver [ROADMAP.md](ROADMAP.md).
- **Componentes:** SRV servidor · ADM painel · SVC Service · BRK Broker · SHL Shell · INS instalador · HUB Central dos Agentes · INF infraestrutura da Pontual. Definições em [ARCHITECTURE.md](ARCHITECTURE.md).
- **Verificação:** U teste de unidade · I teste de integração · E2E teste ponta a ponta · SEG teste de segurança ou roteiro de fuga · PoC prova na Fase 0 · PROC procedimento definido com RH ou jurídico. **H-xx** indica a hipótese de que o requisito depende.

## 1. Escopo

Plataforma centralizada para administrar os computadores das atendentes da Pontual: cerca de 10 máquinas no início, preparada para dezenas ou centenas.

Fora do escopo: captura de teclado, acesso remoto oculto, leitura de conteúdo de aplicativos ou sites, e qualquer desbloqueio remoto da conta da atendente.

**Gravação de tela (decisão do dono, 23/11/2026):** a tela das atendentes é gravada em vídeo (1 quadro por segundo, trechos de 1 minuto), enviada à Central, guardada cifrada e apagada depois de 3 meses. A barra do Terminal avisa a atendente enquanto grava. O aviso de monitoramento entregue às atendentes precisa ser revisado pelo RH ou pelo jurídico antes de a gravação ficar ligada nos computadores.

## 2. Perfis

| Perfil | Descrição |
|---|---|
| Atendente | Usa o terminal. Sem privilégios administrativos no Windows. |
| Suporte | Consulta máquinas, atende alertas, solicita suporte remoto. |
| Admin | Cadastros, configuração, manutenção e atualizações. |
| Superadmin | Gestão de administradores e de chaves. |
| Auditor | Somente leitura de histórico e auditoria (RH, gestão). |

As permissões exatas de cada perfil estão na matriz única de [SECURITY.md](SECURITY.md), seção 3.

## 3. Requisitos funcionais

### Módulo 1 — Interface da atendente

| ID | Requisito | Prior. | Fase | Componentes | Verificação |
|---|---|---|---|---|---|
| RF-101 | O terminal inicia com o Windows, direto na interface da Pontual | E | 2 | SHL, BRK | E2E · H-01 |
| RF-102 | Interface em tela cheia com a identidade visual da Pontual | E | 2 | SHL | E2E |
| RF-103 | Exibe só os aplicativos autorizados para a máquina | E | 2 | SHL, SVC | U, E2E |
| RF-104 | Abre só os sites liberados | E | 2 | SHL, INF | SEG · H-09 |
| RF-105 | Mostra o estado da conexão com o servidor | E | 2 | SHL, SVC | E2E |
| RF-106 | Atualiza a lista de aplicativos sem reiniciar | E | 2 | SRV, SVC, SHL | I, E2E |
| RF-107 | Recupera-se se for encerrada de forma inesperada | E | 2 | SHL, BRK | E2E · H-04 |
| RF-108 | Funciona com a última configuração válida sem servidor | E | 2 | SVC | I |
| RF-109 | A atendente não acessa a área de trabalho nem recursos administrativos | E | 2 | BRK, INS | SEG · H-01, H-02, H-03, H-07 |
| RF-110 | O sistema identifica a atendente em cada máquina; a forma é decidida na Fase 0 | E | 2 | SHL, SVC, SRV | E2E |
| RF-111 | Aviso permanente durante suporte remoto, exibido por processo independente do Shell | E | 6 | BRK, ferramenta | PoC, SEG · H-06 |
| RF-112 | Informa à atendente que o uso é monitorado e o que é registrado | E | 2 | SHL | E2E, PROC |
| RF-113 | Tela de manutenção com aviso e expiração aplicada localmente | E | 2 | SHL, SVC, BRK | E2E |
| RF-114 | Lista de bloqueio fixa de executáveis perigosos, não removível por configuração remota | E | 2 | SVC, BRK, SHL | U, SEG |
| RF-115 | Navegador embutido endurecido; perfil apagado na troca de atendente quando a conta é compartilhada | E | 2 | SHL | SEG, E2E · H-07 |

### Módulo 2 — Central administrativa

| ID | Requisito | Prior. | Fase | Componentes | Verificação |
|---|---|---|---|---|---|
| RF-201 | Login com senha forte, segundo fator obrigatório, códigos de recuperação e acesso de emergência | E | 1 | SRV, ADM | I, E2E |
| RF-202 | Cadastro, aprovação, edição e aposentadoria de computadores | E | 1 | SRV, ADM | I, E2E |
| RF-203 | Cadastro de atendentes | E | 1 | SRV, ADM | I |
| RF-204 | Cadastro de sites e de aplicativos permitidos | E | 2 | SRV, ADM | I |
| RF-205 | Perfis de configuração por máquina, grupo ou todas | E | 2 | SRV, ADM | I |
| RF-206 | Distribuição da configuração com confirmação por máquina | E | 2 | SRV, SVC | I, E2E |
| RF-207 | Histórico de utilização por máquina, atendente e período | E | 3 | SRV, ADM | I |
| RF-208 | Registro de eventos das máquinas | E | 3 | SRV, SVC | I |
| RF-209 | Alertas com regras configuráveis, reconhecimento e resolução | E | 3 | SRV, ADM | I, E2E |
| RF-210 | Publicação de atualizações por canal e acompanhamento | I | 5 | SRV, ADM, BRK | I, SEG |
| RF-211 | Autorização conforme a matriz única de permissões | E | 1 | SRV | I gerado da matriz |
| RF-212 | Auditoria de todas as ações, com adulteração detectável e âncora externa | E | 1 | SRV | I, PROC |
| RF-213 | Modo manutenção com reautenticação, justificativa e prazo | E | 2 | SRV, ADM, SVC | I, E2E |
| RF-214 | Exportação dos dados de uma atendente, para atender ao titular | E | 3 | SRV, ADM | I, PROC |
| RF-215 | Exportação geral de histórico e auditoria | D | 3 | SRV, ADM | I |
| RF-216 | Leituras sensíveis (histórico individual, auditoria, exportação) são auditadas | E | 3 | SRV | I |
| RF-217 | Inclusão ou alteração de executável exige dois administradores | E | 2 | SRV, ADM | I |
| RF-218 | Gestão de unidades | E | 1 | SRV, ADM | I |
| RF-219 | Gestão de administradores e funções | E | 1 | SRV, ADM | I |
| RF-220 | Rotação de chaves do servidor e das máquinas | I | 5 | SRV, SVC | I |
| RF-221 | Registro dos logons locais da conta do TI nas máquinas | I | 3 | SVC, BRK, SRV | I · H-12 |

### Módulo 3 — Escritório virtual 2D

| ID | Requisito | Prior. | Fase | Componentes | Verificação |
|---|---|---|---|---|---|
| RF-301 | Escritório em pixel art 2D original, com identidade da Pontual | E | 4 | ADM | revisão de arte |
| RF-302 | Cada estação representa um computador real cadastrado | E | 4 | ADM, SRV | E2E |
| RF-303 | Clicar na estação mostra atendente, máquina, status, aplicativos em uso, tempo de atividade, última comunicação e alertas | E | 4 | ADM, SRV | E2E |
| RF-304 | A partir dos detalhes: histórico e configuração. O atalho de suporte remoto só é habilitado quando o Módulo 4 existir | E | 4 e 6 | ADM | E2E |
| RF-305 | Animações distintas para seis estados: online, online sem atendente, offline, alerta, manutenção e suporte remoto | E | 4 | ADM | E2E |
| RF-306 | Todos os dados vêm do servidor; nenhum indicador é simulado | E | 4 | ADM, SRV | E2E com servidor de teste |
| RF-307 | Atualização em tempo real | E | 4 | ADM, SRV | E2E |
| RF-308 | O TI posiciona as estações no cenário | I | 4 | ADM, SRV | E2E |
| RF-309 | Visão alternativa em lista | I | 4 | ADM | E2E |
| RF-310 | Sem conexão com o servidor, o cenário fica esmaecido e avisa; não mantém o último estado como atual | E | 4 | ADM | E2E |

### Módulo 4 — Suporte remoto

| ID | Requisito | Prior. | Fase | Componentes | Verificação |
|---|---|---|---|---|---|
| RF-401 | Solicitação de sessão com justificativa e prazo | E | 6 | SRV, ADM | I |
| RF-402 | A sessão só começa após consentimento da atendente ou procedimento formal | E | 6 | SRV, SVC, SHL | E2E, PROC |
| RF-403 | Visualização da tela e controle de mouse e teclado | E | 6 | ferramenta | PoC · H-06 |
| RF-404 | Encerramento pelo administrador ou pela atendente | E | 6 | SRV, SHL, BRK | E2E |
| RF-405 | Expiração automática aplicada no terminal, mesmo sem servidor | E | 6 | SVC, BRK | SEG |
| RF-406 | Registro de início, término, responsável e ações relevantes | E | 6 | SRV | I |
| RF-407 | Nenhum acesso remoto fora de sessão registrada; o agente da ferramenta fica parado fora de sessão | E | 6 | BRK, ferramenta | PoC, SEG · H-06 |
| RF-408 | O procedimento formal exige segundo administrador e aviso prévio na tela | E | 6 | SRV, SHL | I, E2E, PROC |
| RF-409 | Registros da ferramenta de suporte importados para a auditoria | I | 6 | SRV | I · H-06 |

### Módulo 5 — Central dos agentes de IA

Módulo independente e parte importante do produto. É **diferente da interface das atendentes**: mostra os agentes de IA que desenvolvem e mantêm o sistema, não os computadores controlados. A arquitetura está em [ARCHITECTURE.md](ARCHITECTURE.md), seção 13, e o conceito visual em [UI-CONCEPT.md](UI-CONCEPT.md), seção 3.

| ID | Requisito | Prior. | Fase | Componentes | Verificação |
|---|---|---|---|---|---|
| RF-501 | Escritório em pixel art 2D, feito em Phaser, com personagens que representam agentes reais do Claude Code e seus subagentes | E | 7 | HUB | revisão de arte, E2E |
| RF-502 | Cada personagem mostra nome real do agente, especialidade, tarefa atual, status, histórico, arquivos e atividade recente, quando a origem fornece essas informações | E | 7 | HUB | E2E |
| RF-509 | Os eventos vêm dos *hooks* do Claude Code, por um encaminhador que não envia conteúdo e não atrasa o Claude Code | E | 7 | HUB | I · H-13 |
| RF-510 | A cena é atualizada em tempo real por uma arquitetura de eventos | E | 7 | HUB | E2E |
| RF-511 | Um subagente aparece ligado ao agente que o lançou | I | 7 | HUB | E2E · H-13 |
| RF-503 | Progresso exibido só quando a origem fornece dado real | E | 7 | HUB | U, E2E |
| RF-504 | Animações para trabalhando, programando, analisando, testando, aguardando, concluído e com erro | E | 7 | HUB | E2E |
| RF-505 | Personagens refletem só eventos recebidos; um evento sem agente identificado é atribuído à sessão, nunca a um agente presumido | E | 7 | HUB | U · H-13 |
| RF-506 | Contrato de eventos aberto a outras origens | E | 7 | HUB | I |
| RF-507 | Instância isolada, sem acesso às máquinas nem às credenciais de produção | E | 7 | HUB | revisão de implantação |
| RF-508 | O mapeamento entre agente e personagem é configuração, não código | I | 7 | HUB | U |

### Instalação e recuperação

| ID | Requisito | Prior. | Fase | Componentes | Verificação |
|---|---|---|---|---|---|
| RF-601 | Verifica edição, versão e pré-requisitos antes de alterar qualquer coisa | E | 5 | INS | E2E |
| RF-602 | Instala os componentes e cria a conta restrita | E | 5 | INS | E2E |
| RF-603 | Configura a inicialização e registra a máquina como pendente | E | 5 | INS, SRV | E2E |
| RF-604 | Aplica as políticas autorizadas e verifica a comunicação | E | 5 | INS, BRK | E2E |
| RF-605 | Gera relatório da instalação | E | 5 | INS | E2E |
| RF-606 | A conta administrativa do TI não é alterada nem restringida | E | 2 | BRK, INS | SEG · H-03 |
| RF-607 | Toda alteração é reversível por desinstalação | E | 5 | INS, BRK | SEG · H-08 |
| RF-608 | Reversão local do bloqueio pelo TI, disponível desde a primeira versão que bloqueia | E | 2 | BRK | SEG · H-08 |

## 4. Requisitos não funcionais

| ID | Requisito | Verificação |
|---|---|---|
| RNF-01 | Toda comunicação em trânsito é criptografada (TLS 1.2 ou superior) | I |
| RNF-02 | Terminal e servidor se autenticam mutuamente | I |
| RNF-03 | Configurações, manutenção, pedidos de suporte e atualizações são assinados, com sequência crescente e prazo, e verificados no terminal | U, SEG |
| RNF-04 | O protocolo não tem mensagem de execução de comando, e o terminal aplica limites locais que a configuração remota não remove. **Isso não torna as máquinas imunes a um servidor comprometido**; os limites e o risco residual estão em [SECURITY.md](SECURITY.md), seção 2 | SEG, modelo de ameaças |
| RNF-05 | O terminal opera sem servidor com a última configuração válida; manutenção e suporte expiram localmente | I |
| RNF-06 | Mudança de status aparece no painel em até 5 s (offline: até 90 s) | I |
| RNF-07 | Terminal utilizável em até 15 s após o logon | E2E |
| RNF-08 | O desenho suporta 500 máquinas sem mudança de arquitetura | teste de carga |
| RNF-09 | Coleta mínima | revisão |
| RNF-10 | Retenção definida para todas as tabelas e aplicada automaticamente | I |
| RNF-11 | A produção funciona integralmente em rede local. A Central dos Agentes é outra instância e não está sujeita a este requisito | I |
| RNF-12 | Painel utilizável por teclado; estado nunca indicado só por cor | E2E |
| RNF-13 | Testes automatizados nas regras de negócio, na autorização e no bloqueio | integração contínua |
| RNF-14 | Interface em português do Brasil | revisão |
| RNF-15 | Relógios sincronizados por servidor de horário da rede local | I · INF |
| RNF-16 | Nenhuma funcionalidade do Windows é considerada disponível sem teste na edição real e referência à documentação oficial | ADR por hipótese |

## 5. Pendências que dependem da Pontual

### Antes ou durante a Fase 0

1. **Edição e versão do Windows** das máquinas das atendentes. *Respondido em 16/11/2026: Windows 10 e 11, todas Pro. Falta a versão exata de cada máquina (`winver`).*
2. **Domínio (Active Directory)** ou grupo de trabalho; existe alguma ferramenta de gerenciamento de máquinas? *Respondido: grupo de trabalho.*
3. **Onde fica o servidor** de produção.
4. **Modelo de conta da atendente**: máquina fixa ou revezamento; conta Windows individual ou compartilhada. *Respondido: máquina fixa por pessoa (ver ADR 0003). Falta definir as substituições.*
5. **Lista real de aplicativos e sites**, e se algum exige privilégio de administrador. *Respondido em 16/11/2026: ver [APLICATIVOS-E-SITES.md](APLICATIVOS-E-SITES.md). Restam as perguntas da seção 4 daquele documento.*
6. **DNS ou proxy** disponível para limitar os sites.
7. **Servidor de horário** na rede local.
8. **Atualizações do Windows e do navegador embutido**: por onde chegam, se a rede for fechada.
9. **Quem são os dois administradores** exigidos para executáveis, e quem guarda o acesso de emergência.
10. **Destinatário da âncora de auditoria** fora do TI (RH ou jurídico).

### Fase futura: celulares das atendentes

Pedido da Pontual em 16/11/2026: levar a mesma ideia aos celulares, ligados à mesma Central do TI. Informado: os aparelhos são **de uso profissional** e **todos Android**. Nada foi pesquisado nem construído ainda.

A confirmar antes de planejar: se os aparelhos são de propriedade da empresa; quantos são, marca, modelo e versão do Android; para que são usados (só WhatsApp, ou também os portais); se o objetivo é bloquear ou só acompanhar; se podem ser restaurados de fábrica para a configuração. Exige pesquisa própria (modo de dispositivo dedicado do Android, construir contra adotar uma ferramenta de gerenciamento) e validação de RH e jurídico.

### Durante o projeto

11. **Acesso do TI de fora da rede**: é necessário? Existe VPN?
12. **Identidade visual** da Pontual.
13. **Arte em pixel art**: produção própria, contratação ou pacote licenciado.
14. **RH e jurídico**: aviso de monitoramento, procedimento formal de suporte remoto e prazos de retenção.
15. **Assinatura de código**: certificado público ou CA interna.
