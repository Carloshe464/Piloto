using Microsoft.Extensions.Logging;
using Piloto.Core.Abstractions;
using Piloto.Core.Configuration;
using Piloto.Core.Models;
using Piloto.Core.Services;

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

    /// <summary>Marcador presente no motivo de revisão quando o resumo falhou — é por ele
    /// que a varredura de resumos pendentes encontra o que completar. ASCII puro de
    /// propósito: o JSON no banco escapa acentos (á...), e o LIKE do SQL precisa casar.</summary>
    public const string MarcadorErroLlm = "erro no LLM";

    /// <summary>
    /// Descarrega Whisper e LLM da memória (recarregados na próxima ligação). Devolve true
    /// se algo estava carregado. Chamado pela fila após ociosidade: o Piloto usa memória de
    /// pico durante o processamento, não de posse permanente.
    /// </summary>
    public bool LiberarModelos()
    {
        var whisper = _transcriber.LiberarModelo();
        var llm = _llm.LiberarModelo();
        return whisper || llm;
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
        if (transcript.EstaVazio)
        {
            // Sem fala não há o que resumir — e carregar o LLM à toa é justamente o passo
            // mais arriscado numa máquina sem memória. O registro sai marcado adiante.
            _log.LogWarning("Transcrição vazia — LLM pulado");
            resumo = LlmSummary.Vazio();
        }
        else if (_settings.Llm.Habilitado && _modelos.LlmDisponivel)
        {
            // Libera o Whisper antes do LLM apenas quando a memória exige: em máquinas
            // com folga, mantê-lo carregado poupa ~20 s de recarga na ligação seguinte;
            // nas de pouca RAM, a folga liberada decide se o resumo roda.
            if (!MemoriaComportaLlmSemLiberarWhisper())
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
            registro.MarcarRevisao($"Resumo automático indisponível — {MarcadorErroLlm}: {erroLlm}");

        // Uma ligação real sem NENHUMA fala reconhecível é falha (captura ou transcrição),
        // nunca um registro "válido" de aparência normal.
        if (transcript.EstaVazio)
            registro.MarcarRevisao("Transcrição vazia — nenhuma fala reconhecível no áudio; confira a gravação preservada.");

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
    /// false quando as condições ainda não permitem (LLM ausente, sem memória agora) — o
    /// registro fica como está e a retentativa acontece mais tarde.
    /// </summary>
    public async Task<bool> TentarResumoPendenteAsync(CallRecord registro, CancellationToken ct = default)
    {
        if (!_settings.Llm.Habilitado || !_modelos.LlmDisponivel) return false;
        if (registro.Transcript.EstaVazio) return false;
        if (SentinelaBloqueiaVarredura()) return false;

        var listas = _listasProvider();
        if (!MemoriaComportaLlmSemLiberarWhisper())
            _transcriber.LiberarModelo();

        LlmSummary resumo;
        try
        {
            ArmarSentinela();
            resumo = await _llm.ResumirAsync(registro.Transcript, listas, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.LogInformation("Resumo pendente adiado (registro {Id}): {Erro}", registro.Id, ex.Message);
            return false;
        }
        finally
        {
            // Sucesso ou falha GERENCIADA desarmam; só um crash do processo (que leva o
            // finally junto) deixa a sentinela no disco — e ela desativa a varredura.
            DesarmarSentinela();
        }

        registro.Resumo = resumo;
        registro.MotivosRevisao.RemoveAll(m => m.Contains(MarcadorErroLlm, StringComparison.Ordinal));
        registro.PrecisaRevisao = registro.MotivosRevisao.Count > 0;
        _grounding.Aplicar(registro, listas);
        _log.LogInformation("Resumo pendente concluído para o registro {Id}", registro.Id);
        return true;
    }

    // ------------------------------------------------------------------ sentinela
    // A varredura de resumos pendentes carrega o LLM com o app ocioso. Se essa carga
    // derruba o processo (crash nativo: instrução ilegal, OOM), SEM proteção o ciclo
    // vira queda a cada abertura do app — foi o que aconteceu em campo na 0.7.8/0.7.9.
    // A sentinela é criada antes de cada tentativa e removida no fim (sucesso ou falha
    // gerenciada); se existir na próxima tentativa, a anterior matou o processo — a
    // varredura fica desarmada NESTA versão do app. Cada atualização ganha uma chance
    // nova (a correção pode ter chegado), rearmando o bloqueio se cair de novo.

    private bool _sentinelaJaLogada;

    private string CaminhoSentinela => Path.Combine(_settings.PastaDadosExpandida, "resumo-pendente.lock");

    private static string VersaoApp =>
        typeof(TranscriptionPipeline).Assembly.GetName().Version?.ToString(3) ?? "?";

    private bool SentinelaBloqueiaVarredura()
    {
        try
        {
            if (!File.Exists(CaminhoSentinela)) return false;

            var versaoSentinela = File.ReadAllText(CaminhoSentinela).Trim();
            if (versaoSentinela != VersaoApp)
            {
                File.Delete(CaminhoSentinela); // versão nova: uma nova chance
                return false;
            }

            if (!_sentinelaJaLogada)
            {
                _sentinelaJaLogada = true;
                _log.LogWarning(
                    "Varredura de resumos pendentes desarmada: a tentativa anterior derrubou o app nesta versão ({Versao}) — reativa na próxima atualização",
                    versaoSentinela);
            }
            return true;
        }
        catch
        {
            return false; // sem leitura confiável, não bloqueia
        }
    }

    private void ArmarSentinela()
    {
        try { File.WriteAllText(CaminhoSentinela, VersaoApp); } catch { /* melhor tentar o resumo */ }
    }

    private void DesarmarSentinela()
    {
        try { if (File.Exists(CaminhoSentinela)) File.Delete(CaminhoSentinela); } catch { /* fica para a próxima */ }
    }

    /// <summary>
    /// True quando a memória atual comporta carregar o LLM sem descartar o Whisper.
    /// Usa a mesma régua do guard do extractor (arquivo + máx(768 MB, 1/2)). Sem leitura
    /// confiável de memória, responde false — o caminho conservador (liberar) prevalece.
    /// </summary>
    private bool MemoriaComportaLlmSemLiberarWhisper()
    {
        var caminho = _modelos.CandidatosLlm.FirstOrDefault();
        if (caminho is null)
            return true;

        if (!MemoriaDisponivel.TentarObter(out var fisica, out var commit))
            return false;

        try
        {
            var tamanho = new FileInfo(caminho).Length;
            var necessario = tamanho + Math.Max(768L * 1024 * 1024, tamanho / 2);
            return Math.Min(fisica, commit) >= necessario;
        }
        catch (IOException)
        {
            return false;
        }
    }
}
