namespace Piloto.Core.Models;

/// <summary>
/// Metadados da ligação lidos do DOM do Zendesk pela extensão e recebidos via bridge.
/// Todos os campos são opcionais: no MVP (gravação manual) a chamada pode não ter
/// nenhum metadado associado.
/// </summary>
public sealed class CallMetadata
{
    public string? Numero { get; set; }
    public string? TicketId { get; set; }
    public string? Status { get; set; }
    public string? Atendente { get; set; }
    public DateTimeOffset? IniciadaEm { get; set; }
    public DateTimeOffset? EncerradaEm { get; set; }

    /// <summary>Payload bruto recebido da extensão (para auditoria/depuração).</summary>
    public string? OrigemJson { get; set; }

    /// <summary>
    /// Problemas detectados durante a captura (ex.: microfone mudo). Persistem com o item
    /// da fila e viram motivo de revisão no registro — falha de áudio nunca é silenciosa.
    /// </summary>
    public List<string> AvisosCaptura { get; set; } = new();

    public static CallMetadata Vazio() => new();
}
