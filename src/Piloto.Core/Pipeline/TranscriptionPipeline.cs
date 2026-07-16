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

        // O LLM é a camada opcional: se falhar (modelo incompatível, corrompido, sem
        // memória), o registro sai sem resumo e marcado para revisão — a transcrição
        // e os campos objetivos, que já custaram a passada do Whisper, são preservados.
        LlmSummary resumo;
        string? erroLlm = null;
        if (_settings.Llm.Habilitado && _modelos.LlmDisponivel)
        {
            // Whisper e LLM nunca precisam coexistir na memória: libera o primeiro antes
            // de carregar o segundo. Custo: recarregar o Whisper na próxima ligação.
            _transcriber.LiberarModelo();

            _log.LogInformation("Resumindo com LLM local (camada 2)");
            try
            {
                resumo = await _llm.ResumirAsync(transcript, listas, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _log.LogError(ex, "Falha no LLM — registro seguirá sem resumo interpretativo");
                resumo = LlmSummary.Vazio();
                erroLlm = ex.Message;
            }
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

        if (erroLlm is not null)
            registro.MarcarRevisao($"Resumo automático indisponível — erro no LLM: {erroLlm}");

        _log.LogInformation("Grounding (camada 3)");
        _grounding.Aplicar(registro, listas);

        if (registro.PrecisaRevisao)
            _log.LogWarning("Registro marcado para revisão: {Motivos}", string.Join(" | ", registro.MotivosRevisao));

        return registro;
    }
}
