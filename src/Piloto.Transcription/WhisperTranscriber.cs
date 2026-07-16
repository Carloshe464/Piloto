using Microsoft.Extensions.Logging;
using Piloto.Core.Abstractions;
using Piloto.Core.Configuration;
using Piloto.Core.Models;
using Whisper.net;

namespace Piloto.Transcription;

/// <summary>
/// Transcreve os dois canais com Whisper.net (bindings do whisper.cpp).
/// <para>
/// Sempre <c>task=transcribe</c> + <c>language=pt</c>. O modo <c>translate</c> (que verteria
/// para inglês) nunca é ativado. O <see cref="WhisperFactory"/> é caro de criar e fica em cache
/// enquanto o caminho do modelo não muda.
/// </para>
/// </summary>
public sealed class WhisperTranscriber : ITranscriber, IDisposable
{
    private readonly AppSettings _settings;
    private readonly IModelCatalog _modelos;
    private readonly Func<string?> _glossarioProvider;
    private readonly ILogger<WhisperTranscriber> _log;

    private readonly object _lock = new();
    private WhisperFactory? _factory;
    private string? _caminhoCarregado;

    public WhisperTranscriber(
        AppSettings settings,
        IModelCatalog modelos,
        Func<string?> glossarioProvider,
        ILogger<WhisperTranscriber> log)
    {
        _settings = settings;
        _modelos = modelos;
        _glossarioProvider = glossarioProvider;
        _log = log;
    }

    public async Task<Transcript> TranscreverAsync(AudioCapture captura, CancellationToken ct = default)
    {
        var caminhoModelo = _modelos.CaminhoWhisper
            ?? throw new InvalidOperationException("Modelo Whisper ausente.");

        var factory = ObterFactory(caminhoModelo);
        var glossario = _glossarioProvider();

        var segmentos = new List<TranscriptSegment>();
        segmentos.AddRange(await TranscreverCanalAsync(factory, captura.CaminhoAtendente, Speaker.Atendente, glossario, ct).ConfigureAwait(false));
        segmentos.AddRange(await TranscreverCanalAsync(factory, captura.CaminhoCliente, Speaker.Cliente, glossario, ct).ConfigureAwait(false));

        // A fusão por timestamp acontece no construtor (ordena por início).
        return new Transcript(segmentos);
    }

    private async Task<List<TranscriptSegment>> TranscreverCanalAsync(
        WhisperFactory factory, string caminhoWav, Speaker speaker, string? glossario, CancellationToken ct)
    {
        var resultado = new List<TranscriptSegment>();
        var info = new FileInfo(caminhoWav);
        if (!info.Exists)
        {
            _log.LogWarning("Canal {Speaker}: arquivo ausente {Caminho}", speaker, caminhoWav);
            return resultado;
        }

        // O whisper.cpp exige ao menos ~1 s de áudio; um canal silencioso (loopback sem
        // nada tocando) gera WAV só com cabeçalho e derruba a biblioteca nativa inteira
        // (access violation, sem exceção .NET). PCM 16-bit mono: TaxaHz*2 bytes por segundo.
        var bytesMinimos = 44 + _settings.Audio.TaxaHz * 2;
        if (info.Length < bytesMinimos)
        {
            _log.LogWarning("Canal {Speaker}: áudio vazio ou curto demais ({Bytes} bytes) — canal ignorado",
                speaker, info.Length);
            return resultado;
        }

        var builder = factory.CreateBuilder()
            .WithLanguage(_settings.Whisper.Idioma)   // "pt"
            .WithThreads(_settings.Whisper.Threads);
        // Não chamamos WithTranslate(): mantém task=transcribe.
        if (!string.IsNullOrWhiteSpace(glossario))
            builder = builder.WithPrompt(glossario);   // initial_prompt

        using var processor = builder.Build();
        await using var stream = File.OpenRead(caminhoWav);

        await foreach (var seg in processor.ProcessAsync(stream, ct).ConfigureAwait(false))
        {
            var texto = seg.Text?.Trim();
            if (string.IsNullOrEmpty(texto)) continue;
            resultado.Add(new TranscriptSegment
            {
                Speaker = speaker,
                Inicio = seg.Start,
                Fim = seg.End,
                Texto = texto,
            });
        }

        _log.LogInformation("Canal {Speaker}: {N} segmentos", speaker, resultado.Count);
        return resultado;
    }

    public bool LiberarModelo()
    {
        lock (_lock)
        {
            if (_factory is null) return false;
            _factory.Dispose();
            _factory = null;
            _caminhoCarregado = null;
            _log.LogInformation("Modelo Whisper liberado da memória");
            return true;
        }
    }

    private WhisperFactory ObterFactory(string caminhoModelo)
    {
        lock (_lock)
        {
            if (_factory is not null && _caminhoCarregado == caminhoModelo)
                return _factory;

            _factory?.Dispose();
            _log.LogInformation("Carregando modelo Whisper: {Modelo}", Path.GetFileName(caminhoModelo));
            _factory = WhisperFactory.FromPath(caminhoModelo);
            _caminhoCarregado = caminhoModelo;
            return _factory;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _factory?.Dispose();
            _factory = null;
        }
    }
}
