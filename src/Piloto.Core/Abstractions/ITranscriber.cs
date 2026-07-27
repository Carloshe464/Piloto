using Piloto.Core.Models;

namespace Piloto.Core.Abstractions;

/// <summary>
/// Transcreve os dois canais (task=transcribe, language=pt) e funde por timestamp em um
/// diálogo rotulado. Nunca traduz.
/// <para>
/// A implementação em produção é o <c>RemoteTranscriber</c>: o piloto envia os dois canais
/// ao servidor e recebe o trabalho pronto. O <c>WhisperTranscriber</c> local continua no
/// repositório como referência histórica dos filtros calibrados em campo, fora do contêiner.
/// </para>
/// </summary>
public interface ITranscriber
{
    Task<TranscriptionResult> TranscreverAsync(AudioCapture captura, CancellationToken ct = default);

    /// <summary>
    /// Libera o modelo da memória (recarregado na próxima transcrição). Devolve true se
    /// havia algo carregado. Sem modelo local não há nada a liberar — daí o padrão false.
    /// </summary>
    bool LiberarModelo() => false;
}
