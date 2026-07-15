using Piloto.Core.Models;

namespace Piloto.Core.Abstractions;

/// <summary>Persistência: fila, registros, busca FTS5 e retenção.</summary>
public interface ICallRepository
{
    void Inicializar();

    // ----- Fila -----
    long EnfileirarItem(QueueItem item);
    QueueItem? ProximoPendente();
    void AtualizarItem(QueueItem item);
    int ContarPendentes();

    // ----- Registros -----
    long SalvarRegistro(CallRecord registro);
    CallRecord? ObterRegistro(long id);
    IReadOnlyList<CallRecord> ListarRegistros(int limite = 200, int offset = 0);

    /// <summary>Busca full-text (FTS5) na transcrição e no resumo.</summary>
    IReadOnlyList<CallRecord> Buscar(string termo, int limite = 200);

    // ----- Retenção -----
    /// <summary>Remove áudios além de <paramref name="diasAudio"/> e registros além de <paramref name="diasTranscricao"/>.</summary>
    RetencaoResultado AplicarRetencao(int diasAudio, int diasTranscricao);

    // ----- Métricas -----
    ContadoresGerais Contadores();
}

public sealed record RetencaoResultado(int AudiosRemovidos, int RegistrosRemovidos);

public sealed record ContadoresGerais(int TotalChamadas, TimeSpan TempoTotalFalado, int PendentesRevisao);
