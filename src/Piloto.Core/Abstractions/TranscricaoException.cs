namespace Piloto.Core.Abstractions;

/// <summary>
/// Como a fila deve reagir a uma falha de transcrição. É a distinção que decide se uma
/// ligação sobrevive: sem ela, 10 segundos de servidor fora do ar consomem as três
/// tentativas e a ligação vira registro de erro.
/// </summary>
public enum FalhaTranscricao
{
    /// <summary>
    /// Servidor fora do ar, timeout, 5xx. O áudio está em disco e o problema não é dele:
    /// reenviar é o comportamento certo e <b>não</b> consome tentativa.
    /// </summary>
    Transitoria,

    /// <summary>
    /// 400, 401, 413, 415 — o servidor recusou e recusaria de novo. Retentar só repete o
    /// erro; a ligação vai direto para revisão humana com o motivo.
    /// </summary>
    Definitiva,

    /// <summary>
    /// O resultado expirou no servidor (404 no GET, retenção de 900 s) ou o job sumiu. O
    /// áudio continua em disco: reenviar do zero é a saída, e a idempotência torna isso barato.
    /// </summary>
    Reenviar,

    /// <summary>
    /// O job terminou em <c>erro</c> no servidor. Pode ser a ligação (áudio corrompido) ou
    /// um soluço de lá: retentar cria um job novo, então vale tentar — mas com limite, senão
    /// uma ligação problemática ocuparia a fila para sempre.
    /// </summary>
    Processamento,
}

/// <summary>Falha classificada de transcrição — ver <see cref="FalhaTranscricao"/>.</summary>
public class TranscricaoException : Exception
{
    public FalhaTranscricao Tipo { get; }

    public TranscricaoException(FalhaTranscricao tipo, string mensagem, Exception? interna = null)
        : base(mensagem, interna) => Tipo = tipo;
}
