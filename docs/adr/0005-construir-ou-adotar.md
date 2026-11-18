# ADR 0005 — Construir ou adotar

- **Status:** Proposto. Apoiado em documentação e páginas de preço dos fornecedores, vistas em 16/11/2026. Preços são aproximados.
- **Data:** 16/11/2026

## Contexto

A revisão de arquitetura pediu que se avaliasse, antes de programar, se parte do Terminal Pontual deveria ser uma ferramenta pronta de gerenciamento (MDM ou RMM) em vez de código próprio. O cenário é: 10 máquinas Windows Pro, em grupo de trabalho, TI pequeno, exigência de funcionar em rede local sem nuvem, transparência com as atendentes e custo baixo.

As partes avaliadas: (a) bloqueio, (b) distribuição de configuração, (c) inventário, status e histórico de uso, (d) atualização remota, (e) suporte remoto, (f) painel com o escritório virtual 2D.

## O que foi encontrado

| Opção | (a) | (b) | (c) | (d) | (e) | Rede local? | Custo aproximado, 10 máquinas | Limitação principal |
|---|---|---|---|---|---|---|---|---|
| Microsoft Intune, plano 1 | Sim | Sim | Parcial | Sim | Com complemento pago | **Não** | US$ 80 por mês; cerca de US$ 115 com suporte remoto | Exige internet e contas Entra ID |
| Domínio próprio com Samba e GPO | Sim | Sim | Não | Não | Não | Sim | Sem licença; um servidor | Complexidade de domínio; ponto único de falha |
| Action1 | Não | Parcial | Sim | Sim | Sim | **Não** | Gratuito até 200 máquinas | Só nuvem |
| NinjaOne | Não | Parcial | Sim | Sim | Sim | **Não** | Sob cotação | Nuvem e contrato mínimo |
| Tactical RMM | Não | Parcial | Sim | Sim | Sim | Sim | Sem licença; um servidor Linux | Exige domínio e certificado; sem quiosque |
| ManageEngine Endpoint Central, edição gratuita | Parcial | Parcial | Sim | Sim | Sim | Sim | Gratuito até 25 máquinas | Produto pesado; recursos da edição gratuita não confirmados |
| PDQ Connect | Não | Parcial | Sim | Sim | No plano superior | **Não** | US$ 1.200 a 1.800 por ano | Mínimo de 100 dispositivos |
| SiteKiosk | Sim | Sim | Parcial | Parcial | Não | Sob consulta | Cerca de € 2.150 por ano | Caro; voltado a quiosque público |

O que **nenhuma** opção cobre: o painel com o escritório virtual, a interface da atendente, o histórico de uso por atendente com transparência (aviso, coleta declarada, retenção), e uma fonte única da lista de aplicativos e sites que alimente ao mesmo tempo o lançador, o AppLocker e o filtro de sites.

## Decisão proposta

**Caminho misto.**

Adotar:

- **Bloqueio (a):** os mecanismos que já vêm no Windows Pro, aplicados pelo nosso Broker. Ver [ADR 0001](0001-bloqueio-do-windows.md).
- **Suporte remoto (e):** uma ferramenta auto-hospedada pronta. Ver [ADR 0002](0002-suporte-remoto.md).

Construir:

- Interface da atendente e painel com o escritório virtual (f).
- Agente com presença, inventário, histórico de uso e alertas (c).
- Distribuição da lista de aplicativos e sites (b).
- Atualização do próprio agente (d).

## Motivos

- As ferramentas que cobrem política e inventário de forma integrada são de nuvem e violam a exigência de rede local.
- As auto-hospedadas trazem um servidor e uma operação maiores que o problema de 10 máquinas, e ainda deixam o bloqueio e o painel por fazer.
- O painel em tempo real exige telemetria própria; com ela, o inventário e o histórico saem quase sem custo adicional.
- Reimplementar suporte remoto seria o trabalho mais caro e mais sensível do ponto de vista de segurança e de LGPD.

Esta decisão confirma a arquitetura já documentada. Ela não acrescenta nem remove componentes.

## Alternativa a testar dentro do bloqueio

O Acesso Atribuído em modo de experiência restrita pode ser aplicado pelo Broker, sem Intune, aceita conta local padrão e gera as regras do AppLocker sozinho. Em contrapartida, ele mantém a área de trabalho restrita do Windows em vez de um aplicativo único em tela cheia, e algumas de suas políticas atingem todas as contas, inclusive a do TI. Entra no teste do ADR 0001 como segunda opção.

## Quando rever

- Se a Pontual aceitar dependência de nuvem: o Intune passa a cobrir (a), (b), (d) e o LAPS, e o escopo do Broker diminui.
- Se o número de máquinas crescer para centenas: um domínio ou uma ferramenta de gerenciamento passa a compensar.
