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

    /// <summary>
    /// Recupera itens presos em Processando por um encerramento abrupto (crash/queda de
    /// energia). Cada queda conta como tentativa; ao atingir <paramref name="maxTentativas"/>
    /// o item vai para Erro em vez de Pendente — senão um item que derruba o processo
    /// (ex.: crash nativo) entraria em loop infinito de crash a cada inicialização.
    /// Chamar na inicialização, antes de consumir a fila.
    /// </summary>
    int RecuperarItensOrfaos(int maxTentativas);

    // ----- Registros -----
    long SalvarRegistro(CallRecord registro);
    CallRecord? ObterRegistro(long id);
    IReadOnlyList<CallRecord> ListarRegistros(int limite = 200, int offset = 0);

    /// <summary>
    /// Substitui o conteúdo do registro (transcrição, campos, resumo, revisão) mantendo
    /// id, uuid e criado_em — reprocessamento e resumo pendente atualizam em lugar, nunca
    /// duplicam a ligação. Reindexa a busca (FTS).
    /// </summary>
    void AtualizarRegistro(CallRecord registro);

    // A varredura de resumos pendentes saiu na 1.1: quem retenta o resumo é o servidor,
    // que já reprocessa por conta própria e expõe /reprocess.

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
