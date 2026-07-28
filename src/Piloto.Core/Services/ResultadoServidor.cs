using System.Text.Json.Serialization;

namespace Piloto.Core.Services;

/// <summary>
/// Espelho do JSON devolvido por <c>GET /v1/calls/{id}</c>.
/// <para>
/// Fica separado dos modelos do aplicativo de propósito: este é o contrato do servidor e
/// muda com ele. O <see cref="MapeadorResultado"/> traduz para <c>CallRecord</c>, e é lá —
/// num lugar só — que uma mudança de contrato precisa ser tratada.
/// </para>
/// </summary>
public sealed record EstadoLigacao
{
    [JsonPropertyName("call_id")] public string CallId { get; init; } = "";
    [JsonPropertyName("status")] public string Status { get; init; } = "";
    [JsonPropertyName("erro")] public string? Erro { get; init; }
    [JsonPropertyName("progresso")] public double Progresso { get; init; }
    [JsonPropertyName("etapa")] public string? Etapa { get; init; }
    [JsonPropertyName("resultado")] public ResultadoServidor? Resultado { get; init; }

    public bool Concluido => Status is "done";
    public bool Falhou => Status is "failed";
    public bool EmAndamento => !Concluido && !Falhou;
}

public sealed record ResultadoServidor
{
    [JsonPropertyName("call_id")] public string CallId { get; init; } = "";
    [JsonPropertyName("metadados")] public MetadadosServidor Metadados { get; init; } = new();
    [JsonPropertyName("campos_objetivos")] public CamposServidor Campos { get; init; } = new();
    [JsonPropertyName("resumo")] public ResumoServidor Resumo { get; init; } = new();
    [JsonPropertyName("transcricao")] public TranscricaoServidor Transcricao { get; init; } = new();
    [JsonPropertyName("revisao_humana")] public RevisaoServidor Revisao { get; init; } = new();
    [JsonPropertyName("processamento")] public ProcessamentoServidor Processamento { get; init; } = new();
}

public sealed record MetadadosServidor
{
    [JsonPropertyName("ticket")] public string? Ticket { get; init; }
    [JsonPropertyName("telefone")] public string? Telefone { get; init; }
    [JsonPropertyName("agent_id")] public string? AgentId { get; init; }
    [JsonPropertyName("iniciada_em")] public DateTimeOffset? IniciadaEm { get; init; }
    [JsonPropertyName("duracao_ms")] public long DuracaoMs { get; init; }
}

public sealed record CamposServidor
{
    [JsonPropertyName("cpf")] public CampoServidor? Cpf { get; init; }
    [JsonPropertyName("cnpj")] public CampoServidor? Cnpj { get; init; }
    [JsonPropertyName("email")] public CampoServidor? Email { get; init; }
    [JsonPropertyName("nome")] public CampoServidor? Nome { get; init; }
    [JsonPropertyName("telefone")] public CampoServidor? Telefone { get; init; }
}

public sealed record CampoServidor
{
    [JsonPropertyName("valor")] public string Valor { get; init; } = "";
    [JsonPropertyName("formatado")] public string? Formatado { get; init; }
    [JsonPropertyName("confianca")] public double Confianca { get; init; }

    /// <summary>transcricao | cadastro | extensao.</summary>
    [JsonPropertyName("origem")] public string Origem { get; init; } = "transcricao";

    [JsonPropertyName("validado_dv")] public bool ValidadoDv { get; init; }
    [JsonPropertyName("reparado")] public bool Reparado { get; init; }
    [JsonPropertyName("parcial")] public bool Parcial { get; init; }
    [JsonPropertyName("confirmado_por_repeticao")] public bool ConfirmadoPorRepeticao { get; init; }
    [JsonPropertyName("candidatos")] public List<string> Candidatos { get; init; } = new();
    [JsonPropertyName("ancora")] public AncoraServidor? Ancora { get; init; }

    /// <summary>Texto para exibição: o formatado quando existe, senão o valor cru.</summary>
    public string ParaExibicao => string.IsNullOrWhiteSpace(Formatado) ? Valor : Formatado!;
}

/// <summary>Onde no áudio o valor foi ouvido. É o que permite conferir sem procurar.</summary>
public sealed record AncoraServidor
{
    [JsonPropertyName("canal")] public string Canal { get; init; } = "";
    [JsonPropertyName("inicio_ms")] public long InicioMs { get; init; }
    [JsonPropertyName("fim_ms")] public long FimMs { get; init; }
    [JsonPropertyName("texto_bruto")] public string TextoBruto { get; init; } = "";
}

public sealed record ResumoServidor
{
    [JsonPropertyName("quem_ligou")] public string? QuemLigou { get; init; }
    [JsonPropertyName("papel")] public string? Papel { get; init; }
    [JsonPropertyName("motivo_contato")] public string? MotivoContato { get; init; }
    [JsonPropertyName("produto")] public string? Produto { get; init; }
    [JsonPropertyName("status")] public string? Status { get; init; }
    [JsonPropertyName("problema_resolvido")] public bool? ProblemaResolvido { get; init; }
    [JsonPropertyName("satisfacao")] public string? Satisfacao { get; init; }
    [JsonPropertyName("texto")] public string? Texto { get; init; }
    [JsonPropertyName("confianca")] public double Confianca { get; init; }
    [JsonPropertyName("origem")] public string Origem { get; init; } = "regra";
}

public sealed record TranscricaoServidor
{
    [JsonPropertyName("turnos")] public List<TurnoServidor> Turnos { get; init; } = new();
    [JsonPropertyName("modelo")] public string? Modelo { get; init; }
    [JsonPropertyName("duracao_ms")] public long DuracaoMs { get; init; }
}

public sealed record TurnoServidor
{
    /// <summary>agente | cliente.</summary>
    [JsonPropertyName("speaker")] public string Speaker { get; init; } = "";

    [JsonPropertyName("inicio_ms")] public long InicioMs { get; init; }
    [JsonPropertyName("fim_ms")] public long FimMs { get; init; }
    [JsonPropertyName("texto")] public string Texto { get; init; } = "";
    [JsonPropertyName("confianca")] public double Confianca { get; init; }
}

public sealed record RevisaoServidor
{
    [JsonPropertyName("necessaria")] public bool Necessaria { get; init; }
    [JsonPropertyName("motivos")] public List<string> Motivos { get; init; } = new();
}

public sealed record ProcessamentoServidor
{
    [JsonPropertyName("device")] public string? Device { get; init; }
    [JsonPropertyName("modelo")] public string? Modelo { get; init; }
    [JsonPropertyName("duracao_ms")] public long DuracaoMs { get; init; }
    [JsonPropertyName("versao")] public string? Versao { get; init; }
    [JsonPropertyName("llm_usado")] public bool LlmUsado { get; init; }
    [JsonPropertyName("avisos")] public List<string> Avisos { get; init; } = new();
}
