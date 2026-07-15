using Piloto.Core.Configuration;
using Piloto.Core.Models;

namespace Piloto.Llm;

/// <summary>
/// Monta o prompt do resumo a partir do template <c>Prompts/resumo.pt-BR.txt</c>
/// e envolve no formato de turnos do Gemma 3 instruct.
/// </summary>
public sealed class PromptBuilder
{
    private readonly string _template;

    public PromptBuilder(string? caminhoTemplate = null)
    {
        _template = CarregarTemplate(caminhoTemplate);
    }

    public string Construir(Transcript transcript, ListasFechadas listas)
    {
        var corpo = _template
            .Replace("{MOTIVOS}", string.Join(", ", listas.MotivoContato))
            .Replace("{PRODUTOS}", string.Join(", ", listas.Produto))
            .Replace("{STATUS}", string.Join(", ", listas.Status))
            .Replace("{DIALOGO}", transcript.TextoRotulado());

        // Formato de turno do Gemma 3 instruct.
        return $"<start_of_turn>user\n{corpo}<end_of_turn>\n<start_of_turn>model\n";
    }

    private static string CarregarTemplate(string? caminho)
    {
        caminho ??= Path.Combine(AppContext.BaseDirectory, "Prompts", "resumo.pt-BR.txt");
        if (File.Exists(caminho))
            return File.ReadAllText(caminho);
        return TemplatePadrao;
    }

    private const string TemplatePadrao = """
        Você estrutura registros de atendimento em PT-BR. Responda SOMENTE com JSON.
        Nunca invente dados; use null quando não houver. motivo_contato/produto/status devem
        ser exatamente um dos valores permitidos (ou null).
        motivo_contato: {MOTIVOS}
        produto: {PRODUTOS}
        status: {STATUS}
        JSON: {"resumo","motivo_contato","produto","status","pedido","proximo_passo"}
        DIÁLOGO:
        {DIALOGO}
        """;
}
