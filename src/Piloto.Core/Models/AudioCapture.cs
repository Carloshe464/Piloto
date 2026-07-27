namespace Piloto.Core.Models;

/// <summary>
/// Par de arquivos de áudio de uma ligação: um por canal físico.
/// Ambos gravados na mesma base de tempo (iniciados juntos) para permitir a fusão
/// por timestamp na transcrição.
/// </summary>
public sealed class AudioCapture
{
    /// <summary>
    /// Identidade da ligação, criada no encerramento da captura e estável dali em diante:
    /// vai para a fila, viaja como <c>ligacaoId</c>/<c>Idempotency-Key</c> no servidor e
    /// termina como <see cref="CallRecord.Uuid"/>.
    /// <para>
    /// É o que torna a retentativa barata: reenviar com a mesma chave reaproveita o job em
    /// vez de gastar uma segunda passada de GPU — inclusive depois de o app reiniciar, que
    /// é justamente quando o piloto não sabe se o envio anterior chegou.
    /// </para>
    /// </summary>
    public string LigacaoId { get; init; } = Guid.NewGuid().ToString("N");

    public required string CaminhoAtendente { get; init; }
    public required string CaminhoCliente { get; init; }
    public DateTimeOffset IniciadaEm { get; init; }
    public DateTimeOffset EncerradaEm { get; init; }
    public CallMetadata Metadata { get; init; } = CallMetadata.Vazio();

    public TimeSpan Duracao => EncerradaEm - IniciadaEm;
}
