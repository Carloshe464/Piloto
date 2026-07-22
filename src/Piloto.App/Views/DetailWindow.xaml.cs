using System.Text;
using System.Windows;
using Microsoft.Win32;
using Piloto.App.ViewModels;
using Piloto.Core.Abstractions;
using Piloto.Core.Models;
using Piloto.Data.Export;

namespace Piloto.App.Views;

public partial class DetailWindow : Window
{
    private readonly CallRecord _registro;
    private readonly IExporter _exporter;
    private readonly Core.Services.CallEnqueuer _enqueuer;

    public DetailWindow(CallRecord registro, IExporter exporter, Core.Services.CallEnqueuer enqueuer)
    {
        _registro = registro;
        _exporter = exporter;
        _enqueuer = enqueuer;
        InitializeComponent();
        Preencher();
    }

    private void Preencher()
    {
        var m = _registro.Metadata;
        TxtCabecalho.Text = $"Ligação #{_registro.Id}";
        TxtChipData.Text = _registro.CriadoEm.LocalDateTime.ToString("dd/MM/yyyy HH:mm");
        TxtChipNumero.Text = string.IsNullOrWhiteSpace(m.Numero) ? "—" : PiiMasker.Mascarar(m.Numero);
        TxtChipTicket.Text = m.TicketId ?? "—";
        TxtChipDuracao.Text = _registro.Duracao.ToString(@"hh\:mm\:ss");

        if (_registro.PrecisaRevisao)
        {
            PanelRevisao.Visibility = Visibility.Visible;
            TxtRevisao.Text = "Revisão humana necessária:\n• " + string.Join("\n• ", _registro.MotivosRevisao);
        }

        var r = _registro.Resumo;
        TxtResumo.Text = Ou(PiiMasker.Mascarar(r.Resumo));
        TxtMotivo.Text = Ou(r.MotivoContato, "—");
        TxtProduto.Text = Ou(r.Produto, "—");
        TxtStatus.Text = Ou(r.Status, "—");
        TxtPedido.Text = Ou(PiiMasker.Mascarar(r.Pedido));
        TxtProximo.Text = Ou(PiiMasker.Mascarar(r.ProximoPasso));

        TxtCampos.Text = MontarCampos();
        PreencherDialogo();
    }

    private void PreencherDialogo()
    {
        var linhas = _registro.Transcript.Segmentos
            .Select(s => new LinhaDialogoVm
            {
                Rotulo = s.Speaker.Rotulo(),
                Horario = s.Inicio.ToString(@"mm\:ss"),
                Texto = PiiMasker.Mascarar(s.Texto.Trim()),
                EhAtendente = s.Speaker == Speaker.Atendente,
            })
            .ToList();

        ListaDialogo.ItemsSource = linhas;
        TxtDialogoVazio.Visibility = linhas.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private string MontarCampos()
    {
        var sb = new StringBuilder();
        void Linha(string titulo, IReadOnlyList<ExtractedValue> vs, bool mascarar = true)
        {
            var texto = vs.Count == 0
                ? "Não identificado"
                : string.Join("; ", vs.Select(v => $"{(mascarar ? PiiMasker.Mascarar(v.Valor) : v.Valor)} ({v.Confianca:P0})"));
            sb.AppendLine($"{titulo}: {texto}");
        }
        Linha("Telefones", _registro.Campos.Telefones);
        // CPF/CNPJ sem máscara: é o dado que o atendente copia para o cadastro.
        Linha("CPF/CNPJ", _registro.Campos.Cpfs, mascarar: false);
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

    private void CopiarDialogo_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(PiiMasker.Mascarar(_registro.Transcript.TextoRotulado()));
            BtnCopiarDialogo.Content = "Copiado ✓";
        }
        catch { /* clipboard ocupado por outro app — ignora */ }
    }

    /// <summary>
    /// Reenfileira a ligação a partir dos WAVs originais (retidos por 30 dias): o novo
    /// resultado SUBSTITUI transcrição, campos e resumo deste registro. Uso típico da fase
    /// piloto: retestar após atualização do app ou com a máquina folgada (modelo maior).
    /// </summary>
    private void Reprocessar_Click(object sender, RoutedEventArgs e)
    {
        var temAudio =
            (!string.IsNullOrWhiteSpace(_registro.CaminhoAudioAtendente) && File.Exists(_registro.CaminhoAudioAtendente)) ||
            (!string.IsNullOrWhiteSpace(_registro.CaminhoAudioCliente) && File.Exists(_registro.CaminhoAudioCliente));
        if (!temAudio)
        {
            MessageBox.Show("O áudio desta ligação não está mais no disco (retenção) — não dá para reprocessar.",
                "Piloto", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var resposta = MessageBox.Show(
            "Reprocessar esta ligação a partir do áudio?\n\n" +
            "A transcrição, os campos e o resumo atuais serão substituídos pelo novo resultado.",
            "Piloto", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (resposta != MessageBoxResult.Yes) return;

        try
        {
            _enqueuer.Reprocessar(_registro);
            MessageBox.Show("Ligação reenfileirada — será reprocessada em segundo plano.",
                "Piloto", MessageBoxButton.OK, MessageBoxImage.Information);
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show("Falha ao reenfileirar:\n\n" + ex.Message,
                "Piloto", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Fechar_Click(object sender, RoutedEventArgs e) => Close();

    private static string Ou(string? s, string vazio = "Não identificado")
        => string.IsNullOrWhiteSpace(s) ? vazio : s;
}
