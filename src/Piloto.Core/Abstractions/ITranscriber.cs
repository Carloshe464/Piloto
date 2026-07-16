using Piloto.Core.Models;

namespace Piloto.Core.Abstractions;

/// <summary>
/// Transcreve os dois canais com Whisper (task=transcribe, language=pt) e funde por
/// timestamp em um diálogo rotulado. Nunca usa task=translate.
/// </summary>
public interface ITranscriber
{
    Task<Transcript> TranscreverAsync(AudioCapture captura, CancellationToken ct = default);

    /// <summary>
    /// Libera o modelo da memória (recarregado na próxima transcrição). Devolve true se
    /// havia algo carregado. O pipeline chama antes de carregar o LLM (em máquinas com
    /// pouca RAM essa folga decide se o resumo roda) e a fila chama após ociosidade.
    /// </summary>
    bool LiberarModelo() => false;
}
