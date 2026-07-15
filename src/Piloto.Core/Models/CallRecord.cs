namespace Piloto.Core.Models;

/// <summary>
/// Registro estruturado final de uma ligação processada — o que é persistido, buscado
/// e exportado. Reúne metadados, transcrição, campos objetivos (regras) e resumo (LLM),
/// já passados pelo grounding.
/// </summary>
public sealed class CallRecord
{
    public long Id { get; set; }
    public string Uuid { get; set; } = Guid.NewGuid().ToString("N");

    public CallMetadata Metadata { get; set; } = CallMetadata.Vazio();
    public Transcript Transcript { get; set; } = Transcript.Vazio();
    public ObjectiveFields Campos { get; set; } = ObjectiveFields.Vazio();
    public LlmSummary Resumo { get; set; } = LlmSummary.Vazio();

    public DateTimeOffset CriadoEm { get; set; } = DateTimeOffset.Now;
    public TimeSpan Duracao { get; set; }
    public TimeSpan TempoFalado { get; set; }

    /// <summary>Caminhos dos áudios (podem ter sido apagados pela retenção).</summary>
    public string? CaminhoAudioAtendente { get; set; }
    public string? CaminhoAudioCliente { get; set; }

    /// <summary>
    /// Marcado quando o grounding zerou algum valor por não existir na transcrição,
    /// ou quando um campo de lista fechada veio fora da lista. Exige revisão humana.
    /// </summary>
    public bool PrecisaRevisao { get; set; }

    public List<string> MotivosRevisao { get; set; } = new();

    public void MarcarRevisao(string motivo)
    {
        PrecisaRevisao = true;
        if (!MotivosRevisao.Contains(motivo))
            MotivosRevisao.Add(motivo);
    }
}
