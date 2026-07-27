using Piloto.Core.Models;
using Piloto.Remote.Contrato;
using Xunit;

namespace Piloto.Tests;

/// <summary>
/// O contrato 2.0 entrando pela porta da frente (JSON do servidor) e saindo como modelo do
/// piloto. É aqui que se pega a diferença que mais custa caro: <b>campo nulo significa "o
/// servidor não fez"</b>, não "fez e não achou".
/// </summary>
public class MapeadorContratoTests
{
    private static ServidorSaude Saude(bool analise, bool resumo, string contrato = "2.0") => new()
    {
        Ok = true,
        VersaoContrato = contrato,
        Modelo = "medium",
        Device = "cuda",
        ModeloCarregado = true,
        AnaliseDisponivel = analise,
        ResumoDisponivel = resumo,
    };

    /// <summary>Resposta como o servidor a devolve HOJE: canais crus, análise e resumo nulos.</summary>
    private const string JsonCanaisCrus = """
        {
          "jobId": "9f2c",
          "estado": "concluido",
          "ligacaoId": "abc",
          "resultado": {
            "canais": [
              { "speaker": "atendente", "duracaoSegundos": 12.0, "vazio": false, "motivoVazio": null,
                "segmentos": [
                  { "inicio": 0.0, "fim": 3.24, "texto": " Bom dia, em que posso ajudar? ", "confianca": 0.94, "probSemFala": 0.01 },
                  { "inicio": 8.0, "fim": 9.5, "texto": "Perfeito.", "confianca": 0.88, "probSemFala": 0.02 }
                ] },
              { "speaker": "cliente", "duracaoSegundos": 6.0, "vazio": false, "motivoVazio": null,
                "segmentos": [
                  { "inicio": 4.38, "fim": 6.10, "texto": "Preciso da segunda via.", "confianca": 0.93, "probSemFala": 0.0 }
                ] }
            ],
            "dialogo": null,
            "campos": null,
            "resumo": null,
            "resumoEstado": "desativado",
            "avisos": [],
            "metadados": {},
            "modelo": "medium",
            "device": "cuda",
            "versaoServidor": "0.1.0",
            "versaoContrato": "2.0",
            "duracaoAudioSegundos": 12.0,
            "tempoProcessamentoSegundos": 2.3,
            "tempoNaFilaSegundos": 0.0
          }
        }
        """;

    [Fact]
    public void CanaisCrusViramUmDialogoOrdenadoPorTempo()
    {
        var r = MapeadorContrato.MapearJobJson(JsonCanaisCrus, Saude(analise: false, resumo: false));

        Assert.Equal(3, r.Transcript.Segmentos.Count);

        // A fusão dos dois canais é do construtor do Transcript — o cliente concatena e ordena.
        Assert.Equal(
            new[] { Speaker.Atendente, Speaker.Cliente, Speaker.Atendente },
            r.Transcript.Segmentos.Select(s => s.Speaker));

        var primeiro = r.Transcript.Segmentos[0];
        Assert.Equal("Bom dia, em que posso ajudar?", primeiro.Texto);   // trim aplicado
        Assert.Equal(TimeSpan.FromSeconds(3.24), primeiro.Fim);
        Assert.Equal(0.94, primeiro.Confianca);
    }

    [Fact]
    public void SemAnaliseDoServidorOsCamposEOResumoVoltamNulos()
    {
        // Nulo é o sinal que faz o pipeline manter as camadas locais. Se aqui viesse um
        // ObjectiveFields vazio, a ligação sairia sem nenhum campo e ninguém notaria.
        var r = MapeadorContrato.MapearJobJson(JsonCanaisCrus, Saude(analise: false, resumo: false));

        Assert.Null(r.Campos);
        Assert.Null(r.Resumo);
        Assert.Empty(r.Avisos);
    }

    [Fact]
    public void CanalMudoNaoEhErro_VoltaComOMotivo()
    {
        const string json = """
            {
              "jobId": "1", "estado": "concluido",
              "resultado": {
                "canais": [
                  { "speaker": "atendente", "duracaoSegundos": 5.0, "vazio": false,
                    "segmentos": [ { "inicio": 0, "fim": 2, "texto": "Alô?", "confianca": 0.9 } ] },
                  { "speaker": "cliente", "duracaoSegundos": 0.0, "vazio": true,
                    "motivoVazio": "cliente.wav: sem amostras de áudio (só cabeçalho)",
                    "segmentos": [] }
                ]
              }
            }
            """;

        var r = MapeadorContrato.MapearJobJson(json, Saude(analise: false, resumo: false));

        Assert.Single(r.Transcript.Segmentos);          // a ligação segue com o outro canal
        Assert.Contains("sem amostras de áudio", Assert.Single(r.CanaisVazios));
    }

    /// <summary>Resposta com as capacidades ligadas — o estado para o qual o piloto foi escrito.</summary>
    private const string JsonAnaliseCompleta = """
        {
          "jobId": "9f2c",
          "estado": "concluido",
          "resultado": {
            "canais": [
              { "speaker": "atendente", "duracaoSegundos": 10.0, "vazio": false,
                "segmentos": [ { "inicio": 0, "fim": 3, "texto": "cru do atendente", "confianca": 0.5 } ] }
            ],
            "dialogo": {
              "turnos": [
                { "speaker": "cliente", "inicio": 4.38, "fim": 9.5, "texto": "Preciso da segunda via do boleto.", "confianca": 0.93 },
                { "speaker": "atendente", "inicio": 0.0, "fim": 3.2, "texto": "Bom dia, em que posso ajudar?", "confianca": 0.94 }
              ],
              "descartadosPorConfianca": 2,
              "descartadosPorPadrao": 1,
              "repeticoesColapsadas": 0,
              "fatorCompressaoTimestamps": null
            },
            "campos": {
              "telefones": [ { "tipo": "telefone", "valor": "11987654321", "trechoOrigem": "número da ligação (discador)", "confianca": 1.0, "origem": "extensao" } ],
              "documentos": [
                { "tipo": "cnpj", "valor": "12.345.678/0001-90", "trechoOrigem": "12.345.678/0001-90", "confianca": 0.6, "origem": "regra" },
                { "tipo": "cpf", "valor": "111.444.777-35", "trechoOrigem": "cento e onze...", "confianca": 0.95, "origem": "regra" }
              ],
              "emails": [],
              "nomes": [ { "tipo": "nome", "valor": "Vinicius Ferreira", "trechoOrigem": "cadastro do Zendesk", "confianca": 1.0, "origem": "extensao" } ],
              "datas": [], "valores": [], "protocolos": []
            },
            "resumo": {
              "resumo": "Cliente pediu a segunda via do boleto.",
              "motivoContato": "Segunda via de boleto",
              "produto": "Plano Básico",
              "status": "Resolvido",
              "pedido": "Segunda via do boleto do mês corrente.",
              "proximoPasso": "Reenviar o boleto por e-mail em até 24 h."
            },
            "resumoEstado": "concluido",
            "avisos": [ "Número \"4471\" no campo 'resumo' não consta na transcrição (possível alucinação)." ],
            "modelo": "large-v3-turbo", "device": "cuda", "versaoContrato": "2.0"
          }
        }
        """;

    [Fact]
    public void ComAnaliseLigadaODialogoDoServidorVenceOsCanaisCrus()
    {
        var r = MapeadorContrato.MapearJobJson(JsonAnaliseCompleta, Saude(analise: true, resumo: true));

        // `canais` continua vindo (auditoria), mas quem vai para a tela é o diálogo saneado.
        Assert.Equal(2, r.Transcript.Segmentos.Count);
        Assert.DoesNotContain(r.Transcript.Segmentos, s => s.Texto == "cru do atendente");
        Assert.Equal(Speaker.Atendente, r.Transcript.Segmentos[0].Speaker);   // reordenado por tempo
    }

    [Fact]
    public void DocumentosViramCpfsComOTipoPreservado()
    {
        var r = MapeadorContrato.MapearJobJson(JsonAnaliseCompleta, Saude(analise: true, resumo: true));

        Assert.NotNull(r.Campos);
        Assert.Equal(2, r.Campos!.Cpfs.Count);
        Assert.Contains(r.Campos.Cpfs, v => v.Tipo == FieldType.Cnpj && v.Valor == "12.345.678/0001-90");
        Assert.Contains(r.Campos.Cpfs, v => v.Tipo == FieldType.Cpf);

        // Ordenação por força da detecção: o CPF de 0,95 antes do CNPJ de 0,60.
        Assert.Equal(FieldType.Cpf, r.Campos.Cpfs[0].Tipo);
    }

    [Fact]
    public void NomeDoServidorChegaComoCampoDoCadastro()
    {
        var r = MapeadorContrato.MapearJobJson(JsonAnaliseCompleta, Saude(analise: true, resumo: true));

        var nome = Assert.Single(r.Campos!.Nomes);
        Assert.Equal("Vinicius Ferreira", nome.Valor);
        Assert.Equal(FieldSource.Extensao, nome.Origem);
        Assert.Equal(FieldType.Nome, nome.Tipo);
    }

    [Fact]
    public void ResumoEAvisosDoServidorChegamInteiros()
    {
        var r = MapeadorContrato.MapearJobJson(JsonAnaliseCompleta, Saude(analise: true, resumo: true));

        Assert.Equal("Segunda via de boleto", r.Resumo!.MotivoContato);
        Assert.Equal("Resolvido", r.Resumo.Status);
        Assert.Contains("4471", Assert.Single(r.Avisos));
    }

    [Fact]
    public void ResumoDesligadoNoServidorNaoEhUsadoAindaQueVenhaNoJson()
    {
        // O guarda é a capacidade anunciada, não a presença do campo: um servidor que
        // devolva resumo sintético (modo falso) não pode passar por resumo de verdade.
        var r = MapeadorContrato.MapearJobJson(JsonAnaliseCompleta, Saude(analise: true, resumo: false));

        Assert.NotNull(r.Campos);
        Assert.Null(r.Resumo);
    }

    [Fact]
    public void ContratoIncompativelIgnoraAnaliseMasMantemATranscricao()
    {
        // Transcrever é o núcleo do produto: `canais` é a parte estável do contrato e
        // continua valendo. O resto, que pode ter mudado de significado, não.
        var r = MapeadorContrato.MapearJobJson(JsonAnaliseCompleta, Saude(analise: true, resumo: true, contrato: "3.0"));

        Assert.Single(r.Transcript.Segmentos);
        Assert.Equal("cru do atendente", r.Transcript.Segmentos[0].Texto);
        Assert.Null(r.Campos);
        Assert.Null(r.Resumo);
    }

    [Fact]
    public void SemSaudeConhecidaOPilotoNaoAssumeCapacidadeNenhuma()
    {
        var r = MapeadorContrato.MapearJobJson(JsonAnaliseCompleta, saude: null);

        Assert.Null(r.Campos);
        Assert.Null(r.Resumo);
        Assert.NotEmpty(r.Transcript.Segmentos);
    }

    [Fact]
    public void NumeroNuloNoJsonNaoQuebraOCliente()
    {
        // Regressão de campo: o servidor devolve `posicaoNaFila: null` no GET (só o 202 a
        // preenche). Com um `int` não-anulável no DTO isso virava JsonException, que o
        // cliente classificaria como "resposta ilegível" = falha TRANSITÓRIA — e a fila
        // reenviaria para sempre contra um servidor perfeitamente saudável. Todo número do
        // contrato é anulável por causa disto.
        const string json = """
            {
              "jobId": "9f2c", "estado": "concluido", "ligacaoId": "abc", "posicaoNaFila": null,
              "resultado": {
                "canais": [
                  { "speaker": "atendente", "duracaoSegundos": null, "vazio": false, "motivoVazio": null,
                    "segmentos": [ { "inicio": 0.0, "fim": null, "texto": "oi", "confianca": null, "probSemFala": null } ] }
                ],
                "dialogo": { "turnos": [], "descartadosPorConfianca": null, "descartadosPorPadrao": null,
                             "repeticoesColapsadas": null, "fatorCompressaoTimestamps": null },
                "duracaoAudioSegundos": null, "tempoProcessamentoSegundos": null, "tempoNaFilaSegundos": null
              }
            }
            """;

        var r = MapeadorContrato.MapearJobJson(json, Saude(analise: true, resumo: true));

        var seg = Assert.Single(r.Transcript.Segmentos);
        Assert.Equal("oi", seg.Texto);
        Assert.Equal(TimeSpan.Zero, seg.Fim);   // fim ausente cai para o início, nunca negativo
        Assert.Null(seg.Confianca);
    }

    [Fact]
    public void CampoNovoNoJsonNaoQuebraOCliente()
    {
        const string json = """
            {
              "jobId": "1", "estado": "concluido", "novidadeDoServidor": { "qualquer": 1 },
              "resultado": {
                "canais": [ { "speaker": "atendente", "vazio": false, "coisaNova": true,
                              "segmentos": [ { "inicio": 0, "fim": 1, "texto": "oi", "confianca": 0.9, "extra": "x" } ] } ]
              }
            }
            """;

        var r = MapeadorContrato.MapearJobJson(json, Saude(analise: false, resumo: false));
        Assert.Single(r.Transcript.Segmentos);
    }
}
