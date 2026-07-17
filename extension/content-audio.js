// Relay (world ISOLATED): recebe as mensagens do rtc-hook.js via window.postMessage
// e as encaminha ao service worker, que envia ao app pelo WebSocket local.
// Roda em todos os frames onde o hook roda (mesmo "matches" no manifest).

const TIPOS = new Set(['audio_inicio', 'audio_chunk', 'audio_fim', 'call_started', 'call_ended']);

window.addEventListener('message', (ev) => {
  const d = ev.data;
  if (!d || d.__piloto !== true || typeof d.tipo !== 'string' || !TIPOS.has(d.tipo)) return;
  const msg = Object.assign({}, d);
  delete msg.__piloto;
  try {
    chrome.runtime.sendMessage(msg, () => void chrome.runtime.lastError);
  } catch (_) { /* worker reiniciando */ }
});
