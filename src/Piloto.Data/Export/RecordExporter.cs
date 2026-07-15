using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Piloto.Core.Abstractions;
using Piloto.Core.Models;

namespace Piloto.Data.Export;

/// <summary>
/// Exporta registros para TXT (legível), JSON (estruturado) ou CSV (planilha).
/// O template do TXT fica isolado aqui — é o ponto de customização citado no README.
/// </summary>
public sealed class RecordExporter : IExporter
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public string Exportar(CallRecord registro, ExportFormat formato, bool mascararPii = true) => formato switch
    {
        ExportFormat.Txt => ParaTxt(registro, mascararPii),
        ExportFormat.Json => ParaJson(new[] { registro }, mascararPii),
        ExportFormat.Csv => CabecalhoCsv() + Environment.NewLine + LinhaCsv(registro, mascararPii),
        _ => throw new ArgumentOutOfRangeException(nameof(formato)),
    };

    public string ExportarLote(IEnumerable<CallRecord> registros, ExportFormat formato, bool mascararPii = true)
    {
        var lista = registros.ToList();
        return formato switch
        {
            ExportFormat.Txt => string.Join(Environment.NewLine + new string('=', 60) + Environment.NewLine,
                                             lista.Select(r => ParaTxt(r, mascararPii))),
            ExportFormat.Json => ParaJson(lista, mascararPii),
            ExportFormat.Csv => CabecalhoCsv() + Environment.NewLine +
                                string.Join(Environment.NewLine, lista.Select(r => LinhaCsv(r, mascararPii))),
            _ => throw new ArgumentOutOfRangeException(nameof(formato)),
        };
    }

    // -------------------------------------------------------------------- TXT

    private static string ParaTxt(CallRecord r, bool mascarar)
    {
        string M(string? s) => mascarar ? PiiMasker.Mascarar(s) : (s ?? "");
        string OuNaoIdent(string? s) => string.IsNullOrWhiteSpace(s) ? "Não identificado" : s;

        var sb = new StringBuilder();
        sb.AppendLine("=== REGISTRO DE LIGAÇÃO ===");
        sb.AppendLine($"ID: {r.Id}   UUID: {r.Uuid}");
        sb.AppendLine($"Data: {r.CriadoEm.LocalDateTime:dd/MM/yyyy HH:mm}");
        sb.AppendLine($"Duração: {Formatar(r.Duracao)}   Tempo falado: {Formatar(r.TempoFalado)}");
        sb.AppendLine($"Número: {OuNaoIdent(M(r.Metadata.Numero))}   Ticket: {OuNaoIdent(r.Metadata.TicketId)}");
        sb.AppendLine($"Atendente: {OuNaoIdent(r.Metadata.Atendente)}   Status Zendesk: {OuNaoIdent(r.Metadata.Status)}");
        if (r.PrecisaRevisao)
        {
            sb.AppendLine();
            sb.AppendLine("⚠ REVISÃO HUMANA NECESSÁRIA:");
            foreach (var motivo in r.MotivosRevisao) sb.AppendLine($"  - {motivo}");
        }

        sb.AppendLine();
        sb.AppendLine("--- RESUMO ---");
        sb.AppendLine($"Motivo do contato: {OuNaoIdent(r.Resumo.MotivoContato)}");
        sb.AppendLine($"Produto: {OuNaoIdent(r.Resumo.Produto)}");
        sb.AppendLine($"Status: {OuNaoIdent(r.Resumo.Status)}");
        sb.AppendLine($"Resumo: {OuNaoIdent(M(r.Resumo.Resumo))}");
        sb.AppendLine($"Pedido: {OuNaoIdent(M(r.Resumo.Pedido))}");
        sb.AppendLine($"Próximo passo: {OuNaoIdent(M(r.Resumo.ProximoPasso))}");

        sb.AppendLine();
        sb.AppendLine("--- CAMPOS OBJETIVOS ---");
        AppendCampos(sb, "Telefones", r.Campos.Telefones, mascarar);
        AppendCampos(sb, "CPFs", r.Campos.Cpfs, mascarar);
        AppendCampos(sb, "E-mails", r.Campos.Emails, mascarar);
        AppendCampos(sb, "Datas", r.Campos.Datas, mascarar);
        AppendCampos(sb, "Valores", r.Campos.Valores, mascarar);
        AppendCampos(sb, "Protocolos", r.Campos.Protocolos, mascarar);

        sb.AppendLine();
        sb.AppendLine("--- DIÁLOGO ---");
        sb.AppendLine(mascarar ? PiiMasker.Mascarar(r.Transcript.TextoRotulado()) : r.Transcript.TextoRotulado());

        return sb.ToString().TrimEnd();
    }

    private static void AppendCampos(StringBuilder sb, string titulo, IReadOnlyList<ExtractedValue> valores, bool mascarar)
    {
        if (valores.Count == 0)
        {
            sb.AppendLine($"{titulo}: Não identificado");
            return;
        }
        var itens = valores.Select(v =>
        {
            var valor = mascarar ? PiiMasker.Mascarar(v.Valor) : v.Valor;
            return $"{valor} ({v.Confianca:P0})";
        });
        sb.AppendLine($"{titulo}: {string.Join("; ", itens)}");
    }

    // ------------------------------------------------------------------- JSON

    private static string ParaJson(IEnumerable<CallRecord> registros, bool mascarar)
    {
        var dtos = registros.Select(r => new
        {
            r.Id,
            r.Uuid,
            CriadoEm = r.CriadoEm,
            DuracaoSegundos = r.Duracao.TotalSeconds,
            TempoFaladoSegundos = r.TempoFalado.TotalSeconds,
            Metadata = new
            {
                Numero = Aplicar(r.Metadata.Numero, mascarar),
                r.Metadata.TicketId,
                r.Metadata.Status,
                r.Metadata.Atendente,
            },
            PrecisaRevisao = r.PrecisaRevisao,
            MotivosRevisao = r.MotivosRevisao,
            Resumo = new
            {
                r.Resumo.MotivoContato,
                r.Resumo.Produto,
                r.Resumo.Status,
                Texto = Aplicar(r.Resumo.Resumo, mascarar),
                Pedido = Aplicar(r.Resumo.Pedido, mascarar),
                ProximoPasso = Aplicar(r.Resumo.ProximoPasso, mascarar),
            },
            Campos = new
            {
                Telefones = MapearCampos(r.Campos.Telefones, mascarar),
                Cpfs = MapearCampos(r.Campos.Cpfs, mascarar),
                Emails = MapearCampos(r.Campos.Emails, mascarar),
                Datas = MapearCampos(r.Campos.Datas, mascarar),
                Valores = MapearCampos(r.Campos.Valores, mascarar),
                Protocolos = MapearCampos(r.Campos.Protocolos, mascarar),
            },
            Dialogo = Aplicar(r.Transcript.TextoRotulado(), mascarar),
        });

        var lista = dtos.ToList();
        return lista.Count == 1
            ? JsonSerializer.Serialize(lista[0], JsonOpts)
            : JsonSerializer.Serialize(lista, JsonOpts);
    }

    private static object[] MapearCampos(IReadOnlyList<ExtractedValue> valores, bool mascarar)
        => valores.Select(v => (object)new { Valor = Aplicar(v.Valor, mascarar), v.Confianca }).ToArray();

    // -------------------------------------------------------------------- CSV

    private static string CabecalhoCsv()
        => string.Join(';', new[]
        {
            "id", "criado_em", "numero", "ticket", "atendente", "motivo", "produto", "status",
            "precisa_revisao", "duracao_seg", "tempo_falado_seg", "resumo",
        });

    private static string LinhaCsv(CallRecord r, bool mascarar)
    {
        string A(string? s) => Aplicar(s, mascarar) ?? "";
        var campos = new[]
        {
            r.Id.ToString(CultureInfo.InvariantCulture),
            r.CriadoEm.LocalDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            A(r.Metadata.Numero),
            r.Metadata.TicketId ?? "",
            r.Metadata.Atendente ?? "",
            r.Resumo.MotivoContato ?? "",
            r.Resumo.Produto ?? "",
            r.Resumo.Status ?? "",
            r.PrecisaRevisao ? "sim" : "não",
            r.Duracao.TotalSeconds.ToString("0", CultureInfo.InvariantCulture),
            r.TempoFalado.TotalSeconds.ToString("0", CultureInfo.InvariantCulture),
            A(r.Resumo.Resumo),
        };
        return string.Join(';', campos.Select(EscaparCsv));
    }

    private static string EscaparCsv(string campo)
    {
        if (campo.Contains(';') || campo.Contains('"') || campo.Contains('\n') || campo.Contains('\r'))
            return '"' + campo.Replace("\"", "\"\"") + '"';
        return campo;
    }

    // ---------------------------------------------------------------- Helpers

    private static string? Aplicar(string? s, bool mascarar)
        => mascarar ? PiiMasker.Mascarar(s) : s;

    private static string Formatar(TimeSpan t) => t.ToString(@"hh\:mm\:ss");
}
