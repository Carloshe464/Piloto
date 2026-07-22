using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Piloto.Core.Abstractions;
using Piloto.Core.Configuration;
using Piloto.Core.Models;
using Piloto.Core.Services;

namespace Piloto.Llm;

/// <summary>
/// Camada 2 — resumo com LLM local rodando em PROCESSO ISOLADO (Piloto.LlmWorker.exe).
/// O llama.cpp derrubou o app em série nas versões 0.7.7–0.7.11 com crash nativo que
/// nenhum try/catch .NET alcança, sobrevivendo a quatro hipóteses de causa (AVX512,
/// AVX2, .NET confirmando Avx2, GGUF corrompido). Isolado, o crash vira exit code:
/// o app loga o código da exceção do Windows (a evidência que faltava), trata como
/// "registro sem resumo" e segue vivo. O prompt, a gramática GBNF e os parâmetros
/// são montados aqui; o worker é um executor burro.
/// </summary>
public sealed class LlmWorkerExtractor : ILlmExtractor
{
    private readonly AppSettings _settings;
    private readonly IModelCatalog _modelos;
    private readonly PromptBuilder _prompt;
    private readonly ILogger<LlmWorkerExtractor> _log;

    /// <summary>1B em máquina sem AVX gera ~700 tokens em minutos, não horas — acima
    /// disto o worker está travado, não lento.</summary>
    private static readonly TimeSpan TimeoutInferencia = TimeSpan.FromMinutes(15);

    /// <summary>Após esta sequência de quedas NATIVAS do worker (exit code de crash,
    /// não falha gerenciada), parar de tentar até o app reiniciar: o app não corre
    /// perigo, mas cada tentativa custa minutos de CPU do atendente à toa.</summary>
    private const int MaxQuedasConsecutivas = 3;
    private int _quedasConsecutivas;

    public LlmWorkerExtractor(
        AppSettings settings,
        IModelCatalog modelos,
        PromptBuilder prompt,
        ILogger<LlmWorkerExtractor> log)
    {
        _settings = settings;
        _modelos = modelos;
        _prompt = prompt;
        _log = log;
        RemoverSentinelasLegadas();
    }

    public async Task<LlmSummary> ResumirAsync(Transcript transcript, ListasFechadas listas, CancellationToken ct = default)
    {
        if (transcript.EstaVazio)
            return LlmSummary.Vazio();

        if (_quedasConsecutivas >= MaxQuedasConsecutivas)
            throw new InvalidOperationException(
                $"Resumo suspenso nesta sessão: o processo do LLM caiu {_quedasConsecutivas} vezes seguidas. " +
                "Reinicie o Piloto para tentar de novo; o log tem o código de cada queda.");

        var caminho = EscolherModelo();
        VerificarIntegridade(caminho);
        GarantirMemoriaParaCarga(caminho);

        var bruto = await ExecutarWorkerAsync(caminho, transcript, listas, ct).ConfigureAwait(false);

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

    /// <summary>Nada fica residente no app: cada resumo é um processo que nasce e morre —
    /// a memória volta ao SO no fim de cada ligação, melhor ainda para as máquinas fracas.</summary>
    public bool LiberarModelo() => false;

    // ------------------------------------------------------------------ worker
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private sealed record Resposta(string? Saida, string? Erro);

    /// <summary>Crash NATIVO do worker (exit code de exceção do Windows) — o único tipo de
    /// falha que justifica tentar de novo com outra configuração.</summary>
    private sealed class CrashNativoException : Exception
    {
        public CrashNativoException(string mensagem) : base(mensagem) { }
    }

    /// <summary>Uma configuração de tentativa da escada de fallback.</summary>
    private sealed record Tentativa(string Descricao, bool Gramatica, string? Avx, int Contexto);

    /// <summary>
    /// Escada de fallback contra o crash nativo (0xC0000005 consistente em campo): cada
    /// degrau remove uma suspeita — primeiro a gramática GBNF, depois as instruções
    /// AVX + contexto grande. Se um degrau funciona, o log diz QUAL, e isso é a evidência
    /// que aponta a causa raiz; se todos caem, conta UMA queda da sessão (3 suspendem).
    /// </summary>
    private async Task<string> ExecutarWorkerAsync(string caminhoModelo, Transcript transcript, ListasFechadas listas, CancellationToken ct)
    {
        var contexto = _settings.Llm.Contexto;
        var tentativas = new List<Tentativa>
        {
            new("configuração normal", _settings.Llm.Gramatica, Avx: null, contexto),
        };
        if (_settings.Llm.Gramatica)
            tentativas.Add(new("sem gramática GBNF", Gramatica: false, Avx: null, contexto));
        tentativas.Add(new("sem gramática, sem AVX, contexto 2048", Gramatica: false, Avx: "none", Math.Min(contexto, 2048)));

        CrashNativoException? ultimoCrash = null;
        foreach (var tentativa in tentativas)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var saida = await TentarWorkerAsync(caminhoModelo, transcript, listas, tentativa, ct).ConfigureAwait(false);
                if (ultimoCrash is not null)
                    _log.LogWarning(
                        "Resumo obtido no fallback \"{Descricao}\" — o crash nativo está no que a configuração anterior tinha a mais (evidência de causa raiz)",
                        tentativa.Descricao);
                _quedasConsecutivas = 0;
                return saida;
            }
            catch (CrashNativoException ex)
            {
                ultimoCrash = ex;
            }
        }

        _quedasConsecutivas++;
        _log.LogError("Todas as {N} tentativas do worker caíram — queda {Queda}/{Max} da sessão",
            tentativas.Count, _quedasConsecutivas, MaxQuedasConsecutivas);
        throw new InvalidOperationException(ultimoCrash!.Message);
    }

    private async Task<string> TentarWorkerAsync(string caminhoModelo, Transcript transcript, ListasFechadas listas, Tentativa tentativa, CancellationToken ct)
    {
        var exe = Path.Combine(AppContext.BaseDirectory, "Piloto.LlmWorker.exe");
        if (!File.Exists(exe))
            throw new InvalidOperationException(
                "Piloto.LlmWorker.exe não encontrado ao lado do app — instalação incompleta; reinstale o Piloto.");

        var pastaTrabalho = Path.Combine(_settings.PastaDadosExpandida, "llm-work");
        Directory.CreateDirectory(pastaTrabalho);
        var id = Guid.NewGuid().ToString("N")[..8];
        var caminhoRequest = Path.Combine(pastaTrabalho, $"req-{id}.json");
        var caminhoResponse = Path.Combine(pastaTrabalho, $"resp-{id}.json");

        var request = new
        {
            modelo = caminhoModelo,
            prompt = _prompt.Construir(transcript, listas),
            gbnf = tentativa.Gramatica ? GbnfGrammarBuilder.Construir(listas) : null,
            temperatura = _settings.Llm.Temperatura,
            contexto = tentativa.Contexto,
            threads = Hardware.ResolverThreads(_settings.Llm.Threads),
            maxTokens = 700,
            avx = tentativa.Avx,
        };

        try
        {
            File.WriteAllText(caminhoRequest, JsonSerializer.Serialize(request, JsonOpts));

            _log.LogInformation("Worker do LLM iniciado: {Modelo} ({Descricao})",
                Path.GetFileName(caminhoModelo), tentativa.Descricao);
            var inicio = DateTimeOffset.Now;

            using var processo = new Process();
            processo.StartInfo = new ProcessStartInfo
            {
                FileName = exe,
                WorkingDirectory = AppContext.BaseDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            processo.StartInfo.ArgumentList.Add(caminhoRequest);
            processo.StartInfo.ArgumentList.Add(caminhoResponse);

            // PATH mínimo: o worker (self-contained) não precisa de nada dali, e uma DLL
            // nativa estranha de outro programa no PATH da máquina é a suspeita viva do
            // AV em llama_backend_init() — dupla proteção com a blindagem interna do worker.
            processo.StartInfo.Environment["PATH"] = Environment.GetFolderPath(Environment.SpecialFolder.System);

            // Últimas linhas do stderr (log nativo do llama.cpp): em crash, apontam ONDE
            // a carga/inferência morreu — o rastro que cinco versões de hipóteses não tinham.
            var stderr = new Queue<string>();
            processo.ErrorDataReceived += (_, e) =>
            {
                if (string.IsNullOrWhiteSpace(e.Data)) return;
                lock (stderr)
                {
                    stderr.Enqueue(e.Data);
                    // 160 linhas seguram o log de carga verboso do llama.cpp sem expulsar
                    // os breadcrumbs [worker] do início, que dizem até onde o processo chegou.
                    while (stderr.Count > 160) stderr.Dequeue();
                }
            };

            processo.Start();
            processo.BeginErrorReadLine();
            processo.BeginOutputReadLine();

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeoutInferencia);
            try
            {
                await processo.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { processo.Kill(entireProcessTree: true); } catch { /* já saiu */ }
                if (ct.IsCancellationRequested) throw;
                throw new InvalidOperationException(
                    $"O processo do LLM não terminou em {TimeoutInferencia.TotalMinutes:0} min e foi encerrado.");
            }

            var duracao = DateTimeOffset.Now - inicio;
            if (processo.ExitCode == 0)
            {
                var resposta = JsonSerializer.Deserialize<Resposta>(File.ReadAllText(caminhoResponse), JsonOpts);
                _log.LogInformation("Worker do LLM concluído em {Segundos:0} s", duracao.TotalSeconds);
                return resposta?.Saida ?? "";
            }

            if (processo.ExitCode is 2 or 3)
            {
                // Falha gerenciada dentro do worker: a causa veio no response.
                string? erro = null;
                try
                {
                    if (File.Exists(caminhoResponse))
                        erro = JsonSerializer.Deserialize<Resposta>(File.ReadAllText(caminhoResponse), JsonOpts)?.Erro;
                }
                catch { /* sem response legível */ }
                throw new InvalidOperationException(erro ?? $"Worker do LLM falhou (exit {processo.ExitCode}).");
            }

            // Crash NATIVO — o que derrubava o app inteiro até a 0.7.11. Agora é um número
            // no log, um degrau da escada de fallback e, no pior caso, um registro sem resumo.
            // O stderr completo importa: os breadcrumbs [worker]/[llama] do INÍCIO dizem até
            // onde chegou (instruções, carga, inferência) antes do stack de crash do fim.
            string linhas;
            lock (stderr)
                linhas = stderr.Count <= 30
                    ? string.Join(" | ", stderr)
                    : string.Join(" | ", stderr.Take(12)) + " | (…) | " + string.Join(" | ", stderr.TakeLast(18));
            _log.LogError(
                "Worker do LLM MORREU com exit code 0x{Codigo:X8} ({Traducao}) após {Segundos:0} s na tentativa \"{Descricao}\". Stderr: {Stderr}",
                unchecked((uint)processo.ExitCode), TraduzirExitCode(processo.ExitCode),
                duracao.TotalSeconds, tentativa.Descricao, linhas);
            throw new CrashNativoException(
                $"O processo do LLM encerrou inesperadamente (código 0x{unchecked((uint)processo.ExitCode):X8} — {TraduzirExitCode(processo.ExitCode)}).");
        }
        finally
        {
            ApagarSilenciosamente(caminhoRequest);
            ApagarSilenciosamente(caminhoResponse);
        }
    }

    /// <summary>Códigos de exceção do Windows mais prováveis num crash do llama.cpp —
    /// cada um aponta uma causa raiz diferente, e é por este número que o diagnóstico avança.</summary>
    private static string TraduzirExitCode(int exitCode) => unchecked((uint)exitCode) switch
    {
        0xC000001D => "instrução ilegal — a CPU não executa uma instrução do binário",
        0xC0000005 => "access violation — leitura/escrita inválida de memória",
        0xC0000409 => "abort/verificação de segurança — típico de GGML_ASSERT",
        0xC0000374 => "heap corrompido",
        0xC0000017 => "sem memória para iniciar",
        0xE0434352 => "exceção .NET não tratada no worker",
        _ => "código não catalogado",
    };

    private static void ApagarSilenciosamente(string caminho)
    {
        try { if (File.Exists(caminho)) File.Delete(caminho); } catch { /* fica para a próxima limpeza */ }
    }

    /// <summary>As sentinelas por arquivo (0.7.10/0.7.11) protegiam o app de cair em série;
    /// com o worker isolado elas não têm mais função — remove para não confundir diagnóstico.</summary>
    private void RemoverSentinelasLegadas()
    {
        ApagarSilenciosamente(Path.Combine(_settings.PastaDadosExpandida, "llm.lock"));
        ApagarSilenciosamente(Path.Combine(_settings.PastaDadosExpandida, "resumo-pendente.lock"));
    }

    // ------------------------------------------------------------------ escolha do modelo
    /// <summary>
    /// Escolhe entre os candidatos do catálogo (configurado primeiro, depois por tamanho)
    /// o primeiro que cabe na memória desta máquina.
    /// </summary>
    private string EscolherModelo()
    {
        var candidatos = _modelos.CandidatosLlm;
        if (candidatos.Count == 0)
            throw new InvalidOperationException("Modelo LLM ausente.");

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

    // ------------------------------------------------------------------ integridade
    /// <summary>SHA256 e tamanho oficiais (lfs.oid do Hugging Face) dos modelos do catálogo.
    /// O download com retomada (curl -C -) em rede instável pode montar arquivo corrompido,
    /// e o llama.cpp em cima de GGUF corrompido ABORTA o processo sem exceção .NET — o
    /// pré-voo converte isso em erro gerenciado ("registro sem resumo") com causa clara.</summary>
    private static readonly Dictionary<string, (string Sha256, long Tamanho)> HashesOficiais =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["gemma-3-1b-it-Q4_K_M.gguf"] = ("8270790f3ab69fdfe860b7b64008d9a19986d8df7e407bb018184caa08798ebd", 806_058_272),
            ["gemma-3-4b-it-Q4_K_M.gguf"] = ("04a43a22e8d2003deda5acc262f68ec1005fa76c735a9962a8c77042a74a7d19", 2_489_894_016),
        };

    /// <summary>Verificação cara (SHA256 de centenas de MB) roda uma vez por arquivo: o
    /// resultado fica num marcador ao lado do modelo, válido enquanto tamanho+mtime não mudarem.</summary>
    private void VerificarIntegridade(string caminho)
    {
        var nome = Path.GetFileName(caminho);
        if (!HashesOficiais.TryGetValue(nome, out var oficial))
            return; // modelo fora do catálogo (colocado manualmente): não bloqueia

        var info = new FileInfo(caminho);
        if (info.Length != oficial.Tamanho)
            throw new InvalidOperationException(
                $"Modelo LLM corrompido/incompleto: {nome} tem {info.Length} bytes, o oficial tem {oficial.Tamanho}. " +
                "Apague o arquivo e rode \"Baixar modelos\" novamente.");

        var marcador = caminho + ".integridade";
        var esperado = $"{oficial.Sha256}:{info.Length}:{info.LastWriteTimeUtc.Ticks}";
        try
        {
            if (File.Exists(marcador) && File.ReadAllText(marcador).Trim() == esperado)
                return; // este exato arquivo já foi verificado
        }
        catch { /* marcador ilegível: refaz a verificação */ }

        _log.LogInformation("Verificando integridade do modelo {Nome} (uma vez por arquivo)…", nome);
        string hash;
        using (var sha = System.Security.Cryptography.SHA256.Create())
        using (var stream = File.OpenRead(caminho))
            hash = Convert.ToHexString(sha.ComputeHash(stream));

        if (!hash.Equals(oficial.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Modelo LLM corrompido: o SHA256 de {nome} não confere com o oficial. " +
                "Apague o arquivo e rode \"Baixar modelos\" novamente.");

        try { File.WriteAllText(marcador, esperado); } catch { /* verifica de novo na próxima */ }
        _log.LogInformation("Modelo {Nome} íntegro (SHA256 confere)", nome);
    }

    // ------------------------------------------------------------------ memória
    /// <summary>
    /// Sem memória, o malloc nativo aborta o worker no meio da carga — não derruba mais o
    /// app, mas custa minutos e termina igual sem resumo. Checar antes converte em erro
    /// gerenciado imediato, com os números no log.
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
        // do llama.cpp falha mesmo havendo RAM física livre.
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
    /// + buffers de computação, que crescem com o modelo e o contexto. A margem de 768 MB
    /// era calibrada para a era em que o crash derrubava o APP; com o worker isolado, o
    /// pior caso de uma carga apertada é perder minutos — e em campo o guard gordo passou
    /// a ser o próprio bloqueio: a máquina de 12 GB vive com 1,0-1,4 GB livres e a camada 2
    /// nunca rodava (necessários 1.536 MB para um modelo de 769). Margem enxuta: o mmap
    /// pagina dos arquivos sob pressão (lento, mas termina) e o commit livre (7-8 GB lá)
    /// continua sendo checado.
    /// </summary>
    private static long NecessarioParaCarga(string caminho)
    {
        var modelo = new FileInfo(caminho).Length;
        var margem = Math.Max(384L * 1024 * 1024, modelo / 4);
        return modelo + margem;
    }
}
