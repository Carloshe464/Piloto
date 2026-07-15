namespace Piloto.Core.Models;

public enum QueueState
{
    Pendente,
    Processando,
    Concluido,
    Erro,
}

/// <summary>
/// Item da fila de processamento persistida (SQLite). Uma chamada encerrada gera um item;
/// o <see cref="Pipeline.QueueProcessor"/> consome um por vez, em prioridade baixa.
/// </summary>
public sealed class QueueItem
{
    public long Id { get; set; }
    public string CaminhoAudioAtendente { get; set; } = "";
    public string CaminhoAudioCliente { get; set; } = "";

    /// <summary>Metadados serializados (JSON) da chamada, se houver.</summary>
    public string? MetadataJson { get; set; }

    public QueueState Estado { get; set; } = QueueState.Pendente;
    public int Tentativas { get; set; }
    public string? UltimoErro { get; set; }
    public DateTimeOffset CriadoEm { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset? AtualizadoEm { get; set; }

    /// <summary>Id do <see cref="CallRecord"/> gerado quando concluído.</summary>
    public long? RegistroId { get; set; }
}
