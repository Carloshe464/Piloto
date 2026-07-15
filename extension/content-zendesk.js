// Content script: lê metadados da ligação no DOM do Zendesk e envia ao service worker.
//
// IMPORTANTE: os seletores abaixo são PLACEHOLDERS. O DOM do Zendesk (Agent Workspace /
// Talk) varia por conta e versão. Ajuste SELETORES inspecionando a página real do atendente.
// Ponto único de manutenção citado no README: "Seletores do DOM do Zendesk".

const SELETORES = {
  // Número do cliente em ligação (ex.: painel do Zendesk Talk).
  numero: '[data-test-id="talk-call-number"], .talk-call-number, [aria-label="Número do chamador"]',
  // Ticket atualmente aberto.
  ticket: '[data-test-id="ticket-system-field-ticketId"], .ticket-id, header [href*="/tickets/"]',
  // Status do ticket.
  status: '[data-test-id="ticket-fields-status"], .ticket-status-label, [name="status"]',
  // Nome do atendente logado.
  atendente: '[data-test-id="header-agent-menu-button"], .agent-name',
};

const INTERVALO_DEBOUNCE_MS = 800;
let ultimoPayload = '';
let timer = null;

function texto(seletor) {
  const el = document.querySelector(seletor);
  if (!el) return null;
  const t = (el.getAttribute('href')?.match(/\/tickets\/(\d+)/)?.[1]) || el.value || el.textContent;
  return t ? String(t).trim() || null : null;
}

function coletar() {
  return {
    tipo: 'metadata',
    numero: texto(SELETORES.numero),
    ticket: texto(SELETORES.ticket),
    status: texto(SELETORES.status),
    atendente: texto(SELETORES.atendente),
  };
}

function enviarSeMudou() {
  const payload = coletar();
  const chave = JSON.stringify(payload);
  if (chave === ultimoPayload) return;
  // Só envia se ao menos um campo foi encontrado.
  if (!payload.numero && !payload.ticket && !payload.status) return;
  ultimoPayload = chave;
  try {
    chrome.runtime.sendMessage(payload, (resp) => void chrome.runtime.lastError);
  } catch (_) { /* worker reiniciando */ }
}

function agendar() {
  clearTimeout(timer);
  timer = setTimeout(enviarSeMudou, INTERVALO_DEBOUNCE_MS);
}

const observer = new MutationObserver(agendar);
observer.observe(document.documentElement, { childList: true, subtree: true, characterData: true });

// Primeira leitura ao carregar.
agendar();
console.log('[Piloto] content script ativo no Zendesk');
