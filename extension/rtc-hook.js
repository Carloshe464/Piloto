// Hook de WebRTC (world MAIN, document_start): intercepta o RTCPeerConnection do
// softphone (55PBX dentro do Zendesk) e captura as duas pernas da chamada NA FONTE —
// a voz do cliente como chega da central e o microfone do atendente já com o
// cancelamento de eco/ruído do Chrome. Qualidade acima de qualquer loopback, sem API.
//
// O áudio é reamostrado para 16 kHz PCM16 e enviado ao content script (content-audio.js)
// via window.postMessage; de lá segue ao service worker e ao app pelo WebSocket local.
//
// IMPORTANTE: precisa rodar em document_start para o patch acontecer ANTES de o
// softphone criar a conexão. Se o softphone da 55PBX viver em outro domínio/iframe,
// ajuste "matches" no manifest.json (procure no console do frame por "[Piloto] hook").

(() => {
  'use strict';
  if (window.__pilotoRtcHook) return;
  window.__pilotoRtcHook = true;

  const RTCOriginal = window.RTCPeerConnection;
  if (!RTCOriginal) return;

  const TAXA_ALVO = 16000;
  const AMOSTRAS_POR_ENVIO = 8000; // ~0,5 s por mensagem
  // Maior buffer que o ScriptProcessor aceita: menos callbacks na thread principal e
  // mais tolerância às travadas do Zendesk nas máquinas fracas da operação.
  const TAMANHO_BUFFER = 16384;
  // Renegociação troca as tracks em sequência (a velha termina antes de a nova
  // chegar); esta espera evita encerrar a sessão no meio da troca.
  const ESPERA_FIM_MS = 2000;

  let sessao = null;

  function postar(msg) {
    try { window.postMessage(Object.assign({ __piloto: true }, msg), '*'); } catch (_) {}
  }

  // ------------------------------------------------------------ reamostragem
  // Interpolação linear com posição fracionária preservada entre blocos.
  function criarResampler(taxaOrigem) {
    const razao = taxaOrigem / TAXA_ALVO;
    let pos = 0;
    let ultimo = 0;
    return (entrada) => {
      const saida = [];
      while (pos < entrada.length) {
        const i = Math.floor(pos);
        const frac = pos - i;
        const a = i === 0 ? ultimo : entrada[i - 1];
        const b = entrada[Math.min(i, entrada.length - 1)];
        saida.push(a + (b - a) * frac);
        pos += razao;
      }
      pos -= entrada.length;
      ultimo = entrada[entrada.length - 1];
      return saida;
    };
  }

  function paraBase64Pcm16(amostras) {
    const int16 = new Int16Array(amostras.length);
    for (let i = 0; i < amostras.length; i++) {
      const v = Math.max(-1, Math.min(1, amostras[i]));
      int16[i] = v < 0 ? v * 0x8000 : v * 0x7FFF;
    }
    const bytes = new Uint8Array(int16.buffer);
    let bin = '';
    const passo = 0x8000;
    for (let i = 0; i < bytes.length; i += passo)
      bin += String.fromCharCode.apply(null, bytes.subarray(i, i + passo));
    return btoa(bin);
  }

  // ------------------------------------------------------------ sessão
  function iniciarSessao() {
    if (sessao) return sessao;
    const ctx = new (window.AudioContext || window.webkitAudioContext)();
    try { ctx.resume(); } catch (_) {}
    sessao = {
      ctx,
      inicioMs: performance.now(),
      canais: new Map(), // canal -> { bus, aa1, aa2, proc, mudo, fontes: Map<track, {fonte, el, pc}>, fila, enviadas, flush }
      pcs: new Set(),
      timerFim: null,
    };
    postar({ tipo: 'call_started' });
    postar({ tipo: 'audio_inicio', taxa: TAXA_ALVO });
    console.log('[Piloto] hook: sessão de captura iniciada');
    return sessao;
  }

  // Barramento por lado: as fontes (tracks) se somam num GainNode e um único
  // processor cobre o canal da criação ao fim da sessão — troca de track no meio
  // da chamada (renegociação, troca de microfone) não interrompe a linha do tempo,
  // e os intervalos sem fonte viram silêncio em vez de encurtar o WAV.
  function obterCanal(canal) {
    const s = sessao;
    let info = s.canais.get(canal);
    if (info) return info;

    const bus = s.ctx.createGain();
    // A reamostragem por interpolação linear não filtra nada: sem um passa-baixas antes,
    // o conteúdo acima de 8 kHz dobra para dentro da banda (aliasing) e suja o áudio que
    // o Whisper recebe. Dois biquads em cascata ≈ 24 dB/oitava acima do corte.
    const aa1 = s.ctx.createBiquadFilter();
    const aa2 = s.ctx.createBiquadFilter();
    for (const f of [aa1, aa2]) { f.type = 'lowpass'; f.frequency.value = 7000; f.Q.value = 0.7071; }
    const proc = s.ctx.createScriptProcessor(TAMANHO_BUFFER, 1, 1);
    const mudo = s.ctx.createGain();
    mudo.gain.value = 0; // o processor precisa chegar ao destino, mas sem eco audível

    info = {
      bus, aa1, aa2, proc, mudo,
      fontes: new Map(),
      fila: [],
      enviadas: 0,
      resample: criarResampler(s.ctx.sampleRate),
      flush: null,
    };

    // Os dois WAVs precisam compartilhar o mesmo t=0: a fusão do diálogo ordena por
    // timestamp e um canal que só aparece depois — a voz do cliente chega no atender,
    // o microfone entra na discagem — sairia embaralhado no diálogo. O atraso vira
    // silêncio no começo do canal.
    const atrasoAmostras = Math.max(0, Math.round((performance.now() - s.inicioMs) / 1000 * TAXA_ALVO));
    for (let i = 0; i < atrasoAmostras; i++) info.fila.push(0);

    const despachar = (tudo) => {
      while (info.fila.length >= AMOSTRAS_POR_ENVIO || (tudo && info.fila.length > 0)) {
        const lote = info.fila.splice(0, AMOSTRAS_POR_ENVIO);
        info.enviadas += lote.length;
        postar({ tipo: 'audio_chunk', canal, dados: paraBase64Pcm16(lote) });
      }
    };
    info.flush = () => despachar(true);

    proc.onaudioprocess = (e) => {
      try {
        const reamostrado = info.resample(e.inputBuffer.getChannelData(0));
        for (let i = 0; i < reamostrado.length; i++) info.fila.push(reamostrado[i]);
        despachar(false);
      } catch (_) {}
    };

    bus.connect(aa1);
    aa1.connect(aa2);
    aa2.connect(proc);
    proc.connect(mudo);
    mudo.connect(s.ctx.destination);
    s.canais.set(canal, info);
    return info;
  }

  function anexarFonte(canal, track, pc) {
    try {
      if (!track || track.kind !== 'audio') return;
      const s = iniciarSessao();
      if (pc) s.pcs.add(pc);
      if (s.timerFim) { clearTimeout(s.timerFim); s.timerFim = null; }
      if (s.ctx.state === 'suspended') { try { s.ctx.resume(); } catch (_) {} }

      const info = obterCanal(canal);
      if (info.fontes.has(track)) return; // esta track já alimenta o canal

      const stream = new MediaStream([track]);
      const fonte = s.ctx.createMediaStreamSource(stream);
      fonte.connect(info.bus);

      // O Chrome só entrega áudio de track REMOTA ao WebAudio quando ela também está
      // presa a um elemento de mídia (crbug.com/933677). Sem este <audio> mudo, o
      // canal do cliente pode ficar em silêncio absoluto dependendo da ordem em que o
      // softphone conecta o próprio player — e silêncio no Whisper vira alucinação.
      let el = null;
      if (canal === 'cliente') {
        el = new Audio();
        el.muted = true;
        el.volume = 0;
        el.srcObject = stream;
        el.play().catch(() => {});
      }

      info.fontes.set(track, { fonte, el, pc });
      track.addEventListener('ended', () => removerFonte(canal, track));
      console.log('[Piloto] hook: canal "' + canal + '" + track (' +
        info.fontes.size + ' fonte(s), frame: ' + location.origin + ')');
    } catch (e) {
      console.warn('[Piloto] hook: falha ao anexar track', e);
    }
  }

  function removerFonte(canal, track) {
    if (!sessao) return;
    const info = sessao.canais.get(canal);
    if (!info) return;
    const f = info.fontes.get(track);
    if (!f) return;
    info.fontes.delete(track);
    try { f.fonte.disconnect(); } catch (_) {}
    if (f.el) { try { f.el.srcObject = null; } catch (_) {} }
    agendarFimSeVazio();
  }

  // Sem nenhuma fonte viva em nenhum canal, a chamada terminou — mas só depois da
  // espera curta, para sobreviver à troca de tracks de uma renegociação.
  function agendarFimSeVazio() {
    const s = sessao;
    if (!s || s.timerFim) return;
    let vivas = 0;
    for (const [, info] of s.canais) vivas += info.fontes.size;
    if (vivas > 0) return;
    s.timerFim = setTimeout(() => {
      if (sessao === s) { s.timerFim = null; encerrarSessao(); }
    }, ESPERA_FIM_MS);
  }

  function aoMorrerPc(pc) {
    const s = sessao;
    if (!s || !s.pcs.has(pc)) return;
    s.pcs.delete(pc);
    // As fontes desta conexão morreram junto (o Chrome nem sempre dispara 'ended').
    for (const [canal, info] of s.canais)
      for (const track of Array.from(info.fontes.keys()))
        if (info.fontes.get(track).pc === pc) removerFonte(canal, track);
    if (s.pcs.size === 0 && sessao === s) encerrarSessao();
    else agendarFimSeVazio();
  }

  function encerrarSessao() {
    const s = sessao;
    if (!s) return;
    sessao = null;
    if (s.timerFim) clearTimeout(s.timerFim);
    for (const [canal, info] of s.canais) {
      try { info.flush(); } catch (_) {}
      for (const [, f] of info.fontes) {
        try { f.fonte.disconnect(); } catch (_) {}
        if (f.el) { try { f.el.srcObject = null; } catch (_) {} }
      }
      try {
        info.bus.disconnect(); info.aa1.disconnect(); info.aa2.disconnect();
        info.proc.disconnect(); info.mudo.disconnect();
      } catch (_) {}
      console.log('[Piloto] hook: canal "' + canal + '" enviou ' +
        (info.enviadas / TAXA_ALVO).toFixed(1) + ' s de áudio');
    }
    try { s.ctx.close(); } catch (_) {}
    postar({ tipo: 'audio_fim' });
    postar({ tipo: 'call_ended' });
    console.log('[Piloto] hook: sessão de captura encerrada');
  }

  // ------------------------------------------------------------ patches
  // O softphone pode entregar o microfone por addTrack, addTransceiver, addStream
  // (API legada) ou trocá-lo no meio da chamada com replaceTrack — todos cobertos.
  const donoDoSender = new WeakMap();

  const addTrackOriginal = RTCOriginal.prototype.addTrack;
  RTCOriginal.prototype.addTrack = function (track) {
    try { anexarFonte('atendente', track, this); } catch (_) {}
    const sender = addTrackOriginal.apply(this, arguments);
    try { donoDoSender.set(sender, this); } catch (_) {}
    return sender;
  };

  const addTransceiverOriginal = RTCOriginal.prototype.addTransceiver;
  if (addTransceiverOriginal) {
    RTCOriginal.prototype.addTransceiver = function (trackOuTipo) {
      try {
        if (trackOuTipo && typeof trackOuTipo === 'object' && trackOuTipo.kind === 'audio')
          anexarFonte('atendente', trackOuTipo, this);
      } catch (_) {}
      const transceiver = addTransceiverOriginal.apply(this, arguments);
      try { donoDoSender.set(transceiver.sender, this); } catch (_) {}
      return transceiver;
    };
  }

  const addStreamOriginal = RTCOriginal.prototype.addStream;
  if (addStreamOriginal) {
    RTCOriginal.prototype.addStream = function (stream) {
      try {
        if (stream) for (const t of stream.getAudioTracks()) anexarFonte('atendente', t, this);
      } catch (_) {}
      return addStreamOriginal.apply(this, arguments);
    };
  }

  if (window.RTCRtpSender && window.RTCRtpSender.prototype.replaceTrack) {
    const replaceTrackOriginal = window.RTCRtpSender.prototype.replaceTrack;
    window.RTCRtpSender.prototype.replaceTrack = function (track) {
      try {
        if (sessao && track && track.kind === 'audio')
          anexarFonte('atendente', track, donoDoSender.get(this) || null);
      } catch (_) {}
      return replaceTrackOriginal.apply(this, arguments);
    };
  }

  // Hangup local: o Chrome não dispara connectionstatechange no próprio close().
  const closeOriginal = RTCOriginal.prototype.close;
  RTCOriginal.prototype.close = function () {
    try { aoMorrerPc(this); } catch (_) {}
    return closeOriginal.apply(this, arguments);
  };

  function Patched(...args) {
    const pc = new RTCOriginal(...args);
    try {
      pc.addEventListener('track', (ev) => anexarFonte('cliente', ev.track, pc));
      pc.addEventListener('connectionstatechange', () => {
        // 'disconnected' é transitório (oscilação de rede) e a chamada costuma se
        // recuperar — encerrar por ele cortava gravações no meio. Só estados terminais,
        // e só desta conexão: outra pode seguir viva com a chamada.
        if (pc.connectionState === 'closed' || pc.connectionState === 'failed')
          aoMorrerPc(pc);
      });
    } catch (_) {}
    return pc;
  }
  Patched.prototype = RTCOriginal.prototype;
  try { Object.setPrototypeOf(Patched, RTCOriginal); } catch (_) {}
  window.RTCPeerConnection = Patched;
  if (window.webkitRTCPeerConnection) window.webkitRTCPeerConnection = Patched;

  console.log('[Piloto] hook de WebRTC ativo em ' + location.origin);
})();
