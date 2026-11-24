---
description: Manda o Jumento Vinícius criar/mexer nos cards do Trello
argument-hint: um card por linha, ou "mover X para Y" / "listar quadros"
allowed-tools: Agent, Bash
---

O Paulo chamou o Jumento Vinícius. Pedido dele, tal como escreveu:

<pedido>
$ARGUMENTS
</pedido>

Faça assim:

1. Se o pedido acima estiver vazio, não chame agente nenhum: responda em uma
   linha que é para escrever o que quer, dando o exemplo
   `/jumento Arrumar botão Atualizar - trava na Kamila` e lembrando que cada
   linha vira um card.
2. Caso contrário, chame o agente `jumento-vinicius` (ferramenta Agent,
   `subagent_type: "jumento-vinicius"`) passando o pedido **inteiro e sem
   reescrever**, mais o aviso de que, salvo indicação em contrário no texto,
   cada linha é um card e o destino é o quadro/lista padrão salvo.
3. Não crie card você mesmo e não rode o script direto: o trabalho é do Jumento.
   Exceção: se o agente voltar dizendo que falta chave/token do Trello, repasse
   isso ao Paulo com o link <https://trello.com/power-ups/admin> e pare.
4. Quando o agente terminar, repasse ao Paulo a lista do que entrou no Trello
   (título, coluna e link), uma linha por card, e o que falhou se algo falhou.
   Nada de resumo longo nem recap.
