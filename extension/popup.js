const inputPorta = document.getElementById('porta');
const dot = document.getElementById('dot');
const txt = document.getElementById('txt');
const leitura = document.getElementById('leitura');

// Ordem em que os campos aparecem no painel de diagnóstico.
const CAMPOS = [
  ['numero', 'Número'],
  ['ticket', 'Ticket'],
  ['nomeCliente', 'Cliente'],
  ['emailCliente', 'E-mail'],
  ['telefoneCliente', 'Telefone'],
  ['atendente', 'Atendente'],
];

// Mostra o que os seletores estão pegando AGORA. Um campo em "—" com o Zendesk aberto
// significa seletor desatualizado — é o sinal para ajustar SELETORES em content-zendesk.js.
function pintarLeitura(ultimo) {
  leitura.textContent = '';
  for (const [chave, rotulo] of CAMPOS) {
    const valor = ultimo && ultimo[chave];
    const linha = document.createElement('div');
    linha.className = valor ? 'campo' : 'campo vazio';

    const esq = document.createElement('span');
    esq.textContent = rotulo;
    const dir = document.createElement('span');
    dir.textContent = valor || '—';

    linha.append(esq, dir);
    leitura.appendChild(linha);
  }
}

function pintar(estado) {
  dot.className = 'dot ' + (estado === 'conectado' ? 'ok' : estado === 'conectando' ? 'wait' : 'bad');
  txt.textContent = estado === 'conectado' ? 'Conectado ao app'
    : estado === 'conectando' ? 'Conectando…'
    : 'Desconectado (o app Click Write está aberto?)';
}

function atualizarStatus() {
  chrome.runtime.sendMessage({ tipo: 'status' }, (resp) => {
    if (chrome.runtime.lastError) { pintar('desconectado'); pintarLeitura(null); return; }
    pintar(resp?.estado || 'desconectado');
    pintarLeitura(resp?.ultimo);
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
