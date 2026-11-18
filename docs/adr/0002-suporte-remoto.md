# ADR 0002 — Ferramenta de suporte remoto a testar

- **Status:** Proposto. Apoiado em documentação e em código-fonte público; nenhuma ferramenta foi instalada ou testada.
- **Data:** 16/11/2026
- **Hipótese:** H-06

## Contexto

O suporte remoto só é aceitável se cumprir as condições de [ARCHITECTURE.md](../ARCHITECTURE.md), seção 7, e de [SECURITY.md](../SECURITY.md), seção 8: rede local, consentimento obrigatório, aviso durante toda a sessão, agente parado fora de sessão, credencial de curta duração emitida pelo nosso sistema, registros exportáveis e licença compatível.

## Comparação

A = atende · C = atende com configuração · N = não atende · ? = a documentação não informa.

| Ferramenta | Rede local | Consentimento obrigatório | Aviso na sessão | Agente parado | Credencial curta | Registros | Licença |
|---|---|---|---|---|---|---|---|
| MeshCentral | C | C | C | C | C | C | A (Apache 2.0) |
| RustDesk, edição aberta | C | C, frágil | ? | C | C (senha, não token) | N | C (AGPL) |
| RustDesk Pro, plano Basic | C | C | ? | C | C | A | A (paga) |
| Sombra do RDP | A | C | **N** | C | ? | C | A |
| Assistência Remota por convite | A | A | ? | C | C | C, frágil | A |
| Quick Assist | **N** | A | ? | — | N | N | A |
| Remote Help (Intune) | **N** | A | ? | — | N | A | paga |

Nenhuma ferramenta cumpre as sete condições sem configuração. Quick Assist e Remote Help dependem da nuvem da Microsoft. A sombra do RDP não tem aviso durante a sessão.

## Decisão proposta

Testar primeiro o **MeshCentral em modo de rede local**, com:

- serviço do agente **parado por padrão**, iniciado pelo Broker só depois do consentimento;
- consentimento e barra de privacidade exigidos no arquivo de configuração do servidor, não na interface;
- sessões abertas por compartilhamento de dispositivo com duração limitada, emitido pelo nosso servidor;
- eventos exportados para a nossa auditoria.

Plano B: RustDesk Pro, plano Basic. Alternativa nativa de menor custo: Assistência Remota por convite.

## Riscos conhecidos do MeshCentral

- O console web tem contas de administrador que conectam fora do nosso fluxo, e o agente oferece terminal, arquivos e execução de comandos. A barreira principal precisa ser o agente parado; contas de uso direto no console não devem existir.
- Não foi encontrada a regra que diz se o operador consegue anular o consentimento definido no servidor. Precisa de teste.
- A chave de tokens de login do servidor é única e funciona como chave mestra.

## O que a prova de conceito precisa demonstrar

Com um servidor e dois PCs sem domínio:

1. Funciona com a rede externa desconectada.
2. O pedido de consentimento aparece sempre, inclusive para administrador do console; recusar ou deixar expirar bloqueia a sessão.
3. A barra de aviso fica visível do início ao fim, mostra quem está conectado e não pode ser fechada pela atendente sem encerrar a sessão.
4. Com o serviço do agente parado, nenhuma conexão é possível, nem pelo console.
5. O compartilhamento expira no prazo e não serve para outra máquina; o que acontece com uma sessão em andamento quando o prazo vence.
6. Os eventos trazem quem, qual máquina, início, fim e consentimento.
7. Tentativas fora do fluxo (console, terminal, arquivos) são bloqueadas e ficam registradas.
8. O agente instala nas edições do Windows realmente usadas.

Se o MeshCentral falhar nos itens 2, 3, 4 ou 7, ele é descartado e testa-se o plano B.
