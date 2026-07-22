using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Piloto.Core.Abstractions;
using Piloto.Core.Configuration;
using Piloto.Core.Models;
using Piloto.Core.Services;
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

    /// <summary>Beam search só compensa em modelos maiores que small: a máquina que comporta
    /// o medium também paga o custo extra de decodificação sem atrasar a fila de forma visível.</summary>
    private const long LimiarModeloGrandeBytes = 300_000_000;

    /// <summary>Segmentos com probabilidade média abaixo disto são alucinação de
    /// silêncio/ruído ("www...", frases em inglês) com muito mais frequência do que fala real.</summary>
    private const float ConfiancaMinima = 0.30f;

    /// <summary>Silêncio/ruído também vira texto que o Whisper decorou do treinamento —
    /// URLs soltas, créditos de legenda, etiquetas como "[MÚSICA DE FUNDO]" — e nesses a
    /// probabilidade vem ALTA (o modelo confia no que decorou), então o filtro de
    /// confiança não pega: o padrão textual pega. Ninguém dita uma URL como fala inteira
    /// numa ligação; "www ponto..." falado vem transcrito por extenso, não como URL montada.</summary>
    private static readonly Regex PadraoAlucinacao = new(
        @"^\W*(?:www\.|https?://)\S+\W*$|amara\.org|legendas pela comunidade|subtitles by|legendado por" +
        @"|^\W*[\[\(][^\]\)]{1,80}[\]\)]\W*$|^[\W_]+$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // NUNCA usar WithSuppressRegex aqui: o whisper.cpp compila e roda o regex contra os
    // ~51 mil tokens do vocabulário A CADA token decodificado — numa CPU fraca de 4
    // threads, 1 min de ligação passou de segundos para muitos minutos (regressão da
    // 0.7.3). O filtro pós-decode (PadraoAlucinacao) pega as mesmas etiquetas de graça.

    public async Task<Transcript> TranscreverAsync(AudioCapture captura, CancellationToken ct = default)
    {
        var caminhoModelo = EscolherModelo();
        var factory = ObterFactory(caminhoModelo);
        var glossario = _glossarioProvider();
        var usarBeam = new FileInfo(caminhoModelo).Length > LimiarModeloGrandeBytes
                       && Hardware.CpuComportaBeam;

        var segmentos = new List<TranscriptSegment>();
        segmentos.AddRange(await TranscreverCanalAsync(factory, usarBeam, captura.CaminhoAtendente, Speaker.Atendente, glossario, ct).ConfigureAwait(false));
        segmentos.AddRange(await TranscreverCanalAsync(factory, usarBeam, captura.CaminhoCliente, Speaker.Cliente, glossario, ct).ConfigureAwait(false));

        // A fusão por timestamp acontece no construtor (ordena por início).
        return new Transcript(segmentos);
    }

    /// <summary>
    /// Usa o maior modelo que cabe na memória desta máquina (medium em 12 GB, small em
    /// 4 GB). Se um candidato já está carregado, permanece nele — recarga é cara.
    /// </summary>
    private string EscolherModelo()
    {
        var candidatos = _modelos.CandidatosWhisper;
        if (candidatos.Count == 0)
            throw new InvalidOperationException("Modelo Whisper ausente.");

        lock (_lock)
        {
            if (_caminhoCarregado is not null && candidatos.Contains(_caminhoCarregado))
                return _caminhoCarregado;
        }

        foreach (var candidato in candidatos)
        {
            if (MemoriaComporta(candidato))
                return candidato;
            _log.LogInformation("Modelo Whisper {Modelo} não cabe na memória; tentando o próximo",
                Path.GetFileName(candidato));
        }
        return candidatos[^1]; // menor disponível: transcrever é o núcleo do produto, sempre tenta
    }

    private static bool MemoriaComporta(string caminho)
    {
        if (!MemoriaDisponivel.TentarObter(out var fisica, out var commit))
            return true; // sem leitura confiável, não bloqueia
        // Pesos + buffers de computação do whisper.cpp: ~2x o arquivo, mais folga fixa.
        var necessario = new FileInfo(caminho).Length * 2 + 256L * 1024 * 1024;
        return Math.Min(fisica, commit) >= necessario;
    }

    private async Task<List<TranscriptSegment>> TranscreverCanalAsync(
        WhisperFactory factory, bool usarBeam, string caminhoWav, Speaker speaker, string? glossario, CancellationToken ct)
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
            .WithThreads(Hardware.ResolverThreads(_settings.Whisper.Threads))
            // Cada janela de 30 s é decodificada sem herdar o texto da anterior: uma
            // alucinação num trecho silencioso não contamina o resto da ligação.
            .WithNoContext()
            .WithProbabilities();
        // Não chamamos WithTranslate(): mantém task=transcribe.
        if (!string.IsNullOrWhiteSpace(glossario))
            builder = builder.WithPrompt(glossario);   // initial_prompt
        if (usarBeam)
            builder = builder.WithBeamSearchSamplingStrategy().ParentBuilder; // beam padrão da lib (5)

        using var processor = builder.Build();
        await using var stream = File.OpenRead(caminhoWav);

        var descartados = 0;
        await foreach (var seg in processor.ProcessAsync(stream, ct).ConfigureAwait(false))
        {
            var texto = seg.Text?.Trim();
            if (string.IsNullOrEmpty(texto)) continue;

            // Alucinação de silêncio/ruído vem com confiança baixa; melhor perder um
            // murmúrio real do que poluir o diálogo com lixo. Probability 0 = não
            // calculada pela lib — nesse caso o segmento é mantido.
            if (seg.Probability > 0f && seg.Probability < ConfiancaMinima)
            {
                descartados++;
                continue;
            }

            if (PadraoAlucinacao.IsMatch(texto))
            {
                descartados++;
                _log.LogInformation("Canal {Speaker}: alucinação descartada: {Texto}", speaker, texto);
                continue;
            }

            resultado.Add(new TranscriptSegment
            {
                Speaker = speaker,
                Inicio = seg.Start,
                Fim = seg.End,
                Texto = texto,
                // Guardada no registro: um trecho de 35% precisa aparecer diferente de um de 95%.
                Confianca = seg.Probability > 0f ? seg.Probability : null,
            });
        }

        // Loop de alucinação: a mesma frase inocente repetida em série sobre música/ruído
        // vem com confiança alta e texto normal — só a série idêntica denuncia.
        var repetidos = TranscriptSanitizer.ColapsarRepeticoes(resultado);
        if (repetidos > 0)
        {
            descartados += repetidos;
            _log.LogInformation("Canal {Speaker}: {N} repetição(ões) idêntica(s) consecutivas descartadas (loop de alucinação)", speaker, repetidos);
        }

        // Timestamps além do fim do áudio embaralham a intercalação do diálogo e inflam
        // o TempoFalado (registros 29 e 34) — comprime de volta ao teto da duração real.
        var duracaoAudio = DuracaoDoWav(info);
        var fator = duracaoAudio is { } d ? TranscriptSanitizer.ComprimirTimestamps(resultado, d) : null;
        if (fator is { } f)
            _log.LogWarning("Canal {Speaker}: timestamps além do áudio ({FimMax:0.#} s num áudio de {Duracao:0.#} s) — comprimidos por fator {Fator:0.00}",
                speaker, resultado.Max(s => s.Fim).TotalSeconds / f, duracaoAudio!.Value.TotalSeconds, f);

        if (descartados > 0)
            _log.LogInformation("Canal {Speaker}: {N} segmento(s) descartado(s) (baixa confiança ou alucinação)", speaker, descartados);
        _log.LogInformation("Canal {Speaker}: {N} segmentos", speaker, resultado.Count);
        return resultado;
    }

    /// <summary>Duração real do WAV pelo cabeçalho (byte rate no offset 28 do formato
    /// canônico que os gravadores do Piloto escrevem). Null se o cabeçalho não for o esperado —
    /// nesse caso a compressão de timestamps é pulada, nunca aplicada com base errada.</summary>
    private static TimeSpan? DuracaoDoWav(FileInfo info)
    {
        try
        {
            using var fs = info.OpenRead();
            Span<byte> cab = stackalloc byte[44];
            if (fs.Read(cab) != 44) return null;
            if (cab[0] != (byte)'R' || cab[1] != (byte)'I' || cab[2] != (byte)'F' || cab[3] != (byte)'F') return null;

            var byteRate = BitConverter.ToInt32(cab.Slice(28, 4));
            if (byteRate <= 0) return null;
            return TimeSpan.FromSeconds((info.Length - 44) / (double)byteRate);
        }
        catch (IOException)
        {
            return null;
        }
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
