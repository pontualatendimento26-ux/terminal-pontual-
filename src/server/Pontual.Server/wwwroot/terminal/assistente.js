// Assistente Pontual — a conversa da atendente com a IA.
// JavaScript puro. Fala só com o servidor da Pontual (/api/v1/device/assistant); a chave da IA fica lá.
// Nada que vem do servidor ou da IA entra na página como HTML: sempre textContent.
'use strict';

(() => {
  const P = window.PONTUAL || {};
  const ponte = P.hosted && window.chrome && window.chrome.webview ? window.chrome.webview : null;
  const $ = (id) => document.getElementById(id);
  const falas = $('as-falas'), corpo = $('as-corpo'), campo = $('as-texto'), botao = $('as-enviar');

  let ligada = false;       // o servidor confirmou que a assistente vale para esta máquina
  let ocupada = false;      // há uma resposta chegando
  let novaConversa = false; // a próxima pergunta abre uma conversa nova
  let ultimaConversa = null;
  let atendente = '';
  const avaliadas = new Map(); // id da resposta → true (ajudou) ou false (não ajudou)

  // A assistente pode ver o que está na tela (a aba em uso e a conversa do WhatsApp) na hora da pergunta.
  // Precisa de três coisas: o programa do Terminal saber entregar a tela (P.screen), a Central permitir
  // (o servidor diz ao carregar) e a atendente não ter desligado o interruptor acima do campo.
  const CHAVE_TELA = 'pontual.assistente.tela';
  const sabeVerTela = Boolean(ponte && P.screen);
  let telaPermitida = false;
  let aprendeComWhatsApp = false; // a Central registra as conversas do WhatsApp para a análise
  let telaLigada = true;
  try { telaLigada = sessionStorage.getItem(CHAVE_TELA) !== '0'; } catch { /* sem armazenamento: começa ligada */ }
  const pedidosDeTela = new Map();
  let numeroDoPedido = 0;

  function pintarTela() {
    const pode = sabeVerTela && telaPermitida && ligada, botao = $('as-tela');
    botao.hidden = !pode;
    botao.setAttribute('aria-checked', String(telaLigada));
    botao.textContent = telaLigada ? 'Vendo a sua tela e o WhatsApp' : 'Sem ver a sua tela';
    botao.title = telaLigada ? 'Clique para a assistente não ver a tela nas próximas perguntas.' : 'Clique para a assistente ver a tela e o WhatsApp quando você perguntar.';
    $('as-nota').textContent = pode && telaLigada
      ? 'Ao perguntar, a assistente vê a aba aberta e a conversa do WhatsApp. O que você escreve fica registrado; a tela não é guardada.'
      : 'Não informe CPF nem dados de clientes. As conversas ficam registradas pela Pontual.';
    if (aprendeComWhatsApp) $('as-nota').textContent += ' Suas conversas no WhatsApp são lidas pela assistente para aprender.';
  }
  $('as-tela').onclick = () => {
    telaLigada = !telaLigada;
    try { sessionStorage.setItem(CHAVE_TELA, telaLigada ? '1' : '0'); } catch { /* vale só até fechar */ }
    pintarTela();
    campo.focus();
  };

  // Pede ao programa do Terminal a tela (se permitida) e a lista do que ele pode abrir. Se ele não
  // responder a tempo, a pergunta segue sem isso.
  const sabeAbrir = Boolean(ponte && P.actions);
  function pedirTela() {
    const querTela = sabeVerTela && telaPermitida && telaLigada;
    if (!querTela && !sabeAbrir) return Promise.resolve({ screen: null, apps: null });
    return new Promise((resolver) => {
      const id = String(++numeroDoPedido);
      const prazo = setTimeout(() => { pedidosDeTela.delete(id); resolver({ screen: null, apps: null }); }, 8000);
      pedidosDeTela.set(id, (m) => { clearTimeout(prazo); resolver(m); });
      ponte.postMessage({ type: 'assistant-context', id, screen: querTela });
    });
  }
  const pedidosDeAcao = new Map();
  if (ponte) {
    ponte.addEventListener('message', (e) => {
      const m = e.data;
      if (m && m.type === 'assistant-action-result' && pedidosDeAcao.has(m.id)) {
        const nota = pedidosDeAcao.get(m.id); pedidosDeAcao.delete(m.id);
        nota.textContent = typeof m.message === 'string' ? m.message : (m.ok ? 'Pronto.' : 'Não consegui abrir.');
        nota.classList.toggle('falha', !m.ok);
        return;
      }
      if (!m || m.type !== 'assistant-context-result' || !pedidosDeTela.has(m.id)) return;
      const entregar = pedidosDeTela.get(m.id);
      pedidosDeTela.delete(m.id);
      entregar({
        screen: m.screen && typeof m.screen === 'object' ? m.screen : null,
        apps: Array.isArray(m.apps) ? m.apps : null,
      });
    });
  }

  // A IA pede para abrir algo escrevendo, na última linha, [[abrir: Nome]] ou [[pesquisar: texto]].
  // Essa linha não aparece na conversa; vai para o Terminal, que só abre o que está liberado nele.
  const ACAO = /\[\[\s*(abrir|pesquisar)\s*:\s*([^\]\n]{1,200})\]\]/i;
  function semAcao(texto) {
    return String(texto).replace(/\[\[\s*(abrir|pesquisar)\s*:[^\]\n]*\]\]/gi, '').replace(/\[\[[^\]\n]*$/, '').trimEnd();
  }
  function executar(resposta, texto) {
    const achada = sabeAbrir ? ACAO.exec(texto) : null;
    if (!achada) return;
    const id = String(++numeroDoPedido);
    const nota = el('div', 'as-viu as-acao', achada[1].toLowerCase() === 'abrir' ? `Abrindo ${achada[2].trim()}…` : `Pesquisando no Google: ${achada[2].trim()}…`);
    resposta.after(nota);
    pedidosDeAcao.set(id, nota);
    setTimeout(() => { if (pedidosDeAcao.delete(id)) { nota.textContent = 'O Terminal não respondeu.'; nota.classList.add('falha'); } }, 10000);
    ponte.postMessage({ type: 'assistant-action', id, action: achada[1].toLowerCase() === 'abrir' ? 'open' : 'search', value: achada[2].trim() });
  }
  function notaDaTela(alvo, contexto) {
    if (contexto) alvo.after(el('div', 'as-viu', `Com a tela: ${contexto}`));
  }

  // --------------------------------------------------------------- desenho
  function el(tag, classe, texto) {
    const n = document.createElement(tag);
    if (classe) n.className = classe;
    if (texto !== undefined) n.textContent = texto;
    return n;
  }
  // Só **negrito** é interpretado; o resto aparece como veio (as quebras de linha valem pelo CSS).
  function formatar(alvo, texto) {
    alvo.replaceChildren();
    const partes = semAcao(texto).replace(/^#{1,6}\s+/gm, '').split(/\*\*([^*\n]+)\*\*/);
    partes.forEach((parte, i) => { if (parte) alvo.append(i % 2 ? el('strong', '', parte) : parte); });
  }
  function noFim() { return corpo.scrollHeight - corpo.scrollTop - corpo.clientHeight < 80; }
  function descer() { corpo.scrollTop = corpo.scrollHeight; }
  function estado(titulo, texto) {
    const caixa = $('as-estado');
    caixa.replaceChildren(el('div'));
    if (titulo) caixa.firstChild.append(el('h2', '', titulo));
    caixa.firstChild.append(el('p', '', texto));
    caixa.hidden = false; falas.hidden = true;
  }
  function mostrarFalas() { $('as-estado').hidden = true; falas.hidden = false; }
  function divisa(texto) { falas.append(el('div', 'as-divisa', texto)); }
  function fala(papel, texto, id, contexto) {
    const n = el('div', `as-fala ${papel === 'user' ? 'eu' : 'ia'}`);
    if (papel === 'user') n.textContent = texto; else formatar(n, texto);
    falas.append(n);
    if (papel === 'user') notaDaTela(n, contexto);
    if (papel !== 'user' && id) falas.append(avaliacao(id));
    return n;
  }

  // Abaixo de cada resposta: "Ajudou" ou "Não ajudou". A correção escrita vai para a supervisão revisar;
  // a assistente só passa a usá-la depois de aprovada.
  function avaliacao(id) {
    const barra = el('div', 'as-avaliar');
    const botao = (rotulo, classe) => { const b = el('button', classe || '', rotulo); b.type = 'button'; return b; };
    const pintar = () => {
      barra.replaceChildren();
      if (avaliadas.has(id)) { barra.append(el('span', '', avaliadas.get(id) ? 'Você marcou: ajudou' : 'Você marcou: não ajudou. A supervisão vai revisar.')); return; }
      const sim = botao('Ajudou'), nao = botao('Não ajudou');
      sim.onclick = () => enviarAvaliacao(id, true, '', pintar, [sim, nao]);
      nao.onclick = corrigir;
      barra.append(sim, nao);
    };
    function corrigir() {
      const texto = el('textarea');
      texto.rows = 2; texto.maxLength = 2000;
      texto.placeholder = 'O que está errado, ou qual é o certo? (opcional)';
      texto.setAttribute('aria-label', 'O que está errado na resposta, ou qual é o certo');
      const enviar = botao('Enviar', 'forte'), voltar = botao('Cancelar');
      enviar.onclick = () => enviarAvaliacao(id, false, texto.value.trim(), pintar, [enviar, voltar]);
      voltar.onclick = pintar;
      const linha = el('div', 'as-avaliar-linha'); linha.append(enviar, voltar);
      barra.replaceChildren(texto, linha);
      texto.focus();
    }
    pintar();
    return barra;
  }
  async function enviarAvaliacao(id, ajudou, texto, pintar, botoes) {
    botoes.forEach((b) => { b.disabled = true; });
    try {
      const r = await fetch('/api/v1/device/assistant/feedback', { method: 'POST', headers: cabecalhos(true), body: JSON.stringify({ messageId: id, helpful: ajudou, text: texto }) });
      if (!r.ok) throw new Error(await mensagemDeErro(r));
      avaliadas.set(id, ajudou);
      erro('');
      pintar();
    } catch (e) {
      botoes.forEach((b) => { b.disabled = false; });
      erro(e instanceof TypeError ? 'Sem conexão com o servidor. A avaliação não foi enviada.' : e.message);
    }
  }
  function dia(iso) {
    const d = new Date(iso);
    if (Number.isNaN(d.getTime())) return 'Conversa anterior';
    const hoje = new Date();
    const mesmo = d.toDateString() === hoje.toDateString();
    const hora = d.toLocaleTimeString('pt-BR', { hour: '2-digit', minute: '2-digit' });
    return mesmo ? `Hoje, ${hora}` : `${d.toLocaleDateString('pt-BR', { day: '2-digit', month: '2-digit' })}, ${hora}`;
  }
  function erro(mensagem) { $('as-erro').textContent = mensagem || ''; $('as-erro').hidden = !mensagem; }
  function liberar() {
    campo.disabled = !ligada;
    botao.disabled = !ligada || ocupada || !campo.value.trim();
  }
  function vazio() {
    const nome = atendente.trim().split(/\s+/)[0];
    estado(nome ? `Olá, ${nome}` : 'Olá', 'Pergunte como fazer uma operação, as regras de um banco ou o que fazer quando algo dá errado. Eu respondo com base nos roteiros da Pontual.');
  }

  // ------------------------------------------------------------------- API
  const cabecalhos = (json) => ({ Authorization: `Bearer ${P.token}`, ...(json ? { 'Content-Type': 'application/json' } : {}) });
  async function mensagemDeErro(resposta) {
    try { const dados = await resposta.json(); if (dados && typeof dados.error === 'string') return dados.error; } catch { /* sem corpo JSON */ }
    return resposta.status === 429 ? 'Muitas perguntas em pouco tempo. Aguarde um minuto.' : `O servidor respondeu com o erro ${resposta.status}.`;
  }

  async function carregar() {
    if (!P.token) { estado('Assistente indisponível', 'Abra a assistente pelo Terminal Pontual.'); return; }
    let dados;
    try {
      const r = await fetch('/api/v1/device/assistant', { headers: cabecalhos(false), cache: 'no-store' });
      if (!r.ok) throw new Error(await mensagemDeErro(r));
      dados = await r.json();
    } catch (e) {
      ligada = false; liberar(); pintarTela();
      estado('Sem conexão', e instanceof TypeError ? 'Não foi possível falar com o servidor da Pontual. Tente de novo em instantes.' : e.message);
      return;
    }
    ligada = dados.enabled === true;
    atendente = typeof dados.attendantName === 'string' ? dados.attendantName : '';
    telaPermitida = dados.screen === true;
    aprendeComWhatsApp = dados.whatsappLearning === true;
    liberar(); pintarTela();
    if (!ligada) { estado('Assistente desligada', 'A assistente não está ligada para este computador. Fale com o TI.'); return; }

    falas.replaceChildren();
    ultimaConversa = null;
    const lista = Array.isArray(dados.messages) ? dados.messages : [];
    avaliadas.clear();
    for (const f of Array.isArray(dados.feedback) ? dados.feedback : []) avaliadas.set(f.messageId, f.helpful === true);
    for (const m of lista) {
      if (m.conversationId !== ultimaConversa) { divisa(dia(m.at)); ultimaConversa = m.conversationId; }
      fala(m.role, m.text, m.id, m.context);
    }
    if (lista.length) { mostrarFalas(); descer(); } else vazio();
    campo.focus();
  }

  async function enviar(texto) {
    ocupada = true; erro(''); liberar();
    mostrarFalas();
    if (novaConversa || ultimaConversa === null) divisa(dia(new Date().toISOString()));
    const pergunta = fala('user', texto);
    const resposta = el('div', 'as-fala ia');
    const pontos = el('span', 'as-pontos'); pontos.append(el('i'), el('i'), el('i'));
    // Quando a base é grande, a assistente consulta os roteiros antes de responder e diz qual está lendo.
    const consultando = el('span', 'as-consulta');
    resposta.append(pontos, consultando);
    falas.append(resposta);
    descer();

    let recebido = '', falhou = null, respostaId = null;
    try {
      // A tela é lida agora, no momento da pergunta, e vai só com ela.
      const { screen: tela, apps } = await pedirTela();
      const r = await fetch('/api/v1/device/assistant/messages', {
        method: 'POST', headers: cabecalhos(true), body: JSON.stringify({ text: texto, newConversation: novaConversa, ...(tela ? { screen: tela } : {}), ...(apps ? { apps } : {}) }),
      });
      if (!r.ok) throw new Error(await mensagemDeErro(r));
      novaConversa = false;

      const leitor = r.body.getReader(), decodificador = new TextDecoder();
      let resto = '';
      for (;;) {
        const { value, done } = await leitor.read();
        if (done) break;
        resto += decodificador.decode(value, { stream: true });
        let corte;
        while ((corte = resto.indexOf('\n\n')) >= 0) {
          const linha = resto.slice(0, corte); resto = resto.slice(corte + 2);
          if (!linha.startsWith('data: ')) continue;
          let evento; try { evento = JSON.parse(linha.slice(6)); } catch { continue; }
          if (evento.conversationId) { ultimaConversa = evento.conversationId; if (typeof evento.screen === 'string') notaDaTela(pergunta, evento.screen); }
          if (typeof evento.status === 'string' && !recebido) consultando.textContent = evento.status;
          if (evento.done === true && evento.id) respostaId = evento.id;
          if (typeof evento.delta === 'string') {
            const colado = noFim();
            recebido += evento.delta;
            formatar(resposta, recebido);
            if (colado) descer();
          }
          if (typeof evento.error === 'string') falhou = evento.error;
        }
      }
      if (!recebido && !falhou) falhou = 'A assistente não respondeu. Tente de novo.';
    } catch (e) {
      falhou = e instanceof TypeError ? 'A conexão com o servidor caiu. Tente de novo.' : e.message;
    }

    if (falhou) {
      if (recebido) { erro(falhou); } else { resposta.classList.add('falha'); resposta.textContent = falhou; }
    }
    if (respostaId && recebido) { const colado = noFim(); resposta.after(avaliacao(respostaId)); if (colado) descer(); }
    if (recebido && !falhou) executar(resposta, recebido);
    ocupada = false; liberar();
    campo.focus();
  }

  // --------------------------------------------------------------- eventos
  function ajustarAltura() { campo.style.height = 'auto'; campo.style.height = `${Math.min(campo.scrollHeight, 120)}px`; }
  campo.addEventListener('input', () => { ajustarAltura(); liberar(); });
  campo.addEventListener('keydown', (e) => {
    if (e.key === 'Enter' && !e.shiftKey && !e.isComposing) { e.preventDefault(); $('as-form').requestSubmit(); }
  });
  $('as-form').addEventListener('submit', (e) => {
    e.preventDefault();
    const texto = campo.value.trim();
    if (!texto || ocupada || !ligada) return;
    campo.value = ''; ajustarAltura();
    enviar(texto);
  });
  $('as-nova').onclick = () => {
    if (ocupada || !ligada) return;
    novaConversa = true; erro('');
    if (!falas.hidden && falas.lastChild && !falas.lastChild.classList.contains('as-divisa')) { divisa('Nova conversa'); descer(); }
    campo.focus();
  };
  const fechar = () => { if (ponte) ponte.postMessage({ type: 'assistant-close' }); };
  $('as-fechar').onclick = fechar;
  if (!ponte) $('as-fechar').hidden = true;
  document.addEventListener('keydown', (e) => { if (e.key === 'Escape') fechar(); });

  // O programa avisa quando a janela é aberta de novo: a conversa é recarregada se nada estiver em andamento.
  if (ponte) {
    ponte.addEventListener('message', (e) => {
      if (e.data && e.data.type === 'assistant-shown' && !ocupada) { if (ligada) campo.focus(); else carregar(); }
    });
  }

  carregar();
})();
