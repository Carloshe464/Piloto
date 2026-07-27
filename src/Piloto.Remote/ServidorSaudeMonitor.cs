using Microsoft.Extensions.Logging;
using Piloto.Core.Models;

namespace Piloto.Remote;

/// <summary>
/// Guarda o último <c>GET /v1/saude</c> conhecido. Duas funções:
/// <list type="bullet">
///   <item>dizer à UI se o servidor está de pé (e, se não, por quê);</item>
///   <item>dizer ao transcritor <b>o que esperar do resultado</b> — é
///   <c>analiseDisponivel</c>/<c>resumoDisponivel</c> que decide se o piloto exibe o que
///   veio pronto ou extrai por conta própria.</item>
/// </list>
/// A consulta é barata e sem token, mas não precisa ser feita a cada ligação: o resultado
/// vale por alguns minutos.
/// </summary>
public sealed class ServidorSaudeMonitor
{
    private static readonly TimeSpan Validade = TimeSpan.FromMinutes(2);

    private readonly ServidorTranscricaoClient _cliente;
    private readonly ILogger<ServidorSaudeMonitor> _log;
    private readonly SemaphoreSlim _porta = new(1, 1);

    private ServidorSaude? _ultima;
    private string? _ultimoErro;
    private DateTimeOffset _lidaEm = DateTimeOffset.MinValue;

    public ServidorSaudeMonitor(ServidorTranscricaoClient cliente, ILogger<ServidorSaudeMonitor> log)
    {
        _cliente = cliente;
        _log = log;
    }

    /// <summary>Última leitura bem-sucedida, ou null se nunca houve uma.</summary>
    public ServidorSaude? Ultima => _ultima;

    /// <summary>Motivo da última falha de consulta, ou null se a última deu certo.</summary>
    public string? UltimoErro => _ultimoErro;

    public bool Disponivel => _ultimoErro is null && _ultima is { Ok: true };

    public string Endereco => _cliente.Url;

    /// <summary>Disparado quando o estado muda (subiu, caiu, mudou de capacidade).</summary>
    public event EventHandler? Mudou;

    /// <summary>Consulta o servidor agora, ignorando o cache.</summary>
    public async Task<ServidorSaude?> AtualizarAsync(CancellationToken ct = default)
    {
        await _porta.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var antes = (Disponivel, _ultima?.AnaliseDisponivel, _ultima?.ResumoDisponivel);
            try
            {
                var saude = await _cliente.SaudeAsync(ct).ConfigureAwait(false);
                _ultima = saude;
                _ultimoErro = null;
                _log.LogInformation("Servidor de transcrição OK em {Url}: {Descricao}", Endereco, saude.Descricao);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _ultimoErro = ex.Message;
                _log.LogWarning("Servidor de transcrição indisponível em {Url}: {Erro}", Endereco, ex.Message);
            }

            _lidaEm = DateTimeOffset.Now;
            if (antes != (Disponivel, _ultima?.AnaliseDisponivel, _ultima?.ResumoDisponivel))
                Mudou?.Invoke(this, EventArgs.Empty);

            return _ultima;
        }
        finally { _porta.Release(); }
    }

    /// <summary>
    /// Última leitura, renovada se estiver velha. Nunca lança: quando a consulta falha,
    /// devolve o que sabia (ou null) — o envio segue assim mesmo, e é a resposta do POST
    /// que classifica a falha de verdade.
    /// </summary>
    public async Task<ServidorSaude?> ObterAsync(CancellationToken ct = default)
    {
        if (_ultima is not null && DateTimeOffset.Now - _lidaEm < Validade)
            return _ultima;

        try { return await AtualizarAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Falha ao atualizar a saúde do servidor");
            return _ultima;
        }
    }
}
