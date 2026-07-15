namespace Piloto.Core.Models;

/// <summary>Tipo de campo objetivo extraído por regras.</summary>
public enum FieldType
{
    Telefone,
    Cpf,
    Email,
    Data,
    Valor,
    Protocolo,
}

/// <summary>
/// Um valor detectado pela camada de regras, com o texto normalizado, a confiança
/// (0..1) e o trecho de origem para rastreabilidade / grounding.
/// </summary>
public sealed class ExtractedValue
{
    public required FieldType Tipo { get; init; }

    /// <summary>Valor normalizado/canônico (ex.: CPF só com dígitos, valor em centavos formatado).</summary>
    public required string Valor { get; init; }

    /// <summary>Como apareceu no texto (para exibição e auditoria).</summary>
    public required string TrechoOrigem { get; init; }

    /// <summary>0..1 — quanto a regra confia nesta detecção.</summary>
    public required double Confianca { get; init; }

    public override string ToString() => $"{Tipo}={Valor} ({Confianca:P0})";
}

/// <summary>
/// Resultado da Camada 1 (REGRAS): listas de valores objetivos detectados na transcrição.
/// </summary>
public sealed class ObjectiveFields
{
    public List<ExtractedValue> Telefones { get; init; } = new();
    public List<ExtractedValue> Cpfs { get; init; } = new();
    public List<ExtractedValue> Emails { get; init; } = new();
    public List<ExtractedValue> Datas { get; init; } = new();
    public List<ExtractedValue> Valores { get; init; } = new();
    public List<ExtractedValue> Protocolos { get; init; } = new();

    public IEnumerable<ExtractedValue> Todos()
        => Telefones.Concat(Cpfs).Concat(Emails).Concat(Datas).Concat(Valores).Concat(Protocolos);

    public static ObjectiveFields Vazio() => new();
}
