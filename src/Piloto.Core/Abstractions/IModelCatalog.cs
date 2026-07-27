namespace Piloto.Core.Abstractions;

/// <summary>
/// Verifica a presença do modelo LLM (GGUF) em disco. Sem ele o app abre normalmente e as
/// ligações continuam sendo transcritas — o resumo é que fica pendente até o modelo existir.
/// <para>
/// O Whisper saiu daqui na migração para o servidor: quem responde "dá para transcrever?"
/// agora é <c>GET /v1/saude</c>, não a pasta de modelos.
/// </para>
/// </summary>
public interface IModelCatalog
{
    bool LlmDisponivel { get; }

    string? CaminhoLlm { get; }

    /// <summary>
    /// Candidatos a modelo LLM: o configurado primeiro; depois os demais .gguf da pasta
    /// de modelos, do maior para o menor. Quem decide qual cabe na RAM é o extractor —
    /// assim o mesmo instalador serve máquinas de 4 GB (Gemma 1B) e de 16 GB (Gemma 4B).
    /// </summary>
    IReadOnlyList<string> CandidatosLlm { get; }

    IReadOnlyList<string> ModelosAusentes();
}
