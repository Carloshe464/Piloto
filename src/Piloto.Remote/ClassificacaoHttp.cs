using Piloto.Core.Abstractions;

namespace Piloto.Remote;

/// <summary>
/// Traduz o status HTTP do servidor em decisão da fila. É a regra da qual depende uma
/// ligação sobreviver ou não, então mora sozinha e com teste próprio, em vez de enterrada
/// num <c>catch</c>.
/// <para>
/// A distinção que importa: <b>4xx são definitivos</b> (retentar só repete o erro) e
/// <b>falha de rede/5xx é transitória</b> (retentar é o comportamento certo, e a
/// idempotência do servidor torna isso barato).
/// </para>
/// </summary>
public static class ClassificacaoHttp
{
    public static FalhaTranscricao Classificar(int codigo, bool ehGet)
    {
        // 404 no GET é o resultado que expirou (retenção de 900 s) ou o job que sumiu — o
        // áudio continua em disco, então reenviar é a saída. 404 no POST é outra coisa
        // completamente: a rota não existe nesse servidor, e insistir não vai criá-la.
        if (codigo == 404)
            return ehGet ? FalhaTranscricao.Reenviar : FalhaTranscricao.Definitiva;

        return codigo is 400 or 401 or 403 or 405 or 413 or 415 or 422
            ? FalhaTranscricao.Definitiva
            : FalhaTranscricao.Transitoria;
    }

    /// <summary>Mensagem que o atendente e o log vão ler. <paramref name="detalhe"/> é o
    /// trecho curto do corpo — nunca o corpo inteiro, que pode trazer transcrição.</summary>
    public static string Mensagem(int codigo, bool ehGet, string detalhe) => codigo switch
    {
        400 => $"O servidor recusou o envio (400): {detalhe}",
        401 or 403 => "Token do servidor de transcrição ausente ou inválido — confira em Configurações.",
        404 when ehGet => "O resultado expirou no servidor (retenção de 900 s) — o áudio será reenviado.",
        404 => $"Rota não encontrada no servidor (404): {detalhe}",
        413 => "Ligação acima do limite do servidor (413): mais de 200 MB ou canal com mais de 90 minutos.",
        415 => $"Formato de áudio não reconhecido pelo servidor (415): {detalhe}",
        _ => $"O servidor respondeu {codigo}: {detalhe}",
    };
}
