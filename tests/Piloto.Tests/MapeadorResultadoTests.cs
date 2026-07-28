using System.Text.Json;
using Piloto.Core.Models;
using Piloto.Core.Services;
using Xunit;

namespace Piloto.Tests;

/// <summary>
/// Tradução do JSON do servidor para o <c>CallRecord</c> que as telas leem.
/// <para>
/// O JSON abaixo é a resposta real de <c>GET /v1/calls/{id}</c>, não uma versão idealizada.
/// É aqui que se descobre que um campo mudou de nome no servidor — antes de o atendente
/// ver a tela vazia.
/// </para>
/// </summary>
public class MapeadorResultadoTests
{
    private const string JsonServidor = """
    {
      "call_id": "0b2495ccc52d4a44bae8a4a863b67fc4",
      "status": "done",
      "metadados": {
        "ticket": "99887", "telefone": "11987654321", "agent_id": "carlos.lemos",
        "duracao_ms": 63000
      },
      "campos_objetivos": {
        "cpf": {
          "valor": "12345678909", "formatado": "123.456.789-09", "confianca": 0.97,
          "origem": "transcricao", "validado_dv": true, "reparado": false,
          "parcial": false, "confirmado_por_repeticao": true, "candidatos": [],
          "ancora": { "canal": "cliente", "inicio_ms": 18600, "fim_ms": 25000,
                      "texto_bruto": "um, dois, tres, quatro" }
        },
        "cnpj": null,
        "email": {
          "valor": "carlos.lemos@gmail.com", "formatado": "carlos.lemos@gmail.com",
          "confianca": 0.78, "origem": "transcricao", "validado_dv": true,
          "reparado": false, "parcial": false, "confirmado_por_repeticao": false,
          "candidatos": [], "ancora": null
        },
        "nome": {
          "valor": "Carlos Henrique", "formatado": "Carlos Henrique", "confianca": 0.93,
          "origem": "transcricao", "validado_dv": false, "reparado": false,
          "parcial": false, "confirmado_por_repeticao": false, "candidatos": [], "ancora": null
        },
        "telefone": null
      },
      "resumo": {
        "quem_ligou": "Carlos Henrique", "papel": "titular",
        "motivo_contato": "Emissão de nota fiscal", "produto": "Click Notas",
        "status": "Resolvido", "problema_resolvido": true, "satisfacao": "satisfeito",
        "texto": "Cliente relatou rejeicao na emissao. Resolvido na ligacao.",
        "confianca": 0.87, "origem": "regra"
      },
      "transcricao": {
        "turnos": [
          { "speaker": "agente", "inicio_ms": 0, "fim_ms": 3000,
            "texto": "Click Digital, bom dia.", "confianca": 0.9 },
          { "speaker": "cliente", "inicio_ms": 5300, "fim_ms": 13000,
            "texto": "A nota fiscal deu rejeicao.", "confianca": 0.4 }
        ],
        "modelo": "large-v3", "duracao_ms": 63000
      },
      "revisao_humana": { "necessaria": false, "motivos": [] },
      "processamento": {
        "device": "cuda", "modelo": "large-v3", "duracao_ms": 21800,
        "versao": "0.1.0", "llm_usado": true, "avisos": []
      }
    }
    """;

    private static ResultadoServidor Desserializar(string json) =>
        JsonSerializer.Deserialize<ResultadoServidor>(json)!;

    private static CallRecord Mapear(string? json = null, CallMetadata? local = null) =>
        MapeadorResultado.ParaRegistro(Desserializar(json ?? JsonServidor),
                                       local ?? CallMetadata.Vazio());

    // ---------------------------------------------------------------- resumo

    [Fact]
    public void Resumo_vai_para_os_campos_da_tela()
    {
        var r = Mapear().Resumo;

        Assert.Equal("Cliente relatou rejeicao na emissao. Resolvido na ligacao.", r.Resumo);
        Assert.Equal("Emissão de nota fiscal", r.MotivoContato);
        Assert.Equal("Click Notas", r.Produto);
        Assert.Equal("Resolvido", r.Status);
    }

    [Fact]
    public void Desfecho_aparece_no_proximo_passo()
    {
        // problema_resolvido não tem campo próprio na tela; "Próximo passo" é onde o
        // atendente consegue ver o desfecho sem abrir o banco.
        Assert.Equal("Resolvido na ligação.", Mapear().Resumo.ProximoPasso);
    }

    [Fact]
    public void Satisfacao_e_persistida_mesmo_sem_lugar_na_tela()
    {
        var r = Mapear().Resumo;

        Assert.Equal("satisfeito", r.Satisfacao);
        Assert.True(r.ProblemaResolvido);
        Assert.Equal("Carlos Henrique", r.QuemLigou);
    }

    // ---------------------------------------------------------------- campos

    [Fact]
    public void Cpf_entra_formatado_na_lista_de_documentos()
    {
        var cpf = Assert.Single(Mapear().Campos.Cpfs);

        Assert.Equal(FieldType.Cpf, cpf.Tipo);
        Assert.Equal("123.456.789-09", cpf.Valor);
        Assert.Equal(FieldSource.Regra, cpf.Origem);
    }

    [Fact]
    public void Ancora_do_servidor_vira_o_trecho_de_origem()
    {
        // É o que o atendente lê para conferir sem procurar na gravação.
        var cpf = Mapear().Campos.Cpfs[0];

        Assert.Contains("dígito verificador ok", cpf.TrechoOrigem);
        Assert.Contains("confirmado 2×", cpf.TrechoOrigem);
        Assert.Contains("00:18", cpf.TrechoOrigem);
        Assert.Contains("um, dois, tres", cpf.TrechoOrigem);
    }

    [Fact]
    public void Nome_vai_para_o_cabecalho_e_nao_para_a_lista_de_campos()
    {
        var registro = Mapear();

        Assert.Equal("Carlos Henrique", registro.Metadata.NomeCliente);
        Assert.DoesNotContain(registro.Campos.Todos(), v => v.Valor == "Carlos Henrique");
    }

    [Fact]
    public void Campo_ausente_no_servidor_nao_vira_linha_vazia()
    {
        var registro = Mapear();

        Assert.Empty(registro.Campos.Telefones);
        Assert.DoesNotContain(registro.Campos.Cpfs, v => v.Tipo == FieldType.Cnpj);
    }

    [Fact]
    public void Cadastro_do_zendesk_vence_o_que_foi_ouvido()
    {
        var local = new CallMetadata { NomeCliente = "Carlos H. Lemos", TicketId = "12345" };

        var registro = Mapear(local: local);

        Assert.Equal("Carlos H. Lemos", registro.Metadata.NomeCliente);
        Assert.Equal("12345", registro.Metadata.TicketId);
    }

    // ------------------------------------------------------------ transcrição

    [Fact]
    public void Turnos_viram_segmentos_com_o_interlocutor_certo()
    {
        var segs = Mapear().Transcript.Segmentos;

        Assert.Equal(2, segs.Count);
        Assert.Equal(Speaker.Atendente, segs[0].Speaker);
        Assert.Equal(Speaker.Cliente, segs[1].Speaker);
        Assert.Equal(TimeSpan.FromMilliseconds(5300), segs[1].Inicio);
    }

    [Fact]
    public void Confianca_baixa_continua_sendo_sinalizada_na_tela()
    {
        var segs = Mapear().Transcript.Segmentos;

        Assert.False(segs[0].ConfiancaBaixa);
        Assert.True(segs[1].ConfiancaBaixa);
    }

    [Fact]
    public void Uuid_guarda_o_identificador_do_servidor()
    {
        // É por ele que o botão "Reprocessar" pede reprocessamento remoto.
        Assert.Equal("0b2495ccc52d4a44bae8a4a863b67fc4", Mapear().Uuid);
    }

    // ---------------------------------------------------------------- revisão

    [Fact]
    public void Motivos_de_revisao_chegam_traduzidos()
    {
        var json = JsonServidor.Replace(
            """"revisao_humana": { "necessaria": false, "motivos": [] }"""",
            """"revisao_humana": { "necessaria": true, "motivos": ["cpf_parcial", "llm_indisponivel"] }"""");

        var registro = Mapear(json);

        Assert.True(registro.PrecisaRevisao);
        Assert.Contains("CPF incompleto — confira no áudio", registro.MotivosRevisao);
        Assert.Contains(registro.MotivosRevisao, m => m.Contains("apenas por regras"));
    }

    [Fact]
    public void Documento_parcial_traz_os_candidatos_como_sugestao()
    {
        var json = JsonServidor
            .Replace("\"parcial\": false, \"confirmado_por_repeticao\": true, \"candidatos\": []",
                     "\"parcial\": true, \"confirmado_por_repeticao\": false, \"candidatos\": [\"987.654.321-00\"]");

        var cpfs = Mapear(json).Campos.Cpfs;

        // O ouvido e a sugestão, para o atendente escolher em vez de redigitar.
        Assert.Equal(2, cpfs.Count);
        Assert.Contains(cpfs, v => v.Valor == "987.654.321-00");
        Assert.Contains(cpfs, v => v.TrechoOrigem.Contains("PARCIAL"));
    }

    [Fact]
    public void Resultado_sem_nada_identificado_nao_quebra()
    {
        var json = """
        { "call_id": "vazio", "metadados": {}, "campos_objetivos": {},
          "resumo": {}, "transcricao": { "turnos": [] },
          "revisao_humana": {}, "processamento": {} }
        """;

        var registro = MapeadorResultado.ParaRegistro(Desserializar(json), CallMetadata.Vazio());

        Assert.Empty(registro.Transcript.Segmentos);
        Assert.Empty(registro.Campos.Todos());
        Assert.Null(registro.Resumo.Resumo);
        Assert.False(registro.PrecisaRevisao);
    }
}
