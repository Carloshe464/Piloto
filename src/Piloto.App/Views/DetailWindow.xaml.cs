using System.Text;
using System.Windows;
using System.Windows.Controls;
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
        // Sem máscara: é o número da ligação que o atendente confere e copia. A máscara
        // vale para o que sai do app (exportação), não para a tela interna.
        TxtChipNumero.Text = Ou(m.Numero, "—");
        TxtChipTicket.Text = m.TicketId ?? "—";
        TxtChipDuracao.Text = _registro.Duracao.ToString(@"hh\:mm\:ss");

        // Nome do solicitante só aparece quando a extensão conseguiu lê-lo do Zendesk.
        TxtChipCliente.Text = m.NomeCliente ?? "";
        ChipCliente.Visibility = string.IsNullOrWhiteSpace(m.NomeCliente)
            ? Visibility.Collapsed
            : Visibility.Visible;

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

        PreencherCampos();
        PreencherDialogo();
    }

    private void PreencherDialogo()
    {
        var linhas = _registro.Transcript.Segmentos
            .Select(s => new LinhaDialogoVm
            {
                Rotulo = s.Speaker.Rotulo(),
                Horario = s.Inicio.ToString(@"mm\:ss"),
                Texto = PiiMasker.Mascarar(s.Texto.Trim()) + (s.ConfiancaBaixa ? " (⚠ trecho incerto)" : ""),
                EhAtendente = s.Speaker == Speaker.Atendente,
            })
            .ToList();

        ListaDialogo.ItemsSource = linhas;
        TxtDialogoVazio.Visibility = linhas.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Monta a aba "Dados extraídos" <b>sem máscara</b>. A máscara existe para o que SAI
    /// do app (exportação, via <c>ChkMascarar</c>); aqui dentro ela só destruía o dado:
    /// um telefone exibido como "*******5678" e um e-mail como "j***@x.com" não servem
    /// para o cadastro nem para conferir se a transcrição acertou — que é exatamente o
    /// que esta aba existe para permitir. O mesmo raciocínio que já valia para CPF/CNPJ.
    /// </summary>
    private void PreencherCampos()
    {
        ListaCampos.ItemsSource = _registro.Campos.PorCategoria()
            .Select(c => new CategoriaCampoVm
            {
                Titulo = c.Titulo,
                Valores = c.Valores.Select(CampoExtraidoVm.De).ToList(),
            })
            .ToList();
    }

    private void CopiarCampo_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string valor } botao || string.IsNullOrEmpty(valor)) return;
        try
        {
            Clipboard.SetText(valor);
            botao.Content = "Copiado ✓";
        }
        catch { /* clipboard ocupado por outro app — ignora */ }
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
            MessageBox.Show("Exportado para:\n" + dlg.FileName, "Click Write",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show("Falha ao exportar:\n\n" + ex.Message, "Click Write",
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
                "Click Write", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var resposta = MessageBox.Show(
            "Reprocessar esta ligação a partir do áudio?\n\n" +
            "A transcrição, os campos e o resumo atuais serão substituídos pelo novo resultado.",
            "Click Write", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (resposta != MessageBoxResult.Yes) return;

        try
        {
            _enqueuer.Reprocessar(_registro);
            MessageBox.Show("Ligação reenfileirada — será reprocessada em segundo plano.",
                "Click Write", MessageBoxButton.OK, MessageBoxImage.Information);
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show("Falha ao reenfileirar:\n\n" + ex.Message,
                "Click Write", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Fechar_Click(object sender, RoutedEventArgs e) => Close();

    private static string Ou(string? s, string vazio = "Não identificado")
        => string.IsNullOrWhiteSpace(s) ? vazio : s;
}
