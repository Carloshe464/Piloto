const inputPorta = document.getElementById('porta');
const dot = document.getElementById('dot');
const txt = document.getElementById('txt');

function pintar(estado) {
  dot.className = 'dot ' + (estado === 'conectado' ? 'ok' : estado === 'conectando' ? 'wait' : 'bad');
  txt.textContent = estado === 'conectado' ? 'Conectado ao app'
    : estado === 'conectando' ? 'Conectando…'
    : 'Desconectado (o app Piloto está aberto?)';
}

function atualizarStatus() {
  chrome.runtime.sendMessage({ tipo: 'status' }, (resp) => {
    if (chrome.runtime.lastError) { pintar('desconectado'); return; }
    pintar(resp?.estado || 'desconectado');
  });
}

chrome.storage.local.get('porta', ({ porta }) => {
  if (porta) inputPorta.value = porta;
});

document.getElementById('salvar').addEventListener('click', () => {
  const porta = parseInt(inputPorta.value, 10);
  if (!porta || porta < 1 || porta > 65535) return;
  chrome.storage.local.set({ porta }, () => {
    pintar('conectando');
    setTimeout(atualizarStatus, 700);
  });
});

atualizarStatus();
setInterval(atualizarStatus, 1500);
