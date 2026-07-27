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
/// o <see cref="Pipeline.QueueProcessor"/> consome um por vez.
/// <para>
/// Depois da migração para o servidor, a fila ficou <b>mais</b> importante, não menos: é
/// ela que segura a ligação enquanto o servidor está fora do ar. Não há mais transcrição
/// degradada — existe transcrição, ou existe reenvio.
/// </para>
/// </summary>
public sealed class QueueItem
{
    public long Id { get; set; }

    /// <summary>
    /// Identidade da ligação (ver <see cref="AudioCapture.LigacaoId"/>). Persistida porque
    /// é a <c>Idempotency-Key</c> do envio: sobreviver ao reinício do app é o que faz o
    /// reenvio reaproveitar o job em vez de transcrever tudo de novo.
    /// </summary>
    public string? LigacaoId { get; set; }

    public string CaminhoAudioAtendente { get; set; } = "";
    public string CaminhoAudioCliente { get; set; } = "";

    /// <summary>Metadados serializados (JSON) da chamada, se houver.</summary>
    public string? MetadataJson { get; set; }

    public QueueState Estado { get; set; } = QueueState.Pendente;
    public int Tentativas { get; set; }
    public string? UltimoErro { get; set; }
    public DateTimeOffset CriadoEm { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset? AtualizadoEm { get; set; }

    /// <summary>
    /// Momento a partir do qual o item volta a ser elegível. É o recuo depois de uma falha
    /// transitória (servidor fora do ar): sem ele, a fila reenviaria a cada 3 s e encheria
    /// o log de tentativas idênticas. Nulo = elegível agora.
    /// </summary>
    public DateTimeOffset? ProximaTentativaEm { get; set; }

    /// <summary>Id do <see cref="CallRecord"/> gerado quando concluído.</summary>
    public long? RegistroId { get; set; }
}
