using System.Text;
using LLama;
using LLama.Common;
using LLama.Native;
using LLama.Sampling;
using Microsoft.Extensions.Logging;
using Piloto.Core.Abstractions;
using Piloto.Core.Configuration;
using Piloto.Core.Models;
using Piloto.Core.Services;

namespace Piloto.Llm;

/// <summary>
/// Camada 2 — resumo com LLM local (Gemma 3 via LLamaSharp/llama.cpp).
/// Saída JSON determinística (temperatura 0) e forçada por gramática GBNF
/// (GbnfGrammarBuilder): o modelo escolhe valores das listas fechadas em vez de redigir.
/// O modelo é escolhido entre os candidatos do catálogo pelo que cabe na RAM da máquina
/// (4B onde há memória, 1B onde não há). Os pesos ficam em cache enquanto o caminho não muda.
/// </summary>
public sealed class LlamaSummaryExtractor : ILlmExtractor, IDisposable
{
    private readonly AppSettings _settings;
    private readonly IModelCatalog _modelos;
    private readonly PromptBuilder _prompt;
    private readonly ILogger<LlamaSummaryExtractor> _log;

    private readonly object _lock = new();
    private LLamaWeights? _weights;
    private ModelParams? _parameters;
    private string? _caminhoCarregado;

    public LlamaSummaryExtractor(
        AppSettings settings,
        IModelCatalog modelos,
        PromptBuilder prompt,
        ILogger<LlamaSummaryExtractor> log)
    {
        _settings = settings;
        _modelos = modelos;
        _prompt = prompt;
        _log = log;
    }

    public async Task<LlmSummary> ResumirAsync(Transcript transcript, ListasFechadas listas, CancellationToken ct = default)
    {
        if (transcript.EstaVazio)
            return LlmSummary.Vazio();

        var caminho = EscolherModelo();
        var (weights, parameters) = ObterWeights(caminho);
        var executor = new StatelessExecutor(weights, parameters);

        var prompt = _prompt.Construir(transcript, listas);

        // Saída determinística (temperatura 0) e restrita por gramática GBNF: o modelo só
        // consegue emitir o JSON com as seis chaves, e motivo/produto/status saem das listas
        // fechadas (ou null). O grounding (camada 3) continua como última barreira.
        // "gramatica": false no config desliga a restrição (válvula de escape em campo).
        using var pipeline = new DefaultSamplingPipeline
        {
            Temperature = _settings.Llm.Temperatura,
            Grammar = _settings.Llm.Gramatica
                ? new Grammar(GbnfGrammarBuilder.Construir(listas), "root")
                : null,
        };

        var inference = new InferenceParams
        {
            SamplingPipeline = pipeline,
            MaxTokens = 700,
            AntiPrompts = new List<string> { "<end_of_turn>", "<eos>", "</s>" },
        };

        var sb = new StringBuilder();
        await foreach (var token in executor.InferAsync(prompt, inference, ct).ConfigureAwait(false))
            sb.Append(token);

        var bruto = sb.ToString();
        if (LlmResponseParser.ExtrairJson(bruto) is null)
        {
            // Não deveria acontecer com a gramática ligada; se acontecer (saída vazia,
            // truncada etc.), deixa evidência no log e marca o registro para revisão.
            var trecho = bruto.Length > 300 ? bruto[..300] + "…" : bruto;
            _log.LogWarning("LLM não devolveu JSON interpretável; início da saída: {Trecho}",
                trecho.Length == 0 ? "(vazia)" : trecho);
            throw new InvalidOperationException("O LLM não devolveu JSON interpretável.");
        }

        var resumo = LlmResponseParser.Parse(bruto);
        _log.LogInformation("LLM: motivo={Motivo} produto={Produto} status={Status}",
            resumo.MotivoContato ?? "—", resumo.Produto ?? "—", resumo.Status ?? "—");
        return resumo;
    }

    /// <summary>
    /// Escolhe entre os candidatos do catálogo (configurado primeiro, depois por tamanho)
    /// o primeiro que cabe na memória desta máquina. Se um candidato já está carregado,
    /// permanece nele — trocar de modelo a cada chamada custaria uma recarga inteira.
    /// </summary>
    private string EscolherModelo()
    {
        var candidatos = _modelos.CandidatosLlm;
        if (candidatos.Count == 0)
            throw new InvalidOperationException("Modelo LLM ausente.");

        lock (_lock)
        {
            if (_caminhoCarregado is not null && candidatos.Contains(_caminhoCarregado))
                return _caminhoCarregado;
        }

        foreach (var candidato in candidatos)
        {
            if (!MemoriaComporta(candidato))
            {
                _log.LogInformation("Modelo {Modelo} não cabe na memória desta máquina; tentando o próximo",
                    Path.GetFileName(candidato));
                continue;
            }
            if (!string.Equals(candidato, candidatos[0], StringComparison.OrdinalIgnoreCase))
                _log.LogWarning("Usando modelo alternativo {Modelo} (o preferido não cabe na RAM)",
                    Path.GetFileName(candidato));
            return candidato;
        }

        // Nenhum cabe: segue com o menor — GarantirMemoriaParaCarga lança com os números.
        return candidatos[^1];
    }

    private (LLamaWeights, ModelParams) ObterWeights(string caminho)
    {
        lock (_lock)
        {
            if (_weights is not null && _parameters is not null && _caminhoCarregado == caminho)
                return (_weights, _parameters);

            _weights?.Dispose();
            ConfigurarLogNativo();
            GarantirMemoriaParaCarga(caminho);
            _log.LogInformation("Carregando modelo LLM: {Modelo}", Path.GetFileName(caminho));

            var parameters = new ModelParams(caminho)
            {
                ContextSize = (uint)_settings.Llm.Contexto,
                GpuLayerCount = 0, // CPU
                Threads = _settings.Llm.Threads,
            };
            var weights = LLamaWeights.LoadFromFile(parameters);

            _weights = weights;
            _parameters = parameters;
            _caminhoCarregado = caminho;
            return (weights, parameters);
        }
    }

    private static bool _logNativoConfigurado;

    /// <summary>
    /// Roteia o log interno do llama.cpp para o log do app. Em crash nativo durante a carga
    /// (que não gera stack .NET), a última linha nativa no arquivo aponta onde a carga morreu.
    /// </summary>
    private void ConfigurarLogNativo()
    {
        if (_logNativoConfigurado) return;
        try
        {
            NativeLogConfig.llama_log_set(_log);
            _logNativoConfigurado = true;
        }
        catch (Exception ex)
        {
            _logNativoConfigurado = true; // não retenta a cada carga
            _log.LogWarning(ex, "Não foi possível rotear o log nativo do llama.cpp");
        }
    }

    /// <summary>
    /// Falta de memória durante a carga do modelo derruba o processo inteiro (abort/access
    /// violation dentro do llama.cpp — nenhum try/catch .NET alcança). Checar antes converte
    /// o crash em exceção gerenciada, que o pipeline trata como "registro sem resumo".
    /// </summary>
    private void GarantirMemoriaParaCarga(string caminho)
    {
        if (!MemoriaDisponivel.TentarObter(out var fisica, out var commit))
            return; // sem leitura confiável, não bloqueia a carga

        var necessario = NecessarioParaCarga(caminho);

        // Sempre logado: se a carga morrer mesmo assim, estes números são a evidência.
        _log.LogInformation(
            "Memória antes da carga do LLM: {FisicaMb} MB físicos livres, {CommitMb} MB de commit livres, necessários ~{NecessarioMb} MB",
            fisica / 1_048_576, commit / 1_048_576, necessario / 1_048_576);

        // O commit (RAM + pagefile) também limita: com pagefile pequeno/desativado, o malloc
        // do llama.cpp falha e aborta o processo mesmo havendo RAM física livre.
        var limitante = Math.Min(fisica, commit);
        if (limitante >= necessario)
            return;

        _log.LogWarning(
            "Memória insuficiente para o LLM: {DisponivelMb} MB utilizáveis, necessários ~{NecessarioMb} MB — camada 2 pulada",
            limitante / 1_048_576, necessario / 1_048_576);
        throw new InvalidOperationException(
            $"Memória insuficiente para carregar o modelo LLM " +
            $"({limitante / 1_048_576} MB utilizáveis, necessários ~{necessario / 1_048_576} MB).");
    }

    private bool MemoriaComporta(string caminho)
    {
        if (!MemoriaDisponivel.TentarObter(out var fisica, out var commit))
            return true; // sem leitura confiável, não bloqueia
        return Math.Min(fisica, commit) >= NecessarioParaCarga(caminho);
    }

    /// <summary>
    /// Além dos pesos (mmap, mas viram working set ao inferir), o llama.cpp aloca KV cache
    /// + buffers de computação, que crescem com o modelo. Margem proporcional: não afrouxa
    /// a proteção do 4B e ainda deixa o Gemma 1B caber em máquinas de 4 GB.
    /// </summary>
    private static long NecessarioParaCarga(string caminho)
    {
        var modelo = new FileInfo(caminho).Length;
        var margem = Math.Max(384L * 1024 * 1024, modelo / 3);
        return modelo + margem;
    }

    public bool LiberarModelo()
    {
        lock (_lock)
        {
            if (_weights is null) return false;
            _weights.Dispose();
            _weights = null;
            _parameters = null;
            _caminhoCarregado = null;
            _log.LogInformation("Modelo LLM liberado da memória");
            return true;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _weights?.Dispose();
            _weights = null;
        }
    }
}
