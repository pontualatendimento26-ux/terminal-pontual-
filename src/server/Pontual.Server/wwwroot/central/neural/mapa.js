// Central Neural — o mapa interativo do cérebro da IA.
// Canvas 2D puro, sem biblioteca: núcleo holográfico no centro, ramos em volta, zoom, arrasto,
// expandir/recolher, busca e seleção. Não fala com o servidor: recebe os nós prontos de neural.js.
//
// Um nó: { id, pai, tipo, rotulo, sub, estado, novo }
//   tipo: 'nucleo' | 'ramo' | 'grupo' | 'script' | 'document' | 'knowledge' | 'attendant' | 'suggestion'
//   estado: 'active' | 'disabled' | 'pending' | 'error'
'use strict';

const MAPA_CORES = {
  nucleo: '#4fd8ff', script: '#4fd8ff', document: '#5b8cff', knowledge: '#9b8cff',
  attendant: '#3ddc97', suggestion: '#ffb454', erro: '#ff6b7d',
};

class MapaNeural {
  constructor(canvas, { aoSelecionar, aoPassar } = {}) {
    this.canvas = canvas;
    this.ctx = canvas.getContext('2d');
    this.aoSelecionar = aoSelecionar || (() => {});
    this.aoPassar = aoPassar || (() => {});
    this.nos = new Map();
    this.ligacoes = [];
    this.recolhidos = new Set();
    this.vistos = new Set();        // nós que já tiveram o estado inicial (aberto ou recolhido) decidido
    this.visiveis = [];
    this.filtro = () => true;
    this.cam = { x: 0, y: 0, z: 0.8 };
    this.camAlvo = null;
    this.sel = null; this.sobre = null; this.destaque = null;
    this.brilhos = new Map();
    this.calmo = window.matchMedia('(prefers-reduced-motion: reduce)').matches;
    this.tempo = 0;
    this.primeiraVez = true;
    this.esfera = this.montarEsfera(110);

    this.ligarEventos();
    this.observador = new ResizeObserver(() => this.redimensionar());
    this.observador.observe(canvas);
    this.redimensionar();
    const quadro = (agora) => { this.quadroId = requestAnimationFrame(quadro); if (!document.hidden) this.desenhar(agora); };
    this.quadroId = requestAnimationFrame(quadro);
  }

  // ------------------------------------------------------------------ dados
  setDados({ nos, ligacoes }) {
    const antigos = this.nos;
    this.nos = new Map();
    for (const dado of nos) {
      const velho = antigos.get(dado.id);
      this.nos.set(dado.id, { ...dado, filhos: [], x: velho ? velho.x : null, y: velho ? velho.y : null, tx: 0, ty: 0, ang: 0, prof: 0 });
    }
    for (const no of this.nos.values()) {
      const pai = no.pai ? this.nos.get(no.pai) : null;
      if (pai) pai.filhos.push(no);
    }
    // Ramificação grande nasce recolhida, para o mapa não virar um borrão quando a base cresce.
    const total = nos.length;
    for (const no of this.nos.values()) {
      if (this.vistos.has(no.id)) continue;
      this.vistos.add(no.id);
      if (no.tipo === 'grupo' && (no.filhos.length > 12 || total > 90)) this.recolhidos.add(no.id);
      if (no.tipo === 'ramo' && no.filhos.length > 40) this.recolhidos.add(no.id);
    }
    this.ligacoes = ligacoes || [];
    if (this.sel && !this.nos.has(this.sel)) this.sel = null;
    this.organizar();
    if (this.primeiraVez) { this.primeiraVez = false; this.ajustar(true); }
  }

  setFiltro(fn) { this.filtro = fn || (() => true); this.organizar(); }
  no(id) { return this.nos.get(id) || null; }
  ehFolha(no) { return !['nucleo', 'ramo', 'grupo'].includes(no.tipo); }

  // ------------------------------------------------------------- organização
  // Árvore radial: cada ramo ocupa uma fatia do círculo proporcional ao que tem dentro.
  organizar() {
    const raiz = this.nos.get('ia');
    if (!raiz) { this.visiveis = []; return; }

    const passa = (no) => {
      if (this.ehFolha(no)) no.passa = this.filtro(no);
      else { let algum = false; for (const f of no.filhos) algum = passa(f) || algum; no.passa = algum || no.tipo !== 'grupo'; }
      return no.passa;
    };
    passa(raiz);

    const mostrados = (no) => (this.recolhidos.has(no.id) ? [] : no.filhos.filter((f) => f.passa));
    const peso = (no) => {
      const fs = mostrados(no);
      no.peso = fs.length ? fs.reduce((s, f) => s + peso(f), 0) : 1;
      if (no.prof === 1) no.peso = Math.max(no.peso, 2.2);
      return no.peso;
    };
    const fundo = (no, prof) => { no.prof = prof; let m = prof; for (const f of mostrados(no)) m = Math.max(m, fundo(f, prof + 1)); return m; };
    const maisFundo = fundo(raiz, 0);
    peso(raiz);

    const base = [0, 250, 450, 650, 850];
    const precisa = (24 * raiz.peso) / (Math.PI * 2);
    const k = Math.max(1, precisa / base[Math.max(maisFundo, 1)]);
    const raio = (prof) => base[prof] * (prof === 1 ? Math.sqrt(k) : k);
    this.aneis = []; for (let p = 1; p <= maisFundo; p++) this.aneis.push(raio(p));

    this.visiveis = [];
    const por = (no, inicio, fatia) => {
      no.ang = inicio + fatia / 2;
      const fs = mostrados(no);
      let r = raio(no.prof);
      if (no.prof >= 2 && !fs.length && no.irmaos > 5) r += no.indice % 2 ? 34 : -8;
      no.tx = Math.cos(no.ang) * r; no.ty = Math.sin(no.ang) * r;
      if (no.x === null) { const pai = this.nos.get(no.pai); no.x = pai ? (pai.x ?? pai.tx) : no.tx; no.y = pai ? (pai.y ?? pai.ty) : no.ty; }
      no.r = no.tipo === 'nucleo' ? 62 : no.tipo === 'ramo' ? 24 : no.tipo === 'grupo' ? 14 : no.tipo === 'attendant' ? 11 : 8.5;
      no.ocultos = this.recolhidos.has(no.id) ? no.filhos.filter((f) => f.passa).length : 0;
      this.visiveis.push(no);
      let a = inicio;
      fs.forEach((f, i) => { f.indice = i; f.irmaos = fs.length; const parte = fatia * (f.peso / no.peso); por(f, a, parte); a += parte; });
    };
    raiz.tx = 0; raiz.ty = 0;
    por(raiz, -Math.PI / 2, Math.PI * 2);
    raiz.tx = 0; raiz.ty = 0;
  }

  // ---------------------------------------------------------------- comandos
  alternar(id) {
    const no = this.nos.get(id);
    if (!no || this.ehFolha(no) || no.tipo === 'nucleo') return;
    if (this.recolhidos.has(id)) this.recolhidos.delete(id); else this.recolhidos.add(id);
    this.organizar();
  }
  expandirTudo() { this.recolhidos.clear(); this.organizar(); this.ajustar(); }
  recolherTudo() { for (const no of this.nos.values()) if (no.tipo === 'grupo' || no.tipo === 'ramo') this.recolhidos.add(no.id); this.organizar(); this.ajustar(); }
  selecionar(id) { this.sel = id && this.nos.has(id) ? id : null; }

  // Leva a câmera até um nó, abrindo os ramos no caminho.
  focar(id) {
    const no = this.nos.get(id);
    if (!no) return false;
    for (let pai = this.nos.get(no.pai); pai; pai = this.nos.get(pai.pai)) this.recolhidos.delete(pai.id);
    this.organizar();
    if (!this.visiveis.includes(no)) return false;   // escondido pelo filtro
    this.sel = id;
    this.destaque = { id, ate: performance.now() + 2600 };
    this.camAlvo = { x: no.tx, y: no.ty, z: Math.max(this.cam.z, 1.05) };
    return true;
  }

  ajustar(naHora = false) {
    if (!this.visiveis.length) return;
    let minX = -120, maxX = 120, minY = -120, maxY = 120;
    for (const no of this.visiveis) { minX = Math.min(minX, no.tx); maxX = Math.max(maxX, no.tx); minY = Math.min(minY, no.ty); maxY = Math.max(maxY, no.ty); }
    const folga = 150;
    const z = Math.min(this.largura / (maxX - minX + folga * 2), this.altura / (maxY - minY + folga * 2));
    const alvo = { x: (minX + maxX) / 2, y: (minY + maxY) / 2, z: Math.min(Math.max(z, 0.12), 1.15) };
    if (naHora) { this.cam = alvo; this.camAlvo = null; } else this.camAlvo = alvo;
  }

  zoom(fator, px = this.largura / 2, py = this.altura / 2) {
    const antes = this.paraMundo(px, py);
    this.cam.z = Math.min(Math.max(this.cam.z * fator, 0.1), 3.2);
    const depois = this.paraMundo(px, py);
    this.cam.x += antes.x - depois.x; this.cam.y += antes.y - depois.y;
    this.camAlvo = null;
  }

  paraMundo(px, py) { return { x: (px - this.largura / 2) / this.cam.z + this.cam.x, y: (py - this.altura / 2) / this.cam.z + this.cam.y }; }
  paraTela(x, y) { return { x: (x - this.cam.x) * this.cam.z + this.largura / 2, y: (y - this.cam.y) * this.cam.z + this.altura / 2 }; }

  redimensionar() {
    const caixa = this.canvas.getBoundingClientRect();
    this.dpr = Math.min(window.devicePixelRatio || 1, 2);
    this.largura = Math.max(caixa.width, 50); this.altura = Math.max(caixa.height, 50);
    this.canvas.width = Math.round(this.largura * this.dpr); this.canvas.height = Math.round(this.altura * this.dpr);
  }

  destruir() { cancelAnimationFrame(this.quadroId); this.observador.disconnect(); }

  // ----------------------------------------------------------------- eventos
  noEm(px, py) {
    const m = this.paraMundo(px, py);
    let achado = null, melhor = Infinity;
    for (const no of this.visiveis) {
      const d = Math.hypot(no.x - m.x, no.y - m.y), alcance = no.r + 7 / this.cam.z;
      if (d <= alcance && d < melhor) { achado = no; melhor = d; }
    }
    return achado;
  }

  ligarEventos() {
    const c = this.canvas;
    let preso = null;
    const local = (e) => { const caixa = c.getBoundingClientRect(); return { x: e.clientX - caixa.left, y: e.clientY - caixa.top }; };

    c.addEventListener('pointerdown', (e) => {
      if (e.button !== 0) return;
      c.setPointerCapture(e.pointerId);
      preso = { ...local(e), arrastou: false };
    });
    c.addEventListener('pointermove', (e) => {
      const p = local(e);
      if (preso) {
        const dx = p.x - preso.x, dy = p.y - preso.y;
        if (!preso.arrastou && Math.hypot(dx, dy) < 4) return;
        preso.arrastou = true; this.camAlvo = null;
        this.cam.x -= dx / this.cam.z; this.cam.y -= dy / this.cam.z;
        preso.x = p.x; preso.y = p.y;
        c.style.cursor = 'grabbing';
        this.aoPassar(null);
        return;
      }
      const no = this.noEm(p.x, p.y);
      this.sobre = no ? no.id : null;
      c.style.cursor = no ? 'pointer' : 'grab';
      this.aoPassar(no, e.clientX, e.clientY);
    });
    const soltar = (e) => {
      if (!preso) return;
      const foiClique = !preso.arrastou;
      preso = null; c.style.cursor = 'grab';
      if (!foiClique || e.type === 'pointercancel') return;
      const p = local(e), no = this.noEm(p.x, p.y);
      if (no && !this.ehFolha(no) && no.tipo !== 'nucleo') this.alternar(no.id);
      this.sel = no ? no.id : null;
      this.aoSelecionar(no);
    };
    c.addEventListener('pointerup', soltar);
    c.addEventListener('pointercancel', soltar);
    c.addEventListener('pointerleave', () => { if (!preso) { this.sobre = null; this.aoPassar(null); } });
    c.addEventListener('wheel', (e) => {
      e.preventDefault();
      const p = local(e);
      this.zoom(Math.exp(-e.deltaY * (e.ctrlKey ? 0.01 : 0.0016)), p.x, p.y);
    }, { passive: false });
    c.addEventListener('keydown', (e) => {
      const passo = 70 / this.cam.z;
      const teclas = {
        ArrowLeft: () => { this.cam.x -= passo; }, ArrowRight: () => { this.cam.x += passo; },
        ArrowUp: () => { this.cam.y -= passo; }, ArrowDown: () => { this.cam.y += passo; },
        '+': () => this.zoom(1.2), '=': () => this.zoom(1.2), '-': () => this.zoom(1 / 1.2), 0: () => this.ajustar(),
      };
      if (teclas[e.key]) { e.preventDefault(); this.camAlvo = null; teclas[e.key](); }
    });
  }

  // ----------------------------------------------------------------- desenho
  cor(no) { return no.estado === 'error' ? MAPA_CORES.erro : MAPA_CORES[no.ramo || no.tipo] || MAPA_CORES.nucleo; }

  brilho(cor) {
    let folha = this.brilhos.get(cor);
    if (!folha) {
      folha = document.createElement('canvas'); folha.width = folha.height = 96;
      const g = folha.getContext('2d'), grad = g.createRadialGradient(48, 48, 0, 48, 48, 48);
      grad.addColorStop(0, `${cor}66`); grad.addColorStop(0.35, `${cor}22`); grad.addColorStop(1, `${cor}00`);
      g.fillStyle = grad; g.fillRect(0, 0, 96, 96);
      this.brilhos.set(cor, folha);
    }
    return folha;
  }

  // Pontos espalhados por igual numa esfera, cada um ligado aos vizinhos mais próximos: a "malha" do núcleo.
  montarEsfera(n) {
    const pontos = [];
    for (let i = 0; i < n; i++) {
      const y = 1 - (i / (n - 1)) * 2, raio = Math.sqrt(1 - y * y), t = i * Math.PI * (3 - Math.sqrt(5));
      pontos.push({ x: Math.cos(t) * raio, y, z: Math.sin(t) * raio });
    }
    const elos = [];
    pontos.forEach((p, i) => {
      pontos.map((q, j) => ({ j, d: (p.x - q.x) ** 2 + (p.y - q.y) ** 2 + (p.z - q.z) ** 2 }))
        .filter((v) => v.j !== i).sort((a, b) => a.d - b.d).slice(0, 3)
        .forEach((v) => { if (v.j > i) elos.push([i, v.j]); });
    });
    return { pontos, elos };
  }

  desenhar(agora) {
    const { ctx } = this, z = this.cam.z;
    if (!this.calmo) this.tempo = agora;

    if (this.camAlvo) {
      const a = this.camAlvo, f = this.calmo ? 1 : 0.12;
      this.cam.x += (a.x - this.cam.x) * f; this.cam.y += (a.y - this.cam.y) * f; this.cam.z += (a.z - this.cam.z) * f;
      if (Math.abs(a.x - this.cam.x) < 0.5 && Math.abs(a.y - this.cam.y) < 0.5 && Math.abs(a.z - this.cam.z) < 0.002) this.camAlvo = null;
    }
    for (const no of this.visiveis) {
      const f = this.calmo ? 1 : 0.16;
      no.x += (no.tx - no.x) * f; no.y += (no.ty - no.y) * f;
    }

    // Fundo: brilho suave atrás do núcleo.
    ctx.setTransform(this.dpr, 0, 0, this.dpr, 0, 0);
    ctx.clearRect(0, 0, this.largura, this.altura);
    const centro = this.paraTela(0, 0);
    const halo = ctx.createRadialGradient(centro.x, centro.y, 0, centro.x, centro.y, Math.max(this.largura, this.altura) * 0.7);
    halo.addColorStop(0, 'rgba(47, 123, 255, 0.16)'); halo.addColorStop(0.35, 'rgba(79, 216, 255, 0.045)'); halo.addColorStop(1, 'rgba(5, 7, 13, 0)');
    ctx.fillStyle = halo; ctx.fillRect(0, 0, this.largura, this.altura);

    // Daqui em diante, coordenadas do mapa.
    ctx.setTransform(z * this.dpr, 0, 0, z * this.dpr, (this.largura / 2 - this.cam.x * z) * this.dpr, (this.altura / 2 - this.cam.y * z) * this.dpr);

    // Órbitas.
    ctx.lineWidth = 1 / z;
    (this.aneis || []).forEach((r, i) => {
      ctx.beginPath(); ctx.arc(0, 0, r, 0, Math.PI * 2);
      ctx.setLineDash([2 / z, (10 + i * 4) / z]); ctx.strokeStyle = 'rgba(120, 170, 255, 0.13)'; ctx.stroke();
    });
    ctx.setLineDash([]);

    // Caminho em destaque: do nó escolhido (ou sob o mouse) até o núcleo.
    const aceso = new Set();
    for (const id of [this.sel, this.sobre]) for (let no = this.nos.get(id); no; no = this.nos.get(no.pai)) aceso.add(no.id);

    // Conexões.
    let pulsos = 0;
    for (const no of this.visiveis) {
      const pai = this.nos.get(no.pai);
      if (!pai) continue;
      const cor = this.cor(no), forte = aceso.has(no.id) && aceso.has(pai.id), apagado = no.estado === 'disabled';
      ctx.beginPath(); ctx.moveTo(pai.x, pai.y); ctx.lineTo(no.x, no.y);
      ctx.strokeStyle = cor; ctx.globalAlpha = forte ? 0.95 : apagado ? 0.1 : no.prof === 1 ? 0.42 : 0.2;
      ctx.lineWidth = (forte ? 2 : no.prof === 1 ? 1.5 : 1) / Math.max(z, 0.5);
      ctx.stroke();
      // Pulso: um ponto de luz que corre do centro para a ponta.
      if (!this.calmo && !apagado && (no.prof <= 2 || forte) && pulsos < 140) {
        pulsos++;
        const fase = ((this.somaDe(no.id) % 997) / 997 + this.tempo * (forte ? 0.00042 : 0.00016)) % 1;
        ctx.globalAlpha = Math.sin(fase * Math.PI) * (forte ? 1 : 0.75);
        ctx.fillStyle = cor;
        ctx.beginPath(); ctx.arc(pai.x + (no.x - pai.x) * fase, pai.y + (no.y - pai.y) * fase, (forte ? 2.6 : 1.9) / Math.max(z, 0.6), 0, Math.PI * 2); ctx.fill();
      }
    }
    ctx.globalAlpha = 1;

    // Ligações feitas à mão entre conhecimentos: arcos tracejados.
    ctx.setLineDash([5 / z, 5 / z]); ctx.lineWidth = 1.2 / Math.max(z, 0.5);
    for (const l of this.ligacoes) {
      const a = this.nos.get(l.a), b = this.nos.get(l.b);
      if (!a || !b || !this.visiveis.includes(a) || !this.visiveis.includes(b)) continue;
      const forte = this.sel === a.id || this.sel === b.id || this.sobre === a.id || this.sobre === b.id;
      ctx.beginPath(); ctx.moveTo(a.x, a.y); ctx.quadraticCurveTo((a.x + b.x) * 0.28, (a.y + b.y) * 0.28, b.x, b.y);
      ctx.strokeStyle = '#c4b5ff'; ctx.globalAlpha = forte ? 0.95 : 0.4; ctx.stroke();
    }
    ctx.setLineDash([]); ctx.globalAlpha = 1;

    for (const no of this.visiveis) if (no.tipo !== 'nucleo') this.desenharNo(no);
    this.desenharNucleo();

    // Rótulos, em pixels de tela para o texto ficar nítido em qualquer zoom.
    ctx.setTransform(this.dpr, 0, 0, this.dpr, 0, 0);
    ctx.textBaseline = 'middle'; ctx.shadowColor = '#05070d'; ctx.shadowBlur = 8;
    // Com poucos nós cabe o nome de todos; com muitos, os nomes das pontas só aparecem de perto.
    const zFolha = this.visiveis.length <= 45 ? 0.4 : 0.72;
    // Rótulo que cairia em cima de outro não é desenhado (os dos ramos e do nó escolhido têm prioridade).
    const ocupados = [];
    const ordem = (no) => (no.id === this.sel || no.id === this.sobre ? 0 : no.tipo === 'ramo' ? 1 : no.tipo === 'grupo' ? 2 : 3);
    for (const no of this.visiveis.slice().sort((a, b) => ordem(a) - ordem(b))) {
      if (no.tipo === 'nucleo') continue;
      const forte = no.id === this.sel || no.id === this.sobre || (this.destaque && this.destaque.id === no.id);
      const vizinho = no.pai === this.sel || no.pai === this.sobre;
      const mostra = no.tipo === 'ramo' || forte || (no.tipo === 'grupo' ? z >= 0.34 : z >= zFolha || (vizinho && z >= 0.4));
      if (!mostra) continue;
      const p = this.paraTela(no.x, no.y), direita = Math.cos(no.ang) >= 0, folga = no.r * z + 9;
      let rotulo = no.rotulo.length > 30 ? `${no.rotulo.slice(0, 29)}…` : no.rotulo;
      if (no.tipo === 'ramo') {
        ctx.font = '600 12.5px Sora, Inter, system-ui, sans-serif'; ctx.fillStyle = '#e6eefb';
        rotulo = rotulo.toUpperCase();
      } else {
        ctx.font = `${forte ? '600' : '500'} 12px Inter, system-ui, sans-serif`;
        ctx.fillStyle = forte ? '#ffffff' : no.estado === 'disabled' ? '#5f6f8c' : '#a9b9d4';
      }
      const largura = ctx.measureText(rotulo).width, x0 = direita ? p.x + folga : p.x - folga - largura;
      const caixa = [x0 - 4, p.y - 9, x0 + largura + 4, p.y + 9];
      if (!forte && ocupados.some((o) => caixa[0] < o[2] && caixa[2] > o[0] && caixa[1] < o[3] && caixa[3] > o[1])) continue;
      ocupados.push(caixa);
      ctx.textAlign = direita ? 'left' : 'right';
      ctx.fillText(rotulo, p.x + (direita ? folga : -folga), p.y);
    }
    ctx.shadowBlur = 0;
    if (this.destaque && agora > this.destaque.ate) this.destaque = null;
  }

  somaDe(id) { let h = 0; for (let i = 0; i < id.length; i++) h = (h * 31 + id.charCodeAt(i)) >>> 0; return h; }

  desenharNo(no) {
    const { ctx } = this, z = this.cam.z, cor = this.cor(no), r = no.r;
    const apagado = no.estado === 'disabled', escolhido = no.id === this.sel, sobre = no.id === this.sobre;
    ctx.globalAlpha = apagado ? 0.4 : 1;
    if (!apagado) { const b = r * (escolhido || sobre ? 4.4 : 3.2); ctx.drawImage(this.brilho(cor), no.x - b, no.y - b, b * 2, b * 2); }

    ctx.beginPath(); ctx.arc(no.x, no.y, r, 0, Math.PI * 2);
    ctx.fillStyle = '#0a1120'; ctx.fill();
    if (no.estado === 'pending') ctx.setLineDash([3.2, 3.2]);
    ctx.lineWidth = escolhido ? 2.6 : 1.7; ctx.strokeStyle = escolhido ? '#ffffff' : cor; ctx.stroke();
    ctx.setLineDash([]);

    if (this.ehFolha(no)) {
      if (!apagado && no.estado !== 'pending') { ctx.beginPath(); ctx.arc(no.x, no.y, r * 0.42, 0, Math.PI * 2); ctx.fillStyle = cor; ctx.fill(); }
      // Sugestão pendente: anel que respira, para chamar a revisão.
      if (no.estado === 'pending' && !this.calmo) {
        const t = (this.tempo * 0.0011 + (this.somaDe(no.id) % 10) / 10) % 1;
        ctx.globalAlpha = (1 - t) * 0.6; ctx.beginPath(); ctx.arc(no.x, no.y, r + t * 9, 0, Math.PI * 2); ctx.lineWidth = 1; ctx.strokeStyle = cor; ctx.stroke();
      }
      // Atualizado há pouco: um satélite claro.
      if (no.novo && !apagado) { ctx.globalAlpha = 1; ctx.beginPath(); ctx.arc(no.x + r * 0.9, no.y - r * 0.9, 2.6, 0, Math.PI * 2); ctx.fillStyle = '#ffffff'; ctx.fill(); }
    } else {
      // Ramo ou grupo: mostra quantos itens tem; recolhido, ganha um segundo anel.
      if (no.ocultos) { ctx.beginPath(); ctx.arc(no.x, no.y, r + 4, 0, Math.PI * 2); ctx.lineWidth = 1; ctx.globalAlpha = 0.55; ctx.strokeStyle = cor; ctx.stroke(); ctx.globalAlpha = 1; }
      if (z > 0.3) {
        ctx.fillStyle = cor; ctx.textAlign = 'center'; ctx.textBaseline = 'middle';
        ctx.font = `600 ${no.tipo === 'ramo' ? 13 : 10}px Sora, Inter, system-ui, sans-serif`;
        ctx.fillText(String(no.n ?? no.filhos.length), no.x, no.y + 0.5);
      }
    }
    if (this.destaque && this.destaque.id === no.id && !this.calmo) {
      const t = (this.tempo * 0.0016) % 1;
      ctx.globalAlpha = 1 - t; ctx.beginPath(); ctx.arc(no.x, no.y, r + 6 + t * 26, 0, Math.PI * 2); ctx.lineWidth = 2; ctx.strokeStyle = '#ffffff'; ctx.stroke();
    }
    ctx.globalAlpha = 1;
  }

  desenharNucleo() {
    const { ctx } = this, t = this.tempo, R = 62, cor = MAPA_CORES.nucleo;
    const escolhido = this.sel === 'ia' || this.sobre === 'ia';
    ctx.drawImage(this.brilho(cor), -R * 3.4, -R * 3.4, R * 6.8, R * 6.8);
    ctx.drawImage(this.brilho('#9b8cff'), -R * 2.2, -R * 2.2, R * 4.4, R * 4.4);

    // Anéis que giram em velocidades e sentidos diferentes.
    const aneis = [[R + 14, 0.00022, [46, 14], 1.4, 0.75], [R + 26, -0.00013, [4, 9], 1, 0.5], [R + 40, 0.00007, [90, 60], 1, 0.3]];
    for (const [raio, vel, traco, largura, alfa] of aneis) {
      ctx.save(); ctx.rotate(t * vel);
      ctx.beginPath(); ctx.arc(0, 0, raio, 0, Math.PI * 2);
      ctx.setLineDash(traco); ctx.lineWidth = largura; ctx.globalAlpha = alfa * (escolhido ? 1.3 : 1); ctx.strokeStyle = cor; ctx.stroke();
      ctx.restore();
    }
    ctx.setLineDash([]);

    // Esfera de pontos: gira no eixo vertical, levemente inclinada.
    const giro = t * 0.00028, inclina = 0.42, raio = R - 10;
    const cg = Math.cos(giro), sg = Math.sin(giro), ci = Math.cos(inclina), si = Math.sin(inclina);
    const proj = this.esfera.pontos.map((p) => {
      const x = p.x * cg + p.z * sg, z1 = -p.x * sg + p.z * cg;
      const y = p.y * ci - z1 * si, zf = p.y * si + z1 * ci;
      return { x: x * raio, y: y * raio, z: zf };
    });
    ctx.lineWidth = 0.7;
    for (const [a, b] of this.esfera.elos) {
      const pa = proj[a], pb = proj[b], fundo = (pa.z + pb.z) / 2;
      ctx.globalAlpha = 0.1 + (fundo + 1) * 0.2;
      ctx.strokeStyle = fundo > 0 ? cor : '#5b8cff';
      ctx.beginPath(); ctx.moveTo(pa.x, pa.y); ctx.lineTo(pb.x, pb.y); ctx.stroke();
    }
    for (const p of proj) {
      ctx.globalAlpha = 0.25 + (p.z + 1) * 0.375;
      ctx.fillStyle = p.z > 0.2 ? '#e8fbff' : cor;
      ctx.beginPath(); ctx.arc(p.x, p.y, 0.9 + (p.z + 1) * 0.7, 0, Math.PI * 2); ctx.fill();
    }
    // Batimento do centro.
    const bate = this.calmo ? 0.5 : (Math.sin(t * 0.0021) + 1) / 2;
    const miolo = ctx.createRadialGradient(0, 0, 0, 0, 0, 20 + bate * 6);
    miolo.addColorStop(0, 'rgba(232, 251, 255, 0.9)'); miolo.addColorStop(0.4, 'rgba(79, 216, 255, 0.35)'); miolo.addColorStop(1, 'rgba(79, 216, 255, 0)');
    ctx.globalAlpha = 0.75; ctx.fillStyle = miolo; ctx.beginPath(); ctx.arc(0, 0, 26 + bate * 6, 0, Math.PI * 2); ctx.fill();
    ctx.globalAlpha = 1;
  }
}
