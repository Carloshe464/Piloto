using System.Text;
using System.Windows;
using Microsoft.Win32;
using Piloto.Core.Abstractions;
using Piloto.Core.Models;
using Piloto.Data.Export;

namespace Piloto.App.Views;

public partial class DetailWindow : Window
{
    private readonly CallRecord _registro;
    private readonly IExporter _exporter;

    public DetailWindow(CallRecord registro, IExporter exporter)
    {
        _registro = registro;
        _exporter = exporter;
        InitializeComponent();
        Preencher();
    }

    private void Preencher()
    {
        var m = _registro.Metadata;
        var numero = string.IsNullOrWhiteSpace(m.Numero) ? "—" : PiiMasker.Mascarar(m.Numero);
        TxtCabecalho.Text = $"#{_registro.Id} • {_registro.CriadoEm.LocalDateTime:dd/MM/yyyy HH:mm} • "
                            + $"Número {numero} • Ticket {m.TicketId ?? "—"} • {_registro.Duracao:hh\\:mm\\:ss}";

        if (_registro.PrecisaRevisao)
        {
            PanelRevisao.Visibility = Visibility.Visible;
            TxtRevisao.Text = "Revisão humana necessária:\n• " + string.Join("\n• ", _registro.MotivosRevisao);
        }

        var r = _registro.Resumo;
        TxtResumo.Text = "Resumo: " + Ou(PiiMasker.Mascarar(r.Resumo));
        TxtMotivo.Text = "Motivo do contato: " + Ou(r.MotivoContato);
        TxtProduto.Text = "Produto: " + Ou(r.Produto);
        TxtStatus.Text = "Status: " + Ou(r.Status);
        TxtPedido.Text = "Pedido: " + Ou(PiiMasker.Mascarar(r.Pedido));
        TxtProximo.Text = "Próximo passo: " + Ou(PiiMasker.Mascarar(r.ProximoPasso));

        TxtCampos.Text = MontarCampos();
        TxtDialogo.Text = PiiMasker.Mascarar(_registro.Transcript.TextoRotulado());
    }

    private string MontarCampos()
    {
        var sb = new StringBuilder();
        void Linha(string titulo, IReadOnlyList<ExtractedValue> vs)
        {
            var texto = vs.Count == 0
                ? "Não identificado"
                : string.Join("; ", vs.Select(v => $"{PiiMasker.Mascarar(v.Valor)} ({v.Confianca:P0})"));
            sb.AppendLine($"{titulo}: {texto}");
        }
        Linha("Telefones", _registro.Campos.Telefones);
        Linha("CPFs", _registro.Campos.Cpfs);
        Linha("E-mails", _registro.Campos.Emails);
        Linha("Datas", _registro.Campos.Datas);
        Linha("Valores", _registro.Campos.Valores);
        Linha("Protocolos", _registro.Campos.Protocolos);
        return sb.ToString().TrimEnd();
    }

    private void ExportarTxt_Click(object sender, RoutedEventArgs e) => Exportar(ExportFormat.Txt, "txt");
    private void ExportarJson_Click(object sender, RoutedEventArgs e) => Exportar(ExportFormat.Json, "json");
    private void ExportarCsv_Click(object sender, RoutedEventArgs e) => Exportar(ExportFormat.Csv, "csv");

    private void Exportar(ExportFormat formato, string extensao)
    {
        var dlg = new SaveFileDialog
        {
            FileName = $"ligacao-{_registro.Id}.{extensao}",
            Filter = $"{extensao.ToUpperInvariant()}|*.{extensao}|Todos|*.*",
        };
        if (dlg.ShowDialog(this) != true) return;

        try
        {
            var conteudo = _exporter.Exportar(_registro, formato, ChkMascarar.IsChecked == true);
            File.WriteAllText(dlg.FileName, conteudo, new UTF8Encoding(true));
            MessageBox.Show("Exportado para:\n" + dlg.FileName, "Piloto",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show("Falha ao exportar:\n\n" + ex.Message, "Piloto",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Fechar_Click(object sender, RoutedEventArgs e) => Close();

    private static string Ou(string? s) => string.IsNullOrWhiteSpace(s) ? "Não identificado" : s;
}
