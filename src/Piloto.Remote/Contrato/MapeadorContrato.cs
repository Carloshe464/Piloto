using System.Text.Json;
using Piloto.Core.Models;

namespace Piloto.Remote.Contrato;

/// <summary>
/// Traduz o resultado do servidor para o modelo do piloto.
/// <para>
/// A regra que atravessa tudo aqui: <b>o que o servidor não fez volta como null</b>, e a
/// camada local assume. O guarda é a capacidade anunciada em <c>/v1/saude</c>
/// (<c>analiseDisponivel</c>/<c>resumoDisponivel</c>), nunca a versão do contrato — assim,
/// quando o servidor ligar a análise, o piloto passa a exibi-la sem recompilar.
/// </para>
/// </summary>
public static class MapeadorContrato
{
    /// <summary>
    /// Mapeia a resposta de <c>GET /v1/transcricoes/{jobId}</c> a partir do JSON cru.
    /// É esta a porta usada pelos testes: contrato de verdade entra, modelo do piloto sai.
    /// </summary>
    public static TranscriptionResult MapearJobJson(string json, ServidorSaude? saude = null)
    {
        var job = JsonSerializer.Deserialize<JobDto>(json, ContratoJson.Opts)
                  ?? throw new JsonException("Resposta vazia do servidor.");
        return Mapear(job.Resultado, saude);
    }

    internal static TranscriptionResult Mapear(ResultadoDto? resultado, ServidorSaude? saude)
    {
        if (resultado is null)
            return TranscriptionResult.SomenteTranscricao(Transcript.Vazio());

        var usarAnalise = saude?.AnaliseUtilizavel ?? false;
        var usarResumo = saude?.ResumoUtilizavel ?? false;

        // O diálogo do servidor já vem fundido e saneado — é o que o contrato manda exibir.
        // Sem ele (ou sem a capacidade ligada), caem os segmentos crus de `canais`, que são
        // matéria-prima: sem filtro nenhum, na ordem atendente → cliente.
        var transcript = usarAnalise && resultado.Dialogo?.Turnos is { Count: > 0 } turnos
            ? new Transcript(turnos.Where(t => !string.IsNullOrWhiteSpace(t.Texto)).Select(MapearTurno))
            : new Transcript(SegmentosDosCanais(resultado.Canais));

        var avisos = new List<string>();
        if (usarAnalise && resultado.Avisos is { Count: > 0 })
            avisos.AddRange(resultado.Avisos.Where(a => !string.IsNullOrWhiteSpace(a)));

        // Ordem embaralhada é coisa que o humano precisa saber: o servidor comprimiu
        // timestamps que passavam da duração real da ligação.
        if (usarAnalise && resultado.Dialogo?.FatorCompressaoTimestamps is { } fator)
            avisos.Add($"Tempos de fala comprimidos pelo servidor (fator {fator:0.###}) — a ordem do diálogo pode estar imprecisa.");

        var vazios = (resultado.Canais ?? new List<CanalDto>())
            .Where(c => c.Vazio == true)
            .Select(c => c.MotivoVazio ?? $"canal {c.Speaker ?? "?"} sem áudio")
            .ToList();

        return new TranscriptionResult
        {
            Transcript = transcript,
            Campos = usarAnalise ? MapearCampos(resultado.Campos) : null,
            Resumo = usarResumo ? MapearResumo(resultado.Resumo) : null,
            Avisos = avisos,
            CanaisVazios = vazios,
            Origem = $"servidor {resultado.Modelo ?? "?"}/{resultado.Device ?? "?"}",
        };
    }

    /// <summary>
    /// Segmentos dos dois canais, concatenados. A fusão ordenada por tempo é do construtor
    /// do <see cref="Transcript"/> — não se reimplementa ordenação aqui.
    /// </summary>
    private static IEnumerable<TranscriptSegment> SegmentosDosCanais(List<CanalDto>? canais)
    {
        if (canais is null) yield break;

        foreach (var canal in canais)
        {
            var speaker = MapearSpeaker(canal.Speaker);
            foreach (var s in canal.Segmentos ?? new List<SegmentoDto>())
            {
                if (string.IsNullOrWhiteSpace(s.Texto)) continue;
                yield return new TranscriptSegment
                {
                    Speaker = speaker,
                    Inicio = TimeSpan.FromSeconds(s.Inicio ?? 0),
                    Fim = TimeSpan.FromSeconds(s.Fim ?? s.Inicio ?? 0),
                    Texto = s.Texto.Trim(),
                    Confianca = s.Confianca,
                };
            }
        }
    }

    private static TranscriptSegment MapearTurno(TurnoDto t) => new()
    {
        Speaker = MapearSpeaker(t.Speaker),
        Inicio = TimeSpan.FromSeconds(t.Inicio ?? 0),
        Fim = TimeSpan.FromSeconds(t.Fim ?? t.Inicio ?? 0),
        Texto = t.Texto?.Trim() ?? "",
        Confianca = t.Confianca,
    };

    /// <summary>O contrato garante a ordem atendente → cliente; ainda assim quem decide o
    /// rótulo é o campo, não a posição.</summary>
    private static Speaker MapearSpeaker(string? speaker)
        => string.Equals(speaker, "atendente", StringComparison.OrdinalIgnoreCase)
            ? Speaker.Atendente
            : Speaker.Cliente;

    private static ObjectiveFields? MapearCampos(CamposDto? dto)
    {
        if (dto is null) return null;

        var campos = new ObjectiveFields();
        Preencher(campos.Telefones, dto.Telefones, FieldType.Telefone);
        Preencher(campos.Cpfs, dto.Documentos, FieldType.Cpf);   // `tipo` distingue CPF de CNPJ
        Preencher(campos.Emails, dto.Emails, FieldType.Email);
        Preencher(campos.Nomes, dto.Nomes, FieldType.Nome);
        Preencher(campos.Datas, dto.Datas, FieldType.Data);
        Preencher(campos.Valores, dto.Valores, FieldType.Valor);
        Preencher(campos.Protocolos, dto.Protocolos, FieldType.Protocolo);
        campos.Ordenar();
        return campos;
    }

    private static void Preencher(List<ExtractedValue> destino, List<ValorDto>? origem, FieldType padrao)
    {
        foreach (var v in origem ?? new List<ValorDto>())
        {
            if (string.IsNullOrWhiteSpace(v.Valor)) continue;
            ObjectiveFields.Mesclar(destino, new ExtractedValue
            {
                Tipo = MapearTipo(v.Tipo) ?? padrao,
                Valor = v.Valor!.Trim(),
                TrechoOrigem = v.TrechoOrigem?.Trim() ?? "",
                Confianca = v.Confianca,
                Origem = string.Equals(v.Origem, "extensao", StringComparison.OrdinalIgnoreCase)
                    ? FieldSource.Extensao
                    : FieldSource.Regra,
            });
        }
    }

    private static FieldType? MapearTipo(string? tipo) => tipo?.ToLowerInvariant() switch
    {
        "telefone" => FieldType.Telefone,
        "cpf" => FieldType.Cpf,
        "cnpj" => FieldType.Cnpj,
        "email" => FieldType.Email,
        "nome" => FieldType.Nome,
        "data" => FieldType.Data,
        "valor" => FieldType.Valor,
        "protocolo" => FieldType.Protocolo,
        _ => null,
    };

    private static LlmSummary? MapearResumo(ResumoDto? dto)
        => dto is null ? null : new LlmSummary
        {
            Resumo = Limpar(dto.Resumo),
            MotivoContato = Limpar(dto.MotivoContato),
            Produto = Limpar(dto.Produto),
            Status = Limpar(dto.Status),
            Pedido = Limpar(dto.Pedido),
            ProximoPasso = Limpar(dto.ProximoPasso),
        };

    private static string? Limpar(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    internal static ServidorSaude MapearSaude(SaudeDto dto) => new()
    {
        Ok = dto.Ok,
        VersaoServidor = dto.VersaoServidor,
        VersaoContrato = dto.VersaoContrato,
        Modelo = dto.Modelo,
        Device = dto.Device,
        ModeloCarregado = dto.ModeloCarregado,
        Pendentes = dto.Pendentes,
        Processando = dto.Processando,
        AutenticacaoAtiva = dto.AutenticacaoAtiva,
        AnaliseDisponivel = dto.AnaliseDisponivel,
        ResumoDisponivel = dto.ResumoDisponivel,
    };
}
