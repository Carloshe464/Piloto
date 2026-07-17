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
    sessao = { ctx, canais: new Map(), pcs: new Set() };
    postar({ tipo: 'call_started' });
    postar({ tipo: 'audio_inicio', taxa: TAXA_ALVO });
    console.log('[Piloto] hook: sessão de captura iniciada');
    return sessao;
  }

  function anexarCanal(canal, track, pc) {
    try {
      if (!track || track.kind !== 'audio') return;
      const s = iniciarSessao();
      s.pcs.add(pc);
      if (s.canais.has(canal)) return; // já capturando este lado

      const stream = new MediaStream([track]);
      const fonte = s.ctx.createMediaStreamSource(stream);
      const proc = s.ctx.createScriptProcessorNode
        ? s.ctx.createScriptProcessorNode(4096, 1, 1)
        : s.ctx.createScriptProcessor(4096, 1, 1);
      const mudo = s.ctx.createGain();
      mudo.gain.value = 0; // o processor precisa chegar ao destino, mas sem eco audível

      const resample = criarResampler(s.ctx.sampleRate);
      let fila = [];

      proc.onaudioprocess = (e) => {
        try {
          const reamostrado = resample(e.inputBuffer.getChannelData(0));
          for (let i = 0; i < reamostrado.length; i++) fila.push(reamostrado[i]);
          if (fila.length >= AMOSTRAS_POR_ENVIO) {
            postar({ tipo: 'audio_chunk', canal, dados: paraBase64Pcm16(fila) });
            fila = [];
          }
        } catch (_) {}
      };

      fonte.connect(proc);
      proc.connect(mudo);
      mudo.connect(s.ctx.destination);

      s.canais.set(canal, { track, fonte, proc, mudo, flush: () => {
        if (fila.length > 0) {
          postar({ tipo: 'audio_chunk', canal, dados: paraBase64Pcm16(fila) });
          fila = [];
        }
      } });

      track.addEventListener('ended', () => finalizarCanal(canal));
      console.log('[Piloto] hook: canal "' + canal + '" capturando (frame: ' + location.origin + ')');
    } catch (e) {
      console.warn('[Piloto] hook: falha ao anexar canal', e);
    }
  }

  function finalizarCanal(canal) {
    if (!sessao) return;
    const c = sessao.canais.get(canal);
    if (!c) return;
    try { c.flush(); } catch (_) {}
    try { c.fonte.disconnect(); c.proc.disconnect(); c.mudo.disconnect(); } catch (_) {}
    sessao.canais.delete(canal);
    if (sessao.canais.size === 0) encerrarSessao();
  }

  function encerrarSessao() {
    if (!sessao) return;
    for (const [canal] of Array.from(sessao.canais)) finalizarCanal(canal);
    try { sessao.ctx.close(); } catch (_) {}
    sessao = null;
    postar({ tipo: 'audio_fim' });
    postar({ tipo: 'call_ended' });
    console.log('[Piloto] hook: sessão de captura encerrada');
  }

  // ------------------------------------------------------------ patches
  const addTrackOriginal = RTCOriginal.prototype.addTrack;
  RTCOriginal.prototype.addTrack = function (track) {
    try { anexarCanal('atendente', track, this); } catch (_) {}
    return addTrackOriginal.apply(this, arguments);
  };

  function Patched(...args) {
    const pc = new RTCOriginal(...args);
    try {
      pc.addEventListener('track', (ev) => anexarCanal('cliente', ev.track, pc));
      pc.addEventListener('connectionstatechange', () => {
        if (['closed', 'failed', 'disconnected'].includes(pc.connectionState) &&
            sessao && sessao.pcs.has(pc)) {
          encerrarSessao();
        }
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
