// Notas adesivas da Pontual (pedido do dono, 25/11/2026): parecidas com as Notas Autoadesivas do Windows.
// Esta página é a janela de UMA nota (notas.html?id=...) ou a lista de todas as notas (sem id).
// Ela não fala com a Central: lê e grava no armazenamento local (notas-base.js) e a tela inicial sincroniza.
// Dentro do Terminal, cada nota é uma janela própria do Windows (MainWindow.Notes.cs) e as mensagens abaixo pedem
// para abrir, fechar e colorir janelas. Num navegador comum (teste), as janelas viram abas.
// Regra da casa: nada entra na página por innerHTML; o texto da nota é montado com textContent.
(() => {
  'use strict';

  const N = window.PontualNotas;
  const $ = (id) => document.getElementById(id);
  const el = (tag, props = {}, ...filhos) => {
    const n = document.createElement(tag);
    for (const [chave, valor] of Object.entries(props)) {
      if (valor === null || valor === undefined) continue;
      if (chave === 'role' || chave.startsWith('aria-') || chave.startsWith('data-')) n.setAttribute(chave, valor); else n[chave] = valor;
    }
    n.append(...filhos.filter((f) => f !== null && f !== undefined && f !== false));
    return n;
  };
  // Ícones: desenhos fixos, escritos aqui mesmo e montados sem innerHTML.
  const IC = {
    mais: 'M12 5v14M5 12h14', x: 'M6 6l12 12M18 6 6 18', pontos: 'M5 12h.01M12 12h.01M19 12h.01', check: 'm5 12 5 5 9-10',
    lista: 'M8 6h12M8 12h12M8 18h12M4 6h.01M4 12h.01M4 18h.01', lixo: 'M4 7h16M9 7V4h6v3M6 7l1 13h10l1-13M10 11v6M14 11v6',
    busca: 'M11 18a7 7 0 1 0 0-14 7 7 0 0 0 0 14ZM20 20l-3.5-3.5', marcadores: 'M9 6h11M9 12h11M9 18h11M4.5 6h.01M4.5 12h.01M4.5 18h.01',
  };
  function ic(nome, tam = 18, grosso = 2) {
    const ns = 'http://www.w3.org/2000/svg';
    const svg = document.createElementNS(ns, 'svg');
    for (const [k, v] of Object.entries({ width: tam, height: tam, viewBox: '0 0 24 24', fill: 'none', stroke: 'currentColor', 'stroke-width': grosso, 'stroke-linecap': 'round', 'stroke-linejoin': 'round', 'aria-hidden': 'true' })) svg.setAttribute(k, v);
    const p = document.createElementNS(ns, 'path');
    p.setAttribute('d', IC[nome]);
    svg.append(p);
    return svg;
  }

  const P = window.PONTUAL && typeof window.PONTUAL === 'object' && window.PONTUAL.hosted === true ? window.PONTUAL : null;
  const ponte = P && window.chrome && window.chrome.webview ? window.chrome.webview : null;
  const parametros = new URLSearchParams(location.search);
  const meuId = (parametros.get('id') || '').toLowerCase();

  // Pedidos ao Terminal. Fora dele (navegador comum, para teste), as janelas viram abas.
  function aoPrograma(m) {
    if (ponte) { ponte.postMessage(m); return; }
    if (m.type === 'note-show') window.open(`notas.html?id=${encodeURIComponent(m.id)}`, `nota-${m.id}`, 'width=320,height=340');
    else if (m.type === 'notes') window.open('notas.html', 'notas-lista', 'width=360,height=600');
    else if (m.type === 'note-close' || m.type === 'notes-close') window.close();
  }
  const mostrarNota = (n, extra = {}) => aoPrograma({ type: 'note-show', id: n.id, color: n.color, x: n.x, y: n.y, w: n.w, h: n.h, activate: true, ...extra });

  let avisoTimer = 0;
  function avisar(mensagem) {
    const a = $('aviso');
    a.textContent = mensagem; a.hidden = false;
    clearTimeout(avisoTimer);
    avisoTimer = setTimeout(() => { a.hidden = true; }, 5000);
  }

  function criarNota(cor, perto) {
    const n = N.nova(cor);
    if (!n) { avisar(`Você já tem ${N.LIMITE} notas. Apague alguma para criar outra.`); return; }
    mostrarNota(n, { near: perto === true });
  }

  const quando = (iso) => {
    const d = new Date(iso);
    if (Number.isNaN(d.getTime())) return '';
    const hoje = new Date();
    return d.toDateString() === hoje.toDateString()
      ? d.toLocaleTimeString('pt-BR', { hour: '2-digit', minute: '2-digit' })
      : d.toLocaleDateString('pt-BR', { day: 'numeric', month: 'short' });
  };

  // ==================================================================== texto da nota <-> tela
  const NEGRITO = 1, ITALICO = 2, SUBLINHADO = 4, RISCADO = 8;
  const BLOCOS = new Set(['DIV', 'P', 'LI', 'UL', 'OL', 'H1', 'H2', 'H3', 'H4', 'H5', 'H6', 'BLOCKQUOTE', 'PRE']);

  function montar(alvo, conteudo) {
    const filhos = [];
    let lista = null;
    for (const b of conteudo) {
      const linha = el(b.t === 'li' ? 'li' : 'div');
      for (const r of b.r) {
        let no = document.createTextNode(r.s);
        for (const [bit, tag] of [[RISCADO, 's'], [SUBLINHADO, 'u'], [ITALICO, 'i'], [NEGRITO, 'b']]) if (r.f & bit) no = el(tag, {}, no);
        linha.append(no);
      }
      if (!b.r.length) linha.append(el('br'));
      if (b.t === 'li') { if (!lista) { lista = el('ul'); filhos.push(lista); } lista.append(linha); } else { lista = null; filhos.push(linha); }
    }
    alvo.replaceChildren(...filhos);
  }

  function formatoDe(no, atual) {
    let f = atual;
    const tag = no.tagName;
    if (tag === 'B' || tag === 'STRONG') f |= NEGRITO;
    if (tag === 'I' || tag === 'EM') f |= ITALICO;
    if (tag === 'U') f |= SUBLINHADO;
    if (tag === 'S' || tag === 'STRIKE' || tag === 'DEL') f |= RISCADO;
    const st = no.style;
    if (st) {
      if (st.fontWeight === 'bold' || Number(st.fontWeight) >= 600) f |= NEGRITO;
      if (st.fontStyle === 'italic') f |= ITALICO;
      const deco = `${st.textDecorationLine || ''} ${st.textDecoration || ''}`;
      if (deco.includes('underline')) f |= SUBLINHADO;
      if (deco.includes('line-through')) f |= RISCADO;
    }
    return f;
  }

  // Lê o que está na área de edição e devolve os blocos. Só texto e a formatação conhecida saem daqui.
  function ler(raiz) {
    const blocos = [];
    let atual = null;
    const novo = (li) => { atual = { t: li ? 'li' : 'p', r: [] }; blocos.push(atual); };
    const anda = (no, f, li) => {
      for (const c of no.childNodes) {
        if (c.nodeType === Node.TEXT_NODE) {
          const partes = c.data.split('\n');
          partes.forEach((parte, i) => {
            if (i > 0) { if (!atual) novo(li); atual = null; }
            if (!parte) return;
            if (!atual) novo(li);
            atual.r.push({ s: parte, f });
          });
        } else if (c.nodeType === Node.ELEMENT_NODE) {
          const tag = c.tagName;
          if (tag === 'BR') { if (!atual) novo(li); atual = null; }
          else if (BLOCOS.has(tag)) { atual = null; anda(c, formatoDe(c, f), tag === 'LI' || (li && tag !== 'UL' && tag !== 'OL')); atual = null; }
          else anda(c, formatoDe(c, f), li);
        }
      }
    };
    anda(raiz, 0, false);
    return N.limparConteudo(blocos);
  }

  // ==================================================================== a janela de uma nota
  function telaDaNota() {
    let nota = N.ler(meuId);
    if (!nota) {
      // A nota foi apagada (ou é de outro computador): a janela não tem o que mostrar.
      $('raiz').replaceChildren(el('div', { className: 'nota-sumiu', textContent: 'Esta nota não existe mais.' }));
      aoPrograma({ type: 'note-close', id: meuId });
      return;
    }
    if (!nota.open) nota = N.atualizar(meuId, { open: true }, false) || nota;

    const moldura = Boolean(ponte) && P.frame === true;
    const caixa = el('div', { className: `nota cor-${nota.color}${moldura ? ' moldura' : ''}` });
    const texto = el('div', { className: 'nota-texto', contentEditable: 'true', spellcheck: true, role: 'textbox', 'aria-multiline': 'true', 'aria-label': 'Texto da nota', 'data-vazio': 'Escreva uma nota…' });
    const salvo = el('span', { className: 'salvo', textContent: '' });
    const menu = el('div', { className: 'nota-menu', hidden: true });
    const confirma = el('div', { className: 'nota-confirma', hidden: true });

    const botao = (icone, rotulo, acao) => { const b = el('button', { type: 'button', className: 'bt', title: rotulo, 'aria-label': rotulo }, typeof icone === 'string' ? ic(icone) : icone); b.onclick = acao; return b; };

    // ---- gravação: guarda no computador logo depois de cada mudança; a tela inicial manda para a Central.
    let pendente = 0;
    function gravarAgora() {
      if (!pendente) return;
      clearTimeout(pendente); pendente = 0;
      const n = N.atualizar(meuId, { content: ler(texto) });
      if (n) nota = n;
      salvo.textContent = n ? 'Salvo' : 'Não foi possível salvar';
    }
    function mudou() {
      salvo.textContent = '';
      clearTimeout(pendente);
      pendente = setTimeout(gravarAgora, 400);
    }

    function fechar() {
      gravarAgora();
      N.atualizar(meuId, { open: false }, false);
      aoPrograma({ type: 'note-close', id: meuId });
    }
    function pintar(cor) {
      const n = N.atualizar(meuId, { color: cor }, false);
      if (!n) return;
      nota = n;
      caixa.className = `nota cor-${cor}${moldura ? ' moldura' : ''}`;
      aoPrograma({ type: 'note-color', id: meuId, color: cor });
      desenharMenu();
    }
    function pedirExclusao() {
      menu.hidden = true;
      const sim = el('button', { type: 'button', className: 'btn perigo', textContent: 'Excluir' });
      const nao = el('button', { type: 'button', className: 'btn', textContent: 'Cancelar' });
      sim.onclick = () => { clearTimeout(pendente); pendente = 0; N.apagar(meuId); aoPrograma({ type: 'note-close', id: meuId }); };
      nao.onclick = () => { confirma.hidden = true; texto.focus(); };
      confirma.replaceChildren(el('div', { role: 'alertdialog', 'aria-label': 'Excluir nota' }, el('p', { textContent: 'Excluir esta nota? Não dá para desfazer.' }), el('div', { className: 'acoes' }, nao, sim)));
      confirma.hidden = false;
      nao.focus();
    }

    function desenharMenu() {
      const cores = el('div', { className: 'cores', role: 'group', 'aria-label': 'Cor da nota' }, ...N.CORES.map((c) => {
        const b = el('button', { type: 'button', className: `cor-${c}`, title: N.NOMES_CORES[c], 'aria-label': N.NOMES_CORES[c], 'aria-pressed': String(c === nota.color) }, c === nota.color ? ic('check', 18, 2.5) : null);
        b.onclick = () => pintar(c);
        return b;
      }));
      const lista = el('button', { type: 'button', className: 'linha' }, ic('lista'), 'Lista de notas');
      lista.onclick = () => { menu.hidden = true; aoPrograma({ type: 'notes' }); };
      const excluir = el('button', { type: 'button', className: 'linha perigo' }, ic('lixo'), 'Excluir nota');
      excluir.onclick = pedirExclusao;
      menu.replaceChildren(cores, lista, excluir);
    }
    function alternarMenu() {
      menu.hidden = !menu.hidden;
      if (!menu.hidden) { desenharMenu(); const b = menu.querySelector('[aria-pressed="true"]'); if (b) b.focus(); }
    }

    // ---- formatação
    const comandos = [['bold', 'Negrito (Ctrl+B)', 'B', 'font-weight:700'], ['italic', 'Itálico (Ctrl+I)', 'I', 'font-style:italic'],
      ['underline', 'Sublinhado (Ctrl+U)', 'U', 'text-decoration:underline'], ['strikeThrough', 'Riscado (Ctrl+T)', 'ab', 'text-decoration:line-through'],
      ['insertUnorderedList', 'Lista com marcadores (Ctrl+Shift+L)', null, '']];
    const aplicar = (comando) => { texto.focus(); document.execCommand(comando, false, null); mudou(); marcarBotoes(); };
    const botoes = comandos.map(([comando, rotulo, letra, estilo]) => {
      const marca = letra ? el('span', { textContent: letra }) : ic('marcadores');
      if (letra) marca.style.cssText = estilo;
      const b = botao(marca, rotulo, () => aplicar(comando));
      b.setAttribute('aria-pressed', 'false');
      // O clique não pode tirar a seleção do texto.
      b.onmousedown = (e) => e.preventDefault();
      return b;
    });
    function marcarBotoes() {
      comandos.forEach(([comando], i) => {
        let ligado = false;
        try { ligado = document.queryCommandState(comando); } catch { /* sem seleção */ }
        botoes[i].setAttribute('aria-pressed', String(ligado));
      });
    }

    const topo = el('header', { className: 'nota-topo' },
      botao('mais', 'Nova nota (Ctrl+N)', () => criarNota(nota.color, true)), el('span', { className: 'vao' }),
      botao('pontos', 'Menu', alternarMenu), botao('x', 'Fechar nota', fechar));
    const de = nota.from ? el('div', { className: 'nota-de', textContent: `Recado de ${nota.from} (TI)` }) : null;
    caixa.append(...[topo, menu, de, texto, el('footer', { className: 'nota-barra' }, ...botoes, salvo), confirma].filter(Boolean));
    $('raiz').replaceChildren(caixa);
    document.title = N.textoDe(nota.content).split('\n')[0].slice(0, 60) || 'Nota';

    try { document.execCommand('styleWithCSS', false, false); document.execCommand('defaultParagraphSeparator', false, 'div'); } catch { /* padrão do navegador */ }
    montar(texto, nota.content);

    texto.addEventListener('input', mudou);
    texto.addEventListener('blur', gravarAgora);
    texto.addEventListener('beforeinput', (e) => {
      // Limite de tamanho: a Central não guarda mais que isto.
      if (e.inputType && e.inputType.startsWith('insert') && e.inputType !== 'insertParagraph' && texto.textContent.length >= N.MAX_CARACTERES) {
        e.preventDefault();
        avisar('Esta nota chegou ao tamanho máximo. Crie outra nota para continuar.');
      }
    });
    // Colar e arrastar: só texto puro entra na nota (sem imagem nem formatação de fora).
    texto.addEventListener('paste', (e) => {
      e.preventDefault();
      const sobra = N.MAX_CARACTERES - texto.textContent.length;
      const colado = ((e.clipboardData && e.clipboardData.getData('text/plain')) || '').replace(/\r\n?/g, '\n').slice(0, Math.max(0, sobra));
      colado.split('\n').forEach((linha, i) => {
        if (i > 0) document.execCommand('insertParagraph', false, null);
        if (linha) document.execCommand('insertText', false, linha);
      });
    });
    texto.addEventListener('drop', (e) => e.preventDefault());
    document.addEventListener('selectionchange', marcarBotoes);
    document.addEventListener('keydown', (e) => {
      if (e.key === 'Escape') { if (!confirma.hidden) { confirma.hidden = true; texto.focus(); } else if (!menu.hidden) { menu.hidden = true; texto.focus(); } return; }
      if (!e.ctrlKey || e.altKey) return;
      const tecla = e.key.toLowerCase();
      const atalho = e.shiftKey ? { l: 'insertUnorderedList' }[tecla] : { b: 'bold', i: 'italic', u: 'underline', t: 'strikeThrough' }[tecla];
      if (atalho) { e.preventDefault(); aplicar(atalho); }
      else if (!e.shiftKey && tecla === 'n') { e.preventDefault(); criarNota(nota.color, true); }
      else if (!e.shiftKey && tecla === 'd') { e.preventDefault(); pedirExclusao(); }
    });
    caixa.addEventListener('mousedown', (e) => { if (!menu.hidden && !menu.contains(e.target) && !topo.contains(e.target)) menu.hidden = true; });
    window.addEventListener('pagehide', gravarAgora);
    document.addEventListener('visibilitychange', () => { if (document.hidden) gravarAgora(); });

    // A lista (ou o TI, pela Central) apagou esta nota: a janela fecha sozinha.
    window.addEventListener('storage', (e) => {
      if (!N.ehChave(e.key) || N.idDaChave(e.key) !== meuId) return;
      const agora = N.ler(meuId);
      if (!agora) { clearTimeout(pendente); pendente = 0; aoPrograma({ type: 'note-close', id: meuId }); }
    });

    // Mensagens da janela do Windows: os botões do topo e o lugar da janela na tela.
    if (ponte) {
      ponte.addEventListener('message', (e) => {
        const m = e.data;
        if (!m || typeof m !== 'object') return;
        if (m.type === 'new') criarNota(nota.color, true);
        else if (m.type === 'menu') alternarMenu();
        else if (m.type === 'close') fechar();
        else if (m.type === 'geometry' && [m.x, m.y, m.w, m.h].every(Number.isFinite)) { const n = N.atualizar(meuId, { x: m.x, y: m.y, w: m.w, h: m.h }, false); if (n) nota = n; }
      });
    }

    if (parametros.get('foco') !== '0') {
      texto.focus();
      const fim = document.createRange();
      fim.selectNodeContents(texto); fim.collapse(false);
      const sel = getSelection(); sel.removeAllRanges(); sel.addRange(fim);
    }
  }

  // ==================================================================== a lista de notas
  function telaDaLista() {
    document.title = 'Notas Adesivas';
    let confirmando = null;
    const busca = el('input', { type: 'search', placeholder: 'Pesquisar nas notas', autocomplete: 'off', spellcheck: false, 'aria-label': 'Pesquisar nas notas' });
    const cartoes = el('div', { className: 'cartoes' });
    const mais = el('button', { type: 'button', className: 'btn azul' }, ic('mais', 16, 2.5), 'Nova nota');
    mais.onclick = () => { criarNota('yellow', false); desenhar(); };
    const semAcento = (s) => s.normalize('NFD').replace(/[̀-ͯ]/g, '').toLowerCase();

    function desenhar() {
      const termo = semAcento(busca.value.trim());
      const todas = N.todas();
      const notas = todas.filter((n) => !termo || semAcento(N.textoDe(n.content)).includes(termo) || semAcento(n.from).includes(termo));
      if (!notas.length) {
        cartoes.replaceChildren(el('p', { className: 'lista-vazia', textContent: todas.length ? 'Nenhuma nota com esse texto.' : 'Você ainda não tem notas. Clique em “Nova nota” para criar a primeira.' }));
        return;
      }
      cartoes.replaceChildren(...notas.map((n) => {
        const previa = N.textoDe(n.content).trim();
        const abrir = el('button', { type: 'button', className: 'abrir', 'aria-label': `Abrir nota: ${previa.slice(0, 80) || 'vazia'}` },
          el('span', { className: 'quando' }, el('span', { textContent: n.from ? `Recado de ${n.from} (TI)` : n.open ? 'Aberta na tela' : '' }), el('span', { textContent: quando(n.updatedAt) })),
          el('span', { className: `previa${previa ? '' : ' vazia'}`, textContent: previa || 'Nota vazia' }));
        abrir.onclick = () => { const a = N.atualizar(n.id, { open: true }, false) || n; mostrarNota(a); desenhar(); };
        const lixo = el('button', { type: 'button', className: 'lixo', title: 'Excluir nota', 'aria-label': 'Excluir nota' }, ic('lixo', 16));
        lixo.onclick = () => { confirmando = n.id; desenhar(); };
        const cartao = el('div', { className: `cartao cor-${n.color}` }, abrir, lixo);
        if (confirmando === n.id) {
          const sim = el('button', { type: 'button', className: 'btn perigo', textContent: 'Excluir' });
          const nao = el('button', { type: 'button', className: 'btn', textContent: 'Cancelar' });
          // A janela da nota, se estiver aberta, percebe sozinha que a nota sumiu e fecha.
          sim.onclick = () => { confirmando = null; N.apagar(n.id); desenhar(); };
          nao.onclick = () => { confirmando = null; desenhar(); };
          cartao.append(el('div', { className: 'confirma', role: 'alertdialog', 'aria-label': 'Excluir nota' }, el('span', { textContent: 'Excluir esta nota?' }), nao, sim));
        }
        return cartao;
      }));
    }

    busca.oninput = desenhar;
    $('raiz').replaceChildren(el('div', { className: 'lista' },
      el('header', { className: 'lista-topo' }, el('h1', { textContent: 'Notas Adesivas' }), mais),
      el('label', { className: 'lista-busca' }, ic('busca', 16), busca), cartoes,
      el('footer', { className: 'lista-rodape', textContent: P && P.local === true ? 'Suas notas ficam guardadas neste computador.'
        : 'Suas notas ficam guardadas na Central da Pontual, para não se perderem. O TI pode ler as notas e enviar recados.' })));
    desenhar();
    window.addEventListener('storage', (e) => { if (N.ehChave(e.key)) desenhar(); });
    // As horas ("Aberta na tela", data) mudam sem ninguém mexer aqui.
    setInterval(desenhar, 60000);
    document.addEventListener('keydown', (e) => {
      if (e.key === 'Escape') { if (confirmando) { confirmando = null; desenhar(); } else aoPrograma({ type: 'notes-close' }); }
      else if (e.ctrlKey && !e.shiftKey && !e.altKey && e.key.toLowerCase() === 'n') { e.preventDefault(); mais.click(); }
    });
  }

  if (meuId) telaDaNota(); else telaDaLista();
})();
