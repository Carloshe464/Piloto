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

    /// <summary>
    /// Candidatos a modelo LLM: o configurado primeiro; depois os demais .gguf da pasta
    /// de modelos, do maior para o menor. Quem decide qual cabe na RAM é o extractor —
    /// assim o mesmo instalador serve máquinas de 4 GB (Gemma 1B) e de 16 GB (Gemma 4B).
    /// </summary>
    IReadOnlyList<string> CandidatosLlm { get; }

    IReadOnlyList<string> ModelosAusentes();
}
