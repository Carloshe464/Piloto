// Content script: lê metadados da ligação no DOM do Zendesk e envia ao service worker.
//
// IMPORTANTE: os seletores abaixo são PLACEHOLDERS. O DOM do Zendesk (Agent Workspace /
// Talk) varia por conta e versão. Ajuste SELETORES inspecionando a página real do atendente.
// Ponto único de manutenção citado no README: "Seletores do DOM do Zendesk".
//
// E-mail e telefone do cliente NÃO dependem só de seletor: além deles, há um caminho
// genérico que procura links mailto:/tel: no cartão do solicitante. Links são semânticos
// e sobrevivem a reestilização do Zendesk, então o caminho genérico costuma funcionar
// mesmo com os seletores desatualizados.

const SELETORES = {
  // Número do cliente em ligação (ex.: painel do Zendesk Talk).
  numero: '[data-test-id="talk-call-number"], .talk-call-number, [aria-label="Número do chamador"]',
  // Ticket atualmente aberto.
  ticket: '[data-test-id="ticket-system-field-ticketId"], .ticket-id, header [href*="/tickets/"]',
  // Status do ticket.
  status: '[data-test-id="ticket-fields-status"], .ticket-status-label, [name="status"]',
  // Nome do atendente logado.
  atendente: '[data-test-id="header-agent-menu-button"], .agent-name',
  // Contato do solicitante (cartão do cliente na lateral).
  emailCliente: '[data-test-id="requester-email"], [data-test-id="customer-context-email"], .requester .email',
  telefoneCliente: '[data-test-id="requester-phone"], [data-test-id="user-field-phone"], .requester .phone',
  nomeCliente: '[data-test-id="requester-name"], [data-test-id="customer-context-name"], .requester-name',
};

// Onde o cartão do solicitante costuma viver. A busca por mailto:/tel: começa aqui —
// restringir o escopo é o que impede pegar o e-mail do PRÓPRIO atendente, que aparece
// no menu do cabeçalho em toda página do Zendesk.
const CONTAINERS_CLIENTE = [
  '[data-test-id="customer-context"]',
  '[data-test-id="requester-card"]',
  '[data-test-id="essentials-card"]',
  '[data-test-id="ticket-sidebar"]',
  '.requester',
];

// Regiões cujo conteúdo é do atendente/da aplicação, nunca do cliente.
const SELETOR_EXCLUIDO = 'header, nav, [data-test-id="header"], [data-test-id="header-agent-menu-button"]';

const INTERVALO_DEBOUNCE_MS = 800;
let ultimoPayload = '';
let timer = null;

function texto(seletor) {
  const el = document.querySelector(seletor);
  if (!el) return null;
  const t = (el.getAttribute('href')?.match(/\/tickets\/(\d+)/)?.[1]) || el.value || el.textContent;
  return t ? String(t).trim() || null : null;
}

// Agent Workspace navega para /agent/tickets/12345 quando um ticket está aberto —
// independe de seletor de DOM e funciona em qualquer conta/versão do Zendesk.
function ticketDaUrl() {
  const m = location.href.match(/\/(?:agent\/)?tickets\/(\d+)/);
  return m ? m[1] : null;
}

function dentroDeRegiaoExcluida(el) {
  return !!el.closest(SELETOR_EXCLUIDO);
}

// Procura um link href^=prefixo (mailto:/tel:), primeiro dentro do cartão do solicitante
// e só depois na página inteira. Devolve o valor do href sem o esquema.
function valorDeLink(prefixo) {
  const busca = (raiz) => {
    for (const a of raiz.querySelectorAll(`a[href^="${prefixo}"]`)) {
      if (dentroDeRegiaoExcluida(a)) continue;
      const valor = decodeURIComponent(a.getAttribute('href').slice(prefixo.length))
        .split('?')[0]
        .trim();
      if (valor) return valor;
    }
    return null;
  };

  for (const seletor of CONTAINERS_CLIENTE) {
    const container = document.querySelector(seletor);
    if (container) {
      const achado = busca(container);
      if (achado) return achado;
    }
  }
  return busca(document);
}

// Validação leve, só para não mandar rótulo/placeholder como se fosse dado. O app
// valida de novo antes de gravar (ContactMerger) — aqui é para poupar tráfego e ruído.
function emailValido(v) {
  return typeof v === 'string' && /^[^@\s]+@[^@\s.]+\.[^@\s]+$/.test(v);
}

function telefoneValido(v) {
  if (typeof v !== 'string') return false;
  const digitos = v.replace(/\D/g, '');
  // 10-11 dígitos nacionais, ou 12-13 com o prefixo 55 do Brasil (formato E.164 do
  // cadastro). O app normaliza para o formato nacional.
  return digitos.length >= 10 && digitos.length <= 13;
}

function primeiroValido(validador, ...candidatos) {
  for (const c of candidatos) {
    const v = c && String(c).trim();
    if (v && validador(v)) return v;
  }
  return null;
}

function coletar() {
  return {
    tipo: 'metadata',
    numero: texto(SELETORES.numero),
    ticket: ticketDaUrl() || texto(SELETORES.ticket),
    status: texto(SELETORES.status),
    atendente: texto(SELETORES.atendente),
    // Seletor primeiro (é o mais específico); link mailto:/tel: como rede de segurança.
    emailCliente: primeiroValido(emailValido, texto(SELETORES.emailCliente), valorDeLink('mailto:')),
    telefoneCliente: primeiroValido(telefoneValido, texto(SELETORES.telefoneCliente), valorDeLink('tel:')),
    nomeCliente: texto(SELETORES.nomeCliente),
  };
}

function enviarSeMudou() {
  const payload = coletar();
  const chave = JSON.stringify(payload);
  if (chave === ultimoPayload) return;
  // Só envia se ao menos um campo foi encontrado.
  if (!payload.numero && !payload.ticket && !payload.status &&
      !payload.emailCliente && !payload.telefoneCliente) return;
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
console.log('[Click Write] content script ativo no Zendesk');
