using System.Text;
using System.Text.Json;
using LLama;
using LLama.Common;
using LLama.Native;
using LLama.Sampling;
using Microsoft.Extensions.Logging;

namespace Piloto.LlmWorker;

/// <summary>
/// Processo de inferência ISOLADO do LLM. Existe por um único motivo: o llama.cpp pode
/// derrubar o processo com crash nativo (instrução ilegal, abort do GGML, access
/// violation) que nenhum try/catch .NET alcança — em campo isso matou o Piloto em série
/// nas versões 0.7.7–0.7.11, sobrevivendo a quatro hipóteses de causa. Rodando aqui,
/// o crash vira um EXIT CODE que o app lê, loga e trata como "registro sem resumo";
/// o Piloto nunca mais cai por causa do resumo, e o código da exceção do Windows
/// (0xC000001D, 0xC0000005, 0xC0000409...) finalmente diz onde morre.
///
/// Contrato: Piloto.LlmWorker.exe &lt;request.json&gt; &lt;response.json&gt;
///   request : { modelo, prompt, gbnf?, temperatura, contexto, threads, maxTokens }
///   response: { saida } em sucesso; { erro } em falha gerenciada.
/// Exit codes: 0 = sucesso; 3 = falha gerenciada (response tem "erro");
///   2 = uso incorreto; qualquer outro = crash nativo (diagnóstico no stderr do app).
/// </summary>
internal static class Program
{
    private sealed record Request(
        string Modelo,
        string Prompt,
        string? Gbnf,
        float Temperatura,
        int Contexto,
        int Threads,
        int MaxTokens);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static async Task<int> Main(string[] args)
    {
        if (args.Length != 2)
        {
            Console.Error.WriteLine("uso: Piloto.LlmWorker <request.json> <response.json>");
            return 2;
        }

        var caminhoResposta = args[1];
        try
        {
            // Prioridade baixa como o resto do pipeline: a máquina é do atendente.
            try { System.Diagnostics.Process.GetCurrentProcess().PriorityClass = System.Diagnostics.ProcessPriorityClass.BelowNormal; }
            catch { /* sem permissão: segue normal */ }

            var request = JsonSerializer.Deserialize<Request>(File.ReadAllText(args[0]), JsonOpts)
                ?? throw new InvalidOperationException("Request vazio.");

            var saida = await InferirAsync(request).ConfigureAwait(false);
            File.WriteAllText(caminhoResposta, JsonSerializer.Serialize(new { saida }, JsonOpts));
            return 0;
        }
        catch (Exception ex)
        {
            // Falha GERENCIADA (arquivo ausente, JSON inválido, exceção do LLamaSharp).
            // Crash nativo nunca chega aqui — ele mata o processo e o exit code fala.
            try { File.WriteAllText(caminhoResposta, JsonSerializer.Serialize(new { erro = ex.Message }, JsonOpts)); }
            catch { /* resposta ilegível: o app trata pela ausência do arquivo */ }
            Console.Error.WriteLine(ex);
            return 3;
        }
    }

    private static async Task<string> InferirAsync(Request req)
    {
        ConfigurarNativo();

        var parameters = new ModelParams(req.Modelo)
        {
            ContextSize = (uint)req.Contexto,
            GpuLayerCount = 0, // CPU
            Threads = req.Threads,
        };

        Console.Error.WriteLine($"[worker] carregando {Path.GetFileName(req.Modelo)} (contexto {req.Contexto}, {req.Threads} thread(s))");
        using var weights = LLamaWeights.LoadFromFile(parameters);
        Console.Error.WriteLine("[worker] modelo carregado; inferindo");

        var executor = new StatelessExecutor(weights, parameters);
        using var pipeline = new DefaultSamplingPipeline
        {
            Temperature = req.Temperatura,
            Grammar = req.Gbnf is null ? null : new Grammar(req.Gbnf, "root"),
        };

        var inference = new InferenceParams
        {
            SamplingPipeline = pipeline,
            MaxTokens = req.MaxTokens,
            AntiPrompts = new List<string> { "<end_of_turn>", "<eos>", "</s>" },
        };

        var sb = new StringBuilder();
        await foreach (var token in executor.InferAsync(req.Prompt, inference).ConfigureAwait(false))
            sb.Append(token);

        Console.Error.WriteLine($"[worker] inferência concluída ({sb.Length} caracteres)");
        return sb.ToString();
    }

    /// <summary>
    /// Mesma seleção do app: nível de instruções que a CPU executa DE VERDADE (o seletor
    /// do LLamaSharp segue a preferência sem testar a CPU), CUDA/Vulkan desligados, e o
    /// log nativo do llama.cpp no stderr — em crash, a última linha aponta onde morreu.
    /// </summary>
    private static void ConfigurarNativo()
    {
        var nivel = AvxLevel.None;
        try
        {
            if (System.Runtime.Intrinsics.X86.Avx2.IsSupported) nivel = AvxLevel.Avx2;
            else if (System.Runtime.Intrinsics.X86.Avx.IsSupported) nivel = AvxLevel.Avx;
        }
        catch { /* sem intrinsics x86: None roda em tudo */ }
        Console.Error.WriteLine($"[worker] instruções: {nivel}");

        try
        {
            NativeLibraryConfig.All
                .WithCuda(false)
                .WithVulkan(false)
                .WithAvx(nivel);
        }
        catch { /* lib já carregada: segue a seleção anterior */ }

        try { NativeLogConfig.llama_log_set(new StderrLogger()); }
        catch { /* sem log nativo: o exit code continua sendo o diagnóstico principal */ }
    }

    /// <summary>Encaminha o log nativo do llama.cpp para o stderr, que o app captura e
    /// guarda as últimas linhas — o rastro que faltava nos crashes de carga.</summary>
    private sealed class StderrLogger : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var msg = formatter(state, exception).TrimEnd();
            if (msg.Length > 0) Console.Error.WriteLine("[llama] " + msg);
        }
    }
}
