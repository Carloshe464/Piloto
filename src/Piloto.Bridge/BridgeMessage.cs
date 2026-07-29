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

    // ----- Contato do solicitante lido do cadastro do Zendesk -----

    [JsonPropertyName("emailCliente")]
    public string? EmailCliente { get; set; }

    [JsonPropertyName("telefoneCliente")]
    public string? TelefoneCliente { get; set; }

    [JsonPropertyName("nomeCliente")]
    public string? NomeCliente { get; set; }

    /// <summary>Instante em que o softphone abriu a chamada, carimbado no navegador.</summary>
    [JsonPropertyName("iniciadaEm")]
    public DateTimeOffset? IniciadaEm { get; set; }

    /// <summary>Instante em que o softphone encerrou a chamada, carimbado no navegador.</summary>
    [JsonPropertyName("encerradaEm")]
    public DateTimeOffset? EncerradaEm { get; set; }

    // ----- Áudio capturado pela extensão (hook WebRTC no softphone) -----

    /// <summary>"atendente" ou "cliente".</summary>
    [JsonPropertyName("canal")]
    public string? Canal { get; set; }

    /// <summary>PCM16 mono little-endian em base64.</summary>
    [JsonPropertyName("dados")]
    public string? Dados { get; set; }

    /// <summary>Taxa de amostragem do PCM (Hz); a extensão envia 16000.</summary>
    [JsonPropertyName("taxa")]
    public int? Taxa { get; set; }

    public CallMetadata ParaMetadata() => new()
    {
        Numero = Numero,
        TicketId = Ticket,
        Status = Status,
        Atendente = Atendente,
        EmailCliente = EmailCliente,
        TelefoneCliente = TelefoneCliente,
        NomeCliente = NomeCliente,
        IniciadaEm = IniciadaEm,
        EncerradaEm = EncerradaEm,
    };
}

/// <summary>Tipos de evento reconhecidos.</summary>
public static class BridgeMessageTypes
{
    public const string Metadata = "metadata";
    public const string ChamadaIniciada = "call_started";
    public const string ChamadaEncerrada = "call_ended";
    public const string AudioInicio = "audio_inicio";
    public const string AudioChunk = "audio_chunk";
    public const string AudioFim = "audio_fim";
    public const string Ping = "ping";
}

/// <summary>Um bloco de áudio PCM16 recebido da extensão.</summary>
public sealed class AudioChunkEventArgs : EventArgs
{
    public AudioChunkEventArgs(string canal, byte[] dados)
    {
        Canal = canal;
        Dados = dados;
    }

    /// <summary>"atendente" ou "cliente".</summary>
    public string Canal { get; }

    /// <summary>PCM16 mono little-endian.</summary>
    public byte[] Dados { get; }
}
