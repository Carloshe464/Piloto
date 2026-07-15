namespace Piloto.Core.Models;

/// <summary>
/// Par de arquivos de áudio de uma ligação: um por canal físico.
/// Ambos gravados na mesma base de tempo (iniciados juntos) para permitir a fusão
/// por timestamp na transcrição.
/// </summary>
public sealed class AudioCapture
{
    public required string CaminhoAtendente { get; init; }
    public required string CaminhoCliente { get; init; }
    public DateTimeOffset IniciadaEm { get; init; }
    public DateTimeOffset EncerradaEm { get; init; }
    public CallMetadata Metadata { get; init; } = CallMetadata.Vazio();

    public TimeSpan Duracao => EncerradaEm - IniciadaEm;
}
