using Piloto.Core.Abstractions;
using Piloto.Remote;
using Xunit;

namespace Piloto.Tests;

/// <summary>
/// A regra da qual depende uma ligação sobreviver: <b>4xx são definitivos</b> (retentar só
/// repete o erro) e <b>falha de rede/5xx é transitória</b> (retentar é o certo, e a
/// idempotência torna isso barato). Errar para o lado "definitivo" perde a ligação; errar
/// para o lado "transitório" enche a fila de reenvio inútil.
/// </summary>
public class ClassificacaoHttpTests
{
    [Theory]
    [InlineData(400)]   // nenhum canal enviado, JSON inválido — bug do cliente
    [InlineData(401)]   // token ausente/inválido
    [InlineData(403)]
    [InlineData(413)]   // acima de 200 MB, ou canal com mais de 90 min
    [InlineData(415)]   // formato de áudio não reconhecido
    [InlineData(422)]
    public void RecusaDoServidorNaoSeRetenta(int codigo)
    {
        Assert.Equal(FalhaTranscricao.Definitiva, ClassificacaoHttp.Classificar(codigo, ehGet: false));
        Assert.Equal(FalhaTranscricao.Definitiva, ClassificacaoHttp.Classificar(codigo, ehGet: true));
    }

    [Theory]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(429)]   // servidor ocupado: esperar é exatamente o certo
    public void ProblemaDoServidorSeRetenta(int codigo)
    {
        Assert.Equal(FalhaTranscricao.Transitoria, ClassificacaoHttp.Classificar(codigo, ehGet: false));
    }

    [Fact]
    public void QuatroCentosEQuatroDependeDeQualChamadaFalhou()
    {
        // No GET é o resultado que expirou (retenção de 900 s) — o áudio está em disco e
        // reenviar resolve. No POST é a rota que não existe nesse servidor, e insistir não
        // vai criá-la.
        Assert.Equal(FalhaTranscricao.Reenviar, ClassificacaoHttp.Classificar(404, ehGet: true));
        Assert.Equal(FalhaTranscricao.Definitiva, ClassificacaoHttp.Classificar(404, ehGet: false));
    }

    [Fact]
    public void MensagemDe401MandaOAtendenteParaOLugarCerto()
    {
        var msg = ClassificacaoHttp.Mensagem(401, ehGet: false, detalhe: "sem detalhes");
        Assert.Contains("Configurações", msg);
    }

    [Fact]
    public void MensagemDe413ExplicaOLimiteEmVezDeRepetirONumero()
    {
        var msg = ClassificacaoHttp.Mensagem(413, ehGet: false, detalhe: "Payload Too Large");
        Assert.Contains("200 MB", msg);
        Assert.Contains("90 minutos", msg);
    }
}
