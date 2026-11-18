// Tema da Central do TI (claro ou escuro).
// Carregado no <head>, antes do CSS pintar a página, para a tela não piscar na cor errada.
// Vale a escolha gravada neste navegador; sem escolha, segue o tema do Windows.
(function () {
  'use strict';
  var CHAVE = 'pontual.central.tema';
  function gravado() {
    try { var t = localStorage.getItem(CHAVE); return t === 'escuro' || t === 'claro' ? t : null; } catch (e) { return null; }
  }
  function doSistema() {
    return window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches ? 'escuro' : 'claro';
  }
  function aplicar(tema) { document.documentElement.setAttribute('data-tema', tema); }

  aplicar(gravado() || doSistema());

  window.Tema = {
    atual: function () { return document.documentElement.getAttribute('data-tema') === 'escuro' ? 'escuro' : 'claro'; },
    trocar: function () {
      var novo = this.atual() === 'escuro' ? 'claro' : 'escuro';
      aplicar(novo);
      try { localStorage.setItem(CHAVE, novo); } catch (e) { /* sem armazenamento: vale só até recarregar */ }
      return novo;
    },
  };
})();
