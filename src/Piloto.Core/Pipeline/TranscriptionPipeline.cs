using Microsoft.Extensions.Logging;
using Piloto.Core.Abstractions;
using Piloto.Core.Configuration;
using Piloto.Core.Models;

namespace Piloto.Core.Pipeline;

/// <summary>
/// Encadeia as etapas do processamento de uma ligação:
/// transcrição → (normalização + regras) → LLM → grounding → registro persistido.
/// </summary>
public sealed class TranscriptionPipeline
{
    private readonly ITranscriber _transcriber;
    private readonly IRuleExtractor _rules;
    private readonly ILlmExtractor _llm;
    private readonly IGroundingChecker _grounding;
    private readonly IModelCatalog _modelos;
    private readonly AppSettings _settings;
    private readonly Func<ListasFechadas> _listasProvider;
    private readonly ILogger<TranscriptionPipeline> _log;

    public TranscriptionPipeline(
        ITranscriber transcriber,
        IRuleExtractor rules,
        ILlmExtractor llm,
        IGroundingChecker grounding,
        IModelCatalog modelos,
        AppSettings settings,
        Func<ListasFechadas> listasProvider,
        ILogger<TranscriptionPipeline> log)
    {
        _transcriber = transcriber;
        _rules = rules;
        _llm = llm;
        _grounding = grounding;
        _modelos = modelos;
        _settings = settings;
        _listasProvider = listasProvider;
        _log = log;
    }

    public async Task<CallRecord> ProcessarAsync(AudioCapture captura, CancellationToken ct = default)
    {
        var listas = _listasProvider();

        _log.LogInformation("Transcrevendo ligação ({Duracao})", captura.Duracao);
        var transcript = await _transcriber.TranscreverAsync(captura, ct).ConfigureAwait(false);

        _log.LogInformation("Aplicando regras (camada 1)");
        var campos = _rules.Extrair(transcript);

        LlmSummary resumo;
        if (_settings.Llm.Habilitado && _modelos.LlmDisponivel)
        {
            _log.LogInformation("Resumindo com LLM local (camada 2)");
            resumo = await _llm.ResumirAsync(transcript, listas, ct).ConfigureAwait(false);
        }
        else
        {
            _log.LogInformation("LLM desabilitado/ausente — registro sem resumo interpretativo");
            resumo = LlmSummary.Vazio();
        }

        var registro = new CallRecord
        {
            Metadata = captura.Metadata,
            Transcript = transcript,
            Campos = campos,
            Resumo = resumo,
            CriadoEm = DateTimeOffset.Now,
            Duracao = captura.Duracao,
            TempoFalado = transcript.TempoTotalFalado(),
            CaminhoAudioAtendente = captura.CaminhoAtendente,
            CaminhoAudioCliente = captura.CaminhoCliente,
        };

        _log.LogInformation("Grounding (camada 3)");
        _grounding.Aplicar(registro, listas);

        if (registro.PrecisaRevisao)
            _log.LogWarning("Registro marcado para revisão: {Motivos}", string.Join(" | ", registro.MotivosRevisao));

        return registro;
    }
}
