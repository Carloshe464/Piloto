using Piloto.Core.Models;

namespace Piloto.Core.Abstractions;

/// <summary>
/// Transcreve os dois canais com Whisper (task=transcribe, language=pt) e funde por
/// timestamp em um diálogo rotulado. Nunca usa task=translate.
/// </summary>
public interface ITranscriber
{
    Task<Transcript> TranscreverAsync(AudioCapture captura, CancellationToken ct = default);
}
