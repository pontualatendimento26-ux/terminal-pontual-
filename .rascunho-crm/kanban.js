// public/js/kanban.js

// Escapa dados vindos do servidor (nomes, mensagens, cores etc.) antes de
// injetar em innerHTML — nome_cliente e ultima_msg_body podem ser 100%
// controlados por quem manda mensagem no WhatsApp (pushName / corpo da msg).
function escapeHtml(valor) {
  if (valor === null || valor === undefined) return '';
  return String(valor)
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
    .replace(/'/g, '&#39;');
}

// O cartão de quem ainda não tem nome cadastrado cai no `titulo`, e esse
// título às vezes foi gravado com o identificador do chat (5561...@c.us) em
// vez do número puro. No WhatsApp isso quase nunca aparecia, porque o nome do
// perfil (pushName) cobria o buraco; no RCS não existe pushName, então o
// cliente sem nome mostrava a extensão técnica na cara da vendedora.
// O sufixo é coisa interna do canal e não deve chegar na tela.
function semSufixoDeChat(valor) {
  return String(valor || '')
    .replace(/@(c\.us|s\.whatsapp\.net|lid)$/i, '')
    .trim();
}

// ==========================================================================
// MOTIVO DA PERDA
//
// Toda vez que um cliente vai pro PERDIDO — arrastando pra coluna, soltando na
// dropzone ou movendo em massa — o sistema para e pergunta o porquê. Antes a
// perda era tabulada por etiqueta, cada atendente escolhia a sua, e o relatório
// não fechava. A lista é fechada e vem do backend (GET /api/motivos-perdido,
// que lê backend/servicos/motivos-perdido.js) pra não existirem duas cópias divergentes.
// ==========================================================================
let motivosPerdidoCache = null;
// Todos, inclusive os desativados: pra dar nome ao que já está gravado.
let motivosTodosCache = null;

async function carregarMotivosPerdido() {
  if (motivosPerdidoCache) return motivosPerdidoCache;
  try {
    const res = await fetch('/api/motivos-perdido', {
      headers: { Authorization: `Bearer ${localStorage.getItem('token')}` },
    });
    const dados = await res.json();
    if (dados && Array.isArray(dados.motivos) && dados.motivos.length > 0) {
      motivosPerdidoCache = dados.motivos;
      motivosTodosCache = dados.todos || dados.motivos;
    }
  } catch (e) {
    console.error('Falha ao carregar os motivos de perda:', e);
  }
  return motivosPerdidoCache;
}

/**
 * Abre o seletor de motivo e devolve o id escolhido, ou null se a pessoa
 * cancelou (nesse caso quem chamou tem que desfazer a movimentação).
 *
 * Sem opção pré-marcada de propósito: com uma marcada, dá pra confirmar no
 * automático e a resposta vira sempre a primeira da lista — que é o mesmo
 * problema da etiqueta errada, só que com outra roupa.
 *
 * @param {{titulo?: string, quantidade?: number}} [opcoes]
 * @returns {Promise<string|null>}
 */
async function pedirMotivoPerdido(opcoes = {}) {
  const motivos = await carregarMotivosPerdido();
  if (!motivos) {
    Swal.fire(
      'Erro',
      'Não foi possível carregar a lista de motivos. Tente de novo.',
      'error'
    );
    return null;
  }

  const qtd = opcoes.quantidade || 1;
  const subtitulo =
    qtd > 1
      ? `O mesmo motivo será aplicado aos <b>${qtd}</b> cartões selecionados.`
      : 'Essa informação aparece no relatório e no histórico do cliente.';

  const listaHtml = motivos
    .map(
      (m) => `
        <label class="opcao-motivo-perdido">
          <input type="radio" name="motivo-perdido" value="${escapeHtml(m.id)}">
          <span class="opcao-motivo-marcador"><i class="bx bx-check"></i></span>
          <span class="opcao-motivo-texto">${escapeHtml(m.nome)}</span>
        </label>`
    )
    .join('');

  const resultado = await Swal.fire({
    title: opcoes.titulo || 'Por que este cliente foi perdido?',
    html: `
      <p class="motivo-perdido-subtitulo">${subtitulo}</p>
      <div class="lista-motivos-perdido">${listaHtml}</div>`,
    showCancelButton: true,
    confirmButtonText: 'Confirmar perda',
    cancelButtonText: 'Cancelar',
    confirmButtonColor: '#ef4444',
    focusConfirm: false,
    preConfirm: () => {
      const marcado = document.querySelector(
        'input[name="motivo-perdido"]:checked'
      );
      if (!marcado) {
        Swal.showValidationMessage('Escolha um motivo para continuar.');
        return false;
      }
      return marcado.value;
    },
  });

  return resultado.isConfirmed ? resultado.value : null;
}

/** Nome de exibição de um motivo salvo, pro cartão do Kanban. */
function nomeMotivoPerdido(id) {
  if (!id) return '';
  const lista = motivosTodosCache || motivosPerdidoCache || [];
  const achado = lista.find((m) => m.id === id);
  return achado ? achado.nome : String(id);
}

// Paleta fixa para os avatares sem foto — a mesma pessoa/cliente sempre cai
// na mesma cor (hash simples do nome/telefone), só um respaldo visual.
const PALETA_AVATAR = [
  '#3e6b96',
  '#8a5a3b',
  '#5b7a5a',
  '#6b5e9c',
  '#a5723f',
  '#4a5a82',
];
function corAvatarPara(texto) {
  const str = String(texto || '');
  let hash = 0;
  for (let i = 0; i < str.length; i++) {
    hash = (hash * 31 + str.charCodeAt(i)) >>> 0;
  }
  return PALETA_AVATAR[hash % PALETA_AVATAR.length];
}

// Config de fábrica do layout do cartão — usada tanto pro funil ainda sem
// config salva no banco quanto pelo modal "Personalizar Cartões" quando
// abre. Um lugar só pra não desalinhar as 3 cópias que existiam antes.
function configCartaoPadrao() {
  return {
    slot1: 'nome',
    slot2: 'telefone',
    slot3: 'data_ultima_msg',
    slot4: 'vazio',
    slot5: 'vazio',
    slot6: 'vazio',
    slot7: 'vazio',
    slotAvatar: 'foto_cliente',
    slotAvatarTopo: 'foto_vendedor',
  };
}

// Resolve o modo do avatar de cima (topo-esquerda). Configs salvas antes
// dessa opção existir (slotAvatarTopo) não têm esse campo — nesse caso cai
// no antigo campo booleano "mostrar_avatar" (true/undefined => foto do
// responsável, false => sem foto), preservando o comportamento que já existia.
function resolveModoAvatarTopo(config) {
  if (config.slotAvatarTopo) return config.slotAvatarTopo;
  return config.mostrar_avatar === false ? 'vazio' : 'foto_vendedor';
}

// Avatar da direita-inferior do cartão: foto do cliente (padrão) ou do
// responsável pelo lead, conforme o slot "slotAvatar" escolhido no
// "Personalizar Cartões". Configs salvas antes dessa opção existir não têm
// esse campo — nesse caso cai no padrão (foto do cliente), preservando o
// comportamento que já existia.
function renderizarFotoInferior(cartao, modo) {
  const modoFinal = modo || 'foto_cliente';
  if (modoFinal === 'vazio') return '';

  if (modoFinal === 'foto_vendedor') {
    const agente =
      cartao.agentes && cartao.agentes[0] ? escapeHtml(cartao.agentes[0]) : '';
    return `<div class="cartao-foto-cliente">
        <i class="ph-fill ph-user-circle cartao-foto-cliente-generica"></i>
        ${
          agente
            ? `<img src="/fotos-equipe/${agente}.webp" loading="lazy" onerror="this.style.display='none'">`
            : ''
        }
      </div>`;
  }

  const temFoto = cartao.profile_pic_url && cartao.profile_pic_url !== 'sem_foto';
  return `<div class="cartao-foto-cliente">
      <i class="ph-fill ph-user-circle cartao-foto-cliente-generica"></i>
      ${
        temFoto
          ? `<img src="${escapeHtml(cartao.profile_pic_url)}" loading="lazy" onerror="this.style.display='none'">`
          : ''
      }
    </div>`;
}

document.addEventListener('DOMContentLoaded', () => {
  // ==========================================================================
  // 1. ELEMENTOS DA TELA
  // ==========================================================================

  // Header / Gaveta
  const triggerCanal = document.getElementById('trigger-canal');
  const nomeCanalAtual = document.getElementById('nome-canal-atual');
  const popupCanais = document.getElementById('popup-canais');
  const overlayCanais = document.getElementById('overlay-canais');
  const listaCanaisPopup = document.getElementById('lista-canais-popup');
  const btnFecharPopupCanais = document.getElementById(
    'btn-fechar-popup-canais'
  );

  // Opções / Modo Edição
  const menuOpcoesQuadro = document.getElementById('menu-opcoes-quadro');
  const btnEngrenagem = document.getElementById('btn-engrenagem');
  const dropdownEngrenagem = document.getElementById('dropdown-engrenagem');
  const btnAtivarEdicao = document.getElementById('btn-ativar-edicao-colunas');

  // Seleção em massa
  const btnAtivarSelecao = document.getElementById('btn-ativar-selecao');
  const barraSelecaoMassa = document.getElementById('barra-selecao-massa');
  const contagemSelecionadosEl = document.getElementById(
    'contagem-selecionados'
  );
  const btnMassaMover = document.getElementById('btn-massa-mover');
  const btnMassaTag = document.getElementById('btn-massa-tag');
  const btnMassaTransferir = document.getElementById('btn-massa-transferir');
  const btnMassaCancelar = document.getElementById('btn-massa-cancelar');

  const botoesModoEdicao = document.getElementById('botoes-modo-edicao');
  const btnCancelarEdicao = document.getElementById(
    'btn-cancelar-edicao-colunas'
  );
  const btnSalvarEdicao = document.getElementById('btn-salvar-edicao-colunas');

  // Quadro Principal
  const areaColunas = document.getElementById('area-colunas-kanban');
  const msgVazia = document.getElementById('mensagem-vazia-kanban');

  // Elementos de Filtro Avançado e Pesquisa
  const inputBuscaKanban = document.getElementById('input-busca-kanban');
  const btnToggleFiltros = document.getElementById('btn-toggle-filtros-kanban');
  const dropdownFiltros = document.getElementById('dropdown-filtros-kanban');
  const btnFecharFiltros = document.getElementById('btn-fechar-filtros');
  const btnAplicarFiltros = document.getElementById(
    'btn-aplicar-filtros-kanban'
  );
  const btnLimparFiltros = document.getElementById('btn-limpar-filtros-kanban');

  const sfContainerTags = document.getElementById('sf-container-tags');
  const sfCriacaoInicio = document.getElementById('sf-criacao-inicio');
  const sfCriacaoFim = document.getElementById('sf-criacao-fim');
  const sfInteracaoInicio = document.getElementById('sf-interacao-inicio');
  const sfInteracaoFim = document.getElementById('sf-interacao-fim');

  // Modais
  const modalCanal = document.getElementById('modal-novo-canal');
  const modalCartao = document.getElementById('modal-novo-cartao');
  const btnAbrirModalCanal = document.getElementById('btn-abrir-modal-canal');
  const btnSalvarCanal = document.getElementById('btn-salvar-canal');
  const btnSalvarCartao = document.getElementById('btn-salvar-cartao');

  // Inputs Modal Cartão
  const inputNomeCanal = document.getElementById('input-nome-canal');
  const inputColunaAlvoId = document.getElementById('input-coluna-alvo-id');
  const inputNumeroCartao = document.getElementById('input-numero-cartao');
  const inputTituloCartao = document.getElementById('input-titulo-cartao');
  const inputValorCartao = document.getElementById('input-valor-cartao');

  // ==========================================================================
  // 2. ESTADO GLOBAL
  // ==========================================================================

  // Lê os parâmetros da URL caso o usuário venha do Dashboard
  let urlTargetFunilId = new URLSearchParams(window.location.search).get(
    'funilId'
  );
  let urlTargetEtapaId = new URLSearchParams(window.location.search).get(
    'etapaId'
  );

  let canalAtualId = null;
  // Lembra o último funil aberto (por navegador) pra reabrir ele sozinho no
  // próximo F5/visita — some no logout porque a tela de logout já limpa
  // todo o localStorage (ver botao-logout em global.js), então um login novo
  // sempre volta pro primeiro funil.
  const CHAVE_ULTIMO_FUNIL = 'kanban_ultimo_funil_id';

  // Guarda-corpo contra a corrida de trocar de funil rápido: cada chamada a
  // carregarDadosDoCanal (troca de canal ou refresh silencioso via socket)
  // ganha um token novo. Se a resposta chegar depois de uma chamada mais
  // nova já ter assumido, ela é descartada — sem isso, um refresh silencioso
  // do funil antigo podia "vazar" as colunas/cartões dele por cima do funil
  // pro qual o usuário já tinha trocado.
  let tokenCargaAtual = 0;
  // Conta quantas cargas NÃO-silenciosas (troca de funil de verdade, com
  // esqueleto visível) estão em andamento — o quadro só volta a aceitar
  // clique de troca de canal quando essa conta zera.
  let cargasVisiveisEmAndamento = 0;
  let boardCarregando = false;

  // Cartão-fantasma usado só no esqueleto de carregamento — mesma casca
  // visual de .kanban-cartao (avatar + duas linhas de texto), sem nenhum
  // dado real, só blocos pulsando.
  function htmlCartaoEsqueleto() {
    return `
      <div class="kanban-cartao cartao-esqueleto" aria-hidden="true">
        <div class="cartao-linha-topo">
          <div class="cartao-bloco-cliente">
            <div class="esqueleto-bloco esqueleto-avatar"></div>
            <div class="cartao-identidade" style="flex:1; gap:6px; display:flex; flex-direction:column;">
              <div class="esqueleto-bloco esqueleto-linha" style="width:70%;"></div>
              <div class="esqueleto-bloco esqueleto-linha" style="width:40%; height:8px;"></div>
            </div>
          </div>
        </div>
        <div class="esqueleto-bloco esqueleto-linha" style="width:90%; margin-top:10px;"></div>
        <div class="esqueleto-bloco esqueleto-linha" style="width:55%; margin-top:6px;"></div>
      </div>`;
  }

  // Mostra só as 3 colunas fixas de todo funil (Entrada/Ganho/Perdido) com
  // cartões-fantasma dentro, no lugar do quadro real, enquanto os dados
  // ainda não chegaram — evita tanto o "pisca vazio" quanto qualquer clique
  // em cima de um quadro pela metade. Assim que os dados de verdade
  // chegarem, renderizarQuadro() substitui esse innerHTML pelo quadro real.
  function renderizarEsqueletoQuadro() {
    const colunasEsqueleto = [
      { nome: 'Etapa de Entrada', cor: '#94a3b8', cards: 4 },
      { nome: 'Ganho', cor: '#94a3b8', cards: 2 },
      { nome: 'Perdido', cor: '#94a3b8', cards: 2 },
    ];

    areaColunas.innerHTML = colunasEsqueleto
      .map(
        (coluna) => `
      <div class="kanban-coluna coluna-esqueleto">
        <div class="kanban-coluna-header">
          <div class="kanban-coluna-titulo" style="color:${coluna.cor};">
            <span class="coluna-cor-dot-estatica" style="display:inline-block; width:7px; height:7px; border-radius:50%; background-color:${coluna.cor}; flex-shrink:0;"></span>
            <span class="coluna-nome-texto" style="cursor:default;">${coluna.nome}</span>
          </div>
        </div>
        <div class="kanban-coluna-corpo">
          ${Array.from({ length: coluna.cards }, htmlCartaoEsqueleto).join('')}
        </div>
      </div>`
      )
      .join('');
  }

  let modoEdicaoAtivo = false;
  let movimentoCartaoEmAndamento = false;

  // Seleção em massa: id do cartão -> numero_wpp (guardamos o número aqui
  // pra não precisar re-consultar o DOM na hora de mover/marcar tag/
  // transferir cada um)
  let modoSelecaoAtivo = false;
  let cartoesSelecionados = new Map();

  // Ações estruturais (criar/renomear/excluir funil, editar colunas,
  // personalizar layout do cartão) agora exigem admin no backend — a UI
  // espelha isso escondendo os controles para quem não é admin.
  const podeEstrutura = Acesso.pode('kanban.estrutura');
  if (!podeEstrutura) {
    if (btnAbrirModalCanal) btnAbrirModalCanal.style.display = 'none';
    if (btnAtivarEdicao) btnAtivarEdicao.style.display = 'none';
    const btnAbrirConfigCartao = document.getElementById(
      'btn-abrir-config-cartao'
    );
    if (btnAbrirConfigCartao) btnAbrirConfigCartao.style.display = 'none';
    const btnMotivos = document.getElementById('btn-abrir-motivos-perdido');
    if (btnMotivos) btnMotivos.style.display = 'none';
  }
  if (!Acesso.pode('agente_ia.configurar')) {
    const btnAbrirAgenteIaEl = document.getElementById('btn-abrir-agente-ia');
    if (btnAbrirAgenteIaEl) btnAbrirAgenteIaEl.style.display = 'none';
  }
  if (!Acesso.pode('kanban.filtrar_atendente')) {

    // Vendedor só enxerga os próprios cartões (o backend já filtra isso em
    // /dados), então filtrar "por atendente" não faz sentido pra ele — só
    // pra admin, que vê a carteira de todo mundo.
    const grupoAtendente = document.getElementById('filtro-atendente-grupo');
    if (grupoAtendente) grupoAtendente.style.display = 'none';

    // Sem o Atendente, CPF e Motivo de perda dividem a linha em dois.
    const gradeCampos = grupoAtendente && grupoAtendente.closest('.df-grade-campos');
    if (gradeCampos) gradeCampos.classList.add('df-grade-campos--sem-atendente');
  }

  let tagsSelecionadasParaFiltro = [];
  let filtroSemEtiqueta = false;
  let filtroCpf = 'todos'; // 'todos' | 'com' | 'sem'
  // Multi-seleção (23/09/2026): vale o cartão que bater com QUALQUER um dos
  // marcados. Atendente: username ou 'sem_dono'. Motivo: id de
  // backend/servicos/motivos-perdido.js — só cartão perdido tem motivo, então marcar
  // qualquer um já mostra só os perdidos.
  let filtroAgentes = [];
  let filtroMotivos = [];
  // Uma linha fixa por campo personalizado do CRM (renderizada em
  // renderizarFiltrosCamposPersonalizados) — { campo, tipo, grupo, opcoes,
  // operadorEl, valorEl, linhaEl }. Todas as ativas precisam bater (AND)
  // pro cartão aparecer.
  let filtrosCamposPersonalizados = [];
  let canaisGlobais = [];
  let camposDinamicosCRM = [];

  // QUADRO POR PÁGINAS (19/10/2026, ver backend/servicos/kanban-dados.js): o
  // servidor manda cada coluna com o TOTAL já filtrado e só a 1ª página de
  // cartões; rolar busca a próxima página de cada coluna. Busca, filtros e
  // ordenação rodam no servidor (parametrosDoQuadro). Antes vinha o funil
  // inteiro — 121.841 cartões / 128,8 MB no RCS — e tudo era feito aqui.
  let estadoKanban = {
    configCartaoAtual: null,

    colunas: [], // cada uma com .total (já filtrado) vindo do servidor
    cartoesOriginais: [], // os cartões JÁ CARREGADOS (todas as colunas)
    cartoes: [], // o mesmo array (nomes antigos mantidos pelos outros usos)
    porColuna: {}, // colunaId -> { carregados, fim, carregando }
    tamanhoLote: 30,
    ordenacaoAtual: 'manual',
  };

  // ==========================================================================
  // 3. INICIALIZAÇÃO E GAVETA DE CANAIS
  // ==========================================================================
  carregarCanais();

  function abrirGavetaCanais() {
    popupCanais.classList.add('aberto');
    overlayCanais.classList.remove('hidden');
    triggerCanal.classList.add('aberto');

    // --- VERIFICA SE ESTÁ NO MODO EDIÇÃO ---
    const botoesEdicao = document.getElementById('botoes-modo-edicao');
    if (botoesEdicao && !botoesEdicao.classList.contains('hidden')) {
      // Se os botões de edição estiverem aparecendo, ativa o modo de exclusão na lista lateral
      popupCanais.classList.add('modo-edicao-ativo');
    } else {
      // Se não, garante que está escondido
      popupCanais.classList.remove('modo-edicao-ativo');
    }
  }

  function fecharGavetaCanais() {
    popupCanais.classList.remove('aberto');
    overlayCanais.classList.add('hidden');
    triggerCanal.classList.remove('aberto');
  }

  triggerCanal.addEventListener('click', abrirGavetaCanais);
  overlayCanais.addEventListener('click', fecharGavetaCanais);
  btnFecharPopupCanais.addEventListener('click', fecharGavetaCanais);

  // Gatilho do Scroll Global (Paginação)
  areaColunas.addEventListener('scroll', () => {
    if (
      areaColunas.scrollTop + areaColunas.clientHeight >=
      areaColunas.scrollHeight - 300
    ) {
      carregarMaisCartoes();
    }
  });

  function selecionarCanal(id, nome) {
    // Enquanto uma troca de funil anterior ainda está carregando, ignora
    // cliques novos — o overlay já cobre a tela, isso é só um backstop caso
    // algo chame selecionarCanal por fora do clique normal.
    if (boardCarregando) return;
    canalAtualId = id;
    try {
      localStorage.setItem(CHAVE_ULTIMO_FUNIL, id);
    } catch (e) {}
    nomeCanalAtual.textContent = nome;

    // 👇 Pega a configuração do banco e joga na memória da tela
    const canalData = canaisGlobais.find((c) => c.id == id);
    if (canalData && canalData.config_cartao) {
      estadoKanban.configCartaoAtual =
        typeof canalData.config_cartao === 'string'
          ? JSON.parse(canalData.config_cartao)
          : canalData.config_cartao;
    } else {
      // Config Padrão de Fábrica caso o funil seja muito antigo
      estadoKanban.configCartaoAtual = configCartaoPadrao();
    }

    document
      .querySelectorAll('.item-canal')
      .forEach((el) => el.classList.remove('ativo'));
    const itens = Array.from(listaCanaisPopup.children);
    const ativoEl = itens.find((el) => String(el.dataset.canalId) === String(id));
    if (ativoEl) ativoEl.classList.add('ativo');

    menuOpcoesQuadro.style.display = 'block';
    fecharGavetaCanais();
    carregarDadosDoCanal(id);
  }

  async function carregarCanais() {
    try {
      const res = await fetch('/api/kanban/canais', {
        headers: { Authorization: `Bearer ${localStorage.getItem('token')}` },
      });
      const dados = await res.json();

      if (dados.sucesso) {
        canaisGlobais = dados.canais;
        listaCanaisPopup.innerHTML = '';

        if (dados.canais.length === 0) {
          listaCanaisPopup.innerHTML =
            "<div style='padding:15px; color:#888; text-align:center;'>Nenhum canal criado.</div>";
          nomeCanalAtual.textContent = 'Crie um Canal';
          menuOpcoesQuadro.style.display = 'none';
          return;
        }

        dados.canais.forEach((canal) => {
          const div = document.createElement('div');
          div.className = 'item-canal';
          div.dataset.canalId = canal.id;
          if (canal.id == canalAtualId) div.classList.add('ativo');

          // Nome do canal via textContent (não innerHTML) + botões sem dados
          // interpolados em onclick, para evitar XSS/quebra de atributo.
          div.innerHTML = `
            <div style="display: flex; align-items: center; gap: 8px;">
                <i class="ph-bold ph-kanban"></i>
                <span class="nome-canal-texto"></span>
            </div>
            <div style="display: flex; gap: 4px;">
                <button class="btn-editar-funil" title="Editar Nome">
                    <i class="bx bx-edit-alt"></i>
                </button>
                <button class="btn-deletar-funil" title="Excluir Funil">
                    <i class="bx bx-trash"></i>
                </button>
            </div>
          `;
          div.querySelector('.nome-canal-texto').textContent = canal.nome;
          if (podeEstrutura) {
            div
              .querySelector('.btn-editar-funil')
              .addEventListener('click', (event) =>
                editarFunil(canal.id, canal.nome, event)
              );
            div
              .querySelector('.btn-deletar-funil')
              .addEventListener('click', (event) =>
                deletarFunil(canal.id, canal.nome, event)
              );
          } else {
            div.querySelector('.btn-editar-funil').remove();
            div.querySelector('.btn-deletar-funil').remove();
          }

          div.onclick = () => selecionarCanal(canal.id, canal.nome);
          listaCanaisPopup.appendChild(div);
        });

        if (!canalAtualId && dados.canais.length > 0) {
          if (urlTargetFunilId) {
            // Veio de um link direto (ex: Dashboard) — isso manda mais que
            // o último funil lembrado.
            const canalAlvo = dados.canais.find(
              (c) => String(c.id) === String(urlTargetFunilId)
            );
            if (canalAlvo) {
              selecionarCanal(canalAlvo.id, canalAlvo.nome);
            } else {
              selecionarCanal(dados.canais[0].id, dados.canais[0].nome);
            }
          } else {
            // Sem link direto: reabre o último funil que o usuário tinha
            // aberto (se ele ainda existir), senão cai no primeiro da lista.
            let ultimoFunilId = null;
            try {
              ultimoFunilId = localStorage.getItem(CHAVE_ULTIMO_FUNIL);
            } catch (e) {}
            const canalLembrado = ultimoFunilId
              ? dados.canais.find((c) => String(c.id) === String(ultimoFunilId))
              : null;
            if (canalLembrado) {
              selecionarCanal(canalLembrado.id, canalLembrado.nome);
            } else {
              selecionarCanal(dados.canais[0].id, dados.canais[0].nome);
            }
          }
        }
      }
    } catch (erro) {
      console.error('Erro ao carregar canais:', erro);
    }
  }

  // Os filtros e a ordenação da tela, no formato da query string que o
  // servidor entende (backend/servicos/kanban-dados.js → montarFiltros).
  function parametrosDoQuadro() {
    const p = new URLSearchParams();
    const termo = (inputBuscaKanban.value || '').trim();
    if (termo) p.set('busca', termo);
    if (filtroAgentes.length) p.set('agentes', filtroAgentes.join(','));
    if (filtroMotivos.length) p.set('motivos', filtroMotivos.join(','));
    if (filtroSemEtiqueta) p.set('semEtiqueta', '1');
    else if (tagsSelecionadasParaFiltro.length) p.set('tags', tagsSelecionadasParaFiltro.join(','));
    if (filtroCpf !== 'todos') p.set('cpf', filtroCpf);
    if (sfCriacaoInicio.value) p.set('criaIni', sfCriacaoInicio.value);
    if (sfCriacaoFim.value) p.set('criaFim', sfCriacaoFim.value);
    if (sfInteracaoInicio.value) p.set('intIni', sfInteracaoInicio.value);
    if (sfInteracaoFim.value) p.set('intFim', sfInteracaoFim.value);
    const campos = filtrosCamposPersonalizados.filter(filtroCampoEstaAtivo).map((f) => {
      const inputs = Array.from(f.valorEl.querySelectorAll('input, select'));
      return {
        campo: f.campo,
        tipo: f.tipo,
        grupo: f.grupo,
        op: f.operadorEl.value,
        v1: inputs[0] ? inputs[0].value : '',
        v2: inputs[1] ? inputs[1].value : '',
      };
    });
    if (campos.length) p.set('campos', JSON.stringify(campos));
    if (estadoKanban.ordenacaoAtual !== 'manual') p.set('ordem', estadoKanban.ordenacaoAtual);
    return p;
  }

  /**
   * Carrega o quadro. `silencioso`:
   *   false     troca de funil: esqueleto na tela
   *   true      refresh do socket: mantém o scroll e o quanto cada coluna já
   *             tinha rolado (até 300 por coluna)
   *   'filtro'  busca/filtro/ordenação mudou: quadro esmaecido até chegar,
   *             volta pro topo
   */
  async function carregarDadosDoCanal(canalId, silencioso = false) {
    const meuToken = ++tokenCargaAtual;
    const ehRefresh = silencioso === true;
    const ehFiltro = silencioso === 'filtro';

    if (!silencioso) {
      cargasVisiveisEmAndamento++;
      boardCarregando = true;
      renderizarEsqueletoQuadro();
    }
    if (ehFiltro) areaColunas.classList.add('kanban-filtrando');

    try {
      const params = parametrosDoQuadro();
      if (ehRefresh) {
        const porColuna = {};
        Object.entries(estadoKanban.porColuna).forEach(([id, est]) => {
          if (est.carregados > estadoKanban.tamanhoLote)
            porColuna[id] = Math.min(300, est.carregados);
        });
        if (Object.keys(porColuna).length) params.set('porColuna', JSON.stringify(porColuna));
      }
      // Em paralelo com os cartões: o slot "Motivo da perda" traduz o id
      // gravado no cliente pro nome de exibição, e sem a lista em mãos o
      // cartão mostraria "sem_resposta" cru. Cacheado, então só a 1ª carga paga.
      const [res] = await Promise.all([
        fetch(`/api/kanban/canais/${canalId}/dados?${params}`, {
          headers: { Authorization: `Bearer ${localStorage.getItem('token')}` },
          cache: 'no-store',
        }),
        carregarMotivosPerdido(),
      ]);
      const dados = await res.json();

      // Uma carga mais nova (nova troca de funil, refresh do socket ou outro
      // filtro) já assumiu enquanto essa respondia — descarta.
      if (meuToken !== tokenCargaAtual) return;

      if (dados.sucesso) {
        const scrollAnteriorY = areaColunas.scrollTop;
        const scrollAnteriorX = areaColunas.scrollLeft;

        // Servidor ainda sem as rotas por página (antes do restart do
        // server.js): veio o funil INTEIRO e sem `total`. Desenha só a 1ª
        // página de cada coluna — desenhar os 121 mil do RCS travaria o
        // navegador — e não pede mais páginas (a rota não existe ainda).
        const servidorAntigo = dados.colunas.some((c) => typeof c.total !== 'number');
        if (servidorAntigo) {
          const porEtapa = {};
          dados.cartoes.forEach((x) => (porEtapa[x.coluna_id] = porEtapa[x.coluna_id] || []).push(x));
          dados.colunas.forEach((c) => (c.total = (porEtapa[c.id] || []).length));
          dados.cartoes = dados.colunas.flatMap((c) =>
            (porEtapa[c.id] || [])
              .sort((a, b) => a.ordem - b.ordem)
              .slice(0, estadoKanban.tamanhoLote)
          );
        }

        estadoKanban.colunas = dados.colunas;
        estadoKanban.cartoesOriginais = dados.cartoes;
        estadoKanban.cartoes = estadoKanban.cartoesOriginais;
        estadoKanban.porColuna = {};
        dados.colunas.forEach((c) => {
          const carregados = dados.cartoes.filter((x) => x.coluna_id === c.id).length;
          estadoKanban.porColuna[c.id] = {
            carregados,
            fim: servidorAntigo || carregados >= (c.total || 0),
            carregando: false,
          };
        });

        renderizarQuadro(estadoKanban.colunas, estadoKanban.cartoes);

        if (ehRefresh) {
          setTimeout(() => {
            areaColunas.scrollTop = scrollAnteriorY;
            areaColunas.scrollLeft = scrollAnteriorX;
          }, 10);
        } else if (ehFiltro) {
          areaColunas.scrollTop = 0;
        }
      }
    } catch (erro) {
      if (meuToken === tokenCargaAtual && !silencioso)
        areaColunas.innerHTML =
          '<div style="margin:auto; color:red;">Erro ao carregar os dados.</div>';
    } finally {
      if (meuToken === tokenCargaAtual) areaColunas.classList.remove('kanban-filtrando');
      if (!silencioso) {
        cargasVisiveisEmAndamento = Math.max(0, cargasVisiveisEmAndamento - 1);
        if (cargasVisiveisEmAndamento === 0) {
          boardCarregando = false;
        }
      }
    }
  }

  // ==========================================================================
  // 4. MOTOR DE FILTROS AVANÇADOS (KOMMO STYLE)
  // ==========================================================================

  // Abrir e fechar o Dropdown Gigante
  if (btnToggleFiltros) {
    btnToggleFiltros.addEventListener('click', (e) => {
      e.stopPropagation();
      dropdownFiltros.classList.toggle('hidden');

      if (!dropdownFiltros.classList.contains('hidden')) {
        carregarFiltrosSelects();
      }
    });
  }

  if (btnFecharFiltros) {
    btnFecharFiltros.addEventListener('click', () => {
      dropdownFiltros.classList.add('hidden');
    });
  }

  // Filtro de CPF (controle segmentado: Todos / Com CPF / Sem CPF)
  const segmentadoCpf = document.getElementById('sf-segmentado-cpf');
  if (segmentadoCpf) {
    segmentadoCpf.querySelectorAll('.df-segmentado-opcao').forEach((btn) => {
      btn.addEventListener('click', () => {
        filtroCpf = btn.dataset.valor;
        segmentadoCpf
          .querySelectorAll('.df-segmentado-opcao')
          .forEach((b) => b.classList.remove('ativo'));
        btn.classList.add('ativo');
      });
    });
  }

  // Fecha se clicar fora
  document.addEventListener('click', (e) => {
    if (dropdownFiltros && !dropdownFiltros.classList.contains('hidden')) {
      if (
        !dropdownFiltros.contains(e.target) &&
        !btnToggleFiltros.contains(e.target)
      ) {
        dropdownFiltros.classList.add('hidden');
      }
    }
  });

  // Busca Vendedores e Tags
  // Instâncias únicas, reaproveitadas toda vez que o painel abre.
  let ddAgenteFiltro = null;
  let ddMotivoFiltro = null;

  async function carregarFiltrosSelects() {
    renderizarFiltrosCamposPersonalizados();

    if (!ddAgenteFiltro) {
      const opcoes = [{ value: 'sem_dono', label: 'Sem dono (Fila Geral)' }];
      try {
        const res = await fetch('/api/team/vendedores', {
          headers: { Authorization: `Bearer ${localStorage.getItem('token')}` },
        });
        const vendedores = await res.json();
        vendedores.forEach((v) =>
          opcoes.push({ value: v.username, label: v.name || v.username })
        );
      } catch (e) {}

      ddAgenteFiltro = criarDropdownMultiEstilizado({
        opcoes,
        nomeSingular: 'Atendente',
        nomePlural: 'Atendentes',
        limiteInferior: () => dropdownFiltros.querySelector('.df-footer'),
        onChange: (valores) => {
          filtroAgentes = valores;
        },
      });
      document
        .getElementById('sf-agente-dropdown-slot')
        .appendChild(ddAgenteFiltro.elemento);
    }

    if (!ddMotivoFiltro) {
      const motivos = (await carregarMotivosPerdido()) || [];
      ddMotivoFiltro = criarDropdownMultiEstilizado({
        opcoes: motivos.map((m) => ({ value: m.id, label: m.nome })),
        nomeSingular: 'Motivo',
        nomePlural: 'Motivos',
        limiteInferior: () => dropdownFiltros.querySelector('.df-footer'),
        onChange: (valores) => {
          filtroMotivos = valores;
        },
      });
      document
        .getElementById('sf-motivo-dropdown-slot')
        .appendChild(ddMotivoFiltro.elemento);
    }

    if (sfContainerTags.innerHTML.includes('Carregando')) {
      try {
        const res = await fetch('/api/tags?tipo=chat');
        const tags = await res.json();
        sfContainerTags.innerHTML = '';

        // Pílula especial: "Sem etiqueta" — mutuamente exclusiva com as
        // tags reais (não faz sentido combinar "tem a tag X" com "não tem
        // nenhuma"), então escolher uma desmarca a outra.
        const chipSemEtiqueta = document.createElement('div');
        chipSemEtiqueta.className = 'df-chip df-chip--vazia';
        chipSemEtiqueta.textContent = 'Sem etiqueta';
        chipSemEtiqueta.onclick = () => {
          filtroSemEtiqueta = !filtroSemEtiqueta;
          chipSemEtiqueta.classList.toggle('selecionada', filtroSemEtiqueta);
          if (filtroSemEtiqueta) {
            tagsSelecionadasParaFiltro = [];
            sfContainerTags
              .querySelectorAll('.df-chip:not(.df-chip--vazia)')
              .forEach((el) => el.classList.remove('selecionada'));
          }
        };
        sfContainerTags.appendChild(chipSemEtiqueta);

        tags.forEach((tag) => {
          const chip = document.createElement('div');
          chip.className = 'df-chip';
          chip.dataset.id = tag.id;
          chip.innerHTML = `
            <span class="df-chip__ponto" style="background-color: ${escapeHtml(tag.color)}"></span>
            ${escapeHtml(tag.name)}
          `;

          chip.onclick = () => {
            chip.classList.toggle('selecionada');
            const index = tagsSelecionadasParaFiltro.indexOf(tag.id);
            if (index > -1) tagsSelecionadasParaFiltro.splice(index, 1);
            else tagsSelecionadasParaFiltro.push(tag.id);

            if (tagsSelecionadasParaFiltro.length > 0 && filtroSemEtiqueta) {
              filtroSemEtiqueta = false;
              chipSemEtiqueta.classList.remove('selecionada');
            }
          };
          sfContainerTags.appendChild(chip);
        });
      } catch (e) {
        sfContainerTags.innerHTML = 'Erro ao carregar tags.';
      }
    }
  }

  // ------------------------------------------------------------------------
  // Filtro por Campos Personalizados — uma linha FIXA por campo do CRM
  // (sem botão de adicionar/remover, todas ficam sempre visíveis). Cada
  // linha tem seu próprio operador — as opções variam pelo tipo do campo
  // (número, data, texto, lista) — e o input de valor muda de cara conforme
  // o operador escolhido: nenhum input pra "Está vazio", dois pra "Está
  // entre", um pro resto. Linha sem valor preenchido simplesmente não entra
  // na conta do filtro.
  // ------------------------------------------------------------------------
  const OPERADORES_CAMPO_TEXTO = [
    { id: 'contem', label: 'Contém', n: 1 },
    { id: 'igual', label: 'É igual a', n: 1 },
    { id: 'comeca', label: 'Começa com', n: 1 },
    { id: 'termina', label: 'Termina com', n: 1 },
    { id: 'vazio', label: 'Está vazio', n: 0 },
  ];
  const OPERADORES_CAMPO_NUMERO = [
    { id: 'igual', label: 'É igual a', n: 1 },
    { id: 'maior', label: 'É maior que', n: 1 },
    { id: 'menor', label: 'É menor que', n: 1 },
    { id: 'gte', label: 'Maior ou igual a', n: 1 },
    { id: 'lte', label: 'Menor ou igual a', n: 1 },
    { id: 'entre', label: 'Está entre', n: 2 },
    { id: 'vazio', label: 'Está vazio', n: 0 },
  ];
  const OPERADORES_CAMPO_DATA = [
    { id: 'igual', label: 'É igual a', n: 1 },
    { id: 'maior', label: 'Depois de', n: 1 },
    { id: 'menor', label: 'Antes de', n: 1 },
    { id: 'gte', label: 'Maior ou igual a', n: 1 },
    { id: 'lte', label: 'Menor ou igual a', n: 1 },
    { id: 'entre', label: 'Está entre', n: 2 },
    { id: 'vazio', label: 'Está vazio', n: 0 },
  ];
  const OPERADORES_CAMPO_SELECT = [
    { id: 'igual', label: 'É igual a', n: 1 },
    { id: 'vazio', label: 'Está vazio', n: 0 },
  ];

  function grupoDoTipoCampo(tipo) {
    if (tipo === 'number' || tipo === 'currency') return 'numero';
    if (tipo === 'date' || tipo === 'datetime') return 'data';
    if (tipo === 'select' || tipo === 'boolean') return 'select';
    return 'texto';
  }
  function operadoresDoGrupoCampo(grupo) {
    if (grupo === 'numero') return OPERADORES_CAMPO_NUMERO;
    if (grupo === 'data') return OPERADORES_CAMPO_DATA;
    if (grupo === 'select') return OPERADORES_CAMPO_SELECT;
    return OPERADORES_CAMPO_TEXTO;
  }
  function tipoInputDoCampo(tipo) {
    if (tipo === 'number' || tipo === 'currency') return 'number';
    if (tipo === 'date') return 'date';
    if (tipo === 'datetime') return 'datetime-local';
    return 'text';
  }

  // Reconstrói o(s) input(s) de valor de uma linha conforme o operador
  // selecionado no momento, e atualiza o destaque visual "filtro ativo".
  function atualizarValorFiltroCampo(item) {
    const operador = operadoresDoGrupoCampo(item.grupo).find(
      (o) => o.id === item.operadorEl.value
    );
    item.valorEl.innerHTML = '';

    const marcarAtiva = () => {
      item.linhaEl.classList.toggle(
        'df-linha-filtro-campo--ativa',
        filtroCampoEstaAtivo(item)
      );
    };

    if (operador.n === 0) {
      const nota = document.createElement('span');
      nota.className = 'df-linha-filtro-campo-nota';
      nota.textContent = 'Sem valor necessário';
      item.valorEl.appendChild(nota);
      marcarAtiva();
      return;
    }

    const criarCampoValor = () => {
      let el;
      if (item.tipo === 'boolean') {
        el = document.createElement('select');
        el.className = 'df-input';
        el.innerHTML =
          '<option value="">Selecione...</option><option value="true">Sim</option><option value="false">Não</option>';
      } else if (item.grupo === 'select' && item.opcoes.length > 0) {
        el = document.createElement('select');
        el.className = 'df-input';
        el.innerHTML =
          '<option value="">Selecione...</option>' +
          item.opcoes
            .map((o) => `<option value="${escapeHtml(o)}">${escapeHtml(o)}</option>`)
            .join('');
      } else {
        el = document.createElement('input');
        el.className = 'df-input';
        el.type = tipoInputDoCampo(item.tipo);
        if (el.type === 'text') el.placeholder = 'Digite o valor...';
      }
      el.addEventListener('input', marcarAtiva);
      el.addEventListener('change', marcarAtiva);
      return el;
    };

    if (operador.n === 1) {
      item.valorEl.appendChild(criarCampoValor());
    } else {
      const linhaEntre = document.createElement('div');
      linhaEntre.className = 'df-input-row';
      linhaEntre.appendChild(criarCampoValor());
      const sep = document.createElement('span');
      sep.className = 'df-separator';
      sep.textContent = 'e';
      linhaEntre.appendChild(sep);
      linhaEntre.appendChild(criarCampoValor());
      item.valorEl.appendChild(linhaEntre);
    }
    marcarAtiva();
  }

  // Uma linha por campo do CRM, sempre visível. Só roda uma vez (chamadas
  // seguintes são no-op) — é chamada tanto direto após carregar
  // camposDinamicosCRM quanto toda vez que o painel de filtros abre, pro
  // caso de uma dessas duas rodar antes da outra terminar.
  function renderizarFiltrosCamposPersonalizados() {
    const lista = document.getElementById('sf-lista-filtros-campos');
    if (!lista || lista.children.length > 0 || camposDinamicosCRM.length === 0)
      return;

    camposDinamicosCRM.forEach((c) => {
      const grupo = grupoDoTipoCampo(c.tipo);

      const linha = document.createElement('div');
      linha.className = 'df-linha-filtro-campo';

      const nomeEl = document.createElement('span');
      nomeEl.className = 'df-linha-filtro-campo-nome';
      nomeEl.title = c.nome;
      nomeEl.textContent = c.nome.charAt(0).toUpperCase() + c.nome.slice(1);

      const operadorEl = document.createElement('select');
      operadorEl.className = 'df-input df-linha-filtro-campo-operador';
      operadoresDoGrupoCampo(grupo).forEach((op) => {
        const opt = document.createElement('option');
        opt.value = op.id;
        opt.textContent = op.label;
        operadorEl.appendChild(opt);
      });

      const valorEl = document.createElement('div');
      valorEl.className = 'df-linha-filtro-campo-valor';

      const item = {
        campo: c.nome,
        tipo: c.tipo || 'text',
        grupo,
        opcoes: Array.isArray(c.opcoes) ? c.opcoes : [],
        operadorEl,
        valorEl,
        linhaEl: linha,
      };
      filtrosCamposPersonalizados.push(item);

      operadorEl.addEventListener('change', () =>
        atualizarValorFiltroCampo(item)
      );
      atualizarValorFiltroCampo(item);

      linha.appendChild(nomeEl);
      linha.appendChild(operadorEl);
      linha.appendChild(valorEl);
      lista.appendChild(linha);
    });
  }

  // Uma linha só "conta" pro filtro quando tem valor preenchido — exceto
  // "Está vazio", que já é uma condição completa por si só.
  function filtroCampoEstaAtivo(item) {
    const operador = operadoresDoGrupoCampo(item.grupo).find(
      (o) => o.id === item.operadorEl.value
    );
    if (!operador) return false;
    if (operador.n === 0) return true;

    const inputs = Array.from(item.valorEl.querySelectorAll('input, select'));
    if (operador.n === 2) return inputs.every((el) => el.value !== '');
    return inputs.length > 0 && inputs[0].value !== '';
  }

  // "Liberado" (currency) chega salvo como "1.234,56" (máscara BR) — os
  // demais tipos numéricos usam o formato nativo do <input type="number">.
  function valorNumericoDoCartao(valorCru, tipo) {
    let str = String(valorCru).trim();
    if (tipo === 'currency') str = str.replace(/\./g, '');
    str = str.replace(',', '.');
    const n = parseFloat(str);
    return Number.isNaN(n) ? null : n;
  }

  // Compara o valor bruto do campo customizado do cartão (`valorCru`)
  // contra o operador + valor(es) escolhidos na linha `item`.
  function avaliarFiltroCampo(item, valorCru) {
    if (!filtroCampoEstaAtivo(item)) return true;

    const operadorId = item.operadorEl.value;
    const cruStr =
      valorCru === null || valorCru === undefined ? '' : String(valorCru).trim();

    if (operadorId === 'vazio') return cruStr === '';
    if (cruStr === '') return false;

    const inputs = Array.from(item.valorEl.querySelectorAll('input, select'));

    if (item.grupo === 'numero') {
      const cruNum = valorNumericoDoCartao(cruStr, item.tipo);
      if (cruNum === null) return false;
      const v1 = parseFloat(inputs[0].value);
      if (operadorId === 'igual') return cruNum === v1;
      if (operadorId === 'maior') return cruNum > v1;
      if (operadorId === 'menor') return cruNum < v1;
      if (operadorId === 'gte') return cruNum >= v1;
      if (operadorId === 'lte') return cruNum <= v1;
      if (operadorId === 'entre') {
        const v2 = parseFloat(inputs[1].value);
        const min = Math.min(v1, v2);
        const max = Math.max(v1, v2);
        return cruNum >= min && cruNum <= max;
      }
      return true;
    }

    if (item.grupo === 'data') {
      const cruData = new Date(cruStr);
      if (Number.isNaN(cruData.getTime())) return false;
      const d1 = new Date(inputs[0].value);
      if (operadorId === 'igual')
        return cruData.toDateString() === d1.toDateString();
      if (operadorId === 'maior') return cruData > d1;
      if (operadorId === 'menor') return cruData < d1;
      if (operadorId === 'gte') return cruData >= d1;
      if (operadorId === 'lte') return cruData <= d1;
      if (operadorId === 'entre') {
        const d2 = new Date(inputs[1].value);
        const min = d1 <= d2 ? d1 : d2;
        const max = d1 <= d2 ? d2 : d1;
        return cruData >= min && cruData <= max;
      }
      return true;
    }

    // texto e select — comparação simples de string
    const valorFiltro = (inputs[0].value || '').trim().toLowerCase();
    const cruLower = cruStr.toLowerCase();
    if (operadorId === 'igual') return cruLower === valorFiltro;
    if (operadorId === 'contem') return cruLower.includes(valorFiltro);
    if (operadorId === 'comeca') return cruLower.startsWith(valorFiltro);
    if (operadorId === 'termina') return cruLower.endsWith(valorFiltro);
    return true;
  }

  // Limpar Filtros
  if (btnLimparFiltros) {
    btnLimparFiltros.addEventListener('click', () => {
      document
        .querySelectorAll('.df-chip')
        .forEach((el) => el.classList.remove('selecionada'));
      tagsSelecionadasParaFiltro = [];
      filtroSemEtiqueta = false;
      filtroAgentes = [];
      filtroMotivos = [];
      if (ddAgenteFiltro) ddAgenteFiltro.limpar();
      if (ddMotivoFiltro) ddMotivoFiltro.limpar();
      sfCriacaoInicio.value = '';
      sfCriacaoFim.value = '';
      sfInteracaoInicio.value = '';
      sfInteracaoFim.value = '';

      filtroCpf = 'todos';
      if (segmentadoCpf) {
        segmentadoCpf
          .querySelectorAll('.df-segmentado-opcao')
          .forEach((b) => b.classList.toggle('ativo', b.dataset.valor === 'todos'));
      }

      // As linhas de campos personalizados agora são fixas — "Limpar" só
      // reseta cada uma pro operador padrão e apaga o(s) valor(es).
      filtrosCamposPersonalizados.forEach((f) => {
        f.operadorEl.selectedIndex = 0;
        atualizarValorFiltroCampo(f);
      });

      aplicarFiltroPesquisa(1);
      btnToggleFiltros.classList.remove('filtro-ativo');
      dropdownFiltros.classList.add('hidden');
    });
  }

  // Aplicar Filtros
  if (btnAplicarFiltros) {
    btnAplicarFiltros.addEventListener('click', () => {
      const temFiltroAtivo =
        tagsSelecionadasParaFiltro.length > 0 ||
        filtroSemEtiqueta ||
        filtroAgentes.length > 0 ||
        filtroMotivos.length > 0 ||
        sfCriacaoInicio.value ||
        sfCriacaoFim.value ||
        sfInteracaoInicio.value ||
        sfInteracaoFim.value ||
        filtroCpf !== 'todos' ||
        filtrosCamposPersonalizados.some((f) => filtroCampoEstaAtivo(f));

      if (temFiltroAtivo) btnToggleFiltros.classList.add('filtro-ativo');
      else btnToggleFiltros.classList.remove('filtro-ativo');

      const badge = document.getElementById('df-contagem-badge');
      if (badge) {
        let total = tagsSelecionadasParaFiltro.length > 0 || filtroSemEtiqueta ? 1 : 0;
        if (filtroAgentes.length > 0) total++;
        if (filtroMotivos.length > 0) total++;
        if (sfCriacaoInicio.value || sfCriacaoFim.value) total++;
        if (sfInteracaoInicio.value || sfInteracaoFim.value) total++;
        if (filtroCpf !== 'todos') total++;
        total += filtrosCamposPersonalizados.filter((f) =>
          filtroCampoEstaAtivo(f)
        ).length;

        badge.textContent = total;
        badge.classList.toggle('hidden', total === 0);
      }

      aplicarFiltroPesquisa(1);
      dropdownFiltros.classList.add('hidden');
    });
  }

  // Pesquisa de Texto Rápida — agora vai ao servidor, então espera a pessoa
  // parar de digitar (350 ms) em vez de buscar a cada tecla.
  let esperaBuscaKanban = null;
  if (inputBuscaKanban) {
    inputBuscaKanban.addEventListener('input', () => {
      clearTimeout(esperaBuscaKanban);
      esperaBuscaKanban = setTimeout(() => {
        areaColunas.scrollLeft = 0;
        aplicarFiltroPesquisa(1);
      }, 350);
    });
  }

  // Busca, filtros e ordenação: rodam no servidor agora (ver
  // parametrosDoQuadro e backend/servicos/kanban-dados.js) — aqui só pede o quadro de
  // novo. O parâmetro ficou por compatibilidade com quem ainda o passa.
  function aplicarFiltroPesquisa() {
    if (canalAtualId) carregarDadosDoCanal(canalAtualId, 'filtro');
  }

  // ==========================================================================
  // 5. RENDERIZAÇÃO DO QUADRO
  // ==========================================================================

  function renderizarQuadro(colunas, cartoes) {
    areaColunas.innerHTML = '';

    if (colunas.length === 0) {
      msgVazia.textContent =
        'Este canal ainda não possui colunas. Abra a engrenagem no topo para criar.';
      areaColunas.appendChild(msgVazia);

      // 👇 INJEÇÃO 1: Zera e esconde o minimapa se o funil não tiver colunas
      if (window.atualizarEstruturaMinimapa) {
        window.atualizarEstruturaMinimapa(0);
      }
      return;
    }

    colunas.forEach((coluna) => {
      const divColuna = document.createElement('div');
      divColuna.className = 'kanban-coluna';
      if (coluna.fixa) divColuna.classList.add('coluna-fixa');
      divColuna.dataset.colunaId = coluna.id;
      // 'entrada' | 'ganho' | 'perdido' nas 3 colunas de sistema, ausente
      // nas normais — usado pra travar o botão "+" e o drag-and-drop delas.
      if (coluna.tipo_sistema) divColuna.dataset.tipoSistema = coluna.tipo_sistema;

      // O contador é o TOTAL da coluna (vem do servidor, já filtrado), não
      // só o que já está desenhado.
      const cartoesDestaColuna = cartoes.filter(
        (c) => c.coluna_id === coluna.id
      );
      const totalDaColuna =
        typeof coluna.total === 'number' ? coluna.total : cartoesDestaColuna.length;
      divColuna.dataset.totalCartoes = totalDaColuna;

      const nomeColunaSeguro = escapeHtml(coluna.nome);
      const corColunaSegura = escapeHtml(coluna.cor);

      const iconeExcluirColuna =
        !coluna.fixa && podeEstrutura
          ? '<i class="bx bx-trash btn-deletar-coluna" title="Excluir Coluna"></i>'
          : '';

      const contadorHtml = `<span class="kanban-coluna-contador">${totalDaColuna.toLocaleString('pt-BR')}</span>`;
      const dotStyle = `display:inline-block; width:7px; height:7px; border-radius:50%; background-color:${corColunaSegura}; flex-shrink:0;`;

      // Colunas fixas (Entrada/Ganho/Perdido) ganham a bolinha só de enfeite
      // — classe diferente de "coluna-cor-dot" de propósito, pra não cair no
      // handler de duplo-clique que abre o seletor de cor (elas não têm
      // .hidden-cor-input, então cairia num erro se usasse a mesma classe).
      const htmlTitulo = coluna.fixa
        ? `<span class="coluna-cor-dot-estatica" style="${dotStyle}"></span>
         <span class="coluna-nome-texto" style="cursor:default;">${nomeColunaSeguro}</span>${contadorHtml}`
        : `${iconeExcluirColuna}
         <span class="coluna-cor-dot" style="${dotStyle}" title="Duplo clique para cor"></span>
         <span class="coluna-nome-texto" title="Duplo clique para alterar">${nomeColunaSeguro}</span>
         <input type="color" class="hidden-cor-input" value="${corColunaSegura}" style="display:none;">
         ${contadorHtml}`;

      // Linha fina (não chapa sólida) com curva pequena nas DUAS pontas —
      // mesmo raio do border-radius real do cartão (10px) — só que mais
      // larga que o cartão (usa a largura cheia da coluna, 290px, contra os
      // 282px do card) e com um respiro embaixo, não 100% encostada: fica
      // parecendo um "encaixe"/tampa por cima do card, não colada nele.
      const linhaDivisoraSvg = `
        <svg class="coluna-linha-divisora" width="290" height="6" viewBox="0 0 290 6" fill="none" aria-hidden="true">
          <path d="M1,4 A3,3 0 0 1 4,1 H286 A3,3 0 0 1 289,4" stroke="${corColunaSegura}" stroke-width="1.5" stroke-linecap="round"/>
        </svg>`;

      // Sem pílula: só a bolinha + o nome da etapa ficam na cor real da
      // coluna (herdada via "color" pelos filhos), tudo centralizado.
      divColuna.innerHTML = `
      <div class="kanban-coluna-header">
        <div class="kanban-coluna-titulo" style="color:${corColunaSegura};">
          ${htmlTitulo}
        </div>
        ${linhaDivisoraSvg}
      </div>
      <div class="kanban-coluna-corpo" data-coluna-id="${coluna.id}"></div>

      ${
        // Ganho/Perdido são sempre as duas últimas colunas — nada pode ir
        // entre elas nem depois de "Perdido", então nem mostra o "+" aí.
        podeEstrutura &&
        coluna.tipo_sistema !== 'ganho' &&
        coluna.tipo_sistema !== 'perdido'
          ? `<div class="wrapper-add-coluna" title="Adicionar coluna aqui">
        <button class="btn-add-entre-colunas"><i class="bx bx-plus"></i></button>
      </div>`
          : ''
      }
    `;

      // Sem onclick inline: id/nome reais (não escapados) vão por closure,
      // evitando o problema de quebra de atributo/string em dois contextos.
      const btnDeletarColuna = divColuna.querySelector('.btn-deletar-coluna');
      if (btnDeletarColuna) {
        btnDeletarColuna.addEventListener('click', (event) =>
          deletarColuna(coluna.id, coluna.nome, event)
        );
      }

      const corpoColuna = divColuna.querySelector('.kanban-coluna-corpo');
      configurarEventosAreaSoltar(corpoColuna);
      const fragmento = document.createDocumentFragment();
      cartoesDestaColuna.forEach((cartao) => fragmento.appendChild(criarElementoCartao(cartao)));
      corpoColuna.appendChild(fragmento);

      // Adiciona o evento de clique no botão que acabamos de criar
      const btnAddInline = divColuna.querySelector('.btn-add-entre-colunas');
      if (btnAddInline) {
        btnAddInline.addEventListener('click', () => {
          criarColunaInline(divColuna); // Passa a própria coluna como referência
        });
      }

      areaColunas.appendChild(divColuna);
    });

    if (window.atualizarEstruturaMinimapa) {
      window.atualizarEstruturaMinimapa(colunas);
    }

    // SCROLL AUTOMÁTICO: Rola a tela para a etapa clicada no Dashboard
    if (urlTargetEtapaId) {
      setTimeout(() => {
        const colunaAlvo = document.querySelector(
          `.kanban-coluna[data-coluna-id="${urlTargetEtapaId}"]`
        );
        if (colunaAlvo) {
          const containerWidth = areaColunas.clientWidth;
          const colunaLeft = colunaAlvo.offsetLeft;
          const colunaWidth = colunaAlvo.clientWidth;

          areaColunas.scrollTo({
            left: colunaLeft - containerWidth / 2 + colunaWidth / 2,
            behavior: 'smooth',
          });

          // Pisca a borda da coluna para destacar onde o usuário parou
          colunaAlvo.style.transition = 'box-shadow 0.4s ease';
          colunaAlvo.style.boxShadow = '0 0 15px var(--azul-principal)';
          setTimeout(() => (colunaAlvo.style.boxShadow = 'none'), 1800);
        }

        // Limpa a variável e a URL para o efeito não repetir caso o usuário interaja na tela
        urlTargetEtapaId = null;
        window.history.replaceState(
          {},
          document.title,
          window.location.pathname
        );
      }, 300);
    }
  }

  // Rolou até perto do fim: busca a próxima página de cada coluna que ainda
  // tem cartão no servidor (mesmos filtros). Cartão que já está na tela (o
  // arrastar mexe nas posições) não é desenhado de novo.
  async function carregarMaisCartoes() {
    if (!canalAtualId) return;
    const token = tokenCargaAtual;
    const canal = canalAtualId;
    const params = parametrosDoQuadro();
    const pendentes = estadoKanban.colunas.filter((c) => {
      const est = estadoKanban.porColuna[c.id];
      return est && !est.fim && !est.carregando;
    });
    await Promise.all(
      pendentes.map(async (coluna) => {
        const est = estadoKanban.porColuna[coluna.id];
        est.carregando = true;
        try {
          const p = new URLSearchParams(params);
          p.set('offset', est.carregados);
          p.set('limite', estadoKanban.tamanhoLote);
          const res = await fetch(
            `/api/kanban/canais/${canal}/colunas/${coluna.id}/cartoes?${p}`,
            {
              headers: { Authorization: `Bearer ${localStorage.getItem('token')}` },
              cache: 'no-store',
            }
          );
          const dados = await res.json();
          if (token !== tokenCargaAtual || !dados.sucesso) return;
          const corpo = areaColunas.querySelector(
            `.kanban-coluna-corpo[data-coluna-id="${coluna.id}"]`
          );
          if (!corpo) return;
          const novos = dados.cartoes.filter(
            (c) => !corpo.querySelector(`.kanban-cartao[data-cartao-id="${c.id}"]`)
          );
          const fragmento = document.createDocumentFragment();
          novos.forEach((c) => fragmento.appendChild(criarElementoCartao(c)));
          corpo.appendChild(fragmento);
          estadoKanban.cartoesOriginais.push(...novos);
          est.carregados += dados.cartoes.length;
          if (dados.cartoes.length < estadoKanban.tamanhoLote) est.fim = true;
        } catch (e) {
          console.error('Erro ao carregar mais cartões:', e);
        } finally {
          est.carregando = false;
        }
      })
    );
  }

  /**
   * SELO DE CANAL DO CARTÃO — por onde este cliente fala com a gente.
   *
   * O cartão é UM por cliente (o telefone é a chave), então o canal não é
   * propriedade do cartão: é das conversas dele, e ele pode ter as duas.
   * Quem está nos dois é o caso mais importante de mostrar — é onde a
   * vendedora escolhe por onde responder, e onde ela corre o risco de
   * responder pelo canal errado.
   *
   * Sem canal nenhum (cartão criado na mão, cliente que nunca falou) o selo
   * não aparece: melhor nada do que um selo que promete um canal que não
   * existe.
   */
  function seloCanaisDoCartao(canais) {
    const lista = Array.isArray(canais) ? canais : [];
    const temRcs = lista.includes('rcs');
    const temWhats = lista.includes('whatsapp');

    // As duas bolinhas vêm do mesmo helper do sistema (window.seloCanalIcone,
    // em global.js) — o desenho do canal é um só em todas as telas.
    const selo = (canal, titulo) =>
      window.seloCanalIcone(canal, { pequeno: true, titulo });

    if (temRcs && temWhats)
      return (
        '<span class="cartao-canais" title="Este cliente fala pelos dois canais">' +
        selo('whatsapp', 'Este cliente fala pelos dois canais') +
        selo('rcs', 'Este cliente fala pelos dois canais') +
        '</span>'
      );
    if (temRcs)
      return (
        '<span class="cartao-canais">' + selo('rcs', 'Conversa por RCS') + '</span>'
      );
    if (temWhats)
      return (
        '<span class="cartao-canais">' +
        selo('whatsapp', 'Conversa por WhatsApp') +
        '</span>'
      );
    return '';
  }

  function criarElementoCartao(cartao) {
    const div = document.createElement('div');
    div.className = 'kanban-cartao';
    div.draggable = !modoSelecaoAtivo;
    div.dataset.cartaoId = cartao.id;
    div.dataset.numeroWpp = cartao.numero_wpp;
    div.dataset.titulo = cartao.titulo || '';

    if (cartoesSelecionados.has(String(cartao.id))) {
      div.classList.add('cartao-selecionado');
    }

    // Pega a config atual do Funil
    const config = estadoKanban.configCartaoAtual || configCartaoPadrao();

    // Função interna inteligente para traduzir o "Slot" no dado real do cliente
    const getValor = (slotName) => {
      if (!slotName || slotName === 'vazio' || slotName === 'etiquetas')
        return '';
      if (slotName === 'nome')
        return (
          semSufixoDeChat(cartao.nome_cliente || cartao.titulo) || 'Sem Nome'
        );
      if (slotName === 'telefone') return cartao.numero_wpp;
      if (slotName === 'cpf') return cartao.cpf || 'Sem CPF';
      // Só vem preenchido em cliente que está no PERDIDO — o backend limpa a
      // coluna quando o lead sai de lá. Em qualquer outro cartão o slot fica
      // vazio e some sozinho, sem deixar buraco no layout.
      if (slotName === 'motivo_perdido')
        return nomeMotivoPerdido(cartao.motivo_perdido);

      if (slotName === 'data_ultima_msg') {
        if (!cartao.ultima_msg_at) return '';
        const d = new Date(cartao.ultima_msg_at);
        return d.toDateString() === new Date().toDateString()
          ? d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })
          : d.toLocaleDateString([], { day: '2-digit', month: '2-digit' });
      }
      if (slotName === 'data_criacao') {
        if (!cartao.data_criacao_cliente) return '';
        const d = new Date(cartao.data_criacao_cliente);
        return d.toLocaleDateString([], { day: '2-digit', month: '2-digit' });
      }
      if (slotName.startsWith('custom_')) {
        const key = slotName.replace('custom_', '');
        let json = {};
        try {
          json =
            typeof cartao.campos_customizados === 'string'
              ? JSON.parse(cartao.campos_customizados)
              : cartao.campos_customizados || {};
        } catch (e) {}
        return json[key] || '';
      }
      return '';
    };

    // Traduz um slot pro HTML final do card. "etiquetas" é o único valor
    // que não é texto simples — vira as pilulas coloridas de verdade, por
    // isso passa por um caminho à parte (o resto sempre é texto escapado).
    const valorDoSlot = (slotName) => {
      if (slotName === 'etiquetas') {
        if (!cartao.tags || cartao.tags.length === 0)
          return { html: '', vazio: true, etiqueta: true };
        const pills = cartao.tags
          .map((t) => {
            const cor = escapeHtml(t.color);
            return `<span class="cartao-tag-pilula" style="background-color: ${cor}20; color: ${cor};">${escapeHtml(t.name)}</span>`;
          })
          .join('');
        return { html: pills, vazio: false, etiqueta: true };
      }
      const texto = escapeHtml(getValor(slotName));
      return { html: texto, vazio: !texto, etiqueta: false };
    };

    // 1. Monta o Avatar de cima (topo-esquerda): foto do responsável (padrão,
    // mosaico dos agentes ou inicial do cliente como respaldo) ou foto do
    // próprio cliente, conforme o slot "slotAvatarTopo" escolhido no
    // "Personalizar Cartões" — mesmas 3 opções do avatar de baixo.
    let htmlAvatar = '';
    const modoAvatarTopo = resolveModoAvatarTopo(config);
    if (modoAvatarTopo !== 'vazio') {
      let conteudoAvatar = escapeHtml(
        (semSufixoDeChat(cartao.nome_cliente || cartao.titulo) || '?')
          .charAt(0)
          .toUpperCase()
      );
      if (modoAvatarTopo === 'foto_cliente') {
        const temFoto =
          cartao.profile_pic_url && cartao.profile_pic_url !== 'sem_foto';
        if (temFoto) {
          conteudoAvatar = `<div class="avatar-mosaico mosaico-1"><img src="${escapeHtml(cartao.profile_pic_url)}" class="avatar-fatia" onerror="this.style.display='none'"></div>`;
        }
      } else if (
        cartao.agentes &&
        cartao.agentes.length > 0 &&
        cartao.agentes[0] !== null
      ) {
        const gridClass =
          cartao.agentes.length === 1 ? 'mosaico-1' : 'mosaico-2';
        let imgsHtml = cartao.agentes
          .slice(0, 2)
          .map((u) =>
            u
              ? `<img src="/fotos-equipe/${u}.webp" class="avatar-fatia" onerror="this.style.display='none'">`
              : ''
          )
          .join('');
        conteudoAvatar = `<div class="avatar-mosaico ${gridClass}">${imgsHtml}</div>`;
      }
      // Cor de fundo determinística (mesmo cliente sempre cai na mesma cor) —
      // dá um respaldo colorido tanto pra inicial quanto pra foto que falhar.
      const corDeFundoAvatar = corAvatarPara(
        cartao.nome_cliente || cartao.numero_wpp || cartao.titulo
      );
      htmlAvatar = `<div class="cartao-avatar-equipe" style="background-color:${corDeFundoAvatar};">${conteudoAvatar}</div>`;
    }

    // 2. Prévia da última mensagem — sempre fixa, não faz parte do sistema
    // de slots (ultima_msg_body vem do WhatsApp e nome_cliente do pushName,
    // ambos 100% controláveis por quem manda a mensagem, por isso sempre
    // passam por escapeHtml antes de virar innerHTML).
    let previaTexto = escapeHtml(cartao.ultima_msg_body || 'Sem mensagens');
    if (cartao.ultima_msg_type === 'image')
      previaTexto = '<i class="fas fa-camera"></i> Imagem';
    else if (cartao.ultima_msg_type === 'video')
      previaTexto = '<i class="fas fa-video"></i> Vídeo';
    else if (
      cartao.ultima_msg_type === 'ptt' ||
      cartao.ultima_msg_type === 'audio'
    )
      previaTexto = '<i class="fas fa-microphone"></i> Áudio';
    else if (cartao.ultima_msg_type === 'document')
      previaTexto = '<i class="fas fa-file-alt"></i> Arquivo';
    else if (cartao.ultima_msg_type === 'comentario')
      previaTexto = `<i class="ph-bold ph-note-pencil"></i> ${escapeHtml(cartao.ultima_msg_body)}`;

    if (cartao.ultima_msg_from_me) {
      let remetente = escapeHtml(cartao.ultimo_remetente_nome || 'Você');
      if (remetente.toUpperCase().startsWith('FLUXO:'))
        remetente = remetente.replace(/FLUXO:\s*/i, '').trim();
      if (cartao.ultima_msg_type === 'comentario')
        previaTexto = `<em style="color: #888;"><strong>${remetente} (Nota):</strong> ${previaTexto}</em>`;
      else previaTexto = `<strong>${remetente}:</strong> ${previaTexto}`;
    }

    // 3. Traduz os 7 slots configuráveis (nome, telefone, data, as 3 linhas
    // do meio e o campo da linha de baixo) — qualquer um deles pode ser um
    // campo fixo, um campo personalizado do CRM, uma data ou as etiquetas.
    const v1 = valorDoSlot(config.slot1);
    const v2 = valorDoSlot(config.slot2);
    const v3 = valorDoSlot(config.slot3);
    const v4 = valorDoSlot(config.slot4);
    const v5 = valorDoSlot(config.slot5);
    const v7 = valorDoSlot(config.slot7);
    const v6 = valorDoSlot(config.slot6);

    // Envolve o valor traduzido na classe visual da posição (nome, data
    // etc). Se for etiqueta, troca pra um wrapper que deixa as pilulas
    // quebrarem linha em vez de tentar caber numa única linha com reticências.
    const renderSlot = (v, classe, comTitulo) => {
      if (v.vazio) return '';
      if (v.etiqueta)
        return `<span class="${classe} cartao-valor-etiquetas">${v.html}</span>`;
      const tituloAttr = comTitulo ? ` title="${v.html}"` : '';
      return `<span class="${classe}"${tituloAttr}>${v.html}</span>`;
    };

    // 4. Rodapé inferior: campo configurável à esquerda (slot6) + foto
    // configurável à direita (slotAvatar — cliente ou responsável).
    const campoExtraClasse = v6.etiqueta
      ? 'cartao-campo-extra cartao-valor-etiquetas'
      : 'cartao-campo-extra';
    const campoExtraConteudo = v6.vazio
      ? ''
      : v6.etiqueta
        ? v6.html
        : `<i class="ph-bold ph-identification-card"></i><span>${v6.html}</span>`;
    const campoExtraHtml = `<div class="${campoExtraClasse}">${campoExtraConteudo}</div>`;

    const fotoInferiorHtml = renderizarFotoInferior(cartao, config.slotAvatar);

    // Renderiza a casca
    div.innerHTML = `
      <div class="cartao-checkbox-selecao" aria-hidden="true">
        <i class="bx bx-check"></i>
      </div>
      <div class="cartao-linha-topo">
        <div class="cartao-bloco-cliente">
            ${htmlAvatar}
            <div class="cartao-identidade" style="flex:1;">
                ${renderSlot(v1, 'cartao-nome', true)}
                ${renderSlot(v2, 'cartao-subtitulo', false)}
            </div>
        </div>
        <div class="cartao-topo-direita">
          ${seloCanaisDoCartao(cartao.canais)}
          ${renderSlot(v3, 'cartao-data-hora', false)}
        </div>
      </div>

      ${v4.vazio ? '' : `<div class="cartao-linha-extra${v4.etiqueta ? ' cartao-valor-etiquetas' : ''}">${v4.html}</div>`}
      ${v5.vazio ? '' : `<div class="cartao-linha-extra${v5.etiqueta ? ' cartao-valor-etiquetas' : ''}">${v5.html}</div>`}
      ${v7.vazio ? '' : `<div class="cartao-linha-extra${v7.etiqueta ? ' cartao-valor-etiquetas' : ''}">${v7.html}</div>`}

      <div class="cartao-linha-fim">
        <div class="cartao-balao-wrapper">
            <i class="ph-bold ph-chat-circle-text cartao-balao-icone"></i>
            <div class="cartao-balao-previa">${previaTexto}</div>
            <button class="btn-ir-chat-card" title="Responder no Bate-papo">
                <i class="ph-bold ph-paper-plane-tilt"></i>
            </button>
        </div>
      </div>

      <div class="cartao-rodape-inferior">
        ${campoExtraHtml}
        ${fotoInferiorHtml}
      </div>
    `;

    const btnIrChat = div.querySelector('.btn-ir-chat-card');
    if (btnIrChat) {
      btnIrChat.addEventListener('click', () => {
        // Em modo de seleção, deixa o clique borbulhar pro card (que
        // seleciona) em vez de navegar pro bate-papo.
        if (modoSelecaoAtivo) return;
        abrirChatKanban(cartao.numero_wpp);
      });
    }

    // Clique no card inteiro alterna a seleção quando o modo está ativo
    div.addEventListener('click', (e) => {
      if (!modoSelecaoAtivo) return;
      e.preventDefault();
      e.stopPropagation();
      alternarSelecaoCartao(String(cartao.id), cartao.numero_wpp, div);
    });

    // Lógica Drag and Drop original intocada
    div.addEventListener('dragstart', () => {
      div.classList.add('arrastando');
      div.dataset.colunaOrigem = div.closest('.kanban-coluna').dataset.colunaId;
      if (!modoEdicaoAtivo) {
        const dropzones = document.getElementById('dropzones-fechamento');
        if (dropzones) dropzones.classList.add('ativo');
      }
    });

    div.addEventListener('dragend', () => {
      div.classList.remove('arrastando');
      const dropzones = document.getElementById('dropzones-fechamento');
      if (dropzones) dropzones.classList.remove('ativo');
    });

    return div;
  }

  function configurarEventosAreaSoltar(corpoColuna) {
    corpoColuna.addEventListener('dragover', (e) => {
      if (modoEdicaoAtivo) return;
      e.preventDefault();
      const cartaoArrastando = document.querySelector('.arrastando');
      if (!cartaoArrastando) return;

      const elementoAposMouse = obterElementoAposMouse(corpoColuna, e.clientY);
      if (elementoAposMouse == null) corpoColuna.appendChild(cartaoArrastando);
      else corpoColuna.insertBefore(cartaoArrastando, elementoAposMouse);
    });

    corpoColuna.addEventListener('drop', async (e) => {
      if (modoEdicaoAtivo) return;
      const cartaoArrastando = document.querySelector('.arrastando');
      if (!cartaoArrastando) return;

      const cartaoId = cartaoArrastando.dataset.cartaoId;
      const novaColunaId = corpoColuna.dataset.colunaId;
      const colunaOrigemId = cartaoArrastando.dataset.colunaOrigem;

      const todosCartoes = [...corpoColuna.querySelectorAll('.kanban-cartao')];
      const novaOrdemIndex = todosCartoes.indexOf(cartaoArrastando) + 1;
      const mudouDeColuna = colunaOrigemId && colunaOrigemId !== novaColunaId;

      if (mudouDeColuna) {
        const colOrigem = document.querySelector(
          `.kanban-coluna[data-coluna-id="${colunaOrigemId}"]`
        );
        const colDestino = document.querySelector(
          `.kanban-coluna[data-coluna-id="${novaColunaId}"]`
        );

        if (colOrigem) {
          let totalOrigem = parseInt(colOrigem.dataset.totalCartoes) - 1;
          colOrigem.dataset.totalCartoes = totalOrigem;
          colOrigem.querySelector('.kanban-coluna-contador').textContent =
            totalOrigem;
        }
        if (colDestino) {
          let totalDestino = parseInt(colDestino.dataset.totalCartoes) + 1;
          colDestino.dataset.totalCartoes = totalDestino;
          colDestino.querySelector('.kanban-coluna-contador').textContent =
            totalDestino;
        }
        cartaoArrastando.dataset.colunaOrigem = novaColunaId;
        // A próxima página de cada coluna é pedida a partir do que já foi
        // carregado: um cartão a menos numa, um a mais na outra.
        const estOrigem = estadoKanban.porColuna[colunaOrigemId];
        const estDestino = estadoKanban.porColuna[novaColunaId];
        if (estOrigem) estOrigem.carregados = Math.max(0, estOrigem.carregados - 1);
        if (estDestino) estDestino.carregados += 1;
      }

      // Segura os refreshs vindos do socket (kanban_atualizado/nova_mensagem)
      // até a movimentação otimista ser confirmada no banco — a classe
      // 'arrastando' já foi removida pelo 'dragend' bem antes do fetch
      // terminar, então sozinha ela não protege contra um refresh concorrente.
      movimentoCartaoEmAndamento = true;

      try {
        // CHEGOU na coluna PERDIDO: exige o motivo antes de mandar pro
        // servidor. Cancelar aqui recarrega o quadro, devolvendo o cartão pra
        // coluna de origem — o DOM já foi mexido pelo dragover, então não dá
        // pra simplesmente "não fazer nada".
        //
        // A condição olha mudouDeColuna: sem isso, só reordenar dois cartões
        // dentro do PERDIDO já abriria o seletor, cobrando um motivo de quem
        // não estava fechando nada. Pra CORRIGIR um motivo errado, o caminho é
        // o bate-papo (clicar de novo em "Perdido" na barra de status).
        let motivoEscolhido = null;
        const colunaDestino = corpoColuna.closest('.kanban-coluna');
        if (mudouDeColuna && colunaDestino?.dataset.tipoSistema === 'perdido') {
          motivoEscolhido = await pedirMotivoPerdido();
          if (!motivoEscolhido) {
            if (canalAtualId) carregarDadosDoCanal(canalAtualId, true);
            return;
          }
        }

        const res = await fetch('/api/kanban/cartoes/mover', {
          method: 'PUT',
          headers: {
            'Content-Type': 'application/json',
            Authorization: `Bearer ${localStorage.getItem('token')}`,
          },
          body: JSON.stringify({
            cartao_id: cartaoId,
            nova_coluna_id: novaColunaId,
            nova_ordem: novaOrdemIndex,
            numero_wpp: cartaoArrastando.dataset.numeroWpp,
            titulo: cartaoArrastando.dataset.titulo,
            motivo_perdido: motivoEscolhido,
          }),
        });

        if (!res.ok) throw new Error('Falha HTTP ao mover o cartão.');
        const dados = await res.json();
        if (!dados.sucesso) throw new Error('Servidor recusou a movimentação.');
      } catch (erro) {
        console.error('Erro ao mover cartão:', erro);
        Swal.fire(
          'Erro',
          'Não foi possível mover o cartão. O quadro será atualizado.',
          'error'
        );
        // Não tenta desfazer manualmente o DOM: recarrega do servidor para
        // garantir que a tela reflita o estado real (contadores inclusos).
        if (canalAtualId) carregarDadosDoCanal(canalAtualId, true);
      } finally {
        movimentoCartaoEmAndamento = false;
      }
    });
  }

  function obterElementoAposMouse(container, y) {
    const cartoesEstaticos = [
      ...container.querySelectorAll('.kanban-cartao:not(.arrastando)'),
    ];
    return cartoesEstaticos.reduce(
      (maisProximo, filho) => {
        const box = filho.getBoundingClientRect();
        const offset = y - box.top - box.height / 2;
        if (offset < 0 && offset > maisProximo.offset)
          return { offset: offset, element: filho };
        else return maisProximo;
      },
      { offset: Number.NEGATIVE_INFINITY }
    ).element;
  }

  // ==========================================================================
  // 6. GESTÃO DOS MODAIS GERAIS (Canal e Cartão)
  // ==========================================================================
  document.querySelectorAll('.fechar-modal-canal').forEach((btn) => {
    btn.addEventListener('click', () => modalCanal.classList.add('hidden'));
  });
  document.querySelectorAll('.fechar-modal-cartao').forEach((btn) => {
    btn.addEventListener('click', () => modalCartao.classList.add('hidden'));
  });

  btnAbrirModalCanal.addEventListener('click', () => {
    inputNomeCanal.value = '';
    modalCanal.classList.remove('hidden');
  });

  btnSalvarCanal.addEventListener('click', async () => {
    const nome = inputNomeCanal.value.trim();
    if (!nome) return Swal.fire('Aviso', 'Digite o nome do canal.', 'warning');

    btnSalvarCanal.disabled = true;
    try {
      const res = await fetch('/api/kanban/canais', {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          Authorization: `Bearer ${localStorage.getItem('token')}`,
        },
        body: JSON.stringify({ nome }),
      });
      const dados = await res.json();
      if (dados.sucesso) {
        modalCanal.classList.add('hidden');
        Swal.fire('Sucesso', 'Canal criado!', 'success');
        await carregarCanais();
        selecionarCanal(dados.canal.id, dados.canal.nome);
      }
    } catch (e) {
      Swal.fire('Erro', 'Falha ao criar canal.', 'error');
    } finally {
      btnSalvarCanal.disabled = false;
    }
  });

  window.abrirModalCartao = function (colunaId) {
    inputColunaAlvoId.value = colunaId;
    inputNumeroCartao.value = '';
    inputTituloCartao.value = '';
    inputValorCartao.value = '';
    modalCartao.classList.remove('hidden');
  };

  btnSalvarCartao.addEventListener('click', async () => {
    // Só os dígitos, como foi digitado: quem decide o 55 e o nono dígito é o
    // servidor (telefone-brasil.js), que acha a ficha que o cliente já tem.
    // O "se não começa com 55, põe 55" daqui errava com DDD 55 (RS).
    const numero = inputNumeroCartao.value.replace(/\D/g, '');
    const titulo = inputTituloCartao.value.trim();
    const valor = parseFloat(inputValorCartao.value) || 0;
    const colunaId = inputColunaAlvoId.value;

    if (!numero || !titulo)
      return Swal.fire('Aviso', 'Número e Título são obrigatórios.', 'warning');

    btnSalvarCartao.disabled = true;
    try {
      const res = await fetch('/api/kanban/cartoes', {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          Authorization: `Bearer ${localStorage.getItem('token')}`,
        },
        body: JSON.stringify({
          coluna_id: colunaId,
          numero_wpp: numero,
          titulo,
          valor,
        }),
      });
      const dados = await res.json();
      if (dados.sucesso) {
        modalCartao.classList.add('hidden');
        carregarDadosDoCanal(canalAtualId);
      } else {
        Swal.fire(
          'Aviso',
          'O número deve estar previamente cadastrado no Bate-papo.',
          'info'
        );
      }
    } catch (e) {
      Swal.fire('Erro', 'Falha ao criar cartão.', 'error');
    } finally {
      btnSalvarCartao.disabled = false;
    }
  });

  // ==========================================================================
  // 7. MODO EDIÇÃO DE COLUNAS
  // ==========================================================================
  btnEngrenagem.addEventListener('click', (e) => {
    e.stopPropagation();
    dropdownEngrenagem.classList.toggle('hidden');
  });
  document.addEventListener('click', (e) => {
    if (menuOpcoesQuadro && !menuOpcoesQuadro.contains(e.target))
      dropdownEngrenagem.classList.add('hidden');
  });

  btnAtivarEdicao.addEventListener('click', () => {
    dropdownEngrenagem.classList.add('hidden');
    if (!canalAtualId)
      return Swal.fire('Aviso', 'Selecione um canal primeiro', 'warning');
    if (modoSelecaoAtivo)
      return Swal.fire(
        'Aviso',
        'Saia do modo de seleção antes de editar as colunas.',
        'warning'
      );

    modoEdicaoAtivo = true;
    areaColunas.classList.add('area-modo-edicao');
    menuOpcoesQuadro.style.display = 'none';
    botoesModoEdicao.classList.remove('hidden');

    document.querySelectorAll('.kanban-coluna').forEach((col) => {
      if (!col.classList.contains('coluna-fixa')) {
        col.draggable = true;
        configurarDragColuna(col);
      }
    });
  });

  btnCancelarEdicao.addEventListener('click', () => {
    modoEdicaoAtivo = false;
    areaColunas.classList.remove('area-modo-edicao');
    menuOpcoesQuadro.style.display = 'block';
    botoesModoEdicao.classList.add('hidden');
    carregarDadosDoCanal(canalAtualId);
  });

  // ==========================================================================
  // 7.5 SELEÇÃO EM MASSA (mover, etiquetar e transferir vários de uma vez)
  // ==========================================================================
  btnAtivarSelecao.addEventListener('click', () => {
    dropdownEngrenagem.classList.add('hidden');

    if (modoSelecaoAtivo) {
      desativarModoSelecao();
      return;
    }
    if (!canalAtualId)
      return Swal.fire('Aviso', 'Selecione um canal primeiro', 'warning');
    if (modoEdicaoAtivo)
      return Swal.fire(
        'Aviso',
        'Termine a edição das colunas antes de selecionar cartões.',
        'warning'
      );

    ativarModoSelecao();
  });

  function ativarModoSelecao() {
    modoSelecaoAtivo = true;
    cartoesSelecionados.clear();
    areaColunas.classList.add('modo-selecao-ativo');
    atualizarBarraSelecao();
    // Re-renderiza pra desligar o arraste e revelar os checkboxes em todo
    // mundo de uma vez (mais simples e confiável do que remendar o DOM)
    carregarDadosDoCanal(canalAtualId, true);
  }

  function desativarModoSelecao() {
    modoSelecaoAtivo = false;
    cartoesSelecionados.clear();
    areaColunas.classList.remove('modo-selecao-ativo');
    atualizarBarraSelecao();
    if (canalAtualId) carregarDadosDoCanal(canalAtualId, true);
  }

  function alternarSelecaoCartao(cartaoId, numeroWpp, divEl) {
    if (cartoesSelecionados.has(cartaoId)) {
      cartoesSelecionados.delete(cartaoId);
      divEl.classList.remove('cartao-selecionado');
    } else {
      cartoesSelecionados.set(cartaoId, numeroWpp);
      divEl.classList.add('cartao-selecionado');
    }
    atualizarBarraSelecao();
  }

  function atualizarBarraSelecao() {
    const total = cartoesSelecionados.size;
    contagemSelecionadosEl.textContent = total;
    barraSelecaoMassa.classList.toggle('hidden', !modoSelecaoAtivo);

    const semSelecao = total === 0;
    [btnMassaMover, btnMassaTag, btnMassaTransferir].forEach((btn) => {
      // O travado nunca vira `disabled` (ver .acao-travada).
      btn.disabled = semSelecao && !btn.classList.contains('acao-travada');
    });
  }

  btnMassaCancelar.addEventListener('click', () => {
    desativarModoSelecao();
  });

  // ESC sai do modo de seleção — mas só se não tiver nenhum SweetAlert
  // (Mover/Etiquetas/Transferir) aberto por cima, senão o ESC fecha só o
  // popup primeiro, do jeito que a gente já espera que ESC se comporte.
  document.addEventListener('keydown', (e) => {
    if (e.key !== 'Escape') return;
    if (!modoSelecaoAtivo) return;
    if (document.querySelector('.swal2-container')) return;
    desativarModoSelecao();
  });

  // Roda uma ação (executor) pra cada id, um de cada vez — sequencial de
  // propósito: o pool de conexões do banco é pequeno (8), então disparar
  // tudo em paralelo com Promise.all poderia sobrecarregar em lotes grandes.
  async function executarAcaoEmMassa(ids, executor, mensagemSucesso) {
    Swal.fire({
      title: 'Aplicando...',
      text: `0 de ${ids.length}`,
      allowOutsideClick: false,
      allowEscapeKey: false,
      showConfirmButton: false,
      didOpen: () => Swal.showLoading(),
    });

    let sucesso = 0;
    let falhas = 0;
    for (let i = 0; i < ids.length; i++) {
      try {
        await executor(ids[i], i);
        sucesso++;
      } catch (e) {
        console.error('Falha na ação em massa:', e);
        falhas++;
      }
      Swal.update({ text: `${i + 1} de ${ids.length}` });
    }

    desativarModoSelecao();

    if (falhas === 0) {
      Swal.fire({
        icon: 'success',
        title: mensagemSucesso,
        text: `${sucesso} cartão(ões) atualizados.`,
        timer: 1800,
        showConfirmButton: false,
      });
    } else {
      Swal.fire({
        icon: 'warning',
        title: 'Concluído com alguns erros',
        text: `${sucesso} deram certo, ${falhas} falharam. Tente de novo com os que faltaram.`,
      });
    }
  }

  // Dropdown com a mesma cara do "Personalizar Cartões" (gatilho + painel
  // flutuante próprio), só que autônomo — o sistema original (SLOTS_CONFIG_
  // CARTAO/dropdownSlotEls) é registrado 1x no carregamento da página pra
  // elementos fixos; esse aqui nasce e some junto com o SweetAlert que o usa.
  function criarDropdownEstilizado({
    opcoes,
    valorInicial,
    placeholder,
    onChange,
  }) {
    const wrapper = document.createElement('div');
    wrapper.className = 'dropdown-slot dropdown-slot-bloco';

    const trigger = document.createElement('button');
    trigger.type = 'button';
    /* pc-skin-caixa: este dropdown nasce em painel de filtro e em popup do
       SweetAlert, onde o sublinhado tracejado dos cartoes fica deslocado.
       A classe so troca a casca (ver skin-dropdowns.css). */
    trigger.className = 'dropdown-slot-trigger estilo-extra pc-skin-caixa';

    const textoEl = document.createElement('span');
    textoEl.className = 'dropdown-slot-trigger-texto';
    const seta = document.createElement('i');
    seta.className = 'bx bx-chevron-down dropdown-slot-seta';
    trigger.appendChild(textoEl);
    trigger.appendChild(seta);

    const opcoesEl = document.createElement('div');
    opcoesEl.className = 'dropdown-slot-opcoes hidden';

    wrapper.appendChild(trigger);
    wrapper.appendChild(opcoesEl);

    let listaOpcoes = opcoes;
    let valorAtual = valorInicial;

    const atualizarTexto = () => {
      const atual = listaOpcoes.find(
        (o) => String(o.value) === String(valorAtual)
      );
      textoEl.textContent = atual ? atual.label : placeholder || 'Selecione';
      textoEl.classList.toggle('slot-vazio', !atual);
    };

    const fechar = () => {
      opcoesEl.classList.add('hidden');
      trigger.classList.remove('aberto');
      if (opcoesEl.parentElement !== wrapper) wrapper.appendChild(opcoesEl);
    };

    const montarOpcoes = () => {
      opcoesEl.innerHTML = '';
      if (listaOpcoes.length === 0) {
        const vazio = document.createElement('div');
        vazio.className = 'swal-massa-vazio';
        vazio.textContent = 'Nada disponível';
        opcoesEl.appendChild(vazio);
        return;
      }
      listaOpcoes.forEach((o) => {
        const item = document.createElement('div');
        item.className = 'dropdown-slot-opcao';
        item.textContent = o.label;
        if (String(o.value) === String(valorAtual)) item.classList.add('ativa');
        item.addEventListener('click', () => {
          valorAtual = o.value;
          atualizarTexto();
          fechar();
          if (onChange) onChange(valorAtual);
        });
        opcoesEl.appendChild(item);
      });
    };

    const posicionar = () => {
      // style.left/top sao lidos como unidades LOCAIS, entao a medida do
      // gatilho e o tamanho da janela precisam vir na mesma regua.
      const rectTrigger = retanguloLocal(trigger);
      const alturaEstimada = Math.min(listaOpcoes.length * 34 + 8, 220);
      const espacoAbaixo = viewportLocal().altura - rectTrigger.bottom;
      const abrirParaCima =
        espacoAbaixo < alturaEstimada + 12 &&
        rectTrigger.top > alturaEstimada + 12;

      opcoesEl.style.left = `${rectTrigger.left}px`;
      if (abrirParaCima) {
        opcoesEl.style.top = '';
        opcoesEl.style.bottom = `${viewportLocal().altura - rectTrigger.top + 4}px`;
      } else {
        opcoesEl.style.bottom = '';
        opcoesEl.style.top = `${rectTrigger.bottom + 4}px`;
      }
    };

    trigger.addEventListener('click', (e) => {
      e.stopPropagation();
      const estavaAberto = !opcoesEl.classList.contains('hidden');
      document
        .querySelectorAll('.dropdown-slot-opcoes:not(.hidden)')
        .forEach((el) => el.classList.add('hidden'));
      document
        .querySelectorAll('.dropdown-slot-trigger.aberto')
        .forEach((el) => el.classList.remove('aberto'));
      if (estavaAberto) return;
      montarOpcoes();
      // Escapa pro <body> — o mesmo truque do editor de "Personalizar
      // Cartões": dentro do SweetAlert, um position:fixed aninhado ainda
      // pode ficar atrás do rodapé de botões (stacking context do popup).
      // No <body> ele fica garantidamente por cima de tudo.
      document.body.appendChild(opcoesEl);
      posicionar();
      opcoesEl.classList.remove('hidden');
      trigger.classList.add('aberto');
    });

    montarOpcoes();
    atualizarTexto();

    return {
      elemento: wrapper,
      getValor: () => valorAtual,
      setOpcoes(novasOpcoes, novoValor) {
        listaOpcoes = novasOpcoes;
        valorAtual = novoValor;
        montarOpcoes();
        atualizarTexto();
      },
      // Chamado quando o SweetAlert que hospeda esse dropdown fecha — o
      // wrapper some junto com o popup, mas o painel teleportado pro
      // <body> não, então precisa ser removido manualmente.
      destruir() {
        if (opcoesEl.parentElement) opcoesEl.parentElement.removeChild(opcoesEl);
      },
    };
  }

  /**
   * DROPDOWN DE MULTI-SELEÇÃO NO PADRÃO DO SISTEMA (filtros de Atendente e
   * Motivo de perda). A referência é o "Selecione os Bots" do Novo Disparo
   * (envio.js): gatilho .botao-select-v3 com "Selecionar X (0)" / "N X(s)
   * Selecionado(s)", painel .dropdown-bots-flutuante com "Selecionar Todos"
   * em negrito no topo, e cada linha um <label> com o checkbox nativo
   * escondido + <i class="chk-row-check">. Com a MESMA marcação, o
   * skin-dropdowns.css e o skin-checklists.css pintam tudo igual à
   * referência; o pag-kanban.css só repete a base de layout que no Novo
   * Disparo vem do pag-envio-campanha.css (que esta página não carrega).
   *
   * O painel vai pro <body> ao abrir (o painel de filtros corta o que passa
   * da borda) e para antes do `limiteInferior` (o rodapé com "Aplicar").
   */
  function criarDropdownMultiEstilizado({
    opcoes,
    nomeSingular,
    nomePlural,
    onChange,
    limiteInferior,
  }) {
    const wrapper = document.createElement('div');
    wrapper.className = 'dd-multi-kanban';

    const trigger = document.createElement('button');
    trigger.type = 'button';
    trigger.className = 'botao-select-v3 dd-multi-trigger';
    const textoEl = document.createElement('span');
    textoEl.className = 'dd-multi-texto';
    const seta = document.createElement('i');
    seta.className = 'fas fa-chevron-down dd-multi-seta';
    trigger.appendChild(textoEl);
    trigger.appendChild(seta);

    const painel = document.createElement('div');
    painel.className = 'dropdown-bots-flutuante dd-multi-painel escondido';
    wrapper.appendChild(trigger);
    wrapper.appendChild(painel);

    const marcados = new Set();

    const atualizarTexto = () => {
      const n = marcados.size;
      textoEl.textContent =
        n === 0
          ? `Selecionar ${nomePlural} (0)`
          : `${n} ${nomeSingular}(s) Selecionado(s)`;
    };

    const avisar = () => {
      atualizarTexto();
      if (onChange) onChange(Array.from(marcados));
    };

    const criarLinha = (texto, marcado, aoMudar, negrito) => {
      const label = document.createElement('label');
      if (negrito) label.style.fontWeight = 'bold';
      const input = document.createElement('input');
      input.type = 'checkbox';
      input.checked = marcado;
      input.addEventListener('change', () => aoMudar(input.checked));
      const rotulo = document.createElement('span');
      rotulo.className = 'dd-multi-rotulo';
      rotulo.textContent = texto;
      const check = document.createElement('i');
      check.className = 'ph-bold ph-check chk-row-check';
      label.appendChild(input);
      label.appendChild(rotulo);
      label.appendChild(check);
      return { label, input };
    };

    let checkTodos = null;
    const sincronizarTodos = () => {
      if (checkTodos) checkTodos.checked = opcoes.length > 0 && marcados.size === opcoes.length;
    };

    const montarOpcoes = () => {
      painel.innerHTML = '';
      if (opcoes.length === 0) {
        painel.innerHTML = '<span class="texto-placeholder">Nada disponível</span>';
        return;
      }
      const inputs = [];
      const todos = criarLinha('Selecionar Todos', false, (marcar) => {
        opcoes.forEach((o) => (marcar ? marcados.add(o.value) : marcados.delete(o.value)));
        inputs.forEach((i) => (i.checked = marcar));
        avisar();
      }, true);
      checkTodos = todos.input;
      painel.appendChild(todos.label);

      opcoes.forEach((o) => {
        const linha = criarLinha(o.label, marcados.has(o.value), (marcar) => {
          if (marcar) marcados.add(o.value);
          else marcados.delete(o.value);
          sincronizarTodos();
          avisar();
        });
        inputs.push(linha.input);
        painel.appendChild(linha.label);
      });
      sincronizarTodos();
    };

    const fechar = () => {
      painel.classList.add('escondido');
      trigger.classList.remove('aberto');
      if (painel.parentElement !== wrapper) wrapper.appendChild(painel);
    };

    const posicionar = () => {
      const r = retanguloLocal(trigger);
      const limiteEl = typeof limiteInferior === 'function' ? limiteInferior() : null;
      const chao = limiteEl ? retanguloLocal(limiteEl).top - 8 : viewportLocal().altura - 12;
      painel.style.left = `${r.left}px`;
      painel.style.top = `${r.bottom + 5}px`;
      painel.style.width = `${r.width}px`;
      painel.style.maxHeight = `${Math.max(120, chao - r.bottom - 5)}px`;
    };

    trigger.addEventListener('click', (e) => {
      e.stopPropagation();
      const estavaAberto = !painel.classList.contains('escondido');
      document
        .querySelectorAll('.dd-multi-painel:not(.escondido)')
        .forEach((p) => p.classList.add('escondido'));
      if (estavaAberto) return fechar();
      montarOpcoes();
      document.body.appendChild(painel);
      posicionar();
      painel.classList.remove('escondido');
      trigger.classList.add('aberto');
    });

    // Cliques dentro do painel param aqui: senão chegariam no document e
    // fechariam a lista E o painel de filtros (a lista mora no <body>, fora
    // dele). Qualquer outro clique na página fecha a lista.
    painel.addEventListener('click', (e) => e.stopPropagation());
    document.addEventListener('click', () => {
      if (!painel.classList.contains('escondido')) fechar();
    });
    // Outro dropdown abriu e escondeu este por fora: devolve o painel.
    new MutationObserver(() => {
      if (painel.classList.contains('escondido') && painel.parentElement !== wrapper) fechar();
    }).observe(painel, { attributes: true, attributeFilter: ['class'] });

    montarOpcoes();
    atualizarTexto();

    return {
      elemento: wrapper,
      getValores: () => Array.from(marcados),
      limpar() {
        marcados.clear();
        montarOpcoes();
        atualizarTexto();
      },
    };
  }

  // --- MOVER EM MASSA (funil + etapa) ---
  btnMassaMover.addEventListener('click', async () => {
    if (btnMassaMover.disabled) return;
    const ids = Array.from(cartoesSelecionados.keys());
    if (ids.length === 0) return;

    let estrutura;
    try {
      const res = await fetch('/api/kanban/estrutura', {
        headers: { Authorization: `Bearer ${localStorage.getItem('token')}` },
      });
      estrutura = await res.json();
      if (!estrutura.success) throw new Error();
    } catch (e) {
      return Swal.fire(
        'Erro',
        'Não foi possível carregar os funis e etapas.',
        'error'
      );
    }

    if (!estrutura.canais || estrutura.canais.length === 0) {
      return Swal.fire('Aviso', 'Nenhum funil disponível.', 'warning');
    }

    const etapasDoFunil = (funilId) =>
      estrutura.colunas
        .filter((col) => String(col.canal_id) === String(funilId))
        .map((et) => ({ value: et.id, label: et.nome }));

    let ddFunil = null;
    let ddEtapa = null;
    const fecharAoClicarFora = (e) => {
      if (e.target.closest('.dropdown-slot')) return;
      document
        .querySelectorAll('.dropdown-slot-opcoes:not(.hidden)')
        .forEach((el) => el.classList.add('hidden'));
      document
        .querySelectorAll('.dropdown-slot-trigger.aberto')
        .forEach((el) => el.classList.remove('aberto'));
    };

    const { value: escolha } = await Swal.fire({
      title: `Mover ${ids.length} cartão(ões)`,
      html: `
        <div style="text-align:left; display:flex; flex-direction:column; gap:14px; margin-top:6px;">
          <div>
            <label class="swal-massa-label">Funil</label>
            <div id="swal-massa-funil-slot"></div>
          </div>
          <div>
            <label class="swal-massa-label">Etapa</label>
            <div id="swal-massa-etapa-slot"></div>
          </div>
        </div>
      `,
      customClass: { popup: 'swal-massa-popup' },
      didOpen: () => {
        const etapasIniciais = etapasDoFunil(canalAtualId);
        ddEtapa = criarDropdownEstilizado({
          opcoes: etapasIniciais,
          valorInicial: etapasIniciais.length ? etapasIniciais[0].value : null,
          placeholder: 'Selecione a etapa',
        });
        document
          .getElementById('swal-massa-etapa-slot')
          .appendChild(ddEtapa.elemento);

        ddFunil = criarDropdownEstilizado({
          opcoes: estrutura.canais.map((c) => ({
            value: c.id,
            label: c.nome,
          })),
          valorInicial: canalAtualId,
          placeholder: 'Selecione o funil',
          onChange: (novoFunilId) => {
            const etapas = etapasDoFunil(novoFunilId);
            ddEtapa.setOpcoes(etapas, etapas.length ? etapas[0].value : null);
          },
        });
        document
          .getElementById('swal-massa-funil-slot')
          .appendChild(ddFunil.elemento);

        document.addEventListener('click', fecharAoClicarFora);
      },
      didClose: () => {
        document.removeEventListener('click', fecharAoClicarFora);
        if (ddFunil) ddFunil.destruir();
        if (ddEtapa) ddEtapa.destruir();
      },
      showCancelButton: true,
      confirmButtonText: 'Mover',
      cancelButtonText: 'Cancelar',
      confirmButtonColor: 'var(--azul-principal, #115599)',
      preConfirm: () => {
        const etapaId = ddEtapa ? ddEtapa.getValor() : null;
        if (!etapaId) {
          Swal.showValidationMessage('Escolha uma etapa de destino.');
          return false;
        }
        return { etapaId };
      },
    });

    if (!escolha) return;

    // Mover em massa pro PERDIDO: pergunta o motivo UMA vez e aplica nos
    // selecionados. Perguntar por cartão faria a pessoa clicar dezenas de
    // vezes e acabaria empurrando todo mundo pro primeiro item da lista.
    const etapaAlvo = (estrutura.colunas || []).find(
      (col) => String(col.id) === String(escolha.etapaId)
    );
    let motivoEmMassa = null;
    if (etapaAlvo && etapaAlvo.tipo_sistema === 'perdido') {
      motivoEmMassa = await pedirMotivoPerdido({ quantidade: ids.length });
      if (!motivoEmMassa) return;
    }

    await executarAcaoEmMassa(
      ids,
      async (cartaoId, indice) => {
        const res = await fetch('/api/kanban/cartoes/mover', {
          method: 'PUT',
          headers: {
            'Content-Type': 'application/json',
            Authorization: `Bearer ${localStorage.getItem('token')}`,
          },
          body: JSON.stringify({
            cartao_id: cartaoId,
            nova_coluna_id: escolha.etapaId,
            nova_ordem: 900 + indice,
            motivo_perdido: motivoEmMassa,
          }),
        });
        if (!res.ok) throw new Error('Falha HTTP ao mover.');
        const dados = await res.json();
        if (!dados.sucesso) throw new Error(dados.mensagem || 'Falha ao mover.');
      },
      'Cartões movidos!'
    );
  });

  // --- ADICIONAR/REMOVER ETIQUETA EM MASSA ---
  btnMassaTag.addEventListener('click', async () => {
    if (btnMassaTag.disabled) return;
    const ids = Array.from(cartoesSelecionados.keys());
    if (ids.length === 0) return;

    let todasTags = [];
    try {
      const res = await fetch('/api/tags?tipo=chat');
      todasTags = await res.json();
    } catch (e) {}

    if (!todasTags || todasTags.length === 0) {
      return Swal.fire(
        'Aviso',
        'Nenhuma etiqueta cadastrada ainda. Crie uma etiqueta primeiro.',
        'warning'
      );
    }

    // Só é usada no modo "Remover" — não faz sentido oferecer tirar uma
    // etiqueta que nenhum dos cartões selecionados tem.
    const idsSet = new Set(ids);
    const mapaTagsPresentes = new Map();
    (estadoKanban.cartoesOriginais || []).forEach((c) => {
      if (!idsSet.has(String(c.id))) return;
      (c.tags || []).forEach((t) => mapaTagsPresentes.set(String(t.id), t));
    });
    const tagsPresentesNosSelecionados = Array.from(mapaTagsPresentes.values());

    let acaoEscolhida = 'add';
    let tagsEscolhidasIds = new Set();

    const { value: escolha } = await Swal.fire({
      title: `Etiquetas em ${ids.length} cartão(ões)`,
      html: `
        <div style="text-align:left; display:flex; flex-direction:column; gap:14px; margin-top:6px;">
          <div class="swal-toggle-linha">
            <button type="button" id="swal-tag-add" class="swal-toggle-acao ativo">Adicionar</button>
            <button type="button" id="swal-tag-rem" class="swal-toggle-acao">Remover</button>
          </div>
          <div id="swal-lista-tags" class="swal-massa-lista-tags"></div>
          <p style="margin:0; font-size:11.5px; color:var(--texto-suave);">
            Pode escolher mais de uma etiqueta.
          </p>
        </div>
      `,
      customClass: { popup: 'swal-massa-popup' },
      didOpen: () => {
        const btnAdd = document.getElementById('swal-tag-add');
        const btnRem = document.getElementById('swal-tag-rem');
        const lista = document.getElementById('swal-lista-tags');

        const renderizarLista = () => {
          tagsEscolhidasIds = new Set();
          lista.innerHTML = '';
          const origem =
            acaoEscolhida === 'add' ? todasTags : tagsPresentesNosSelecionados;

          if (origem.length === 0) {
            const vazio = document.createElement('div');
            vazio.className = 'swal-massa-vazio';
            vazio.textContent =
              'Nenhum desses cartões tem etiqueta pra remover.';
            lista.appendChild(vazio);
            return;
          }

          origem.forEach((tag) => {
            const pill = document.createElement('button');
            pill.type = 'button';
            pill.className = 'swal-massa-tag-pill cartao-tag-pilula';
            pill.textContent = tag.name;
            pill.style.backgroundColor = tag.color + '20';
            pill.style.color = tag.color;
            pill.addEventListener('click', () => {
              if (tagsEscolhidasIds.has(tag.id)) {
                tagsEscolhidasIds.delete(tag.id);
                pill.classList.remove('selecionada');
              } else {
                tagsEscolhidasIds.add(tag.id);
                pill.classList.add('selecionada');
              }
            });
            lista.appendChild(pill);
          });
        };

        btnAdd.addEventListener('click', () => {
          acaoEscolhida = 'add';
          btnAdd.classList.add('ativo');
          btnRem.classList.remove('ativo');
          renderizarLista();
        });
        btnRem.addEventListener('click', () => {
          acaoEscolhida = 'remove';
          btnRem.classList.add('ativo');
          btnAdd.classList.remove('ativo');
          renderizarLista();
        });

        renderizarLista();
      },
      showCancelButton: true,
      confirmButtonText: 'Aplicar',
      cancelButtonText: 'Cancelar',
      confirmButtonColor: 'var(--azul-principal, #115599)',
      preConfirm: () => {
        if (tagsEscolhidasIds.size === 0) {
          Swal.showValidationMessage('Escolha ao menos uma etiqueta.');
          return false;
        }
        return { tagIds: Array.from(tagsEscolhidasIds), acao: acaoEscolhida };
      },
    });

    if (!escolha) return;

    await executarAcaoEmMassa(
      ids,
      async (cartaoId) => {
        const numeroWpp = cartoesSelecionados.get(cartaoId);
        // Uma etiqueta de cada vez, sequencial, pro mesmo cartão — a rota
        // só aceita uma por chamada.
        for (const tagId of escolha.tagIds) {
          const res = await fetch('/api/chat/tags/toggle', {
            method: 'POST',
            headers: {
              'Content-Type': 'application/json',
              Authorization: `Bearer ${localStorage.getItem('token')}`,
            },
            body: JSON.stringify({
              chatId: numeroWpp,
              tagId,
              action: escolha.acao,
            }),
          });
          if (!res.ok) throw new Error('Falha HTTP ao aplicar etiqueta.');
          const dados = await res.json();
          if (dados.success === false)
            throw new Error('Falha ao aplicar etiqueta.');
        }
      },
      escolha.acao === 'add' ? 'Etiquetas adicionadas!' : 'Etiquetas removidas!'
    );
  });

  // --- TRANSFERIR RESPONSÁVEL EM MASSA ---
  btnMassaTransferir.addEventListener('click', async () => {
    if (
      btnMassaTransferir.disabled ||
      btnMassaTransferir.classList.contains('acao-travada')
    )
      return;
    const ids = Array.from(cartoesSelecionados.keys());
    if (ids.length === 0) return;

    let vendedores = [];
    try {
      const res = await fetch('/api/team/vendedores', {
        headers: { Authorization: `Bearer ${localStorage.getItem('token')}` },
      });
      vendedores = await res.json();
    } catch (e) {}

    let ddVendedor = null;
    const fecharAoClicarFora = (e) => {
      if (e.target.closest('.dropdown-slot')) return;
      document
        .querySelectorAll('.dropdown-slot-opcoes:not(.hidden)')
        .forEach((el) => el.classList.add('hidden'));
      document
        .querySelectorAll('.dropdown-slot-trigger.aberto')
        .forEach((el) => el.classList.remove('aberto'));
    };

    const { value: respostaSwal } = await Swal.fire({
      title: `Transferir ${ids.length} cartão(ões)`,
      html: `
        <div style="text-align:left; margin-top:6px;">
          <label class="swal-massa-label">Novo responsável</label>
          <div id="swal-massa-vendedor-slot"></div>
        </div>
      `,
      customClass: { popup: 'swal-massa-popup' },
      didOpen: () => {
        const opcoes = [
          { value: '', label: 'Fila Geral (sem responsável)' },
        ].concat(
          (vendedores || []).map((v) => ({
            value: v.id,
            label: v.name || v.username,
          }))
        );
        ddVendedor = criarDropdownEstilizado({
          opcoes,
          valorInicial: '',
          placeholder: 'Selecione o responsável',
        });
        document
          .getElementById('swal-massa-vendedor-slot')
          .appendChild(ddVendedor.elemento);
        document.addEventListener('click', fecharAoClicarFora);
      },
      didClose: () => {
        document.removeEventListener('click', fecharAoClicarFora);
        if (ddVendedor) ddVendedor.destruir();
      },
      showCancelButton: true,
      confirmButtonText: 'Transferir',
      cancelButtonText: 'Cancelar',
      confirmButtonColor: 'var(--azul-principal, #115599)',
      preConfirm: () => (ddVendedor ? ddVendedor.getValor() : ''),
    });

    if (respostaSwal === undefined) return; // cancelou
    const responsavelId = respostaSwal;

    await executarAcaoEmMassa(
      ids,
      async (cartaoId) => {
        const numeroWpp = cartoesSelecionados.get(cartaoId);
        const res = await fetch('/api/chat/transfer', {
          method: 'POST',
          headers: {
            'Content-Type': 'application/json',
            Authorization: `Bearer ${localStorage.getItem('token')}`,
          },
          body: JSON.stringify({
            chatId: numeroWpp,
            botId: null,
            responsavelId: responsavelId || null,
          }),
        });
        if (res.status === 403) {
          throw new Error('Sem permissão para transferir este cliente.');
        }
        if (!res.ok) throw new Error('Falha HTTP ao transferir.');
        const dados = await res.json();
        if (dados.success === false)
          throw new Error(dados.message || 'Falha ao transferir.');
      },
      'Cartões transferidos!'
    );
  });

  let contadorNovaColuna = 1;

  // Nova função que recebe a coluna de referência para saber ONDE injetar a nova
  function criarColunaInline(colunaReferencia) {
    const msgVaziaRemover = document.getElementById('mensagem-vazia-kanban');
    if (msgVaziaRemover) msgVaziaRemover.remove();

    const divColuna = document.createElement('div');
    divColuna.className = 'kanban-coluna nova-coluna-edicao';
    divColuna.dataset.colunaId = 'nova_' + contadorNovaColuna++;
    divColuna.draggable = true;

    divColuna.innerHTML = `
        <div class="kanban-coluna-header" style="border-color: #ffffff">
          <div class="kanban-coluna-titulo" style="width: 100%; display: flex; align-items: center; gap: 8px;">
            <i class="bx bx-trash btn-deletar-coluna" onclick="this.closest('.kanban-coluna').remove()" title="Cancelar Coluna"></i>
            <span class="coluna-cor-dot" style="display:inline-block; width:12px; height:12px; border-radius:50%; background-color:#ffffff; cursor:pointer;" title="Duplo clique para cor"></span>
            <span class="coluna-nome-texto" style="cursor:text;" title="Duplo clique para alterar">Sem Nome</span>
            <input type="color" class="hidden-cor-input" value="#ffffff" style="display:none;">
          </div>
        </div>
        <div class="kanban-coluna-corpo"></div>
        
        <div class="wrapper-add-coluna" title="Adicionar coluna aqui">
          <button class="btn-add-entre-colunas"><i class="bx bx-plus"></i></button>
        </div>
      `;

    configurarDragColuna(divColuna);

    // Amarra o evento no botão dessa coluna nova para permitir criações em cadeia
    const btnAddNovo = divColuna.querySelector('.btn-add-entre-colunas');
    btnAddNovo.addEventListener('click', () => criarColunaInline(divColuna));

    // A mágica acontece aqui: Insere a div EXATAMENTE após a coluna onde você clicou
    colunaReferencia.insertAdjacentElement('afterend', divColuna);
  }

  areaColunas.addEventListener('dblclick', (e) => {
    if (!modoEdicaoAtivo) return;
    if (e.target.classList.contains('coluna-cor-dot')) {
      const containerTitulo = e.target.closest('.kanban-coluna-titulo');
      const inputCor = containerTitulo.querySelector('.hidden-cor-input');
      const header = e.target.closest('.kanban-coluna-header');
      inputCor.click();
      inputCor.onchange = () => {
        e.target.style.backgroundColor = inputCor.value;
        header.style.borderColor = inputCor.value;
      };
    }
    if (e.target.classList.contains('coluna-nome-texto')) {
      const spanTexto = e.target;
      const textoAntigo = spanTexto.textContent;
      const input = document.createElement('input');
      input.type = 'text';
      input.value = textoAntigo === 'Sem Nome' ? '' : textoAntigo;
      input.className = 'input-titulo-inline';
      spanTexto.replaceWith(input);
      input.focus();
      const salvarEdicaoTexto = () => {
        const novoTexto = input.value.trim() || 'Sem Nome';
        spanTexto.textContent = novoTexto;
        input.replaceWith(spanTexto);
      };
      input.addEventListener('blur', salvarEdicaoTexto);
      input.addEventListener('keypress', (ev) => {
        if (ev.key === 'Enter') input.blur();
      });
    }
  });

  function configurarDragColuna(coluna) {
    coluna.addEventListener('dragstart', (e) => {
      if (!modoEdicaoAtivo) return;
      e.stopPropagation();
      coluna.classList.add('arrastando-coluna');
      ultimoAlvoDropColuna = undefined;
      iniciarAutoScrollColunas();
    });
    coluna.addEventListener('dragend', (e) => {
      if (!modoEdicaoAtivo) return;
      e.stopPropagation();
      coluna.classList.remove('arrastando-coluna');
      pararAutoScrollColunas();
    });
  }

  // Lembra o último alvo já processado pra só remexer no DOM (e disparar a
  // animação abaixo) quando a posição realmente muda — sem isso o dragover
  // dispara a cada pixel e recalcularia/animaria sem parar.
  let ultimoAlvoDropColuna;
  let dragMouseX = null;

  // Reavalia se a coluna arrastada precisa trocar de lugar, usando a última
  // posição de mouse conhecida — chamada tanto pelo dragover (quando o mouse
  // se move) quanto pelo laço do auto-scroll (quando o mouse fica parado
  // perto da borda e novas colunas vão aparecendo por baixo dele).
  function avaliarReordenacaoDrag() {
    const colunaArrastando = document.querySelector('.arrastando-coluna');
    if (!colunaArrastando || dragMouseX == null) return;

    const alvo = limitarAlvoDeDropColuna(
      obterColunaAposMouse(areaColunas, dragMouseX)
    );
    if (alvo === colunaArrastando || alvo === ultimoAlvoDropColuna) return;
    ultimoAlvoDropColuna = alvo;

    animarReordenacaoColunas(colunaArrastando, () => {
      if (alvo == null) areaColunas.appendChild(colunaArrastando);
      else areaColunas.insertBefore(colunaArrastando, alvo);
    });
  }

  areaColunas.addEventListener('dragover', (e) => {
    if (!modoEdicaoAtivo) return;
    e.preventDefault();
    dragMouseX = e.clientX;
    avaliarReordenacaoDrag();
  });

  // Auto-scroll nas bordas: com o quadro tendo várias colunas (ex.: mandar a
  // 4ª pra posição 28), o mouse não alcança colunas fora da tela — então,
  // enquanto o cursor ficar perto da borda esquerda/direita da área durante
  // o arraste, o quadro rola sozinho naquela direção (mais rápido quanto
  // mais perto da borda), até o cursor se afastar dela ou o arraste acabar.
  const AUTOSCROLL_ZONA_PX = 90;
  const AUTOSCROLL_VELOCIDADE_MAX = 18;
  let autoScrollRAF = null;

  function iniciarAutoScrollColunas() {
    const passo = () => {
      if (dragMouseX != null) {
        const rect = areaColunas.getBoundingClientRect();
        const distEsquerda = dragMouseX - rect.left;
        const distDireita = rect.right - dragMouseX;

        if (distEsquerda >= 0 && distEsquerda < AUTOSCROLL_ZONA_PX) {
          const forca = 1 - distEsquerda / AUTOSCROLL_ZONA_PX;
          areaColunas.scrollLeft -= forca * AUTOSCROLL_VELOCIDADE_MAX;
          avaliarReordenacaoDrag();
        } else if (distDireita >= 0 && distDireita < AUTOSCROLL_ZONA_PX) {
          const forca = 1 - distDireita / AUTOSCROLL_ZONA_PX;
          areaColunas.scrollLeft += forca * AUTOSCROLL_VELOCIDADE_MAX;
          avaliarReordenacaoDrag();
        }
      }
      autoScrollRAF = requestAnimationFrame(passo);
    };
    autoScrollRAF = requestAnimationFrame(passo);
  }

  function pararAutoScrollColunas() {
    if (autoScrollRAF) cancelAnimationFrame(autoScrollRAF);
    autoScrollRAF = null;
    dragMouseX = null;
  }

  // Efeito "as outras colunas escorregam pro lado" enquanto você arrasta —
  // técnica FLIP: mede a posição de cada coluna ANTES da troca, aplica a
  // mudança de DOM de verdade, e então anima de volta a diferença (a coluna
  // que está sendo arrastada fica de fora, ela já segue o cursor sozinha).
  function animarReordenacaoColunas(colunaArrastando, mutacaoDom) {
    const colunas = [...areaColunas.querySelectorAll('.kanban-coluna')].filter(
      (c) => c !== colunaArrastando
    );
    // O delta vira translateX(), que e lido em unidades LOCAIS — medir em
    // visuais deixaria a animacao curta demais em telas com escala.
    const posicoesAntes = new Map(
      colunas.map((c) => [c, retanguloLocal(c).left])
    );

    mutacaoDom();

    colunas.forEach((c) => {
      const antes = posicoesAntes.get(c);
      const depois = retanguloLocal(c).left;
      const delta = antes - depois;
      if (!delta) return;

      c.style.transition = 'none';
      c.style.transform = `translateX(${delta}px)`;
      requestAnimationFrame(() => {
        requestAnimationFrame(() => {
          c.style.transition = 'transform 0.22s ease';
          c.style.transform = '';
          c.addEventListener(
            'transitionend',
            () => {
              c.style.transition = '';
            },
            { once: true }
          );
        });
      });
    });
  }

  // "Etapa de Entrada" é sempre a primeira coluna e "Ganho"/"Perdido" são
  // sempre as duas últimas — o backend já ignora a posição dessas 3 na hora
  // de salvar (só as colunas normais são reordenadas em
  // /colunas/sincronizar), então isso aqui é só pra não deixar a tela
  // *parecer* que uma coluna normal foi solta antes da Entrada ou
  // depois/entre Ganho e Perdido, o que confundiria quem estiver editando.
  function limitarAlvoDeDropColuna(alvo) {
    const colunaGanho = areaColunas.querySelector(
      '.kanban-coluna[data-tipo-sistema="ganho"]'
    );
    if (alvo == null) return colunaGanho || null;
    if (alvo.dataset.tipoSistema === 'entrada') return alvo.nextElementSibling;
    if (
      alvo.dataset.tipoSistema === 'ganho' ||
      alvo.dataset.tipoSistema === 'perdido'
    )
      return colunaGanho;
    return alvo;
  }

  function obterColunaAposMouse(container, x) {
    const colunasEstaticas = [
      ...container.querySelectorAll('.kanban-coluna:not(.arrastando-coluna)'),
    ];
    return colunasEstaticas.reduce(
      (maisProximo, filho) => {
        const box = filho.getBoundingClientRect();
        const offset = x - box.left - box.width / 2;
        if (offset < 0 && offset > maisProximo.offset)
          return { offset: offset, element: filho };
        else return maisProximo;
      },
      { offset: Number.NEGATIVE_INFINITY }
    ).element;
  }

  btnSalvarEdicao.addEventListener('click', async () => {
    const colunasDOM = document.querySelectorAll('.kanban-coluna');
    const dadosColunas = [];
    let temErro = false;

    colunasDOM.forEach((col) => {
      if (col.classList.contains('coluna-fixa')) return;
      const id = col.dataset.colunaId;

      const spanNome = col.querySelector('.coluna-nome-texto');
      const inputNome = col.querySelector('.input-titulo-inline');
      const nome = spanNome
        ? spanNome.textContent.trim()
        : inputNome
          ? inputNome.value.trim()
          : 'Sem Nome';

      const cor = col.querySelector('.hidden-cor-input').value;

      if (!nome || nome === 'Sem Nome') temErro = true;
      dadosColunas.push({ id, nome, cor });
    });

    if (temErro)
      return Swal.fire(
        'Atenção',
        'Dê um nome para a sua nova coluna antes de salvar.',
        'warning'
      );

    btnSalvarEdicao.innerHTML = '<i class="bx bx-loader-alt bx-spin"></i>';
    btnSalvarEdicao.disabled = true;

    try {
      const res = await fetch(
        `/api/kanban/canais/${canalAtualId}/colunas/sincronizar`,
        {
          method: 'PUT',
          headers: {
            'Content-Type': 'application/json',
            Authorization: `Bearer ${localStorage.getItem('token')}`,
          },
          body: JSON.stringify({ colunas: dadosColunas }),
        }
      );

      const data = await res.json();
      if (data.sucesso) {
        Swal.fire({
          title: 'Sucesso!',
          text: 'Quadro atualizado.',
          icon: 'success',
          timer: 1500,
          showConfirmButton: false,
        });
        btnCancelarEdicao.click();
      } else Swal.fire('Erro', 'Falha ao salvar as colunas.', 'error');
    } catch (e) {
      Swal.fire('Erro', 'Falha de conexão.', 'error');
    } finally {
      btnSalvarEdicao.innerHTML =
        '<i class="bx bx-check" style="font-size: 24px;"></i>';
      btnSalvarEdicao.disabled = false;
    }
  });

  // ==========================================================================
  // 8. REAL-TIME (SOCKET.IO)
  // ==========================================================================
  const socket = io({ auth: { token: localStorage.getItem('token') } });

  // Vários eventos 'kanban_atualizado' podem chegar em rajada (ex: campanha
  // em massa movendo dezenas de leads em sequência) — sem isso, cada um
  // dispara uma reconstrução completa do quadro. Agrupa numa única recarga.
  let debounceKanbanAtualizado = null;
  socket.on('kanban_atualizado', () => agendarRefreshKanban());
  function agendarRefreshKanban() {
    clearTimeout(debounceKanbanAtualizado);
    debounceKanbanAtualizado = setTimeout(() => {
      const estaArrastando = document.querySelector('.arrastando');
      if (
        canalAtualId &&
        !modoEdicaoAtivo &&
        !modoSelecaoAtivo &&
        !estaArrastando &&
        !movimentoCartaoEmAndamento
      ) {
        carregarDadosDoCanal(canalAtualId, true);
      }
    }, 600);
  }

  socket.on('nova_mensagem', (msg) => {
    // Notificações internas (card movido, tag add/removida, transferência
    // de dono etc.) viajam pelo mesmo evento de socket que mensagens reais
    // do WhatsApp, mas são só log de sistema — não devem substituir a
    // prévia da última mensagem do card, senão mistura e confunde.
    if (msg.type === 'notification_template') return;

    const numLimpo = msg.chat_id.replace('@c.us', '').replace(/\D/g, '');
    const cartoes = document.querySelectorAll('.kanban-cartao');
    let cartaoEncontradoNaTela = false;

    cartoes.forEach((cartao) => {
      const numCartao = String(cartao.dataset.numeroWpp).replace(/\D/g, '');
      if (
        numCartao === numLimpo ||
        `55${numCartao}` === numLimpo ||
        numCartao === `55${numLimpo}`
      ) {
        cartaoEncontradoNaTela = true;
        const balao = cartao.querySelector('.cartao-balao-previa');

        if (balao) {
          let previaTexto = escapeHtml(msg.body || 'Sem mensagens');
          if (msg.type === 'image')
            previaTexto = '<i class="fas fa-camera"></i> Imagem';
          else if (msg.type === 'video')
            previaTexto = '<i class="fas fa-video"></i> Vídeo';
          else if (msg.type === 'ptt' || msg.type === 'audio')
            previaTexto = '<i class="fas fa-microphone"></i> Áudio';
          else if (msg.type === 'document')
            previaTexto = '<i class="fas fa-file-alt"></i> Arquivo';
          else if (msg.type === 'comentario')
            previaTexto = `<i class="ph-bold ph-note-pencil"></i> <em style="color: #888;">${escapeHtml(msg.body)}</em>`;

          if (msg.is_from_me) {
            let remetente = escapeHtml(msg.sent_by_name || 'Você');
            if (remetente.toUpperCase().startsWith('FLUXO:')) {
              remetente = remetente.replace(/FLUXO:\s*/i, '').trim();
            }
            if (msg.type === 'comentario') {
              previaTexto = `<strong>${remetente} (Nota):</strong> ${previaTexto}`;
            } else {
              previaTexto = `<strong>${remetente}:</strong> ${previaTexto}`;
            }
          }

          balao.innerHTML = previaTexto;

          cartao.style.transition = 'border-color 0.3s';
          cartao.style.borderColor = 'var(--azul-principal)';
          setTimeout(
            () => (cartao.style.borderColor = 'var(--cor-borda)'),
            1500
          );
        }
      }
    });

    // Cliente que não está desenhado (outra página, filtrado, ou novo): o
    // mesmo refresh agrupado do kanban_atualizado, em vez de um por mensagem.
    if (!cartaoEncontradoNaTela && canalAtualId) agendarRefreshKanban();
  });

  // ==========================================================================
  // EVENTO DE CLIQUE NOS BOTÕES DE ORDENAÇÃO
  // ==========================================================================
  document.querySelectorAll('.sort-option').forEach((btn) => {
    btn.addEventListener('click', (e) => {
      e.stopPropagation();

      document
        .querySelectorAll('.sort-option')
        .forEach((b) => b.classList.remove('selected'));

      btn.classList.add('selected');

      estadoKanban.ordenacaoAtual = btn.dataset.sort;

      document.getElementById('dropdown-engrenagem').classList.add('hidden');

      aplicarFiltroPesquisa();
    });
  });

  // 👇 COLE ESTE CÓDIGO NOVO AQUI 👇

  // ==========================================================================
  // 10. EDITOR DO LAYOUT MODULAR DOS CARTÕES
  // ==========================================================================
  const modalConfig = document.getElementById('modal-config-cartao');
  const btnAbrirConfig = document.getElementById('btn-abrir-config-cartao');
  const btnSalvarConfig = document.getElementById('btn-salvar-config-cartao');
  // dropdownEngrenagem já está declarado no topo do arquivo!

  // Busca os Campos Dinâmicos do Sistema para preencher as opções (IIFE Async)
  (async () => {
    try {
      const res = await fetch('/api/crm-campos', {
        headers: { Authorization: `Bearer ${localStorage.getItem('token')}` },
      });
      const dados = await res.json();
      if (dados.success) camposDinamicosCRM = dados.campos;
      renderizarFiltrosCamposPersonalizados();
    } catch (e) {
      console.error('Erro ao puxar campos dinâmicos:', e);
    }
  })();

  function popularSelects() {
    // Todos os campos de texto ganham os campos personalizados do CRM —
    // slotAvatar/slotAvatarTopo ficam de fora de propósito, eles só
    // escolhem QUAL foto aparece (cliente/responsável), não são campos de texto.
    const selects = [
      document.getElementById('slot1'),
      document.getElementById('slot2'),
      document.getElementById('slot3'),
      document.getElementById('slot4'),
      document.getElementById('slot5'),
      document.getElementById('slot6'),
      document.getElementById('slot7'),
    ];

    selects.forEach((sel) => {
      // 👇 REMOVE O GRUPO INTEIRO AO INVÉS SÓ DAS OPÇÕES
      const grupoAntigo = sel.querySelector(
        'optgroup[label="Campos personalizados"]'
      );
      if (grupoAntigo) grupoAntigo.remove();

      if (camposDinamicosCRM.length > 0) {
        const group = document.createElement('optgroup');
        group.label = 'Campos personalizados';
        camposDinamicosCRM.forEach((c) => {
          const option = document.createElement('option');
          option.value = 'custom_' + c.nome;
          option.textContent = c.nome.charAt(0).toUpperCase() + c.nome.slice(1);
          group.appendChild(option);
        });
        sel.appendChild(group);
      }
    });

    SLOTS_CONFIG_CARTAO.forEach(sincronizarDropdownSlot);
  }

  // --------------------------------------------------------------------
  // Dropdowns customizados dos slots (mesmo padrão visual usado em
  // Instâncias/Fluxos/Bate-papo: um botão-gatilho + uma lista flutuante
  // própria, no lugar da lista nativa do navegador). O <select> de cada
  // slot continua no DOM, só escondido — ele é a única fonte da verdade,
  // então o resto do código (abrir modal, salvar layout) não precisou
  // mudar: continua lendo/escrevendo em `document.getElementById('slotN').value`.
  // slot6 = campo da linha de baixo; slotAvatar = foto da linha de baixo;
  // slotAvatarTopo = foto de cima (topo-esquerda); slot7 = 3ª linha do meio
  // (junto com slot4 e slot5).
  // --------------------------------------------------------------------
  const SLOTS_CONFIG_CARTAO = [
    'slot1',
    'slot2',
    'slot3',
    'slot4',
    'slot5',
    'slot7',
    'slot6',
    'slotAvatar',
    'slotAvatarTopo',
  ];

  // Guarda os elementos de cada slot por referência estável (em vez de
  // re-buscar via querySelector), porque o painel de opções é "teleportado"
  // pro <body> enquanto aberto (ver posicionarPainelSlot) e deixaria de ser
  // encontrado dentro do wrapper original nesse meio-tempo.
  const dropdownSlotEls = new Map();

  function fecharTodosDropdownsSlot() {
    dropdownSlotEls.forEach(({ trigger, opcoesEl, wrapper }) => {
      opcoesEl.classList.add('hidden');
      trigger.classList.remove('aberto');
      opcoesEl.style.top = '';
      opcoesEl.style.left = '';
      opcoesEl.style.right = '';
      // Devolve o painel pro lugar de origem no DOM (estava no <body>)
      if (opcoesEl.parentElement !== wrapper) wrapper.appendChild(opcoesEl);
    });
  }

  // Move o painel pra fora do card/modal (que têm overflow: hidden pra
  // arredondar cantos) e calcula a posição real na tela a partir do botão,
  // com flip automático pra cima quando não sobra espaço embaixo.
  function posicionarPainelSlot({ trigger, opcoesEl, wrapper }) {
    document.body.appendChild(opcoesEl);

    const alinharDireita = wrapper.classList.contains('dropdown-slot-data');
    const rectTrigger = retanguloLocal(trigger);
    const alturaPainel = opcoesEl.offsetHeight;
    const espacoAbaixo = viewportLocal().altura - rectTrigger.bottom;
    const abrirParaCima =
      espacoAbaixo < alturaPainel + 12 && rectTrigger.top > alturaPainel + 12;

    opcoesEl.style.top = abrirParaCima
      ? `${Math.max(8, rectTrigger.top - alturaPainel - 4)}px`
      : `${rectTrigger.bottom + 4}px`;

    if (alinharDireita) {
      opcoesEl.style.left = 'auto';
      opcoesEl.style.right = `${viewportLocal().largura - rectTrigger.right}px`;
    } else {
      opcoesEl.style.left = `${rectTrigger.left}px`;
      opcoesEl.style.right = 'auto';
    }
  }

  function criarOpcaoDropdownSlot(optionEl, selectEl) {
    const div = document.createElement('div');
    div.className = 'dropdown-slot-opcao';
    div.textContent = optionEl.textContent;
    if (optionEl.value === selectEl.value) div.classList.add('ativa');

    div.addEventListener('click', () => {
      selectEl.value = optionEl.value;
      selectEl.dispatchEvent(new Event('change', { bubbles: true }));
      fecharTodosDropdownsSlot();
      sincronizarDropdownSlot(selectEl.id);
    });

    return div;
  }

  function sincronizarDropdownSlot(slotId) {
    const dados = dropdownSlotEls.get(slotId);
    if (!dados) return;
    const { select, textoEl, opcoesEl } = dados;

    // Reconstrói a listinha visual a partir das <option>/<optgroup> reais
    opcoesEl.innerHTML = '';
    Array.from(select.children).forEach((filho) => {
      if (filho.tagName === 'OPTGROUP') {
        const titulo = document.createElement('div');
        titulo.className = 'dropdown-slot-titulo-grupo';
        titulo.textContent = filho.label;
        opcoesEl.appendChild(titulo);
        Array.from(filho.children).forEach((opt) =>
          opcoesEl.appendChild(criarOpcaoDropdownSlot(opt, select))
        );
      } else if (filho.tagName === 'OPTION') {
        opcoesEl.appendChild(criarOpcaoDropdownSlot(filho, select));
      }
    });

    const selecionada = select.options[select.selectedIndex];
    textoEl.textContent = selecionada ? selecionada.textContent : 'Sem valor';
    textoEl.classList.toggle('slot-vazio', select.value === 'vazio');
  }

  (function inicializarDropdownsSlot() {
    SLOTS_CONFIG_CARTAO.forEach((slotId) => {
      const select = document.getElementById(slotId);
      const wrapper = select ? select.closest('.dropdown-slot') : null;
      if (!wrapper) return;

      const trigger = wrapper.querySelector('.dropdown-slot-trigger');
      const textoEl = trigger.querySelector('.dropdown-slot-trigger-texto');
      const opcoesEl = wrapper.querySelector('.dropdown-slot-opcoes');
      const dados = { select, wrapper, trigger, textoEl, opcoesEl };
      dropdownSlotEls.set(slotId, dados);

      trigger.addEventListener('click', (e) => {
        e.stopPropagation();
        const jaEstavaAberto = !opcoesEl.classList.contains('hidden');
        fecharTodosDropdownsSlot();
        if (!jaEstavaAberto) {
          opcoesEl.classList.remove('hidden');
          trigger.classList.add('aberto');
          posicionarPainelSlot(dados);
        }
      });

      sincronizarDropdownSlot(slotId);
    });

    document.addEventListener('click', fecharTodosDropdownsSlot);
    window.addEventListener('resize', fecharTodosDropdownsSlot);
    // capture:true pra pegar o scroll de qualquer área da página, MAS sem
    // fechar quando o scroll é dentro do próprio painel (senão rolar a
    // listinha de opções — mouse ou barra de rolagem — fecharia ela sozinha)
    document.addEventListener(
      'scroll',
      (e) => {
        if (e.target && e.target.classList?.contains('dropdown-slot-opcoes'))
          return;
        fecharTodosDropdownsSlot();
      },
      true
    );
  })();

  // Abriu o Modal: Carrega os dados da memória para a tela
  if (btnAbrirConfig) {
    console.log('✅ SUCESSO: O botão de personalizar foi encontrado na tela!');

    btnAbrirConfig.addEventListener('click', (e) => {
      e.stopPropagation(); // Evita que o clique feche o menu sem querer
      console.log('✅ SUCESSO: Você clicou no botão!');

      dropdownEngrenagem.classList.add('hidden');
      if (!canalAtualId)
        return Swal.fire('Aviso', 'Selecione um canal primeiro', 'warning');

      popularSelects();

      const config = estadoKanban.configCartaoAtual || configCartaoPadrao();

      // Tenta preencher os campos (proteção com try/catch para não travar a tela)
      try {
        document.getElementById('slot1').value = config.slot1 || 'vazio';
        document.getElementById('slot2').value = config.slot2 || 'vazio';
        document.getElementById('slot3').value = config.slot3 || 'vazio';
        document.getElementById('slot4').value = config.slot4 || 'vazio';
        document.getElementById('slot5').value = config.slot5 || 'vazio';
        document.getElementById('slot7').value = config.slot7 || 'vazio';
        document.getElementById('slot6').value = config.slot6 || 'vazio';
        document.getElementById('slotAvatar').value =
          config.slotAvatar || 'foto_cliente';
        document.getElementById('slotAvatarTopo').value =
          resolveModoAvatarTopo(config);
        SLOTS_CONFIG_CARTAO.forEach(sincronizarDropdownSlot);
      } catch (err) {
        console.error('Erro ao preencher os selects do modal:', err);
      }

      // Removemos a classe que oculta
      modalConfig.classList.remove('hidden');

      // 👇 FORÇA BRUTA: Injetamos estilos inquebráveis direto no elemento
      modalConfig.style.setProperty('display', 'flex', 'important');
      modalConfig.style.setProperty('z-index', '999999', 'important');
      modalConfig.style.setProperty('opacity', '1', 'important');
      modalConfig.style.setProperty('visibility', 'visible', 'important');
    });
  } else {
    console.error(
      "❌ ERRO: O JavaScript não encontrou o botão 'btn-abrir-config-cartao' no HTML."
    );
  }

  // ==========================================================================
  // 10.0 MOTIVOS DE PERDA — a lista é da EMPRESA (vale pra todos os funis).
  // Renomear, desativar, reordenar e criar. Nada é apagado: o id pode estar
  // gravado em clientes. Os de sistema (o CRM usa o id) não desativam.
  // ==========================================================================
  const modalMotivos = document.getElementById('modal-motivos-perdido');
  let motivosEmEdicao = [];

  function abrirModalMotivos() {
    modalMotivos.classList.remove('hidden');
    modalMotivos.style.setProperty('display', 'flex', 'important');
    modalMotivos.style.setProperty('z-index', '999999', 'important');
    modalMotivos.style.setProperty('opacity', '1', 'important');
    modalMotivos.style.setProperty('visibility', 'visible', 'important');
  }
  function fecharModalMotivos() {
    modalMotivos.classList.add('hidden');
    ['display', 'z-index', 'opacity', 'visibility'].forEach((p) => modalMotivos.style.removeProperty(p));
  }

  function desenharMotivos() {
    const esc = (t) => String(t ?? '').replace(/[&<>"']/g, (c) => `&#${c.charCodeAt(0)};`);
    const ultimo = motivosEmEdicao.length - 1;
    document.getElementById('mp-lista').innerHTML = motivosEmEdicao
      .map(
        (m, i) => `
        <div class="mp-linha${m.ativo ? '' : ' inativo'}" data-i="${i}">
          <div class="mp-ordem">
            <button type="button" data-mover="-1" aria-label="Subir" ${i === 0 ? 'disabled' : ''}><i class="ph-bold ph-caret-up"></i></button>
            <button type="button" data-mover="1" aria-label="Descer" ${i === ultimo ? 'disabled' : ''}><i class="ph-bold ph-caret-down"></i></button>
          </div>
          <input type="text" class="mp-nome" maxlength="60" value="${esc(m.nome)}" placeholder="Nome do motivo" aria-label="Nome do motivo">
          ${m.sistema ? '<i class="ph-bold ph-lock-simple mp-sistema" title="Usado pelo sistema: não pode ser desativado"></i>' : ''}
          <label class="checkbox-custom" title="${m.ativo ? 'Ativo' : 'Desativado'}">
            <input type="checkbox" class="mp-ativo" ${m.ativo ? 'checked' : ''} ${m.sistema ? 'disabled' : ''}>
            <span class="switch-track"></span>
          </label>
        </div>`
      )
      .join('');
  }

  const listaMotivosEl = document.getElementById('mp-lista');
  listaMotivosEl.addEventListener('input', (e) => {
    const linha = e.target.closest('.mp-linha');
    if (linha && e.target.classList.contains('mp-nome')) motivosEmEdicao[Number(linha.dataset.i)].nome = e.target.value;
  });
  listaMotivosEl.addEventListener('change', (e) => {
    const linha = e.target.closest('.mp-linha');
    if (!linha || !e.target.classList.contains('mp-ativo')) return;
    motivosEmEdicao[Number(linha.dataset.i)].ativo = e.target.checked;
    linha.classList.toggle('inativo', !e.target.checked);
  });
  listaMotivosEl.addEventListener('click', (e) => {
    const botao = e.target.closest('[data-mover]');
    if (!botao || botao.disabled) return;
    const i = Number(botao.closest('.mp-linha').dataset.i);
    const j = i + Number(botao.dataset.mover);
    [motivosEmEdicao[i], motivosEmEdicao[j]] = [motivosEmEdicao[j], motivosEmEdicao[i]];
    desenharMotivos();
  });
  document.getElementById('mp-adicionar').addEventListener('click', () => {
    motivosEmEdicao.push({ id: null, nome: '', ativo: true, sistema: false });
    desenharMotivos();
    const campos = listaMotivosEl.querySelectorAll('.mp-nome');
    campos[campos.length - 1].focus();
  });
  document.querySelectorAll('.fechar-modal-motivos').forEach((b) => b.addEventListener('click', fecharModalMotivos));

  const btnAbrirMotivos = document.getElementById('btn-abrir-motivos-perdido');
  if (btnAbrirMotivos) {
    btnAbrirMotivos.addEventListener('click', async (e) => {
      e.stopPropagation();
      dropdownEngrenagem.classList.add('hidden');
      try {
        const res = await fetch('/api/motivos-perdido', {
          headers: { Authorization: `Bearer ${localStorage.getItem('token')}` },
        });
        const dados = await res.json();
        motivosEmEdicao = (dados.todos || dados.motivos || []).map((m) => ({ ...m, ativo: m.ativo !== false }));
      } catch (erro) {
        return Swal.fire('Erro', 'Não foi possível carregar os motivos.', 'error');
      }
      desenharMotivos();
      abrirModalMotivos();
    });
  }

  document.getElementById('btn-salvar-motivos').addEventListener('click', async () => {
    const botao = document.getElementById('btn-salvar-motivos');
    const motivos = motivosEmEdicao
      .filter((m) => m.id || m.nome.trim()) // linha nova deixada em branco não conta
      .map((m) => ({ id: m.id || undefined, nome: m.nome.trim(), ativo: m.ativo }));
    botao.disabled = true;
    try {
      const res = await fetch('/api/motivos-perdido', {
        method: 'PUT',
        headers: { Authorization: `Bearer ${localStorage.getItem('token')}`, 'Content-Type': 'application/json' },
        body: JSON.stringify({ motivos }),
      });
      const dados = await res.json();
      if (!res.ok || !dados.success) throw new Error(dados.message || 'Erro ao salvar.');
      motivosPerdidoCache = null; // a próxima escolha de motivo relê a lista nova
      motivosTodosCache = null;
      await carregarMotivosPerdido();
      fecharModalMotivos();
      Swal.fire({ icon: 'success', title: 'Motivos salvos', timer: 1400, showConfirmButton: false });
    } catch (erro) {
      Swal.fire('Erro', erro.message, 'error');
    } finally {
      botao.disabled = false;
    }
  });

  // ==========================================================================
  // 10.1 AGENTE DE IA (por funil) — abre uma página inteira, não um modal
  // ==========================================================================
  const btnAbrirAgenteIa = document.getElementById('btn-abrir-agente-ia');
  if (btnAbrirAgenteIa) {
    btnAbrirAgenteIa.addEventListener('click', (e) => {
      e.stopPropagation();
      dropdownEngrenagem.classList.add('hidden');
      if (!canalAtualId)
        return Swal.fire('Aviso', 'Selecione um canal primeiro', 'warning');
      window.location.href = '/agente-ia?funilId=' + canalAtualId;
    });
  }

  // Função para fechar limpando a Força Bruta
  function fecharModalConfig() {
    modalConfig.classList.add('hidden');
    modalConfig.style.removeProperty('display');
    modalConfig.style.removeProperty('z-index');
    modalConfig.style.removeProperty('opacity');
    modalConfig.style.removeProperty('visibility');
    fecharTodosDropdownsSlot();
  }

  // Fechar o Modal (Botões Cancelar e o X)
  document.querySelectorAll('.fechar-modal-config').forEach((btn) => {
    btn.addEventListener('click', fecharModalConfig);
  });

  // Salvar no Banco
  if (btnSalvarConfig) {
    btnSalvarConfig.addEventListener('click', async () => {
      const novaConfig = {
        slot1: document.getElementById('slot1').value,
        slot2: document.getElementById('slot2').value,
        slot3: document.getElementById('slot3').value,
        slot4: document.getElementById('slot4').value,
        slot5: document.getElementById('slot5').value,
        slot7: document.getElementById('slot7').value,
        slot6: document.getElementById('slot6').value,
        slotAvatar: document.getElementById('slotAvatar').value,
        slotAvatarTopo: document.getElementById('slotAvatarTopo').value,
      };

      btnSalvarConfig.disabled = true;
      btnSalvarConfig.innerHTML = '<i class="bx bx-loader-alt bx-spin"></i>';

      try {
        const res = await fetch(
          `/api/kanban/canais/${canalAtualId}/config-cartao`,
          {
            method: 'PUT',
            headers: {
              'Content-Type': 'application/json',
              Authorization: `Bearer ${localStorage.getItem('token')}`,
            },
            body: JSON.stringify({ config_cartao: novaConfig }),
          }
        );

        const data = await res.json();
        if (data.sucesso) {
          estadoKanban.configCartaoAtual = novaConfig;

          const canalData = canaisGlobais.find((c) => c.id == canalAtualId);
          if (canalData) canalData.config_cartao = novaConfig;

          mostrarAlerta('Layout atualizado!', 'success');

          // Redesenha a tela instantaneamente (agora consegue achar o renderizarQuadro!)
          renderizarQuadro(estadoKanban.colunas, estadoKanban.cartoes);
        } else {
          Swal.fire('Erro', 'Não foi possível salvar a configuração.', 'error');
        }
      } catch (e) {
        Swal.fire('Erro', 'Falha na comunicação com o servidor.', 'error');
      } finally {
        btnSalvarConfig.disabled = false;
        btnSalvarConfig.innerHTML = 'Salvar Layout';
      }
    });
  }
});

window.abrirChatKanban = function (numeroWpp) {
  if (
    String(numeroWpp).includes('@lid') ||
    String(numeroWpp).includes('@g.us')
  ) {
    window.location.href = `/bate-papo?chatId=${numeroWpp}`;
    return;
  }

  // O número do cartão já é a forma do cadastro (com o 55): vai como está.
  // O bate-papo resolve com/sem o nono dígito no servidor.
  const numeroLimpo = String(numeroWpp).replace('@c.us', '').replace(/\D/g, '');

  window.location.href = `/bate-papo?chatId=${numeroLimpo}@c.us`;
};

// ==========================================================================
// CRIAR NOVO FUNIL (CANAL)
// ==========================================================================

window.criarNovoCanal = async function () {
  document.getElementById('popup-canais').classList.remove('aberto');
  document.getElementById('overlay-canais').classList.add('hidden');
  document.getElementById('trigger-canal').classList.remove('aberto');

  const { value: nomeCanal } = await Swal.fire({
    title: 'Criar Novo Funil',
    input: 'text',
    inputPlaceholder: 'Ex: Suporte Técnico',
    showCancelButton: true,
    confirmButtonColor: 'var(--azul-principal, #007bff)',
    cancelButtonText: 'Cancelar',
    confirmButtonText: 'Criar',
    inputValidator: (value) => {
      if (!value) {
        return 'O nome do funil é obrigatório!';
      }
    },
  });

  if (nomeCanal) {
    try {
      const res = await fetch('/api/kanban/canais', {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          Authorization: `Bearer ${localStorage.getItem('token')}`,
        },
        body: JSON.stringify({ nome: nomeCanal }),
      });

      const data = await res.json();

      if (data.sucesso) {
        Swal.fire({
          icon: 'success',
          title: 'Funil Criado!',
          text: 'A página será atualizada.',
          timer: 1500,
          showConfirmButton: false,
        }).then(() => {
          // Recarrega a tela para o novo funil aparecer na lista imediatamente
          window.location.reload();
        });
      } else {
        Swal.fire('Erro', data.mensagem || 'Erro ao criar funil.', 'error');
      }
    } catch (erro) {
      console.error(erro);
      Swal.fire('Erro', 'Falha de comunicação com o servidor.', 'error');
    }
  }
};

// ==========================================================================
// EDITAR (RENOMEAR) FUNIL (CANAL)
// ==========================================================================
window.editarFunil = async function (id, nomeAtual, event) {
  // Impede que o clique no botão dispare a seleção do funil no fundo
  event.stopPropagation();

  const { value: novoNome } = await Swal.fire({
    title: 'Renomear Funil',
    input: 'text',
    inputValue: nomeAtual,
    showCancelButton: true,
    confirmButtonColor: 'var(--azul-principal, #007bff)',
    cancelButtonText: 'Cancelar',
    confirmButtonText: 'Salvar',
    inputValidator: (value) => {
      if (!value || value.trim() === '') {
        return 'O nome do funil não pode ficar vazio!';
      }
    },
  });

  // Se a pessoa digitou um nome e ele é diferente do atual, salva!
  if (novoNome && novoNome.trim() !== nomeAtual) {
    try {
      const res = await fetch(`/api/kanban/canais/${id}`, {
        method: 'PUT',
        headers: {
          'Content-Type': 'application/json',
          Authorization: `Bearer ${localStorage.getItem('token')}`,
        },
        body: JSON.stringify({ nome: novoNome.trim() }),
      });

      const data = await res.json();
      if (data.sucesso) {
        Swal.fire({
          icon: 'success',
          title: 'Renomeado!',
          timer: 1000,
          showConfirmButton: false,
        }).then(() => {
          // Recarrega a página para atualizar o nome na lista e no cabeçalho
          window.location.reload();
        });
      } else {
        Swal.fire(
          'Erro',
          data.mensagem || 'Não foi possível renomear.',
          'error'
        );
      }
    } catch (e) {
      console.error(e);
      Swal.fire('Erro', 'Falha na comunicação com o servidor.', 'error');
    }
  }
};

// ==========================================================================
// DELETAR FUNIL (CANAL)
// ==========================================================================
window.deletarFunil = async function (id, nome, event) {
  // Impede que o clique na lixeira dispare o clique de selecionar o funil
  event.stopPropagation();

  const confirmacao = await Swal.fire({
    title: 'Excluir Funil?',
    html: `Você tem certeza que deseja excluir o funil <b>${escapeHtml(nome)}</b>?<br><br><span style="color: #ef4444; font-size: 13px;"><b>Atenção:</b> Todas as colunas, configurações e o posicionamento dos clientes neste funil serão perdidos para sempre!</span>`,
    icon: 'warning',
    showCancelButton: true,
    confirmButtonColor: '#ef4444',
    cancelButtonColor: '#6c757d',
    confirmButtonText: 'Sim, excluir!',
    cancelButtonText: 'Cancelar',
  });

  if (confirmacao.isConfirmed) {
    try {
      const res = await fetch(`/api/kanban/canais/${id}`, {
        method: 'DELETE',
        headers: { Authorization: `Bearer ${localStorage.getItem('token')}` },
      });

      const data = await res.json();
      if (data.sucesso) {
        Swal.fire({
          icon: 'success',
          title: 'Funil Excluído',
          text: 'O funil foi deletado com sucesso.',
          timer: 1500,
          showConfirmButton: false,
        }).then(() => {
          // Recarrega a página para atualizar o menu e limpar os dados velhos
          window.location.reload();
        });
      } else {
        Swal.fire(
          'Erro',
          data.mensagem || 'Não foi possível excluir.',
          'error'
        );
      }
    } catch (e) {
      console.error(e);
      Swal.fire('Erro', 'Falha na comunicação com o servidor.', 'error');
    }
  }
};

// ==========================================================================
// DELETAR COLUNA / ETAPA DO KANBAN
// ==========================================================================
window.deletarColuna = async function (id, nome, event) {
  event.stopPropagation();

  const confirmacao = await Swal.fire({
    title: 'Excluir Etapa?',
    html: `Tem certeza que deseja excluir a etapa <b>${escapeHtml(nome)}</b>?<br><br><span style="color: #ef4444; font-size: 13px;"><b>Atenção:</b> Todos os clientes que estiverem nesta etapa sumirão deste funil!</span>`,
    icon: 'warning',
    showCancelButton: true,
    confirmButtonColor: '#ef4444',
    cancelButtonColor: '#6c757d',
    confirmButtonText: 'Sim, excluir!',
    cancelButtonText: 'Cancelar',
  });

  if (confirmacao.isConfirmed) {
    try {
      const res = await fetch(`/api/kanban/colunas/${id}`, {
        method: 'DELETE',
        headers: { Authorization: `Bearer ${localStorage.getItem('token')}` },
      });

      const data = await res.json();
      if (data.sucesso) {
        Swal.fire({
          icon: 'success',
          title: 'Etapa Excluída',
          timer: 1500,
          showConfirmButton: false,
        }).then(() => {
          // Recarrega apenas os dados do quadro na tela
          carregarDadosDoCanal(canalAtualId);
        });
      } else {
        Swal.fire('Erro', 'Não foi possível excluir a coluna.', 'error');
      }
    } catch (e) {
      console.error(e);
      Swal.fire('Erro', 'Falha na comunicação com o servidor.', 'error');
    }
  }
};

// ==========================================================================
// DROP ZONES GLOBAIS (GANHO E PERDIDO)
// ==========================================================================
document.addEventListener('DOMContentLoaded', () => {
  const dropzonesAreas = document.querySelectorAll('.dropzone-area');

  dropzonesAreas.forEach((zone) => {
    // Quando o mouse passa por cima da zona com o cartão
    zone.addEventListener('dragover', (e) => {
      e.preventDefault();
      zone.classList.add('drag-over');
    });

    // Quando o mouse sai da zona
    zone.addEventListener('dragleave', () => {
      zone.classList.remove('drag-over');
    });

    // Quando o cara solta o botão do mouse (DROP)
    zone.addEventListener('drop', async (e) => {
      e.preventDefault();
      zone.classList.remove('drag-over');

      const cartaoArrastando = document.querySelector('.arrastando');
      if (!cartaoArrastando) return;

      // Esconde as zonas imediatamente para dar fluidez
      document.getElementById('dropzones-fechamento').classList.remove('ativo');

      const acao = zone.dataset.acao; // vai ser 'ganho' ou 'perdido'
      const numeroWpp = cartaoArrastando.dataset.numeroWpp;

      // É por aqui que a vendedora marca perda: ela não enxerga a coluna
      // PERDIDO no quadro, só esta dropzone. Pergunta o motivo ANTES de
      // apagar o cartão da tela — cancelar tem que deixar tudo como estava.
      let motivoEscolhido = null;
      if (acao === 'perdido') {
        motivoEscolhido = await pedirMotivoPerdido();
        if (!motivoEscolhido) return;
      }

      // Faz o cartão "sumir" visualmente logo de cara pra parecer instantâneo
      cartaoArrastando.style.opacity = '0.2';

      try {
        // Chama a nossa rota mágica criada no Passo Anterior!
        const res = await fetch('/api/chat/status-rapido', {
          method: 'POST',
          headers: {
            'Content-Type': 'application/json',
            Authorization: `Bearer ${localStorage.getItem('token')}`,
          },
          body: JSON.stringify({
            chatId: numeroWpp,
            acao: acao,
            motivo_perdido: motivoEscolhido,
          }),
        });

        const data = await res.json();
        if (data.success) {
          // Destrói o cartão da tela, já que ele sumiu pro vendedor
          cartaoArrastando.remove();

          mostrarAlerta(
            `Cliente movido para ${acao.toUpperCase()}!`,
            'success'
          );
        }
      } catch (err) {
        console.error('Erro ao usar dropzone', err);
        Swal.fire('Erro', 'Falha na comunicação com o servidor.', 'error');
        cartaoArrastando.style.opacity = '1'; // Devolve o cartão se der erro
      }
    });
  });
});

// ==========================================================================
// 9. NAVEGAÇÃO AVANÇADA (MINIMAPA, PAN E SHIFT+SCROLL)
// ==========================================================================
document.addEventListener('DOMContentLoaded', () => {
  const areaColunas = document.getElementById('area-colunas-kanban');

  // Elementos do Minimapa
  const minimapa = document.getElementById('kanban-minimapa');
  const minimapaTrilha = document.getElementById('minimapa-trilha');
  const minimapaLente = document.getElementById('minimapa-lente');

  // Variáveis de controle para o Drag do Minimapa
  let isDraggingLens = false;
  let startX;
  let scrollLeftStart;

  // Variáveis de controle para o Grab to Pan (Arrastar a tela)
  let isPanning = false;
  let panStartX;
  let panScrollLeftStart;

  // -------------------------------------------------------------
  // A. SHIFT + SCROLL (Rolagem horizontal)
  // -------------------------------------------------------------
  areaColunas.addEventListener('wheel', (e) => {
    if (e.shiftKey) {
      e.preventDefault();
      areaColunas.scrollLeft += e.deltaY > 0 ? 50 : -50;
    }
  });

  // -------------------------------------------------------------
  // B. GRAB TO PAN (Clicar no fundo vazio e arrastar)
  // -------------------------------------------------------------
  areaColunas.addEventListener('mousedown', (e) => {
    // Só ativa se clicar diretamente no fundo (não em cartões ou botões)
    if (
      e.target.classList.contains('area-colunas-kanban') ||
      e.target.classList.contains('kanban-coluna-corpo')
    ) {
      isPanning = true;
      areaColunas.style.cursor = 'grabbing';
      panStartX = e.pageX;
      panScrollLeftStart = areaColunas.scrollLeft;
    }
  });

  document.addEventListener('mouseup', () => {
    if (isPanning) {
      isPanning = false;
      areaColunas.style.cursor = '';
    }
    if (isDraggingLens) {
      isDraggingLens = false;
      minimapaLente.style.cursor = 'grab';
      document.body.style.userSelect = ''; // Devolve a seleção de texto
    }
  });

  document.addEventListener('mousemove', (e) => {
    // Lógica do Grab to Pan (Tela principal)
    if (isPanning) {
      e.preventDefault();
      const x = e.pageX;
      const walk = (x - panStartX) * 1.5; // Velocidade do arraste
      areaColunas.scrollLeft = panScrollLeftStart - walk;
    }

    // Lógica do Arraste da Lente (Minimapa)
    if (isDraggingLens) {
      e.preventDefault();
      const trilhaRect = minimapaTrilha.getBoundingClientRect();
      let mouseLocalX = e.clientX - trilhaRect.left;

      // Limita o mouse dentro da trilha
      mouseLocalX = Math.max(0, Math.min(mouseLocalX, trilhaRect.width));

      // Calcula a porcentagem da posição do mouse e reflete no scroll
      const porcentagem = mouseLocalX / trilhaRect.width;
      areaColunas.scrollLeft = porcentagem * areaColunas.scrollWidth;
    }
  });

  // -------------------------------------------------------------
  // C. SINCRONIZAR MINIMAPA COM A TELA
  // -------------------------------------------------------------

  // Atualiza a lente quando a tela real faz scroll
  areaColunas.addEventListener('scroll', sincronizarLente);
  window.addEventListener('resize', sincronizarLente); // Ajusta se a janela mudar de tamanho

  function sincronizarLente() {
    if (areaColunas.scrollWidth <= areaColunas.clientWidth) {
      // Se não tem scroll (tela grande ou poucas colunas), esconde o minimapa
      minimapa.classList.add('hidden');
      return;
    } else {
      minimapa.classList.remove('hidden');
    }

    // Calcula largura da lente (%)
    const proporcaoVisivel = areaColunas.clientWidth / areaColunas.scrollWidth;
    minimapaLente.style.width = `${proporcaoVisivel * 100}%`;

    // Calcula posição X da lente (%)
    const proporcaoScroll = areaColunas.scrollLeft / areaColunas.scrollWidth;
    minimapaLente.style.left = `${proporcaoScroll * 100}%`;
  }

  // -------------------------------------------------------------
  // D. ARRASTAR A LENTE DO MINIMAPA
  // -------------------------------------------------------------
  minimapaLente.addEventListener('mousedown', (e) => {
    e.stopPropagation();
    isDraggingLens = true;
    minimapaLente.style.cursor = 'grabbing';
    document.body.style.userSelect = 'none'; // Evita selecionar texto sem querer
  });

  // Clicar direto na trilha faz a lente pular para aquele ponto
  minimapaTrilha.addEventListener('mousedown', (e) => {
    if (e.target === minimapaLente) return;
    const trilhaRect = minimapaTrilha.getBoundingClientRect();
    const cliqueX = e.clientX - trilhaRect.left;
    const porcentagem = cliqueX / trilhaRect.width;

    // Suaviza o pulo
    areaColunas.scrollTo({
      left: porcentagem * areaColunas.scrollWidth,
      behavior: 'smooth',
    });
  });

  // -------------------------------------------------------------
  // E. RENDERIZAR OS BLOQUINHOS DO MINIMAPA (COM CORES)
  // -------------------------------------------------------------
  window.atualizarEstruturaMinimapa = function (colunas) {
    // Remove os bloquinhos antigos, mantendo só a lente
    const blocosAntigos = minimapaTrilha.querySelectorAll('.mini-coluna');
    blocosAntigos.forEach((b) => b.remove());

    // Se não tiver colunas ou for zero, apenas alinha a lente e sai
    if (!colunas || colunas.length === 0) {
      setTimeout(sincronizarLente, 100);
      return;
    }

    // Cria novos bloquinhos baseados nas cores reais
    colunas.forEach((coluna) => {
      const bloco = document.createElement('div');
      bloco.className = 'mini-coluna';

      // Pega a cor da coluna (se não existir, usa um cinza padrão)
      const corHex = coluna.cor || '#cbd5e1';

      // O truque da opacidade: Adicionamos '30' no final do HEX para dar ~18% de transparência pro fundo
      // E '60' para a borda ficar um pouco mais visível (~38% de opacidade)
      bloco.style.backgroundColor = corHex + '30';
      bloco.style.borderColor = corHex + '60';

      minimapaTrilha.insertBefore(bloco, minimapaLente);
    });

    setTimeout(sincronizarLente, 100); // Aguarda renderizar
  };
});
