# Terminal Pontual — Conceito das interfaces 2D

Status: **revisão 2**, protótipo conceitual. Incorpora a revisão do agente `ecc:architect` e as decisões aprovadas pela Pontual. Os esquemas abaixo mostram composição e comportamento; não são arte final. Itens marcados com **[H-xx]** são hipóteses listadas em [ARCHITECTURE.md](ARCHITECTURE.md), seção 10.

## 1. Direção de arte

- **Estilo:** pixel art 2D, vista de cima em três quartos, clima acolhedor de jogo de gerenciamento.
- **Originalidade:** toda a arte é própria da Pontual. Nenhum personagem, cenário, sprite ou paleta de outro jogo é reutilizado.
- **Grade:** blocos de 16×16 px, ampliados em múltiplos inteiros (3× ou 4×), sem suavização.
- **Paleta:** restrita (cerca de 32 cores), derivada das cores da marca Pontual, com tons quentes de madeira e luz para o ambiente.
- **Personagens:** 16×24 px, proporção simpática, variações de cabelo, pele e roupa para que cada estação seja reconhecível.
- **Tipografia:** fonte de pixel para rótulos no cenário; fonte normal e legível nos painéis.
- **Movimento:** animações curtas em ciclo (4 a 8 quadros). Opção de reduzir movimento.

Produção: sprites no Aseprite ou LibreSprite, cenário montado no Tiled, exportados como folhas de sprites e mapa em JSON para o Phaser.

## 2. Escritório virtual (Módulo 3)

### Composição da tela

```
┌──────────────────────────────────────────────────────────────┬───────────────────┐
│ Pontual · Escritório    Unidade: Matriz ▾   [Cenário][Lista] │  ESTAÇÃO 04       │
├──────────────────────────────────────────────────────────────┤  ───────────────  │
│  ▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓ janela ▓▓▓▓▓▓▓▓ janela ▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓  │  Atendente        │
│  ▓ planta                                       quadro    ▓  │  Nome da máquina  │
│  ▓   ┌──┐    ┌──┐    ┌──┐    ┌──┐    ┌──┐                 ▓  │  ● Online         │
│  ▓   │01│    │02│    │03│    │04│    │05│     bebedouro   ▓  │                   │
│  ▓   └☺─┘    └☺─┘    └──┘    └☺─┘    └☺─┘                 ▓  │  Em uso agora     │
│  ▓    ●       ●       ○       ▲       ●                   ▓  │  · aplicativo A   │
│  ▓                                                        ▓  │  · aplicativo B   │
│  ▓   ┌──┐    ┌──┐    ┌──┐    ┌──┐    ┌──┐                 ▓  │                   │
│  ▓   │06│    │07│    │08│    │09│    │10│     estante     ▓  │  Ativa há         │
│  ▓   └☺─┘    └──┘    └☺─┘    └☺─┘    └☺─┘                 ▓  │  Última comunic.  │
│  ▓    ●       ✚       ●       ●       ●                   ▓  │                   │
│  ▓ tapete                                  porta do TI    ▓  │  Alertas (1)      │
│  ▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓  │  ▲ descrição      │
├──────────────────────────────────────────────────────────────┤                   │
│ ● 7 online   ○ 1 offline   ▲ 1 alerta   ✚ 1 manutenção       │ [Histórico]       │
│                                                              │ [Configurações]   │
│                                                              │ [Suporte remoto]  │
└──────────────────────────────────────────────────────────────┴───────────────────┘
```

- O cenário (Phaser) ocupa a área principal. O painel lateral (React) abre ao clicar em uma estação.
- A barra inferior resume o estado geral e funciona como filtro.
- A alternância **Cenário / Lista** oferece as mesmas informações em tabela.
- Em modo de edição, o administrador arrasta as estações para refletir a disposição real da sala.

### Estados de uma estação

Cada estado combina forma, cor e movimento, para não depender só de cor.

| Estado | Origem do dado | Monitor | Personagem | Indicador |
|---|---|---|---|---|
| Online | presença recebida há menos de 90 s | aceso, brilho pulsando de leve | sentado, digitando | ● ponto verde |
| Online, sem atendente | máquina online, sem sessão de atendente | aceso, tela de entrada | cadeira vazia | ● ponto verde |
| Offline | sem presença | apagado | ausente, cadeira afastada | ○ contorno cinza |
| Alerta | alerta aberto para a máquina | piscando em âmbar | olhando para o monitor | ▲ balão de alerta saltando |
| Manutenção | modo manutenção ativo | tela de ferramentas | personagem do TI ao lado da mesa | ✚ placa de manutenção |
| Suporte remoto (a partir da Fase 6) | sessão de suporte ativa | moldura destacada | atendente e personagem do TI | ícone de conexão animado |

Estes seis estados são os mesmos do requisito RF-305. A manutenção indica máquina fora de serviço, com o TI trabalhando na própria conta; ela não representa desbloqueio da conta da atendente.

Se o painel perder a conexão com o servidor, o cenário inteiro fica esmaecido com o aviso "sem conexão com o servidor". Ele não continua mostrando o último estado como se fosse atual.

### Interação

1. Passar o cursor: realce da estação e rótulo com o nome da máquina.
2. Clicar: abre o painel com os dados vindos de `GET /devices/{id}`, atualizados por SignalR.
3. Botões do painel: histórico e configurações, conforme as permissões do usuário. O botão de suporte remoto aparece **desabilitado**, com a indicação "disponível em versão futura", até o Módulo 4 existir (Fase 6).
4. Teclado: Tab percorre as estações; Enter abre o painel.

## 3. Central dos agentes de IA (Módulo 5)

### Composição da tela

```
┌──────────────────────────────────────────────────────────────┬───────────────────┐
│ Pontual · Central dos Agentes       Fonte: Claude Code ▾     │  security-reviewer│
├──────────────────────────────────────────────────────────────┤  ───────────────  │
│  ▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓  │  Especialidade    │
│  ▓  quadro de tarefas          sala de testes             ▓  │  Segurança        │
│  ▓  ┌────────────┐             ┌───────┐                  ▓  │                   │
│  ▓  │ ▪ ▪ ▪  ▫ ▫ │              │ ☺ ⚙  │  QA              ▓  │  Status           │
│  ▓  └────────────┘             └───────┘                  ▓  │  Analisando       │
│  ▓                                                        ▓  │                   │
│  ▓   ┌──┐ arquiteto    ┌──┐ backend     ┌──┐ frontend     ▓  │  Tarefa atual     │
│  ▓   │☺ │ ✎            │☺ │ ⌨           │  │ (vazio)      ▓  │  descrição        │
│  ▓   └──┘              └──┘             └──┘              ▓  │                   │
│  ▓                                                        ▓  │  Progresso        │
│  ▓   ┌──┐ segurança    ┌──┐ banco                         ▓  │  sem dados        │
│  ▓   │☺ │ 🔍           │☺ │ ✓        área de descanso     ▓  │                   │
│  ▓   └──┘              └──┘             ☺ zZ              ▓  │  Arquivos         │
│  ▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓  │  · caminho/arq    │
├──────────────────────────────────────────────────────────────┤                   │
│ Atividade recente                                            │  Histórico        │
│ 14:03 security-reviewer  leu LoginEndpoint.cs                │  · execuções      │
│ 14:02 backend            editou DeviceHub.cs                 │    anteriores     │
└──────────────────────────────────────────────────────────────┴───────────────────┘
```

- Esta tela **não é a interface das atendentes**. Ela mostra os agentes de IA que desenvolvem o sistema e é usada pela equipe técnica.
- Esta tela pertence a uma **instância isolada** (`Pontual.AgentHub`), separada do painel de produção, sem acesso às máquinas nem às credenciais de produção.
- A cena é feita em Phaser e atualizada em tempo real pelos eventos dos *hooks* do Claude Code (arquitetura em [ARCHITECTURE.md](ARCHITECTURE.md), seção 13).
- Um subagente aparece ligado ao agente que o lançou, por uma linha ou pela proximidade das mesas.
- Os rótulos das mesas no esquema acima são ilustrativos. Na tela, cada mesa mostra o **nome real do agente**.
- Cada mesa pertence a um **tipo de agente real**. O personagem só aparece na mesa quando há uma execução ativa daquele tipo.
- Se a origem não informar qual agente executou uma ferramenta **[H-13]**, a atividade aparece numa mesa única da sessão. Ela nunca é atribuída a um agente presumido.
- Agentes sem execução ficam fora de cena ou na área de descanso. Mesas vazias continuam vazias.
- O quadro de tarefas mostra itens reais quando a origem os informa.
- A faixa inferior é o fluxo de eventos recebidos, em ordem cronológica.

### Estados dos personagens

| Estado | Evento real que o provoca | Animação |
|---|---|---|
| Programando | ferramenta de edição ou escrita em andamento | digitando rápido, código subindo na tela |
| Analisando | ferramenta de leitura ou busca | lupa, folheando papéis |
| Testando | a origem declarou atividade de teste, por uma lista configurável de comandos de teste | na sala de testes, engrenagem girando |
| Trabalhando | outra ferramenta em andamento | digitando em ritmo normal |
| Aguardando | esperando entrada ou permissão | parado, balão de reticências |
| Concluído | execução encerrada sem erro | comemora e volta à área de descanso |
| Com erro | falha de ferramenta ou de execução | balão vermelho com exclamação, tela piscando |

### Personagens e agentes reais

Os personagens correspondem a agentes que existem de fato, e cada um é exibido com o **nome real do agente**. A especialidade é um rótulo descritivo ao lado do nome, não um papel inventado: um agente de revisão aparece como revisor, não como desenvolvedor.

O mapeamento entre agente e personagem é **configuração** (tabela `agent_personas`), não código. Com o ECC instalado hoje, a configuração inicial possível é:

| Agente real | Especialidade exibida |
|---|---|
| `architect`, `code-architect`, `planner` | Arquitetura e planejamento |
| `csharp-reviewer` | Revisão de C# e .NET |
| `database-reviewer` | Revisão de banco de dados |
| `react-reviewer`, `typescript-reviewer` | Revisão de frontend |
| `security-reviewer` | Revisão de segurança |
| `tdd-guide`, `e2e-runner`, `pr-test-analyzer` | Testes |

O ECC não tem agente desenvolvedor de backend ou de frontend, nem especialista em Windows ou DevOps. A implementação em si é feita pela sessão principal, que aparece na mesa da sessão. Para ter personagens de Windows e DevOps, a proposta é criar agentes próprios do projeto em `.claude/agents/`. Enquanto não existirem, esses personagens não aparecem.

### Progresso

A barra de progresso só é desenhada quando a origem envia progresso real (por exemplo, itens concluídos de uma lista de tarefas). Caso contrário, o painel mostra "sem dados de progresso".

## 4. Acessibilidade e desempenho

- Visão em lista equivalente para as duas cenas.
- Estados com forma e texto, não só cor.
- Navegação por teclado e rótulos para leitores de tela nos painéis.
- Opção de reduzir movimento, respeitando a preferência do sistema.
- Uma única folha de sprites por cena; meta de 60 quadros por segundo com 100 estações visíveis.
