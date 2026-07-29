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
    private readonly Core.Services.ClickWriteUploader _uploader;
    private readonly Core.Services.SincronizadorServidor _sincronizador;

    public DetailWindow(CallRecord registro, IExporter exporter,
                        Core.Services.ClickWriteUploader uploader,
                        Core.Services.SincronizadorServidor sincronizador)
    {
        _registro = registro;
        _exporter = exporter;
        _uploader = uploader;
        _sincronizador = sincronizador;
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
        PreencherSatisfacao(r.Satisfacao);

        PreencherCampos();
        PreencherDialogo();
    }

    /// <summary>
    /// Como o cliente saiu da ligação, vindo do servidor. A cor carrega a informação: numa
    /// lista de ligações do dia, o âmbar e o vermelho são o que o supervisor procura.
    /// <para>Oculto quando não foi possível classificar — chip vazio ocupa espaço e não
    /// diz nada, e "não identificado" aqui seria confundido com neutro.</para>
    /// </summary>
    private void PreencherSatisfacao(string? satisfacao)
    {
        var (texto, fundo, frente) = satisfacao switch
        {
            "satisfeito" => ("Satisfeito", "SucessoFundo", "SucessoClaro"),
            "com_duvidas" => ("Com dúvidas", "AlertaFundo", "AlertaClaro"),
            "triste" => ("Insatisfeito", "PerigoFundo", "PerigoClaro"),
            _ => (null, null, null),
        };

        if (texto is null)
        {
            ChipSatisfacao.Visibility = Visibility.Collapsed;
            return;
        }

        TxtSatisfacao.Text = texto;
        ChipSatisfacao.Background = (System.Windows.Media.Brush)FindResource(fundo!);
        TxtSatisfacao.Foreground = (System.Windows.Media.Brush)FindResource(frente!);
        ChipSatisfacao.Visibility = Visibility.Visible;
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
    /// Manda o servidor processar esta ligação de novo. O áudio não sobe outra vez — o
    /// servidor guardou os dois canais, então reprocessar custa uma requisição.
    /// Uso típico: depois de um ajuste de vocabulário no servidor, refazer a ligação que
    /// saiu errada e comparar.
    /// </summary>
    private async void Reprocessar_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_registro.Uuid))
        {
            MessageBox.Show("Esta ligação não tem identificador do servidor — não dá para reprocessar.",
                "Click Write", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var resposta = MessageBox.Show(
            "Reprocessar esta ligação no servidor?\n\n" +
            "A transcrição, os campos e o resumo atuais serão substituídos pelo novo resultado.",
            "Click Write", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (resposta != MessageBoxResult.Yes) return;

        try
        {
            if (await _uploader.ReprocessarAsync(_registro.Uuid))
            {
                // Sem voltar a acompanhar, o servidor reprocessava e o app nunca ficava
                // sabendo: nada no log, nada na tela, e o registro continuava com o
                // resultado antigo. O acompanhamento vive em disco, então sobrevive ao
                // fechamento da janela e do próprio app.
                _sincronizador.Acompanhar(
                    _registro.Uuid, _registro.Metadata,
                    _registro.CaminhoAudioAtendente, _registro.CaminhoAudioCliente);

                MessageBox.Show("Reprocessamento pedido — o resultado chega em segundo plano.",
                    "Click Write", MessageBoxButton.OK, MessageBoxImage.Information);
                Close();
            }
            else
            {
                // 409: o áudio saiu do servidor pela política de retenção.
                MessageBox.Show("O servidor não tem mais o áudio desta ligação (retenção) — não dá para reprocessar.",
                    "Click Write", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show("Falha ao falar com o servidor:\n\n" + ex.Message,
                "Click Write", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Fechar_Click(object sender, RoutedEventArgs e) => Close();

    private static string Ou(string? s, string vazio = "Não identificado")
        => string.IsNullOrWhiteSpace(s) ? vazio : s;
}
