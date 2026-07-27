using System.Text.Json;

namespace Piloto.Remote.Contrato;

/// <summary>
/// Espelho do contrato 2.0 do servidor de transcrição (CONTRATO.md). Classes burras de
/// propósito: quem traduz para o modelo do piloto é o <see cref="MapeadorContrato"/>.
/// <para>
/// Campo desconhecido no JSON é ignorado, e campo ausente vira <c>null</c>/zero — o
/// servidor pode acrescentar coisas sem quebrar um cliente já instalado.
/// </para>
/// </summary>
internal static class ContratoJson
{
    public static readonly JsonSerializerOptions Opts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };
}

/// <summary>Estados do job. O piloto compara por texto — estado desconhecido não derruba nada.</summary>
internal static class EstadoJob
{
    public const string Pendente = "pendente";
    public const string Processando = "processando";
    public const string Transcrito = "transcrito";
    public const string Concluido = "concluido";
    public const string Erro = "erro";

    /// <summary>Ordem do avanço. -1 = estado desconhecido (nunca considerado "chegou").</summary>
    public static int Ordem(string? estado) => estado switch
    {
        Pendente => 0,
        Processando => 1,
        Transcrito => 2,
        Concluido => 3,
        _ => -1,
    };

    public static bool Alcancou(string? atual, string alvo)
    {
        var o = Ordem(atual);
        return o >= 0 && o >= Ordem(alvo);
    }
}

internal sealed class SaudeDto
{
    public bool Ok { get; set; }
    public string? VersaoServidor { get; set; }
    public string? VersaoContrato { get; set; }
    public string? Modelo { get; set; }
    public string? Device { get; set; }
    public bool ModeloCarregado { get; set; }
    public int Pendentes { get; set; }
    public int Processando { get; set; }
    public bool AutenticacaoAtiva { get; set; }
    public bool AnaliseDisponivel { get; set; }
    public bool ResumoDisponivel { get; set; }
}

// Todo número aqui é ANULÁVEL de propósito. Não é zelo: `posicaoNaFila` vem preenchido no
// 202 e `null` no GET, e um `int` não-anulável transforma isso em JsonException — que o
// cliente classificaria como "resposta ilegível", ou seja, falha transitória, e a fila
// ficaria reenviando para sempre contra um servidor perfeitamente saudável.
internal sealed class JobDto
{
    public string? JobId { get; set; }
    public string? Estado { get; set; }
    public string? LigacaoId { get; set; }
    public int? PosicaoNaFila { get; set; }
    public string? Erro { get; set; }
    public ResultadoDto? Resultado { get; set; }
}

internal sealed class ResultadoDto
{
    public List<CanalDto>? Canais { get; set; }
    public DialogoDto? Dialogo { get; set; }
    public CamposDto? Campos { get; set; }
    public ResumoDto? Resumo { get; set; }
    public string? ResumoEstado { get; set; }
    public List<string>? Avisos { get; set; }
    public string? Modelo { get; set; }
    public string? Device { get; set; }
    public string? VersaoServidor { get; set; }
    public string? VersaoContrato { get; set; }
    public double? DuracaoAudioSegundos { get; set; }
    public double? TempoProcessamentoSegundos { get; set; }
    public double? TempoNaFilaSegundos { get; set; }
}

internal sealed class CanalDto
{
    public string? Speaker { get; set; }
    public double? DuracaoSegundos { get; set; }
    public bool? Vazio { get; set; }
    public string? MotivoVazio { get; set; }
    public List<SegmentoDto>? Segmentos { get; set; }
}

internal sealed class SegmentoDto
{
    public double? Inicio { get; set; }
    public double? Fim { get; set; }
    public string? Texto { get; set; }
    public double? Confianca { get; set; }
    public double? ProbSemFala { get; set; }
}

internal sealed class DialogoDto
{
    public List<TurnoDto>? Turnos { get; set; }
    public int? DescartadosPorConfianca { get; set; }
    public int? DescartadosPorPadrao { get; set; }
    public int? RepeticoesColapsadas { get; set; }

    /// <summary>&lt; 1 quando os tempos passavam da duração real e foram comprimidos.
    /// Não-nulo é sinal de desconfiar da ordem do diálogo.</summary>
    public double? FatorCompressaoTimestamps { get; set; }
}

internal sealed class TurnoDto
{
    public string? Speaker { get; set; }
    public double? Inicio { get; set; }
    public double? Fim { get; set; }
    public string? Texto { get; set; }
    public double? Confianca { get; set; }
}

internal sealed class CamposDto
{
    public List<ValorDto>? Telefones { get; set; }

    /// <summary>CPF e CNPJ juntos, distinguidos por <c>tipo</c>. Equivale a
    /// <c>ObjectiveFields.Cpfs</c> no C#, nome mantido pela compatibilidade do JSON
    /// já persistido no banco do piloto.</summary>
    public List<ValorDto>? Documentos { get; set; }

    public List<ValorDto>? Emails { get; set; }
    public List<ValorDto>? Nomes { get; set; }
    public List<ValorDto>? Datas { get; set; }
    public List<ValorDto>? Valores { get; set; }
    public List<ValorDto>? Protocolos { get; set; }
}

internal sealed class ValorDto
{
    public string? Tipo { get; set; }
    public string? Valor { get; set; }
    public string? TrechoOrigem { get; set; }
    public double Confianca { get; set; }

    /// <summary><c>regra</c> = ouvido, sujeito a erro de reconhecimento.
    /// <c>extensao</c> = cadastrado, o atendente pode copiar sem conferir.</summary>
    public string? Origem { get; set; }
}

internal sealed class ResumoDto
{
    public string? Resumo { get; set; }
    public string? MotivoContato { get; set; }
    public string? Produto { get; set; }
    public string? Status { get; set; }
    public string? Pedido { get; set; }
    public string? ProximoPasso { get; set; }
}
