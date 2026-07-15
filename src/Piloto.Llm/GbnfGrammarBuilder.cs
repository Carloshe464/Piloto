using System.Text;
using Piloto.Core.Configuration;

namespace Piloto.Llm;

/// <summary>
/// Gera uma gramática GBNF que força a saída do LLM a um JSON com as chaves fixas e,
/// para <c>motivo_contato</c>, <c>produto</c> e <c>status</c>, restringe o valor às listas
/// fechadas (ou <c>null</c>). Assim o modelo <b>escolhe</b> — não redige.
/// </summary>
public static class GbnfGrammarBuilder
{
    public static string Construir(ListasFechadas listas)
    {
        var sb = new StringBuilder();

        sb.AppendLine("""
            root   ::= "{" ws
              "\"resumo\":" ws string ws "," ws
              "\"motivo_contato\":" ws motivo ws "," ws
              "\"produto\":" ws produto ws "," ws
              "\"status\":" ws status ws "," ws
              "\"pedido\":" ws stringnull ws "," ws
              "\"proximo_passo\":" ws stringnull ws
            "}" ws
            """);
        sb.AppendLine();
        sb.AppendLine("motivo  ::= " + Alternativas(listas.MotivoContato));
        sb.AppendLine("produto ::= " + Alternativas(listas.Produto));
        sb.AppendLine("status  ::= " + Alternativas(listas.Status));
        sb.AppendLine();
        sb.AppendLine("""
            stringnull ::= string | "null"
            string ::= "\"" char* "\""
            char   ::= [^"\\] | "\\" ["\\/bfnrt]
            ws     ::= [ \t\n]*
            """);

        return sb.ToString();
    }

    /// <summary>Alternância de literais JSON entre os valores da lista e <c>null</c>.</summary>
    private static string Alternativas(IReadOnlyList<string> valores)
    {
        var literais = valores
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => "\"\\\"" + EscaparGbnf(v) + "\\\"\"");
        var todas = literais.Append("\"null\"");
        return string.Join(" | ", todas);
    }

    /// <summary>Escapa um valor para aparecer dentro de um literal de string GBNF.</summary>
    private static string EscaparGbnf(string valor)
    {
        var sb = new StringBuilder(valor.Length);
        foreach (var c in valor)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }
}
