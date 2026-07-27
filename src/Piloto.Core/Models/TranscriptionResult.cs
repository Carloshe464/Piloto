namespace Piloto.Core.Models;

/// <summary>
/// O que o transcritor devolve ao pipeline. Além do diálogo, carrega o que o
/// <b>servidor</b> já processou — quando ele tem as capacidades ligadas
/// (<c>analiseDisponivel</c> / <c>resumoDisponivel</c> em <c>/v1/saude</c>).
/// <para>
/// A convenção que importa: <see cref="Campos"/> ou <see cref="Resumo"/> em <c>null</c>
/// significa <b>"o servidor não fez isto"</b> — e a camada local assume. Nunca "fez e não
/// achou nada": isso é lista vazia / campos nulos dentro de um objeto presente.
/// </para>
/// </summary>
public sealed class TranscriptionResult
{
    public required Transcript Transcript { get; init; }

    /// <summary>Campos objetivos vindos do servidor, ou null se ele não os extraiu.</summary>
    public ObjectiveFields? Campos { get; init; }

    /// <summary>Resumo vindo do servidor, ou null se ele não o gerou.</summary>
    public LlmSummary? Resumo { get; init; }

    /// <summary>Achados que pedem olho humano (grounding do servidor). Viram motivo de revisão.</summary>
    public IReadOnlyList<string> Avisos { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Motivo de cada canal que voltou vazio ("cliente.wav: sem amostras de áudio"). Um canal
    /// mudo é situação corriqueira — o loopback que não capturou nada — e <b>não é erro</b>;
    /// quem decide o que fazer com isso é o pipeline, que tem a tela e o banco.
    /// </summary>
    public IReadOnlyList<string> CanaisVazios { get; init; } = Array.Empty<string>();

    /// <summary>Quem transcreveu, para o log ("servidor medium/cuda", "whisper local").</summary>
    public string? Origem { get; init; }

    public static TranscriptionResult SomenteTranscricao(Transcript transcript, string? origem = null)
        => new() { Transcript = transcript, Origem = origem };
}
