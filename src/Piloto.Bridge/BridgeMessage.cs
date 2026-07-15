using System.Text.Json.Serialization;
using Piloto.Core.Models;

namespace Piloto.Bridge;

/// <summary>
/// Contrato de mensagens trocadas com a extensão do navegador (JSON via WebSocket local).
/// A extensão lê o DOM do Zendesk e envia número, ticket e status; na fase 2 também
/// eventos de início/fim de chamada para acionar a gravação automática.
/// </summary>
public sealed class BridgeMessage
{
    [JsonPropertyName("tipo")]
    public string Tipo { get; set; } = "metadata";

    [JsonPropertyName("numero")]
    public string? Numero { get; set; }

    [JsonPropertyName("ticket")]
    public string? Ticket { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("atendente")]
    public string? Atendente { get; set; }

    public CallMetadata ParaMetadata() => new()
    {
        Numero = Numero,
        TicketId = Ticket,
        Status = Status,
        Atendente = Atendente,
    };
}

/// <summary>Tipos de evento reconhecidos.</summary>
public static class BridgeMessageTypes
{
    public const string Metadata = "metadata";
    public const string ChamadaIniciada = "call_started";
    public const string ChamadaEncerrada = "call_ended";
    public const string Ping = "ping";
}
