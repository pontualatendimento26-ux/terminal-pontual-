# ADR 0003 — Uma conta Windows por atendente

- **Status:** Proposto. Depende da confirmação da Pontual.
- **Data:** 16/11/2026

## Contexto

O sistema precisa saber quem está em cada máquina (requisito RF-110). [SECURITY.md](../SECURITY.md), seção 10, deixou duas opções: conta Windows por atendente, ou conta compartilhada com login no terminal.

A Pontual informou que **cada atendente usa sempre a mesma máquina** e que as máquinas estão em grupo de trabalho.

## Decisão proposta

Cada máquina tem **uma conta local padrão própria da sua atendente**. A identificação é a própria sessão do Windows; o terminal não tem tela de login nem guarda credenciais.

## Motivos

- Com máquina fixa, basta uma conta por máquina. O custo da opção em grupo de trabalho (criar cada conta em cada máquina) não se aplica.
- O perfil do navegador embutido fica isolado por pessoa. Não há sessões nem senhas passando de uma atendente para outra.
- Nenhum hash de credencial fica nas máquinas nem no nosso banco. A coluna `attendants.credential_hash` e a rota `/device/attendant-login` deixam de ser necessárias.
- Funciona sem servidor.

## Consequências

- Uma substituição temporária (férias, falta) usa a conta da titular ou exige criar outra conta na máquina. O procedimento precisa ser definido com a Pontual.
- A senha da conta é responsabilidade da atendente e do Windows; a política de senha local precisa ser definida.
- Se a Pontual passar a ter revezamento de máquinas, este ADR é revisto.

## Pendência

Confirmar com a Pontual como são tratadas as substituições.
