namespace Piloto.Core.Models;

/// <summary>
/// Resultado da Camada 2 (LLM LOCAL). Campos interpretativos:
/// resumo em PT-BR e escolhas dentro das listas fechadas.
/// <para>
/// <see cref="MotivoContato"/>, <see cref="Produto"/> e <see cref="Status"/> vêm de listas
/// fechadas. <see cref="Resumo"/>, <see cref="Pedido"/> e <see cref="ProximoPasso"/> são texto livre.
/// Qualquer campo não identificado deve ser <c>null</c> — nunca inventado.
/// </para>
/// </summary>
public sealed class LlmSummary
{
    public string? Resumo { get; set; }
    public string? MotivoContato { get; set; }
    public string? Produto { get; set; }
    public string? Status { get; set; }
    public string? Pedido { get; set; }
    public string? ProximoPasso { get; set; }

    public static LlmSummary Vazio() => new();
}
