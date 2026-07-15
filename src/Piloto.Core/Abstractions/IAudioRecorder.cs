using Piloto.Core.Models;

namespace Piloto.Core.Abstractions;

/// <summary>
/// Gravador WASAPI de 2 canais (microfone do atendente + loopback do navegador).
/// No MVP é acionado manualmente ("Gravar"); na fase 2, pela extensão.
/// </summary>
public interface IAudioRecorder
{
    bool EstaGravando { get; }

    /// <summary>Inicia a gravação dos dois canais, associando os metadados atuais.</summary>
    void Iniciar(CallMetadata metadata);

    /// <summary>Encerra a gravação e devolve o par de arquivos gerado.</summary>
    AudioCapture Parar();

    /// <summary>Descarta a gravação atual sem persistir (botão "não gravar esta chamada").</summary>
    void Descartar();

    event EventHandler<bool>? EstadoGravacaoMudou;
}
