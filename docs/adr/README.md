# Registros de decisão de arquitetura

Cada arquivo registra uma decisão, o contexto, as alternativas e as consequências. As hipóteses **H-xx** são as de [ARCHITECTURE.md](../ARCHITECTURE.md), seção 10.

Significado do status:

- **Proposto**: decisão apoiada em documentação, ainda sem teste em máquina. Não pode ser tratada como fato.
- **Aceito**: comprovado por teste e aprovado pela Pontual.
- **Substituído**: trocado por outro ADR.

| Nº | Decisão | Status | Hipóteses |
|---|---|---|---|
| [0001](0001-bloqueio-do-windows.md) | Mecanismos de bloqueio do Windows para a conta da atendente | Proposto | H-01, H-02, H-03, H-04 |
| [0002](0002-suporte-remoto.md) | Ferramenta de suporte remoto a testar | Proposto | H-06 |
| [0003](0003-identificacao-da-atendente.md) | Uma conta Windows por atendente | Proposto | — |
| [0004](0004-senha-da-conta-do-ti.md) | Senha da conta administrativa do TI sem LAPS | Proposto | H-10 |
| [0005](0005-construir-ou-adotar.md) | Construir ou adotar: caminho misto | Proposto | — |

As pesquisas que embasam estes registros foram feitas em 16/11/2026, na documentação oficial da Microsoft e das ferramentas avaliadas. Nada foi testado em máquina ainda.
