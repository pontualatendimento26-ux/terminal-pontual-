// Monta o demonstrativo a partir das telas REAIS do Terminal: node prototipos/demonstrativo/montar.js
const fs = require('fs'), path = require('path');
const raiz = path.join(__dirname, '../..'), tela = path.join(raiz, 'src/server/Pontual.Server/wwwroot/terminal');
const ler = (f) => fs.readFileSync(path.join(tela, f), 'utf8').replace(/^\uFEFF/, '');
const trocar = (t, a, b) => { if (!t.includes(a)) throw new Error('A tela mudou; não achei: ' + a); return t.replace(a, () => b); };
const css = ler('app.css');
let js = ler('app.js');
js = trocar(js, '`logos/${logo}.svg`', 'window.__LOGOS[logo]');
js = trocar(js, "parametros.get('calc') === '1'", 'window.__CALC === true');
let inicio = trocar(ler('index.html'), '<link rel="stylesheet" href="app.css">', `<style>${css}</style>`);
inicio = trocar(inicio, '<script src="app.js"></script>', `<script>${js}</script>`);
let assistente = trocar(ler('assistente.html'), '<link rel="stylesheet" href="app.css">', `<style>${css}</style>`);
assistente = trocar(assistente, '<link rel="stylesheet" href="assistente.css">', `<style>${ler('assistente.css')}</style>`);
assistente = trocar(assistente, '<script src="assistente.js"></script>', `<script>${ler('assistente.js')}</script>`);
const logos = {};
for (const f of fs.readdirSync(path.join(tela, 'logos'))) if (f.endsWith('.svg')) logos[f.slice(0, -4)] = 'data:image/svg+xml;base64,' + fs.readFileSync(path.join(tela, 'logos', f)).toString('base64');
const mapa = JSON.parse(ler('logos/mapa.json'));
mapa.hosts['sistema-interno'] = 'pontual';
for (const h of Object.keys(mapa.hosts)) if (/^[0-9]+[.]/.test(h)) delete mapa.hosts[h];
const local = JSON.parse(fs.readFileSync(path.join(raiz, 'src/terminal/Pontual.Terminal.Shell/config-local.json'), 'utf8').replace(/^﻿/, ''));
// Endereços da rede interna não vão para o arquivo que será enviado.
const items = local.items.map((i) => (/\/\/(192\.168|10\.)/.test(i.target) ? { ...i, target: 'http://sistema-interno/', hosts: ['sistema-interno'], externo: false } : i));
for (const [id, name, target] of [['excel', 'Excel', 'excel.exe'], ['word', 'Word', 'winword.exe']]) items.push({ id, name, kind: 'program', target, group: 'Pontual e ferramentas', hosts: [] });
const dados = { inicio, assistente, logos, mapa, config: { version: 1, attendantName: 'Atendente Exemplo', observationMode: false, items } };
let casca = fs.readFileSync(path.join(__dirname, 'casca.html'), 'utf8');
casca = trocar(casca, '/*APPCSS*/', css);
casca = trocar(casca, '/*DADOS*/null', JSON.stringify(dados).replace(/</g, '\\u003c'));
const saida = path.join(raiz, 'dist/Demonstrativo-Terminal');
fs.mkdirSync(saida, { recursive: true });
fs.writeFileSync(path.join(saida, 'Demonstrativo-Terminal.html'), casca);
for (const f of ['Abrir no localhost.cmd', 'servir.ps1']) fs.copyFileSync(path.join(__dirname, f), path.join(saida, f));
// A Central serve esta cópia em /demonstrativo (vai junto na próxima atualização da Central).
const naCentral = path.join(raiz, 'src/server/Pontual.Server/wwwroot/demonstrativo');
fs.mkdirSync(naCentral, { recursive: true });
fs.writeFileSync(path.join(naCentral, 'index.html'), casca);
console.log('Pronto:', saida, Math.round(casca.length / 1024) + ' KB');
