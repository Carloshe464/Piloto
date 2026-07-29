using Piloto.Core.Models;

namespace Piloto.Core.Services;

/// <summary>
/// Traduz o JSON do servidor para o <see cref="CallRecord"/> do aplicativo.
/// <para>
/// É o único ponto do app que conhece o formato do servidor. As telas continuam lendo
/// <c>CallRecord</c> exatamente como quando a transcrição acontecia aqui — nada na
/// visualização muda. Se o contrato do servidor mudar, muda só este arquivo.
/// </para>
/// </summary>
public static class MapeadorResultado
{
    public static CallRecord CriarProvisorio(AudioCapture captura)
    {
        var campos = ObjectiveFields.Vazio();
        AdicionarDoCadastro(campos.Tickets, captura.Metadata.TicketId, FieldType.Ticket);
        AdicionarDoCadastro(campos.Telefones,
            captura.Metadata.TelefoneCliente ?? captura.Metadata.Numero,
            FieldType.Telefone);

        return new CallRecord
        {
            Uuid = $"local-{Guid.NewGuid():N}",
            Metadata = captura.Metadata,
            Campos = campos,
            Resumo = new LlmSummary { Status = "Processando" },
            CriadoEm = captura.IniciadaEm,
            Duracao = captura.Duracao,
            CaminhoAudioAtendente = captura.CaminhoAtendente,
            CaminhoAudioCliente = captura.CaminhoCliente,
        };
    }

    public static CallRecord ParaRegistro(
        ResultadoServidor origem,
        CallMetadata metadataLocal,
        string? caminhoAudioAtendente = null,
        string? caminhoAudioCliente = null)
    {
        var registro = new CallRecord
        {
            // O identificador do servidor vira o Uuid do registro local: é por ele que o
            // botão "Reprocessar" pede o reprocessamento remoto, sem reenviar áudio.
            Uuid = origem.CallId,
            Metadata = MesclarMetadata(metadataLocal, origem),
            Transcript = ParaTranscript(origem.Transcricao),
            Campos = ParaCampos(origem.Campos, metadataLocal, origem),
            Resumo = ParaResumo(origem.Resumo),
            Duracao = TimeSpan.FromMilliseconds(origem.Transcricao.DuracaoMs > 0
                ? origem.Transcricao.DuracaoMs
                : origem.Metadados.DuracaoMs),
            CriadoEm = origem.Metadados.IniciadaEm ?? DateTimeOffset.Now,
            CaminhoAudioAtendente = caminhoAudioAtendente,
            CaminhoAudioCliente = caminhoAudioCliente,
        };

        registro.TempoFalado = registro.Transcript.TempoTotalFalado();

        // O servidor já decidiu o que precisa de olho humano; o app só repassa, sem
        // reavaliar. Reavaliar aqui criaria duas fontes de verdade para a mesma pergunta.
        foreach (var motivo in origem.Revisao.Motivos)
            registro.MarcarRevisao(Traduzir(motivo));

        foreach (var aviso in origem.Processamento.Avisos)
            registro.MarcarRevisao(aviso);

        return registro;
    }

    /// <summary>
    /// O nome do solicitante pode vir de dois lados: do cartão do Zendesk (lido pela
    /// extensão) ou da própria ligação (ouvido pelo servidor). O do cadastro vence —
    /// é dado digitado, não reconhecido de áudio.
    /// </summary>
    private static CallMetadata MesclarMetadata(CallMetadata local, ResultadoServidor origem)
    {
        var nomeOuvido = origem.Campos.Nome?.ParaExibicao;
        return new CallMetadata
        {
            Numero = origem.Metadados.Telefone ?? local.Numero,
            TicketId = origem.Metadados.Ticket ?? local.TicketId,
            Status = local.Status,
            Atendente = local.Atendente ?? origem.Metadados.AgentId,
            EmailCliente = local.EmailCliente ?? origem.Campos.Email?.ParaExibicao,
            TelefoneCliente = origem.Campos.Telefone?.ParaExibicao
                              ?? local.TelefoneCliente
                              ?? local.Numero
                              ?? origem.Metadados.Telefone,
            NomeCliente = local.NomeCliente ?? nomeOuvido,
            IniciadaEm = local.IniciadaEm,
        };
    }

    private static Transcript ParaTranscript(TranscricaoServidor origem) =>
        new(origem.Turnos.Select(t => new TranscriptSegment
        {
            Speaker = t.Speaker.Equals("agente", StringComparison.OrdinalIgnoreCase)
                ? Speaker.Atendente
                : Speaker.Cliente,
            Inicio = TimeSpan.FromMilliseconds(t.InicioMs),
            Fim = TimeSpan.FromMilliseconds(t.FimMs),
            Texto = t.Texto,
            Confianca = t.Confianca,
        }));

    private static ObjectiveFields ParaCampos(
        CamposServidor origem, CallMetadata local, ResultadoServidor resultado)
    {
        var campos = ObjectiveFields.Vazio();

        // CPF e CNPJ compartilham a lista "Cpfs" — o Tipo é que os distingue, e é assim
        // que a tela já os agrupa em "CPF/CNPJ".
        Adicionar(campos.Cpfs, origem.Cpf, FieldType.Cpf);
        Adicionar(campos.Cpfs, origem.Cnpj, FieldType.Cnpj);
        Adicionar(campos.Emails, origem.Email, FieldType.Email);
        Adicionar(campos.Nomes, origem.Nome, FieldType.Nome);
        if (origem.Telefone is not null)
            Adicionar(campos.Telefones, origem.Telefone, FieldType.Telefone);

        // O número do discador e o telefone do cadastro nunca chegam como campo do
        // servidor: ele só devolve o que ouviu. Sem isto, uma ligação em que ninguém
        // disse o telefone em voz alta sai com "Telefones: Não identificado" mesmo com
        // o número na tela do Zendesk.
        if (origem.Telefone is null)
            AdicionarDoCadastro(campos.Telefones,
                local.TelefoneCliente ?? local.Numero ?? resultado.Metadados.Telefone,
                FieldType.Telefone);

        // Ticket e nome do cadastro vêm da extensão, não da ligação: é o dado que o
        // atendente confere no Zendesk e o que amarra a gravação ao atendimento.
        AdicionarDoCadastro(campos.Tickets, resultado.Metadados.Ticket ?? local.TicketId, FieldType.Ticket);
        AdicionarDoCadastro(campos.Nomes, local.NomeCliente, FieldType.Nome);
        AdicionarDoCadastro(campos.Emails, local.EmailCliente, FieldType.Email);

        campos.Ordenar();
        return campos;
    }

    private static void AdicionarDoCadastro(List<ExtractedValue> destino, string? valor, FieldType tipo)
    {
        if (string.IsNullOrWhiteSpace(valor))
            return;

        ObjectiveFields.Mesclar(destino, new ExtractedValue
        {
            Tipo = tipo,
            Valor = valor.Trim(),
            TrechoOrigem = "cadastro Zendesk / discador",
            Confianca = 1.0,
            Origem = FieldSource.Extensao,
        });
    }

    private static void Adicionar(List<ExtractedValue> destino, CampoServidor? campo, FieldType tipo)
    {
        if (campo is null || string.IsNullOrWhiteSpace(campo.Valor))
            return;

        ObjectiveFields.Mesclar(destino, new ExtractedValue
        {
            Tipo = tipo,
            Valor = campo.ParaExibicao,
            TrechoOrigem = TrechoDe(campo),
            Confianca = campo.Confianca,
            // "cadastro" e "extensao" no servidor são o mesmo conceito que Extensao aqui:
            // dado digitado em algum sistema, não ouvido na ligação.
            Origem = campo.Origem is "cadastro" or "extensao"
                ? FieldSource.Extensao
                : FieldSource.Regra,
        });

        // Quando o servidor não conseguiu fechar o documento, ele devolve os valores que
        // passariam no dígito verificador. Uma sugestão é ajuda; quatro são ruído: com o
        // documento ditado uma vez só, o reparo por edição produz meia dúzia de CNPJs
        // igualmente "válidos" e nenhum deles é conferível pelo atendente. Nesse caso
        // fica só o que foi ouvido, marcado como PARCIAL — o valor dito na ligação.
        if (!campo.Parcial || campo.Candidatos.Count != 1)
            return;

        ObjectiveFields.Mesclar(destino, new ExtractedValue
        {
            Tipo = tipo,
            Valor = campo.Candidatos[0],
            TrechoOrigem = "sugestão — confira no áudio",
            Confianca = Math.Max(0.05, campo.Confianca - 0.05),
            Origem = FieldSource.Regra,
        });
    }

    /// <summary>
    /// Trecho de origem exibido ao lado do valor. A âncora do servidor traz o que foi dito
    /// e em que momento — é o que permite conferir sem procurar na gravação inteira.
    /// </summary>
    private static string TrechoDe(CampoServidor campo)
    {
        var marcas = new List<string>();
        if (campo.ValidadoDv) marcas.Add("dígito verificador ok");
        if (campo.ConfirmadoPorRepeticao) marcas.Add("confirmado 2×");
        if (campo.Reparado) marcas.Add("corrigido automaticamente");
        if (campo.Parcial) marcas.Add("PARCIAL — confira");

        var selo = marcas.Count > 0 ? $"[{string.Join(", ", marcas)}] " : "";
        if (campo.Ancora is null)
            return selo.TrimEnd();

        var minuto = campo.Ancora.InicioMs / 60000;
        var segundo = campo.Ancora.InicioMs % 60000 / 1000;
        return $"{selo}{minuto:00}:{segundo:00} — \"{campo.Ancora.TextoBruto}\"";
    }

    private static LlmSummary ParaResumo(ResumoServidor origem) => new()
    {
        Resumo = origem.Texto,
        MotivoContato = origem.MotivoContato,
        Produto = origem.Produto,
        Status = origem.Status,
        // O servidor não produz "pedido": ele resume, não extrai a solicitação como campo.
        Pedido = null,
        ProximoPasso = ProximoPassoDe(origem),
        Satisfacao = origem.Satisfacao,
        ProblemaResolvido = origem.ProblemaResolvido,
        QuemLigou = origem.QuemLigou,
    };

    /// <summary>
    /// "Próximo passo" é o único campo da tela onde o desfecho cabe naturalmente. Sem isto,
    /// <c>problema_resolvido</c> ficaria só no banco e o atendente não veria o desfecho em
    /// lugar nenhum.
    /// </summary>
    private static string? ProximoPassoDe(ResumoServidor origem) => origem.ProblemaResolvido switch
    {
        true => "Resolvido na ligação.",
        false when !string.IsNullOrWhiteSpace(origem.Status) => $"Em aberto — {origem.Status}.",
        false => "Não resolvido na ligação.",
        null => null,
    };

    /// <summary>Motivos de revisão do servidor em português de tela.</summary>
    private static string Traduzir(string motivo) => motivo switch
    {
        "cpf_parcial" => "CPF incompleto — confira no áudio",
        "cnpj_parcial" => "CNPJ incompleto — confira no áudio",
        "cpf_reparado" => "CPF corrigido automaticamente — confira",
        "cnpj_reparado" => "CNPJ corrigido automaticamente — confira",
        "email_parcial" => "E-mail incompleto — confira no áudio",
        "nome_parcial" => "Nome incerto",
        "cpf_divergente_do_cadastro" => "CPF diverge do cadastro — pode não ser o titular",
        "cnpj_divergente_do_cadastro" => "CNPJ diverge do cadastro",
        "motivo_nao_identificado" => "Motivo do contato não identificado",
        "satisfacao_nao_identificada" => "Satisfação não identificada",
        "produto_incerto" => "Produto incerto",
        "resumo_confianca_baixa" => "Resumo com baixa confiança",
        "llm_indisponivel" => "Resumo gerado apenas por regras (modelo indisponível)",
        _ => motivo,
    };
}
