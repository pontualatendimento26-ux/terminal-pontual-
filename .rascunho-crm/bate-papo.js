// public/js/bate-papo.js

// ==========================================================================
// MOTIVO DA PERDA
//
// Marcar um cliente como PERDIDO exige dizer por quê. No bate-papo isso pode
// acontecer por dois caminhos: a barra de status lateral (segmento "Perdido") e
// o dropdown de funil do cabeçalho (movendo pra uma etapa do tipo perdido).
//
// A lista vem do backend (GET /api/motivos-perdido, que lê
// backend/servicos/motivos-perdido.js) — mesma fonte usada pelo Kanban, pra não existir
// uma segunda cópia que sai do ar quando alguém adiciona um motivo.
//
// Este bloco fica FORA do DOMContentLoaded de propósito: a barra de status vive
// dentro dele, mas moverClienteParaFunil é uma função global lá do fim do
// arquivo, e as duas precisam do mesmo seletor.
// ==========================================================================
let motivosPerdidoCacheChat = null;
let motivosTodosChat = null; // inclusive desativados: dá nome ao que já está gravado

// Formatadores de data/hora das MENSAGENS da conversa, criados uma vez só.
// `toLocaleTimeString/DateString` monta um formatador novo a cada chamada, e
// o renderizarMensagens chamava 4 por balão: abrir a conversa de 751
// mensagens gastava a maior parte do tempo nisso. A saída é a mesma (mesmo
// idioma e mesmas opções). Mesmo remédio da lista lateral (FORMATO_*_LISTA).
const FORMATO_HORA_MSG = new Intl.DateTimeFormat([], {
  hour: '2-digit',
  minute: '2-digit',
});
const FORMATO_DATA_MSG = new Intl.DateTimeFormat('pt-BR');
const FORMATO_DIA_SEMANA_MSG = new Intl.DateTimeFormat('pt-BR', {
  weekday: 'long',
});
const FORMATO_DATA_SISTEMA_MSG = new Intl.DateTimeFormat('pt-BR', {
  day: '2-digit',
  month: '2-digit',
  year: 'numeric',
});
const FORMATO_HORA_SISTEMA_MSG = new Intl.DateTimeFormat('pt-BR', {
  hour: '2-digit',
  minute: '2-digit',
});

// MENSAGEM SÓ DE EMOJI (1 a 3) aparece grande e sem balão, como no
// WhatsApp; 4 ou mais, ou emoji misturado com texto, fica no balão normal.
// Conta por caractere VISÍVEL (grafema), não por letra: 👍🏽, 👨‍👩‍👧 e 🇧🇷
// são vários códigos por baixo mas UM emoji na tela.
const SEGMENTADOR_EMOJI =
  typeof Intl !== 'undefined' && Intl.Segmenter
    ? new Intl.Segmenter('pt', { granularity: 'grapheme' })
    : null;
const RE_UM_EMOJI = (() => {
  try {
    // RGI_Emoji: a lista oficial de emojis (não pega número nem letra solta).
    return new RegExp('^\\p{RGI_Emoji}$', 'v');
  } catch (e) {
    return /^(\p{Extended_Pictographic}|\p{Regional_Indicator}{2})[️‍\p{Extended_Pictographic}\p{Emoji_Modifier}]*$/u;
  }
})();
function contarSoEmojis(texto) {
  if (!texto || texto.length > 60 || !SEGMENTADOR_EMOJI) return 0;
  const limpo = String(texto).trim();
  if (!limpo) return 0;
  let qtd = 0;
  for (const { segment } of SEGMENTADOR_EMOJI.segment(limpo)) {
    if (/^\s+$/.test(segment)) continue;
    if (!RE_UM_EMOJI.test(segment)) return 0;
    if (++qtd > 3) return 0;
  }
  return qtd;
}

// Escape genérico do bate-papo. Nasceu pro seletor de motivo da perda, mas
// serve pra qualquer texto de cliente/usuário que vá parar dentro de um
// innerHTML aqui — os cards de lembrete usam este mesmo.
function escaparHtmlChat(valor) {
  if (valor === null || valor === undefined) return '';
  return String(valor)
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
    .replace(/'/g, '&#39;');
}

// HTML de texto que o PRÓPRIO sistema grava com marcação (linha do tempo
// com <span style>, "apagada por: <b>nome</b>", "Editada por") mas que também
// carrega texto de cliente e nome digitado por gente da equipe. Fica só o que
// não executa nada: as tags abaixo, com style (sem url()) e class; todo o
// resto vira texto. O <template> monta o HTML sem rodar nem baixar nada.
const TAGS_DO_SISTEMA = new Set(['B', 'I', 'EM', 'STRONG', 'U', 'S', 'BR', 'SPAN', 'SMALL']);
function htmlLimpoDoSistema(html) {
  const molde = document.createElement('template');
  molde.innerHTML = String(html ?? '');
  const limpar = (pai) => {
    for (const no of [...pai.childNodes]) {
      if (no.nodeType === 3) continue;
      if (no.nodeType !== 1 || !TAGS_DO_SISTEMA.has(no.tagName)) {
        no.replaceWith(document.createTextNode(no.nodeType === 1 ? no.textContent : ''));
        continue;
      }
      for (const at of [...no.attributes]) {
        const fica =
          (at.name === 'style' && !/url\s*\(|expression|@import|\\/i.test(at.value)) ||
          (at.name === 'class' && /^[\w\s-]*$/.test(at.value));
        if (!fica) no.removeAttribute(at.name);
      }
      limpar(no);
    }
  };
  limpar(molde.content);
  return molde.innerHTML;
}

async function carregarMotivosPerdidoChat() {
  if (motivosPerdidoCacheChat) return motivosPerdidoCacheChat;
  try {
    const res = await fetch('/api/motivos-perdido', {
      headers: { Authorization: `Bearer ${localStorage.getItem('token')}` },
    });
    const dados = await res.json();
    if (dados && Array.isArray(dados.motivos) && dados.motivos.length > 0) {
      motivosPerdidoCacheChat = dados.motivos;
      motivosTodosChat = dados.todos || dados.motivos;
    }
  } catch (e) {
    console.error('Falha ao carregar os motivos de perda:', e);
  }
  return motivosPerdidoCacheChat;
}

/** Nome de exibição do motivo salvo, pro selo do painel CRM. */
function nomeMotivoPerdidoChat(id) {
  if (!id) return '';
  const achado = (motivosTodosChat || motivosPerdidoCacheChat || []).find((m) => m.id === id);
  return achado ? achado.nome : String(id);
}

/**
 * Pergunta o motivo da perda. Devolve o id escolhido ou null se cancelou —
 * nesse caso quem chamou não pode seguir com a mudança de status.
 *
 * Nenhuma opção vem pré-marcada: com uma marcada, dá pra confirmar no
 * automático e todo lead cairia no primeiro motivo da lista, que é o mesmo
 * problema da etiqueta errada com outra roupa.
 *
 * @returns {Promise<string|null>}
 */
async function pedirMotivoPerdidoChat() {
  const motivos = await carregarMotivosPerdidoChat();
  if (!motivos) {
    Swal.fire(
      'Erro',
      'Não foi possível carregar a lista de motivos. Tente de novo.',
      'error'
    );
    return null;
  }

  const listaHtml = motivos
    .map(
      (m) => `
        <label class="opcao-motivo-perdido">
          <input type="radio" name="motivo-perdido" value="${escaparHtmlChat(m.id)}">
          <span class="opcao-motivo-marcador"><i class="bx bx-check"></i></span>
          <span class="opcao-motivo-texto">${escaparHtmlChat(m.nome)}</span>
        </label>`
    )
    .join('');

  const resultado = await Swal.fire({
    title: 'Por que este cliente foi perdido?',
    html: `
      <p class="motivo-perdido-subtitulo">Essa informação aparece no relatório e no histórico do cliente.</p>
      <div class="lista-motivos-perdido">${listaHtml}</div>`,
    showCancelButton: true,
    confirmButtonText: 'Confirmar perda',
    cancelButtonText: 'Cancelar',
    confirmButtonColor: '#ef4444',
    focusConfirm: false,
    preConfirm: () => {
      const marcado = document.querySelector(
        'input[name="motivo-perdido"]:checked'
      );
      if (!marcado) {
        Swal.showValidationMessage('Escolha um motivo para continuar.');
        return false;
      }
      return marcado.value;
    },
  });

  return resultado.isConfirmed ? resultado.value : null;
}

/**
 * Mostra/esconde o selo do motivo no painel CRM. Recebe o id salvo no cliente
 * (que o backend só mantém preenchido enquanto o lead está em perdido), então
 * não precisa cruzar com o status pra decidir se aparece.
 * @param {string|null} motivoId
 */
function atualizarSeloMotivoPerdido(motivoId) {
  const selo = document.getElementById('selo-motivo-perdido');
  if (!selo) return;
  if (!motivoId) {
    selo.classList.add('escondido');
    selo.textContent = '';
    return;
  }
  selo.classList.remove('escondido');
  selo.innerHTML = `<i class="bx bx-x-circle"></i><span>${escaparHtmlChat(
    nomeMotivoPerdidoChat(motivoId)
  )}</span>`;
}

// ==========================================================================
// PAPEL DE PAREDE DA CONVERSA
//
// São 6 blocos de ícones equivalentes em assets/. Este sorteio troca o bloco a
// cada conversa aberta e a cada F5, e ainda desloca o ponto de partida do
// ladrilho — então além de mudarem de lugar e de tamanho, entram na tela
// desenhos que não estavam nela antes.
//
// Escreve --papel-claro e --papel-escuro, e NÃO background-image direto: quem
// escolhe entre o arquivo claro e o escuro continua sendo o CSS. Um inline aqui
// congelaria o fundo no tema em que o sorteio aconteceu, e trocar de tema
// deixaria o papel do tema errado na tela.
//
// Custo medido no Chrome: ~27ms na primeira vez que um bloco aparece (baixar +
// rasterizar) e ~1ms nas trocas seguintes, porque o bitmap fica em cache. Como
// abrirConversa já faz fetch das mensagens, isso não acrescenta espera visível.
// ==========================================================================
const TOTAL_PAPEIS_CHAT = 6;
let papelChatAtual = 0;

function sortearPapelDeParedeChat() {
  const areas = document.querySelectorAll('.coluna-area-conversa');
  if (!areas.length) return;

  // Nunca repete o bloco anterior — sorteio livre repetiria 1 vez em 6, e essas
  // trocas pareceriam não ter feito nada.
  let n = papelChatAtual;
  while (n === papelChatAtual) {
    n = 1 + Math.floor(Math.random() * TOTAL_PAPEIS_CHAT);
  }
  papelChatAtual = n;

  const desloca =
    Math.floor(Math.random() * 778) + 'px ' + Math.floor(Math.random() * 778) + 'px';

  areas.forEach((area) => {
    area.style.setProperty('--papel-claro', 'url("/assets/fundo-chat-' + n + '-claro.svg")');
    area.style.setProperty('--papel-escuro', 'url("/assets/fundo-chat-' + n + '-escuro.svg")');
    area.style.backgroundPosition = desloca;
  });
}

/**
 * DE QUAL CANAL É CADA BOT — 'whatsapp' ou 'rcs'.
 *
 * O RCS entrou no sistema como se fosse mais um chip (mesma conversa, mesmo
 * cartão, telefone como chave), mas ele aceita bem menos coisa: não tem
 * áudio, figurinha, reação, citação, editar nem apagar. A vendedora não tem
 * como adivinhar isso — o hábito dela é o WhatsApp. Por isso a tela precisa
 * dizer, o tempo todo, em qual canal ela está falando.
 */
const CANAL_POR_BOT = {};
// O nome de exibição de cada bot ("RCS — Pontual"), da mesma lista
// (/api/sessions/all). Serve pra aba que nasce pelo socket — ver
// canalNovoDaMensagem.
const NOME_POR_BOT = {};
// Nome pra mostrar de um canal. O servidor só conhece o nome de quem está ativo
// na memória; bot arquivado chega com o próprio id ("Vieira__23_Bu_-_0496"),
// então o nome cadastrado (NOME_POR_BOT) vale mais.
function nomeDoCanal(canal) {
  if (NOME_POR_BOT[canal.bot_id]) return NOME_POR_BOT[canal.bot_id];
  return canal.botName || canal.bot_id;
}

function canalDoBot(botId) {
  return CANAL_POR_BOT[botId] || 'whatsapp';
}

/**
 * O MAPA DE CANAIS JÁ CHEGOU? (30/09/2026)
 *
 * CANAL_POR_BOT nasce vazio e só se enche quando /api/sessions/all responde —
 * e essa chamada só sai DEPOIS da lista de conversas. Até lá, canalDoBot()
 * diz "whatsapp" pra todo bot, inclusive o RCS. Quem abria uma conversa
 * nesse intervalo (quase sempre quem vinha do Relatórios/Kanban pela URL,
 * que abre a conversa no mesmo instante em que a lista chega) ganhava o
 * cabeçalho do WhatsApp na aba do RCS: selo verde, sem o botão "Vincular
 * números"/"Chamar no WhatsApp" e sem as travas do canal. Quem abre a
 * conversa espera por esta promessa (com teto, ver abrirConversa).
 */
let avisarMapaCanaisPronto = () => {};
const mapaCanaisPronto = new Promise((resolve) => {
  avisarMapaCanaisPronto = resolve;
});
let mapaCanaisCarregado = false;

/**
 * A ABA DE UM CANAL QUE A CONVERSA AINDA NÃO TINHA, nascida de uma mensagem
 * do socket. O caso típico: o cliente recebeu a campanha de RCS e respondeu
 * — a conversa dele ganha o canal RCS naquele instante. Até 21/09/2026 ela
 * nascia com o id técnico ("rcs_pontual") no lugar do nome, até a lista
 * recarregar.
 */
function canalNovoDaMensagem(msg) {
  return {
    bot_id: msg.bot_id,
    botName: NOME_POR_BOT[msg.bot_id] || msg.bot_id,
    has_permission: true,
    status: 'online',
    unread: msg.is_from_me ? 0 : 1,
  };
}

function ehCanalRcs(botId) {
  return canalDoBot(botId) === 'rcs';
}

/**
 * OS ARQUIVOS QUE O RCS ACEITA — a mesma lista do backend
 * (rcs/enviar-do-chat.js + envio-otima.js MIMES_BASE64): imagem JPG/PNG/GIF,
 * vídeo MP4/MPEG/WEBM e, desde 30/10/2026, PDF e áudio (MP3/OGG/AAC/M4A).
 * WEBP, vídeo do iPhone (.mov), Word, Excel... a Ótima recusa.
 */
const TIPOS_ARQUIVO_RCS = [
  'image/jpeg',
  'image/png',
  'image/gif',
  'video/mp4',
  'video/mpeg',
  'video/webm',
  'application/pdf',
  'audio/ogg',
  'audio/mpeg',
  'audio/mp3',
  'audio/aac',
  'audio/mp4',
  'audio/x-m4a',
];
const ACCEPT_ARQUIVO_RCS = '.jpg,.jpeg,.png,.gif,.mp4,.mpeg,.webm,.pdf,.ogg,.mp3,.aac,.m4a';

function arquivoCabeNoRcs(arquivo) {
  const tipo = String((arquivo && arquivo.type) || '').toLowerCase();
  if (tipo) return TIPOS_ARQUIVO_RCS.includes(tipo === 'image/jpg' ? 'image/jpeg' : tipo);
  // Sem tipo (acontece com arquivo de mensagem pronta): vai pela extensão.
  const ext = String((arquivo && arquivo.name) || '').toLowerCase().split('.').pop();
  return ACCEPT_ARQUIVO_RCS.split(',').includes('.' + ext);
}

/**
 * AJUSTA A TELA AO CANAL ATIVO.
 *
 * O bate-papo é o mesmo pros dois canais, mas o RCS aceita bem menos coisa.
 * Deixar o botão de áudio clicável num canal sem áudio faria a vendedora
 * gravar, esperar e só então receber um erro — com o cliente do outro lado
 * esperando. Aqui os botões impossíveis ficam apagados ANTES do clique, e o
 * título explica o porquê ao passar o mouse.
 *
 * Desde 30/10/2026 o RCS manda PDF e áudio (rota de arquivo em base64 da
 * Ótima), então "Documentos", "Áudios" e o gravador funcionam nos dois
 * canais. Documento que não é PDF é recusado no servidor com o motivo. As
 * bolhas de RCS seguem sem responder, reagir, editar e apagar (ver
 * renderizarMensagens).
 */
function aplicarRestricoesDoCanal(botId) {
  const ehRcs = ehCanalRcs(botId);

  const travar = (elemento, motivo) => {
    if (!elemento) return;
    elemento.classList.toggle('acao-indisponivel-rcs', ehRcs);
    if (ehRcs) {
      if (!elemento.dataset.tituloOriginal)
        elemento.dataset.tituloOriginal = elemento.title || '';
      elemento.title = motivo;
    } else if (elemento.dataset.tituloOriginal !== undefined) {
      elemento.title = elemento.dataset.tituloOriginal;
    }
  };

  // A aba de figurinhas fica dentro do seletor de emoji; o emoji em si
  // funciona no RCS (é texto), a figurinha não.
  travar(
    document.querySelector('.picker-tab-btn[data-tab="stickers"]'),
    'O RCS não aceita figurinha.'
  );
}

/**
 * JANELA DE 24 HORAS DO RCS — a pílula do cabeçalho.
 *
 * No RCS conversacional a primeira mensagem é paga e abre 24h de conversa
 * livre; passou o prazo, a próxima cobra de novo e reabre. Esta pílula é o
 * que transforma uma regra de contrato em algo que a vendedora enxerga na
 * hora de decidir se manda "oi".
 *
 * Mora no CABEÇALHO, à direita, antes do X de fechar (até 21/09/2026 era
 * uma faixa de ponta a ponta em cima da caixa de digitação; o user achou
 * feia e escolheu este lugar num protótipo com seis opções). Só existe
 * quando a aba aberta é a do RCS: numa aba de WhatsApp ela some.
 *
 * O relógio anda de segundo em segundo, mas a CONTA vem do servidor: pega
 * `segundos_restantes` uma vez, marca a hora em que acaba e, a cada tique,
 * mostra quanto falta até ela — assim ele não escorrega se a aba ficar em
 * segundo plano (o navegador atrasa os timers de aba escondida). Quando
 * chega a zero, pergunta de novo ao servidor em vez de adivinhar.
 */
let relogioJanelaRcs = null;
// Cada chamada ganha um número; resposta que chega depois de uma chamada
// mais nova é jogada fora. Sem isto, trocar de cliente rápido deixava a
// resposta atrasada do anterior pintar a janela do cliente errado.
let vezDaJanelaRcs = 0;
let observadorCabecalhoRcs = null;

function pararRelogioJanelaRcs() {
  if (relogioJanelaRcs) {
    clearInterval(relogioJanelaRcs);
    relogioJanelaRcs = null;
  }
}

function formatarRelogioJanela(segundos) {
  const s = Math.max(0, Math.floor(segundos));
  const dois = (n) => String(n).padStart(2, '0');
  return `${dois(Math.floor(s / 3600))}:${dois(Math.floor((s % 3600) / 60))}:${dois(s % 60)}`;
}

/**
 * Tela estreita (gaveta de espiada, celular, menu lateral aberto num monitor
 * pequeno): a frase inteira espremeria a busca do meio ou as abas. Aí a
 * pílula mostra só o ícone e o relógio — a frase continua no `title`.
 * Decide medindo de verdade, não por largura de tela: dá aos dois lados do
 * cabeçalho a largura que a pílula pede (a busca encolhe no meio, sem sair
 * do centro) e só compacta se a busca fosse ficar com menos de 220px.
 */
function ajustarTamanhoPilulaJanela() {
  const cab = document.querySelector('.chat-header-top');
  if (!cab) return;
  const pilula = document.getElementById('aviso-janela-rcs');
  if (!pilula) {
    // Sem pílula (aba do WhatsApp): os dois lados voltam ao mínimo de
    // sempre e a busca, aos 480px.
    cab.style.removeProperty('--lado-cabecalho');
    return;
  }
  const zona = pilula.parentElement;

  // Quanto a zona da direita precisa pra caber TUDO que está nela lado a
  // lado (pílula + X + os vãos). O X no celular é absoluto e não conta.
  const precisa = () => {
    const filhos = Array.from(zona.children).filter(
      (el) => getComputedStyle(el).position !== 'absolute'
    );
    const vao = parseFloat(getComputedStyle(zona).columnGap) || 0;
    // largura + margem DIREITA de cada um (é onde mora o respiro entre a
    // pílula e o X). A esquerda fica de fora de propósito: a do X é `auto`,
    // e o navegador a devolve já com o espaço que sobrou — somá-la faria a
    // conta crescer a cada medida.
    const soma = filhos.reduce(
      (t, el) =>
        t + el.offsetWidth + (parseFloat(getComputedStyle(el).marginRight) || 0),
      0
    );
    return Math.max(180, Math.ceil(soma + vao * Math.max(0, filhos.length - 1)));
  };

  // Primeiro tenta a frase inteira: os dois lados ganham essa largura e a
  // busca encolhe no meio.
  pilula.classList.remove('compacta');
  cab.style.setProperty('--lado-cabecalho', precisa() + 'px');

  // Busca pequena demais (ou, no celular, abas espremidas): compacta, e os
  // lados encolhem pro tamanho da pílula compacta.
  const busca = cab.querySelector('.header-busca-central');
  const abas = cab.querySelector('.header-meta-stack');
  const apertou =
    (busca && busca.offsetParent && busca.offsetWidth < 220) ||
    (abas && abas.offsetWidth < 170);
  if (apertou) {
    pilula.classList.add('compacta');
    cab.style.setProperty('--lado-cabecalho', precisa() + 'px');
  }
}

// A largura da pílula depende da fonte (Poppins). Se ela terminar de
// carregar depois da primeira medida, a conta fica velha — mede de novo.
if (document.fonts && document.fonts.ready) {
  document.fonts.ready.then(() => ajustarTamanhoPilulaJanela());
}

async function atualizarAvisoJanelaRcs(botId, chatId) {
  const minhaVez = ++vezDaJanelaRcs;
  const zona = document.querySelector('.chat-header-top .header-zona-direita');
  let aviso = document.getElementById('aviso-janela-rcs');

  if (!zona || !ehCanalRcs(botId)) {
    pararRelogioJanelaRcs();
    if (aviso) aviso.remove();
    ajustarTamanhoPilulaJanela();
    return;
  }

  const numero = String(chatId || '').replace(/\D/g, '');
  if (!numero) return;

  try {
    const res = await fetch(`/api/rcs/janela/${numero}`, {
      headers: { Authorization: `Bearer ${localStorage.getItem('token')}` },
    });
    if (minhaVez !== vezDaJanelaRcs) return; // chegou atrasada
    if (!res.ok) return;
    const j = await res.json();
    if (minhaVez !== vezDaJanelaRcs) return;

    aviso = document.getElementById('aviso-janela-rcs');
    if (!aviso) {
      aviso = document.createElement('div');
      aviso.id = 'aviso-janela-rcs';
      zona.insertBefore(aviso, zona.firstChild);
    }
    pararRelogioJanelaRcs();

    if (j.aberta) {
      aviso.className = 'janela-rcs aberta';
      aviso.title =
        'Enquanto a janela estiver aberta, as mensagens para este cliente não são cobradas.';
      aviso.innerHTML =
        '<span class="janela-rcs-icone"><i class="ph-bold ph-clock"></i></span>' +
        '<span class="janela-rcs-rotulo">Janela de conversa aberta:</span>' +
        '<strong class="janela-rcs-tempo"></strong>';
      const tempo = aviso.querySelector('.janela-rcs-tempo');
      const acabaEm = Date.now() + j.segundos_restantes * 1000;
      const tique = () => {
        // Pílula que saiu da tela (conversa fechada, outra aberta): o
        // relógio se desliga sozinho em vez de ficar rodando à toa.
        if (!document.body.contains(aviso)) return pararRelogioJanelaRcs();
        const falta = (acabaEm - Date.now()) / 1000;
        if (falta <= 0) {
          pararRelogioJanelaRcs();
          atualizarAvisoJanelaRcs(botId, chatId);
          return;
        }
        tempo.textContent = formatarRelogioJanela(falta);
      };
      tique();
      relogioJanelaRcs = setInterval(tique, 1000);
    } else {
      aviso.className = 'janela-rcs fechada';
      aviso.title =
        'A próxima mensagem abre uma nova conversa de 24h, e ela é cobrada.';
      aviso.innerHTML =
        '<span class="janela-rcs-icone"><i class="ph-bold ph-lock-simple"></i></span>' +
        '<span class="janela-rcs-rotulo">Janela de conversa fechada</span>' +
        '<span class="janela-rcs-curto">Fechada</span>';
    }

    ajustarTamanhoPilulaJanela();
    // Reavalia o tamanho quando o cabeçalho muda de largura (janela
    // redimensionada, gaveta arrastada, menu lateral abrindo). Um observador
    // só, criado na primeira vez.
    if (!observadorCabecalhoRcs && window.ResizeObserver) {
      const cab = document.querySelector('.chat-header-top');
      if (cab) {
        observadorCabecalhoRcs = new ResizeObserver(() =>
          ajustarTamanhoPilulaJanela()
        );
        observadorCabecalhoRcs.observe(cab);
      }
    }
  } catch (e) {
    // Sem aviso é melhor do que aviso errado: some em vez de mentir.
    if (minhaVez !== vezDaJanelaRcs) return;
    pararRelogioJanelaRcs();
    const a = document.getElementById('aviso-janela-rcs');
    if (a) a.remove();
    ajustarTamanhoPilulaJanela();
  }
}

/**
 * O selo que aparece na aba do canal.
 *
 * Bolinha com o ícone do canal, do helper do sistema (window.seloCanalIcone,
 * em global.js). A palavra "RCS" saiu daqui: ela disputava espaço com o nome
 * do bot na aba, que é o que a vendedora procura, e já existe uma etiqueta
 * verde escrita "RCS" em milhares de clientes significando outra coisa (que o
 * lead veio de campanha). O ícone identifica o canal sem esse encontro.
 */
function seloDoCanal(botId) {
  const ehRcs = ehCanalRcs(botId);
  return window.seloCanalIcone(ehRcs ? 'rcs' : 'whatsapp', {
    pequeno: true,
    titulo: ehRcs ? 'Conversa por RCS' : 'Conversa por WhatsApp',
  });
}

/**
 * OS SELOS DE CANAL NA BORDA DA FOTO, na lista lateral.
 *
 * Um selo por canal em que o cliente tem conversa — WhatsApp, RCS, ou os
 * dois lado a lado, levemente sobrepostos. Ficam no canto INFERIOR ESQUERDO
 * da foto, que era o único livre: o de cima à esquerda é da bolinha de
 * status e o de baixo à direita, do contador de conversas.
 *
 * Por que na foto e não numa linha a mais embaixo dela: a linha extra
 * crescia cada card uns 22px, e numa tela cabia uma conversa a menos. Na
 * borda da foto o card não cresce nada. (Decidido em 21/09/2026, com as
 * cinco opções lado a lado num protótipo.)
 */
function selosCanalDoAvatar(canais) {
  const lista = Array.isArray(canais) ? canais : [];
  const temRcs = lista.some((c) => ehCanalRcs(c.bot_id));
  const temWhats = lista.some((c) => !ehCanalRcs(c.bot_id));
  if (!temRcs && !temWhats) return '';
  let selos = '';
  if (temWhats)
    selos += window.seloCanalIcone('whatsapp', {
      pequeno: true,
      titulo: 'Conversa por WhatsApp',
    });
  if (temRcs)
    selos += window.seloCanalIcone('rcs', {
      pequeno: true,
      titulo: 'Conversa por RCS',
    });
  return `<span class="selos-canal-avatar">${selos}</span>`;
}

/**
 * TROCA O MIOLO DA FOTO SEM LEVAR OS ENFEITES JUNTO.
 *
 * A foto da lista tem três enfeites pendurados nela (status, contador e
 * selos de canal), e o miolo — a imagem ou a letra — é trocado em vários
 * momentos: a foto do cliente chega depois, a foto falha ao carregar, o
 * agente de IA descobre o nome e troca a letra. Cada um desses lugares
 * fazia `innerHTML = ...` e salvava na mão só os enfeites que conhecia; o
 * da troca de nome esquecia o contador, que sumia sem ninguém perceber.
 * Aqui é um lugar só, e ele guarda todos.
 *
 * `conteudo`: string de HTML ou um nó (a letra vai como nó de texto, pra
 * não passar nome de cliente por innerHTML).
 */
const SELETOR_ENFEITES_AVATAR =
  '.status-dot-lista, .badge-canais-count, .selos-canal-avatar';

function trocarMioloDoAvatar(avatarEl, conteudo) {
  const enfeites = Array.from(avatarEl.children).filter((el) =>
    el.matches(SELETOR_ENFEITES_AVATAR)
  );
  avatarEl.innerHTML = '';
  if (typeof conteudo === 'string') avatarEl.innerHTML = conteudo;
  else if (conteudo) avatarEl.appendChild(conteudo);
  enfeites.forEach((el) => avatarEl.appendChild(el));
}

// Balão do anúncio, como o WhatsApp mostra no celular: desenhado acima da
// mensagem do cliente que veio do clique (servicos/anuncio-cliente.js).
// ============================================================
// CARTÃO DE CONTATO (o cliente compartilhou um ou vários contatos)
// ============================================================
// Dados em msg.detalhe.contatos = [{ nome, telefone, whatsapp }]
// (backend/servicos/conteudo-extra.js). "Abrir conversa" abre o bate-papo
// do número se ele já está na lista; senão abre a janela Adicionar Cliente
// preenchida, no chip desta conversa — a mesma que confere WhatsApp, dono e
// nono dígito, então não nasce cliente repetido.
function formatarTelefoneContato(telefone) {
  const d = String(telefone || '').replace(/\D/g, '');
  if (!d) return '';
  // Sem DDI (só DDD + número) ou com o 55 do Brasil: formato de sempre.
  if (d.length === 10 || d.length === 11 || (d.startsWith('55') && (d.length === 12 || d.length === 13))) {
    return formatarTelefoneBR(d);
  }
  return '+' + d;
}

// Mesmo celular com ou sem o nono dígito (o WhatsApp manda a conta antiga
// sem o 9; o cadastro pode estar com ele).
function mesmoCelularContato(a, b) {
  const curto = (n) => {
    let d = String(n || '').replace(/\D/g, '');
    if (!d.startsWith('55') && (d.length === 10 || d.length === 11)) d = '55' + d;
    return d.length === 13 && d.startsWith('55') && d[4] === '9' ? d.slice(0, 4) + d.slice(5) : d;
  };
  const x = curto(a);
  return !!x && x === curto(b);
}

function iniciaisContato(nome) {
  const partes = String(nome || '').trim().split(/\s+/).filter(Boolean);
  if (!partes.length) return '<i class="ph-bold ph-user"></i>';
  const letras = (partes[0][0] + (partes.length > 1 ? partes[partes.length - 1][0] : '')).toUpperCase();
  return escaparHtmlChat(letras);
}

function linhaContato(c) {
  const nome = c.nome || formatarTelefoneContato(c.telefone);
  return `
      <div class="contato-corpo">
          <div class="contato-avatar">${iniciaisContato(c.nome)}</div>
          <div class="contato-info">
              <span class="contato-nome" title="${escaparHtmlChat(nome)}">${escaparHtmlChat(nome)}</span>
              ${c.telefone ? `<span class="contato-tel">${escaparHtmlChat(formatarTelefoneContato(c.telefone))}</span>` : ''}
              ${c.telefone && !c.whatsapp ? '<span class="contato-sem-wpp">sem WhatsApp</span>' : ''}
          </div>
      </div>`;
}

function botoesContato(c) {
  const dados = `data-numero="${escaparHtmlChat(String(c.telefone || '').replace(/\D/g, ''))}" data-nome="${escaparHtmlChat(c.nome || '')}"`;
  return `
      <div class="contato-acoes">
          ${c.whatsapp ? `<button type="button" class="contato-acao" data-acao="abrir" ${dados}><i class="ph-bold ph-chat-circle"></i><span>Abrir conversa</span></button>` : ''}
          ${c.telefone ? `<button type="button" class="contato-acao" data-acao="copiar" ${dados}><i class="ph-bold ph-copy"></i><span>Copiar número</span></button>` : ''}
      </div>`;
}

function montarCartaoContato(contatos) {
  const lista = contatos.filter((c) => c && (c.nome || c.telefone));
  if (!lista.length) return '';
  if (lista.length === 1) {
    return `<div class="contato-cartao">${linhaContato(lista[0])}${botoesContato(lista[0])}</div>`;
  }
  const primeiro = lista[0].nome || formatarTelefoneContato(lista[0].telefone);
  // Vários: o resumo em cima; "Ver todos" abre a lista, e cada contato tem o
  // próprio cartão (com os botões dele) dentro.
  return `
      <div class="contato-cartao">
          <div class="contato-corpo">
              <div class="contato-avatar">${iniciaisContato(lista[0].nome)}</div>
              <div class="contato-info">
                  <span class="contato-nome" title="${escaparHtmlChat(primeiro)}">${escaparHtmlChat(primeiro)} e mais ${lista.length - 1}</span>
                  <span class="contato-tel">${lista.length} contatos</span>
              </div>
          </div>
          <div class="contato-lista" hidden>
              ${lista.map((c) => `<div class="contato-item">${linhaContato(c)}${botoesContato(c)}</div>`).join('')}
          </div>
          <div class="contato-acoes">
              <button type="button" class="contato-acao" data-acao="ver-todos" aria-expanded="false"><i class="ph-bold ph-users"></i><span>Ver todos</span></button>
          </div>
      </div>`;
}

// Um ouvinte só pra todos os cartões (o histórico é redesenhado o tempo todo).
document.addEventListener('click', (e) => {
  const botao = e.target.closest('.contato-cartao .contato-acao');
  if (!botao) return;
  e.preventDefault();
  e.stopPropagation();
  const acao = botao.dataset.acao;
  const rotulo = botao.querySelector('span');
  if (acao === 'ver-todos') {
    const lista = botao.closest('.contato-cartao').querySelector('.contato-lista');
    const abrir = lista.hidden;
    lista.hidden = !abrir;
    botao.setAttribute('aria-expanded', String(abrir));
    rotulo.textContent = abrir ? 'Esconder' : 'Ver todos';
    return;
  }
  const numero = botao.dataset.numero || '';
  if (acao === 'copiar') {
    if (botao.classList.contains('copiado')) return;
    // Mesma cópia do OCR (tem o plano B pra rede sem https).
    ocrCopiarTexto(numero).then((copiou) => {
      if (!copiou) return;
      const antes = rotulo.textContent;
      rotulo.textContent = 'Copiado';
      botao.classList.add('copiado');
      setTimeout(() => {
        rotulo.textContent = antes;
        botao.classList.remove('copiado');
      }, 1500);
    });
    return;
  }
  if (acao === 'abrir' && typeof window.abrirContatoCompartilhado === 'function') {
    window.abrirContatoCompartilhado(numero, botao.dataset.nome || '');
  }
});

// Duração no canto do vídeo-recado, quando o navegador já leu o cabeçalho.
window.mostrarDuracaoRecado = function (video) {
  const s = Math.round(video.duration);
  const alvo = video.parentElement && video.parentElement.querySelector('.recado-tempo');
  if (!alvo || !isFinite(s) || s <= 0) return;
  alvo.textContent = `${Math.floor(s / 60)}:${String(s % 60).padStart(2, '0')}`;
  alvo.hidden = false;
};

function montarBalaoAnuncio(anuncio) {
  const a = typeof anuncio === 'string' ? JSON.parse(anuncio) : anuncio;
  const row = document.createElement('div');
  row.className = 'msg-row enviada anuncio-row';
  const nomeDaRede = a.app === 'instagram' ? 'Instagram' : a.app === 'facebook' ? 'Facebook' : '';
  row.innerHTML = `
    <div class="msg-bubble bolha-cartao-rcs anuncio-balao">
      <button type="button" class="anuncio-fechar" aria-label="Minimizar anúncio" title="Minimizar">
        <i class="bx bx-x"></i>
      </button>
      <div class="cartao-campanha-rcs anuncio-cartao">
        <div class="anuncio-topo">
          <div class="cartao-campanha-rcs-midia anuncio-midia">
            <img class="anuncio-imagem" alt="" loading="lazy">
          </div>
          <div class="anuncio-resumo">
            <strong></strong>
            <button type="button" class="anuncio-abrir">Mostrar detalhes</button>
          </div>
        </div>
        <div class="cartao-campanha-rcs-corpo anuncio-corpo">
          <div class="anuncio-detalhes">
            <p></p>
            <a target="_blank" rel="noopener noreferrer" hidden>Ver anúncio</a>
          </div>
          <div class="anuncio-saudacao"></div>
          <div class="anuncio-rodape">Mensagem de saudação automática</div>
        </div>
      </div>
    </div>`;
  row.querySelector('strong').textContent = `Anúncio${nomeDaRede ? ` do ${nomeDaRede}` : ''}`;
  row.querySelector('.anuncio-detalhes p').textContent = a.texto || '';
  const imagem = row.querySelector('.anuncio-imagem');
  imagem.addEventListener('error', () => imagem.parentElement.remove());
  // Aberto, o balão tem a largura da imagem: acima disso ela perderia definição.
  imagem.addEventListener('load', () => {
    const largura = Math.min(Math.max(imagem.naturalWidth, 260), 380);
    row.querySelector('.anuncio-balao').style.setProperty('--largura-aberto', `${largura}px`);
  });
  imagem.src = a.imagem;
  const link = row.querySelector('.anuncio-detalhes a');
  if (a.link) {
    link.href = a.link;
    link.hidden = false;
  }
  const balao = row.querySelector('.anuncio-balao');
  row.querySelector('.anuncio-abrir').addEventListener('click', () => balao.classList.add('aberto'));
  row.querySelector('.anuncio-fechar').addEventListener('click', () => {
    balao.classList.remove('aberto');
    row.scrollIntoView({ block: 'nearest' }); // o texto aberto é comprido: não deixa a tela pular
  });
  const saudacao = row.querySelector('.anuncio-saudacao');
  saudacao.textContent = a.saudacao || '';
  if (!a.saudacao) {
    saudacao.remove();
    row.querySelector('.anuncio-rodape').remove();
  }
  return row;
}

/**
 * O QUE O RCS GUARDA A MAIS NA MENSAGEM (coluna mensagens.rcs_detalhe):
 * `cartao` na mensagem da campanha, `clique` na resposta que foi toque em
 * botão. Vem como objeto do banco e do socket; o parse é só por garantia.
 */
function detalheRcsDaMensagem(msg) {
  let d = msg && msg.rcs_detalhe;
  if (typeof d === 'string') {
    try {
      d = JSON.parse(d);
    } catch (e) {
      d = null;
    }
  }
  return d && typeof d === 'object' ? d : null;
}

// Texto que vai DENTRO de um onclick="fn('...')": primeiro vira string de JS,
// depois atributo de HTML. Nessa ordem — o navegador desfaz o HTML antes de
// rodar o JS.
function textoParaOnclick(valor) {
  return escaparHtmlChat(textoParaJs(valor));
}
// Só a parte do JS: pra setAttribute('onclick', ...), que não passa pelo HTML.
function textoParaJs(valor) {
  return String(valor || '')
    .replace(/\\/g, '\\\\')
    .replace(/'/g, "\\'")
    .replace(/\r?\n/g, ' ');
}

/**
 * A MENSAGEM DA CAMPANHA, DESENHADA COMO O CLIENTE VIU NO CELULAR.
 *
 * Até 21/09/2026 a bolha mostrava só o texto do template: a vendedora não
 * sabia que tinha ido um vídeo nem quais botões o cliente tinha pra tocar —
 * e a resposta "Não, obrigado." chegava sem contexto. O desenho segue a
 * prévia da tela de Disparo RCS: mídia em cima, título, texto, e cada botão
 * no seu balão por baixo do cartão.
 *
 * `corpo` é o texto da coluna `texto`, que já começa pelo título (a campanha
 * grava "título\n\ntexto"): o título sai dali e vai em negrito em cima.
 * Título e texto ficam DENTRO do .texto-formatado, que é onde a busca da
 * conversa procura.
 */
function montarCartaoRcs(cartao, corpo, { dataTexto = '', remetente = '' } = {}) {
  const titulo = String(cartao.titulo || '').trim();
  let texto = String(corpo || '');
  if (titulo && texto.startsWith(titulo))
    texto = texto.slice(titulo.length).replace(/^\s*\n/, '');

  let midiaHtml = '';
  if (cartao.midia) {
    const ehVideo = cartao.midia_tipo === 'video';
    const abrir = `abrirVisualizador('${textoParaOnclick(cartao.midia)}', '${
      ehVideo ? 'video' : 'imagem'
    }', '', '${textoParaOnclick(dataTexto)}', '', 'MÍDIA', '${textoParaOnclick(remetente)}')`;
    const src = escaparHtmlChat(cartao.midia);
    midiaHtml = `
      <div class="cartao-campanha-rcs-midia" onclick="${abrir}">
        ${
          ehVideo
            ? `<video src="${src}#t=0.1" preload="metadata" muted playsinline></video>
               <span class="cartao-campanha-rcs-play"><i class="fas fa-play"></i></span>`
            : `<img src="${src}" alt="" loading="lazy">`
        }
      </div>`;
  }

  const botoes = montarBotoesRcs(cartao.botoes);

  const horizontal = String(cartao.orientacao || '').toUpperCase() === 'HORIZONTAL';
  const classes = [
    'cartao-campanha-rcs',
    horizontal ? 'horizontal' : 'vertical',
    horizontal
      ? String(cartao.alinhamento || '').toUpperCase() === 'RIGHT'
        ? 'midia-direita'
        : ''
      : 'tam-' + (['SHORT', 'MEDIUM', 'TALL'].includes(cartao.tamanho) ? cartao.tamanho : 'MEDIUM'),
  ]
    .filter(Boolean)
    .join(' ');

  return `
    <div class="${classes}">
      ${midiaHtml}
      <div class="cartao-campanha-rcs-corpo">
        <span class="texto-formatado cartao-campanha-rcs-texto">${
          titulo ? `<b>${escaparHtmlChat(titulo)}</b>` : ''
        }${texto ? formatarTextoWhatsApp(texto) : ''}</span>
      </div>
    </div>
    ${botoes}`;
}

/**
 * OS BOTÕES DO RCS embaixo da mensagem — do cartão da campanha ou de uma
 * mensagem comum que foi com botões (mensagem pronta de RCS, automação).
 * Aceita os dois jeitos de escrever o tipo: o do template da Ótima
 * (URL/REPLY) e o do CRM (link/resposta, ver backend/rcs/botoes.js).
 * Botão de link abre o link; o de resposta é só desenho — quem toca é o
 * cliente, e o que ele tocou acende depois (marcarBotaoTocadoNoCartao).
 */
function montarBotoesRcs(lista) {
  const html = (Array.isArray(lista) ? lista : [])
    .map((b) => {
      const ehLink = ['URL', 'LINK', 'OPEN_URL'].includes(String(b.tipo || '').toUpperCase());
      const icone = `<i class="ph-bold ${ehLink ? 'ph-arrow-square-out' : 'ph-arrow-bend-up-left'}"></i>`;
      const rotulo = escaparHtmlChat(b.texto || '');
      if (ehLink && /^https?:\/\//i.test(b.url || ''))
        return `<a class="cartao-campanha-rcs-botao link" href="${escaparHtmlChat(b.url)}" target="_blank" rel="noopener noreferrer" title="${escaparHtmlChat(b.url)}">${icone}<span>${rotulo}</span></a>`;
      return `<span class="cartao-campanha-rcs-botao${ehLink ? ' link' : ''}">${icone}<span>${rotulo}</span></span>`;
    })
    .join('');
  return html ? `<div class="cartao-campanha-rcs-botoes">${html}</div>` : '';
}

/** A bolha do cartão da campanha que tem este id, se estiver na tela. */
function bolhaDoCartaoRcs(wppId) {
  if (!wppId) return null;
  const status = document.querySelector(
    `.msg-status[data-wpp-id="${CSS.escape(String(wppId))}"]`
  );
  return status ? status.closest('.msg-bubble') : null;
}

/**
 * RESPOSTA DO CLIENTE QUE FOI UM TOQUE NUM BOTÃO do cartão, e não texto
 * digitado. Sem a marca, "Não, obrigado." parecia que o cliente escreveu —
 * e a vendedora não tinha como saber que era a opção pronta do cartão.
 *
 * Vira uma CITAÇÃO do cartão, igual a quando alguém responde uma mensagem
 * no chat (a mesma caixinha .quoted-msg-box, na cor do RCS), com o texto do
 * botão embaixo como mensagem comum — é assim que o WhatsApp mostra resposta
 * de botão. Até 21/09/2026 era um balão com rótulo e pílula próprios, que
 * destoava dos outros balões do sistema. Clicar na citação leva ao cartão.
 */
function montarCitacaoDoCliqueRcs(clique) {
  const bolha = bolhaDoCartaoRcs(clique.respondendo_a);
  // Cartão da campanha: cita o título. Mensagem comum com botões: cita o
  // começo do texto dela.
  const tituloEl = bolha && bolha.querySelector('.cartao-campanha-rcs-texto > b');
  const ehCartao = !!(bolha && bolha.querySelector('.cartao-campanha-rcs'));
  const textoEl = bolha && bolha.querySelector('.texto-formatado');
  const titulo = tituloEl
    ? tituloEl.textContent.trim()
    : textoEl && textoEl.textContent.trim()
      ? textoEl.textContent.trim().slice(0, 120)
      : 'Mensagem com botões';
  return `
    <div class="quoted-msg-box reply-rcs rcs-clique-citacao" onclick="scrollToMsg('${textoParaOnclick(clique.respondendo_a)}')" title="Ver a mensagem">
      <div class="quoted-content-flex">
        <div class="quoted-icon-box"><i class="ph-bold ph-hand-tap"></i></div>
        <div class="quoted-text-content">
          <span class="quoted-author">${ehCartao || !bolha ? 'Tocou no botão do cartão' : 'Tocou no botão da mensagem'}</span>
          <span class="quoted-text">${escaparHtmlChat(titulo)}</span>
        </div>
      </div>
    </div>`;
}

/**
 * Marca, no cartão da campanha, o botão que o cliente tocou. O clique guarda
 * o id da mensagem que tinha o botão (`respondendo_a`) e o texto é o do
 * próprio botão. Cartão fora da tela (conversa antiga, outra aba): não faz
 * nada — a citação no balão do clique já diz o que foi escolhido.
 */
function marcarBotaoTocadoNoCartao(clique, textoDoBotao) {
  if (!clique) return;
  const bolha = bolhaDoCartaoRcs(clique.respondendo_a);
  if (!bolha) return;
  const alvo = String(textoDoBotao || '').trim();
  // Os botões de mensagem comum ficam FORA do balão (.botoes-rcs-fora), no
  // mesmo wrapper; os do cartão da campanha, dentro. Procura nos dois.
  const onde = bolha.closest('.msg-wrapper-vertical') || bolha;
  onde.querySelectorAll('.cartao-campanha-rcs-botao').forEach((b) => {
    if (b.textContent.trim() !== alvo) return;
    b.classList.add('tocado');
    b.title = 'O cliente tocou neste botão';
  });
}

document.addEventListener('DOMContentLoaded', () => {
  // papel de parede: sorteia um bloco por carregamento de página
  sortearPapelDeParedeChat();

  // ==========================================================================
  // 🥷 MODO GAVETA (bate-papo embutido no iframe da tela de Relatórios)
  // ==========================================================================
  // Só liga a classe; todo o layout está em pag-bate-papo.css (body.modo-gaveta).
  // Antes isto injetava um <style> gigante em runtime — com @import das fontes
  // que esta página já carrega no <head> e medidas em 100vw/100vh que o zoom da
  // escala-responsiva encolhia, deixando moldura branca dentro da gaveta.
  const urlParamsGaveta = new URLSearchParams(window.location.search);
  if (urlParamsGaveta.get('modo') === 'gaveta') {
    document.body.classList.add('modo-gaveta');
  }
  // Elementos da Lista
  const containerLista = document.getElementById('container-lista-chats');

  // MEMÓRIA DA LISTA INCREMENTAL e FORMATADORES DE DATA dos cards.
  // Declarados aqui em cima de propósito: `const` não existe antes da linha
  // em que é declarado, e se a lista fosse desenhada antes de o código
  // chegar lá embaixo, perto de reconciliarListaChats(), daria erro. Aqui
  // eles já existem antes de qualquer desenho.
  const cacheCardsLista = new Map(); // chat_id -> { el, assinatura }
  // A data de cada card era formatada com toLocaleTimeString/
  // toLocaleDateString, que montam um formatador novo a CADA chamada: ~0,05ms
  // cada, vezes 1000 cards, vezes cada mensagem que chega. Guardados prontos,
  // a saída é idêntica (conferido em 3000 datas, 21/09/2026) e custa ~1ms.
  const FORMATO_HORA_LISTA = new Intl.DateTimeFormat([], {
    hour: '2-digit',
    minute: '2-digit',
  });
  const FORMATO_DIA_SEMANA_LISTA = new Intl.DateTimeFormat('pt-BR', {
    weekday: 'long',
  });
  const FORMATO_DATA_LISTA = new Intl.DateTimeFormat('pt-BR');
  const inputBusca = document.getElementById('input-busca-chat');
  const selectFiltro = document.getElementById('select-filtro-bot');
  // Elementos do Chat
  const areaVazia = document.getElementById('area-conversa-vazia');
  const areaAtiva = document.getElementById('area-conversa-ativa');
  const containerMsgs = document.getElementById('container-mensagens');
  // Elementos de Header e Input
  const headerNome = document.getElementById('header-nome-contato');
  const inputMsg = document.getElementById('input-mensagem-texto');
  const btnEnviar = document.getElementById('botao-enviar-msg');
  const painelInfo = document.getElementById('painel-info-cliente');
  const btnToggleInfo = document.getElementById('btn-toggle-info');
  const btnAssumirConversaIa = document.getElementById(
    'btn-assumir-conversa-ia'
  );
  const badgeIaRespondendo = document.getElementById('badge-ia-respondendo');
  const btnFecharInfoMobile = document.getElementById('btn-fechar-info-mobile');
  const btnToggleComentario = document.getElementById('btn-toggle-comentario');
  const inputWrapperChat = document.querySelector('.input-wrapper-chat');
  let isModoComentario = false;
  if (btnToggleComentario) {
    btnToggleComentario.addEventListener('click', (e) => {
      e.preventDefault();
      isModoComentario = !isModoComentario;
      if (isModoComentario) {
        inputWrapperChat.classList.add('modo-comentario');
        btnToggleComentario.classList.add('ativo');
        inputMsg.placeholder = 'Digite seu comentário...';
      } else {
        inputWrapperChat.classList.remove('modo-comentario');
        btnToggleComentario.classList.remove('ativo');
        inputMsg.placeholder = 'Digite sua mensagem...';
      }
      inputMsg.focus();
    });
  }

  // ==========================================================================
  // ⏰ LEMBRETES DO CLIENTE
  //
  // O card amarelo no meio da conversa e o modal que o cria. O apito na hora
  // que vence NÃO mora aqui: é global (frontend/js/global.js), porque ele tem
  // que tocar em qualquer tela do CRM, não só no bate-papo.
  // ==========================================================================
  const btnCriarLembrete = document.getElementById('btn-criar-lembrete');
  const modalLembrete = document.getElementById('modal-lembrete');
  const inputLembreteTexto = document.getElementById('lembrete-texto');
  const inputLembreteHora = document.getElementById('lembrete-hora');
  const selectLembreteDest = document.getElementById('lembrete-destinatario');
  const resumoLembrete = document.getElementById('lembrete-resumo');
  const contadorLembrete = document.getElementById('lembrete-contador-num');
  const btnSalvarLembrete = document.getElementById('btn-salvar-lembrete');

  const cabecalhoAuth = () => ({
    'Content-Type': 'application/json',
    Authorization: `Bearer ${localStorage.getItem('token')}`,
  });

  /**
   * "hoje às 17:00", "amanhã às 09:00" ou "03/09/2026 às 09:00".
   *
   * Só os dois dias que a pessoa consegue situar de cabeça viram palavra; do
   * terceiro em diante "quinta-feira" não diz QUAL quinta, então vale mais a
   * data. E como isto é calculado na hora de desenhar, um lembrete marcado
   * pro dia 3 aparece como data hoje e vira "amanhã" sozinho quando chegar o
   * dia 2.
   */
  function formatarPrazoLembrete(valorData) {
    const d = new Date(valorData);
    if (isNaN(d.getTime())) return '';
    const hora = d.toLocaleTimeString('pt-BR', {
      hour: '2-digit',
      minute: '2-digit',
    });
    const hoje = new Date();
    const amanha = new Date();
    amanha.setDate(hoje.getDate() + 1);

    if (d.toDateString() === hoje.toDateString()) return `hoje às ${hora}`;
    if (d.toDateString() === amanha.toDateString()) return `amanhã às ${hora}`;
    return `${d.toLocaleDateString('pt-BR')} às ${hora}`;
  }

  /** A dona do lembrete, quem criou e o admin podem mexer. */
  function podeMexerNoLembrete(l) {
    if (!l) return false;
    if (podeLembreteDeOutros) return true;
    return (
      l.usuario_id === usuarioLogado.id || l.criado_por === usuarioLogado.id
    );
  }

  /**
   * Cancelar é mais restrito: só quem CRIOU (e o admin). Quem recebeu um
   * lembrete delegado adia e conclui, mas não some com a cobrança de outra
   * pessoa. Isto aqui só esconde o botão — quem barra de verdade é o
   * `podeCancelar` do backend/motores/lembretes.js.
   */
  function podeCancelarLembrete(l) {
    if (!l) return false;
    if (podeLembreteDeOutros) return true;
    return l.criado_por === usuarioLogado.id;
  }

  /**
   * O card amarelo. Recebe a mensagem tipo 'lembrete' e o objeto `lembrete`
   * que veio junto (do JOIN no /api/chat/messages ou do socket).
   *
   * Sem o objeto `lembrete` o card ainda é desenhado, só que sem estado nem
   * botões — é o caso de um lembrete cujo registro foi apagado do banco: a
   * anotação continua valendo como histórico da conversa.
   */
  /**
   * O cartão da ligação, no jeito de um registro de chamada: ícone redondo
   * com a cor do resultado, o resultado como título, número e duração
   * embaixo, e a gravação só quando alguém atendeu (caixa postal, recusa e
   * afins não têm conversa pra ouvir — o arquivo continua guardado).
   * O servidor grava o texto como
   *   "Ligação para (61) 99665-4376 · atendida · 0:16"
   *   "Ligação para (61) 99665-4376 · não atendeu (caixa postal)"
   *   "Ligação para (61) 99665-4376 não completou"
   * (backend/servicos/telefonia.js); daqui sai o número, o resultado e o tempo.
   */
  const RESULTADOS_LIGACAO = [
    // [texto do servidor, título, tom, ícone]
    [/^atendida/, 'Ligação atendida', 'ok', 'ph-phone-outgoing'],
    [/^recusou \(/, 'Recusou', 'erro', 'ph-phone-slash'],
    [/caixa postal\)$/, 'Caixa postal', 'neutro', 'ph-voicemail'],
    [/^recusou ou ocupado/, 'Recusou ou ocupado', 'erro', 'ph-phone-slash'],
    [/^não atendeu/, 'Não atendeu', 'neutro', 'ph-phone-outgoing'],
    [/^número indisponível/, 'Número indisponível', 'erro', 'ph-phone-x'],
    [/^a linha não chamou/, 'A linha não chamou', 'erro', 'ph-phone-x'],
  ];
  function montarCardLigacao(msg, dataObj) {
    const texto = String(msg.body || '');
    const numero = (/\(\d{2}\) [\d-]+/.exec(texto) || [''])[0];
    let titulo = 'Ligação';
    let tom = 'neutro';
    let icone = 'ph-phone-outgoing';
    let duracao = '';
    if (/não completou$/.test(texto)) {
      [titulo, tom, icone] = ['Não completou', 'erro', 'ph-phone-x'];
    } else {
      for (const parte of texto.split(' · ').slice(1)) {
        if (/^\d+:\d{2}$/.test(parte)) duracao = parte;
        const achado = RESULTADOS_LIGACAO.find(([re]) => re.test(parte));
        if (achado) [, titulo, tom, icone] = achado;
      }
      // Ligação de antes do resultado: só ia o tempo de conversa, então
      // foi atendida.
      if (titulo === 'Ligação' && duracao) [titulo, tom] = ['Ligação atendida', 'ok'];
    }
    const hora = dataObj.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
    const autor = escaparHtmlChat(msg.sent_by_name || 'Equipe');
    const idAudio = String(msg.wpp_id || msg.id || 'lig').replace(/[^\w-]/g, '_');
    // A gravação fica recolhida, como os avisos agrupados do sistema: a
    // setinha à direita diz que tem mais, e o clique em qualquer parte do
    // cartão abre. O player só é montado (e o áudio só é baixado) aí.
    const temGravacao = !!msg.media_path && tom === 'ok';
    const linhaExtra = temGravacao
      ? ` ligacao-expansivel" role="button" tabindex="0" title="Ouvir gravação" aria-expanded="false" onclick="window.alternarGravacaoLigacao(this)" onkeydown="if(event.key==='Enter'||event.key===' '){event.preventDefault();window.alternarGravacaoLigacao(this)}`
      : '';
    const seta = temGravacao ? '<i class="bx bx-chevron-down ligacao-seta"></i>' : '';
    const player = temGravacao
      ? `<div class="ligacao-gravacao" data-url="${escaparHtmlChat(msg.media_path)}" data-id="${idAudio}"></div>`
      : '';
    return `
      <div class="msg-wrapper-vertical">
        <div class="msg-bubble ligacao-card ligacao-${tom}">
          <div class="ligacao-linha${linhaExtra}">
            <span class="ligacao-icone"><i class="ph-bold ${icone}"></i></span>
            <div class="ligacao-info">
              <div class="ligacao-titulo">
                <span>${titulo}</span>
                <span class="ligacao-hora">${hora}</span>
              </div>
              <div class="ligacao-sub">
                <span>${escaparHtmlChat(numero || texto)}${duracao ? ` · ${duracao}` : ''}</span>
                <span class="ligacao-autor">${autor}</span>
              </div>
            </div>
            ${seta}
          </div>
          ${player}
        </div>
      </div>`;
  }

  // Abre/fecha a gravação do cartão da ligação. Na primeira abertura monta o
  // player; ao fechar, pausa se estiver tocando.
  window.alternarGravacaoLigacao = function (linha) {
    const card = linha.closest('.ligacao-card');
    const caixa = card && card.querySelector('.ligacao-gravacao');
    if (!caixa) return;
    const abrir = !card.classList.contains('ligacao-aberta');
    if (abrir && !caixa.firstElementChild) {
      caixa.innerHTML = gerarHTMLAudioPlayer(escaparHtmlChat(caixa.dataset.url), caixa.dataset.id);
    }
    if (!abrir) {
      const audio = caixa.querySelector('audio');
      if (audio && !audio.paused) window.togglePlayAudio(caixa.dataset.id);
    }
    card.classList.toggle('ligacao-aberta', abrir);
    linha.title = abrir ? 'Fechar gravação' : 'Ouvir gravação';
    linha.setAttribute('aria-expanded', String(abrir));
  };

  function montarCardLembrete(msg, dataObj) {
    const l = msg.lembrete || null;
    const hora = dataObj.toLocaleTimeString([], {
      hour: '2-digit',
      minute: '2-digit',
    });
    const autor = escaparHtmlChat(msg.sent_by_name || 'Equipe');
    const texto = escaparHtmlChat(l ? l.texto : msg.body || '');

    let estado = l ? l.status : 'sem-registro';
    // Entre o horário chegar e a varredura rodar (até 30s) o status no banco
    // ainda é 'pendente'. Mostrar "agendado" num lembrete que já passou da hora
    // faria a tela parecer travada.
    if (l && l.status === 'pendente' && new Date(l.lembrar_em) <= new Date())
      estado = 'disparado';

    const rotulos = {
      pendente: 'Agendado',
      disparado: 'Venceu',
      concluido: 'Concluído',
      cancelado: 'Cancelado',
      'sem-registro': 'Anotação',
    };
    const icones = {
      pendente: 'ph-alarm',
      disparado: 'ph-bell-ringing',
      concluido: 'ph-check-circle',
      cancelado: 'ph-x-circle',
      'sem-registro': 'ph-alarm',
    };

    let prazoHtml = '';
    if (l) {
      // Quem manda aqui é o `disparado_em`, não o status. Um lembrete concluído
      // pode ter avisado (ela atendeu o apito e fechou depois) ou não ter
      // avisado nunca (ela resolveu o assunto antes da hora) — e "era pra
      // avisar" só é verdade no segundo caso. Dizer isso no primeiro faz o card
      // desmentir um apito que a pessoa acabou de ouvir.
      // O `estado === 'disparado'` cobre a janela de até 30s entre o horário
      // chegar e a varredura gravar o `disparado_em`: sem ele, nesse intervalo
      // a pastilha diria "Venceu" enquanto o texto ainda dizia "Avisar".
      const jaAvisou = !!l.disparado_em || estado === 'disparado';
      const encerrado = estado === 'concluido' || estado === 'cancelado';

      let verbo;
      if (jaAvisou) verbo = 'Avisado';
      else if (encerrado) verbo = 'Era pra avisar';
      else verbo = 'Avisar';

      prazoHtml = `<div class="lembrete-prazo">
          <i class="ph-bold ph-clock"></i>
          <span>${verbo} <b>${escaparHtmlChat(formatarPrazoLembrete(l.lembrar_em))}</b></span>
        </div>`;
    }

    // Só aparece quando o lembrete é de OUTRA pessoa — no caso normal, em que
    // quem criou é quem vai ser avisada, essa linha seria ruído em todo card.
    let destinoHtml = '';
    if (l && l.usuario_nome && l.criado_por !== l.usuario_id) {
      destinoHtml = `<span class="lembrete-tag-destino"><i class="ph-bold ph-user"></i> para ${escaparHtmlChat(l.usuario_nome)}</span>`;
    }
    let adiadoHtml = '';
    if (l && l.adiado_vezes > 0) {
      adiadoHtml = `<span class="lembrete-tag-adiado"><i class="ph-bold ph-arrow-clockwise"></i> adiado ${l.adiado_vezes}x</span>`;
    }

    const podeAgir =
      l &&
      (estado === 'pendente' || estado === 'disparado') &&
      podeMexerNoLembrete(l);

    let acoesHtml = '';
    let painelAdiarHtml = '';
    if (podeAgir) {
      // Já adiou nesta rodada: o botão vira selo cinza até o lembrete avisar
      // de novo. O bloqueio de verdade está no backend — isto aqui só conta
      // pra pessoa por que não dá pra clicar.
      // Só ícone, como no apito do canto: o rótulo vai no `title`/`aria-label`,
      // então continua acessível e continua aparecendo ao parar o mouse em
      // cima. Escritos por extenso, os dois botões pesavam mais que o recado.
      const botaoAdiar = l.adiamento_usado
        ? `<button class="lembrete-btn-acao adiado" disabled
                   title="Você poderá adiar de novo quando ele avisar"
                   aria-label="Já adiado">
             <i class="ph-bold ph-check-circle"></i>
           </button>`
        : `<button class="lembrete-btn-acao adiar" onclick="lembreteAlternarAdiar(${l.id})"
                   title="Adiar" aria-label="Adiar">
             <i class="ph-bold ph-clock-clockwise"></i>
           </button>`;

      // Cancelar fica no meio, entre adiar e concluir, e some pra quem não
      // criou o lembrete. Continua cinza no hover (não vermelho): cancelar
      // aqui não apaga nada, só encerra a cobrança sem fingir que ela foi
      // resolvida — o card segue na conversa como histórico.
      const botaoCancelar = podeCancelarLembrete(l)
        ? `<button class="lembrete-btn-acao cancelar" onclick="lembreteCancelar(${l.id})"
                   title="Cancelar lembrete" aria-label="Cancelar lembrete">
             <i class="ph-bold ph-x"></i>
           </button>`
        : '';

      acoesHtml = `
        <div class="lembrete-acoes">
          ${botaoAdiar}
          ${botaoCancelar}
          <button class="lembrete-btn-acao concluir" onclick="lembreteConcluir(${l.id})"
                  title="Concluir" aria-label="Concluir">
            <i class="ph-bold ph-check"></i>
          </button>
        </div>`;

      // A linha de adiar mora FORA do balão, à esquerda dele: abrir por cima da
      // conversa (como era antes, num diálogo escuro) tapava justamente o
      // histórico que a pessoa está consultando pra decidir o novo horário.
      if (!l.adiamento_usado) {
        painelAdiarHtml = `
          <div class="lembrete-adiar-linha hidden lembrete-estado-${estado}"
               data-painel-adiar="${l.id}">
            <span class="lembrete-adiar-rotulo">Adiar para daqui</span>
            <input type="text" class="lembrete-adiar-campo" data-adiar-h="${l.id}"
                   inputmode="numeric" maxlength="2" value="01" aria-label="Horas" />
            <span class="lembrete-adiar-unidade">Hora</span>
            <span class="lembrete-adiar-sep">:</span>
            <input type="text" class="lembrete-adiar-campo" data-adiar-m="${l.id}"
                   inputmode="numeric" maxlength="2" value="00" aria-label="Minutos" />
            <span class="lembrete-adiar-unidade">Min.</span>
            <button class="lembrete-adiar-ok" onclick="lembreteConfirmarAdiar(${l.id})" title="Confirmar">
              <i class="ph-bold ph-check"></i>
            </button>
            <button class="lembrete-adiar-fechar" onclick="lembreteAlternarAdiar(${l.id})" title="Cancelar">
              <i class="ph-bold ph-x"></i>
            </button>
          </div>`;
      }
    }

    // Etiquetas e botões dividem a MESMA linha: eram duas fileiras empilhadas,
    // e depois que os botões viraram quadrados de ícone a segunda fileira
    // passou a ser quase só espaço vazio. Se um dos lados não existir, o
    // outro ocupa a linha sozinho (quem alinha é o CSS, não um espaçador).
    const tagsHtml =
      destinoHtml || adiadoHtml
        ? `<div class="lembrete-tags">${destinoHtml}${adiadoHtml}</div>`
        : '';
    const rodapeHtml =
      tagsHtml || acoesHtml
        ? `<div class="lembrete-rodape">${tagsHtml}${acoesHtml}</div>`
        : '';

    return `
      ${painelAdiarHtml}
      <div class="msg-wrapper-vertical">
        <div class="msg-bubble lembrete-card lembrete-estado-${estado}">
          <div class="msg-header-outside">
            <span class="msg-sender-label">${autor} <i class="ph-bold ph-alarm"></i></span>
            <span class="msg-meta">${hora}</span>
          </div>
          <div class="lembrete-topo">
            <!-- ph-bold e não ph-fill: ícone de contorno, vazado. Mancha
                 cheia pesa mais que o próprio recado num selo de 10px. -->
            <span class="lembrete-selo"><i class="ph-bold ${icones[estado]}"></i> LEMBRETE</span>
            <span class="lembrete-status-pill">${rotulos[estado]}</span>
          </div>
          <div class="lembrete-texto">${texto}</div>
          ${prazoHtml}
          ${rodapeHtml}
        </div>
      </div>`;
  }

  /** Redesenha um card sozinho, sem recarregar a conversa inteira. */
  function repintarCardLembrete(lembrete) {
    if (!lembrete || !lembrete.wpp_id) return;
    const row = document.querySelector(
      `.lembrete-row[data-wpp-id="${lembrete.wpp_id}"]`
    );
    if (!row) return;
    const meta = row.querySelector('.msg-meta');
    const autorEl = row.querySelector('.msg-sender-label');
    // Reaproveita o horário e o autor que já estavam desenhados: eles são da
    // MENSAGEM (quando o card foi criado), não do lembrete, e não mudam.
    const dataFalsa = new Date();
    const horaOriginal = meta ? meta.textContent.trim() : '';
    row.innerHTML = montarCardLembrete(
      {
        lembrete,
        body: lembrete.texto,
        sent_by_name: autorEl
          ? autorEl.textContent.trim()
          : lembrete.criado_por_nome,
      },
      {
        toLocaleTimeString: () => horaOriginal,
      }
    );
  }

  // (o socket.on('lembrete_atualizado') fica lá embaixo, junto dos outros
  // ouvintes — aqui em cima a variável `socket` ainda não existe)

  // --- Ações do card ---------------------------------------------------

  async function chamarAcaoLembrete(id, acao, corpo) {
    try {
      const res = await fetch(`/api/lembretes/${id}/${acao}`, {
        method: 'POST',
        headers: cabecalhoAuth(),
        body: JSON.stringify(corpo || {}),
      });
      const dados = await res.json();
      if (!res.ok || !dados.success) {
        mostrarAlerta(
          dados.message || 'Não consegui atualizar o lembrete.',
          'error'
        );
        return null;
      }
      if (dados.lembrete) repintarCardLembrete(dados.lembrete);
      return dados.lembrete;
    } catch (e) {
      mostrarAlerta('Falha de conexão ao atualizar o lembrete.', 'error');
      return null;
    }
  }

  window.lembreteConcluir = async function (id) {
    const l = await chamarAcaoLembrete(id, 'concluir');
    if (l) mostrarAlerta('Lembrete concluído.', 'success');
  };

  /**
   * Cancelar pergunta antes; concluir não. A diferença é que concluir é o
   * caminho normal ("fiz"), e cancelar diz que a tarefa não vai acontecer —
   * clique errado aqui apaga um compromisso com cliente.
   */
  window.lembreteCancelar = async function (id) {
    const confirmacao = await Swal.fire({
      title: 'Cancelar este lembrete?',
      text: 'Ele para de avisar e fica no histórico como cancelado.',
      icon: 'question',
      showCancelButton: true,
      confirmButtonColor: '#ea580c',
      confirmButtonText: 'Sim, cancelar',
      cancelButtonText: 'Voltar',
    });
    if (!confirmacao.isConfirmed) return;
    const l = await chamarAcaoLembrete(id, 'cancelar');
    if (l) mostrarAlerta('Lembrete cancelado.', 'success');
  };

  /** Abre/fecha a linha de adiar que fica ao lado do card. */
  window.lembreteAlternarAdiar = function (id) {
    const painel = document.querySelector(`[data-painel-adiar="${id}"]`);
    if (!painel) return;

    // Só um aberto por vez: dois desses na tela ao mesmo tempo viram confusão
    // sobre qual lembrete está sendo adiado.
    document.querySelectorAll('[data-painel-adiar]').forEach((p) => {
      if (p !== painel) p.classList.add('hidden');
    });

    const vaiAbrir = painel.classList.contains('hidden');
    painel.classList.toggle('hidden', !vaiAbrir);
    if (!vaiAbrir) return;

    const campoH = painel.querySelector(`[data-adiar-h="${id}"]`);
    const campoM = painel.querySelector(`[data-adiar-m="${id}"]`);

    // O painel é remontado a cada repintura do card, então os campos são novos
    // — mas dentro da MESMA repintura ele pode abrir e fechar várias vezes, e
    // sem esta marca cada abertura empilharia mais um jogo de ouvintes.
    if (!painel.dataset.pronto) {
      painel.dataset.pronto = '1';
      prepararCampoRelogio(campoH, 23, () => campoM.focus());
      prepararCampoRelogio(campoM, 59, null);
      painel.addEventListener('keydown', (e) => {
        if (e.key === 'Enter') {
          e.preventDefault();
          window.lembreteConfirmarAdiar(id);
        } else if (e.key === 'Escape') {
          painel.classList.add('hidden');
        }
      });
    }

    campoH.focus();
    campoH.select();
  };

  window.lembreteConfirmarAdiar = async function (id) {
    const painel = document.querySelector(`[data-painel-adiar="${id}"]`);
    if (!painel) return;
    const horas =
      parseInt(painel.querySelector(`[data-adiar-h="${id}"]`).value, 10) || 0;
    const mins =
      parseInt(painel.querySelector(`[data-adiar-m="${id}"]`).value, 10) || 0;
    const minutos = horas * 60 + mins;

    if (minutos < 1) {
      mostrarAlerta('Diga por quanto tempo adiar.', 'warning');
      return;
    }

    const l = await chamarAcaoLembrete(id, 'adiar', { minutos });
    if (l)
      mostrarAlerta(
        `Adiado para ${formatarPrazoLembrete(l.lembrar_em)}.`,
        'success'
      );
  };

  // --- Modal de criação ------------------------------------------------

  function fecharModalLembrete() {
    if (modalLembrete) modalLembrete.classList.add('hidden');
  }

  // --- Calendário do lembrete ------------------------------------------
  //
  // Feito à mão em vez de <input type="date"> porque o nativo abre o
  // calendário DO NAVEGADOR: ele ignora o tema do sistema, muda de aparência
  // entre Chrome e Edge e não dá pra estilizar. Aqui o mês inteiro fica na
  // tela, já no visual do CRM.

  const MESES_LEMBRETE = [
    'Janeiro',
    'Fevereiro',
    'Março',
    'Abril',
    'Maio',
    'Junho',
    'Julho',
    'Agosto',
    'Setembro',
    'Outubro',
    'Novembro',
    'Dezembro',
  ];

  // O dia escolhido (só ano/mês/dia; a hora vem dos campos do rodapé) e o mês
  // que está sendo exibido, que muda ao navegar sem escolher nada.
  let diaSelecionadoLembrete = null;
  let mesVisivelLembrete = new Date();

  const inputMinutoLembrete = document.getElementById('lembrete-minuto');
  const gradeDiasLembrete = document.getElementById('lembrete-cal-dias');
  const rotuloMesLembrete = document.getElementById('lembrete-cal-mes');

  const mesmoDia = (a, b) =>
    a &&
    b &&
    a.getFullYear() === b.getFullYear() &&
    a.getMonth() === b.getMonth() &&
    a.getDate() === b.getDate();

  function desenharCalendarioLembrete() {
    if (!gradeDiasLembrete) return;

    const ano = mesVisivelLembrete.getFullYear();
    const mes = mesVisivelLembrete.getMonth();
    rotuloMesLembrete.textContent = `${MESES_LEMBRETE[mes]} de ${ano}`;

    const hoje = new Date();
    hoje.setHours(0, 0, 0, 0);

    // Começa no domingo da semana em que cai o dia 1º, pra grade fechar certo.
    const primeiro = new Date(ano, mes, 1);
    const inicio = new Date(primeiro);
    inicio.setDate(1 - primeiro.getDay());

    let html = '';
    for (let i = 0; i < 42; i++) {
      const dia = new Date(inicio);
      dia.setDate(inicio.getDate() + i);
      dia.setHours(0, 0, 0, 0);

      // A grade tem 6 linhas fixas pra altura do modal não pular ao trocar de
      // mês. A última só é desenhada se o mês realmente chegar nela.
      if (i >= 35 && dia.getMonth() !== mes) break;

      const classes = ['lembrete-cal-dia'];
      if (dia.getMonth() !== mes) classes.push('fora-do-mes');
      if (mesmoDia(dia, hoje)) classes.push('hoje');
      if (mesmoDia(dia, diaSelecionadoLembrete)) classes.push('selecionado');

      // Dia que já passou não pode receber lembrete. Fica visível (a pessoa se
      // localiza no mês por ele) mas não clicável.
      const passado = dia < hoje;
      if (passado) classes.push('passado');

      html +=
        `<button type="button" class="${classes.join(' ')}"` +
        (passado
          ? ' disabled'
          : ` data-iso="${dia.getFullYear()}-${String(dia.getMonth() + 1).padStart(2, '0')}-${String(dia.getDate()).padStart(2, '0')}"`) +
        `>${dia.getDate()}</button>`;
    }
    gradeDiasLembrete.innerHTML = html;
  }

  /** Preenche dia + hora a partir de um Date e redesenha tudo. */
  function definirQuandoLembrete(d) {
    const pad = (n) => String(n).padStart(2, '0');
    diaSelecionadoLembrete = new Date(
      d.getFullYear(),
      d.getMonth(),
      d.getDate()
    );
    mesVisivelLembrete = new Date(d.getFullYear(), d.getMonth(), 1);
    inputLembreteHora.value = pad(d.getHours());
    inputMinutoLembrete.value = pad(d.getMinutes());
    desenharCalendarioLembrete();
    atualizarResumoLembrete();
  }

  /** O Date escolhido no modal, ou null se estiver incompleto/inválido. */
  function lerQuandoLembrete() {
    if (!diaSelecionadoLembrete) return null;
    const h = parseInt(inputLembreteHora.value, 10);
    const m = parseInt(inputMinutoLembrete.value, 10);
    if (isNaN(h) || isNaN(m) || h < 0 || h > 23 || m < 0 || m > 59) return null;
    const d = new Date(diaSelecionadoLembrete);
    d.setHours(h, m, 0, 0);
    return isNaN(d.getTime()) ? null : d;
  }

  function atualizarResumoLembrete() {
    if (!resumoLembrete) return;
    const textoEl = document.getElementById('lembrete-resumo-texto');
    const d = lerQuandoLembrete();

    const escrever = (classe, html) => {
      resumoLembrete.className =
        'lembrete-resumo' + (classe ? ' ' + classe : '');
      resumoLembrete.innerHTML = html;
    };

    if (!d) {
      escrever(
        '',
        '<span class="lembrete-resumo-texto">Escolha um dia e um horário.</span>'
      );
      return;
    }
    if (d.getTime() <= Date.now()) {
      escrever(
        'invalido',
        '<span class="lembrete-resumo-texto"><i class="ph-bold ph-warning"></i> Esse horário já passou.</span>'
      );
      return;
    }

    // "hoje"/"amanhã" no lugar do nome do dia quando for o caso: é o que a
    // pessoa tem na cabeça, e a data completa vem logo ao lado de qualquer jeito.
    const hoje = new Date();
    const amanha = new Date();
    amanha.setDate(hoje.getDate() + 1);
    let nomeDia;
    if (mesmoDia(d, hoje)) nomeDia = 'hoje';
    else if (mesmoDia(d, amanha)) nomeDia = 'amanhã';
    else
      nomeDia = d
        .toLocaleDateString('pt-BR', { weekday: 'long' })
        .replace('-feira', '');

    const dataCurta = d.toLocaleDateString('pt-BR');
    const hora = d.toLocaleTimeString('pt-BR', {
      hour: '2-digit',
      minute: '2-digit',
    });

    const minutos = Math.round((d.getTime() - Date.now()) / 60000);
    let daqui;
    if (minutos < 60) daqui = `em ${minutos} min`;
    else if (minutos < 60 * 24) daqui = `em ${Math.round(minutos / 60)}h`;
    else {
      const dias = Math.round(minutos / (60 * 24));
      daqui = dias === 1 ? 'em 1 dia' : `em ${dias} dias`;
    }

    escrever(
      'ok',
      `<span class="lembrete-resumo-texto">
         <i class="ph-bold ph-bell-ringing"></i>
         Lembrete marcado para <b>${escaparHtmlChat(nomeDia)}</b>
         (${escaparHtmlChat(dataCurta)}) às <b>${escaparHtmlChat(hora)}</b>
       </span>
       <span class="lembrete-resumo-daqui">${escaparHtmlChat(daqui)}</span>`
    );
  }

  if (gradeDiasLembrete) {
    gradeDiasLembrete.addEventListener('click', (e) => {
      const botao = e.target.closest('.lembrete-cal-dia');
      if (!botao || !botao.dataset.iso) return;
      const [a, m, dd] = botao.dataset.iso.split('-').map(Number);
      diaSelecionadoLembrete = new Date(a, m - 1, dd);
      desenharCalendarioLembrete();
      atualizarResumoLembrete();
    });
  }

  const navegarMesLembrete = (passo) => {
    mesVisivelLembrete = new Date(
      mesVisivelLembrete.getFullYear(),
      mesVisivelLembrete.getMonth() + passo,
      1
    );
    desenharCalendarioLembrete();
  };
  const btnMesAnterior = document.getElementById('lembrete-cal-anterior');
  const btnMesProximo = document.getElementById('lembrete-cal-proximo');
  if (btnMesAnterior)
    btnMesAnterior.addEventListener('click', () => navegarMesLembrete(-1));
  if (btnMesProximo)
    btnMesProximo.addEventListener('click', () => navegarMesLembrete(1));

  /**
   * Campo de hora ou de minuto: só dígito, 24 horas (00-23 e 00-59), e zero à
   * esquerda ao sair — quem digita "9" quer 09.
   *
   * Serve tanto o relógio do modal quanto a linha de adiar do card, por isso
   * `aoMudar` é parâmetro: no modal ele reescreve o resumo, no card não há
   * resumo nenhum pra atualizar.
   */
  function prepararCampoRelogio(campo, maximo, aoCompletar, aoMudar) {
    if (!campo) return;
    const mudou = () => {
      if (aoMudar) aoMudar();
    };

    campo.addEventListener('input', () => {
      campo.value = campo.value.replace(/\D/g, '').slice(0, 2);
      // Passa pro campo dos minutos sozinha quando o valor já não pode crescer
      // mais (2 dígitos, ou um primeiro dígito alto demais pra ter segundo).
      const n = parseInt(campo.value, 10);
      if (
        aoCompletar &&
        (campo.value.length === 2 || (!isNaN(n) && n * 10 > maximo))
      ) {
        aoCompletar();
      }
      mudou();
    });

    // Setas pra cima/baixo mexem no valor, como em qualquer campo de hora.
    campo.addEventListener('keydown', (e) => {
      if (e.key !== 'ArrowUp' && e.key !== 'ArrowDown') return;
      e.preventDefault();
      const atual = parseInt(campo.value, 10) || 0;
      const passo = e.key === 'ArrowUp' ? 1 : -1;
      const novo = (atual + passo + (maximo + 1)) % (maximo + 1);
      campo.value = String(novo).padStart(2, '0');
      mudou();
    });

    campo.addEventListener('blur', () => {
      let n = parseInt(campo.value, 10);
      if (isNaN(n)) n = 0;
      campo.value = String(Math.min(Math.max(n, 0), maximo)).padStart(2, '0');
      mudou();
    });

    campo.addEventListener('focus', () => campo.select());
  }

  prepararCampoRelogio(
    inputLembreteHora,
    23,
    () => inputMinutoLembrete.focus(),
    atualizarResumoLembrete
  );
  prepararCampoRelogio(inputMinutoLembrete, 59, null, atualizarResumoLembrete);

  let vendedoresLembreteCarregados = false;
  async function abrirModalLembrete() {
    if (!chatSelecionado) {
      mostrarAlerta('Abra uma conversa antes de criar um lembrete.', 'warning');
      return;
    }
    const nomeCliente =
      chatSelecionado.crm_nome ||
      chatSelecionado.name ||
      formatarTelefoneBR(chatSelecionado.chat_id.replace(/\D/g, ''));
    document.getElementById('lembrete-nome-cliente').textContent = nomeCliente;

    inputLembreteTexto.value = '';
    if (contadorLembrete) contadorLembrete.textContent = '0';

    // Padrão: amanhã de manhã. É de longe o caso mais comum ("liga pra ele
    // amanhã"), então na maioria das vezes é só escrever e salvar.
    const padrao = new Date();
    padrao.setDate(padrao.getDate() + 1);
    padrao.setHours(9, 0, 0, 0);
    definirQuandoLembrete(padrao);

    const blocoDelegar = document.getElementById('lembrete-bloco-delegar');
    if (podeLembreteDeOutros && blocoDelegar) {
      blocoDelegar.style.display = 'block';
      if (!vendedoresLembreteCarregados) {
        vendedoresLembreteCarregados = true;
        try {
          const res = await fetch('/api/team/vendedores', {
            headers: {
              Authorization: `Bearer ${localStorage.getItem('token')}`,
            },
          });
          const vendedores = await res.json();
          selectLembreteDest.innerHTML = `<option value="${usuarioLogado.id}">Eu mesmo(a)</option>`;
          vendedores.forEach((v) => {
            if (String(v.id) === String(usuarioLogado.id)) return;
            const opt = document.createElement('option');
            opt.value = v.id;
            opt.textContent = v.name || v.username;
            selectLembreteDest.appendChild(opt);
          });
        } catch (e) {
          vendedoresLembreteCarregados = false;
        }
      }
    }

    modalLembrete.classList.remove('hidden');
    inputLembreteTexto.focus();
  }

  if (btnCriarLembrete) {
    btnCriarLembrete.addEventListener('click', (e) => {
      e.preventDefault();
      abrirModalLembrete();
    });
  }

  const btnFecharModalLembrete = document.getElementById(
    'btn-fechar-modal-lembrete'
  );
  if (btnFecharModalLembrete)
    btnFecharModalLembrete.addEventListener('click', fecharModalLembrete);
  if (modalLembrete) {
    modalLembrete.addEventListener('click', (e) => {
      if (e.target === modalLembrete) fecharModalLembrete();
    });
  }

  if (inputLembreteTexto) {
    inputLembreteTexto.addEventListener('input', () => {
      if (contadorLembrete)
        contadorLembrete.textContent = inputLembreteTexto.value.length;
    });
  }
  if (btnSalvarLembrete) {
    btnSalvarLembrete.addEventListener('click', async () => {
      if (!chatSelecionado) return;
      const texto = inputLembreteTexto.value.trim();
      const quando = lerQuandoLembrete();
      if (!texto)
        return mostrarAlerta(
          'Escreva do que você quer ser lembrada.',
          'warning'
        );
      if (!quando || quando.getTime() <= Date.now())
        return mostrarAlerta('Escolha uma data e hora futuras.', 'warning');

      const htmlOriginal = btnSalvarLembrete.innerHTML;
      btnSalvarLembrete.disabled = true;
      btnSalvarLembrete.innerHTML =
        '<i class="fas fa-spinner fa-spin"></i> Criando...';
      try {
        const res = await fetch('/api/lembretes', {
          method: 'POST',
          headers: cabecalhoAuth(),
          body: JSON.stringify({
            numeroWpp: chatSelecionado.chat_id.replace(/\D/g, ''),
            botId: chatSelecionado.bot_id,
            texto,
            // ISO com fuso: o servidor guarda o instante absoluto, não a
            // string "09:00" solta.
            lembrarEm: quando.toISOString(),
            usuarioId:
              podeLembreteDeOutros && selectLembreteDest
                ? selectLembreteDest.value
                : undefined,
          }),
        });
        const dados = await res.json();
        if (!res.ok || !dados.success) {
          mostrarAlerta(
            dados.message || 'Não consegui criar o lembrete.',
            'error'
          );
          return;
        }
        fecharModalLembrete();
        mostrarAlerta(
          `Combinado! Te aviso ${formatarPrazoLembrete(dados.lembrete.lembrar_em)}.`,
          'success'
        );
      } catch (e) {
        mostrarAlerta('Falha de conexão ao criar o lembrete.', 'error');
      } finally {
        btnSalvarLembrete.disabled = false;
        btnSalvarLembrete.innerHTML = htmlOriginal;
      }
    });
  }

  // Ctrl+L com a conversa aberta. Fora do campo de texto também funciona.
  document.addEventListener('keydown', (e) => {
    if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'l') {
      if (!chatSelecionado) return;
      e.preventDefault();
      abrirModalLembrete();
    }
    if (
      e.key === 'Escape' &&
      modalLembrete &&
      !modalLembrete.classList.contains('hidden')
    ) {
      fecharModalLembrete();
    }
  });

  // Inputs CRM
  const crmNome = document.getElementById('crm-nome');
  const crmCpf = document.getElementById('crm-cpf');
  const crmEmail = document.getElementById('crm-email');
  const crmTelefone = document.getElementById('crm-telefone');
  const crmNotas = document.getElementById('crm-notas');
  const btnSalvarCrm = document.getElementById('btn-salvar-crm');
  const infoAvatarBig = document.getElementById('info-avatar-big');
  const infoNumeroWpp = document.getElementById('info-numero-wpp');
  const infoNomeLateralBig = document.getElementById(
    'info-nome-cliente-lateral'
  );
  const btnMagicWand = document.getElementById('btn-magic-wand');
  const magicWandMenu = document.getElementById('magic-wand-menu');
  const listaMagicPrompts = document.getElementById('lista-magic-prompts');
  const btnNovoPrompt = document.getElementById('btn-novo-prompt');
  const modalAiPrompt = document.getElementById('modal-ai-prompt');
  const btnFecharModalPrompt = document.getElementById(
    'btn-fechar-modal-prompt'
  );
  const btnSalvarPrompt = document.getElementById('btn-salvar-prompt');
  const inputPromptId = document.getElementById('input-prompt-id');
  const inputPromptTitulo = document.getElementById('input-prompt-titulo');
  const inputPromptInterno = document.getElementById('input-prompt-interno');
  const inputsCpf = [
    document.getElementById('crm-cpf'),
    document.getElementById('novo-cliente-cpf'),
  ];
  inputsCpf.forEach((input) => {
    if (input) {
      input.addEventListener('input', (e) => {
        e.target.value = window.aplicarMascaraCPF(e.target.value);
      });
      input.addEventListener('blur', (e) => {
        let cpfAjustado = window.limparEPreencherCPF(e.target.value);
        if (cpfAjustado) e.target.value = window.aplicarMascaraCPF(cpfAjustado);
      });
    }
  });
  // Dados do Usuário
  const usuarioLogado = JSON.parse(localStorage.getItem('usuario') || '{}');
  // O que esta pessoa pode no bate-papo (js/acesso.js; o servidor confere de novo).
  const veEquipe = Acesso.pode('clientes.ver_equipe');
  const podeLembreteDeOutros = Acesso.pode('lembretes.para_outros');
  const podeEtiquetas = Acesso.pode('etiquetas.gerenciar');
  const podeVincularRcs = Acesso.pode('rcs.vincular_numeros');
  const podeEscolherResponsavel = Acesso.pode('clientes.escolher_responsavel');
  const podePrompts = Acesso.pode('ia.editar_prompts');
  // Elementos DOM (Agora é seguro pegar eles)
  const containerTagsCrm = document.getElementById('container-tags-cliente');
  const btnAddTagCrm = document.getElementById('btn-add-tag-crm');
  const dropdownTagsCrm = document.getElementById('dropdown-tags-crm');
  // Estado Local
  let todosChats = [];
  let chatSelecionado = null; // { chatId, botId }
  // wpp_id da mensagem que a PRÓXIMA carga de histórico deve focar em vez de
  // abrir no rodapé. Quem enche isto é o apito do lembrete (direto ou pelo
  // `?msg=` da URL). Zera sozinho depois de usar: é pedido de uma vez só, não
  // um modo — senão toda troca de conversa voltaria pra mesma mensagem velha.
  let wppIdParaFocar = null;
  let paginaAtual = 1;
  let carregandoChats = false;
  let temMaisChats = true;
  let todasTagsSistema = [];
  let tagsDoClienteAtual = [];
  let modoSelecaoMassa = false;
  let chatsSelecionadosMassa = new Set();
  // Transferência em massa (rodízio) — só admin. `equipeTransferenciaMassa`
  // guarda a lista de atendentes (cache); `atendentesMarcadosMassa` guarda os
  // ids marcados NA ORDEM em que foram marcados — essa ordem é o rodízio
  // (chat i vai pro atendentesMarcadosMassa[i % n]).
  let equipeTransferenciaMassa = null;
  const atendentesMarcadosMassa = [];
  let ultimaAtualizacaoBots = 0;
  // Filtro de bots da lista lateral. Virou multi-seleção: a lista guarda os
  // instancia_id marcados e VAZIA quer dizer "Todos os Bots" (nunca guardamos
  // a string 'todos' aqui dentro, senão ela vazaria pra query como se fosse um
  // bot de verdade). O <select> nativo continua no DOM, escondido, só pra não
  // quebrar quem ainda lê o .value dele — fica com o id quando é exatamente um
  // bot e com 'todos' em qualquer outro caso.
  let botsSelecionados = [];
  // "Desmarcar todos" (24/09/2026): lista vazia já significa "todos", então
  // o estado de NENHUM bot marcado precisa de uma marca própria. Com ela
  // ligada a lista lateral fica vazia até a vendedora marcar algum bot.
  let nenhumBotFiltro = false;
  // A vendedora mexeu nas pílulas de "Status Bot" do popup e ainda não deu
  // Aplicar? Enquanto isso o refresh de 30s do dropdown não pode repintar as
  // pílulas por cima do que ela escolheu.
  let statusBotEditado = false;
  // Vendedora: bots em que ela já teve conversa na tela desde que abriu a
  // página. É daqui que sai a lista do dropdown dela (ver
  // carregarFiltroBotsComStatus) — nunca encolhe por causa de um filtro.
  const botsComClientesVistos = new Set();
  // id -> { name, html, online }, pra montar o resumo do trigger sem depender do DOM,
  // que é reconstruído a cada 30s pelo carregarFiltroBotsComStatus.
  let infoBotsFiltro = new Map();
  let timerFiltroBots = null;
  // Tem clique de filtro esperando virar busca? Evita repetir a mesma consulta
  // quando o menu fecha sem nada ter mudado.
  let filtroBotsPendente = false;
  let timerCliqueLongo = null;

  // O que vai no ?botId= das rotas de lista/contagem: CSV dos marcados ou
  // 'todos'. O backend aceita os dois formatos.
  function paramBotsFiltro() {
    // Nenhum bot marcado: manda um id que não existe. O backend filtra por
    // ANY() e devolve lista vazia e contagem zero — sem precisar de rota
    // nova nem de restart do servidor.
    if (nenhumBotFiltro) return '__nenhum__';
    return botsSelecionados.length ? botsSelecionados.join(',') : 'todos';
  }

  // true quando o bot da mensagem passa pelo filtro atual da lista.
  function botPassaNoFiltro(botIdMsg, normalizar) {
    if (nenhumBotFiltro) return false;
    if (!botsSelecionados.length) return true;
    const alvo = normalizar ? normalizar(botIdMsg) : botIdMsg;
    return botsSelecionados.some((b) =>
      normalizar ? normalizar(b) === alvo : b === alvo
    );
  }
  window.figurinhasSalvas = [];
  async function carregarMemoriaFigurinhas() {
    try {
      const res = await fetch('/api/stickers', {
        headers: { Authorization: `Bearer ${localStorage.getItem('token')}` },
      });
      const data = await res.json();
      if (data.success) window.figurinhasSalvas = data.stickers;
    } catch (e) {}
  }
  carregarMemoriaFigurinhas();
  if (window.intervaloCronometroSla !== null) {
    clearInterval(window.intervaloCronometroSla);
    window.intervaloCronometroSla = null;
  }
  // MÁQUINA DO TEMPO: Atualiza o SLA em tempo real
  window.atualizarVisualSLA = function () {
    const elCronometro = document.getElementById('cronometro-sla');
    if (!elCronometro || !chatSelecionado) return;
    // Limpa qualquer relógio antigo rodando
    if (window.intervaloCronometroSla) {
      clearInterval(window.intervaloCronometroSla);
    }
    const renderSLA = () => {
      if (!chatSelecionado) {
        if (window.intervaloCronometroSla)
          clearInterval(window.intervaloCronometroSla);
        return;
      }
      if (chatSelecionado.last_message_from_me) {
        elCronometro.style.display = 'inline-block';
        elCronometro.innerHTML = `<i class="bx bx-check-double"></i> Aguardando Retorno`;
        elCronometro.style.backgroundColor = 'rgba(100, 116, 139, 0.15)';
        elCronometro.style.color = '#64748b';
      } else {
        // Se a última mensagem foi do cliente
        const dataMsg = new Date(chatSelecionado.last_message_at).getTime();
        const agora = new Date().getTime();
        const diffSegundosTotais = Math.floor((agora - dataMsg) / 1000);
        const diffMinutos = Math.floor(diffSegundosTotais / 60);
        // REGRA DE TRANQUILIDADE: Se faz menos de 1 minuto, exibe o status como se estivesse respondido
        if (diffMinutos < 1) {
          elCronometro.style.display = 'inline-block';
          elCronometro.innerHTML = `<i class="bx bx-check-double"></i> Aguardando Retorno`;
          elCronometro.style.backgroundColor = 'rgba(100, 116, 139, 0.15)';
          elCronometro.style.color = '#64748b';
        } else {
          // Mais de 1 minuto: Ativa o Cronômetro Digital (HH:MM:SS)
          elCronometro.style.display = 'inline-block';
          // Cálculos matemáticos para o formato 00:00:00
          const horas = Math.floor(diffSegundosTotais / 3600)
            .toString()
            .padStart(2, '0');
          const minutos = Math.floor((diffSegundosTotais % 3600) / 60)
            .toString()
            .padStart(2, '0');
          const segundos = (diffSegundosTotais % 60)
            .toString()
            .padStart(2, '0');
          const tempoFormatado = `${horas}:${minutos}:${segundos}`;
          // 1. Verifica se o GIF já está na tela para não recriar e resetar a animação
          const iconeExiste = elCronometro.querySelector('.icone-gif-colorido');
          if (!iconeExiste) {
            // Cria a tag do GIF e um "span" separado apenas para segurar os números do tempo
            elCronometro.innerHTML = `<span class="icone-gif-colorido"></span><span class="sla-texto-tempo"></span>`;
          }
          // 2. Atualiza APENAS o texto do tempo, deixando o GIF rodando livremente
          elCronometro.querySelector('.sla-texto-tempo').innerText =
            ` Esperando: ${tempoFormatado}`;
          // 3. Aplica as cores de acordo com o tempo de SLA (O CSS vai puxar essa cor pro GIF)
          if (diffMinutos < 10) {
            // Até 10 minutos: AMARELO
            elCronometro.style.backgroundColor = 'rgba(234, 179, 8, 0.15)';
            elCronometro.style.color = '#eab308';
          } else if (diffMinutos < 30) {
            // De 10 a 30 minutos: LARANJA
            elCronometro.style.backgroundColor = 'rgba(249, 115, 22, 0.15)';
            elCronometro.style.color = '#f97316';
          } else {
            // Passou de 30 minutos: VERMELHO
            elCronometro.style.backgroundColor = 'rgba(239, 68, 68, 0.15)';
            elCronometro.style.color = '#ef4444';
          }
        }
      }
    };
    renderSLA(); // Roda a primeira vez instantaneamente
    window.intervaloCronometroSla = setInterval(renderSLA, 1000); // Roda a cada 1 segundo para mostrar os segundos correndo!
  };

  const urlParamFiltros = new URLSearchParams(window.location.search);
  const tagInicialDaUrl = urlParamFiltros.get('tagId');
  const statusInicialDaUrl = urlParamFiltros.get('status');

  let filtrosAtivos = {
    ordem: 'desc',
    status: statusInicialDaUrl ? statusInicialDaUrl : 'todos',
    periodo: 'todos',
    dataInicio: '',
    dataFim: '',
    tags: tagInicialDaUrl ? [parseInt(tagInicialDaUrl)] : [],
    vendedores: [],
    etapas: [], // ids das etapas marcadas no filtro "Funil e etapa"
  };

  if (statusInicialDaUrl) {
    setTimeout(() => {
      const btnTodos = document.querySelector(
        '[data-tipo="status"][data-valor="todos"]'
      );
      const btnAlvo = document.querySelector(
        `[data-tipo="status"][data-valor="${statusInicialDaUrl}"]`
      );
      if (btnTodos) btnTodos.classList.remove('ativo');
      if (btnAlvo) btnAlvo.classList.add('ativo');
    }, 100);
  }

  if (btnToggleInfo && painelInfo) {
    btnToggleInfo.addEventListener('click', () => {
      painelInfo.classList.toggle('fechado');
    });
  }
  if (btnFecharInfoMobile && painelInfo) {
    btnFecharInfoMobile.addEventListener('click', () => {
      painelInfo.classList.add('fechado');
    });
  }
  if (btnSalvarCrm) {
    btnSalvarCrm.addEventListener('click', async () => {
      if (!chatSelecionado) return;
      const btnHtml = btnSalvarCrm.innerHTML;
      btnSalvarCrm.disabled = true;
      btnSalvarCrm.innerHTML =
        '<i class="fas fa-spinner fa-spin"></i> Salvando...';
      // 1. PREPARAÇÃO DOS DADOS (AGORA COM CAMPOS DINÂMICOS)
      const camposCustomizados = {};
      camposDinamicosCRM.forEach((campo) => {
        const inputEl = document.getElementById(`crm-dinamico-${campo.id}`);
        if (inputEl) {
          if (inputEl.type === 'checkbox') {
            camposCustomizados[campo.nome] = inputEl.checked ? 'true' : 'false';
          } else {
            camposCustomizados[campo.nome] = inputEl.value.trim();
          }
        }
      });
      // 👇 CORREÇÃO: Força o envio do Telefone e E-mail para dentro do JSON de campos dinâmicos!
      if (crmTelefone)
        camposCustomizados['telefone'] = crmTelefone.value.trim();
      if (crmEmail) camposCustomizados['email'] = crmEmail.value.trim();
      const payload = {
        chatId: chatSelecionado.chat_id,
        botId: chatSelecionado.bot_id,
        crmData: {
          nome: crmNome ? crmNome.value.trim() : '',
          cpf: crmCpf ? window.limparEPreencherCPF(crmCpf.value) : '',
          notas: crmNotas ? crmNotas.value.trim() : '',
          campos_customizados: camposCustomizados,
        },
      };
      try {
        const res = await fetch('/api/chat/update-crm', {
          method: 'POST',
          headers: {
            'Content-Type': 'application/json',
            // IMPORTANTE: Adicionei o token, senão dá erro 403/401
            Authorization: `Bearer ${localStorage.getItem('token')}`,
          },
          body: JSON.stringify(payload),
        });
        const data = await res.json();
        if (data.success) {
          mostrarAlerta('Dados salvos com sucesso!', 'success');
          // --- ATUALIZAÇÃO VISUAL INSTANTÂNEA ---
          if (payload.crmData.nome) {
            // 1. Atualiza na memória do navegador
            chatSelecionado.name = payload.crmData.nome;
            chatSelecionado.crm_nome = payload.crmData.nome;
            if (infoNomeLateralBig)
              infoNomeLateralBig.textContent = payload.crmData.nome;
            // 2. Busca o item na lista LATERAL e troca pelo Nome
            const itemLista = document.querySelector(
              `.chat-item[data-chat-id="${chatSelecionado.chat_id}"]`
            );
            if (itemLista) {
              const nomeEl = itemLista.querySelector('.chat-nome-cliente');
              // Atualiza a lista lateral para o NOME
              if (nomeEl) nomeEl.textContent = payload.crmData.nome;
              // Atualiza a bolinha da inicial (Avatar) na lista
              const avatarEl = itemLista.querySelector('.chat-avatar');
              if (avatarEl)
                avatarEl.textContent = payload.crmData.nome
                  .charAt(0)
                  .toUpperCase();
            }
            // 3. (REMOVIDO) Atualiza o Header
            // Antes tinha um código aqui que mudava o topo para o nome.
            // Removemos ele para manter o TELEFONE no topo.
            /* const headerEl = document.getElementById("header-nome-contato");
             if (headerEl) headerEl.textContent = payload.crmData.nome;
             */
          }
          // --------------------------------------------------
        } else {
          mostrarAlerta('Erro ao salvar: ' + data.message, 'error');
        }
      } catch (e) {
        console.error(e);
        mostrarAlerta('Erro de conexão.', 'error');
      } finally {
        btnSalvarCrm.disabled = false;
        btnSalvarCrm.innerHTML = btnHtml;
      }
    });
  }
  const socket = io({
    auth: {
      token: localStorage.getItem('token'),
    },
    transports: ['websocket'],
  });
  window.socket = socket;
  // ============================================================
  // 🚨 ALERTA ANTI-PÂNICO DE CONEXÃO (AUTO-RECONNECT) BLINDADO
  // ============================================================
  const alertaConexao = document.createElement('div');
  alertaConexao.id = 'alerta-conexao-socket';
  Object.assign(alertaConexao.style, {
    position: 'fixed',
    top: '-50px',
    left: '50%',
    transform: 'translateX(-50%)',
    padding: '8px 20px',
    borderRadius: '0 0 10px 10px',
    color: '#fff',
    fontFamily: "'Roboto', sans-serif",
    fontSize: '13px',
    fontWeight: '600',
    zIndex: '999999',
    transition: 'top 0.4s ease, background-color 0.4s ease',
    boxShadow: '0 4px 6px rgba(0,0,0,0.1)',
    display: 'flex',
    alignItems: 'center',
    gap: '8px',
  });
  document.body.appendChild(alertaConexao);
  let tempoMinimoVermelho = false;
  let conexaoVoltouPendente = false;
  let timeoutVermelho = null;
  let timeoutVerde = null;
  socket.on('disconnect', () => {
    clearTimeout(timeoutVermelho);
    clearTimeout(timeoutVerde);
    alertaConexao.innerHTML =
      '<i class="fas fa-wifi" style="animation: pulse 1s infinite;"></i> Conexão perdida. Tentando reconectar...';
    alertaConexao.style.backgroundColor = '#ef4444';
    alertaConexao.style.top = '0px';
    tempoMinimoVermelho = true;
    conexaoVoltouPendente = false;
    timeoutVermelho = setTimeout(() => {
      tempoMinimoVermelho = false;
      if (conexaoVoltouPendente) mostrarAlertaVerde();
    }, 2500);
  });
  socket.on('connect', () => {
    if (alertaConexao.style.top !== '0px' && !tempoMinimoVermelho) return;
    if (tempoMinimoVermelho) {
      conexaoVoltouPendente = true;
    } else {
      mostrarAlertaVerde();
    }
  });

  function mostrarAlertaVerde() {
    clearTimeout(timeoutVerde);
    alertaConexao.innerHTML =
      '<i class="fas fa-check-circle"></i> Conectado ao servidor!';
    alertaConexao.style.backgroundColor = '#10b981';
    conexaoVoltouPendente = false;
    timeoutVerde = setTimeout(() => {
      alertaConexao.style.top = '-50px';
    }, 2500);

    if (typeof carregarChats === 'function') {
      carregarChats(1, false);
    }

    if (typeof chatSelecionado !== 'undefined' && chatSelecionado) {
      const chatNaHora = chatSelecionado;
      fetch(
        `/api/chat/messages?chatId=${chatSelecionado.chat_id}&botId=${chatSelecionado.bot_id}`,
        {
          headers: { Authorization: `Bearer ${localStorage.getItem('token')}` },
        }
      )
        .then((res) => res.json())
        .then((data) => {
          if (chatSelecionado !== chatNaHora) return;
          if (data.success && typeof renderizarMensagens === 'function') {
            // A internet piscou (ou o servidor reiniciou) e a conversa é
            // redesenhada inteira — o que joga pro rodapé. Quem estava lendo
            // lá em cima volta pra MESMA mensagem: guarda a primeira visível
            // e a distância dela até o topo, e repõe depois do desenho.
            const caixa = document.getElementById('container-mensagens');
            const noFim =
              caixa.scrollHeight - caixa.scrollTop - caixa.clientHeight <= 80;
            let ancora = null;
            if (!noFim) {
              const topo = caixa.getBoundingClientRect().top;
              const linha = [...caixa.querySelectorAll('.msg-row[data-msg-id]')].find(
                (l) => l.getBoundingClientRect().bottom > topo
              );
              if (linha)
                ancora = {
                  id: linha.dataset.msgId,
                  desvio: linha.getBoundingClientRect().top - topo,
                };
            }
            renderizarMensagens(data.messages, false);
            if (ancora)
              requestAnimationFrame(() =>
                requestAnimationFrame(() => {
                  const linha = caixa.querySelector(
                    `.msg-row[data-msg-id="${CSS.escape(ancora.id)}"]`
                  );
                  if (!linha) return;
                  // Retângulos vêm na régua visual; scrollTop é local (zoom
                  // da escala global — ver escalaVisualAtual no global.js).
                  caixa.scrollTop +=
                    (linha.getBoundingClientRect().top -
                      caixa.getBoundingClientRect().top -
                      ancora.desvio) /
                    escalaVisualAtual();
                })
              );
          }
        })
        .catch((e) =>
          console.error('Erro na re-sincronização automática do chat:', e)
        );
    }
  }

  // 1. Escuta em tempo real quando alguém reage
  socket.on('nova_reacao', (data) => {
    const badge = document.getElementById(`badge-react-${data.wpp_id}`);
    if (badge) {
      const emojis = Object.values(data.reacoes_atuais).filter((e) => e !== '');
      badge.textContent = emojis.join('');
      if (emojis.length > 0) {
        badge.classList.remove('hidden');
      } else {
        badge.classList.add('hidden');
      }
    }
  });
  // 2. Abre o menu rápido de Emojis
  window.abrirMenuReacoes = function (messageId, event) {
    event.stopPropagation();
    // Se o menu já existe, a gente reaproveita. Se não, cria.
    let menu = document.getElementById('menu-reacoes-rapido');
    if (!menu) {
      menu = document.createElement('div');
      menu.id = 'menu-reacoes-rapido';
      menu.className = 'menu-reacoes-rapido hidden';
      document.body.appendChild(menu);
      // Fecha ao clicar fora
      document.addEventListener('click', (e) => {
        if (!menu.contains(e.target)) menu.classList.add('hidden');
      });
    }
    // Preenche com os emojis mais comuns (pode alterar depois se quiser)
    const emojis = ['👍', '❤️', '😂', '😮', '😢', '🙏', '❌', '✅'];
    menu.innerHTML = '';
    emojis.forEach((emoji) => {
      const btn = document.createElement('div');
      btn.className = 'emoji-btn-react';
      btn.innerText = emoji;
      btn.onclick = () => {
        enviarReacaoParaApi(messageId, emoji);
        menu.classList.add('hidden');
      };
      menu.appendChild(btn);
    });
    // Posiciona o menu no mouse
    menu.style.left = `${event.pageX - 70}px`;
    menu.style.top = `${event.pageY - 40}px`;
    menu.classList.remove('hidden');
  };
  // 3. Dispara a Reação para o Back-end
  window.enviarReacaoParaApi = async function (messageId, emoji) {
    if (!chatSelecionado) return;
    try {
      const res = await fetch('/api/chat/react', {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          Authorization: `Bearer ${localStorage.getItem('token')}`,
        },
        body: JSON.stringify({
          chatId: chatSelecionado.chat_id,
          botId: chatSelecionado.bot_id,
          messageId: messageId,
          emoji: emoji,
        }),
      });
      const data = await res.json();
      if (!data.success) mostrarAlerta('Erro ao reagir.', 'error');
    } catch (e) {
      mostrarAlerta('Erro de conexão ao reagir.', 'error');
    }
  };
  // 4. Remover Reação ao clicar no Emoji
  window.removerReacao = function (messageId, event) {
    if (event) event.stopPropagation(); // Evita que o clique abra fotos/vídeos sem querer
    // Manda um emoji VAZIO para a API, o que força a exclusão no backend e no WhatsApp!
    window.enviarReacaoParaApi(messageId, '');
  };
  // ============================================================
  // 🤖 AGENTE DE IA: badge "IA respondendo" + botão de assumir/devolver
  // ============================================================
  let agenteIaAtivoNoFunilAtual = false;

  async function atualizarEstadoAgenteIA(numeroWpp) {
    if (!btnAssumirConversaIa) return;
    agenteIaAtivoNoFunilAtual = false;
    btnAssumirConversaIa.classList.add('hidden');
    if (badgeIaRespondendo) badgeIaRespondendo.classList.add('hidden');
    try {
      const res = await fetch(`/api/agente-ia/clientes/${numeroWpp}/pausa`, {
        headers: { Authorization: `Bearer ${localStorage.getItem('token')}` },
      });
      const json = await res.json();
      if (!json.success || !json.agente_ativo) return; // Funil sem Agente IA ligado: mantém tudo escondido.
      agenteIaAtivoNoFunilAtual = true;
      renderizarEstadoAgenteIA(numeroWpp, json.ia_pausada);
    } catch (e) {
      // Silencioso: se a checagem falhar, simplesmente não mostra o badge/botão.
    }
  }

  function renderizarEstadoAgenteIA(numeroWpp, iaPausada) {
    if (
      !agenteIaAtivoNoFunilAtual ||
      !chatSelecionado ||
      chatSelecionado.chat_id.replace('@c.us', '') !== numeroWpp
    )
      return;

    btnAssumirConversaIa.classList.remove('hidden');
    if (iaPausada) {
      badgeIaRespondendo.classList.add('hidden');
      btnAssumirConversaIa.classList.remove('ia-esta-ativa');
      btnAssumirConversaIa.title = 'Devolver conversa para a IA';
    } else {
      badgeIaRespondendo.classList.remove('hidden');
      btnAssumirConversaIa.classList.add('ia-esta-ativa');
      btnAssumirConversaIa.title = 'Assumir conversa (pausar a IA)';
    }
  }

  if (btnAssumirConversaIa) {
    btnAssumirConversaIa.addEventListener('click', async () => {
      if (!chatSelecionado) return;
      const numeroWpp = chatSelecionado.chat_id.replace('@c.us', '');
      const estaAtiva =
        btnAssumirConversaIa.classList.contains('ia-esta-ativa');
      try {
        await fetch(`/api/agente-ia/clientes/${numeroWpp}/pausa`, {
          method: 'PUT',
          headers: {
            'Content-Type': 'application/json',
            Authorization: `Bearer ${localStorage.getItem('token')}`,
          },
          body: JSON.stringify({ pausar: estaAtiva }),
        });
        renderizarEstadoAgenteIA(numeroWpp, estaAtiva);
        window.mostrarAlerta &&
          window.mostrarAlerta(
            estaAtiva
              ? 'Você assumiu a conversa.'
              : 'Conversa devolvida para a IA.',
            'success'
          );
      } catch (e) {
        window.mostrarAlerta &&
          window.mostrarAlerta('Erro ao atualizar a IA.', 'error');
      }
    });
  }

  if (typeof socket !== 'undefined') {
    socket.on('agente_ia_pausa_atualizada', ({ numero_wpp, ia_pausada }) => {
      renderizarEstadoAgenteIA(numero_wpp, ia_pausada);
    });
  }

  // ============================================================
  // SOCKET.IO: ATUALIZAÇÃO EM TEMPO REAL (VERSÃO FINAL LIMPA)
  // ============================================================
  // A confirmação de uma mensagem pode chegar antes da própria mensagem (são
  // processos diferentes no servidor). Sem bolha pra pintar, ela se perdia e o
  // relógio ficava preso até reabrir a conversa.
  const acksSemBolha = new Map();
  const ACKS_SEM_BOLHA_MAX = 200;

  socket.on('nova_mensagem', (msg) => {
    const elContainer = document.getElementById('container-mensagens');
    const limpar = (v) =>
      String(v || '')
        .replace('@c.us', '')
        .replace('@g.us', '')
        .trim();
    if (!chatSelecionado) {
      if (typeof carregarChats === 'function') carregarChats(1, false);
      return;
    }
    const chatAberto = limpar(chatSelecionado.chat_id);
    const msgChat = limpar(msg.chat_id);
    const mesmoChat = chatAberto === msgChat;
    const botAberto = limpar(chatSelecionado.bot_id);
    const msgBot = limpar(msg.bot_id);
    const mesmoBot = botAberto === msgBot;
    if (mesmoChat) {
      const deveAtualizar = mesmoBot;
      if (deveAtualizar) {
        let ts = parseInt(msg.timestamp);
        if (ts.toString().length === 10) ts *= 1000;
        chatSelecionado.last_message_at = ts;
        chatSelecionado.last_message_from_me = msg.is_from_me;
        if (typeof window.atualizarVisualSLA === 'function')
          window.atualizarVisualSLA();
        // Estava no fim da conversa? Medido ANTES de desenhar: depois, a
        // própria mensagem nova entra na conta — uma longa fazia quem estava no
        // fim "não estar mais", e a folga larga (350px) puxava quem estava
        // lendo umas mensagens acima.
        const estavaNoFim =
          elContainer.scrollHeight -
            elContainer.scrollTop -
            elContainer.clientHeight <=
          80;
        // Mensagem que saiu DESTA tela (tinha bolha "enviando..."). Só ela
        // leva a pessoa pro fim à força. `is_from_me` sozinho não serve:
        // vale também pra robô, automação, agente de IA, colega e vendedora
        // pelo celular — o cliente mandava, o robô respondia na hora e quem
        // estava lendo lá em cima era arrastado pra baixo.
        let saiuDestaTela = false;
        // O card de lembrete também chega com is_from_me, mas não é resposta a
        // nenhuma bolha temporária: sem esta exceção, criar um lembrete logo
        // depois de mandar uma mensagem apagaria a bolha "enviando..." dela.
        if (msg.is_from_me && msg.type !== 'lembrete') {
          // Apaga apenas a PRIMEIRA bolha temporária que encontrar, respeitando a ordem de envio
          const primeiraTemp = elContainer.querySelector('.msg-temp');
          if (primeiraTemp) {
            primeiraTemp.remove();
            saiuDestaTela = true;
          }
          // Sem status = caminho que nem manda (automação, nota). 0 é relógio.
          if (msg.ack === undefined || msg.ack === null) msg.ack = 1;
          const ackGuardado = acksSemBolha.get(msg.wpp_id);
          if (
            ackGuardado > msg.ack ||
            (ackGuardado === -1 && msg.ack < 2)
          ) {
            msg.ack = ackGuardado;
          }
          acksSemBolha.delete(msg.wpp_id);
        }
        // 2. Desenha
        renderizarMensagens([msg], true);
        // 3. Borrão (Espião)
        if (!msg.is_from_me) {
          const canalAtivo = chatSelecionado.canais.find(
            (c) => limpar(c.bot_id) === limpar(msg.bot_id)
          );
          if (canalAtivo && canalAtivo.has_permission === false) {
            const ultimasMsgs = elContainer.querySelectorAll('.msg-row');
            if (ultimasMsgs.length > 0) {
              ultimasMsgs[ultimasMsgs.length - 1].classList.add('msg-blur');
            }
          }
        }
        // 4. Scroll: desce só se já estava no fim (ou se foi ela que mandou
        // daqui); lendo mais acima, fica onde está e a mensagem vai pro
        // contador do radar.
        if (typeof rolarParaFim === 'function')
          rolarParaFim(estavaNoFim || saiuDestaTela, true);
      } else {
        // Background
        const canalAlvo = chatSelecionado.canais.find(
          (c) => limpar(c.bot_id) === limpar(msg.bot_id)
        );
        if (canalAlvo) {
          if (!msg.is_from_me) canalAlvo.unread = (canalAlvo.unread || 0) + 1;
          renderizarAbas(chatSelecionado.canais);
        } else if (msg.bot_id) {
          // Canal que esta conversa ainda não tinha (o cliente respondeu o
          // RCS com a conversa dele aberta na tela). Antes nada acontecia
          // aqui: a aba só aparecia no próximo redesenho do cabeçalho, e com
          // o id técnico no lugar do nome. O não-lido já nasce contado.
          chatSelecionado.canais.push(canalNovoDaMensagem(msg));
          renderizarAbas(chatSelecionado.canais);
        }
      }
    }
    // ATUALIZAÇÃO EM MEMÓRIA (Sem derrubar o servidor!)
    const chatIndex = todosChats.findIndex(
      (c) => limpar(c.chat_id) === msgChat
    );
    if (chatIndex !== -1) {
      // Puxa o chat afetado
      const chatAfetado = todosChats[chatIndex];
      // 👇 A MÁGICA AQUI: Trava de segurança!
      // Só atualiza a barra lateral se NÃO for uma notificação fantasma do
      // sistema. 'lembrete' entra na mesma trava: o backend também não mexe na
      // prévia da conversa quando cria o card (mexer jogaria o cliente pro
      // filtro "Enviada" sem nenhuma mensagem ter saído), então atualizar aqui
      // deixaria a tela discordando do banco até o próximo F5.
      if (
        msg.type !== 'notification_template' &&
        msg.type !== 'sistema' &&
        msg.type !== 'lembrete'
      ) {
        // Atualiza os dados da prévia na barra lateral
        chatAfetado.last_message_body =
          msg.type === 'image'
            ? 'Imagem'
            : msg.type === 'audio' || msg.type === 'ptt'
              ? 'Áudio'
              : msg.type === 'video'
                ? 'Vídeo'
                : msg.type === 'document'
                  ? 'Arquivo'
                  : msg.type === 'sticker'
                    ? 'Figurinha'
                    : msg.body || 'Mensagem';
        chatAfetado.last_message_type = msg.type;
        chatAfetado.last_message_from_me = msg.is_from_me;
        chatAfetado.last_sent_by_name = msg.sent_by_name || 'Sistema';
        // Prévia vermelha (ver descreverCardChat): esta passa a ser a última
        // mensagem. A recusa pode ter chegado antes dela (acksSemBolha) ou
        // vir junto (msg.ack, já acertado lá em cima na conversa aberta).
        // Nota e ligação não são "a última" pro backend: não mexem aqui.
        if (msg.type !== 'comentario' && msg.type !== 'ligacao') {
          chatAfetado.ultima_wpp_id = msg.wpp_id || null;
          chatAfetado.ultima_falhou = !!(
            msg.is_from_me &&
            (msg.ack === -1 || acksSemBolha.get(msg.wpp_id) === -1)
          );
        }
        if (msg.bot_id && chatAfetado.canais) {
          const canalExiste = chatAfetado.canais.find(
            (c) => c.bot_id === msg.bot_id
          );
          // (Se a conversa está aberta, o bloco lá de cima já pôs o canal —
          // quase sempre é o mesmo array, e aqui ele já existe.)
          if (!canalExiste) chatAfetado.canais.push(canalNovoDaMensagem(msg));
        }
        let ts = parseInt(msg.timestamp);
        if (ts.toString().length === 10) ts *= 1000;
        chatAfetado.last_message_at = new Date(ts).toISOString();
        todosChats.splice(chatIndex, 1);
        // "Recebida"/"Enviada" julgam pela ÚLTIMA mensagem: quando ela
        // responde (ou o cliente escreve de volta), o cliente troca de lado e
        // precisa sair da lista filtrada na hora. Antes ele continuava ali até
        // alguém dar F5 — o mesmo defeito que a gente acabou de tirar da
        // consulta do banco, só que na tela.
        // Com bot(s) escolhido(s) no topo, mensagem de bot fora da seleção não
        // mexe no filtro, porque quem manda ali é a última conversa dos bots
        // filtrados.
        const filtroDirecao = filtrosAtivos.status;
        const contaProFiltro = botPassaNoFiltro(msgBot, limpar);
        const trocouDeLado =
          contaProFiltro &&
          ((filtroDirecao === 'recebidas' && msg.is_from_me) ||
            (filtroDirecao === 'enviadas' && !msg.is_from_me));
        // Continua no filtro: volta pro topo, como sempre foi
        if (!trocouDeLado) todosChats.unshift(chatAfetado);
        renderizarLista(todosChats);
      }
    } else {
      // Se a mensagem for de um cliente novo que não tá na tela, puxamos da API.
      // MAS, só puxamos se for uma mensagem de verdade (pra não fazer o banco trabalhar à toa por causa de notificação)
      if (
        msg.type !== 'notification_template' &&
        typeof carregarChats === 'function'
      ) {
        carregarChats(1, false);
      }
    }
  });
  // Card de lembrete mudou de estado (venceu, foi adiado, foi concluído) —
  // repinta só aquele card, sem recarregar a conversa.
  socket.on('lembrete_atualizado', (dados) => {
    if (!dados || !dados.lembrete) return;
    repintarCardLembrete(dados.lembrete);
  });

  // ============================================================
  // SOCKET: ATUALIZAR APENAS A BARRA LATERAL (USADO EM CAMPANHAS)
  // ============================================================
  socket.on('atualizar_chat_lateral', (data) => {
    const limpar = (v) =>
      String(v || '')
        .replace('@c.us', '')
        .replace('@g.us', '')
        .trim();
    const chatIndex = todosChats.findIndex(
      (c) => limpar(c.chat_id) === limpar(data.chat_id)
    );
    if (chatIndex !== -1) {
      const chatAfetado = todosChats[chatIndex];
      chatAfetado.last_message_body = data.last_message_body;
      chatAfetado.last_message_type = data.last_message_type;
      chatAfetado.last_message_from_me = data.last_message_from_me;
      chatAfetado.last_sent_by_name = data.last_sent_by_name;
      chatAfetado.last_message_at = new Date().toISOString();
      // A campanha não manda o wpp_id: a prévia nova começa sem alerta.
      chatAfetado.ultima_wpp_id = null;
      chatAfetado.ultima_falhou = false;
      // Se eu mandei campanha automática e ele não estava lido, eu zero o contador
      if (data.last_message_from_me) chatAfetado.unread_count = 0;
      todosChats.splice(chatIndex, 1);
      // Mesma regra do 'nova_mensagem': a campanha que sai por aqui também é
      // "última mensagem nossa", então tira o cliente do filtro de Recebida.
      const trocouDeLado =
        (filtrosAtivos.status === 'recebidas' && data.last_message_from_me) ||
        (filtrosAtivos.status === 'enviadas' && !data.last_message_from_me);
      if (!trocouDeLado) todosChats.unshift(chatAfetado);
      renderizarLista(todosChats);
    }
  });
  // ============================================================
  // 📸 SOCKET: ATUALIZA FOTO DE PERFIL DO CLIENTE EM TEMPO REAL
  // ============================================================
  socket.on('atualizar_foto_perfil', (data) => {
    // 1. Atualiza na memória
    const chatMemoria = todosChats.find((c) => c.chat_id === data.chat_id);
    if (chatMemoria) {
      chatMemoria.profile_pic_url = data.profile_pic_url;
    }
    const nomeClientePerfil = textoParaOnclick(
      nomeOuTelefoneCliente(chatMemoria) ||
      nomeOuTelefoneCliente({ chat_id: data.chat_id })
    );
    // 2. Se for vendedora (ou admin vendo sem dono), atualiza a bolinha na lista lateral
    if (
      !veEquipe ||
      (chatMemoria &&
        (!chatMemoria.agentes || chatMemoria.agentes.length === 0))
    ) {
      const itemLista = document.querySelector(
        `.chat-item[data-chat-id="${data.chat_id}"]`
      );
      if (itemLista) {
        const avatarEl = itemLista.querySelector('.chat-avatar');
        if (avatarEl && data.profile_pic_url !== 'sem_foto') {
          // 👇 Clicável aqui também! 👇
          trocarMioloDoAvatar(
            avatarEl,
            `<img src="${escaparHtmlChat(data.profile_pic_url)}" loading="lazy" style="width: 100%; height: 100%; object-fit: cover; border-radius: 50%; cursor: pointer;" onclick="window.abrirVisualizador('${textoParaOnclick(data.profile_pic_url)}', 'avatar', '', '', '', '', '${nomeClientePerfil}'); event.stopPropagation();">`
          );
        }
      }
    }
    // 3. Atualiza o avatar gigante se a conversa deste cliente estiver aberta agora
    if (chatSelecionado && chatSelecionado.chat_id === data.chat_id) {
      if (infoAvatarBig && data.profile_pic_url !== 'sem_foto') {
        // 👇 Clicável aqui também! 👇
        infoAvatarBig.innerHTML = `<img src="${escaparHtmlChat(data.profile_pic_url)}" style="width: 100%; height: 100%; border-radius: 50%; object-fit: cover; cursor: pointer;" onclick="window.abrirVisualizador('${textoParaOnclick(data.profile_pic_url)}', 'avatar', '', '', '', '', '${nomeClientePerfil}'); event.stopPropagation();">`;
      }
    }
  });
  // ============================================================
  // SOCKET.IO: ATUALIZAÇÃO DE NOME/DADOS/TAGS EM TEMPO REAL
  // ============================================================
  socket.on('cliente_atualizado', async (data) => {
    // data = { chatId, newName, tags }
    let nomeAtualizado = data.newName;
    // 'cliente_atualizado' é um io.emit GLOBAL: chega na tela de TODAS as
    // vendedoras, inclusive nas que não atendem esse cliente. A busca abaixo
    // só pode rodar se o chat for meu (está na minha lista ou aberto aqui) —
    // senão ela bate no cliente de outra pessoa, volta 403, e o interceptador
    // global entendia isso como sessão expirada e deslogava todo mundo junto.
    const ehMeuChat =
      (Array.isArray(todosChats) &&
        todosChats.some((c) => c.chat_id === data.chatId)) ||
      (typeof chatSelecionado !== 'undefined' &&
        chatSelecionado &&
        chatSelecionado.chat_id === data.chatId);

    // 🕵️ MÁGICA: Se a IA (ou uma API) atualizou o banco mas não mandou o nome no socket, vamos buscar no banco rapidinho!
    if (!nomeAtualizado && ehMeuChat) {
      try {
        const numeroPuro = data.chatId.replace(/\D/g, '');
        const res = await fetch(`/api/crm/${numeroPuro}`, {
          headers: { Authorization: `Bearer ${localStorage.getItem('token')}` },
        });
        const json = await res.json();
        if (json.success && json.data && json.data.nome) {
          nomeAtualizado = json.data.nome;
        }
      } catch (e) {
        console.error(
          'Erro ao buscar dados do cliente atualizado via Socket',
          e
        );
      }
    }
    // 1. Atualiza a memória local da lista lateral
    const chatNaMemoria = todosChats.find((c) => c.chat_id === data.chatId);
    if (chatNaMemoria && nomeAtualizado) {
      chatNaMemoria.name = nomeAtualizado;
      chatNaMemoria.crm_nome = nomeAtualizado;
    }
    // 2. Atualiza a Lista Lateral (Troca o número pelo Nome novo instantaneamente)
    const itemLista = document.querySelector(
      `.chat-item[data-chat-id="${data.chatId}"]`
    );
    if (itemLista && nomeAtualizado) {
      const nomeEl = itemLista.querySelector('.chat-nome-cliente');
      if (nomeEl) nomeEl.textContent = nomeAtualizado;
      // Troca a bolinha da lista lateral pela letra inicial do nome novo
      const avatarEl = itemLista.querySelector('.chat-avatar');
      if (avatarEl) {
        trocarMioloDoAvatar(
          avatarEl,
          document.createTextNode(nomeAtualizado.charAt(0).toUpperCase())
        );
      }
    }
    // 3. Se o cliente que a IA atualizou estiver ABERTO na minha tela agora mesmo
    if (chatSelecionado && chatSelecionado.chat_id === data.chatId) {
      // Atualiza o Cabeçalho (Header) e o Avatar Gigante da direita
      if (nomeAtualizado) {
        const headerNome = document.getElementById('header-nome-contato');
        const infoAvatarBig = document.getElementById('info-avatar-big');
        const crmNome = document.getElementById('crm-nome');
        if (headerNome) headerNome.textContent = nomeAtualizado;
        if (infoAvatarBig)
          infoAvatarBig.textContent = nomeAtualizado.charAt(0).toUpperCase();
        if (infoNomeLateralBig) infoNomeLateralBig.textContent = nomeAtualizado;
        // Atualiza o Input de Nome do CRM (só se o usuário não estiver digitando nele)
        if (crmNome && document.activeElement !== crmNome) {
          crmNome.value = nomeAtualizado;
        }
      }
      // De qualquer forma, manda recarregar a barra lateral de informações do CRM (Isso puxa o CPF e outros campos novos)
      carregarTagsDoCliente(chatSelecionado);
      carregarDadosDoCliente(chatSelecionado.chat_id);
    }
  });
  // ============================================================
  // 🔄 RECARREGA O FUNIL (KANBAN) EM TEMPO REAL SEM F5
  // ============================================================
  socket.on('kanban_atualizado', () => {
    // Se o usuário estiver com o painel de um cliente aberto na tela agora
    if (typeof chatSelecionado !== 'undefined' && chatSelecionado) {
      // Chama a função que já existe e que vai no banco buscar a nova etapa
      if (typeof window.atualizarVisualizacaoFunil === 'function') {
        window.atualizarVisualizacaoFunil(chatSelecionado);
      }
    }
  });
  // 👇 NOVO: ESCUTA A MUDANÇA DE STATUS PARA ESCONDER O CLIENTE 👇
  socket.on('status_lead_atualizado', (data) => {
    // data = { numero_wpp, novoStatus }
    const chatIdAlvo = data.numero_wpp + '@c.us';
    // 1. Atualiza a memória (se ele estiver na lista do lado)
    const chatNaMemoria = todosChats.find((c) => c.chat_id === chatIdAlvo);
    if (chatNaMemoria) {
      chatNaMemoria.status_lead = data.novoStatus;
    }
    // Alguém reabriu o lead em outra tela: o backend já apagou o motivo, então
    // o selo tem que sumir daqui também em vez de acusar uma perda desfeita.
    // (Só o desfazer chega por aqui — quando a perda acontece nesta tela, o
    // selo é atualizado no próprio fluxo, com o motivo em mãos.)
    if (
      data.novoStatus !== 'perdido' &&
      typeof chatSelecionado !== 'undefined' &&
      chatSelecionado &&
      chatSelecionado.chat_id === chatIdAlvo
    ) {
      chatSelecionado.motivo_perdido = null;
      atualizarSeloMotivoPerdido(null);
    }
    // 2. A MÁGICA: Se for VENDEDOR e o cliente foi pra GANHO/PERDIDO, arranca da tela!
    // A lista só mostra lead 'aberto' (vale pra admin também), então ganho/
    // perdido sai da lista na hora, sem esperar o F5.
    if (data.novoStatus === 'ganho' || data.novoStatus === 'perdido') {
      // Tira o item da lista da esquerda
      const chatItem = document.querySelector(
        `.chat-item[data-chat-id="${chatIdAlvo}"]`
      );
      if (chatItem) chatItem.remove();
      // Apaga da memória
      todosChats = todosChats.filter((c) => c.chat_id !== chatIdAlvo);
      // Vendedora com essa conversa aberta: fecha. Admin mantém aberta (só
      // sai da lista), pra não fechar na cara de quem está lendo/finalizando.
      if (
        !veEquipe &&
        chatSelecionado &&
        chatSelecionado.chat_id === chatIdAlvo
      ) {
        if (typeof fecharConversaAtual === 'function') fecharConversaAtual();
      }
    }
    // 3. Atualiza aquela nossa barra de luzes do rodapé (Se o Admin estiver com ela aberta)
    if (chatSelecionado && chatSelecionado.chat_id === chatIdAlvo) {
      document.querySelectorAll('.segmento-status').forEach((seg) => {
        seg.classList.remove('ativo');
        if (seg.dataset.status === data.novoStatus) {
          seg.classList.add('ativo');
        }
      });
    }
  });
  // 👆 FIM DA ESCUTA DO STATUS 👆
  socket.on('tags_cliente_atualizadas', (data) => {
    // data = { chatId, tags }
    // 1. Atualiza a memória global da lista lateral
    const chatNaMemoria = todosChats.find((c) => c.chat_id === data.chatId);
    if (chatNaMemoria) {
      chatNaMemoria.tags = data.tags;
    }
    // 2. Chama a sua função nativa para pintar a bolinha na lista lateral
    if (typeof atualizarVisualTagsLateral === 'function') {
      atualizarVisualTagsLateral(data.chatId, data.tags);
    }
    // 3. Se o cliente for EXATAMENTE o que a vendedora está atendendo agora, atualiza os chips do CRM
    if (chatSelecionado && chatSelecionado.chat_id === data.chatId) {
      tagsDoClienteAtual = data.tags;
      if (typeof renderizarTagsCrm === 'function') {
        renderizarTagsCrm();
      }
    }
  });
  // ============================================================
  // ATUALIZAÇÃO DE STATUS (MENSAGENS ACK E BOTS ONLINE/OFFLINE)
  // ============================================================
  socket.on('status_update', (data) => {
    // 1. Se for um Array, é a lista de Bots atualizando (vindo da tela de Instâncias)
    if (Array.isArray(data)) {
      data.forEach((bot) => {
        todosChats.forEach((chat) => {
          if (chat.canais) {
            const canal = chat.canais.find((c) => c.bot_id === bot.id);
            if (canal) {
              canal.status =
                bot.status === 'ready' ||
                bot.status === 'authenticated' ||
                bot.online
                  ? 'online'
                  : 'offline';
            }
          }
        });
      });
      // Atualiza a tela imediatamente se o chat afetado estiver aberto
      if (chatSelecionado) {
        renderizarAbas(chatSelecionado.canais);
        trocarCanal(chatSelecionado.bot_id, false, true);
      }
      return;
    }
    // 2. Se for um Objeto com wppId, é o ACK da mensagem (Checks do WhatsApp)
    if (data && data.wppId) {
      const statusSpan = document.querySelector(
        `.msg-status[data-wpp-id='${data.wppId}']`
      );
      if (statusSpan) {
        statusSpan.innerHTML = getStatusIcon(
          data.ack,
          data.botId || data.bot_id || (chatSelecionado && chatSelecionado.bot_id)
        );
        // Balão cinza enquanto está em falha; volta ao normal no primeiro ACK
        const bolha = statusSpan.closest('.msg-bubble');
        if (bolha && !bolha.classList.contains('revoked')) {
          bolha.classList.toggle('msg-falha', data.ack === -1);
        }
      } else {
        acksSemBolha.set(data.wppId, data.ack);
        if (acksSemBolha.size > ACKS_SEM_BOLHA_MAX) {
          acksSemBolha.delete(acksSemBolha.keys().next().value);
        }
      }
      // Prévia vermelha na lista: se a mensagem que mudou de status é a que
      // está na prévia, liga (recusa) ou desliga (entregue depois) o alerta.
      if (data.chatId) {
        const semSufixo = (v) => String(v || '').replace('@c.us', '').trim();
        const chatDaLista = todosChats.find(
          (c) => semSufixo(c.chat_id) === semSufixo(data.chatId)
        );
        if (chatDaLista && chatDaLista.ultima_wpp_id === data.wppId) {
          const falhou = data.ack === -1;
          if (chatDaLista.ultima_falhou !== falhou) {
            chatDaLista.ultima_falhou = falhou;
            redesenharCardDaLista(chatDaLista);
          }
        }
      }
    }
  });
  // Escuta também a atualização individual de um bot (Blindagem Total)
  socket.on('bot_status', (bot) => {
    todosChats.forEach((chat) => {
      if (chat.canais) {
        const canal = chat.canais.find((c) => c.bot_id === bot.id);
        if (canal) {
          canal.status =
            bot.status === 'ready' ||
            bot.status === 'authenticated' ||
            bot.online
              ? 'online'
              : 'offline';
        }
      }
    });
    if (chatSelecionado) {
      renderizarAbas(chatSelecionado.canais);
      trocarCanal(chatSelecionado.bot_id, false, true);
    }
  });

  socket.on('mensagem_apagada', (data) => {
    const limpar = (v) =>
      String(v || '')
        .replace('@c.us', '')
        .replace('@g.us', '')
        .trim();

    // 1. Atualiza visualmente o balão se o chat estiver aberto
    if (
      chatSelecionado &&
      limpar(chatSelecionado.chat_id) === limpar(data.chat_id) &&
      limpar(chatSelecionado.bot_id) === limpar(data.bot_id)
    ) {
      const statusSpan = document.querySelector(
        `.msg-status[data-wpp-id="${data.wpp_id}"]`
      );
      const bolha = statusSpan ? statusSpan.closest('.msg-bubble') : null;
      const wrapper = bolha ? bolha.closest('.msg-wrapper-vertical') : null;
      if (bolha && wrapper) {
        wrapper
          .querySelectorAll(
            '.btn-reply-hover, .btn-delete-hover, .btn-edit-hover, .btn-react-hover'
          )
          .forEach((b) => b.remove());
        const header = bolha.querySelector('.msg-header-outside');
        bolha.innerHTML = '';
        bolha.className = 'msg-bubble revoked';
        if (header) bolha.appendChild(header);
        const spanTexto = document.createElement('span');
        spanTexto.className = 'texto-formatado';
        spanTexto.innerHTML = `<i class="bx bx-block" style="margin-right:6px;"></i>${data.novo_texto}`;
        bolha.appendChild(spanTexto);
        const transcricao = wrapper.querySelector(
          '.transcription-container-outside'
        );
        if (transcricao) transcricao.remove();
      }
    }

    // 2. MÁGICA CORRIGIDA: Atualiza a barra lateral com a ÚLTIMA mensagem real e ordena corretamente
    const chatIndex = todosChats.findIndex(
      (c) => limpar(c.chat_id) === limpar(data.chat_id)
    );
    if (chatIndex !== -1) {
      const chatAfetado = todosChats[chatIndex];

      // Pega os dados que o servidor calculou como sendo a verdadeira última mensagem
      if (data.lateral_texto) {
        chatAfetado.last_message_type = data.lateral_tipo;
        chatAfetado.last_message_body = data.lateral_texto;
        chatAfetado.last_message_from_me = data.lateral_from_me;
        chatAfetado.last_sent_by_name = data.lateral_sender;
        chatAfetado.last_message_at = new Date(data.lateral_data).toISOString();
      } else {
        // Fallback de segurança
        chatAfetado.last_message_type = 'revoked';
        chatAfetado.last_message_body = 'Mensagem apagada';
      }
      // A prévia virou outra mensagem: o alerta de recusa volta no próximo
      // carregamento da lista, se for o caso.
      chatAfetado.ultima_wpp_id = null;
      chatAfetado.ultima_falhou = false;

      // Remove o chat da posição atual
      todosChats.splice(chatIndex, 1);
      todosChats.push(chatAfetado);

      // Reordena a lista baseada na data real (respeitando o filtro de ordem escolhido na tela)
      if (filtrosAtivos.ordem === 'asc') {
        todosChats.sort(
          (a, b) => new Date(a.last_message_at) - new Date(b.last_message_at)
        );
      } else {
        todosChats.sort(
          (a, b) => new Date(b.last_message_at) - new Date(a.last_message_at)
        );
      }

      renderizarLista(todosChats);
    }
  });

  // ============================================================
  // SOCKET: MÍDIA QUE CHEGOU SEM ARQUIVO FOI RECUPERADA (OU DESISTIU)
  // ============================================================
  // Recarrega a conversa inteira em vez de remendar a bolha: o desenho de
  // cada tipo de mídia mora dentro do renderizarMensagens e duplicá-lo aqui
  // divergiria no primeiro ajuste. Acontece poucas vezes por dia.
  socket.on('midia_atualizada', (data) => {
    if (!chatSelecionado || !data || !data.wppId) return;
    const bolha = document.querySelector(
      `.midia-indisponivel[data-midia-wpp-id="${CSS.escape(data.wppId)}"]`
    );
    if (!bolha) return;
    const numeroAberto = String(chatSelecionado.chat_id || '').replace(/\D/g, '');
    if (numeroAberto !== String(data.numeroWpp)) return;
    if (chatSelecionado.bot_id !== data.instanciaId) return;

    const chatNaHora = chatSelecionado;
    const distanciaDoFim = containerMsgs.scrollHeight - containerMsgs.scrollTop;
    fetch(
      `/api/chat/messages?chatId=${chatNaHora.chat_id}&botId=${chatNaHora.bot_id}`,
      { headers: { Authorization: `Bearer ${localStorage.getItem('token')}` } }
    )
      .then((res) => res.json())
      .then((resp) => {
        // Trocou de conversa enquanto buscava: não desenha a antiga por cima.
        if (!resp.success || chatSelecionado !== chatNaHora) return;
        renderizarMensagens(resp.messages, false);
        // renderizarMensagens joga pro rodapé; volta pra onde a pessoa estava.
        requestAnimationFrame(() =>
          requestAnimationFrame(() => {
            containerMsgs.scrollTop = containerMsgs.scrollHeight - distanciaDoFim;
          })
        );
      })
      .catch((e) => console.error('Erro ao atualizar mídia recuperada:', e));
  });

  // ============================================================
  // 🖼️ SOCKET: ATUALIZA A MINIATURA DO ARQUIVO AO VIVO
  // ============================================================
  socket.on('atualizar_miniatura', (data) => {
    // 1. Procura um elemento que exista em TODAS as mensagens (O badge de reação)
    const elementoReferencia = document.getElementById(
      `badge-react-${data.wppId}`
    );
    if (!elementoReferencia) return;
    // 2. Acha a bolha da mensagem e o elemento do arquivo atual
    const bubble = elementoReferencia.closest('.msg-bubble');
    if (!bubble) return;
    // 🎯 INJEÇÃO CRÍTICA: Avisa pra bolha que ela virou uma mídia para colar a borda!
    bubble.classList.add('has-media');
    const fileCardAnterior = bubble.querySelector('.nexus-file-card');
    const wrapperExistente = bubble.querySelector('.nexus-file-card-wrapper');
    if (!fileCardAnterior && !wrapperExistente) return;
    const elementoBase = wrapperExistente || fileCardAnterior;
    let mediaPath = '';
    let fileName = 'Anexo';
    let fileExt = 'PDF';
    const elCardDados = wrapperExistente
      ? wrapperExistente.querySelector('.nexus-file-card')
      : fileCardAnterior;
    if (elCardDados) {
      const onclickText = elCardDados.getAttribute('onclick') || '';
      // Os dois textos vêm escapados pra JS (textoParaOnclick): aceita \' no
      // meio e desfaz o escape.
      const paramsMatch = onclickText.match(
        /forceDownload\('((?:\\.|[^'\\])+)',\s*'((?:\\.|[^'\\])+)'\)/
      );
      if (paramsMatch) {
        mediaPath = paramsMatch[1].replace(/\\(.)/g, '$1');
        fileName = paramsMatch[2].replace(/\\(.)/g, '$1');
        fileExt = fileName.split('.').pop().toUpperCase();
      }
      const iconBox = elCardDados.querySelector('.file-icon-box');
      const iconColor = iconBox ? iconBox.style.backgroundColor : '#e74c3c';
      // 4. Constrói o HTML NOVO COMPLETO (Design Exclusivo Limpo)
      const novoWrapper = document.createElement('div');
      novoWrapper.className = 'media-wrapper exclusive-preview';
      novoWrapper.style.cursor = 'pointer';
      novoWrapper.style.position = 'relative';
      let lastDotSocket = fileName.lastIndexOf('.');
      let fileNameVisual =
        lastDotSocket !== -1 ? fileName.substring(0, lastDotSocket) : fileName;

      // 👇 A Mágica de Capturar a Hora da Bolha
      const metaSpan = bubble.querySelector('.msg-meta');
      const horaVisual = metaSpan ? metaSpan.textContent.trim() : '';
      const dataDeHoje = new Date().toLocaleDateString('pt-BR');
      const stringDataFinal = horaVisual ? `${dataDeHoje} - ${horaVisual}` : '';

      const tipoVisualizacaoSocket = fileExt === 'PDF' ? 'pdf' : 'imagem';

      // 👇 NOVA LÓGICA DO NOME NO SOCKET 👇
      let remetentePreview = 'Equipe';
      const isEnviada = bubble.closest('.msg-row.enviada');
      if (isEnviada) {
        const senderLabel = bubble.querySelector('.msg-sender-label');
        if (senderLabel)
          remetentePreview = senderLabel.textContent
            .replace(/<[^>]*>?/gm, '')
            .trim();
      } else {
        let nomeCrm = chatSelecionado.crm_nome || chatSelecionado.name || '';
        let telefone = chatSelecionado.chat_id.replace(/\D/g, '');
        if (!nomeCrm || nomeCrm === telefone || nomeCrm.includes('@c.us')) {
          remetentePreview = formatarTelefoneBR(telefone);
        } else {
          const partes = nomeCrm.trim().split(' ');
          remetentePreview =
            partes.length > 1 ? `${partes[0]} ${partes[1]}` : partes[0];
        }
      }
      // 1. Injeta o onclick na DIV inteira com o remetente no final!
      // (setAttribute não passa pelo HTML: só o escape de JS)
      novoWrapper.setAttribute(
        'onclick',
        `window.abrirVisualizador('${textoParaJs(mediaPath)}', '${tipoVisualizacaoSocket}', '${textoParaJs(fileNameVisual)}', '${textoParaJs(stringDataFinal)}', '${textoParaJs(iconColor)}', '${textoParaJs(fileExt)}', '${textoParaJs(remetentePreview)}')`
      );

      // 2. Removemos o onclick da <img> para não dar conflito
      novoWrapper.innerHTML = `
            <img src="${escaparHtmlChat(data.thumbnailUrl)}" alt="Preview Documento" class="file-preview-img" onerror="this.parentElement.style.display='none'">
            <div class="exclusive-top-overlay">
                <span class="exclusive-file-name" title="${escaparHtmlChat(fileNameVisual)}">${escaparHtmlChat(fileNameVisual)}</span>
            </div>
            <div class="exclusive-bottom-overlay">
                <span class="exclusive-file-badge" style="background-color: ${escaparHtmlChat(iconColor)};">${escaparHtmlChat(fileExt)}</span>
                <a href="#" onclick="window.forceDownload('${textoParaOnclick(mediaPath)}', '${textoParaOnclick(fileName)}'); event.stopPropagation(); event.preventDefault();" class="exclusive-download-btn" title="Baixar">
                    <i class="fas fa-arrow-down"></i>
                </a>
            </div>
        `;

      // 5. Substitui o antigo pelo novo!
      elementoBase.replaceWith(novoWrapper);
    }
  });
  // ============================================================
  // SOCKET: RECARREGAMENTO FORÇADO (QUANDO UM BOT É UNIFICADO)
  // ============================================================
  socket.on('forcar_reload_chats', () => {
    if (typeof carregarChats === 'function') {
      // Recarrega a lista lateral puxando o banco de dados novo limpo
      carregarChats(1, true);
      // Se a vendedora estiver com o chat fantasma aberto na cara dela, fecha para evitar bugar o envio!
      if (chatSelecionado) {
        if (typeof fecharConversaAtual === 'function') fecharConversaAtual();
      }
      mostrarAlerta('Conexão de robô sincronizada com sucesso!', 'info');
    }
  });

  // ============================================================
  // SOCKET: F5 FORÇADO PELO ADMIN NESSA TELA ESPECÍFICA
  // ============================================================
  socket.on('forcar_reload_pagina', () => {
    window.location.reload();
  });

  window.apagarMensagemParaTodos = async function (wppId) {
    if (!chatSelecionado) return; // Confirmação dupla
    const confirmou = await window.mostrarConfirmacao(
      'Apagar mensagem para todos no WhatsApp? Esta ação não pode ser desfeita.'
    );
    if (!confirmou) return;
    const btnClicado = document.querySelector(
      `.btn-delete-hover[onclick="apagarMensagemParaTodos('${wppId}')"]`
    );
    let iconeOriginal = '';
    if (btnClicado) {
      iconeOriginal = btnClicado.innerHTML;
      btnClicado.innerHTML = '<i class="bx bx-loader-alt bx-spin"></i>';
      btnClicado.disabled = true;
    }
    try {
      const res = await fetch('/api/chat/delete-message', {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          Authorization: `Bearer ${localStorage.getItem('token')}`,
        },
        body: JSON.stringify({
          botId: chatSelecionado.bot_id,
          wppId: wppId,
        }),
      });
      const data = await res.json();
      if (!data.success) {
        mostrarAlerta(data.message, 'error');
        if (btnClicado) {
          btnClicado.innerHTML = iconeOriginal;
          btnClicado.disabled = false;
        }
      }
    } catch (e) {
      console.error('Erro ao apagar:', e);
      mostrarAlerta('Erro de conexão ao tentar apagar a mensagem.', 'error');
      if (btnClicado) {
        btnClicado.innerHTML = iconeOriginal;
        btnClicado.disabled = false;
      }
    }
  }; // 1. Carregar Lista de Contatos (MODO OMNICHANNEL)
  async function carregarChats(pagina = 1, resetarLista = false) {
    if (carregandoChats) return;
    carregandoChats = true;
    // Loading na primeira vez
    if (pagina === 1 && resetarLista && containerLista.children.length === 0) {
      containerLista.innerHTML =
        '<div class="chat-loading"><i class="fas fa-spinner fa-spin"></i> Carregando...</div>';
    }
    try {
      // --- COLETANDO OS FILTROS DA TELA ---
      const filtroBot = paramBotsFiltro();
      const textoBusca = inputBusca ? inputBusca.value.trim() : '';
      const vends = (filtrosAtivos.vendedores || []).join(',');
      const tgs = (filtrosAtivos.tags || []).join(',');
      const stat = filtrosAtivos.status || 'todos';
      const ord = filtrosAtivos.ordem || 'desc';
      // Passa a bola pro Backend processar! (Agora enviando os dados de período)
      const per = filtrosAtivos.periodo || 'todos';
      const dIni = filtrosAtivos.dataInicio || '';
      const dFim = filtrosAtivos.dataFim || '';
      const etps = (filtrosAtivos.etapas || []).join(',');
      const url = `/api/chat/contacts?page=${pagina}&busca=${encodeURIComponent(textoBusca)}&botId=${encodeURIComponent(filtroBot)}&status=${stat}&vendedores=${vends}&tags=${tgs}&etapas=${etps}&ordem=${ord}&periodo=${per}&dataInicio=${dIni}&dataFim=${dFim}`;
      const res = await fetch(url, {
        headers: { Authorization: `Bearer ${localStorage.getItem('token')}` },
      });
      const data = await res.json();
      if (data.success) {
        // Se for reset forçado, limpa tudo
        if (resetarLista) {
          paginaAtual = 1;
          todosChats = [];
          containerLista.innerHTML = '';
        }
        // --- LÓGICA ATUALIZADA: CLIENTE ÚNICO ---
        data.chats.forEach((novoChat) => {
          // Agora procuramos APENAS pelo chat_id (Telefone), pois os bots estão agrupados dentro dele
          const index = todosChats.findIndex(
            (c) => c.chat_id === novoChat.chat_id
          );
          if (index !== -1) {
            // Atualiza cliente existente
            todosChats[index] = novoChat;
          } else {
            // Adiciona novo cliente
            todosChats.push(novoChat);
          }
        });
        // Garante a ordem respeitando o filtro selecionado na tela
        if (filtrosAtivos.ordem === 'asc') {
          // Mais Antigas primeiro
          todosChats.sort(
            (a, b) => new Date(a.last_message_at) - new Date(b.last_message_at)
          );
        } else {
          // Mais Recentes primeiro (Padrão)
          todosChats.sort(
            (a, b) => new Date(b.last_message_at) - new Date(a.last_message_at)
          );
        }
        temMaisChats = data.temMais;
        // Renderiza
        renderizarLista(todosChats, resetarLista);
        if (resetarLista || Date.now() - ultimaAtualizacaoBots > 30000) {
          ultimaAtualizacaoBots = Date.now();
          carregarFiltroBotsComStatus();
        }
        if (pagina === 1 && resetarLista) {
          const params = new URLSearchParams(window.location.search);
          const paramChatId = params.get('chatId');
          const paramBotId = params.get('botId'); // Os relatórios também mandam o botId na URL
          if (paramChatId) {
            let chatPraAbrir = todosChats.find(
              (c) => c.chat_id === paramChatId
            );
            if (chatPraAbrir) {
              // CENÁRIO 1: O cliente estava entre os 1000 mais recentes (Abordagem Normal)
              if (paramBotId) chatPraAbrir.bot_id = paramBotId; // Força abrir a aba do bot que o Relatório pediu
              abrirConversa(chatPraAbrir);
              window.history.replaceState(
                {},
                document.title,
                window.location.pathname
              );
            } else {
              // CENÁRIO 2: O cliente é muito antigo e não carregou na tela inicial (Resgate)
              (async () => {
                try {
                  const numPuro = paramChatId.replace(/\D/g, '');
                  const resSingle = await fetch(`/api/chat/single/${numPuro}`, {
                    headers: {
                      Authorization: `Bearer ${localStorage.getItem('token')}`,
                    },
                  });
                  const jsonSingle = await resSingle.json();
                  if (jsonSingle.success && jsonSingle.chat) {
                    // O servidor devolve a conversa na forma do CADASTRO (com
                    // ou sem o nono dígito). Se ela já está na lista com essa
                    // forma, usa a que existe — senão o mesmo cliente
                    // aparecia duas vezes na lateral.
                    const jaNaLista = todosChats.find(
                      (c) => c.chat_id === jsonSingle.chat.chat_id
                    );
                    chatPraAbrir = jaNaLista || jsonSingle.chat;
                    if (paramBotId) chatPraAbrir.bot_id = paramBotId;
                    // Injeta o cliente fantasma no TOPO da sua lista lateral
                    if (!jaNaLista) todosChats.unshift(chatPraAbrir);
                    renderizarLista(todosChats);
                    // Abre a conversa
                    abrirConversa(chatPraAbrir);
                    window.history.replaceState(
                      {},
                      document.title,
                      window.location.pathname
                    );
                  } else {
                    mostrarAlerta(
                      'Conversa antiga não encontrada ou acesso restrito.',
                      'warning'
                    );
                  }
                } catch (errResgate) {
                  console.error('Erro ao resgatar chat antigo:', errResgate);
                }
              })();
            }
          }
        }
      }
    } catch (erro) {
      console.error('Erro ao carregar chats:', erro);
    } finally {
      carregandoChats = false;
      const loadingDiv = document.getElementById('loading-scroll');
      if (loadingDiv) loadingDiv.remove();
    }
  }
  function renderizarLista(chats, limpar = false) {
    // 1. 🚀 SALVA A POSIÇÃO DO SCROLL (Para a tela não pular)
    const scrollAtual = containerLista.scrollTop;
    // 2. A tela NÃO é mais apagada aqui: reconciliarListaChats() arruma só o
    //    que mudou, e é ela que garante que não sobra card duplicado (os
    //    "Clones Fantasmas" que este apagão existia pra matar).
    let listaProcessada = [...chats];
    const badgeFiltro = document.getElementById('badge-filtro-unread');
    if (badgeFiltro) {
      // Puxa os dados dos inputs/filtros atuais da tela
      const filtroBot = paramBotsFiltro();
      const textoBusca = document.getElementById('input-busca-chat')
        ? document.getElementById('input-busca-chat').value.trim()
        : '';
      const vends = (filtrosAtivos.vendedores || []).join(',');
      const tgs = (filtrosAtivos.tags || []).join(',');
      const stat = filtrosAtivos.status || 'todos';
      // Monta a URL com todos os filtros
      const etps = (filtrosAtivos.etapas || []).join(',');
      const urlCount = `/api/chat/unread-count?busca=${encodeURIComponent(textoBusca)}&botId=${encodeURIComponent(filtroBot)}&status=${stat}&vendedores=${vends}&tags=${tgs}&etapas=${etps}`;
      fetch(urlCount, {
        headers: { Authorization: `Bearer ${localStorage.getItem('token')}` },
      })
        .then((res) => res.json())
        .then((dataCount) => {
          if (dataCount.success && dataCount.total > 0) {
            badgeFiltro.textContent =
              dataCount.total > 999 ? '+999' : dataCount.total;
            badgeFiltro.classList.remove('hidden');
            badgeFiltro.title = `${dataCount.total} conversas pendentes neste filtro`; // <--- Alterado aqui
          } else {
            badgeFiltro.classList.add('hidden');
          }
        })
        .catch(() => {});
    }
    if (listaProcessada.length === 0) {
      containerLista.innerHTML =
        '<div style="padding:20px; text-align:center; color:#888;">Nenhuma conversa encontrada.</div>';
      return;
    }
    // O DESENHO É INCREMENTAL (21/09/2026). Até aqui a lista era apagada e
    // refeita do zero a cada chamada — e esta função roda a cada mensagem
    // que chega. Com 1000 conversas carregadas isso custava ~170ms por
    // mensagem e a tela engasgava nas rajadas. Agora só o card que mudou é
    // refeito; os outros ficam onde estão. Ver reconciliarListaChats().
    reconciliarListaChats(listaProcessada);
    // 3. 🚀 DEVOLVE O SCROLL PARA ONDE ESTAVA (A vendedora não vai nem notar que a lista recarregou)
    if (!limpar) {
      containerLista.scrollTop = scrollAtual;
    } else {
      // Filtro novo = lista nova, e ela começa do topo. Antes isso vinha de
      // graça: apagar tudo zerava a rolagem. Como a lista não é mais
      // apagada, o topo agora é pedido com todas as letras.
      containerLista.scrollTop = 0;
    }
  }

  /* ==========================================================================
     LISTA LATERAL INCREMENTAL
     ==========================================================================
     Cada card é descrito como TEXTO (classe + HTML) e comparado com o texto
     do que já está na tela. Igual: o card fica, intocado. Diferente: só
     ele é refeito. Montar o texto de 1000 cards é barato; o caro — e o que
     travava — era o navegador jogar fora 17 mil elementos e desenhar tudo
     de novo, recarregando até as fotos, a cada mensagem que chegava.

     POR QUE COMPARAR O HTML INTEIRO, e não uma lista de campos: o dia em
     que alguém puser um campo novo no card, a comparação já o enxerga
     sozinha. Uma lista de campos ia esquecê-lo, e o card ficaria mostrando
     informação velha sem erro nenhum no console.

     O que mudar direto no DOM de um card (tirar a bolinha de não lidas,
     marcar como aberto, trocar a foto) acompanha sempre a mudança no dado
     — então o próximo desenho vê a diferença e refaz o card igual ao que o
     desenho antigo faria.
     ========================================================================== */
  // (a memória dos cards, cacheCardsLista, é declarada lá em cima, junto do
  //  containerLista — ver o porquê lá.)
  function reconciliarListaChats(chats) {
    const desejados = [];
    const usados = new Set();

    for (const chat of chats) {
      const { classe, html } = descreverCardChat(chat);
      const assinatura = classe + '\n' + html;
      const guardado = cacheCardsLista.get(chat.chat_id);
      let el;
      if (usados.has(chat.chat_id)) {
        // O mesmo cliente duas vezes na lista (não deveria acontecer). O
        // desenho antigo mostrava dois cards; aqui também, pra não mudar
        // nada além da velocidade.
        el = criarElementoCardChat(chat, classe, html);
      } else if (guardado && guardado.assinatura === assinatura) {
        el = guardado.el;
        el._chatAtual = chat;
      } else {
        el = criarElementoCardChat(chat, classe, html);
        // Troca no MESMO lugar: assim a arrumação da ordem, lá embaixo, só
        // precisa mexer em quem de fato mudou de posição.
        if (guardado && guardado.el.parentNode === containerLista) {
          containerLista.replaceChild(el, guardado.el);
        }
        cacheCardsLista.set(chat.chat_id, { el, assinatura });
      }
      usados.add(chat.chat_id);
      desejados.push(el);
    }

    // Coloca na ordem mexendo só no que está fora do lugar. Uma mensagem
    // nova num cliente do meio da lista = UM card movido pro topo.
    let cursor = containerLista.firstChild;
    for (const el of desejados) {
      if (el === cursor) cursor = cursor.nextSibling;
      else containerLista.insertBefore(el, cursor);
    }
    // O que sobrou depois do último card desejado sai: clientes que saíram
    // do filtro, o "Carregando...", a rodinha do fim da lista. É isto que
    // garante que um card nunca aparece duplicado (os "clones fantasmas").
    while (cursor) {
      const proximo = cursor.nextSibling;
      containerLista.removeChild(cursor);
      cursor = proximo;
    }

    for (const id of cacheCardsLista.keys()) {
      if (!usados.has(id)) cacheCardsLista.delete(id);
    }
  }

  /**
   * Refaz UM card que já está na tela, no mesmo lugar, sem mexer na ordem
   * nem nos outros. Pra quando só um detalhe do card muda (o alerta de
   * mensagem recusada): o renderizarLista inteiro ainda reconta as não
   * lidas no servidor, à toa. Card fora da tela (filtro, outra página): nada
   * a fazer, ele nasce certo quando aparecer.
   */
  function redesenharCardDaLista(chat) {
    const guardado = cacheCardsLista.get(chat.chat_id);
    if (!guardado || guardado.el.parentNode !== containerLista) return;
    const { classe, html } = descreverCardChat(chat);
    const assinatura = classe + '\n' + html;
    if (guardado.assinatura === assinatura) return;
    const el = criarElementoCardChat(chat, classe, html);
    containerLista.replaceChild(el, guardado.el);
    cacheCardsLista.set(chat.chat_id, { el, assinatura });
  }

  /** Descreve o card como texto — classe e HTML — sem criar nada no DOM. */
  function descreverCardChat(chat) {
    let classe = 'chat-item';
    if (chatSelecionado && chat.chat_id === chatSelecionado.chat_id) {
      classe += ' ativo';
    }
    // 👇 CORREÇÃO: Devolve a classe verdinha se o chat estiver na memória de seleção!
    if (
      typeof modoSelecaoMassa !== 'undefined' &&
      modoSelecaoMassa &&
      chatsSelecionadosMassa.has(chat.chat_id)
    ) {
      classe += ' chat-selecionado-massa';
    }
    // Data e Hora (Padrão WhatsApp: Hoje, Ontem, Dia da Semana, Data)
    const dataMsg = new Date(chat.last_message_at);
    const hoje = new Date();
    // Zera as horas para comparar apenas os "dias corridos" reais
    const msgZero = new Date(
      dataMsg.getFullYear(),
      dataMsg.getMonth(),
      dataMsg.getDate()
    );
    const hojeZero = new Date(
      hoje.getFullYear(),
      hoje.getMonth(),
      hoje.getDate()
    );
    // Calcula a diferença em dias
    const diffTempo = hojeZero - msgZero;
    const diffDias = Math.floor(diffTempo / (1000 * 60 * 60 * 24));
    let dataStr = '';
    if (diffDias === 0) {
      dataStr = FORMATO_HORA_LISTA.format(dataMsg);
    } else if (diffDias === 1) {
      dataStr = 'Ontem';
    } else if (diffDias > 1 && diffDias < 7) {
      let diaSemana = FORMATO_DIA_SEMANA_LISTA.format(dataMsg);
      dataStr = diaSemana.charAt(0).toUpperCase() + diaSemana.slice(1);
    } else {
      dataStr = FORMATO_DATA_LISTA.format(dataMsg);
    }
    // Dados Visuais
    const numeroLimpo = chat.chat_id.replace('@c.us', '').replace('55', '');
    let nomeExibicao = chat.crm_nome || chat.name || numeroLimpo; // Blindagem: Remove o @c.us caso ele venha embutido no nome
    if (nomeExibicao && nomeExibicao.includes('@c.us')) {
      nomeExibicao = nomeExibicao.replace('@c.us', '');
    }
    const inicial = nomeExibicao.charAt(0).toUpperCase();
    const nomeExibicaoHtml = escaparHtmlChat(nomeExibicao);

    // Preview
    let textoPrevia = '';
    let isItalic = false;

    if (chat.last_message_type === 'revoked') {
      textoPrevia =
        '<i class="bx bx-block" style="transform: translateY(1px);"></i> Mensagem apagada';
      isItalic = true;
    } else if (chat.last_message_type === 'restricted') {
      textoPrevia =
        '<i class="bx bx-block" style="transform: translateY(1px);"></i> Mensagem Recusada';
      isItalic = true;
    } else if (chat.last_message_type === 'image') {
      textoPrevia = '<i class="fas fa-camera"></i> Imagem';
    } else if (chat.last_message_type === 'video') {
      textoPrevia = '<i class="fas fa-video"></i> Vídeo';
    } else if (
      chat.last_message_type === 'ptt' ||
      chat.last_message_type === 'audio'
    ) {
      textoPrevia = '<i class="fas fa-microphone"></i> Áudio';
    } else if (chat.last_message_type === 'document') {
      textoPrevia = '<i class="fas fa-file-alt"></i> Arquivo';
    } else if (chat.last_message_type === 'sticker') {
      textoPrevia = '<i class="ph-bold ph-sticker"></i> Figurinha';
    } else if (chat.last_message_type === 'contact') {
      textoPrevia = `<i class="ph-bold ph-user-circle"></i> ${escaparHtmlChat(chat.last_message_body || 'Contato')}`;
    } else if (chat.last_message_type === 'ligacao') {
      textoPrevia = '<i class="ph-bold ph-phone-outgoing"></i> Ligação';
    } else if (chat.last_message_type === 'comentario') {
      textoPrevia = `<i class="ph-bold ph-note-pencil"></i> ${htmlLimpoDoSistema(chat.last_message_body)}`;
      isItalic = true;
    } else {
      // Texto do cliente vai como texto. O nosso pode ter o HTML dos avisos
      // do sistema ("Conta restrita. Responda pelo <b>App/Web</b>."): passa
      // pelo filtro, que deixa só a marcação inofensiva.
      textoPrevia =
        (chat.last_message_from_me ? htmlLimpoDoSistema : escaparHtmlChat)(
          limparFormatacaoWhatsApp(chat.last_message_body)
        ) || 'Iniciar conversa...';
    }

    if (chat.last_message_from_me) {
      let remetente = chat.last_sent_by_name || 'Você';
      if (remetente.toUpperCase().startsWith('FLUXO:')) {
        remetente = remetente.replace(/FLUXO:\s*/i, '').trim();
      }
      // Mostra só o primeiro e o segundo nome, pra sobrar espaço pro início da mensagem na lista
      const partesNomeRemetente = remetente.trim().split(/\s+/);
      if (partesNomeRemetente.length > 2) {
        remetente = partesNomeRemetente.slice(0, 2).join(' ');
      }
      if (isItalic) {
        // Engloba nome e texto em itálico e cor suave
        textoPrevia = `<em style="color: #9ca3af;"><strong>${escaparHtmlChat(remetente)}:</strong> ${textoPrevia}</em>`;
      } else {
        textoPrevia = `<strong>${escaparHtmlChat(remetente)}:</strong> ${textoPrevia}`;
      }
    } else {
      if (isItalic) {
        textoPrevia = `<em style="color: #9ca3af;">${textoPrevia}</em>`;
      }
    }

    // MENSAGEM RECUSADA (23/11/2026): a última mensagem é nossa e o WhatsApp
    // recusou (status -1). A prévia fica vermelha (sem ícone: com ele ficava
    // chamativo demais) — a atendente manda, troca de conversa e não via que
    // não foi.
    // Nota, lembrete e ligação na prévia escondem o aviso: elas não são
    // mensagem pro cliente, e o vermelho ia parecer que a nota falhou.
    const previaFalhou =
      chat.ultima_falhou === true &&
      chat.last_message_from_me &&
      !['comentario', 'lembrete', 'ligacao'].includes(chat.last_message_type);
    let atributosPrevia = 'class="chat-last-msg"';
    if (previaFalhou) {
      const dicaFalha = ehCanalRcs(chat.bot_id)
        ? 'Não entregue pelo RCS: o celular do cliente não tem RCS ativo.'
        : 'Mensagem não enviada: o WhatsApp recusou. Abra a conversa.';
      atributosPrevia = `class="chat-last-msg previa-falhou" title="${dicaFalha}"`;
    }

    // Tags (CARROSSEL INTELIGENTE COM AUTO-PLAY QUANDO ATIVO)
    let htmlTags = '';
    if (chat.tags && Array.isArray(chat.tags) && chat.tags.length > 0) {
      const precisaDeLoop = chat.tags.length > 3;
      const listaFinalTags = precisaDeLoop
        ? [...chat.tags, ...chat.tags, ...chat.tags]
        : chat.tags;
      const classeLoop = precisaDeLoop ? 'infinite-loop' : '';
      htmlTags = `<div class="linha-tags-texto ${classeLoop}"><div class="tags-track">`;
      listaFinalTags.forEach((tag) => {
        const corTag = escaparHtmlChat(tag.color);
        htmlTags += `<span class="tag-texto-lista" style="background-color: ${corTag}20; color: ${corTag};">${escaparHtmlChat(tag.name)}</span>`;
      });
      htmlTags += `</div></div>`;
    }
    // LAZY LOADING NA IMAGEM (loading="lazy")
    let htmlAvatarContent = escaparHtmlChat(inicial);
    // 👑 REGRA DE EXIBIÇÃO DA FOTO:
    if (veEquipe && chat.agentes && chat.agentes.length > 0) {
      const username = chat.agentes[0];
      const usernameSafe = textoParaOnclick(username);
      const fotoVendedor = `/fotos-equipe/${encodeURIComponent(username)}.webp`;
      // O nome real do vendedor é resolvido NA HORA DO CLIQUE (não aqui na
      // renderização), pra não ficar preso mostrando o username caso o
      // fetch de /api/team/vendedores ainda não tenha terminado
      htmlAvatarContent = `
          <img src="${escaparHtmlChat(fotoVendedor)}" loading="lazy"
               style="width: 100%; height: 100%; object-fit: cover; border-radius: 50%; cursor: pointer;"
               onclick="window.abrirAvatarVendedor('${textoParaOnclick(fotoVendedor)}', '${usernameSafe}'); event.stopPropagation();"
               data-inicial="${escaparHtmlChat(inicial)}"
               onerror="this.replaceWith(this.dataset.inicial);">
      `;
    } else if (chat.profile_pic_url && chat.profile_pic_url !== 'sem_foto') {
      const nomeClienteAvatar = textoParaOnclick(nomeOuTelefoneCliente(chat));
      htmlAvatarContent = `
          <img src="${escaparHtmlChat(chat.profile_pic_url)}" loading="lazy"
               style="width: 100%; height: 100%; object-fit: cover; border-radius: 50%; cursor: pointer;"
               onclick="window.abrirVisualizador('${textoParaOnclick(chat.profile_pic_url)}', 'avatar', '', '', '', '', '${nomeClienteAvatar}'); event.stopPropagation();"
               data-inicial="${escaparHtmlChat(inicial)}"
               onerror="this.replaceWith(this.dataset.inicial);">
      `;
    }
    // CONTADOR DE CONVERSAS: só aparece com 2 ou mais.
    // Até 21/09/2026 aparecia em todo card, com "1" inclusive. O "1" não
    // dizia nada — o selo de canal já mostra que existe uma conversa — e
    // era mais uma bolinha na foto de quase todo cliente.
    let htmlContadorCanais = '';
    const qtdCanais = chat.canais ? chat.canais.length : 0;
    if (qtdCanais >= 2) {
      htmlContadorCanais = `
          <div class="badge-canais-count" title="${qtdCanais} canais conectados">
              ${qtdCanais}
          </div>
      `;
    }
    // SELOS DE CANAL, na borda da foto — ver selosCanalDoAvatar().
    // Antes só o RCS ganhava marca, colada antes do nome; agora os dois
    // canais aparecem, e fora da linha do nome, que já é a mais disputada
    // do card.
    const htmlSelosCanal = selosCanalDoAvatar(chat.canais);

    const html = `
      <div class="chat-item-topo-tags">${htmlTags}</div>
      <div class="chat-item-corpo">
        <div class="chat-avatar">${htmlAvatarContent} ${htmlContadorCanais}${htmlSelosCanal}</div>
        <div class="chat-info">
          <div class="chat-nome-top">
            <span class="chat-nome-cliente">${nomeExibicaoHtml}</span>
            <span class="chat-data">${dataStr}</span>
          </div>
          <div class="chat-preview-bottom">
            <span ${atributosPrevia}>${textoPrevia}</span>
            ${chat.unread_count > 0 ? `<span class="badge-unread">${chat.unread_count}</span>` : ''}
          </div>
        </div>
      </div>
    `;
    return { classe, html };
  }

  /**
   * Cria o card de verdade, com os ouvintes de clique.
   *
   * `div._chatAtual` é o objeto do chat que o clique abre, e ele é trocado
   * sempre que o card é reaproveitado. O desenho antigo recriava o card a
   * cada vez e o clique sempre levava o objeto mais novo da lista; se aqui
   * o clique guardasse o objeto da hora em que o card nasceu, abrir a
   * conversa poderia trabalhar numa cópia velha — e o que ela zerasse (as
   * não lidas, por exemplo) não chegaria na lista.
   */
  function criarElementoCardChat(chat, classe, html) {
    const div = document.createElement('div');
    div.className = classe;
    div.dataset.chatId = chat.chat_id;
    div.innerHTML = html;
    div._chatAtual = chat;
    const iniciarCliqueLongo = (e) => {
      if (modoSelecaoMassa) return; // Se já tá selecionando, ignora o tempo
      timerCliqueLongo = setTimeout(() => {
        ativarModoSelecao(chat.chat_id);
      }, 1000); // 700 milissegundos segurando para ativar
    };
    const cancelarCliqueLongo = () => {
      if (timerCliqueLongo) clearTimeout(timerCliqueLongo);
    };
    div.addEventListener('mousedown', iniciarCliqueLongo);
    div.addEventListener('touchstart', iniciarCliqueLongo, { passive: true });
    div.addEventListener('mouseup', cancelarCliqueLongo);
    div.addEventListener('mouseleave', cancelarCliqueLongo);
    div.addEventListener('touchend', cancelarCliqueLongo);
    div.addEventListener('touchcancel', cancelarCliqueLongo);
    div.addEventListener('click', (e) => {
      cancelarCliqueLongo();
      if (isDraggingTags) {
        isDraggingTags = false;
        return;
      } // Se o modo fantasma estiver ativo, a gente só seleciona a caixinha, não abre a conversa!
      if (modoSelecaoMassa) {
        window.toggleSelecaoChat(chat.chat_id, div);
        return;
      }
      abrirConversa(div._chatAtual);
    });
    return div;
  }
  // --- EVENTO DE SCROLL (Para carregar mais ao descer) ---
  containerLista.addEventListener('scroll', () => {
    if (inputBusca.value) return;
    if (
      containerLista.scrollTop + containerLista.clientHeight >=
      containerLista.scrollHeight - 50
    ) {
      if (temMaisChats && !carregandoChats) {
        // Cria loading pequeno no final
        if (!document.getElementById('loading-scroll')) {
          const d = document.createElement('div');
          d.id = 'loading-scroll';
          d.style.textAlign = 'center';
          d.style.padding = '10px';
          d.innerHTML = '<i class="fas fa-spinner fa-spin"></i>';
          containerLista.appendChild(d);
        }
        paginaAtual++;
        carregarChats(paginaAtual, false);
      }
    }
  });
  // --- Dropdown de bots da lista lateral (MULTI-SELEÇÃO) ---
  //
  // Cada linha é um toggle e o menu NÃO fecha ao marcar: filtrar dois bots não
  // pode significar abrir o dropdown duas vezes. A lista só é recarregada 450ms
  // depois do último clique (ou na hora, quando o menu fecha), senão marcar três
  // bots seguidos dispararia três buscas.
  async function carregarFiltroBotsComStatus() {
    const selectOriginal = document.getElementById('select-filtro-bot');
    if (!selectOriginal) return;
    // 1. Esconde o select nativo feio (fica só como espelho do filtro)
    selectOriginal.style.display = 'none';
    // 2. Cria a estrutura visual do novo Dropdown se ela não existir
    let customWrapper = document.getElementById('custom-bot-select');
    if (!customWrapper) {
      customWrapper = document.createElement('div');
      customWrapper.id = 'custom-bot-select';
      customWrapper.className = 'custom-bot-select';
      customWrapper.innerHTML = `
            <div class="custom-select-trigger" id="custom-select-trigger">
                <div id="custom-select-display" style="display:flex; align-items:center; gap:8px;">Todos os Bots</div>
                <i class="fas fa-chevron-down" style="color:#888;"></i>
            </div>
            <div class="custom-options-container hidden" id="custom-options-container"></div>
        `;
      selectOriginal.parentNode.insertBefore(
        customWrapper,
        selectOriginal.nextSibling
      );
      document
        .getElementById('custom-select-trigger')
        .addEventListener('click', (e) => {
          e.stopPropagation();
          const cont = document.getElementById('custom-options-container');
          const estavaAberto = !cont.classList.contains('hidden');
          cont.classList.toggle('hidden');
          // Fechou no braço com clique ainda no debounce? Aplica agora.
          if (estavaAberto) aplicarFiltroBots(true);
        });
      document.addEventListener('click', (e) => {
        if (!customWrapper.contains(e.target)) {
          const cont = document.getElementById('custom-options-container');
          if (cont && !cont.classList.contains('hidden')) {
            cont.classList.add('hidden');
            aplicarFiltroBots(true);
          }
        }
      });
    }
    try {
      // 1. Muda para a rota que puxa TUDO
      const res = await fetch('/api/sessions/all', {
        headers: { Authorization: `Bearer ${localStorage.getItem('token')}` },
      });
      if (res.status === 401 || res.status === 403) return;
      let bots = await res.json();
      // Guarda de qual CANAL é cada bot (whatsapp ou rcs). A tela usa isso
      // pra marcar a aba e pra travar o que o RCS não aceita — sem isso a
      // vendedora tentaria mandar áudio num canal que não tem áudio.
      //
      // A lista lateral é desenhada ANTES desta resposta chegar, e até aqui
      // todo bot é tratado como WhatsApp. Sem o redesenho abaixo, quem abria
      // o bate-papo via cliente de RCS com o selo do WhatsApp até a próxima
      // mensagem chegar (achado em 21/09/2026). O redesenho é incremental:
      // só os cards cujo canal mudou são refeitos, e só quando algum mudou —
      // esta função roda de novo a cada 30s e não pode redesenhar à toa.
      let canalMudou = false;
      let nomeMudou = false;
      bots.forEach((b) => {
        if (!b || !b.id) return;
        const canal = b.canal || 'whatsapp';
        if (canalDoBot(b.id) !== canal) canalMudou = true;
        CANAL_POR_BOT[b.id] = canal;
        if (b.name) {
          if (NOME_POR_BOT[b.id] !== b.name) nomeMudou = true;
          NOME_POR_BOT[b.id] = b.name;
        }
      });
      mapaCanaisCarregado = true;
      avisarMapaCanaisPronto();
      if (canalMudou && todosChats.length) renderizarLista(todosChats);
      // Rede de segurança: se uma conversa já estava aberta com o canal
      // errado (o mapa demorou mais que a espera do abrirConversa), o
      // cabeçalho e os botões do canal são refeitos agora — antes só a lista
      // lateral era corrigida e o cabeçalho ficava errado até trocar de aba.
      if ((canalMudou || nomeMudou) && chatSelecionado) {
        const botAberto = chatSelecionado.bot_id;
        renderizarAbas(chatSelecionado.canais, true);
        aplicarRestricoesDoCanal(botAberto);
        atualizarAvisoJanelaRcs(botAberto, chatSelecionado.chat_id);
        atualizarBotaoPuxarRcs(botAberto);
      }
      // Formatação visual do nome dos canais (ex: limpa os underlines)
      todosChats.forEach((chat) => {
        if (chat.canais) {
          chat.canais.forEach((canal) => {
            if (!canal.botName || canal.botName === canal.bot_id) {
              // O nome cadastrado do bot vale mais do que o id arrumado.
              if (NOME_POR_BOT[canal.bot_id]) {
                canal.botName = NOME_POR_BOT[canal.bot_id];
                return;
              }
              let nomeLindo = canal.bot_id
                .replace(/__/g, ' | ')
                .replace(/_/g, ' ');
              nomeLindo = nomeLindo
                .toLowerCase()
                .replace(/(?:^|\s|\||-)\w/g, (m) => m.toUpperCase());
              canal.botName = nomeLindo;
            }
          });
        }
      });
      // Filtro para vendedores só verem os bots que eles têm cliente
      //
      // O conjunto SÓ CRESCE enquanto a página está aberta. Antes ele era
      // refeito com as conversas da tela, e a tela já vem filtrada pelo
      // próprio dropdown: "desmarcar todos" esvaziava a lista -> o dropdown
      // ficava sem bot -> voltava pra "Todos" (a piscada que a Bianca viu em
      // 24/09/2026). Escolher alguns bots também quebrava: a lista encolhia
      // pros escolhidos e aí "os escolhidos" viravam "todos".
      if (!veEquipe) {
        todosChats.forEach((chat) => {
          if (chat.canais) {
            chat.canais.forEach((canal) => botsComClientesVistos.add(canal.bot_id));
          } else if (chat.bot_id) {
            botsComClientesVistos.add(chat.bot_id);
          }
        });
        bots = bots.filter((bot) => botsComClientesVistos.has(bot.id));
      }
      selectOriginal.innerHTML = '<option value="todos">Todos os Bots</option>';
      const customContainer = document.getElementById(
        'custom-options-container'
      );
      // O menu pode estar ABERTO agora: na multi-seleção ele fica aberto entre
      // um clique e outro, e cada clique recarrega a lista, que volta aqui.
      // Sem guardar o scroll, quem marca o segundo bot lá embaixo da lista de
      // 80 veria o menu pular pro topo no meio da marcação.
      const scrollMenu = customContainer.scrollTop;
      customContainer.innerHTML = '';
      // "Todos os Bots" aqui não é uma opção entre as outras: é o interruptor
      // geral. Tudo marcado -> desmarca tudo (lista vazia até marcar algum
      // bot); qualquer outro estado -> marca tudo de novo. O menu continua
      // aberto, porque depois de "desmarcar tudo" o próximo passo é marcar os
      // bots que interessam.
      const divTodos = document.createElement('div');
      divTodos.className = 'custom-option opcao-bot-todos';
      divTodos.innerHTML = `
          <div class="linha-todos-bots">
              <span style="font-weight: bold;">Todos os Bots</span>
              <span class="dica-marcar-todos">desmarcar todos</span>
          </div>`;
      divTodos.onclick = (e) => {
        e.stopPropagation();
        const tudoMarcado = !nenhumBotFiltro && !botsSelecionados.length;
        botsSelecionados = [];
        nenhumBotFiltro = tudoMarcado;
        sincronizarVisualFiltroBots();
        aplicarFiltroBots(false);
      };
      customContainer.appendChild(divTodos);
      if (!bots || bots.length === 0) {
        // Se a vendedora não tem cliente nenhum, garante que fica no "Todos"
        if (botsSelecionados.length || nenhumBotFiltro) {
          nenhumBotFiltro = false;
          botsSelecionados = [];
          filtroBotsPendente = true;
          sincronizarVisualFiltroBots();
          aplicarFiltroBots(true);
        } else {
          sincronizarVisualFiltroBots();
        }
        return;
      }
      bots.sort((a, b) => {
        if (a.deleted !== b.deleted) return a.deleted ? 1 : -1;
        if (a.online !== b.online) return a.online ? -1 : 1;
        return a.name.localeCompare(b.name);
      });
      // Bot que sumiu da lista (arquivado de vez, ou vendedora que perdeu o
      // último cliente dele) não pode continuar filtrando escondido.
      const idsValidos = new Set(bots.map((b) => b.id));
      const qtdAntes = botsSelecionados.length;
      botsSelecionados = botsSelecionados.filter((id) => idsValidos.has(id));
      // Se o que sobrou é a lista inteira, isso é "todos": volta pra lista
      // vazia pra não mandar 77 ids ao servidor no lugar de um ?botId=todos.
      if (botsSelecionados.length === bots.length) botsSelecionados = [];
      const perdeuAlgum = botsSelecionados.length !== qtdAntes;
      infoBotsFiltro = new Map();
      bots.forEach((bot) => {
        const opt = document.createElement('option');
        opt.value = bot.id;
        selectOriginal.appendChild(opt);
        infoBotsFiltro.set(bot.id, {
          name: bot.name,
          html: htmlConteudoBot(bot, false),
          // Arquivado conta como desconectado: é o mesmo "Conexão
          // Interrompida" na conversa.
          online: !!bot.online,
        });
        const divOpt = document.createElement('div');
        divOpt.className = 'custom-option opcao-bot-multi';
        divOpt.dataset.botId = bot.id;
        divOpt.innerHTML = htmlConteudoBot(bot, true);
        divOpt.onclick = (e) => {
          e.stopPropagation();
          toggleBotFiltro(bot.id);
        };
        customContainer.appendChild(divOpt);
      });
      customContainer.scrollTop = scrollMenu;
      sincronizarVisualFiltroBots();
      if (perdeuAlgum) {
        filtroBotsPendente = true;
        aplicarFiltroBots(true);
      }
    } catch (e) {
      console.error('[Filtro Bots] Erro ao carregar:', e);
    }
  }
  // Linha do bot no dropdown. `comCheck` false devolve o mesmo conteúdo sem a
  // caixinha — é o que vai no trigger quando só um bot está marcado.
  function htmlConteudoBot(bot, comCheck) {
    let icone = '🔴';
    let sufixo = '';
    let corTexto = '';
    if (bot.online) icone = '🟢';
    else if (bot.deleted) {
      icone = '🚫';
      sufixo = " <span style='color:#888; font-size:10px;'>(Arquivado)</span>";
      corTexto = 'color: #888;';
    }
    let tagsHtml = '';
    if (bot.tags && bot.tags.length > 0) {
      tagsHtml = bot.tags
        .map((t) => {
          // A cor vem do banco e cai dentro de um style=: só passa o que tem
          // cara de cor, senão dá pra escrever CSS pelo atributo.
          const cor = /^#[0-9a-fA-F]{3,8}$/.test(String(t.color || ''))
            ? t.color
            : '#9ca3af';
          return `<span class="tag-pill-dropdown" style="background-color: ${cor}20; color: ${cor}; border: 1px solid ${cor};">${escaparHtmlChat(t.name)}</span>`;
        })
        .join('');
    } else {
      tagsHtml = `<span class="tag-pill-sem-tag">SEM TAG</span>`;
    }
    const check = comCheck
      ? `<span class="check-bot-multi"><i class="fas fa-check"></i></span>`
      : '';
    return `
            <div class="custom-option-grid ${comCheck ? 'grid-bot-multi' : ''}">
                ${check}
                <span style="text-align: center;">${icone}</span>
                <div class="tags-wrapper-dropdown">
                    ${tagsHtml}
                </div>
                <span class="nome-bot-dropdown" style="${corTexto} font-weight: 500;">
                    ${escaparHtmlChat(bot.name)}${sufixo}
                </span>
            </div>
        `;
  }
  // Marca/desmarca um bot sem fechar o menu.
  //
  // Em "Todos os Bots" TODAS as linhas aparecem marcadas, então o primeiro
  // clique numa linha é uma DESmarcação: o filtro passa a ser "todos menos
  // este". É como as vendedoras usam o filtro — elas tiram os bots que não
  // interessam em vez de marcar um por um numa lista de quase 80.
  function toggleBotFiltro(botId) {
    const idsVisiveis = Array.from(infoBotsFiltro.keys());
    if (nenhumBotFiltro) {
      // Saindo do "desmarcar todos": o clique MARCA este bot.
      nenhumBotFiltro = false;
      botsSelecionados = idsVisiveis.length === 1 ? [] : [botId];
    } else if (!botsSelecionados.length) {
      const complemento = idsVisiveis.filter((id) => id !== botId);
      if (complemento.length) botsSelecionados = complemento;
      else nenhumBotFiltro = true; // lista com um bot só e ele foi desmarcado
    } else {
      const pos = botsSelecionados.indexOf(botId);
      if (pos === -1) botsSelecionados.push(botId);
      else botsSelecionados.splice(pos, 1);
      // Desmarcou o último: nenhum bot. Remarcou todos: volta pra lista
      // vazia, que é o que vira ?botId=todos lá na consulta.
      if (!botsSelecionados.length) nenhumBotFiltro = true;
      else if (botsSelecionados.length === idsVisiveis.length)
        botsSelecionados = [];
    }
    sincronizarVisualFiltroBots();
    aplicarFiltroBots(false);
  }
  // --- "Status Bot" (popup de Filtros) x dropdown de bots ---
  //
  // As pílulas Conectado/Desconectado NÃO são um segundo filtro: elas são um
  // atalho que marca/desmarca bots no dropdown do topo. Assim só existe UMA
  // seleção de bots e um filtro nunca sobrescreve o outro. A pílula aparece
  // acesa quando há pelo menos um bot daquele grupo marcado no dropdown — por
  // isso, em "Todos os Bots", as duas vêm acesas.
  function statusBotDerivado() {
    const estado = { conectado: false, desconectado: false };
    if (nenhumBotFiltro) return estado;
    const ids = botsSelecionados.length
      ? botsSelecionados
      : Array.from(infoBotsFiltro.keys());
    // Ainda sem a lista de bots carregada: "todos" = as duas acesas.
    if (!ids.length || !infoBotsFiltro.size) {
      return { conectado: true, desconectado: true };
    }
    ids.forEach((id) => {
      const info = infoBotsFiltro.get(id);
      if (!info) return;
      if (info.online) estado.conectado = true;
      else estado.desconectado = true;
    });
    // Seleção só com bots que não estão mais na lista (não deveria
    // acontecer, o carregamento limpa): trata como "todos".
    if (!estado.conectado && !estado.desconectado && botsSelecionados.length)
      return { conectado: true, desconectado: true };
    return estado;
  }
  function sincronizarPilulasStatusBot(forcar) {
    if (statusBotEditado && !forcar) return;
    const estado = statusBotDerivado();
    document.querySelectorAll('[data-tipo="status-bot"]').forEach((btn) => {
      btn.classList.toggle('ativo', !!estado[btn.dataset.valor]);
    });
  }
  // Aplica as pílulas no dropdown. Mexe só no grupo cuja pílula MUDOU:
  // apagar "Desconectado" tira todos os desconectados e deixa o resto como
  // estava; acender de volta devolve todos eles. Devolve true se mudou algo.
  function aplicarStatusBotNoFiltro(querConectado, querDesconectado) {
    const atual = statusBotDerivado();
    if (
      atual.conectado === querConectado &&
      atual.desconectado === querDesconectado
    )
      return false;
    const ids = Array.from(infoBotsFiltro.keys());
    if (!ids.length) return false;
    const marcados = new Set(
      nenhumBotFiltro ? [] : botsSelecionados.length ? botsSelecionados : ids
    );
    const mexer = (online, quer) => {
      ids
        .filter((id) => infoBotsFiltro.get(id).online === online)
        .forEach((id) => (quer ? marcados.add(id) : marcados.delete(id)));
    };
    if (querConectado !== atual.conectado) mexer(true, querConectado);
    if (querDesconectado !== atual.desconectado) mexer(false, querDesconectado);
    const lista = ids.filter((id) => marcados.has(id));
    nenhumBotFiltro = lista.length === 0;
    botsSelecionados = lista.length === ids.length ? [] : lista;
    sincronizarVisualFiltroBots();
    espelharSelectFiltroBot();
    return true;
  }
  // Espelha `botsSelecionados` na tela: marca as linhas e reescreve o trigger.
  function sincronizarVisualFiltroBots() {
    // Sem nenhum bot escolhido o filtro é "todos" — e aí todas as linhas
    // aparecem MARCADAS, não desmarcadas: o que a tela mostra é exatamente o
    // que a lista de conversas está mostrando.
    const tudoMarcado = !nenhumBotFiltro && botsSelecionados.length === 0;
    const cont = document.getElementById('custom-options-container');
    if (cont) {
      cont.querySelectorAll('.opcao-bot-multi').forEach((div) => {
        div.classList.toggle(
          'bot-marcado',
          tudoMarcado || botsSelecionados.includes(div.dataset.botId)
        );
      });
      const todos = cont.querySelector('.opcao-bot-todos');
      if (todos) {
        todos.classList.toggle('bot-marcado', tudoMarcado);
        // A dica diz o que o clique na linha do topo vai fazer.
        const dica = todos.querySelector('.dica-marcar-todos');
        if (dica)
          dica.textContent = tudoMarcado ? 'desmarcar todos' : 'marcar todos';
      }
    }
    sincronizarPilulasStatusBot();
    const display = document.getElementById('custom-select-display');
    if (!display) return;
    // Seleção que é exatamente um grupo inteiro de status ganha o nome do
    // grupo — é o que aparece depois de usar o "Status Bot" do popup.
    const ids = Array.from(infoBotsFiltro.keys());
    const doGrupo = (online) =>
      ids.filter((id) => infoBotsFiltro.get(id).online === online);
    const mesmaLista = (a, b) =>
      a.length > 1 && a.length === b.length && a.every((id) => b.includes(id));
    if (nenhumBotFiltro) {
      display.innerHTML =
        '<span class="resumo-bots-filtro resumo-bots-nenhum">Nenhum bot marcado</span>';
      display.removeAttribute('title');
    } else if (tudoMarcado) {
      display.innerHTML = 'Todos os Bots';
      display.removeAttribute('title');
    } else if (mesmaLista(botsSelecionados, doGrupo(true))) {
      display.innerHTML = `<span class="resumo-bots-filtro"><span class="bolinha-status-bot conectado"></span>Só conectados <b>(${botsSelecionados.length})</b></span>`;
      display.title = botsSelecionados
        .map((id) => (infoBotsFiltro.get(id) || {}).name || id)
        .join('\n');
    } else if (mesmaLista(botsSelecionados, doGrupo(false))) {
      display.innerHTML = `<span class="resumo-bots-filtro"><span class="bolinha-status-bot desconectado"></span>Só desconectados <b>(${botsSelecionados.length})</b></span>`;
      display.title = botsSelecionados
        .map((id) => (infoBotsFiltro.get(id) || {}).name || id)
        .join('\n');
    } else if (botsSelecionados.length === 1) {
      const info = infoBotsFiltro.get(botsSelecionados[0]);
      display.innerHTML = info
        ? info.html
        : escaparHtmlChat(botsSelecionados[0]);
      display.title = info ? info.name : botsSelecionados[0];
    } else {
      const fora = Array.from(infoBotsFiltro.keys()).filter(
        (id) => !botsSelecionados.includes(id)
      );
      // Quem tirou dois bots de 77 não quer ler "75 bots selecionados": o que
      // ela fez foi EXCLUIR dois, e é isso que o resumo tem que contar.
      if (fora.length > 0 && fora.length <= 3) {
        display.innerHTML = `<span class="resumo-bots-filtro">Todos menos <b>${fora.length}</b></span>`;
        display.title = fora
          .map((id) => `− ${(infoBotsFiltro.get(id) || {}).name || id}`)
          .join('\n');
      } else {
        display.innerHTML = `<span class="resumo-bots-filtro"><b>${botsSelecionados.length}</b> bots selecionados</span>`;
        display.title = botsSelecionados
          .map((id) => (infoBotsFiltro.get(id) || {}).name || id)
          .join('\n');
      }
    }
  }
  // Recarrega a lista com o filtro novo. `imediato` = agora (o menu fechou);
  // senão espera o usuário parar de clicar.
  // Espelho pro select nativo: só faz sentido com um bot marcado.
  function espelharSelectFiltroBot() {
    const selectOriginal = document.getElementById('select-filtro-bot');
    if (!selectOriginal) return;
    const alvo = botsSelecionados.length === 1 ? botsSelecionados[0] : 'todos';
    const existe = Array.from(selectOriginal.options).some(
      (o) => o.value === alvo
    );
    if (existe) selectOriginal.value = alvo;
  }
  function aplicarFiltroBots(imediato) {
    espelharSelectFiltroBot();
    clearTimeout(timerFiltroBots);
    if (imediato) {
      if (!filtroBotsPendente) return;
      filtroBotsPendente = false;
      carregarChats(1, true);
      return;
    }
    filtroBotsPendente = true;
    timerFiltroBots = setTimeout(() => {
      filtroBotsPendente = false;
      carregarChats(1, true);
    }, 450);
  }
  // --- Função Global mantida por compatibilidade: seleciona UM bot só ---
  window.selecionarBotCustom = function (valorId) {
    botsSelecionados = valorId && valorId !== 'todos' ? [valorId] : [];
    nenhumBotFiltro = false;
    sincronizarVisualFiltroBots();
    const cont = document.getElementById('custom-options-container');
    if (cont) cont.classList.add('hidden');
    filtroBotsPendente = true;
    aplicarFiltroBots(true);
  };
  /**
   * Rola até uma mensagem e pisca ela. Vale pro card do lembrete (que é uma
   * `.msg-row` com `data-wpp-id`) e pra qualquer balão comum — nas mensagens
   * enviadas o `data-wpp-id` mora no `.msg-status`, daí o segundo caminho.
   *
   * `block: 'center'` e não `'start'`: parar com a mensagem colada no topo
   * esconde o que veio ANTES dela, que costuma ser o contexto de por que o
   * lembrete foi criado.
   */
  function focarMensagem(wppId) {
    if (!wppId) return false;
    const alvo =
      containerMsgs.querySelector(`.msg-row[data-wpp-id="${wppId}"]`) ||
      containerMsgs
        .querySelector(`[data-wpp-id="${wppId}"]`)
        ?.closest('.msg-row');
    if (!alvo) return false;
    alvo.scrollIntoView({ behavior: 'smooth', block: 'center' });
    const peca = alvo.querySelector('.msg-bubble') || alvo;
    peca.classList.add('msg-focada');
    // 3,2s = os três pulsos de 1s do `.msg-focada` (ver pag-bate-papo.css) mais
    // uma folga. Tirar a classe antes disso corta o último pulso no meio.
    setTimeout(() => peca.classList.remove('msg-focada'), 3200);
    return true;
  }

  // ============================================================
  // 3. ABRIR CONVERSA (CORRIGIDO: CARREGA TAGS DO CRM)
  // ============================================================
  /**
   * O BOTÃO "CHAMAR NO WHATSAPP" (23/09/2026).
   *
   * Só na aba do RCS. Pra vendedora, ele abre a lista dos chips que um admin
   * vinculou a ela (rota /api/rcs/vinculos-meus) e, ao escolher, o sistema
   * abre a conversa naquele chip, passa o cliente pra ela e leva o cartão pro
   * Atendimento Inicial. Pro admin, o mesmo botão abre a tela de vínculo —
   * é lá que ele diz quais números cada uma pode usar.
   *
   * Por que limitar: a casa tem chip de tráfego, de disparo e o pessoal de
   * cada uma. Sem o vínculo, dá pra começar a conversa pelo número da colega
   * sem perceber — é o mesmo cuidado que já existe no Novo Disparo.
   */
  function atualizarBotaoPuxarRcs(botId) {
    const btn = document.getElementById('btn-puxar-rcs');
    if (!btn) return;
    const mostra = ehCanalRcs(botId) && !!chatSelecionado;
    btn.classList.toggle('hidden', !mostra);
    const texto = document.getElementById('btn-puxar-rcs-texto');
    if (texto) texto.textContent = podeVincularRcs ? 'Vincular números' : 'Chamar no WhatsApp';
    btn.classList.toggle('modo-admin', !!podeVincularRcs);
    btn.title = podeVincularRcs
      ? 'Vincular números: escolher quais cada atendente pode usar'
      : 'Chamar este cliente no WhatsApp';
    btn.setAttribute('aria-label', btn.title);
  }

  /** Vendedora: escolhe o chip e o sistema abre a conversa lá. */
  async function puxarClienteParaWhatsapp() {
    const numero = (chatSelecionado?.chat_id || '').replace(/\D/g, '');
    if (!numero) return;
    // As etiquetas do chip vêm de /api/sessions/all (é de lá que o "Adicionar
    // Cliente" tira as dele). São elas que a vendedora usa pra escolher, então
    // valem a segunda chamada; se falhar, a lista sai só com os nomes.
    let bots = [];
    let etiquetasPorChip = new Map();
    try {
      const [rVinc, rSess] = await Promise.all([
        fetch('/api/rcs/vinculos-meus', { headers: cabecalhoAuth() }),
        fetch('/api/sessions/all', { headers: cabecalhoAuth() }).catch(() => null),
      ]);
      bots = (await rVinc.json()).bots || [];
      if (rSess && rSess.ok)
        etiquetasPorChip = new Map(
          (await rSess.json()).map((s) => [s.id, s.tags || []])
        );
    } catch (e) {
      return mostrarAlerta('Não consegui buscar os seus números.', 'error');
    }
    if (!bots.length)
      return Swal.fire({
        title: 'Nenhum número liberado',
        text: 'Peça a um admin para vincular os números que você pode usar para chamar cliente do RCS.',
        icon: 'info',
      });

    // Mesma linha do dropdown de bot do "Adicionar Cliente": etiqueta à
    // esquerda, nome à direita. As classes são as de lá, pra não nascer um
    // segundo visual de lista de chip no sistema.
    const linhaDoChip = (b) => {
      const tags = etiquetasPorChip.get(b.id) || [];
      const tagsHtml = tags.length
        ? tags
            .map(
              (t) =>
                `<span class="tag-pill-dropdown" style="background-color:${escaparHtmlChat(t.color)}20;color:${escaparHtmlChat(t.color)}">${escaparHtmlChat(t.name)}</span>`
            )
            .join('')
        : '<span class="tag-pill-sem-tag">SEM TAG</span>';
      // Sem o ícone do WhatsApp: aqui todo chip é de WhatsApp, ele só ocupava
      // espaço. Por isso a grade tem duas colunas, e não as três de lá.
      return `<div class="custom-option-grid" style="grid-template-columns:80px 1fr">
                <div class="tags-wrapper-dropdown">${tagsHtml}</div>
                <span class="nome-bot-dropdown">${escaparHtmlChat(b.nome)}</span>
              </div>`;
    };

    // Ordena pela etiqueta, que é por onde elas procuram; sem etiqueta vai pro
    // fim, e dentro da mesma etiqueta vale o nome do chip.
    const chaveDaEtiqueta = (b) => {
      const t = (etiquetasPorChip.get(b.id) || [])[0];
      return t ? '0' + t.name.toLowerCase() : '1';
    };
    bots.sort(
      (a, b) =>
        chaveDaEtiqueta(a).localeCompare(chaveDaEtiqueta(b)) ||
        String(a.nome).localeCompare(String(b.nome))
    );

    const { value: escolhido } = await Swal.fire({
      title: 'Chamar no WhatsApp',
      html: `<div style="text-align:left;font-size:13px;color:#64748b;margin-bottom:12px">
               Cliente será transferido para o funil <b>Atendimento Portabilidade</b>
               na etapa <b>Atendimento Inicial</b>.
             </div>
             <div id="drop-chip-puxar" style="position:relative;text-align:left">
               <input type="hidden" id="swal-chip-puxar" value="${bots[0].id}">
               <div id="trigger-chip-puxar" class="custom-select-trigger">
                 <span id="display-chip-puxar" style="display:flex;flex:1;min-width:0">
                   ${linhaDoChip(bots[0])}
                 </span>
                 <i class="bx bx-chevron-down" style="color:var(--texto-suave)"></i>
               </div>
               <div id="options-chip-puxar" class="custom-options-container hidden">
                 ${bots
                   .map(
                     (b) => `<div class="custom-option" data-value="${b.id}">${linhaDoChip(b)}</div>`
                   )
                   .join('')}
               </div>
             </div>`,
      showCancelButton: true,
      confirmButtonText: 'Abrir conversa',
      cancelButtonText: 'Cancelar',
      confirmButtonColor: '#00ba80',
      didOpen: () => {
        // Sem isto a lista abre por baixo da borda do modal (o SweetAlert
        // recorta o que passa do container) e por baixo dos botões, que são
        // irmãos dela e vêm depois no HTML.
        const caixa = document.querySelector('.swal2-html-container');
        if (caixa) {
          caixa.style.overflow = 'visible';
          caixa.style.position = 'relative';
          caixa.style.zIndex = '5';
        }
        const lista = document.getElementById('options-chip-puxar');
        document.getElementById('trigger-chip-puxar').addEventListener('click', () => {
          lista.classList.toggle('hidden');
        });
        lista.addEventListener('click', (e) => {
          const op = e.target.closest('.custom-option');
          if (!op) return;
          document.getElementById('swal-chip-puxar').value = op.dataset.value;
          document.getElementById('display-chip-puxar').innerHTML = op.innerHTML;
          lista.classList.add('hidden');
        });
      },
      preConfirm: () => document.getElementById('swal-chip-puxar').value,
    });
    if (!escolhido) return;

    try {
      const r = await fetch('/api/rcs/puxar-para-whatsapp', {
        method: 'POST',
        headers: { ...cabecalhoAuth(), 'Content-Type': 'application/json' },
        body: JSON.stringify({ numeroWpp: numero, instanciaId: escolhido }),
      });
      const d = await r.json();
      // Cliente sem WhatsApp merece uma janela, não um aviso de canto: é ela
      // que evita a vendedora escrever uma mensagem inteira à toa. O servidor
      // confere pelo chip escolhido e não cria nada quando a conta não existe.
      if (d.codigo === 'sem_whatsapp')
        return Swal.fire({
          title: 'Cliente não possui WhatsApp',
          text: 'Não existe conta vinculada no whatsapp para esse cliente',
          icon: 'warning',
          confirmButtonText: 'Entendi',
          confirmButtonColor: 'var(--azul-principal)',
        });
      if (!d.ok) return mostrarAlerta(d.erro || 'Não deu pra abrir a conversa.', 'error');
      mostrarAlerta('Conversa aberta no WhatsApp! O cliente é seu.', 'success');

      // A aba nova entra na hora, como no "Adicionar Cliente": encaixa o canal
      // no chat que já está na lista, sobe ele pro topo e abre JÁ no WhatsApp.
      // Esperar o recarregamento da lista não bastava — ela pagina, e o chat
      // podia nem voltar na primeira página.
      const nomeChip = bots.find((b) => b.id === escolhido)?.nome || escolhido;
      const canalNovo = {
        bot_id: escolhido,
        botName: nomeChip,
        has_permission: true,
        isOnline: true,
        status: 'online',
        unread: 0,
        ...(d.canal || {}),
      };
      const i = todosChats.findIndex(
        (c) => (c.chat_id || '').replace(/\D/g, '') === numero
      );
      let chat = i !== -1 ? todosChats[i] : null;
      if (chat) {
        if (!chat.canais.some((c) => c.bot_id === escolhido)) chat.canais.push(canalNovo);
        todosChats.splice(i, 1);
        todosChats.unshift(chat);
        renderizarLista(todosChats);
      } else {
        await carregarChats(1, true);
        chat = todosChats.find((c) => (c.chat_id || '').replace(/\D/g, '') === numero);
      }
      if (chat) await abrirConversa(chat, escolhido);
    } catch (e) {
      mostrarAlerta('Falha de rede ao abrir a conversa.', 'error');
    }
  }

  /**
   * ADMIN: quais chips cada atendente pode usar pra chamar cliente do RCS.
   * Mesma cara do "Vincular números" da tela de Novo Disparo (as classes
   * .painel-lista-clean / .item-bot-clean vêm de pag-campanhas.css, que o
   * estilo.css carrega no sistema inteiro) — a janela de antes, montada no
   * SweetAlert, ficou confusa do lado daquela.
   */
  const vinculoRcs = { usuarioId: null, bots: [], marcados: new Set(), busca: '' };

  function fecharVinculoRcs() {
    const fundo = document.getElementById('fundo-modal-vinculo-rcs');
    if (fundo) fundo.style.display = 'none';
  }

  function desenharListaVinculoRcs() {
    const lista = document.getElementById('lista-vinculo-rcs');
    const total = document.getElementById('count-vinculo-rcs');
    const sel = document.getElementById('sel-count-vinculo-rcs');
    if (!lista) return;
    if (total) total.textContent = vinculoRcs.bots.length;
    if (sel) sel.textContent = `${vinculoRcs.marcados.size} selecionado(s)`;

    const vazio = (texto) =>
      `<div class="empty-state"><i class="bx bx-user-x"></i><span>${texto}</span></div>`;
    if (!vinculoRcs.usuarioId) return (lista.innerHTML = vazio('Selecione uma atendente acima'));
    if (!vinculoRcs.bots.length) return (lista.innerHTML = vazio('Nenhum número conectado'));

    const termo = vinculoRcs.busca.toLowerCase();
    const filtrados = vinculoRcs.bots.filter(
      (b) => b.nome.toLowerCase().includes(termo) || String(b.numero || '').includes(termo)
    );
    if (!filtrados.length) return (lista.innerHTML = vazio('Nenhum número encontrado'));

    lista.innerHTML = '';
    filtrados.forEach((bot) => {
      const div = document.createElement('div');
      div.className = 'item-bot-clean' + (vinculoRcs.marcados.has(bot.id) ? ' selecionado' : '');
      div.innerHTML =
        '<div class="circulo-check"><i class="bx bx-check"></i></div>' +
        '<div class="info-bot-clean"><span class="nome-bot-clean"></span>' +
        '<span class="desc-bot-clean"></span></div>' +
        (bot.conectado ? '' : '<span class="vinc-off">desconectado</span>');
      div.querySelector('.nome-bot-clean').textContent = bot.nome;
      div.querySelector('.desc-bot-clean').textContent = bot.numero
        ? formatarTelefoneBR(bot.numero)
        : '';
      div.addEventListener('click', () => {
        const tinha = vinculoRcs.marcados.has(bot.id);
        if (tinha) vinculoRcs.marcados.delete(bot.id);
        else vinculoRcs.marcados.add(bot.id);
        // Só esta linha muda: remontar a lista inteira fazia piscar.
        div.classList.toggle('selecionado', !tinha);
        const s = document.getElementById('sel-count-vinculo-rcs');
        if (s) s.textContent = `${vinculoRcs.marcados.size} selecionado(s)`;
      });
      lista.appendChild(div);
    });
  }

  async function carregarChipsDoUsuarioVinculo(usuarioId) {
    vinculoRcs.usuarioId = usuarioId || null;
    vinculoRcs.bots = [];
    vinculoRcs.marcados = new Set();
    if (!usuarioId) return desenharListaVinculoRcs();
    try {
      const r = await fetch(`/api/rcs/vinculos/${usuarioId}`, { headers: cabecalhoAuth() });
      const d = await r.json();
      vinculoRcs.bots = d.bots || [];
      vinculoRcs.marcados = new Set(vinculoRcs.bots.filter((b) => b.marcado).map((b) => b.id));
    } catch (e) {
      mostrarAlerta('Não consegui buscar os números.', 'error');
    }
    desenharListaVinculoRcs();
  }

  async function abrirVinculoDeChips() {
    const fundo = document.getElementById('fundo-modal-vinculo-rcs');
    const selUsuario = document.getElementById('select-usuario-vinculo-rcs');
    if (!fundo || !selUsuario) return;

    let usuarios = [];
    try {
      const r = await fetch('/api/rcs/vinculos', { headers: cabecalhoAuth() });
      usuarios = (await r.json()).usuarios || [];
    } catch (e) {
      return mostrarAlerta('Não consegui buscar os atendentes.', 'error');
    }
    selUsuario.innerHTML =
      '<option value="">Selecione...</option>' +
      usuarios
        .map((u) => `<option value="${u.id}">${escaparHtmlChat(u.nome)} — ${u.chips} número(s)</option>`)
        .join('');
    if (window.PcSelect && window.PcSelect.aplicar) window.PcSelect.aplicar(selUsuario);

    vinculoRcs.usuarioId = null;
    vinculoRcs.bots = [];
    vinculoRcs.marcados = new Set();
    vinculoRcs.busca = '';
    const busca = document.getElementById('busca-vinculo-rcs');
    if (busca) busca.value = '';
    desenharListaVinculoRcs();
    fundo.style.display = 'flex';
  }

  document.getElementById('fechar-vinculo-rcs')?.addEventListener('click', fecharVinculoRcs);
  document.getElementById('cancelar-vinculo-rcs')?.addEventListener('click', fecharVinculoRcs);
  document.getElementById('fundo-modal-vinculo-rcs')?.addEventListener('click', (e) => {
    if (e.target.id === 'fundo-modal-vinculo-rcs') fecharVinculoRcs();
  });
  document
    .getElementById('select-usuario-vinculo-rcs')
    ?.addEventListener('change', (e) => carregarChipsDoUsuarioVinculo(e.target.value));
  document.getElementById('busca-vinculo-rcs')?.addEventListener('input', (e) => {
    vinculoRcs.busca = e.target.value;
    desenharListaVinculoRcs();
  });
  document.getElementById('salvar-vinculo-rcs')?.addEventListener('click', async (e) => {
    if (!vinculoRcs.usuarioId) return mostrarAlerta('Escolha a atendente primeiro.', 'warning');
    const btn = e.currentTarget;
    btn.disabled = true;
    try {
      const r = await fetch(`/api/rcs/vinculos/${vinculoRcs.usuarioId}`, {
        method: 'PUT',
        headers: { ...cabecalhoAuth(), 'Content-Type': 'application/json' },
        body: JSON.stringify({ instancias: [...vinculoRcs.marcados] }),
      });
      const d = await r.json();
      if (!d.ok) throw new Error('o servidor recusou');
      mostrarAlerta(`${d.chips} número(s) vinculado(s).`, 'success');
      fecharVinculoRcs();
    } catch (err) {
      mostrarAlerta('Não deu pra salvar o vínculo.', 'error');
    } finally {
      btn.disabled = false;
    }
  });

  // O clique do botão: admin configura, vendedora puxa.
  document.getElementById('btn-puxar-rcs')?.addEventListener('click', () => {
    if (podeVincularRcs) abrirVinculoDeChips();
    else puxarClienteParaWhatsapp();
  });

  async function abrirConversa(chat, botPreferido = null) {
    // papel de parede: cada conversa abre com um fundo diferente do anterior
    sortearPapelDeParedeChat();

    if (typeof inputBuscaInterna !== 'undefined' && inputBuscaInterna) {
      inputBuscaInterna.value = '';
      if (typeof controlesBuscaNav !== 'undefined' && controlesBuscaNav)
        controlesBuscaNav.classList.add('hidden');
      elementosEncontradosBusca = [];
      resultadoAtualIndex = -1;
    }
    // Tenta abrir primeiro um canal que eu tenho permissão. Se não tiver, abre o primeiro.
    // `botPreferido` é pra quem já sabe em qual aba quer cair (o "Chamar no
    // WhatsApp" do RCS), sem abrir numa e trocar pra outra depois.
    const canalInicial =
      (botPreferido && chat.canais.find((c) => c.bot_id === botPreferido)) ||
      chat.canais.find((c) => c.has_permission) ||
      chat.canais[0];
    chatSelecionado = {
      ...chat,
      bot_id: canalInicial.bot_id,
      botStatus: canalInicial.status,
      canais: chat.canais,
    };
    const modalConsulta = document.getElementById('modal-consulta-api');
    const btnConsulta = document.getElementById('btn-abrir-consulta');
    if (modalConsulta) {
      modalConsulta.classList.add('hidden');
      if (btnConsulta) btnConsulta.classList.remove('ativo');
      const inputCpfConsulta = document.getElementById('input-cpf-consulta');
      if (inputCpfConsulta) {
        inputCpfConsulta.value = '';
        inputCpfConsulta.readOnly = true;
      }
      const btnToggleCpf = document.getElementById('btn-toggle-cpf-consulta');
      if (btnToggleCpf) btnToggleCpf.classList.remove('selecionado-azul');
      modoCpfManual = false;
      const containerMatriculas = document.getElementById(
        'container-multiplas-matriculas'
      );
      const selectMatricula = document.getElementById(
        'select-matricula-consulta'
      );
      if (containerMatriculas) containerMatriculas.classList.add('hidden');
      if (selectMatricula)
        selectMatricula.innerHTML =
          '<option value="">Selecione a matrícula...</option>';
      const camposDados = [
        'prop-nome-cliente',
        'prop-cpf-cliente',
        'prop-idade-cliente',
        'prop-orgao-cliente',
      ];
      camposDados.forEach((id) => {
        const el = document.getElementById(id);
        if (el) {
          el.textContent = '---';
          el.parentElement.removeAttribute('data-tooltip');
        }
      });
      const listaContratos = document.getElementById(
        'lista-contratos-proposta'
      );
      const areaResultados = document.getElementById(
        'resultados-consulta-siape-dinamico'
      );
      if (listaContratos) listaContratos.innerHTML = '';
      if (areaResultados) areaResultados.classList.add('hidden');
      cacheCompletoConsultaSiape = null;
    }
    const telefoneFormatado = chat.chat_id.replace('@c.us', ''); // UI Básica
    areaVazia.classList.add('escondido');
    areaAtiva.classList.remove('escondido');
    let nomeVisualHeader = chat.crm_nome || chat.name || telefoneFormatado; // Blindagem: Remove o @c.us do cabeçalho também
    if (nomeVisualHeader && nomeVisualHeader.includes('@c.us')) {
      nomeVisualHeader = nomeVisualHeader.replace('@c.us', '');
    }
    if (headerNome) headerNome.textContent = nomeVisualHeader;
    if (typeof window.atualizarVisualSLA === 'function')
      window.atualizarVisualSLA();
    atualizarEstadoAgenteIA(telefoneFormatado);
    // CRM (Dados Básicos)
    if (infoAvatarBig) {
      if (chat.profile_pic_url && chat.profile_pic_url !== 'sem_foto') {
        const nomeClienteAvatarBig = textoParaOnclick(nomeOuTelefoneCliente(chat));
        infoAvatarBig.innerHTML = `<img src="${escaparHtmlChat(chat.profile_pic_url)}" style="width: 100%; height: 100%; border-radius: 50%; object-fit: cover; cursor: pointer;" onclick="window.abrirVisualizador('${textoParaOnclick(chat.profile_pic_url)}', 'avatar', '', '', '', '', '${nomeClienteAvatarBig}'); event.stopPropagation();">`;
      } else {
        const nomeParaAvatar = chat.crm_nome || telefoneFormatado;
        infoAvatarBig.textContent = nomeParaAvatar.charAt(0).toUpperCase();
      }
    }
    if (infoNumeroWpp) infoNumeroWpp.textContent = telefoneFormatado;
    if (infoNomeLateralBig) {
      let nomeParaExibir = chat.crm_nome || chat.name || telefoneFormatado;
      if (nomeParaExibir.includes('@c.us'))
        nomeParaExibir = nomeParaExibir.replace('@c.us', '');
      infoNomeLateralBig.textContent = nomeParaExibir;
    }
    // 1. Carrega dados de texto (CPF, Email...)
    carregarDadosDoCliente(chat.chat_id);
    // 2. [CORREÇÃO] Carrega as TAGS visuais no painel CRM
    carregarTagsDoCliente(chatSelecionado);
    //  3. CARREGA A GALERIA DE MÍDIAS LATERAL
    carregarGaleriaMidias(chatSelecionado.chat_id);
    // Lista Lateral Ativa
    document
      .querySelectorAll('.chat-item')
      .forEach((el) => el.classList.remove('ativo'));
    const itemClicado = document.querySelector(
      `.chat-item[data-chat-id="${chat.chat_id}"]`
    );
    if (itemClicado) itemClicado.classList.add('ativo');
    // Sem o mapa de canais, o RCS seria desenhado como WhatsApp (ver
    // mapaCanaisPronto). Espera por ele no máximo 4s — se a rota falhar, a
    // conversa abre assim mesmo e a rede de segurança do
    // carregarFiltroBotsComStatus corrige quando o mapa chegar.
    if (!mapaCanaisCarregado) {
      const esteChat = chatSelecionado;
      await Promise.race([
        mapaCanaisPronto,
        new Promise((r) => setTimeout(r, 4000)),
      ]);
      // Clicou em outra conversa durante a espera: esta abertura perdeu a vez.
      if (chatSelecionado !== esteChat) return;
    }
    // DESENHA AS ABAS (forçado: trocou de conversa, nada do cabeçalho antigo
    // pode sobreviver — nem um menu "+N" que tenha ficado aberto)
    renderizarAbas(chat.canais, true);
    // CARREGA O CANAL
    window.atualizarVisualizacaoFunil(chat);
    await trocarCanal(canalInicial.bot_id, false);
    atualizarBotaoLer(chat);
    document.querySelectorAll('.segmento-status').forEach((seg) => {
      seg.classList.remove('ativo');
      // Assumimos 'aberto' se o status for nulo (lead novo)
      if (seg.dataset.status === (chat.status_lead || 'aberto')) {
        seg.classList.add('ativo');
      }
    });
    if (selectResponsavel) {
      const canalAtual =
        chat.canais.find((c) => c.bot_id === chat.bot_id) || chat.canais[0];
      selectResponsavel.value = canalAtual.responsavel_id || '';
      if (typeof sincronizarTriggerResponsavel === 'function')
        sincronizarTriggerResponsavel();
    }
  }
  // --- Abas de canal do cabeçalho (o bot da conversa + o menu "+N") ---------
  //
  // Este cabeçalho é redesenhado do zero (`innerHTML = ''`) a cada
  // `status_update`/`bot_status`, e esses eventos chegam com a LISTA INTEIRA de
  // bots a cada piscada de chip na Evolution — num dia de chip instável o log
  // registrou 16 mil. O resultado era o botão "+N" morrendo embaixo do cursor:
  // o :hover piscava e o dropdown aberto sumia no meio do clique.
  //
  // Duas travas resolvem isso sem mexer no servidor:
  //   1. assinatura do que já está na tela — evento repetido não redesenha nada;
  //   2. redraw adiado enquanto o menu está aberto — ele só se refaz quando o
  //      menu fecha, então nunca desaparece na mão de quem está escolhendo.
  let assinaturaAbas = null;
  let abasPendentes = null;

  function menuCanaisAberto() {
    return !!document.querySelector('.chat-dropdown-menu.show');
  }

  // Fecha o menu "+N". `redesenharPendente` aplica o redraw que ficou na espera
  // — quem já vai redesenhar de qualquer jeito (trocar de canal) passa false.
  function fecharMenuCanais(redesenharPendente = true) {
    document
      .querySelectorAll('.chat-dropdown-menu.show')
      .forEach((el) => el.classList.remove('show'));
    const pendente = abasPendentes;
    abasPendentes = null;
    if (
      redesenharPendente &&
      pendente &&
      chatSelecionado &&
      chatSelecionado.canais === pendente
    ) {
      renderizarAbas(pendente, true);
    }
  }

  // Tudo o que aparece no cabeçalho. Se isso não mudou, não há o que redesenhar.
  function assinaturaDosCanais(canais, botAtivoId) {
    return JSON.stringify([
      chatSelecionado ? chatSelecionado.chat_id : null,
      botAtivoId,
      canais.map((c) => [
        c.bot_id,
        // O canal entra na assinatura: sem ele, o cabeçalho desenhado com o
        // selo errado nunca era refeito pelos avisos de status.
        canalDoBot(c.bot_id),
        nomeDoCanal(c),
        c.status,
        c.unread || 0,
        c.has_permission !== false,
      ]),
    ]);
  }

  function renderizarAbas(canais, forcar = false) {
    const container = document.getElementById('chat-tabs-container');
    if (!container) return;
    if (!chatSelecionado || !canais || !canais.length) {
      container.innerHTML = '';
      assinaturaAbas = null;
      abasPendentes = null;
      return;
    }
    // 1. Separa o canal ATIVO dos demais
    // Se chatSelecionado não tiver bot_id (primeiro load), pega o primeiro canal
    const botAtivoId = chatSelecionado.bot_id || canais[0].bot_id;
    const assinatura = assinaturaDosCanais(canais, botAtivoId);
    if (
      !forcar &&
      container.childElementCount > 0 &&
      assinatura === assinaturaAbas
    )
      return;
    if (!forcar && menuCanaisAberto()) {
      // Guarda a lista e sai: o menu está aberto, quem redesenha é o fechamento.
      abasPendentes = canais;
      return;
    }
    abasPendentes = null;
    assinaturaAbas = assinatura;
    container.innerHTML = '';
    const canalAtivo = canais.find((c) => c.bot_id === botAtivoId) || canais[0];
    const outrosCanais = canais.filter((c) => c.bot_id !== botAtivoId);
    // ---------------------------------------------------------
    // A. RENDERIZA O BOTÃO GRANDE (CANAL ATIVO)
    // ---------------------------------------------------------
    const btnAtivo = document.createElement('button');
    btnAtivo.className = 'chat-tab ativo'; // Sempre ativo visualmente
    // Icone de cadeado se sem permissão
    const iconLock =
      canalAtivo.has_permission === false
        ? '<i class="fas fa-lock" style="font-size:10px; margin-left:4px; opacity:0.7"></i>'
        : '';
    btnAtivo.innerHTML = `
        <span class="tab-status-dot ${canalAtivo.status}"></span>
        ${seloDoCanal(canalAtivo.bot_id)}
        ${nomeDoCanal(canalAtivo)} ${iconLock}
        ${canalAtivo.unread > 0 ? `<span class="tab-badge">${canalAtivo.unread}</span>` : ''}
    `;
    // Clique no ativo (pode servir para recarregar ou abrir menu de opções do bot futuramente)
    btnAtivo.onclick = () => trocarCanal(canalAtivo.bot_id);
    container.appendChild(btnAtivo);
    // ... (Parte A da função continua igual) ...
    // ---------------------------------------------------------
    // B. LÓGICA DE AGRUPAMENTO (DROPDOWN) - ATUALIZADA
    // ---------------------------------------------------------
    if (outrosCanais.length > 0) {
      const wrapper = document.createElement('div');
      wrapper.className = 'chat-dropdown-wrapper';
      // Calcula total de não lidos escondidos
      const totalUnreadHidden = outrosCanais.reduce(
        (acc, curr) => acc + (curr.unread || 0),
        0
      );
      // Texto do botão: Apenas "+X" (ex: "+2")
      const labelBtn = `+${outrosCanais.length}`;
      // HTML da Bolinha Flutuante (Só aparece se tiver > 0)
      const badgeHtml =
        totalUnreadHidden > 0
          ? `<span class="more-badge-float">${totalUnreadHidden}</span>`
          : '';
      const btnMore = document.createElement('button');
      // Removemos a classe 'has-unread' da lógica de cor, mantemos apenas por segurança se precisar
      btnMore.className = `btn-more-channels`;
      // Injeta o texto + a bolinha
      btnMore.innerHTML = `${labelBtn} ${badgeHtml}`;
      // O Menu Dropdown (MANTÉM IGUAL AO ANTERIOR)
      const dropdownMenu = document.createElement('div');
      dropdownMenu.className = 'chat-dropdown-menu';
      outrosCanais.forEach((canal) => {
        const item = document.createElement('button');
        item.className = 'dropdown-item-channel';
        const lockItem =
          canal.has_permission === false
            ? '<i class="fas fa-lock" style="font-size:10px; margin-left:4px; color:#777"></i>'
            : '';
        item.innerHTML = `
                <div class="dd-bot-info">
                    <span class="dd-status-dot ${canal.status}"></span>
                    ${seloDoCanal(canal.bot_id)}
                    <span>${nomeDoCanal(canal)} ${lockItem}</span>
                </div>
                ${canal.unread > 0 ? `<span class="dd-badge">${canal.unread}</span>` : ''}
            `;
        item.onclick = (e) => {
          e.stopPropagation();
          // `trocarCanal` já redesenha as abas (o bot ativo mudou), então o
          // redraw pendente daqui não interessa mais.
          fecharMenuCanais(false);
          trocarCanal(canal.bot_id);
        };
        dropdownMenu.appendChild(item);
      });
      btnMore.onclick = (e) => {
        e.stopPropagation();
        const vaiAbrir = !dropdownMenu.classList.contains('show');
        // Ao ABRIR não se aplica o redraw pendente: ele destruiria este menu no
        // mesmo clique. Ao fechar, sim — é a hora de pôr a tela em dia.
        fecharMenuCanais(!vaiAbrir);
        if (vaiAbrir) dropdownMenu.classList.add('show');
      };
      wrapper.appendChild(btnMore);
      wrapper.appendChild(dropdownMenu);
      container.appendChild(wrapper);
    }
  }
  // Fecha o menu "+N" ao clicar fora dele (e aplica o redraw que ficou na fila).
  document.addEventListener('click', (e) => {
    const abertos = document.querySelectorAll('.chat-dropdown-menu.show');
    if (!abertos.length) return;
    const clicouDentro = Array.from(abertos).some((dd) =>
      dd.parentElement.contains(e.target)
    );
    if (!clicouDentro) fecharMenuCanais(true);
  });
  document.addEventListener('keydown', (e) => {
    if (e.key === 'Escape' && menuCanaisAberto()) fecharMenuCanais(true);
  });
  // ============================================================
  // 5. TROCAR DE CANAL (COM MODO ESPIÃO E VALIDAÇÃO BLINDADA)
  // ============================================================
  async function trocarCanal(
    botId,
    redesenharAbas = true,
    isSocketRefresh = false
  ) {
    const canalAlvo = chatSelecionado.canais.find((c) => c.bot_id === botId);
    if (!canalAlvo) return;
    chatSelecionado.bot_id = botId;
    chatSelecionado.botStatus = canalAlvo.status;
    if (redesenharAbas) renderizarAbas(chatSelecionado.canais);
    // O que a tela permite depende do canal: o RCS não tem áudio nem
    // figurinha, e tem janela de 24h com custo.
    aplicarRestricoesDoCanal(botId);
    if (ehCanalRcs(botId)) largarRespostaOuEdicaoPendente();
    // Botões de mensagem pronta escolhidos em outra conversa (ou outro canal)
    // não podem ir parar aqui.
    if (!isSocketRefresh) largarBotoesPendentes();
    if (!isSocketRefresh) largarLocalizacaoPendente();
    atualizarAvisoJanelaRcs(botId, chatSelecionado.chat_id);
    atualizarBotaoPuxarRcs(botId);
    // --- LÓGICA DE BLOQUEIO DE INPUT (OFFLINE OU SEM PERMISSÃO) ---
    const inputArea = document.querySelector('.chat-input-area');
    const avisoOffline = document.getElementById('aviso-bot-offline');
    // Verifica se já existe aviso de restrição, se não cria
    let avisoRestrito = document.getElementById('aviso-acesso-restrito');
    if (!avisoRestrito) {
      avisoRestrito = document.createElement('div');
      avisoRestrito.id = 'aviso-acesso-restrito';
      avisoRestrito.className = 'aviso-restrito hidden';
      avisoRestrito.innerHTML =
        '<i class="fas fa-lock"></i> <span>Visualização restrita. Você não participa deste canal.</span>';
      inputArea.parentNode.insertBefore(avisoRestrito, inputArea.nextSibling);
    }
    const container = document.getElementById('container-mensagens');
    // 1. Verifica Permissão
    if (canalAlvo.has_permission === false) {
      inputArea.classList.add('bloqueado-permissao');
      avisoRestrito.classList.remove('hidden');
      avisoOffline.classList.add('hidden');
      container.classList.add('modo-blur');
    } else {
      container.classList.remove('modo-blur');
      avisoRestrito.classList.add('hidden');
      inputArea.classList.remove('bloqueado-permissao');
      // 2. Verifica Status do Bot (BLINDAGEM INTELIGENTE)
      const status = String(canalAlvo.status).toLowerCase();
      // O sistema agora aceita qualquer um desses nomes para liberar a tela
      const isOnline =
        status === 'online' ||
        status === 'ready' ||
        status === 'authenticated' ||
        status === 'connected';
      if (!isOnline) {
        inputArea.classList.add('hidden');
        avisoOffline.classList.remove('hidden');
      } else {
        inputArea.classList.remove('hidden');
        avisoOffline.classList.add('hidden');
      }
    }
    // Se for apenas uma atualização em tempo real do Socket, termina aqui (não recarrega mensagens pra tela não piscar)
    if (isSocketRefresh) return;
    // Limpa e Carrega Histórico
    containerMsgs.innerHTML =
      '<div style="text-align:center; padding:20px; color:#999;"><i class="fas fa-spinner fa-spin"></i> Carregando histórico...</div>';
    try {
      // A equipe (pra foto de quem mandou cada balão) vem junto com o
      // histórico, não depois: na prática só a 1ª conversa espera por ela.
      const [res] = await Promise.all([
        fetch(
          `/api/chat/messages?chatId=${chatSelecionado.chat_id}&botId=${botId}`,
          {
            headers: {
              Authorization: `Bearer ${localStorage.getItem('token')}`,
            },
          }
        ),
        prepararAvatarsEquipe().catch(() => {}),
      ]);
      const data = await res.json();
      if (data.success) {
        renderizarMensagens(data.messages);
        // APLICA O BLUR NAS MENSAGENS INDIVIDUAIS SE NÃO TIVER PERMISSÃO
        if (canalAlvo.has_permission === false) {
          const linhas = containerMsgs.querySelectorAll('.msg-row');
          linhas.forEach((row) => row.classList.add('msg-blur'));
        }
      }
    } catch (erro) {
      containerMsgs.innerHTML =
        '<div style="text-align:center; padding:50px; color:#999;"><i class="fas fa-lock" style="font-size:40px; margin-bottom:10px;"></i><br>Conteúdo protegido.</div>';
    }
  }
  function renderizarMensagens(msgs, append = false) {
    if (!append) {
      containerMsgs.innerHTML = '';
    }
    let ultimaDataRenderizada = null;
    let caixaDiaAtual = null;
    // Quem falou no balão anterior (ver avatarDoRemetente): a foto só entra
    // quando muda. Dia novo, linha do sistema e lembrete zeram a sequência.
    let autorAnterior = null;
    // Quem falou (pro NOME em cima do balão). Igual à foto, exceto robôs:
    // Sistema e Automação dividem a foto, mas cada um mostra o próprio nome.
    let pessoaAnterior = null;
    // Balão repetido: todo `data-wpp-id` da página nasce DENTRO da conversa
    // (status da enviada, transcrição do áudio, lembrete). Numa abertura
    // completa a conversa acabou de ser limpa, então basta lembrar o que
    // este desenho já pôs. Antes era um querySelector no documento inteiro a
    // cada balão, que cresce ao quadrado: na conversa de 751 mensagens era
    // um dos dois maiores custos de abrir. Chegando pelo socket (append) é
    // uma mensagem só e a varredura continua a de sempre.
    const idsDesteDesenho = append ? null : new Set();
    const jaNaTela = (id) =>
      idsDesteDesenho
        ? idsDesteDesenho.has(String(id))
        : !!document.querySelector(`[data-wpp-id="${id}"]`);
    if (append) {
      const headersNaTela = containerMsgs.querySelectorAll(
        '.date-header-bubble'
      );
      if (headersNaTela.length > 0) {
        ultimaDataRenderizada =
          headersNaTela[headersNaTela.length - 1].innerText.trim();
        const caixasDia = containerMsgs.querySelectorAll(
          '.dia-conversa-wrapper'
        );
        if (caixasDia.length > 0) {
          caixaDiaAtual = caixasDia[caixasDia.length - 1];
          const ultimaLinha = caixaDiaAtual.lastElementChild;
          if (ultimaLinha && ultimaLinha.dataset.autor)
            autorAnterior = ultimaLinha.dataset.autor;
          if (ultimaLinha && ultimaLinha.dataset.pessoa)
            pessoaAnterior = ultimaLinha.dataset.pessoa;
        }
      }
    }
    msgs.forEach((msg) => {
      let transcriptionHtml = '';
      let conteudoHTML = '';
      let classeMedia = '';
      let dataObj;
      if (typeof msg.timestamp === 'string' && msg.timestamp.includes('T')) {
        dataObj = new Date(msg.timestamp);
      } else {
        let ts = parseInt(msg.timestamp);
        if (ts.toString().length === 10) ts *= 1000;
        dataObj = new Date(ts);
      }
      if (isNaN(dataObj.getTime())) dataObj = new Date();
      const textoDataAtual = formatarDataAmigavel(dataObj);
      if (textoDataAtual !== ultimaDataRenderizada) {
        caixaDiaAtual = document.createElement('div');
        caixaDiaAtual.className = 'dia-conversa-wrapper';
        const divHeader = document.createElement('div');
        divHeader.className = 'date-header-container';
        divHeader.innerHTML = `<span class="date-header-bubble">${textoDataAtual}</span>`;
        caixaDiaAtual.appendChild(divHeader);
        containerMsgs.appendChild(caixaDiaAtual);
        ultimaDataRenderizada = textoDataAtual;
        autorAnterior = null;
        pessoaAnterior = null;
      }
      const containerAlvo = caixaDiaAtual || containerMsgs;

      // ⏰ CARD DE LEMBRETE. Tem que vir ANTES do bloco de sistema logo abaixo:
      // aquele bloco decide pelo TEXTO da mensagem ("moveu o cliente",
      // "transferiu este contato"...), então um lembrete escrito com uma dessas
      // frases seria desenhado como linha do tempo do sistema.
      if (msg.type === 'lembrete') {
        const idLembreteMsg = msg.wpp_id || msg.id;
        if (jaNaTela(idLembreteMsg)) return;
        if (idsDesteDesenho) idsDesteDesenho.add(String(idLembreteMsg));
        const rowLembrete = document.createElement('div');
        rowLembrete.className = 'msg-row enviada lembrete-row';
        rowLembrete.dataset.wppId = idLembreteMsg;
        rowLembrete.innerHTML = montarCardLembrete(msg, dataObj);
        containerAlvo.appendChild(rowLembrete);
        autorAnterior = null;
        pessoaAnterior = null;
        return;
      }

      // 📞 LIGAÇÃO feita pelo telefone do CRM (js/telefone.js): cartão
      // próprio, no molde do lembrete (neutro, listra de cor, largura fixa,
      // sem tiques), com a gravação dentro.
      if (msg.type === 'ligacao') {
        const idLigacaoMsg = msg.wpp_id || msg.id;
        if (jaNaTela(idLigacaoMsg)) return;
        if (idsDesteDesenho) idsDesteDesenho.add(String(idLigacaoMsg));
        const rowLigacao = document.createElement('div');
        rowLigacao.className = 'msg-row enviada ligacao-row';
        rowLigacao.dataset.wppId = idLigacaoMsg;
        rowLigacao.innerHTML = montarCardLigacao(msg, dataObj);
        containerAlvo.appendChild(rowLigacao);
        autorAnterior = null;
        pessoaAnterior = null;
        return;
      }

      const frasesSistema = [
        'adicionou a etiqueta',
        'removeu a etiqueta',
        'atualizou o cadastro',
        'importou dados',
        'transferiu este contato',
        'moveu o cliente',
        'criou este contato',
        'processou este contato',
        'falhou ao extrair',
        'alterou o status',
      ];
      const textoMsg = msg.body ? msg.body.toLowerCase() : '';
      // Frase de sistema só conta em mensagem NOSSA: o cliente que escreve
      // "alterou o status" não vira linha do tempo.
      const ehSistema =
        (msg.is_from_me && frasesSistema.some((frase) => textoMsg.includes(frase))) ||
        msg.type === 'notification_template';
      if (ehSistema) {
        autorAnterior = null;
        pessoaAnterior = null;
        let icone = 'bx bx-bell';
        if (textoMsg.includes('etiqueta')) icone = 'bx bx-tag';
        else if (
          textoMsg.includes('atualizou') ||
          textoMsg.includes('importou dados')
        )
          icone = 'bx bx-edit';
        else if (textoMsg.includes('criou este contato'))
          icone = 'bx bx-user-plus';
        else if (textoMsg.includes('moveu') || textoMsg.includes('transferiu'))
          icone = 'bx bx-transfer-alt';
        else if (textoMsg.includes('processou este contato'))
          icone = 'bx bx-rocket';
        else if (textoMsg.includes('falhou ao extrair')) icone = 'bx bx-bot';
        // 👇 NOVO ÍCONE DE STATUS 👇
        else if (
          textoMsg.includes('alterou o status') ||
          textoMsg.includes('marcou este lead')
        )
          icone = 'bx bx-check-shield';
        // Eventos que os agentes (de IA e externo) passaram a registrar aqui.
        // Sem uma regra própria, todos caíam no sininho genérico e a linha do
        // tempo virava uma coluna de ícones iguais — que é o mesmo que não ter
        // ícone nenhum pra quem lê correndo.
        else if (
          textoMsg.includes('atribuiu esta conversa') ||
          textoMsg.includes('distribuiu o cliente')
        )
          icone = 'bx bx-user-check';
        else if (textoMsg.includes('encerrou este atendimento'))
          icone = 'bx bx-log-out-circle';
        else if (textoMsg.includes('retomou o atendimento'))
          icone = 'bx bx-bot';
        else if (textoMsg.includes('iniciou a automação'))
          icone = 'bx bx-play-circle';
        else if (textoMsg.includes('avisou a equipe'))
          icone = 'bx bx-megaphone';
        else if (textoMsg.includes('chamou a integração')) icone = 'bx bx-plug';
        const dataStr = FORMATO_DATA_SISTEMA_MSG.format(dataObj);
        const horaStr = FORMATO_HORA_SISTEMA_MSG.format(dataObj);
        const textoMostrar = htmlLimpoDoSistema(msg.body || 'Notificação de Sistema / Template');
        const itemHtml = `
            <div class="icone-timeline"><i class="${icone}"></i></div>
            <div class="texto-timeline">${textoMostrar}</div>
            <div class="hora-auditoria">
                <span>${dataStr}</span>
                <span>${horaStr}</span>
            </div>
        `;
        const todasAsMensagens = containerAlvo.querySelectorAll('.msg-row');
        const lastNode =
          todasAsMensagens.length > 0
            ? todasAsMensagens[todasAsMensagens.length - 1]
            : null;
        if (lastNode && lastNode.classList.contains('sistema-group')) {
          const grupoItens = lastNode.querySelector('.grupo-itens');
          const headerTexto = lastNode.querySelector('.header-texto-qtd');
          const novoItem = document.createElement('div');
          novoItem.className = 'linha-tempo-sistema grouped-item';
          novoItem.innerHTML = itemHtml;
          grupoItens.appendChild(novoItem);
          const qtd = grupoItens.children.length;
          headerTexto.innerHTML = `<strong>${qtd} atualizações</strong> do sistema`;
          return;
        } else if (lastNode && lastNode.classList.contains('sistema-single')) {
          const itemAnteriorHtml = lastNode.querySelector(
            '.linha-tempo-sistema'
          ).innerHTML;
          const rowGroup = document.createElement('div');
          rowGroup.className = 'msg-row sistema sistema-group';
          rowGroup.innerHTML = `
              <div class="linha-tempo-grupo">
                  <div class="grupo-header" onclick="this.parentElement.classList.toggle('expandido')">
                      <div class="grupo-header-left">
                          <i class="bx bx-list-ul"></i>
                          <span class="header-texto-qtd"><strong>2 atualizações</strong> do sistema</span>
                      </div>
                      <i class="bx bx-chevron-down seta-grupo"></i>
                  </div>
                  <div class="grupo-itens">
                      <div class="linha-tempo-sistema grouped-item">${itemAnteriorHtml}</div>
                      <div class="linha-tempo-sistema grouped-item">${itemHtml}</div>
                  </div>
              </div>
          `;
          lastNode.replaceWith(rowGroup);
          return;
        } else {
          const row = document.createElement('div');
          row.className = 'msg-row sistema sistema-single';
          row.innerHTML = `<div class="linha-tempo-sistema">${itemHtml}</div>`;
          containerAlvo.appendChild(row);
          return;
        }
      }
      // Mídia gravada sem arquivo: a Evolution não conseguiu baixar do
      // WhatsApp. O servidor tenta de novo por alguns minutos; enquanto isso
      // a bolha diz "baixando", e só depois de desistir (midia_falhou) diz que
      // não deu. Sem isso ela aparecia como texto comum, ou nem aparecia.
      const midiaSemArquivo =
        !msg.media_path &&
        ['image', 'video', 'ptt', 'audio', 'document', 'sticker'].includes(msg.type);

      if (!msg.body && !msg.media_path && !midiaSemArquivo) return;

      if (msg.anuncio) {
        containerAlvo.appendChild(montarBalaoAnuncio(msg.anuncio));
        autorAnterior = null;
        pessoaAnterior = null;
      }

      const row = document.createElement('div');
      row.className = `msg-row ${msg.is_from_me ? 'enviada' : 'recebida'}`;
      if (msg.type === 'comentario') {
        row.classList.add('comentario');
      }

      let remetenteMidia = '';
      if (msg.is_from_me) {
        remetenteMidia = msg.sent_by_name || 'Equipe';
        if (remetenteMidia.toUpperCase().startsWith('FLUXO:'))
          remetenteMidia = 'Sistema';
      } else {
        let nomeCrm = chatSelecionado.crm_nome || chatSelecionado.name || '';
        let telefone = chatSelecionado.chat_id.replace(/\D/g, '');
        if (!nomeCrm || nomeCrm === telefone || nomeCrm.includes('@c.us')) {
          remetenteMidia = formatarTelefoneBR(telefone);
        } else {
          const partes = nomeCrm.trim().split(' ');
          remetenteMidia =
            partes.length > 1 ? `${partes[0]} ${partes[1]}` : partes[0];
        }
      }
      const safeRemetente = textoParaOnclick(remetenteMidia);

      const horaVisualMidia = FORMATO_HORA_MSG.format(dataObj);
      const dataVisualMidia = FORMATO_DATA_MSG.format(dataObj);
      const stringDataFinal = `${dataVisualMidia} - ${horaVisualMidia}`;

      let midiaProcessada = false;
      if (msg.media_path) {
        const extensao = msg.media_path.split('.').pop().toLowerCase();
        if (msg.type === 'image') {
          classeMedia = 'has-media';
          // Baixar sem abrir o visualizador: o mesmo botão do cartão de PDF.
          // O nome sai do arquivo, sem o prefixo interno (env_/rec_/camp_).
          const nomeImagem = msg.media_path
            .split('/')
            .pop()
            .replace(/^env_\d+_/i, '')
            .replace(/^rcs_\d+_/i, '')
            .replace(/^camp_\d+_[a-z0-9]+_/i, '')
            .replace(/^rec_\d+_[a-z0-9]+_/i, '')
            .replace(/'/g, '');
          conteudoHTML += `
              <div class="media-wrapper media-wrapper-imagem" onclick="abrirVisualizador('${textoParaOnclick(msg.media_path)}', 'imagem', '', '${stringDataFinal}', '', 'MÍDIA', '${safeRemetente}')" style="cursor: pointer;">
                  <img src="${escaparHtmlChat(msg.media_path)}" class="chat-media-img">
                  <a href="#" onclick="window.forceDownload('${textoParaOnclick(msg.media_path)}', '${textoParaOnclick(nomeImagem)}'); event.stopPropagation(); event.preventDefault();" class="exclusive-download-btn btn-baixar-imagem" title="Baixar imagem">
                      <i class="fas fa-arrow-down"></i>
                  </a>
              </div>`;

          midiaProcessada = true;
        } else if (msg.type === 'video' && msg.detalhe && msg.detalhe.recado) {
          // Vídeo-recado (o redondo do WhatsApp): sem balão, como a figurinha.
          classeMedia = 'has-media has-sticker has-recado';
          conteudoHTML += `
              <div class="recado-wrapper" onclick="abrirVisualizador('${textoParaOnclick(msg.media_path)}', 'video', '', '${stringDataFinal}', '', 'MÍDIA', '${safeRemetente}')" title="Vídeo-recado">
                  <video src="${escaparHtmlChat(msg.media_path)}" class="chat-media-recado" preload="metadata" muted playsinline
                      onloadedmetadata="window.mostrarDuracaoRecado && window.mostrarDuracaoRecado(this)"></video>
                  <span class="recado-play"><i class="fas fa-play"></i></span>
                  <span class="recado-tempo" hidden></span>
              </div>`;
          midiaProcessada = true;
        } else if (msg.type === 'video') {
          classeMedia = 'has-media';
          conteudoHTML += `
              <div class="media-wrapper" onclick="abrirVisualizador('${textoParaOnclick(msg.media_path)}', 'video', '', '${stringDataFinal}', '', 'MÍDIA', '${safeRemetente}')" style="cursor: pointer; position: relative;">


                  <video src="${escaparHtmlChat(msg.media_path)}" class="chat-media-video"></video>
                  <div style="position:absolute; top:50%; left:50%; transform:translate(-50%, -50%); background:rgba(0,0,0,0.5); border-radius:50%; width:40px; height:40px; display:flex; align-items:center; justify-content:center;">
                     <i class="fas fa-play" style="color:white; font-size:18px;"></i>
                  </div>
              </div>`;
          midiaProcessada = true;
        } else if (msg.type === 'ptt' || msg.type === 'audio') {
          const audioId =
            msg.wpp_id ||
            msg.id ||
            'audio_' + Math.random().toString(36).substr(2, 9);
          conteudoHTML += gerarHTMLAudioPlayer(
            escaparHtmlChat(msg.media_path),
            String(audioId).replace(/[^\w-]/g, '_')
          );
          // Botão só pra quem pode transcrever (empresa com o recurso e
          // permissão "Transcrever áudio"); a que já existe sempre aparece.
          if ((msg.type === 'ptt' || msg.type === 'audio') && (msg.transcription || Acesso.pode('chat.transcrever'))) {
            const jaTemTranscricao = !!msg.transcription;
            // Áudio ENVIADO por nós não vem com transcrição automática (só
            // gera sob demanda ao clicar, pra não gastar token à toa); o
            // RECEBIDO do cliente já chega transcrito.
            const textoFinal = jaTemTranscricao
              ? escaparHtmlChat(msg.transcription)
              : msg.is_from_me
                ? ''
                : '<em>(Transcrição não disponível ou processando...)</em>';
            const textoBotaoInicial = jaTemTranscricao
              ? 'Ver transcrição'
              : 'Transcrever áudio';
            transcriptionHtml = `
              <div class="transcription-container-outside" data-wpp-id="${msg.wpp_id || ''}" data-has-transcription="${jaTemTranscricao ? '1' : '0'}">

                  <div class="transcription-text-box hidden">${textoFinal}</div>
                  <button class="btn-toggle-transcription" onclick="toggleTranscription(this, event)">
                      <i class="ph-bold ph-magic-wand"></i> <span>${textoBotaoInicial}</span>
                  </button>
              </div>`;
          }
          midiaProcessada = true;
        } else if (msg.type === 'sticker' && /\.json$/i.test(msg.media_path)) {
          // Figurinha animada: o servidor guardou a animação (Lottie); o
          // js/figurinha-animada.js desenha um quadro dela. Sem estrela de
          // favoritar: o reenvio de figurinha só sabe mandar imagem.
          classeMedia = 'has-media has-sticker';
          conteudoHTML += `
              <div class="media-wrapper sticker-wrapper">
                  <div class="chat-media-sticker figurinha-animada" data-animacao="${escaparHtmlChat(msg.media_path)}" role="img" aria-label="Figurinha animada"></div>
              </div>`;
          midiaProcessada = true;
        } else if (msg.type === 'sticker') {
          classeMedia = 'has-media has-sticker';
          const isFav =
            window.figurinhasSalvas &&
            window.figurinhasSalvas.includes(msg.media_path);
          const classeFav = isFav ? 'favoritado' : '';
          const iconeFav = isFav ? 'bxs-star' : 'bx-star';
          // 👇 O PLANO B: Se a Evolution não mandou a imagem, coloca um ícone de erro provisório
          const imgSrc =
            msg.media_path ||
            'https://cdn-icons-png.flaticon.com/512/107/107817.png';
          const opacidade = !msg.media_path
            ? 'filter: grayscale(1); opacity: 0.3;'
            : '';
          conteudoHTML += `
              <div class="media-wrapper sticker-wrapper" style="cursor: pointer; overflow: visible; position: relative; display: inline-block;">
                  <img src="${escaparHtmlChat(imgSrc)}" class="chat-media-sticker" loading="lazy" style="${opacidade}">
                  <button class="btn-fav-sticker-corner ${classeFav}" onclick="window.toggleFavoritoSticker('${textoParaOnclick(msg.media_path)}', this, event)" title="Favoritar Figurinha">
                      <i class="bx ${iconeFav}"></i>
                  </button>
              </div>`;
          midiaProcessada = true;
        }
        if (!midiaProcessada) {
          const infoArquivo = getIconeArquivo(extensao);
          let nomeParaMostrar = 'Anexo';
          if (msg.body && msg.body.length < 50 && msg.body.includes('.')) {
            nomeParaMostrar = msg.body;
          } else {
            nomeParaMostrar = msg.media_path.split('/').pop();
          }
          nomeParaMostrar = nomeParaMostrar
            .replace(/^env_\d+_/i, '')
            .replace(/^rcs_\d+_/i, '')
            .replace(/^camp_\d+_[a-z0-9]+_/i, '')
            .replace(/^rec_\d+_[a-z0-9]+_/i, '');

          if (msg.thumbnail_url) {
            classeMedia = 'has-media';
            let lastDotIndex = nomeParaMostrar.lastIndexOf('.');
            let nomeVisual =
              lastDotIndex !== -1
                ? nomeParaMostrar.substring(0, lastDotIndex)
                : nomeParaMostrar;
            let tipoVisualizacao = extensao === 'pdf' ? 'pdf' : 'imagem';
            const nomeVisualJs = textoParaOnclick(nomeVisual);
            nomeVisual = escaparHtmlChat(nomeVisual);
            conteudoHTML += `
                <div class="media-wrapper exclusive-preview" onclick="abrirVisualizador('${textoParaOnclick(msg.media_path)}', '${tipoVisualizacao}', '${nomeVisualJs}', '${stringDataFinal}', '${infoArquivo.cor}', '${textoParaOnclick(extensao)}', '${safeRemetente}')" style="cursor: pointer; position: relative;">













                    <img src="${escaparHtmlChat(msg.thumbnail_url)}" alt="Preview Documento" class="file-preview-img" onerror="this.parentElement.style.display='none'">
                    <div class="exclusive-top-overlay">
                        <span class="exclusive-file-name" title="${nomeVisual}">${nomeVisual}</span>
                    </div>
                    <div class="exclusive-bottom-overlay">
                        <span class="exclusive-file-badge" style="background-color: ${infoArquivo.cor};">${escaparHtmlChat(extensao.toUpperCase())}</span>
                        <a href="#" onclick="window.forceDownload('${textoParaOnclick(msg.media_path)}', '${textoParaOnclick(nomeParaMostrar)}'); event.stopPropagation(); event.preventDefault();" class="exclusive-download-btn" title="Baixar">
                            <i class="fas fa-arrow-down"></i>
                        </a>
                    </div>
                </div>`;
          } else {
            // Se não for PDF ou não tiver miniatura, desenha o cartão original intocável!
            conteudoHTML += `
                <a href="#" onclick="window.forceDownload('${textoParaOnclick(msg.media_path)}', '${textoParaOnclick(nomeParaMostrar)}'); event.preventDefault();" class="nexus-file-card" title="${escaparHtmlChat(nomeParaMostrar)}">
                    <div class="file-icon-box" style="background-color: ${infoArquivo.cor};"><i class="${infoArquivo.icone}"></i></div>
                    <div class="file-info">
                       <span class="file-name">${escaparHtmlChat(nomeParaMostrar)}</span>
                       <span class="file-ext">${escaparHtmlChat(extensao.toUpperCase())}</span>
                    </div>
                    <div class="file-download-icon"><i class="fas fa-arrow-down"></i></div>
                </a>`;
          }
        }
      }
      // O nome do arquivo que o WhatsApp põe sozinho como legenda do documento
      // vai pra dentro do aviso, e não numa bolha de texto solta embaixo.
      let nomeArquivoSemMidia = '';
      if (midiaSemArquivo) {
        if (
          msg.type === 'document' &&
          msg.body &&
          msg.body.length < 120 &&
          /\.[a-z0-9]{2,5}$/i.test(msg.body.trim())
        ) {
          nomeArquivoSemMidia = msg.body.trim();
        }
        // Pendente há mais de 15 min = as tentativas do servidor foram
        // interrompidas (ex: restart) sem chegar a marcar a falha.
        const falhou =
          msg.midia_falhou || Date.now() - dataObj.getTime() > 15 * 60 * 1000;
        const textos = {
          image: ['Foto não pôde ser baixada', 'Baixando foto…'],
          video: ['Vídeo não pôde ser baixado', 'Baixando vídeo…'],
          ptt: ['Áudio não pôde ser baixado', 'Baixando áudio…'],
          audio: ['Áudio não pôde ser baixado', 'Baixando áudio…'],
          document: ['Documento não pôde ser baixado', 'Baixando documento…'],
          sticker: ['Figurinha não pôde ser baixada', 'Baixando figurinha…'],
        }[msg.type];
        // Quem entrega o arquivo é o canal da conversa (WhatsApp ou RCS).
        const quemEntrega = ehCanalRcs(msg.bot_id) ? 'O RCS' : 'O WhatsApp';
        const subtitulo = falhou
          ? msg.is_from_me
            ? `${quemEntrega} não entregou o arquivo ao sistema`
            : 'Peça para o cliente enviar de novo'
          : `${quemEntrega} demorou a entregar o arquivo`;
        conteudoHTML += `
            <div class="midia-indisponivel ${falhou ? 'falhou' : 'baixando'}" data-midia-wpp-id="${escaparHtmlChat(msg.wpp_id || '')}">
                <div class="midia-indisponivel-icone"><i class="ph-bold ${falhou ? 'ph-warning-circle' : 'ph-circle-notch'}"></i></div>
                <div class="midia-indisponivel-info">
                    <span class="midia-indisponivel-titulo">${falhou ? textos[0] : textos[1]}</span>
                    ${nomeArquivoSemMidia ? `<span class="midia-indisponivel-nome" title="${escaparHtmlChat(nomeArquivoSemMidia)}">${escaparHtmlChat(nomeArquivoSemMidia)}</span>` : ''}
                    <span class="midia-indisponivel-sub">${subtitulo}</span>
                </div>
            </div>`;
      }
      // RCS: a mensagem da campanha vira o cartão que o cliente viu (mídia,
      // título, botões) no lugar do texto comum. O toque num botão continua
      // texto comum, mas ganha a citação do cartão (ver quotedHtml, abaixo).
      const detalheRcs = detalheRcsDaMensagem(msg);
      const cliqueRcs =
        detalheRcs && detalheRcs.clique && !msg.is_from_me ? detalheRcs.clique : null;
      let desenhouRcs = false;
      if (detalheRcs && detalheRcs.cartao) {
        conteudoHTML += montarCartaoRcs(detalheRcs.cartao, msg.body, {
          dataTexto: stringDataFinal,
          remetente: remetenteMidia,
        });
        classeMedia += ' bolha-cartao-rcs';
        desenhouRcs = true;
      }
      // Localização (enviada pelo "/" ou mandada pelo cliente): o cartão do
      // mapa no lugar do texto "📍 nome — endereço". Ver js/localizacao-mapa.js.
      let desenhouLocal = false;
      if (msg.type === 'location' && msg.localizacao && window.mapaLocalizacao) {
        conteudoHTML += window.mapaLocalizacao.cartaoHtml(msg.localizacao);
        classeMedia += ' bolha-localizacao';
        desenhouLocal = true;
      }
      // Cartão de contato compartilhado pelo cliente: o texto da mensagem é só
      // o resumo ("Maria e mais 2", pra prévia da lista) — o cartão substitui.
      let desenhouContato = false;
      if (msg.type === 'contact' && msg.detalhe && Array.isArray(msg.detalhe.contatos)) {
        conteudoHTML += montarCartaoContato(msg.detalhe.contatos);
        classeMedia += ' bolha-contato';
        desenhouContato = true;
      }
      // Mensagem de banco que o cliente encaminhou (vinha com botões).
      if (msg.detalhe && msg.detalhe.encaminhada) {
        conteudoHTML += `<div class="marca-encaminhada"><i class="ph-bold ph-share-fat"></i>Encaminhada</div>`;
      }
      if (
        msg.body &&
        !desenhouRcs &&
        !desenhouLocal &&
        !desenhouContato &&
        !nomeArquivoSemMidia &&
        msg.body !== '(midia)' &&
        msg.body !== '(Mídia)' &&
        msg.body !== '(Áudio)'
      ) {
        let mostrarTexto = true;
        if (msg.media_path) {
          const texto = msg.body.toLowerCase().trim();
          const nomeSalvo = msg.media_path.split('/').pop().toLowerCase();
          const extAtual = nomeSalvo.split('.').pop();
          const extensoesBloqueio = [
            '.pdf',
            '.docx',
            '.doc',
            '.xls',
            '.xlsx',
            '.csv',
            '.ppt',
            '.pptx',
            '.jpg',
            '.jpeg',
            '.png',
            '.webp',
            '.gif',
            '.bmp',
            '.svg',
            '.mp4',
            '.avi',
            '.mp3',
            '.wav',
            '.ogg',
            '.opus',
            '.m4a',
            '.zip',
            '.rar',
            '.7z',
            '.tar',
            '.gz',
            '.psd',
            '.ai',
            '.eps',
            '.cdr',
            '.ipa',
            '.apk',
            '.exe',
            '.msi',
            '.bat',
            '.txt',
            '.json',
            '.xml',
            '.html',
            '.css',
            '.js',
          ];
          if (extensoesBloqueio.some((ext) => texto.endsWith(ext)))
            mostrarTexto = false;
          if (texto === nomeSalvo) mostrarTexto = false;
          if (extAtual && texto.endsWith('.' + extAtual)) mostrarTexto = false;
        }
        if (mostrarTexto) {
          let textoLimpo = msg.body;
          let sufixoHtml = '';
          // O "Editada por" sai de DENTRO do texto e vira irmão dele: na foto
          // com legenda (grade foto | legenda) ele ficava preso na coluna da
          // legenda, no meio do balão; solto, a grade o põe embaixo de tudo.
          // Só em mensagem nossa (é o servidor que grava): no texto do cliente
          // "<br><span" é só texto, e passaria cru pro balão.
          if (msg.is_from_me && textoLimpo && textoLimpo.includes('<br><span')) {
            textoLimpo = msg.body.split('<br><span')[0];
            sufixoHtml = htmlLimpoDoSistema('<span' + msg.body.split('<br><span')[1]);
          }
          conteudoHTML += `<span class="texto-formatado">${formatarTextoWhatsApp(textoLimpo)}</span>${sufixoHtml}`;
        }
      }
      // Mensagem nossa que foi com botões de RCS (mensagem pronta de RCS,
      // automação): os botões FORA do balão, logo embaixo e da mesma largura,
      // como o WhatsApp oficial mostra botão de mensagem de empresa. Dentro
      // do balão (até 22/09/2026) eles subiam por cima da foto na bolha de
      // imagem + texto e ficavam espremidos. O cartão da campanha segue
      // desenhando os dele dentro do cartão.
      let botoesForaHtml = '';
      if (detalheRcs && !detalheRcs.cartao && Array.isArray(detalheRcs.botoes)) {
        const htmlBotoes = montarBotoesRcs(detalheRcs.botoes);
        if (htmlBotoes) {
          botoesForaHtml = `<div class="botoes-rcs-fora">${htmlBotoes}</div>`;
          classeMedia += ' bolha-com-botoes-rcs';
        }
      }
      const hora = horaVisualMidia;
      const idReal = msg.wpp_id || (msg.id && msg.id.id) || msg.id;
      if (jaNaTela(idReal)) return;
      // Alvo do clique na citação (scrollToMsg). Atributo próprio, e não
      // `data-wpp-id`: aquele é o que o jaNaTela usa pra barrar repetido, e a
      // recebida nunca teve — por isso a citação dela não achava nada.
      if (idReal) row.dataset.msgId = String(idReal);
      let statusHtml = '';
      if (msg.is_from_me) {
        statusHtml = `<span class="msg-status" data-wpp-id="${escaparHtmlChat(idReal)}">${getStatusIcon(msg.ack, msg.bot_id || (chatSelecionado && chatSelecionado.bot_id))}</span>`;
      }
      let senderLabelHtml = '';
      if (msg.is_from_me) {
        let nomeRemetente = msg.sent_by_name
          ? msg.sent_by_name
          : 'WhatsApp Lite';
        let iconeRemetente = msg.sent_by_name ? 'ph-headset' : 'ph-chat-text';
        // Robô leva ícone de robô. "Agente Externo" entra aqui junto com Sistema
        // e FLUXO: sem isso ele sairia de headset, o mesmo ícone das vendedoras,
        // que é justamente a confusão que o rótulo veio desfazer. Cuidado para
        // não confundir com "Externo" (sem "Agente"), que é a vendedora
        // respondendo pelo WhatsApp Web e continua de headset, corretamente.
        if (
          msg.sent_by_name === 'Sistema' ||
          msg.sent_by_name === 'Agente Externo' ||
          (msg.sent_by_name &&
            msg.sent_by_name.toUpperCase().startsWith('FLUXO:'))
        ) {
          iconeRemetente = 'ph-robot';
        }
        senderLabelHtml = `<span class="msg-sender-label" title="${escaparHtmlChat(nomeRemetente)}">${escaparHtmlChat(nomeRemetente)} <i class="ph-bold ${iconeRemetente}"></i></span>`;
      }
      let headerOutsideHtml = `
          <div class="msg-header-outside">
              ${senderLabelHtml}
              <span class="msg-meta">${hora}</span>
              ${statusHtml}
          </div>
      `;
      let textoLimpoCitar = msg.body ? msg.body.split('<br><span')[0] : '';
      let textoParaCitar = textoLimpoCitar;
      if (textoParaCitar === '(Mídia)' || textoParaCitar === '(midia)') {
        textoParaCitar = '';
      }
      if (msg.type === 'ptt' || msg.type === 'audio') {
        textoParaCitar = 'Mensagem de voz';
      } else if (msg.type === 'image') {
        textoParaCitar = textoParaCitar || 'Foto';
      } else if (msg.type === 'video') {
        textoParaCitar = textoParaCitar || 'Vídeo';
      } else if (msg.type === 'document') {
        textoParaCitar = textoParaCitar || 'Arquivo';
      } else if (msg.type === 'sticker') {
        textoParaCitar = 'Figurinha';
      } else if (msg.type === 'comentario') {
        textoParaCitar = 'Anotação Interna';
      } else if (!textoParaCitar) {
        textoParaCitar = 'Mensagem';
      }
      let autorParaCitar = msg.is_from_me ? 'Você' : 'Cliente';
      let btnReplyHtml = `<button class="btn-reply-hover" onclick="prepararResposta('${textoParaOnclick(idReal)}', '${autorParaCitar}', '${textoParaOnclick(textoParaCitar)}', '${textoParaOnclick(msg.type)}')" title="Responder"><i class="bx bx-reply"></i></button>`;
      let btnDeleteHtml = '';
      if (msg.is_from_me && msg.type !== 'revoked') {
        btnDeleteHtml = `<button class="btn-delete-hover" onclick="apagarMensagemParaTodos('${textoParaOnclick(idReal)}')" title="Apagar para todos"><i class="bx bx-trash"></i></button>`;
      }
      let btnEditHtml = '';
      const diffMins = (new Date().getTime() - dataObj.getTime()) / 60000;
      // Foto e vídeo com legenda também editam (a legenda): a Evolution 2.3.7
      // monta a edição de imageMessage/videoMessage. Documento NÃO — ela
      // responde "Message not compatible" (formatUpdateMessage só conhece
      // texto, imagem e vídeo). Sem legenda não há o que editar.
      const legendaEditavel =
        ['image', 'video'].includes(msg.type) &&
        !!msg.body &&
        !['(Mídia)', '(midia)', '(mídia)'].includes(msg.body.trim());
      if (
        msg.is_from_me &&
        (msg.type === 'chat' || legendaEditavel) &&
        diffMins <= 15 &&
        msg.type !== 'revoked'
      ) {
        const textoLimpoEdit = (msg.body || '').split('<br><span')[0];
        const safeText = encodeURIComponent(textoLimpoEdit);
        btnEditHtml = `<button class="btn-edit-hover" onclick="prepararEdicao('${textoParaOnclick(idReal)}', '${textoParaOnclick(safeText)}')" title="Editar Mensagem"><i class="bx bx-pencil"></i></button>`;
      }
      let btnReactHtml = '';
      if (msg.type !== 'revoked' && msg.type !== 'comentario') {
        btnReactHtml = `<button class="btn-react-hover" onclick="abrirMenuReacoes('${textoParaOnclick(idReal)}', event)" title="Reagir"><i class="bx bx-smile"></i></button>`;
      }
      let emojisHTML = '';
      if (msg.reacoes && typeof msg.reacoes === 'object') {
        const listaEmojis = Object.values(msg.reacoes).filter((e) => e !== '');
        if (listaEmojis.length > 0) emojisHTML = escaparHtmlChat(listaEmojis.join(''));
      }
      let reacaoBadgeHtml = `<div class="msg-reaction-badge ${emojisHTML ? '' : 'hidden'}" id="badge-react-${escaparHtmlChat(idReal)}" onclick="removerReacao('${textoParaOnclick(idReal)}', event)" title="Clique para remover" style="cursor: pointer;">${emojisHTML}</div>`;
      if (msg.type === 'comentario') {
        btnReplyHtml = '';
        btnDeleteHtml = '';
        btnEditHtml = '';
        btnReactHtml = '';
      }
      // RCS: a API da Ótima não tem responder (citação), reagir, editar nem
      // apagar — os botões nem são desenhados. A barrinha de ações do celular
      // (mobile.js) é feita desses mesmos botões, então lá some junto. A
      // anotação interna também fica sem, igual no WhatsApp.
      if (ehCanalRcs(msg.bot_id || (chatSelecionado && chatSelecionado.bot_id))) {
        btnReplyHtml = '';
        btnDeleteHtml = '';
        btnEditHtml = '';
        btnReactHtml = '';
      }
      let quotedHtml = '';
      if (msg.quoted_wpp_id || msg.quoted_body) {
        let qBody = msg.quoted_body || 'Mensagem';
        let qType = msg.quoted_type || 'chat';
        let qIcon = 'bx-message-rounded-dots';
        let qClass = 'reply-default';
        if (qType === 'ptt' || qType === 'audio') {
          qIcon = 'bx-microphone';
          qClass = 'reply-audio';
        } else if (qType === 'image') {
          qIcon = 'bx-image';
          qClass = 'reply-image';
        } else if (qType === 'video') {
          qIcon = 'bx-video';
          qClass = 'reply-video';
        } else if (qType === 'document') {
          qIcon = 'bx-file';
          qClass = 'reply-document';
        }
        quotedHtml = `
      <div class="quoted-msg-box ${qClass}" onclick="scrollToMsg('${textoParaOnclick(msg.quoted_wpp_id || '')}')">
          <div class="quoted-content-flex">
              ${qType !== 'chat' && qType !== 'texto' ? `<div class="quoted-icon-box"><i class="bx ${qIcon}"></i></div>` : ''}
              <div class="quoted-text-content">
                  <span class="quoted-author">${escaparHtmlChat(msg.quoted_participant || 'Mensagem')}</span>
                  <span class="quoted-text">${escaparHtmlChat(String(qBody).split('<br><span')[0])}</span>
              </div>
          </div>
      </div>
      `;
      }
      // Toque em botão do cartão de RCS: cita o cartão, como uma resposta.
      if (cliqueRcs) quotedHtml = montarCitacaoDoCliqueRcs(cliqueRcs);

      // 👇 TRATAMENTO VISUAL SE A MENSAGEM FOR APAGADA 👇
      if (msg.type === 'revoked') {
        btnReplyHtml = '';
        btnDeleteHtml = '';
        quotedHtml = '';
        transcriptionHtml = '';
        conteudoHTML = `<span class="texto-formatado"><i class="bx bx-block" style="margin-right:6px;"></i>${htmlLimpoDoSistema(msg.body)}</span>`;
        classeMedia += ' revoked';
      }

      // 👇 NOVO: TRATAMENTO VISUAL SE A MENSAGEM FOI BARRADA PELA META 👇
      if (msg.type === 'restricted') {
        btnReplyHtml = '';
        btnDeleteHtml = '';
        btnEditHtml = '';
        btnReactHtml = '';
        quotedHtml = '';
        transcriptionHtml = '';
        // Mesma estrutura cinza de mensagem apagada, garantindo o ícone e fonte
        conteudoHTML = `<span class="texto-formatado"><i class="bx bx-block" style="margin-right:6px;"></i>${htmlLimpoDoSistema(msg.body)}</span>`;
        classeMedia += ' revoked';
      }

      // Envio com ERROR do WhatsApp: conteúdo fica, balão cinza + alerta.
      // As 'restricted' antigas já tiveram o texto apagado e seguem cinza.
      if (msg.is_from_me && msg.ack === -1 && msg.type !== 'restricted') {
        classeMedia += ' msg-falha';
      }

      // Só emoji (1 a 3): grande e sem balão (ver contarSoEmojis). Fica de
      // fora quem precisa do balão pra fazer sentido: resposta a outra
      // mensagem, anotação interna, mídia, cartão/botões de RCS, apagada e
      // envio que falhou (o balão cinza é o aviso).
      if (
        (msg.type === 'chat' || msg.type === 'texto') &&
        !(msg.is_from_me && msg.ack === -1) &&
        !quotedHtml &&
        !msg.media_path &&
        !desenhouRcs &&
        !botoesForaHtml &&
        !(msg.body || '').includes('<')
      ) {
        const qtdEmojis = contarSoEmojis(msg.body);
        if (qtdEmojis) classeMedia += ` so-emoji so-emoji-${qtdEmojis}`;
      }

      // Os mesmos elementos que carregam `data-wpp-id` no HTML abaixo.
      if (idsDesteDesenho) {
        if (statusHtml) idsDesteDesenho.add(String(idReal));
        if (transcriptionHtml) idsDesteDesenho.add(String(msg.wpp_id || ''));
      }

      const avatar = avatarDoRemetente(msg, chatSelecionado);
      row.dataset.autor = avatar.chave;
      const avatarHtml = avatar.chave !== autorAnterior ? avatar.html : '';
      // 1º da sequência: leva a foto e o balão com rabinho (CSS).
      if (avatarHtml) row.classList.add('msg-row-inicio');
      // Mesmo remetente do balão anterior: o nome some (fica no DOM, só
      // escondido pelo CSS, porque há código que lê o nome do balão).
      row.dataset.pessoa = avatar.pessoa;
      if (avatar.pessoa === pessoaAnterior) row.classList.add('msg-row-mesmo-nome');
      pessoaAnterior = avatar.pessoa;
      autorAnterior = avatar.chave;

      row.innerHTML = `
          <div class="msg-wrapper-vertical">
              ${avatarHtml}
              ${btnDeleteHtml}
              ${btnEditHtml}
              ${btnReplyHtml}
              ${btnReactHtml} <div class="msg-bubble ${classeMedia}">
                  ${headerOutsideHtml}
                  ${quotedHtml}
                  ${conteudoHTML}
                  ${reacaoBadgeHtml}
              </div>
              ${botoesForaHtml}
              ${transcriptionHtml}
          </div>`;
      containerAlvo.appendChild(row);
      // O cartão respondido já está na tela (vem antes na conversa): acende
      // nele o botão que o cliente tocou.
      if (cliqueRcs) marcarBotaoTocadoNoCartao(cliqueRcs, msg.body);
    });
    if (!append) {
      // Abrir uma conversa cai no rodapé (última mensagem), que é o certo em
      // 99% das vezes. A exceção é quando alguém pediu uma mensagem
      // ESPECÍFICA — o "abrir conversa" do apito do lembrete: cair no rodapé
      // ali obriga a pessoa a rolar pra cima caçando o card que ela acabou de
      // clicar, que pode estar dias acima.
      const alvo = wppIdParaFocar;
      wppIdParaFocar = null;
      // rAF duplo: o primeiro devolve o controle depois do layout das linhas
      // que acabaram de entrar; o segundo pega a altura já com as caixas de
      // dia posicionadas. Sem isso o scroll calcula em cima de uma altura que
      // ainda vai mudar e para no lugar errado.
      if (alvo) {
        requestAnimationFrame(() =>
          requestAnimationFrame(() => {
            if (!focarMensagem(alvo))
              containerMsgs.scrollTop = containerMsgs.scrollHeight;
          })
        );
      } else {
        containerMsgs.scrollTop = containerMsgs.scrollHeight;
      }
    }
  }
  // --- FUNÇÕES DE CITAÇÃO (REPLY) ---
  let mensagemParaResponder = null;
  window.prepararResposta = function (idMsg, autor, texto, tipo) {
    mensagemParaResponder = {
      id: idMsg,
      autor: autor,
      texto: texto,
      tipo: tipo || 'chat',
    };
    const container = document.getElementById('reply-preview-container');
    const contentFlex = container.querySelector('.reply-preview-content');
    const inputEl = document.getElementById('input-mensagem-texto');
    // Configurações baseadas no tipo
    let qType = tipo || 'chat';
    let qIcon = 'bx-message-rounded-dots';
    let qClass = 'reply-default';
    if (qType === 'ptt' || qType === 'audio') {
      qIcon = 'bx-microphone';
      qClass = 'reply-audio';
    } else if (qType === 'image') {
      qIcon = 'bx-image';
      qClass = 'reply-image';
    } else if (qType === 'video') {
      qIcon = 'bx-video';
      qClass = 'reply-video';
    } else if (qType === 'document') {
      qIcon = 'bx-file';
      qClass = 'reply-document';
    }
    // 1. Aplica a classe na caixa de preview (Removemos o borderLeftColor via JS)
    if (container) container.className = `reply-preview-bar ${qClass} hidden`;
    // 2. Monta o iconezinho sem styles inline de cor
    let iconHtml =
      qType !== 'chat' && qType !== 'texto'
        ? `<div class="quoted-icon-box"><i class="bx ${qIcon}"></i></div>`
        : '';
    // 3. Injeta na barra! (Sem colors inline)
    if (contentFlex) {
      contentFlex.innerHTML = `
        <div style="display: flex; align-items: center; width: 100%;">
            ${iconHtml}
            <div style="display: flex; flex-direction: column; overflow: hidden; flex: 1; padding-left: ${iconHtml ? '10px' : '0'};">
                <span class="reply-preview-name">${escaparHtmlChat(autor || 'Contato')}</span>
                <span class="reply-preview-text">${escaparHtmlChat(texto || 'Mensagem')}</span>
            </div>
        </div>
      `;
    }
    if (container) container.classList.remove('hidden');
    if (inputEl) inputEl.focus();
  };
  // NÃO PODEMOS PERDER ESSA FUNÇÃO AQUI!
  window.fecharPreviewResposta = function () {
    mensagemParaResponder = null;
    const container = document.getElementById('reply-preview-container');
    if (container) container.classList.add('hidden');
  };
  // Resposta (citação) ou edição armada na aba do WhatsApp, e a vendedora
  // troca pra aba do RCS: lá não existe nenhuma das duas. Sem isto a citação
  // sumia no envio sem aviso, e a edição tentaria editar pelo RCS. A resposta
  // cai sem apagar o que ela já digitou; a edição cai COM o texto, porque o
  // texto na caixa era a mensagem antiga, e mandá-lo sairia como mensagem
  // nova pro cliente.
  function largarRespostaOuEdicaoPendente() {
    if (mensagemEmEdicao) {
      window.fecharPreviewResposta();
    } else if (mensagemParaResponder) {
      mensagemParaResponder = null;
      const container = document.getElementById('reply-preview-container');
      if (container) container.classList.add('hidden');
    }
  }
  // 5. Enviar Mensagem (Lógica Central: Áudio, Texto ou Arquivos)
  async function enviarMensagem() {
    // ==========================================================
    // 🎙️ INTERCEPTADOR DE ÁUDIO (A MÁGICA DA OPÇÃO 3)
    // ==========================================================
    const painelGravacao = document.getElementById('painel-gravacao');
    const divEstadoGravando = document.getElementById('rec-estado-gravando');
    if (painelGravacao && !painelGravacao.classList.contains('hidden')) {
      // Preview de áudio de Mensagem Pronta: confirmar aqui manda o áudio de verdade
      if (modoPreviewMensagemProntaAudio) {
        confirmarEnvioMensagemProntaAudio();
        return;
      }
      // Se estiver gravando ou tiver partes pausadas, chama a nova função finalizarEnvio
      if (
        mediaRecorder &&
        divEstadoGravando &&
        !divEstadoGravando.classList.contains('hidden')
      ) {
        finalizarEnvio();
      } else if (chunksAudio.length > 0) {
        finalizarEnvio();
      }
      return;
    }
    // ==========================================================
    // 📍 Localização escolhida no "/": vai primeiro. Se ela digitou algo
    // depois (ou anexou), segue o envio normal logo em seguida.
    if (localizacaoPendente) {
      const foi = await enviarLocalizacaoPendente();
      if (!foi || (!inputMsg.value.trim() && listaArquivosParaEnviar.length === 0)) return;
    }
    const texto = inputMsg.value.trim();
    // CENÁRIO 1: EXISTEM ARQUIVOS NA BARRA DE PREVIEW PARA ENVIAR
    if (listaArquivosParaEnviar.length > 0) {
      // Feedback Visual
      const btnIcon = btnEnviar.querySelector('i');
      const classeOriginal = btnIcon.className;
      btnIcon.className = 'fas fa-spinner fa-spin';
      btnEnviar.disabled = true;
      try {
        // Envia um por um

        for (let i = 0; i < listaArquivosParaEnviar.length; i++) {
          const arquivo = listaArquivosParaEnviar[i];
          const formData = new FormData();
          formData.append('chatId', chatSelecionado.chat_id);
          formData.append('botId', chatSelecionado.bot_id);
          formData.append('file', arquivo);

          // 👇 Avisa o backend se a pessoa forçou o envio como documento
          if (arquivo.modoEnvio === 'documento') {
            formData.append('forceDocument', 'true');
          }

          // Se tiver texto digitado, manda como legenda APENAS no primeiro arquivo

          if (i === 0 && texto) {
            formData.append('message', texto); // <-- Importante: mantido igual para a API backend
          }
          // Botões de RCS vão no primeiro arquivo, o que leva a legenda.
          const botoesAnexo = i === 0 ? botoesParaEnviar() : null;
          if (botoesAnexo) formData.append('botoes', botoesAnexo);
          // 👇 MÁGICA 1: Anexa a CITAÇÃO (Reply) no primeiro arquivo da fila!
          if (i === 0 && mensagemParaResponder) {
            formData.append('quoted_wpp_id', mensagemParaResponder.id);
            formData.append('quoted_body', mensagemParaResponder.texto);
            formData.append('quoted_participant', mensagemParaResponder.autor);
            formData.append('quoted_type', mensagemParaResponder.tipo);
          }
          await fetch('/api/chat/send', {
            method: 'POST',
            headers: {
              Authorization: `Bearer ${localStorage.getItem('token')}`,
            },
            body: formData,
          });
        }
        // Sucesso: Limpa tudo
        fecharPreviewAnexos();
        inputMsg.value = '';
        fecharPreviewResposta();
        largarBotoesPendentes();
      } catch (erro) {
        console.error(erro);
        mostrarAlerta('Erro ao enviar arquivos.', 'error');
      } finally {
        // Restaura o botão
        btnIcon.className = classeOriginal;
        btnEnviar.disabled = false;
      }
      return;
    }
    // CENÁRIO 2: APENAS TEXTO
    if (!texto || !chatSelecionado) return;
    if (mensagemEmEdicao) {
      enviarEdicao(texto);
      return;
    }
    // Feedback visual imediato
    inputMsg.value = '';
    inputMsg.style.height = 'auto';
    // Pega quem está logado para prever o nome
    const nomeUsuarioLogado =
      usuarioLogado.name || usuarioLogado.username || 'Sistema';
    const row = document.createElement('div');
    row.className = 'msg-row enviada msg-temp';
    // Mesma regra dos balões de verdade (a foto só no 1º da sequência). A
    // provisória fica solta no fim da conversa; a anterior pode ser outra
    // provisória ou a última linha da caixa do dia.
    const avatarTemp = avatarDoRemetente(
      { is_from_me: true, sent_by_name: nomeUsuarioLogado },
      chatSelecionado
    );
    let linhaAnterior = containerMsgs.lastElementChild;
    if (linhaAnterior && linhaAnterior.classList.contains('dia-conversa-wrapper'))
      linhaAnterior = linhaAnterior.lastElementChild;
    const autorAntesTemp = linhaAnterior && linhaAnterior.dataset.autor;
    row.dataset.autor = avatarTemp.chave;
    const tempAbreSequencia = avatarTemp.chave !== autorAntesTemp;
    if (tempAbreSequencia) row.classList.add('msg-row-inicio');
    row.dataset.pessoa = avatarTemp.pessoa;
    if (linhaAnterior && linhaAnterior.dataset.pessoa === avatarTemp.pessoa)
      row.classList.add('msg-row-mesmo-nome');
    row.innerHTML = `
      <div class="msg-wrapper-vertical">
          ${tempAbreSequencia ? avatarTemp.html : ''}
          <div class="msg-bubble" style="opacity: 0.7">
              <div class="msg-header-outside">
                  <span class="msg-sender-label">${nomeUsuarioLogado} <i class="ph-bold ph-headset"></i></span>
                  <span class="msg-meta"><i class="far fa-clock"></i> Enviando...</span>
              </div>
              <span>${formatarTextoWhatsApp(texto)}</span>
          </div>
      </div>`;
    containerMsgs.appendChild(row);
    rolarParaFim(true);
    if (chatSelecionado) {
      chatSelecionado.last_message_from_me = true;
      chatSelecionado.last_message_at = new Date().getTime();
      // 👇 Garante que a barra lateral puxe o seu nome na hora do envio!
      chatSelecionado.last_sent_by_name = nomeUsuarioLogado;
      if (typeof window.atualizarVisualSLA === 'function')
        window.atualizarVisualSLA();
    }
    try {
      const formData = new FormData();
      formData.append('chatId', chatSelecionado.chat_id);
      formData.append('botId', chatSelecionado.bot_id);
      formData.append('message', texto);
      if (isModoComentario) {
        formData.append('tipoExtra', 'comentario');
      }
      // Botões de RCS da mensagem pronta (anotação interna não leva).
      const botoesTexto = isModoComentario ? null : botoesParaEnviar();
      if (botoesTexto) formData.append('botoes', botoesTexto);
      if (mensagemParaResponder) {
        formData.append('quoted_wpp_id', mensagemParaResponder.id);
        formData.append('quoted_body', mensagemParaResponder.texto);
        formData.append('quoted_participant', mensagemParaResponder.autor);
        formData.append('quoted_type', mensagemParaResponder.tipo);
      }
      const res = await fetch('/api/chat/send', {
        method: 'POST',
        headers: {
          Authorization: `Bearer ${localStorage.getItem('token')}`,
        },
        body: formData,
      });
      const data = await res.json();
      if (!data.success) {
        mostrarAlerta(data.message, 'error');
        row.remove();
      } else {
        // Atualiza hora local
        row.querySelector('.msg-meta').innerHTML =
          new Date().toLocaleTimeString([], {
            hour: '2-digit',
            minute: '2-digit',
          });
        row.querySelector('.msg-bubble').style.opacity = '1';
        fecharPreviewResposta();
        if (botoesTexto) largarBotoesPendentes();

        // RCS: se esta mensagem reabriu a janela, ela CUSTOU. A vendedora
        // precisa saber na hora — é a diferença entre conversar de graça e
        // abrir uma cobrança sem perceber.
        if (data.canal === 'rcs') {
          if (data.cobrada)
            mostrarAlerta(
              'Nova conversa de RCS aberta (com custo). As próximas 24h são livres.',
              'warning'
            );
          atualizarAvisoJanelaRcs(
            chatSelecionado.bot_id,
            chatSelecionado.chat_id
          );
        }
        if (isModoComentario) {
          isModoComentario = false;
          inputWrapperChat.classList.remove('modo-comentario');
          btnToggleComentario.classList.remove('ativo');
          inputMsg.placeholder = 'Digite sua mensagem...';
        }
      }
    } catch (e) {
      console.error(e);
      mostrarAlerta('Erro de conexão.', 'error');
      row.remove();
    }
  }
  if (inputBusca) {
    let timeoutBusca = null;
    inputBusca.addEventListener('input', () => {
      clearTimeout(timeoutBusca);
      timeoutBusca = setTimeout(() => {
        carregarChats(1, true);
      }, 600);
    });
  }
  if (selectFiltro) {
    selectFiltro.addEventListener('change', () => {
      carregarChats(1, true);
    });
  }
  btnEnviar.addEventListener('click', enviarMensagem);
  inputMsg.addEventListener('input', function () {
    this.style.height = 'auto';
    this.style.height = this.scrollHeight + 'px';
  });
  // Lógica do Enter e Ctrl+Enter / Shift+Enter
  inputMsg.addEventListener('keydown', (e) => {
    if (e.key === 'Enter') {
      if (e.shiftKey || e.ctrlKey) {
        return;
      }
      // 👇 Se o painel de "/comandos" estiver aberto com resultado, o Enter
      // é interceptado AQUI (keydown roda antes do keyup que faz a busca),
      // senão a mensagem crua ("/teste") seria enviada antes da gente ter
      // chance de trocar pelo texto da mensagem pronta. Igual WhatsApp:
      // 1º Enter copia o texto pro campo, 2º Enter (já sem "/") envia.
      if (
        slashContainer &&
        !slashContainer.classList.contains('hidden') &&
        inputMsg.value.startsWith('/') &&
        comandosFiltradosAtuais.length > 0
      ) {
        e.preventDefault();
        const idx = indexComandoAtivo === -1 ? 0 : indexComandoAtivo;
        selecionarComando(comandosFiltradosAtuais[idx]);
        return;
      }
      e.preventDefault();
      enviarMensagem();
    }
  });
  // Inicia carregando
  carregarChats(1, true);
  // Função para retornar o HTML do ícone baseado no status (ack)
  function getStatusIcon(ack, botId) {
    // Ack: 1=Enviado (1 check), 2=Recebido (2 checks), 3=Lido (2 checks azuis)
    // Se for null ou 0, é pendente (relógio). -1 = o canal devolveu falha.
    // A falha tem causa diferente por canal: no WhatsApp é o chip restrito
    // (responder pelo App/Web resolve); no RCS é quase sempre o celular do
    // cliente sem RCS ativo ("Indisponível" da Ótima) — aí a mensagem nunca
    // chega, e mandar a vendedora pro App/Web era instrução errada.
    if (ack === -1) {
      const dica = ehCanalRcs(botId)
        ? 'Não entregue pelo RCS: o celular do cliente não tem RCS ativo.'
        : 'Conta restrita. Responda pelo App/Web.';
      return `<span class="status-falha" data-dica="${dica}"><i class="fas fa-exclamation-circle status-icon falha"></i></span>`;
    }
    if (!ack || ack === 0) return '<i class="far fa-clock status-icon"></i>'; // Pendente
    if (ack === 1) return '<i class="fas fa-check status-icon"></i>'; // Enviado (1 tick)
    if (ack === 2) return '<i class="fas fa-check-double status-icon"></i>'; // Entregue (2 ticks cinza)
    if (ack >= 3) return '<i class="fas fa-check-double status-icon lido"></i>'; // Lido (2 ticks azul)
    return '';
  }

  // --- LÓGICA DE ANEXAR ARQUIVO (Estilo WhatsApp) ---
  const btnAnexar = document.getElementById('btn-anexar');
  const inputAnexo = document.getElementById('input-anexo');
  const menuAnexos = document.getElementById('menu-anexos');

  // 1. Clicar no clip abre o menu de opções flutuante
  btnAnexar.addEventListener('click', (e) => {
    e.stopPropagation();
    if (!chatSelecionado)
      return mostrarAlerta('Selecione uma conversa.', 'warning');

    // Fecha o painel de emojis se estiver aberto para não encavalar
    const emojiContainer = document.getElementById('emoji-picker-container');
    if (emojiContainer) emojiContainer.classList.add('hidden');

    menuAnexos.classList.toggle('hidden');
  });

  let modoAnexoAtual = 'padrao'; // Variável global para guardar a intenção

  // 1.1 Trata os cliques nas opções do menu e define o filtro do Explorer
  document.querySelectorAll('.btn-anexo-opcao').forEach((btn) => {
    btn.addEventListener('click', (e) => {
      e.stopPropagation();
      const tipo = btn.dataset.tipo;
      modoAnexoAtual = tipo; // Salva se ele clicou em documento, midia ou audio

      // Filtra o explorer do Windows/Mac de acordo com o botão clicado
      if (tipo === 'documento') {
        // No WhatsApp aceita tudo (inclusive imagem, pra ir sem perder
        // qualidade); no RCS o único documento que a Ótima aceita é PDF.
        inputAnexo.accept =
          chatSelecionado && ehCanalRcs(chatSelecionado.bot_id) ? '.pdf' : '*/*';
      } else if (tipo === 'midia') {
        // No RCS o Explorer já abre só com o que a Ótima aceita (sem WEBP,
        // sem o .mov do iPhone), em vez de deixar escolher e recusar depois.
        inputAnexo.accept =
          chatSelecionado && ehCanalRcs(chatSelecionado.bot_id)
            ? ACCEPT_ARQUIVO_RCS
            : 'image/*,video/*';
      } else if (tipo === 'audio') {
        inputAnexo.accept = 'audio/*';
      }

      menuAnexos.classList.add('hidden');
      inputAnexo.click(); // Abre a janela já filtrada!
    });
  });

  // 1.2 Fecha o menu ao clicar em qualquer outro lugar da tela
  document.addEventListener('click', (e) => {
    if (menuAnexos && !menuAnexos.classList.contains('hidden')) {
      if (!menuAnexos.contains(e.target) && !btnAnexar.contains(e.target)) {
        menuAnexos.classList.add('hidden');
      }
    }
  });

  // Na conversa de RCS, tira da fila o que a Ótima não aceita e avisa na
  // hora. O clipe já filtra o Explorer, mas colar e arrastar não passam por
  // ele (e o Explorer deixa trocar pra "Todos os arquivos").
  function soArquivosQueOCanalAceita(arquivos) {
    if (!chatSelecionado || !ehCanalRcs(chatSelecionado.bot_id)) return arquivos;
    const aceitos = arquivos.filter(arquivoCabeNoRcs);
    if (aceitos.length < arquivos.length)
      mostrarAlerta(
        'O RCS aceita foto (JPG, PNG ou GIF), vídeo (MP4), PDF e áudio (MP3, OGG, AAC ou M4A). Outros arquivos vão pelo WhatsApp.',
        'warning'
      );
    return aceitos;
  }

  // 2. Quando seleciona arquivos (Agora adiciona na lista, NÃO envia)
  inputAnexo.addEventListener('change', (e) => {
    const novosArquivos = soArquivosQueOCanalAceita(Array.from(e.target.files));
    if (!novosArquivos.length) {
      inputAnexo.value = '';
      modoAnexoAtual = 'padrao';
      return;
    }
    if (listaArquivosParaEnviar.length + novosArquivos.length > 6) {
      mostrarAlerta('Máximo de 6 arquivos por vez.', 'warning');
      return;
    }

    // 👇 MÁGICA: Carimba cada arquivo com o modo que o usuário escolheu
    novosArquivos.forEach((f) => (f.modoEnvio = modoAnexoAtual));

    listaArquivosParaEnviar = [...listaArquivosParaEnviar, ...novosArquivos];
    renderizarPreviewBar(); // Mostra a barra preta
    inputAnexo.value = ''; // Limpa para permitir selecionar mais
    modoAnexoAtual = 'padrao'; // Reseta o modo
  });

  // 3. (NOVO) Colar Imagem direto no Input (Ctrl+V)
  if (inputMsg) {
    inputMsg.addEventListener('paste', (e) => {
      // Pega os itens da área de transferência
      const items = (e.clipboardData || e.originalEvent.clipboardData).items;
      let arquivosColados = [];
      // 👇 CORREÇÃO: Usar um loop for clássico em vez de for...in 👇
      for (let i = 0; i < items.length; i++) {
        const item = items[i];
        // Verifica se é um arquivo e se é imagem
        if (item.kind === 'file' && item.type.includes('image')) {
          const blob = item.getAsFile();
          // 👇 TRAVA ANTI-CLONE: Verifica se já pegamos essa mesma imagem em outro formato 👇
          const jaExiste = arquivosColados.some((f) => f.size === blob.size);
          if (!jaExiste) {
            arquivosColados.push(blob);
          }
        }
      }
      // Se encontrou imagens no Ctrl+V
      if (arquivosColados.length > 0) {
        e.preventDefault(); // Impede que cole o "caminho" ou lixo no texto
        arquivosColados = soArquivosQueOCanalAceita(arquivosColados);
        if (!arquivosColados.length) return;
        if (listaArquivosParaEnviar.length + arquivosColados.length > 6) {
          mostrarAlerta('Máximo de 6 arquivos por vez.', 'warning');
          return;
        }
        // Adiciona na lista e renderiza a barra preta
        listaArquivosParaEnviar = [
          ...listaArquivosParaEnviar,
          ...arquivosColados,
        ];
        renderizarPreviewBar();
      }
    });
  }
  // 4. (CORRIGIDO) Arrastar e Soltar (Drag & Drop RESTRICTO AO CHAT)
  const dropOverlay = document.getElementById('drop-zone-overlay');
  // Variável para contar quantas vezes o mouse entrou/saiu (Evita piscar)
  let dragCounter = 0;
  if (dropOverlay && areaAtiva) {
    // A) Entrou na Área da Conversa
    areaAtiva.addEventListener('dragenter', (e) => {
      e.preventDefault();
      e.stopPropagation();
      // Só ativa se tiver arrastando arquivos
      if (e.dataTransfer.types && e.dataTransfer.types.includes('Files')) {
        dragCounter++; // Aumenta o contador
        dropOverlay.classList.remove('hidden');
      }
    });
    // B) Saiu da Área da Conversa
    areaAtiva.addEventListener('dragleave', (e) => {
      e.preventDefault();
      e.stopPropagation();
      dragCounter--; // Diminui o contador
      // Se o contador zerou, significa que saiu REALMENTE da área principal
      if (dragCounter === 0) {
        dropOverlay.classList.add('hidden');
      }
    });
    // C) Arrastando por cima (Obrigatório para o Drop funcionar)
    areaAtiva.addEventListener('dragover', (e) => {
      e.preventDefault();
      e.stopPropagation();
    });
    // D) Soltou o arquivo
    areaAtiva.addEventListener('drop', (e) => {
      e.preventDefault();
      e.stopPropagation();
      // Reseta tudo
      dragCounter = 0;
      dropOverlay.classList.add('hidden');
      let arquivosSoltos = Array.from(e.dataTransfer.files);
      if (arquivosSoltos.length > 0) {
        if (!chatSelecionado) {
          mostrarAlerta('Selecione uma conversa primeiro.', 'warning');
          return;
        }
        arquivosSoltos = soArquivosQueOCanalAceita(arquivosSoltos);
        if (!arquivosSoltos.length) return;
        if (listaArquivosParaEnviar.length + arquivosSoltos.length > 6) {
          mostrarAlerta('Máximo de 6 arquivos por vez.', 'warning');
          return;
        }
        // Adiciona na lista e renderiza
        listaArquivosParaEnviar = [
          ...listaArquivosParaEnviar,
          ...arquivosSoltos,
        ];
        renderizarPreviewBar();
      }
    });
  }
  // ============================================================
  // 🎙️ GRAVAÇÃO COM PAUSE, PREVIEW E RETOMADA (O PULO DO GATO: FRONTEND OGG/OPUS)
  // ============================================================
  const btnGravarAudio = document.getElementById('botao-gravar-audio');
  const painelGravacao = document.getElementById('painel-gravacao');
  const btnCancelarRec = document.getElementById('btn-cancelar-audio');
  const btnPausarRec = document.getElementById('btn-pausar-audio');
  const btnRetomarRec = document.getElementById('btn-retomar-audio');
  const btnConfirmarRec = document.getElementById('btn-confirmar-audio');
  const divEstadoGravando = document.getElementById('rec-estado-gravando');
  const divEstadoPreview = document.getElementById('rec-estado-preview');
  const timerDisplay = document.getElementById('timer-gravacao');
  const timerPreviewTotal = document.getElementById('timer-preview-total');
  const btnPreviewPlay = document.getElementById('btn-preview-play');
  const barraPreview = document.getElementById('rec-preview-progress');
  // --- VARIÁVEIS DE ESTADO ---
  let mediaRecorder = null;
  let streamGlobal = null;
  let intervaloTimer = null;
  let chunksAudio = [];
  let tempoAcumulado = 0;
  let tempoAtualSegmento = 0;
  let audioPreviewObj = null;
  // Quando não-nulo, o painel de áudio está mostrando o preview de uma Mensagem
  // Pronta de áudio (em vez de uma gravação ao vivo) — guarda o objeto da mensagem
  let modoPreviewMensagemProntaAudio = null;
  // =======================================================
  // MOTOR DO VISUALIZADOR DE ÁUDIO AO VIVO
  // =======================================================
  let visualizerContext = null;
  let visualizerAnalyser = null;
  let visualizerAnimFrame = null;
  function iniciarVisualizadorAoVivo(stream) {
    const canvas = document.getElementById('live-wave-canvas');
    if (!canvas) return;
    // Deixamos a largura em 100% para a "mola" do HTML empurrar
    canvas.style.width = '100%';
    canvas.style.height = '30px';
    const canvasCtx = canvas.getContext('2d');
    visualizerContext = new (
      window.AudioContext || window.webkitAudioContext
    )();
    visualizerAnalyser = visualizerContext.createAnalyser();
    visualizerAnalyser.fftSize = 256;
    const source = visualizerContext.createMediaStreamSource(stream);
    source.connect(visualizerAnalyser);
    const bufferLength = visualizerAnalyser.frequencyBinCount;
    const dataArray = new Uint8Array(bufferLength);
    function desenhar() {
      visualizerAnimFrame = requestAnimationFrame(desenhar);
      // 👇 A MÁGICA DA RESPONSIVIDADE
      if (canvas.width !== canvas.offsetWidth && canvas.offsetWidth > 0) {
        canvas.width = canvas.offsetWidth;
        canvas.height = 40; // Aumentamos o teto interno do Canvas para 40px
      }
      visualizerAnalyser.getByteFrequencyData(dataArray);
      canvasCtx.clearRect(0, 0, canvas.width, canvas.height);
      let soma = 0;
      for (let i = 0; i < bufferLength; i++) {
        soma += dataArray[i];
      }
      let volumeMedio = soma / bufferLength;
      const isFalando = volumeMedio > 12;
      const numBarras = 60;
      const pontos = [];
      for (let i = 0; i < numBarras; i++) {
        const amostraIndex = Math.floor(i * (bufferLength / numBarras) * 0.5);
        let valor = dataArray[amostraIndex];
        // 👇 SOLTANDO O FREIO: Tiramos de 0.8 (80%) para 0.95 (95% da altura total)
        // Isso vai fazer a onda pular com muito mais intensidade!
        let barHeight = isFalando ? (valor / 255) * (canvas.height * 0.95) : 2;
        if (barHeight < 2) barHeight = 2;
        pontos.push(barHeight);
      }
      const CY = canvas.height;
      const step = canvas.width / (numBarras - 1);
      // Gradiente animado mantido!
      const offset = (Date.now() * 0.015) % (canvas.width * 2);
      const gradient = canvasCtx.createLinearGradient(
        -canvas.width + offset,
        0,
        canvas.width + offset,
        0
      );
      gradient.addColorStop(0, '#0c3d6e');
      gradient.addColorStop(0.5, '#1e9e4f');
      gradient.addColorStop(1, '#0c3d6e');
      canvasCtx.fillStyle = gradient;
      canvasCtx.beginPath();
      canvasCtx.moveTo(0, CY);
      canvasCtx.lineTo(0, CY - pontos[0]);
      for (let i = 0; i < numBarras - 1; i++) {
        let x0 = i * step;
        let y0 = CY - pontos[i];
        let x1 = (i + 1) * step;
        let y1 = CY - pontos[i + 1];
        let cx = (x0 + x1) / 2;
        canvasCtx.bezierCurveTo(cx, y0, cx, y1, x1, y1);
      }
      canvasCtx.lineTo(canvas.width, CY);
      canvasCtx.closePath();
      canvasCtx.fill();
    }
    desenhar();
  }
  function pararVisualizadorAoVivo() {
    if (visualizerAnimFrame) cancelAnimationFrame(visualizerAnimFrame);
    if (visualizerContext && visualizerContext.state !== 'closed') {
      visualizerContext.close();
    }
    const canvas = document.getElementById('live-wave-canvas');
    if (canvas) {
      const ctx = canvas.getContext('2d');
      ctx.clearRect(0, 0, canvas.width, canvas.height); // Limpa a tela
    }
  }
  // =======================================================
  const workerContent =
    "importScripts('https://cdn.jsdelivr.net/npm/opus-media-recorder@latest/encoderWorker.umd.js');";
  const workerBlob = new Blob([workerContent], {
    type: 'application/javascript',
  });
  const workerUrl = URL.createObjectURL(workerBlob);
  const workerOptions = {
    encoderWorkerFactory: function () {
      return new Worker(workerUrl);
    },
    OggOpusEncoderWasmPath:
      'https://cdn.jsdelivr.net/npm/opus-media-recorder@latest/OggOpusEncoder.wasm',
    WebMOpusEncoderWasmPath:
      'https://cdn.jsdelivr.net/npm/opus-media-recorder@latest/WebMOpusEncoder.wasm',
  };
  if (btnGravarAudio) {
    btnGravarAudio.addEventListener('click', () => {
      if (!chatSelecionado)
        return mostrarAlerta('Selecione uma conversa.', 'warning');
      limparEstadoGravacao();
      iniciarSegmentoGravacao();
    });
  }
  if (btnRetomarRec) {
    btnRetomarRec.addEventListener('click', () => {
      if (mediaRecorder && mediaRecorder.state === 'paused') {
        mediaRecorder.resume();
        alternarVisual('gravando');
        iniciarTimerVisual();
        iniciarVisualizadorAoVivo(streamGlobal);
      }
    });
  }
  if (btnPausarRec) {
    btnPausarRec.addEventListener('click', () => {
      if (mediaRecorder && mediaRecorder.state === 'recording') {
        mediaRecorder.pause();
        mediaRecorder.requestData();
        pararVisualizadorAoVivo();
        tempoAcumulado += tempoAtualSegmento;
        tempoAtualSegmento = 0;
        clearInterval(intervaloTimer);
        setTimeout(() => {
          gerarPreviewGeral();
          alternarVisual('preview');
        }, 150);
      }
    });
  }
  if (btnConfirmarRec) {
    btnConfirmarRec.addEventListener('click', () => {
      finalizarEnvio();
    });
  }
  if (btnCancelarRec) {
    btnCancelarRec.addEventListener('click', () => {
      limparEstadoGravacao();
      painelGravacao.classList.add('hidden');
      btnGravarAudio.classList.remove('hidden');
    });
  }
  async function iniciarSegmentoGravacao() {
    try {
      if (!mediaRecorder) {
        streamGlobal = await navigator.mediaDevices.getUserMedia({
          audio: true,
        });
        iniciarVisualizadorAoVivo(streamGlobal);
        // Força o formato OGG limpo!
        const options = { mimeType: 'audio/ogg' };
        // Em vez do gravador normal, chamamos o OpusMediaRecorder
        mediaRecorder = new OpusMediaRecorder(
          streamGlobal,
          options,
          workerOptions
        );
        chunksAudio = [];
        mediaRecorder.ondataavailable = (e) => {
          if (e.data.size > 0) chunksAudio.push(e.data);
        };
        mediaRecorder.onstop = () => {
          if (streamGlobal)
            streamGlobal.getTracks().forEach((track) => track.stop());
        };
        mediaRecorder.start();
      }
      alternarVisual('gravando');
      iniciarTimerVisual();
    } catch (e) {
      console.error(e);
      mostrarAlerta('Erro ao acessar o microfone.', 'error');
    }
  }
  function finalizarEnvio() {
    if (!mediaRecorder) return;
    mediaRecorder.onstop = () => {
      if (streamGlobal)
        streamGlobal.getTracks().forEach((track) => track.stop());
      // O arquivo agora nasce purinho em OGG
      const blobFinal = new Blob(chunksAudio, { type: 'audio/ogg' });
      enviarAudioParaServidor(blobFinal);
      limparEstadoGravacao();
      painelGravacao.classList.add('hidden');
      btnGravarAudio.classList.remove('hidden');
    };
    if (mediaRecorder.state !== 'inactive') {
      mediaRecorder.stop();
    }
  }
  function iniciarTimerVisual() {
    clearInterval(intervaloTimer);
    tempoAtualSegmento = 0;
    timerDisplay.innerText = formatarTempo(tempoAcumulado);
    intervaloTimer = setInterval(() => {
      tempoAtualSegmento++;
      timerDisplay.innerText = formatarTempo(
        tempoAcumulado + tempoAtualSegmento
      );
    }, 1000);
  }
  function alternarVisual(modo) {
    const blocoInputTexto = document.querySelector('.input-linha-texto');
    const btnAnexar = document.getElementById('btn-anexar');
    if (modo === 'gravando') {
      divEstadoGravando.classList.remove('hidden');
      divEstadoPreview.classList.add('hidden');
      btnPausarRec.classList.remove('hidden');
      btnRetomarRec.classList.add('hidden');
      painelGravacao.classList.remove('hidden');
      btnGravarAudio.classList.add('hidden');
      if (blocoInputTexto) blocoInputTexto.classList.add('hidden');
      if (btnAnexar) btnAnexar.classList.add('hidden');
    } else if (modo === 'preview') {
      divEstadoGravando.classList.add('hidden');
      divEstadoPreview.classList.remove('hidden');
      btnPausarRec.classList.add('hidden');
      btnRetomarRec.classList.remove('hidden');
    }
  }
  function gerarPreviewGeral() {
    const blobTotal = new Blob(chunksAudio, { type: 'audio/ogg' });
    const url = URL.createObjectURL(blobTotal);
    if (audioPreviewObj) audioPreviewObj.pause();
    audioPreviewObj = new Audio(url);
    timerPreviewTotal.innerText = formatarTempo(tempoAcumulado);
    barraPreview.style.width = '0%';
    btnPreviewPlay.innerHTML = '<i class="bx bx-play"></i>';
    btnPreviewPlay.onclick = () => {
      if (audioPreviewObj.paused) {
        audioPreviewObj.play();
        btnPreviewPlay.innerHTML = '<i class="bx bx-pause"></i>';
      } else {
        audioPreviewObj.pause();
        btnPreviewPlay.innerHTML = '<i class="bx bx-play"></i>';
      }
    };
    audioPreviewObj.ontimeupdate = () => {
      if (audioPreviewObj.duration && audioPreviewObj.duration !== Infinity) {
        const pct =
          (audioPreviewObj.currentTime / audioPreviewObj.duration) * 100;
        barraPreview.style.width = pct + '%';
      }
    };
    audioPreviewObj.onended = () => {
      btnPreviewPlay.innerHTML = '<i class="bx bx-play"></i>';
      barraPreview.style.width = '0%';
    };
  }
  function limparEstadoGravacao() {
    pararVisualizadorAoVivo();
    if (mediaRecorder && mediaRecorder.state !== 'inactive') {
      mediaRecorder.stop();
    }
    if (streamGlobal) {
      streamGlobal.getTracks().forEach((track) => track.stop());
    }
    mediaRecorder = null;
    streamGlobal = null;
    chunksAudio = [];
    tempoAcumulado = 0;
    tempoAtualSegmento = 0;
    clearInterval(intervaloTimer);
    if (audioPreviewObj) {
      audioPreviewObj.pause();
      audioPreviewObj = null;
    }
    modoPreviewMensagemProntaAudio = null;
    const acoesDireitaRec = document.querySelector('.rec-actions-right');
    if (acoesDireitaRec) acoesDireitaRec.classList.remove('hidden');
    const blocoInputTexto = document.querySelector('.input-linha-texto');
    const btnAnexar = document.getElementById('btn-anexar');
    if (blocoInputTexto) blocoInputTexto.classList.remove('hidden');
    if (btnAnexar) btnAnexar.classList.remove('hidden');
  }
  // 🎙️📝 Preview de áudio de Mensagem Pronta: mesma barra de "pausar e ouvir" da
  // gravação ao vivo, só que sem pausar/retomar (não é uma gravação em andamento)
  async function mostrarPreviewAudioMensagemPronta(msg) {
    if (!chatSelecionado) {
      mostrarAlerta(
        'Selecione uma conversa antes de usar este atalho.',
        'warning'
      );
      return;
    }
    try {
      const respostaArquivo = await fetch(msg.file_path);
      if (!respostaArquivo.ok)
        throw new Error('Falha ao buscar o arquivo salvo da mensagem pronta.');
      const blob = await respostaArquivo.blob();

      // Descarta qualquer gravação/preview em andamento antes de abrir este
      limparEstadoGravacao();
      modoPreviewMensagemProntaAudio = msg;

      const url = URL.createObjectURL(blob);
      audioPreviewObj = new Audio(url);
      timerPreviewTotal.innerText = '0:00';
      barraPreview.style.width = '0%';
      btnPreviewPlay.innerHTML = '<i class="bx bx-play"></i>';
      btnPreviewPlay.onclick = () => {
        if (audioPreviewObj.paused) {
          audioPreviewObj.play();
          btnPreviewPlay.innerHTML = '<i class="bx bx-pause"></i>';
        } else {
          audioPreviewObj.pause();
          btnPreviewPlay.innerHTML = '<i class="bx bx-play"></i>';
        }
      };
      audioPreviewObj.onloadedmetadata = () => {
        if (audioPreviewObj.duration && audioPreviewObj.duration !== Infinity) {
          timerPreviewTotal.innerText = formatarTempo(
            Math.round(audioPreviewObj.duration)
          );
        }
      };
      audioPreviewObj.ontimeupdate = () => {
        if (audioPreviewObj.duration && audioPreviewObj.duration !== Infinity) {
          const pct =
            (audioPreviewObj.currentTime / audioPreviewObj.duration) * 100;
          barraPreview.style.width = pct + '%';
        }
      };
      audioPreviewObj.onended = () => {
        btnPreviewPlay.innerHTML = '<i class="bx bx-play"></i>';
        barraPreview.style.width = '0%';
      };

      // Mostra o painel só com o player (sem os botões de pausar/retomar gravação,
      // que não fazem sentido aqui: não dá pra "continuar gravando" algo pronto)
      divEstadoGravando.classList.add('hidden');
      divEstadoPreview.classList.remove('hidden');
      const acoesDireitaRec = document.querySelector('.rec-actions-right');
      if (acoesDireitaRec) acoesDireitaRec.classList.add('hidden');
      painelGravacao.classList.remove('hidden');
      btnGravarAudio.classList.add('hidden');
      const blocoInputTexto = document.querySelector('.input-linha-texto');
      const btnAnexar = document.getElementById('btn-anexar');
      if (blocoInputTexto) blocoInputTexto.classList.add('hidden');
      if (btnAnexar) btnAnexar.classList.add('hidden');
    } catch (erro) {
      console.error('Erro ao preparar preview da mensagem pronta:', erro);
      mostrarAlerta('Erro ao carregar o áudio da mensagem pronta.', 'error');
    }
  }
  async function confirmarEnvioMensagemProntaAudio() {
    const msg = modoPreviewMensagemProntaAudio;
    if (!msg) return;
    const variaveis = montarMapaVariaveisCrm();
    const legenda = substituirVariaveisCrm(msg.conteudo, variaveis);
    // Já limpa o painel/estado antes de enviar (a mesma UX do "cancelar")
    limparEstadoGravacao();
    painelGravacao.classList.add('hidden');
    btnGravarAudio.classList.remove('hidden');
    await enviarMidiaDeMensagemPronta(msg, legenda);
  }
  async function enviarAudioParaServidor(blob) {
    if (!chatSelecionado) return;
    const btnEnviarPrincipal = document.getElementById('botao-enviar-msg');
    const oldIcon = btnEnviarPrincipal.innerHTML;
    btnEnviarPrincipal.innerHTML = '<i class="fas fa-spinner fa-spin"></i>';
    btnEnviarPrincipal.disabled = true;
    // Enviando limpinho como .ogg
    const arquivoAudio = new File([blob], `audio-gravado.ogg`, {
      type: 'audio/ogg',
    });
    const formData = new FormData();
    formData.append('chatId', chatSelecionado.chat_id);
    formData.append('botId', chatSelecionado.bot_id);
    formData.append('file', arquivoAudio);
    if (mensagemParaResponder) {
      formData.append('quotedMsgId', mensagemParaResponder);
    }
    try {
      const res = await fetch('/api/chat/send', {
        method: 'POST',
        headers: {
          Authorization: `Bearer ${localStorage.getItem('token')}`,
        },
        body: formData,
      });
      const data = await res.json();
      if (!data.success) {
        mostrarAlerta(data.message, 'error');
      } else {
        fecharPreviewResposta();
      }
    } catch (error) {
      mostrarAlerta('Erro ao enviar áudio.', 'error');
    } finally {
      btnEnviarPrincipal.innerHTML = oldIcon;
      btnEnviarPrincipal.disabled = false;
    }
  }
  // ============================================================
  // 😄 LÓGICA DE EMOJIS
  // ============================================================
  const btnEmoji = document.getElementById('btn-emoji-selector');
  const emojiContainer = document.getElementById('emoji-picker-container');
  const emojiPicker = document.querySelector('emoji-picker');
  // Textos do seletor em português ("Buscar emoji", nomes das categorias).
  // Os nomes dos emojis e a busca em português vêm do locale/data-source no
  // HTML. Se o CDN não responder, o seletor só fica com os textos em inglês.
  if (emojiPicker) {
    import('https://cdn.jsdelivr.net/npm/emoji-picker-element@1/i18n/pt_PT.js')
      .then((m) => {
        emojiPicker.i18n = { ...m.default, searchLabel: 'Buscar emoji' };
      })
      .catch(() => {});
  }
  // 1. Abrir/Fechar ao clicar no botão
  if (btnEmoji && emojiContainer) {
    btnEmoji.addEventListener('click', (e) => {
      e.stopPropagation(); // Não deixa o clique fechar imediatamente
      emojiContainer.classList.toggle('hidden');
      // Ajusta tema do emoji picker
      const isDark = document.body.classList.contains('modo-escuro');
      emojiPicker.classList.toggle('dark', isDark);
      emojiPicker.classList.toggle('light', !isDark);
    });
  }
  // 2. Fechar se clicar fora
  document.addEventListener('click', (e) => {
    if (emojiContainer && !emojiContainer.classList.contains('hidden')) {
      // Se o clique NÃO foi dentro do picker E NÃO foi no botão
      if (
        !emojiContainer.contains(e.target) &&
        e.target !== btnEmoji &&
        !btnEmoji.contains(e.target)
      ) {
        emojiContainer.classList.add('hidden');
      }
    }
  });
  // 3. Ao selecionar um emoji
  if (emojiPicker) {
    emojiPicker.addEventListener('emoji-click', (event) => {
      const emoji = event.detail.unicode;
      const input = document.getElementById('input-mensagem-texto');
      if (input) {
        insertAtCursor(input, emoji);
        // Não fecha a janela para permitir selecionar vários
      }
    });
  }
  // Função auxiliar para inserir onde o cursor está (não só no final)
  function insertAtCursor(myField, myValue) {
    if (document.selection) {
      myField.focus();
      sel = document.selection.createRange();
      sel.text = myValue;
    } else if (myField.selectionStart || myField.selectionStart == '0') {
      var startPos = myField.selectionStart;
      var endPos = myField.selectionEnd;
      myField.value =
        myField.value.substring(0, startPos) +
        myValue +
        myField.value.substring(endPos, myField.value.length);
      // Move o cursor para depois do emoji
      myField.selectionStart = startPos + myValue.length;
      myField.selectionEnd = startPos + myValue.length;
      myField.focus();
    } else {
      myField.value += myValue;
      myField.focus();
    }
  }
  // --- LÓGICA DE BOTÕES DO HEADER (FILTRO E TAGS) ---
  const btnFiltro = document.getElementById('btn-filtro-avancado');
  const btnTags = document.getElementById('btn-tags-manager');
  const popupFiltros = document.getElementById('popup-filtros');
  const popupTags = document.getElementById('popup-tags');
  const btnAplicarFiltros = document.getElementById('btn-aplicar-filtros');
  const btnLimparFiltros = document.getElementById('btn-limpar-filtros');
  const areaDataCustom = document.getElementById('area-data-custom');
  // 1. Abrir/Fechar Popup de FILTROS
  if (btnFiltro) {
    btnFiltro.addEventListener('click', (e) => {
      e.stopPropagation();
      // CORREÇÃO: Fecha o popup de TAGS se estiver aberto
      if (popupTags) popupTags.classList.add('hidden');
      if (btnTags) btnTags.classList.remove('ativo');
      // Abrindo: as pílulas de Status Bot mostram o que o dropdown de bots
      // tem marcado AGORA (ele pode ter mudado com o popup fechado).
      if (popupFiltros.classList.contains('hidden')) {
        statusBotEditado = false;
        sincronizarPilulasStatusBot(true);
      }
      popupFiltros.classList.toggle('hidden');
      btnFiltro.classList.toggle('ativo');
      atualizarResumoFiltros();
      ajustarAlturaFiltros();
    });
  }
  // 2. Abrir/Fechar Popup de TAGS
  if (btnTags) {
    btnTags.addEventListener('click', (e) => {
      e.stopPropagation();
      // Fecha o popup de FILTROS se estiver aberto
      if (popupFiltros) popupFiltros.classList.add('hidden');
      if (btnFiltro) btnFiltro.classList.remove('ativo');
      popupTags.classList.toggle('hidden');
      btnTags.classList.toggle('ativo');
    });
  }
  // 3. Fechar ao clicar fora (TUDO DENTRO DO MESMO EVENTO)
  document.addEventListener('click', (e) => {
    // A) Verifica Filtros
    if (popupFiltros && !popupFiltros.classList.contains('hidden')) {
      if (!popupFiltros.contains(e.target) && !btnFiltro.contains(e.target)) {
        popupFiltros.classList.add('hidden');
        btnFiltro.classList.remove('ativo');
      }
    }
    // B) Verifica Tags (CORREÇÃO: AGORA ESTÁ DENTRO DO CLICK)
    if (popupTags && !popupTags.classList.contains('hidden')) {
      // Importante: Não fechar se clicar no SweetAlert (modal de criar tag)
      const clicouNoSwal =
        document.querySelector('.swal2-container') &&
        document.querySelector('.swal2-container').contains(e.target);
      if (
        !popupTags.contains(e.target) &&
        !btnTags.contains(e.target) &&
        !clicouNoSwal
      ) {
        popupTags.classList.add('hidden');
        btnTags.classList.remove('ativo');
      }
    }
    // C) Verifica Funil Suspenso (A MÁGICA NOVA ENTRA AQUI!)
    const dropdownFunil = document.getElementById('dropdown-funil-atendimento');
    const btnToggleFunil = document.getElementById('btn-toggle-funil');
    if (dropdownFunil && !dropdownFunil.classList.contains('hidden')) {
      // Se clicou fora do dropdown e fora do botão que abre ele
      if (
        !dropdownFunil.contains(e.target) &&
        !btnToggleFunil.contains(e.target)
      ) {
        dropdownFunil.classList.add('hidden');
        if (btnToggleFunil) btnToggleFunil.classList.remove('aberto');
      }
    }
  });
  // Botão Limpar Seleção de Tags
  const btnLimparTags = document.getElementById('btn-limpar-tags');
  if (btnLimparTags) {
    btnLimparTags.addEventListener('click', () => {
      // Remove visual (tira o azul das tags)
      document
        .querySelectorAll('.tag-item-filtro.selecionado')
        .forEach((el) => {
          el.classList.remove('selecionado');
        });
      filtrosAtivos.tags = [];
      carregarChats(1, true);
    });
  }
  // 2. Seleção das Opções (Botões Pílula)
  document.querySelectorAll('.opcao-filtro').forEach((btn) => {
    btn.addEventListener('click', (e) => {
      // Status Bot não é escolha única: cada pílula liga/desliga sozinha.
      if (btn.dataset.tipo === 'status-bot') {
        btn.classList.toggle('ativo');
        statusBotEditado = true;
        return;
      }
      const tipo = e.target.dataset.tipo; // ordem, status, periodo
      const valor = e.target.dataset.valor;
      // Remove ativo dos irmãos
      e.target.parentElement
        .querySelectorAll('.opcao-filtro')
        .forEach((b) => b.classList.remove('ativo'));
      // Adiciona ativo neste
      e.target.classList.add('ativo');
      // Atualiza estado local (ainda não aplica)
      if (tipo === 'ordem') filtrosAtivos.ordem = valor;
      if (tipo === 'status') filtrosAtivos.status = valor;
      if (tipo === 'periodo') {
        filtrosAtivos.periodo = valor;
        // Se for custom, mostra inputs de data
        if (valor === 'custom') areaDataCustom.classList.remove('hidden');
        else areaDataCustom.classList.add('hidden');
      }
      atualizarResumoFiltros();
    });
  });
  // 3. Aplicar Filtros
  if (btnAplicarFiltros) {
    btnAplicarFiltros.addEventListener('click', () => {
      if (filtrosAtivos.periodo === 'custom') {
        filtrosAtivos.dataInicio =
          document.getElementById('filtro-data-inicio').value;
        filtrosAtivos.dataFim =
          document.getElementById('filtro-data-fim').value;
      }
      const listaVends = document.getElementById('lista-filtro-vendedores');
      if (listaVends) {
        const selecionados = Array.from(
          listaVends.querySelectorAll('.item-filtro-vendedor.selecionado')
        ).map((el) => el.dataset.username);
        filtrosAtivos.vendedores = selecionados;
      }
      filtrosAtivos.etapas = etapasMarcadasNoFiltro();
      // Status Bot vira marcação no dropdown de bots (só se a vendedora
      // mexeu nas pílulas — senão uma seleção feita à mão no dropdown seria
      // atropelada por um Aplicar de período, por exemplo).
      if (statusBotEditado) {
        const acesa = (v) =>
          !!document.querySelector(
            `[data-tipo="status-bot"][data-valor="${v}"].ativo`
          );
        aplicarStatusBotNoFiltro(acesa('conectado'), acesa('desconectado'));
        statusBotEditado = false;
        sincronizarPilulasStatusBot(true);
      }
      clearTimeout(timerFiltroBots);
      filtroBotsPendente = false;
      popupFiltros.classList.add('hidden');
      btnFiltro.classList.remove('ativo');
      carregarChats(1, true);
      mostrarAlerta('Filtros aplicados!', 'success');
    });
  }
  // 4. Limpar Filtros
  if (btnLimparFiltros) {
    btnLimparFiltros.addEventListener('click', () => {
      filtrosAtivos = {
        ordem: 'desc',
        status: 'todos',
        periodo: 'todos',
        dataInicio: '',
        dataFim: '',
        tags: [],
        vendedores: [],
        etapas: [],
      };
      document
        .querySelectorAll('.tag-item-filtro.selecionado')
        .forEach((el) => el.classList.remove('selecionado'));
      document
        .querySelectorAll('.item-filtro-etapa.selecionado')
        .forEach((el) => el.classList.remove('selecionado'));
      atualizarFiltroFunilEtapa();
      document
        .querySelectorAll('.item-filtro-vendedor.selecionado')
        .forEach((el) => el.classList.remove('selecionado'));
      const contadorVends = document.getElementById(
        'contador-vend-selecionados'
      );
      if (contadorVends) contadorVends.style.display = 'none';
      document
        .querySelectorAll('.opcao-filtro')
        .forEach((btn) => btn.classList.remove('ativo'));
      document
        .querySelector('[data-tipo="ordem"][data-valor="desc"]')
        .classList.add('ativo');
      document
        .querySelector('[data-tipo="status"][data-valor="todos"]')
        .classList.add('ativo');
      document
        .querySelector('[data-tipo="periodo"][data-valor="todos"]')
        .classList.add('ativo');
      // Limpar acende as duas pílulas de Status Bot, e isso devolve ao
      // dropdown os grupos que tinham sido tirados por elas.
      aplicarStatusBotNoFiltro(true, true);
      statusBotEditado = false;
      sincronizarPilulasStatusBot(true);
      clearTimeout(timerFiltroBots);
      filtroBotsPendente = false;
      areaDataCustom.classList.add('hidden');
      popupFiltros.classList.add('hidden');
      atualizarResumoFiltros();
      carregarChats(1, true);
    });
  }
  carregarTagsSistema();
  // Se NÃO for admin, esconde o botão de "Nova Tag" lá do Header (Filtro)
  if (!podeEtiquetas) {
    const btnCriarTagHeader = document.getElementById('btn-criar-tag');
    if (btnCriarTagHeader) btnCriarTagHeader.style.display = 'none';
  }
  // ------------------------------------------------------------
  // A. CARREGAR TAGS DO SISTEMA (Para o Filtro e Dropdown)
  // ------------------------------------------------------------
  async function carregarTagsSistema() {
    try {
      const res = await fetch('/api/tags?tipo=chat');
      todasTagsSistema = await res.json();
      // Atualiza o Popup de Filtro (Header)
      renderizarTagsFiltroHeader();
    } catch (e) {
      console.error('Erro ao carregar tags:', e);
    }
  }
  // ============================================================
  // B. GESTÃO DE TAGS (CRIAR E EDITAR)
  // ============================================================
  // Elementos
  const btnCriarTagHeader = document.getElementById('btn-criar-tag');
  const menuCriarTag = document.getElementById('menu-criar-tag');
  const inputNomeTag = document.getElementById('input-nome-nova-tag');
  const inputCorTag = document.getElementById('input-cor-nova-tag');
  const previewCorTag = document.getElementById('preview-cor-tag');
  const btnSalvarTag = document.getElementById('btn-confirmar-tag');
  const btnCancelarTag = document.getElementById('btn-cancelar-tag');
  // VARIÁVEL DE ESTADO PARA EDIÇÃO
  let idTagEmEdicao = null; // null = criando nova | número = editando existente
  // 1. Botão "Nova" (Modo Criação)
  if (btnCriarTagHeader) {
    btnCriarTagHeader.addEventListener('click', (e) => {
      e.stopPropagation();
      if (!podeEtiquetas) return mostrarAlerta('Apenas administradores.', 'warning');
      // Reseta para modo CRIAÇÃO
      idTagEmEdicao = null;
      inputNomeTag.value = '';
      inputCorTag.value = '#02abf8';
      previewCorTag.style.backgroundColor = '#02abf8';
      // Mostra o menu
      menuCriarTag.classList.remove('hidden');
      inputNomeTag.focus();
    });
  }

  // bate-papo.js
  // 2. Renderizar Lista (AGORA COM CONTAGEM DE CHATS E FILTRO DA URL)
  function renderizarTagsFiltroHeader() {
    const container = document.querySelector('.lista-tags-scroll');
    if (!container) return;
    container.innerHTML = '';
    if (todasTagsSistema.length === 0) {
      container.innerHTML =
        '<div style="padding:10px; color:#aaa; font-size:12px;">Nenhuma tag criada.</div>';
      return;
    }
    todasTagsSistema.forEach((tag) => {
      const div = document.createElement('div');
      div.className = 'tag-item-filtro';

      // 👇 SE A TAG ESTIVER NO NOSSO FILTRO ATUAL, JÁ PINTA ELA DE AZUL
      if (filtrosAtivos.tags.includes(tag.id)) {
        div.classList.add('selecionado');
      }

      div.dataset.id = tag.id;
      // Garante que count seja pelo menos 0 se vier nulo

      const contagem = tag.count || 0;
      // Ações (Só Admin vê)
      let acoesHtml = '';
      if (podeEtiquetas) {
        acoesHtml = `
      <div class="acoes-tag-item">
        <i class="bx bx-pencil btn-edit-tag-sistema" title="Editar" onclick="prepararEdicaoTag(event, ${tag.id}, '${textoParaOnclick(tag.name)}', '${textoParaOnclick(tag.color)}')"></i>
        <i class="bx bx-trash btn-delete-tag-sistema" title="Excluir" onclick="excluirTagSistema(event, ${tag.id})"></i>
      </div>
    `;
      }
      // HTML DO ITEM (Com o contador no meio)
      div.innerHTML = `
    <div style="display:flex; align-items:center; gap:10px; flex:1;">
      <span class="tag-cor" style="background: ${escaparHtmlChat(tag.color)};"></span>
      <span style="font-size: 11px; color: #999; font-weight: 500; min-width: 20px;">
        ${contagem}
      </span>
      <span class="tag-nome">${escaparHtmlChat(tag.name)}</span>
    </div>
    ${acoesHtml}
  `;
      // Clique para filtrar (Logica de seleção e filtro automático)
      div.addEventListener('click', (e) => {
        // Se clicou no lixo ou lápis, não faz nada (só edita/exclui)
        if (e.target.closest('.acoes-tag-item')) return;
        // 1. Marca/Desmarca visualmente
        div.classList.toggle('selecionado');
        // 2. Pega todos os IDs que estão marcados agora
        const tagsSelecionadas = Array.from(
          document.querySelectorAll('.tag-item-filtro.selecionado')
        ).map((el) => parseInt(el.dataset.id));
        // 3. Salva no estado global
        filtrosAtivos.tags = tagsSelecionadas;
        // 4. ATUALIZA A LISTA NA HORA (Buscando do banco)
        carregarChats(1, true);
      });
      container.appendChild(div);
    });
  }
  // 3. Função Global para Preparar Edição (Chamada pelo onclick do lápis)
  window.prepararEdicaoTag = function (e, id, nome, cor) {
    e.stopPropagation(); // Não fecha o menu
    // Define modo EDIÇÃO
    idTagEmEdicao = id;
    // Preenche os campos
    inputNomeTag.value = nome;
    inputCorTag.value = cor;
    previewCorTag.style.backgroundColor = cor;
    // Abre o menu mini
    menuCriarTag.classList.remove('hidden');
    inputNomeTag.focus();
  };
  // 4. Atualizar Preview de Cor
  if (inputCorTag && previewCorTag) {
    inputCorTag.addEventListener('input', (e) => {
      previewCorTag.style.backgroundColor = e.target.value;
    });
  }
  // 5. Cancelar
  if (btnCancelarTag) {
    btnCancelarTag.addEventListener('click', () => {
      menuCriarTag.classList.add('hidden');
      idTagEmEdicao = null; // Limpa estado
      inputNomeTag.value = '';
    });
  }
  // 6. SALVAR (CREATE ou UPDATE)
  if (btnSalvarTag) {
    btnSalvarTag.addEventListener('click', async () => {
      const nome = inputNomeTag.value.trim();
      const cor = inputCorTag.value;
      if (!nome) return mostrarAlerta('Digite um nome.', 'warning');
      // Feedback visual
      const originalIcon = btnSalvarTag.innerHTML;
      btnSalvarTag.innerHTML = '<i class="fas fa-spinner fa-spin"></i>';
      btnSalvarTag.disabled = true;
      try {
        let url = '/api/tags';
        let method = 'POST';
        // SE TIVER ID, É EDIÇÃO
        if (idTagEmEdicao) {
          url = `/api/tags/${idTagEmEdicao}`;
          method = 'PUT';
        }
        const res = await fetch(url, {
          method: method,
          headers: {
            'Content-Type': 'application/json',
            Authorization: `Bearer ${localStorage.getItem('token')}`,
          },
          body: JSON.stringify({ name: nome, color: cor, tipo: 'chat' }),
        });
        const data = await res.json();
        if (data.success) {
          // Sucesso
          menuCriarTag.classList.add('hidden');
          inputNomeTag.value = '';
          idTagEmEdicao = null; // Reseta
          carregarTagsSistema(); // Recarrega lista
          // Se estiver editando a tag de um cliente aberto na tela, recarrega as tags dele também
          if (chatSelecionado) carregarTagsDoCliente(chatSelecionado);
        } else {
          mostrarAlerta(data.message, 'error');
        }
      } catch (e) {
        mostrarAlerta('Erro ao salvar tag.', 'error');
      } finally {
        btnSalvarTag.innerHTML = originalIcon;
        btnSalvarTag.disabled = false;
      }
    });
  }
  // ------------------------------------------------------------
  // C. EXCLUIR TAG DO SISTEMA (SÓ ADMIN)
  // ------------------------------------------------------------
  window.excluirTagSistema = async function (e, id) {
    e.stopPropagation(); // Não selecionar a linha
    if (!podeEtiquetas) return;
    const confirmou = await mostrarConfirmacao(
      'Isso removerá esta tag de TODOS os clientes.'
    );
    if (!confirmou) return;
    try {
      await fetch(`/api/tags/${id}`, {
        method: 'DELETE',
        headers: { Authorization: `Bearer ${localStorage.getItem('token')}` },
      });
      carregarTagsSistema();
      // Se o cliente aberto tinha essa tag, atualiza ele também
      if (chatSelecionado) carregarTagsDoCliente(chatSelecionado);
    } catch (e) {
      console.error(e);
    }
  };
  // QUEM NÃO RECEBE DISPARO (servicos/nao-disparar.js): chip ao lado das
  // etiquetas quando o cliente mandou parar ou disse que o número não é dele.
  // Quem dispara (rcs.disparo) vê o X e tira da lista; o servidor confere.
  const chipNaoDisparar = document.getElementById('crm-nao-disparar');
  async function carregarNaoDisparar(chat) {
    if (!chipNaoDisparar) return;
    chipNaoDisparar.style.display = 'none';
    const tel = String(chat.chat_id || '').replace(/\D/g, '');
    if (!tel) return;
    try {
      const r = await fetch(`/api/rcs/nao-disparar/${tel}`, {
        headers: { Authorization: `Bearer ${localStorage.getItem('token')}` },
      });
      const d = await r.json();
      // A conversa pode ter trocado enquanto a resposta vinha.
      if (!d.ok || !d.na_lista || String(chatSelecionado?.chat_id || '').replace(/\D/g, '') !== tel) return;
      chipNaoDisparar.innerHTML =
        '<i class="bx bx-bell-off"></i> Não recebe disparo' +
        (d.pode_tirar ? ' <i class="bx bx-x" title="Tirar da lista: volta a receber disparo"></i>' : '');
      chipNaoDisparar.title = `O cliente ${d.rotulo}. Fica fora do Disparo RCS e do disparo de WhatsApp.`;
      chipNaoDisparar.style.display = 'inline-flex';
      const x = chipNaoDisparar.querySelector('.bx-x');
      if (x) x.onclick = () => tirarDaListaNaoDisparar(tel);
    } catch (e) {
      // sem chip
    }
  }
  async function tirarDaListaNaoDisparar(tel) {
    try {
      const r = await fetch(`/api/rcs/nao-disparar/${tel}`, {
        method: 'DELETE',
        headers: { Authorization: `Bearer ${localStorage.getItem('token')}` },
      });
      const d = await r.json();
      if (!d.ok) throw new Error(d.erro || 'falha');
      chipNaoDisparar.style.display = 'none';
    } catch (e) {
      alert('Não deu pra tirar da lista: ' + e.message);
    }
  }
  // 1. Carregar Tags do Cliente (Chamar ao abrirConversa)
  async function carregarTagsDoCliente(chat) {
    if (!containerTagsCrm) return;
    carregarNaoDisparar(chat);
    // Limpa e põe loading
    containerTagsCrm.innerHTML = '<i class="fas fa-spinner fa-spin"></i>';
    try {
      const res = await fetch(`/api/chat/${chat.chat_id}/${chat.bot_id}/tags`, {
        headers: { Authorization: `Bearer ${localStorage.getItem('token')}` },
      });
      const data = await res.json();
      tagsDoClienteAtual = data.tags || [];
      renderizarTagsCrm();
      // MÁGICA DE SINCRONIA: Repassa as tags do CRM direto para a Lista Lateral!
      const chatNaMemoria = todosChats.find((c) => c.chat_id === chat.chat_id);
      if (chatNaMemoria) {
        chatNaMemoria.tags = tagsDoClienteAtual;
        atualizarVisualTagsLateral(chat.chat_id, tagsDoClienteAtual);
      }
    } catch (e) {
      containerTagsCrm.innerHTML = 'Erro ao carregar.';
    }
  }
  // Função auxiliar para injetar a tag no HTML da lista lateral na hora
  function atualizarVisualTagsLateral(chatId, tags) {
    const itemLista = document.querySelector(
      `.chat-item[data-chat-id="${chatId}"]`
    );
    if (!itemLista) return;
    const containerTags = itemLista.querySelector('.chat-item-topo-tags');
    if (!containerTags) return;
    if (!tags || tags.length === 0) {
      containerTags.innerHTML = '';
      return;
    }
    const precisaDeLoop = tags.length > 3;
    const listaFinalTags = precisaDeLoop ? [...tags, ...tags, ...tags] : tags;
    const classeLoop = precisaDeLoop ? 'infinite-loop' : '';
    let htmlTags = `<div class="linha-tags-texto ${classeLoop}"><div class="tags-track">`;
    listaFinalTags.forEach((tag) => {
      htmlTags += `<span class="tag-texto-lista" style="background-color: ${escaparHtmlChat(tag.color)}20; color: ${escaparHtmlChat(tag.color)};">${escaparHtmlChat(tag.name)}</span>`;
    });
    htmlTags += `</div></div>`;
    containerTags.innerHTML = htmlTags;
  }
  // 2. Renderizar (Mostrar os Chips coloridos no CRM)
  function renderizarTagsCrm() {
    containerTagsCrm.innerHTML = '';
    if (tagsDoClienteAtual.length === 0) {
      containerTagsCrm.innerHTML =
        '<span style="color:#aaa; font-size:12px;">Sem tags.</span>';
      return;
    }
    tagsDoClienteAtual.forEach((tag) => {
      const chip = document.createElement('span');
      chip.className = 'tag-chip-crm'; // CSS abaixo
      chip.style.backgroundColor = tag.color + '20'; // Cor com transparência (fundo)
      chip.style.color = tag.color; // Cor do texto forte
      chip.innerHTML = `
      ${escaparHtmlChat(tag.name)}
      <i class="bx bx-x" onclick="vincularTagCliente(${tag.id}, 'remove')"></i>
    `;
      containerTagsCrm.appendChild(chip);
    });
  }
  // --- LÓGICA DE VINCULAR TAG AO CLIENTE ---
  // 1. Abrir/Fechar o Dropdown e popular com as tags disponíveis
  if (btnAddTagCrm) {
    btnAddTagCrm.addEventListener('click', (e) => {
      e.stopPropagation();
      // Se o dropdown já estiver visível, apenas fecha
      if (!dropdownTagsCrm.classList.contains('hidden')) {
        dropdownTagsCrm.classList.add('hidden');
        return;
      }
      // Popula o dropdown com as tags que o cliente AINDA NÃO TEM
      dropdownTagsCrm.innerHTML = '';
      const disponiveis = todasTagsSistema.filter(
        (sisTag) =>
          !tagsDoClienteAtual.some((cliTag) => cliTag.id === sisTag.id)
      );
      if (disponiveis.length === 0) {
        dropdownTagsCrm.innerHTML =
          '<div style="padding:10px; font-size:12px; color:#999;">Nenhuma tag nova disponível.</div>';
      } else {
        disponiveis.forEach((tag) => {
          const item = document.createElement('div');
          item.className = 'item-tag-selecao';
          item.innerHTML = `
                    <span class="bolinha-cor-mini" style="background-color: ${escaparHtmlChat(tag.color)}"></span>
                    <span class="nome-tag-selecao">${escaparHtmlChat(tag.name)}</span>
                `;
          item.onclick = () => vincularTagCliente(tag.id, 'add');
          dropdownTagsCrm.appendChild(item);
        });
      }
      dropdownTagsCrm.classList.remove('hidden');
    });
  }
  // 2. Função para salvar o vínculo no Banco de Dados
  window.vincularTagCliente = async function (tagId, action) {
    if (!chatSelecionado) return;
    try {
      const response = await fetch('/api/chat/tags/toggle', {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          Authorization: `Bearer ${localStorage.getItem('token')}`,
        },
        body: JSON.stringify({
          chatId: chatSelecionado.chat_id,
          botId: chatSelecionado.bot_id,
          tagId: tagId,
          action: action, // 'add' ou 'remove'
        }),
      });
      const data = await response.json();
      if (data.success) {
        // Fecha o dropdown se for uma adição
        if (action === 'add') dropdownTagsCrm.classList.add('hidden');
        // Recarrega as tags do cliente para atualizar o CRM e a lista lateral
        await carregarTagsDoCliente(chatSelecionado);
        // Atualiza a bolinha na lista lateral sem recarregar a página toda
        atualizarTagsNaListaLateral(chatSelecionado.chat_id, tagId, action);
      }
    } catch (e) {
      console.error('Erro ao vincular tag:', e);
      mostrarAlerta('Erro ao atualizar etiquetas.', 'error');
    }
  };
  // 3. Auxiliar para atualizar a bolinha na lista lateral em tempo real
  function atualizarTagsNaListaLateral(chatId, tagId, action) {
    const chatNaMemoria = todosChats.find((c) => c.chat_id === chatId);
    if (!chatNaMemoria) return;
    if (!chatNaMemoria.tags) chatNaMemoria.tags = [];
    if (action === 'add') {
      const tagDados = todasTagsSistema.find((t) => t.id === tagId);
      // 👇 A MÁGICA: Só adiciona se a tag AINDA NÃO EXISTIR no array!
      if (tagDados && !chatNaMemoria.tags.some((t) => t.id === tagId)) {
        chatNaMemoria.tags.push(tagDados);
      }
    } else {
      chatNaMemoria.tags = chatNaMemoria.tags.filter((t) => t.id !== tagId);
    }
    renderizarLista(todosChats); // Re-renderiza a lista lateral com as novas bolinhas
  }
  // ============================================================
  // FUNÇÃO PARA BUSCAR DADOS DO CLIENTE (INTEGRAÇÃO DINÂMICA)
  // ============================================================
  async function carregarDadosDoCliente(chatId) {
    // Limpa o selo do motivo antes de buscar: sem isso, trocar de conversa
    // deixaria o motivo do cliente anterior na tela até a resposta chegar.
    atualizarSeloMotivoPerdido(null);
    if (crmNome) crmNome.value = 'Carregando...';
    if (crmTelefone) crmTelefone.value = '...';
    if (crmNotas) crmNotas.value = 'Carregando...';
    // Mostra loading nos campos dinâmicos
    if (containerCamposDinamicos) {
      containerCamposDinamicos.innerHTML =
        '<div style="text-align:center; padding: 10px; color: #999;"><i class="fas fa-spinner fa-spin"></i> Carregando campos...</div>';
    }
    try {
      const numeroPuro = chatId.replace(/\D/g, '');
      const response = await fetch(`/api/crm/${numeroPuro}`, {
        headers: { Authorization: `Bearer ${localStorage.getItem('token')}` },
      });
      const json = await response.json();
      if (json.success && json.data) {
        const dados = json.data;
        // Motivo da perda: o backend só mantém essa coluna preenchida enquanto
        // o lead está em PERDIDO, então o próprio valor decide se o selo
        // aparece. Precisa da lista carregada pra traduzir o id em nome.
        await carregarMotivosPerdidoChat();
        atualizarSeloMotivoPerdido(dados.motivo_perdido);
        // 👇 CORREÇÃO: Já puxa o JSON customizado aqui em cima para ler o telefone dele
        const dadosCustomizados =
          typeof dados.campos_customizados === 'string'
            ? JSON.parse(dados.campos_customizados)
            : dados.campos_customizados || {};
        // Preenche Fixos
        if (crmNome) crmNome.value = dados.nome || '';
        if (crmCpf) {
          let cpfBanco = dados.cpf || '';
          crmCpf.value = cpfBanco
            ? window.aplicarMascaraCPF(window.limparEPreencherCPF(cpfBanco))
            : '';
        }
        if (crmNotas) crmNotas.value = dados.notas || '';
        // 👇 CORREÇÃO: Tenta ler o telefone novo (do JSON) ou o velho (se ainda existir)
        if (crmTelefone) {
          const foneDb =
            dadosCustomizados.telefone || dados.telefone_extra || '';
          crmTelefone.value = foneDb ? foneDb : formatarTelefoneBR(numeroPuro);
        }
        if (crmEmail) {
          crmEmail.value = dadosCustomizados.email || dados.email || '';
        }
        // Desenha os campos dinâmicos e injeta os valores salvos!
        const colunasSoltas = [
          'banco',
          'idade',
          'situacao_funcional',
          'orgao',
          'liberado',
          'parcela',
          'reembolso',
        ];
        colunasSoltas.forEach((col) => {
          if (
            dados[col] &&
            !dadosCustomizados[col] &&
            !dadosCustomizados[col.charAt(0).toUpperCase() + col.slice(1)]
          ) {
            dadosCustomizados[col] = dados[col];
          }
        });
        if (typeof desenharInputsDinamicosCrm === 'function') {
          desenharInputsDinamicosCrm(dadosCustomizados);
        }
      } else {
        // Se não achou no banco, limpa tudo e desenha os campos vazios
        if (crmNome) crmNome.value = '';
        if (crmNotas) crmNotas.value = '';
        if (crmTelefone) {
          crmTelefone.value = formatarTelefoneBR(numeroPuro);
        }
        if (typeof desenharInputsDinamicosCrm === 'function') {
          desenharInputsDinamicosCrm({});
        }
      }
    } catch (erro) {
      console.error('Erro ao carregar CRM:', erro);
      if (crmNome) crmNome.value = 'Erro ao carregar';
    }
  }
  // ============================================================
  // 📄 EXPORTAR CONVERSA (PDF VISUAL)
  // Cole isso DENTRO do document.addEventListener
  // ============================================================
  const btnExportar = document.getElementById('btn-exportar-chat');
  if (btnExportar) {
    if (!Acesso.pode('chat.exportar')) {
      btnExportar.style.display = 'none';
    }
    btnExportar.addEventListener('click', async () => {
      // 1. Verifica se tem chat aberto
      if (!chatSelecionado) {
        return mostrarAlerta(
          'Selecione uma conversa para exportar.',
          'warning'
        );
      }
      // 2. Feedback Visual (Spinner no botão)
      const iconOriginal = btnExportar.innerHTML;
      btnExportar.innerHTML = `<i class='fas fa-spinner fa-spin'></i>`;
      btnExportar.disabled = true;
      try {
        // 3. Chama a Rota de PDF (Ajustada para o novo endpoint)
        const response = await fetch(
          `/api/reports/chat-history-pdf?chatId=${chatSelecionado.chat_id}&botId=${chatSelecionado.bot_id}`,
          {
            method: 'GET',
            headers: {
              Authorization: `Bearer ${localStorage.getItem('token')}`,
            },
          }
        );
        if (response.ok) {
          // 4. Converte a resposta em Arquivo (Blob)
          const blob = await response.blob();
          const url = window.URL.createObjectURL(blob);
          // 5. Cria o link de download invisível
          const a = document.createElement('a');
          a.href = url;
          // Define o nome do arquivo com a extensão .pdf
          const nomeLimpo = (chatSelecionado.name || 'Cliente').replace(
            /[^a-z0-9]/gi,
            '_'
          );
          a.download = `Historico_Chat_${nomeLimpo}.pdf`;
          document.body.appendChild(a);
          a.click();
          // 6. Limpeza
          window.URL.revokeObjectURL(url);
          document.body.removeChild(a);
          mostrarAlerta('PDF gerado com sucesso!', 'success');
        } else {
          const text = await response.text();
          mostrarAlerta('Erro ao gerar PDF: ' + text, 'error');
        }
      } catch (error) {
        console.error('Erro no download:', error);
        mostrarAlerta('Erro de conexão ao baixar PDF.', 'error');
      } finally {
        // 7. Restaura o ícone original do botão
        btnExportar.innerHTML = iconOriginal;
        btnExportar.disabled = false;
      }
    });
  }
  // ============================================================
  // 📞 LIGAR PRO CLIENTE (js/telefone.js; só aparece pra quem tem linha)
  // ============================================================
  const btnLigar = document.getElementById('btn-ligar');
  if (btnLigar) {
    btnLigar.addEventListener('click', () => {
      if (!chatSelecionado || !window.TelefoneCRM) return;
      const numero = chatSelecionado.chat_id.replace(/\D/g, '');
      const nome = chatSelecionado.crm_nome || chatSelecionado.name || '';
      window.TelefoneCRM.ligar({
        numero,
        instancia: chatSelecionado.bot_id,
        nome: nome && !nome.includes('@') && nome.replace(/\D/g, '') !== numero ? nome : '',
      });
    });
  }
  // ============================================================
  // ✅ BOTÃO MARCAR COMO LIDO (NOVO)
  // ============================================================
  const btnMarcarLido = document.getElementById('btn-marcar-lido');
  if (btnMarcarLido) {
    btnMarcarLido.addEventListener('click', async () => {
      if (!chatSelecionado) return;
      // Feedback visual (Spinner)
      const iconOriginal = btnMarcarLido.innerHTML;
      const iconElement = btnMarcarLido.querySelector('i');
      if (iconElement) iconElement.className = 'fas fa-spinner fa-spin';
      btnMarcarLido.disabled = true;
      try {
        // Chama a API
        const res = await fetch(`/api/chats/${chatSelecionado.chat_id}/read`, {
          method: 'POST',
          headers: {
            'Content-Type': 'application/json',
            Authorization: `Bearer ${localStorage.getItem('token')}`,
          },
        });
        if (res.ok) {
          // 1. Atualiza memória
          chatSelecionado.unread_count = 0;
          // 2. Remove a bolinha da lista lateral
          const itemLista = document.querySelector(
            `.chat-item[data-chat-id="${chatSelecionado.chat_id}"]`
          );
          if (itemLista) {
            const badge = itemLista.querySelector('.badge-unread');
            if (badge) badge.remove();
          }
          // 3. Atualiza as abas (tabs) dos canais se houver contador nelas
          if (chatSelecionado.canais) {
            chatSelecionado.canais.forEach((c) => (c.unread = 0));
            renderizarAbas(chatSelecionado.canais);
          }
          // 4. Esconde o botão pois já está lido
          btnMarcarLido.style.display = 'none';
          // 👇 5. A MÁGICA NOVA: Atualiza a bolinha vermelha lá do topo! 👇
          const badgeTopo = document.getElementById('badge-filtro-unread');
          if (badgeTopo && !badgeTopo.classList.contains('hidden')) {
            // Pega o número atual, tira o '+' se tiver, e diminui 1
            let countAtual =
              parseInt(badgeTopo.textContent.replace('+', '')) || 0;
            let novoCount = Math.max(0, countAtual - 1);
            if (novoCount > 0) {
              badgeTopo.textContent = novoCount > 999 ? '+999' : novoCount;
              badgeTopo.title = `${novoCount} conversas pendentes neste filtro`;
            } else {
              badgeTopo.classList.add('hidden'); // Se zerou, esconde a bolinha do topo
            }
          }
        } else {
          console.error('Erro ao marcar como lido');
        }
      } catch (error) {
        console.error('Erro de conexão:', error);
      } finally {
        // Restaura botão (embora ele vá sumir se der certo)
        if (iconElement) iconElement.className = 'fas fa-check-double';
        btnMarcarLido.innerHTML = iconOriginal;
        btnMarcarLido.disabled = false;
      }
    });
  }
  // Função auxiliar para mostrar/esconder o botão
  function atualizarBotaoLer(chat) {
    if (!btnMarcarLido) return;
    // Soma o total de unread de todos os canais ou usa o total geral
    let totalUnread = chat.unread_count || 0;
    // Se tiver canais, garante a soma atualizada
    if (chat.canais && Array.isArray(chat.canais)) {
      totalUnread = chat.canais.reduce(
        (acc, curr) => acc + (curr.unread || 0),
        0
      );
    }
    if (totalUnread > 0) {
      btnMarcarLido.style.display = 'inline-flex';
    } else {
      btnMarcarLido.style.display = 'none';
    }
  }
  // ============================================================
  // ❌ FECHAR CONVERSA (BOTÃO X E TECLA ESC)
  // ============================================================
  const btnFecharChat = document.getElementById('btn-fechar-chat');
  function fecharConversaAtual() {
    if (!chatSelecionado) return; // Se não tem nada aberto, não faz nada
    // 1. Limpa a variável global
    chatSelecionado = null;
    // 2. Manipula a Interface (Esconde Chat / Mostra Vazio)
    const areaAtiva = document.getElementById('area-conversa-ativa');
    const areaVazia = document.getElementById('area-conversa-vazia');
    if (areaAtiva) areaAtiva.classList.add('escondido');
    if (areaVazia) areaVazia.classList.remove('escondido');
    // 3. Remove a classe 'ativo' da lista lateral
    document.querySelectorAll('.chat-item').forEach((el) => {
      el.classList.remove('ativo');
    });
    // 👇 4. A MÁGICA DA SEGURANÇA: Fecha o painel do CRM junto! 👇
    const painelInfo = document.getElementById('painel-info-cliente');
    if (painelInfo && !painelInfo.classList.contains('fechado')) {
      painelInfo.classList.add('fechado');
    }
    // Fecha o Modal de Consulta se estiver aberto
    const modalConsulta = document.getElementById('modal-consulta-api');
    const btnConsulta = document.getElementById('btn-abrir-consulta');
    if (modalConsulta && !modalConsulta.classList.contains('hidden')) {
      modalConsulta.classList.add('hidden');
      if (btnConsulta) btnConsulta.classList.remove('ativo');
    }
    // 5. Limpa título da aba/janela
    document.title = 'Nexus CRM - Bate-papo';
  }
  if (btnFecharChat) {
    btnFecharChat.addEventListener('click', fecharConversaAtual);
  }
  // 👑 GERENCIADOR UNIVERSAL DE 'ESC' (A HIERARQUIA PERFEITA)
  document.addEventListener('keydown', (e) => {
    if (e.key === 'Escape') {
      // NÍVEL 1: Ocupa a tela inteira (Visualizador de Mídia Nativo)
      const viewer = document.getElementById('media-viewer');
      if (viewer && viewer.classList.contains('ativo')) {
        if (typeof fecharVisualizador === 'function') fecharVisualizador();
        return; // Mata a execução aqui!
      }
      let fechouAlgumModalFlutuante = false;
      // NÍVEL 2: Modais Gigantes no meio da tela
      const modaisCentro = [
        {
          id: 'modal-galeria-midias',
          fecharFunc: () => fecharGaleriaCompleta(),
        },
        { id: 'modal-add-cliente' },
        { id: 'modal-config-crm' },
        { id: 'modal-ai-prompt' },
      ];
      modaisCentro.forEach((modalInfo) => {
        const el = document.getElementById(modalInfo.id);
        if (el && !el.classList.contains('hidden')) {
          if (modalInfo.fecharFunc) modalInfo.fecharFunc();
          else el.classList.add('hidden');
          fechouAlgumModalFlutuante = true;
        }
      });
      // NÍVEL 3: Menus Suspensos, Popups e Caixas de Preview
      const menusSuspensos = [
        { id: 'popup-tags', btnId: 'btn-tags-manager', classBtn: 'ativo' },
        {
          id: 'popup-filtros',
          btnId: 'btn-filtro-avancado',
          classBtn: 'ativo',
        },
        { id: 'emoji-picker-container' },
        { id: 'magic-wand-menu' },
        { id: 'dropdown-tags-crm' },
        {
          id: 'dropdown-funil-atendimento',
          btnId: 'btn-toggle-funil',
          classBtn: 'aberto',
        },
        {
          id: 'modal-consulta-api',
          btnId: 'btn-abrir-consulta',
          classBtn: 'ativo',
        }, // <-- ADICIONADO AQUI
        {
          id: 'reply-preview-container',
          fecharFunc: () => {
            if (typeof fecharPreviewResposta === 'function')
              fecharPreviewResposta();
          },
        },
      ];
      menusSuspensos.forEach((menu) => {
        const el = document.getElementById(menu.id);
        if (el && !el.classList.contains('hidden')) {
          if (menu.fecharFunc) {
            menu.fecharFunc();
          } else {
            el.classList.add('hidden');
          }
          if (menu.btnId) {
            const btn = document.getElementById(menu.btnId);
            if (btn) btn.classList.remove(menu.classBtn);
          }
          fechouAlgumModalFlutuante = true;
        }
      });
      // SE QUALQUER MODAL OU POPUP FOI FECHADO, A GENTE PARA AQUI. (Não fecha o CRM nem o Chat)
      if (fechouAlgumModalFlutuante) return;
      if (typeof modoSelecaoMassa !== 'undefined' && modoSelecaoMassa) {
        window.desativarModoSelecao();
        return;
      }
      const painelInfo = document.getElementById('painel-info-cliente');
      if (painelInfo && !painelInfo.classList.contains('fechado')) {
        painelInfo.classList.add('fechado');
        return; // PARA AQUI.
      }
      // NÍVEL 5: A Conversa em Si (A última coisa a fechar)
      fecharConversaAtual();
    }
  });
  // ============================================================
  // LÓGICA: ADICIONAR NOVO CLIENTE (COM DROPDOWNS CUSTOMIZADOS E KANBAN)
  // ============================================================
  const btnAddCliente = document.getElementById('btn-add-cliente');
  const modalAddCliente = document.getElementById('modal-add-cliente');
  const btnFecharModalCliente = document.getElementById(
    'btn-fechar-modal-cliente'
  );
  const btnSalvarNovoCliente = document.getElementById(
    'btn-salvar-novo-cliente'
  );
  const boxVendedor = document.getElementById('box-vendedor');
  const hiddenVendedor = document.getElementById('hidden-novo-vendedor');
  const hiddenBot = document.getElementById('hidden-novo-bot');
  const hiddenTag = document.getElementById('hidden-novo-tag');
  const hiddenFunil = document.getElementById('hidden-novo-funil'); // NOVO
  const optionsVendedor = document.getElementById('options-novo-vendedor');
  const optionsBot = document.getElementById('options-novo-bot');
  const optionsTag = document.getElementById('options-novo-tag');
  const optionsFunil = document.getElementById('options-novo-funil'); // NOVO
  function setupCustomDropdownInModal(
    triggerId,
    optionsId,
    hiddenId,
    isTagSelect = false
  ) {
    const trigger = document.getElementById(triggerId);
    const optionsContainer = document.getElementById(optionsId);
    const hiddenInput = document.getElementById(hiddenId);
    if (!trigger || !optionsContainer || !hiddenInput) return;
    trigger.addEventListener('click', (e) => {
      e.stopPropagation();
      document.querySelectorAll('.custom-options-container').forEach((opt) => {
        if (opt !== optionsContainer) opt.classList.add('hidden');
      });
      optionsContainer.classList.toggle('hidden');
    });
    optionsContainer.addEventListener('click', (e) => {
      const option = e.target.closest('.custom-option');
      if (!option) return;
      hiddenInput.value = option.dataset.value;
      const displaySpan = trigger.querySelector('span');
      if (isTagSelect && displaySpan) {
        const spanPill = option.querySelector('span');
        if (spanPill && spanPill.classList.contains('tag-pill-dropdown')) {
          displaySpan.className = spanPill.className;
          displaySpan.style.backgroundColor = spanPill.style.backgroundColor;
          displaySpan.style.color = spanPill.style.color;
          displaySpan.style.border = spanPill.style.border;
          displaySpan.innerHTML = spanPill.innerHTML;
        } else {
          displaySpan.className = 'tag-pill-sem-tag';
          displaySpan.style.backgroundColor = '';
          displaySpan.style.color = '';
          displaySpan.style.border = '';
          displaySpan.textContent = 'SEM TAG';
        }
      } else if (displaySpan) {
        if (triggerId === 'trigger-novo-bot') {
          const nomeBot = option.querySelector('.nome-bot-dropdown');
          displaySpan.textContent = nomeBot
            ? nomeBot.textContent
            : option.textContent.trim();
        } else if (triggerId === 'trigger-novo-funil') {
          // Pega o texto da opção que está dentro da span para ficar limpo
          const nomeEtapa = option.querySelector('span:last-child');
          displaySpan.textContent = nomeEtapa
            ? nomeEtapa.textContent
            : option.textContent.trim();
        } else {
          displaySpan.textContent = option.textContent.trim();
        }
      }
      optionsContainer.classList.add('hidden');
    });
    document.addEventListener('click', (e) => {
      if (!trigger.contains(e.target) && !optionsContainer.contains(e.target)) {
        optionsContainer.classList.add('hidden');
      }
    });
  }
  // Inicializa os comportamentos dos 4 dropdowns
  setupCustomDropdownInModal(
    'trigger-novo-vendedor',
    'options-novo-vendedor',
    'hidden-novo-vendedor'
  );
  setupCustomDropdownInModal(
    'trigger-novo-bot',
    'options-novo-bot',
    'hidden-novo-bot'
  );
  setupCustomDropdownInModal(
    'trigger-novo-tag',
    'options-novo-tag',
    'hidden-novo-tag',
    true
  );
  setupCustomDropdownInModal(
    'trigger-novo-funil',
    'options-novo-funil',
    'hidden-novo-funil'
  );
  // 1. Abrir Modal e Carregar Dados
  // `preenchido` ({ nome, telefone, botId }): vem do "Abrir conversa" do
  // cartão de contato — a janela já abre com o contato e o chip da conversa.
  async function abrirModalAddCliente(preenchido) {
    modalAddCliente.classList.remove('hidden');
    // Resets Visuais
    hiddenVendedor.value = '';
    const displayVendedor = document.querySelector(
      '#trigger-novo-vendedor span'
    );
    if (displayVendedor)
      displayVendedor.textContent = 'Selecione o Correspondente...';
    hiddenBot.value = '';
    const displayBot = document.querySelector('#trigger-novo-bot span');
    if (displayBot) displayBot.textContent = 'Selecione o Bot...';
    hiddenTag.value = '';
    const displayTag = document.querySelector('#trigger-novo-tag span');
    if (displayTag) {
      displayTag.className = 'tag-pill-sem-tag';
      displayTag.style.backgroundColor = '';
      displayTag.style.color = '';
      displayTag.style.border = '';
      displayTag.textContent = 'SEM TAG';
    }
    hiddenFunil.value = 'entrada';
    const displayFunil = document.querySelector('#trigger-novo-funil span');
    if (displayFunil) displayFunil.textContent = 'Caixa de Entrada (Padrão)';
    // --- POPULAR TAGS ---
    let tagsHtml = `
      <div class="custom-option" data-value="">
         <span class="tag-pill-sem-tag" style="font-size: 11px; padding: 4px 8px;">SEM TAG</span>
      </div>`;
    todasTagsSistema.forEach((tag) => {
      const corFundo = tag.color + '20';
      tagsHtml += `
        <div class="custom-option" data-value="${tag.id}">
          <span class="tag-pill-dropdown" style="background-color: ${escaparHtmlChat(corFundo)}; color: ${escaparHtmlChat(tag.color)}; font-size: 11px; padding: 4px 8px;">
            ${escaparHtmlChat(tag.name)}
          </span>
        </div>`;
    });
    optionsTag.innerHTML = tagsHtml;
    // --- POPULAR BOTS (Todos para todo mundo) ---
    const botsCarregados = carregarBotsCriacaoCliente();
    // --- POPULAR FUNIS E ETAPAS ---
    carregarFunisCriacaoCliente();
    // --- CONTROLE ADMIN ---
    if (podeEscolherResponsavel) {
      boxVendedor.style.display = 'block';
      try {
        const res = await fetch('/api/team/vendedores', {
          headers: {
            Authorization: `Bearer ${localStorage.getItem('token')}`,
          },
        });
        const vendedores = await res.json();
        let vendHtml =
          '<div class="custom-option" data-value="">Livre (Fila Geral)</div>';
        vendedores.forEach((v) => {
          vendHtml += `<div class="custom-option" data-value="${v.id}">${v.name || v.username}</div>`;
        });
        optionsVendedor.innerHTML = vendHtml;
      } catch (e) {
        console.error('Erro vendedores', e);
      }
    } else {
      boxVendedor.style.display = 'none';
    }
    if (preenchido) {
      document.getElementById('novo-cliente-nome').value = preenchido.nome || '';
      document.getElementById('novo-cliente-telefone').value = preenchido.telefone || '';
      document.getElementById('novo-cliente-cpf').value = '';
      await botsCarregados;
      const opcaoBot = Array.from(optionsBot.querySelectorAll('.custom-option')).find(
        (o) => o.dataset.value === preenchido.botId
      );
      // Mesmo caminho do clique: preenche o campo escondido e o rótulo.
      if (opcaoBot) opcaoBot.click();
    }
  }
  if (btnAddCliente) {
    btnAddCliente.addEventListener('click', () => abrirModalAddCliente());
  }
  // Cartão de contato: "Abrir conversa". Já está na lista = abre direto;
  // senão a janela Adicionar Cliente decide (cria, ou abre o que já existe).
  window.abrirContatoCompartilhado = function (numero, nome) {
    const digitos = String(numero || '').replace(/\D/g, '');
    if (!digitos) return;
    const botAtual = chatSelecionado ? chatSelecionado.bot_id : null;
    const alvo = todosChats.find((c) => mesmoCelularContato(c.chat_id, digitos));
    if (alvo) {
      window.abrirConversaPorNumero(String(alvo.chat_id).replace(/\D/g, ''), botAtual);
      return;
    }
    abrirModalAddCliente({ nome, telefone: digitos, botId: botAtual });
  };
  if (btnFecharModalCliente) {
    btnFecharModalCliente.addEventListener('click', () =>
      modalAddCliente.classList.add('hidden')
    );
  }
  async function carregarBotsCriacaoCliente() {
    const displayBot = document.querySelector('#trigger-novo-bot span');
    try {
      if (displayBot) displayBot.textContent = 'Carregando bots...';
      optionsBot.innerHTML = '';
      const resBots = await fetch('/api/sessions/all', {
        headers: { Authorization: `Bearer ${localStorage.getItem('token')}` },
      });
      let bots = await resBots.json();
      bots = bots.filter((b) => b.online === true && b.deleted === false);
      if (bots.length === 0) {
        if (displayBot) displayBot.textContent = 'Nenhum Bot disponível';
        optionsBot.innerHTML =
          '<div class="custom-option" style="color:#999;">Nenhum Bot online/ativo encontrado</div>';
        return;
      }
      if (displayBot) displayBot.textContent = 'Selecione o Bot...';
      let botsHtml = `
        <div style="padding: 8px; border-bottom: 1px solid var(--cor-borda); position: sticky; top: 0; background: var(--fundo-cartao); z-index: 10;">
          <input type="text" id="input-busca-bot-dropdown" placeholder="Pesquisar bot..." autocomplete="off" style="width: 100%; padding: 8px 10px; border: 1px solid var(--cor-borda); border-radius: 6px; font-size: 12px; outline: none; background: var(--fundo-input); color: var(--texto-principal); box-sizing: border-box;">
        </div>
      `;
      bots.forEach((b) => {
        let tagsHtml = '<span class="tag-pill-sem-tag">SEM TAG</span>';
        if (b.tags && b.tags.length > 0) {
          tagsHtml = b.tags
            .map((t) => {
              const corFundo = t.color + '20';
              return `<span class="tag-pill-dropdown" style="background-color: ${escaparHtmlChat(corFundo)}; color: ${escaparHtmlChat(t.color)};">${escaparHtmlChat(t.name)}</span>`;
            })
            .join('');
        }
        botsHtml += `
          <div class="custom-option" data-value="${b.id}">
            <div class="custom-option-grid">
               <i class="bx bxl-whatsapp" style="color: #25D366; font-size: 16px;"></i>
               <div class="tags-wrapper-dropdown">${tagsHtml}</div>
               <span class="nome-bot-dropdown">${escaparHtmlChat(b.name)}</span>
            </div>
          </div>`;
      });
      optionsBot.innerHTML = botsHtml;
      const inputBuscaBot = document.getElementById('input-busca-bot-dropdown');
      if (inputBuscaBot) {
        inputBuscaBot.addEventListener('click', (e) => e.stopPropagation());
        inputBuscaBot.addEventListener('input', (e) => {
          const termo = e.target.value.toLowerCase();
          const opcoes = optionsBot.querySelectorAll('.custom-option');
          opcoes.forEach((opt) => {
            const textoBot = opt.textContent.toLowerCase();
            if (textoBot.includes(termo)) {
              opt.style.display = '';
            } else {
              opt.style.display = 'none';
            }
          });
        });
      }
    } catch (e) {
      if (displayBot) displayBot.textContent = 'Erro ao carregar';
    }
  }
  // --- NOVA FUNÇÃO: CARREGA A ESTRUTURA DO KANBAN COM SANFONA ---
  async function carregarFunisCriacaoCliente() {
    optionsFunil.innerHTML =
      '<div style="padding:10px;text-align:center;">Carregando...</div>';
    const estrutura = await carregarEstruturaKanban();
    let html = `
      <div class="custom-option" data-value="entrada" style="border-bottom: 1px solid var(--cor-borda); padding: 12px;">
         <span style="font-weight: 600; font-size: 13px;">Fila Geral (Caixa de Entrada)</span>
      </div>`;
    if (estrutura && estrutura.canais) {
      estrutura.canais.forEach((canal) => {
        const colunas = estrutura.colunas.filter(
          (c) => c.canal_id === canal.id
        );
        if (colunas.length > 0) {
          // Cria o Cabeçalho da Sanfona
          html += `
            <div class="grupo-funil-modal">
                <div class="titulo-funil-modal" onclick="toggleSanfonaFunilModal(this, event)">
                    <span>${escaparHtmlChat(canal.nome)}</span>
                    <i class='bx bx-chevron-down chevron-canal'></i>
                </div>
                <div class="etapas-container-modal hidden">
          `;
          // Cria as Opções de Etapa dentro da Sanfona
          colunas.forEach((col) => {
            html += `
                  <div class="custom-option etapa-option" data-value="${col.id}">
                      <div style="display: flex; align-items: center; gap: 8px;">
                          <span style="width: 8px; height: 8px; border-radius: 50%; background-color: ${escaparHtmlChat(col.cor)};"></span>
                          <span>${escaparHtmlChat(col.nome)}</span>
                      </div>
                  </div>`;
          });
          html += `
                </div>
            </div>`;
        }
      });
    }
    optionsFunil.innerHTML = html;
  }
  // --- FUNÇÃO PARA ABRIR/FECHAR A SANFONA ---
  window.toggleSanfonaFunilModal = function (elemento, evento) {
    evento.stopPropagation(); // Impede que clicar no título feche o dropdown inteiro
    const container = document.getElementById('options-novo-funil');
    // Fecha todos os outros funis que estiverem abertos
    container.querySelectorAll('.etapas-container-modal').forEach((el) => {
      if (el !== elemento.nextElementSibling) el.classList.add('hidden');
    });
    container.querySelectorAll('.chevron-canal').forEach((el) => {
      if (el !== elemento.querySelector('.chevron-canal'))
        el.classList.remove('girar');
    });
    // Abre ou fecha o funil que foi clicado
    const etapas = elemento.nextElementSibling;
    const icon = elemento.querySelector('.chevron-canal');
    etapas.classList.toggle('hidden');
    if (icon) icon.classList.toggle('girar');
  };
  // 4. Salvar Cliente (Agora enxuto e move de funil em tempo real)
  if (btnSalvarNovoCliente) {
    btnSalvarNovoCliente.addEventListener('click', async () => {
      const telefone = document
        .getElementById('novo-cliente-telefone')
        .value.trim();
      const botId = hiddenBot.value;
      const tagId = hiddenTag.value || null;
      const funilEtapaId = hiddenFunil.value || 'entrada';
      if (!telefone)
        return mostrarAlerta('O Telefone é obrigatório!', 'warning');
      if (!botId)
        return mostrarAlerta(
          'Você precisa selecionar um Bot/Canal!',
          'warning'
        );
      const iconOriginal = btnSalvarNovoCliente.innerHTML;
      btnSalvarNovoCliente.innerHTML =
        '<i class="fas fa-spinner fa-spin"></i> Criando...';
      btnSalvarNovoCliente.disabled = true;
      // Payload Enxuto: Apenas Nome e CPF no crmData
      const payload = {
        botId: botId,
        telefone: telefone,
        tagId: tagId,
        crmData: {
          nome: document.getElementById('novo-cliente-nome').value.trim(),
          cpf: window.limparEPreencherCPF(
            document.getElementById('novo-cliente-cpf').value
          ),
        },
      };
      try {
        const res = await fetch('/api/chat/add-new-client', {
          method: 'POST',
          headers: {
            'Content-Type': 'application/json',
            Authorization: `Bearer ${localStorage.getItem('token')}`,
          },
          body: JSON.stringify(payload),
        });
        const data = await res.json();
        // Mesma janela do "Chamar no WhatsApp": o servidor conferiu pelo chip
        // escolhido e não criou nada.
        if (data.codigo === 'sem_whatsapp') {
          Swal.fire({
            title: 'Cliente não possui WhatsApp',
            text: 'Não existe conta vinculada no whatsapp para esse número',
            icon: 'warning',
            confirmButtonText: 'Entendi',
            confirmButtonColor: 'var(--azul-principal)',
          });
          return;
        }
        if (data.codigo === 'de_outra_pessoa') {
          Swal.fire({
            title: 'Cliente de outra atendente',
            text: data.message,
            icon: 'info',
            confirmButtonText: 'Entendi',
            confirmButtonColor: 'var(--azul-principal)',
          });
          return;
        }
        if (data.success) {
          // 👇 A MÁGICA: Se escolheu uma etapa que não seja a Fila de Entrada, move ele agora!
          if (funilEtapaId !== 'entrada') {
            try {
              await fetch('/api/chat/mover-funil', {
                method: 'POST',
                headers: {
                  'Content-Type': 'application/json',
                  Authorization: `Bearer ${localStorage.getItem('token')}`,
                },
                body: JSON.stringify({
                  numero_wpp: data.chat.chat_id,
                  nome_cliente: data.chat.crm_nome || data.chat.name,
                  coluna_id: funilEtapaId,
                }),
              });
            } catch (e) {
              console.error(
                'Cliente criado, mas falhou ao mover pro Kanban',
                e
              );
            }
          }
          mostrarAlerta(
            !data.ja_existia
              ? 'Cliente criado com sucesso!'
              : data.conversa_nova
                ? 'Esse cliente já estava no sistema: abrimos a conversa por este número.'
                : 'Esse cliente já estava no sistema e já tinha conversa por este número.',
            'success'
          );
          modalAddCliente.classList.add('hidden');
          document
            .querySelectorAll('#modal-add-cliente input[type="text"]')
            .forEach((inp) => (inp.value = ''));
          // Se for ADMIN e tiver selecionado um Vendedor, a gente roda o transferir logo após criar!
          if (podeEscolherResponsavel && hiddenVendedor.value) {
            await fetch('/api/chat/transfer', {
              method: 'POST',
              headers: {
                'Content-Type': 'application/json',
                Authorization: `Bearer ${localStorage.getItem('token')}`,
              },
              body: JSON.stringify({
                chatId: data.chat.chat_id,
                botId: data.chat.bot_id,
                responsavelId: hiddenVendedor.value,
              }),
            });
          }
          // O servidor devolve o número como o cliente JÁ está gravado (com
          // ou sem o nono dígito), então o card que já está na lista é
          // achado aqui e ganha a aba nova — em vez de nascer um card gêmeo.
          const numeroChat = data.chat.chat_id.replace(/\D/g, '');
          const indexExistente = todosChats.findIndex(
            (c) => (c.chat_id || '').replace(/\D/g, '') === numeroChat
          );
          let chatParaAbrir = null;
          if (indexExistente !== -1) {
            const chatAntigo = todosChats[indexExistente];
            chatAntigo.name = data.chat.name;
            chatAntigo.crm_nome = data.chat.crm_nome;
            const canalJaExiste = chatAntigo.canais.find(
              (c) => c.bot_id === botId
            );
            if (
              !canalJaExiste &&
              data.chat.canais &&
              data.chat.canais.length > 0
            ) {
              chatAntigo.canais.push(data.chat.canais[0]);
            }
            todosChats.splice(indexExistente, 1);
            todosChats.unshift(chatAntigo);
            chatParaAbrir = chatAntigo;
            renderizarLista(todosChats);
          } else if (data.ja_existia) {
            // Cliente antigo fora da página carregada: o card inventado aqui
            // teria só a aba nova (sem as outras conversas dele). A lista
            // vem do servidor, completa.
            await carregarChats(1, true);
            chatParaAbrir = todosChats.find(
              (c) => (c.chat_id || '').replace(/\D/g, '') === numeroChat
            );
          } else {
            todosChats.unshift(data.chat);
            chatParaAbrir = data.chat;
            renderizarLista(todosChats);
          }
          // Abre JÁ no chip escolhido (sem isso abria na primeira aba do
          // cliente, que pode ser a do RCS).
          if (chatParaAbrir) abrirConversa(chatParaAbrir, botId);
        } else {
          mostrarAlerta('Erro: ' + data.message, 'error');
        }
      } catch (e) {
        mostrarAlerta('Erro de conexão.', 'error');
      } finally {
        btnSalvarNovoCliente.innerHTML = iconOriginal;
        btnSalvarNovoCliente.disabled = false;
      }
    });
  }
  const btnToggleFunil = document.getElementById('btn-toggle-funil');
  const dropdownFunil = document.getElementById('dropdown-funil-atendimento');
  if (btnToggleFunil) {
    btnToggleFunil.addEventListener('click', () => {
      const isAberto = btnToggleFunil.classList.contains('aberto');
      if (!isAberto) {
        // Se vai abrir agora, manda renderizar a lista de etapas
        renderizarDropdownFunil();
      }
      btnToggleFunil.classList.toggle('aberto');
      dropdownFunil.classList.toggle('hidden');
    });
  }
  // ============================================================
  // 🔄 TRANSFERÊNCIA DE ATENDIMENTO (ROTEAMENTO)
  // ============================================================
  const blocoTransferencia = document.getElementById('bloco-transferencia');
  const selectResponsavel = document.getElementById('select-responsavel-chat');
  const btnTransferir = document.getElementById('btn-transferir-chat');

  // Dropdown customizado do "Responsável" (mesmo estilo usado na tela de Fluxos):
  // o <select> nativo continua sendo a fonte da verdade (escondido no CSS),
  // o dropdown só espelha o valor dele.
  const triggerResponsavel = document.querySelector(
    '.select-custom-responsavel-trigger'
  );
  const opcoesResponsavel = document.getElementById('opcoes-responsavel-chat');
  const textoResponsavelAtual = document.getElementById(
    'texto-responsavel-atual'
  );

  function montarOpcoesCustomResponsavel() {
    if (!opcoesResponsavel || !selectResponsavel) return;
    opcoesResponsavel.innerHTML = '';
    Array.from(selectResponsavel.options).forEach((opt) => {
      const div = document.createElement('div');
      div.className = 'select-custom-responsavel-opcao';
      div.dataset.valor = opt.value;
      div.textContent = opt.textContent;
      div.addEventListener('click', () => {
        selectResponsavel.value = opt.value;
        sincronizarTriggerResponsavel();
        opcoesResponsavel.classList.add('hidden');
      });
      opcoesResponsavel.appendChild(div);
    });
    sincronizarTriggerResponsavel();
  }

  function sincronizarTriggerResponsavel() {
    if (!selectResponsavel || !textoResponsavelAtual) return;
    const selecionado =
      selectResponsavel.options[selectResponsavel.selectedIndex];
    textoResponsavelAtual.textContent = selecionado
      ? selecionado.textContent
      : 'Livre (Fila Geral)';

    if (opcoesResponsavel) {
      opcoesResponsavel
        .querySelectorAll('.select-custom-responsavel-opcao')
        .forEach((el) => {
          el.classList.toggle(
            'ativa',
            el.dataset.valor === (selectResponsavel.value || '')
          );
        });
    }
  }

  // A vendedora volta a transferir (15/10/2026): o servidor só deixa mexer
  // nos clientes da carteira dela; admin segue transferindo qualquer um.
  if (triggerResponsavel && opcoesResponsavel) {
    triggerResponsavel.addEventListener('click', (e) => {
      e.stopPropagation();
      opcoesResponsavel.classList.toggle('hidden');
    });
    document.addEventListener('click', () =>
      opcoesResponsavel.classList.add('hidden')
    );
  }

  // MÁGICA 1: Tira o bloqueio do Admin. Carrega pra todo mundo!
  if (blocoTransferencia && selectResponsavel && Acesso.pode('chat.transferir')) {
    blocoTransferencia.style.display = 'block';
    fetch('/api/team/vendedores', {
      headers: { Authorization: `Bearer ${localStorage.getItem('token')}` },
    })
      .then((res) => res.json())
      .then((vendedores) => {
        vendedores.forEach((v) => {
          const opt = document.createElement('option');
          opt.value = v.id;
          opt.textContent = v.name || v.username;
          selectResponsavel.appendChild(opt);
        });
        montarOpcoesCustomResponsavel();
      });
  }
  if (btnTransferir) {
    btnTransferir.addEventListener('click', async () => {
      if (!chatSelecionado) return;
      // 👇 A BLINDAGEM DA CORRIDA: Salva os dados ANTES do Socket agir!
      const chatAlvoId = chatSelecionado.chat_id;
      const botAlvoId = chatSelecionado.bot_id;
      const originalHTML = btnTransferir.innerHTML;
      btnTransferir.innerHTML = '<i class="fas fa-spinner fa-spin"></i>';
      btnTransferir.disabled = true;
      try {
        const res = await fetch('/api/chat/transfer', {
          method: 'POST',
          headers: {
            'Content-Type': 'application/json',
            Authorization: `Bearer ${localStorage.getItem('token')}`,
          },
          body: JSON.stringify({
            chatId: chatAlvoId, // Usa a variável blindada
            botId: botAlvoId, // Usa a variável blindada
            responsavelId: selectResponsavel.value || null,
          }),
        });
        const data = await res.json();
        if (data.success) {
          mostrarAlerta(
            selectResponsavel.value
              ? 'Atendimento transferido!'
              : 'Retornado para Fila Geral',
            'success'
          );
          if (!veEquipe) {
            todosChats = todosChats.filter(
              (c) => c.chat_id !== chatAlvoId // Usa a variável blindada
            );
            const itemHTML = document.querySelector(
              `.chat-item[data-chat-id="${chatAlvoId}"]` // Usa a variável blindada
            );
            if (itemHTML) itemHTML.remove();
            fecharConversaAtual();
          } else {
            // Admin continua vendo, atualiza se o chat ainda estiver selecionado
            if (chatSelecionado) {
              const canal = chatSelecionado.canais.find(
                (c) => c.bot_id === botAlvoId
              );
              if (canal) canal.responsavel_id = selectResponsavel.value || null;
            }
          }
        } else {
          // Se o backend bloqueou por segurança
          mostrarAlerta(data.message || 'Falha ao transferir', 'error');
        }
      } catch (e) {
        mostrarAlerta('Erro de rede na transferência', 'error');
      } finally {
        btnTransferir.innerHTML = originalHTML;
        btnTransferir.disabled = false;
      }
    });
  }
  // O filtro de vendedoras (só admin) guarda logins; o dono novo vem como id.
  // Sem filtro, ou sem conseguir traduzir algum login, deixa passar — na
  // dúvida é melhor o cliente ficar na lista do que sumir sem motivo.
  function chatPassaNoFiltroVendedores(responsavelId) {
    const logins = filtrosAtivos.vendedores || [];
    if (!logins.length) return true;
    const semDono = responsavelId === null || responsavelId === undefined || responsavelId === '';
    if (logins.includes('SEM_DONO') && semDono) return true;
    if (semDono) return false;
    const itens = document.querySelectorAll('.item-filtro-vendedor');
    for (const login of logins) {
      if (login === 'SEM_DONO') continue;
      const item = Array.from(itens).find((el) => el.dataset.username === login);
      if (!item || !item.dataset.userId) return true;
      if (String(item.dataset.userId) === String(responsavelId)) return true;
    }
    return false;
  }
  socket.on('chat_transferido', (data) => {
    if (!data || !data.chatId) return;
    const idLimpo = data.chatId.replace('@c.us', '');
    const infoUsuario = JSON.parse(localStorage.getItem('usuario') || '{}');
    const veEquipeLocal = Acesso.pode('clientes.ver_equipe');
    // 🛡️ MÁGICA 3: Se não sou admin, e o chat foi para outra pessoa (ou fila geral), arranca da minha tela!
    if (
      !veEquipeLocal &&
      String(data.responsavelId) !== String(infoUsuario.id)
    ) {
      // 1. Remove da memória
      todosChats = todosChats.filter((c) => !c.chat_id.includes(idLimpo));
      // 2. Remove da barra lateral
      const itemLista = document.querySelector(
        `.chat-item[data-chat-id*="${idLimpo}"]`
      );
      if (itemLista) itemLista.remove();
      // 3. Se estava aberto na minha cara agorinha, fecha o chat e o CRM!
      if (chatSelecionado && chatSelecionado.chat_id.includes(idLimpo)) {
        fecharConversaAtual();
      }
      // Não era meu: tirar da tela já basta, não tem o que buscar no servidor.
      return;
    } else if (!chatPassaNoFiltroVendedores(data.responsavelId)) {
      // Admin filtrando por vendedora e o cliente foi pra alguém fora do
      // filtro: sai da lista (a conversa aberta, se for ela, fica aberta).
      todosChats = todosChats.filter((c) => !c.chat_id.includes(idLimpo));
      const itemLista = document.querySelector(
        `.chat-item[data-chat-id*="${idLimpo}"]`
      );
      if (itemLista) itemLista.remove();
      return;
    } else {
      // Se veio para mim (ou se sou Admin), atualizo os dados internos de boa
      const chatNaMemoria = todosChats.find((c) => c.chat_id.includes(idLimpo));
      if (chatNaMemoria && chatNaMemoria.canais) {
        chatNaMemoria.canais.forEach((canal) => {
          canal.responsavel_id = data.responsavelId;
        });
      }
      if (chatSelecionado && chatSelecionado.chat_id.includes(idLimpo)) {
        const canalAtual =
          chatSelecionado.canais.find(
            (c) => c.bot_id === chatSelecionado.bot_id
          ) || chatSelecionado.canais[0];
        if (canalAtual) canalAtual.responsavel_id = data.responsavelId;
        const selResp = document.getElementById('select-responsavel-chat');
        if (selResp) selResp.value = data.responsavelId || '';
        if (typeof sincronizarTriggerResponsavel === 'function')
          sincronizarTriggerResponsavel();
      }
    }
    // Atualiza SEM zerar a lista. Esse evento vai pra tela de todo mundo a
    // cada cliente que a roleta distribui (ou seja, logo depois de um cliente
    // mandar mensagem), e o reset jogava a vendedora de volta pro topo e
    // descartava as páginas que ela já tinha rolado.
    if (!carregandoChats) {
      carregarChats(1, false);
    }
  });
  // ============================================================
  // ⚙️ MÓDULO: CAMPOS DINÂMICOS DO CRM
  // ============================================================
  let camposDinamicosCRM = [];
  const btnConfigCrm = document.getElementById('btn-configurar-crm');
  const modalConfigCrm = document.getElementById('modal-config-crm');
  const btnFecharConfigCrm = document.getElementById('btn-fechar-config-crm');
  const btnAddNovoCampo = document.getElementById('btn-add-novo-campo');
  const inputNovoNomeCampo = document.getElementById('input-novo-nome-campo');
  const listaCamposConfigurados = document.getElementById(
    'lista-campos-configurados'
  );
  const containerCamposDinamicos = document.getElementById(
    'container-campos-dinamicos-crm'
  );
  // Mostrar botão de config apenas se for Admin
  if (Acesso.pode('crm.configurar_campos') && btnConfigCrm) {
    btnConfigCrm.style.display = 'flex';
  }
  // 1. Carregar estrutura dos campos do Banco de Dados
  async function carregarEstruturaCamposDinamicos() {
    try {
      const res = await fetch('/api/crm-campos', {
        headers: { Authorization: `Bearer ${localStorage.getItem('token')}` },
      });
      const data = await res.json();
      if (data.success) {
        camposDinamicosCRM = data.campos;
        renderizarListaConfigCampos();
      }
    } catch (e) {
      console.error('Erro ao carregar campos dinâmicos:', e);
    }
  }
  // Lógica para mostrar/esconder o campo de opções caso seja uma Lista
  const inputTipoCampo = document.getElementById('input-novo-tipo-campo');
  const boxOpcoes = document.getElementById('box-opcoes-campo');
  if (inputTipoCampo) {
    inputTipoCampo.addEventListener('change', (e) => {
      if (e.target.value === 'select') {
        boxOpcoes.style.display = 'flex';
      } else {
        boxOpcoes.style.display = 'none';
      }
    });
  }
  // 2. Renderizar a lista dentro do Modal de Configuração (Para o Admin)
  function renderizarListaConfigCampos() {
    if (!listaCamposConfigurados) return;
    listaCamposConfigurados.innerHTML = '';
    if (camposDinamicosCRM.length === 0) {
      listaCamposConfigurados.innerHTML =
        '<span style="color:#999; font-size:12px;">Nenhum campo customizado criado ainda.</span>';
      return;
    }
    camposDinamicosCRM.forEach((campo) => {
      const div = document.createElement('div');
      div.className = 'item-campo-config';
      div.draggable = true;
      div.dataset.id = campo.id;
      // Injeta também a badge dizendo que tipo de campo ele é
      div.innerHTML = `
        <div style="display:flex; align-items:center; gap:10px;">
            <i class="bx bx-menu drag-handle-campo" title="Arraste para reordenar"></i>
            <span>${escaparHtmlChat(campo.nome)}</span>
            <span class="badge-tipo-campo">${campo.tipo || 'text'}</span>
        </div>
        <button class="btn-excluir-campo" draggable="false" onclick="excluirCampoDinamico(${campo.id}, '${textoParaOnclick(campo.nome)}')">
          <i class="bx bx-trash"></i>
        </button>
      `;

      div.addEventListener('dragstart', () => {
        itemCampoArrastando = div;
        div.classList.add('arrastando-campo');
      });
      div.addEventListener('dragend', () => {
        div.classList.remove('arrastando-campo');
        itemCampoArrastando = null;
        salvarNovaOrdemCampos();
      });
      div.addEventListener('dragover', (e) => {
        e.preventDefault();
        if (!itemCampoArrastando || itemCampoArrastando === div) return;
        const rect = div.getBoundingClientRect();
        const depoisDoMeio = e.clientY > rect.top + rect.height / 2;
        listaCamposConfigurados.insertBefore(
          itemCampoArrastando,
          depoisDoMeio ? div.nextSibling : div
        );
      });

      listaCamposConfigurados.appendChild(div);
    });
  }

  // Arrastar-e-soltar pra reordenar os campos dinâmicos (o ID que fica sendo
  // arrastado no momento, compartilhado entre os handlers de cada linha)
  let itemCampoArrastando = null;

  async function salvarNovaOrdemCampos() {
    const ids = Array.from(
      listaCamposConfigurados.querySelectorAll('.item-campo-config')
    ).map((el) => Number(el.dataset.id));

    // Atualiza a ordem em memória na hora, sem esperar recarregar do servidor
    camposDinamicosCRM.sort((a, b) => ids.indexOf(a.id) - ids.indexOf(b.id));

    try {
      await fetch('/api/crm-campos/reordenar', {
        method: 'PUT',
        headers: {
          'Content-Type': 'application/json',
          Authorization: `Bearer ${localStorage.getItem('token')}`,
        },
        body: JSON.stringify({ ids }),
      });
    } catch (e) {
      console.error('Erro ao salvar nova ordem dos campos:', e);
    }
  }
  // 3. Abrir e Fechar Modal
  if (btnConfigCrm) {
    btnConfigCrm.addEventListener('click', () => {
      modalConfigCrm.classList.remove('hidden');
    });
  }
  if (btnFecharConfigCrm) {
    btnFecharConfigCrm.addEventListener('click', () => {
      modalConfigCrm.classList.add('hidden');
      if (chatSelecionado) {
        carregarDadosDoCliente(chatSelecionado.chat_id);
      }
    });
  }
  // 4. Criar Novo Campo
  if (btnAddNovoCampo) {
    btnAddNovoCampo.addEventListener('click', async () => {
      const nome = inputNovoNomeCampo.value.trim();
      const tipo = inputTipoCampo ? inputTipoCampo.value : 'text';
      const opcoesRaw = document.getElementById('input-opcoes-campo').value;
      if (!nome)
        return mostrarAlerta('Digite um nome para o campo.', 'warning');
      let opcoesArr = [];
      if (tipo === 'select') {
        opcoesArr = opcoesRaw
          .split(',')
          .map((o) => o.trim())
          .filter((o) => o);
        if (opcoesArr.length === 0)
          return mostrarAlerta(
            'Digite as opções da lista separadas por vírgula.',
            'warning'
          );
      }
      const iconOriginal = btnAddNovoCampo.innerHTML;
      btnAddNovoCampo.innerHTML = '<i class="bx bx-loader-alt bx-spin"></i>';
      btnAddNovoCampo.disabled = true;
      try {
        const res = await fetch('/api/crm-campos', {
          method: 'POST',
          headers: {
            'Content-Type': 'application/json',
            Authorization: `Bearer ${localStorage.getItem('token')}`,
          },
          body: JSON.stringify({ nome, tipo, opcoes: opcoesArr }),
        });
        const data = await res.json();
        if (data.success) {
          inputNovoNomeCampo.value = '';
          document.getElementById('input-opcoes-campo').value = '';
          inputTipoCampo.value = 'text';
          boxOpcoes.style.display = 'none';
          await carregarEstruturaCamposDinamicos();
          mostrarAlerta('Campo criado!', 'success');
        } else {
          mostrarAlerta(data.message, 'error');
        }
      } catch (e) {
        mostrarAlerta('Erro ao criar campo.', 'error');
      } finally {
        btnAddNovoCampo.innerHTML = iconOriginal;
        btnAddNovoCampo.disabled = false;
      }
    });
  }
  // 5. Excluir Campo (Função Global)
  window.excluirCampoDinamico = async function (id, nome) {
    const certeza = await mostrarConfirmacao(
      `Tem certeza que deseja excluir o campo "${nome}"? Os dados não serão apagados do banco, mas sumirão da tela.`
    );
    if (!certeza) return;
    try {
      const res = await fetch(`/api/crm-campos/${id}`, {
        method: 'DELETE',
        headers: { Authorization: `Bearer ${localStorage.getItem('token')}` },
      });
      const data = await res.json();
      if (data.success) {
        await carregarEstruturaCamposDinamicos();
      } else {
        mostrarAlerta(data.message, 'error');
      }
    } catch (e) {
      mostrarAlerta('Erro ao excluir.', 'error');
    }
  };
  window.limparEPreencherCPF = function (valor) {
    if (!valor) return '';
    let limpo = valor.replace(/\D/g, '');
    if (limpo.length > 0 && limpo.length < 11) {
      limpo = limpo.padStart(11, '0'); // Preenche zeros à esquerda do Excel
    }
    return limpo.slice(0, 11);
  };
  window.aplicarMascaraCPF = function (valor) {
    let limpo = valor.replace(/\D/g, '').slice(0, 11);
    if (limpo.length > 9)
      return limpo.replace(/(\d{3})(\d{3})(\d{3})(\d{1,2})/, '$1.$2.$3-$4');
    if (limpo.length > 6)
      return limpo.replace(/(\d{3})(\d{3})(\d{1,3})/, '$1.$2.$3');
    if (limpo.length > 3) return limpo.replace(/(\d{3})(\d{1,3})/, '$1.$2');
    return limpo;
  };
  // Função utilitária para formatar moeda em tempo real enquanto digita
  window.mascaraMoedaCrm = function (campo) {
    let valor = campo.value.replace(/\D/g, '');
    if (valor === '') {
      campo.value = '';
      return;
    }
    valor = (parseInt(valor) / 100).toFixed(2) + '';
    valor = valor.replace('.', ',');
    valor = valor.replace(/(\d)(?=(\d{3})+(?!\d))/g, '$1.');
    campo.value = 'R$ ' + valor;
  };
  // 6. A FÁBRICA DE RENDERIZAÇÃO: Mapeia o tipo e desenha o elemento HTML correto
  window.desenharInputsDinamicosCrm = function (dadosSalvos = {}) {
    if (!containerCamposDinamicos) return;
    containerCamposDinamicos.innerHTML = '';
    const dadosSalvosLower = {};
    for (let key in dadosSalvos) {
      dadosSalvosLower[key.toLowerCase().trim()] = dadosSalvos[key];
    }
    camposDinamicosCRM.forEach((campo) => {
      const nomeLimpo = campo.nome.toLowerCase().trim();
      if (
        nomeLimpo === 'cpf' ||
        nomeLimpo === 'nome' ||
        nomeLimpo === 'telefone'
      ) {
        return;
      }
      const valor =
        dadosSalvos[campo.nome] || dadosSalvosLower[nomeLimpo] || '';
      const tipo = campo.tipo || 'text';
      const div = document.createElement('div');
      div.className = 'grupo-crm-v2 crm-item-lista'; // <-- CLASSE NOVA PARA LISTA
      // Avaliador de Tipos
      if (tipo === 'textarea') {
        div.innerHTML = `
            <label>${escaparHtmlChat(campo.nome)}</label>
            <textarea id="crm-dinamico-${campo.id}" rows="1" placeholder="..."> ${escaparHtmlChat(valor)}</textarea>
          `;
      } else if (tipo === 'boolean') {
        const isChecked = valor === 'true' ? 'checked' : '';
        div.innerHTML = `
            <div style="display: flex; flex-direction: column; gap: 2px;">
                <label style="margin: 0;">${escaparHtmlChat(campo.nome)}</label>
                <div style="padding-top: 4px;">
                    <label class="switch-crm" style="transform: scale(0.8); transform-origin: left center; margin: 0;">
                      <input type="checkbox" id="crm-dinamico-${campo.id}" ${isChecked}>
                      <span class="slider-crm"></span>
                    </label>
                </div>
            </div>
          `;
      } else if (tipo === 'select') {
        let opcoesHtml = '<option value="">...</option>';
        if (Array.isArray(campo.opcoes)) {
          campo.opcoes.forEach((opt) => {
            const selected = valor === opt ? 'selected' : '';
            opcoesHtml += `<option value="${escaparHtmlChat(opt)}" ${selected}>${escaparHtmlChat(opt)}</option>`;
          });
        }
        div.innerHTML = `
            <label>${escaparHtmlChat(campo.nome)}</label>
            <select id="crm-dinamico-${campo.id}">
                ${opcoesHtml}
            </select>
          `;
      } else {
        // Trata os tipos baseados em input padrão
        let tipoInput = 'text';
        let placeholder = '...';
        let mascaraExtra = '';
        if (tipo === 'number') {
          tipoInput = 'number';
          placeholder = '0';
        }
        if (tipo === 'date') tipoInput = 'date';
        if (tipo === 'datetime') tipoInput = 'datetime-local';
        if (tipo === 'currency') {
          tipoInput = 'text';
          placeholder = 'R$ 0,00';
          mascaraExtra = 'oninput="mascaraMoedaCrm(this)"';
        }
        div.innerHTML = `
            <label>${escaparHtmlChat(campo.nome)}</label>
            <input type="${tipoInput}" id="crm-dinamico-${campo.id}" value="${escaparHtmlChat(valor)}" placeholder="${placeholder}" ${mascaraExtra} />
          `;
      }
      containerCamposDinamicos.appendChild(div);
    });
  };
  carregarEstruturaCamposDinamicos();
  // ============================================================
  // ✏️ LÓGICA DE EDIÇÃO DE MENSAGENS
  // ============================================================
  let mensagemEmEdicao = null;
  window.prepararEdicao = function (wppId, textoEncoded) {
    let textoRaw = decodeURIComponent(textoEncoded);
    // Limpa a frase "Editada por: Nome" do final para voltar só o texto original pra caixa
    textoRaw = textoRaw.split('<br><span style="font-size: 10.5px;')[0];
    // Tira os <br> e volta pra Enter normal
    textoRaw = textoRaw.replace(/<br>/g, '\n').replace(/<[^>]*>?/gm, '');
    mensagemEmEdicao = wppId;
    const input = document.getElementById('input-mensagem-texto');
    input.value = textoRaw;
    input.focus();
    // 👇 A MÁGICA DO TAMANHO DA CAIXA: Força o input a crescer baseado no texto injetado
    input.style.height = 'auto';
    input.style.height = input.scrollHeight + 'px';
    // Transforma a barra de Reply na barra de Edição (Laranja)
    const container = document.getElementById('reply-preview-container');
    const contentFlex = container.querySelector('.reply-preview-content');
    container.className = `reply-preview-bar reply-document`; // Fica laranjinha Premium
    contentFlex.innerHTML = `
      <div style="display: flex; align-items: center; width: 100%;">
          <div class="quoted-icon-box"><i class="bx bx-pencil"></i></div>
          <div style="display: flex; flex-direction: column; overflow: hidden; flex: 1; padding-left: 10px;">
              <span class="reply-preview-name" style="color: #be6703;">Editando Mensagem</span>
              <span class="reply-preview-text">Corrija o texto e aperte Enter para salvar</span>
          </div>
      </div>
      `;
    container.classList.remove('hidden');
  };
  // Garante que o "X" feche a edição também
  const oldFecharPreview = window.fecharPreviewResposta;
  window.fecharPreviewResposta = function () {
    mensagemEmEdicao = null;
    oldFecharPreview();
    // 👇 Limpa a caixa de texto e volta pro tamanho normal (1 linha) quando cancelar a edição
    const input = document.getElementById('input-mensagem-texto');
    if (input) {
      input.value = '';
      input.style.height = 'auto';
    }
  };
  async function enviarEdicao(novoTexto) {
    const wppIdEdit = mensagemEmEdicao;
    fecharPreviewResposta(); // Limpa a barra
    inputMsg.value = '';
    try {
      const res = await fetch('/api/chat/edit-message', {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          Authorization: `Bearer ${localStorage.getItem('token')}`,
        },
        body: JSON.stringify({
          botId: chatSelecionado.bot_id,
          wppId: wppIdEdit,
          newText: novoTexto,
        }),
      });
      const data = await res.json();
      if (!data.success) mostrarAlerta(data.message, 'error');
    } catch (e) {
      mostrarAlerta('Erro de conexão ao editar.', 'error');
    }
  }
  // ============================================================
  // ✏️ SOCKET: RECEBE A MENSAGEM EDITADA NA TELA E ATUALIZA BOTÕES
  // ============================================================
  socket.on('mensagem_editada', (data) => {
    if (!chatSelecionado) return;
    const limpar = (v) =>
      String(v || '')
        .replace('@c.us', '')
        .replace('@g.us', '')
        .trim();
    if (
      limpar(chatSelecionado.chat_id) === limpar(data.chat_id) &&
      limpar(chatSelecionado.bot_id) === limpar(data.bot_id)
    ) {
      const statusSpan = document.querySelector(
        `.msg-status[data-wpp-id="${data.wpp_id}"]`
      );
      if (statusSpan) {
        const wrapper = statusSpan.closest('.msg-wrapper-vertical');
        const bolha = wrapper.querySelector('.msg-bubble');
        const spanTexto = bolha.querySelector('.texto-formatado');
        if (spanTexto) {
          const [textoPuro, restoSufixo] = data.novo_texto.split('<br><span');
          // "Editada por" fora do texto, como no desenho do balão (lá está o
          // porquê): troca a marca antiga, se já tinha, pela nova.
          spanTexto.innerHTML = formatarTextoWhatsApp(textoPuro);
          bolha
            .querySelectorAll(':scope > .msg-editada-tag')
            .forEach((el) => el.remove());
          if (restoSufixo !== undefined)
            spanTexto.insertAdjacentHTML('afterend', '<span' + restoSufixo);
          const btnEdit = wrapper.querySelector('.btn-edit-hover');
          if (btnEdit) {
            const safeText = encodeURIComponent(textoPuro);
            btnEdit.setAttribute(
              'onclick',
              `prepararEdicao('${data.wpp_id}', '${safeText}')`
            );
          }
          const btnReply = wrapper.querySelector('.btn-reply-hover');
          if (btnReply) {
            const textoParaCitar = textoPuro
              .replace(/"/g, '&quot;')
              .replace(/'/g, "\\'");
            btnReply.setAttribute(
              'onclick',
              `prepararResposta('${data.wpp_id}', 'Você', '${textoParaCitar}', 'chat')`
            );
          }
        }
      }
    }
    if (typeof carregarChats === 'function') carregarChats(1, false);
  });
  // ============================================================
  // 🪄 LÓGICA DA VARINHA MÁGICA (IA ASSISTENTE)
  // ============================================================
  // 1. Abrir/Fechar Menu
  if (btnMagicWand) {
    btnMagicWand.addEventListener('click', (e) => {
      e.stopPropagation();
      // Fecha outros menus abertos por segurança
      if (emojiContainer) emojiContainer.classList.add('hidden');
      if (popupFiltros) popupFiltros.classList.add('hidden');
      const isHidden = magicWandMenu.classList.contains('hidden');
      if (isHidden) {
        carregarMagicPrompts(); // Busca do banco antes de mostrar
        magicWandMenu.classList.remove('hidden');
      } else {
        magicWandMenu.classList.add('hidden');
      }
    });
  }
  // 2. Fechar menu ao clicar fora
  document.addEventListener('click', (e) => {
    if (
      magicWandMenu &&
      !magicWandMenu.contains(e.target) &&
      !btnMagicWand.contains(e.target)
    ) {
      magicWandMenu.classList.add('hidden');
    }
  });
  // 3. Buscar Prompts do Servidor
  async function carregarMagicPrompts() {
    try {
      const res = await fetch('/api/ai-prompts', {
        headers: { Authorization: `Bearer ${localStorage.getItem('token')}` },
      });
      const data = await res.json();
      if (data.success) {
        renderizarMagicMenu(data.prompts);
      }
    } catch (e) {
      console.error('Erro ao carregar prompts IA:', e);
    }
  }
  // 4. Desenhar o Menu na Tela
  function renderizarMagicMenu(prompts) {
    listaMagicPrompts.innerHTML = '';
    if (prompts.length === 0) {
      listaMagicPrompts.innerHTML =
        '<div style="padding:15px; color:#999; font-size:12px; text-align:center;">Nenhum assistente criado.</div>';
    }
    prompts.forEach((p) => {
      const row = document.createElement('div');
      row.className = 'magic-item-row';
      let acoesHtml = '';
      if (podePrompts) {
        acoesHtml = `
          <div class="magic-item-acoes">
            <button class="magic-btn-acao edit" onclick="event.stopPropagation(); abrirEdicaoPrompt(${p.id}, '${textoParaOnclick(p.titulo)}', '${textoParaOnclick(p.prompt_interno)}')">
              <i class="bx bx-pencil"></i>
            </button>
            <button class="magic-btn-acao delete" onclick="event.stopPropagation(); excluirPrompt(${p.id})">
              <i class="bx bx-trash"></i>
            </button>
          </div>
        `;
      }
      row.innerHTML = `
        <span class="magic-item-texto">${escaparHtmlChat(p.titulo)}</span>
        ${acoesHtml}
      `;
      // Evento de Clique: Executar a IA!
      row.onclick = () => executarMágicaIA(p.id);
      listaMagicPrompts.appendChild(row);
    });
    // Trava de 6 itens para o botão de Novo
    const blocoAdd = document.getElementById('bloco-add-prompt');
    if (podePrompts && blocoAdd) {
      blocoAdd.style.display = 'block';
      btnNovoPrompt.disabled = prompts.length >= 6;
      btnNovoPrompt.style.opacity = prompts.length >= 6 ? '0.5' : '1';
      btnNovoPrompt.title =
        prompts.length >= 6 ? 'Limite de 6 assistentes atingido' : '';
    }
  }
  // 5. A GRANDE MÁGICA: Chamar a OpenAI
  async function executarMágicaIA(promptId) {
    const textoParaCorrigir = inputMsg.value.trim();
    if (!textoParaCorrigir)
      return mostrarAlerta(
        'Escreva algo primeiro para a IA ajudar!',
        'warning'
      );
    // Feedback Visual: Bloqueia a pílula e gira o ícone
    magicWandMenu.classList.add('hidden');
    const inputArea = document.querySelector('.input-linha-texto');
    const iconeVarinha = btnMagicWand.querySelector('i');
    inputArea.classList.add('ia-pensando');
    iconeVarinha.className = 'ph-bold ph-magic-wand icone-ia-girando';
    inputMsg.placeholder = 'IA pensando...';
    try {
      const res = await fetch('/api/chat/magic-wand', {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          Authorization: `Bearer ${localStorage.getItem('token')}`,
        },
        body: JSON.stringify({ textoOriginal: textoParaCorrigir, promptId }),
      });
      const data = await res.json();
      if (data.success) {
        inputMsg.value = data.textoMelhorado;
        // Ajusta a altura da caixa caso o texto cresça
        inputMsg.style.height = 'auto';
        inputMsg.style.height = inputMsg.scrollHeight + 'px';
      } else {
        mostrarAlerta(data.message, 'error');
      }
    } catch (e) {
      mostrarAlerta('Erro ao conectar com a IA.', 'error');
    } finally {
      inputArea.classList.remove('ia-pensando');
      iconeVarinha.className = 'ph-bold ph-magic-wand';
      inputMsg.placeholder = 'Digite sua mensagem...';
      inputMsg.focus();
    }
  }
  // --- FUNÇÕES DE GESTÃO (SÓ ADMIN) ---
  if (btnNovoPrompt) {
    btnNovoPrompt.onclick = () => {
      inputPromptId.value = '';
      inputPromptTitulo.value = '';
      inputPromptInterno.value = '';
      document.getElementById('titulo-modal-prompt').innerText =
        '✨ Novo Assistente IA';
      modalAiPrompt.classList.remove('hidden');
    };
  }
  window.abrirEdicaoPrompt = (id, titulo, prompt) => {
    inputPromptId.value = id;
    inputPromptTitulo.value = titulo;
    inputPromptInterno.value = prompt;
    document.getElementById('titulo-modal-prompt').innerText =
      '✏️ Editar Assistente';
    modalAiPrompt.classList.remove('hidden');
  };
  if (btnFecharModalPrompt) {
    btnFecharModalPrompt.onclick = () => modalAiPrompt.classList.add('hidden');
  }
  if (btnSalvarPrompt) {
    btnSalvarPrompt.onclick = async () => {
      const id = inputPromptId.value;
      const titulo = inputPromptTitulo.value.trim();
      const prompt_interno = inputPromptInterno.value.trim();
      if (!titulo || !prompt_interno)
        return mostrarAlerta('Preencha todos os campos.', 'warning');
      try {
        const metodo = id ? 'PUT' : 'POST';
        const url = id ? `/api/ai-prompts/${id}` : '/api/ai-prompts';
        const res = await fetch(url, {
          method: metodo,
          headers: {
            'Content-Type': 'application/json',
            Authorization: `Bearer ${localStorage.getItem('token')}`,
          },
          body: JSON.stringify({ titulo, prompt_interno }),
        });
        if (res.ok) {
          modalAiPrompt.classList.add('hidden');
          carregarMagicPrompts();
          mostrarAlerta('Assistente salvo!', 'success');
        }
      } catch (e) {
        mostrarAlerta('Erro ao salvar.', 'error');
      }
    };
  }
  window.excluirPrompt = async (id) => {
    const confirmou = await mostrarConfirmacao(
      'Deseja excluir este assistente?'
    );
    if (!confirmou) return;
    try {
      const res = await fetch(`/api/ai-prompts/${id}`, {
        method: 'DELETE',
        headers: { Authorization: `Bearer ${localStorage.getItem('token')}` },
      });
      if (res.ok) {
        carregarMagicPrompts();
        mostrarAlerta('Assistente removido.', 'success');
      }
    } catch (e) {
      mostrarAlerta('Erro ao excluir.', 'error');
    }
  };
  // ==========================================================================
  // 🌟 MÓDULO: ÁLBUM DE FIGURINHAS (STICKERS) E ABAS
  // ==========================================================================
  const tabBtnsPicker = document.querySelectorAll('.picker-tab-btn');
  const tabContentsPicker = document.querySelectorAll('.picker-tab-content');
  // 1. Lógica de trocar as Abas (Emojis <-> Figurinhas)
  if (tabBtnsPicker.length > 0) {
    tabBtnsPicker.forEach((btn) => {
      btn.addEventListener('click', (e) => {
        e.preventDefault();
        e.stopPropagation();
        // Remove a classe ativo de todo mundo
        tabBtnsPicker.forEach((b) => b.classList.remove('ativo'));
        tabContentsPicker.forEach((c) => c.classList.remove('ativo'));
        // Ativa a aba clicada
        btn.classList.add('ativo');
        const tabId = btn.getAttribute('data-tab');
        document.getElementById(`aba-picker-${tabId}`).classList.add('ativo');
        // Se clicou na aba de figurinhas, puxa do banco de dados!
        if (tabId === 'stickers') {
          carregarFigurinhasFavoritas();
        }
      });
    });
  }
  // 2. Carregar as figurinhas do banco de dados
  async function carregarFigurinhasFavoritas() {
    const loading = document.getElementById('loading-figurinhas');
    const vazio = document.getElementById('vazio-figurinhas');
    const lista = document.getElementById('lista-figurinhas-favoritas');
    loading.style.display = 'block';
    vazio.style.display = 'none';
    lista.innerHTML = '';
    try {
      const res = await fetch('/api/stickers', {
        headers: { Authorization: `Bearer ${localStorage.getItem('token')}` },
      });
      const data = await res.json();
      loading.style.display = 'none';
      if (data.success && data.stickers.length > 0) {
        data.stickers.forEach((url) => {
          const div = document.createElement('div');
          div.className = 'sticker-item-favorito';
          div.innerHTML = `
            <img src="${url}" loading="lazy">
            <div class="btn-remove-sticker" onclick="window.toggleFavoritoSticker('${url}', null, event)" title="Remover dos favoritos">
              <i class="bx bx-x"></i>
            </div>
          `;
          // Ao clicar na figurinha (fora do X vermelho), ENVIA pro cliente!
          div.onclick = (e) => {
            if (!e.target.closest('.btn-remove-sticker')) {
              window.enviarStickerSalvo(url);
            }
          };
          lista.appendChild(div);
        });
      } else {
        vazio.style.display = 'block';
      }
    } catch (e) {
      console.error(e);
      loading.style.display = 'none';
      vazio.style.display = 'block';
      vazio.innerHTML = 'Erro de conexão ao carregar figurinhas.';
    }
  }
  // 3. Favoritar / Desfavoritar
  window.toggleFavoritoSticker = async function (url, btnElement, event) {
    if (event) event.stopPropagation();
    try {
      const res = await fetch('/api/stickers/toggle', {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          Authorization: `Bearer ${localStorage.getItem('token')}`,
        },
        body: JSON.stringify({ caminho_arquivo: url }),
      });
      const data = await res.json();
      if (data.success) {
        mostrarAlerta(data.message, 'success');
        // MÁGICA VISUAL: Pinta a estrelinha na hora sem recarregar a tela!
        if (btnElement) {
          const icon = btnElement.querySelector('i');
          if (data.action === 'added') {
            btnElement.classList.add('favoritado');
            icon.classList.remove('bx-star'); // Tira a vazada
            icon.classList.add('bxs-star'); // Bota a preenchida
            if (window.figurinhasSalvas) window.figurinhasSalvas.push(url); // Salva na memória
          } else {
            btnElement.classList.remove('favoritado');
            icon.classList.remove('bxs-star');
            icon.classList.add('bx-star');
            if (window.figurinhasSalvas)
              window.figurinhasSalvas = window.figurinhasSalvas.filter(
                (u) => u !== url
              ); // Remove da memória
          }
        }
        // Se a gente estiver com a aba do álbum aberta, recarrega para sumir/aparecer na hora
        const tabStickers = document.getElementById('aba-picker-stickers');
        if (tabStickers && tabStickers.classList.contains('ativo')) {
          carregarFigurinhasFavoritas();
        }
      } else {
        mostrarAlerta(data.message, 'error');
      }
    } catch (e) {
      mostrarAlerta('Erro ao favoritar figurinha.', 'error');
    }
  };
  // 4. Enviar a figurinha favoritada pro cliente
  window.enviarStickerSalvo = async function (url) {
    if (!chatSelecionado)
      return mostrarAlerta('Selecione um chat primeiro!', 'warning');
    const btnEnviarPrincipal = document.getElementById('botao-enviar-msg');
    const oldIcon = btnEnviarPrincipal.innerHTML;
    btnEnviarPrincipal.innerHTML = '<i class="fas fa-spinner fa-spin"></i>';
    btnEnviarPrincipal.disabled = true;
    try {
      // 🪄 Mágica: Pega a imagem do HD e transforma num arquivo Blob pra mandar pro backend
      const response = await fetch(url);
      const blob = await response.blob();
      const file = new File([blob], 'sticker.webp', {
        type: blob.type || 'image/webp',
      });
      const formData = new FormData();
      formData.append('chatId', chatSelecionado.chat_id);
      formData.append('botId', chatSelecionado.bot_id);
      formData.append('file', file);
      formData.append('isSticker', 'true'); // 👈 Avisa o Backend que é figurinha!
      const res = await fetch('/api/chat/send', {
        method: 'POST',
        headers: { Authorization: `Bearer ${localStorage.getItem('token')}` },
        body: formData,
      });
      const data = await res.json();
      if (data.success) {
        // Fecha o menu de emojis/figurinhas com sucesso
        document
          .getElementById('emoji-picker-container')
          .classList.add('hidden');
      } else {
        mostrarAlerta(data.message, 'error');
      }
    } catch (e) {
      mostrarAlerta('Erro de rede ao enviar figurinha.', 'error');
    } finally {
      btnEnviarPrincipal.innerHTML = oldIcon;
      btnEnviarPrincipal.disabled = false;
    }
  };
  // ============================================================
  // 📁 CARREGAR GALERIA DE MÍDIAS LATERAL (CRM GLOBAL)
  // ============================================================
  window.scrollCarrossel = function (direcao) {
    const carrossel = document.getElementById('carrosselMidias');
    if (carrossel) {
      const quantidadePulo = 100 * direcao;
      carrossel.scrollBy({ left: quantidadePulo, behavior: 'smooth' });
    }
  };
  async function carregarGaleriaMidias(chatId) {
    const containerMidias = document.getElementById('carrosselMidias');
    if (!containerMidias) return; // Se a div não existir na tela, aborta silenciosamente
    // Feedback de carregamento
    containerMidias.innerHTML =
      '<span style="color:#999; font-size:12px; padding:10px;"><i class="fas fa-spinner fa-spin"></i> Carregando...</span>';
    try {
      const numeroPuro = chatId.replace(/\D/g, '');
      // O carrossel é resumo mesmo: 30 basta. Mas o contador tem que dizer o
      // tamanho do acervo, senão ele mente que só existem 30 arquivos.
      const res = await fetch(`/api/crm/${numeroPuro}/midias?limit=30`, {
        headers: { Authorization: `Bearer ${localStorage.getItem('token')}` },
      });
      const data = await res.json();
      const rotuloContagem = document.getElementById(
        'contagem-midias-lateral'
      );
      if (rotuloContagem) {
        const total = Number(data.total || 0);
        const mostradas = (data.midias || []).length;
        rotuloContagem.textContent =
          total > mostradas
            ? `(${mostradas} de ${total})`
            : total
              ? `(${total})`
              : '';
      }
      if (data.success && data.midias && data.midias.length > 0) {
        containerMidias.innerHTML = ''; // Limpa o carregando
        data.midias.forEach((midia) => {
          const ext = midia.caminho_midia.split('.').pop().toLowerCase();
          const div = document.createElement('div');
          div.className = 'midia-item';
          // Lógica de Renderização Visual (Imagem vs Documento)
          const isImagem = ['jpg', 'jpeg', 'png', 'webp', 'gif'].includes(ext);
          let thumbHtml = '';
          if (isImagem) {
            // Imagem preenche o fundo do quadradinho
            thumbHtml = `<div class="midia-thumb" style="background-image: url('${midia.caminho_midia}');" onclick="abrirVisualizador('${midia.caminho_midia}', 'imagem')"></div>`;
          } else {
            // Usa a sua função genial de icones
            const infoArquivo = getIconeArquivo(ext);
            let acaoClique = `window.open('${midia.caminho_midia}', '_blank')`; // Padrão: Baixa o arquivo
            if (['mp4', 'avi', 'mkv', 'mov'].includes(ext)) {
              acaoClique = `abrirVisualizador('${midia.caminho_midia}', 'video')`; // Se for vídeo, abre o seu player
            }
            thumbHtml = `
            <div class="midia-thumb" style="color: ${infoArquivo.cor}; font-size: 22px; background-color: #2A2D34;" onclick="${acaoClique}">
              <i class="${infoArquivo.icone}"></i>
            </div>`;
          }
          const tipoArquivo = ext.toUpperCase();
          // O nome do arquivo entra no title da miniatura: no carrossel só cabe
          // a extensão, e passar o mouse é o único jeito de saber qual é qual.
          const dica = midia.nome_arquivo
            ? `${midia.nome_arquivo}\nCanal: ${midia.bot_nome || 'Desconhecido'}`
            : `Canal: ${midia.bot_nome || 'Desconhecido'}`;
          div.innerHTML = `
          ${thumbHtml}
          <span class="midia-tipo" title="${escaparHtmlChat(dica)}">${tipoArquivo}</span>
        `;
          containerMidias.appendChild(div);
        });
      } else {
        containerMidias.innerHTML =
          '<span style="color:#8B8D94; font-size:11px; padding:10px;">Nenhuma mídia nesta conversa.</span>';
      }
    } catch (error) {
      console.error('Erro ao carregar mídias da lateral:', error);
      const rotuloErro = document.getElementById('contagem-midias-lateral');
      if (rotuloErro) rotuloErro.textContent = '';
      containerMidias.innerHTML =
        '<span style="color:#ef4444; font-size:11px; padding:10px;">Erro ao carregar.</span>';
    }
  }
  // ==========================================================================
  // 📁 GALERIA EXPANDIDA — paginada e filtrada NO SERVIDOR
  //
  // Até 23/09/2026 a rota devolvia as 30 mídias mais recentes do cliente
  // (somando todos os chips) e este modal filtrava/buscava por cima dessas 30
  // no navegador. Resultado: cliente com 124 mídias num chip e 29 mais novas
  // noutro mostrava ZERO ao filtrar pelo primeiro, mesmo com tudo visível no
  // chat. Agora chip, busca e página são parâmetros da rota, e a lista cresce
  // por acréscimo — nunca redesenhamos o que já está na tela, pra não jogar a
  // rolagem de volta pro topo a cada "Carregar mais".
  // ==========================================================================
  const GALERIA_PAGINA = 60;
  let midiasCacheGaleria = [];
  let filtroGaleriaAtual = 'todos';
  let galeriaOffset = 0;
  let galeriaTotal = 0;
  let galeriaNomeCliente = '';
  // Último grupo de dia desenhado: a página seguinte ("Carregar mais") pode
  // continuar o mesmo dia, e aí entra no grupo que já existe.
  let grupoGaleriaAtual = null;
  let galeriaCarregando = false;
  // Sequência de requisição: a busca dispara uma consulta por pausa de
  // digitação, e sem isso uma resposta lenta de "pd" podia chegar depois da de
  // "pdf" e repovoar a lista com o resultado errado.
  let galeriaReqSeq = 0;
  let galeriaBuscaTimer = null;

  function textoBuscaGaleria() {
    const campo = document.getElementById('input-busca-galeria');
    return campo ? campo.value.trim() : '';
  }

  function atualizarRodapeGaleria() {
    const rodape = document.getElementById('galeria-rodape');
    const rotulo = document.getElementById('galeria-contagem');
    const botao = document.getElementById('btn-galeria-carregar-mais');
    if (!rodape || !rotulo || !botao) return;

    const mostrados = midiasCacheGaleria.length;
    // Subtítulo: "Nome do cliente · 163 arquivos" (o total real do servidor,
    // não o que já foi carregado).
    const subtitulo = document.getElementById('galeria-subtitle-nome');
    if (subtitulo)
      subtitulo.textContent =
        galeriaNomeCliente +
        (galeriaTotal
          ? ` · ${galeriaTotal} arquivo${galeriaTotal === 1 ? '' : 's'}`
          : '');
    if (!mostrados) {
      rodape.style.display = 'none';
      return;
    }
    rodape.style.display = 'flex';
    rotulo.textContent =
      mostrados < galeriaTotal
        ? `Mostrando ${mostrados} de ${galeriaTotal} arquivos`
        : `${galeriaTotal} arquivo${galeriaTotal === 1 ? '' : 's'}`;
    botao.style.display = mostrados < galeriaTotal ? 'inline-flex' : 'none';
    botao.disabled = galeriaCarregando;
    botao.textContent = galeriaCarregando ? 'Carregando...' : 'Carregar mais';
  }

  async function buscarPaginaGaleria({ reiniciar }) {
    if (!chatSelecionado) return;
    if (galeriaCarregando) return;

    const container = document.getElementById('lista-galeria-expandida');
    if (!container) return;

    if (reiniciar) {
      galeriaOffset = 0;
      midiasCacheGaleria = [];
      galeriaTotal = 0;
      pararAudiosGaleria();
      container.innerHTML =
        '<div class="gal-aviso"><i class="fas fa-spinner fa-spin"></i> Carregando acervo...</div>';
    }

    galeriaCarregando = true;
    atualizarRodapeGaleria();
    const meuSeq = ++galeriaReqSeq;

    try {
      const numeroPuro = chatSelecionado.chat_id.replace(/\D/g, '');
      const params = new URLSearchParams({
        limit: String(GALERIA_PAGINA),
        offset: String(galeriaOffset),
      });
      if (filtroGaleriaAtual === 'atual' && chatSelecionado.bot_id)
        params.set('botId', chatSelecionado.bot_id);
      const busca = textoBuscaGaleria();
      if (busca) params.set('busca', busca);

      const res = await fetch(`/api/crm/${numeroPuro}/midias?${params}`, {
        headers: { Authorization: `Bearer ${localStorage.getItem('token')}` },
      });
      const data = await res.json();
      if (meuSeq !== galeriaReqSeq) return; // chegou atrasada, já tem outra valendo
      if (!data.success) throw new Error(data.message || 'Falha na galeria');

      const novas = data.midias || [];
      galeriaTotal = Number(data.total || 0);
      if (reiniciar) {
        container.innerHTML = '';
        grupoGaleriaAtual = null;
      }
      midiasCacheGaleria = midiasCacheGaleria.concat(novas);
      galeriaOffset = midiasCacheGaleria.length;

      if (!midiasCacheGaleria.length) {
        container.innerHTML = busca
          ? '<div class="gal-aviso">Nenhum arquivo encontrado para esta busca.</div>'
          : '<div class="gal-aviso">Nenhum arquivo nesta conversa.</div>';
      } else {
        // Buscando, tudo abre: um resultado dentro de aba fechada passaria
        // batido.
        novas.forEach((midia) =>
          colocarNaGaleria(container, midia, !!busca)
        );
      }
    } catch (error) {
      console.error('Erro ao carregar galeria:', error);
      if (meuSeq === galeriaReqSeq && reiniciar)
        container.innerHTML =
          '<div class="gal-aviso gal-aviso-erro">Erro ao carregar os arquivos.</div>';
    } finally {
      if (meuSeq === galeriaReqSeq) {
        galeriaCarregando = false;
        atualizarRodapeGaleria();
      }
    }
  }

  window.abrirGaleriaCompleta = async function () {
    if (!chatSelecionado) return;
    const modal = document.getElementById('modal-galeria-midias');
    const subtitulo = document.getElementById('galeria-subtitle-nome');
    galeriaNomeCliente = nomeOuTelefoneCliente(chatSelecionado);
    subtitulo.textContent = galeriaNomeCliente;
    modal.classList.remove('hidden');
    const campoBusca = document.getElementById('input-busca-galeria');
    if (campoBusca) campoBusca.value = '';
    await buscarPaginaGaleria({ reiniciar: true });
  };

  window.fecharGaleriaCompleta = function () {
    document.getElementById('modal-galeria-midias').classList.add('hidden');
    document.getElementById('input-busca-galeria').value = '';
    // Invalida o que estiver no ar: reabrir tem que começar limpo, senão uma
    // página em trânsito despeja itens do cliente anterior na lista nova.
    galeriaReqSeq++;
    galeriaCarregando = false;
    midiasCacheGaleria = [];
    // Fechar o modal não pode deixar áudio tocando sozinho no fundo.
    pararAudiosGaleria();
  };

  window.setFiltroGaleria = function (tipo) {
    if (tipo === filtroGaleriaAtual) return;
    filtroGaleriaAtual = tipo;
    document
      .getElementById('btn-filtro-galeria-atual')
      .classList.toggle('ativo', tipo === 'atual');
    document
      .getElementById('btn-filtro-galeria-todos')
      .classList.toggle('ativo', tipo === 'todos');
    buscarPaginaGaleria({ reiniciar: true });
  };

  window.carregarMaisGaleria = function () {
    buscarPaginaGaleria({ reiniciar: false });
  };

  window.filtrarGaleriaExpandida = function () {
    clearTimeout(galeriaBuscaTimer);
    galeriaBuscaTimer = setTimeout(
      () => buscarPaginaGaleria({ reiniciar: true }),
      300
    );
  };

  // --------------------------------------------------------------------------
  // GRUPOS POR DIA (abas que abrem e fecham). Os arquivos chegam do mais novo
  // pro mais antigo, então basta comparar com o último grupo desenhado: mesmo
  // dia entra nele, dia novo abre outro. O primeiro grupo da lista vem aberto
  // e os outros fechados; buscando, todos abrem.
  // --------------------------------------------------------------------------
  const FORMATO_DIA_GALERIA = new Intl.DateTimeFormat('pt-BR', {
    day: '2-digit',
    month: 'long',
    year: 'numeric',
  });
  const FORMATO_HORA_GALERIA = new Intl.DateTimeFormat('pt-BR', {
    hour: '2-digit',
    minute: '2-digit',
  });

  function nomeDiaGaleria(data) {
    const hoje = new Date();
    const ontem = new Date();
    ontem.setDate(hoje.getDate() - 1);
    if (data.toDateString() === hoje.toDateString()) return 'Hoje';
    if (data.toDateString() === ontem.toDateString()) return 'Ontem';
    return FORMATO_DIA_GALERIA.format(data);
  }

  function colocarNaGaleria(container, midia, abrirTudo) {
    const data = new Date(midia.data_hora);
    const chaveDia = data.toDateString();
    if (!grupoGaleriaAtual || grupoGaleriaAtual.chave !== chaveDia) {
      const aberto = abrirTudo || !container.querySelector('.gal-grupo');
      const grupo = document.createElement('div');
      grupo.className = 'gal-grupo' + (aberto ? ' aberto' : '');
      grupo.innerHTML = `
        <button type="button" class="gal-dia" onclick="this.parentElement.classList.toggle('aberto')">
          <i class="bx bx-chevron-right gal-seta"></i>
          <span class="gal-dia-nome">${nomeDiaGaleria(data)}</span>
          <span class="gal-dia-qtd"></span>
        </button>
        <div class="gal-gaveta"><div class="gal-gaveta-miolo"></div></div>`;
      container.appendChild(grupo);
      grupoGaleriaAtual = {
        chave: chaveDia,
        miolo: grupo.querySelector('.gal-gaveta-miolo'),
        contador: grupo.querySelector('.gal-dia-qtd'),
        qtd: 0,
      };
    }
    grupoGaleriaAtual.miolo.appendChild(criarLinhaGaleria(midia, data));
    grupoGaleriaAtual.qtd++;
    const q = grupoGaleriaAtual.qtd;
    grupoGaleriaAtual.contador.textContent = `${q} arquivo${q === 1 ? '' : 's'}`;
  }

  // --------------------------------------------------------------------------
  // PLAYER LEVE DA GALERIA. O do chat desenha a onda, mas pra isso baixa e
  // decodifica o áudio inteiro assim que aparece; numa galeria com vários
  // áudios isso baixaria tudo ao abrir o modal. Este só cria o <audio> no
  // primeiro play. Um toca por vez, e fechar o modal para tudo.
  // --------------------------------------------------------------------------
  const audiosGaleria = new Map();
  let seqPlayerGaleria = 0;

  function tempoGaleria(s) {
    if (!isFinite(s) || s < 0) return '0:00';
    return `${Math.floor(s / 60)}:${String(Math.floor(s % 60)).padStart(2, '0')}`;
  }

  function audioDaGaleria(id) {
    if (audiosGaleria.has(id)) return audiosGaleria.get(id);
    const el = document.getElementById(id);
    if (!el) return null;
    const au = new Audio(el.dataset.src);
    const progresso = el.querySelector('.gal-progresso');
    const tempo = el.querySelector('.gal-tempo');
    const icone = el.querySelector('.gal-play i');
    const marcar = (tocando) => {
      icone.className = tocando ? 'ph-fill ph-pause' : 'ph-fill ph-play';
      el.classList.toggle('tocando', tocando);
    };
    au.addEventListener('loadedmetadata', () => {
      tempo.textContent = `0:00 / ${tempoGaleria(au.duration)}`;
    });
    au.addEventListener('timeupdate', () => {
      progresso.style.width = `${(au.currentTime / au.duration) * 100 || 0}%`;
      tempo.textContent = `${tempoGaleria(au.currentTime)} / ${tempoGaleria(au.duration)}`;
    });
    au.addEventListener('play', () => marcar(true));
    au.addEventListener('pause', () => marcar(false));
    au.addEventListener('ended', () => {
      marcar(false);
      au.currentTime = 0;
    });
    au.addEventListener('error', () => {
      marcar(false);
      tempo.textContent = 'Indisponível';
    });
    audiosGaleria.set(id, au);
    return au;
  }

  function pararAudiosGaleria() {
    audiosGaleria.forEach((au) => {
      au.pause();
      au.removeAttribute('src');
      au.load();
    });
    audiosGaleria.clear();
    // O load() acima descarta o evento "pause" que ainda estava na fila, e o
    // botão ficaria com cara de tocando: volta o visual na mão.
    document.querySelectorAll('.gal-player.tocando').forEach((p) => {
      p.classList.remove('tocando');
      const icone = p.querySelector('.gal-play i');
      if (icone) icone.className = 'ph-fill ph-play';
    });
  }

  window.galeriaTocar = function (id) {
    const au = audioDaGaleria(id);
    if (!au) return;
    audiosGaleria.forEach((outro, k) => {
      if (k !== id) outro.pause();
    });
    if (au.paused) au.play().catch(() => {});
    else au.pause();
  };

  window.galeriaPular = function (ev, id) {
    const au = audioDaGaleria(id);
    if (!au) return;
    const r = ev.currentTarget.getBoundingClientRect();
    const fracao = Math.min(1, Math.max(0, (ev.clientX - r.left) / r.width));
    const ir = () => {
      au.currentTime = fracao * au.duration;
    };
    if (isFinite(au.duration)) ir();
    else au.addEventListener('loadedmetadata', ir, { once: true });
  };

  window.galeriaVelocidade = function (id) {
    const au = audioDaGaleria(id);
    if (!au) return;
    const seq = [1, 1.5, 2];
    au.playbackRate = seq[(seq.indexOf(au.playbackRate) + 1) % seq.length];
    const btn = document.querySelector(`#${id} .gal-vel`);
    if (btn) btn.textContent = `${au.playbackRate}x`;
  };

  function criarLinhaGaleria(midia, data) {
    const ext = midia.caminho_midia.split('.').pop().toLowerCase();
    const infoArquivo = getIconeArquivo(ext);
    const isImagem = ['jpg', 'jpeg', 'png', 'webp', 'gif'].includes(ext);
    // O caminho entra em dois contextos diferentes: como valor de atributo
    // (src/style) e como string dentro de um onclick. Um arquivo chamado
    // "Proposta O'Brien.pdf" quebrava o segundo.
    const caminhoAttr = escaparHtmlChat(midia.caminho_midia);
    const caminhoJs = textoParaOnclick(midia.caminho_midia);
    const isAudio =
      ['ogg', 'mp3', 'wav', 'aac', 'm4a'].includes(ext) ||
      midia.tipo === 'ptt' ||
      midia.tipo === 'audio';

    // ==========================================
    // 🧠 LÓGICA DE LIMPEZA DE NOME
    //
    // `nome_arquivo` é o nome como o cliente mandou, com acento, espaço e
    // parêntese — vem do payload cru da Evolution, não do caminho em disco
    // (ver backend/servicos/nome-arquivo-midia.js). Quando existe, ele ganha de tudo:
    // "contracheque_9_2026 (1).pdf" em vez de "contracheque_9_2026__1_.pdf".
    // Foto e áudio quase nunca têm, e aí segue a regra antiga.
    // ==========================================
    let nomeReal = midia.nome_arquivo || midia.legenda;
    let arquivoCru = midia.caminho_midia.split('/').pop();
    arquivoCru = arquivoCru
      .replace(/^env_\d+_/i, '')
      .replace(/^rcs_\d+_/i, '')
      .replace(/^camp_\d+_[a-z0-9]+_/i, '')
      .replace(/^rec_\d+_[a-z0-9]+_/i, '');
    if (
      !nomeReal ||
      nomeReal === '(Mídia)' ||
      nomeReal === '(midia)' ||
      nomeReal === '(Áudio)'
    ) {
      if (arquivoCru.length > 35) {
        nomeReal = `${isImagem ? 'Imagem' : 'Documento'}_${ext.toUpperCase()}`;
      } else {
        nomeReal = arquivoCru;
      }
    }
    if (isAudio) nomeReal = 'Mensagem de voz';
    const nomeDownloadForcado = nomeReal.endsWith(`.${ext}`)
      ? nomeReal
      : `${nomeReal}.${ext}`;

    let acaoVer = `window.open('${caminhoJs}', '_blank')`;
    if (isImagem) acaoVer = `abrirVisualizador('${caminhoJs}', 'imagem')`;
    if (['mp4', 'avi', 'mov'].includes(ext))
      acaoVer = `abrirVisualizador('${caminhoJs}', 'video')`;

    // Miniatura: foto de verdade, ou o ícone do tipo (a cor do ícone é o
    // único colorido da linha).
    const thumb = isImagem
      ? `<div class="gal-thumb" style="background-image: url('${caminhoAttr}')" onclick="${acaoVer}"></div>`
      : `<div class="gal-thumb" style="color: ${isAudio ? 'var(--texto-suave)' : infoArquivo.cor}"${isAudio ? '' : ` onclick="${acaoVer}"`}>
           <i class="${isAudio ? 'fas fa-microphone' : infoArquivo.icone}"></i>
         </div>`;

    // Hora | chip. O tipo não entra: já está no nome e no ícone.
    const meta = `
      <div class="gal-meta">
        <span class="gal-hora">${FORMATO_HORA_GALERIA.format(data)}</span>
        <span class="gal-sep"></span>
        <span class="gal-chip">${escaparHtmlChat(midia.bot_nome || 'Bot')}</span>
      </div>`;

    // Áudio: nome, barra do áudio, e hora | chip embaixo.
    let player = '';
    if (isAudio) {
      const idPlayer = `gal-aud-${++seqPlayerGaleria}`;
      player = `
        <div class="gal-player" id="${idPlayer}" data-src="${caminhoAttr}">
          <button type="button" class="gal-play" onclick="galeriaTocar('${idPlayer}')" title="Ouvir"><i class="ph-fill ph-play"></i></button>
          <div class="gal-trilho" onclick="galeriaPular(event, '${idPlayer}')"><div class="gal-progresso"></div></div>
          <span class="gal-tempo">0:00</span>
          <button type="button" class="gal-vel" onclick="galeriaVelocidade('${idPlayer}')" title="Velocidade">1x</button>
        </div>`;
    }

    const div = document.createElement('div');
    div.className = 'gal-linha' + (isAudio ? ' gal-linha-audio' : '');
    div.innerHTML = `
      ${thumb}
      <div class="gal-info"${isAudio ? '' : ` onclick="${acaoVer}"`}>
        <div class="gal-nome" title="${escaparHtmlChat(nomeReal)}">${escaparHtmlChat(nomeReal)}</div>
        ${player}
        ${meta}
      </div>
      <a href="#" class="gal-baixar" title="Baixar"
         onclick="window.forceDownload('${caminhoJs}', '${textoParaOnclick(nomeDownloadForcado)}'); event.preventDefault();">
        <i class="bx bx-download"></i>
      </a>`;
    return div;
  }
  // ==========================================================================
  // 🚀 BARRA DE STATUS PREMIUM (LÓGICA E SWEETALERT)
  // ==========================================================================
  document.querySelectorAll('.segmento-status').forEach((segmento) => {
    segmento.addEventListener('click', async () => {
      if (!chatSelecionado) return;
      const statusAlvo = segmento.dataset.status;
      const statusNome = segmento.getAttribute('title');
      // Clicar no status que já está aceso normalmente não faz nada — menos em
      // "Perdido", onde reclicar é como se corrige um motivo tabulado errado,
      // que é justamente o problema que essa tela veio resolver.
      if (segmento.classList.contains('ativo') && statusAlvo !== 'perdido')
        return;

      // "Perdido" troca a confirmação genérica pelo seletor de motivo: escolher
      // o motivo JÁ É a confirmação, então não faz sentido perguntar duas vezes.
      let motivoEscolhido = null;
      if (statusAlvo === 'perdido') {
        motivoEscolhido = await pedirMotivoPerdidoChat();
        if (!motivoEscolhido) return;
      } else {
        // SweetAlert Elegante
        const confirmacao = await Swal.fire({
          title: `Mover para ${statusNome}?`,
          text: 'O status do lead será atualizado no sistema.',
          icon: 'question',
          showCancelButton: true,
          confirmButtonColor: window.getComputedStyle(segmento).color,
          confirmButtonText: 'Sim, alterar',
          cancelButtonText: 'Cancelar',
        });
        if (!confirmacao.isConfirmed) return;
      }
      try {
        const res = await fetch('/api/chat/status-rapido', {
          method: 'POST',
          headers: {
            'Content-Type': 'application/json',
            Authorization: `Bearer ${localStorage.getItem('token')}`,
          },
          body: JSON.stringify({
            chatId: chatSelecionado.chat_id,
            acao: statusAlvo,
            motivo_perdido: motivoEscolhido,
          }),
        });
        const data = await res.json();
        if (data.success) {
          atualizarSeloMotivoPerdido(motivoEscolhido);
          chatSelecionado.motivo_perdido = motivoEscolhido;
          // 1. Muda visualmente na hora
          document
            .querySelectorAll('.segmento-status')
            .forEach((s) => s.classList.remove('ativo'));
          segmento.classList.add('ativo');
          // 2. Atualiza a memória
          chatSelecionado.status_lead = statusAlvo;
          mostrarAlerta(`Status alterado para ${statusNome}!`, 'success');
          // 3. O Fator Limpeza (Se for Vendedor e o lead Fechou/Ganhou, tira da tela)
          if (
            !veEquipe &&
            (statusAlvo === 'ganho' || statusAlvo === 'perdido')
          ) {
            const chatItem = document.querySelector(
              `.chat-item[data-chat-id="${chatSelecionado.chat_id}"]`
            );
            if (chatItem) chatItem.remove();
            todosChats = todosChats.filter(
              (c) => c.chat_id !== chatSelecionado.chat_id
            );
            if (areaVazia) areaVazia.classList.remove('escondido');
            if (areaAtiva) areaAtiva.classList.add('escondido');
            if (painelInfo) painelInfo.classList.add('fechado');
            chatSelecionado = null;
          }
        } else {
          mostrarAlerta(
            data.message || 'Não foi possível alterar o status.',
            'error'
          );
        }
      } catch (e) {
        console.error('Erro status rápido', e);
        mostrarAlerta('Erro de conexão ao servidor.', 'error');
      }
    });
  });
  /**
   * Abre a conversa de um número, venha de onde vier (sino, apito de lembrete,
   * link externo) — e SEM depender de o contato estar desenhado na lista.
   *
   * O jeito antigo procurava o contato no HTML da barra lateral por 10
   * segundos e desistia calado. Isso falha em três situações comuns e nenhuma
   * delas é rara: a lista está filtrada (busca, "não lidos", filtro de
   * vendedora), o cliente é antigo demais pra ter entrado na primeira página,
   * ou a conversa não subiu pro topo. O lembrete cai bem nesse último caso,
   * porque de propósito ele NÃO mexe na prévia da conversa.
   *
   * Aqui, se o cliente não está na lista carregada, ele é buscado no servidor
   * (/api/chat/single, que já valida permissão) e injetado no topo.
   *
   * @returns {Promise<boolean>} false = não achou ou sem acesso.
   */
  /**
   * `wppIdFoco` (opcional): abre a conversa parada NAQUELA mensagem em vez do
   * rodapé. É o que faz o "abrir conversa" do apito cair em cima do card do
   * lembrete, e não no fim da conversa.
   */
  window.abrirConversaPorNumero = async function (
    numeroBruto,
    botId,
    wppIdFoco
  ) {
    const numPuro = String(numeroBruto || '').replace(/\D/g, '');
    if (!numPuro) return false;

    let alvo = todosChats.find(
      (c) => String(c.chat_id || '').replace(/\D/g, '') === numPuro
    );

    if (!alvo) {
      try {
        const res = await fetch(`/api/chat/single/${numPuro}`, {
          headers: { Authorization: `Bearer ${localStorage.getItem('token')}` },
        });
        const json = await res.json();
        if (!json.success || !json.chat) {
          mostrarAlerta(
            json.message || 'Conversa não encontrada ou sem acesso.',
            'warning'
          );
          return false;
        }
        // O servidor devolve a forma do cadastro (com/sem o 9): se essa já
        // está na lista, usa a existente em vez de duplicar o cliente.
        const jaNaLista = todosChats.find((c) => c.chat_id === json.chat.chat_id);
        alvo = jaNaLista || json.chat;
        if (!jaNaLista) todosChats.unshift(alvo);
        renderizarLista(todosChats);
      } catch (e) {
        console.error('Erro ao buscar a conversa:', e);
        mostrarAlerta('Falha de conexão ao abrir a conversa.', 'error');
        return false;
      }
    }

    // Abre já na aba do número por onde o lembrete/aviso foi criado, em vez da
    // aba mais recente — é lá que está o contexto de quem clicou.
    if (botId) alvo.bot_id = botId;
    // Antes do abrirConversa: quem lê isto é o fim do render do histórico, que
    // o abrirConversa dispara.
    wppIdParaFocar = wppIdFoco || null;
    abrirConversa(alvo);
    return true;
  };

  const urlParams = new URLSearchParams(window.location.search);
  const chatIdNaUrl = urlParams.get('chat');
  // `msg`: wpp_id da mensagem em que a conversa deve parar. Vem do apito do
  // lembrete quando ele é clicado de OUTRA tela (Kanban, Disparo...), onde não
  // dá pra chamar abrirConversaPorNumero direto porque a página vai recarregar.
  const msgNaUrl = urlParams.get('msg');
  if (chatIdNaUrl) {
    // Espera a primeira carga da lista pra aproveitar o que já veio e só ir ao
    // servidor se o cliente realmente não estiver ali.
    const esperarLista = setInterval(() => {
      if (carregandoChats || todosChats.length === 0) return;
      clearInterval(esperarLista);
      window
        .abrirConversaPorNumero(chatIdNaUrl, urlParams.get('botId'), msgNaUrl)
        .finally(() => {
          // Limpa a URL pra um F5 não reabrir o mesmo cliente.
          window.history.replaceState(
            {},
            document.title,
            window.location.pathname
          );
        });
    }, 300);
    // Rede de segurança: se a lista não carregar (sem conversas, erro de API),
    // tenta assim mesmo pelo servidor em vez de desistir calado como antes.
    setTimeout(() => {
      clearInterval(esperarLista);
      if (chatSelecionado) return; // já abriu pelo caminho de cima
      window.abrirConversaPorNumero(
        chatIdNaUrl,
        urlParams.get('botId'),
        msgNaUrl
      );
    }, 6000);
  }
  // ============================================================
  // LÓGICA DO FILTRO MULTI-SELECT DE VENDEDORES (SÓ ADMIN) - BLINDADO
  // ============================================================
  async function carregarFiltroVendedoresAdmin() {
    const container = document.getElementById('filtro-vendedores-container');
    const lista = document.getElementById('lista-filtro-vendedores');
    const contador = document.getElementById('contador-vend-selecionados');
    // Segurança: Se o HTML não estiver na tela, nem tenta rodar
    if (!container || !lista) return;
    // 🛡️ TRAVA BLINDADA: Lê o cargo do usuário DIRETO da memória do PC agora
    const infoUsuario = JSON.parse(localStorage.getItem('usuario') || '{}');
    if (!Acesso.pode('chat.filtrar_atendente')) {
      container.style.display = 'none';
      return; // Se não for admin, para aqui.
    }
    // Se chegou aqui, é Admin! Mostra a caixa!
    container.style.display = 'block';
    // Pega o header e remove eventos antigos duplicados (segurança pro F5)
    const headerAntigo = document.getElementById('header-filtro-vend');
    const headerNovo = headerAntigo.cloneNode(true);
    headerAntigo.replaceWith(headerNovo);
    const seta = document.getElementById('seta-filtro-vend');
    // Ação de abrir/fechar a sanfona (fecha a de Funil e etapa)
    headerNovo.addEventListener('click', (e) => {
      e.stopPropagation();
      abrirSanfonaFiltro('vend');
    });
    try {
      const res = await fetch('/api/team/vendedores', {
        headers: { Authorization: `Bearer ${localStorage.getItem('token')}` },
      });
      const vendedores = await res.json();
      lista.innerHTML = '';
      // Opção 1: "Livre (Sem Vendedor)"
      const divLivre = document.createElement('div');
      divLivre.className = 'item-filtro-vendedor';
      divLivre.dataset.username = 'SEM_DONO';
      divLivre.innerHTML = `<div class="check-box-vendedor"></div> <span style="font-weight:600; color:var(--texto-suave);">Fila Geral (Sem dono)</span>`;
      lista.appendChild(divLivre);
      // Opção 2+: Renderiza cada vendedor puxado do banco
      vendedores.forEach((v) => {
        const div = document.createElement('div');
        div.className = 'item-filtro-vendedor';
        div.dataset.username = v.username;
        // O 'chat_transferido' chega com o id do novo dono, não com o login
        div.dataset.userId = v.id;
        div.innerHTML = `<div class="check-box-vendedor"></div> <span>${v.name || v.username}</span>`;
        lista.appendChild(div);
      });
      // Lógica de clicar na caixinha para selecionar
      lista.querySelectorAll('.item-filtro-vendedor').forEach((item) => {
        item.addEventListener('click', (e) => {
          e.stopPropagation(); // Não fecha o popup geral
          item.classList.toggle('selecionado');
          // Varre a lista e pega todo mundo que está marcado com a classe "selecionado"
          const selecionados = Array.from(
            lista.querySelectorAll('.item-filtro-vendedor.selecionado')
          ).map((el) => el.dataset.username);
          // Salva na sua variável global de filtros (Se ela existir)
          if (typeof filtrosAtivos !== 'undefined') {
            filtrosAtivos.vendedores = selecionados;
          }
          // Escreve aquele "(3)" azulzinho do lado do título
          if (selecionados.length > 0) {
            contador.textContent = `(${selecionados.length})`;
            contador.style.display = 'inline';
          } else {
            contador.style.display = 'none';
          }
          atualizarResumoFiltros();
        });
      });
    } catch (e) {
      console.error('Erro ao puxar vendedores:', e);
      lista.innerHTML =
        '<span style="color:red; font-size:11px;">Erro ao carregar equipe.</span>';
    }
  }
  // Já engatilha a função pra rodar assim que a tela abrir
  carregarFiltroVendedoresAdmin();

  // ============================================================
  // FILTRO "FUNIL E ETAPA" (19/10/2026) — pedido das vendedoras: ver no
  // bate-papo só os clientes cujo cartão está em certos funis/etapas. Vale
  // pra todo mundo (a vendedora já só vê a carteira dela). Visual = o mesmo
  // seletor de funil da coluna do CRM (grupo-funil-dd / titulo-funil-dd /
  // etapas-container-dd / btn-etapa-dd, ver renderizarDropdownFunil), só que
  // marcando várias: etapa marcada fica azul com ✓, "Todas as etapas" marca
  // o funil inteiro e a faixa do funil mostra quantas estão marcadas. O que
  // vai pro servidor é a lista de etapas (?etapas=1,2,3 no /api/chat/contacts).
  // ============================================================
  function etapasMarcadasNoFiltro() {
    return Array.from(
      document.querySelectorAll('#lista-filtro-etapas .item-filtro-etapa.selecionado')
    ).map((el) => parseInt(el.dataset.etapaId, 10));
  }
  function atualizarFiltroFunilEtapa() {
    document.querySelectorAll('#lista-filtro-etapas .grupo-funil-dd').forEach((grupo) => {
      const etapas = grupo.querySelectorAll('.item-filtro-etapa');
      const marcadas = grupo.querySelectorAll('.item-filtro-etapa.selecionado').length;
      const contagem = grupo.querySelector('.contagem-etapas-filtro');
      contagem.textContent = marcadas;
      contagem.classList.toggle('hidden', marcadas === 0);
      const todas = grupo.querySelector('.btn-etapa-todas');
      const tudoMarcado = etapas.length > 0 && marcadas === etapas.length;
      todas.classList.toggle('selecionado', tudoMarcado);
      todas.querySelector('span').textContent = tudoMarcado ? 'Desmarcar todas' : 'Todas as etapas';
    });
    const contador = document.getElementById('contador-etapas-selecionadas');
    const total = etapasMarcadasNoFiltro().length;
    if (contador) {
      contador.textContent = `(${total})`;
      contador.style.display = total ? 'inline' : 'none';
    }
    atualizarResumoFiltros();
  }

  // ============================================================
  // PAINEL DE FILTROS (refeito em 19/10/2026): resumo nas linhas de
  // Vendedores e Funil e etapa, aviso de quantos filtros estão marcados no
  // topo, altura presa à tela (o miolo rola e o "Aplicar" fica sempre
  // visível) e uma lista aberta por vez. O estado de verdade continua em
  // filtrosAtivos — aqui é só o que o painel mostra.
  // ============================================================
  function atualizarResumoFiltros() {
    const nVend = document.querySelectorAll(
      '#lista-filtro-vendedores .item-filtro-vendedor.selecionado'
    ).length;
    const nEtapas = etapasMarcadasNoFiltro().length;
    const rv = document.getElementById('pf-resumo-vend');
    if (rv) {
      rv.textContent = nVend ? `${nVend} ${nVend > 1 ? 'selecionados' : 'selecionado'}` : 'Todos';
      rv.classList.toggle('pf-tem', nVend > 0);
    }
    const re = document.getElementById('pf-resumo-etapas');
    if (re) {
      re.textContent = nEtapas ? `${nEtapas} ${nEtapas > 1 ? 'etapas' : 'etapa'}` : 'Todos os funis';
      re.classList.toggle('pf-tem', nEtapas > 0);
    }
    const marcadaForaDoPadrao = (tipo, padrao) => {
      const b = document.querySelector(`#popup-filtros .opcao-filtro.ativo[data-tipo="${tipo}"]`);
      return !!b && b.dataset.valor !== padrao;
    };
    let n = 0;
    if (marcadaForaDoPadrao('status', 'todos')) n++;
    if (marcadaForaDoPadrao('periodo', 'todos')) n++;
    if (nVend) n++;
    if (nEtapas) n++;
    const topo = document.getElementById('pf-filtros-ativos');
    if (topo) topo.textContent = n ? `${n} ${n > 1 ? 'filtros ativos' : 'filtro ativo'}` : '';
  }
  // A altura máxima é o que sobra da tela abaixo do painel (em unidades
  // locais, por causa da escala responsiva — ver global.js).
  function ajustarAlturaFiltros() {
    const painel = document.getElementById('popup-filtros');
    if (!painel || painel.classList.contains('hidden')) return;
    painel.style.maxHeight = '';
    const topo = retanguloLocal(painel).top;
    painel.style.maxHeight = Math.max(240, Math.floor(viewportLocal().altura - topo - 12)) + 'px';
  }
  window.addEventListener('resize', ajustarAlturaFiltros);
  // Abrir uma lista fecha a outra (as duas abertas não cabem em tela pequena).
  function abrirSanfonaFiltro(qual) {
    const pares = {
      vend: ['filtro-vendedores-container', 'lista-filtro-vendedores', 'seta-filtro-vend'],
      etapas: ['filtro-etapas-container', 'lista-filtro-etapas', 'seta-filtro-etapas'],
    };
    const lista = document.getElementById(pares[qual][1]);
    if (!lista) return;
    const abrir = lista.classList.contains('hidden');
    Object.values(pares).forEach(([c, l, sId]) => {
      const cont = document.getElementById(c);
      const lst = document.getElementById(l);
      const seta = document.getElementById(sId);
      if (lst) lst.classList.add('hidden');
      if (cont) cont.classList.remove('pf-aberta');
      if (seta) seta.classList.remove('aberto');
    });
    if (abrir) {
      lista.classList.remove('hidden');
      document.getElementById(pares[qual][0]).classList.add('pf-aberta');
      const seta = document.getElementById(pares[qual][2]);
      if (seta) seta.classList.add('aberto');
    }
  }
  async function carregarFiltroFunilEtapa() {
    const lista = document.getElementById('lista-filtro-etapas');
    const header = document.getElementById('header-filtro-etapas');
    const seta = document.getElementById('seta-filtro-etapas');
    if (!lista || !header) return;
    header.addEventListener('click', (e) => {
      e.stopPropagation();
      abrirSanfonaFiltro('etapas');
    });
    try {
      const res = await fetch('/api/kanban/estrutura', {
        headers: { Authorization: `Bearer ${localStorage.getItem('token')}` },
      });
      const dados = await res.json();
      if (!dados.success) throw new Error('estrutura');
      lista.innerHTML = '';
      dados.canais.forEach((funil) => {
        const etapas = dados.colunas.filter((c) => String(c.canal_id) === String(funil.id));
        if (!etapas.length) return;
        const grupo = document.createElement('div');
        grupo.className = 'grupo-funil-dd';

        // Faixa do funil: abre/fecha as etapas (uma aberta por vez, igual ao
        // seletor do CRM) e mostra quantas estão marcadas.
        const titulo = document.createElement('div');
        titulo.className = 'titulo-funil-dd';
        titulo.innerHTML =
          '<span class="titulo-funil-filtro"><span></span><span class="contagem-etapas-filtro hidden">0</span></span><i class="bx bx-chevron-down chevron-canal"></i>';
        titulo.querySelector('.titulo-funil-filtro span').textContent = funil.nome;
        const caixa = document.createElement('div');
        caixa.className = 'etapas-container-dd';
        titulo.addEventListener('click', (e) => {
          e.stopPropagation();
          const aberto = caixa.classList.contains('aberto');
          lista.querySelectorAll('.etapas-container-dd').forEach((c) => c.classList.remove('aberto'));
          lista.querySelectorAll('.chevron-canal').forEach((c) => c.classList.remove('girar'));
          if (!aberto) {
            caixa.classList.add('aberto');
            titulo.querySelector('.chevron-canal').classList.add('girar');
          }
        });

        const todas = document.createElement('button');
        todas.type = 'button';
        todas.className = 'btn-etapa-dd btn-etapa-todas';
        todas.innerHTML = '<i class="bx bx-check-double"></i><span>Todas as etapas</span>';
        todas.addEventListener('click', (e) => {
          e.stopPropagation();
          const marcar = !todas.classList.contains('selecionado');
          caixa
            .querySelectorAll('.item-filtro-etapa')
            .forEach((el) => el.classList.toggle('selecionado', marcar));
          atualizarFiltroFunilEtapa();
        });
        caixa.appendChild(todas);

        etapas.forEach((etapa) => {
          const btn = document.createElement('button');
          btn.type = 'button';
          btn.className = 'btn-etapa-dd item-filtro-etapa';
          btn.dataset.etapaId = etapa.id;
          btn.innerHTML =
            '<span class="cor-etapa-dot"></span><span class="nome-etapa-filtro"></span><i class="bx bx-check check-etapa-filtro"></i>';
          btn.querySelector('.cor-etapa-dot').style.backgroundColor = etapa.cor || '#94a3b8';
          btn.querySelector('.nome-etapa-filtro').textContent = etapa.nome;
          if ((filtrosAtivos.etapas || []).includes(etapa.id)) btn.classList.add('selecionado');
          btn.addEventListener('click', (e) => {
            e.stopPropagation();
            btn.classList.toggle('selecionado');
            atualizarFiltroFunilEtapa();
          });
          caixa.appendChild(btn);
        });

        grupo.appendChild(titulo);
        grupo.appendChild(caixa);
        lista.appendChild(grupo);
      });
      atualizarFiltroFunilEtapa();
    } catch (e) {
      console.error('Erro ao carregar funis do filtro:', e);
      lista.innerHTML =
        '<span style="color:red; font-size:11px;">Erro ao carregar os funis.</span>';
    }
  }
  carregarFiltroFunilEtapa();
  // ============================================================
  // ⚡ LÓGICA DE COMANDOS DE BARRA (/SLASH COMMANDS) - BLINDADA
  // ============================================================
  const slashContainer = document.getElementById('slash-commands-container');
  const caixaDeTextoSlash = document.getElementById('input-mensagem-texto'); // <-- Ele caça o input sozinho agora!
  let mensagensProntas = [];
  // Botões de RCS da mensagem pronta escolhida, esperando o envio (ver
  // armarBotoesPendentes).
  let botoesPendentesRcs = [];
  let indexComandoAtivo = -1;
  // 'geral' = mensagens dos admins | 'minha' = mensagens da própria pessoa
  // Vendedora abre o menu direto nas "Minhas"; admin continua abrindo em "Geral"
  let abaComandoAtiva = Acesso.pode('mensagens.gerais') ? 'geral' : 'minha';
  let comandosFiltradosAtuais = [];
  // 1. Carrega as mensagens do banco (Geral + Minhas, já classificadas pelo backend)
  async function carregarMensagensProntas() {
    try {
      const res = await fetch('/api/config/mensagens?contexto=chat', {
        headers: { Authorization: `Bearer ${localStorage.getItem('token')}` },
      });
      const data = await res.json();
      if (data.success) mensagensProntas = data.mensagens;
    } catch (e) {
      console.error('Erro ao carregar atalhos:', e);
    }
  }
  carregarMensagensProntas();
  // Busca dos atalhos. O TÍTULO manda: se algum atalho casa pelo título, a
  // lista mostra só esses. O conteúdo continua sendo pesquisável, mas como
  // PLANO B, pra quem lembra do texto e não do apelido do atalho.
  //
  // O porquê: quem chama os atalhos de "1".."7" digitava "/1" e via também o
  // "/5", porque o texto do 5 tem "1." dentro (a lista de operações: "1. ...
  // 2. ... 3. ..."). Daí "/4" vir limpo e "/1", "/2" e "/3" virem sujos — não
  // era bug de número, era o conteúdo casando junto. Com título tendo
  // prioridade, "/1" volta a ser só o atalho 1.
  // Na conversa de RCS, as mensagens prontas que a Ótima recusaria (áudio,
  // PDF e outros documentos) nem aparecem no "/" — mesma regra do clipe.
  //
  // BOTÕES PENDENTES (mensagem pronta de RCS com botões, escolhida no "/").
  // Vão junto no próximo envio — texto, ou anexo com legenda — e o aviso
  // acima da caixa de texto mostra quais, com um X pra mandar sem eles.
  // Trocar de conversa ou de canal larga os botões.
  function armarBotoesPendentes(lista) {
    botoesPendentesRcs = Array.isArray(lista) ? lista.slice() : [];
    desenharAvisoBotoesPendentes();
  }
  function largarBotoesPendentes() {
    if (!botoesPendentesRcs.length) return;
    botoesPendentesRcs = [];
    desenharAvisoBotoesPendentes();
  }
  /** Os botões que vão no envio agora (só em conversa de RCS). */
  function botoesParaEnviar() {
    return botoesPendentesRcs.length && chatSelecionado && ehCanalRcs(chatSelecionado.bot_id)
      ? JSON.stringify(botoesPendentesRcs)
      : null;
  }
  function desenharAvisoBotoesPendentes() {
    let aviso = document.getElementById('aviso-botoes-rcs');
    if (!botoesPendentesRcs.length) {
      if (aviso) aviso.remove();
      return;
    }
    if (!aviso) {
      aviso = document.createElement('div');
      aviso.id = 'aviso-botoes-rcs';
      aviso.className = 'aviso-botoes-rcs';
      const area = document.querySelector('.chat-input-area');
      if (!area) return;
      area.parentNode.insertBefore(aviso, area);
    }
    aviso.innerHTML =
      '<i class="ph-bold ph-cursor-click"></i><span class="aviso-botoes-rcs-rotulo">Vai com os botões:</span>' +
      botoesPendentesRcs
        .map((b) => `<span class="aviso-botoes-rcs-pilula">${escaparHtmlChat(b.texto)}</span>`)
        .join('') +
      '<button type="button" class="aviso-botoes-rcs-tirar" title="Mandar sem os botões"><i class="bx bx-x"></i></button>';
    aviso.querySelector('.aviso-botoes-rcs-tirar').onclick = largarBotoesPendentes;
  }

  // LOCALIZAÇÃO PENDENTE (mensagem pronta do tipo 'localizacao', escolhida no
  // "/"). Fica num cartão acima da caixa de texto até ela confirmar (Enter ou
  // o botão de enviar) ou tirar no X. O texto que vai junto (o conteudo da
  // mensagem pronta — localização no WhatsApp não tem legenda) entra na caixa
  // de texto, editável, e sai logo depois do mapa pelo envio normal. Sai pelo /api/chat/send com o id da
  // mensagem pronta: as coordenadas vêm do banco, não da tela. Trocar de
  // conversa larga. Ver js/localizacao-mapa.js e backend/servicos/localizacao.js.
  let localizacaoPendente = null;
  function armarLocalizacaoPendente(msg, textoJunto) {
    if (!chatSelecionado) {
      mostrarAlerta('Selecione uma conversa antes de usar este atalho.', 'warning');
      return;
    }
    if (!msg.localizacao) {
      mostrarAlerta('Essa mensagem pronta está sem as coordenadas. Confira em Mensagens Prontas.', 'warning');
      return;
    }
    localizacaoPendente = msg;
    if (caixaDeTextoSlash) {
      caixaDeTextoSlash.value = textoJunto || '';
      caixaDeTextoSlash.style.height = 'auto';
      caixaDeTextoSlash.style.height = caixaDeTextoSlash.scrollHeight + 'px';
      caixaDeTextoSlash.focus();
    }
    desenharAvisoLocalizacao();
  }
  function largarLocalizacaoPendente() {
    if (!localizacaoPendente) return;
    localizacaoPendente = null;
    desenharAvisoLocalizacao();
  }
  function desenharAvisoLocalizacao() {
    let aviso = document.getElementById('aviso-localizacao');
    if (!localizacaoPendente) {
      if (aviso) aviso.remove();
      return;
    }
    if (!aviso) {
      aviso = document.createElement('div');
      aviso.id = 'aviso-localizacao';
      aviso.className = 'aviso-localizacao';
      const area = document.querySelector('.chat-input-area');
      if (!area) return;
      area.parentNode.insertBefore(aviso, area);
    }
    const cartao = window.mapaLocalizacao
      ? window.mapaLocalizacao.cartaoHtml(localizacaoPendente.localizacao, { mini: true })
      : escaparHtmlChat(localizacaoPendente.conteudo || '');
    aviso.innerHTML =
      '<div class="aviso-localizacao-corpo"><span class="aviso-localizacao-rotulo">📍 Enter manda esta localização' +
      (localizacaoPendente.conteudo ? ' e, logo depois, o texto da caixa' : '') +
      '.</span>' +
      cartao +
      '</div><button type="button" class="aviso-localizacao-tirar" title="Não enviar"><i class="bx bx-x"></i></button>';
    aviso.querySelector('.aviso-localizacao-tirar').onclick = largarLocalizacaoPendente;
  }
  async function enviarLocalizacaoPendente() {
    const msg = localizacaoPendente;
    if (!msg || !chatSelecionado) return false;
    const btnIcon = btnEnviar ? btnEnviar.querySelector('i') : null;
    const classeOriginal = btnIcon ? btnIcon.className : null;
    if (btnIcon) btnIcon.className = 'fas fa-spinner fa-spin';
    if (btnEnviar) btnEnviar.disabled = true;
    try {
      const formData = new FormData();
      formData.append('chatId', chatSelecionado.chat_id);
      formData.append('botId', chatSelecionado.bot_id);
      formData.append('localizacaoId', msg.id);
      if (mensagemParaResponder) {
        formData.append('quoted_wpp_id', mensagemParaResponder.id);
        formData.append('quoted_body', mensagemParaResponder.texto);
        formData.append('quoted_participant', mensagemParaResponder.autor);
        formData.append('quoted_type', mensagemParaResponder.tipo);
      }
      const res = await fetch('/api/chat/send', {
        method: 'POST',
        headers: { Authorization: `Bearer ${localStorage.getItem('token')}` },
        body: formData,
      });
      const data = await res.json().catch(() => ({}));
      if (!data.success) {
        mostrarAlerta(data.message || 'Erro ao enviar a localização.', 'error');
        return false;
      }
      largarLocalizacaoPendente();
      // A citação (se tinha) foi com o mapa. O fecharPreviewResposta também
      // limpa a caixa de texto (é o cancelar da edição), e ali está o texto
      // que vai junto logo em seguida: guarda e devolve.
      const textoJunto = inputMsg.value;
      fecharPreviewResposta();
      inputMsg.value = textoJunto;
      return true;
    } catch (erro) {
      console.error('Erro ao enviar localização:', erro);
      mostrarAlerta('Erro ao enviar a localização.', 'error');
      return false;
    } finally {
      if (btnIcon) btnIcon.className = classeOriginal;
      if (btnEnviar) btnEnviar.disabled = false;
    }
  }

  // E, em qualquer canal, some a mensagem pronta que usa "(imagem)": essa
  // variável é a proposta gerada pela AUTOMAÇÃO (bloco Gerar imagem), que o
  // bate-papo não tem — usada aqui, o cliente receberia "(imagem)" escrito.
  // Ex.: "Proposta RCS (automação)", criada em 21/09/2026.
  function mensagensProntasDoCanal() {
    const noRcs = !!(chatSelecionado && ehCanalRcs(chatSelecionado.bot_id));
    // O CANAL da mensagem pronta (21/09/2026): "Só RCS" some da conversa de
    // WhatsApp e "Só WhatsApp" some da de RCS; "Geral" aparece nas duas.
    const doChat = mensagensProntas.filter((m) => {
      if (/\(imagem\)/i.test(String(m.conteudo || ''))) return false;
      const canal = m.canal || 'geral';
      return canal === 'geral' || canal === (noRcs ? 'rcs' : 'whatsapp');
    });
    if (!noRcs) return doChat;
    return doChat.filter((m) => {
      if (m.tipo === 'audio' || m.tipo === 'localizacao') return false;
      if (!m.file_path) return true;
      return arquivoCabeNoRcs({ name: String(m.file_path).split('?')[0] });
    });
  }
  // Sem acento e minúsculo: "/localizacao" acha "Localização".
  const semAcentoComando = (t) =>
    String(t || '')
      .normalize('NFD')
      .replace(/[\u0300-\u036f]/g, '')
      .toLowerCase();
  function filtrarComandos(busca) {
    const termo = semAcentoComando(busca).trim();
    const daAba = mensagensProntasDoCanal().filter(
      (m) => m.escopo === abaComandoAtiva
    );
    if (!termo) return daAba;
    // Nota menor = mais relevante. 3 é "só o conteúdo casou".
    const notaDe = (m) => {
      const titulo = semAcentoComando(m.titulo);
      if (titulo === termo) return 0;
      if (titulo.startsWith(termo)) return 1;
      if (titulo.includes(termo)) return 2;
      return 3;
    };
    const candidatos = [];
    let achouPeloTitulo = false;
    daAba.forEach((m) => {
      const nota = notaDe(m);
      if (nota < 3) {
        achouPeloTitulo = true;
        candidatos.push({ m, nota });
      } else if (semAcentoComando(m.conteudo).includes(termo)) {
        candidatos.push({ m, nota });
      }
    });
    return candidatos
      .filter(({ nota }) => !achouPeloTitulo || nota < 3)
      .sort((a, b) => a.nota - b.nota)
      .map(({ m }) => m);
  }
  function atualizarPainelComandos(busca) {
    comandosFiltradosAtuais = filtrarComandos(busca);
    // Igual WhatsApp: a primeira sugestão já nasce destacada, então um Enter
    // simples (sem precisar de seta) já seleciona o comando do topo da lista.
    indexComandoAtivo = comandosFiltradosAtuais.length > 0 ? 0 : -1;
    renderizarComandos(comandosFiltradosAtuais);
  }
  // 2. Monitora a digitação
  if (caixaDeTextoSlash) {
    caixaDeTextoSlash.addEventListener('keyup', (e) => {
      const texto = e.target.value;
      // Se o texto começa com "/"
      if (texto.startsWith('/')) {
        const busca = texto.substring(1).toLowerCase(); // O que vem depois da barra
        atualizarPainelComandos(busca);
        // Controle por setas do teclado
        if (comandosFiltradosAtuais.length > 0) {
          if (e.key === 'ArrowDown') {
            indexComandoAtivo =
              (indexComandoAtivo + 1) % comandosFiltradosAtuais.length;
            atualizarSelecaoTeclado();
            return;
          }
          if (e.key === 'ArrowUp') {
            indexComandoAtivo =
              (indexComandoAtivo - 1 + comandosFiltradosAtuais.length) %
              comandosFiltradosAtuais.length;
            atualizarSelecaoTeclado();
            return;
          }
          // Enter é tratado no 'keydown' do inputMsg (roda antes deste keyup e
          // consegue barrar o envio da mensagem crua "/comando"). Tratar de
          // novo aqui duplicaria a seleção nos casos em que selecionarComando
          // é assíncrono (ex.: mídia sem legenda, que ainda não limpou o "/"
          // do campo quando este keyup dispara).
        }
      } else {
        if (slashContainer) slashContainer.classList.add('hidden');
        indexComandoAtivo = -1;
      }
    });
    // Teclado de celular (Gboard/Samsung com texto preditivo) nem sempre
    // dispara keyup por tecla — o 'input' sai sempre que o texto muda.
    caixaDeTextoSlash.addEventListener('input', (e) => {
      const texto = e.target.value;
      if (texto.startsWith('/')) {
        atualizarPainelComandos(texto.substring(1).toLowerCase());
      } else if (slashContainer && !slashContainer.classList.contains('hidden')) {
        slashContainer.classList.add('hidden');
        indexComandoAtivo = -1;
      }
    });
  }
  function renderizarComandos(lista) {
    if (!slashContainer) return;
    slashContainer.innerHTML = '';

    // 👇 Cabeçalho com as abas "Geral" e "Minhas" no canto superior direito do menu
    const divTabs = document.createElement('div');
    divTabs.className = 'slash-tabs-header';
    const doCanal = mensagensProntasDoCanal();
    const qtdGeral = doCanal.filter((m) => m.escopo === 'geral').length;
    const qtdMinhas = doCanal.filter((m) => m.escopo === 'minha').length;
    divTabs.innerHTML = `
      <button type="button" class="slash-tab-btn${abaComandoAtiva === 'geral' ? ' ativo' : ''}" data-aba="geral">Geral (${qtdGeral})</button>
      <button type="button" class="slash-tab-btn${abaComandoAtiva === 'minha' ? ' ativo' : ''}" data-aba="minha">Minhas (${qtdMinhas})</button>
    `;
    divTabs.querySelectorAll('.slash-tab-btn').forEach((btn) => {
      btn.addEventListener('click', (e) => {
        e.preventDefault();
        e.stopPropagation();
        abaComandoAtiva = btn.dataset.aba;
        const buscaAtual = caixaDeTextoSlash
          ? caixaDeTextoSlash.value.substring(1).toLowerCase()
          : '';
        atualizarPainelComandos(buscaAtual);
        if (caixaDeTextoSlash) caixaDeTextoSlash.focus();
      });
    });
    slashContainer.appendChild(divTabs);

    const divLista = document.createElement('div');
    divLista.className = 'slash-tabs-lista';
    if (lista.length === 0) {
      divLista.innerHTML = `<div class="command-item-vazio">Nenhuma mensagem ${abaComandoAtiva === 'geral' ? 'geral' : 'sua'} encontrada.</div>`;
    } else {
      lista.forEach((msg, idx) => {
        const div = document.createElement('div');
        div.className =
          'command-item' + (idx === indexComandoAtivo ? ' teclado-ativo' : '');
        const previewTexto =
          msg.tipo === 'localizacao' && msg.localizacao
            ? escaparHtmlChat(
                '📍 ' + (msg.localizacao.nome || 'Localização') +
                  (msg.conteudo ? ' · ' + msg.conteudo.substring(0, 80) : '')
              )
            : msg.tipo === 'audio'
            ? '🎙️ Mensagem de Áudio'
            : msg.conteudo
              ? `${escaparHtmlChat(msg.conteudo.substring(0, 100))}...`
              : msg.file_path
                ? '📎 Mídia Anexada'
                : '';
        const qtdBotoes = Array.isArray(msg.botoes) ? msg.botoes.length : 0;
        div.innerHTML = `
        <span class="command-trigger">/${escaparHtmlChat(msg.titulo)}${
          qtdBotoes
            ? ` <span class="command-botoes-rcs"><i class="ph-bold ph-cursor-click"></i> ${qtdBotoes} ${qtdBotoes > 1 ? 'botões' : 'botão'}</span>`
            : ''
        }</span>
        <span class="command-text">${previewTexto}</span>
      `;
        div.onclick = () => selecionarComando(msg);
        divLista.appendChild(div);
      });
    }
    slashContainer.appendChild(divLista);

    slashContainer.classList.remove('hidden');
  }
  // 🧠 Monta o mapa completo de variáveis disponíveis: fixas do CRM + campos dinâmicos/customizados
  function montarMapaVariaveisCrm() {
    const inputCrmNome = document.getElementById('crm-nome');
    const inputCrmTelefone = document.getElementById('crm-telefone');
    const headerNome = document.getElementById('header-nome-contato');
    // 1. Tenta pegar o nome do painel lateral do CRM. Se estiver vazio, pega do Cabeçalho do chat.
    const nomeCompleto =
      inputCrmNome && inputCrmNome.value
        ? inputCrmNome.value
        : headerNome
          ? headerNome.textContent
          : '';
    const primeiroNome = nomeCompleto.trim().split(' ')[0] || '';
    // 2. Tenta pegar o telefone do painel lateral
    const telefone =
      inputCrmTelefone && inputCrmTelefone.value
        ? inputCrmTelefone.value.replace(/\D/g, '')
        : '';
    // 3. Pega os dados da vendedora logada no PC
    const infoUsuario = JSON.parse(localStorage.getItem('usuario') || '{}');
    const nomeAtendente =
      infoUsuario.name || infoUsuario.username || 'Atendente';
    const primeiroNomeAtendente = nomeAtendente.split(' ')[0];
    // 4. CPF e e-mail do painel lateral (antes ficavam de fora da substituição)
    const cpfValor = crmCpf ? window.limparEPreencherCPF(crmCpf.value) : '';
    const emailValor = crmEmail ? crmEmail.value.trim() : '';

    const variaveis = {
      primeiro_nome: primeiroNome,
      nome: primeiroNome,
      nome_completo: nomeCompleto,
      telefone: telefone,
      nome_atendente: primeiroNomeAtendente,
      cpf: cpfValor,
      email: emailValor,
    };

    // 👇 NOVO: Qualquer campo dinâmico/customizado criado no CRM vira variável automaticamente
    camposDinamicosCRM.forEach((campo) => {
      const inputEl = document.getElementById(`crm-dinamico-${campo.id}`);
      if (!inputEl) return;
      const chave = campo.nome.toLowerCase();
      if (chave in variaveis) return; // Não deixa campo customizado sobrescrever variável fixa
      variaveis[chave] =
        inputEl.type === 'checkbox'
          ? inputEl.checked
            ? 'true'
            : 'false'
          : inputEl.value || '';
    });

    return variaveis;
  }
  // 👇 TRADUTOR UNIVERSAL: Aceita tanto chaves { } quanto parênteses ( ) para QUALQUER variável do mapa
  function substituirVariaveisCrm(texto, variaveis) {
    let resultado = texto || '';
    Object.entries(variaveis).forEach(([chave, valor]) => {
      const regexEscapada = chave.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
      const regex = new RegExp(
        `\\{${regexEscapada}\\}|\\(${regexEscapada}\\)`,
        'gi'
      );
      resultado = resultado.replace(regex, valor);
    });
    return resultado;
  }
  // 🎙️📎 Envia direto pro chat uma mensagem pronta de Áudio ou com Mídia/arquivo anexado
  async function enviarMidiaDeMensagemPronta(msg, legenda) {
    if (!chatSelecionado) {
      mostrarAlerta(
        'Selecione uma conversa antes de usar este atalho.',
        'warning'
      );
      return;
    }
    const btnIcon = btnEnviar ? btnEnviar.querySelector('i') : null;
    const classeOriginal = btnIcon ? btnIcon.className : null;
    if (btnIcon) btnIcon.className = 'fas fa-spinner fa-spin';
    if (btnEnviar) btnEnviar.disabled = true;
    try {
      const respostaArquivo = await fetch(msg.file_path);
      if (!respostaArquivo.ok)
        throw new Error('Falha ao buscar o arquivo salvo da mensagem pronta.');
      const blob = await respostaArquivo.blob();
      const nomeArquivo = msg.file_path.split('/').pop();
      const arquivo = new File([blob], nomeArquivo, {
        type: blob.type || 'application/octet-stream',
      });

      const formData = new FormData();
      formData.append('chatId', chatSelecionado.chat_id);
      formData.append('botId', chatSelecionado.bot_id);
      formData.append('file', arquivo);
      // Áudio (ptt) não aceita legenda na Evolution API, então só anexa texto para mídia/documento
      if (msg.tipo !== 'audio' && legenda) {
        formData.append('message', legenda);
      }
      if (mensagemParaResponder) {
        formData.append('quoted_wpp_id', mensagemParaResponder.id);
        formData.append('quoted_body', mensagemParaResponder.texto);
        formData.append('quoted_participant', mensagemParaResponder.autor);
        formData.append('quoted_type', mensagemParaResponder.tipo);
      }

      const res = await fetch('/api/chat/send', {
        method: 'POST',
        headers: { Authorization: `Bearer ${localStorage.getItem('token')}` },
        body: formData,
      });
      const data = await res.json();
      if (!data.success) {
        mostrarAlerta(
          data.message || 'Erro ao enviar mensagem pronta.',
          'error'
        );
      } else {
        fecharPreviewResposta();
      }
    } catch (erro) {
      console.error('Erro ao enviar mídia da mensagem pronta:', erro);
      mostrarAlerta('Erro ao enviar a mensagem pronta.', 'error');
    } finally {
      if (btnIcon) btnIcon.className = classeOriginal;
      if (btnEnviar) btnEnviar.disabled = false;
    }
  }
  // 📎 Mídia/arquivo de Mensagem Pronta: mesma barrinha de anexo usada quando ela
  // anexa manualmente uma imagem/vídeo/arquivo — fica ali em cima com a legenda
  // no input de baixo, e ela confirma/edita antes de mandar (sem envio direto)
  async function anexarMensagemProntaNoPreview(msg, legenda) {
    if (!chatSelecionado) {
      mostrarAlerta(
        'Selecione uma conversa antes de usar este atalho.',
        'warning'
      );
      return;
    }
    try {
      const respostaArquivo = await fetch(msg.file_path);
      if (!respostaArquivo.ok)
        throw new Error('Falha ao buscar o arquivo salvo da mensagem pronta.');
      const blob = await respostaArquivo.blob();
      const nomeArquivo = msg.file_path.split('/').pop();
      const arquivo = new File([blob], nomeArquivo, {
        type: blob.type || 'application/octet-stream',
      });

      listaArquivosParaEnviar = [...listaArquivosParaEnviar, arquivo];
      renderizarPreviewBar();

      if (caixaDeTextoSlash && legenda) {
        caixaDeTextoSlash.value = legenda;
        caixaDeTextoSlash.style.height = 'auto';
        caixaDeTextoSlash.style.height = caixaDeTextoSlash.scrollHeight + 'px';
      }
    } catch (erro) {
      console.error('Erro ao anexar mídia da mensagem pronta:', erro);
      mostrarAlerta('Erro ao carregar o arquivo da mensagem pronta.', 'error');
    }
  }
  function selecionarComando(msg) {
    const variaveis = montarMapaVariaveisCrm();
    const textoProcessado = substituirVariaveisCrm(msg.conteudo, variaveis);

    if (slashContainer) slashContainer.classList.add('hidden');
    indexComandoAtivo = -1;

    // Mensagem pronta de RCS com botões: os botões ficam esperando o envio
    // (outra mensagem escolhida depois troca ou larga os de antes).
    if (chatSelecionado && ehCanalRcs(chatSelecionado.bot_id) && Array.isArray(msg.botoes) && msg.botoes.length)
      armarBotoesPendentes(msg.botoes);
    else largarBotoesPendentes();

    // 📍 Localização: cartão de confirmação acima da caixa de texto; o Enter
    // (ou o botão de enviar) manda o cartão de mapa do WhatsApp.
    if (msg.tipo === 'localizacao') {
      armarLocalizacaoPendente(msg, textoProcessado);
      return;
    }

    // 🎙️ Mensagem de Áudio: mostra a barrinha de "pausar e ouvir" pra ela confirmar
    // antes de mandar pro cliente (só não dá pra "continuar gravando", já que o
    // áudio já foi gravado antes, não é uma gravação em andamento agora)
    if (msg.tipo === 'audio') {
      if (caixaDeTextoSlash) caixaDeTextoSlash.value = '';
      mostrarPreviewAudioMensagemPronta(msg);
      return;
    }

    // 📎 Mídia/arquivo anexado (imagem, vídeo, documento): anexa na barra de preview,
    // igual anexar manualmente, pra ela poder conferir e editar a legenda antes de mandar
    if (msg.file_path) {
      anexarMensagemProntaNoPreview(msg, textoProcessado);
      return;
    }

    // Mensagem só de texto: mantém o comportamento original (joga no campo pra revisar antes de enviar)
    if (caixaDeTextoSlash) {
      caixaDeTextoSlash.value = textoProcessado;
      caixaDeTextoSlash.focus();
      // Ajusta a altura da caixa caso o texto seja gigante
      caixaDeTextoSlash.style.height = 'auto';
      caixaDeTextoSlash.style.height = caixaDeTextoSlash.scrollHeight + 'px';
    }
  }
  function atualizarSelecaoTeclado() {
    if (!slashContainer) return;
    const itens = slashContainer.querySelectorAll('.command-item');
    itens.forEach((el, idx) => {
      if (idx === indexComandoAtivo) el.classList.add('teclado-ativo');
      else el.classList.remove('teclado-ativo');
    });
  }
  // ============================================================
  // 🔍 LÓGICA DO MODAL DE CONSULTA DE SALDO (API EXTERNA)
  // ============================================================
  const btnAbrirConsulta = document.getElementById('btn-abrir-consulta');
  const modalConsulta = document.getElementById('modal-consulta-api');
  const btnFecharConsulta = document.getElementById('btn-fechar-consulta');
  const selectConvenio = document.getElementById('select-convenio-consulta');
  const msgImplementacao = document.getElementById(
    'msg-implementacao-consulta'
  );
  const blocoSiape = document.getElementById('bloco-siape-consulta');
  const inputCpfConsulta = document.getElementById('input-cpf-consulta');
  const btnToggleCpfConsulta = document.getElementById(
    'btn-toggle-cpf-consulta'
  );
  const inputFatorConsulta = document.getElementById('input-fator-consulta');
  const btnEditarFator = document.getElementById('btn-editar-fator');
  const btnExecutarConsulta = document.getElementById('btn-executar-consulta');
  let modoCpfManual = false;
  if (btnAbrirConsulta) {
    btnAbrirConsulta.addEventListener('click', (e) => {
      e.stopPropagation();
      modalConsulta.classList.toggle('hidden');
      btnAbrirConsulta.classList.toggle('ativo');
      // Auto-preenche o CPF caso exista no CRM atual
      if (
        !modoCpfManual &&
        crmCpf &&
        !modalConsulta.classList.contains('hidden')
      ) {
        inputCpfConsulta.value = window.limparEPreencherCPF(crmCpf.value);
      }
    });
  }
  if (btnFecharConsulta) {
    btnFecharConsulta.addEventListener('click', () => {
      modalConsulta.classList.add('hidden');
      btnAbrirConsulta.classList.remove('ativo');
    });
  }
  if (selectConvenio) {
    selectConvenio.addEventListener('change', (e) => {
      const val = e.target.value;
      msgImplementacao.classList.add('hidden');
      blocoSiape.classList.add('hidden');
      if (val === 'siape') {
        blocoSiape.classList.remove('hidden');
      } else if (val) {
        msgImplementacao.classList.remove('hidden');
      }
    });
  }
  if (btnToggleCpfConsulta) {
    btnToggleCpfConsulta.addEventListener('click', () => {
      modoCpfManual = !modoCpfManual;
      if (modoCpfManual) {
        inputCpfConsulta.readOnly = false;
        inputCpfConsulta.placeholder = 'Digite o CPF...';
        inputCpfConsulta.value = '';
        btnToggleCpfConsulta.classList.add('selecionado-azul'); // Liga o azul
        inputCpfConsulta.focus();
      } else {
        inputCpfConsulta.readOnly = true;
        inputCpfConsulta.placeholder = 'CPF do sistema...';
        btnToggleCpfConsulta.classList.remove('selecionado-azul'); // Volta pro cinza
        inputCpfConsulta.value = crmCpf
          ? window.limparEPreencherCPF(crmCpf.value)
          : '';
      }
    });
  }
  if (inputCpfConsulta) {
    inputCpfConsulta.addEventListener('input', (e) => {
      if (modoCpfManual) {
        e.target.value = window.aplicarMascaraCPF(e.target.value);
        if (crmCpf) crmCpf.value = e.target.value;
      }
    });
  }
  if (btnEditarFator) {
    btnEditarFator.addEventListener('click', () => {
      inputFatorConsulta.readOnly = !inputFatorConsulta.readOnly;
      if (!inputFatorConsulta.readOnly) {
        inputFatorConsulta.focus();
        btnEditarFator.classList.add('selecionado-azul'); // Liga o azul
      } else {
        btnEditarFator.classList.remove('selecionado-azul'); // Volta pro cinza
      }
    });
  }
  let cacheCompletoConsultaSiape = null;
  if (btnExecutarConsulta) {
    btnExecutarConsulta.addEventListener('click', async () => {
      const cpfCru = window.limparEPreencherCPF(inputCpfConsulta.value);
      const fator = inputFatorConsulta.value;
      // Lê se a vendedora já escolheu alguma matrícula na tela
      const selectMatricula = document.getElementById(
        'select-matricula-consulta'
      );
      const containerMatriculas = document.getElementById(
        'container-multiplas-matriculas'
      );
      let matriculaSelecionada = null;
      if (
        !containerMatriculas.classList.contains('hidden') &&
        selectMatricula.value
      ) {
        matriculaSelecionada = selectMatricula.value;
      }
      if (!cpfCru || cpfCru.length !== 11)
        return mostrarAlerta('Insira um CPF válido.', 'warning');
      if (!chatSelecionado)
        return mostrarAlerta('Selecione uma conversa ativa.', 'warning');
      const btnIcon = btnExecutarConsulta.innerHTML;
      btnExecutarConsulta.innerHTML = '<i class="fas fa-spinner fa-spin"></i>';
      btnExecutarConsulta.disabled = true;
      const placeholderResultados = document.getElementById(
        'placeholder-resultados-galeria'
      );
      const areaResultados = document.getElementById(
        'resultados-consulta-siape-dinamico'
      );
      if (areaResultados) areaResultados.classList.add('hidden');
      if (placeholderResultados) {
        placeholderResultados.innerHTML = `
          <i class="fas fa-spinner fa-spin" style="font-size: 38px; color: var(--azul-principal); margin-bottom: 15px;"></i>
          <span style="font-size: 15px; font-weight: 600; color: var(--texto-principal);">Buscando Dados...</span>
          <span style="font-size: 13px; color: var(--texto-suave); margin-top: 5px;">Conectando ao sistema SIAPE.</span>
        `;
        placeholderResultados.style.display = 'flex';
      }
      try {
        const res = await fetch('/api/consulta/siape', {
          method: 'POST',
          headers: {
            'Content-Type': 'application/json',
            Authorization: `Bearer ${localStorage.getItem('token')}`,
          },
          body: JSON.stringify({
            cpf: cpfCru,
            fator,
            chatId: chatSelecionado.chat_id,
            matricula: matriculaSelecionada, // Envia junto pro backend
          }),
        });
        const json = await res.json();
        if (json.success && json.data) {
          if (placeholderResultados)
            placeholderResultados.style.display = 'none';
          cacheCompletoConsultaSiape = json.data;
          mostrarAlerta('Consulta SIAPE realizada!', 'success');
          if (crmCpf) {
            crmCpf.value = window.aplicarMascaraCPF(cpfCru);
            chatSelecionado.cpf = cpfCru;
          }
          processarResultadosConsultaSiape();
        } else if (json.precisa_matricula) {
          if (placeholderResultados)
            placeholderResultados.style.display = 'none';
          mostrarAlerta(
            'Este cliente possui mais de uma matrícula. Por favor, selecione qual consultar!',
            'info'
          );
          const containerMatriculas = document.getElementById(
            'container-multiplas-matriculas'
          );
          const areaResultados = document.getElementById(
            'resultados-consulta-siape-dinamico'
          );
          const selectMatricula = document.getElementById(
            'select-matricula-consulta'
          );
          areaResultados.classList.remove('hidden');
          containerMatriculas.classList.remove('hidden');
          selectMatricula.innerHTML =
            '<option value="">Selecione a matrícula...</option>';
          json.matriculas.forEach((mat) => {
            const opt = document.createElement('option');
            opt.value = mat;
            opt.textContent = `Matrícula: ${mat}`;
            selectMatricula.appendChild(opt);
          });
          const wrapperMatricula = document.querySelector(
            '.wrapper-de-select-matricula-consulta'
          );
          if (
            wrapperMatricula &&
            typeof wrapperMatricula.atualizarOpcoes === 'function'
          ) {
            wrapperMatricula.atualizarOpcoes();
          }
          document.getElementById('prop-nome-cliente').textContent = '---';
          document.getElementById('prop-cpf-cliente').textContent = '---';
          document.getElementById('prop-idade-cliente').textContent = '---';
          document.getElementById('prop-orgao-cliente').textContent = '---';
          document.getElementById('lista-contratos-proposta').innerHTML = '';
          selectMatricula.onchange = () => {
            if (selectMatricula.value) {
              btnExecutarConsulta.click();
            }
          };
        } else {
          if (placeholderResultados) {
            let textoErro =
              json.message ||
              'Falha desconhecida ao processar simulação na API.';
            let iconeErro = 'fas fa-exclamation-triangle';
            if (json.statusErro === 400) {
              textoErro =
                'CPF Inválido. Verifique se os 11 números foram digitados corretamente e tente novamente.';
              iconeErro = 'fas fa-id-card-alt';
            } else if (json.statusErro === 401) {
              textoErro =
                'Token de acesso inválido ou expirado. Tente novamente.';
              iconeErro = 'fas fa-key';
            } else if (json.statusErro === 403) {
              textoErro =
                'Acesso bloqueado: IP não autorizado. Solicite a liberação do servidor na Bitech.';
              iconeErro = 'fas fa-shield-alt';
            } else if (json.statusErro === 404) {
              textoErro =
                'CPF ou Matrícula não localizados na base de dados do SIAPE. Verifique os números.';
              iconeErro = 'fas fa-search-minus';
            }
            placeholderResultados.innerHTML = `
              <i class="${iconeErro}" style="font-size: 38px; color: #ef4444; margin-bottom: 15px;"></i>
              <span style="font-size: 15px; font-weight: 600; color: #ef4444;">Consulta Recusada</span>
              <span style="font-size: 13px; color: var(--texto-suave); margin-top: 5px; text-align: center; max-width: 80%;">${textoErro}</span>
            `;
            placeholderResultados.style.display = 'flex';
          }
        }
      } catch (err) {
        if (placeholderResultados) {
          placeholderResultados.innerHTML = `
              <i class="fas fa-wifi" style="font-size: 38px; color: #ef4444; margin-bottom: 15px;"></i>
              <span style="font-size: 15px; font-weight: 600; color: #ef4444;">Erro de Conexão</span>
              <span style="font-size: 13px; color: var(--texto-suave); margin-top: 5px;">O servidor não conseguiu se comunicar com a API.</span>
           `;
          placeholderResultados.style.display = 'flex';
        }
      } finally {
        btnExecutarConsulta.innerHTML = btnIcon;
        btnExecutarConsulta.disabled = false;
      }
    });
  }
  // ==========================================================================
  // 🎨 TRANSFORMAÇÃO DOS DROPS DO SIMULADOR EM DROPDOWNS CUSTOMIZADOS FLUTUANTES
  // ==========================================================================
  function customizarSeletorSimulador(selectId, placeholderText) {
    const selectOriginal = document.getElementById(selectId);
    if (!selectOriginal) return;
    // 1. Esconde o seletor nativo do navegador
    selectOriginal.style.display = 'none';
    // Remove wrapper antigo se o F5/recarregamento rodar duas vezes
    const wrapperAntigo = selectOriginal.parentNode.querySelector(
      `.wrapper-de-${selectId}`
    );
    if (wrapperAntigo) wrapperAntigo.remove();
    // 2. Cria a nova estrutura visual flutuante
    const wrapper = document.createElement('div');
    wrapper.className = `simulador-dropdown-wrapper wrapper-de-${selectId}`;
    wrapper.innerHTML = `
      <div class="simulador-dropdown-trigger">
        <span>${placeholderText}</span>
        <i class="fas fa-chevron-down"></i>
      </div>
      <div class="simulador-dropdown-options"></div>
    `;
    // Insere logo após o select original
    selectOriginal.parentNode.insertBefore(wrapper, selectOriginal.nextSibling);
    const trigger = wrapper.querySelector('.simulador-dropdown-trigger');
    const optionsContainer = wrapper.querySelector(
      '.simulador-dropdown-options'
    );
    const displaySpan = trigger.querySelector('span');
    // Função interna para reconstruir as opções (essencial para a Matrícula que muda dinamicamente!)
    wrapper.atualizarOpcoes = function () {
      optionsContainer.innerHTML = '';
      const optionsNativas = selectOriginal.querySelectorAll('option');
      // Se a opção selecionada nativa mudou, atualiza o texto do botão principal
      if (selectOriginal.value) {
        const opcaoAtiva = selectOriginal.querySelector(
          `option[value="${selectOriginal.value}"]`
        );
        if (opcaoAtiva) displaySpan.textContent = opcaoAtiva.textContent;
      } else {
        displaySpan.textContent = placeholderText;
      }
      optionsNativas.forEach((opt) => {
        const divOpt = document.createElement('div');
        divOpt.className = 'simulador-custom-option';
        if (selectOriginal.value === opt.value)
          divOpt.classList.add('selected');
        divOpt.textContent = opt.textContent;
        divOpt.onclick = (e) => {
          e.stopPropagation();
          // Seta o valor no select nativo do HTML
          selectOriginal.value = opt.value;
          displaySpan.textContent = opt.textContent;
          // Fecha o drop
          wrapper.classList.remove('aberto');
          // 🔥 ENGANA O SISTEMA: Dispara o 'change' original para acionar seus cálculos/filtros do back-end!
          selectOriginal.dispatchEvent(new Event('change'));
        };
        optionsContainer.appendChild(divOpt);
      });
    };
    // Abre e fecha o drop ao clicar
    trigger.onclick = (e) => {
      e.stopPropagation();
      // Atualiza as opções antes de abrir (obrigatório para pegar as matrículas injetadas via API)
      wrapper.atualizarOpcoes();
      // Fecha qualquer outro dropdown do simulador que esteja aberto
      document.querySelectorAll('.simulador-dropdown-wrapper').forEach((w) => {
        if (w !== wrapper) w.classList.remove('aberto');
      });
      wrapper.classList.toggle('aberto');
    };
    // Fecha se o usuário clicar em qualquer outro lugar da tela
    document.addEventListener('click', (e) => {
      if (!wrapper.contains(e.target)) {
        wrapper.classList.remove('aberto');
      }
    });
    // Inicia a primeira carga de opções
    wrapper.atualizarOpcoes();
    // Monitora se algum script externo mexer no select nativo para atualizar o visual do nosso drop
    selectOriginal.addEventListener('change', () => {
      const opcaoAtiva = selectOriginal.querySelector(
        `option[value="${selectOriginal.value}"]`
      );
      displaySpan.textContent = opcaoAtiva
        ? opcaoAtiva.textContent
        : placeholderText;
    });
  }
  // Ativa a mágica para o campo de Convênio ao carregar a página
  customizarSeletorSimulador(
    'select-convenio-consulta',
    'Selecione o convênio...'
  );
  // Ativa a mágica para o campo de Matrícula ao carregar a página
  customizarSeletorSimulador(
    'select-matricula-consulta',
    'Selecione a matrícula...'
  );
  function processarResultadosConsultaSiape() {
    const areaResultados = document.getElementById(
      'resultados-consulta-siape-dinamico'
    );
    const containerMatriculas = document.getElementById(
      'container-multiplas-matriculas'
    );
    const selectMatricula = document.getElementById(
      'select-matricula-consulta'
    );
    if (!cacheCompletoConsultaSiape) return;
    areaResultados.classList.remove('hidden');
    // Mantemos isso caso a resposta da API de sucesso devolva as matrículas também
    const matriculas = cacheCompletoConsultaSiape.matriculas_disponiveis || [];
    if (matriculas.length > 1) {
      containerMatriculas.classList.remove('hidden');
      selectMatricula.innerHTML = '';
      matriculas.forEach((mat) => {
        const opt = document.createElement('option');
        opt.value = mat;
        opt.textContent = `Matrícula: ${mat}`;
        if (
          String(mat) === String(cacheCompletoConsultaSiape.matricula_simulada)
        ) {
          opt.selected = true;
        }
        selectMatricula.appendChild(opt);
      });
      // Se a vendedora quiser trocar DEPOIS de já ter consultado
      selectMatricula.onchange = (e) => {
        if (e.target.value) {
          document.getElementById('btn-executar-consulta').click();
        }
      };
    } else {
      containerMatriculas.classList.add('hidden');
    }
    atualizarCamposValoresMatricula(
      cacheCompletoConsultaSiape.matricula_simulada
    );
  }
  function atualizarCamposValoresMatricula(matriculaId) {
    const cliente = cacheCompletoConsultaSiape.cliente || {};
    // Captura os elementos reais da Ficha
    const elNome = document.getElementById('prop-nome-cliente');
    const elCpf = document.getElementById('prop-cpf-cliente');
    const elIdade = document.getElementById('prop-idade-cliente');
    const elOrgao = document.getElementById('prop-orgao-cliente');
    // 👇 INJEÇÃO DE DADOS E BALÃO CUSTOMIZADO (DATA-TOOLTIP NO PAI) 👇
    if (elNome) {
      const nomeVal = cliente.nome || '---';
      elNome.textContent = nomeVal;
      // Injeta no PAI (.linha-info-proposta) para o balão não ser cortado
      if (nomeVal !== '---')
        elNome.parentElement.setAttribute('data-tooltip', nomeVal);
      else elNome.parentElement.removeAttribute('data-tooltip');
    }
    if (elCpf) {
      const cpfVal = cliente.cpf || '---';
      elCpf.textContent = cpfVal;
      if (cpfVal !== '---')
        elCpf.parentElement.setAttribute('data-tooltip', cpfVal);
      else elCpf.parentElement.removeAttribute('data-tooltip');
    }
    if (elIdade) {
      const idadeVal = cliente.idade ? `${cliente.idade} anos` : '---';
      elIdade.textContent = idadeVal;
      if (idadeVal !== '---')
        elIdade.parentElement.setAttribute('data-tooltip', idadeVal);
      else elIdade.parentElement.removeAttribute('data-tooltip');
    }
    if (elOrgao) {
      const orgaoVal = cliente.orgaodesc || '---';
      elOrgao.textContent = orgaoVal;
      if (orgaoVal !== '---')
        elOrgao.parentElement.setAttribute('data-tooltip', orgaoVal);
      else elOrgao.parentElement.removeAttribute('data-tooltip');
    }
    const listaContratosContainer = document.getElementById(
      'lista-contratos-proposta'
    );
    listaContratosContainer.innerHTML = '';
    const contratos = cacheCompletoConsultaSiape.contratos || [];
    if (contratos.length === 0) {
      listaContratosContainer.innerHTML =
        '<div style="text-align:center; padding:10px; color:#999; font-size:11px;">Nenhum contrato encontrado.</div>';
      return;
    }
    // Desenha cada card de contrato dinamicamente
    contratos.forEach((contrato, index) => {
      const card = document.createElement('div');
      card.className = 'card-contrato-proposta selecionado';
      card.dataset.index = index;
      const formatar = (v) =>
        parseFloat(v || 0).toLocaleString('pt-BR', {
          style: 'currency',
          currency: 'BRL',
        });
      card.innerHTML = `
        <i class="fas fa-check-circle check-icon"></i>
        <div style="font-weight: 600; font-size: 12px; margin-bottom: 5px; color: var(--texto-principal)">
          ${contrato.banco_emprestimo} <span style="font-weight: 400; color: #888;">| ${contrato.prazo}x</span>
        </div>
        <div class="detalhe-contrato"><span>Parcela:</span> <strong>${formatar(contrato.parcela)}</strong></div>
        <div class="detalhe-contrato"><span>Saldo Devedor:</span> <strong>${formatar(contrato.saldo_devedor)}</strong></div>
        <div class="detalhe-contrato"><span>Taxa:</span> <strong>${contrato.taxa_aplicada}%</strong></div>
        <div class="detalhe-contrato" style="margin-top: 4px; padding-top: 4px; border-top: 1px dashed var(--cor-borda);">
          <span>Liberado:</span> <strong style="color: ${contrato.liberado < 0 ? '#ef4444' : '#00cc88'};">${formatar(contrato.liberado)}</strong>
        </div>
      `;
      card.addEventListener('click', () => {
        card.classList.toggle('selecionado');
      });
      listaContratosContainer.appendChild(card);
    });
  }
  // BOTÃO: COPIAR IMAGEM PARA O CTRL+V
  const btnCopiarPropostaImagem = document.getElementById(
    'btn-copiar-proposta-imagem'
  );
  if (btnCopiarPropostaImagem) {
    btnCopiarPropostaImagem.addEventListener('click', async () => {
      if (!cacheCompletoConsultaSiape) return;
      // 1. Coleta apenas os contratos que a vendedora selecionou (ficou verdinho)
      const cardsSelecionados = Array.from(
        document.querySelectorAll('.card-contrato-proposta.selecionado')
      );
      if (cardsSelecionados.length === 0)
        return mostrarAlerta('Selecione pelo menos um contrato!', 'warning');
      const indicesSelecionados = cardsSelecionados.map((card) =>
        parseInt(card.dataset.index)
      );
      // 2. Clona os dados originais e filtra só o que ela quer mandar
      const dadosFiltrados = JSON.parse(
        JSON.stringify(cacheCompletoConsultaSiape)
      );
      dadosFiltrados.contratos = dadosFiltrados.contratos.filter((_, i) =>
        indicesSelecionados.includes(i)
      );
      // 3. Recalcula os totais com base apenas no que foi selecionado
      let totalLiberado = 0;
      dadosFiltrados.contratos.forEach((c) => {
        totalLiberado += parseFloat(c.liberado || 0);
      });
      let totalReembolso = 0;
      // Puxa o reembolso original da API
      if (cacheCompletoConsultaSiape && cacheCompletoConsultaSiape.totais) {
        totalReembolso = parseFloat(
          cacheCompletoConsultaSiape.totais.total_reembolso || 0
        );
      }
      // 🚨 MATEMÁTICA FORÇADA (Igual ao texto): Ignora a API e soma na marra!
      let totalCliente = totalLiberado + totalReembolso;
      // Injeta no objeto da imagem para o Puppeteer desenhar
      dadosFiltrados.totais = {
        total_liberado: totalLiberado,
        total_reembolso: totalReembolso,
        total_liberado_cliente: totalCliente,
      };
      const oldInner = btnCopiarPropostaImagem.innerHTML;
      btnCopiarPropostaImagem.innerHTML =
        '<i class="fas fa-spinner fa-spin"></i> Gerando e Copiando...';
      btnCopiarPropostaImagem.disabled = true;
      try {
        // Pede pro Puppeteer gerar o HTML só com os dados selecionados
        const res = await fetch('/api/consulta/gerar-imagem-proposta', {
          method: 'POST',
          headers: {
            'Content-Type': 'application/json',
            Authorization: `Bearer ${localStorage.getItem('token')}`,
          },
          body: JSON.stringify({ dadosProposta: dadosFiltrados }),
        });
        const data = await res.json();
        if (data.success && data.imageUrl) {
          // Busca a imagem gerada e transforma em Blob
          const resImg = await fetch(data.imageUrl);
          const blob = await resImg.blob();
          // A MÁGICA: Joga no Clipboard (Área de Transferência)
          const clipboardItem = new ClipboardItem({ 'image/png': blob });
          await navigator.clipboard.write([clipboardItem]);
          mostrarAlerta('Imagem Copiada! Pressione Ctrl+V no chat', 'success');
          // Fecha o modal para ela já colar
          modalConsulta.classList.add('hidden');
          btnAbrirConsulta.classList.remove('ativo');
          inputMsg.focus(); // Foca na caixa de texto pra ela colar direto
        } else {
          mostrarAlerta('Erro ao gerar proposta visual.', 'error');
        }
      } catch (e) {
        mostrarAlerta('Erro de permissão ou conexão ao copiar.', 'error');
      } finally {
        btnCopiarPropostaImagem.disabled = false;
        btnCopiarPropostaImagem.innerHTML = oldInner;
      }
    });
  }
  // ==========================================================
  // 📄 COPIAR MÚLTIPLAS PROPOSTAS EM TEXTO FORMATADO (V3)
  // ==========================================================
  document
    .getElementById('btn-copiar-proposta-texto')
    .addEventListener('click', async () => {
      // 1. Captura TODOS os cards que as meninas deixaram verde (.selecionado)
      const cardsSelecionados = Array.from(
        document.querySelectorAll('.card-contrato-proposta.selecionado')
      );
      if (cardsSelecionados.length === 0) {
        mostrarAlerta('Selecione pelo menos uma oportunidade!', 'warning');
        return;
      }
      // 2. Coleta o primeiro nome do cliente de forma inteligente
      const nomeCompleto =
        document.getElementById('prop-nome-cliente')?.innerText || 'Cliente';
      const primeiroNome = nomeCompleto.trim().split(' ')[0];
      // 3. Estrutura o cabeçalho do seu modelo perfeito
      let textoProposta = `*${primeiroNome}*\n\n`;
      textoProposta += `Realizei sua consulta e no momento esses são os detalhes da sua proposta!\n`;
      // Linha única inicial abrindo a listagem de propostas
      textoProposta += `_____________________________\n`;
      // Variáveis para somar os totais dinamicamente
      let somaTotalLiberado = 0;
      // 4. LOOP CORRIGIDO: Varre cada contrato e monta sem duplicar as linhas separadoras
      cardsSelecionados.forEach((card) => {
        // Captura o nome do banco que está na div de topo do card
        const headerTexto = card.querySelector(
          'div[style*="font-weight: 600"]'
        ).innerText;
        const bancoNome = headerTexto.split('|')[0].trim();
        // Função interna isolada para ler as linhas de dentro DESTE card específico
        const obterValorLinha = (rotulo) => {
          const linhas = Array.from(card.querySelectorAll('.detalhe-contrato'));
          const linhaAlvo = linhas.find((el) => el.innerText.includes(rotulo));
          return linhaAlvo
            ? linhaAlvo.querySelector('strong').innerText.trim()
            : '---';
        };
        const parcela = obterValorLinha('Parcela:');
        const saldo = obterValorLinha('Saldo Devedor:');
        const taxa = obterValorLinha('Taxa:');
        const liberado = obterValorLinha('Liberado:');
        const prazo = headerTexto.includes('|')
          ? headerTexto.split('|')[1].replace('x', '').trim()
          : '---';
        // Conversão matemática limpa para a soma dos totais no rodapé
        const limparMoeda = (txt) =>
          parseFloat(
            txt
              .replace(/[^\d,-]/g, '')
              .replace('.', '')
              .replace(',', '.')
          ) || 0;
        somaTotalLiberado += limparMoeda(liberado);
        // Injeta o bloco visual compacto exatamente como você pediu (Sem a linha repetida no topo)
        textoProposta += ` \`Banco: ${bancoNome}\`\n`;
        textoProposta += `⤷ \n`;
        textoProposta += ` • \`\`\`Parcela: ${parcela}\`\`\`\n`;
        textoProposta += ` • \`\`\`Prazo: ${prazo}\`\`\`\n`;
        textoProposta += ` • \`\`\`Taxa: ${taxa}\`\`\`\n`;
        textoProposta += ` • \`\`\`Saldo: ${saldo}\`\`\`\n`;
        textoProposta += ` • \`\`\`Liberado: ${liberado}\`\`\`\n`;
        textoProposta += `_____________________________\n`;
      });
      // 5. Busca os valores da API e aplica a Mágica Inteligente do filtro
      let totalReembolso = 0;
      let totalCliente = 0;
      if (cacheCompletoConsultaSiape && cacheCompletoConsultaSiape.totais) {
        totalReembolso = parseFloat(
          cacheCompletoConsultaSiape.totais.total_reembolso || 0
        );
        // Se marcou todos os contratos, usa os totais oficiais da Bitech
        if (
          cardsSelecionados.length ===
          document.querySelectorAll('.card-contrato-proposta').length
        ) {
          somaTotalLiberado = parseFloat(
            cacheCompletoConsultaSiape.totais.total_liberado ||
              somaTotalLiberado
          );
          totalCliente = parseFloat(
            cacheCompletoConsultaSiape.totais.total_liberado_cliente || 0
          );
        } else {
          // Se filtrou contratos, soma o Liberado escolhido + Reembolso
          totalCliente = somaTotalLiberado + totalReembolso;
        }
      }
      const formatarMoeda = (valor) =>
        valor.toLocaleString('pt-BR', { style: 'currency', currency: 'BRL' });
      // Rodapé com os valores consolidados e idênticos aos da Imagem
      textoProposta += `\n\n`;
      textoProposta += `*Liberado:* ${formatarMoeda(somaTotalLiberado)}\n`;
      textoProposta += `*Reembolso:* ${formatarMoeda(totalReembolso)}\n`;
      textoProposta += `*Total Cliente:* ${formatarMoeda(totalCliente)}`;
      try {
        await navigator.clipboard.writeText(textoProposta);
        mostrarAlerta('Propostas em texto copiadas!', 'success');
      } catch (err) {
        console.error('Erro ao copiar multi-proposta: ', err);
        Swal.fire({
          icon: 'error',
          title: 'Erro ao copiar',
          text: 'Área de transferência travada pelo navegador.',
        });
      }
    }); // ✅ SELEÇÃO EM MASSA (MODO FANTASMA / CLIQUE LONGO)
  // ============================================================
  // ============================================================
  window.ativarModoSelecao = function (chatIdInicial) {
    modoSelecaoMassa = true;
    chatsSelecionadosMassa.clear(); // Adiciona uma classe no body para o nosso CSS agir depois (sumir filtros e mostrar checkboxes)
    document.body.classList.add('modo-selecao-massa-ativo'); // Mostra a barra flutuante (Criaremos no HTML no próximo passo)
    const barraAcoes = document.getElementById('barra-acoes-massa');
    if (barraAcoes) barraAcoes.classList.remove('hidden'); // Já seleciona automaticamente o chat que a vendedora segurou o dedo!
    const divInicial = document.querySelector(
      `.chat-item[data-chat-id="${chatIdInicial}"]`
    );
    if (divInicial) {
      window.toggleSelecaoChat(chatIdInicial, divInicial);
    }
  };
  window.toggleSelecaoChat = function (chatId, elementoHTML) {
    if (chatsSelecionadosMassa.has(chatId)) {
      chatsSelecionadosMassa.delete(chatId);
      elementoHTML.classList.remove('chat-selecionado-massa');
    } else {
      chatsSelecionadosMassa.add(chatId);
      elementoHTML.classList.add('chat-selecionado-massa');
    }
    atualizarContadorSelecaoMassa();
  };
  window.selecionarTodosDaLista = function () {
    // Seleciona apenas os que estão visíveis e renderizados na lista no momento! (Respeita o filtro)
    const visiveis = document.querySelectorAll('.chat-item');
    visiveis.forEach((el) => {
      const id = el.dataset.chatId;
      if (id && !chatsSelecionadosMassa.has(id)) {
        chatsSelecionadosMassa.add(id);
        el.classList.add('chat-selecionado-massa');
      }
    });
    atualizarContadorSelecaoMassa();
  };
  function atualizarContadorSelecaoMassa() {
    const textoContador = document.getElementById('contador-selecao-massa');
    if (textoContador) {
      textoContador.innerText = `${chatsSelecionadosMassa.size} selecionados`;
    }
    // Se o painel do rodízio estiver aberto, o "20 ÷ 3" tem que acompanhar
    atualizarHintTransferirMassa();
  }
  window.desativarModoSelecao = function () {
    modoSelecaoMassa = false;
    chatsSelecionadosMassa.clear();
    document.body.classList.remove('modo-selecao-massa-ativo');
    const barraAcoes = document.getElementById('barra-acoes-massa');
    if (barraAcoes) barraAcoes.classList.add('hidden'); // Tira a classe verde/azul das caixinhas
    document.querySelectorAll('.chat-selecionado-massa').forEach((el) => {
      el.classList.remove('chat-selecionado-massa');
    });
    fecharPainelTransferirMassa();
  };
  window.confirmarLeituraEmMassa = async function () {
    if (chatsSelecionadosMassa.size === 0) {
      return mostrarAlerta('Selecione pelo menos uma conversa.', 'warning');
    }
    const btnConfirmar = document.getElementById('btn-confirmar-lido-massa');
    const oldIcon = btnConfirmar.innerHTML;
    btnConfirmar.innerHTML =
      '<i class="fas fa-spinner fa-spin"></i> Limpando...';
    btnConfirmar.disabled = true;
    const chatIdsArray = Array.from(chatsSelecionadosMassa);
    try {
      const res = await fetch('/api/chats/read-batch', {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          Authorization: `Bearer ${localStorage.getItem('token')}`,
        },
        body: JSON.stringify({ chatIds: chatIdsArray }),
      });
      const data = await res.json();
      if (data.success) {
        // 1. Mágica do Front: Limpa as bolinhas da tela na hora, SEM DAR F5, MANTENDO O FILTRO
        chatIdsArray.forEach((id) => {
          // Atualiza a Memória
          const chatMem = todosChats.find((c) => c.chat_id === id);
          if (chatMem) {
            chatMem.unread_count = 0;
            if (chatMem.canais) chatMem.canais.forEach((c) => (c.unread = 0));
          } // Tira a bolinha do html na marra
          const itemLista = document.querySelector(
            `.chat-item[data-chat-id="${id}"]`
          );
          if (itemLista) {
            const badge = itemLista.querySelector('.badge-unread');
            if (badge) badge.remove();
          }
        }); // 2. Se a vendedora tiver esse chat aberto na tela direita, remove o botão "Marcar Lido" dali também
        if (
          chatSelecionado &&
          chatsSelecionadosMassa.has(chatSelecionado.chat_id)
        ) {
          const btnMarcarLido = document.getElementById('btn-marcar-lido');
          if (btnMarcarLido) btnMarcarLido.style.display = 'none';
          chatSelecionado.unread_count = 0;
        }
        mostrarAlerta(`${chatIdsArray.length} contatos limpos!`, 'success');
        window.desativarModoSelecao(); // Força atualizar o contador de não lidos vermelho gigante lá no topo da lista
        if (typeof carregarFiltroBotsComStatus === 'function')
          carregarFiltroBotsComStatus(); // Chama indiretamente o contador (já tá dentro da sua lógica)
        renderizarLista(todosChats, false); // Roda isso só para forçar o contador do topo a atualizar (passa false pra não dar reset)
      } else {
        mostrarAlerta('Erro ao limpar contatos.', 'error');
      }
    } catch (e) {
      mostrarAlerta('Erro de conexão com o banco de dados.', 'error');
    } finally {
      btnConfirmar.innerHTML = oldIcon;
      btnConfirmar.disabled = false;
    }
  };

  // ============================================================
  // 🔀 TRANSFERÊNCIA EM MASSA (RODÍZIO) — só admin
  // A pessoa seleciona N conversas (clique longo), aperta "Transferir
  // Atendimento", marca 1+ atendentes no painel e confirma. O backend
  // distribui em rodízio: conversa i vai pro atendente marcado[i % qtd].
  // 20 conversas / 3 atendentes = 7, 7, 6.
  // ============================================================
  if (Acesso.pode('chat.transferir_massa')) {
    document.body.classList.add('pode-transferir-massa');
    const linhaTransfMassa = document.getElementById('acoes-massa-transferir');
    if (linhaTransfMassa) linhaTransfMassa.hidden = false;
  }

  function fecharPainelTransferirMassa() {
    const painel = document.getElementById('painel-transferir-massa');
    if (painel) painel.classList.add('hidden');
    const btn = document.getElementById('btn-transferir-massa');
    if (btn) btn.classList.remove('ativo');
    atendentesMarcadosMassa.length = 0;
  }

  async function carregarEquipeTransferenciaMassa() {
    if (equipeTransferenciaMassa) return equipeTransferenciaMassa;
    try {
      const res = await fetch('/api/team/vendedores', {
        headers: { Authorization: `Bearer ${localStorage.getItem('token')}` },
      });
      const lista = await res.json();
      equipeTransferenciaMassa = Array.isArray(lista) ? lista : [];
    } catch (e) {
      equipeTransferenciaMassa = [];
    }
    return equipeTransferenciaMassa;
  }

  window.togglePainelTransferirMassa = async function () {
    const painel = document.getElementById('painel-transferir-massa');
    const btn = document.getElementById('btn-transferir-massa');
    if (!painel) return;
    if (!painel.classList.contains('hidden')) {
      fecharPainelTransferirMassa();
      return;
    }
    if (chatsSelecionadosMassa.size === 0) {
      return mostrarAlerta('Selecione as conversas primeiro.', 'warning');
    }
    if (btn) btn.classList.add('ativo');
    await carregarEquipeTransferenciaMassa();
    renderizarPainelTransferirMassa();
    painel.classList.remove('hidden');
  };

  function renderizarPainelTransferirMassa() {
    const lista = document.getElementById('ptm-lista');
    if (!lista) return;
    lista.innerHTML = '';
    if (!equipeTransferenciaMassa || !equipeTransferenciaMassa.length) {
      lista.innerHTML =
        '<div class="ptm-vazio">Nenhum atendente encontrado.</div>';
      atualizarHintTransferirMassa();
      return;
    }
    equipeTransferenciaMassa.forEach((u) => {
      const id = String(u.id);
      const ordem = atendentesMarcadosMassa.indexOf(id);
      const marcado = ordem !== -1;
      const item = document.createElement('div');
      item.className = 'ptm-item' + (marcado ? ' marcado' : '');
      item.dataset.userId = id;
      // A caixinha e o selo de ordem nascem sempre, mesmo desmarcados: e o
      // que permite atualizar o painel no lugar depois, sem remontar.
      item.innerHTML = `
        <span class="ptm-check"><i class="ph-bold ph-check"></i></span>
        <span class="ptm-nome">${escaparHtmlChat(u.name || u.username || 'Sem nome')}</span>
        <span class="ptm-ordem"${marcado ? '' : ' hidden'}>${marcado ? `${ordem + 1}º` : ''}</span>
      `;
      item.addEventListener('click', () => {
        const i = atendentesMarcadosMassa.indexOf(id);
        if (i === -1) atendentesMarcadosMassa.push(id);
        else atendentesMarcadosMassa.splice(i, 1);
        sincronizarPainelTransferirMassa();
      });
      lista.appendChild(item);
    });
    atualizarHintTransferirMassa();
  }

  // Reflete a selecao no painel que JA esta na tela. O clique passa por aqui
  // em vez de remontar a lista: remontar trocava cada linha por um elemento
  // novo, e elemento novo ja nasce no estado final -- a transicao do CSS nao
  // tinha de onde sair, e a troca aparecia como uma piscada. Continua sendo
  // preciso varrer a lista toda (nao so a linha clicada) porque desmarcar o
  // 1o promove o 2o, entao os selos de ordem mudam em cascata.
  function sincronizarPainelTransferirMassa() {
    const lista = document.getElementById('ptm-lista');
    if (!lista) return;
    lista.querySelectorAll('.ptm-item').forEach((item) => {
      const ordem = atendentesMarcadosMassa.indexOf(item.dataset.userId);
      const marcado = ordem !== -1;
      item.classList.toggle('marcado', marcado);
      const selo = item.querySelector('.ptm-ordem');
      if (selo) {
        selo.textContent = marcado ? `${ordem + 1}º` : '';
        selo.hidden = !marcado;
      }
    });
    atualizarHintTransferirMassa();
  }

  // Atualiza só o botão de confirmar ("Transferir N conversas") — o painel
  // não mostra mais a conta de "20 ÷ 3 ≈ 7 cada", ficou poluído.
  function atualizarHintTransferirMassa() {
    const btnConf = document.getElementById('btn-confirmar-transferir-massa');
    if (!btnConf) return;
    const n = chatsSelecionadosMassa.size;
    const k = atendentesMarcadosMassa.length;
    const span = btnConf.querySelector('span');
    if (span)
      span.textContent =
        k > 0 ? `Transferir ${n} conversa${n > 1 ? 's' : ''}` : 'Transferir';
    btnConf.disabled = k === 0;
  }

  window.confirmarTransferenciaEmMassa = async function () {
    const chatIds = Array.from(chatsSelecionadosMassa);
    const responsavelIds = [...atendentesMarcadosMassa];
    if (chatIds.length === 0)
      return mostrarAlerta('Selecione as conversas.', 'warning');
    if (responsavelIds.length === 0)
      return mostrarAlerta('Marque pelo menos um atendente.', 'warning');

    const nomes = responsavelIds
      .map((id) => {
        const u = (equipeTransferenciaMassa || []).find(
          (x) => String(x.id) === id
        );
        return u ? u.name || u.username : id;
      })
      .join(', ');

    const conf = await Swal.fire({
      icon: 'question',
      title: 'Confirmar rodízio?',
      html: `<b>${chatIds.length}</b> conversa(s) serão distribuídas em rodízio entre:<br><b>${escaparHtmlChat(nomes)}</b>`,
      showCancelButton: true,
      confirmButtonText: 'Transferir',
      cancelButtonText: 'Cancelar',
      confirmButtonColor: '#0284c7', // --azul-info do sistema
    });
    if (!conf.isConfirmed) return;

    const btnConf = document.getElementById('btn-confirmar-transferir-massa');
    const oldHtml = btnConf ? btnConf.innerHTML : '';
    if (btnConf) {
      btnConf.disabled = true;
      btnConf.innerHTML =
        '<i class="fas fa-spinner fa-spin"></i> Transferindo...';
    }
    try {
      const res = await fetch('/api/chat/transfer-batch', {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          Authorization: `Bearer ${localStorage.getItem('token')}`,
        },
        body: JSON.stringify({ chatIds, responsavelIds }),
      });
      const data = await res.json();
      if (data.success) {
        const ok =
          typeof data.transferidos === 'number'
            ? data.transferidos
            : chatIds.length;
        mostrarAlerta(
          `${ok} conversa(s) transferida(s) em rodízio!`,
          'success'
        );
        window.desativarModoSelecao();
        if (!carregandoChats) carregarChats(1, true);
      } else {
        mostrarAlerta(
          data.message || 'Falha ao transferir em massa.',
          'error'
        );
      }
    } catch (e) {
      mostrarAlerta('Erro de rede na transferência em massa.', 'error');
    } finally {
      if (btnConf) {
        btnConf.disabled = false;
        btnConf.innerHTML = oldHtml;
      }
    }
  };

  // ============================================================
  // ⬇️ RADAR: BOTÃO FLUTUANTE DE MENSAGENS NOVAS
  // ============================================================
  const btnRadar = document.createElement('button');
  btnRadar.id = 'btn-radar-scroll';
  // O botão com a setinha e a bolinha do contador embutida
  btnRadar.innerHTML = `
    <i class="bx bx-chevron-down" style="font-size: 28px; color: #64748b;"></i>
    <span class="radar-badge hidden" style="position: absolute; top: -5px; right: -5px; background-color: #25D366; color: white; font-size: 11px; font-weight: bold; width: 18px; height: 18px; border-radius: 50%; display: flex; align-items: center; justify-content: center; box-shadow: 0 2px 4px rgba(0,0,0,0.2);">0</span>
  `;
  // Visual Premium Flutuante
  Object.assign(btnRadar.style, {
    position: 'absolute',
    bottom: '80px', // Fica um pouco acima do input de digitar
    right: '20px',
    width: '42px',
    height: '42px',
    borderRadius: '50%',
    backgroundColor: 'var(--fundo-cartao, #ffffff)',
    boxShadow: '0 3px 6px rgba(0,0,0,0.15)',
    border: '1px solid var(--cor-borda, #e2e8f0)',
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    cursor: 'pointer',
    zIndex: '100',
    opacity: '0',
    visibility: 'hidden',
    transition: 'opacity 0.3s ease, visibility 0.3s ease',
  });
  // Amarra ele de forma segura no container principal do chat
  if (areaAtiva) {
    areaAtiva.style.position = 'relative';
    areaAtiva.appendChild(btnRadar);
  }
  // Lógica: Aparecer/Esconder quando a vendedora roda a bolinha do mouse
  if (containerMsgs) {
    containerMsgs.addEventListener('scroll', () => {
      const distanceFromBottom =
        containerMsgs.scrollHeight -
        containerMsgs.scrollTop -
        containerMsgs.clientHeight;
      const badge = btnRadar.querySelector('.radar-badge');
      if (distanceFromBottom > 150) {
        // Se subiu, mostra o botão
        btnRadar.style.visibility = 'visible';
        btnRadar.style.opacity = '1';
      } else {
        // Se ela mesma rolou de volta pro fundo, esconde o radar e zera tudo
        btnRadar.style.visibility = 'hidden';
        btnRadar.style.opacity = '0';
        if (badge) {
          badge.classList.add('hidden');
          badge.innerText = '0';
        }
      }
    });
  }
  // O que acontece quando ela clica no radar? A tela desce sozinha!
  btnRadar.addEventListener('click', () => {
    containerMsgs.scrollTo({
      top: containerMsgs.scrollHeight,
      behavior: 'smooth',
    });
  });
  // ==========================================================================
  // 🔍 MOTOR DE BUSCA INTERNA DE MENSAGENS (ESTILO WHATSAPP WEB)
  // ==========================================================================
  const inputBuscaInterna = document.getElementById(
    'input-busca-mensagem-interna'
  );
  const controlesBuscaNav = document.getElementById('controles-busca-nav');
  const contadorBusca = document.getElementById('contador-resultados-busca');
  const btnBuscaSubir = document.getElementById('btn-busca-subir');
  const btnBuscaDescer = document.getElementById('btn-busca-descer');
  const btnLimparBusca = document.getElementById('btn-limpar-busca-interna');
  let elementosEncontradosBusca = [];
  let resultadoAtualIndex = -1;
  if (inputBuscaInterna) {
    inputBuscaInterna.addEventListener('input', (e) => {
      const termo = e.target.value.toLowerCase().trim();
      limparRealcesEFontesDeBusca();
      if (!termo || termo.length < 2) {
        if (controlesBuscaNav) controlesBuscaNav.classList.add('hidden');
        elementosEncontradosBusca = [];
        resultadoAtualIndex = -1;
        return;
      }
      elementosEncontradosBusca = [];
      const bolhasDeTexto = document.querySelectorAll(
        '#container-mensagens .msg-bubble:not(.revoked)'
      );
      bolhasDeTexto.forEach((bolha) => {
        const spanTexto = bolha.querySelector('.texto-formatado');
        if (spanTexto) {
          const textoOriginalHTML = spanTexto.innerHTML;
          const textoPuroLower = spanTexto.textContent.toLowerCase();
          if (textoPuroLower.includes(termo)) {
            elementosEncontradosBusca.push(bolha);
            const regexEscapada = termo.replace(
              /[-\/\\^$*+?.()|[\]{}]/g,
              '\\$&'
            );
            const regexMatcher = new RegExp(`(${regexEscapada})`, 'gi');
            spanTexto.innerHTML = textoOriginalHTML.replace(
              regexMatcher,
              '<mark class="realce-busca" style="background-color: #fef08a; color: #000; padding: 0 2px; border-radius: 2px;">$1</mark>'
            );
          }
        }
      });
      if (elementosEncontradosBusca.length > 0) {
        resultadoAtualIndex = elementosEncontradosBusca.length - 1;
        if (controlesBuscaNav) controlesBuscaNav.classList.remove('hidden');
        moverFocoParaMensagemEncontrada();
      } else {
        if (controlesBuscaNav) controlesBuscaNav.classList.remove('hidden');
        if (contadorBusca) contadorBusca.textContent = '0/0';
        resultadoAtualIndex = -1;
      }
    });
    if (btnLimparBusca) {
      btnLimparBusca.addEventListener('click', (e) => {
        e.stopPropagation();
        inputBuscaInterna.value = '';
        inputBuscaInterna.dispatchEvent(new Event('input'));
        inputBuscaInterna.focus();
      });
    }
    if (btnBuscaSubir) {
      btnBuscaSubir.addEventListener('click', (e) => {
        e.stopPropagation();
        if (elementosEncontradosBusca.length === 0) return;
        resultadoAtualIndex--;
        if (resultadoAtualIndex < 0)
          resultadoAtualIndex = elementosEncontradosBusca.length - 1;
        moverFocoParaMensagemEncontrada();
      });
    }
    if (btnBuscaDescer) {
      btnBuscaDescer.addEventListener('click', (e) => {
        e.stopPropagation();
        if (elementosEncontradosBusca.length === 0) return;
        resultadoAtualIndex =
          (resultadoAtualIndex + 1) % elementosEncontradosBusca.length;
        moverFocoParaMensagemEncontrada();
      });
    }
  }
  function moverFocoParaMensagemEncontrada() {
    if (resultadoAtualIndex === -1 || elementosEncontradosBusca.length === 0)
      return;
    document
      .querySelectorAll('.msg-bubble.destaque-resultado-ativo')
      .forEach((b) => {
        b.classList.remove('destaque-resultado-ativo');
        b.style.boxShadow = '';
      });
    if (contadorBusca) {
      contadorBusca.textContent = `${resultadoAtualIndex + 1}/${elementosEncontradosBusca.length}`;
    }
    const bolhaAlvo = elementosEncontradosBusca[resultadoAtualIndex];
    if (bolhaAlvo) {
      bolhaAlvo.scrollIntoView({ behavior: 'smooth', block: 'center' });
      // Injeta a classe de controle
      bolhaAlvo.classList.add('destaque-resultado-ativo');
      const isDark = document.body.classList.contains('modo-escuro');
      const corDestaque = isDark
        ? '0 0 0 3px rgba(0, 186, 128, 0.6)'
        : '0 0 0 3px rgba(0, 102, 68, 0.6)';
      bolhaAlvo.style.boxShadow = corDestaque;
    }
  }
  function limparRealcesEFontesDeBusca() {
    document.querySelectorAll('.msg-bubble .realce-busca').forEach((mark) => {
      mark.replaceWith(document.createTextNode(mark.textContent));
    });
    document
      .querySelectorAll('.msg-bubble.destaque-resultado-ativo')
      .forEach((b) => {
        b.classList.remove('destaque-resultado-ativo');
        b.style.boxShadow = '';
      });
  }
  // fim do DOMContentLoaded
});
// ==========================================================================
// MÁGICA DO CARROSSEL INFINITO (CIRCULAR DRAG)
// ==========================================================================
let isDraggingTags = false;
let startXTags;
let scrollLeftTags;
let activeTagSlider = null;
document.addEventListener('mousedown', (e) => {
  const slider = e.target.closest('.linha-tags-texto.infinite-loop');
  if (!slider) return;
  isDraggingTags = false;
  activeTagSlider = slider;
  startXTags = e.pageX - activeTagSlider.offsetLeft;
  scrollLeftTags = activeTagSlider.scrollLeft;
  activeTagSlider.classList.add('active');
});
document.addEventListener('mousemove', (e) => {
  if (!activeTagSlider) return;
  const x = e.pageX - activeTagSlider.offsetLeft;
  const walk = x - startXTags;
  if (Math.abs(walk) > 3) {
    isDraggingTags = true;
    e.preventDefault();
    activeTagSlider.scrollLeft = scrollLeftTags - walk;
    // --- LÓGICA DO LOOP INFINITO ---
    // Se chegar perto do fim (direita), joga pro meio
    if (activeTagSlider.scrollLeft >= activeTagSlider.scrollWidth / 1.5) {
      activeTagSlider.scrollLeft = activeTagSlider.scrollWidth / 3;
      startXTags = e.pageX - activeTagSlider.offsetLeft; // Recalibra o ponto de arrasto
      scrollLeftTags = activeTagSlider.scrollLeft;
    }
    // Se chegar perto do início (esquerda), joga pro meio
    if (activeTagSlider.scrollLeft <= 0) {
      activeTagSlider.scrollLeft = activeTagSlider.scrollWidth / 3;
      startXTags = e.pageX - activeTagSlider.offsetLeft;
      scrollLeftTags = activeTagSlider.scrollLeft;
    }
  }
});
document.addEventListener('mouseup', () => {
  if (activeTagSlider) activeTagSlider.classList.remove('active');
  activeTagSlider = null;
});
window.toggleTranscription = async function (btn, event) {
  event.stopPropagation();
  // Busca a caixa de texto que está logo ACIMA do botão
  const container = btn.closest('.transcription-container-outside');
  const textBox = container.querySelector('.transcription-text-box');
  const span = btn.querySelector('span');
  const isHidden = textBox.classList.contains('hidden');

  // Já tem transcrição pronta (recebida automática, ou já buscada antes): só abre/fecha.
  if (container.dataset.hasTranscription === '1') {
    if (isHidden) {
      textBox.classList.remove('hidden');
      span.innerText = 'Fechar transcrição';
      btn.classList.add('ativo');
    } else {
      textBox.classList.add('hidden');
      span.innerText = 'Ver transcrição';
      btn.classList.remove('ativo');
    }
    return;
  }

  // Ainda não tem transcrição e já está aberto (mostrando o "processando" de
  // uma recebida, por exemplo): só fecha, sem disparar outra busca.
  if (!isHidden) {
    textBox.classList.add('hidden');
    span.innerText = 'Transcrever áudio';
    btn.classList.remove('ativo');
    return;
  }

  // 🎙️ Sob demanda: áudio ENVIADO por nós (ou um recebido que falhou antes)
  // sem transcrição ainda. Busca agora, na hora do clique.
  const wppId = container.dataset.wppId;
  if (!wppId) {
    if (typeof mostrarAlerta === 'function') {
      mostrarAlerta(
        'Não foi possível localizar esse áudio para transcrever.',
        'error'
      );
    }
    return;
  }

  textBox.classList.remove('hidden');
  textBox.innerHTML = '<em>Transcrevendo...</em>';
  span.innerText = 'Transcrevendo...';
  btn.classList.add('ativo');
  btn.disabled = true;

  try {
    const res = await fetch('/api/chat/transcrever-audio', {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        Authorization: `Bearer ${localStorage.getItem('token')}`,
      },
      body: JSON.stringify({ wpp_id: wppId }),
    });
    const data = await res.json();
    if (!data.success) throw new Error(data.message || 'Falha ao transcrever.');

    textBox.textContent = data.transcription;
    container.dataset.hasTranscription = '1';
    span.innerText = 'Fechar transcrição';
  } catch (err) {
    // Volta pro estado inicial (fechado) pra um próximo clique já tentar de novo direto.
    textBox.innerHTML = '';
    textBox.classList.add('hidden');
    span.innerText = 'Transcrever áudio';
    btn.classList.remove('ativo');
    if (typeof mostrarAlerta === 'function') {
      mostrarAlerta('Erro ao transcrever o áudio. Tente novamente.', 'error');
    }
  } finally {
    btn.disabled = false;
  }
};
// ============================================================
//  LÓGICA DA BARRA HORIZONTAL (DESIGN CLEAN & FOCUS)
// ============================================================
let listaArquivosParaEnviar = [];
// As miniaturas usam URL.createObjectURL. Sem revogar, cada re-render (que
// acontece toda vez que edita/remove um anexo) deixa um blob preso na memória
// da aba até dar F5.
let urlsPreviewAnexos = [];
function liberarUrlsPreviewAnexos() {
  urlsPreviewAnexos.forEach((url) => URL.revokeObjectURL(url));
  urlsPreviewAnexos = [];
}
function renderizarPreviewBar() {
  const previewBar = document.getElementById('preview-bar-container');
  const thumbsList = document.getElementById('preview-thumbs-list');
  const overlay = document.getElementById('overlay-foco-chat');
  const inputArea = document.querySelector('.chat-input-area');
  const btnMic = document.getElementById('botao-gravar-audio');
  // CENÁRIO 1: NÃO TEM ARQUIVOS (Excluiu tudo no X ou acabou de enviar)
  if (listaArquivosParaEnviar.length === 0) {
    if (previewBar) previewBar.classList.add('hidden');
    if (thumbsList) thumbsList.innerHTML = '';
    liberarUrlsPreviewAnexos();
    // AQUI ESTÁ A CORREÇÃO: Removemos o comentário e devolvemos a lógica
    if (overlay) {
      overlay.classList.remove('ativo'); // Tira a opacidade suavemente
      setTimeout(() => overlay.classList.add('hidden'), 300); // Dá um display:none depois da animação
    }
    if (inputArea) inputArea.classList.remove('focado-preview');
    if (btnMic) btnMic.style.display = 'flex'; // Volta o microfone
    return;
  }
  // CENÁRIO 2: TEM ARQUIVOS (Ativa o Modo Foco e Esconde Mic)
  if (btnMic) btnMic.style.display = 'none';
  if (previewBar) previewBar.classList.remove('hidden');
  if (thumbsList) thumbsList.innerHTML = '';
  liberarUrlsPreviewAnexos();
  if (overlay) {
    overlay.classList.remove('hidden');
    // Pequeno delay para permitir a transição CSS funcionar
    setTimeout(() => overlay.classList.add('ativo'), 10);
  }
  if (inputArea) inputArea.classList.add('focado-preview');
  // Gera os Cards
  listaArquivosParaEnviar.forEach((arquivo, index) => {
    const thumbItem = document.createElement('div');
    thumbItem.className = 'thumb-item';
    // 1. Wrapper do Conteúdo (Imagem/Video) - Para ter borda arredondada
    const contentWrapper = document.createElement('div');
    contentWrapper.className = 'thumb-content-wrapper';
    if (arquivo.type.startsWith('image/')) {
      const img = document.createElement('img');
      const urlImagem = URL.createObjectURL(arquivo);
      urlsPreviewAnexos.push(urlImagem);
      img.src = urlImagem;
      contentWrapper.appendChild(img);
    } else if (arquivo.type.startsWith('video/')) {
      const video = document.createElement('video');
      const urlVideo = URL.createObjectURL(arquivo);
      urlsPreviewAnexos.push(urlVideo);
      video.src = urlVideo;
      video.controls = false; // Sem controles pra ficar bonito
      contentWrapper.appendChild(video);
    } else {
      contentWrapper.innerHTML = `
                <div class="thumb-generic-file">
                    <i class="fas fa-file-alt"></i>
                    <span>${arquivo.name.slice(0, 20)}...</span>
                </div>`;
    }
    // 2. Botão X Flutuante (Fica FORA do Wrapper, mas DENTRO do Item)
    const btnRemove = document.createElement('button');
    btnRemove.className = 'btn-remove-thumb';
    btnRemove.innerHTML = "<i class='fas fa-times'></i>";
    btnRemove.onclick = (e) => {
      e.stopPropagation();
      removerArquivoDoPreview(index);
    };
    // 3. Botão Lápis: abre o editor de imagem (GIF fica de fora, viraria
    // imagem parada ao passar pelo canvas)
    let btnEditar = null;
    let seloEditada = null;
    if (arquivo.type.startsWith('image/') && !/gif/i.test(arquivo.type)) {
      btnEditar = document.createElement('button');
      btnEditar.className = 'btn-editar-thumb';
      btnEditar.title = 'Editar imagem (girar, recortar, escrever, destacar)';
      btnEditar.innerHTML = "<i class='fas fa-pen'></i>";
      btnEditar.onclick = (e) => {
        e.stopPropagation();
        editarImagemDoPreview(index);
      };
      if (arquivo.editadaNoCrm) {
        seloEditada = document.createElement('div');
        seloEditada.className = 'thumb-editada-badge';
        seloEditada.innerHTML = "<i class='fas fa-pen'></i> Editada";
      }
    }
    // Monta o Card
    thumbItem.appendChild(contentWrapper); // Imagem
    thumbItem.appendChild(btnRemove); // Botão Flutuante
    if (btnEditar) thumbItem.appendChild(btnEditar);
    if (seloEditada) thumbItem.appendChild(seloEditada);
    thumbsList.appendChild(thumbItem);
  });
  // Foca no input para digitar legenda
  const inputMsg = document.getElementById('input-mensagem-texto');
  if (inputMsg) inputMsg.focus();
}
// Abre o editor de imagem para o anexo e, ao salvar, troca o arquivo da fila
// pela versão editada (o original é descartado - quem quiser o original de
// volta cancela o editor ou remove e anexa de novo).
window.editarImagemDoPreview = function (index) {
  const arquivo = listaArquivosParaEnviar[index];
  if (!arquivo) return;
  if (typeof window.abrirEditorImagem !== 'function') {
    mostrarAlerta(
      'O editor de imagem não carregou. Atualize a página.',
      'error'
    );
    return;
  }
  window.abrirEditorImagem(arquivo, (arquivoEditado) => {
    // Procura pelo próprio arquivo em vez de confiar no índice: a fila pode
    // ter mudado enquanto o editor estava aberto.
    const posicao = listaArquivosParaEnviar.indexOf(arquivo);
    if (posicao === -1) return;
    listaArquivosParaEnviar[posicao] = arquivoEditado;
    renderizarPreviewBar();
  });
};
// Remove item individual
window.removerArquivoDoPreview = function (index) {
  listaArquivosParaEnviar.splice(index, 1);
  renderizarPreviewBar();
};
// Cancela tudo
window.fecharPreviewAnexos = function () {
  listaArquivosParaEnviar = [];
  renderizarPreviewBar();
};
// ============================================================
// 🎙️ REPRODUTOR DE ÁUDIO COM PICOS ASSIMÉTRICOS E 60FPS
// ============================================================
// 0. Motor Global de Animações
window.audioAnimations = {};
window.gerarHTMLAudioPlayer = function (urlAudio, idUnico) {
  // Base 100% reta cravada no chão (Agora no Y=48, porque o teto subiu para 50)
  const pathInicial = 'M 0 48 L 100 48 Z';
  return `
    <div class="nexus-audio-player" id="player-${idUnico}">
        <audio src="${urlAudio}" id="audio-${idUnico}" preload="metadata"></audio>
        <button class="btn-play-audio" onclick="window.togglePlayAudio('${idUnico}')">
            <i class="bx bx-play" id="icon-play-${idUnico}"></i>
        </button>
        <div class="audio-wave-wrapper" id="wave-wrapper-${idUnico}" onclick="window.buscarAudio(event, '${idUnico}')">
            <svg class="audio-wave-svg" viewBox="0 0 100 50" preserveAspectRatio="none">
                <path id="path-bg-base-${idUnico}" class="onda-base" style="opacity: 0.35;" d="${pathInicial}"></path>
                <path id="path-fg-base-${idUnico}" class="onda-base" style="opacity: 0.85;" d="${pathInicial}"></path>
                <clipPath id="clip-${idUnico}">
                    <rect id="rect-clip-${idUnico}" class="mascara-progresso" x="0" y="0" width="0" height="50"></rect>
                </clipPath>
                <g clip-path="url(#clip-${idUnico})">
                    <path id="path-bg-fill-${idUnico}" class="onda-fill" style="opacity: 0.45;" d="${pathInicial}"></path>
                    <path id="path-fg-fill-${idUnico}" class="onda-fill" style="opacity: 1;" d="${pathInicial}"></path>
                </g>
            </svg>
        </div>
        <div class="audio-controls-right">
            <button class="btn-audio-speed" id="btn-speed-${idUnico}" onclick="window.mudarVelocidade('${idUnico}')">1x</button>
        </div>
        <span class="audio-duration-text" id="duration-${idUnico}">0:00</span>
       <img src="data:image/gif;base64,R0lGODlhAQABAAD/ACwAAAAAAQABAAACADs=" onload="window.carregarDuracaoAudio('${idUnico}', '${urlAudio}')" style="display:none;">
    </div>
  `;
};
// 2. Utilitário para formatar tempo (MM:SS)
window.formatarTempo = function (segundos) {
  if (!segundos || isNaN(segundos) || segundos === Infinity) return '00:00';
  const m = Math.floor(segundos / 60)
    .toString()
    .padStart(2, '0');
  const s = Math.floor(segundos % 60)
    .toString()
    .padStart(2, '0');
  return `${m}:${s}`;
};
// 3. Inicializa o áudio e aciona a leitura
window.carregarDuracaoAudio = function (id, urlAudio) {
  const audio = document.getElementById(`audio-${id}`);
  const durationSpan = document.getElementById(`duration-${id}`);
  if (!audio) return;
  audio.addEventListener('loadedmetadata', () => {
    if (audio.duration && audio.duration !== Infinity) {
      if (durationSpan)
        durationSpan.innerText = window.formatarTempo(audio.duration);
    }
  });
  if (audio.readyState >= 1 && audio.duration !== Infinity) {
    if (durationSpan)
      durationSpan.innerText = window.formatarTempo(audio.duration);
  }
  // Backup seguro para atualizar o tempo se a guia perder o foco
  audio.addEventListener('timeupdate', () => {
    window.atualizarProgressoAudio(id);
  });
  audio.addEventListener('ended', () => {
    window.resetarAudio(id);
  });
  if (urlAudio) window.desenharOndasReais(urlAudio, id);
};
// 4. Constrói a montanha (Sem barriga, base de 25% e Filtro de Ruído)
const _waveCache = {};
// Só decodifica, não toca: o Offline não abre saída de som. Antes cada áudio
// abria um AudioContext que nunca fechava; passando de umas 50 saídas abertas o
// <audio> morria com "audio render error" e só o F5 devolvia.
let contextoDaOnda = null;
function decodificarOnda(arrayBuffer) {
  if (!contextoDaOnda) contextoDaOnda = new OfflineAudioContext(1, 1, 44100);
  return contextoDaOnda.decodeAudioData(arrayBuffer);
}
window.desenharOndasReais = async function (url, idUnico) {
  if (_waveCache[idUnico]) {
    const bgSalvo = _waveCache[idUnico].bg;
    const fgSalvo = _waveCache[idUnico].fg;
    const bgBase = document.getElementById(`path-bg-base-${idUnico}`);
    const bgFill = document.getElementById(`path-bg-fill-${idUnico}`);
    const fgBase = document.getElementById(`path-fg-base-${idUnico}`);
    const fgFill = document.getElementById(`path-fg-fill-${idUnico}`);
    if (bgBase) bgBase.setAttribute('d', bgSalvo);
    if (bgFill) bgFill.setAttribute('d', bgSalvo);
    if (fgBase) fgBase.setAttribute('d', fgSalvo);
    if (fgFill) fgFill.setAttribute('d', fgSalvo);
    return;
  }
  try {
    const response = await fetch(url, {
      headers: { Authorization: `Bearer ${localStorage.getItem('token')}` },
    });
    if (!response.ok) throw new Error('Bloqueado pelo servidor');
    const arrayBuffer = await response.arrayBuffer();
    const audioBuffer = await decodificarOnda(arrayBuffer);
    const rawData = audioBuffer.getChannelData(0);
    let duracao = audioBuffer.duration || 1;
    let samples = Math.floor(duracao * 2.0);
    // Limite reajustado para 25 barras, para devolver as curvas!
    samples = Math.max(15, Math.min(25, samples));
    const blockSize = Math.floor(rawData.length / samples);
    const filteredData = [];
    for (let i = 0; i < samples; i++) {
      let blockStart = blockSize * i;
      let sum = 0;
      for (let j = 0; j < blockSize; j++) {
        sum += Math.abs(rawData[blockStart + j]);
      }
      let media = sum / blockSize;
      media = Math.pow(media, 0.65);
      filteredData.push(media);
    }
    const maxAmplitude = Math.max(...filteredData);
    const multiplier = maxAmplitude > 0 ? 1 / maxAmplitude : 0;
    // 🌊 1. ONDA DE FUNDO (Aumentado em exatamente 25% -> de 36 para 45)
    const bgPoints = filteredData.map((data) =>
      Math.max(1, data * multiplier * 45)
    );
    // 🌊 2. ONDA DA FRENTE
    const fgPoints = [];
    for (let i = 0; i < filteredData.length; i++) {
      let curr = filteredData[i];
      let next = filteredData[i + 1] || curr * 0.5;
      let deslocamento = curr * 0.4 + next * 0.6;
      // Aumentado proporcionalmente para 30, para não sumir perto da nova onda gigante
      fgPoints.push(Math.max(1, deslocamento * multiplier * 30));
    }
    // O chão da onda desceu para 48 para acompanhar o novo teto de 50
    const CY = 48;
    let step = 100 / (samples - 1);
    const gerarPath = (pontos) => {
      let p = `M 0 ${CY} `;
      p += `L 0 ${CY - pontos[0]} `;
      for (let i = 0; i < pontos.length - 1; i++) {
        let x0 = i * step;
        let y0 = CY - pontos[i];
        let x1 = (i + 1) * step;
        let y1 = CY - pontos[i + 1];
        let cx = (x0 + x1) / 2;
        p += `C ${cx} ${y0}, ${cx} ${y1}, ${x1} ${y1} `;
      }
      p += `L 100 ${CY} Z`;
      return p;
    };
    const pathBg = gerarPath(bgPoints);
    const pathFg = gerarPath(fgPoints);
    const bgBase = document.getElementById(`path-bg-base-${idUnico}`);
    const bgFill = document.getElementById(`path-bg-fill-${idUnico}`);
    const fgBase = document.getElementById(`path-fg-base-${idUnico}`);
    const fgFill = document.getElementById(`path-fg-fill-${idUnico}`);
    if (bgBase) bgBase.setAttribute('d', pathBg);
    if (bgFill) bgFill.setAttribute('d', pathBg);
    if (fgBase) fgBase.setAttribute('d', pathFg);
    if (fgFill) fgFill.setAttribute('d', pathFg);
    if (Object.keys(_waveCache).length > 200) {
      const oldest = Object.keys(_waveCache)[0];
      delete _waveCache[oldest];
    }
    _waveCache[idUnico] = { bg: pathBg, fg: pathFg };
  } catch (error) {
    console.warn('Erro ao extrair montanha:', error);
  }
};
// 5. Play / Pause
window.togglePlayAudio = function (id) {
  const audio = document.getElementById(`audio-${id}`);
  const icon = document.getElementById(`icon-play-${id}`);
  if (!audio) return;
  document.querySelectorAll('audio').forEach((a) => {
    if (a.id !== `audio-${id}`) {
      a.pause();
      const outroId = a.id.replace('audio-', '');
      const outroIcon = document.getElementById(`icon-play-${outroId}`);
      if (outroIcon) outroIcon.className = 'bx bx-play';
      if (window.audioAnimations[outroId]) {
        cancelAnimationFrame(window.audioAnimations[outroId]);
      }
    }
  });
  if (audio.paused) {
    // Falhou ao carregar (servidor reiniciando, arquivo ainda chegando): o
    // <audio> não tenta de novo sozinho, então o clique é que recarrega.
    if (audio.error) audio.load();
    audio.play().catch((e) => {
      console.log('Erro play:', e);
      if (icon) icon.className = 'bx bx-play';
    });
    if (icon) icon.className = 'bx bx-pause';
    window.iniciarAnimacaoFluida(id);
  } else {
    audio.pause();
    if (icon) icon.className = 'bx bx-play';
    if (window.audioAnimations[id]) {
      cancelAnimationFrame(window.audioAnimations[id]);
    }
  }
};
// 6. Motor 60FPS
window.iniciarAnimacaoFluida = function (id) {
  const audio = document.getElementById(`audio-${id}`);
  if (!audio) return;
  function loop() {
    if (!audio.paused && !audio.ended) {
      window.atualizarProgressoAudio(id);
      window.audioAnimations[id] = requestAnimationFrame(loop);
    }
  }
  window.audioAnimations[id] = requestAnimationFrame(loop);
};
// 7. Atualiza o visual
window.atualizarProgressoAudio = function (id) {
  const audio = document.getElementById(`audio-${id}`);
  const clipRect = document.getElementById(`rect-clip-${id}`);
  const durationSpan = document.getElementById(`duration-${id}`);
  if (audio && audio.duration && clipRect) {
    const percent = (audio.currentTime / audio.duration) * 100;
    clipRect.setAttribute('width', percent);
    if (durationSpan) {
      if (!audio.paused) {
        durationSpan.innerText = window.formatarTempo(audio.currentTime);
      } else if (audio.currentTime > 0 && !audio.ended) {
        durationSpan.innerText = window.formatarTempo(audio.currentTime);
      } else {
        durationSpan.innerText = window.formatarTempo(audio.duration);
      }
    }
  }
};
// 8. Reseta o player
window.resetarAudio = function (id) {
  const audio = document.getElementById(`audio-${id}`);
  const icon = document.getElementById(`icon-play-${id}`);
  const clipRect = document.getElementById(`rect-clip-${id}`);
  const durationSpan = document.getElementById(`duration-${id}`);
  if (icon) icon.className = 'bx bx-play';
  if (clipRect) clipRect.setAttribute('width', '0');
  if (audio && audio.duration && durationSpan) {
    durationSpan.innerText = window.formatarTempo(audio.duration);
  }
  if (window.audioAnimations[id]) {
    cancelAnimationFrame(window.audioAnimations[id]);
  }
};
// 9. Clicar para pular
window.buscarAudio = function (e, id) {
  const audio = document.getElementById(`audio-${id}`);
  const wrapper = document.getElementById(`wave-wrapper-${id}`);
  if (!audio || !wrapper) return;
  const rect = wrapper.getBoundingClientRect();
  const x = e.clientX - rect.left;
  const width = rect.width;
  if (width > 0 && audio.duration) {
    const percent = x / width;
    audio.currentTime = percent * audio.duration;
    window.atualizarProgressoAudio(id);
  }
};
// 10. Mudar Velocidade
window.mudarVelocidade = function (id) {
  const audio = document.getElementById(`audio-${id}`);
  const btn = document.getElementById(`btn-speed-${id}`);
  if (!audio || !btn) return;
  let nova = 1.0;
  if (audio.playbackRate === 1.0) nova = 1.5;
  else if (audio.playbackRate === 1.5) nova = 2.0;
  else nova = 1.0;
  audio.playbackRate = nova;
  btn.innerText = nova + 'x';
  btn.style.color = nova > 1 ? '#ffffff' : '';
};
function getIconeArquivo(ext) {
  switch (ext) {
    // PDF
    case 'pdf':
      return { icone: 'fas fa-file-pdf', cor: '#e74c3c' };
    // Word
    case 'doc':
    case 'docx':
      return { icone: 'fas fa-file-word', cor: '#2b579a' };
    // Excel
    case 'xls':
    case 'xlsx':
    case 'csv':
      return { icone: 'fas fa-file-excel', cor: '#217346' };
    // PowerPoint
    case 'ppt':
    case 'pptx':
      return { icone: 'fas fa-file-powerpoint', cor: '#d24726' };
    // --- NOVO: IMAGENS COMO ARQUIVO (Roxo/Anil) ---
    case 'jpg':
    case 'jpeg':
    case 'png':
    case 'webp':
    case 'bmp':
    case 'svg':
    case 'gif':
    case 'tiff':
    case 'tif':
    case 'heic':
      return { icone: 'fas fa-file-image', cor: '#8e44ad' };
    // --- NOVO: VÍDEOS COMO ARQUIVO (Rosa/Vermelho Escuro) ---
    case 'mp4':
    case 'avi':
    case 'mkv':
    case 'mov':
      return { icone: 'fas fa-file-video', cor: '#c0392b' };
    // Compactados
    case 'zip':
    case 'rar':
    case '7z':
      return { icone: 'fas fa-file-archive', cor: '#f1c40f' };
    // Design
    case 'psd':
    case 'ai':
    case 'eps':
      return { icone: 'fas fa-palette', cor: '#e67e22' };
    // Código
    case 'txt':
    case 'json':
    case 'js':
    case 'html':
    case 'css':
      return { icone: 'fas fa-file-code', cor: '#34495e' };
    // Padrão
    default:
      return { icone: 'fas fa-file', cor: '#7f8c8d' };
  }
}

// ============================================================
// 🖼️ VISUALIZADOR DE MÍDIA (DEFINITIVO: ZOOM NO MOUSE + ARRASTAR + PDF)
// ============================================================
const viewerOverlay = document.getElementById('media-viewer');
const viewerImg = document.getElementById('viewer-image');
const viewerVideo = document.getElementById('viewer-video');
const btnCloseViewer = document.getElementById('btn-viewer-close');
const btnDownloadViewer = document.getElementById('btn-viewer-download');
const containerViewer = document.querySelector('.media-viewer-content');

const viewerPdfWrapper = document.getElementById('viewer-pdf-wrapper');
const viewerSideRail = document.getElementById('viewer-side-rail');
const viewerZoomLevelEl = document.getElementById('viewer-zoom-level');
const viewerPageCountEl = document.getElementById('viewer-page-count');
const viewerThumbs = document.getElementById('viewer-thumbs');
const btnViewerZoomIn = document.getElementById('btn-viewer-zoom-in');
const btnViewerZoomOut = document.getElementById('btn-viewer-zoom-out');
const btnViewerPagePrev = document.getElementById('btn-viewer-page-prev');
const btnViewerPageNext = document.getElementById('btn-viewer-page-next');
const btnViewerRotate = document.getElementById('btn-viewer-rotate');

const viewerFileBadgeSolo = document.getElementById('viewer-file-badge-solo');
const viewerSearchInput = document.getElementById('viewer-search-input');
const viewerSearchNav = document.getElementById('viewer-search-nav');
const viewerSearchCount = document.getElementById('viewer-search-count');
const btnViewerSearchPrev = document.getElementById('btn-viewer-search-prev');
const btnViewerSearchNext = document.getElementById('btn-viewer-search-next');
const btnViewerSearchClear = document.getElementById('btn-viewer-search-clear');

// Estado do Zoom/Pan/Giro (imagens e vídeos)
let scale = 1;
let pointX = 0;
let pointY = 0;
let startX = 0;
let startY = 0;
let isPanning = false;
let rotation = 0;
let fitScaleAtual = 1;
// Tab + seta gira a imagem. Moram AQUI (escopo do arquivo) porque os handlers
// de teclado do visualizador ficam fora do DOMContentLoaded — declaradas lá
// dentro, davam ReferenceError e o atalho de girar não funcionava.
let isTabPressed = false;
let canRotate = true;

// Estado do visualizador de PDF (rolagem contínua, como um navegador)
let pdfDoc = null;
let pdfSessaoAtual = 0;
let pdfCurrentPage = 1;
let pdfTotalPages = 1;
let pdfBaseScale = 1;
let pdfZoomFactor = 1;
let pdfPaginas = []; // [{ pagina, wrapper, canvas, textLayer, viewportBase, intrinsicRotate, renderizado, renderizando, escalaRenderizada }]
let pdfObserver = null;
let pdfScrollRaf = null;
let pdfWheelDebounceTimer = null;
let pdfRotation = 0; // giro extra (0/90/180/270) somado à rotação própria do PDF
const PDF_ZOOM_STEPS = [0.5, 0.75, 1, 1.25, 1.5, 2, 2.5, 3];

// Busca de texto no PDF (estilo Ctrl+F)
let pdfTextoPorPagina = []; // [{ pagina, itens: [string,...] }]
let pdfTextoCarregado = false;
let pdfSearchQuery = '';
let pdfSearchMatches = []; // [{ pagina, ordinal }] — ordinal = a Nª ocorrência dentro da página
let pdfSearchIndex = -1;

function atualizarZoomLabel() {
  if (!viewerZoomLevelEl) return;
  if (viewerPdfWrapper && !viewerPdfWrapper.classList.contains('hidden')) {
    viewerZoomLevelEl.textContent = `${Math.round(pdfZoomFactor * 100)}%`;
  } else {
    const pct = fitScaleAtual ? (scale / fitScaleAtual) * 100 : 100;
    viewerZoomLevelEl.textContent = `${Math.round(pct)}%`;
  }
}

function atualizarPageNavUI(atual, total) {
  if (viewerPageCountEl) viewerPageCountEl.textContent = `${atual} / ${total}`;
  if (btnViewerPagePrev) btnViewerPagePrev.disabled = total <= 1 || atual <= 1;
  if (btnViewerPageNext)
    btnViewerPageNext.disabled = total <= 1 || atual >= total;
}

// semTransicao: aplica o novo transform de uma vez, sem animar. Usado pelo
// giro de 90° — ali mudam rotate, translate e scale ao mesmo tempo, e o
// navegador interpola os três juntos, fazendo a foto descrever um arco pela
// tela (e às vezes travar no meio do caminho) em vez de simplesmente virar.
function setTransform(semTransicao = false) {
  // Descobre qual elemento está aberto na tela (Img ou Video)
  let el = null;
  if (!viewerImg.classList.contains('hidden')) el = viewerImg;
  else if (!viewerVideo.classList.contains('hidden')) el = viewerVideo;
  if (el) {
    // transition antes do transform: as duas mudanças entram no mesmo frame,
    // então o 'none' já vale pro valor novo e o salto é instantâneo.
    el.style.transition =
      semTransicao || isPanning ? 'none' : 'transform 0.1s ease-out';
    el.style.transform = `translate(${pointX}px, ${pointY}px) scale(${scale}) rotate(${rotation}deg)`;
  }
  atualizarZoomLabel();
}
function centralizarImagem(el) {
  if (!containerViewer || !el) return;
  el.style.transformOrigin = '0 0';
  el.style.position = 'absolute';
  el.style.top = '0';
  el.style.left = '0';
  scale = 1;
  rotation = 0;
  const containerRect = retanguloLocal(containerViewer);
  const naturalW =
    el.videoWidth || el.naturalWidth || el.width || el.offsetWidth;
  const naturalH =
    el.videoHeight || el.naturalHeight || el.height || el.offsetHeight;
  if (!naturalW || !naturalH) return;
  let startScale = 1;
  // Imagens e Vídeos
  if (naturalW >= naturalH) {
    // Quadradas ou Deitadas: Reduz para ~55% da tela para não ficarem enormes
    const maxW = containerRect.width * 0.55;
    const maxH = containerRect.height * 0.8;
    startScale = Math.min(maxW / naturalW, maxH / naturalH);
  } else {
    // Em pé (Retrato): Cabe 100% dentro da tela (95% para margem) sem precisar arrastar
    const maxW = containerRect.width * 0.9;
    const maxH = containerRect.height * 0.95;
    startScale = Math.min(maxW / naturalW, maxH / naturalH);
  }
  const visualW = naturalW * startScale;
  const visualH = naturalH * startScale;
  // Posicionamento (X e Y), sempre centralizado
  pointX = (containerRect.width - visualW) / 2;
  pointY = (containerRect.height - visualH) / 2;
  scale = startScale;
  fitScaleAtual = startScale;
  el.style.width = `${naturalW}px`;
  el.style.height = `${naturalH}px`;
  setTransform();
}
// Foto de perfil: sem o sistema de pan/zoom, só centralizada e quadrada
// (o resto do enquadramento — moldura quadrada, cantos arredondados — é CSS)
function centralizarAvatar(el) {
  el.style.transition = 'none';
  el.style.transform = '';
  el.style.position = 'static';
  el.style.top = '';
  el.style.left = '';
  el.style.width = '';
  el.style.height = '';
}
// Zoom da imagem/vídeo pela roda do mouse, centrado na posição do cursor
function wheelZoomImagemVideo(e) {
  if (viewerOverlay && viewerOverlay.classList.contains('modo-avatar')) return;
  let el = null;
  if (!viewerImg.classList.contains('hidden')) el = viewerImg;
  else if (!viewerVideo.classList.contains('hidden')) el = viewerVideo;
  if (!el) return;

  e.preventDefault();
  // pointX/pointY e scale vivem em unidades LOCAIS (vão pro transform), então
  // a posição do mouse precisa entrar na mesma régua — ver escalaVisualAtual()
  // em global.js. Sem isso o ponto focal do zoom desliza em telas com escala.
  const esc = escalaVisualAtual();
  const mouseX = e.clientX / esc;
  const mouseY = e.clientY / esc;
  const xs = (mouseX - pointX) / scale;
  const ys = (mouseY - pointY) / scale;
  const delta = -Math.sign(e.deltaY);
  const factor = 0.15;
  let newScale = scale + delta * factor * scale;
  newScale = Math.min(Math.max(0.1, newScale), 20); // Limites de zoom
  pointX = mouseX - xs * newScale;
  pointY = mouseY - ys * newScale;
  scale = newScale;
  setTransform();
}

// --- EVENTOS DE MOUSE (ZOOM E ARRASTAR) ---
if (containerViewer) {
  // 1. ZOOM (Roda do Mouse)
  containerViewer.addEventListener(
    'wheel',
    (e) => {
      // PDF: Ctrl/Cmd + roda dá zoom. Roda normal rola o documento inteiro
      // de forma nativa (suave de verdade, sem simular troca de página).
      if (pdfAtivo()) {
        if (e.ctrlKey || e.metaKey) {
          e.preventDefault();
          zoomPdfContinuo(e.deltaY);
        }
        return;
      }
      wheelZoomImagemVideo(e);
    },
    { passive: false }
  );
  // 2. INICIAR ARRASTAR (MouseDown)
  containerViewer.addEventListener('mousedown', (e) => {
    if (viewerOverlay && viewerOverlay.classList.contains('modo-avatar'))
      return;
    // Clique na área do PDF: as setas ↑ ↓ voltam a rolar (ver o keydown). À
    // mão porque o Ctrl+arrastar abaixo cancela o foco natural do clique.
    if (pdfAtivo()) containerViewer.focus({ preventScroll: true });
    // Ctrl segurado = marcar texto pra copiar (OCR), não arrastar a imagem
    if (e.ctrlKey && ocrTentarIniciarSelecao(e)) return;
    if (pdfAtivo()) return; // PDF rola, não arrasta
    if (
      e.target !== viewerImg &&
      e.target !== viewerVideo &&
      e.target !== containerViewer
    )
      return;
    isPanning = true;
    // clientX é visual, pointX é local: sem converter, a imagem escorregaria
    // mais devagar que o cursor em telas com escala.
    const esc = escalaVisualAtual();
    startX = e.clientX / esc - pointX;
    startY = e.clientY / esc - pointY;
    containerViewer.style.cursor = 'grabbing';
    e.preventDefault();
  });
  // 3. MOVENDO (MouseMove)
  window.addEventListener('mousemove', (e) => {
    if (!isPanning) return;
    e.preventDefault();
    const esc = escalaVisualAtual();
    pointX = e.clientX / esc - startX;
    pointY = e.clientY / esc - startY;
    setTransform();
  });
  // 4. PARAR ARRASTAR (MouseUp)
  window.addEventListener('mouseup', () => {
    isPanning = false;
    containerViewer.style.cursor = 'default';
  });
  // 5. Rolagem do PDF: descobre qual página está em foco pra atualizar
  // contador/miniatura, e vai renderizando em nitidez as páginas que
  // vão entrando na tela (rolagem 100% nativa do navegador)
  containerViewer.addEventListener('scroll', () => {
    if (!pdfAtivo()) return;
    if (pdfScrollRaf) return;
    pdfScrollRaf = requestAnimationFrame(() => {
      pdfScrollRaf = null;
      atualizarPaginaAtualPeloScroll();
    });
  });
}

// ============================================================
// 🔎 COPIAR TEXTO DA IMAGEM (Ctrl + arrastar) — OCR SOB DEMANDA
// A atendente segura Ctrl e arrasta um retângulo em cima do texto do
// documento; o pedaço marcado sobe recortado pro backend e volta como
// texto já na área de transferência — é só dar Ctrl+V no banco.
// Vale pra imagem, vídeo e PDF — inclusive o PDF que é só um scan de foto,
// onde não existe texto de verdade pra selecionar (nem o do navegador, nem
// o daqui). Foto de perfil fica de fora.
// ============================================================
const ocrCamada = document.getElementById('viewer-ocr-camada');
const ocrSelecaoEl = document.getElementById('viewer-ocr-selecao');
const ocrStatusEl = document.getElementById('viewer-ocr-status');
const ocrDica = document.getElementById('viewer-ocr-dica');
const ocrChip = document.getElementById('viewer-ocr-chip');
const ocrChipTitulo = document.getElementById('viewer-ocr-chip-titulo');
const ocrTextoEl = document.getElementById('viewer-ocr-texto');
const btnOcrCopiar = document.getElementById('btn-viewer-ocr-copiar');
const btnOcrFechar = document.getElementById('btn-viewer-ocr-fechar');
const viewerBodyEl = document.querySelector('.viewer-body');

let ocrSelecionando = false;
let ocrCtrlPressionado = false;
let ocrOrigem = null; // canto onde o arrasto começou (unidades locais)
let ocrRetangulo = null; // retângulo atual { x, y, w, h }
let ocrSessao = 0; // descarta a resposta de uma seleção abandonada
let ocrCopiouTimer = null; // devolve o ícone de "check" pro de copiar
let ocrChipArrastando = false;
let ocrChipPegada = { x: 0, y: 0 }; // onde ela pegou dentro do balão

const ocrIconeCopiarPadrao = btnOcrCopiar ? btnOcrCopiar.innerHTML : '';
const OCR_ICONE_CHECK =
  '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><polyline points="5,13 10,18 19,7"/></svg>';

function ocrMidiaAtual() {
  if (!viewerOverlay || !viewerOverlay.classList.contains('ativo')) return null;
  if (viewerOverlay.classList.contains('modo-avatar')) return null;
  if (pdfAtivo()) return null;
  if (viewerImg && !viewerImg.classList.contains('hidden')) return viewerImg;
  if (viewerVideo && !viewerVideo.classList.contains('hidden'))
    return viewerVideo;
  return null;
}

// PDF só entra na brincadeira depois que as páginas existem na tela —
// antes disso não há o que recortar (o arquivo ainda está carregando).
function ocrPdfAtivo() {
  if (!viewerOverlay || !viewerOverlay.classList.contains('ativo'))
    return false;
  if (viewerOverlay.classList.contains('modo-avatar')) return false;
  return !!(pdfAtivo() && pdfDoc && pdfPaginas.length > 0);
}

function ocrDisponivel() {
  return !!(ocrMidiaAtual() || ocrPdfAtivo());
}

// Ponto do mouse na MESMA régua de pointX/pointY: unidades locais e
// relativas ao canto do container (ver escalaVisualAtual() no global.js).
// Sem isso o retângulo desliza do cursor em monitor com escala.
function ocrPontoLocal(evento) {
  const esc = escalaVisualAtual();
  const r = retanguloLocal(containerViewer);
  return {
    x: evento.clientX / esc - r.left + containerViewer.scrollLeft,
    y: evento.clientY / esc - r.top + containerViewer.scrollTop,
  };
}

function ocrArmar(ligado) {
  if (!containerViewer) return;
  const vale = !!(ligado && ocrDisponivel());
  containerViewer.classList.toggle('viewer-ocr-armado', vale);
  // O arrasto normal escreve cursor inline ('grabbing'/'default'), e
  // inline ganha da classe — então a mira também precisa ser inline.
  containerViewer.style.cursor = vale ? 'crosshair' : '';
  if (ocrDica) ocrDica.classList.toggle('armada', vale);
}

// Chamada ao abrir/fechar o visualizador: a dica só aparece onde o
// recurso funciona, e nada fica preso na tela ao trocar de mídia.
function ocrSincronizarUI() {
  const temMidia = ocrDisponivel();
  if (ocrDica) ocrDica.classList.toggle('hidden', !temMidia);
  if (!temMidia) {
    ocrEncerrarSelecao();
    ocrFecharChip();
    ocrArmar(false);
  }
}

function ocrDesenharSelecao() {
  if (!ocrSelecaoEl || !ocrRetangulo) return;
  ocrSelecaoEl.style.left = ocrRetangulo.x + 'px';
  ocrSelecaoEl.style.top = ocrRetangulo.y + 'px';
  ocrSelecaoEl.style.width = ocrRetangulo.w + 'px';
  ocrSelecaoEl.style.height = ocrRetangulo.h + 'px';
  // Seleção colada no topo: o rótulo "Lendo…" desce pra não sair de vista
  if (ocrStatusEl) ocrStatusEl.classList.toggle('abaixo', ocrRetangulo.y < 32);
}

// Some com o retângulo. Não mexe no chip do resultado.
function ocrEncerrarSelecao() {
  ocrSelecionando = false;
  ocrRetangulo = null;
  ocrOrigem = null;
  if (ocrSelecaoEl) ocrSelecaoEl.classList.add('hidden');
  if (ocrStatusEl) ocrStatusEl.classList.add('hidden');
  if (ocrCamada) ocrCamada.classList.add('hidden');
}

// Devolve true quando assume o arrasto (aí o pan da imagem não roda)
function ocrTentarIniciarSelecao(evento) {
  if (!ocrDisponivel()) return false;
  ocrFecharChip();
  ocrSessao++; // qualquer leitura ainda em voo perde a vez
  ocrSelecionando = true;
  ocrOrigem = ocrPontoLocal(evento);
  ocrRetangulo = { x: ocrOrigem.x, y: ocrOrigem.y, w: 0, h: 0 };
  if (ocrCamada) ocrCamada.classList.remove('hidden');
  if (ocrSelecaoEl) ocrSelecaoEl.classList.remove('hidden');
  if (ocrStatusEl) ocrStatusEl.classList.add('hidden');
  ocrDesenharSelecao();
  ocrArmar(true);
  // Em PDF pode ter sobrado texto marcado da seleção nativa do navegador;
  // deixar aquele azul aceso no meio do recorte só confunde.
  const selecaoNativa = window.getSelection && window.getSelection();
  if (selecaoNativa && !selecaoNativa.isCollapsed)
    selecaoNativa.removeAllRanges();
  evento.preventDefault(); // trava o "arrastar imagem"/seleção nativa do navegador
  return true;
}

// Desfaz o transform do visualizador (translate + scale + rotate) e
// redesenha SÓ o pedaço marcado, na resolução original da mídia — o
// zoom da tela não deve piorar nem inflar o que vai pro OCR.
function ocrRecortar(el, ret) {
  const larguraNat = el.naturalWidth || el.videoWidth || 0;
  const alturaNat = el.naturalHeight || el.videoHeight || 0;
  if (!larguraNat || !alturaNat) throw new Error('Mídia sem dimensão');

  const larguraRecorte = ret.w / scale;
  const alturaRecorte = ret.h / scale;

  // Recorte pequeno (uma linha de CPF) sobe de resolução pro modelo
  // enxergar o traço; recorte enorme desce, pra não subir 8000px à toa.
  let fator = Math.min(3, Math.max(1, 700 / larguraRecorte));
  const maiorLado = Math.max(larguraRecorte, alturaRecorte) * fator;
  if (maiorLado > 2000) fator *= 2000 / maiorLado;

  const canvas = document.createElement('canvas');
  canvas.width = Math.max(1, Math.round(larguraRecorte * fator));
  canvas.height = Math.max(1, Math.round(alturaRecorte * fator));
  const ctx = canvas.getContext('2d');
  ctx.imageSmoothingEnabled = true;
  ctx.imageSmoothingQuality = 'high';
  // Fundo branco: em PNG transparente o texto viraria letra invisível
  ctx.fillStyle = '#ffffff';
  ctx.fillRect(0, 0, canvas.width, canvas.height);
  ctx.scale(fator, fator);
  // Mesma composição do setTransform(), na ordem inversa: leva o canto
  // do retângulo da tela pra origem do canvas e reaplica o giro.
  ctx.translate((pointX - ret.x) / scale, (pointY - ret.y) / scale);
  ctx.rotate((rotation * Math.PI) / 180);
  ctx.drawImage(el, 0, 0, larguraNat, alturaNat);
  return ocrCanvasParaDataUrl(canvas);
}

// PNG é o formato ideal pro OCR (não borra o traço da letra), mas um
// recorte grande vira um base64 que o backend recusa (teto de 8 MB).
// Nesse caso vale mais mandar JPEG de qualidade alta do que falhar.
function ocrCanvasParaDataUrl(canvas) {
  const png = canvas.toDataURL('image/png');
  if (png.length <= 5 * 1024 * 1024) return png;
  return canvas.toDataURL('image/jpeg', 0.92);
}

// ---------- RECORTE EM PDF ----------
// Aqui não existe um <img> pra recortar: o que está na tela são canvas de
// página, um por página, empilhados dentro do container que rola. Então o
// retângulo marcado é cruzado com cada página pra descobrir que pedaço de
// qual página ele pegou (normalmente uma só; seleção que atravessa a
// emenda pega duas).
function ocrPecasPdf(ret) {
  const pecas = [];
  if (!containerViewer) return pecas;
  const rCont = retanguloLocal(containerViewer);
  const rolX = containerViewer.scrollLeft;
  const rolY = containerViewer.scrollTop;

  for (const p of pdfPaginas) {
    const rp = retanguloLocal(p.wrapper);
    // Canto da página na MESMA régua do retângulo (local + rolagem)
    const px = rp.left - rCont.left + rolX;
    const py = rp.top - rCont.top + rolY;
    const x1 = Math.max(ret.x, px);
    const y1 = Math.max(ret.y, py);
    const x2 = Math.min(ret.x + ret.w, px + rp.width);
    const y2 = Math.min(ret.y + ret.h, py + rp.height);
    if (x2 - x1 < 4 || y2 - y1 < 4) continue; // encostou de raspão
    pecas.push({
      info: p,
      x: x1 - px, // dentro da página, em px CSS
      y: y1 - py,
      w: x2 - x1,
      h: y2 - y1,
      larguraPagina: rp.width,
      alturaPagina: rp.height,
    });
  }
  return pecas;
}

// Redesenha o pedaço a partir do PDF, e não do canvas que está na tela:
// mesmo com o documento em 50% de zoom o recorte sai em alta resolução,
// que é o que evita o OCR trocar dígito de CPF em documento escaneado.
async function ocrDesenharPecaPdf(peca, fator, destino, dy) {
  const page = await pdfDoc.getPage(peca.info.pagina);
  const rotacao = (peca.info.intrinsicRotate + pdfRotation) % 360;
  // A escala vem da largura que a página REALMENTE tem na tela — assim o
  // recorte cai onde ela marcou, sem depender do estado do zoom.
  const vpBase = page.getViewport({ scale: 1, rotation: rotacao });
  const viewport = page.getViewport({
    scale: (peca.larguraPagina / vpBase.width) * fator,
    rotation: rotacao,
  });

  const tmp = document.createElement('canvas');
  tmp.width = Math.max(1, Math.round(peca.w * fator));
  tmp.height = Math.max(1, Math.round(peca.h * fator));
  const ctx = tmp.getContext('2d');
  ctx.fillStyle = '#ffffff'; // PDF com fundo transparente viraria letra invisível
  ctx.fillRect(0, 0, tmp.width, tmp.height);
  // Renderiza a página inteira nesse canvas pequeno deslocada pra que o
  // pedaço marcado caia exatamente na origem (mesma técnica de "tile").
  await page.render({
    canvasContext: ctx,
    viewport,
    transform: [1, 0, 0, 1, -peca.x * fator, -peca.y * fator],
  }).promise;
  destino.drawImage(tmp, 0, dy);
}

// Plano B se o redesenho falhar: recorta do canvas que já está na tela.
// Perde nitidez (fica preso ao zoom atual), mas é melhor que nada.
function ocrDesenharPecaDaTela(peca, fator, destino, dy) {
  const c = peca.info.canvas;
  if (!c || !c.width || !c.height) return;
  const fx = c.width / peca.larguraPagina;
  const fy = c.height / peca.alturaPagina;
  destino.drawImage(
    c,
    peca.x * fx,
    peca.y * fy,
    Math.max(1, peca.w * fx),
    Math.max(1, peca.h * fy),
    0,
    dy,
    Math.max(1, peca.w * fator),
    Math.max(1, peca.h * fator)
  );
}

async function ocrRecortarPdf(ret) {
  const pecas = ocrPecasPdf(ret);
  if (!pecas.length) throw new Error('Seleção fora das páginas');

  const largura = Math.max(...pecas.map((p) => p.w));
  const altura = pecas.reduce((soma, p) => soma + p.h, 0);
  // Mesma ideia do recorte de imagem: linha curta sobe de resolução pro
  // modelo enxergar o traço, recorte enorme desce pra não subir à toa.
  let fator = Math.min(4, Math.max(1, 900 / largura));
  const maiorLado = Math.max(largura, altura) * fator;
  if (maiorLado > 2200) fator *= 2200 / maiorLado;

  const canvas = document.createElement('canvas');
  canvas.width = Math.max(1, Math.round(largura * fator));
  canvas.height = Math.max(1, Math.round(altura * fator));
  const ctx = canvas.getContext('2d');
  ctx.imageSmoothingEnabled = true;
  ctx.imageSmoothingQuality = 'high';
  ctx.fillStyle = '#ffffff';
  ctx.fillRect(0, 0, canvas.width, canvas.height);

  // Seleção que atravessa duas páginas vira uma tira só, uma embaixo da
  // outra — é como ela enxerga na tela, e o texto sai na mesma ordem.
  let deslocY = 0;
  for (const peca of pecas) {
    try {
      await ocrDesenharPecaPdf(peca, fator, ctx, Math.round(deslocY));
    } catch (err) {
      console.warn('[OCR] Redesenho da página falhou, usando a tela:', err);
      ocrDesenharPecaDaTela(peca, fator, ctx, Math.round(deslocY));
    }
    deslocY += peca.h * fator;
  }
  return ocrCanvasParaDataUrl(canvas);
}

async function ocrCopiarTexto(texto) {
  try {
    await navigator.clipboard.writeText(texto);
    return true;
  } catch (err) {
    // Sem permissão de área de transferência (ou origem sem HTTPS):
    // cai no caminho antigo, que funciona em qualquer contexto.
    try {
      const caixa = document.createElement('textarea');
      caixa.value = texto;
      caixa.setAttribute('readonly', '');
      caixa.style.position = 'fixed';
      caixa.style.top = '-1000px';
      caixa.style.opacity = '0';
      document.body.appendChild(caixa);
      caixa.select();
      const copiou = document.execCommand('copy');
      document.body.removeChild(caixa);
      return copiou;
    } catch (err2) {
      return false;
    }
  }
}

function ocrRestaurarIconeCopiar() {
  clearTimeout(ocrCopiouTimer);
  if (!btnOcrCopiar) return;
  btnOcrCopiar.classList.remove('copiou');
  btnOcrCopiar.innerHTML = ocrIconeCopiarPadrao;
}

// Confirmação sem texto: o ícone de copiar vira um "check" verde por um
// instante, só pra ela saber que o Ctrl+V já está carregado.
function ocrPiscarCopiado() {
  if (!btnOcrCopiar) return;
  clearTimeout(ocrCopiouTimer);
  btnOcrCopiar.classList.add('copiou');
  btnOcrCopiar.innerHTML = OCR_ICONE_CHECK;
  ocrCopiouTimer = setTimeout(ocrRestaurarIconeCopiar, 1500);
}

// A caixa cresce com o texto até o limite do CSS (aí passa a rolar), pra
// um documento inteiro não aparecer cortado pela metade.
function ocrAjustarAlturaTexto() {
  if (!ocrTextoEl) return;
  ocrTextoEl.style.height = 'auto';
  ocrTextoEl.style.height = Math.min(ocrTextoEl.scrollHeight + 2, 220) + 'px';
}

// O balão fica na tela até ela fechar no X (ou no Esc). Não some sozinho:
// com muita informação, copiar e colar no banco demora mais do que
// qualquer tempo que a gente chutasse, e sumir no meio seria pior.
function ocrFecharChip() {
  if (ocrChip) ocrChip.classList.add('hidden');
  ocrRestaurarIconeCopiar();
}

// Resultado ancorado logo abaixo do que ela marcou (e nunca fora da tela).
// Fica editável de propósito: OCR erra dígito, e é mais rápido corrigir um
// "8" que virou "B" ali do que digitar o documento inteiro de novo.
function ocrMostrarChip(ret, dados) {
  const texto = dados.texto || '';
  const erro = dados.erro || '';
  const copiou = !!dados.copiou;

  ocrEncerrarSelecao();
  if (!ocrChip || !viewerBodyEl || !containerViewer) return;

  // Deu certo não precisa de legenda: o que interessa é o texto embaixo.
  // A faixa de cima só existe pra avisar quando algo deu errado.
  const aviso = erro || (texto && !copiou ? 'Copie na mão' : '');
  ocrChipTitulo.textContent = aviso;
  ocrChipTitulo.classList.toggle('erro', !!aviso);
  if (copiou) ocrPiscarCopiado();
  ocrTextoEl.value = texto;
  ocrTextoEl.classList.toggle('hidden', !texto);
  if (btnOcrCopiar) btnOcrCopiar.classList.toggle('hidden', !texto);

  ocrChip.classList.remove('hidden');
  ocrAjustarAlturaTexto(); // antes de medir: a altura do balão depende disso
  const rBody = retanguloLocal(viewerBodyEl);
  const rCont = retanguloLocal(containerViewer);
  const deslocX = rCont.left - rBody.left; // trilho lateral, quando aberto
  const deslocY = rCont.top - rBody.top;
  const largura = ocrChip.offsetWidth;
  const altura = ocrChip.offsetHeight;

  // O retângulo vive na régua do CONTEÚDO (inclui a rolagem, por causa do
  // PDF); o balão vive na tela. Sem descontar a rolagem, ele apareceria
  // páginas abaixo — ou seja, fora de vista.
  const rolX = containerViewer.scrollLeft;
  const rolY = containerViewer.scrollTop;
  let x = deslocX + ret.x - rolX;
  let y = deslocY + ret.y + ret.h - rolY + 12;
  // Não coube embaixo da seleção: sobe pra cima dela
  if (y + altura > rBody.height - 10) y = deslocY + ret.y - rolY - altura - 12;
  ocrChip.style.left =
    Math.max(10, Math.min(x, rBody.width - largura - 10)) + 'px';
  ocrChip.style.top =
    Math.max(10, Math.min(y, rBody.height - altura - 10)) + 'px';
}

async function ocrLerSelecao(ret) {
  const ehPdf = ocrPdfAtivo();
  const el = ehPdf ? null : ocrMidiaAtual();
  if (!ehPdf && !el) {
    ocrEncerrarSelecao();
    return;
  }
  const sessao = ++ocrSessao;

  if (ocrStatusEl) {
    ocrStatusEl.textContent = 'Lendo…';
    ocrStatusEl.classList.remove('hidden');
  }

  let recorte;
  try {
    recorte = ehPdf ? await ocrRecortarPdf(ret) : ocrRecortar(el, ret);
    if (sessao !== ocrSessao) return; // o recorte do PDF é assíncrono
  } catch (err) {
    // Canvas "sujo" (mídia servida por outro domínio) cai aqui
    console.error('[OCR] Falha ao recortar:', err);
    if (sessao !== ocrSessao) return;
    ocrMostrarChip(ret, { erro: 'Não consegui recortar essa mídia.' });
    return;
  }

  try {
    const resposta = await fetch('/api/chat/ocr-imagem', {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        Authorization: 'Bearer ' + localStorage.getItem('token'),
      },
      body: JSON.stringify({ imagem: recorte }),
    });
    const dados = await resposta.json().catch(() => ({}));
    if (sessao !== ocrSessao) return; // ela já começou outra seleção

    if (!resposta.ok || !dados.success) {
      ocrMostrarChip(ret, { erro: dados.message || 'Falha ao ler o texto.' });
      return;
    }

    const texto = (dados.texto || '').trim();
    if (!texto) {
      ocrMostrarChip(ret, { erro: 'Nenhum texto legível nesse pedaço.' });
      return;
    }

    const copiou = await ocrCopiarTexto(texto);
    ocrMostrarChip(ret, { texto: texto, copiou: copiou });
  } catch (err) {
    if (sessao !== ocrSessao) return;
    console.error('[OCR] Falha na leitura:', err);
    ocrMostrarChip(ret, { erro: 'Erro de conexão ao ler o texto.' });
  }
}

if (containerViewer) {
  // O mousedown fica no handler de arrastar lá de cima (é ele que decide
  // entre arrastar a imagem e selecionar); aqui só o resto do gesto.
  window.addEventListener('mousemove', (e) => {
    if (!ocrSelecionando) return;
    const p = ocrPontoLocal(e);
    ocrRetangulo = {
      x: Math.min(p.x, ocrOrigem.x),
      y: Math.min(p.y, ocrOrigem.y),
      w: Math.abs(p.x - ocrOrigem.x),
      h: Math.abs(p.y - ocrOrigem.y),
    };
    ocrDesenharSelecao();
    e.preventDefault();
  });

  window.addEventListener('mouseup', () => {
    if (!ocrSelecionando) return;
    ocrSelecionando = false;
    const ret = ocrRetangulo;
    // Terminou o recorte: no PDF desarma sempre (senão o user-select:none fica
    // preso na camada de texto enquanto ela ainda segura o Ctrl, e ela não
    // consegue mais marcar texto pra copiar no braço). Novo recorte re-arma
    // sozinho no próximo Ctrl+arrastar. Imagem/vídeo seguem a regra do Ctrl.
    ocrArmar(ocrPdfAtivo() ? false : ocrCtrlPressionado);
    // Clique solto ou arrasto minúsculo: trata como desistência
    if (!ret || ret.w < 8 || ret.h < 8) {
      ocrEncerrarSelecao();
      return;
    }
    ocrLerSelecao(ret);
  });
}

document.addEventListener('keydown', (e) => {
  if (e.key === 'Control') {
    ocrCtrlPressionado = true;
    // No PDF NÃO pré-armar só com o Ctrl: a classe .viewer-ocr-armado põe
    // user-select:none na camada de texto e o Chrome descarta a seleção
    // nativa que a atendente fez justamente pra dar Ctrl+C. Aqui a mira só
    // liga quando o arrasto começa de fato — Ctrl + botão esquerdo segurado,
    // lá no mousedown (ocrTentarIniciarSelecao). Imagem/vídeo não têm camada
    // de texto pra brigar, então continuam pré-armando pra dar a dica visual.
    if (!ocrPdfAtivo()) ocrArmar(true);
  }
});

document.addEventListener('keyup', (e) => {
  if (e.key === 'Control') {
    ocrCtrlPressionado = false;
    if (!ocrSelecionando) ocrArmar(false);
  }
});

// Alt+Tab no meio do caminho deixaria a mira presa no cursor
window.addEventListener('blur', () => {
  ocrCtrlPressionado = false;
  if (!ocrSelecionando) ocrArmar(false);
});

// Esc cancela a seleção/o resultado ANTES de fechar o visualizador —
// este listener é registrado antes do Esc do viewer de propósito.
document.addEventListener('keydown', (e) => {
  if (e.key !== 'Escape') return;
  const selecaoNaTela =
    ocrSelecionando ||
    (ocrSelecaoEl && !ocrSelecaoEl.classList.contains('hidden'));
  if (selecaoNaTela) {
    ocrSessao++; // descarta a leitura que ainda estiver vindo
    ocrEncerrarSelecao();
    ocrArmar(ocrPdfAtivo() ? false : ocrCtrlPressionado);
    e.preventDefault();
    e.stopImmediatePropagation();
    return;
  }
  if (ocrChip && !ocrChip.classList.contains('hidden')) {
    ocrFecharChip();
    e.preventDefault();
    e.stopImmediatePropagation();
  }
});

if (btnOcrCopiar) {
  btnOcrCopiar.addEventListener('click', async () => {
    const texto = ocrTextoEl.value.trim();
    if (!texto) return;
    const copiou = await ocrCopiarTexto(texto);
    if (copiou) {
      ocrPiscarCopiado();
      ocrChipTitulo.textContent = '';
      ocrChipTitulo.classList.remove('erro');
    } else {
      ocrChipTitulo.textContent = 'Não consegui copiar sozinho';
      ocrChipTitulo.classList.add('erro');
    }
  });
}

if (btnOcrFechar) btnOcrFechar.addEventListener('click', ocrFecharChip);

// --- ARRASTAR O BALÃO ---
// Ele nasce colado na seleção, e às vezes é justo em cima do que ela ainda
// precisa enxergar na foto. Pega em qualquer parte do balão, menos na caixa
// de texto (onde ela digita e usa a alcinha de redimensionar) e nos botões.
if (ocrChip) {
  ocrChip.addEventListener('mousedown', (e) => {
    e.stopPropagation();
    if (e.button !== 0) return;
    if (e.target.closest('.viewer-ocr-texto, .viewer-ocr-chip-btn')) return;
    const esc = escalaVisualAtual();
    ocrChipArrastando = true;
    ocrChipPegada = {
      x: e.clientX / esc - (parseFloat(ocrChip.style.left) || 0),
      y: e.clientY / esc - (parseFloat(ocrChip.style.top) || 0),
    };
    ocrChip.classList.add('arrastando');
    e.preventDefault();
  });

  window.addEventListener('mousemove', (e) => {
    if (!ocrChipArrastando) return;
    const esc = escalaVisualAtual();
    const rBody = retanguloLocal(viewerBodyEl);
    let x = e.clientX / esc - ocrChipPegada.x;
    let y = e.clientY / esc - ocrChipPegada.y;
    // Não deixa sair da área do visualizador (senão some e não volta)
    x = Math.max(0, Math.min(x, rBody.width - ocrChip.offsetWidth));
    y = Math.max(0, Math.min(y, rBody.height - ocrChip.offsetHeight));
    ocrChip.style.left = x + 'px';
    ocrChip.style.top = y + 'px';
    e.preventDefault();
  });

  window.addEventListener('mouseup', () => {
    if (!ocrChipArrastando) return;
    ocrChipArrastando = false;
    ocrChip.classList.remove('arrastando');
  });
}

if (ocrTextoEl) {
  ocrTextoEl.addEventListener('input', ocrAjustarAlturaTexto);
  ocrTextoEl.addEventListener('keydown', (e) => {
    if (e.key === 'Escape') ocrFecharChip();
    // Tab e setas aqui dentro são pra digitar, não pra girar a imagem
    e.stopPropagation();
  });
}

// --- ZOOM PELOS BOTÕES DA BARRA LATERAL (imagem, vídeo ou PDF) ---
function pdfAtivo() {
  return viewerPdfWrapper && !viewerPdfWrapper.classList.contains('hidden');
}

function aplicarZoomBotao(direcao) {
  if (pdfAtivo()) {
    zoomPdf(direcao);
    return;
  }
  const el = !viewerImg.classList.contains('hidden')
    ? viewerImg
    : !viewerVideo.classList.contains('hidden')
      ? viewerVideo
      : null;
  if (!el || !containerViewer) return;
  const rect = retanguloLocal(containerViewer);
  const cx = rect.width / 2;
  const cy = rect.height / 2;
  const xs = (cx - pointX) / scale;
  const ys = (cy - pointY) / scale;
  const factor = direcao > 0 ? 1.25 : 0.8;
  const newScale = Math.min(Math.max(fitScaleAtual * 0.2, scale * factor), 20);
  pointX = cx - xs * newScale;
  pointY = cy - ys * newScale;
  scale = newScale;
  setTransform();
}

if (btnViewerZoomIn)
  btnViewerZoomIn.addEventListener('click', () => aplicarZoomBotao(1));
if (btnViewerZoomOut)
  btnViewerZoomOut.addEventListener('click', () => aplicarZoomBotao(-1));

// Gira a imagem 90° por vez, sempre recalculando o encaixe (fit) pro novo
// enquadramento — ao contrário do atalho de teclado (Tab+seta), que só
// girava no lugar, aqui a foto sempre volta a caber e ficar centralizada,
// sem precisar arrastar pra reencontrar ela depois de girar.
function girarImagemViewer(delta) {
  if (!viewerImg || viewerImg.classList.contains('hidden') || !containerViewer)
    return;
  const el = viewerImg;
  rotation = (rotation + delta * 90) % 360;
  if (rotation < 0) rotation += 360;

  const naturalW = el.naturalWidth || el.width || el.offsetWidth;
  const naturalH = el.naturalHeight || el.height || el.offsetHeight;
  if (!naturalW || !naturalH) {
    setTransform(true);
    return;
  }

  const girado = rotation % 180 !== 0;
  const efetivoW = girado ? naturalH : naturalW;
  const efetivoH = girado ? naturalW : naturalH;

  const containerRect = retanguloLocal(containerViewer);
  let startScale;
  if (efetivoW >= efetivoH) {
    startScale = Math.min(
      (containerRect.width * 0.55) / efetivoW,
      (containerRect.height * 0.8) / efetivoH
    );
  } else {
    startScale = Math.min(
      (containerRect.width * 0.9) / efetivoW,
      (containerRect.height * 0.95) / efetivoH
    );
  }

  scale = startScale;
  fitScaleAtual = startScale;

  const visualW = efetivoW * startScale;
  const visualH = efetivoH * startScale;
  const centerX = (containerRect.width - visualW) / 2;
  const centerY = (containerRect.height - visualH) / 2;

  // pointX/pointY ancoram o elemento ANTES do giro (transform-origin: 0 0,
  // giro acontece primeiro na composição do transform); cada ângulo joga
  // um canto diferente do elemento pra fora da origem, então o ponto de
  // ancoragem pra manter a caixa já girada centralizada muda por ângulo.
  if (rotation === 90) {
    pointX = centerX + visualW;
    pointY = centerY;
  } else if (rotation === 180) {
    pointX = centerX + visualW;
    pointY = centerY + visualH;
  } else if (rotation === 270) {
    pointX = centerX;
    pointY = centerY + visualH;
  } else {
    pointX = centerX;
    pointY = centerY;
  }

  setTransform(true);
}

if (btnViewerRotate)
  btnViewerRotate.addEventListener('click', () => {
    if (pdfAtivo()) girarPdf();
    else girarImagemViewer(1);
  });

// --- VISUALIZADOR DE PDF: rolagem contínua com todas as páginas empilhadas,
// igual navegador (pdf.js: canvas nítido + texto selecionável) ---

function larguraAlturaPagina(viewportBase) {
  const cssScale = pdfBaseScale * pdfZoomFactor;
  const w = viewportBase.width * cssScale;
  const h = viewportBase.height * cssScale;
  // viewportBase já reflete a rotação própria do PDF (intrinsicRotate) — o
  // que falta considerar aqui é só o giro extra que o usuário aplicou.
  return pdfRotation % 180 !== 0 ? { w: h, h: w } : { w, h };
}

function criarPaginaPdfDom(numPagina, viewportBase) {
  const wrapper = document.createElement('div');
  wrapper.className = 'pdf-page';
  wrapper.dataset.pagina = String(numPagina);
  const { w, h } = larguraAlturaPagina(viewportBase);
  wrapper.style.width = `${w}px`;
  wrapper.style.height = `${h}px`;

  const canvas = document.createElement('canvas');
  const textLayer = document.createElement('div');
  textLayer.className = 'pdf-text-layer';
  wrapper.appendChild(canvas);
  wrapper.appendChild(textLayer);
  viewerPdfWrapper.appendChild(wrapper);

  pdfPaginas.push({
    pagina: numPagina,
    wrapper,
    canvas,
    textLayer,
    viewportBase,
    intrinsicRotate: viewportBase.rotation || 0,
    renderizado: false,
    renderizando: false,
    escalaRenderizada: null,
  });
}

// Renderiza (ou re-renderiza) uma página específica em alta nitidez.
// "forcar" ignora o cache — usado quando o zoom muda.
async function renderizarPaginaPdfEm(numPagina, sessao, forcar = false) {
  const info = pdfPaginas.find((p) => p.pagina === numPagina);
  if (!info || !pdfDoc || info.renderizando) return;
  if (info.renderizado && !forcar) return;
  info.renderizando = true;
  try {
    const page = await pdfDoc.getPage(numPagina);
    if (sessao !== pdfSessaoAtual) return;

    const dpr = window.devicePixelRatio || 1;
    const cssScale = pdfBaseScale * pdfZoomFactor;
    const rotacaoTotal = (info.intrinsicRotate + pdfRotation) % 360;
    const cssViewport = page.getViewport({
      scale: cssScale,
      rotation: rotacaoTotal,
    });
    const renderViewport = page.getViewport({
      scale: cssScale * dpr,
      rotation: rotacaoTotal,
    });

    // Renderiza num canvas invisível (buffer) e só troca no canvas visível
    // quando já está pronto — evita "piscar" a página em branco na espera
    const bufferCanvas = document.createElement('canvas');
    bufferCanvas.width = renderViewport.width;
    bufferCanvas.height = renderViewport.height;
    const task = page.render({
      canvasContext: bufferCanvas.getContext('2d'),
      viewport: renderViewport,
    });
    try {
      await task.promise;
    } catch (e) {
      return; // render cancelado (troca rápida de zoom/página)
    }
    if (sessao !== pdfSessaoAtual) return;

    // O tamanho do wrapper é controlado só por redimensionarTodasPaginasPdf()
    // (fonte única de verdade). O canvas nunca define seu próprio tamanho em
    // px — ele só segue o wrapper via CSS (width/height: 100%), então nunca
    // fica "preso" num tamanho antigo enquanto o zoom muda por baixo dele.
    info.canvas.width = renderViewport.width;
    info.canvas.height = renderViewport.height;
    info.canvas.getContext('2d').drawImage(bufferCanvas, 0, 0);

    // Camada de texto selecionável/copiável por cima do canvas
    info.textLayer.innerHTML = '';
    info.textLayer.style.width = `${cssViewport.width}px`;
    info.textLayer.style.height = `${cssViewport.height}px`;
    const textContent = await page.getTextContent();
    if (sessao !== pdfSessaoAtual) return;
    pdfjsLib.renderTextLayer({
      textContent,
      container: info.textLayer,
      viewport: cssViewport,
      textDivs: [],
    });
    // A camada de texto acabou de ser reconstruída do zero (innerHTML=''
    // lá em cima) — qualquer marcação de busca antiga nela já se foi.
    // Reaplica se tiver uma busca ativa, senão o destaque "some" toda vez
    // que zoom/giro/rolagem força um re-render (só a atual, sem rolar a
    // tela — isso é coisa de navegação explícita, não de render passivo).
    reaplicarBuscaNaPagina(numPagina);

    info.renderizado = true;
    info.escalaRenderizada = pdfZoomFactor;
  } finally {
    info.renderizando = false;
  }
}

function configurarObserverPdf(sessao) {
  if (pdfObserver) pdfObserver.disconnect();
  pdfObserver = new IntersectionObserver(
    (entries) => {
      if (sessao !== pdfSessaoAtual) return;
      entries.forEach((entry) => {
        if (!entry.isIntersecting) return;
        const numPagina = Number(entry.target.dataset.pagina);
        renderizarPaginaPdfEm(numPagina, sessao);
      });
    },
    {
      root: containerViewer,
      rootMargin: '600px 0px 600px 0px', // pré-renderiza um pouco antes de entrar na tela
      threshold: 0.01,
    }
  );
  pdfPaginas.forEach((p) => pdfObserver.observe(p.wrapper));
}

// Descobre qual página está mais "em foco" na tela e atualiza contador/miniatura
function atualizarPaginaAtualPeloScroll() {
  if (!containerViewer || pdfPaginas.length === 0) return;
  const contRect = containerViewer.getBoundingClientRect();
  const linhaRef = contRect.top + contRect.height * 0.3;
  let melhor = pdfPaginas[0];
  for (const p of pdfPaginas) {
    const r = p.wrapper.getBoundingClientRect();
    if (r.top <= linhaRef) melhor = p;
    else break;
  }
  if (melhor.pagina !== pdfCurrentPage) {
    pdfCurrentPage = melhor.pagina;
    atualizarPageNavUI(pdfCurrentPage, pdfTotalPages);
    atualizarMiniaturaAtivaPdf();
  }
}

// Redimensiona TODAS as páginas juntas pro tamanho do zoom atual. É só
// layout (width/height), sem re-render pesado — por isso pode rodar a
// cada tique da roda sem travar, e mantém a pilha inteira sincronizada
// (nunca uma página com tamanho diferente da outra). É a ÚNICA função que
// mexe no tamanho do wrapper — nenhuma outra parte do código pode fazer isso,
// senão duas fontes de verdade competindo é exatamente o que desincroniza as
// páginas. Também ancora o scroll na página atual, pra ela não "andar" na
// tela enquanto as páginas ao redor mudam de tamanho.
function redimensionarTodasPaginasPdf() {
  const atual = pdfPaginas.find((p) => p.pagina === pdfCurrentPage);
  const topoAntes =
    atual && containerViewer ? retanguloLocal(atual.wrapper).top : null;

  pdfPaginas.forEach((p) => {
    const { w, h } = larguraAlturaPagina(p.viewportBase);
    p.wrapper.style.width = `${w}px`;
    p.wrapper.style.height = `${h}px`;
  });

  if (topoAntes !== null) {
    // scrollTop é local, então o topo precisa vir na mesma régua
    const topoDepois = retanguloLocal(atual.wrapper).top;
    containerViewer.scrollTop += topoDepois - topoAntes;
  }
}

let pdfResizeRaf = null;
function agendarRedimensionamentoPdf() {
  if (pdfResizeRaf) return;
  pdfResizeRaf = requestAnimationFrame(() => {
    pdfResizeRaf = null;
    redimensionarTodasPaginasPdf();
  });
}

function zoomPdf(direcao) {
  if (!pdfDoc) return;
  let idx = 0;
  let menorDiff = Infinity;
  PDF_ZOOM_STEPS.forEach((v, i) => {
    const diff = Math.abs(v - pdfZoomFactor);
    if (diff < menorDiff) {
      menorDiff = diff;
      idx = i;
    }
  });
  idx = Math.min(PDF_ZOOM_STEPS.length - 1, Math.max(0, idx + direcao));
  pdfZoomFactor = PDF_ZOOM_STEPS[idx];
  aplicarNovoZoomPdf();
}

// Ctrl/Cmd + roda: zoom contínuo e liso. Redimensiona a pilha inteira junto
// (todas as páginas no mesmo tamanho, sempre) e só re-renderiza a página
// atual em nitidez de verdade quando o usuário parar de rolar a roda.
function zoomPdfContinuo(deltaY) {
  if (!pdfDoc) return;
  const fator = 1 - deltaY * 0.0015;
  pdfZoomFactor = Math.min(5, Math.max(0.3, pdfZoomFactor * fator));
  atualizarZoomLabel();
  agendarRedimensionamentoPdf();
  clearTimeout(pdfWheelDebounceTimer);
  pdfWheelDebounceTimer = setTimeout(aplicarNovoZoomPdf, 180);
}

// Reflui todas as páginas pro novo tamanho de zoom e re-renderiza a atual
// em nitidez; as demais são invalidadas e renderizam de novo ao entrarem
// na tela (via IntersectionObserver), evitando re-renderizar o PDF inteiro.
async function aplicarNovoZoomPdf() {
  if (!pdfDoc) return;
  const sessao = pdfSessaoAtual;

  redimensionarTodasPaginasPdf(); // já ancora o scroll na página atual
  pdfPaginas.forEach((p) => {
    if (p.pagina !== pdfCurrentPage) p.renderizado = false;
  });

  await renderizarPaginaPdfEm(pdfCurrentPage, sessao, true);
}

// Gira TODAS as páginas 90° (documento inteiro pode ter sido fotografado
// deitado). Reflui a pilha pro novo tamanho (largura/altura trocadas em
// 90°/270°) e invalida o render de todas as páginas, já que o próprio
// conteúdo do canvas muda de orientação — diferente do zoom, aqui não dá
// pra só invalidar "as outras".
async function aplicarNovaRotacaoPdf() {
  if (!pdfDoc) return;
  const sessao = pdfSessaoAtual;
  redimensionarTodasPaginasPdf();
  pdfPaginas.forEach((p) => {
    p.renderizado = false;
  });

  // Girar muda a ORIENTAÇÃO do conteúdo, não só a nitidez (diferente do
  // zoom) — uma página com o canvas desatualizado fica visivelmente
  // ERRADA (conteúdo em pé dentro de uma caixa já virada deitada), não só
  // borrada. Por isso força o re-render de TODAS as páginas aqui, em vez
  // de confiar só no IntersectionObserver — que não dispara de novo pra
  // uma página que já estava (e continua) visível, só reage quando ela
  // cruza a borda de interseção. A atual renderiza primeiro pra resposta
  // visual imediata; as outras seguem em ordem, em segundo plano.
  await renderizarPaginaPdfEm(pdfCurrentPage, sessao, true);
  for (const p of pdfPaginas) {
    if (sessao !== pdfSessaoAtual) return;
    if (p.pagina === pdfCurrentPage) continue;
    await renderizarPaginaPdfEm(p.pagina, sessao, true);
  }
}

function girarPdf() {
  pdfRotation = (pdfRotation + 90) % 360;
  aplicarNovaRotacaoPdf();
}

async function irParaPaginaPdf(n, comportamento = 'smooth') {
  if (!pdfDoc || n < 1 || n > pdfTotalPages) return;
  const info = pdfPaginas.find((p) => p.pagina === n);
  if (!info || !containerViewer) return;
  containerViewer.scrollTo({
    top: info.wrapper.offsetTop - 20,
    behavior: comportamento,
  });
  // Sem esperar o evento de scroll: com a seta segurada, a próxima tecla já
  // tem que partir da página nova.
  if (comportamento === 'instant') atualizarPaginaAtualPeloScroll();
}

async function gerarMiniaturasPdf(sessao) {
  if (!viewerThumbs) return;
  viewerThumbs.innerHTML = '';
  const THUMB_W = 143; // 30% maior que o tamanho original (110px)
  for (let i = 1; i <= pdfTotalPages; i++) {
    if (sessao !== pdfSessaoAtual) return;
    const page = await pdfDoc.getPage(i);
    const viewportBase = page.getViewport({ scale: 1 });
    const escalaThumb = THUMB_W / viewportBase.width;
    const dpr = window.devicePixelRatio || 1;
    const viewportThumb = page.getViewport({ scale: escalaThumb * dpr });

    const wrapper = document.createElement('div');
    wrapper.className = 'viewer-thumb';
    wrapper.dataset.pagina = String(i);
    // Trava a proporção real da página (retrato ou paisagem), como no navegador
    wrapper.style.aspectRatio = `${viewportBase.width} / ${viewportBase.height}`;
    const canvas = document.createElement('canvas');
    canvas.width = viewportThumb.width;
    canvas.height = viewportThumb.height;
    const numBadge = document.createElement('span');
    numBadge.className = 'thumb-num';
    numBadge.textContent = String(i);
    wrapper.appendChild(canvas);
    wrapper.appendChild(numBadge);
    wrapper.addEventListener('click', () => irParaPaginaPdf(i));
    viewerThumbs.appendChild(wrapper);

    const ctx = canvas.getContext('2d');
    try {
      await page.render({ canvasContext: ctx, viewport: viewportThumb })
        .promise;
    } catch (e) {
      /* ignora corrida ao trocar de arquivo durante a geração */
    }
  }
  if (sessao !== pdfSessaoAtual) return;
  atualizarMiniaturaAtivaPdf();
}

function atualizarMiniaturaAtivaPdf() {
  if (!viewerThumbs) return;
  viewerThumbs.querySelectorAll('.viewer-thumb').forEach((t) => {
    t.classList.toggle('ativo', Number(t.dataset.pagina) === pdfCurrentPage);
  });
  const ativa = viewerThumbs.querySelector('.viewer-thumb.ativo');
  if (ativa) ativa.scrollIntoView({ block: 'nearest' });
}

// ============================================================
// 🔎 BUSCA DE TEXTO NO PDF (estilo Ctrl+F)
// ============================================================

function normalizarBuscaPdf(s) {
  return (s || '')
    .normalize('NFD')
    .replace(/\p{Diacritic}/gu, '')
    .toLowerCase();
}

// Extrai o texto de TODAS as páginas em segundo plano (não depende de a
// página já ter sido renderizada visualmente) pra busca alcançar o PDF
// inteiro, não só o que já rolou na tela.
async function carregarTextoCompletoPdf(sessao) {
  pdfTextoPorPagina = [];
  pdfTextoCarregado = false;
  if (!pdfDoc) return;
  for (let i = 1; i <= pdfTotalPages; i++) {
    if (sessao !== pdfSessaoAtual) return;
    const page = await pdfDoc.getPage(i);
    const textContent = await page.getTextContent();
    if (sessao !== pdfSessaoAtual) return;
    pdfTextoPorPagina.push({
      pagina: i,
      itens: textContent.items.map((it) => it.str),
    });
  }
  if (sessao !== pdfSessaoAtual) return;
  pdfTextoCarregado = true;
  if (pdfSearchQuery.trim()) executarBuscaPdf(pdfSearchQuery);
}

function atualizarContadorBuscaPdf() {
  if (!viewerSearchCount) return;
  viewerSearchCount.textContent = pdfSearchMatches.length
    ? `${pdfSearchIndex + 1}/${pdfSearchMatches.length}`
    : '0/0';
}

function escapeHtmlBuscaPdf(s) {
  return s.replace(
    /[&<>]/g,
    (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;' })[c]
  );
}

// Desfaz as marcações (<mark>) de volta pra texto puro, em qualquer
// página. Só usado quando a busca muda de verdade (query nova ou
// limpa) — nunca depois de um render normal, porque aí o próprio render
// já reconstruiu a camada de texto do zero e não sobrou marcação velha.
function limparHighlightBuscaPdf() {
  document.querySelectorAll('.pdf-search-hit').forEach((mark) => {
    const pai = mark.parentNode;
    if (!pai) return;
    pai.replaceChild(document.createTextNode(mark.textContent), mark);
    pai.normalize();
  });
}

// Reset completo (arquivo novo ou visualizador fechado)
function limparBuscaPdf() {
  pdfTextoPorPagina = [];
  pdfTextoCarregado = false;
  pdfSearchQuery = '';
  pdfSearchMatches = [];
  pdfSearchIndex = -1;
  if (viewerSearchInput) viewerSearchInput.value = '';
  if (viewerSearchNav) viewerSearchNav.hidden = true;
  if (btnViewerSearchClear) btnViewerSearchClear.hidden = true;
  limparHighlightBuscaPdf();
}

function executarBuscaPdf(query) {
  pdfSearchQuery = query;
  limparHighlightBuscaPdf(); // tira marcação da busca anterior, se tinha
  const q = normalizarBuscaPdf(query.trim());
  pdfSearchMatches = [];
  if (q && pdfTextoCarregado) {
    pdfTextoPorPagina.forEach(({ pagina, itens }) => {
      let ordinal = 0;
      itens.forEach((str) => {
        if (normalizarBuscaPdf(str).includes(q)) {
          pdfSearchMatches.push({ pagina, ordinal });
          ordinal++;
        }
      });
    });
  }
  pdfSearchIndex = pdfSearchMatches.length ? 0 : -1;
  atualizarContadorBuscaPdf();
  if (viewerSearchNav) viewerSearchNav.hidden = query.trim() === '';
  if (pdfSearchIndex >= 0) irParaResultadoBuscaPdf();
}

// Marca, dentro de UMA página já renderizada, só o TRECHO que bate com a
// busca — não o item de texto inteiro (um item do PDF costuma ser a linha
// toda, tipo "MARCO ANTONIO DE OLIVEIRA"; buscar "marco" não pode pintar
// o nome inteiro, só o pedaço digitado). ordinalAtual: a Nª ocorrência
// desta página que é a "atual" (-1 se nenhuma for). Retorna o elemento
// <mark> atual, se algum ficou marcado nesta página.
function destacarOcorrenciasNaPagina(pagina, ordinalAtual) {
  const info = pdfPaginas.find((p) => p.pagina === pagina);
  if (!info) return null;
  const query = pdfSearchQuery.trim();
  if (!query) return null;
  const qNorm = normalizarBuscaPdf(query);
  let ordinal = 0;
  let elAtual = null;
  Array.from(info.textLayer.children).forEach((span) => {
    const original = span.textContent;
    const normalizado = normalizarBuscaPdf(original);
    if (!normalizado.includes(qNorm)) return;
    let idx = 0;
    let ultimo = 0;
    let out = '';
    while ((idx = normalizado.indexOf(qNorm, idx)) !== -1) {
      const ehAtual = ordinal === ordinalAtual;
      out += escapeHtmlBuscaPdf(original.slice(ultimo, idx));
      out +=
        '<mark class="pdf-search-hit' +
        (ehAtual ? ' pdf-search-hit-atual' : '') +
        '">' +
        escapeHtmlBuscaPdf(original.slice(idx, idx + qNorm.length)) +
        '</mark>';
      ordinal++;
      ultimo = idx + qNorm.length;
      idx = ultimo;
    }
    out += escapeHtmlBuscaPdf(original.slice(ultimo));
    span.innerHTML = out;
    if (!elAtual) elAtual = span.querySelector('.pdf-search-hit-atual');
  });
  return elAtual;
}

// Reaplica a busca ativa numa página que acabou de (re)renderizar. Chamado
// tanto pela navegação explícita (aí sim centraliza a tela no resultado)
// quanto automaticamente todo render (zoom, giro, ou rolar até uma página
// nova) — sem rolar a tela nesse caso, que é só reposição passiva do
// destaque, não o usuário pedindo pra ir a algum lugar.
function reaplicarBuscaNaPagina(pagina, opts) {
  if (!pdfSearchQuery.trim() || !pdfSearchMatches.length) return;
  const matchAtual = pdfSearchMatches[pdfSearchIndex];
  const ordinalAtual =
    matchAtual && matchAtual.pagina === pagina ? matchAtual.ordinal : -1;
  const elAtual = destacarOcorrenciasNaPagina(pagina, ordinalAtual);
  if (opts && opts.centralizarNaTela && elAtual) {
    elAtual.scrollIntoView({ block: 'center', behavior: 'smooth' });
  }
}

async function irParaResultadoBuscaPdf() {
  const match = pdfSearchMatches[pdfSearchIndex];
  if (!match) return;
  const sessao = pdfSessaoAtual;
  await irParaPaginaPdf(match.pagina);
  await renderizarPaginaPdfEm(match.pagina, sessao);
  if (sessao !== pdfSessaoAtual) return;
  reaplicarBuscaNaPagina(match.pagina, { centralizarNaTela: true });
}

function irParaOcorrenciaBusca(delta) {
  if (!pdfSearchMatches.length) return;
  pdfSearchIndex =
    (pdfSearchIndex + delta + pdfSearchMatches.length) %
    pdfSearchMatches.length;
  atualizarContadorBuscaPdf();
  irParaResultadoBuscaPdf();
}

function atualizarBotaoLimparBusca() {
  if (!btnViewerSearchClear || !viewerSearchInput) return;
  btnViewerSearchClear.hidden = viewerSearchInput.value === '';
}

if (viewerSearchInput) {
  viewerSearchInput.addEventListener('input', () => {
    executarBuscaPdf(viewerSearchInput.value);
    atualizarBotaoLimparBusca();
  });
  viewerSearchInput.addEventListener('keydown', (e) => {
    if (e.key === 'Enter') {
      e.preventDefault();
      irParaOcorrenciaBusca(e.shiftKey ? -1 : 1);
    } else if (e.key === 'Escape') {
      if (viewerSearchInput.value) {
        e.stopPropagation(); // some com a busca antes de fechar o visualizador
        viewerSearchInput.value = '';
        executarBuscaPdf('');
        atualizarBotaoLimparBusca();
      }
    }
  });
}
if (btnViewerSearchNext)
  btnViewerSearchNext.addEventListener('click', () => irParaOcorrenciaBusca(1));
if (btnViewerSearchPrev)
  btnViewerSearchPrev.addEventListener('click', () =>
    irParaOcorrenciaBusca(-1)
  );
if (btnViewerSearchClear) {
  btnViewerSearchClear.addEventListener('click', () => {
    viewerSearchInput.value = '';
    executarBuscaPdf('');
    atualizarBotaoLimparBusca();
    viewerSearchInput.focus();
  });
}

// Ctrl/Cmd+F: em vez de abrir a busca nativa do navegador (que não acha
// nada útil atrás do visualizador em tela cheia), foca e seleciona o
// texto da busca do PDF, do jeito que Ctrl+F normalmente se comporta.
document.addEventListener('keydown', (e) => {
  if (!viewerOverlay || !viewerOverlay.classList.contains('ativo')) return;
  if (!pdfAtivo()) return;
  if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'f') {
    e.preventDefault();
    viewerSearchInput.focus();
    viewerSearchInput.select();
  }
});

async function abrirPdf(url) {
  pdfSessaoAtual += 1;
  const sessao = pdfSessaoAtual;
  clearTimeout(pdfWheelDebounceTimer);
  if (pdfResizeRaf) {
    cancelAnimationFrame(pdfResizeRaf);
    pdfResizeRaf = null;
  }
  if (pdfObserver) {
    pdfObserver.disconnect();
    pdfObserver = null;
  }
  pdfDoc = null;
  pdfPaginas = [];
  pdfCurrentPage = 1;
  pdfTotalPages = 1;
  pdfBaseScale = 1;
  pdfZoomFactor = 1;
  pdfRotation = 0;
  limparBuscaPdf();
  if (viewerPdfWrapper) viewerPdfWrapper.innerHTML = '';
  if (viewerThumbs) viewerThumbs.innerHTML = '';
  // Miniaturas sempre aparecem para PDF, tenha 1 página ou várias
  if (viewerSideRail) viewerSideRail.classList.remove('hidden');
  atualizarPageNavUI(1, 1);

  try {
    const doc = await pdfjsLib.getDocument(url).promise;
    if (sessao !== pdfSessaoAtual) return;
    pdfDoc = doc;
    pdfTotalPages = doc.numPages;
    atualizarPageNavUI(1, pdfTotalPages);
    carregarTextoCompletoPdf(sessao); // roda em segundo plano, não trava a exibição

    const primeiraPagina = await doc.getPage(1);
    const viewportBase1 = primeiraPagina.getViewport({ scale: 1 });
    const containerRect = retanguloLocal(containerViewer);
    const targetW = Math.min(containerRect.width * 0.62, 900);
    const targetH = containerRect.height * 0.9;
    pdfBaseScale = Math.min(
      targetW / viewportBase1.width,
      targetH / viewportBase1.height
    );

    criarPaginaPdfDom(1, viewportBase1);
    for (let i = 2; i <= pdfTotalPages; i++) {
      if (sessao !== pdfSessaoAtual) return;
      const page = await doc.getPage(i);
      criarPaginaPdfDom(i, page.getViewport({ scale: 1 }));
    }
    if (sessao !== pdfSessaoAtual) return;

    configurarObserverPdf(sessao);
    // Só agora existem páginas pra recortar: liga a dica do "Ctrl + arrastar"
    ocrSincronizarUI();
    await renderizarPaginaPdfEm(1, sessao);
    if (sessao !== pdfSessaoAtual) return;
    gerarMiniaturasPdf(sessao);
  } catch (err) {
    console.error('Erro ao carregar PDF no visualizador:', err);
  }
}

if (btnViewerPagePrev)
  btnViewerPagePrev.addEventListener('click', () =>
    irParaPaginaPdf(pdfCurrentPage - 1)
  );
if (btnViewerPageNext)
  btnViewerPageNext.addEventListener('click', () =>
    irParaPaginaPdf(pdfCurrentPage + 1)
  );

// --- FUNÇÕES GLOBAIS DE ABERTURA ---
window.abrirVisualizador = async function (
  url,
  tipo,
  nomeArquivo = 'Arquivo',
  dataEnvio = '',
  corBadge = '#8e44ad',
  extBadge = 'MÍDIA',
  remetente = ''
) {
  if (!url) return;
  // Foto de perfil (cliente/vendedor): visualização simples e quadrada,
  // sem download, sem badge de tipo de arquivo e sem zoom/página
  if (viewerOverlay) {
    viewerOverlay.classList.toggle('modo-avatar', tipo === 'avatar');
    viewerOverlay.classList.toggle('modo-pdf', tipo === 'pdf');
  }
  limparBuscaPdf();
  // ← / → entre as mídias da conversa: só quando abriu por um cartão do chat
  montarNavegacaoViewer(tipo, url);
  // 👇 MÁGICA DO AUTO-DETECT DE EXTENSÃO 👇
  if (extBadge === 'MÍDIA') {
    const extensaoDescoberta = url.split('.').pop().toLowerCase();
    extBadge = extensaoDescoberta.toUpperCase();
    if (typeof getIconeArquivo === 'function') {
      const infoArquivo = getIconeArquivo(extensaoDescoberta);
      if (infoArquivo && infoArquivo.cor) corBadge = infoArquivo.cor;
    }
  }
  // 👇 MÁGICA 2: HIGIENIZAÇÃO DO NOME DO ARQUIVO (IGUALZINHO À THUMBNAIL) 👇
  let nomeLimpoVisual = nomeArquivo;
  // 1. Decodifica a URL para manter os parênteses () originais intactos
  if (nomeLimpoVisual === 'Arquivo' || !nomeLimpoVisual) {
    nomeLimpoVisual = decodeURIComponent(url.split('/').pop());
  } else {
    nomeLimpoVisual = decodeURIComponent(nomeLimpoVisual);
  }
  // 2. Tira APENAS "env_XYZ_" ou "rec_XYZ_" gerados pelo sistema
  nomeLimpoVisual = nomeLimpoVisual
    .replace(/^env_\d+_/i, '')
    .replace(/^rcs_\d+_/i, '')
    .replace(/^camp_\d+_[a-z0-9]+_/i, '')
    .replace(/^rec_\d+_[a-z0-9]+_/i, '');
  // 3. Tira a extensão (.pdf, .jpg, etc) se ainda estiver lá
  const temExtensao = nomeLimpoVisual.lastIndexOf('.');
  if (temExtensao !== -1) {
    nomeLimpoVisual = nomeLimpoVisual.substring(0, temExtensao);
  }
  // 4. Se ficou vazio, dá um fallback
  if (!nomeLimpoVisual || nomeLimpoVisual.trim() === '')
    nomeLimpoVisual = 'Documento';
  // Reseta visualização anterior
  if (viewerImg) {
    viewerImg.classList.add('hidden');
    viewerImg.src = '';
  }
  if (viewerVideo) {
    viewerVideo.classList.add('hidden');
    viewerVideo.src = '';
  }

  if (viewerPdfWrapper) {
    viewerPdfWrapper.classList.add('hidden');
    viewerPdfWrapper.innerHTML = '';
  }
  if (containerViewer) containerViewer.classList.remove('content-pdf-mode');
  // Fecha qualquer sessão de PDF anterior (cancela renders/miniaturas em andamento)
  clearTimeout(pdfWheelDebounceTimer);
  if (pdfResizeRaf) {
    cancelAnimationFrame(pdfResizeRaf);
    pdfResizeRaf = null;
  }
  pdfSessaoAtual += 1;
  pdfDoc = null;
  pdfPaginas = [];
  if (pdfObserver) {
    pdfObserver.disconnect();
    pdfObserver = null;
  }
  // Miniaturas só existem para PDF; imagem/vídeo não têm páginas
  if (viewerSideRail) viewerSideRail.classList.add('hidden');
  if (viewerThumbs) viewerThumbs.innerHTML = '';
  atualizarPageNavUI(1, 1);

  // 👇 INJETA OS DADOS VISUAIS NO TOPO DA TELA 👇
  const elName = document.getElementById('viewer-file-name');
  const elDate = document.getElementById('viewer-file-date');
  const elBadge = document.getElementById('viewer-file-badge');

  // Cabeçalho mostra só o nome do arquivo — sem identificar quem mandou
  if (elName) elName.textContent = nomeLimpoVisual;

  if (elDate) {
    elDate.textContent = dataEnvio;
    elDate.style.display = dataEnvio ? 'block' : 'none';
  }
  // Selo do tipo de arquivo: dentro da busca no modo PDF, sozinho nos
  // outros modos (mesmo texto/cor nos dois, só muda onde aparece)
  if (elBadge) {
    elBadge.textContent = extBadge;
    elBadge.style.setProperty('--badge-dot', corBadge);
  }
  if (viewerFileBadgeSolo) {
    viewerFileBadgeSolo.textContent = extBadge;
    viewerFileBadgeSolo.style.setProperty('--badge-dot', corBadge);
  }
  const elAvatarNome = document.getElementById('viewer-avatar-nome');
  if (elAvatarNome)
    elAvatarNome.textContent = tipo === 'avatar' ? remetente : '';
  // Configura botão de download com nome limpo
  if (btnDownloadViewer) {
    let nomeParaBaixar = url
      .split('/')
      .pop()
      .replace(/^env_\d+_/i, '')
      .replace(/^rcs_\d+_/i, '')
      .replace(/^camp_\d+_[a-z0-9]+_/i, '')
      .replace(/^rec_\d+_[a-z0-9]+_/i, '');
    btnDownloadViewer.onclick = (e) => {
      e.stopPropagation();
      forceDownload(url, nomeParaBaixar);
    };
  }
  // Exibe o overlay
  if (viewerOverlay) {
    viewerOverlay.classList.remove('hidden');
    setTimeout(() => {
      viewerOverlay.classList.add('ativo');
      // Liga/desliga a dica do "Ctrl + arrastar" conforme o tipo de mídia
      ocrSincronizarUI();
    }, 10);
  }
  // 📸 SE FOR IMAGEM (ou foto de perfil, que usa o mesmo <img> em modo simples)
  if ((tipo === 'imagem' || tipo === 'avatar') && viewerImg) {
    viewerImg.onload = () => {
      if (tipo === 'avatar') centralizarAvatar(viewerImg);
      else centralizarImagem(viewerImg);
    };
    viewerImg.src = url;
    viewerImg.classList.remove('hidden');
  }
  // 🎥 SE FOR VÍDEO
  else if (tipo === 'video' && viewerVideo) {
    viewerVideo.onloadedmetadata = () => {
      centralizarImagem(viewerVideo);
    };
    viewerVideo.src = url;
    viewerVideo.classList.remove('hidden');
  }

  // 📄 SE FOR PDF
  else if (tipo === 'pdf' && viewerPdfWrapper) {
    containerViewer.classList.add('content-pdf-mode');
    viewerPdfWrapper.classList.remove('hidden');
    abrirPdf(url);
  }
};
function fecharVisualizador() {
  if (!viewerOverlay) return;
  viewerOverlay.classList.remove('ativo');
  ocrSincronizarUI();
  viewerNavAtivo = false;
  viewerNavUrlAtual = '';
  desenharTiraMiniaturasViewer([], -1);
  // Pausa vídeo se estiver tocando
  if (viewerVideo && !viewerVideo.paused) {
    viewerVideo.pause();
  }
  setTimeout(() => {
    if (viewerImg) viewerImg.src = '';
    if (viewerVideo) viewerVideo.src = '';
    viewerOverlay.classList.add('hidden');
    viewerOverlay.classList.remove('modo-avatar', 'modo-pdf');
    scale = 1; // Reseta zoom
    rotation = 0;
    pdfRotation = 0;
    limparBuscaPdf();
    clearTimeout(pdfWheelDebounceTimer);
    if (pdfResizeRaf) {
      cancelAnimationFrame(pdfResizeRaf);
      pdfResizeRaf = null;
    }
    pdfSessaoAtual += 1; // Cancela renders/miniaturas de PDF pendentes
    pdfDoc = null;
    pdfPaginas = [];
    if (pdfObserver) {
      pdfObserver.disconnect();
      pdfObserver = null;
    }
    if (viewerPdfWrapper) viewerPdfWrapper.innerHTML = '';
    if (viewerThumbs) viewerThumbs.innerHTML = '';
  }, 300);
}
// Eventos de Fechar e Girar
if (btnCloseViewer)
  btnCloseViewer.addEventListener('click', fecharVisualizador);
// Fechar ao clicar no fundo escuro (fora da imagem)
if (viewerOverlay) {
  viewerOverlay.addEventListener('mousedown', (e) => {
    // Se o clique for exatamente no fundo escuro ou no container invisível, e não na imagem.
    // No PDF o container é a área da página: clicar ali não fecha (é onde as
    // setas passam a rolar, e é também a barra de rolagem).
    if (
      e.target === viewerOverlay ||
      (e.target === containerViewer && !pdfAtivo())
    ) {
      fecharVisualizador();
    }
  });
}
document.addEventListener('keydown', (e) => {
  if (viewerOverlay && viewerOverlay.classList.contains('ativo')) {
    // 1. Fechar com ESC (funciona em qualquer modo)
    if (e.key === 'Escape') {
      fecharVisualizador();
      return;
    }
    // Foto de perfil (avatar): visualização simples, sem girar/zoom
    if (viewerOverlay.classList.contains('modo-avatar')) return;
    // Não rouba a seta de quem está digitando (busca do PDF, texto do OCR).
    const alvo = e.target;
    const digitando =
      alvo &&
      (alvo.tagName === 'INPUT' ||
        alvo.tagName === 'TEXTAREA' ||
        alvo.isContentEditable);
    // ↑ / ↓ no PDF, igual ao leitor do Chrome: depois de um clique na régua de
    // miniaturas trocam de página; no resto, são a rolagem do próprio navegador
    // (o foco na área da página é o que faz ela valer logo ao abrir o arquivo).
    if (
      (e.key === 'ArrowUp' || e.key === 'ArrowDown') &&
      pdfAtivo() &&
      !digitando &&
      !e.ctrlKey &&
      !e.altKey &&
      !e.metaKey
    ) {
      if (viewerSideRail && viewerSideRail.contains(document.activeElement)) {
        e.preventDefault();
        irParaPaginaPdf(
          pdfCurrentPage + (e.key === 'ArrowDown' ? 1 : -1),
          'instant'
        );
      } else if (!containerViewer.contains(document.activeElement)) {
        containerViewer.focus({ preventScroll: true });
      }
      return;
    }
    // ← / → (sem Tab, que é o girar): mídia anterior / seguinte da conversa.
    if (
      (e.key === 'ArrowLeft' || e.key === 'ArrowRight') &&
      !isTabPressed &&
      !e.ctrlKey &&
      !e.altKey &&
      !e.metaKey
    ) {
      if (!digitando && viewerNavAtivo) {
        e.preventDefault();
        if (!e.repeat) navegarMidiaViewer(e.key === 'ArrowRight' ? 1 : -1);
        return;
      }
    }
    if (!canRotate) return;
    if (e.key === 'Tab') {
      e.preventDefault();
      isTabPressed = true;
    }
    // 2. MÁGICA: Girar a imagem (Apenas se for imagem, não afeta vídeos)
    if (viewerImg && !viewerImg.classList.contains('hidden')) {
      if (isTabPressed && e.key === 'ArrowRight') {
        e.preventDefault();
        girarImagemViewer(1);
        canRotate = false;
      } else if (isTabPressed && e.key === 'ArrowLeft') {
        e.preventDefault();
        girarImagemViewer(-1);
        canRotate = false;
      }
    }
  }
});
document.addEventListener('keyup', (e) => {
  if (e.key === 'Tab') {
    isTabPressed = false;
  }
  if (e.key === 'ArrowRight' || e.key === 'ArrowLeft') {
    canRotate = true;
  }
});

// --- NAVEGAÇÃO ENTRE AS MÍDIAS DA CONVERSA (← / → do teclado) ---
// Pedido das vendedoras: antes era abrir, fechar e abrir a próxima. Agora,
// com o visualizador aberto por um cartão do chat, ← e → pulam pra mídia
// anterior/seguinte da conversa (foto, vídeo ou documento), na ordem em
// que aparecem na tela.
//
// Não existe uma segunda lista de mídias: a navegação lê, na hora, os
// cartões que já têm onclick="abrirVisualizador(...)" dentro do
// #container-mensagens e dispara o MESMO onclick do cartão vizinho. Assim o
// que abre é idêntico ao clique (nome, data, selo, remetente), e cartões
// que chegam/saem da tela (novas mensagens, rolagem) entram sozinhos.
const viewerNavContador = document.getElementById('viewer-nav-contador');
const viewerNavTira = document.getElementById('viewer-nav-tira');
let viewerNavAtivo = false; // abriu por um cartão do chat?
let viewerNavUrlAtual = ''; // url da mídia aberta, na forma que está no onclick
let viewerNavOrigem = null; // cartão clicado (preenchido na fase de captura)

// Guarda qual cartão foi clicado ANTES do onclick dele rodar (captura roda
// antes da bolha), pra abrirVisualizador saber de onde veio a abertura.
document.addEventListener(
  'click',
  (e) => {
    const alvo =
      e.target && e.target.closest
        ? e.target.closest('[onclick*="abrirVisualizador"]')
        : null;
    viewerNavOrigem = alvo && alvo.closest('#container-mensagens') ? alvo : null;
  },
  true
);

// Primeiro argumento do onclick="abrirVisualizador('...', ...)" do cartão,
// já sem as entidades HTML (getAttribute devolve decodificado).
function urlDoCartaoViewer(el) {
  const m = /abrirVisualizador\(\s*'((?:\\.|[^'\\])*)'/.exec(
    (el && el.getAttribute('onclick')) || ''
  );
  return m ? m[1] : '';
}

// Desfaz o escape de JS (\' e \\) pra comparar com a url "crua"
function urlCruaDoCartaoViewer(el) {
  return urlDoCartaoViewer(el).replace(/\\(.)/g, '$1');
}

// Cartões de mídia da conversa, na ordem da tela, um por arquivo
function listarMidiasDaConversa() {
  const escopo = document.getElementById('container-mensagens');
  if (!escopo) return [];
  const vistos = new Set();
  const lista = [];
  escopo.querySelectorAll('[onclick*="abrirVisualizador"]').forEach((el) => {
    const u = urlDoCartaoViewer(el);
    if (!u || vistos.has(u)) return;
    vistos.add(u);
    lista.push(el);
  });
  return lista;
}

function indiceMidiaAtual(lista) {
  return lista.findIndex((el) => urlDoCartaoViewer(el) === viewerNavUrlAtual);
}

function atualizarNavegacaoViewerUI() {
  let idx = -1;
  let total = 0;
  let lista = [];
  if (viewerNavAtivo) {
    lista = listarMidiasDaConversa();
    total = lista.length;
    idx = indiceMidiaAtual(lista);
  }
  const temLista = viewerNavAtivo && idx !== -1 && total > 1;
  if (viewerNavContador)
    viewerNavContador.textContent = temLista ? `${idx + 1} de ${total}` : '';
  desenharTiraMiniaturasViewer(temLista ? lista : [], idx);
}

// Miniatura de um cartão do chat: a foto/primeiro quadro do vídeo/preview
// do PDF que o próprio cartão já mostra; sem nada disso, o ícone do tipo.
function miniaturaDoCartaoViewer(el) {
  const mini = document.createElement('div');
  mini.className = 'viewer-nav-mini';
  const img = el.querySelector('img');
  const video = el.querySelector('video');
  if (img && img.getAttribute('src')) {
    const i = document.createElement('img');
    i.src = img.getAttribute('src');
    i.alt = '';
    mini.appendChild(i);
  } else if (video && video.getAttribute('src')) {
    mini.classList.add('tem-video');
    const v = document.createElement('video');
    v.src = video.getAttribute('src');
    v.muted = true;
    v.preload = 'metadata';
    mini.appendChild(v);
    const play = document.createElement('span');
    play.className = 'viewer-nav-mini-play';
    play.innerHTML = '<i class="fas fa-play"></i>';
    mini.appendChild(play);
  } else {
    const ext = urlCruaDoCartaoViewer(el).split('.').pop().toLowerCase();
    const info = getIconeArquivo(ext);
    const ic = document.createElement('i');
    ic.className = (info && info.icone) || 'fas fa-file';
    if (info && info.cor) ic.style.color = info.cor;
    mini.appendChild(ic);
  }
  return mini;
}

// Tira estilo WhatsApp: janela de 5 em volta da atual. Nas pontas da lista
// a janela encosta (0..4 ou n-5..n-1) e a atual fica no canto, ainda grande.
let viewerNavTiraCache = new Map(); // cartão -> miniatura (não recria a cada troca)
function desenharTiraMiniaturasViewer(lista, idx) {
  if (!viewerNavTira) return;
  if (!lista.length || idx === -1) {
    viewerNavTira.hidden = true;
    viewerNavTira.innerHTML = '';
    viewerNavTiraCache = new Map();
    return;
  }
  const total = lista.length;
  const inicio = Math.max(0, Math.min(idx - 2, total - 5));
  const fim = Math.min(total, inicio + 5);
  const novoCache = new Map();
  viewerNavTira.innerHTML = '';
  for (let i = inicio; i < fim; i++) {
    const el = lista[i];
    const mini = viewerNavTiraCache.get(el) || miniaturaDoCartaoViewer(el);
    novoCache.set(el, mini);
    const d = Math.min(2, Math.abs(i - idx));
    mini.classList.remove('d0', 'd1', 'd2');
    mini.classList.add(`d${d}`);
    mini.title = d === 0 ? 'Mídia atual' : `Mídia ${i + 1} de ${total}`;
    mini.onclick = (e) => {
      e.stopPropagation();
      if (d !== 0) irParaMidiaViewer(i);
    };
    viewerNavTira.appendChild(mini);
  }
  viewerNavTiraCache = novoCache;
  viewerNavTira.hidden = false;
}

// Chamada no abrirVisualizador: decide se esta abertura veio de um cartão
// do chat (aí liga ← / →) ou de outro lugar (galeria, foto de perfil...)
function montarNavegacaoViewer(tipo, url) {
  const origem = viewerNavOrigem;
  viewerNavOrigem = null;
  viewerNavAtivo = false;
  viewerNavUrlAtual = '';
  // O cartão clicado precisa ser mesmo o desta url (um clique no "baixar"
  // de dentro do cartão também passa pela captura, mas não abre nada)
  if (tipo !== 'avatar' && origem && urlCruaDoCartaoViewer(origem) === url) {
    viewerNavAtivo = true;
    viewerNavUrlAtual = urlDoCartaoViewer(origem);
  }
  atualizarNavegacaoViewerUI();
}

function navegarMidiaViewer(delta) {
  if (!viewerNavAtivo) return;
  const lista = listarMidiasDaConversa();
  const atual = indiceMidiaAtual(lista);
  if (atual === -1) {
    atualizarNavegacaoViewerUI();
    return;
  }
  irParaMidiaViewer(atual + delta, lista);
}

// Abre a mídia de índice `indice` da conversa (teclado ou miniatura)
function irParaMidiaViewer(indice, lista) {
  if (!viewerNavAtivo) return;
  if (!viewerOverlay || !viewerOverlay.classList.contains('ativo')) return;
  if (!lista) lista = listarMidiasDaConversa();
  const alvo = lista[indice];
  if (!alvo || typeof alvo.onclick !== 'function') return;
  // Dispara o onclick do cartão direto, sem bolha: um clique de verdade
  // subiria até o balão e poderia marcar/rolar a mensagem.
  viewerNavOrigem = alvo;
  alvo.onclick.call(alvo, new MouseEvent('click'));
}

// Função Auxiliar Download
function forceDownload(url, filename) {
  fetch(url)
    .then((response) => response.blob())
    .then((blob) => {
      const link = document.createElement('a');
      link.href = URL.createObjectURL(blob);
      link.download = filename;
      document.body.appendChild(link);
      link.click();
      document.body.removeChild(link);
    })
    .catch(console.error);
}
// Clique na citação (reply): sobe até a mensagem original e pisca, como no
// WhatsApp. Antes só achava a ENVIADA (o `data-wpp-id` mora no tique) e o
// destaque ia pro `.msg-status`, que não fica dentro do balão.
window.scrollToMsg = function (msgId) {
  if (!msgId) return;
  const id = CSS.escape(String(msgId));
  const linha =
    document.querySelector(`.msg-row[data-msg-id="${id}"]`) ||
    document.querySelector(`[data-wpp-id="${id}"]`)?.closest('.msg-row');
  if (!linha) {
    // Citação de mensagem que não está nesta conversa (outro chip, ou já
    // apagada na limpeza do banco).
    if (typeof window.mostrarAlerta === 'function')
      window.mostrarAlerta('A mensagem original não está nesta conversa.', 'info');
    return;
  }
  linha.scrollIntoView({ behavior: 'smooth', block: 'center' });
  const peca = linha.querySelector('.msg-bubble') || linha;
  peca.classList.remove('msg-focada');
  void peca.offsetWidth; // reinicia a animação se clicar de novo
  peca.classList.add('msg-focada');
  // 3,2s = os três pulsos de 1s do `.msg-focada` (pag-bate-papo.css) + folga.
  setTimeout(() => peca.classList.remove('msg-focada'), 3200);
};
// Formatação de Telefone
function formatarTelefoneBR(numero) {
  let limpo = numero.replace(/\D/g, '');
  if (!limpo.startsWith('55')) limpo = '55' + limpo;
  if (limpo.length === 13) {
    return limpo.replace(/(\d{2})(\d{2})(\d{5})(\d{4})/, '+$1 ($2) $3-$4');
  } else if (limpo.length === 12) {
    return limpo.replace(/(\d{2})(\d{2})(\d{4})(\d{4})/, '+$1 ($2) $3-$4');
  }
  return '+' + limpo;
}
// Nome do cliente pro visualizador de foto de perfil: usa o nome do CRM
// se tiver, senão cai pro telefone formatado (+DDI (DDD) Número)
function nomeOuTelefoneCliente(chatObj) {
  if (!chatObj) return '';
  const telefone = (chatObj.chat_id || '').replace(/\D/g, '');
  const nome = chatObj.crm_nome || chatObj.name || '';
  if (!nome || nome === telefone || nome.includes('@c.us')) {
    return formatarTelefoneBR(telefone);
  }
  return nome;
}
// Nome real do vendedor pro visualizador de foto de perfil (a foto em si
// fica salva pelo username, mas o nome mostrado deve ser o de verdade).
// Guarda a PROMISE em andamento (não só o resultado) pra quem chamar
// enquanto o fetch ainda está no ar espere o mesmo fetch em vez de cair
// no fallback do username antes da hora.
let mapaNomesVendedores = null;
let promiseMapaNomesVendedores = null;
function carregarMapaNomesVendedores() {
  if (mapaNomesVendedores) return Promise.resolve(mapaNomesVendedores);
  if (promiseMapaNomesVendedores) return promiseMapaNomesVendedores;
  promiseMapaNomesVendedores = (async () => {
    const mapa = {};
    try {
      const res = await fetch('/api/team/vendedores', {
        headers: { Authorization: `Bearer ${localStorage.getItem('token')}` },
      });
      const vendedores = await res.json();
      vendedores.forEach((v) => {
        if (v.username) mapa[v.username] = v.name || v.username;
      });
    } catch (e) {
      console.error('Erro ao carregar nomes de vendedores', e);
    }
    mapaNomesVendedores = mapa;
    return mapa;
  })();
  return promiseMapaNomesVendedores;
}
// Resolve o nome do vendedor NA HORA DO CLIQUE (não na hora de renderizar a
// lista) — assim nunca fica preso mostrando o username porque o fetch ainda
// não tinha terminado quando a linha da lista foi desenhada.
window.abrirAvatarVendedor = async function (url, username) {
  const mapa = await carregarMapaNomesVendedores();
  const nome = (mapa && mapa[username]) || username;
  window.abrirVisualizador(url, 'avatar', '', '', '', '', nome);
};

// ============================================================
// FOTINHA AO LADO DOS BALÕES (estilo grupo do WhatsApp)
// ============================================================
// Só o PRIMEIRO balão de cada sequência do mesmo remetente leva a foto; os
// seguintes ficam alinhados embaixo, sem foto. Quem decide a sequência é a
// `chave` (o que o olho vê): dois robôs diferentes seguidos são a mesma foto,
// então contam como um só.
//
// Mensagem nossa não guarda o login de quem mandou, só o NOME exibido
// (mensagens.nome_remetente), e nome antigo é mais curto que o do cadastro
// ("Sarah Nathalie" x "Sarah Nathalie Calado de Oliveira"). Por isso o nome
// casa com o usuário também por pedaço, desde que só UM usuário sirva.
//
// Desempenho: o HTML de cada remetente é montado uma vez e guardado; a foto
// da equipe é o <login>.webp padronizado em 450x450 pelo servidor (~6-25KB;
// antes subia qualquer tamanho, chegou uma de 3508px); e a mesma URL repetida
// em vários balões baixa e decodifica uma vez só.
const REMETENTES_ROBO = new Set([
  '',
  'sistema',
  'agente ia',
  'agente externo',
  'automacao',
]);
const cacheAvatarRemetente = new Map();
let equipeParaAvatar = null; // [{ login, partes }]
const loginsSemFoto = new Set();

function partesDoNome(nome) {
  return String(nome || '')
    .normalize('NFD')
    .replace(/[̀-ͯ]/g, '')
    .toLowerCase()
    .trim()
    .split(/\s+/)
    .filter(Boolean);
}

function loginDoRemetente(nome) {
  if (!equipeParaAvatar) return null;
  const partes = partesDoNome(nome);
  if (!partes.length) return null;
  const inteiro = partes.join(' ');
  const exato = equipeParaAvatar.find((u) => u.partes.join(' ') === inteiro);
  if (exato) return exato.login;
  const candidatos = equipeParaAvatar.filter(
    (u) => u.partes[0] === partes[0] && partes.every((p) => u.partes.includes(p))
  );
  return candidatos.length === 1 ? candidatos[0].login : null;
}

// Chamada antes de desenhar a conversa. A lista da equipe é a mesma do
// visualizador de foto (um fetch só pra página toda).
async function prepararAvatarsEquipe() {
  if (equipeParaAvatar) return;
  const mapa = await carregarMapaNomesVendedores();
  const logins = Object.keys(mapa || {});
  if (!logins.length) return; // fetch falhou: tenta de novo na próxima conversa
  equipeParaAvatar = logins.map((login) => ({
    login,
    partes: partesDoNome(mapa[login]),
  }));
  // O que foi desenhado antes da equipe chegar saiu com a inicial.
  cacheAvatarRemetente.clear();
}

// A foto do vendedor não existe (404): troca TODAS as dele na tela pela
// inicial e não pede mais essa URL nesta página.
window.fotoEquipeFalhou = function (img) {
  const login = img.dataset.login;
  loginsSemFoto.add(login);
  cacheAvatarRemetente.clear();
  document
    .querySelectorAll(`.msg-avatar[data-login="${CSS.escape(login)}"]`)
    .forEach((el) => {
      el.textContent = el.dataset.inicial || '?';
    });
};

// Foto de cliente que expirou/não abre: fica a inicial, igual à lista.
window.fotoAvatarBalaoFalhou = function (img) {
  const caixa = img.parentElement;
  if (caixa) caixa.textContent = caixa.dataset.inicial || '?';
};

function htmlAvatarIcone(classe, arquivo, titulo) {
  return `<span class="msg-avatar msg-avatar-icone ${classe}" title="${escaparHtmlChat(titulo)}"><img src="/assets/${arquivo}" alt="" decoding="async"></span>`;
}

// { chave, html } de quem mandou a mensagem.
function avatarDoRemetente(msg, chat) {
  if (!msg.is_from_me) {
    const numeroLimpo = String((chat && chat.chat_id) || '')
      .replace('@c.us', '')
      .replace('55', '');
    // Mesma regra da lista lateral: nome do CRM, senão o nome do WhatsApp,
    // senão o número (e aí a "inicial" é o primeiro dígito).
    const nome = String(
      (chat && (chat.crm_nome || chat.name)) || numeroLimpo
    ).replace('@c.us', '');
    const foto = chat && chat.profile_pic_url;
    const chaveCache = `cliente|${nome}|${foto || ''}`;
    let pronto = cacheAvatarRemetente.get(chaveCache);
    if (!pronto) {
      const inicial = escaparHtmlChat(nome.charAt(0).toUpperCase() || '?');
      const miolo =
        foto && foto !== 'sem_foto'
          ? `<img src="${escaparHtmlChat(foto)}" alt="" loading="lazy" decoding="async" onerror="fotoAvatarBalaoFalhou(this)">`
          : inicial;
      pronto = {
        chave: 'cliente',
        pessoa: 'cliente',
        html: `<span class="msg-avatar" data-inicial="${inicial}" title="${escaparHtmlChat(nome)}">${miolo}</span>`,
      };
      cacheAvatarRemetente.set(chaveCache, pronto);
    }
    return pronto;
  }

  const nome = String(msg.sent_by_name || '').trim();
  const chaveCache = 'nosso|' + nome;
  let pronto = cacheAvatarRemetente.get(chaveCache);
  if (pronto) return pronto;

  const normal = partesDoNome(nome).join(' ');
  if (normal === 'externo') {
    // Vendedora respondendo pelo celular / WhatsApp Web, fora do sistema.
    // icone-waba-suave.svg, não o icone-waba.svg: o verde cheio do original
    // destoava ao lado das outras fotos. O original segue nos selos de canal.
    pronto = { chave: 'waba', pessoa: 'waba', html: htmlAvatarIcone('msg-avatar-waba', 'icone-waba-suave.svg', 'Externo (WhatsApp)') };
  } else if (normal === 'campanha rcs') {
    pronto = { chave: 'rcs', pessoa: 'rcs', html: htmlAvatarIcone('msg-avatar-rcs', 'icone-rcs.svg', 'Campanha RCS') };
  } else if (REMETENTES_ROBO.has(normal) || normal.startsWith('fluxo:')) {
    // Mesma foto pra todo robô, mas o NOME muda (Sistema x Automação):
    // pessoa diferente = o nome aparece de novo, mesmo sem trocar a foto.
    pronto = { chave: 'robo', pessoa: 'robo|' + normal, html: htmlAvatarIcone('msg-avatar-robo', 'robot.svg', nome || 'Sistema') };
  } else {
    const login = loginDoRemetente(nome);
    const inicial = escaparHtmlChat(nome.charAt(0).toUpperCase() || '?');
    const miolo =
      login && !loginsSemFoto.has(login)
        ? `<img src="/fotos-equipe/${encodeURIComponent(login)}.webp" alt="" loading="lazy" decoding="async" data-login="${escaparHtmlChat(login)}" onerror="fotoEquipeFalhou(this)">`
        : inicial;
    pronto = {
      chave: login ? 'equipe:' + login : 'nome:' + normal,
      pessoa: login ? 'equipe:' + login : 'nome:' + normal,
      html: `<span class="msg-avatar" data-inicial="${inicial}"${login ? ` data-login="${escaparHtmlChat(login)}"` : ''} title="${escaparHtmlChat(nome)}">${miolo}</span>`,
    };
  }
  // Sem a equipe carregada o vendedor sai com a inicial; não guarda isso.
  if (equipeParaAvatar || !pronto.chave.startsWith('nome:'))
    cacheAvatarRemetente.set(chaveCache, pronto);
  return pronto;
}
// --- NOVO: Função para formatar a data do cabeçalho ---
function formatarDataAmigavel(dataObj) {
  const hoje = new Date();
  const ontem = new Date();
  ontem.setDate(hoje.getDate() - 1);
  // Zera as horas para comparar apenas o dia
  const d = new Date(dataObj).setHours(0, 0, 0, 0);
  const h = new Date(hoje).setHours(0, 0, 0, 0);
  const o = new Date(ontem).setHours(0, 0, 0, 0);
  if (d === h) return 'Hoje';
  if (d === o) return 'Ontem';
  // Verifica se é desta semana (últimos 7 dias)
  const diffDias = (h - d) / (1000 * 60 * 60 * 24);
  if (diffDias < 7 && diffDias > 0) {
    // Retorna dia da semana (ex: "segunda-feira")
    return FORMATO_DIA_SEMANA_MSG.format(dataObj);
  }
  // Data normal (ex: 25/01/2026)
  return FORMATO_DATA_MSG.format(dataObj);
}
function formatarTextoWhatsApp(texto) {
  if (!texto) return '';
  // 1. Escapa tags HTML para evitar que código malicioso quebre o chat
  let formatado = texto.replace(/</g, '&lt;').replace(/>/g, '&gt;');
  // ==========================================
  // 🔗 NOVO: DETECTOR DE LINKS INTELIGENTE
  // ==========================================
  // Detecta links com http/https ou que começam com www.
  const regexLink = /(https?:\/\/[^\s]+|www\.[^\s]+)/g;
  formatado = formatado.replace(regexLink, (url) => {
    // Se o link começar com www., adiciona o https:// por trás para o navegador não tentar abrir localmente
    const href = (url.startsWith('www.') ? `https://${url}` : url).replace(
      /"/g,
      '%22'
    );
    return `<a href="${href}" target="_blank" rel="noopener noreferrer" class="link-mensagem-chat">${url}</a>`;
  });
  // ==========================================
  // 2. Bloco de código com 3 crases (```texto```) - Apenas fonte monoespaçada pura
  formatado = formatado.replace(
    /```([\s\S]*?)```/g,
    '<span class="mono-puro">$1</span>'
  );
  // 3. Código embutido com 1 crase (`texto`) - Com fundinho sutil
  formatado = formatado.replace(
    /`([^`]+)`/g,
    '<code class="codigo-linha">$1</code>'
  );
  // 4. Negrito (*texto*)
  formatado = formatado.replace(/\*([^*]+)\*/g, '<strong>$1</strong>');
  // 5. Itálico (_texto_)
  formatado = formatado.replace(/_([^_]+)_/g, '<em>$1</em>');
  // 6. Tachado (~texto~)
  formatado = formatado.replace(/~([^~]+)~/g, '<del>$1</del>');
  // 7. Quebra de linha (Enter)
  formatado = formatado.replace(/\n/g, '<br>');
  return formatado;
}
// ============================================================
// FUNÇÃO PARA LIMPAR SIMBOLOS DO WHATSAPP NA BARRA LATERAL
// ============================================================
function limparFormatacaoWhatsApp(texto) {
  if (!texto) return '';
  // Limpa as tags HTML se houver e remove os símbolos mantendo o texto de dentro
  return texto
    .replace(/```([\s\S]*?)```/g, '$1') // Tira as 3 crases
    .replace(/`([^`]+)`/g, '$1') // Tira 1 crase
    .replace(/\*([^*]+)\*/g, '$1') // Tira os asteriscos
    .replace(/_([^_]+)_/g, '$1') // Tira os underlines
    .replace(/~([^~]+)~/g, '$1'); // Tira osils (~),
}
// `jaDecidido`: quem chamou já mediu se a pessoa estava no fim (antes de
// desenhar a mensagem nova) — aí `forcar` é a resposta final: true desce,
// false conta no radar. Sem ele, mede aqui com a folga antiga.
function rolarParaFim(forcar = false, jaDecidido = false) {
  const container = document.getElementById('container-mensagens');
  if (!container) return;
  const btnRadar = document.getElementById('btn-radar-scroll');
  const radarBadge = btnRadar ? btnRadar.querySelector('.radar-badge') : null;
  const distanceFromBottom =
    container.scrollHeight - container.scrollTop - container.clientHeight;
  if (forcar === true || (!jaDecidido && distanceFromBottom <= 350)) {
    container.scrollTop = container.scrollHeight;
    if (btnRadar) {
      btnRadar.style.visibility = 'hidden';
      btnRadar.style.opacity = '0';
      if (radarBadge) {
        radarBadge.classList.add('hidden');
        radarBadge.innerText = '0';
      }
    }
  } else {
    if (radarBadge) {
      let count = parseInt(radarBadge.innerText) || 0;
      count++;
      radarBadge.innerText = count;
      radarBadge.classList.remove('hidden');
      btnRadar.style.visibility = 'visible';
      btnRadar.style.opacity = '1';
    }
  }
}
// ============================================================
// 🎯 MÓDULO: GERENCIAR FUNIL POR DENTRO DO CHAT
// ============================================================
let estruturaKanbanCache = null;
let chatAtualDoFunil = null;
let cacheBotsCanais = null; // Guarda a memória dos bots para não pesar o sistema
window.atualizarVisualizacaoFunil = async function (chat) {
  chatAtualDoFunil = chat;
  const chatId = chat.chat_id;
  const botId = chat.bot_id; // Identifica qual robô o cliente chamou
  const labelCanal = document.getElementById('label-canal-nome');
  const containerLinha = document.getElementById('linha-progresso-funil');
  const numPuro = chatId.replace(/\D/g, '');
  try {
    const res = await fetch(`/api/chat/cliente-funil/${numPuro}`);
    const json = await res.json();
    if (json.success && json.data) {
      // O CLIENTE JÁ ESTÁ EM UMA ETAPA ESPECÍFICA DO KANBAN
      const { nome_canal, nome_coluna, ordem_atual, etapas } = json.data;
      // NOVA LÓGICA VISUAL: 2 LINHAS (FUNIL EM CIMA, ETAPA EM BAIXO)
      labelCanal.innerHTML = `
        <span style="display: block; font-weight: 700; font-size: 13px;">${nome_canal}</span>
        <span style="display: block; font-weight: 500; font-size: 11px; color: #9ca3af; margin-top: 2px;">${nome_coluna}</span>
      `;
      containerLinha.innerHTML = '';
      etapas.forEach((etapa) => {
        const span = document.createElement('span');
        span.className = 'step-line';
        if (etapa.ordem <= ordem_atual) {
          span.classList.add('active');
          span.style.backgroundColor = etapa.cor || '#9ca3af';
        }
        span.title = etapa.nome;
        containerLinha.appendChild(span);
      });
    } else {
      // --- O CLIENTE ACABOU DE CHEGAR (AINDA NÃO FOI MOVIDO) ---
      let nomeFunil = 'Caixa de Entrada';
      try {
        if (!cacheBotsCanais) {
          const resBots = await fetch('/api/sessions', {
            headers: {
              Authorization: `Bearer ${localStorage.getItem('token')}`,
            },
          });
          cacheBotsCanais = await resBots.json();
        }
        const botAtual = cacheBotsCanais.find((b) => b.id === botId);
        if (botAtual && botAtual.canal_nome) {
          nomeFunil = botAtual.canal_nome;
        }
      } catch (e) {
        console.error('Erro ao buscar nome do funil:', e);
      }
      // NOVA LÓGICA VISUAL: 2 LINHAS (FUNIL EM CIMA, ETAPA EM BAIXO)
      labelCanal.innerHTML = `
        <span style="display: block; font-weight: 700; font-size: 13px;">${nomeFunil}</span>
        <span style="display: block; font-weight: 500; font-size: 11px; color: var(--texto-suave); margin-top: 2px;">Funil de Entrada</span>
      `;
      containerLinha.innerHTML =
        '<span class="step-line active" style="background-color: #9ca3af; width: 100%;"></span>';
    }
  } catch (e) {
    console.error('Erro ao carregar funil:', e);
  }
};
// Pega a estrutura do banco (Canais e Colunas)
async function carregarEstruturaKanban() {
  if (estruturaKanbanCache) return estruturaKanbanCache;
  try {
    const res = await fetch('/api/kanban/estrutura', {
      headers: { Authorization: `Bearer ${localStorage.getItem('token')}` },
    });
    const data = await res.json();
    if (data.success) {
      estruturaKanbanCache = data;
      return data;
    }
  } catch (e) {
    console.error(e);
  }
  return null;
}
// Desenha os botões na tela (AGORA COM EFEITO SANFONA / CARDÁPIO)
async function renderizarDropdownFunil() {
  const dropdown = document.getElementById('dropdown-funil-atendimento');
  dropdown.innerHTML =
    '<div style="text-align:center; padding:10px;"><i class="fas fa-spinner fa-spin"></i> Carregando Funis...</div>';
  const estrutura = await carregarEstruturaKanban();
  if (!estrutura || estrutura.canais.length === 0) {
    dropdown.innerHTML =
      '<div style="padding:10px; font-size:12px; color:#999;">Nenhum funil configurado no Kanban.</div>';
    return;
  }
  dropdown.innerHTML = '';
  // Passa por cada Canal e cria as opções
  estrutura.canais.forEach((canal) => {
    const colunasDoCanal = estrutura.colunas.filter(
      (c) => c.canal_id === canal.id
    );
    if (colunasDoCanal.length === 0) return;
    const grupo = document.createElement('div');
    grupo.className = 'grupo-funil-dd';
    // 1. Cria o Título Clicável (A Parmegiana)
    const headerCanal = document.createElement('div');
    headerCanal.className = 'titulo-funil-dd';
    headerCanal.innerHTML = `<span>${escaparHtmlChat(canal.nome)}</span> <i class='bx bx-chevron-down chevron-canal'></i>`;
    // 2. Cria a "Caixa" que vai guardar as etapas (Os ingredientes)
    const containerEtapas = document.createElement('div');
    containerEtapas.className = 'etapas-container-dd';
    // 3. A Lógica de Clicar e Abrir/Fechar
    headerCanal.onclick = () => {
      const isOpen = containerEtapas.classList.contains('aberto');
      // PRIMEIRO: Fecha todos os outros canais que estiverem abertos
      dropdown
        .querySelectorAll('.etapas-container-dd')
        .forEach((c) => c.classList.remove('aberto'));
      dropdown
        .querySelectorAll('.chevron-canal')
        .forEach((c) => c.classList.remove('girar'));
      // SEGUNDO: Se o que eu cliquei estava fechado, eu abro ele!
      if (!isOpen) {
        containerEtapas.classList.add('aberto');
        headerCanal.querySelector('.chevron-canal').classList.add('girar');
      }
    };
    // 4. Preenche os ingredientes (Colunas)
    colunasDoCanal.forEach((coluna) => {
      const btn = document.createElement('button');
      btn.className = 'btn-etapa-dd';
      btn.innerHTML = `<span class="cor-etapa-dot" style="background-color: ${escaparHtmlChat(coluna.cor)}"></span> ${escaparHtmlChat(coluna.nome)}`;
      btn.onclick = () =>
        moverClienteParaFunil(coluna.id, coluna.nome, coluna.tipo_sistema);
      containerEtapas.appendChild(btn);
    });
    // 5. Monta tudo e joga na tela
    grupo.appendChild(headerCanal);
    grupo.appendChild(containerEtapas);
    dropdown.appendChild(grupo);
  });
}
// Faz a requisição para mover (AGORA COM ALERTA INTELIGENTE)
window.moverClienteParaFunil = async function (
  colunaId,
  nomeEtapa,
  tipoSistemaAlvo
) {
  if (!chatAtualDoFunil) return;
  // 1. Descobre qual é o Status Atual e qual vai ser o Status Novo
  const statusAtual = chatAtualDoFunil.status_lead || 'aberto';
  let statusAlvo = 'aberto'; // Por padrão, colunas normais e "entrada" são 'aberto'
  if (tipoSistemaAlvo === 'ganho') statusAlvo = 'ganho';
  if (tipoSistemaAlvo === 'perdido') statusAlvo = 'perdido';
  // 2. SÓ EXIBE O ALERTA SE O STATUS ESTIVER MUDANDO (Ex: aberto -> ganho)
  //
  // Indo pro PERDIDO, o seletor de motivo entra no lugar da confirmação: quem
  // escolhe um motivo já está confirmando, e duas caixas seguidas viram
  // clique automático. Diferente dos outros status, aqui a pergunta acontece
  // mesmo que o lead já estivesse perdido — remarcar é justamente onde a
  // pessoa costuma querer corrigir o motivo.
  let motivoEscolhido = null;
  if (statusAlvo === 'perdido') {
    motivoEscolhido = await pedirMotivoPerdidoChat();
    if (!motivoEscolhido) return;
  } else if (statusAtual !== statusAlvo) {
    const confirmacao = await Swal.fire({
      title: `Mover para ${nomeEtapa}?`,
      text: 'O status do lead será atualizado no sistema.',
      icon: 'question',
      showCancelButton: true,
      confirmButtonColor: '#3b82f6',
      confirmButtonText: 'Sim, mover',
      cancelButtonText: 'Cancelar',
    });
    if (!confirmacao.isConfirmed) return; // Se cancelar, aborta tudo
  }
  // 3. Continua o processo normal de mover e fechar o menu
  const btnToggle = document.getElementById('btn-toggle-funil');
  const dropdown = document.getElementById('dropdown-funil-atendimento');
  btnToggle.classList.remove('aberto');
  dropdown.classList.add('hidden');
  try {
    const res = await fetch('/api/chat/mover-funil', {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        Authorization: `Bearer ${localStorage.getItem('token')}`,
      },
      body: JSON.stringify({
        numero_wpp: chatAtualDoFunil.chat_id,
        nome_cliente: chatAtualDoFunil.crm_nome || chatAtualDoFunil.name,
        coluna_id: colunaId,
        motivo_perdido: motivoEscolhido,
      }),
    });
    const data = await res.json();
    if (data.success) {
      mostrarAlerta(`Movido para ${nomeEtapa}!`, 'success');
      chatAtualDoFunil.motivo_perdido = motivoEscolhido;
      atualizarSeloMotivoPerdido(motivoEscolhido);
      // Atualiza o visual das bolinhas de progresso
      window.atualizarVisualizacaoFunil(chatAtualDoFunil);
    } else if (data.motivo_obrigatorio) {
      mostrarAlerta(data.message || 'Escolha o motivo da perda.', 'error');
    }
  } catch (e) {
    console.error('🔥 ERRO AO MOVER FUNIL:', e);
    mostrarAlerta('Erro de conexão', 'error');
  }
};
