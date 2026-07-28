using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Windows;
using Piloto.App.Services;
using Piloto.Core.Abstractions;
using Piloto.Core.Configuration;

namespace Piloto.App.Views;

public partial class SettingsWindow : Window
{
    private readonly ConfigService _config;
    private readonly Core.Services.ClickWriteUploader _uploader;

    public SettingsWindow(ConfigService config, Core.Services.ClickWriteUploader uploader)
    {
        _config = config;
        _uploader = uploader;
        InitializeComponent();
        Carregar();
    }

    private void Carregar()
    {
        var s = _config.Settings;
        var listas = _config.CarregarListas();

        TxtMotivos.Text = string.Join(Environment.NewLine, listas.MotivoContato);
        TxtProdutos.Text = string.Join(Environment.NewLine, listas.Produto);
        TxtStatus.Text = string.Join(Environment.NewLine, listas.Status);
        TxtGlossario.Text = _config.CarregarGlossario();

        TxtRetAudio.Text = s.RetencaoDias.Audio.ToString(CultureInfo.InvariantCulture);
        TxtRetTransc.Text = s.RetencaoDias.Transcricao.ToString(CultureInfo.InvariantCulture);
        TxtPorta.Text = s.Bridge.Porta.ToString(CultureInfo.InvariantCulture);

        TxtServidorUrl.Text = s.Servidor.Url;
        TxtServidorToken.Text = s.Servidor.Token;

        AtualizarStatusModelos();
    }

    /// <summary>
    /// O painel que mostrava se os modelos locais estavam presentes agora mostra se o
    /// servidor está respondendo. É a mesma pergunta de sempre — "dá para processar?" —
    /// e a resposta mudou de lugar junto com a inferência.
    /// </summary>
    private async void AtualizarStatusModelos()
    {
        var s = _config.Settings.Servidor;
        var comToken = string.IsNullOrWhiteSpace(s.Token) ? "sem token" : "token configurado";
        TxtStatusModelos.Text = $"Servidor: {s.Url}\n{comToken}\nverificando…";

        var noAr = await _uploader.ServidorNoArAsync();
        var retidas = _uploader.PendentesEmDisco();

        TxtStatusModelos.Text =
            $"Servidor: {s.Url}\n" +
            $"{comToken}   •   {(noAr ? "respondendo" : "INACESSÍVEL")}\n" +
            (retidas > 0
                ? $"{retidas} ligação(ões) guardada(s) nesta máquina, aguardando envio."
                : "Nada pendente de envio.");
    }

    /// <summary>
    /// Testa o endereço e o token <b>digitados</b>, não os salvos: o atendente precisa
    /// saber se o dado novo funciona antes de gravá-lo por cima do que funcionava.
    /// Distingue as três falhas que importam — não alcança, alcança e recusa, alcança e
    /// aceita — porque cada uma manda procurar num lugar diferente.
    /// </summary>
    private async void TestarConexao_Click(object sender, RoutedEventArgs e)
    {
        var url = TxtServidorUrl.Text.Trim().TrimEnd('/');
        var token = TxtServidorToken.Text.Trim();
        TxtStatusModelos.Text = $"Servidor: {url}\ntestando…";

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            if (!string.IsNullOrWhiteSpace(token))
                http.DefaultRequestHeaders.Add("X-Token", token);

            using var r = await http.GetAsync($"{url}/v1/health");
            TxtStatusModelos.Text = (int)r.StatusCode switch
            {
                401 => $"Servidor: {url}\nO servidor respondeu, mas RECUSOU o token.\n"
                       + "Confira o token com quem administra o servidor.",
                >= 200 and < 300 => $"Servidor: {url}\nConexão OK — servidor respondendo e token aceito.",
                var c => $"Servidor: {url}\nRespondeu HTTP {c}. Confira o endereço.",
            };
        }
        catch (Exception ex)
        {
            TxtStatusModelos.Text =
                $"Servidor: {url}\nNÃO FOI POSSÍVEL ALCANÇAR o servidor.\n{ex.Message}\n"
                + "Verifique o endereço, a rede e se o servidor está ligado.";
        }
    }

    private void AbrirPastaModelos_Click(object sender, RoutedEventArgs e)
    {
        // O botão passou a abrir a pasta de dados: é onde ficam as ligações retidas
        // (pendentes) e as que aguardam resultado — o que alguém precisa inspecionar
        // quando algo não chega ao servidor. Modelos não existem mais nesta máquina.
        var pasta = _config.Settings.PastaDadosExpandida;
        try
        {
            Directory.CreateDirectory(pasta);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{pasta}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show("Não foi possível abrir a pasta:\n" + ex.Message, "Click Write",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Salvar_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var listas = new ListasFechadas
            {
                MotivoContato = Linhas(TxtMotivos.Text),
                Produto = Linhas(TxtProdutos.Text),
                Status = Linhas(TxtStatus.Text),
            };
            _config.SalvarListas(listas);
            _config.SalvarGlossario(TxtGlossario.Text);

            var s = _config.Settings;
            s.RetencaoDias.Audio = ParseInt(TxtRetAudio.Text, s.RetencaoDias.Audio);
            s.RetencaoDias.Transcricao = ParseInt(TxtRetTransc.Text, s.RetencaoDias.Transcricao);
            s.Bridge.Porta = ParseInt(TxtPorta.Text, s.Bridge.Porta);
            s.Servidor.Url = TxtServidorUrl.Text.Trim();
            s.Servidor.Token = TxtServidorToken.Text.Trim();
            // Edição manual vence o provisionamento: sem carimbar a data, a próxima
            // abertura reaplicaria o arquivo do instalador por cima do que foi digitado.
            s.Servidor.ProvisionadoEm = DateTimeOffset.UtcNow;
            _config.SalvarSettings(s);

            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show("Falha ao salvar:\n\n" + ex.Message, "Click Write",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Cancelar_Click(object sender, RoutedEventArgs e) => Close();

    private static List<string> Linhas(string texto)
        => texto.Split('\n')
                .Select(l => l.Trim().TrimEnd('\r'))
                .Where(l => l.Length > 0)
                .ToList();

    private static int ParseInt(string texto, int fallback)
        => int.TryParse(texto.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;
}
