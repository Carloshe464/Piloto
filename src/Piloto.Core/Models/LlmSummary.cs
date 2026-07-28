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

    /// <summary>
    /// Como o cliente saiu da ligação: <c>satisfeito</c>, <c>com_duvidas</c> ou <c>triste</c>.
    /// <para>
    /// Vem do servidor e é persistido, mas <b>ainda não aparece na tela</b> — a janela de
    /// detalhe não tem campo para ele. Guardado desde já para não perder o histórico: quando
    /// a tela ganhar o campo, os registros antigos já terão o dado.
    /// </para>
    /// </summary>
    public string? Satisfacao { get; set; }

    /// <summary>Se o problema foi resolvido na própria ligação. Mesma situação da
    /// <see cref="Satisfacao"/>: persistido, ainda sem lugar na tela.</summary>
    public bool? ProblemaResolvido { get; set; }

    /// <summary>Quem estava do outro lado da linha, com o papel quando identificado
    /// ("Ana da Silva (terceiro)").</summary>
    public string? QuemLigou { get; set; }

    public static LlmSummary Vazio() => new();
}
