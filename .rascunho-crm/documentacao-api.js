// documentacao-api.js
//
// Motor genérico da tela de Documentação da API. A ideia: cada rota que
// você QUISER mostrar pra alguém de fora vira um objeto dentro de
// CATEGORIAS, e a tela inteira (nav lateral, detalhe, exemplo de código
// em 6 linguagens, painel de teste ao vivo) se monta sozinha a partir
// disso. Nenhuma rota real foi cadastrada ainda — é só o ecossistema.
//
// Formato de cada rota (exemplo, não está ativo em nenhuma categoria):
// {
//   id: 'gerar-link-sso',              // único, usado nos data-attributes
//   titulo: 'Gerar link de acesso',    // nome simples, aparece na nav e no detalhe
//   metodo: 'POST',                    // GET | POST | PUT | DELETE | PATCH
//   path: '/api/integracao/sso/gerar-link', // sem domínio — a tela completa com a origem atual
//   descricao: 'O que essa rota faz, em 1 ou 2 frases simples, sem jargão.',
//   auth: {                            // null se a rota for pública
//     header: 'Authorization',
//     formato: 'Bearer <token>',
//     exemplo: 'Bearer 8f2a...',       // valor de exemplo pro snippet de código
//     descricao: 'Token fixo de integração.',
//   },
//   baseUrl: 'http://192.168.0.243:3000', // opcional — se ausente, usa a origem atual da página
//   parametros: [
//     {
//       nome: 'atendente',
//       local: 'body',                 // path | body | query | header — "path" é pedaço tipo :id no caminho
//       tipo: 'string',
//       obrigatorio: true,
//       exemplo: 'Nome Completo',
//       descricao: 'Nome cadastrado do usuário no CRM.',
//     },
//   ],
//   respostas: [
//     { status: 200, label: 'sucesso', corpo: { success: true } },
//     { status: 400, label: 'validação', corpo: { success: false, message: '...' } },
//   ],
//   testavel: true,
//   // Os parâmetros aparecem documentados NOS DOIS casos — o que muda é
//   // só poder disparar a chamada daqui:
//   // true  → ação segura de repetir à toa (GET, ou POST/PUT que só
//   //         consulta): campos viram INPUT editável + botão
//   //         "Experimentar", rodando contra o servidor de verdade.
//   // false → ação de efeito real (manda mensagem, apaga, move o cliente,
//   //         transfere atendimento...): mesmos campos, mesma explicação,
//   //         mas só de leitura — cada um mostrando o valor de exemplo, e
//   //         sem botão de disparar. Mesmo desenho da doc da Evolution
//   //         API. Ver renderCampo(p, rota, editavel).
//   avisoTeste: 'Por que esta rota não tem teste ao vivo.',
//   // Só vale com testavel:false. Sem isto, o texto padrão fala em
//   // "efeito real (apaga, move, transfere...)" — que é mentira numa
//   // rota de leitura pesada, onde o motivo é o tamanho da resposta.
//
//   testeExigeUmDe: ['telefone', 'atendente'],
//   // Trava o botão "Experimentar" enquanto NENHUM desses campos estiver
//   // preenchido. Para rota que, sem filtro, devolveria a base inteira: a
//   // rota de verdade continua aceitando a chamada sem filtro (é o uso do
//   // parceiro), o que fica barrado é só disparar isso a partir da tela.
// }
//
// ===== DUAS REGRAS PERMANENTES DA TELA DE TESTE =====
// Valem pra toda rota, inclusive as que forem cadastradas depois. As duas
// tratam do mesmo risco: o "Experimentar" roda no navegador de quem está
// lendo a documentação, então uma resposta enorme trava a página de quem
// só queria conferir o formato.
//
// 1. LIMITE DE EXIBIÇÃO (LIMITE_ITENS_TESTE, aplicado em
//    limitarParaExibicao): toda lista que voltar de um teste é cortada nos
//    10 primeiros itens ANTES de virar JSON na tela, com uma linha final
//    dizendo quantos ficaram de fora. É corte de EXIBIÇÃO apenas — o
//    servidor devolveu tudo, e quem chamar a rota de outro sistema recebe
//    a lista inteira normalmente.
//
// 2. FILTRO OBRIGATÓRIO (testeExigeUmDe): rota que sem filtro devolveria a
//    base inteira só libera o botão depois que a pessoa restringir a
//    busca. Vale só aqui na tela; a rota segue aberta pra quem a consome.
const LIMITE_ITENS_TESTE = 10;
const CATEGORIAS = [
  {
    titulo: 'Autenticação',
    rotas: [
      {
        id: 'agex-login',
        titulo: 'Gerar token',
        metodo: 'POST',
        path: '/login',
        descricao:
          'Use o login e a senha da conta dedicada do agente pra gerar um token — válido por 24 horas. Depois de gerado aqui, ele passa a ser usado automaticamente em todas as outras rotas do Agente Externo enquanto esta aba ficar aberta, sem precisar colar em cada uma.',
        auth: null,
        parametros: [
          { nome: 'username', local: 'body', tipo: 'string', obrigatorio: true, exemplo: 'usuário', descricao: 'Login da conta dedicada do agente.' },
          { nome: 'password', local: 'body', tipo: 'password', obrigatorio: true, exemplo: '••••••••', descricao: 'Senha da conta dedicada.' },
        ],
        respostas: [
          { status: 200, label: 'autenticado', corpo: { success: true, token: 'eyJhbGciOiJIUzI1NiIs...', user: { id: 12, name: 'Agente Parceiro', username: 'agente.parceiro', role: 'user' } } },
          { status: 401, label: 'usuário ou senha inválidos', corpo: { success: false, message: 'Senha incorreta' } },
        ],
        testavel: true,
      },
    ],
  },
  {
    titulo: 'SSO — Login sem senha',
    rotas: [
      {
        id: 'sso-gerar-link',
        titulo: 'Gerar link de acesso',
        metodo: 'POST',
        path: '/api/integracao/sso/gerar-link',
        descricao:
          'Chamada feita pelo OUTRO sistema (servidor contra servidor, nunca pelo navegador do correspondente) no momento em que ele clica pra entrar no CRM por lá. Devolve um link de entrada de uso único, válido por 90 segundos.',
        auth: {
          header: 'Authorization',
          formato: 'Bearer <token>',
          exemplo: null, // preenchido com o token real ao carregar a página (ver carregarConfig)
          descricao: 'Token fixo de integração, combinado uma vez com o parceiro — não expira.',
        },
        parametros: [
          {
            nome: 'atendente',
            local: 'body',
            tipo: 'string',
            obrigatorio: true,
            exemplo: 'Nome Completo Do Atendente',
            descricao: 'Nome cadastrado do usuário no CRM (não é o login). Não diferencia maiúsculas/minúsculas nem espaços nas pontas.',
          },
        ],
        respostas: [
          { status: 200, label: 'link gerado', corpo: { success: true, redirectUrl: 'http://192.168.0.243:3000/html/sso-entrar.html?codigo=a1b2c3...', expiraEm: 90 } },
          { status: 400, label: 'atendente ausente', corpo: { success: false, message: 'Campo "atendente" é obrigatório.' } },
          { status: 401, label: 'token de integração inválido', corpo: { success: false, message: 'Token de integração inválido.' } },
          { status: 404, label: 'atendente não encontrado', corpo: { success: false, message: 'Atendente não encontrado.' } },
          { status: 409, label: 'nome ambíguo', corpo: { success: false, message: 'Mais de um usuário com esse nome — não dá pra saber qual é.' } },
        ],
        testavel: true,
      },
    ],
  },
  {
    titulo: 'Clientes',
    rotas: [
      {
        id: 'clientes-listar',
        titulo: 'Listar carteira de clientes',
        metodo: 'GET',
        path: '/api/integracao/clientes',
        descricao:
          'Devolve todos os clientes do CRM, cada um com o telefone (só números: DDI + DDD + número) e o nome do atendente responsável por ele no momento. Chamada servidor a servidor. É uma varredura completa da base — guarde o resultado do seu lado em vez de pedir em laço.',
        auth: {
          header: 'Authorization',
          formato: 'Bearer <token>',
          exemplo: null, // preenchido com o token real ao carregar a página (ver carregarConfig)
          descricao: 'Mesmo token fixo de integração usado no SSO — combinado uma vez com o parceiro, não expira.',
        },
        parametros: [
          { nome: 'telefone', local: 'query', tipo: 'string', obrigatorio: false, exemplo: '5511999999999', descricao: 'Opcional. Traz só esse cliente. Só números (DDI + DDD + número) — pontuação é ignorada. Telefone que não existe devolve erro, não lista vazia.' },
          { nome: 'atendente', local: 'query', tipo: 'string', obrigatorio: false, exemplo: 'Kamila Ribeiro', descricao: 'Opcional. Traz a carteira desse atendente. É o nome cadastrado no CRM (não o login), sem diferenciar maiúsculas nem espaços nas pontas. Nome que não existe devolve erro.' },
          { nome: 'limite', local: 'query', tipo: 'número', obrigatorio: false, exemplo: '5000', descricao: 'Opcional. Sem ele vem a lista inteira de uma vez, que é o uso normal. Use só se quiser quebrar a resposta em partes (máximo 50000 por chamada).' },
          { nome: 'depois_de', local: 'query', tipo: 'string', obrigatorio: false, exemplo: '5511999999999', descricao: 'Opcional, só faz sentido junto de "limite": repita aqui o valor que veio em "proximo" pra pegar a parte seguinte da lista.' },
        ],
        respostas: [
          {
            status: 200,
            label: 'lista',
            corpo: {
              success: true,
              quantidade: 12107,
              proximo: null,
              clientes: [
                { telefone: '5511999999999', responsavel: 'Kamila Ribeiro' },
                { telefone: '556799887766', responsavel: 'Bianca Almeida Passos' },
                { telefone: '5521988776655', responsavel: null },
              ],
            },
          },
          { status: 401, label: 'token de integração inválido', corpo: { success: false, message: 'Token de integração inválido.' } },
          { status: 404, label: 'telefone ou atendente não encontrado', corpo: { success: false, message: 'Atendente "Fulana de Tal" não encontrado.' } },
          { status: 409, label: 'nome de atendente ambíguo', corpo: { success: false, message: 'Mais de um usuário chamado "Maria" — não dá pra saber qual é.' } },
          { status: 500, label: 'erro interno', corpo: { success: false, message: 'Erro interno ao listar os clientes.' } },
        ],
        testavel: true,
        // Sem filtro, a rota devolve os ~12 mil clientes — resposta que a
        // tela não tem por que carregar. Chamada de fora segue livre.
        testeExigeUmDe: ['telefone', 'atendente'],
      },
    ],
  },
  {
    titulo: 'Agente Externo',
    rotas: [
      {
        id: 'agex-mensagens',
        titulo: 'Enviar mensagem',
        metodo: 'POST',
        path: '/api/agente-externo/v1/conversas/:id/mensagens',
        descricao: 'Manda uma mensagem de texto, um arquivo (por URL), uma localização (cartão de mapa do WhatsApp) ou uma nota interna pro cliente dessa conversa, pelo WhatsApp.',
        auth: { header: 'Authorization', formato: 'Bearer <token>', exemplo: null, descricao: 'Token obtido em "Autenticar (obter token)".' },
        parametros: [
          { nome: 'id', local: 'path', tipo: 'string', obrigatorio: true, exemplo: '284841', descricao: 'ID da conversa — use o valor que chega no webhook em conversa.id, e que precisa pertencer ao funil deste agente. NÃO é um número fixo: cada conversa tem o seu, e ele muda se o contato for apagado e recriado.' },
          { nome: 'texto', local: 'body', tipo: 'string', obrigatorio: false, exemplo: 'Olá! Sua proposta já está pronta.', descricao: 'Texto da mensagem. Obrigatório se não mandar "arquivo" nem marcar "interna".' },
          { nome: 'arquivo', local: 'body', tipo: 'objeto', obrigatorio: false, exemplo: '{"url":"https://exemplo.com/proposta.pdf"}', descricao: 'Manda um arquivo por URL — o CRM baixa e reenvia pelo WhatsApp. Não aceita junto com "interna".' },
          { nome: 'interna', local: 'body', tipo: 'boolean', obrigatorio: false, exemplo: 'false', descricao: 'true grava uma nota interna no histórico, sem mandar nada pro cliente de verdade.' },
          { nome: 'localizacao', local: 'body', tipo: 'objeto', obrigatorio: false, exemplo: '{"id":102}', descricao: 'Manda o cartão de localização do WhatsApp (mapa que abre no Maps). Use {"id": ...} com uma das cadastradas (veja "Listar localizações") — ex.: o endereço da loja — ou {"latitude": -15.80, "longitude": -48.01, "nome": "...", "endereco": "..."} pra um ponto qualquer. Com "texto" junto, o texto sai logo depois do mapa. Não aceita junto com "arquivo" nem "interna".' },
        ],
        respostas: [
          { status: 200, label: 'enviado', corpo: { success: true, id: '3EB0C767D71E1EFA...' } },
          { status: 400, label: 'faltou o conteúdo', corpo: { success: false, message: 'Informe texto, arquivo ou localizacao.' } },
          { status: 400, label: 'localização não encontrada', corpo: { success: false, message: 'Localização não encontrada. Veja GET /api/agente-externo/v1/localizacoes.' } },
          { status: 403, label: 'fora do escopo', corpo: { success: false, message: 'Conversa fora do escopo deste token.' } },
          { status: 404, label: 'conversa não encontrada', corpo: { success: false, message: 'Conversa não encontrada.' } },
          { status: 409, label: 'linha offline', corpo: { success: false, message: 'A linha de WhatsApp deste funil está offline no momento.' } },
          { status: 502, label: 'falha no envio', corpo: { success: false, message: 'Falha ao enviar a mensagem pelo WhatsApp: ...' } },
        ],
        testavel: false,
      },
      {
        id: 'agex-atendimento',
        titulo: 'Transferir atendimento',
        metodo: 'PUT',
        path: '/api/agente-externo/v1/conversas/:id/atendimento',
        descricao: 'Passa a conversa pra um atendente humano ou devolve ela pro robô. Em "humano", informe "usuario_id" (veja "Listar usuários") pra escolher exatamente quem recebe — sem ele, o sistema sorteia um correspondente ao acaso (nunca devolve pra conta do próprio agente).',
        auth: { header: 'Authorization', formato: 'Bearer <token>', exemplo: null, descricao: 'Token obtido em "Autenticar (obter token)".' },
        parametros: [
          { nome: 'id', local: 'path', tipo: 'string', obrigatorio: true, exemplo: '284841', descricao: 'ID da conversa — use o valor que chega no webhook em conversa.id. NÃO é um número fixo: cada conversa tem o seu, e ele muda se o contato for apagado e recriado.' },
          { nome: 'modo', local: 'body', tipo: 'string', obrigatorio: true, exemplo: 'humano', descricao: '"humano" transfere pra um atendente. "agente" devolve o atendimento pro robô.' },
          { nome: 'usuario_id', local: 'body', tipo: 'string', obrigatorio: false, exemplo: '8', descricao: 'Só vale com modo "humano". ID do usuário (de "Listar usuários") que deve virar o responsável pela conversa. Sem esse campo, o sistema sorteia um correspondente automaticamente.' },
          { nome: 'motivo', local: 'body', tipo: 'string', obrigatorio: false, exemplo: 'Cliente pediu falar com uma pessoa', descricao: 'Aparece no histórico da conversa e na notificação de quem recebeu.' },
        ],
        respostas: [
          { status: 200, label: 'transferido', corpo: { success: true, responsavel: { id: '8', nome: 'Kamila Ribeiro' } } },
          { status: 400, label: 'modo inválido', corpo: { success: false, message: 'Campo "modo" precisa ser "humano" ou "agente".' } },
          { status: 403, label: 'fora do escopo', corpo: { success: false, message: 'Conversa fora do escopo deste token.' } },
          { status: 404, label: 'conversa não encontrada', corpo: { success: false, message: 'Conversa não encontrada.' } },
          { status: 404, label: 'usuário não encontrado', corpo: { success: false, message: 'Usuário "999" não encontrado. Use GET /usuarios para ver os disponíveis.' } },
        ],
        testavel: false,
      },
      {
        id: 'agex-encerrar',
        titulo: 'Encerrar atendimento',
        metodo: 'PUT',
        path: '/api/agente-externo/v1/conversas/:id/encerrar',
        descricao: 'Registra no histórico da conversa que o agente encerrou aquele atendimento. Entra como notificação de sistema na linha do tempo do bate-papo (junto de "moveu o cliente", "transferiu este contato"), e não como nota interna.',
        auth: { header: 'Authorization', formato: 'Bearer <token>', exemplo: null, descricao: 'Token obtido em "Autenticar (obter token)".' },
        parametros: [
          { nome: 'id', local: 'path', tipo: 'string', obrigatorio: true, exemplo: '284841', descricao: 'ID da conversa — use o valor que chega no webhook em conversa.id. NÃO é um número fixo: cada conversa tem o seu, e ele muda se o contato for apagado e recriado.' },
          { nome: 'comentario', local: 'body', tipo: 'string', obrigatorio: false, exemplo: 'Resolvido, cliente confirmou interesse.', descricao: 'Texto opcional. Aparece como "Motivo:" no fim da notificação de encerramento.' },
        ],
        respostas: [
          { status: 200, label: 'encerrado', corpo: { success: true } },
          { status: 403, label: 'fora do escopo', corpo: { success: false, message: 'Conversa fora do escopo deste token.' } },
          { status: 404, label: 'conversa não encontrada', corpo: { success: false, message: 'Conversa não encontrada.' } },
        ],
        testavel: false,
      },
      {
        id: 'agex-etapas',
        titulo: 'Listar etapas do funil',
        metodo: 'GET',
        path: '/api/agente-externo/v1/etapas',
        descricao: 'Lista as etapas do funil deste agente, na ordem do quadro — usa isso pra saber o que dá pra escolher ao tabular.',
        auth: { header: 'Authorization', formato: 'Bearer <token>', exemplo: null, descricao: 'Token obtido em "Autenticar (obter token)".' },
        parametros: [],
        respostas: [
          { status: 200, label: 'sucesso', corpo: [{ id: '3', nome: 'Em negociação', ordem: 2, cor: '#2994ff', tipo: null, exige_motivo: false, motivos: null }, { id: '6', nome: 'Perdido', ordem: 6, cor: '#dc2626', tipo: 'perdido', exige_motivo: true, motivos: [{ id: 'sem_margem', nome: 'Sem Margem' }] }] },
        ],
        testavel: true,
      },
      {
        id: 'agex-motivos-perdido',
        titulo: 'Listar motivos de perda',
        metodo: 'GET',
        path: '/api/agente-externo/v1/motivos-perdido',
        descricao: 'A lista fechada de motivos aceitos ao mover um cliente pra etapa de PERDIDO. É lista fechada de propósito: antes cada atendente tabulava a perda com uma etiqueta livre e o relatório não fechava.',
        auth: { header: 'Authorization', formato: 'Bearer <token>', exemplo: null, descricao: 'Token obtido em "Autenticar (obter token)".' },
        parametros: [],
        respostas: [
          { status: 200, label: 'sucesso', corpo: [{ id: 'sem_resposta', nome: 'Sem resposta' }, { id: 'sem_margem', nome: 'Sem Margem' }, { id: 'indefinido', nome: 'Motivo Indefinido' }] },
        ],
        testavel: true,
      },
      {
        id: 'agex-localizacoes',
        titulo: 'Listar localizações',
        metodo: 'GET',
        path: '/api/agente-externo/v1/localizacoes',
        descricao: 'As localizações cadastradas em Mensagens Prontas (ex.: o endereço da loja). Mande uma pro cliente com {"localizacao": {"id": ...}} em "Enviar mensagem" — use quando ele perguntar onde fica a loja ou quiser vir pessoalmente.',
        auth: { header: 'Authorization', formato: 'Bearer <token>', exemplo: null, descricao: 'Token obtido em "Autenticar (obter token)".' },
        parametros: [],
        respostas: [
          { status: 200, label: 'sucesso', corpo: [{ id: 102, titulo: 'Localização da loja', nome: 'PONTUAL CONSIGNADOS', endereco: 'Ed. Vogue, 5º andar, salas 502 a 507 · SHVP Rua 05, Ch. 116, Lote 1E - Vicente Pires, DF', latitude: -15.808267, longitude: -48.019207 }] },
        ],
        testavel: true,
      },
      {
        id: 'agex-campos',
        titulo: 'Gravar dados no cadastro (CRM)',
        metodo: 'PUT',
        path: '/api/agente-externo/v1/conversas/:id/campos',
        descricao: 'Grava no cadastro do cliente os dados que o agente descobriu na conversa (CPF, nome, órgão...). MESCLA: manda só o que mudou — o que não vier no corpo continua como está. Campo que não existe é recusado com 400, e a resposta traz a lista do que existe.',
        auth: { header: 'Authorization', formato: 'Bearer <token>', exemplo: null, descricao: 'Token obtido em "Autenticar (obter token)".' },
        parametros: [
          { nome: 'id', local: 'path', tipo: 'string', obrigatorio: true, exemplo: '284841', descricao: 'ID da conversa — o mesmo valor que chega no webhook em conversa.id.' },
          { nome: 'campos', local: 'body', tipo: 'object', obrigatorio: true, exemplo: '{"cpf":"06322199129","nome":"PEDRO MARTINS"}', descricao: 'Pares campo/valor. Fixos aceitos: nome, cpf, notas. Os demais precisam estar cadastrados como campo dinâmico no CRM (o nome bate sem diferenciar maiúscula/minúscula). Valor é texto, cortado em 500 caracteres.' },
        ],
        respostas: [
          { status: 200, label: 'gravado', corpo: { success: true, atualizados: ['cpf', 'nome'], mudou: 2 } },
          { status: 200, label: 'nada mudou', corpo: { success: true, atualizados: ['cpf'], mudou: 0 } },
          { status: 400, label: 'campo desconhecido', corpo: { success: false, erro: 'campo_desconhecido', message: 'Campo(s) não cadastrado(s): renda.', campos_disponiveis: ['nome', 'cpf', 'notas', 'Liberado', 'Parcela'] } },
          { status: 400, label: 'corpo vazio', corpo: { success: false, message: 'Nenhum campo para gravar.' } },
          { status: 403, label: 'fora do escopo', corpo: { success: false, message: 'Conversa fora do escopo deste token.' } },
          { status: 404, label: 'conversa não encontrada', corpo: { success: false, message: 'Conversa não encontrada.' } },
        ],
        testavel: false,
      },
      {
        id: 'agex-etapa',
        titulo: 'Tabular (mover etapa)',
        metodo: 'PUT',
        path: '/api/agente-externo/v1/conversas/:id/etapa',
        descricao: 'Move o cartão do cliente dessa conversa pra outra etapa do funil. Mover pra etapa de PERDIDO exige "motivo_perdido" junto — nas demais etapas só o etapa_id basta.',
        auth: { header: 'Authorization', formato: 'Bearer <token>', exemplo: null, descricao: 'Token obtido em "Autenticar (obter token)".' },
        parametros: [
          { nome: 'id', local: 'path', tipo: 'string', obrigatorio: true, exemplo: '284841', descricao: 'ID da conversa — use o valor que chega no webhook em conversa.id. NÃO é um número fixo: cada conversa tem o seu, e ele muda se o contato for apagado e recriado.' },
          { nome: 'etapa_id', local: 'body', tipo: 'string', obrigatorio: true, exemplo: '3', descricao: 'ID de uma etapa deste funil — use "Listar etapas do funil" pra ver as disponíveis.' },
          { nome: 'motivo_perdido', local: 'body', tipo: 'string', obrigatorio: false, exemplo: 'sem_margem', descricao: 'OBRIGATÓRIO só quando a etapa de destino é a de PERDIDO (a que vem com exige_motivo: true em "Listar etapas do funil"). Nas outras etapas o campo é ignorado. Aceita apenas os ids de "Listar motivos de perda" — id fora da lista é recusado, não vira "indefinido".' },
        ],
        respostas: [
          { status: 200, label: 'movido', corpo: { success: true, etapa: { id: '3', nome: 'Em negociação' }, etapa_anterior_id: '2', status_lead: 'aberto', motivo_perdido: null } },
          { status: 200, label: 'perdido c/ motivo', corpo: { success: true, etapa: { id: '6', nome: 'Perdido' }, etapa_anterior_id: '3', status_lead: 'perdido', motivo_perdido: 'sem_margem' } },
          { status: 400, label: 'motivo faltando', corpo: { success: false, erro: 'motivo_perdido_obrigatorio', motivo_obrigatorio: true, message: 'Informe "motivo_perdido" para mover este cliente para PERDIDO.', motivos_disponiveis: [{ id: 'sem_margem', nome: 'Sem Margem' }] } },
          { status: 400, label: 'motivo inválido', corpo: { success: false, erro: 'motivo_perdido_invalido', message: '"sem_interese" não é um motivo de perda válido.', motivos_disponiveis: [{ id: 'sem_interesse', nome: 'Sem Interesse' }] } },
          { status: 400, label: 'etapa inválida', corpo: { success: false, message: 'Etapa não encontrada neste funil. Use GET /etapas para ver as disponíveis.' } },
          { status: 403, label: 'fora do escopo', corpo: { success: false, message: 'Conversa fora do escopo deste token.' } },
          { status: 404, label: 'conversa não encontrada', corpo: { success: false, message: 'Conversa não encontrada.' } },
        ],
        testavel: false,
      },
      {
        id: 'agex-canais',
        titulo: 'Listar canais',
        metodo: 'GET',
        path: '/api/agente-externo/v1/canais',
        descricao: 'Lista os números de WhatsApp (linhas) do funil deste agente.',
        auth: { header: 'Authorization', formato: 'Bearer <token>', exemplo: null, descricao: 'Token obtido em "Autenticar (obter token)".' },
        parametros: [],
        respostas: [
          { status: 200, label: 'sucesso', corpo: [{ canal: 'whatsapp', linhas: [{ id: '7', nome: 'Linha 1', numero: '5511999999999', ativo: true }] }] },
        ],
        testavel: true,
      },
      {
        id: 'agex-usuarios',
        titulo: 'Listar usuários',
        metodo: 'GET',
        path: '/api/agente-externo/v1/usuarios',
        descricao: 'Lista todos os usuários cadastrados no CRM (id e nome de exibição, não o login). Usa isso pra escolher quem recebe a conversa em "Transferir atendimento" (campo "usuario_id"), em vez de deixar no sorteio automático.',
        auth: { header: 'Authorization', formato: 'Bearer <token>', exemplo: null, descricao: 'Token obtido em "Autenticar (obter token)".' },
        parametros: [],
        respostas: [
          { status: 200, label: 'sucesso', corpo: [{ id: '8', nome: 'Kamila Ribeiro' }, { id: '12', nome: 'Agente Parceiro' }] },
        ],
        testavel: true,
      },
      {
        id: 'agex-midia',
        titulo: 'Baixar anexo',
        metodo: 'GET',
        path: '/api/agente-externo/v1/midia/:wppId',
        descricao: 'Baixa o arquivo (foto, documento etc.) de uma mensagem da conversa. A resposta é o próprio arquivo, não um JSON.',
        auth: { header: 'Authorization', formato: 'Bearer <token>', exemplo: null, descricao: 'Token obtido em "Autenticar (obter token)".' },
        parametros: [
          { nome: 'wppId', local: 'path', tipo: 'string', obrigatorio: true, exemplo: '3EB0C767D71E1EFA', descricao: 'ID da mensagem no WhatsApp (campo wpp_id).' },
        ],
        respostas: [
          { status: 200, label: 'arquivo (binário)', corpo: { aviso: 'Corpo da resposta é o arquivo em si, não JSON.' } },
          { status: 403, label: 'fora do escopo', corpo: { success: false, message: 'Anexo fora do escopo deste token.' } },
          { status: 404, label: 'anexo não encontrado', corpo: { success: false, message: 'Anexo não encontrado.' } },
        ],
        testavel: true,
      },
      {
        id: 'agex-del-conversa',
        titulo: 'Apagar conversa',
        metodo: 'DELETE',
        path: '/api/agente-externo/v1/conversas/:id',
        descricao: 'Apaga a conversa por completo. Ação irreversível.',
        auth: { header: 'Authorization', formato: 'Bearer <token>', exemplo: null, descricao: 'Token obtido em "Autenticar (obter token)".' },
        parametros: [{ nome: 'id', local: 'path', tipo: 'string', obrigatorio: true, exemplo: '284841', descricao: 'ID da conversa — use o valor que chega no webhook em conversa.id. NÃO é um número fixo: cada conversa tem o seu, e ele muda se o contato for apagado e recriado.' }],
        respostas: [
          { status: 200, label: 'apagada', corpo: { success: true } },
          { status: 403, label: 'fora do escopo', corpo: { success: false, message: 'Conversa fora do escopo deste token.' } },
          { status: 404, label: 'conversa não encontrada', corpo: { success: false, message: 'Conversa não encontrada.' } },
        ],
        testavel: false,
      },
      {
        id: 'agex-del-contato',
        titulo: 'Apagar contato',
        metodo: 'DELETE',
        path: '/api/agente-externo/v1/contatos/:id',
        descricao: 'Apaga o cliente/contato por completo. Ação irreversível.',
        auth: { header: 'Authorization', formato: 'Bearer <token>', exemplo: null, descricao: 'Token obtido em "Autenticar (obter token)".' },
        parametros: [{ nome: 'id', local: 'path', tipo: 'string', obrigatorio: true, exemplo: '456', descricao: 'ID do contato (contato_id do cliente — não é o número de telefone).' }],
        respostas: [
          { status: 200, label: 'apagado', corpo: { success: true } },
          { status: 403, label: 'fora do escopo', corpo: { success: false, message: 'Contato fora do escopo deste token.' } },
          { status: 404, label: 'contato não encontrado', corpo: { success: false, message: 'Contato não encontrado.' } },
        ],
        testavel: false,
      },
    ],
  },
];

// ============================================================
// ERROS DE TOKEN — comuns a TODAS as rotas do Agente Externo
// ============================================================
// Ficam aqui e são injetados abaixo, em vez de copiados nas 12 rotas: quem
// integra tropeça primeiro no token (o parceiro tomou dezenas de 401 num dia
// só por mandar a chamada sem o header), e essa é justamente a resposta que
// ninguém lembra de documentar rota a rota. Centralizado, uma correção vale
// pra todas — e rota nova já nasce com a tabela de erros completa.
const ERROS_DE_TOKEN = [
  {
    status: 401,
    label: 'sem token',
    corpo: { success: false, message: 'Token de acesso ausente.' },
  },
  {
    status: 401,
    label: 'token expirado',
    corpo: {
      success: false,
      code: 'TOKEN_EXPIRADO',
      message: 'Token expirado. Faça login novamente.',
    },
  },
  {
    status: 403,
    label: 'token inválido',
    corpo: { success: false, message: 'Token inválido.' },
  },
  {
    status: 403,
    label: 'conta não é agente de nenhum funil',
    corpo: {
      success: false,
      message:
        'Este usuário não é o agente externo de nenhum funil ativo. Verifique se o agente está ligado e se o usuário certo está selecionado na configuração do funil.',
    },
  },
];

// Anexa os erros de token em cada rota do Agente Externo, sem duplicar o que a
// rota já declara por conta própria.
for (const categoria of CATEGORIAS) {
  for (const rota of categoria.rotas) {
    if (!String(rota.path || '').startsWith('/api/agente-externo/')) continue;
    const jaTem = new Set(
      (rota.respostas || []).map((r) => `${r.status} ${r.label}`)
    );
    rota.respostas = [
      ...(rota.respostas || []),
      ...ERROS_DE_TOKEN.filter((e) => !jaTem.has(`${e.status} ${e.label}`)),
    ];
  }
}

const LINGUAGENS = [
  { id: 'curl', label: 'cURL', icone: 'bx bx-terminal' },
  { id: 'js', label: 'JavaScript', icone: 'bx bxl-javascript' },
  { id: 'node', label: 'Node.js', icone: 'bx bxl-nodejs' },
  { id: 'python', label: 'Python', icone: 'bx bxl-python' },
  { id: 'php', label: 'PHP', icone: 'bx bxl-php' },
  { id: 'java', label: 'Java', icone: 'bx bxl-java' },
];

// Classe de gramática do Prism (highlight estilo VS Code) por linguagem.
const PRISM_LANG = { curl: 'bash', js: 'javascript', node: 'javascript', python: 'python', php: 'php', java: 'java' };

// Rótulo compacto de onde um campo vive na requisição — usado na etiqueta
// de cada campo, tanto no detalhe quanto nos geradores de código.
const rotuloLocal = { auth: 'header', path: 'URL', body: 'body', query: 'query', header: 'header' };

document.addEventListener('DOMContentLoaded', () => {
  // Esta página não tem o botão de tema da sidebar (não tem sidebar), então
  // a configurarTema() do global.js retorna cedo e nunca aplica o modo
  // escuro salvo. Aplicamos aqui pra continuar consistente com o resto do site.
  if (localStorage.getItem('tema') === 'escuro') {
    document.body.classList.add('modo-escuro');
  }

  const el = {
    nav: document.getElementById('doc-nav-lista'),
    detalhe: document.getElementById('doc-detalhe'),
    cardSnippet: document.getElementById('doc-card-snippet'),
    cardResposta: document.getElementById('doc-card-resposta'),
    busca: document.getElementById('doc-input-busca'),
  };

  let rotaSelecionada = null;
  let linguagemAtual = 'curl';
  let respostaAoVivo = null; // { status, corpo } — some quando troca de rota
  // Títulos das categorias recolhidas na lista da esquerda. Guarda quem
  // está FECHADO (e não quem está aberto) pra categoria nova nascer
  // aberta sem precisar ser cadastrada aqui.
  const gruposFechados = new Set();

  // ===== Helpers de dados =====
  function todasAsRotas() {
    return CATEGORIAS.flatMap((cat) => cat.rotas.map((r) => ({ ...r, categoria: cat.titulo })));
  }

  function classeMetodo(metodo) {
    return `doc-metodo-${(metodo || '').toLowerCase()}`;
  }

  function paramsPorLocal(rota, local) {
    return (rota.parametros || []).filter((p) => p.local === local);
  }

  // Troca ":id"/":wppId" etc no caminho pelos valores passados — os que não
  // vierem preenchidos continuam mostrando o placeholder ":nome" mesmo.
  function preencherPath(caminho, valores) {
    return caminho.replace(/:([a-zA-Z_]+)/g, (m, nome) =>
      valores && valores[nome] !== undefined && valores[nome] !== '' ? valores[nome] : m
    );
  }

  function pathExemplo(rota) {
    const valores = {};
    paramsPorLocal(rota, 'path').forEach((p) => (valores[p.nome] = p.exemplo ?? ''));
    return valores;
  }

  // Sem valoresPath, usa os valores de exemplo cadastrados na rota — é o que
  // os geradores de código e o detalhe usam. O painel de teste ao vivo passa
  // os valores reais digitados pela pessoa.
  function montarUrl(rota, valoresPath) {
    const base = rota.baseUrl || window.location.origin;
    return `${base}${preencherPath(rota.path, valoresPath || pathExemplo(rota))}`;
  }

  // ===== Config dinâmica (token fixo do SSO + URLs base reais) =====
  // Sem isso a tela mostraria placeholder onde dava pra mostrar algo de fato
  // copiável e testável. Vem de uma rota admin — nunca fica hardcoded aqui,
  // já que este arquivo é servido sem exigir login.
  async function carregarConfig() {
    try {
      const res = await fetch('/api/config/documentacao-api');
      const dados = await res.json();
      if (!dados.success) return;

      CATEGORIAS.forEach((cat) => {
        cat.rotas.forEach((r) => {
          // As duas rotas servidor-a-servidor do parceiro compartilham a
          // mesma credencial fixa (SSO_INTEGRACAO_TOKEN) e o mesmo host.
          if (r.id === 'sso-gerar-link' || r.id === 'clientes-listar') {
            if (dados.ssoBaseUrl) r.baseUrl = dados.ssoBaseUrl;
            if (dados.ssoToken && r.auth) r.auth.exemplo = `Bearer ${dados.ssoToken}`;
          }
          if (r.id.startsWith('agex-') && dados.agenteExternoBaseUrl) {
            r.baseUrl = dados.agenteExternoBaseUrl;
          }
        });
      });
    } catch (err) {
      console.error('[Documentação API] Não consegui carregar a configuração dinâmica:', err.message);
    }
  }

  // ===== Token do Agente Externo, capturado ao testar "Gerar token" =====
  // Vale só nesta aba (sessionStorage — fecha a aba, esquece; F5 mantém).
  // Uma vez gerado, substitui o "Bearer <token>" genérico em TODAS as rotas
  // do Agente Externo automaticamente — ver authExemploEfetivo() — sem
  // precisar colar o mesmo token rota por rota.
  const CHAVE_TOKEN_SESSAO = 'doc-api-token-agex';
  function tokenAtivo() {
    try {
      return sessionStorage.getItem(CHAVE_TOKEN_SESSAO) || null;
    } catch {
      return null;
    }
  }
  function definirTokenAtivo(token) {
    try {
      if (token) sessionStorage.setItem(CHAVE_TOKEN_SESSAO, token);
      else sessionStorage.removeItem(CHAVE_TOKEN_SESSAO);
    } catch {
      // sessionStorage indisponível (aba anônima, iframe restrito...) — o
      // token só dura enquanto os campos na tela não forem limpos.
    }
  }
  // O valor de auth a usar como exemplo/preenchimento: token gerado (rotas
  // "agex-*") tem prioridade sobre o exemplo fixo cadastrado na rota (usado
  // por integrações com token fixo, como o SSO — nunca pisa nele).
  function authExemploEfetivo(rota) {
    if (!rota.auth) return null;
    if (rota.id.startsWith('agex-')) {
      const token = tokenAtivo();
      if (token) return `Bearer ${token}`;
    }
    return rota.auth.exemplo;
  }

  // ===== Geradores de exemplo de código =====
  // Exemplo de "objeto"/"boolean" no corpo vem como string no cadastro da
  // rota (fica mais simples de escrever) — aqui vira o tipo de verdade, pra
  // o JSON gerado no exemplo de código ficar real (objeto aninhado, boolean
  // sem aspas), não uma string crua.
  function interpretarExemplo(p) {
    if (p.exemplo === undefined) return '';
    if (p.tipo === 'boolean') return p.exemplo === true || p.exemplo === 'true';
    if (p.tipo === 'objeto' && typeof p.exemplo === 'string') {
      try {
        return JSON.parse(p.exemplo);
      } catch {
        return p.exemplo;
      }
    }
    return p.exemplo;
  }

  // Valores digitados no painel de Testar têm prioridade; campo intocado
  // cai no exemplo cadastrado. Só serve pra PREVIEW (snippet de código) —
  // a execução real (executarTeste) nunca completa com exemplo, manda só o
  // que a pessoa de fato digitou.
  function comFallbackExemplo(campos, valoresAoVivo) {
    const valores = {};
    campos.forEach((p) => {
      const live = valoresAoVivo && valoresAoVivo[p.nome];
      valores[p.nome] = live && live.valor !== '' ? live.valor : interpretarExemplo(p);
    });
    return valores;
  }

  function corpoExemplo(rota, valoresAoVivo) {
    const campos = paramsPorLocal(rota, 'body');
    if (!campos.length) return null;
    return comFallbackExemplo(campos, valoresAoVivo);
  }

  function urlComQuery(rota, valoresAoVivo) {
    const valoresPath = comFallbackExemplo(paramsPorLocal(rota, 'path'), valoresAoVivo);
    const url = montarUrl(rota, valoresPath);
    const campos = paramsPorLocal(rota, 'query');
    if (!campos.length) return url;

    const usp = new URLSearchParams();
    const valores = comFallbackExemplo(campos, valoresAoVivo);
    campos.forEach((p) => {
      // Parâmetro de busca OPCIONAL só entra na URL se a pessoa digitar —
      // o valor de exemplo não basta. Ao contrário do corpo (onde listar
      // o campo opcional documenta o formato), um "?limite=5000" grudado
      // no exemplo ensina como padrão justo o que é a exceção. Campo
      // obrigatório continua sempre presente, com exemplo mesmo.
      const live = valoresAoVivo && valoresAoVivo[p.nome];
      const digitado = live && live.valor !== '';
      if (!p.obrigatorio && !digitado) return;
      usp.set(p.nome, valores[p.nome] ?? '');
    });

    const qs = usp.toString();
    return qs ? `${url}?${qs}` : url;
  }

  function headersExemplo(rota, valoresAoVivo) {
    const headers = [];
    if (rota.auth) {
      const live = valoresAoVivo && valoresAoVivo[rota.auth.header];
      const padrao = authExemploEfetivo(rota) || rota.auth.formato;
      headers.push([rota.auth.header, live && live.valor !== '' ? live.valor : padrao]);
    }
    const camposHeader = paramsPorLocal(rota, 'header');
    const valoresHeader = comFallbackExemplo(camposHeader, valoresAoVivo);
    camposHeader.forEach((p) => headers.push([p.nome, valoresHeader[p.nome] ?? '']));
    return headers;
  }

  function gerarCurl(rota, valoresAoVivo) {
    const url = urlComQuery(rota, valoresAoVivo);
    const linhas = [`curl -X ${rota.metodo} "${url}" \\`];
    headersExemplo(rota, valoresAoVivo).forEach(([nome, valor]) => linhas.push(`  -H "${nome}: ${valor}" \\`));
    const corpo = corpoExemplo(rota, valoresAoVivo);
    if (corpo) {
      linhas.push(`  -H "Content-Type: application/json" \\`);
      linhas.push(`  -d '${JSON.stringify(corpo, null, 2)}'`);
    } else {
      linhas[linhas.length - 1] = linhas[linhas.length - 1].replace(/\s\\$/, '');
    }
    return linhas.join('\n');
  }

  function gerarJs(rota, valoresAoVivo) {
    const url = urlComQuery(rota, valoresAoVivo);
    const corpo = corpoExemplo(rota, valoresAoVivo);
    const headers = headersExemplo(rota, valoresAoVivo).map(([n, v]) => `    "${n}": "${v}",`);
    if (corpo) headers.push(`    "Content-Type": "application/json",`);
    return [
      `fetch("${url}", {`,
      `  method: "${rota.metodo}",`,
      headers.length ? `  headers: {\n${headers.join('\n')}\n  },` : null,
      corpo ? `  body: JSON.stringify(${JSON.stringify(corpo, null, 2).replace(/\n/g, '\n  ')}),` : null,
      `})`,
      `  .then((res) => res.json())`,
      `  .then((dados) => console.log(dados));`,
    ]
      .filter(Boolean)
      .join('\n');
  }

  function gerarNode(rota, valoresAoVivo) {
    const url = urlComQuery(rota, valoresAoVivo);
    const corpo = corpoExemplo(rota, valoresAoVivo);
    const headers = {};
    headersExemplo(rota, valoresAoVivo).forEach(([n, v]) => (headers[n] = v));
    const opcoes = { method: rota.metodo, headers };
    if (corpo) opcoes.data = corpo;
    return [
      `const axios = require("axios");`,
      ``,
      `axios({`,
      `  url: "${url}",`,
      `  method: "${rota.metodo}",`,
      Object.keys(headers).length ? `  headers: ${JSON.stringify(headers, null, 2).replace(/\n/g, '\n  ')},` : null,
      corpo ? `  data: ${JSON.stringify(corpo, null, 2).replace(/\n/g, '\n  ')},` : null,
      `})`,
      `  .then((res) => console.log(res.data))`,
      `  .catch((err) => console.error(err.response?.data || err.message));`,
    ]
      .filter(Boolean)
      .join('\n');
  }

  function gerarPython(rota, valoresAoVivo) {
    const url = urlComQuery(rota, valoresAoVivo);
    const corpo = corpoExemplo(rota, valoresAoVivo);
    const headers = {};
    headersExemplo(rota, valoresAoVivo).forEach(([n, v]) => (headers[n] = v));
    const linhas = [`import requests`, ``, `resposta = requests.request(`, `    "${rota.metodo}",`, `    "${url}",`];
    if (Object.keys(headers).length) linhas.push(`    headers=${pyDict(headers)},`);
    if (corpo) linhas.push(`    json=${pyDict(corpo)},`);
    linhas.push(`)`, ``, `print(resposta.status_code, resposta.json())`);
    return linhas.join('\n');
  }

  function pyDict(obj) {
    return JSON.stringify(obj, null, 4).replace(/\n/g, '\n    ');
  }

  function gerarPhp(rota, valoresAoVivo) {
    const url = urlComQuery(rota, valoresAoVivo);
    const corpo = corpoExemplo(rota, valoresAoVivo);
    const headers = headersExemplo(rota, valoresAoVivo).map(([n, v]) => `    "${n}: ${v}",`);
    if (corpo) headers.push(`    "Content-Type: application/json",`);
    const linhas = [
      `$curl = curl_init();`,
      `curl_setopt_array($curl, [`,
      `  CURLOPT_URL => "${url}",`,
      `  CURLOPT_RETURNTRANSFER => true,`,
      `  CURLOPT_CUSTOMREQUEST => "${rota.metodo}",`,
    ];
    if (corpo) linhas.push(`  CURLOPT_POSTFIELDS => json_encode(${phpArray(corpo)}),`);
    if (headers.length) linhas.push(`  CURLOPT_HTTPHEADER => [\n${headers.join('\n')}\n  ],`);
    linhas.push(`]);`, ``, `$resposta = curl_exec($curl);`, `curl_close($curl);`, `echo $resposta;`);
    return linhas.join('\n');
  }

  function phpArray(obj) {
    const linhas = Object.entries(obj).map(([k, v]) => `    "${k}" => "${v}",`);
    return `[\n${linhas.join('\n')}\n  ]`;
  }

  function gerarJava(rota, valoresAoVivo) {
    const url = urlComQuery(rota, valoresAoVivo);
    const corpo = corpoExemplo(rota, valoresAoVivo);
    const headers = headersExemplo(rota, valoresAoVivo);
    const linhas = [
      `HttpClient client = HttpClient.newHttpClient();`,
      ``,
      `HttpRequest.Builder builder = HttpRequest.newBuilder()`,
      `    .uri(URI.create("${url}"))`,
    ];
    headers.forEach(([n, v]) => linhas.push(`    .header("${n}", "${v}")`));
    if (corpo) {
      linhas.push(`    .header("Content-Type", "application/json")`);
      linhas.push(`    .method("${rota.metodo}", HttpRequest.BodyPublishers.ofString(`);
      linhas.push(`        "${JSON.stringify(corpo).replace(/"/g, '\\"')}"));`);
    } else {
      linhas.push(`    .method("${rota.metodo}", HttpRequest.BodyPublishers.noBody());`);
    }
    linhas.push(
      ``,
      `HttpResponse<String> resposta = client.send(builder.build(), HttpResponse.BodyHandlers.ofString());`,
      `System.out.println(resposta.statusCode() + " " + resposta.body());`
    );
    return linhas.join('\n');
  }

  const GERADORES = {
    curl: gerarCurl,
    js: gerarJs,
    node: gerarNode,
    python: gerarPython,
    php: gerarPhp,
    java: gerarJava,
  };

  // ===== Render: coluna 1 — nav de rotas =====
  function renderNav(filtro = '') {
    const termo = filtro.trim().toLowerCase();
    const categoriasFiltradas = CATEGORIAS.map((cat) => ({
      ...cat,
      rotas: cat.rotas.filter(
        (r) =>
          !termo ||
          r.titulo.toLowerCase().includes(termo) ||
          r.path.toLowerCase().includes(termo) ||
          r.metodo.toLowerCase().includes(termo)
      ),
    })).filter((cat) => cat.rotas.length);

    if (!CATEGORIAS.length) {
      el.nav.innerHTML = `
        <div class="doc-vazio">
          <i class="ph-bold ph-tray"></i>
          <h2>Nenhuma rota publicada</h2>
          <p>Quando as primeiras rotas forem adicionadas aqui, elas aparecem organizadas por categoria.</p>
        </div>`;
      return;
    }

    if (!categoriasFiltradas.length) {
      el.nav.innerHTML = `
        <div class="doc-vazio">
          <i class="ph-bold ph-magnifying-glass"></i>
          <h2>Nada encontrado</h2>
          <p>Nenhuma rota bate com "${escapeHtml(filtro)}".</p>
        </div>`;
      return;
    }

    // Buscando, nenhum grupo fica recolhido: esconder um resultado que
    // bate com o termo faria a busca parecer quebrada. O que a pessoa
    // fechou continua guardado e volta quando ela limpa a busca.
    const buscando = termo.length > 0;

    el.nav.innerHTML = categoriasFiltradas
      .map((cat) => {
        const fechado = !buscando && gruposFechados.has(cat.titulo);
        return `
        <div class="doc-nav-grupo${fechado ? ' doc-fechado' : ''}" data-grupo="${escapeHtml(cat.titulo)}">
          <button class="doc-nav-grupo-titulo" type="button" aria-expanded="${!fechado}">
            <i class="ph-bold ph-caret-down doc-nav-caret"></i>
            <span>${escapeHtml(cat.titulo)}</span>
            <span class="doc-nav-grupo-qtd">${cat.rotas.length}</span>
          </button>
          <div class="doc-nav-grupo-itens">
            <div>
              ${cat.rotas
                .map(
                  (r) => `
                  <button
                    class="doc-nav-item ${classeMetodo(r.metodo)}${rotaSelecionada?.id === r.id ? ' doc-ativo' : ''}"
                    data-id="${r.id}"
                    type="button"
                  >
                    <span class="doc-pill-metodo ${classeMetodo(r.metodo)}">${r.metodo}</span>
                    <span>${escapeHtml(r.titulo)}</span>
                  </button>`
                )
                .join('')}
            </div>
          </div>
        </div>`;
      })
      .join('');

    el.nav.querySelectorAll('.doc-nav-grupo-titulo').forEach((btn) => {
      btn.addEventListener('click', () => {
        const grupo = btn.closest('.doc-nav-grupo');
        const fechando = !grupo.classList.contains('doc-fechado');
        grupo.classList.toggle('doc-fechado', fechando);
        btn.setAttribute('aria-expanded', String(!fechando));
        // Só a classe muda — a animação é do CSS. Re-renderizar a lista
        // aqui trocaria o elemento no meio da transição e ela não rodaria.
        if (fechando) gruposFechados.add(grupo.dataset.grupo);
        else gruposFechados.delete(grupo.dataset.grupo);
      });
    });

    el.nav.querySelectorAll('.doc-nav-item').forEach((btn) => {
      btn.addEventListener('click', () => {
        const rota = todasAsRotas().find((r) => r.id === btn.dataset.id);
        if (rota) selecionarRota(rota);
      });
    });
  }

  // ===== Render: coluna 2 — detalhe da rota =====
  // Um campo (auth + cada parâmetro) vira UM bloco só: nome, onde ele vive
  // na requisição, se é obrigatório, a explicação — e o próprio input pra
  // testar, tudo junto. Antes eram duas seções (uma só de leitura, outra só
  // de input) repetindo os mesmos campos duas vezes.
  //
  // `editavel` false (rota com efeito real) mantém TODA a documentação do
  // campo e troca só o input por uma amostra do valor esperado, sem caixa
  // de escrever — o parâmetro continua documentado, o que some é a chance
  // de disparar a ação sem querer. Mesmo desenho da doc da Evolution API.
  function renderCampo(p, rota, editavel = true) {
    const ehAuth = p.local === 'auth';
    const tokenGeradoAtivo = editavel && ehAuth && rota.id.startsWith('agex-') && !!tokenAtivo();
    const valorPreenchido = ehAuth ? authExemploEfetivo(rota) || '' : '';
    const exemplo = ehAuth ? rota.auth.formato || '' : String(p.exemplo ?? '');

    const controle = editavel
      ? `<input
          type="${p.tipo === 'password' ? 'password' : 'text'}"
          id="doc-campo-${escapeHtml(p.nome)}"
          class="doc-input"
          autocomplete="${p.tipo === 'password' ? 'new-password' : 'off'}"
          autocorrect="off"
          autocapitalize="off"
          spellcheck="false"
          data-lpignore="true"
          data-1p-ignore="true"
          ${valorPreenchido ? `value="${escapeHtml(valorPreenchido)}"` : `placeholder="${escapeHtml(exemplo)}"`}
          data-local="${p.local}"
          data-nome="${escapeHtml(p.nome)}"
        />`
      : exemplo
        ? `<div class="doc-valor-exemplo"><span class="doc-valor-exemplo-rotulo">exemplo</span><code>${escapeHtml(exemplo)}</code></div>`
        : '';

    return `
      <div class="doc-parametro">
        <div class="doc-parametro-linha">
          <label class="doc-parametro-nome" for="doc-campo-${escapeHtml(p.nome)}">${escapeHtml(p.nome)}</label>
          <span class="doc-tag-cinza">${escapeHtml(rotuloLocal[p.local] || p.local)} · ${escapeHtml(p.tipo || 'string')}</span>
          ${p.obrigatorio ? '<span class="doc-tag-obrigatorio">obrigatório</span>' : ''}
          ${tokenGeradoAtivo ? '<button type="button" class="doc-token-trocar" id="doc-token-trocar" title="Esquecer o token gerado e voltar a preencher manualmente">trocar token</button>' : ''}
        </div>
        ${p.descricao ? `<p class="doc-parametro-desc">${escapeHtml(p.descricao)}</p>` : ''}
        ${controle}
      </div>`;
  }

  function renderDetalhe() {
    if (!rotaSelecionada) {
      el.detalhe.innerHTML = `
        <div class="doc-vazio">
          <i class="ph-bold ph-selection-plus"></i>
          <h2>${CATEGORIAS.length ? 'Nenhuma rota selecionada' : 'Nenhuma rota documentada ainda'}</h2>
          <p>${
            CATEGORIAS.length
              ? 'Escolha uma rota na lista ao lado pra ver os detalhes, os parâmetros e testar ao vivo.'
              : 'Quando adicionarmos as primeiras rotas aqui, escolha uma na lista ao lado pra ver os detalhes, os parâmetros e testar ao vivo.'
          }</p>
        </div>`;
      return;
    }

    const r = rotaSelecionada;
    const rotaTestavel = r.testavel !== false;

    // O teste ao vivo mora aqui (campos + botão), logo depois do endereço —
    // o resultado aparece no card da direita, que também já lista as
    // respostas possíveis (não precisa repetir a lista aqui).
    //
    // Os campos aparecem SEMPRE, testável ou não: é a referência de quem
    // vai escrever o código do outro lado. Numa rota com efeito real eles
    // vêm só de leitura (ver renderCampo) e sem o botão de disparar.
    const campos = [
      ...(r.auth ? [{ nome: r.auth.header, local: 'auth', tipo: 'string', obrigatorio: true, exemplo: r.auth.exemplo || r.auth.formato || '', descricao: r.auth.descricao }] : []),
      ...(r.parametros || []),
    ];
    const camposHtml = campos.map((p) => renderCampo(p, r, rotaTestavel)).join('');
    const rodape = rotaTestavel
      ? `<button class="doc-btn-experimentar" id="doc-btn-experimentar" type="button">Experimentar <i class="ph-bold ph-play"></i></button>
         ${
           (r.testeExigeUmDe || []).length
             ? `<p class="doc-testar-nota" id="doc-testar-nota"></p>`
             : ''
         }`
      : `<p class="doc-testar-aviso"><i class="ph-bold ph-shield-warning"></i> ${escapeHtml(
          r.avisoTeste
            || 'Rota de efeito real (envia mensagem, apaga dado, move ou transfere o cliente de verdade): sem teste ao vivo, de propósito. Copie o exemplo de código ao lado e rode do seu sistema.'
        )}</p>`;

    // Os parâmetros moram num painel com corpo próprio, e não soltos no
    // fundo da página: é a mesma linguagem dos cards da direita, e sem
    // isso a coluna do meio vira texto flutuando no bege — some a noção de
    // onde um campo termina e o outro começa.
    const painelCampos = camposHtml
      ? `<section class="doc-bloco">
          <div class="doc-bloco-topo">
            <h2 class="doc-bloco-titulo">${rotaTestavel ? 'Parâmetros e teste' : 'Parâmetros'}</h2>
            <span class="doc-bloco-selo">${campos.length} ${campos.length === 1 ? 'campo' : 'campos'}</span>
          </div>
          <div class="doc-testar-campos" id="doc-testar-campos">${camposHtml}</div>
          <div class="doc-bloco-rodape">${rodape}</div>
        </section>`
      : `<section class="doc-bloco"><div class="doc-bloco-rodape doc-bloco-rodape-solo">${rodape}</div></section>`;

    el.detalhe.innerHTML = `
      <section class="doc-endpoint">
        <header class="doc-endpoint-cabecalho">
          <span class="doc-eyebrow">${escapeHtml(r.categoria || '')}</span>
          <h1 class="doc-titulo-endpoint">${escapeHtml(r.titulo)}</h1>
          <p class="doc-descricao-endpoint">${escapeHtml(r.descricao || '')}</p>

          <div class="doc-url-bar">
            <span class="doc-pill-metodo ${classeMetodo(r.metodo)}">${r.metodo}</span>
            <code id="doc-url-completa">${escapeHtml(montarUrl(r))}</code>
            <button class="doc-btn-copiar" id="doc-btn-copiar-url" type="button" title="Copiar endereço completo">
              <i class="ph-bold ph-copy"></i>
            </button>
          </div>
        </header>

        ${painelCampos}
      </section>`;

    document.getElementById('doc-btn-copiar-url')?.addEventListener('click', () => {
      const texto = document.getElementById('doc-url-completa').textContent;
      navigator.clipboard.writeText(texto).then(() => window.mostrarAlerta?.('Endereço copiado!', 'success'));
    });

    document.getElementById('doc-btn-experimentar')?.addEventListener('click', executarTeste);
    document.getElementById('doc-token-trocar')?.addEventListener('click', () => {
      definirTokenAtivo(null);
      renderDetalhe();
      renderCardSnippet();
    });

    atualizarTravaDoTeste();
  }

  // Regra 2 da tela de teste (ver cabeçalho do arquivo): rota que sem
  // filtro devolveria a base inteira só libera o botão depois que a pessoa
  // restringir a busca. Roda a cada tecla, então precisa ser barata.
  function atualizarTravaDoTeste() {
    const exigidos = rotaSelecionada?.testeExigeUmDe || [];
    const btn = document.getElementById('doc-btn-experimentar');
    const nota = document.getElementById('doc-testar-nota');
    if (!exigidos.length || !btn) return;

    const valores = coletarCampos();
    const preenchido = exigidos.some((nome) => (valores[nome]?.valor || '').trim() !== '');

    btn.disabled = !preenchido;
    if (nota) {
      nota.textContent = preenchido
        ? ''
        : `Preencha ${exigidos.join(' ou ')} pra testar aqui — sem filtro a resposta traria a base inteira. A rota em si aceita a chamada sem filtro normalmente.`;
    }
  }

  // ===== Render: card 1 — exemplo de código =====
  function renderCardSnippet() {
    if (!rotaSelecionada) {
      el.cardSnippet.innerHTML = `
        <div class="doc-card-vazio">
          <i class="ph-bold ph-brackets-curly"></i>
          <p>Selecione uma rota pra ver o exemplo de código em várias linguagens.</p>
        </div>`;
      return;
    }

    const r = rotaSelecionada;
    const langAtual = LINGUAGENS.find((l) => l.id === linguagemAtual);

    el.cardSnippet.innerHTML = `
      <div class="doc-card-cabecalho">
        <span class="doc-testar-titulo">${escapeHtml(r.titulo)}</span>
        <div class="doc-card-acoes">
          <button class="doc-btn-ghost" id="doc-btn-copiar-snippet" type="button" title="Copiar código">
            <i class="ph-bold ph-copy"></i>
          </button>
          <div class="doc-lang-seletor" id="doc-lang-seletor">
            <button class="doc-btn-ghost" id="doc-lang-botao" type="button" aria-haspopup="listbox" aria-expanded="false">
              <i class="${langAtual.icone}"></i>
              <span id="doc-lang-atual">${langAtual.label}</span>
              <i class="ph-bold ph-caret-down doc-lang-caret"></i>
            </button>
            <ul class="doc-lang-lista" id="doc-lang-lista" role="listbox" hidden>
              ${LINGUAGENS.map(
                (l) => `<li role="option" data-lang="${l.id}" class="${l.id === linguagemAtual ? 'doc-ativo' : ''}"><i class="${l.icone}"></i>${l.label}</li>`
              ).join('')}
            </ul>
          </div>
        </div>
      </div>
      <div class="doc-card-corpo">
        <div class="doc-code-wrap"><pre><code id="doc-snippet-corpo" class="language-${PRISM_LANG[linguagemAtual]}"></code></pre></div>
      </div>
    `;

    const codeEl = document.getElementById('doc-snippet-corpo');
    codeEl.textContent = GERADORES[linguagemAtual](r, coletarCampos());
    if (window.Prism) Prism.highlightElement(codeEl);

    const seletor = document.getElementById('doc-lang-seletor');
    const botao = document.getElementById('doc-lang-botao');
    const lista = document.getElementById('doc-lang-lista');

    botao.addEventListener('click', (e) => {
      e.stopPropagation();
      const abrir = lista.hasAttribute('hidden');
      lista.toggleAttribute('hidden', !abrir);
      seletor.classList.toggle('doc-ativo', abrir);
      botao.setAttribute('aria-expanded', String(abrir));
    });

    lista.querySelectorAll('li').forEach((item) => {
      item.addEventListener('click', () => {
        linguagemAtual = item.dataset.lang;
        renderCardSnippet();
      });
    });

    document.getElementById('doc-btn-copiar-snippet').addEventListener('click', () => {
      const texto = document.getElementById('doc-snippet-corpo').textContent;
      navigator.clipboard.writeText(texto).then(() => window.mostrarAlerta?.('Código copiado!', 'success'));
    });
  }

  document.addEventListener('click', () => {
    const lista = document.getElementById('doc-lang-lista');
    const seletor = document.getElementById('doc-lang-seletor');
    if (lista && !lista.hasAttribute('hidden')) {
      lista.setAttribute('hidden', '');
      seletor?.classList.remove('doc-ativo');
    }
  });
  document.addEventListener('keydown', (e) => {
    if (e.key !== 'Escape') return;
    const lista = document.getElementById('doc-lang-lista');
    const seletor = document.getElementById('doc-lang-seletor');
    if (lista) {
      lista.setAttribute('hidden', '');
      seletor?.classList.remove('doc-ativo');
    }
  });

  // ===== Render: card 2 — só as respostas (o teste em si mora no detalhe) =====
  function renderCardResposta() {
    if (!rotaSelecionada) {
      el.cardResposta.innerHTML = `
        <div class="doc-card-vazio">
          <i class="ph-bold ph-flask"></i>
          <p>Selecione uma rota pra ver as respostas possíveis.</p>
        </div>`;
      return;
    }

    const r = rotaSelecionada;

    // A resposta do teste OCUPA a aba do status que ela devolveu, em vez
    // de virar uma aba à parte: testar e receber 401 numa rota que já
    // documenta 401 criava duas abas "401" lado a lado, e a cada teste
    // nascia mais uma. Aqui o exemplo cadastrado é substituído pelo que o
    // servidor respondeu de verdade, a aba ganha o pontinho de "ao vivo"
    // e já vem selecionada.
    const abas = (r.respostas || []).map((res) => ({ ...res, aoVivo: false }));
    let abaInicial = 0;

    if (respostaAoVivo) {
      const i = abas.findIndex(
        (a) => Number(a.status) === Number(respostaAoVivo.status)
      );
      if (i >= 0) {
        abas[i] = { ...abas[i], corpo: respostaAoVivo.corpo, aoVivo: true };
        abaInicial = i;
      } else {
        // Status que a rota não previa (503, ou o 0 de falha de rede):
        // aí sim é aba nova, senão a resposta não teria onde aparecer.
        abas.unshift({ ...respostaAoVivo, label: 'ao vivo', aoVivo: true });
        abaInicial = 0;
      }
    }

    if (!abas.length) {
      el.cardResposta.innerHTML = `
        <div class="doc-card-vazio">
          <i class="ph-bold ph-flask"></i>
          <p>Sem exemplos de resposta cadastrados pra essa rota.</p>
        </div>`;
      return;
    }

    el.cardResposta.innerHTML = `
      <div class="doc-card-corpo">
        <div class="doc-resposta-topo">
          <div class="doc-resposta-abas" id="doc-resposta-abas"></div>
          <button class="doc-btn-ghost" id="doc-btn-copiar-resposta" type="button" title="Copiar resposta">
            <i class="ph-bold ph-copy"></i>
          </button>
        </div>
        <div class="doc-code-wrap"><pre><code id="doc-resposta-corpo" class="language-json"></code></pre></div>
      </div>
    `;

    document.getElementById('doc-btn-copiar-resposta').addEventListener('click', () => {
      const texto = document.getElementById('doc-resposta-corpo').textContent;
      navigator.clipboard.writeText(texto).then(() => window.mostrarAlerta?.('Resposta copiada!', 'success'));
    });

    renderAbasResposta(abas, abaInicial);
  }

  function renderAbasResposta(abas, indexAtiva) {
    const elAbas = document.getElementById('doc-resposta-abas');
    const elCorpo = document.getElementById('doc-resposta-corpo');
    if (!elAbas || !elCorpo) return;

    // A "numeração" é o próprio código de status (200, 400, 404...) — igual
    // uma barra de abas comum, sem caixinha em volta, só sublinhado no ativo.
    elAbas.innerHTML = abas
      .map(
        (a, i) => `
        <button
          class="doc-resposta-aba${i === indexAtiva ? ' doc-ativo' : ''}"
          data-index="${i}"
          type="button"
          title="${a.label ? escapeHtml(a.label) : ''}"
        >
          ${a.aoVivo ? '<span class="doc-tag-ao-vivo"></span>' : ''}${a.status}
        </button>`
      )
      .join('');

    elCorpo.textContent = JSON.stringify(abas[indexAtiva].corpo, null, 2);
    if (window.Prism) Prism.highlightElement(elCorpo);

    elAbas.querySelectorAll('.doc-resposta-aba').forEach((btn) => {
      btn.addEventListener('click', () => renderAbasResposta(abas, Number(btn.dataset.index)));
    });
  }

  // ===== Teste ao vivo =====
  // Importante: o interceptor global de fetch (global.js) injeta o token de
  // sessão do admin em toda chamada relativa e força logout em qualquer
  // resposta 401/403 — péssimo pra um painel onde testar um 401 de propósito
  // é justamente o objetivo. Por isso o teste aqui usa XMLHttpRequest puro,
  // que o interceptor não enxerga.
  function coletarCampos() {
    const valores = {};
    document.querySelectorAll('#doc-testar-campos [data-nome]').forEach((input) => {
      valores[input.dataset.nome] = { local: input.dataset.local, valor: input.value };
    });
    return valores;
  }

  // Reflete o que a pessoa está digitando no painel de Testar direto no
  // código de exemplo, ao vivo — só troca o texto do <code>, sem
  // reconstruir o card inteiro (senão o dropdown de linguagem piscaria a
  // cada tecla).
  function atualizarSnippetAoVivo() {
    if (!rotaSelecionada) return;
    const codeEl = document.getElementById('doc-snippet-corpo');
    if (!codeEl) return;
    codeEl.textContent = GERADORES[linguagemAtual](rotaSelecionada, coletarCampos());
    if (window.Prism) Prism.highlightElement(codeEl);
  }

  function executarRequisicao({ url, metodo, headers, corpo }) {
    return new Promise((resolve) => {
      const xhr = new XMLHttpRequest();
      xhr.open(metodo, url, true);
      Object.entries(headers).forEach(([chave, valor]) => {
        if (valor) {
          try {
            xhr.setRequestHeader(chave, valor);
          } catch (err) {
            console.warn('[Documentação API] Cabeçalho ignorado:', chave, err.message);
          }
        }
      });
      xhr.onload = () => {
        let corpoResposta;
        try {
          corpoResposta = JSON.parse(xhr.responseText);
        } catch {
          corpoResposta = xhr.responseText || '(resposta vazia)';
        }
        resolve({ status: xhr.status, corpo: corpoResposta });
      };
      xhr.onerror = () => resolve({ status: 0, corpo: { erro: 'Falha de rede — confira o endereço e sua conexão.' } });
      xhr.send(corpo ? JSON.stringify(corpo) : null);
    });
  }

  async function executarTeste() {
    const r = rotaSelecionada;
    const btn = document.getElementById('doc-btn-experimentar');
    const valores = coletarCampos();

    const headers = {};
    let corpo = null;
    const query = new URLSearchParams();
    const valoresPath = {};

    Object.entries(valores).forEach(([nome, { local, valor }]) => {
      if (local === 'auth') headers[r.auth.header] = valor;
      else if (local === 'header') headers[nome] = valor;
      else if (local === 'path') valoresPath[nome] = valor;
      else if (local === 'query') {
        if (valor) query.set(nome, valor);
      } else if (local === 'body') {
        corpo = corpo || {};
        corpo[nome] = valor;
      }
    });
    if (corpo) headers['Content-Type'] = 'application/json';

    // Testar ao vivo roda dentro do NAVEGADOR, então precisa da mesma
    // origem que serviu esta página — nunca de "rota.baseUrl" (o endereço
    // público de exemplo, pro parceiro externo copiar). É o mesmo backend
    // dos dois jeitos, mas pedir pra um domínio diferente por XHR vira
    // requisição cross-origin, e o servidor não libera CORS pra chamada de
    // página (só espera chamada servidor-a-servidor) — o navegador bloqueia
    // e some com um "Falha de rede" enganoso, que parece bug e não é.
    const qs = query.toString();
    const url = `${window.location.origin}${preencherPath(r.path, valoresPath)}` + (qs ? `?${qs}` : '');

    btn.disabled = true;
    const textoOriginal = btn.innerHTML;
    btn.innerHTML = 'Testando... <i class="ph-bold ph-circle-notch doc-spin"></i>';

    const resposta = await executarRequisicao({ url, metodo: r.metodo, headers, corpo });

    // "Gerar token" deu certo: guarda o token pra essa aba — as outras
    // rotas do Agente Externo passam a usá-lo sozinhas (ver
    // authExemploEfetivo), sem precisar colar em cada uma. Lê da resposta
    // CRUA, antes do corte de exibição.
    if (r.id === 'agex-login' && resposta.status === 200 && resposta.corpo?.token) {
      definirTokenAtivo(resposta.corpo.token);
    }

    respostaAoVivo = { ...resposta, corpo: limitarParaExibicao(resposta.corpo) };

    btn.disabled = false;
    btn.innerHTML = textoOriginal;
    renderCardResposta();
    atualizarTravaDoTeste();
  }

  // Regra 1 da tela de teste (ver cabeçalho do arquivo): corta toda lista
  // longa da resposta antes de ela virar JSON na tela. Vale pra qualquer
  // rota, incluindo as que vierem depois — nenhuma precisa se cadastrar
  // pra isso valer.
  //
  // O corte é só de EXIBIÇÃO: a resposta crua já chegou inteira, e quem
  // consome a rota de outro sistema recebe tudo. A linha final avisa
  // quantos ficaram de fora, no lugar exato onde a lista foi cortada, pra
  // ninguém achar que a rota devolveu só isso.
  // Desce pela resposta inteira, e não só no primeiro nível: uma lista
  // longa aninhada (o "linhas" dentro de cada canal, por exemplo) também
  // afogaria a tela. Corta ANTES de descer, pra não processar 12 mil itens
  // e jogar fora 12 mil menos dez. O teto de profundidade evita passeio
  // longo numa resposta inesperadamente aninhada.
  function limitarParaExibicao(corpo, profundidade = 0) {
    if (profundidade > 4) return corpo;

    if (Array.isArray(corpo)) {
      return cortarLista(corpo).map((item) => limitarParaExibicao(item, profundidade + 1));
    }
    if (corpo && typeof corpo === 'object') {
      const saida = {};
      Object.entries(corpo).forEach(([chave, valor]) => {
        saida[chave] = limitarParaExibicao(valor, profundidade + 1);
      });
      return saida;
    }
    return corpo;
  }

  function cortarLista(lista) {
    if (lista.length <= LIMITE_ITENS_TESTE) return lista;
    const restantes = lista.length - LIMITE_ITENS_TESTE;
    return [
      ...lista.slice(0, LIMITE_ITENS_TESTE),
      `… e mais ${restantes} ${restantes === 1 ? 'item' : 'itens'} — a resposta veio completa, só esta tela de teste que mostra os ${LIMITE_ITENS_TESTE} primeiros.`,
    ];
  }

  // ===== Seleção de rota =====
  function selecionarRota(rota) {
    rotaSelecionada = rota;
    respostaAoVivo = null;
    linguagemAtual = 'curl';
    renderNav(el.busca.value);
    renderDetalhe();
    renderCardSnippet();
    renderCardResposta();
  }

  function escapeHtml(texto) {
    return String(texto ?? '').replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
  }

  // ===== Inicialização =====
  (async function inicializar() {
    await carregarConfig();
    renderNav();
    renderDetalhe();
    renderCardSnippet();
    renderCardResposta();
  })();

  el.busca.addEventListener('input', () => renderNav(el.busca.value));

  // Delegado no container estável (o innerHTML dos campos é recriado a
  // cada troca de rota) — cobre qualquer campo, não só login/senha.
  el.detalhe.addEventListener('input', (e) => {
    if (!e.target.matches('#doc-testar-campos [data-nome]')) return;
    atualizarSnippetAoVivo();
    atualizarTravaDoTeste();
  });
});
