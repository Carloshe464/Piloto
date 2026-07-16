using System.Runtime.InteropServices;
using System.Text;
using LLama;
using LLama.Common;
using LLama.Native;
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
        if (!TentarObterMemoria(out var fisica, out var commit))
            return; // sem leitura confiável, não bloqueia a carga

        // Além dos pesos (mmap, mas viram working set ao inferir), o llama.cpp aloca
        // KV cache + buffers de computação, que crescem com o modelo. Margem proporcional:
        // não afrouxa a proteção do 4B e ainda deixa o Gemma 1B caber em máquinas de 4 GB.
        var modelo = new FileInfo(caminho).Length;
        var margem = Math.Max(384L * 1024 * 1024, modelo / 3);
        var necessario = modelo + margem;

        // Sempre logado: se a carga morrer mesmo assim, estes números são a evidência.
        _log.LogInformation(
            "Memória antes da carga do LLM: {FisicaMb} MB físicos livres, {CommitMb} MB de commit livres, modelo {ModeloMb} MB",
            fisica / 1_048_576, commit / 1_048_576, modelo / 1_048_576);

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

    private static bool TentarObterMemoria(out long fisicaBytes, out long commitBytes)
    {
        fisicaBytes = 0;
        commitBytes = 0;
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return false;

        var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref status))
            return false;

        fisicaBytes = (long)status.ullAvailPhys;
        commitBytes = (long)status.ullAvailPageFile;
        return true;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    public void Dispose()
    {
        lock (_lock)
        {
            _weights?.Dispose();
            _weights = null;
        }
    }
}
