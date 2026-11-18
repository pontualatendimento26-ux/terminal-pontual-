// Pixel art original da Central dos Agentes, desenhada por código.
// Nenhum sprite vem de outro jogo: personagens, móveis e cenário são gerados
// aqui, pixel a pixel, em texturas de canvas.

import Phaser from 'phaser';

type Ctx = CanvasRenderingContext2D;

export const CHAR_W = 16;
export const CHAR_H = 18;
export const CHAR_VARIANTS = 12;

/** Quadros do personagem, na ordem em que ficam na textura. */
export const FRAME = {
  idle: 0,
  blink: 1,
  typeA: 2,
  typeB: 3,
  lookAside: 4,
  cheer: 5,
  smile: 6,
  panic: 7,
  shock: 8,
} as const;

const OUTLINE = '#2b1d1f';

const SKINS: [string, string][] = [
  ['#f4cba4', '#dba67c'],
  ['#e3ab7c', '#c4895a'],
  ['#bb7c52', '#98603c'],
  ['#8b5a3d', '#6d4329'],
];
const HAIRS = ['#3b2a22', '#7b4b26', '#c98b2e', '#a43d2e', '#2d2d3c', '#d9d2c4'];
// Roupa social: camisas e paletós em tons de escritório.
const SHIRTS: [string, string][] = [
  ['#f2f2ee', '#cfd2d4'], // camisa branca
  ['#a9c8e8', '#86a7c8'], // camisa azul-clara
  ['#2f3f5e', '#223049'], // paletó azul-marinho
  ['#5b6066', '#45494f'], // paletó grafite
  ['#d8d2c4', '#b8b1a2'], // camisa bege
  ['#7a2f3a', '#5e232c'], // blazer vinho
];
const TIES: [string, string][] = [
  ['#b0303a', '#8a2129'],
  ['#2f4f8a', '#223a68'],
  ['#3c6e4a', '#2b5236'],
  ['#c08c30', '#9a6e20'],
];

function rect(ctx: Ctx, color: string, x: number, y: number, w: number, h: number): void {
  ctx.fillStyle = color;
  ctx.fillRect(x, y, w, h);
}

function texture(
  scene: Phaser.Scene,
  key: string,
  width: number,
  height: number,
  draw: (ctx: Ctx, tex: Phaser.Textures.CanvasTexture) => void,
): void {
  if (scene.textures.exists(key)) return;
  const tex = scene.textures.createCanvas(key, width, height);
  if (!tex) return;
  const ctx = tex.getContext();
  ctx.imageSmoothingEnabled = false;
  draw(ctx, tex);
  tex.refresh();
}

// ---------------------------------------------------------------- personagens

const face = (inner: string) => `...o${inner}o...`;
const chin = (inner: string) => `....o${inner}o....`;
const wide = (inner: string) => `.o${inner}o.`;
const mid = (inner: string) => `..o${inner}o..`;

const HEAD_TOP = [
  '................',
  '.....oooooo.....',
  '....ohhhhhho....',
  '...ohhhhhhhho...',
  '...ohhhhhhhho...',
  face('hhsssshh'),
  face('hssssssh'),
];

type Expression = [string, string, string, string];
const EXPRESSIONS: Record<string, Expression> = {
  normal: ['sesssses', 'sesssses', 'sSssssSs', 'ssmmss'],
  blink: ['ssssssss', 'sSssssSs', 'sSssssSs', 'ssmmss'],
  aside: ['ssesssse', 'ssesssse', 'sSssssSs', 'ssmmss'],
  happy: ['sesssses', 'ssssssss', 'sSssssSs', 'smmmms'],
  shock: ['sesssses', 'sesssses', 'ssssssss', 'sMMMMs'],
};

const BOTTOM = '..oooooooooooo..';
const BODIES: Record<string, string[]> = {
  idle: [mid('cccccccccc'), wide('cccccccccccc'), wide('CccccccccccC'), wide('sCccccccccCs'), mid('CccccccccC'), BOTTOM],
  typeA: [mid('cccccccccc'), wide('cccccccccccc'), wide('CccccccccccC'), wide('CccccccccccC'), mid('CcssccsscC'), BOTTOM],
  typeB: [mid('cccccccccc'), wide('cccccccccccc'), wide('CccccccccccC'), wide('CccccccccccC'), mid('CssccccssC'), BOTTOM],
  noArms: [mid('cccccccccc'), mid('cccccccccc'), mid('CccccccccC'), mid('CccccccccC'), mid('CccccccccC'), BOTTOM],
};

type Overlay = [number, number, string][];
const mirror = (points: Overlay): Overlay => [...points, ...points.map(([x, y, c]) => [15 - x, y, c] as [number, number, string])];

// Braços erguidos em comemoração.
const ARMS_UP = mirror([
  [1, 3, 's'], [1, 4, 's'], [2, 4, 's'],
  [1, 5, 'c'], [2, 5, 'c'], [1, 6, 'c'], [2, 6, 'c'],
  [2, 7, 'c'], [2, 8, 'C'], [2, 9, 'c'], [2, 10, 'C'], [2, 11, 'c'],
]);
// Mãos na cabeça.
const HANDS_ON_HEAD = mirror([
  [2, 4, 's'], [2, 5, 's'], [3, 4, 's'],
  [2, 6, 'c'], [1, 7, 'c'], [1, 8, 'C'], [1, 9, 'c'], [1, 10, 'C'], [2, 11, 'c'], [1, 11, 'c'],
]);

function buildFrame(expression: Expression, body: string[], overlay: Overlay = []): string[] {
  const rows = [
    ...HEAD_TOP,
    face(expression[0]),
    face(expression[1]),
    face(expression[2]),
    chin(expression[3]),
    '.....oSSSSo.....',
    ...body,
  ].map((row) => row.split(''));
  // Gola (w) e gravata (k = nó, t = corpo, T = ponta). Sem gravata, a paleta
  // troca essas letras por pele e camisa: fica uma gola aberta.
  for (const [x, y, c] of COLLAR_AND_TIE) rows[y][x] = c;
  for (const [x, y, c] of overlay) rows[y][x] = c;
  return rows.map((row) => row.join(''));
}

const COLLAR_AND_TIE: Overlay = [
  [6, 12, 'w'], [9, 12, 'w'], [5, 12, 'w'], [10, 12, 'w'],
  [7, 12, 'k'], [8, 12, 'k'],
  [7, 13, 't'], [8, 13, 't'],
  [7, 14, 't'], [8, 14, 't'],
  [7, 15, 'T'], [8, 15, 'T'],
];

const FRAMES: string[][] = [
  buildFrame(EXPRESSIONS.normal, BODIES.idle),
  buildFrame(EXPRESSIONS.blink, BODIES.idle),
  buildFrame(EXPRESSIONS.normal, BODIES.typeA),
  buildFrame(EXPRESSIONS.normal, BODIES.typeB),
  buildFrame(EXPRESSIONS.aside, BODIES.idle),
  buildFrame(EXPRESSIONS.happy, BODIES.noArms, ARMS_UP),
  buildFrame(EXPRESSIONS.happy, BODIES.idle),
  buildFrame(EXPRESSIONS.shock, BODIES.noArms, HANDS_ON_HEAD),
  buildFrame(EXPRESSIONS.shock, BODIES.idle),
];

function characterPalette(variant: number): Record<string, string> {
  const [skin, skinShade] = SKINS[variant % SKINS.length];
  const hair = HAIRS[(variant * 5 + 1) % HAIRS.length];
  const [shirt, shirtShade] = SHIRTS[(variant * 7 + 2) % SHIRTS.length];
  const collar = shirt === '#f2f2ee' ? '#ffffff' : '#f2f2ee';
  // Dois em cada três usam gravata; os demais, gola aberta.
  const [tie, tieShade] = variant % 3 === 2 ? [shirt, shirt] : TIES[variant % TIES.length];
  const knot = variant % 3 === 2 ? skin : tie;
  return {
    o: OUTLINE, h: hair, s: skin, S: skinShade, e: OUTLINE, m: '#9c4a3c', M: '#4a1818',
    c: shirt, C: shirtShade, w: collar, k: knot, t: tie, T: tieShade,
  };
}

function buildCharacters(scene: Phaser.Scene): void {
  for (let variant = 0; variant < CHAR_VARIANTS; variant += 1) {
    const palette = characterPalette(variant);
    texture(scene, `char${variant}`, CHAR_W * FRAMES.length, CHAR_H, (ctx, tex) => {
      FRAMES.forEach((frame, index) => {
        frame.forEach((row, y) => {
          for (let x = 0; x < row.length; x += 1) {
            const color = palette[row[x]];
            if (color) rect(ctx, color, index * CHAR_W + x, y, 1, 1);
          }
        });
        tex.add(index, 0, index * CHAR_W, 0, CHAR_W, CHAR_H);
      });
    });
  }
}

/** Escolhe a aparência de um agente de forma estável a partir da sua chave. */
export function variantFor(key: string): number {
  let hash = 0;
  for (let i = 0; i < key.length; i += 1) hash = (hash * 31 + key.charCodeAt(i)) >>> 0;
  return hash % CHAR_VARIANTS;
}

// -------------------------------------------------------------------- cenário

// Paleta do escritório: cinzas frios, carpete, laminado branco e metal.
const METAL = '#8e959b';
const METAL_DARK = '#5f666c';
const METAL_LIGHT = '#b9c0c6';
const LAMINATE = '#eef0f1';

function buildRoom(scene: Phaser.Scene): void {
  // Carpete em placas, cada uma com um leve tom diferente.
  texture(scene, 'floor', 32, 32, (ctx) => {
    rect(ctx, '#7b838d', 0, 0, 32, 32);
    rect(ctx, '#757d87', 16, 0, 16, 16);
    rect(ctx, '#757d87', 0, 16, 16, 16);
    rect(ctx, '#6a717a', 0, 0, 32, 1);
    rect(ctx, '#6a717a', 0, 16, 32, 1);
    rect(ctx, '#6a717a', 0, 0, 1, 32);
    rect(ctx, '#6a717a', 16, 0, 1, 32);
    for (const [x, y] of [[4, 5], [11, 9], [7, 13], [21, 3], [27, 11], [19, 8], [5, 22], [12, 27], [24, 20], [29, 26], [20, 29]]) {
      rect(ctx, '#848c96', x, y, 1, 1);
    }
  });

  // Parede pintada de cinza-claro, com rodapé.
  texture(scene, 'wall', 16, 48, (ctx) => {
    rect(ctx, '#d9dde0', 0, 0, 16, 42);
    rect(ctx, '#d2d7da', 7, 0, 1, 42);
    rect(ctx, '#c7cdd1', 0, 0, 16, 2);
    rect(ctx, METAL_LIGHT, 0, 41, 16, 1);
    rect(ctx, '#6c737a', 0, 42, 16, 4);
    rect(ctx, '#4a5056', 0, 46, 16, 2);
  });

  // Janela de alumínio com persiana e prédios lá fora.
  texture(scene, 'window', 40, 32, (ctx) => {
    rect(ctx, METAL_DARK, 0, 2, 40, 28);
    rect(ctx, '#9cc7e2', 2, 4, 36, 22);
    rect(ctx, '#c4e0f0', 2, 4, 36, 6);
    const towers: [number, number, number, string][] = [
      [3, 15, 6, '#6f8aa3'], [9, 11, 5, '#5d7790'], [14, 17, 7, '#7a94ab'],
      [21, 9, 6, '#5d7790'], [27, 14, 5, '#6f8aa3'], [32, 12, 6, '#5d7790'],
    ];
    for (const [x, top, w, color] of towers) {
      rect(ctx, color, x, top, w, 26 - top);
      for (let y = top + 2; y < 25; y += 3) {
        for (let wx = x + 1; wx < x + w - 1; wx += 2) rect(ctx, '#e8eef2', wx, y, 1, 1);
      }
    }
    // persiana meio baixada
    for (let y = 4; y < 13; y += 2) rect(ctx, '#e6e9eb', 2, y, 36, 1);
    rect(ctx, METAL_LIGHT, 2, 13, 36, 1);
    rect(ctx, METAL, 19, 4, 2, 22);
    rect(ctx, METAL_LIGHT, 0, 1, 40, 2);
    rect(ctx, '#c4cacf', 0, 26, 40, 3);
    rect(ctx, METAL, 0, 29, 40, 1);
  });

  // Quadro branco com anotações e um gráfico.
  texture(scene, 'board', 40, 24, (ctx) => {
    rect(ctx, METAL, 0, 0, 40, 22);
    rect(ctx, '#f7f8f8', 1, 1, 38, 20);
    rect(ctx, '#3a6fb0', 4, 4, 12, 1);
    rect(ctx, '#3a6fb0', 4, 7, 9, 1);
    rect(ctx, '#3a6fb0', 4, 10, 11, 1);
    rect(ctx, '#c8402c', 4, 14, 7, 1);
    rect(ctx, '#3c8a50', 22, 14, 3, 4);
    rect(ctx, '#3c8a50', 26, 11, 3, 7);
    rect(ctx, '#3c8a50', 30, 7, 3, 11);
    rect(ctx, '#2b2f35', 21, 18, 14, 1);
    rect(ctx, '#2b2f35', 21, 5, 1, 13);
    rect(ctx, METAL_DARK, 6, 22, 28, 2);
    rect(ctx, '#c8402c', 12, 21, 3, 1);
    rect(ctx, '#3a6fb0', 17, 21, 3, 1);
  });

  texture(scene, 'clock', 12, 12, (ctx) => {
    rect(ctx, METAL_DARK, 2, 0, 8, 12);
    rect(ctx, METAL_DARK, 0, 2, 12, 8);
    rect(ctx, '#ffffff', 2, 1, 8, 10);
    rect(ctx, '#ffffff', 1, 2, 10, 8);
    rect(ctx, OUTLINE, 5, 3, 1, 4);
    rect(ctx, OUTLINE, 6, 6, 3, 1);
  });

  // Mesa de reunião com cadeiras, na frente do escritório.
  texture(scene, 'meeting', 124, 46, (ctx) => {
    const chairAt = (x: number, y: number) => {
      rect(ctx, '#1e2126', x, y, 12, 7);
      rect(ctx, '#2f333a', x + 1, y + 1, 10, 5);
      rect(ctx, '#3d4148', x + 2, y + 1, 8, 1);
    };
    for (let x = 14; x < 110; x += 20) {
      chairAt(x, 0);
      chairAt(x, 39);
    }
    rect(ctx, '#1e2126', 0, 17, 6, 12);
    rect(ctx, '#2f333a', 1, 18, 4, 10);
    rect(ctx, '#1e2126', 118, 17, 6, 12);
    rect(ctx, '#2f333a', 119, 18, 4, 10);
    rect(ctx, METAL_DARK, 6, 6, 112, 34);
    rect(ctx, '#d6dbdf', 7, 7, 110, 31);
    rect(ctx, LAMINATE, 7, 7, 110, 2);
    rect(ctx, '#b9c0c6', 7, 35, 110, 3);
    // notebook, papéis, telefone de conferência e copo
    rect(ctx, '#2b2f35', 22, 12, 14, 9);
    rect(ctx, '#86b6d8', 23, 13, 12, 6);
    rect(ctx, '#ffffff', 50, 22, 9, 11);
    rect(ctx, '#c9cfd4', 51, 24, 6, 1);
    rect(ctx, '#c9cfd4', 51, 26, 7, 1);
    rect(ctx, '#ffffff', 61, 23, 9, 11);
    rect(ctx, '#2b2f35', 59, 15, 7, 4);
    rect(ctx, '#7dbb72', 61, 16, 1, 1);
    rect(ctx, '#ffffff', 88, 13, 4, 5);
    rect(ctx, '#86b6d8', 88, 13, 4, 1);
    rect(ctx, '#2b2f35', 96, 24, 14, 9);
    rect(ctx, '#86b6d8', 97, 25, 12, 6);
  });
}

function buildFurniture(scene: Phaser.Scene): void {
  // Mesa de laminado branco com pés de metal.
  texture(scene, 'desk', 44, 24, (ctx) => {
    rect(ctx, METAL_DARK, 0, 0, 44, 11);
    rect(ctx, LAMINATE, 1, 1, 42, 9);
    rect(ctx, '#ffffff', 1, 1, 42, 1);
    rect(ctx, '#d6dbdf', 1, 8, 42, 2);
    rect(ctx, METAL_DARK, 3, 11, 38, 9);
    rect(ctx, '#c9cfd4', 4, 11, 36, 8);
    rect(ctx, '#b9c0c6', 4, 17, 36, 2);
    rect(ctx, METAL, 1, 11, 2, 13);
    rect(ctx, METAL, 41, 11, 2, 13);
    rect(ctx, METAL_DARK, 0, 22, 4, 2);
    rect(ctx, METAL_DARK, 40, 22, 4, 2);
    // teclado e mouse
    rect(ctx, '#2c2c3a', 20, 5, 14, 4);
    rect(ctx, '#55556a', 21, 6, 12, 2);
    for (let x = 22; x < 33; x += 2) rect(ctx, '#9a9ab2', x, 6, 1, 1);
    rect(ctx, '#2c2c3a', 36, 6, 2, 3);
    // papéis e caneca
    rect(ctx, '#ffffff', 2, 3, 6, 5);
    rect(ctx, '#c9cfd4', 3, 4, 4, 1);
    rect(ctx, '#2f4f8a', 40, 2, 3, 4);
    rect(ctx, '#ffffff', 40, 2, 3, 1);
  });

  texture(scene, 'monitor', 18, 16, (ctx) => {
    rect(ctx, '#1a1a24', 0, 0, 18, 12);
    rect(ctx, '#2c2c3a', 1, 1, 16, 10);
    rect(ctx, '#0c1016', 2, 2, 14, 8);
    rect(ctx, METAL, 7, 12, 4, 2);
    rect(ctx, METAL_DARK, 4, 14, 10, 2);
  });

  // Tela acesa: branca com linhas, para receber a cor do estado por tinta.
  texture(scene, 'screen', 14, 8, (ctx) => {
    rect(ctx, '#ffffff', 0, 0, 14, 8);
    rect(ctx, '#b4b4b4', 1, 1, 7, 1);
    rect(ctx, '#b4b4b4', 1, 3, 10, 1);
    rect(ctx, '#b4b4b4', 3, 5, 6, 1);
    rect(ctx, '#d2d2d2', 1, 6, 4, 1);
  });

  // Cadeira de escritório preta, de tela.
  texture(scene, 'chair', 18, 14, (ctx) => {
    rect(ctx, OUTLINE, 2, 0, 14, 14);
    rect(ctx, OUTLINE, 1, 1, 16, 12);
    rect(ctx, '#2f333a', 2, 1, 14, 12);
    rect(ctx, '#3d4148', 3, 2, 12, 2);
    for (let y = 5; y < 10; y += 2) rect(ctx, '#383c43', 3, y, 12, 1);
    rect(ctx, '#24272c', 2, 10, 14, 3);
  });

  // Divisória de baia, atrás de cada mesa.
  texture(scene, 'partition', 84, 18, (ctx) => {
    rect(ctx, METAL_DARK, 0, 0, 84, 18);
    rect(ctx, METAL_LIGHT, 0, 0, 84, 2);
    rect(ctx, '#6f86a0', 2, 2, 80, 15);
    for (const [x, y] of [[6, 5], [19, 11], [33, 7], [47, 13], [58, 4], [71, 9], [78, 14], [12, 14], [40, 3], [64, 12]]) {
      rect(ctx, '#7890aa', x, y, 1, 1);
    }
    rect(ctx, '#62788f', 2, 15, 80, 2);
    rect(ctx, METAL, 0, 2, 2, 16);
    rect(ctx, METAL, 82, 2, 2, 16);
    // recado colado na divisória
    rect(ctx, '#f6d78a', 66, 4, 6, 6);
    rect(ctx, '#c08c30', 67, 6, 4, 1);
  });

  texture(scene, 'plant', 16, 24, (ctx) => {
    rect(ctx, '#3a6a3c', 2, 6, 12, 9);
    rect(ctx, '#4f8850', 1, 8, 6, 5);
    rect(ctx, '#4f8850', 9, 4, 5, 7);
    rect(ctx, '#7dbb72', 4, 3, 5, 6);
    rect(ctx, '#7dbb72', 10, 8, 4, 3);
    rect(ctx, '#3a6a3c', 6, 1, 3, 3);
    rect(ctx, '#a6d68a', 5, 4, 2, 2);
    rect(ctx, '#a6d68a', 11, 6, 1, 2);
    rect(ctx, METAL_DARK, 3, 15, 10, 9);
    rect(ctx, '#e6e9eb', 4, 16, 8, 7);
    rect(ctx, '#c9cfd4', 4, 21, 8, 2);
    rect(ctx, '#ffffff', 3, 15, 10, 2);
  });

  // Arquivo de aço com quatro gavetas.
  texture(scene, 'cabinet', 18, 36, (ctx) => {
    rect(ctx, METAL_DARK, 0, 0, 18, 36);
    rect(ctx, '#a7afb6', 1, 1, 16, 34);
    for (let i = 0; i < 4; i += 1) {
      const y = 2 + i * 8;
      rect(ctx, '#959da5', 2, y, 14, 7);
      rect(ctx, '#b9c0c6', 2, y, 14, 1);
      rect(ctx, METAL_DARK, 6, y + 3, 6, 1);
      rect(ctx, '#ffffff', 7, y + 1, 4, 1);
    }
  });

  // Impressora sobre um gabinete.
  texture(scene, 'printer', 24, 30, (ctx) => {
    rect(ctx, METAL_DARK, 0, 12, 24, 18);
    rect(ctx, '#a7afb6', 1, 13, 22, 16);
    rect(ctx, '#959da5', 3, 15, 8, 12);
    rect(ctx, '#959da5', 13, 15, 8, 12);
    rect(ctx, METAL_DARK, 1, 0, 22, 12);
    rect(ctx, '#e6e9eb', 2, 1, 20, 10);
    rect(ctx, LAMINATE, 2, 1, 20, 2);
    rect(ctx, '#2b2f35', 5, 6, 14, 2);
    rect(ctx, '#ffffff', 6, 3, 12, 4);
    rect(ctx, '#3c3c4c', 17, 9, 4, 1);
    rect(ctx, '#7dbb72', 4, 9, 1, 1);
  });

  // Bancada do café.
  texture(scene, 'coffee', 22, 30, (ctx) => {
    rect(ctx, METAL_DARK, 0, 12, 22, 18);
    rect(ctx, '#c9cfd4', 1, 13, 20, 16);
    rect(ctx, '#b9c0c6', 2, 15, 8, 12);
    rect(ctx, '#b9c0c6', 12, 15, 8, 12);
    rect(ctx, LAMINATE, 0, 11, 22, 2);
    rect(ctx, '#1a1a24', 3, 0, 11, 11);
    rect(ctx, '#3c3c4c', 4, 1, 9, 9);
    rect(ctx, '#d9604a', 5, 2, 2, 2);
    rect(ctx, '#1a1a24', 7, 6, 3, 2);
    rect(ctx, '#f3e7c9', 7, 8, 3, 3);
    rect(ctx, '#ffffff', 16, 7, 3, 4);
    rect(ctx, '#86b6d8', 16, 5, 3, 2);
  });

  texture(scene, 'cooler', 12, 28, (ctx) => {
    rect(ctx, '#86b6d8', 3, 0, 6, 9);
    rect(ctx, '#b6d8ee', 4, 1, 2, 6);
    rect(ctx, '#6a9cc0', 4, 9, 4, 2);
    rect(ctx, OUTLINE, 1, 11, 10, 17);
    rect(ctx, '#e9e2d0', 2, 12, 8, 15);
    rect(ctx, '#d9604a', 3, 15, 2, 2);
    rect(ctx, '#5a8fd0', 7, 15, 2, 2);
    rect(ctx, '#c8c0aa', 2, 22, 8, 5);
  });

  texture(scene, 'shadow', 28, 8, (ctx) => {
    ctx.fillStyle = 'rgba(20, 24, 30, 0.3)';
    ctx.beginPath();
    ctx.ellipse(14, 4, 14, 4, 0, 0, Math.PI * 2);
    ctx.fill();
  });

  texture(scene, 'star', 7, 7, (ctx) => {
    const rows = ['...o...', '..oyo..', 'ooyyyoo', 'oyyyyyo', '.oyyyo.', '.oyoyo.', '.oo.oo.'];
    rows.forEach((row, y) => {
      for (let x = 0; x < row.length; x += 1) {
        if (row[x] === 'o') rect(ctx, OUTLINE, x, y, 1, 1);
        if (row[x] === 'y') rect(ctx, '#f6c84a', x, y, 1, 1);
      }
    });
  });
}

// --------------------------------------------------------------------- balões

function bubble(scene: Phaser.Scene, key: string, icon: (ctx: Ctx) => void): void {
  texture(scene, key, 12, 12, (ctx) => {
    rect(ctx, OUTLINE, 1, 0, 10, 10);
    rect(ctx, OUTLINE, 0, 1, 12, 8);
    rect(ctx, OUTLINE, 4, 10, 3, 1);
    rect(ctx, OUTLINE, 5, 11, 1, 1);
    rect(ctx, '#fff8e6', 1, 1, 10, 8);
    rect(ctx, '#fff8e6', 5, 9, 1, 1);
    icon(ctx);
  });
}

function buildBubbles(scene: Phaser.Scene): void {
  bubble(scene, 'b_wait', (ctx) => {
    for (const x of [2, 5, 8]) rect(ctx, '#3a6f96', x, 4, 2, 2);
  });
  bubble(scene, 'b_done', (ctx) => {
    for (const [x, y] of [[2, 4], [3, 5], [4, 6], [5, 5], [6, 4], [7, 3], [8, 2]]) rect(ctx, '#3f8f4a', x, y, 2, 2);
  });
  bubble(scene, 'b_error', (ctx) => {
    rect(ctx, '#c8402c', 5, 2, 2, 4);
    rect(ctx, '#c8402c', 5, 7, 2, 1);
  });
  bubble(scene, 'b_unknown', (ctx) => {
    rect(ctx, '#775fa0', 4, 2, 4, 1);
    rect(ctx, '#775fa0', 7, 3, 1, 2);
    rect(ctx, '#775fa0', 5, 5, 2, 1);
    rect(ctx, '#775fa0', 5, 7, 2, 1);
  });
  bubble(scene, 'b_search', (ctx) => {
    rect(ctx, OUTLINE, 3, 2, 4, 1);
    rect(ctx, OUTLINE, 2, 3, 1, 3);
    rect(ctx, OUTLINE, 7, 3, 1, 3);
    rect(ctx, OUTLINE, 3, 6, 4, 1);
    rect(ctx, '#86b6d8', 3, 3, 4, 3);
    rect(ctx, OUTLINE, 8, 7, 1, 1);
    rect(ctx, OUTLINE, 9, 8, 1, 1);
  });
  bubble(scene, 'b_test', (ctx) => {
    rect(ctx, '#55556a', 4, 2, 4, 6);
    rect(ctx, '#55556a', 3, 3, 6, 4);
    rect(ctx, '#55556a', 2, 4, 8, 2);
    rect(ctx, '#fff8e6', 5, 4, 2, 2);
  });
  bubble(scene, 'b_code', (ctx) => {
    for (const [x, y] of [[4, 2], [3, 3], [2, 4], [3, 5], [4, 6], [7, 2], [8, 3], [9, 4], [8, 5], [7, 6]]) {
      rect(ctx, '#3a6f96', x, y, 1, 2);
    }
  });
  bubble(scene, 'b_spawn', (ctx) => {
    rect(ctx, '#c08c30', 5, 2, 2, 6);
    rect(ctx, '#c08c30', 3, 4, 6, 2);
  });
}

export function buildTextures(scene: Phaser.Scene): void {
  buildRoom(scene);
  buildFurniture(scene);
  buildBubbles(scene);
  buildCharacters(scene);
}
