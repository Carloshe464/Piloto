using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Piloto.Core.Models;

namespace Piloto.Bridge;

/// <summary>
/// Servidor WebSocket local (127.0.0.1) para a extensão do navegador. Implementado sobre
/// <see cref="TcpListener"/> com handshake manual — assim liga na porta de loopback sem exigir
/// reserva de URL/admin do HttpListener. Só aceita conexões da interface de loopback.
/// </summary>
public sealed class ZendeskBridgeServer : IAsyncDisposable
{
    private const string WsGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    private readonly int _porta;
    private readonly ILogger<ZendeskBridgeServer> _log;
    private readonly List<WebSocket> _clientes = new();
    private readonly object _lock = new();

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;

    public event EventHandler<CallMetadata>? MetadataAtualizada;
    public event EventHandler<CallMetadata>? ChamadaIniciada;
    public event EventHandler<CallMetadata>? ChamadaEncerrada;

    /// <summary>Extensão começou a transmitir áudio da chamada (taxa de amostragem em Hz).</summary>
    public event EventHandler<int>? AudioIniciado;

    /// <summary>Bloco PCM16 de um canal ("atendente"/"cliente").</summary>
    public event EventHandler<AudioChunkEventArgs>? AudioChunkRecebido;

    /// <summary>Extensão encerrou a transmissão de áudio (fim da chamada).</summary>
    public event EventHandler? AudioEncerrado;

    public ZendeskBridgeServer(int porta, ILogger<ZendeskBridgeServer> log)
    {
        _porta = porta;
        _log = log;
    }

    public bool EmExecucao => _acceptLoop is { IsCompleted: false };

    public void Iniciar()
    {
        if (EmExecucao) return;
        _cts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Loopback, _porta);
        _listener.Start();
        _log.LogInformation("Bridge ouvindo em ws://127.0.0.1:{Porta}", _porta);
        _acceptLoop = AceitarAsync(_cts.Token);
    }

    private async Task AceitarAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener!.AcceptTcpClientAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex) { _log.LogWarning(ex, "Falha ao aceitar conexão"); continue; }

            _ = Task.Run(() => AtenderClienteAsync(client, ct), ct);
        }
    }

    private async Task AtenderClienteAsync(TcpClient client, CancellationToken ct)
    {
        using var _ = client;
        WebSocket? ws = null;
        try
        {
            var stream = client.GetStream();
            if (!await FazerHandshakeAsync(stream, ct).ConfigureAwait(false))
                return;

            ws = WebSocket.CreateFromStream(stream, isServer: true, subProtocol: null,
                keepAliveInterval: TimeSpan.FromSeconds(30));
            lock (_lock) _clientes.Add(ws);

            await ReceberAsync(ws, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* encerrando */ }
        catch (Exception ex) { _log.LogWarning(ex, "Conexão da extensão encerrada com erro"); }
        finally
        {
            if (ws is not null)
            {
                lock (_lock) _clientes.Remove(ws);
                ws.Dispose();
            }
        }
    }

    private async Task<bool> FazerHandshakeAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[8192];
        var sb = new StringBuilder();
        int lido;
        while ((lido = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            sb.Append(Encoding.ASCII.GetString(buffer, 0, lido));
            if (sb.ToString().Contains("\r\n\r\n")) break;
            if (sb.Length > 32 * 1024) return false;
        }

        var request = sb.ToString();
        var chave = ExtrairHeader(request, "Sec-WebSocket-Key");
        if (string.IsNullOrEmpty(chave))
        {
            _log.LogWarning("Handshake sem Sec-WebSocket-Key — ignorando");
            return false;
        }

        var accept = Convert.ToBase64String(
            SHA1.HashData(Encoding.ASCII.GetBytes(chave + WsGuid)));

        var resposta =
            "HTTP/1.1 101 Switching Protocols\r\n" +
            "Upgrade: websocket\r\n" +
            "Connection: Upgrade\r\n" +
            $"Sec-WebSocket-Accept: {accept}\r\n\r\n";
        var bytes = Encoding.ASCII.GetBytes(resposta);
        await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
        return true;
    }

    private async Task ReceberAsync(WebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var ms = new MemoryStream();
            WebSocketReceiveResult resultado;
            do
            {
                resultado = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
                if (resultado.MessageType == WebSocketMessageType.Close)
                {
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", ct).ConfigureAwait(false);
                    return;
                }
                ms.Write(buffer, 0, resultado.Count);
            }
            while (!resultado.EndOfMessage);

            var texto = Encoding.UTF8.GetString(ms.ToArray());
            ProcessarMensagem(texto);
        }
    }

    private void ProcessarMensagem(string texto)
    {
        BridgeMessage? msg;
        try { msg = JsonSerializer.Deserialize<BridgeMessage>(texto); }
        catch (Exception ex) { _log.LogWarning(ex, "Mensagem inválida da extensão: {Texto}", texto); return; }
        if (msg is null) return;

        var metadata = msg.ParaMetadata();
        switch (msg.Tipo)
        {
            case BridgeMessageTypes.ChamadaIniciada:
                metadata.IniciadaEm = DateTimeOffset.Now;
                ChamadaIniciada?.Invoke(this, metadata);
                break;
            case BridgeMessageTypes.ChamadaEncerrada:
                metadata.EncerradaEm = DateTimeOffset.Now;
                ChamadaEncerrada?.Invoke(this, metadata);
                break;
            case BridgeMessageTypes.AudioInicio:
                AudioIniciado?.Invoke(this, msg.Taxa is > 0 ? msg.Taxa.Value : 16000);
                break;
            case BridgeMessageTypes.AudioChunk:
                if (!string.IsNullOrEmpty(msg.Canal) && !string.IsNullOrEmpty(msg.Dados))
                {
                    byte[] dados;
                    try { dados = Convert.FromBase64String(msg.Dados); }
                    catch (FormatException) { break; }
                    AudioChunkRecebido?.Invoke(this, new AudioChunkEventArgs(msg.Canal, dados));
                }
                break;
            case BridgeMessageTypes.AudioFim:
                AudioEncerrado?.Invoke(this, EventArgs.Empty);
                break;
            case BridgeMessageTypes.Ping:
                break;
            default:
                MetadataAtualizada?.Invoke(this, metadata);
                break;
        }
    }

    /// <summary>Envia um JSON a todos os clientes conectados (ex.: estado de gravação).</summary>
    public async Task BroadcastAsync(object payload, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(payload);
        var bytes = Encoding.UTF8.GetBytes(json);
        WebSocket[] alvos;
        lock (_lock) alvos = _clientes.ToArray();

        foreach (var ws in alvos)
        {
            if (ws.State != WebSocketState.Open) continue;
            try { await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct).ConfigureAwait(false); }
            catch (Exception ex) { _log.LogDebug(ex, "Falha ao enviar broadcast"); }
        }
    }

    private static string? ExtrairHeader(string request, string nome)
    {
        foreach (var linha in request.Split("\r\n"))
        {
            var idx = linha.IndexOf(':');
            if (idx <= 0) continue;
            if (linha[..idx].Trim().Equals(nome, StringComparison.OrdinalIgnoreCase))
                return linha[(idx + 1)..].Trim();
        }
        return null;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            _cts?.Cancel();
            _listener?.Stop();
            if (_acceptLoop is not null) await _acceptLoop.ConfigureAwait(false);
        }
        catch { /* ignore */ }
        finally
        {
            WebSocket[] alvos;
            lock (_lock) { alvos = _clientes.ToArray(); _clientes.Clear(); }
            foreach (var ws in alvos) ws.Dispose();
            _cts?.Dispose();
        }
    }
}
