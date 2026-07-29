// Service worker: dono da conexão WebSocket com o app Piloto (ws://127.0.0.1:PORTA).
// O content script apenas lê o DOM e manda mensagens; aqui elas são encaminhadas ao app.
// Em MV3 o worker pode hibernar; um alarme periódico reabre a conexão quando necessário.

const PORTA_PADRAO = 8517;
let socket = null;
// O popup mostra o último retrato completo lido no Zendesk. Ele também é a fonte
// de verdade que acompanha o começo da chamada: o áudio pode vir de um iframe da
// 55PBX, que não enxerga o DOM do ticket.
let ultimoMetadado = null;
let sessaoAtual = null;
const controlesPendentes = [];

async function obterPorta() {
  const { porta } = await chrome.storage.local.get('porta');
  return porta || PORTA_PADRAO;
}

function estado() {
  if (!socket) return 'desconectado';
  switch (socket.readyState) {
    case WebSocket.CONNECTING: return 'conectando';
    case WebSocket.OPEN: return 'conectado';
    default: return 'desconectado';
  }
}

async function conectar() {
  if (socket && (socket.readyState === WebSocket.OPEN || socket.readyState === WebSocket.CONNECTING)) {
    return;
  }
  const porta = await obterPorta();
  try {
    socket = new WebSocket(`ws://127.0.0.1:${porta}`);
    socket.addEventListener('open', () => {
      console.log('[Click Write] conectado ao app');
      // Primeiro restaura o contexto do Zendesk; em seguida, os eventos de
      // início/fim que chegaram durante a reconexão. Chunks de áudio nunca
      // entram na fila, pois reenviá-los corromperia a linha do tempo.
      if (ultimoMetadado) socket.send(JSON.stringify(ultimoMetadado));
      while (controlesPendentes.length) socket.send(JSON.stringify(controlesPendentes.shift()));
    });
    socket.addEventListener('close', () => { socket = null; });
    socket.addEventListener('error', () => { try { socket && socket.close(); } catch (_) {} socket = null; });
  } catch (e) {
    console.warn('[Click Write] falha ao conectar', e);
    socket = null;
  }
}

function enviar(obj, lembrar = true) {
  // Chunks de áudio nunca são "lembrados": reenviar um chunk velho após
  // reconexão corromperia a gravação em andamento.
  if (lembrar && obj?.tipo === 'metadata') ultimoMetadado = obj;
  if (socket && socket.readyState === WebSocket.OPEN) {
    try { socket.send(JSON.stringify(obj)); return true; }
    catch (e) { console.warn('[Click Write] falha ao enviar', e); }
  }
  if (obj?.tipo === 'call_started' || obj?.tipo === 'call_ended' || obj?.tipo === 'audio_inicio' || obj?.tipo === 'audio_fim')
    controlesPendentes.push(obj);
  conectar();
  return false;
}

const TIPOS_METADATA = new Set(['metadata', 'call_started', 'call_ended']);
const TIPOS_AUDIO = new Set(['audio_inicio', 'audio_chunk', 'audio_fim']);

chrome.runtime.onMessage.addListener((msg, _sender, sendResponse) => {
  if (msg?.tipo === 'metadata') {
    // content-zendesk envia um retrato completo; não misturamos campos de um
    // ticket anterior com o atual.
    ultimoMetadado = Object.assign({}, msg);
    const enviado = enviar(ultimoMetadado);
    sendResponse({ ok: enviado, estado: estado() });
    return true;
  }
  if (msg?.tipo === 'call_started') {
    // Carimba no navegador o instante da fronteira real da chamada e anexa o
    // ticket/telefone que o content script já leu no Zendesk.
    sessaoAtual = Object.assign({}, ultimoMetadado || {}, msg, {
      tipo: 'call_started',
      iniciadaEm: new Date().toISOString(),
    });
    const enviado = enviar(sessaoAtual);
    sendResponse({ ok: enviado, estado: estado() });
    return true;
  }
  if (msg?.tipo === 'call_ended') {
    const fim = Object.assign({}, sessaoAtual || ultimoMetadado || {}, msg, {
      tipo: 'call_ended',
      encerradaEm: new Date().toISOString(),
    });
    sessaoAtual = null;
    const enviado = enviar(fim);
    sendResponse({ ok: enviado, estado: estado() });
    return true;
  }
  if (TIPOS_AUDIO.has(msg?.tipo)) {
    // Mantém o áudio e os metadados da mesma chamada juntos mesmo quando o
    // iframe do softphone não tem acesso ao DOM do Zendesk.
    const comContexto = msg.tipo === 'audio_inicio'
      ? Object.assign({}, sessaoAtual || ultimoMetadado || {}, msg)
      : msg;
    const enviado = enviar(comContexto, /* lembrar */ false);
    sendResponse({ ok: enviado });
    return true;
  }
  if (msg?.tipo === 'status') {
    // Devolve também o último metadado lido: é como o popup mostra, na máquina do
    // atendente, se os seletores do DOM ainda estão pegando (eles quebram quando o
    // Zendesk muda o layout — o ponto de manutenção citado no README).
    sendResponse({ estado: estado(), ultimo: ultimoMetadado });
    return true;
  }
});

// Mantém a conexão viva/reabre periodicamente.
chrome.alarms.create('piloto-keepalive', { periodInMinutes: 0.5 });
chrome.alarms.onAlarm.addListener((a) => { if (a.name === 'piloto-keepalive') conectar(); });

conectar();
