# Terminal Pontual — Segurança e LGPD

Status: **revisão 2**. Incorpora a revisão do agente `ecc:architect` e as decisões aprovadas pela Pontual.

Este documento **não promete segurança absoluta**. Ele descreve os controles, o que cada um impede, o que apenas detecta e o risco que permanece. Itens marcados com **[H-xx]** são hipóteses ainda não comprovadas, listadas em [ARCHITECTURE.md](ARCHITECTURE.md), seção 10.

Os pontos de LGPD e de relação de trabalho precisam de validação do RH e do jurídico da Pontual; este texto não substitui parecer jurídico.

## 1. Princípios

1. **Menor privilégio.** A atendente é usuária padrão. A parte do terminal que fala com a rede não tem privilégio; a parte privilegiada não fala com a rede.
2. **Protocolo fechado.** Não existe mensagem de execução de comando. Isso limita o que o servidor pode pedir, mas **não torna as máquinas imunes a um servidor comprometido** (seção 2).
3. **Verificação local.** O terminal valida assinatura, sequência e prazo de tudo o que recebe, e aplica limites próprios que nenhuma configuração remota remove.
4. **Transparência.** Nada é coletado ou acessado às escondidas da atendente.
5. **Coleta mínima.**
6. **Auditoria com adulteração detectável.** O sistema não impede que quem administra o servidor altere registros; ele torna a alteração detectável por terceiros (seção 7).
7. **Reversibilidade.** Nenhum bloqueio é irreversível, e o bloqueio nunca atinge a conta do TI.

## 2. O que acontece se o servidor ou uma conta de administrador forem comprometidos

| O atacante consegue | Por quê | O que limita o dano |
|---|---|---|
| Publicar configuração válida | A chave de configuração fica no servidor | Lista de bloqueio fixa no terminal; execução sempre como usuário padrão; alerta de configuração publicada |
| Liberar para a atendente um programa já instalado que não esteja na lista fixa | Idem; a segunda aprovação é verificada pelo servidor, que está comprometido | Diretório protegido; lista de permissão de execução do Windows aplicada na instalação |
| Tirar máquinas de serviço (manutenção) | Mensagem assinada pela mesma chave | Manutenção não desbloqueia a conta da atendente; prazo máximo aplicado localmente |
| Pedir sessão de suporte | Idem | Consentimento ou aviso prévio na tela; aviso permanente; expiração local. Nunca é oculto |
| Ler dados de utilização | Estão no banco | Coleta mínima e retenção curta |

| O atacante **não** consegue | Por quê |
|---|---|
| Executar comando ou script arbitrário pelo protocolo | Não existe essa mensagem; interpretadores estão na lista fixa |
| Instalar software ou atualizar o terminal | Exige a chave de release, que fica fora do servidor |
| Obter privilégio de administrador na máquina por configuração | O Broker só executa um conjunto fechado de operações |
| Remover a lista de bloqueio fixa | Ela só muda por atualização assinada |
| Iniciar suporte sem que apareça na tela | O aviso é local e independente do Shell **[H-06]** |

**Decisão em aberto:** manter a chave do catálogo de executáveis fora do servidor eliminaria a segunda linha da primeira tabela, ao custo de uma etapa manual a cada mudança de executável. Fica para o modelo de ameaças da Fase 0.

## 3. Matriz de permissões

Esta é a **única** definição de autorização do sistema. A [API.md](API.md) refere-se às permissões por estes códigos. Não há hierarquia implícita entre funções: vale apenas o que está marcado.

Exigências: **R** reautenticação · **J** justificativa obrigatória · **P** prazo obrigatório · **2A** dois administradores (quem aprova não pode ser quem propôs). Toda ação da matriz é auditada.

| Permissão | Auditor | Suporte | Admin | Superadmin | Exigências |
|---|---|---|---|---|---|
| `devices.read` — ver máquinas e status | ✓ | ✓ | ✓ | ✓ | |
| `devices.manage` — editar, aprovar, definir canal, aposentar | | | ✓ | ✓ | |
| `devices.revoke` — revogar máquina | | | ✓ | ✓ | R, J |
| `enrollment.create` — gerar código de cadastro | | | ✓ | ✓ | R |
| `sites.manage` — unidades | | | ✓ | ✓ | |
| `usage.read` — histórico por máquina | ✓ | ✓ | ✓ | ✓ | |
| `usage.read.individual` — histórico por atendente | ✓ | | ✓ | ✓ | leitura auditada |
| `alerts.read` | ✓ | ✓ | ✓ | ✓ | |
| `alerts.handle` — reconhecer e resolver | | ✓ | ✓ | ✓ | |
| `alert-rules.manage` | | | ✓ | ✓ | R, J |
| `attendants.read` | ✓ | ✓ | ✓ | ✓ | |
| `attendants.manage` | | | ✓ | ✓ | |
| `apps.read` | ✓ | ✓ | ✓ | ✓ | |
| `apps.websites.manage` — sites autorizados | | | ✓ | ✓ | |
| `apps.executables.propose` — propor inclusão ou alteração de executável | | | ✓ | ✓ | R, J |
| `apps.executables.approve` — aprovar a proposta | | | ✓ | ✓ | R, 2A |
| `config.read` | ✓ | ✓ | ✓ | ✓ | |
| `config.edit` — editar perfil | | | ✓ | ✓ | |
| `config.publish` — publicar configuração | | | ✓ | ✓ | R, J |
| `maintenance.manage` — entrar e sair de manutenção | | | ✓ | ✓ | R, J, P |
| `support.request` — solicitar suporte remoto | | ✓ | ✓ | ✓ | R, J, P |
| `support.formal.authorize` — autorizar suporte por procedimento formal | | | ✓ | ✓ | R, J, 2A |
| `support.read` — ver sessões de suporte | ✓ | ✓ | ✓ | ✓ | |
| `updates.read` | ✓ | ✓ | ✓ | ✓ | |
| `updates.release` — liberar atualização | | | ✓ | ✓ | R, J |
| `layouts.manage` — escritório virtual | | | ✓ | ✓ | |
| `audit.read` | ✓ | | ✓ | ✓ | |
| `subject.export` — exportar dados de uma atendente | | | ✓ | ✓ | R, J |
| `admins.manage` — administradores e funções | | | | ✓ | R, J |
| `keys.manage` — rotação de chaves | | | | ✓ | R, J |

Consequências:

- **Sites** são administrados por um único administrador.
- **Executáveis** exigem duas pessoas. A instalação precisa de pelo menos dois administradores ativos; com TI de uma pessoa, a segunda aprovação cabe a um gestor com a função Admin.
- **Segurança e manutenção** sempre com reautenticação, justificativa e, quando aplicável, prazo.
- O Suporte **não** lê a auditoria; o Auditor lê, mas não age.

## 4. Autenticação

### Administradores

- Senha com hash moderno (ASP.NET Core Identity) e **segundo fator obrigatório**. O cadastro do segundo fator é a primeira e única ação possível no primeiro acesso.
- Códigos de recuperação emitidos no cadastro do segundo fator.
- **Acesso de emergência:** uma conta Superadmin de reserva, com credenciais guardadas em envelope lacrado sob responsabilidade definida pela Pontual. Todo uso gera alerta.
- Cookie de sessão `HttpOnly`, `Secure`, `SameSite=Strict`, com proteção contra CSRF.
- Expiração por inatividade e bloqueio progressivo.

### Máquinas

1. O administrador gera um código de cadastro de uso único e validade curta.
2. O instalador gera o par de chaves na máquina e envia a chave pública com o código. A máquina entra como **pendente**.
3. Um administrador **aprova** a máquina no painel. Só então ela recebe configuração.
4. A máquina obtém *tokens* de curta duração provando a posse da chave.
5. O servidor verifica o estado da máquina **a cada requisição** e derruba a conexão em tempo real ao revogar. *Tokens* curtos sozinhos não bastam.

Um cadastro com nome de máquina já existente não substitui o anterior: entra como pendente e o administrador decide.

## 5. Proteção do terminal

- **Três processos**, conforme [ARCHITECTURE.md](ARCHITECTURE.md): Shell (atendente), Service (rede, sem privilégio), Broker (privilegiado, sem rede) **[H-05]**.
- **Canal local hostil.** A atendente é o adversário desse canal: qualquer processo dela pode se conectar ao pipe. O Service valida tudo, aceita só mensagens tipadas e nunca decide algo sensível com base nelas. O Shell verifica a identidade do processo servidor **[H-17]**.
- **Aplicativos** são abertos pelo Shell, com a conta da atendente.
- **Lista de bloqueio fixa** de interpretadores e ferramentas de sistema, embutida no binário assinado.
- **Políticas por usuário**, só na conta da atendente **[H-03]**.
- **WebView2 endurecido** (ARCHITECTURE, seção 6). Com conta Windows compartilhada, o perfil do navegador é apagado a cada troca de atendente.
- **Manutenção** não desbloqueia a conta da atendente; tem prazo aplicado localmente e aviso na tela.
- **Reversão** local pelo TI, disponível desde a primeira versão que aplica bloqueio.
- **Senha da conta do TI** diferente por máquina, com gestão automática se disponível **[H-10]**.

### Roteiro de fuga

Executado na Fase 0 e repetido antes de cada versão **[H-07]**. Cobre, no mínimo: diálogos de abrir, salvar e imprimir dos aplicativos liberados; ajuda e links que abrem navegador; teclas de acessibilidade; macros e recursos de script dos aplicativos; no WebView2, ferramentas de desenvolvedor, menu de contexto, novas janelas, `file://`, downloads e visualizador de PDF; combinações de teclas do sistema; mídia removível.

## 6. Atualizações

- O **manifesto** é assinado pela chave de release, guardada fora do servidor. O **pacote** tem assinatura de código de um editor fixado no terminal.
- O terminal só aceita **versão maior** que a instalada. Voltar atrás é publicar uma versão nova, de número maior, com o conteúdo anterior.
- O Broker copia o pacote para um diretório gravável só pelo sistema e **verifica de novo ali**, antes de instalar.
- A atualização do próprio Service e do Broker é feita por um processo instalador separado. O Shell é trocado quando não está em uso.
- Se a nova versão não passar na verificação de saúde, o terminal volta sozinho à versão imediatamente anterior, guardada localmente.

## 7. Auditoria

O que é garantido: **adulteração detectável**, não impossível.

- Registro só de inserção para a aplicação; o usuário de banco da aplicação não tem `UPDATE` nem `DELETE`.
- Cada registro encadeia o hash do anterior. As inserções são serializadas para a cadeia não se ramificar.
- **Âncora externa:** o hash mais recente é enviado periodicamente a destinatários fora do TI (RH ou jurídico). Quem administra o banco consegue reescrever a tabela, mas não o que já foi enviado.
- O ator pode ser administrador, atendente, máquina ou sistema.
- **Leituras sensíveis também são auditadas:** histórico individual, auditoria e exportação de titular.

Fora do alcance do painel, e portanto tratados à parte:

| Ação | Tratamento |
|---|---|
| Logon local da conta do TI numa máquina | Coletado pelo terminal e enviado como evento **[H-12]** |
| Acesso direto ao banco de dados | Não é auditado pelo sistema. Depende de separação de funções e do registro do próprio PostgreSQL |
| Console da ferramenta de suporte | Contas do console desativadas; registros importados **[H-06]** |

## 8. Suporte remoto

Requisitos inegociáveis:

- só dentro de **sessão registrada**, com administrador identificado, justificativa e prazo;
- **consentimento da atendente na tela**, ou procedimento formal;
- o procedimento formal exige **autorização de um segundo administrador** e um **aviso prévio na tela** antes do início; ele dispensa o clique da atendente, nunca a visibilidade;
- **aviso permanente** durante a sessão, exibido por processo independente do Shell;
- a atendente pode encerrar a sessão por consentimento; no procedimento formal, o encerramento é do TI e fica registrado;
- **expiração aplicada no terminal**, mesmo sem servidor;
- o agente da ferramenta fica **parado fora de sessão**;
- início, término e ações relevantes vão para a auditoria.

Durante a sessão, o administrador vê o que está na tela, inclusive **dados de clientes da Pontual**. Isso é tratamento de dados pessoais de terceiros e precisa constar no procedimento e no registro de operações.

A ferramenta é escolhida por PoC na Fase 0 **[H-06]**. Se nenhuma cumprir as condições, o módulo não é entregue como descrito.

## 9. Matriz de ameaças

| ID | Ameaça | Controles | Risco residual | Verificação |
|---|---|---|---|---|
| T-01 | Atendente abre programa não autorizado | Lista de permissão de execução, shell substituto, lista fixa | Depende de H-01 e H-02; programa liberado com recurso de script | Roteiro de fuga |
| T-02 | Atendente sai do terminal por diálogo, acessibilidade ou WebView2 | Configuração do WebView2, políticas por usuário | Vetores novos a cada versão de aplicativo | Roteiro de fuga a cada versão |
| T-03 | Atendente encerra Shell, Service ou Broker | Permissões dos serviços, reinício, evento | Shell pode ser encerrado; é reiniciado e gera evento | Teste de terminal |
| T-04 | Processo da atendente fala com o pipe ou se passa pelo Service | Entrada validada, verificação do processo servidor | Depende de H-17; consentimento pode ser dado por processo da própria atendente | Teste de segurança |
| T-05 | Máquina falsa ou clonada | Chave por máquina, aprovação manual, checagem a cada requisição | Sem TPM, administrador local extrai a chave | Teste de integração |
| T-06 | Interceptação na rede local | TLS, CA fixada no Service | Comprometimento da CA | Teste de integração |
| T-07 | Servidor comprometido | Seção 2 | Liberação de programa não listado; indisponibilidade | Modelo de ameaças |
| T-08 | Conta de administrador roubada | Segundo fator, reautenticação, dois administradores para executáveis | Ações de um único administrador (sites, manutenção, pedido de suporte) | Testes de autorização |
| T-09 | Administrador abusa do acesso | Justificativa, segunda pessoa, auditoria com âncora, Auditor independente | Conluio entre dois administradores | Revisão de auditoria |
| T-10 | Reenvio de configuração antiga | Sequência global crescente no conteúdo assinado | — | Teste de unidade |
| T-11 | Atualização maliciosa ou antiga | Chave de release fora do servidor, editor fixado, versão crescente, reverificação | Comprometimento da máquina de release | Teste de atualização |
| T-12 | Troca do pacote entre verificar e instalar | Diretório do sistema e reverificação | — | Teste de segurança |
| T-13 | Suporte remoto fora do fluxo | Agente parado fora de sessão, console sem contas | Depende inteiramente de H-06 | PoC e teste a cada versão |
| T-14 | Suporte sem que a atendente perceba | Aviso local independente, aviso prévio no procedimento formal | Depende de H-06 | PoC |
| T-15 | Adulteração da auditoria | Encadeamento e âncora externa | Detecta, não impede; acesso direto ao banco não é auditado | Verificação periódica da cadeia |
| T-16 | Máquina esquecida em manutenção | Prazo aplicado localmente, aviso na tela | — | Teste de terminal |
| T-17 | Sessão de uma atendente fica para a seguinte | Limpeza do perfil do WebView2, sem gerenciador de senhas | Depende do modelo de conta (seção 10) | Teste E2E |
| T-18 | Vazamento de segredos | Sem segredos no repositório, varredura nos *commits*, chaves fora das máquinas de desenvolvimento | Erro humano | Integração contínua |
| T-19 | Central dos Agentes usada como ponte para a produção | Instância isolada, sem credenciais nem rede de produção | — | Revisão de implantação |
| T-20 | Relógio errado invalida prazos e ordem da auditoria | Servidor de horário interno | Máquina com relógio alterado localmente por administrador | Teste de integração |

## 10. Identidade da atendente

Decisão a fechar na Fase 0. O sistema precisa saber quem está em cada máquina, mas a forma muda a segurança:

| Opção | Vantagem | Risco |
|---|---|---|
| **A. Conta Windows por atendente** (hipótese preferida) | Perfis isolados; não guardamos credencial nenhuma | Em grupo de trabalho, cada conta precisa existir em cada máquina |
| B. Conta Windows compartilhada com login no terminal | Troca rápida de atendente | Perfil compartilhado exige limpeza; login offline exigiria guardar hash da credencial em cada máquina |

Se a opção B for escolhida, o login só funciona com servidor disponível, para não guardar credenciais no terminal.

## 11. LGPD e transparência

### O que é registrado

| Dado | Finalidade |
|---|---|
| Máquina ligada, online ou offline, e horários | Operação e suporte |
| Qual atendente está em qual máquina | Operação e suporte |
| Quais aplicativos autorizados foram abertos e por quanto tempo | Gestão de uso e licenças |
| Tentativas de abrir aplicativo ou site bloqueado | Segurança |
| Eventos técnicos | Manutenção |
| Sessões de suporte remoto | Auditoria |
| Logons locais da conta do TI | Auditoria |

### O que não é registrado

Teclas digitadas; imagens da tela (exceto a transmissão ao vivo em suporte autorizado, que não é gravada); conteúdo de aplicativos, mensagens, documentos ou páginas; títulos de janela e endereços completos de sites; áudio, câmera ou localização.

### Medidas

- **Aviso de monitoramento** redigido com o RH, entregue às atendentes e exibido no terminal.
- **Finalidade e base legal documentadas**, com registro das operações.
- **Relatório de impacto (RIPD)** recomendado antes da produção.
- **Retenção definida para todas as tabelas** e descarte automático (ver [DATABASE.md](DATABASE.md)).
- **Direitos do titular:** exportação dos dados de uma atendente é requisito essencial, com rota própria.
- **Leitura de histórico individual auditada.**
- **Dados de clientes vistos em suporte remoto** tratados no procedimento.
- **Procedimento de incidente** com responsáveis e prazos.

## 12. Riscos que permanecem

Mesmo com tudo implementado como descrito:

1. Um servidor comprometido consegue liberar programas não listados e tirar máquinas de serviço.
2. Dois administradores em conluio aprovam qualquer executável.
3. Quem administra o banco consegue alterar a auditoria; a alteração é detectável, não impedida.
4. Sem TPM, a identidade da máquina é extraível por um administrador local.
5. O bloqueio depende de mecanismos do Windows cujo comportamento na edição real ainda não foi comprovado.
6. Aplicativos liberados podem ter recursos próprios (macros, diálogos) que servem de saída.
7. Sem DNS ou proxy, o controle de sites é parcial.
8. A ausência de acesso remoto oculto depende da ferramenta escolhida.
9. Um administrador local na máquina (o TI) está fora do modelo: ele pode desfazer qualquer controle.

## 13. Desenvolvimento seguro

- Revisão de segurança em toda mudança de autenticação, autorização, bloqueio, atualização ou suporte.
- Testes automatizados de autorização gerados a partir da matriz da seção 3: cada permissão, cada função, cada rota.
- Análise estática, verificação de dependências e varredura de segredos na integração contínua.
- Roteiro de fuga e teste de reversão obrigatórios antes de cada versão do terminal.
- Chaves de release e de assinatura de código fora das máquinas de desenvolvimento.
