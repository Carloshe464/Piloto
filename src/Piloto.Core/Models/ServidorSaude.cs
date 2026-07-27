namespace Piloto.Core.Models;

/// <summary>
/// Resposta de <c>GET /v1/saude</c> — teste de conectividade e de <b>capacidade</b> ao
/// mesmo tempo. É por ela que o piloto sabe o que virá preenchido no resultado, em vez
/// de assumir.
/// </summary>
public sealed class ServidorSaude
{
    /// <summary>Versão de contrato que este cliente sabe interpretar.</summary>
    public const string ContratoSuportado = "2.0";

    public bool Ok { get; init; }
    public string? VersaoServidor { get; init; }
    public string? VersaoContrato { get; init; }
    public string? Modelo { get; init; }
    public string? Device { get; init; }
    public bool ModeloCarregado { get; init; }
    public int Pendentes { get; init; }
    public int Processando { get; init; }
    public bool AutenticacaoAtiva { get; init; }

    /// <summary>Servidor devolve <c>dialogo</c> e <c>campos</c> preenchidos.</summary>
    public bool AnaliseDisponivel { get; init; }

    /// <summary>Servidor devolve <c>resumo</c> preenchido.</summary>
    public bool ResumoDisponivel { get; init; }

    /// <summary>
    /// Mesma família de contrato (compara a versão maior). Contrato diferente não impede
    /// transcrever — <c>canais</c> é a parte estável —, mas faz o piloto ignorar
    /// <c>dialogo</c>, <c>campos</c> e <c>resumo</c> em vez de fingir que os entende.
    /// </summary>
    public bool ContratoCompativel =>
        Maior(VersaoContrato) is { } v && v == Maior(ContratoSuportado);

    /// <summary>Análise utilizável: o servidor diz que faz E o contrato é o que conhecemos.</summary>
    public bool AnaliseUtilizavel => AnaliseDisponivel && ContratoCompativel;

    public bool ResumoUtilizavel => ResumoDisponivel && ContratoCompativel;

    public string Descricao =>
        $"{Modelo ?? "?"} em {Device ?? "?"} · contrato {VersaoContrato ?? "?"} · "
        + $"fila {Pendentes} pendente(s), {Processando} em processamento";

    private static string? Maior(string? versao)
    {
        if (string.IsNullOrWhiteSpace(versao)) return null;
        var ponto = versao.IndexOf('.');
        return ponto < 0 ? versao.Trim() : versao[..ponto].Trim();
    }
}
