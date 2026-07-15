using System.Text.Json;
using Piloto.Core.Abstractions;
using Piloto.Core.Models;

namespace Piloto.Core.Services;

/// <summary>
/// Converte uma <see cref="AudioCapture"/> encerrada em um <see cref="QueueItem"/> persistido.
/// Ponto de entrada da fila a partir do gravador.
/// </summary>
public sealed class CallEnqueuer
{
    private readonly ICallRepository _repo;

    public CallEnqueuer(ICallRepository repo) => _repo = repo;

    public long Enfileirar(AudioCapture captura)
    {
        var item = new QueueItem
        {
            CaminhoAudioAtendente = captura.CaminhoAtendente,
            CaminhoAudioCliente = captura.CaminhoCliente,
            MetadataJson = JsonSerializer.Serialize(captura.Metadata),
            Estado = QueueState.Pendente,
            CriadoEm = DateTimeOffset.Now,
        };
        return _repo.EnfileirarItem(item);
    }
}
