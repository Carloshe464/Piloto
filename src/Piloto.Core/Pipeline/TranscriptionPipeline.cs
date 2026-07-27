using Microsoft.Extensions.Logging;
using Piloto.Core.Abstractions;
using Piloto.Core.Configuration;
using Piloto.Core.Models;

namespace Piloto.Core.Pipeline;

/// <summary>
/// Encadeia as etapas do processamento de uma ligação:
/// transcrição → (normalização + regras) → LLM → grounding → registro persistido.
/// <para>
/// Depois da migração, as duas etapas do meio são <b>condicionais</b>: quando o servidor
/// devolve <c>campos</c> e <c>resumo</c> (capacidades ligadas em <c>/v1/saude</c>), o piloto
/// usa o que veio pronto e não refaz o trabalho. Enquanto ele não devolve, as camadas locais
/// seguem sendo a única extração que existe — e essa decisão é por chamada, não por versão.
/// </para>
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

    /// <summary>Marcador presente no motivo de revisão quando o resumo falhou — é por ele
    /// que a varredura de resumos pendentes encontra o que completar. ASCII puro de
    /// propósito: o JSON no banco escapa acentos (á...), e o LIKE do SQL precisa casar.</summary>
    public const string MarcadorErroLlm = "erro no LLM";

    /// <summary>
    /// Descarrega o LLM da memória (recarregado na próxima ligação). Devolve true se algo
    /// estava carregado. Chamado pela fila após ociosidade: o Piloto usa memória de pico
    /// durante o processamento, não de posse permanente.
    /// </summary>
    public bool LiberarModelos() => _llm.LiberarModelo();

    public async Task<CallRecord> ProcessarAsync(AudioCapture captura, CancellationToken ct = default)
    {
        var listas = _listasProvider();

        _log.LogInformation("Transcrevendo ligação {Ligacao} ({Duracao})", captura.LigacaoId, captura.Duracao);
        var resultado = await _transcriber.TranscreverAsync(captura, ct).ConfigureAwait(false);
        var transcript = resultado.Transcript;

        // ----------------------------------------------------------- Camada 1: campos
        ObjectiveFields campos;
        if (resultado.Campos is { } camposDoServidor)
        {
            _log.LogInformation("Campos objetivos vieram prontos do servidor ({Origem})", resultado.Origem);
            campos = camposDoServidor;
        }
        else
        {
            _log.LogInformation("Aplicando regras (camada 1)");
            campos = _rules.Extrair(transcript);
        }

        // Contato lido do cadastro do Zendesk entra depois das regras e vence o que a
        // transcrição deu para o mesmo valor: é dado digitado, não dado ouvido. Idempotente,
        // então roda mesmo quando os campos vieram do servidor (que também os ancora).
        ContactMerger.Aplicar(campos, captura.Metadata);

        // ----------------------------------------------------------- Camada 2: resumo
        LlmSummary resumo;
        string? erroLlm = null;
        var dialogoTruncadoNoResumo = false;
        var resumoPendente = false;

        if (resultado.Resumo is { } resumoDoServidor)
        {
            _log.LogInformation("Resumo veio pronto do servidor");
            resumo = resumoDoServidor;
        }
        else if (transcript.EstaVazio)
        {
            // Sem fala não há o que resumir — e carregar o LLM à toa é justamente o passo
            // mais arriscado numa máquina sem memória. O registro sai marcado adiante.
            _log.LogWarning("Transcrição vazia — LLM pulado");
            resumo = LlmSummary.Vazio();
        }
        else if (_settings.Llm.Habilitado && _modelos.LlmDisponivel)
        {
            // O PromptBuilder corta ligações longas (início + fim) para caber no contexto;
            // quem lê o resumo precisa saber que o miolo não foi lido.
            dialogoTruncadoNoResumo = transcript.TextoRotulado().Length > ResumoLimites.MaxCharsDialogo;

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
        else if (_settings.Llm.Habilitado)
        {
            // LLM ligado mas ausente do disco. A transcrição (a parte cara) já está feita e
            // vai para o banco agora; o resumo fica pendente e a varredura o completa quando
            // o modelo existir. Antes da migração isto não acontecia — a fila ficava pausada.
            _log.LogWarning("Modelo LLM ausente — registro salvo sem resumo, para completar depois");
            resumo = LlmSummary.Vazio();
            resumoPendente = true;
        }
        else
        {
            _log.LogInformation("LLM desabilitado — registro sem resumo interpretativo");
            resumo = LlmSummary.Vazio();
        }

        var registro = new CallRecord
        {
            // A identidade nasce na captura e atravessa fila e servidor: um único id do
            // microfone ao banco. (No reprocessamento o uuid original é restaurado.)
            Uuid = captura.LigacaoId,
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
            registro.MarcarRevisao($"Resumo automático indisponível — {MarcadorErroLlm}: {erroLlm}");

        if (resumoPendente)
            registro.MarcarRevisao($"Resumo automático ainda não gerado — {MarcadorErroLlm}: modelo ausente na máquina.");

        if (dialogoTruncadoNoResumo)
            registro.MarcarRevisao("Ligação longa: o resumo automático considerou início e fim do diálogo — o trecho intermediário não foi lido pelo LLM.");

        // Achados do grounding do servidor (número citado que não consta na transcrição,
        // valor fora da lista fechada). São dado; a política de revisão continua sendo daqui.
        foreach (var aviso in resultado.Avisos)
            registro.MarcarRevisao(aviso);

        // Fisicamente impossível falar além do fim da ligação: timestamps suspeitos
        // embaralham a ordem do diálogo (a compressão no transcritor trata o caso comum;
        // isto é a rede de segurança que avisa o humano quando ainda assim escapou).
        if (!transcript.EstaVazio && captura.Duracao > TimeSpan.Zero)
        {
            var fimMax = transcript.Segmentos.Max(s => s.Fim);
            if (fimMax > captura.Duracao * 1.1 + TimeSpan.FromSeconds(2))
                registro.MarcarRevisao(
                    $"Tempos de fala além da duração da ligação (fala até {fimMax:mm\\:ss} numa ligação de {captura.Duracao:mm\\:ss}) — a ordem do diálogo pode estar imprecisa.");
        }

        // Uma ligação real sem NENHUMA fala reconhecível é falha (captura ou transcrição),
        // nunca um registro "válido" de aparência normal. Um único canal mudo, ao contrário,
        // é corriqueiro (loopback que não capturou nada) e não marca revisão sozinho.
        if (transcript.EstaVazio)
        {
            var causa = resultado.CanaisVazios.Count > 0
                ? " Causa relatada: " + string.Join(" | ", resultado.CanaisVazios)
                : "";
            registro.MarcarRevisao(
                "Transcrição vazia — nenhuma fala reconhecível no áudio; confira a gravação preservada." + causa);
        }
        else if (resultado.CanaisVazios.Count > 0)
        {
            _log.LogInformation("Canal sem áudio (não é erro): {Motivos}", string.Join(" | ", resultado.CanaisVazios));
        }

        // Problemas detectados na captura (mic mudo etc.) viram revisão com causa explícita:
        // transcrição ruim por áudio ruim nunca passa como se fosse normal.
        foreach (var aviso in captura.Metadata.AvisosCaptura)
            registro.MarcarRevisao(aviso);

        _log.LogInformation("Grounding (camada 3)");
        _grounding.Aplicar(registro, listas);

        if (registro.PrecisaRevisao)
            _log.LogWarning("Registro marcado para revisão: {Motivos}", string.Join(" | ", registro.MotivosRevisao));

        return registro;
    }

    /// <summary>
    /// Reexecuta apenas as camadas 2 (LLM) e 3 (grounding) sobre um registro já transcrito
    /// cujo resumo falhou — a transcrição salva no banco é a entrada, o áudio não é tocado.
    /// Devolve true quando o resumo foi gerado e aplicado ao registro (o chamador persiste);
    /// false quando as condições ainda não permitem (LLM ausente) — o registro fica como
    /// está e a retentativa acontece mais tarde.
    /// </summary>
    public async Task<bool> TentarResumoPendenteAsync(CallRecord registro, CancellationToken ct = default)
    {
        if (!_settings.Llm.Habilitado || !_modelos.LlmDisponivel) return false;
        if (registro.Transcript.EstaVazio) return false;

        var listas = _listasProvider();

        LlmSummary resumo;
        try
        {
            resumo = await _llm.ResumirAsync(registro.Transcript, listas, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.LogInformation("Resumo pendente adiado (registro {Id}): {Erro}", registro.Id, ex.Message);
            return false;
        }

        registro.Resumo = resumo;
        registro.MotivosRevisao.RemoveAll(m => m.Contains(MarcadorErroLlm, StringComparison.Ordinal));
        registro.PrecisaRevisao = registro.MotivosRevisao.Count > 0;
        _grounding.Aplicar(registro, listas);
        _log.LogInformation("Resumo pendente concluído para o registro {Id}", registro.Id);
        return true;
    }
}
