using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Controls;
using Hardcodet.Wpf.TaskbarNotification;

namespace Piloto.App.Services;

/// <summary>
/// Ícone de bandeja: indicador visível de gravação (regra de privacidade), menu de contexto
/// e notificações de novas transcrições.
/// <para>
/// Usa a propriedade <see cref="TaskbarIcon.Icon"/> (System.Drawing.Icon), e não
/// <c>IconSource</c>: nesta versão do Hardcodet o setter de IconSource tenta
/// <c>new Uri(imagem.ToString())</c> e falha ("Invalid URI") para imagens geradas em memória.
/// </para>
/// </summary>
public sealed class TrayIconController : IDisposable
{
    private readonly TaskbarIcon _tray;
    private readonly MenuItem _itemGravar;
    private readonly MenuItem _itemNaoGravar;

    private static readonly Icon IconeOcioso = CarregarIconeAplicativo();
    private static readonly Icon IconeGravando = CriarIconeGravando(IconeOcioso);

    public TrayIconController(Action abrir, Action alternarGravacao, Action naoGravar,
        Action configuracoes, Action abrirMonitor, Action sair)
    {
        _itemGravar = new MenuItem { Header = "Iniciar gravação" };
        _itemGravar.Click += (_, _) => alternarGravacao();

        _itemNaoGravar = new MenuItem { Header = "Não gravar esta chamada", IsEnabled = false };
        _itemNaoGravar.Click += (_, _) => naoGravar();

        var itemAbrir = new MenuItem { Header = "Abrir Click Write" };
        itemAbrir.Click += (_, _) => abrir();

        var itemConfig = new MenuItem { Header = "Configurações…" };
        itemConfig.Click += (_, _) => configuracoes();

        var itemMonitor = new MenuItem { Header = "Monitor de atividades" };
        itemMonitor.Click += (_, _) => abrirMonitor();

        var itemSair = new MenuItem { Header = "Sair" };
        itemSair.Click += (_, _) => sair();

        var menu = new ContextMenu();
        menu.Items.Add(itemAbrir);
        menu.Items.Add(new Separator());
        menu.Items.Add(_itemGravar);
        menu.Items.Add(_itemNaoGravar);
        menu.Items.Add(new Separator());
        menu.Items.Add(itemMonitor);
        menu.Items.Add(itemConfig);
        menu.Items.Add(itemSair);

        _tray = new TaskbarIcon
        {
            ToolTipText = "Click Write — pronto",
            Icon = IconeOcioso,
            ContextMenu = menu,
        };
        _tray.TrayMouseDoubleClick += (_, _) => abrir();
    }

    public void AtualizarGravacao(bool gravando)
    {
        _tray.Icon = gravando ? IconeGravando : IconeOcioso;
        _tray.ToolTipText = gravando ? "Click Write — GRAVANDO" : "Click Write — pronto";
        _itemGravar.Header = gravando ? "Parar e transcrever" : "Iniciar gravação";
        _itemNaoGravar.IsEnabled = gravando;
    }

    public void Notificar(string titulo, string mensagem)
        => _tray.ShowBalloonTip(titulo, mensagem, BalloonIcon.Info);

    private static Icon CarregarIconeAplicativo()
    {
        var caminho = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(caminho) && File.Exists(caminho))
        {
            using var extraido = Icon.ExtractAssociatedIcon(caminho);
            if (extraido is not null) return (Icon)extraido.Clone();
        }

        return CriarIcone(Color.FromArgb(0x0f, 0x68, 0x73));
    }

    /// <summary>Mantém a marca visível e acrescenta um indicador vermelho durante a gravação.</summary>
    private static Icon CriarIconeGravando(Icon baseIcon)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            g.DrawIcon(baseIcon, new Rectangle(0, 0, 32, 32));
            using var contorno = new SolidBrush(Color.White);
            using var gravando = new SolidBrush(Color.FromArgb(0xdc, 0x26, 0x26));
            g.FillEllipse(contorno, 20, 20, 12, 12);
            g.FillEllipse(gravando, 22, 22, 8, 8);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    /// <summary>Fallback simples para ambientes que não expõem o ícone do executável.</summary>
    private static Icon CriarIcone(Color cor)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var brush = new SolidBrush(cor);
            g.FillEllipse(brush, 3, 3, 26, 26);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    public void Dispose() => _tray.Dispose();
}
