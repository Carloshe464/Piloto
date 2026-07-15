using System.Text;
using LLama;
using LLama.Common;
using LLama.Sampling;
using Microsoft.Extensions.Logging;
using Piloto.Core.Abstractions;
using Piloto.Core.Configuration;
using Piloto.Core.Models;

namespace Piloto.Llm;

/// <summary>
/// Camada 2 — resumo com LLM local (Gemma 3 4B Q4 via LLamaSharp/llama.cpp).
/// Saída JSON determinística (temperatura 0), execução em CPU. A restrição às listas
/// fechadas é garantida pelo grounding; a gramática GBNF (GbnfGrammarBuilder) está pronta,
/// porém desligada por padrão. Os pesos ficam em cache enquanto o caminho não muda.
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

        var caminho = _modelos.CaminhoLlm
            ?? throw new InvalidOperationException("Modelo LLM ausente.");

        var (weights, parameters) = ObterWeights(caminho);
        var executor = new StatelessExecutor(weights, parameters);

        var prompt = _prompt.Construir(transcript, listas);

        // Saída determinística (temperatura 0). A restrição às listas fechadas é garantida
        // pelo grounding (camada 3), que anula qualquer valor fora da lista. Para forçar o
        // JSON por gramática GBNF, gere-a com GbnfGrammarBuilder.Construir(listas) e atribua
        // a DefaultSamplingPipeline.Grammar — confirme antes a API da versão do LLamaSharp.
        var pipeline = new DefaultSamplingPipeline
        {
            Temperature = _settings.Llm.Temperatura,
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

        var resumo = LlmResponseParser.Parse(sb.ToString());
        _log.LogInformation("LLM: motivo={Motivo} produto={Produto} status={Status}",
            resumo.MotivoContato ?? "—", resumo.Produto ?? "—", resumo.Status ?? "—");
        return resumo;
    }

    private (LLamaWeights, ModelParams) ObterWeights(string caminho)
    {
        lock (_lock)
        {
            if (_weights is not null && _parameters is not null && _caminhoCarregado == caminho)
                return (_weights, _parameters);

            _weights?.Dispose();
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

    public void Dispose()
    {
        lock (_lock)
        {
            _weights?.Dispose();
            _weights = null;
        }
    }
}
