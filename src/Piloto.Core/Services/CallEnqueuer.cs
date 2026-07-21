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

    /// <summary>
    /// Reenfileira uma ligação já processada a partir dos MESMOS WAVs (retidos por 30 dias).
    /// O item nasce com <see cref="QueueItem.RegistroId"/> apontando para o registro
    /// original: ao concluir, o processador atualiza a ligação em lugar — id/uuid estáveis,
    /// nada duplicado. Uso típico: retestar com outro modelo/versão ou com a máquina folgada.
    /// </summary>
    public long Reprocessar(CallRecord registro)
    {
        var item = new QueueItem
        {
            CaminhoAudioAtendente = registro.CaminhoAudioAtendente ?? "",
            CaminhoAudioCliente = registro.CaminhoAudioCliente ?? "",
            MetadataJson = JsonSerializer.Serialize(registro.Metadata),
            Estado = QueueState.Pendente,
            CriadoEm = DateTimeOffset.Now,
            RegistroId = registro.Id,
        };
        return _repo.EnfileirarItem(item);
    }
}
