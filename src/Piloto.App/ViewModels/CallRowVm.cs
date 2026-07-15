using Piloto.Core.Models;
using Piloto.Data.Export;

namespace Piloto.App.ViewModels;

/// <summary>Linha do histórico exibida na janela principal (com número mascarado).</summary>
public sealed class CallRowVm
{
    public long Id { get; init; }
    public string Data { get; init; } = "";
    public string Numero { get; init; } = "";
    public string Motivo { get; init; } = "";
    public string Produto { get; init; } = "";
    public string Status { get; init; } = "";
    public string Duracao { get; init; } = "";
    public string Revisao { get; init; } = "";

    public static CallRowVm De(CallRecord r) => new()
    {
        Id = r.Id,
        Data = r.CriadoEm.LocalDateTime.ToString("dd/MM/yyyy HH:mm"),
        Numero = string.IsNullOrWhiteSpace(r.Metadata.Numero) ? "—" : PiiMasker.Mascarar(r.Metadata.Numero),
        Motivo = r.Resumo.MotivoContato ?? "—",
        Produto = r.Resumo.Produto ?? "—",
        Status = r.Resumo.Status ?? "—",
        Duracao = r.Duracao.ToString(@"hh\:mm\:ss"),
        Revisao = r.PrecisaRevisao ? "⚠" : "",
    };
}
