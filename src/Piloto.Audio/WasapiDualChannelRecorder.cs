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

    public bool EstaGravando { get; private set; }
    public event EventHandler<bool>? EstadoGravacaoMudou;

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

            _mic.DataAvailable += (_, e) => Escrever(_micWriter, e);
            _loopback.DataAvailable += (_, e) => Escrever(_loopWriter, e);

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
