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
            .Replace("{DIALOGO}", LimitarDialogo(transcript.TextoRotulado()));

        // Formato de turno do Gemma 3 instruct.
        return $"<start_of_turn>user\n{corpo}<end_of_turn>\n<start_of_turn>model\n";
    }

    /// <summary>
    /// Contexto padrão é 4096 tokens (~3,5 caracteres/token em PT). Descontando template +
    /// listas (~500 tokens) e a saída (700), sobram ~2.900 tokens ≈ 10.000 caracteres para
    /// o diálogo. Ligações longas são cortadas no meio: a abertura carrega o motivo do
    /// contato; o final, a resolução e o próximo passo. Sem o corte, o prompt estoura o
    /// contexto e a saída vem truncada ou vazia.
    /// </summary>
    private const int MaxCharsDialogo = 10_000;

    internal static string LimitarDialogo(string dialogo)
    {
        if (dialogo.Length <= MaxCharsDialogo) return dialogo;
        var inicio = dialogo[..(MaxCharsDialogo / 3)];
        var fim = dialogo[^(MaxCharsDialogo * 2 / 3)..];
        return $"{inicio}\n[... trecho intermediário da ligação omitido ...]\n{fim}";
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
