# ADR 0004 — Senha da conta administrativa do TI sem LAPS

- **Status:** Proposto.
- **Data:** 16/11/2026
- **Hipótese:** H-10 (derrubada pela documentação)

## Contexto

Se a conta administrativa local do TI tiver a mesma senha em todas as máquinas, quem a descobre em uma máquina entra em todas. A hipótese H-10 era usar o Windows LAPS para ter uma senha diferente por máquina, trocada automaticamente.

## O que a documentação oficial diz

O Windows LAPS está presente no Windows Pro, mas só gerencia a senha em máquinas ingressadas no Active Directory ou no Entra ID. Sem diretório configurado, a opção de backup fica desabilitada e as demais configurações são ignoradas. Não há modo documentado para grupo de trabalho.

Fonte: learn.microsoft.com, `windows-server/identity/laps/laps-overview` e `laps-management-policy-settings`.

## Decisão proposta

Como as máquinas estão em grupo de trabalho, **o LAPS não se aplica**. Enquanto for assim:

1. A conta do TI tem **senha diferente em cada máquina**, gerada aleatoriamente.
2. As senhas ficam em um **cofre de senhas** do TI, fora do nosso sistema.
3. A troca é manual, em intervalo definido pelo TI e sempre que alguém da equipe sair.

O Terminal Pontual não armazena nem gerencia essa senha.

## Alternativa a avaliar

Ingressar as máquinas no Entra ID permitiria usar o LAPS; a licença gratuita do Entra ID basta para o backup da senha. Isso muda a premissa de rede local sem dependência de nuvem e entra na avaliação "construir ou adotar" da Fase 0.

## Consequências

- O controle depende de disciplina do TI, não de automação.
- O logon local da conta do TI continua sendo registrado como evento, se a hipótese H-12 for comprovada.
