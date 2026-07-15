using System.Text.RegularExpressions;
using Piloto.Core.Abstractions;
using Piloto.Core.Configuration;
using Piloto.Core.Models;
using Piloto.Core.Text;

namespace Piloto.Core.Grounding;

/// <summary>
/// Camada 3 — GROUNDING. Regra de ouro: nada que não exista na transcrição pode
/// aparecer como dado limpo no registro.
/// <list type="number">
///   <item>Listas fechadas: <see cref="LlmSummary.MotivoContato"/>, <see cref="LlmSummary.Produto"/>
///         e <see cref="LlmSummary.Status"/> fora da lista viram <c>null</c> e marcam revisão.</item>
///   <item>Números interpretativos: qualquer sequência de 3+ dígitos citada pelo LLM que
///         não conste na transcrição marca o registro para revisão humana.</item>
/// </list>
/// </summary>
public sealed class GroundingChecker : IGroundingChecker
{
    private static readonly Regex Numeros = new(@"\d[\d.\-/]{2,}\d|\d{3,}", RegexOptions.Compiled);
    private readonly ITextNormalizer _normalizer;

    public GroundingChecker(ITextNormalizer normalizer) => _normalizer = normalizer;

    public void Aplicar(CallRecord registro, ListasFechadas listas)
    {
        var resumo = registro.Resumo;
        var textoNormalizado = _normalizer.Normalizar(registro.Transcript.TextoCorrido());
        var digitosTranscricao = TextUtils.SomenteDigitos(textoNormalizado);

        ValidarLista(registro, "motivo_contato", listas.MotivoContato, () => resumo.MotivoContato, v => resumo.MotivoContato = v);
        ValidarLista(registro, "produto", listas.Produto, () => resumo.Produto, v => resumo.Produto = v);
        ValidarLista(registro, "status", listas.Status, () => resumo.Status, v => resumo.Status = v);

        VerificarNumeros(registro, "resumo", resumo.Resumo, digitosTranscricao);
        VerificarNumeros(registro, "pedido", resumo.Pedido, digitosTranscricao);
        VerificarNumeros(registro, "próximo passo", resumo.ProximoPasso, digitosTranscricao);
    }

    private static void ValidarLista(CallRecord registro, string nomeCampo, IReadOnlyList<string> lista,
        Func<string?> get, Action<string?> set)
    {
        var valor = get();
        if (string.IsNullOrWhiteSpace(valor)) return;

        if (!ListasFechadas.Contem(lista, valor))
        {
            set(null);
            registro.MarcarRevisao($"Campo '{nomeCampo}' retornou valor fora da lista fechada: \"{valor}\".");
        }
    }

    private static void VerificarNumeros(CallRecord registro, string nomeCampo, string? texto, string digitosTranscricao)
    {
        if (string.IsNullOrWhiteSpace(texto)) return;

        foreach (Match m in Numeros.Matches(texto))
        {
            var digitos = TextUtils.SomenteDigitos(m.Value);
            if (digitos.Length < 3) continue;

            if (!digitosTranscricao.Contains(digitos, StringComparison.Ordinal))
            {
                registro.MarcarRevisao(
                    $"Número \"{m.Value}\" no campo '{nomeCampo}' não consta na transcrição (possível alucinação).");
            }
        }
    }
}
