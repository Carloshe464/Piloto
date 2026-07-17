namespace Piloto.Core.Services;

/// <summary>
/// Heurísticas de hardware para adaptação automática. A régua do produto é a máquina
/// fraca da operação: os padrões descem nelas e só sobem onde há capacidade de sobra.
/// </summary>
public static class Hardware
{
    /// <summary>
    /// Resolve o número de threads dos modelos: valor configurado &gt; 0 vale como está;
    /// 0 = automático (metade dos threads lógicos, entre 2 e 8) — modesto nas máquinas
    /// fracas, aproveita as fortes sem saturar a máquina do atendente.
    /// </summary>
    public static int ResolverThreads(int configurado)
        => configurado > 0 ? configurado : Math.Clamp(Environment.ProcessorCount / 2, 2, 8);

    /// <summary>
    /// CPUs com menos de 8 threads lógicos não pagam o beam search (~2x mais lento)
    /// sem atrasar a fila de forma perceptível — nelas, greedy com o mesmo modelo.
    /// </summary>
    public static bool CpuComportaBeam => Environment.ProcessorCount >= 8;
}
