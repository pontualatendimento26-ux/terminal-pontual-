// Notas adesivas da Pontual (pedido do dono, 25/11/2026): o que é comum à tela inicial e às janelas das notas.
// As notas vivem no armazenamento do navegador interno do Terminal, uma chave por nota. As janelas das notas e a
// lista só leem e escrevem ali; quem conversa com a Central é a tela inicial (app.js), que fica aberta o dia todo:
// ela envia o que mudou e busca os recados do TI. Assim a nota funciona sem conexão e nada se perde ao fechar a janela.
// Regra da casa: o texto da nota nunca é HTML. É uma lista de blocos, e a tela é montada só com textContent.
window.PontualNotas = (() => {
  'use strict';

  const PREFIXO = 'pontual.nota.';
  const CORES = ['yellow', 'green', 'pink', 'purple', 'blue', 'gray', 'charcoal'];
  const NOMES_CORES = { yellow: 'Amarelo', green: 'Verde', pink: 'Rosa', purple: 'Roxo', blue: 'Azul', gray: 'Cinza', charcoal: 'Grafite' };
  const LIMITE = 50, MAX_CARACTERES = 20000, MAX_BLOCOS = 400, MAX_TRECHOS = 60;
  const ID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

  const inteiro = (v, min, max) => (Number.isFinite(v) ? Math.min(max, Math.max(min, Math.round(v))) : 0);

  // crypto.randomUUID só existe em página segura (https); a Central da rede interna é http.
  function novoId() {
    const b = crypto.getRandomValues(new Uint8Array(16));
    b[6] = (b[6] & 0x0f) | 0x40; b[8] = (b[8] & 0x3f) | 0x80;
    const h = [...b].map((x) => x.toString(16).padStart(2, '0')).join('');
    return `${h.slice(0, 8)}-${h.slice(8, 12)}-${h.slice(12, 16)}-${h.slice(16, 20)}-${h.slice(20)}`;
  }

  // Deixa o texto na forma aceita pela Central, venha de onde vier (da própria tela, do armazenamento ou do servidor).
  function limparConteudo(conteudo) {
    const blocos = [];
    let total = 0;
    for (const b of Array.isArray(conteudo) ? conteudo.slice(0, MAX_BLOCOS) : []) {
      const trechos = [];
      for (const r of b && Array.isArray(b.r) ? b.r : []) {
        if (!r || typeof r.s !== 'string' || !r.s || total >= MAX_CARACTERES) continue;
        const s = r.s.replace(/[\u0000-\u001f\u007f ]/g, ' ').slice(0, MAX_CARACTERES - total);
        const f = Number.isInteger(r.f) ? r.f & 15 : 0;
        total += s.length;
        const ultimo = trechos[trechos.length - 1];
        if (ultimo && (ultimo.f === f || trechos.length >= MAX_TRECHOS)) ultimo.s += s; else trechos.push({ s, f });
      }
      blocos.push({ t: b && b.t === 'li' ? 'li' : 'p', r: trechos });
    }
    while (blocos.length && !blocos[blocos.length - 1].r.length) blocos.pop();
    return blocos;
  }

  const textoDe = (conteudo) => (Array.isArray(conteudo) ? conteudo : []).map((b) => (b.t === 'li' ? '• ' : '') + b.r.map((r) => r.s).join('')).join('\n');

  function validar(n) {
    if (!n || typeof n !== 'object' || !ID.test(n.id)) return null;
    return {
      id: n.id.toLowerCase(), color: CORES.includes(n.color) ? n.color : CORES[0], content: limparConteudo(n.content),
      x: inteiro(n.x, -20000, 20000), y: inteiro(n.y, -20000, 20000), w: inteiro(n.w, 0, 4000), h: inteiro(n.h, 0, 4000),
      open: n.open === true, from: typeof n.from === 'string' ? n.from.slice(0, 120) : '',
      createdAt: typeof n.createdAt === 'string' ? n.createdAt : new Date().toISOString(),
      updatedAt: typeof n.updatedAt === 'string' ? n.updatedAt : new Date().toISOString(),
      // rev sobe a cada mudança feita neste computador; sync é a última rev que a Central confirmou.
      rev: inteiro(n.rev, 0, 1e9), sync: inteiro(n.sync, -1, 1e9), del: n.del === true, parada: n.parada === true,
    };
  }

  // Lê uma nota. As apagadas que ainda esperam a Central confirmar só aparecem com comApagadas.
  function ler(id, comApagadas = false) {
    try {
      const n = validar(JSON.parse(localStorage.getItem(PREFIXO + id) || 'null'));
      return n && (comApagadas || !n.del) ? n : null;
    } catch { return null; }
  }
  function todas(comApagadas = false) {
    const lista = [];
    try {
      for (let i = 0; i < localStorage.length; i++) {
        const chave = localStorage.key(i);
        if (!chave || !chave.startsWith(PREFIXO)) continue;
        const n = ler(chave.slice(PREFIXO.length), comApagadas);
        if (n) lista.push(n);
      }
    } catch { /* sem armazenamento: não há notas */ }
    return lista.sort((a, b) => (a.updatedAt < b.updatedAt ? 1 : -1));
  }
  function guardar(n) {
    try { localStorage.setItem(PREFIXO + n.id, JSON.stringify(n)); return true; } catch { return false; }
  }
  function esquecer(id) {
    try { localStorage.removeItem(PREFIXO + id); } catch { /* nada a tirar */ }
  }

  // Muda campos de uma nota. Relê o que está guardado antes, porque a lista e a janela da nota podem mexer na mesma nota.
  function atualizar(id, campos, tocar = true) {
    const n = ler(id, true);
    if (!n) return null;
    Object.assign(n, campos);
    n.rev += 1; n.parada = false;
    if (tocar) n.updatedAt = new Date().toISOString();
    return guardar(n) ? n : null;
  }
  function nova(cor) {
    if (todas().length >= LIMITE) return null;
    const agora = new Date().toISOString();
    const n = validar({ id: novoId(), color: cor, content: [], open: true, createdAt: agora, updatedAt: agora, rev: 1, sync: 0 });
    return guardar(n) ? n : null;
  }
  // Apagar deixa uma marca até a Central confirmar; sem isso a nota voltaria na próxima busca.
  const apagar = (id) => atualizar(id, { del: true, open: false });

  const ehChave = (chave) => typeof chave === 'string' && chave.startsWith(PREFIXO);
  const idDaChave = (chave) => chave.slice(PREFIXO.length);

  // ------------------------------------------------------------ conversa com a Central (só a tela inicial chama)
  const corpo = (n) => ({ color: n.color, content: n.content, x: n.x, y: n.y, width: n.w, height: n.h, open: n.open });
  // Recusa definitiva da Central para aquela nota (formato, limite de notas): insistir não adianta.
  const recusa = (e) => e && (e.status === 400 || e.status === 404 || e.status === 409);

  let ocupado = false;
  // Envia o que mudou e busca o que é novo. Devolve { mostrar: [notas a abrir na tela], fechar: [ids retirados] };
  // nulo se já havia uma rodada em andamento. Falha de rede ou máquina pendente: lança o erro e tenta na próxima.
  async function sincronizar(api) {
    if (ocupado) return null;
    ocupado = true;
    const resultado = { mostrar: [], fechar: [] };
    try {
      for (const n of todas(true)) {
        if (n.rev === n.sync || n.parada) continue;
        try {
          if (n.del) {
            await api('DELETE', `/device/notes/${n.id}`);
            const agora = ler(n.id, true);
            if (agora && agora.del) esquecer(n.id);
          } else {
            await api('PUT', `/device/notes/${n.id}`, corpo(n));
            const agora = ler(n.id, true);
            if (agora) { agora.sync = n.rev; guardar(agora); }
          }
        } catch (e) {
          if (!recusa(e)) throw e;
          const agora = ler(n.id, true);
          if (agora && agora.rev === n.rev) { agora.parada = true; guardar(agora); }
        }
      }

      const r = await api('GET', '/device/notes');
      const noServidor = new Set();
      for (const id of Array.isArray(r && r.removed) ? r.removed : []) {
        // Recado que o TI retirou: sai da tela e da lista.
        if (typeof id === 'string' && ler(id.toLowerCase(), true)) { esquecer(id.toLowerCase()); resultado.fechar.push(id.toLowerCase()); }
      }
      for (const s of Array.isArray(r && r.notes) ? r.notes : []) {
        const n = validar({ ...s, w: s && s.width, h: s && s.height, rev: 0, sync: 0 });
        if (!n) continue;
        noServidor.add(n.id);
        // O que já existe aqui vale mais: só este computador altera as próprias notas.
        if (ler(n.id, true)) continue;
        if (guardar(n) && n.open) resultado.mostrar.push(n);
      }
      // Nota que a Central não tem mais (banco restaurado, computador recadastrado): manda de novo na próxima rodada.
      for (const n of todas()) {
        if (!noServidor.has(n.id) && n.rev === n.sync && !n.parada) { n.sync = -1; guardar(n); }
      }
      return resultado;
    } finally {
      ocupado = false;
    }
  }

  return { CORES, NOMES_CORES, LIMITE, MAX_CARACTERES, ler, todas, atualizar, nova, apagar, limparConteudo, textoDe, ehChave, idDaChave, sincronizar };
})();
