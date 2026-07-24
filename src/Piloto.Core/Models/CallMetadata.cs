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

    // ----- Contato do cliente lido do cadastro do Zendesk (não da fala) -----
    //
    // E-mail e telefone ditados por voz são o que o Whisper MAIS erra: uma letra a menos
    // no e-mail e um dígito trocado no telefone não têm como ser detectados por regra
    // nenhuma — saem plausíveis e errados. Quando a extensão consegue lê-los do cadastro
    // do solicitante, essa é a fonte da verdade e vence a transcrição.

    public string? EmailCliente { get; set; }
    public string? TelefoneCliente { get; set; }
    public string? NomeCliente { get; set; }

    /// <summary>Payload bruto recebido da extensão (para auditoria/depuração).</summary>
    public string? OrigemJson { get; set; }

    /// <summary>
    /// Problemas detectados durante a captura (ex.: microfone mudo). Persistem com o item
    /// da fila e viram motivo de revisão no registro — falha de áudio nunca é silenciosa.
    /// </summary>
    public List<string> AvisosCaptura { get; set; } = new();

    public static CallMetadata Vazio() => new();
}
