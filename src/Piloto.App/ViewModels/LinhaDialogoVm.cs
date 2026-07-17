namespace Piloto.App.ViewModels;

/// <summary>Uma fala do diálogo renderizada como balão na janela de detalhe.</summary>
public sealed class LinhaDialogoVm
{
    public string Rotulo { get; init; } = "";
    public string Horario { get; init; } = "";
    public string Texto { get; init; } = "";
    public bool EhAtendente { get; init; }
}
