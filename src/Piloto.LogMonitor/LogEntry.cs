namespace Piloto.LogMonitor;

public sealed class LogEntry
{
    public string Hora { get; init; } = "";
    public string Modulo { get; init; } = "";
    public string Tipo { get; init; } = "";
    public string Descricao { get; init; } = "";
    public string Detalhes { get; set; } = "";
    public bool EhErro => Tipo.Contains("Error", StringComparison.OrdinalIgnoreCase)
                          || Tipo.Contains("Critical", StringComparison.OrdinalIgnoreCase);
}
