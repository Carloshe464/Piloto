using Piloto.Core.Models;

namespace Piloto.Core.Abstractions;

public enum ExportFormat
{
    Txt,
    Json,
    Csv,
}

/// <summary>Exporta um registro (ou coleção) para TXT/JSON/CSV, com PII mascarada por padrão.</summary>
public interface IExporter
{
    string Exportar(CallRecord registro, ExportFormat formato, bool mascararPii = true);
    string ExportarLote(IEnumerable<CallRecord> registros, ExportFormat formato, bool mascararPii = true);
}
