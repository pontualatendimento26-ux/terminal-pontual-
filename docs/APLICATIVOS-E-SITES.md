# Terminal Pontual — Aplicativos e sites das atendentes

Lista informada pela Pontual em 16/11/2026. Responde à pendência 5 de [REQUIREMENTS.md](REQUIREMENTS.md), seção 5.

Os endereços foram reduzidos ao domínio e, quando útil, à página de entrada. Códigos de sessão, números de proposta e parâmetros de autenticação que vieram nos links originais **não foram registrados**.

Nada aqui foi testado. A coluna "A confirmar" lista o que só o uso real mostra.

## 1. Aplicativos

| Aplicativo | Como é usado hoje | A confirmar |
|---|---|---|
| Excel | Aplicativo instalado | Versão do Office; se as planilhas usam macros; de onde vêm e para onde vão os arquivos |
| WhatsApp Web | Pelo navegador (`web.whatsapp.com`) | Se alguma máquina usa o aplicativo instalado em vez do site |
| Teams | Pelo navegador (`teams.microsoft.com`) | Se alguma máquina usa o aplicativo instalado |

## 2. Sites

### Portais de bancos e promotoras

| # | Domínio | Página de entrada informada |
|---|---|---|
| 1 | `epfweb.safra.com.br` | `/Home/Login` |
| 2 | `portaldecredito.queromaisconsignado.com.br` | `/login` |
| 3 | `teddy.consig360.com.br` | `/` |
| 4 | `fintechdocorban.nossafintech.com.br` | `/home/overview` |
| 5 | `funcaoconsig.digio.com.br` | `/FIMENU/Login/AC.UI.LOGIN.aspx` |
| 6 | `c6.c6consig.com.br` | `/WebAutorizador/Login/AC.UI.LOGIN.aspx` |
| 7 | `www.parceirosantander.com.br` | `/spa-base/landing-page` |
| 8 | `esteira.redconsig.com.br` | `/solicitacao-saldo-aprovadas` |
| 9 | `bemweb.bempromotora.com.br` | `/autenticacao/login` |
| 10 | `front.meucashcard.com.br` | `/WebAppBPOCartao/` |
| 11 | `akipromotora.app` | `/WebFIMenuMVC/Login/AC.UI.LOGIN.aspx` |
| 12 | `panconsig.pansolucoes.com.br` | `/WebAutorizador/` |
| 13 | `correspondente.bb.com.br` | `/cbo-portal-acesso/` |
| 14 | `front.paranabanco.b.br` | `/login` |
| 15 | `finanto.joinbank.com.br` | `/` |
| 16 | `consignado.daycoval.com.br` | `/Autorizador/` |
| 17 | `portaldecredito.daycoval.com.br` | `/login` |

### Comunicação

| # | Domínio | Uso |
|---|---|---|
| 18 | `web.whatsapp.com` | WhatsApp Web |
| 19 | `teams.microsoft.com` | Teams |
| 20 | `mail.google.com` | Gmail |

### Sistema interno

| # | Endereço | Observação |
|---|---|---|
| 21 | `http://192.168.0.243:3000` | Servidor na rede local, páginas `/html/login.html` e `/html/instancias.html`. Usa HTTP, sem criptografia |

### Site da empresa

| # | Domínio | Página de entrada informada |
|---|---|---|
| 22 | `www.pontualconsig.com.br` | `/index.html` |

## 3. O que esta lista muda no projeto

### Cada site usa mais de um domínio

O login do Paraná Banco já mostra isso: a entrada é em `front.paranabanco.b.br`, mas a autenticação passa por `api.paranabanco.com.br` e por `portalcentralcorrespondente.paranabanco.com.br`. O mesmo vale, com alta probabilidade, para os demais portais (login, arquivos estáticos, captcha) e com certeza para WhatsApp, Teams e Gmail, que carregam conteúdo de vários domínios.

Consequência: a lista de permissão de sites não pode ser montada só com os 22 domínios acima. Cada site precisa passar por um **período de observação**, em que o terminal registra os domínios realmente usados, antes de o bloqueio ser ligado. Isso entra na Fase 2.

### O sistema interno usa HTTP

[ARCHITECTURE.md](ARCHITECTURE.md), seção 6, manda recusar tudo o que não for `https`. O sistema em `192.168.0.243:3000` exigiria uma exceção nominal para esse endereço, ou a publicação dele em HTTPS. Uma exceção significa que o que trafega entre a atendente e esse sistema, inclusive a senha, pode ser lido por quem estiver na rede local.

### Gmail, WhatsApp Web e Teams são canais de saída de dados

O negócio lida com dados pessoais e financeiros de clientes (crédito consignado). Com e-mail, WhatsApp e Teams liberados, qualquer dado visto na tela pode ser enviado para fora, por texto ou anexo. Isso é a ameaça N-14 do [modelo de ameaças](THREAT-MODEL.md).

O bloqueio do terminal não resolve isso, porque essas ferramentas são necessárias ao trabalho. O que cabe decidir:

- se o Gmail é a conta corporativa ou pessoal, e se dá para restringir à corporativa;
- se o envio e o recebimento de arquivos por essas ferramentas são necessários;
- se o risco é aceito e documentado como tal.

### Excel é uma porta de saída do bloqueio

Macros, o diálogo de abrir e salvar arquivos e os suplementos permitem navegar no disco e executar código. Isso é parte da ameaça N-03. O teste de fuga da Fase 0 precisa cobrir o Excel com as planilhas reais, e as macros devem ser desligadas por política se não forem usadas.

### Vários portais usam a mesma plataforma

Os endereços com `AC.UI.LOGIN.aspx` e `WebAutorizador` (Digio, C6, Aki, Pan, Daycoval) indicam o mesmo fornecedor de sistema. O comportamento no navegador embutido tende a ser igual entre eles, o que simplifica o teste: validar um deles a fundo e conferir os demais.

## 4. Perguntas para a Pontual

1. O Excel usa macros? Qual é a versão do Office?
2. O Gmail é corporativo (domínio da empresa) ou conta pessoal de cada atendente?
3. As atendentes precisam enviar e receber arquivos pelo WhatsApp, pelo Teams ou pelo e-mail? Baixar arquivos dos portais?
4. O que é o sistema em `192.168.0.243:3000`? É da própria Pontual? Dá para colocá-lo em HTTPS?
5. Algum portal exige certificado digital, token físico, leitor de cartão ou extensão do navegador?
6. Todas as atendentes usam todos os sites, ou há grupos com listas diferentes?
7. As atendentes imprimem? Em qual impressora?
8. Algum desses sites abre janelas novas (pop-ups) ou gera PDF para baixar?
