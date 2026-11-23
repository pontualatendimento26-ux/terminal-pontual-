// Botão "QR code" do topo: mostra um código que leva o celular direto para /baixar/.
(function () {
  var btn = document.getElementById('btn-qr'), dlg = document.getElementById('dlg-qr');
  if (!btn || !dlg) return;
  function desenhar(url) {
    var q = qrcode(0, 'M'); q.addData(url); q.make();
    document.getElementById('qr-imagem').innerHTML = q.createSvgTag({ cellSize: 6, margin: 2 });
    document.getElementById('qr-link').textContent = url;
  }
  btn.addEventListener('click', function () {
    var h = location.hostname, porta = location.port ? ':' + location.port : '';
    var local = h === 'localhost' || h.indexOf('127.') === 0 || h === '::1' || h === '[::1]';
    desenhar(location.protocol + '//' + location.host + '/baixar/');
    if (local) {
      fetch('/baixar/endereco').then(function (r) { return r.json(); }).then(function (ips) {
        var ip = ips.filter(function (x) { return /^(192\.168|10\.|172\.)/.test(x); })[0] || ips[0];
        if (ip) desenhar('http://' + ip + porta + '/baixar/');
      }).catch(function () {});
    }
    dlg.showModal();
  });
  dlg.addEventListener('click', function (e) {
    if (e.target === dlg || e.target.hasAttribute('data-fechar')) dlg.close();
  });
})();
