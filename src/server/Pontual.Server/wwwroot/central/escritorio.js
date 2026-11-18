// Visão "Escritório" em pixel art. O desenho é o do protótipo aprovado
// (prototipos/index.html); aqui cada mesa é uma máquina real, até 10 por tela.
// Não usa dados do servidor como HTML: só desenha no canvas.
'use strict';

const Escritorio = (() => {
  const W = 400, H = 224, MAXIMO = 10;
  const PELES = ['#f4cba4', '#e3ab7c', '#bb7c52', '#8b5a3d'];
  const CABELOS = ['#3b2a22', '#7b4b26', '#c98b2e', '#2d2d3c', '#a43d2e'];
  const BLUSAS = ['#1976d2', '#2e9e5b', '#0c8fd0', '#7c5cc4', '#d97706', '#0f766e'];
  const TELA = { online: ['#9ff0c4', '#7fe0ae'], offline: ['#232a38', '#232a38'], alerta: ['#ffd166', '#ff9f43'], manutencao: ['#9db8ff', '#9db8ff'] };

  let cx = null;
  const R = (c, x, y, w, h) => { cx.fillStyle = c; cx.fillRect(x, y, w, h); };
  const posicao = (i) => ({ x: 46 + (i % 5) * 77, y: 104 + Math.floor(i / 5) * 76 });

  function pessoa(x, y, v, quadro, parada) {
    const pele = PELES[v % 4], cabelo = CABELOS[(v * 3 + 1) % 5], blusa = BLUSAS[(v * 5 + 2) % 6];
    R('#2b1d1f', x + 1, y - 1, 10, 10); R(pele, x + 2, y + 2, 8, 6);
    R(cabelo, x + 2, y, 8, 3); R(cabelo, x + 2, y + 3, 1, 3); R(cabelo, x + 9, y + 3, 1, 3);
    R('#2b1d1f', x + 4, y + 5, 1, 2); R('#2b1d1f', x + 7, y + 5, 1, 2);
    R('#2b1d1f', x, y + 9, 12, 7); R(blusa, x + 1, y + 9, 10, 6);
    if (!parada) { const d = quadro % 2; R(pele, x + 2 + d, y + 14, 2, 1); R(pele, x + 8 - d, y + 14, 2, 1); }
  }

  // Uma mesa por atendente. Ela pode ter computador, celular ou os dois:
  // - no computador: tela acesa e mãos no teclado; o celular, se houver, fica deitado na mesa;
  // - só no celular: ela segura o aparelho; o monitor, se houver, fica apagado.
  const emUso = (estado) => estado === 'online' || estado === 'alerta';
  function estacao(m, { x, y }, quadro) {
    const pc = m.pc || null, cel = m.celular || null, pisca = quadro % 2 === 0;
    const noPc = emUso(pc), noCelular = emUso(cel);
    const alerta = pc === 'alerta' || cel === 'alerta', manutencao = pc === 'manutencao' || cel === 'manutencao';
    const corDaTela = (estado) => (TELA[estado] || TELA.offline)[pisca ? 0 : 1];
    cx.fillStyle = 'rgba(40,22,12,0.25)'; cx.beginPath(); cx.ellipse(x, y + 12, 24, 4, 0, 0, 7); cx.fill();
    R('#1b2333', x - 2, y - 24, 16, 15); R('#3d5488', x - 1, y - 23, 14, 13);
    // As mãos só se mexem no teclado: com alerta ou só no celular, ficam paradas.
    if (noPc || noCelular) pessoa(x, y - 25, m.visual, quadro, alerta || !noPc);
    R('#4a2c18', x - 24, y - 10, 48, 5); R('#b07c48', x - 23, y - 10, 46, 3); R('#cd9a62', x - 23, y - 10, 46, 1);
    R('#4a2c18', x - 22, y - 5, 44, 12); R('#8a5a34', x - 21, y - 5, 42, 10); R('#74492a', x - 18, y - 3, 14, 6); R('#74492a', x + 4, y - 3, 14, 6);
    R('#4a2c18', x - 21, y + 7, 4, 4); R('#4a2c18', x + 17, y + 7, 4, 4);
    if (pc) {
      R('#141a26', x - 22, y - 25, 18, 13); R('#141a26', x - 15, y - 12, 4, 2);
      R(corDaTela(pc), x - 20, y - 23, 14, 9);
      if (pc === 'online') { R('rgba(13,71,161,0.45)', x - 19, y - 21, 7, 1); R('rgba(13,71,161,0.45)', x - 19, y - 19, 10, 1); R('rgba(13,71,161,0.45)', x - 19, y - 17, 5, 1); }
      R('#2c3344', x - 1, y - 9, 14, 2);
    }
    if (cel && noCelular && !noPc) {
      // Celular na mão, na frente do corpo, com as duas mãos segurando.
      const pele = PELES[m.visual % 4];
      R('#141a26', x + 3, y - 18, 6, 10); R(corDaTela(cel), x + 4, y - 17, 4, 7);
      R(pele, x + 2, y - 12, 1, 3); R(pele, x + 9, y - 12, 1, 3);
    } else if (cel) {
      // Celular deitado na mesa, à direita do teclado: aceso se estiver ligado.
      R('#141a26', x + 15, y - 11, 7, 4); R(corDaTela(cel), x + 16, y - 10, 5, 2);
    }
    if (alerta || manutencao) {
      const by = y - 44 + (pisca ? 0 : 1);
      R('#2b1d1f', x + 2, by, 11, 11); R('#ffffff', x + 3, by + 1, 9, 9); R('#2b1d1f', x + 6, by + 11, 2, 2);
      if (alerta) { R('#d97706', x + 7, by + 2, 2, 5); R('#d97706', x + 7, by + 8, 2, 1); }
      else { R('#1d4ed8', x + 7, by + 2, 2, 7); R('#1d4ed8', x + 4, by + 5, 7, 2); }
    }
    // Em manutenção, quem aparece ao lado da mesa é a pessoa do TI.
    if (manutencao) pessoa(x + 24, y - 18, 7, quadro, true);
  }

  // maquinas: uma por mesa: { pc, celular, visual }. pc e celular são o estado do aparelho
  // ('online' | 'offline' | 'alerta' | 'manutencao') ou null quando a atendente não tem aquele aparelho.
  function desenhar(canvas, maquinas, quadro) {
    cx = canvas.getContext('2d');
    R('#eef3f9', 0, 0, W, 40);
    for (let x = 6; x < W; x += 16) R('#e2eaf3', x, 0, 2, 40);
    R('#2e416d', 0, 40, W, 9); R('#3d5488', 0, 40, W, 2); R('#1b2a4a', 0, 49, W, 2); R('#c9a273', 0, 51, W, H - 51);
    for (let y = 51, n = 0; y < H; y += 11, n += 1) { R('#b88d5c', 0, y + 10, W, 1); R('#d2ad80', 0, y, W, 1); for (let x = (n % 2) * 28 + 14; x < W; x += 56) R('#b88d5c', x, y, 1, 10); }
    R('rgba(27,18,8,0.18)', 0, 51, W, 3);
    for (const x of [28, 118, 248, 338]) {
      R('#ffffff', x, 6, 34, 26); R('#a8d8ee', x + 2, 8, 30, 22); R('#cdeaf4', x + 2, 20, 30, 10); R('#ffffff', x + 16, 8, 2, 22); R('#ffffff', x + 2, 18, 30, 2); R('#ffffff', x + 6, 11, 7, 2);
      cx.fillStyle = 'rgba(255,244,200,0.16)'; cx.beginPath(); cx.moveTo(x + 2, 52); cx.lineTo(x + 32, 52); cx.lineTo(x + 46, 92); cx.lineTo(x + 12, 92); cx.fill();
    }
    for (const [x, y] of [[8, 60], [W - 20, 60], [8, H - 30], [W - 20, H - 30]]) { R('#2f7d4a', x, y, 12, 10); R('#43a35a', x + 2, y - 4, 8, 8); R('#7dcf8f', x + 4, y - 2, 3, 3); R('#2b1d1f', x + 1, y + 10, 10, 8); R('#c8553d', x + 2, y + 11, 8, 6); }
    R('#0d47a1', 160, H - 22, 80, 16); R('#1976d2', 162, H - 20, 76, 12); R('#10b981', 166, H - 16, 68, 4);
    maquinas.slice(0, MAXIMO).forEach((m, i) => estacao(m, posicao(i), quadro));
  }

  // Área clicável de cada mesa, em porcentagem do canvas.
  function areaDaMesa(i) {
    const { x, y } = posicao(i);
    return { left: `${((x - 36) / W) * 100}%`, top: `${((y - 46) / H) * 100}%`, width: `${(72 / W) * 100}%`, height: `${(74 / H) * 100}%` };
  }

  return { MAXIMO, desenhar, areaDaMesa };
})();
