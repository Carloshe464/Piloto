using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Piloto.Core.Abstractions;
using Piloto.Core.Configuration;
using Piloto.Core.Models;

namespace Piloto.Audio;

/// <summary>
/// Gravador WASAPI de 2 canais: microfone do atendente (captura) e loopback do navegador
/// (saída de áudio). Como o headset é obrigatório, o loopback do dispositivo de renderização
/// carrega apenas a voz do cliente vinda do Zendesk — sem contaminação de caixas de som.
/// <para>
/// Cada canal é gravado no formato nativo do dispositivo e, ao parar, convertido para
/// 16 kHz mono PCM 16-bit — a entrada esperada pelo Whisper.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WasapiDualChannelRecorder : IAudioRecorder
{
    private readonly AppSettings _settings;
    private readonly ILogger<WasapiDualChannelRecorder> _log;
    private readonly object _lock = new();

    private WasapiCapture? _mic;
    private WasapiLoopbackCapture? _loopback;
    private WaveFileWriter? _micWriter;
    private WaveFileWriter? _loopWriter;

    private string? _micTemp;
    private string? _loopTemp;
    private DateTimeOffset _inicio;
    private CallMetadata _metadata = CallMetadata.Vazio();

    // Pico absoluto observado em cada canal durante a chamada (0..1). Detecta microfone
    // mudo/baixíssimo — a causa mais comum de "transcrição ruim" que ninguém percebe.
    private float _picoMic;
    private float _picoLoop;
    private Timer? _timerNivel;

    private const float PicoMicMinimo = 0.02f;   // abaixo disto a fala é inaproveitável
    private const float PicoLoopMinimo = 0.005f; // loopback totalmente mudo

    public bool EstaGravando { get; private set; }
    public event EventHandler<bool>? EstadoGravacaoMudou;
    public event EventHandler<string>? AvisoCaptura;

    public WasapiDualChannelRecorder(AppSettings settings, ILogger<WasapiDualChannelRecorder> log)
    {
        _settings = settings;
        _log = log;
    }

    public void Iniciar(CallMetadata metadata)
    {
        lock (_lock)
        {
            if (EstaGravando) return;
            Directory.CreateDirectory(_settings.PastaAudio);

            _metadata = metadata;
            _inicio = DateTimeOffset.Now;
            var stamp = _inicio.ToString("yyyyMMdd-HHmmss");
            var id = Guid.NewGuid().ToString("N")[..8];
            _micTemp = Path.Combine(_settings.PastaAudio, $"{stamp}-{id}-atendente-raw.wav");
            _loopTemp = Path.Combine(_settings.PastaAudio, $"{stamp}-{id}-cliente-raw.wav");

            _mic = new WasapiCapture(); // dispositivo de captura padrão (microfone do headset)
            _loopback = new WasapiLoopbackCapture(); // dispositivo de renderização padrão (saída do headset)

            _micWriter = new WaveFileWriter(_micTemp, _mic.WaveFormat);
            _loopWriter = new WaveFileWriter(_loopTemp, _loopback.WaveFormat);

            var fmtMic = _mic.WaveFormat;
            var fmtLoop = _loopback.WaveFormat;
            _mic.DataAvailable += (_, e) =>
            {
                _picoMic = Math.Max(_picoMic, PicoDoBuffer(e, fmtMic));
                Escrever(_micWriter, e);
            };
            _loopback.DataAvailable += (_, e) =>
            {
                _picoLoop = Math.Max(_picoLoop, PicoDoBuffer(e, fmtLoop));
                Escrever(_loopWriter, e);
            };

            _picoMic = 0f;
            _picoLoop = 0f;

            // Checagem única aos 8 s: se o microfone segue mudo, avisa o atendente
            // enquanto ainda dá para ajeitar o headset — em vez de descobrir no fim.
            _timerNivel = new Timer(_ =>
            {
                if (EstaGravando && _picoMic < PicoLoopMinimo)
                    AvisoCaptura?.Invoke(this,
                        "Nenhum áudio no microfone até agora — verifique o headset.");
            }, null, TimeSpan.FromSeconds(8), Timeout.InfiniteTimeSpan);

            _mic.StartRecording();
            _loopback.StartRecording();

            EstaGravando = true;
            EstadoGravacaoMudou?.Invoke(this, true);
            _log.LogInformation("Gravação iniciada (ticket {Ticket})", metadata.TicketId ?? "—");
        }
    }

    private void Escrever(WaveFileWriter? writer, WaveInEventArgs e)
    {
        if (writer is null) return;
        lock (_lock)
        {
            if (writer is { CanWrite: true })
                writer.Write(e.Buffer, 0, e.BytesRecorded);
        }
    }

    public AudioCapture Parar()
    {
        lock (_lock)
        {
            if (!EstaGravando)
                throw new InvalidOperationException("Nenhuma gravação em andamento.");

            var fim = DateTimeOffset.Now;
            PararCapturas();

            var micFinal = SubstituirSufixo(_micTemp!, "-raw", "");
            var loopFinal = SubstituirSufixo(_loopTemp!, "-raw", "");

            ConverterPara16kMono(_micTemp!, micFinal);
            ConverterPara16kMono(_loopTemp!, loopFinal);
            ApagarSilenciosamente(_micTemp!);
            ApagarSilenciosamente(_loopTemp!);

            // Voz baixa é a causa nº 1 de transcrição ruim com captura "ok".
            NormalizarVolume(micFinal);
            NormalizarVolume(loopFinal);

            if (_picoMic < PicoMicMinimo)
                _metadata.AvisosCaptura.Add(
                    "Áudio do atendente ausente ou baixíssimo na gravação — verifique o microfone do headset.");
            if (_picoLoop < PicoLoopMinimo)
                _metadata.AvisosCaptura.Add(
                    "Nenhum áudio do cliente foi captado — verifique a saída de som do headset.");

            EstaGravando = false;
            EstadoGravacaoMudou?.Invoke(this, false);

            _metadata.IniciadaEm ??= _inicio;
            _metadata.EncerradaEm ??= fim;

            _log.LogInformation("Gravação encerrada ({Duracao})", fim - _inicio);
            return new AudioCapture
            {
                CaminhoAtendente = micFinal,
                CaminhoCliente = loopFinal,
                IniciadaEm = _inicio,
                EncerradaEm = fim,
                Metadata = _metadata,
            };
        }
    }

    public void Descartar()
    {
        lock (_lock)
        {
            if (!EstaGravando) return;
            PararCapturas();
            ApagarSilenciosamente(_micTemp);
            ApagarSilenciosamente(_loopTemp);
            EstaGravando = false;
            EstadoGravacaoMudou?.Invoke(this, false);
            _log.LogInformation("Gravação descartada a pedido do atendente");
        }
    }

    private void PararCapturas()
    {
        _timerNivel?.Dispose();
        _timerNivel = null;

        try { _mic?.StopRecording(); } catch { /* ignore */ }
        try { _loopback?.StopRecording(); } catch { /* ignore */ }

        _micWriter?.Dispose();
        _loopWriter?.Dispose();
        _micWriter = null;
        _loopWriter = null;

        _mic?.Dispose();
        _loopback?.Dispose();
        _mic = null;
        _loopback = null;
    }

    /// <summary>Converte um WAV nativo em 16 kHz mono PCM 16-bit.</summary>
    private void ConverterPara16kMono(string origem, string destino)
    {
        try
        {
            using var reader = new AudioFileReader(origem);
            ISampleProvider mono = reader.WaveFormat.Channels switch
            {
                1 => reader,
                2 => new StereoToMonoSampleProvider(reader) { LeftVolume = 0.5f, RightVolume = 0.5f },
                _ => new MultiplexingSampleProvider(new ISampleProvider[] { reader }, 1),
            };

            ISampleProvider resample = reader.WaveFormat.SampleRate == _settings.Audio.TaxaHz
                ? mono
                : new WdlResamplingSampleProvider(mono, _settings.Audio.TaxaHz);

            WaveFileWriter.CreateWaveFile16(destino, resample);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Falha ao converter {Origem}; mantendo o WAV nativo", origem);
            File.Copy(origem, destino, overwrite: true);
        }
    }

    /// <summary>Pico absoluto (0..1) do buffer capturado. Formato desconhecido devolve 1
    /// para nunca gerar alarme falso de "sem áudio".</summary>
    private static float PicoDoBuffer(WaveInEventArgs e, WaveFormat formato)
    {
        if (e.BytesRecorded <= 0) return 0f;

        if (formato.Encoding == WaveFormatEncoding.IeeeFloat && formato.BitsPerSample == 32)
        {
            var pico = 0f;
            for (var i = 0; i + 4 <= e.BytesRecorded; i += 4)
                pico = Math.Max(pico, Math.Abs(BitConverter.ToSingle(e.Buffer, i)));
            return pico;
        }

        if (formato.Encoding == WaveFormatEncoding.Pcm && formato.BitsPerSample == 16)
        {
            var pico = 0f;
            for (var i = 0; i + 2 <= e.BytesRecorded; i += 2)
                pico = Math.Max(pico, Math.Abs(BitConverter.ToInt16(e.Buffer, i)) / 32768f);
            return pico;
        }

        return 1f;
    }

    /// <summary>
    /// Eleva o volume do WAV até pico ~0,9 quando a gravação saiu baixa. Silêncio absoluto
    /// não é amplificado (só subiria o ruído) e áudio já alto não é tocado.
    /// </summary>
    private void NormalizarVolume(string caminho)
    {
        try
        {
            if (!File.Exists(caminho)) return;

            float pico = 0f;
            using (var reader = new AudioFileReader(caminho))
            {
                var buf = new float[16384];
                int lidos;
                while ((lidos = reader.Read(buf, 0, buf.Length)) > 0)
                    for (var i = 0; i < lidos; i++)
                        pico = Math.Max(pico, Math.Abs(buf[i]));
            }

            if (pico < PicoMicMinimo || pico > 0.85f) return;

            var ganho = Math.Min(0.9f / pico, 20f);
            var temp = caminho + ".norm";
            using (var reader = new AudioFileReader(caminho))
            {
                var vol = new VolumeSampleProvider(reader) { Volume = ganho };
                WaveFileWriter.CreateWaveFile16(temp, vol);
            }
            File.Delete(caminho);
            File.Move(temp, caminho);
            _log.LogInformation("Volume normalizado ({Arquivo}): pico {Pico:F2} -> 0,90",
                Path.GetFileName(caminho), pico);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Falha ao normalizar volume de {Caminho}", caminho);
        }
    }

    private static string SubstituirSufixo(string caminho, string de, string para)
    {
        var dir = Path.GetDirectoryName(caminho)!;
        var nome = Path.GetFileNameWithoutExtension(caminho).Replace(de, para);
        return Path.Combine(dir, nome + ".wav");
    }

    private static void ApagarSilenciosamente(string? caminho)
    {
        if (string.IsNullOrWhiteSpace(caminho)) return;
        try { if (File.Exists(caminho)) File.Delete(caminho); } catch { /* ignore */ }
    }
}
