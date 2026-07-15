namespace Piloto.Core.Abstractions;

/// <summary>
/// Verifica a presença dos modelos (Whisper GGML e LLM GGUF). Sem os modelos o app
/// abre normalmente, mas a fila fica pausada com o aviso "modelos ausentes".
/// </summary>
public interface IModelCatalog
{
    bool WhisperDisponivel { get; }
    bool LlmDisponivel { get; }

    /// <summary>True quando o pipeline pode rodar (Whisper presente; LLM pode estar desligado no config).</summary>
    bool PipelinePronto { get; }

    string? CaminhoWhisper { get; }
    string? CaminhoLlm { get; }

    IReadOnlyList<string> ModelosAusentes();
}
