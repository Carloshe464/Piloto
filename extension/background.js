// Service worker: dono da conexão WebSocket com o app Piloto (ws://127.0.0.1:PORTA).
// O content script apenas lê o DOM e manda mensagens; aqui elas são encaminhadas ao app.
// Em MV3 o worker pode hibernar; um alarme periódico reabre a conexão quando necessário.

const PORTA_PADRAO = 8517;
let socket = null;
let ultimaMensagem = null;

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
      console.log('[Piloto] conectado ao app');
      if (ultimaMensagem) enviar(ultimaMensagem);
    });
    socket.addEventListener('close', () => { socket = null; });
    socket.addEventListener('error', () => { try { socket && socket.close(); } catch (_) {} socket = null; });
  } catch (e) {
    console.warn('[Piloto] falha ao conectar', e);
    socket = null;
  }
}

function enviar(obj) {
  ultimaMensagem = obj;
  if (socket && socket.readyState === WebSocket.OPEN) {
    try { socket.send(JSON.stringify(obj)); return true; }
    catch (e) { console.warn('[Piloto] falha ao enviar', e); }
  }
  conectar();
  return false;
}

chrome.runtime.onMessage.addListener((msg, _sender, sendResponse) => {
  if (msg?.tipo === 'metadata' || msg?.tipo === 'call_started' || msg?.tipo === 'call_ended') {
    const enviado = enviar(msg);
    sendResponse({ ok: enviado, estado: estado() });
    return true;
  }
  if (msg?.tipo === 'status') {
    sendResponse({ estado: estado() });
    return true;
  }
});

// Mantém a conexão viva/reabre periodicamente.
chrome.alarms.create('piloto-keepalive', { periodInMinutes: 0.5 });
chrome.alarms.onAlarm.addListener((a) => { if (a.name === 'piloto-keepalive') conectar(); });

conectar();
