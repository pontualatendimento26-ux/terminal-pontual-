document.addEventListener('DOMContentLoaded', () => {
  const funilId = new URLSearchParams(window.location.search).get('funilId');
  if (!funilId) {
    window.location.href = '/kanban';
    return;
  }

  const token = localStorage.getItem('token');
  const headersJson = { 'Content-Type': 'application/json', Authorization: `Bearer ${token}` };
  const headersAuth = { Authorization: `Bearer ${token}` };

  let agenteAtual = null;
  let regrasAtuais = [];
  let testeHistorico = [];
  let dadosApoio = { tags: [], camposCrm: [], automacoes: [] };

  // ================= ABAS =================
  document.querySelectorAll('.agia-tab-btn').forEach((btn) => {
    btn.addEventListener('click', () => {
      document.querySelectorAll('.agia-tab-btn').forEach((b) => b.classList.remove('ativo'));
      document.querySelectorAll('.agia-tab-content').forEach((c) => c.classList.remove('ativo'));
      btn.classList.add('ativo');
      document.getElementById(`agia-tab-${btn.dataset.tab}`).classList.add('ativo');
      if (btn.dataset.tab === 'painel') carregarEstatisticas();
      if (btn.dataset.tab === 'integracoes') carregarIntegracoes();
      if (btn.dataset.tab === 'externo') carregarAgenteExterno();
      if (btn.dataset.tab === 'reoferta') carregarReoferta();
    });
  });

  // ================= DROPDOWN CUSTOMIZADO (mesmo padrão do Kanban) =================
  const todosDropdownsIA = [];

  function fecharTodosDropdownsIA() {
    todosDropdownsIA.forEach(({ trigger, opcoesEl, wrapper }) => {
      opcoesEl.classList.add('hidden');
      trigger.classList.remove('aberto');
      opcoesEl.style.top = '';
      opcoesEl.style.left = '';
      opcoesEl.style.width = '';
      if (opcoesEl.parentElement !== wrapper) wrapper.appendChild(opcoesEl);
    });
  }

  function posicionarPainelIA({ trigger, opcoesEl, wrapper }) {
    document.body.appendChild(opcoesEl);
    // style.left/top sao lidos como unidades LOCAIS, entao a medida do
    // gatilho e o tamanho da janela precisam vir na mesma regua.
    const rect = retanguloLocal(trigger);
    opcoesEl.style.width = `${rect.width}px`;
    const alturaPainel = opcoesEl.offsetHeight;
    const espacoAbaixo = viewportLocal().altura - rect.bottom;
    const abrirParaCima =
      espacoAbaixo < alturaPainel + 12 && rect.top > alturaPainel + 12;
    opcoesEl.style.top = abrirParaCima
      ? `${Math.max(8, rect.top - alturaPainel - 4)}px`
      : `${rect.bottom + 4}px`;
    opcoesEl.style.left = `${rect.left}px`;
  }

  function inicializarDropdownCustomizado(selectEl) {
    if (!selectEl || selectEl.dataset.dropdownInit) return;
    selectEl.dataset.dropdownInit = '1';

    const wrapper = document.createElement('div');
    wrapper.className = 'agia-dropdown';
    selectEl.parentNode.insertBefore(wrapper, selectEl);
    wrapper.appendChild(selectEl);
    selectEl.classList.add('agia-select-oculto');

    const trigger = document.createElement('button');
    trigger.type = 'button';
    trigger.className = 'agia-dropdown-trigger';
    trigger.innerHTML =
      '<span class="agia-dropdown-trigger-texto"></span><i class="ph-bold ph-caret-down agia-dropdown-seta"></i>';
    wrapper.appendChild(trigger);

    const opcoesEl = document.createElement('div');
    opcoesEl.className = 'agia-dropdown-opcoes hidden';
    wrapper.appendChild(opcoesEl);

    const textoEl = trigger.querySelector('.agia-dropdown-trigger-texto');
    const dados = { select: selectEl, wrapper, trigger, opcoesEl };

    function criarOpcao(optionEl) {
      const div = document.createElement('div');
      div.className = 'agia-dropdown-opcao';
      div.textContent = optionEl.textContent;
      if (optionEl.value === selectEl.value) div.classList.add('ativa');
      div.addEventListener('click', () => {
        selectEl.value = optionEl.value;
        selectEl.dispatchEvent(new Event('change', { bubbles: true }));
        fecharTodosDropdownsIA();
        sincronizar();
      });
      return div;
    }

    function sincronizar() {
      opcoesEl.innerHTML = '';
      Array.from(selectEl.children).forEach((filho) => {
        if (filho.tagName === 'OPTGROUP') {
          const titulo = document.createElement('div');
          titulo.className = 'agia-dropdown-titulo-grupo';
          titulo.textContent = filho.label;
          opcoesEl.appendChild(titulo);
          Array.from(filho.children).forEach((opt) => opcoesEl.appendChild(criarOpcao(opt)));
        } else if (filho.tagName === 'OPTION') {
          opcoesEl.appendChild(criarOpcao(filho));
        }
      });
      const selecionada = selectEl.options[selectEl.selectedIndex];
      textoEl.textContent = selecionada ? selecionada.textContent : '';
    }

    trigger.addEventListener('click', (e) => {
      e.stopPropagation();
      const jaAberto = !opcoesEl.classList.contains('hidden');
      fecharTodosDropdownsIA();
      if (!jaAberto) {
        opcoesEl.classList.remove('hidden');
        trigger.classList.add('aberto');
        posicionarPainelIA(dados);
      }
    });

    selectEl._agiaSync = sincronizar;
    todosDropdownsIA.push(dados);
    sincronizar();
  }

  function inicializarDropdownsEm(container) {
    container.querySelectorAll('select:not([data-dropdown-init])').forEach(inicializarDropdownCustomizado);
  }

  document.addEventListener('click', fecharTodosDropdownsIA);
  window.addEventListener('resize', fecharTodosDropdownsIA);

  // ================= CARREGAMENTO INICIAL =================
  async function carregarDadosApoio() {
    try {
      const [tagsRes, camposRes, automacoesRes] = await Promise.all([
        fetch('/api/tags', { headers: headersAuth }),
        fetch('/api/agente-ia/campos-crm-disponiveis', { headers: headersAuth }),
        fetch('/api/agente-ia/automacoes-disponiveis', { headers: headersAuth }),
      ]);
      const tagsJson = await tagsRes.json();
      const camposJson = await camposRes.json();
      const automacoesJson = await automacoesRes.json();
      dadosApoio.tags = Array.isArray(tagsJson) ? tagsJson : [];
      dadosApoio.camposCrm = camposJson.campos || [];
      dadosApoio.automacoes = automacoesJson.automacoes || [];
    } catch (e) {
      console.error('Erro ao carregar dados de apoio:', e);
    }
  }

  async function carregarAgente() {
    try {
      const res = await fetch(`/api/agente-ia/${funilId}`, { headers: headersAuth });
      const json = await res.json();
      if (!json.success) throw new Error(json.message || 'Falha ao carregar agente.');

      agenteAtual = json.agente;
      regrasAtuais = json.regras || [];

      preencherPersona(agenteAtual.persona || {});
      preencherConfig(agenteAtual.config_geral || {});
      const selTamanho = document.getElementById('agia-tamanho-resposta');
      const selPeriodo = document.getElementById('agia-periodo-limite');
      const selAcaoLimite = document.getElementById('agia-acao-limite');
      const selIntegracaoMetodo = document.getElementById('agia-integracao-metodo');
      inicializarDropdownCustomizado(selTamanho);
      inicializarDropdownCustomizado(selPeriodo);
      inicializarDropdownCustomizado(selAcaoLimite);
      inicializarDropdownCustomizado(selIntegracaoMetodo);
      selTamanho._agiaSync && selTamanho._agiaSync();
      selPeriodo._agiaSync && selPeriodo._agiaSync();
      selAcaoLimite._agiaSync && selAcaoLimite._agiaSync();
      selIntegracaoMetodo._agiaSync && selIntegracaoMetodo._agiaSync();
      document.getElementById('agia-toggle-ativo').checked = !!agenteAtual.ativo;
      atualizarLabelToggle();
      renderizarRegras();
      renderizarFontes(json.fontes || []);

      const funilRes = await fetch('/api/kanban/canais', { headers: headersAuth });
      const funilJson = await funilRes.json();
      const funil = (funilJson.canais || []).find((c) => String(c.id) === String(funilId));
      if (funil) document.getElementById('agia-nome-funil').textContent = `— ${funil.nome}`;
    } catch (e) {
      console.error(e);
      window.mostrarAlerta && window.mostrarAlerta('Erro ao carregar o agente.', 'error');
    }
  }

  document.getElementById('agia-toggle-ativo').addEventListener('change', async (e) => {
    atualizarLabelToggle();
    try {
      await fetch(`/api/agente-ia/${funilId}/ativo`, {
        method: 'PUT',
        headers: headersJson,
        body: JSON.stringify({ ativo: e.target.checked }),
      });
    } catch (err) {}
  });

  function atualizarLabelToggle() {
    const checked = document.getElementById('agia-toggle-ativo').checked;
    document.getElementById('agia-toggle-label').textContent = checked ? 'Ligado' : 'Desligado';
  }

  // ================= PERSONA =================
  function preencherPersona(persona) {
    document.getElementById('agia-papel').value = persona.papel || '';
    document.getElementById('agia-personalidade').value = persona.personalidade || '';
    document.getElementById('agia-tom-voz').value = persona.tom_voz || '';
    document.getElementById('agia-idioma').value = persona.idioma || 'Português do Brasil';
    document.getElementById('agia-tamanho-resposta').value = persona.tamanho_resposta || 'media';
    document.getElementById('agia-pausa-resposta').value = persona.pausa_resposta_seg ?? 3;

    const lista = document.getElementById('agia-diretrizes-lista');
    lista.innerHTML = '';
    (persona.diretrizes && persona.diretrizes.length ? persona.diretrizes : ['']).forEach((d) =>
      adicionarLinhaDiretriz(d)
    );
  }

  function adicionarLinhaDiretriz(valor = '') {
    const lista = document.getElementById('agia-diretrizes-lista');
    const linha = document.createElement('div');
    linha.className = 'agia-diretriz-item';
    linha.innerHTML = `
      <input type="text" value="${escaparAtributo(valor)}" placeholder="Ex: nunca prometa taxa antes de confirmar no sistema" />
      <button type="button" title="Remover"><i class="ph-bold ph-trash"></i></button>
    `;
    linha.querySelector('button').addEventListener('click', () => linha.remove());
    lista.appendChild(linha);
  }

  document.getElementById('agia-add-diretriz').addEventListener('click', () => adicionarLinhaDiretriz());

  document.getElementById('agia-salvar-persona').addEventListener('click', async () => {
    const diretrizes = Array.from(document.querySelectorAll('#agia-diretrizes-lista input'))
      .map((i) => i.value.trim())
      .filter(Boolean);

    const persona = {
      papel: document.getElementById('agia-papel').value.trim(),
      personalidade: document.getElementById('agia-personalidade').value.trim(),
      tom_voz: document.getElementById('agia-tom-voz').value.trim(),
      idioma: document.getElementById('agia-idioma').value.trim(),
      tamanho_resposta: document.getElementById('agia-tamanho-resposta').value,
      pausa_resposta_seg: Number(document.getElementById('agia-pausa-resposta').value) || 0,
      diretrizes,
    };

    try {
      await fetch(`/api/agente-ia/${funilId}/persona`, {
        method: 'PUT',
        headers: headersJson,
        body: JSON.stringify({ persona }),
      });
      if (agenteAtual) agenteAtual.persona = persona;
      window.mostrarAlerta && window.mostrarAlerta('Persona salva!', 'success');
    } catch (e) {
      window.mostrarAlerta && window.mostrarAlerta('Erro ao salvar persona.', 'error');
    }
  });

  // ================= CHAT DE TESTE =================
  const teseMensagensEl = document.getElementById('agia-teste-mensagens');
  const testeInput = document.getElementById('agia-teste-input');
  const testeBtnEnviar = document.getElementById('agia-teste-enviar');

  function adicionarBolhaTeste(texto, tipo) {
    const bolha = document.createElement('div');
    bolha.className = `agia-msg-bolha ${tipo}`;
    bolha.textContent = texto;
    teseMensagensEl.appendChild(bolha);
    teseMensagensEl.scrollTop = teseMensagensEl.scrollHeight;
  }

  async function enviarMensagemTeste() {
    const texto = testeInput.value.trim();
    if (!texto) return;

    adicionarBolhaTeste(texto, 'cliente');
    testeInput.value = '';
    testeInput.disabled = true;
    testeBtnEnviar.disabled = true;

    try {
      const res = await fetch(`/api/agente-ia/${funilId}/teste`, {
        method: 'POST',
        headers: headersJson,
        body: JSON.stringify({ historico: testeHistorico, mensagem: texto }),
      });
      const json = await res.json();
      if (!json.success) throw new Error(json.message || 'Falha no teste.');

      testeHistorico.push({ role: 'user', content: texto });

      (json.acoesExecutadas || []).forEach((acao) => {
        const desc = acao.resultado?.descricao || acao.ferramenta;
        adicionarBolhaTeste(`⚙️ ${desc}`, 'ia-acao');
      });

      if (json.textoResposta && json.textoResposta.trim()) {
        adicionarBolhaTeste(json.textoResposta, 'ia');
        testeHistorico.push({ role: 'assistant', content: json.textoResposta });
      } else if (!(json.acoesExecutadas || []).length) {
        adicionarBolhaTeste('(a IA decidiu não responder neste turno)', 'ia-acao');
      }
    } catch (e) {
      adicionarBolhaTeste('Erro ao falar com a IA: ' + e.message, 'ia-acao');
    } finally {
      testeInput.disabled = false;
      testeBtnEnviar.disabled = false;
      testeInput.focus();
    }
  }

  testeBtnEnviar.addEventListener('click', enviarMensagemTeste);
  testeInput.addEventListener('keydown', (e) => {
    if (e.key === 'Enter') enviarMensagemTeste();
  });

  // ================= AÇÕES (REGRAS) =================
  const GATILHOS = {
    janela_horario: { label: 'Mensagem recebida FORA do horário configurado', aval: 'deterministico' },
    tempo_desde_ultima_resposta: { label: 'Tempo desde a última resposta do cliente', aval: 'deterministico' },
    formato_mensagem: { label: 'Formato da mensagem do cliente', aval: 'deterministico' },
    contem_texto: { label: 'Mensagem contém um destes textos', aval: 'deterministico' },
    tempo_pausada_sem_resposta_vendedor: {
      label: 'IA pausada há muito tempo sem resposta do correspondente',
      aval: 'deterministico',
    },
    intencao_comprar: { label: 'Cliente quer comprar', aval: 'semantico' },
    intencao_suporte: { label: 'Cliente quer suporte', aval: 'semantico' },
    intencao_reembolso: { label: 'Cliente quer reembolso', aval: 'semantico' },
    intencao_humano: { label: 'Cliente quer falar com um humano', aval: 'semantico' },
    intencao_agendar: { label: 'Cliente quer agendar', aval: 'semantico' },
    intencao_ajuda: { label: 'Cliente quer ajuda', aval: 'semantico' },
    sentimento: { label: 'Sentimento do cliente', aval: 'semantico' },
    mencao_topico: { label: 'Cliente menciona um assunto', aval: 'semantico' },
  };

  const ACOES_POR_AVALIACAO = {
    deterministico: ['mensagem_fixa', 'transferir_humano', 'adicionar_tag', 'remover_tag', 'retomar_ia', 'nao_responder'],
    semantico: [
      'mensagem_fixa',
      'info_fixa',
      'focar_topico',
      'adicionar_tag',
      'remover_tag',
      'pedir_info',
      'preencher_campo_crm',
      'iniciar_automacao',
      'enviar_proposta',
      'transferir_humano',
      'nao_responder',
    ],
  };

  const LABEL_ACAO = {
    mensagem_fixa: 'Sempre usar esta mensagem',
    info_fixa: 'Sempre responder com esta informação',
    focar_topico: 'Sempre falar sobre este assunto',
    adicionar_tag: 'Adicionar tag',
    remover_tag: 'Remover tag',
    pedir_info: 'Pedir algo ao cliente',
    preencher_campo_crm: 'Preencher campo do CRM',
    iniciar_automacao: 'Iniciar automação',
    enviar_proposta: 'Enviar proposta dinâmica',
    transferir_humano: 'Transferir para humano',
    retomar_ia: 'Retomar a IA (voltar do modo pausado)',
    nao_responder: 'Não responder',
  };

  function montarOptionsGatilho(selecionado) {
    const dets = Object.entries(GATILHOS).filter(([, v]) => v.aval === 'deterministico');
    const sems = Object.entries(GATILHOS).filter(([, v]) => v.aval === 'semantico');
    const opt = (tipo, def) =>
      `<option value="${tipo}" ${tipo === selecionado ? 'selected' : ''}>${def.label}</option>`;
    return `
      <optgroup label="Determinístico (não gasta IA)">${dets.map(([t, d]) => opt(t, d)).join('')}</optgroup>
      <optgroup label="Semântico (a IA decide)">${sems.map(([t, d]) => opt(t, d)).join('')}</optgroup>
    `;
  }

  function montarOptionsAcao(avaliacao, selecionado) {
    const lista = ACOES_POR_AVALIACAO[avaliacao] || ACOES_POR_AVALIACAO.deterministico;
    return lista
      .map((tipo) => `<option value="${tipo}" ${tipo === selecionado ? 'selected' : ''}>${LABEL_ACAO[tipo]}</option>`)
      .join('');
  }

  function campoExtraGatilho(tipo, cfg) {
    cfg = cfg || {};
    if (tipo === 'janela_horario') {
      const dias = cfg.dias || ['seg', 'ter', 'qua', 'qui', 'sex'];
      return `
        <input type="time" class="ge-inicio" value="${cfg.inicio || '08:00'}" title="Início do horário" />
        <input type="time" class="ge-fim" value="${cfg.fim || '18:00'}" title="Fim do horário" />
      `;
    }
    if (tipo === 'tempo_desde_ultima_resposta' || tipo === 'tempo_pausada_sem_resposta_vendedor') {
      return `<input type="number" class="ge-horas" min="0" value="${cfg.horas ?? 24}" placeholder="Horas" />`;
    }
    if (tipo === 'formato_mensagem') {
      const opcoes = [
        ['chat', 'Texto'],
        ['image', 'Imagem'],
        ['ptt', 'Áudio'],
        ['video', 'Vídeo'],
        ['document', 'Documento'],
        ['sticker', 'Figurinha'],
      ];
      return `<select class="ge-tipo">${opcoes
        .map(([v, l]) => `<option value="${v}" ${cfg.tipo === v ? 'selected' : ''}>${l}</option>`)
        .join('')}</select>`;
    }
    if (tipo === 'contem_texto') {
      return `<input type="text" class="ge-palavras" value="${escaparAtributo((cfg.palavras || []).join(', '))}" placeholder="palavras separadas por vírgula" />`;
    }
    if (tipo === 'sentimento') {
      return `<input type="text" class="ge-sentimento" value="${escaparAtributo(cfg.sentimento || '')}" placeholder="Ex: irritado, frustrado" />`;
    }
    if (tipo === 'mencao_topico') {
      return `<input type="text" class="ge-topico" value="${escaparAtributo(cfg.topico || '')}" placeholder="Assunto" />`;
    }
    return '';
  }

  function campoExtraAcao(tipo, cfg) {
    cfg = cfg || {};
    if (['mensagem_fixa', 'info_fixa'].includes(tipo)) {
      return `<input type="text" class="ae-texto" value="${escaparAtributo(cfg.texto || '')}" placeholder="Texto" />`;
    }
    if (tipo === 'focar_topico') {
      return `<input type="text" class="ae-topico" value="${escaparAtributo(cfg.topico || '')}" placeholder="Assunto" />`;
    }
    if (tipo === 'pedir_info') {
      return `<input type="text" class="ae-texto" value="${escaparAtributo(cfg.texto || '')}" placeholder="O que pedir ao cliente" />`;
    }
    if (tipo === 'adicionar_tag' || tipo === 'remover_tag') {
      return `<select class="ae-tag">${dadosApoio.tags
        .map((t) => `<option value="${t.id}" ${cfg.tag_id == t.id ? 'selected' : ''}>${escaparHtml(t.nome || t.name)}</option>`)
        .join('')}</select>`;
    }
    if (tipo === 'preencher_campo_crm') {
      return `<select class="ae-campo">${dadosApoio.camposCrm
        .map((c) => `<option value="${escaparAtributo(c)}" ${cfg.campo === c ? 'selected' : ''}>${escaparHtml(c)}</option>`)
        .join('')}</select>`;
    }
    if (tipo === 'iniciar_automacao') {
      return `<select class="ae-automacao">${dadosApoio.automacoes
        .map((a) => `<option value="${a.id}" ${cfg.automacao_id == a.id ? 'selected' : ''}>${escaparHtml(a.nome)}</option>`)
        .join('')}</select>`;
    }
    return '';
  }

  function lerCampoExtraGatilho(card, tipo) {
    if (tipo === 'janela_horario')
      return {
        inicio: card.querySelector('.ge-inicio')?.value,
        fim: card.querySelector('.ge-fim')?.value,
      };
    if (tipo === 'tempo_desde_ultima_resposta' || tipo === 'tempo_pausada_sem_resposta_vendedor')
      return { horas: Number(card.querySelector('.ge-horas')?.value) || 0 };
    if (tipo === 'formato_mensagem') return { tipo: card.querySelector('.ge-tipo')?.value };
    if (tipo === 'contem_texto')
      return {
        palavras: (card.querySelector('.ge-palavras')?.value || '')
          .split(',')
          .map((p) => p.trim())
          .filter(Boolean),
      };
    if (tipo === 'sentimento') return { sentimento: card.querySelector('.ge-sentimento')?.value };
    if (tipo === 'mencao_topico') return { topico: card.querySelector('.ge-topico')?.value };
    return {};
  }

  function lerCampoExtraAcao(card, tipo) {
    if (['mensagem_fixa', 'info_fixa', 'pedir_info'].includes(tipo))
      return { texto: card.querySelector('.ae-texto')?.value };
    if (tipo === 'focar_topico') return { topico: card.querySelector('.ae-topico')?.value };
    if (tipo === 'adicionar_tag' || tipo === 'remover_tag')
      return { tag_id: Number(card.querySelector('.ae-tag')?.value) || null };
    if (tipo === 'preencher_campo_crm') return { campo: card.querySelector('.ae-campo')?.value };
    if (tipo === 'iniciar_automacao')
      return { automacao_id: Number(card.querySelector('.ae-automacao')?.value) || null };
    return {};
  }

  function renderizarRegras() {
    const container = document.getElementById('agia-regras-lista');
    container.innerHTML = '';
    regrasAtuais.forEach((regra, idx) => container.appendChild(criarCardRegra(regra, idx)));
  }

  function criarCardRegra(regra, idx) {
    const card = document.createElement('div');
    card.className = 'agia-regra-card';
    card.dataset.idx = idx;

    const avaliacaoAtual = GATILHOS[regra.gatilho_tipo]?.aval || 'deterministico';

    card.innerHTML = `
      <div class="agia-regra-linha">
        <div class="agia-campo">
          <label>Quando</label>
          <select class="rg-gatilho">${montarOptionsGatilho(regra.gatilho_tipo)}</select>
        </div>
        <div class="agia-campo">
          <label>Então</label>
          <select class="rg-acao">${montarOptionsAcao(avaliacaoAtual, regra.acao_tipo)}</select>
        </div>
      </div>
      <div class="agia-regra-extra rg-extra-gatilho">${campoExtraGatilho(regra.gatilho_tipo, regra.gatilho_config)}</div>
      <div class="agia-regra-extra rg-extra-acao">${campoExtraAcao(regra.acao_tipo, regra.acao_config)}</div>
      <div class="agia-regra-rodape">
        <label class="agia-checkbox-linha" style="font-size: 12px">
          <input type="checkbox" class="rg-ativo" ${regra.ativo !== false ? 'checked' : ''} /> Regra ativa
        </label>
        <div class="agia-regra-acoes-btns">
          <button type="button" class="botao botao-cinza agia-btn-sm agia-btn-excluir"><i class="ph-bold ph-trash"></i> Excluir</button>
          <button type="button" class="botao botao-amarelo agia-btn-sm agia-btn-salvar-regra"><i class="ph-bold ph-floppy-disk"></i> Salvar</button>
        </div>
      </div>
    `;

    inicializarDropdownsEm(card);

    const selectGatilho = card.querySelector('.rg-gatilho');
    const selectAcao = card.querySelector('.rg-acao');

    selectGatilho.addEventListener('change', () => {
      const novoTipo = selectGatilho.value;
      const extraGatilho = card.querySelector('.rg-extra-gatilho');
      extraGatilho.innerHTML = campoExtraGatilho(novoTipo, {});
      inicializarDropdownsEm(extraGatilho);

      const novaAval = GATILHOS[novoTipo]?.aval || 'deterministico';
      selectAcao.innerHTML = montarOptionsAcao(novaAval, null);
      selectAcao._agiaSync && selectAcao._agiaSync();

      const extraAcao = card.querySelector('.rg-extra-acao');
      extraAcao.innerHTML = campoExtraAcao(selectAcao.value, {});
      inicializarDropdownsEm(extraAcao);
    });

    selectAcao.addEventListener('change', () => {
      const extraAcao = card.querySelector('.rg-extra-acao');
      extraAcao.innerHTML = campoExtraAcao(selectAcao.value, {});
      inicializarDropdownsEm(extraAcao);
    });

    card.querySelector('.agia-btn-excluir').addEventListener('click', async () => {
      if (regra.id) {
        await fetch(`/api/agente-ia/regras/${regra.id}`, { method: 'DELETE', headers: headersAuth });
      }
      regrasAtuais.splice(idx, 1);
      renderizarRegras();
    });

    card.querySelector('.agia-btn-salvar-regra').addEventListener('click', async () => {
      const gatilhoTipo = selectGatilho.value;
      const acaoTipo = selectAcao.value;
      const payload = {
        tipo_avaliacao: GATILHOS[gatilhoTipo]?.aval || 'deterministico',
        gatilho_tipo: gatilhoTipo,
        gatilho_config: lerCampoExtraGatilho(card, gatilhoTipo),
        acao_tipo: acaoTipo,
        acao_config: lerCampoExtraAcao(card, acaoTipo),
        ordem: idx,
        ativo: card.querySelector('.rg-ativo').checked,
      };

      try {
        if (regra.id) {
          await fetch(`/api/agente-ia/regras/${regra.id}`, {
            method: 'PUT',
            headers: headersJson,
            body: JSON.stringify(payload),
          });
          Object.assign(regra, payload);
        } else {
          const res = await fetch(`/api/agente-ia/${funilId}/regras`, {
            method: 'POST',
            headers: headersJson,
            body: JSON.stringify(payload),
          });
          const json = await res.json();
          regrasAtuais[idx] = json.regra;
        }
        window.mostrarAlerta && window.mostrarAlerta('Regra salva!', 'success');
      } catch (e) {
        window.mostrarAlerta && window.mostrarAlerta('Erro ao salvar regra.', 'error');
      }
    });

    return card;
  }

  document.getElementById('agia-add-regra').addEventListener('click', () => {
    regrasAtuais.push({
      tipo_avaliacao: 'deterministico',
      gatilho_tipo: 'contem_texto',
      gatilho_config: {},
      acao_tipo: 'mensagem_fixa',
      acao_config: {},
      ativo: true,
    });
    renderizarRegras();
  });

  // ================= FONTE =================
  function renderizarFontes(fontes) {
    const container = document.getElementById('agia-fontes-lista');
    container.innerHTML = '';
    fontes.forEach((f) => {
      const item = document.createElement('div');
      item.className = 'agia-fonte-item';
      const kb = f.tamanho_bytes ? `${(f.tamanho_bytes / 1024).toFixed(1)} KB` : '';
      item.innerHTML = `
        <i class="ph-bold ph-file-text"></i>
        <span class="nome">${escaparHtml(f.nome_arquivo)}</span>
        <span class="tam">${kb}</span>
        <button title="Remover"><i class="ph-bold ph-trash"></i></button>
      `;
      item.querySelector('button').addEventListener('click', async () => {
        await fetch(`/api/agente-ia/fontes/${f.id}`, { method: 'DELETE', headers: headersAuth });
        item.remove();
      });
      container.appendChild(item);
    });
  }

  async function subirFonte(file) {
    if (!file) return;
    const formData = new FormData();
    formData.append('arquivo', file);
    try {
      const res = await fetch(`/api/agente-ia/${funilId}/fontes`, {
        method: 'POST',
        headers: headersAuth,
        body: formData,
      });
      const json = await res.json();
      if (!json.success) throw new Error(json.message || 'Falha ao enviar arquivo.');
      const container = document.getElementById('agia-fontes-lista');
      const atuais = Array.from(container.children).length;
      renderizarFontes([{ ...json.fonte }]);
      // Recarrega a lista inteira pra manter ordem/estado consistentes
      carregarAgente();
    } catch (e) {
      window.mostrarAlerta && window.mostrarAlerta(e.message, 'error');
    }
  }

  const uploadArea = document.getElementById('agia-upload-area');
  const inputFonte = document.getElementById('agia-input-fonte');
  uploadArea.addEventListener('click', () => inputFonte.click());
  inputFonte.addEventListener('change', () => subirFonte(inputFonte.files[0]));
  uploadArea.addEventListener('dragover', (e) => {
    e.preventDefault();
    uploadArea.classList.add('arrastando');
  });
  uploadArea.addEventListener('dragleave', () => uploadArea.classList.remove('arrastando'));
  uploadArea.addEventListener('drop', (e) => {
    e.preventDefault();
    uploadArea.classList.remove('arrastando');
    if (e.dataTransfer.files[0]) subirFonte(e.dataTransfer.files[0]);
  });

  // ================= INTEGRAÇÕES =================
  let integracoesCarregadas = false;
  let agenteExternoCarregado = false;

  async function carregarIntegracoes() {
    if (integracoesCarregadas) return;
    try {
      const res = await fetch(`/api/agente-ia/${funilId}/integracoes`, { headers: headersAuth });
      const json = await res.json();
      if (!json.success) return;
      integracoesCarregadas = true;
      renderizarIntegracoes(json.integracoes || []);
    } catch (e) {
      window.mostrarAlerta && window.mostrarAlerta('Erro ao carregar integrações.', 'error');
    }
  }

  function renderizarIntegracoes(lista) {
    const container = document.getElementById('agia-integracoes-lista');
    container.innerHTML = '';
    if (lista.length === 0) {
      container.innerHTML =
        '<p class="agia-subtitulo" style="margin:0">Nenhuma integração cadastrada ainda.</p>';
      return;
    }
    lista.forEach((integ) => container.appendChild(criarCardIntegracao(integ)));
  }

  function criarCardIntegracao(integ) {
    const cfg = integ.config || {};
    const card = document.createElement('div');
    card.className = 'agia-integracao-card' + (integ.ativo ? '' : ' inativa');
    card.innerHTML = `
      <div class="agia-integracao-topo">
        <span class="agia-integracao-nome">${escaparHtml(cfg.nome || '')}</span>
        <span class="agia-integracao-badge ${integ.ativo ? '' : 'off'}">${integ.ativo ? 'Ativa' : 'Inativa'}</span>
      </div>
      <div class="agia-integracao-url">${escaparHtml(cfg.metodo || 'POST')} ${escaparHtml(cfg.url || '')}</div>
      ${cfg.descricao ? `<div class="agia-integracao-descricao">${escaparHtml(cfg.descricao)}</div>` : ''}
      <div class="agia-integracao-btns">
        <button type="button" class="botao botao-cinza agia-btn-sm btn-toggle">${integ.ativo ? 'Desativar' : 'Ativar'}</button>
        <button type="button" class="botao botao-cinza agia-btn-sm btn-editar"><i class="ph-bold ph-pencil-simple"></i> Editar</button>
        <button type="button" class="botao botao-cinza agia-btn-sm btn-testar"><i class="ph-bold ph-play"></i> Testar</button>
        <button type="button" class="botao botao-cinza agia-btn-sm btn-excluir"><i class="ph-bold ph-trash"></i></button>
      </div>
      <div class="agia-integracao-resultado hidden"></div>
    `;

    card.querySelector('.btn-toggle').addEventListener('click', async () => {
      await fetch(`/api/agente-ia/integracoes/${integ.id}`, {
        method: 'PUT',
        headers: headersJson,
        body: JSON.stringify({ ...cfg, ativo: !integ.ativo }),
      });
      integracoesCarregadas = false;
      carregarIntegracoes();
    });

    card.querySelector('.btn-editar').addEventListener('click', () => {
      document.getElementById('agia-integracao-editando-id').value = integ.id;
      document.getElementById('agia-integracao-nome').value = cfg.nome || '';
      document.getElementById('agia-integracao-metodo').value = cfg.metodo || 'POST';
      document.getElementById('agia-integracao-metodo')._agiaSync &&
        document.getElementById('agia-integracao-metodo')._agiaSync();
      document.getElementById('agia-integracao-url').value = cfg.url || '';
      document.getElementById('agia-integracao-descricao').value = cfg.descricao || '';
      document.getElementById('agia-integracao-ativo').checked = !!integ.ativo;
      document.getElementById('agia-integracao-form-titulo').textContent = 'Editando integração';
      document.getElementById('agia-integracao-cancelar').classList.remove('hidden');
    });

    card.querySelector('.btn-excluir').addEventListener('click', async () => {
      await fetch(`/api/agente-ia/integracoes/${integ.id}`, { method: 'DELETE', headers: headersAuth });
      integracoesCarregadas = false;
      carregarIntegracoes();
    });

    card.querySelector('.btn-testar').addEventListener('click', async (e) => {
      const btn = e.currentTarget;
      const resultadoEl = card.querySelector('.agia-integracao-resultado');
      btn.disabled = true;
      resultadoEl.classList.remove('hidden', 'erro');
      resultadoEl.textContent = 'Testando...';
      try {
        const res = await fetch(`/api/agente-ia/integracoes/${integ.id}/testar`, {
          method: 'POST',
          headers: headersJson,
          body: JSON.stringify({ dados: { teste: true, origem: 'Agente de IA (teste manual)' } }),
        });
        const json = await res.json();
        if (json.success) {
          resultadoEl.textContent = `HTTP ${json.status} — ${json.resposta}`;
        } else {
          resultadoEl.classList.add('erro');
          resultadoEl.textContent = json.message || 'Falha no teste.';
        }
      } catch (err) {
        resultadoEl.classList.add('erro');
        resultadoEl.textContent = 'Erro de conexão ao testar.';
      } finally {
        btn.disabled = false;
      }
    });

    return card;
  }

  function limparFormIntegracao() {
    document.getElementById('agia-integracao-editando-id').value = '';
    document.getElementById('agia-integracao-nome').value = '';
    document.getElementById('agia-integracao-metodo').value = 'POST';
    document.getElementById('agia-integracao-metodo')._agiaSync &&
      document.getElementById('agia-integracao-metodo')._agiaSync();
    document.getElementById('agia-integracao-url').value = '';
    document.getElementById('agia-integracao-descricao').value = '';
    document.getElementById('agia-integracao-ativo').checked = false;
    document.getElementById('agia-integracao-form-titulo').textContent = 'Nova integração';
    document.getElementById('agia-integracao-cancelar').classList.add('hidden');
  }

  document.getElementById('agia-integracao-cancelar').addEventListener('click', limparFormIntegracao);

  document.getElementById('agia-integracao-salvar').addEventListener('click', async () => {
    const id = document.getElementById('agia-integracao-editando-id').value;
    const payload = {
      nome: document.getElementById('agia-integracao-nome').value.trim(),
      metodo: document.getElementById('agia-integracao-metodo').value,
      url: document.getElementById('agia-integracao-url').value.trim(),
      descricao: document.getElementById('agia-integracao-descricao').value.trim(),
      ativo: document.getElementById('agia-integracao-ativo').checked,
    };
    if (!payload.nome || !payload.url) {
      window.mostrarAlerta && window.mostrarAlerta('Nome e URL são obrigatórios.', 'error');
      return;
    }
    try {
      const res = await fetch(
        id ? `/api/agente-ia/integracoes/${id}` : `/api/agente-ia/${funilId}/integracoes`,
        {
          method: id ? 'PUT' : 'POST',
          headers: headersJson,
          body: JSON.stringify(payload),
        }
      );
      const json = await res.json();
      if (!json.success) {
        window.mostrarAlerta && window.mostrarAlerta(json.message || 'Erro ao salvar.', 'error');
        return;
      }
      window.mostrarAlerta && window.mostrarAlerta('Integração salva!', 'success');
      limparFormIntegracao();
      integracoesCarregadas = false;
      carregarIntegracoes();
    } catch (e) {
      window.mostrarAlerta && window.mostrarAlerta('Erro ao salvar integração.', 'error');
    }
  });

  // ================= AGENTE EXTERNO =================
  async function preencherUsuariosDoAgente(selecionado) {
    const select = document.getElementById('agext-usuario');
    try {
      const res = await fetch('/api/team/vendedores', { headers: headersAuth });
      const usuarios = await res.json();

      select.innerHTML = '<option value="">— selecione —</option>';
      (Array.isArray(usuarios) ? usuarios : []).forEach((u) => {
        const op = document.createElement('option');
        op.value = u.id;
        op.textContent = `${u.name || u.username} (${u.username})`;
        select.appendChild(op);
      });
      select.value = selecionado ? String(selecionado) : '';
    } catch (e) {
      // Sem a lista o operador ainda enxerga o resto da aba; só não consegue
      // trocar o usuário. Melhor que a aba inteira não abrir.
      select.innerHTML = '<option value="">— não consegui carregar os usuários —</option>';
    }
  }

  // O agente externo e a IA interna são dois motores independentes na mesma
  // tela, e o liga/desliga grande do topo é só o da IA interna. Sem este aviso
  // o topo lê "Desligado" com o externo respondendo clientes a todo vapor — foi
  // exatamente essa leitura que escondeu o incidente de 14/08 a 17/08.
  function sincronizarSeloAgenteExterno(ativo) {
    document.getElementById('agext-selo').classList.toggle('hidden', !ativo);
    document.getElementById('agext-aviso-topo').classList.toggle('hidden', !ativo);
  }

  // Empresa sem o recurso "Agente externo" (aba Empresas) ou pessoa sem a
  // permissão: a aba nem aparece.
  const podeAgenteExterno = Acesso.pode('agente_externo.configurar');
  if (!podeAgenteExterno) {
    const aba = document.querySelector('.agia-tab-btn[data-tab="externo"]');
    if (aba) aba.style.display = 'none';
  }

  async function carregarAgenteExterno() {
    if (agenteExternoCarregado || !podeAgenteExterno) return;
    try {
      const res = await fetch(`/api/agente-ia/${funilId}/agente-externo`, { headers: headersAuth });
      const json = await res.json();
      if (!json.success) throw new Error(json.message || 'Falha ao carregar.');
      agenteExternoCarregado = true;
      const cfg = json.config;
      sincronizarSeloAgenteExterno(!!(cfg && cfg.ativo));
      document.getElementById('agext-ativo').checked = !!(cfg && cfg.ativo);
      document.getElementById('agext-nome').value = cfg?.nome || '';
      document.getElementById('agext-url').value = cfg?.webhook_base_url || '';
      // O endereço vem do servidor (AGENTE_EXTERNO_MIDIA_BASE_URL) e não do
      // window.location: esta tela é aberta em localhost, e mandar o parceiro
      // chamar "localhost:3000" o mandava pra máquina dele mesmo.
      const baseEndpoint = cfg?.endpoint_base;
      const elEndpoint = document.getElementById('agext-endpoint-base');
      elEndpoint.textContent = baseEndpoint
        ? `${baseEndpoint}/api/agente-externo/v1`
        : 'Configure AGENTE_EXTERNO_MIDIA_BASE_URL no .env do servidor para exibir o endereço.';
      elEndpoint.classList.toggle('agia-endpoint-pendente', !baseEndpoint);

      // A lista de usuários vem depois da config, e a seleção é aplicada aqui:
      // preencher o select antes de saber quem está salvo deixaria o campo
      // vazio até alguém mexer, e um salvar sem querer apagaria o vínculo.
      await preencherUsuariosDoAgente(cfg?.usuario_id ?? null);
    } catch (e) {
      window.mostrarAlerta && window.mostrarAlerta('Erro ao carregar o agente externo.', 'error');
    }
  }

  document.getElementById('agext-salvar').addEventListener('click', async () => {
    const payload = {
      nome: document.getElementById('agext-nome').value.trim(),
      webhook_base_url: document.getElementById('agext-url').value.trim(),
      ativo: document.getElementById('agext-ativo').checked,
      usuario_id: document.getElementById('agext-usuario').value || null,
    };
    if (payload.ativo && !payload.webhook_base_url) {
      window.mostrarAlerta && window.mostrarAlerta('Informe a URL do webhook antes de ativar.', 'error');
      return;
    }
    if (payload.ativo && !payload.usuario_id) {
      window.mostrarAlerta && window.mostrarAlerta('Escolha o usuário do agente antes de ativar.', 'error');
      return;
    }
    try {
      const res = await fetch(`/api/agente-ia/${funilId}/agente-externo`, {
        method: 'PUT',
        headers: headersJson,
        body: JSON.stringify(payload),
      });
      const json = await res.json();
      if (!json.success) {
        window.mostrarAlerta && window.mostrarAlerta(json.message || 'Erro ao salvar.', 'error');
        return;
      }
      sincronizarSeloAgenteExterno(!!payload.ativo);
      window.mostrarAlerta && window.mostrarAlerta('Agente Externo salvo!', 'success');
    } catch (e) {
      window.mostrarAlerta && window.mostrarAlerta('Erro ao salvar o agente externo.', 'error');
    }
  });

  // Não há mais botão de gerar chave: a credencial do agente é o usuário e a
  // senha dele, geridos na tela de Equipe como os de qualquer conta.

  // ================= PAINEL (ESTATÍSTICAS) =================
  const LABEL_FERRAMENTA = {
    mover_card: 'Mover etapa do funil',
    adicionar_tag: 'Adicionar tag',
    remover_tag: 'Remover tag',
    atualizar_campo_crm: 'Atualizar campo do CRM',
    gerar_e_enviar_proposta: 'Gerar e enviar proposta',
    transferir_para_humano: 'Transferir para humano',
    transferir_humano: 'Transferir para humano',
    nao_responder: 'Não responder',
    iniciar_automacao: 'Iniciar automação',
    enviar_mensagem_pronta: 'Enviar mensagem pronta',
    marcar_status_lead: 'Marcar status do lead',
    atribuir_vendedor: 'Atribuir correspondente',
    notificar_equipe: 'Notificar equipe',
    registrar_comentario_interno: 'Registrar comentário interno',
    mensagem_fixa: 'Mensagem fixa (regra)',
    retomar_ia: 'Retomar a IA (regra)',
  };
  const LABEL_MOTIVO = {
    erro_ia: 'Erro ao chamar a IA',
    transferido_humano: 'Transferido para humano',
  };

  let statsCarregadas = false;
  async function carregarEstatisticas() {
    if (statsCarregadas) return;
    try {
      const res = await fetch(`/api/agente-ia/${funilId}/estatisticas`, { headers: headersAuth });
      const json = await res.json();
      if (!json.success) return;
      statsCarregadas = true;

      document.getElementById('agia-stat-respostas').textContent = json.respostas_enviadas;
      document.getElementById('agia-stat-clientes').textContent = json.clientes_unicos;
      const totalTokens = (json.tokens?.prompt || 0) + (json.tokens?.completion || 0);
      document.getElementById('agia-stat-tokens').textContent = totalTokens.toLocaleString('pt-BR');

      const listaFerramentas = document.getElementById('agia-painel-ferramentas');
      listaFerramentas.innerHTML = (json.ferramentas || [])
        .map(
          (f) =>
            `<div class="agia-painel-item"><span>${escaparHtml(LABEL_FERRAMENTA[f.ferramenta] || f.ferramenta)}</span><span class="qtd">${f.total}</span></div>`
        )
        .join('');

      const listaMotivos = document.getElementById('agia-painel-motivos');
      listaMotivos.innerHTML = (json.motivos_nao_respondida || [])
        .map(
          (m) =>
            `<div class="agia-painel-item"><span>${escaparHtml(LABEL_MOTIVO[m.motivo] || m.motivo || 'Outro')}</span><span class="qtd">${m.total}</span></div>`
        )
        .join('');
    } catch (e) {
      window.mostrarAlerta && window.mostrarAlerta('Erro ao carregar estatísticas.', 'error');
    }
  }

  // ================= CONFIGURAÇÕES =================
  function preencherConfig(cfg) {
    document.getElementById('agia-limite-mensagens').value = cfg.limite_mensagens_contexto ?? 20;
    document.getElementById('agia-limite-creditos').value = cfg.limite_creditos_periodo ?? 500;
    document.getElementById('agia-periodo-limite').value = cfg.periodo_limite || 'mensal';
    document.getElementById('agia-acao-limite').value = cfg.acao_ao_atingir_limite || 'pausar_ia';
    document.getElementById('agia-debounce').value = cfg.debounce_seg ?? 8;
    document.getElementById('agia-confirmar-fechamento').checked = !!cfg.confirmar_fechamento;
  }

  document.getElementById('agia-salvar-config').addEventListener('click', async () => {
    const config_geral = {
      limite_mensagens_contexto: Number(document.getElementById('agia-limite-mensagens').value) || 20,
      limite_creditos_periodo: Number(document.getElementById('agia-limite-creditos').value) || 0,
      periodo_limite: document.getElementById('agia-periodo-limite').value,
      acao_ao_atingir_limite: document.getElementById('agia-acao-limite').value,
      debounce_seg: Number(document.getElementById('agia-debounce').value) || 8,
      confirmar_fechamento: document.getElementById('agia-confirmar-fechamento').checked,
    };
    try {
      await fetch(`/api/agente-ia/${funilId}/config-geral`, {
        method: 'PUT',
        headers: headersJson,
        body: JSON.stringify({ config_geral }),
      });
      if (agenteAtual) agenteAtual.config_geral = config_geral;
      window.mostrarAlerta && window.mostrarAlerta('Configurações salvas!', 'success');
    } catch (e) {
      window.mostrarAlerta && window.mostrarAlerta('Erro ao salvar configurações.', 'error');
    }
  });

  // ================= REOFERTA (tarefa proativa) =================
  const DIAS_REOFERTA = [
    ['seg', 'Seg'], ['ter', 'Ter'], ['qua', 'Qua'], ['qui', 'Qui'], ['sex', 'Sex'], ['sab', 'Sáb'], ['dom', 'Dom'],
  ];
  let reofertaConfig = null;
  const lista = (id) =>
    document.getElementById(id).value.split(',').map((s) => s.trim()).filter(Boolean);

  function atualizarSeloReoferta(ativo) {
    document.getElementById('agia-reoferta-ativo-label').textContent = ativo ? 'Ligada' : 'Desligada';
    document.getElementById('agia-reoferta-selo').classList.toggle('hidden', !ativo);
  }

  async function carregarReoferta() {
    try {
      const res = await fetch(`/api/agente-ia/${funilId}/reoferta`, { headers: headersAuth });
      const json = await res.json();
      if (!json.success) throw new Error(json.message);
      const c = (reofertaConfig = json.config);
      document.getElementById('agia-reoferta-ativo').checked = !!json.ativo;
      atualizarSeloReoferta(!!json.ativo);
      document.getElementById('agia-reoferta-inspiracao').value = c.inspiracao || '';
      document.getElementById('agia-reoferta-dias').value = c.dias_parado;
      document.getElementById('agia-reoferta-minimo').value = c.valor_minimo;
      document.getElementById('agia-reoferta-max-chip').value = c.max_por_chip_dia;
      document.getElementById('agia-reoferta-max-total').value = c.max_total_dia;
      document.getElementById('agia-reoferta-int-min').value = c.intervalo_chip_min[0];
      document.getElementById('agia-reoferta-int-max').value = c.intervalo_chip_min[1];
      document.getElementById('agia-reoferta-geral-min').value = c.intervalo_geral_min[0];
      document.getElementById('agia-reoferta-geral-max').value = c.intervalo_geral_min[1];
      document.getElementById('agia-reoferta-inicio').value = c.inicio;
      document.getElementById('agia-reoferta-fim').value = c.fim;
      document.getElementById('agia-reoferta-tags-contam').value = (c.tags_contam || []).join(', ');
      document.getElementById('agia-reoferta-tags-excluem').value = (c.tags_excluem || []).join(', ');
      document.getElementById('agia-reoferta-prefixos').value = (c.prefixos_proibidos || []).join(', ');
      document.getElementById('agia-reoferta-dias-semana').innerHTML = DIAS_REOFERTA.map(
        ([v, n]) =>
          `<label class="agia-checkbox-linha"><input type="checkbox" value="${v}" ${(c.dias_semana || []).includes(v) ? 'checked' : ''} /> ${n}</label>`
      ).join('');
      const sel = document.getElementById('agia-reoferta-etapa');
      sel.innerHTML =
        '<option value="">Primeira etapa do funil</option>' +
        (json.etapas || [])
          .map((e) => `<option value="${e.id}" ${String(c.etapa_destino_id) === String(e.id) ? 'selected' : ''}>${escaparHtml(e.nome)}</option>`)
          .join('');
      const s = json.estatisticas || {};
      document.getElementById('agia-reoferta-numeros').innerHTML =
        `<strong>${s.enviados_hoje || 0}</strong> enviadas hoje · <strong>${s.enviados_total || 0}</strong> no total · ` +
        `${s.descartados || 0} descartados pela IA · ${s.sem_valor || 0} sem valor · ` +
        `${s.aguardando_chip || 0} esperando chip · ${s.chip_indisponivel || 0} sem chip · ${s.erros || 0} erros`;
    } catch (e) {
      window.mostrarAlerta && window.mostrarAlerta('Erro ao carregar a reoferta.', 'error');
    }
  }

  document.getElementById('agia-reoferta-ativo').addEventListener('change', (e) => atualizarSeloReoferta(e.target.checked));

  document.getElementById('agia-reoferta-salvar').addEventListener('click', async () => {
    const num = (id) => Number(document.getElementById(id).value);
    const config = {
      ...(reofertaConfig || {}),
      inspiracao: document.getElementById('agia-reoferta-inspiracao').value.trim(),
      dias_parado: num('agia-reoferta-dias'),
      valor_minimo: num('agia-reoferta-minimo'),
      max_por_chip_dia: num('agia-reoferta-max-chip'),
      max_total_dia: num('agia-reoferta-max-total'),
      intervalo_chip_min: [num('agia-reoferta-int-min'), num('agia-reoferta-int-max')],
      intervalo_geral_min: [num('agia-reoferta-geral-min'), num('agia-reoferta-geral-max')],
      inicio: document.getElementById('agia-reoferta-inicio').value,
      fim: document.getElementById('agia-reoferta-fim').value,
      dias_semana: [...document.querySelectorAll('#agia-reoferta-dias-semana input:checked')].map((i) => i.value),
      etapa_destino_id: document.getElementById('agia-reoferta-etapa').value || null,
      tags_contam: lista('agia-reoferta-tags-contam'),
      tags_excluem: lista('agia-reoferta-tags-excluem'),
      prefixos_proibidos: lista('agia-reoferta-prefixos'),
    };
    try {
      const res = await fetch(`/api/agente-ia/${funilId}/reoferta`, {
        method: 'PUT',
        headers: headersJson,
        body: JSON.stringify({ ativo: document.getElementById('agia-reoferta-ativo').checked, config }),
      });
      const json = await res.json();
      if (!json.success) throw new Error(json.message);
      window.mostrarAlerta && window.mostrarAlerta('Reoferta salva!', 'success');
      carregarReoferta();
    } catch (e) {
      window.mostrarAlerta && window.mostrarAlerta('Erro ao salvar a reoferta.', 'error');
    }
  });

  // ================= UTIL =================
  function escaparHtml(texto) {
    const div = document.createElement('div');
    div.innerText = texto ?? '';
    return div.innerHTML;
  }

  function escaparAtributo(texto) {
    return escaparHtml(texto).replace(/"/g, '&quot;');
  }

  document.getElementById('agext-ir-para-aba').addEventListener('click', () => {
    document.querySelector('.agia-tab-btn[data-tab="externo"]').click();
  });

  // O agente externo é carregado já na abertura, e não só quando alguém clica na
  // aba: é o que o aviso do topo precisa para ser verdade desde o primeiro
  // segundo. Quem nunca abriu a aba é justamente quem não sabe que ele existe.
  carregarDadosApoio()
    .then(carregarAgente)
    .then(carregarAgenteExterno);
});
