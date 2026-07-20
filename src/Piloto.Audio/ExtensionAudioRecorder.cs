using Microsoft.Extensions.Logging;
using NAudio.Wave;
using Piloto.Core.Configuration;
using Piloto.Core.Models;

namespace Piloto.Audio;

/// <summary>
/// Monta a gravação a partir do áudio transmitido pela extensão (hook WebRTC no
/// softphone): dois WAVs 16 kHz mono PCM16, um por canal, prontos para o pipeline.
/// A sessão é automática — começa e termina nas fronteiras reais da chamada,
/// detectadas pelo hook, sem o atendente clicar em nada.
/// </summary>
public sealed class ExtensionAudioRecorder
{
    /// <summary>Pico mínimo de um chunk para contar como "sinal audível". Ruído de linha
    /// fica abaixo; fala em telefonia fica bem acima.</summary>
    private const float PicoAudivel = 0.02f;

    /// <summary>Canal com menos que isto de sinal audível na chamada inteira é captura
    /// falha (lado mudo), não conversa — um único pico de ruído não desarma o aviso.</summary>
    private const double SegundosAudiveisMinimos = 1.0;

    private static readonly TimeSpan DuracaoMinima = TimeSpan.FromSeconds(1);

    private readonly AppSettings _settings;
    private readonly ILogger<ExtensionAudioRecorder> _log;
    private readonly object _lock = new();

    private WaveFileWriter? _atendente;
    private WaveFileWriter? _cliente;
    private string? _caminhoAtendente;
    private string? _caminhoCliente;
    private int _taxa = 16000;
    private double _segundosAudiveisAtendente;
    private double _segundosAudiveisCliente;
    private DateTimeOffset _inicio;
    private CallMetadata _metadata = CallMetadata.Vazio();

    public ExtensionAudioRecorder(AppSettings settings, ILogger<ExtensionAudioRecorder> log)
    {
        _settings = settings;
        _log = log;
    }

    public bool Ativa { get; private set; }

    public void Iniciar(CallMetadata metadata, int taxa)
    {
        lock (_lock)
        {
            if (Ativa) return;
            Directory.CreateDirectory(_settings.PastaAudio);

            _metadata = metadata;
            _taxa = taxa;
            _inicio = DateTimeOffset.Now;
            _segundosAudiveisAtendente = 0;
            _segundosAudiveisCliente = 0;

            var stamp = _inicio.ToString("yyyyMMdd-HHmmss");
            var id = Guid.NewGuid().ToString("N")[..8];
            _caminhoAtendente = Path.Combine(_settings.PastaAudio, $"{stamp}-{id}-atendente.wav");
            _caminhoCliente = Path.Combine(_settings.PastaAudio, $"{stamp}-{id}-cliente.wav");

            var formato = new WaveFormat(taxa, 16, 1);
            _atendente = new WaveFileWriter(_caminhoAtendente, formato);
            _cliente = new WaveFileWriter(_caminhoCliente, formato);

            Ativa = true;
            _log.LogInformation("Captura pela extensão iniciada (ticket {Ticket}, {Taxa} Hz)",
                metadata.TicketId ?? "—", taxa);
        }
    }

    public void ReceberChunk(string canal, byte[] dados)
    {
        lock (_lock)
        {
            if (!Ativa || dados.Length == 0) return;

            var writer = canal == "atendente" ? _atendente : _cliente;
            if (writer is null) return;
            writer.Write(dados, 0, dados.Length);

            if (PicoPcm16(dados) < PicoAudivel) return;
            var segundos = dados.Length / (double)(_taxa * 2);
            if (canal == "atendente") _segundosAudiveisAtendente += segundos;
            else _segundosAudiveisCliente += segundos;
        }
    }

    /// <summary>
    /// Fecha a sessão e devolve a captura pronta para a fila — ou null quando não houve
    /// áudio aproveitável (chamada com menos de 1 s: toque rejeitado, glitch do softphone).
    /// </summary>
    public AudioCapture? Encerrar()
    {
        lock (_lock)
        {
            if (!Ativa) return null;

            var fim = DateTimeOffset.Now;
            var duracaoAtendente = TimeSpan.FromSeconds((double)(_atendente?.Length ?? 0) / (_taxa * 2));
            var duracaoCliente = TimeSpan.FromSeconds((double)(_cliente?.Length ?? 0) / (_taxa * 2));
            FecharWriters();
            Ativa = false;

            if (duracaoAtendente < DuracaoMinima && duracaoCliente < DuracaoMinima)
            {
                ApagarSilenciosamente(_caminhoAtendente);
                ApagarSilenciosamente(_caminhoCliente);
                _log.LogInformation("Captura pela extensão descartada (menos de 1 s de áudio)");
                return null;
            }

            if (_segundosAudiveisAtendente < SegundosAudiveisMinimos)
                _metadata.AvisosCaptura.Add(
                    $"Áudio do atendente ausente ou inaudível na captura da extensão ({_segundosAudiveisAtendente:0.0} s de sinal) — verifique o microfone.");
            if (_segundosAudiveisCliente < SegundosAudiveisMinimos)
                _metadata.AvisosCaptura.Add(
                    $"Áudio do cliente ausente ou inaudível na captura da extensão ({_segundosAudiveisCliente:0.0} s de sinal) — a fala do cliente na transcrição não é confiável.");

            _metadata.IniciadaEm ??= _inicio;
            _metadata.EncerradaEm ??= fim;

            _log.LogInformation("Captura pela extensão encerrada ({Duracao})", fim - _inicio);
            return new AudioCapture
            {
                CaminhoAtendente = _caminhoAtendente!,
                CaminhoCliente = _caminhoCliente!,
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
            if (!Ativa) return;
            FecharWriters();
            Ativa = false;
            ApagarSilenciosamente(_caminhoAtendente);
            ApagarSilenciosamente(_caminhoCliente);
            _log.LogInformation("Captura pela extensão descartada");
        }
    }

    private void FecharWriters()
    {
        _atendente?.Dispose();
        _cliente?.Dispose();
        _atendente = null;
        _cliente = null;
    }

    private static float PicoPcm16(byte[] dados)
    {
        var pico = 0f;
        for (var i = 0; i + 2 <= dados.Length; i += 2)
            pico = Math.Max(pico, Math.Abs(BitConverter.ToInt16(dados, i)) / 32768f);
        return pico;
    }

    private static void ApagarSilenciosamente(string? caminho)
    {
        if (string.IsNullOrWhiteSpace(caminho)) return;
        try { if (File.Exists(caminho)) File.Delete(caminho); } catch { /* ignore */ }
    }
}
